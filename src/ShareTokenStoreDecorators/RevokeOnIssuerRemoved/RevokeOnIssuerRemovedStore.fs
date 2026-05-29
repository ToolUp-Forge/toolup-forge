module ToolUp.ShareTokenStoreDecorators.RevokeOnIssuerRemoved

open System
open ToolUp.Platform

// ─── RevokeOnIssuerRemoved — Phase 66 Stream C.5 ─────────────────────
//
// `IShareTokenStore` decorator that revokes a departed team member's
// outstanding share-tokens. Subscribes to `MembershipChanged.Removed`
// on `NotificationKind.PlatformReservedScope`; on removal it enumerates
// the leaver's claims via `IShareTokenStore.ListByIssuer` (the C.4
// substrate) and revokes each with audit attribution
// `actor = "system:RevokeOnIssuerRemoved"`. Once a member is removed,
// any share-link they minted while a member stops granting access.
//
// **Why a decorator, not a hosted service:** wiring via
// `ServerApp.withShareTokenStoreDecorator` (Stream C.6) means the
// subscription's lifecycle is bound to the resolved store — no
// separate registration, and `SurfaceCoherenceValidator` (Stream B.2)
// warns when the decorator is wired but no `ClaimBearer` / `Team`
// surface exists (rules 9 / 10). The wrapper delegates every
// `IShareTokenStore` method to the inner store unchanged; its only
// behaviour is the membership-driven revocation side channel.
//
// **Idempotency (at-least-once delivery, no cross-publisher ordering —
// the `INotificationChannel` contract):** the revocation is naturally
// idempotent. `IShareTokenStore.Revoke` is itself idempotent
// (re-revoking an already-revoked token returns `Ok`), and the handler
// skips already-revoked claims before issuing the revoke, so
// re-receiving the same `Removed` event re-enumerates the now-revoked
// claims and does nothing. A duplicate or out-of-order delivery cannot
// over-revoke or resurrect a token.
//
// **Scope identity:** a team member's share-tokens are issued under the
// team's `StorageScope.ScopeId`, which is the raw team id (the
// `Container` is `team-{teamId}` but the `ScopeId` carried into
// `ShareTokenIssueRequest.ScopeId` is `teamId`). `MembershipChanged`
// carries the same raw `TeamId`, so `ListByIssuer(payload.TeamId, …)`
// targets the correct scope without re-deriving the container name.
//
// **Audit:** the decorator does not emit its own audit event — it
// passes `actor = "system:RevokeOnIssuerRemoved"` to `Revoke`, and the
// underlying store (the default `BlobShareTokenStore`) records the
// `ShareTokenRevoked` audit event with that actor. Keeping the
// decorator free of an `IAuditLog` dependency keeps it composable over
// any `IShareTokenStore` impl.
//
// **GP 12 audit:** identity-by-value (string scope / token / issuer ids
// only — no live handles); async-at-boundary (every delegated member
// returns the inner store's `Async<_>`); retry-as-data (revoke failures
// surface as logged `ShareTokenError` values, no callbacks); stateless
// between invocations (the handler reads everything from the event +
// inner store, holds no per-event state); no cross-shard ordering
// dependence (idempotent under reorder / redelivery); precision N/A
// (no timing primitive).

[<Literal>]
let RevocationActor = "system:RevokeOnIssuerRemoved"

/// Decorator wrapping an inner `IShareTokenStore`. Subscribes at
/// construction (compose time, before HTTP binds — mirrors
/// `DefaultSubjectResolver`'s subscription pattern) and unsubscribes on
/// `Dispose` (singleton teardown).
type RevokeOnIssuerRemovedStore(inner: IShareTokenStore, notifications: INotificationChannel, ?logger: ILogger) =

    let logInfo (msg: string) =
        match logger with
        | Some l -> l.Info msg
        | None -> ()

    let logWarn (msg: string) =
        match logger with
        | Some l -> l.Warn msg
        | None -> ()

    let logError (msg: string) (ex: exn) =
        match logger with
        | Some l -> l.Error(msg, Some ex)
        | None -> ()

    let revokeLeaversClaims (payload: MembershipChangedPayload) = async {
        try
            let! claims = inner.ListByIssuer(payload.TeamId, payload.AffectedUserId)
            let active = claims |> List.filter (fun c -> not c.Revoked)

            for claim in active do
                let! result = inner.Revoke(claim.ScopeId, claim.TokenId, RevocationActor)

                match result with
                | Ok() -> ()
                | Error err ->
                    logWarn
                        $"RevokeOnIssuerRemoved: revoke failed for token '{claim.TokenId}' in scope '{claim.ScopeId}' (issuer '{payload.AffectedUserId}'): %A{err}"

            if not active.IsEmpty then
                logInfo
                    $"RevokeOnIssuerRemoved: revoked {active.Length} share-token(s) for removed member '{payload.AffectedUserId}' of team '{payload.TeamId}'."
        with ex ->
            logError
                $"RevokeOnIssuerRemoved: failed to enumerate / revoke claims for removed member '{payload.AffectedUserId}' of team '{payload.TeamId}'."
                ex
    }

    let handle (envelope: NotificationEnvelope) =
        match envelope.Notification with
        | MembershipChanged payload ->
            match payload.ChangeKind with
            | MembershipChangeKind.Removed -> revokeLeaversClaims payload |> Async.Start
            | MembershipChangeKind.Added
            | MembershipChangeKind.RoleChanged
            | MembershipChangeKind.ActiveTeamSet -> ()
        | _ -> ()

    // Composition-time only: constructor runs once at decorator
    // application (compose) before HTTP binds, no ambient sync context
    // to deadlock against. Mirrors `DefaultSubjectResolver`'s pattern.
    let subscriptionId =
        notifications.Subscribe(NotificationKind.PlatformReservedScope, handle)
        |> Async.RunSynchronously

    interface IDisposable with
        // Shutdown only: registered as a singleton via the decorator
        // chain, so Dispose runs once on app teardown.
        member _.Dispose() =
            notifications.Unsubscribe subscriptionId |> Async.RunSynchronously

    interface IShareTokenStore with
        member _.Issue request = inner.Issue request
        member _.Validate token = inner.Validate token
        member _.MarkUsed(scopeId, tokenId) = inner.MarkUsed(scopeId, tokenId)

        member _.Revoke(scopeId, tokenId, actorUserId) =
            inner.Revoke(scopeId, tokenId, actorUserId)

        member _.ListByResource(scopeId, resourceKind, resourceId) =
            inner.ListByResource(scopeId, resourceKind, resourceId)

        member _.ListByIssuer(scopeId, issuerUserId) =
            inner.ListByIssuer(scopeId, issuerUserId)

/// Decorator factory for `ServerApp.withShareTokenStoreDecorator`.
/// Captures the notification channel (and optional logger) and returns
/// the `IShareTokenStore -> IShareTokenStore` wrapper the compose
/// pipeline folds around the resolved store.
///
/// Usage:
/// ```fsharp
/// app
/// |> ServerApp.withShareTokenStoreDecorator (RevokeOnIssuerRemoved.decorator notifications (Some logger))
/// ```
let decorator (notifications: INotificationChannel) (logger: ILogger option) : IShareTokenStore -> IShareTokenStore =
    fun inner -> new RevokeOnIssuerRemovedStore(inner, notifications, ?logger = logger) :> IShareTokenStore
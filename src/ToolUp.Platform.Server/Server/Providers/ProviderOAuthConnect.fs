// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ProviderOAuthConnect

open System
open ToolUp.Platform
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets

// ─── Phase 43.B — bind + refresh an OAuthConnected ProviderEntry ──
//
// The two halves of the S1 one-click-connect lifecycle, kept out of
// both the HTTP handler and the job handler so the SAME code runs on
// the interactive callback path and the scheduled refresh path. Each
// half is a plain function over injected substrate — no DI lookup, no
// `HttpContext` — which is what lets the contract pack exercise them
// with in-memory stores and no network.
//
// **Nothing here touches an `IAIProvider`.** A provider-profile entry
// is a platform concept (`ToolUp.Platform.Core`); the AI assistant is
// one consumer of it. Keeping the binding here rather than in
// `ToolUp.AI.Server` is what lets a non-AI BYOK consumer use one-click
// connect too, and keeps `ToolUp.Platform` AI-free (GP 1).
//
// **Stateless between calls (GP 12 rule 4).** Every function reads the
// profile and the secrets it needs on entry and writes them before
// returning. Nothing is cached across invocations, so a scheduled
// refresh survives a process recycle, an Orleans deactivation, or
// running on a different node than the one that connected.

/// Outcome of one scheduled refresh attempt for a single entry.
/// Retry / supervision as data (GP 12 rule 3) — the job handler
/// branches on the tag; no callback escapes.
type ProviderOAuthRefreshOutcome =
    /// The cached access token is not close enough to expiry yet. The
    /// overwhelmingly common case: one `ISecretStore.GetSecret` plus a
    /// `DateTime` comparison, then done.
    | NotDue
    /// Refresh succeeded; the new access token + expiry are persisted
    /// and the entry's health is `Healthy`.
    | Refreshed of newExpiry: DateTime
    /// The upstream will not mint a new token from the stored refresh
    /// token (revoked, `invalid_grant`, client credentials gone). The
    /// entry's health is now `NeedsReauthorization` and an audit event
    /// was emitted. Terminal for this entry until the user reconnects.
    | NeedsReauthorization of reason: string
    /// The attempt failed in a way that may recover — network trouble,
    /// upstream 5xx. Health is downgraded to `Degraded` with the error
    /// count bumped; the job substrate retries per its `JobRetryPolicy`.
    | TransientFailure of reason: string
    /// The entry is not OAuth-connected, or its binding is missing.
    /// Not an error the user can act on — the caller skips it.
    | NotOAuthConnected

/// Upsert an OAuth-connected entry into a profile. Preserves a prior
/// entry's `Tags`, `Model` and `Health` when one already exists under
/// the same label, so reconnecting an entry does not silently discard
/// the user's metadata or the probe's most recent verdict.
///
/// Returns `Error` when the label is taken by an entry with a
/// DIFFERENT origin. Overwriting a pasted-key entry with an
/// OAuth-connected one would orphan the user's stored API key while
/// looking like a successful connect, so the substrate refuses and
/// says which label is in the way.
let bindEntry
    (flow: IProviderOAuthFlow)
    (correlation: OAuthCorrelationKey)
    (connectedAt: DateTime)
    (profile: ProviderProfile)
    : Result<ProviderProfile, string> =
    let label = correlation.Id
    let prior = profile.Entries |> List.tryFind (fun e -> e.Label = label)

    match prior with
    | Some p when p.Origin <> CredentialOrigin.OAuthConnected ->
        Error
            $"A provider entry labelled '{label}' already exists with a pasted API key. Delete it first, or connect under a different label."
    | _ ->
        let binding: ProviderOAuthBinding = {
            FlowName = flow.Name
            Correlation = correlation
            ConnectedAt = connectedAt
        }

        let entry: ProviderEntry = {
            Label = label
            ProviderId = flow.ProviderId
            Model = prior |> Option.bind _.Model |> Option.orElse flow.DefaultModel
            SecretKeyName = ProviderOAuthKeys.refreshTokenKey flow.Name correlation
            Tags = prior |> Option.map _.Tags |> Option.defaultValue []
            Origin = CredentialOrigin.OAuthConnected
            Health = prior |> Option.map _.Health |> Option.defaultValue ProviderHealth.unknown
            OAuthBinding = Some binding
            UpdatedAt = connectedAt
        }

        Ok {
            profile with
                Entries = (profile.Entries |> List.filter (fun e -> e.Label <> label)) @ [ entry ]
                UpdatedAt = connectedAt
        }

/// Persist a completed Authorization Code exchange as an
/// `OAuthConnected` `ProviderEntry`.
///
/// Order is deliberate: the refresh token is written FIRST, then the
/// profile. A crash between the two leaves an orphan secret (harmless
/// — nothing references it) rather than an entry pointing at a
/// credential that was never stored (which resolves as a broken
/// provider on the next chat turn).
let completeConnect
    (providerProfile: IProviderProfile)
    (secretStore: ISecretStore)
    (auditLog: IAuditLog option)
    (scope: StorageScope)
    (userId: string)
    (flow: IProviderOAuthFlow)
    (correlation: OAuthCorrelationKey)
    (credentials: OAuthCredentials)
    (connectedAt: DateTime)
    : Async<Result<unit, string>> =
    async {
        if not (OAuthCorrelationKey.isWellFormed correlation) then
            return Error $"Invalid provider correlation key '{OAuthCorrelationKey.render correlation}'."
        else
            let refreshKey = ProviderOAuthKeys.refreshTokenKey flow.Name correlation
            let! stored = secretStore.SetSecret(scope.Container, refreshKey, credentials.RefreshToken)

            match stored with
            | Error e -> return Error $"Failed to persist refresh token: {e}"
            | Ok() ->
                // Seed the access-token cache when the exchange
                // already minted one, so the first consumer call does
                // not pay a refresh round-trip it does not need.
                do!
                    match credentials.AccessToken, credentials.ExpiresAt with
                    | Some token, Some expiry -> async {
                        let! _ =
                            secretStore.SetSecret(
                                scope.Container,
                                ProviderOAuthKeys.accessTokenKey flow.Name correlation,
                                token
                            )

                        let! _ =
                            secretStore.SetSecret(
                                scope.Container,
                                ProviderOAuthKeys.accessExpiryKey flow.Name correlation,
                                expiry.ToUniversalTime().ToString("o")
                            )

                        return ()
                      }
                    | _ -> async { return () }

                let! existing = providerProfile.Get scope
                let current = existing |> Option.defaultValue (ProviderProfile.empty ())

                match bindEntry flow correlation connectedAt current with
                | Error e -> return Error e
                | Ok updated ->
                    let! saved = providerProfile.Set(scope, updated)

                    match saved with
                    | Error e -> return Error e
                    | Ok() ->
                        // Best-effort audit. `OAuthConnected` is reused
                        // rather than a new audit case being appended:
                        // `AuditEvent` is a large DU matched
                        // exhaustively across the SDK and every sink,
                        // and this event carries exactly the same
                        // facts. Its `DataSourceId` field carries the
                        // RENDERED correlation key
                        // (`provider-entry:{label}`) so a reader can
                        // tell the two families apart without a schema
                        // change.
                        match auditLog with
                        | Some log ->
                            log.Record(
                                scope.ScopeId,
                                OAuthConnected {
                                    UserId = userId
                                    ScopeId = scope.ScopeId
                                    FlowName = flow.Name
                                    DataSourceId = OAuthCorrelationKey.render correlation
                                    ConnectedAt = connectedAt
                                }
                            )
                            |> Async.Start
                        | None -> ()

                        return Ok()
    }

/// Classify a refresh-path `OAuthError`. `NetworkError` is the only
/// case that may recover on its own; everything else needs either an
/// operator (missing client credentials) or the user (a revoked
/// grant), and in both cases the honest signal to the UI is
/// "reconnect" rather than a silently-degrading entry.
let private classifyRefreshError (err: OAuthError) : ProviderOAuthRefreshOutcome =
    match err with
    | NetworkError msg -> TransientFailure msg
    | ProviderRejected msg -> NeedsReauthorization msg
    | ClientCredentialMissing key -> NeedsReauthorization $"OAuth client credential missing: {key}"
    | StateMismatch msg -> NeedsReauthorization msg
    | RevocationUnsupported -> TransientFailure "revocation unsupported"
    | OAuthFlowFailed msg -> NeedsReauthorization msg

/// Default lead time before access-token expiry at which the refresh
/// job fires. Five minutes, matching the Phase 10h
/// `OAuthRefreshDescriptor` default and for the same reason: the
/// scheduler's floor is `JobPrecision.Minute`, so the window has to
/// absorb worst-case tick latency plus one transient retry.
let DefaultLeadTime: TimeSpan = TimeSpan.FromMinutes 5.0

/// Flip an entry to `NeedsReauthorization` and emit the audit event.
/// Health is written through `SetEntryHealth` (never `Set`) so a user
/// editing routing in the same instant is not clobbered by the
/// background job.
let private markNeedsReauthorization
    (providerProfile: IProviderProfile)
    (auditLog: IAuditLog option)
    (scope: StorageScope)
    (entry: ProviderEntry)
    (flow: IProviderOAuthFlow)
    (reason: string)
    : Async<unit> =
    async {
        let! _ =
            providerProfile.SetEntryHealth(
                scope,
                entry.Label,
                {
                    entry.Health with
                        RecentErrorCount = entry.Health.RecentErrorCount + 1
                        Status = ProviderHealthStatus.NeedsReauthorization
                }
            )

        match auditLog with
        | Some log ->
            // `OAuthRefreshFailed` is the shipped audit case for
            // exactly this transition (its own doc comment says the
            // substrate flips the credential to
            // `NeedsReauthorization`). `UserId = "system"` per that
            // payload's convention for a scheduled refresh.
            do!
                log.Record(
                    scope.ScopeId,
                    OAuthRefreshFailed {
                        UserId = "system"
                        ScopeId = scope.ScopeId
                        FlowName = flow.Name
                        DataSourceId = OAuthCorrelationKey.render (OAuthCorrelationKey.providerEntry entry.Label)
                        Reason = reason
                    }
                )
        | None -> ()
    }

/// Run one proactive refresh attempt for a single OAuth-connected
/// entry.
///
/// `leadTime` is how far ahead of expiry the refresh fires. The
/// scheduler's floor is `JobPrecision.Minute`, so a lead time shorter
/// than a couple of minutes risks the token expiring between two
/// ticks — `DefaultLeadTime` (5 minutes) matches the Phase 10h
/// `OAuthRefreshDescriptor` default for the same reason.
///
/// `now` is passed in rather than read from the clock so the contract
/// pack can drive the window deterministically (GP 12 rule 6: the
/// timing contract is explicit, never implicit).
let refreshEntry
    (providerProfile: IProviderProfile)
    (secretStore: ISecretStore)
    (auditLog: IAuditLog option)
    (scope: StorageScope)
    (flow: IProviderOAuthFlow)
    (entry: ProviderEntry)
    (now: DateTime)
    (leadTime: TimeSpan)
    : Async<ProviderOAuthRefreshOutcome> =
    async {
        match ProviderEntry.oauthBinding entry with
        | None -> return NotOAuthConnected
        | Some binding ->
            let correlation = binding.Correlation
            let expiryKey = ProviderOAuthKeys.accessExpiryKey flow.Name correlation
            let! cachedExpiry = secretStore.GetSecret(scope.Container, expiryKey)

            // No cached expiry at all → treat the window as open, so
            // the first tick after a connect seeds it. A corrupt value
            // does the same rather than pinning the entry at "not due"
            // forever.
            let windowOpen =
                match cachedExpiry with
                | None -> true
                | Some iso ->
                    match
                        DateTime.TryParse(
                            iso,
                            Globalization.CultureInfo.InvariantCulture,
                            Globalization.DateTimeStyles.RoundtripKind
                        )
                    with
                    | true, expiry -> (expiry.ToUniversalTime() - now) <= leadTime
                    | _ -> true

            if not windowOpen then
                return NotDue
            else
                let refreshKey = ProviderOAuthKeys.refreshTokenKey flow.Name correlation
                let! refreshToken = secretStore.GetSecret(scope.Container, refreshKey)

                match refreshToken with
                | None ->
                    let reason = $"No refresh token is stored under '{refreshKey}'."
                    do! markNeedsReauthorization providerProfile auditLog scope entry flow reason
                    return NeedsReauthorization reason
                | Some token ->
                    let ctx = OAuthFlowContext.forCorrelation scope.Container correlation
                    let! result = flow.RefreshAccessToken(ctx, token)

                    match result with
                    | Ok minted ->
                        let! _ =
                            secretStore.SetSecret(
                                scope.Container,
                                ProviderOAuthKeys.accessTokenKey flow.Name correlation,
                                minted.Token
                            )

                        let! _ =
                            secretStore.SetSecret(
                                scope.Container,
                                expiryKey,
                                minted.ExpiresAt.ToUniversalTime().ToString("o")
                            )

                        let! _ =
                            providerProfile.SetEntryHealth(
                                scope,
                                entry.Label,
                                {
                                    LastVerifiedAt = Some now
                                    RecentErrorCount = 0
                                    RateLimitHeadroom = entry.Health.RateLimitHeadroom
                                    Status = ProviderHealthStatus.Healthy
                                }
                            )

                        return Refreshed minted.ExpiresAt
                    | Error err ->
                        match classifyRefreshError err with
                        | NeedsReauthorization reason ->
                            do! markNeedsReauthorization providerProfile auditLog scope entry flow reason
                            return NeedsReauthorization reason
                        | outcome ->
                            let reason =
                                match outcome with
                                | TransientFailure r -> r
                                | _ -> OAuthError.toMessage err

                            let! _ =
                                providerProfile.SetEntryHealth(
                                    scope,
                                    entry.Label,
                                    {
                                        entry.Health with
                                            RecentErrorCount = entry.Health.RecentErrorCount + 1
                                            Status = ProviderHealthStatus.Degraded
                                    }
                                )

                            return TransientFailure reason
    }
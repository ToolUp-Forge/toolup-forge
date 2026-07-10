module ToolUp.Platform.PlatformTenantApiHandler

open System.Collections.Concurrent
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 54 — IPlatformTenantApi server handler ────────────────────
//
// Drives the tenant-lifecycle aggregator from the
// `/api/_platform/tenants/*` admin surface. Per-request DI resolution
// (mirrors the other `_platform` admin handlers): the registered
// `ITenantLifecycle` hooks, `IAuditLog`, the process-local
// `TenantLifecycleSnapshot`, and the caller's `AccessContext` are all
// resolved per call so a substrate that isn't wired can't cascade a
// failure into route construction.
//
// **Server-authoritative actor.** The wire `actorUserId` parameter is
// advisory (API shape / forward compat); the handler pins the *actual*
// actor to the authenticated caller's `AccessContext.UserId` so the
// audit trail can't be spoofed by a client-supplied id. The whole
// surface is Owner / Platform-Admin gated via
// `AccessContext.canModifyPlatformConfig`; a missing `AccessContext`
// (no resolved auth) is treated as non-admin — fail-closed.

/// Process-local holder of the most-recent `LifecycleSummary` per scope.
/// Registered as a DI singleton when `TenantLifecycle` is enabled.
///
/// **Phase 54e — this is now a read-through cache** in front of the
/// durable `ILifecycleSummaryStore`: written after each run alongside the
/// durable store, and read first by `GetLifecycleSummary` (a miss reads
/// the durable store and back-fills here). On a fresh replica or after a
/// restart the cache is cold, so the read falls through to the durable
/// store — that is the cross-replica/restart-survival fix. When no
/// durable store is registered (minimal/test wiring) this remains the
/// sole backing, preserving prior Phase 54 process-local behaviour. The
/// audit trail (`TenantProvisioned` / `TenantDeprovisioned`) remains the
/// durable record of the *fact* a run happened.
type TenantLifecycleSnapshot() =
    let last = ConcurrentDictionary<string, LifecycleSummary>()

    member _.Set(scopeId: string, summary: LifecycleSummary) = last[scopeId] <- summary

    member _.Get(scopeId: string) : LifecycleSummary option =
        match last.TryGetValue scopeId with
        | true, s -> Some s
        | false, _ -> None

/// Uniform refusal message for non-admin callers — matches the
/// `PlatformAdminApi` banner so the client surfaces one consistent error.
let adminError = "platform admin role required"

// ─── Phase 54i — offboard confirmation gate constants ────────────────

/// Uniform refusal surfaced to the client whenever a confirmation-gated
/// offboard is blocked at the gate (token-less call under a confirmation
/// mode, or a missing/expired/wrong-scope/self-approved token). The
/// specific cause goes to the audit trail; the client sees one banner so
/// it can't enumerate which tokens exist (mirrors the share-token
/// "indistinguishable failure" posture).
let offboardConfirmationRequired = "offboard confirmation required"

/// Clear error when a confirmation mode is configured but no
/// `IShareTokenStore` is composed to mint/redeem tokens.
let offboardConfirmationNoStore =
    "offboard confirmation requires an IShareTokenStore (compose the share-token substrate)"

/// Phase 543 — clear error when principal enumeration lacks its
/// substrate: the derived sweep reads membership blobs (`IBlobStorage`)
/// and scope events (`IEventStore`), so a wiring without both cannot
/// enumerate honestly — fail closed with the remedy, never return a
/// silently-partial list.
let principalEnumerationNoSubstrate =
    "principal enumeration requires IBlobStorage + IEventStore (compose the storage and event-store substrate)"

/// Phase 54h — returned to the loser when a distributed `ILifecycleLock`
/// reports another replica already mid-offboard of this scope. Under the
/// in-process default this never surfaces (same-scope offboards serialise);
/// it only appears in a multi-replica deployment that composed a
/// cross-replica lock.
let offboardAlreadyInProgress = "offboard already in progress for this scope"

/// `ResourceKind` the confirmation tokens are minted under. Namespaced so
/// a confirmation token can never be confused with a Forms / share-link
/// token even within the same `IShareTokenStore`.
[<Literal>]
let offboardResourceKind = "_platform.tenant-offboard"

/// Confirmation-token validity window. Short by design — long enough to
/// fetch a second admin under `TwoPersonRule`, short enough that a minted
/// token isn't a standing armed-teardown capability.
let offboardConfirmationWindow = System.TimeSpan.FromMinutes 15.0

/// Build the `IPlatformTenantApi` for one request.
let platformTenantApi (ctx: HttpContext) : IPlatformTenantApi =
    let services = ctx.RequestServices

    let resolveHooks () =
        match services.GetService(typeof<seq<ITenantLifecycle>>) with
        | :? seq<ITenantLifecycle> as hooks -> List.ofSeq hooks
        | _ -> []

    let auditLog =
        match services.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as a -> Some a
        | _ -> None

    let snapshot =
        match services.GetService(typeof<TenantLifecycleSnapshot>) with
        | :? TenantLifecycleSnapshot as s -> s
        | _ -> TenantLifecycleSnapshot()

    // Phase 54e — durable backing for the last summary. Registered under
    // `EnabledTenantLifecycle`; `None` only in minimal/test wiring, where
    // the process-local snapshot remains the sole backing (prior Phase 54
    // behaviour). The snapshot is a read-through cache in front of it.
    let summaryStore =
        match services.GetService(typeof<ILifecycleSummaryStore>) with
        | :? ILifecycleSummaryStore as s -> Some s
        | _ -> None

    let accessContext =
        match services.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> Some ac
        | _ -> None

    // Phase 54a — optional background-job substrate. `None` when no
    // scheduler is composed; `DeprovisionTenantAsync` then returns a clear
    // "requires an IJobScheduler" error and the inline paths are
    // unaffected (GP 13).
    let scheduler =
        match services.GetService(typeof<IJobScheduler>) with
        | :? IJobScheduler as s -> Some s
        | _ -> None

    // Phase 54i — offboard confirmation gate. `confirmationMode` reads the
    // deployment's `ServerConfig` (default `NoConfirmation` when absent —
    // minimal/test wiring, which then behaves exactly as Phase 54).
    // `shareTokens` is `None` unless the Phase 21b share-token substrate is
    // composed; under a confirmation mode the mint / redeem methods then
    // return `offboardConfirmationNoStore`.
    let confirmationMode =
        match services.GetService(typeof<ServerConfig>) with
        | :? ServerConfig as c -> c.TenantOffboardConfirmation
        | _ -> NoConfirmation

    let shareTokens =
        match services.GetService(typeof<IShareTokenStore>) with
        | :? IShareTokenStore as s -> Some s
        | _ -> None

    // Phase 543 — substrate for the derived principal enumeration.
    // Both `None` only in minimal/test wiring; `ListPrincipals` then
    // returns `principalEnumerationNoSubstrate` rather than a
    // silently-partial list.
    let blobStorage =
        match services.GetService(typeof<IBlobStorage>) with
        | :? IBlobStorage as s -> Some s
        | _ -> None

    let eventStore =
        match services.GetService(typeof<IEventStore>) with
        | :? IEventStore as s -> Some s
        | _ -> None

    // Phase 54b — completed-step offboard ledger. A fresh provision of a
    // scope supersedes any prior offboard ledger (re-onboarding resets
    // it), so the inline provision path clears the Deprovisioning ledger.
    // `None` in minimal/test wiring — then the clear is a no-op.
    let ledger =
        match services.GetService(typeof<ILifecycleLedger>) with
        | :? ILifecycleLedger as l -> Some l
        | _ -> None

    // Phase 54h — cross-replica offboard exclusion lock. Resolves the
    // composed `ILifecycleLock` (a distributed impl in a multi-replica
    // deployment); falls back to the process-local `InProcessLifecycleLock`
    // default when none is registered — minimal/test wiring, which then
    // serialises same-scope offboards exactly as Phase 54 did.
    let lifecycleLock =
        match services.GetService(typeof<ILifecycleLock>) with
        | :? ILifecycleLock as l -> l
        | _ -> InProcessLifecycleLock.shared

    let clearOffboardLedger (scopeId: string) = async {
        match ledger with
        | Some l ->
            try
                do! l.Clear(scopeId, Deprovisioning)
            with _ ->
                () // best-effort — a stale ledger only costs a redundant skip
        | None -> ()
    }

    // Server-authoritative actor — the authenticated caller, never the
    // wire-supplied id. Empty when no AccessContext resolved (which also
    // fails the admin gate below).
    let actor = accessContext |> Option.map _.UserId |> Option.defaultValue ""

    let isAdmin =
        accessContext
        |> Option.map AccessContext.canModifyPlatformConfig
        |> Option.defaultValue false

    // Wire the aggregator's audit-emit seam to IAuditLog.Record (a no-op
    // when audit is off). IAuditLog.Record is best-effort by contract —
    // a sink failure can't fail an offboard.
    let emitAudit (scopeId: string) (event: AuditEvent) =
        match auditLog with
        | Some a -> a.Record(scopeId, event)
        | None -> async { return () }

    // Phase 54e — persist the run's summary durably (best-effort) and
    // refresh the process-local cache. A durable-store failure must not
    // fail an offboard: the hooks already ran and the audit trail already
    // recorded the run, so a blob-write outage degrades to "the admin UI
    // reads a stale/empty summary", never "the offboard errored".
    let persist (scopeId: string) (summary: LifecycleSummary) = async {
        match summaryStore with
        | Some store ->
            try
                do! store.SetLast(scopeId, summary)
            with _ ->
                () // swallowed by contract — see comment above
        | None -> ()

        snapshot.Set(scopeId, summary)
    }

    // Phase 54e — read-through cache. The process-local snapshot answers a
    // hit directly; on a miss (fresh replica, post-restart process) read
    // the durable store and populate the cache so subsequent reads are
    // local. With no durable store registered this is the prior Phase 54
    // process-local-only behaviour.
    let readSummary (scopeId: string) : Async<LifecycleSummary option> = async {
        match snapshot.Get scopeId with
        | Some s -> return Some s
        | None ->
            match summaryStore with
            | Some store ->
                match! store.GetLast scopeId with
                | Some s ->
                    snapshot.Set(scopeId, s)
                    return Some s
                | None -> return None
            | None -> return None
    }

    // Phase 54i — emit a refusal audit row + return the uniform refusal
    // *message* (the caller wraps it in `Error`). The specific `reason`
    // goes to the audit trail; the returned banner is uniform.
    let refuseOffboard (scopeId: string) (reason: string) : Async<string> = async {
        do!
            emitAudit
                scopeId
                (AuditEvent.TenantOffboardConfirmationRefused {
                    ScopeId = scopeId
                    Actor = actor
                    Reason = reason
                })

        return offboardConfirmationRequired
    }

    // Phase 54i — the gate the token-less destructive paths consult. Under
    // a confirmation mode every token-less destructive call is refused
    // (the operator must mint a token + use `DeprovisionTenantConfirmed`);
    // under `NoConfirmation` it is a pass-through (`None` = proceed).
    let confirmationGate (scopeId: string) : Async<string option> = async {
        if OffboardConfirmationMode.requiresToken confirmationMode then
            let! banner = refuseOffboard scopeId "confirmation required (token-less offboard blocked)"
            return Some banner
        else
            return None
    }

    // Phase 54i — map a share-token validation failure to an audit reason.
    let validationReason =
        function
        | ShareTokenError.Expired -> "token expired"
        | ShareTokenError.RevokedToken -> "token revoked"
        | ShareTokenError.UseLimitExceeded -> "token already used"
        | ShareTokenError.NotFound
        | ShareTokenError.InvalidSignature
        | ShareTokenError.Malformed -> "token invalid"
        | ShareTokenError.RateLimited -> "token rate-limited"
        | ShareTokenError.StorageFailed _ -> "token store unavailable"

    // Phase 54i — run the (already-authorised, already-confirmed) offboard
    // inline and persist, returning the summary. Shared by the confirmed
    // path; identical aggregator call to `DeprovisionTenant`.
    //
    // Phase 54h — guarded by the resolved `ILifecycleLock`. Under a
    // distributed lock a concurrent replica mid-offboard of this scope
    // yields `AlreadyInProgress`, surfaced as `Error
    // offboardAlreadyInProgress`; the in-process default serialises and so
    // always `Ran` (Phase 54 behaviour). Persists only on a run.
    let runConfirmedOffboard (scopeId: string) : Async<Result<LifecycleSummary, string>> = async {
        match!
            TenantLifecycleAggregator.runGuardedWith
                lifecycleLock
                emitAudit
                (resolveHooks ())
                Deprovisioning
                scopeId
                actor
        with
        | TenantLifecycleAggregator.GuardedRun.AlreadyInProgress -> return Error offboardAlreadyInProgress
        | TenantLifecycleAggregator.GuardedRun.Ran summary ->
            do! persist scopeId summary
            return Ok summary
    }

    {
        ProvisionTenant =
            fun (scopeId, _wireActor, request) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    // Phase 305 — thread the deploy-plane ProvisioningRequest
                    // through to the hooks so request-aware hooks
                    // (ConfigSeed / OwnerTeamBootstrap) seed per-deployment
                    // values + the explicit owner rather than schema defaults
                    // / the acting admin. Hooks that don't opt into
                    // ITenantLifecycleProvisionContext are unaffected.
                    let! summary =
                        TenantLifecycleAggregator.runGuardedRequest
                            emitAudit
                            (Some request)
                            (resolveHooks ())
                            Provisioning
                            scopeId
                            actor

                    do! persist scopeId summary
                    // Phase 54b — re-onboarding supersedes a prior offboard
                    // ledger so a future offboard of this scope starts fresh.
                    do! clearOffboardLedger scopeId
                    return Ok summary
            }

        DeprovisionTenant =
            fun (scopeId, _wireActor, _reason) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    match! confirmationGate scopeId with
                    | Some banner -> return Error banner
                    | None -> return! runConfirmedOffboard scopeId
            }

        GetLifecycleSummary =
            fun scopeId -> async {
                if not isAdmin then
                    return Error adminError
                else
                    let! summary = readSummary scopeId
                    return Ok summary
            }

        // Phase 54a — explicit inline offboard. Same path as
        // `DeprovisionTenant` above (runGuarded inline + persist); named so
        // callers/tests can request the inline path unambiguously.
        DeprovisionTenantSync =
            fun (scopeId, _wireActor, _reason) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    match! confirmationGate scopeId with
                    | Some banner -> return Error banner
                    | None -> return! runConfirmedOffboard scopeId
            }

        // Phase 54a — background offboard. Enqueues + fires a lifecycle
        // job and returns the handle promptly; the background
        // `LifecycleJobHandler` runs the resumable sweep and persists the
        // summary as it progresses. Requires a composed `IJobScheduler`.
        DeprovisionTenantAsync =
            fun (scopeId, _wireActor, _reason) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    match! confirmationGate scopeId with
                    | Some banner -> return Error banner
                    | None ->
                        match scheduler with
                        | None ->
                            return
                                Error
                                    "background offboard requires an IJobScheduler (compose JobScheduler = InProcessJobScheduler); use DeprovisionTenant / DeprovisionTenantSync for the inline path"
                        | Some sch -> return! TenantLifecycleAggregator.enqueue sch Deprovisioning scopeId actor
            }

        // Phase 54c — read-only offboard preview. No mutation, no
        // destructive audit; aggregates each hook's would-affect item.
        PreviewDeprovision =
            fun scopeId -> async {
                if not isAdmin then
                    return Error adminError
                else
                    let! preview = TenantLifecycleAggregator.previewDeprovision (resolveHooks ()) scopeId actor
                    return Ok preview
            }

        // Phase 54j — export-then-erase. The export step resolves
        // IDataExporter / IBlobStorage from DI (via exportArchive); the
        // aggregator enforces the fail-closed ordering (export durably,
        // audit, then erase). Persist the erasure summary on success.
        ExportThenDeprovision =
            fun (scopeId, _wireActor, _reason) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    match! confirmationGate scopeId with
                    | Some banner -> return Error banner
                    | None ->
                        let runExport () =
                            DataSubjectRequestLifecycle.exportArchive services scopeId

                        // Phase 54h — guard the whole export-then-erase
                        // bundle by the resolved `ILifecycleLock`. Under a
                        // distributed lock a peer replica mid-bundle yields
                        // `AlreadyInProgress` (surfaced as the same
                        // `offboardAlreadyInProgress` error the inline
                        // offboard path returns); the in-process default
                        // serialises and so never refuses. Persists only on a
                        // completed run.
                        match!
                            TenantLifecycleAggregator.exportThenDeprovisionWith
                                lifecycleLock
                                emitAudit
                                runExport
                                (resolveHooks ())
                                scopeId
                                actor
                        with
                        | TenantLifecycleAggregator.GuardedExportThenDeprovision.AlreadyInProgress ->
                            return Error offboardAlreadyInProgress
                        | TenantLifecycleAggregator.GuardedExportThenDeprovision.ExportFailed e -> return Error e
                        | TenantLifecycleAggregator.GuardedExportThenDeprovision.Ran result ->
                            do! persist scopeId result.Summary
                            return Ok result
            }

        // Phase 54i — mint a short-lived confirmation token (one-time,
        // in-scope) for a pending offboard. Requires the share-token
        // substrate; emits `TenantOffboardConfirmationRequested`.
        RequestDeprovisionToken =
            fun (scopeId, reason) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    match shareTokens with
                    | None -> return Error offboardConfirmationNoStore
                    | Some store ->
                        let request: ShareTokenIssueRequest = {
                            ScopeId = scopeId
                            ResourceKind = offboardResourceKind
                            ResourceId = scopeId
                            // The reason rides the attributed-handle slot so
                            // the redeeming admin / audit can recover it.
                            AttributedHandle = Some reason
                            IssuedBy = actor
                            ExpiresAt = Some(System.DateTimeOffset.UtcNow.Add offboardConfirmationWindow)
                            UseLimit = Some(Some 1)
                            RateLimit = None
                        }

                        match! store.Issue request with
                        | Error e -> return Error(sprintf "failed to mint offboard confirmation token: %A" e)
                        | Ok token ->
                            do!
                                emitAudit
                                    scopeId
                                    (AuditEvent.TenantOffboardConfirmationRequested {
                                        ScopeId = scopeId
                                        RequestedBy = actor
                                        Reason = reason
                                        ExpiresAt = token.Claim.ExpiresAt
                                    })

                            return
                                Ok {
                                    Token = token.Token
                                    ScopeId = scopeId
                                    Reason = reason
                                    RequestedBy = actor
                                    ExpiresAt = token.Claim.ExpiresAt
                                }
            }

        // Phase 54i — redeem a confirmation token and run the offboard.
        // Validates scope + freshness + (under TwoPersonRule) requester ≠
        // redeemer, consumes the one-time token, then runs the same
        // offboard as `DeprovisionTenant`. Every refusal is audited before
        // any destruction.
        DeprovisionTenantConfirmed =
            fun (scopeId, _wireActor, _reason, confirmationToken) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    match shareTokens with
                    | None -> return Error offboardConfirmationNoStore
                    | Some store ->
                        match! store.Validate confirmationToken with
                        | Error e ->
                            let! banner = refuseOffboard scopeId (validationReason e)
                            return Error banner
                        | Ok claim ->
                            // Scope-binding: the token must have been minted
                            // for THIS scope's offboard, not reused from
                            // another scope / another surface.
                            if
                                claim.ScopeId <> scopeId
                                || claim.ResourceKind <> offboardResourceKind
                                || claim.ResourceId <> scopeId
                            then
                                let! banner = refuseOffboard scopeId "token scope mismatch"
                                return Error banner
                            elif confirmationMode = TwoPersonRule && claim.IssuedBy = actor then
                                let! banner = refuseOffboard scopeId "two-person rule: requester cannot self-approve"

                                return Error banner
                            else
                                // Consume the one-time token BEFORE the
                                // destructive run so a concurrent / replayed
                                // redemption of the same token is rejected.
                                match! store.MarkUsed(claim.ScopeId, claim.TokenId) with
                                | Error e ->
                                    let! banner = refuseOffboard scopeId (validationReason e)
                                    return Error banner
                                | Ok() ->
                                    match! runConfirmedOffboard scopeId with
                                    | Error e -> return Error e
                                    | Ok summary ->
                                        do!
                                            emitAudit
                                                scopeId
                                                (AuditEvent.TenantOffboardConfirmationApproved {
                                                    ScopeId = scopeId
                                                    RequestedBy = claim.IssuedBy
                                                    ApprovedBy = actor
                                                })

                                        return Ok summary
            }

        // Phase 54f — schedule a grace-period offboard. Registers an
        // IJobScheduler poll job; the offboard fires after the window
        // unless cancelled. Requires a composed IJobScheduler.
        ScheduleDeprovision =
            fun (scopeId, _wireActor, afterDays, reason) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    match scheduler with
                    | None ->
                        return
                            Error
                                "scheduled offboard requires an IJobScheduler (compose JobScheduler = InProcessJobScheduler); use DeprovisionTenant for the immediate path"
                    | Some sch ->
                        match! TenantLifecycleAggregator.scheduleDeprovision sch scopeId actor afterDays reason with
                        | Error e -> return Error e
                        | Ok sd ->
                            do!
                                emitAudit
                                    scopeId
                                    (AuditEvent.TenantDeprovisionScheduled {
                                        ScopeId = scopeId
                                        RequestedBy = actor
                                        Reason = reason
                                        DueAt = sd.DueAt
                                        JobId = sd.JobId
                                    })

                            return Ok sd
            }

        // Phase 54f — cancel a pending grace-period offboard. Idempotent:
        // no pending offboard (or no scheduler) is a successful no-op.
        CancelScheduledDeprovision =
            fun scopeId -> async {
                if not isAdmin then
                    return Error adminError
                else
                    match scheduler with
                    | None -> return Ok()
                    | Some sch ->
                        match! TenantLifecycleAggregator.cancelScheduledDeprovision sch scopeId with
                        | None -> return Ok() // nothing pending — idempotent
                        | Some sd ->
                            do!
                                emitAudit
                                    scopeId
                                    (AuditEvent.TenantDeprovisionCancelled {
                                        ScopeId = scopeId
                                        CancelledBy = actor
                                        Reason = sd.Reason
                                    })

                            return Ok()
            }

        // Phase 54f — read the pending grace-period offboard (read-only).
        GetScheduledDeprovision =
            fun scopeId -> async {
                if not isAdmin then
                    return Error adminError
                else
                    match scheduler with
                    | None -> return Ok None
                    | Some sch ->
                        let! sd = TenantLifecycleAggregator.getScheduledDeprovision sch scopeId
                        return Ok sd
            }

        // Phase 543 — derived principal enumeration (read-only, no
        // audit, no mutation). Admin-gated like every other method on
        // this contract; fails closed with a clear remedy when the
        // storage / event-store substrate is not composed.
        ListPrincipals =
            fun () -> async {
                if not isAdmin then
                    return Error adminError
                else
                    match blobStorage, eventStore with
                    | Some storage, Some events ->
                        let! principals = PrincipalRegistry.listPrincipals storage events
                        return Ok principals
                    | _ -> return Error principalEnumerationNoSubstrate
            }
    }
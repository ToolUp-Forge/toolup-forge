// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AnonymousSessionMigration

open System
open System.Collections.Concurrent
open System.Threading
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory
open ToolUp.Platform

// ─── AnonymousSessionMigrationMiddleware — Phase 66 Stream C.1 ───────
//                                          (continuation)
//
// Closes the loop on the `IAnonymousSessionMigrator` substrate (the
// interface + summary / error types + no-op default shipped in the
// C.1 substrate slice). This is the trigger-detection middleware that
// fires the migrator on the **first authenticated request that
// follows an anonymous session in the same browser**.
//
// **Pipeline placement.** Registered AFTER `SurfaceEnforcementMiddleware`
// (`ConfigurePipeline.fs`) so only requests that have already passed
// the per-route surface gate reach the migrator — an anonymous caller
// rejected with `401 authentication_required` never triggers a
// migration attempt. By this point `ScopeResolutionMiddleware` has
// stashed the resolved `Subject` and (on `Ok`) the derived
// `StorageScope` on `HttpContext.Items`.
//
// **Trigger semantics (design §3.7, six steps).**
//   1. Recover the anonymous session id from the sealed binding cookie
//      (Phase 337 — was the `X-User-Id` header through Phase 135). A
//      browser that previously held an anonymous session still presents
//      that cookie alongside its new `Authorization: Bearer` credential,
//      so the pre-sign-in session is identified by a value this server
//      issued rather than one the client asserts.
//   2. Resolve the authenticated target user id from the stashed
//      `Subject` (`AuthenticatedUser uid` / `TeamMember(uid, _)` only —
//      `AnonymousSession` and `ClaimBearer` are not migration targets).
//      Compare the inbound session id to `LastSeenAnonymousSessionId`
//      (tracked per user in `IMemoryCache`); invoke the migrator only
//      when they differ AND the session id is not itself the user id
//      (which it is under `HeaderAuthProvider`, where `X-User-Id` is the
//      authenticated identity — there is no separate anonymous session
//      to migrate in that case).
//   3. `Ok _`            → update `LastSeenAnonymousSessionId`.
//   4. `NotEligible _`   → update `LastSeenAnonymousSessionId` (the
//                          migrator declined for a benign reason — no
//                          data, wrong shape, already migrated; do not
//                          re-attempt on every subsequent request).
//   5. `PartialFailure _`→ update `LastSeenAnonymousSessionId` + audit
//                          (per OQ5: item failures are not generically
//                          retriable, so updating prevents thrash; the
//                          audit row carries the migrated-vs-failed
//                          split for the operator runbook).
//   6. `InfrastructureFailed _` → do NOT update (the next authenticated
//                          request from the same user retries).
//
// **Why `IMemoryCache` for `LastSeenAnonymousSessionId`.** The SDK owns
// no per-user persistent record — authentication is delegated to an
// external `IAuthProvider`, the default `IUserClaims` is a NoOp that
// persists nothing, and `IConfigStore` has no schema for a per-user
// "last seen session" write. `IMemoryCache` is the pragmatic store: a
// 24-hour sliding entry per user is long enough to suppress re-attempts
// across a working session, and migrator idempotency makes a re-attempt
// after a process restart (cache loss) harmless.
//
// **Double-migration guard.** A static `ConcurrentDictionary<string,
// SemaphoreSlim>` provides a per-user-id lock. Two concurrent first
// requests from the same browser (parallel XHRs on page load) would
// otherwise both observe an empty `LastSeenAnonymousSessionId` and both
// invoke the migrator. The semaphore serialises them; the second waiter
// re-reads `LastSeenAnonymousSessionId` inside the lock (double-checked)
// and finds the first request already recorded the migration, so it
// skips. The semaphore is per user id, so unrelated users never
// contend.
//
// **Inline, not fire-and-forget.** Unlike the login-audit side effect
// (`ScopeResolutionMiddleware`), the migration is awaited before the
// request proceeds to its handler — the first authenticated request is
// typically the user loading their own data, and the migrated items
// must be present in the user scope before the handler reads it.

/// Cache-key prefix for the per-user `LastSeenAnonymousSessionId`
/// record. Keyed by authenticated user id; value is the last anonymous
/// session id this user's migration was attempted against.
[<Literal>]
let private LastSeenKeyPrefix = "anon-migrate:lastseen:"

/// Stashed-`Subject` items key (mirrors `SurfaceEnforcement
/// .SubjectItemsKey`; duplicated as a literal because this module
/// compiles after `SurfaceEnforcementMiddleware.fs` and re-`open`ing it
/// only to read one literal adds a needless module dependency).
[<Literal>]
let private SubjectItemsKey = "ToolUp.Subject"

/// Stashed-`StorageScope` items key (set by `ScopeResolutionMiddleware`
/// only on `Ok`). Used to attribute the migration audit row to the
/// resolved scope; falls back to `user-{uid}` when absent.
[<Literal>]
let private StorageScopeItemsKey = "ToolUp.StorageScope"

/// Per-user-id locks for the double-migration guard. Static so the lock
/// set is shared across every request for the process lifetime.
let private userLocks = ConcurrentDictionary<string, SemaphoreSlim>()

let private lockFor (userId: string) : SemaphoreSlim =
    userLocks.GetOrAdd(userId, (fun _ -> new SemaphoreSlim(1, 1)))

/// The authenticated migration target id for a `Subject`, or `None`
/// when the subject is not a valid migration target (`AnonymousSession`
/// / `ClaimBearer`).
let private migrationTargetId (subject: Subject) : string option =
    match subject with
    | AuthenticatedUser uid -> Some uid
    | TeamMember(uid, _) -> Some uid
    | AnonymousSession _
    | ClaimBearer _ -> None

let private runAsync (computation: Async<'a>) : System.Threading.Tasks.Task<'a> = Async.StartImmediateAsTask computation

/// ASP.NET Core middleware that detects the anonymous→authenticated
/// transition and invokes the registered `IAnonymousSessionMigrator`
/// once per (user, anonymous-session-id) pair. Path-scoped to `/api/*`
/// (a migration trigger on a static-asset request would be noise).
type AnonymousSessionMigrationMiddleware(next: RequestDelegate) =
    member _.InvokeAsync(ctx: HttpContext) : System.Threading.Tasks.Task =
        task {
            let path = ctx.Request.Path
            let isApi = path.StartsWithSegments(PathString "/api")

            // Phase 337 — the anonymous session id comes from the sealed
            // binding cookie, never from the self-asserted `X-User-Id`
            // header. Phase 135 read the header and then re-checked the
            // binding, which was equivalent in effect but left a raw
            // request field on the path into the migrator; reading the
            // seal directly means there is no unverified value to check.
            // `Unprotect` authenticates before the payload is read, so a
            // `Some` here is server-issued by construction.
            let inboundSessionId = AnonymousSessionBinding.boundSessionId ctx

            let subjectOpt =
                match ctx.Items.TryGetValue SubjectItemsKey with
                | true, (:? Subject as s) -> Some s
                | _ -> None

            let targetIdOpt = subjectOpt |> Option.bind migrationTargetId

            // Phase 135 minted the binding here, for whatever session id
            // an anonymous request asserted, and only when a real migrator
            // was composed. Phase 337 removed both halves: the id is now
            // SERVER-ISSUED rather than merely bound (trust-on-first-use
            // binds an attacker to the victim's id, which is why the
            // scope-selection leg stayed open), and
            // `ScopeResolutionMiddleware` — which runs earlier and knows
            // the resolved subject — owns the mint for every anonymous
            // subject, not only the migrating ones.

            // Trigger preconditions: an `/api/*` request, a server-issued
            // anonymous session id recovered from the seal, an
            // authenticated migration target, and a session id differing
            // from the user id (otherwise `X-User-Id` *is* the auth
            // identity — no separate anonymous session exists, as under
            // `HeaderAuthProvider`). The ownership proof that Phase 135
            // added as a separate `isBoundTo` check is now structural:
            // `inboundSessionId` cannot hold a value this server did not
            // seal, so an attacker replaying a victim's (non-secret)
            // session id has nothing to replay it into.
            match isApi, inboundSessionId, targetIdOpt with
            | true, Some sid, Some uid when sid <> uid ->
                match ctx.RequestServices.GetService(typeof<IMemoryCache>) with
                | :? IMemoryCache as cache ->
                    let lastSeenKey = LastSeenKeyPrefix + uid

                    let alreadyMigrated () =
                        match cache.TryGetValue lastSeenKey with
                        | true, (:? string as seen) -> seen = sid
                        | _ -> false

                    // Cheap pre-check outside the lock — the common case
                    // (migration already done earlier this session) skips
                    // the semaphore entirely.
                    if not (alreadyMigrated ()) then
                        let gate = lockFor uid
                        do! gate.WaitAsync()

                        try
                            // Double-checked: a concurrent first request
                            // may have recorded the migration while we
                            // waited on the semaphore.
                            if not (alreadyMigrated ()) then
                                let migrator =
                                    match ctx.RequestServices.GetService(typeof<IAnonymousSessionMigrator>) with
                                    | :? IAnonymousSessionMigrator as m -> m
                                    | _ -> NoOpAnonymousSessionMigrator() :> IAnonymousSessionMigrator

                                let subject =
                                    // `targetIdOpt = Some uid` implies the
                                    // subject is a migration target.
                                    subjectOpt |> Option.defaultValue (AuthenticatedUser uid)

                                let! result = runAsync (migrator.Migrate(sid, subject))

                                let setLastSeen () =
                                    let opts = MemoryCacheEntryOptions()
                                    opts.SlidingExpiration <- Nullable(TimeSpan.FromHours 24.0)
                                    cache.Set(lastSeenKey, sid, opts) |> ignore

                                let scopeId =
                                    match ctx.Items.TryGetValue StorageScopeItemsKey with
                                    | true, (:? StorageScope as scope) -> scope.ScopeId
                                    | _ -> "user-" + uid

                                let emitAudit
                                    (outcome: string)
                                    (summary: MigrationSummary)
                                    (failedItems: int)
                                    (error: string option)
                                    =
                                    match ctx.RequestServices.GetService(typeof<IAuditLog>) with
                                    | :? IAuditLog as auditLog ->
                                        auditLog.Record(
                                            scopeId,
                                            AnonymousSessionMigrated {
                                                AnonymousSessionId = sid
                                                TargetUserId = uid
                                                Outcome = outcome
                                                ItemsMigrated = summary.ItemsMigrated
                                                BytesMigrated = summary.BytesMigrated
                                                Modules = summary.Modules
                                                FailedItems = failedItems
                                                Error = error
                                                OccurredAt = DateTimeOffset.UtcNow
                                            }
                                        )
                                        |> Async.Start
                                    | _ -> ()

                                match result with
                                | Ok summary ->
                                    setLastSeen ()
                                    emitAudit "Ok" summary 0 None
                                | Result.Error(MigrationError.NotEligible _) ->
                                    // Benign decline — record the attempt
                                    // so we don't re-invoke on every
                                    // request, but don't audit (would be
                                    // as noisy as the unwired-default case).
                                    setLastSeen ()
                                | Result.Error(MigrationError.PartialFailure(partial, failedItems, lastError)) ->
                                    setLastSeen ()
                                    emitAudit "PartialFailure" partial failedItems (Some lastError)
                                | Result.Error(MigrationError.InfrastructureFailed message) ->
                                    // Do NOT update LastSeen — the next
                                    // authenticated request retries.
                                    emitAudit "InfrastructureFailed" MigrationSummary.empty 0 (Some message)
                        finally
                            gate.Release() |> ignore
                | _ ->
                    // No IMemoryCache registered — cannot track LastSeen
                    // safely, so skip migration rather than risk thrash.
                    ()
            | _ -> ()

            do! next.Invoke(ctx)
        }
        :> System.Threading.Tasks.Task
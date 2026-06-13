module ToolUp.Platform.Tests.InProcess.AnonymousSessionMigrationMiddlewareTests

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AnonymousSessionMigration

// ─── AnonymousSessionMigrationMiddleware — Phase 66 D.6 ──────────────
//
// Drives the real `AnonymousSessionMigrationMiddleware.InvokeAsync`
// through a `DefaultHttpContext` wired with an `IMemoryCache`, a
// recording `IAuditLog`, and a counting `IAnonymousSessionMigrator`.
// Covers the two D.6 bullets the C.1 substrate slice deferred:
//
//   * Trigger detection (design §3.7 six steps) — when the migrator
//     fires, which `Result` cases update `LastSeenAnonymousSessionId`
//     and which audit, and the four no-trigger guards (non-target
//     subject, X-User-Id == user id, missing header, non-/api path).
//   * The `SemaphoreSlim`-per-userId double-migration guard — two
//     concurrent first requests for the same user invoke the migrator
//     exactly once.
//
// Determinism note: the middleware emits its audit via
// `auditLog.Record(...) |> Async.Start`, but the `Record` *call* (which
// enqueues in the recording double below) runs synchronously inside
// `InvokeAsync` before the trivial returned async is started — so by the
// time `InvokeAsync`'s task completes the audit has been captured. The
// recording double therefore enqueues eagerly in the method body, not
// inside an `async { }`.

// ─── Test doubles ────────────────────────────────────────────────────

type private RecordingAuditLog() =
    let events = System.Collections.Concurrent.ConcurrentQueue<string * AuditEvent>()

    interface IAuditLog with
        member _.Record(scopeId, audit) =
            // Eager enqueue — see determinism note above.
            events.Enqueue(scopeId, audit)
            async.Return()

        member _.GetAuditTrail(_scopeId, _dateRange, _eventType) = async.Return []

    member _.Events = events |> List.ofSeq

/// Migrator that counts invocations and returns a fixed result. An
/// optional `gate` async lets a test hold the migrator inside its
/// critical section to force concurrent overlap for the semaphore test.
let private countingMigrator
    (calls: int ref)
    (result: Result<MigrationSummary, MigrationError>)
    (gate: Async<unit> option)
    : IAnonymousSessionMigrator =
    { new IAnonymousSessionMigrator with
        member _.Migrate(_sid, _subject) = async {
            incr calls

            match gate with
            | Some g -> do! g
            | None -> ()

            return result
        }
    }

let private summary (items: int) (bytes: int64) (modules: string list) : MigrationSummary = {
    ItemsMigrated = items
    BytesMigrated = bytes
    Modules = modules
}

let private mkProvider (cache: IMemoryCache) (audit: IAuditLog) (migrator: IAnonymousSessionMigrator) =
    let services = ServiceCollection()
    services.AddSingleton<IMemoryCache>(cache) |> ignore
    services.AddSingleton<IAuditLog>(audit) |> ignore
    services.AddSingleton<IAnonymousSessionMigrator>(migrator) |> ignore
    // Phase 135 — the migration gate requires a server-issued binding
    // cookie sealed by DataProtection; register it so `invoke` can mint
    // a valid binding and exercise the (now secure) migration path.
    services.AddDataProtection() |> ignore
    services.BuildServiceProvider() :> IServiceProvider

let private newCache () : IMemoryCache =
    new MemoryCache(MemoryCacheOptions()) :> IMemoryCache

/// Drive the middleware once. Returns whether `next` was invoked.
let private invoke
    (provider: IServiceProvider)
    (subject: Subject option)
    (storageScope: StorageScope option)
    (xUserId: string option)
    (method: string)
    (path: string)
    : bool =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Request.Method <- method
    ctx.Request.Path <- PathString path

    match xUserId with
    | Some v ->
        ctx.Request.Headers["X-User-Id"] <- StringValues v
        // Phase 135 — attach the server-issued HttpOnly binding cookie
        // proving this browser owned the anonymous session (minted with
        // the provider's DataProtection key ring). The migration gate
        // requires it; the no-trigger guard cases still get no migration
        // because their OTHER preconditions fail (non-target subject,
        // sid == uid, non-/api path).
        match AnonymousSessionBinding.mint ctx v with
        | Some token -> ctx.Request.Headers["Cookie"] <- StringValues(AnonymousSessionBinding.CookieName + "=" + token)
        | None -> ()
    | None -> ()

    match subject with
    | Some s -> ctx.Items["ToolUp.Subject"] <- box s
    | None -> ()

    match storageScope with
    | Some sc -> ctx.Items["ToolUp.StorageScope"] <- box sc
    | None -> ()

    let mutable nextInvoked = false

    let next =
        RequestDelegate(fun _ ->
            nextInvoked <- true
            Task.CompletedTask)

    let mw = AnonymousSessionMigrationMiddleware(next)
    (mw.InvokeAsync ctx).GetAwaiter().GetResult()
    nextInvoked

// Distinct fixtures. Use unique user ids per concurrency-sensitive test
// because the double-migration guard's SemaphoreSlim dictionary is
// process-static (keyed by user id) and shared across tests.
let private aSession = "session-abc"

let tests =
    testList "Phase 66 D.6 — AnonymousSessionMigrationMiddleware" [

        // ── Trigger fires once; Ok updates LastSeen + audits ──────────
        test "Ok migration → migrator invoked, LastSeen recorded (2nd request skips), audit Outcome=Ok" {
            let calls = ref 0
            let audit = RecordingAuditLog()
            let migrator = countingMigrator calls (Ok(summary 3 300L [ "Forms" ])) None
            let cache = newCache ()
            let provider = mkProvider cache audit migrator

            let subject = Some(AuthenticatedUser "user-ok")

            let scope =
                Some {
                    ScopeId = "user-ok"
                    Container = "user-user-ok"
                    Persist = true
                }

            let next1 = invoke provider subject scope (Some aSession) "POST" "/api/x"
            let next2 = invoke provider subject scope (Some aSession) "POST" "/api/x"

            Expect.isTrue next1 "request always continues the pipeline"
            Expect.isTrue next2 "second request also continues"
            Expect.equal calls.Value 1 "migrator invoked once; the LastSeen guard suppresses the second attempt"

            match audit.Events with
            | [ (scopeId, AnonymousSessionMigrated p) ] ->
                Expect.equal scopeId "user-ok" "audit attributed to the stashed StorageScope.ScopeId"
                Expect.equal p.Outcome "Ok" "outcome recorded"
                Expect.equal p.AnonymousSessionId aSession "anonymous session id carried"
                Expect.equal p.TargetUserId "user-ok" "target user id carried"
                Expect.equal p.ItemsMigrated 3 "item count from the summary"
                Expect.equal p.Modules [ "Forms" ] "contributing modules carried"
                Expect.isNone p.Error "Ok carries no error"
            | other -> failtestf "expected exactly one AnonymousSessionMigrated(Ok) audit, got %A" other
        }

        // ── NotEligible updates LastSeen but does NOT audit ───────────
        test "NotEligible → LastSeen recorded (2nd request skips), no audit emitted" {
            let calls = ref 0
            let audit = RecordingAuditLog()

            let migrator =
                countingMigrator calls (Result.Error(MigrationError.NotEligible "no data")) None

            let cache = newCache ()
            let provider = mkProvider cache audit migrator
            let subject = Some(AuthenticatedUser "user-ne")

            invoke provider subject None (Some aSession) "POST" "/api/x" |> ignore
            invoke provider subject None (Some aSession) "POST" "/api/x" |> ignore

            Expect.equal calls.Value 1 "benign decline still records the attempt so it isn't retried every request"
            Expect.isEmpty audit.Events "NotEligible is not audited (as noisy as the unwired default)"
        }

        // ── InfrastructureFailed does NOT update LastSeen — retried ───
        test "InfrastructureFailed → LastSeen NOT recorded, retried next request, audited each time" {
            let calls = ref 0
            let audit = RecordingAuditLog()

            let migrator =
                countingMigrator calls (Result.Error(MigrationError.InfrastructureFailed "blob down")) None

            let cache = newCache ()
            let provider = mkProvider cache audit migrator
            let subject = Some(AuthenticatedUser "user-inf")

            invoke provider subject None (Some aSession) "POST" "/api/x" |> ignore
            invoke provider subject None (Some aSession) "POST" "/api/x" |> ignore

            Expect.equal calls.Value 2 "substrate failure leaves LastSeen unset so the next request retries"

            match audit.Events with
            | [ (_, AnonymousSessionMigrated a); (_, AnonymousSessionMigrated b) ] ->
                Expect.equal a.Outcome "InfrastructureFailed" "first attempt audited as InfrastructureFailed"
                Expect.equal b.Outcome "InfrastructureFailed" "retry audited too"
                Expect.equal a.Error (Some "blob down") "error message carried"
            | other -> failtestf "expected two InfrastructureFailed audits, got %A" other
        }

        // ── PartialFailure updates LastSeen and audits ────────────────
        test "PartialFailure → LastSeen recorded (2nd request skips), audit carries failedItems + error" {
            let calls = ref 0
            let audit = RecordingAuditLog()

            let migrator =
                countingMigrator
                    calls
                    (Result.Error(MigrationError.PartialFailure(summary 2 200L [ "AI" ], 1, "row 4 rejected")))
                    None

            let cache = newCache ()
            let provider = mkProvider cache audit migrator
            let subject = Some(TeamMember("user-pf", "team-9"))

            invoke provider subject None (Some aSession) "POST" "/api/x" |> ignore
            invoke provider subject None (Some aSession) "POST" "/api/x" |> ignore

            Expect.equal
                calls.Value
                1
                "partial failure updates LastSeen per OQ5 (item failures not generically retriable)"

            match audit.Events with
            | [ (_, AnonymousSessionMigrated p) ] ->
                Expect.equal p.Outcome "PartialFailure" "outcome recorded"
                Expect.equal p.ItemsMigrated 2 "migrated count from the partial summary"
                Expect.equal p.FailedItems 1 "failed-item count carried"
                Expect.equal p.Error (Some "row 4 rejected") "last error carried"
                Expect.equal p.TargetUserId "user-pf" "team member's user id is the target"
            | other -> failtestf "expected one PartialFailure audit, got %A" other
        }

        // ── No-trigger guard: X-User-Id == user id ────────────────────
        test "X-User-Id equal to the authenticated user id → no migration (no separate anon session)" {
            let calls = ref 0
            let audit = RecordingAuditLog()
            let migrator = countingMigrator calls (Ok MigrationSummary.empty) None
            let provider = mkProvider (newCache ()) audit migrator

            let next =
                invoke provider (Some(AuthenticatedUser "user-1")) None (Some "user-1") "POST" "/api/x"

            Expect.isTrue next "request continues"
            Expect.equal calls.Value 0 "X-User-Id is the auth identity, not an anonymous session — no trigger"
            Expect.isEmpty audit.Events "no audit when no migration runs"
        }

        // ── No-trigger guard: non-target subject ──────────────────────
        test "AnonymousSession subject → no migration target → no trigger" {
            let calls = ref 0
            let migrator = countingMigrator calls (Ok MigrationSummary.empty) None
            let provider = mkProvider (newCache ()) (RecordingAuditLog()) migrator

            let next =
                invoke provider (Some(AnonymousSession "s")) None (Some aSession) "POST" "/api/x"

            Expect.isTrue next "request continues"
            Expect.equal calls.Value 0 "an anonymous subject is not a migration target"
        }

        // ── No-trigger guard: missing X-User-Id header ────────────────
        test "Missing X-User-Id header → no trigger" {
            let calls = ref 0
            let migrator = countingMigrator calls (Ok MigrationSummary.empty) None
            let provider = mkProvider (newCache ()) (RecordingAuditLog()) migrator

            let next =
                invoke provider (Some(AuthenticatedUser "user-nh")) None None "POST" "/api/x"

            Expect.isTrue next "request continues"
            Expect.equal calls.Value 0 "no inbound session id → nothing to migrate"
        }

        // ── No-trigger guard: non-/api path ───────────────────────────
        test "Non-/api path → no trigger even with a valid anon→auth transition" {
            let calls = ref 0
            let migrator = countingMigrator calls (Ok MigrationSummary.empty) None
            let provider = mkProvider (newCache ()) (RecordingAuditLog()) migrator

            let next =
                invoke provider (Some(AuthenticatedUser "user-np")) None (Some aSession) "GET" "/dashboard"

            Expect.isTrue next "request continues"
            Expect.equal calls.Value 0 "migration triggers only on /api/* paths"
        }

        // ── Default NoOp migrator → uniform NotEligible, no audit ─────
        test "Unwired (NoOp) migrator → records LastSeen, no audit, 2nd request skips" {
            let audit = RecordingAuditLog()
            let migrator = NoOpAnonymousSessionMigrator() :> IAnonymousSessionMigrator
            let cache = newCache ()
            let provider = mkProvider cache audit migrator
            let subject = Some(AuthenticatedUser "user-noop")

            let n1 = invoke provider subject None (Some aSession) "POST" "/api/x"
            let n2 = invoke provider subject None (Some aSession) "POST" "/api/x"

            Expect.isTrue n1 "request continues with the default migrator"
            Expect.isTrue n2 "second request continues"
            Expect.isEmpty audit.Events "the NoOp default returns NotEligible — not audited"
        }

        // ── SemaphoreSlim double-migration guard ──────────────────────
        testCaseAsync "Two concurrent first requests for the same user → migrator invoked exactly once"
        <| async {
            let calls = ref 0
            let audit = RecordingAuditLog()

            // Hold the migrator inside its critical section until both
            // request tasks have started, forcing the second to wait on
            // the per-user semaphore and then skip on the LastSeen
            // re-check once the first completes.
            let gate = TaskCompletionSource<unit>()

            let migrator =
                countingMigrator calls (Ok(summary 1 10L [ "Forms" ])) (Some(gate.Task |> Async.AwaitTask))

            let cache = newCache ()
            let provider = mkProvider cache audit migrator
            let subject = Some(AuthenticatedUser "user-concurrent")

            let runOne () =
                Task.Run(fun () -> invoke provider subject None (Some aSession) "POST" "/api/x")

            let t1 = runOne ()
            let t2 = runOne ()

            // Give both tasks a moment to reach the semaphore, then
            // release the migrator held by whichever acquired the lock.
            do! Async.Sleep 100
            gate.SetResult()

            let! results = Task.WhenAll [| t1; t2 |] |> Async.AwaitTask

            Expect.isTrue (Array.forall id results) "both requests continue the pipeline"
            Expect.equal calls.Value 1 "the per-user semaphore + LastSeen double-check admits exactly one migration"

            match audit.Events with
            | [ (_, AnonymousSessionMigrated p) ] -> Expect.equal p.Outcome "Ok" "the single migration audited once"
            | other -> failtestf "expected exactly one audit from the deduplicated migration, got %A" other
        }
    ]
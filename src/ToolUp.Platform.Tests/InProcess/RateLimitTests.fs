module ToolUp.Platform.Tests.InProcess.RateLimitTests

open System
open System.IO
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Remoting.Server

// ─── Phase 69g.tail — per-method `[<RateLimit>]` attribution, telemetry
// + audit denial integration, operator status-board surface ───────────
//
// Contract tests for the rate-limit half of the Phase 69 family tails:
//   * classification recognises BOTH attribute families — the
//     server-tier `ToolUp.Remoting.Server.RateLimitAttribute` and the
//     tier-shared `ToolUp.Platform.RateLimitAttribute` mirror that
//     Fable-compiled API records carry (simple-name matching, normalised
//     to the server-tier budget at startup), and reflects over
//     non-public records (the Phase 69d.tail fail-open fix, applied here
//     too);
//   * per-budget enforcement against `InMemoryRateLimitStore` (allow
//     under budget, deny over), multi-budget AND semantics, positive
//     RetryAfter on denial, per-subject isolation (the security
//     boundary);
//   * the forge SDK's expensive methods carry conservative budgets (the
//     annotation sweep — pinned so removal fails the pack);
//   * the dispatcher orders the pre-flight stages correctly (auth +
//     idempotency-replay both precede rate-limit) and emits denial
//     telemetry (`MethodOutcome.RateLimited`) + a `RateLimitExceeded`
//     audit row (source-audit pins, same idiom as the 69h.tail pack).

// ── Source-audit helpers (mirror the 69h.tail AuditTests idiom) ──────

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private serverSource (relative: string list) =
    File.ReadAllText(
        Path.Combine(
            repoRoot () :: "src" :: "ToolUp.Platform.Server" :: "Server" :: relative
            |> List.toArray
        )
    )

let private srcSource (relative: string list) =
    File.ReadAllText(Path.Combine(repoRoot () :: "src" :: relative |> List.toArray))

// ── Test fixture records ────────────────────────────────────────────

/// Server-family budgets (this assembly's own attribute set). `private`
/// so the record type is non-public — the classifier must still arm it
/// (the NonPublic reflection flag, Phase 69d.tail parity).
type private ServerRateLimitedApi = {
    [<RateLimit(2, RateLimitWindow.perMinute)>]
    Capped: unit -> Async<int>
    [<RateLimit(1, RateLimitWindow.perSecond)>]
    [<RateLimit(100, RateLimitWindow.perHour)>]
    Burst: unit -> Async<int>
    Unbudgeted: unit -> Async<int>
}

/// Platform-mirror budgets (the tier-shared `ToolUp.Platform.Core` set
/// that Fable-compiled API records carry). The classifier must produce
/// identical budgets — the simple-name recognition is the load-bearing
/// Phase 69g.tail substrate change for the Core mirror.
type private MirrorRateLimitedApi = {
    [<ToolUp.Platform.RateLimit(2, ToolUp.Platform.RateLimitSeconds.perMinute)>]
    Capped: unit -> Async<int>
    [<ToolUp.Platform.RateLimit(1, ToolUp.Platform.RateLimitSeconds.perSecond)>]
    [<ToolUp.Platform.RateLimit(100, ToolUp.Platform.RateLimitSeconds.perHour)>]
    Burst: unit -> Async<int>
    Unbudgeted: unit -> Async<int>
}

// ── Budget-shape helpers ────────────────────────────────────────────

let private budgetShapes (cls: Map<string, RateLimitAttribute list>) (methodName: string) =
    cls
    |> Map.tryFind methodName
    |> Option.defaultWith (fun () -> failtestf "method %s not found in classification" methodName)
    |> List.map (fun b -> b.Count, b.WindowSeconds)

let private run x = Async.RunSynchronously x

[<Tests>]
let tests =
    testList "Phase 69g.tail — rate-limit attribution" [

        // ── Classification: server family ──
        test "server-family budgets classify per method" {
            let cls = RateLimit.classify typeof<ServerRateLimitedApi>

            Expect.equal (budgetShapes cls "Capped") [ 2, 60 ] "Capped carries one per-minute budget of 2"

            Expect.equal
                (budgetShapes cls "Burst")
                [ 1, 1; 100, 3600 ]
                "Burst carries both budgets in declaration order"

            Expect.equal (budgetShapes cls "Unbudgeted") [] "Unbudgeted carries no budget"
        }

        test "mirror-family budgets classify identically (family-agnostic recognition)" {
            let server = RateLimit.classify typeof<ServerRateLimitedApi>
            let mirror = RateLimit.classify typeof<MirrorRateLimitedApi>

            // Compare shape-by-shape — the attribute instances differ by
            // CLR type (server-tier vs Core mirror) but the normalised
            // (Count, WindowSeconds) budgets must be equal. Pre-69g.tail
            // the Core mirror was silently invisible to the classifier
            // (exact-type match), so a Fable API record's budget never
            // reached the wire — the original defect.
            for m in [ "Capped"; "Burst"; "Unbudgeted" ] do
                Expect.equal
                    (budgetShapes mirror m)
                    (budgetShapes server m)
                    (sprintf
                        "%s: the ToolUp.Platform.* mirror must normalise to the same budget as the server-tier family"
                        m)
        }

        test "classifier reflects over non-public records" {
            // The fixtures above are `private`, so a bare
            // `FSharpType.IsRecord` (without BindingFlags.NonPublic)
            // would report false and silently skip budget enforcement —
            // the same fail-open hole Phase 69d.tail closed for auth.
            // A non-empty classification proves the NonPublic flag is set.
            let cls = RateLimit.classify typeof<ServerRateLimitedApi>
            Expect.isNonEmpty (budgetShapes cls "Capped") "a private record's budgets must still arm the classifier"
        }

        // ── Enforcement semantics ──
        test "budget allows under the cap and denies over it" {
            let store =
                InMemoryRateLimitStore() :> ToolUp.Remoting.Server.IRemotingRateLimitStore

            let budgets = [ RateLimitAttribute(2, RateLimitWindow.perMinute) ]

            let d1 = run (RateLimit.evaluate store "subject:a" "Capped" budgets)
            let d2 = run (RateLimit.evaluate store "subject:a" "Capped" budgets)
            let d3 = run (RateLimit.evaluate store "subject:a" "Capped" budgets)

            Expect.equal d1 (Admission.Admit 1) "first call within budget, one left"
            Expect.equal d2 (Admission.Admit 0) "second call within budget, none left"

            match d3 with
            | Admission.Deny retryAfter ->
                Expect.isGreaterThan retryAfter TimeSpan.Zero "denial carries a positive RetryAfter"
            | Admission.Admit _ -> failtest "third call should exceed the budget of 2"
        }

        test "multi-budget AND: the tightest budget denies even when a wider one has room" {
            let store =
                InMemoryRateLimitStore() :> ToolUp.Remoting.Server.IRemotingRateLimitStore
            // 1 call/second AND 100 calls/hour. The per-second cap bites
            // on the second call within the same second; the hourly cap
            // (99 remaining) is irrelevant — AND semantics.
            let budgets = [
                RateLimitAttribute(1, RateLimitWindow.perSecond)
                RateLimitAttribute(100, RateLimitWindow.perHour)
            ]

            let d1 = run (RateLimit.evaluate store "subject:a" "Burst" budgets)
            let d2 = run (RateLimit.evaluate store "subject:a" "Burst" budgets)

            Expect.equal d1 (Admission.Admit 0) "first call passes both budgets; the tightest has none left"

            match d2 with
            | Admission.Deny _ -> ()
            | Admission.Admit _ -> failtest "second call must be denied by the per-second budget despite hourly room"
        }

        test "per-subject isolation: one subject's exhaustion does not affect another" {
            let store =
                InMemoryRateLimitStore() :> ToolUp.Remoting.Server.IRemotingRateLimitStore

            let budgets = [ RateLimitAttribute(1, RateLimitWindow.perMinute) ]

            run (RateLimit.evaluate store "subject:a" "Capped" budgets) |> ignore
            let aSecond = run (RateLimit.evaluate store "subject:a" "Capped" budgets)
            let bFirst = run (RateLimit.evaluate store "subject:b" "Capped" budgets)

            match aSecond with
            | Admission.Deny _ -> ()
            | Admission.Admit _ -> failtest "subject:a's second call should be denied"

            Expect.equal
                bFirst
                (Admission.Admit 0)
                "subject:b's first call must be unaffected by subject:a (security boundary)"
        }

        test "deriveSubjectKey prefers the auth subject and falls back to a per-IP key" {
            let withSubject =
                { new IAuthContext with
                    member _.HasRole _ = false
                    member _.HasClaim(_, _) = false
                    member _.HasTenant() = false
                    member _.IsAnonymous() = false
                    member _.SubjectId = "user:42"
                }

            Expect.equal
                (RateLimit.deriveSubjectKey (Some withSubject) (Some "1.2.3.4"))
                "subject:user:42"
                "resolved subject wins"

            Expect.equal
                (RateLimit.deriveSubjectKey None (Some "1.2.3.4"))
                "ip:1.2.3.4"
                "anonymous falls back to per-IP"

            Expect.equal (RateLimit.deriveSubjectKey None None) "anonymous-unknown" "no IP resolvable"
        }

        // ── Annotation sweep (Phase 69g.tail Task D) ──
        test "FileManagementApi.UploadFile carries the conservative upload budget" {
            // Platform.Core record reachable from this pack — proves the
            // Core mirror annotation flows through classify end-to-end.
            let cls = RateLimit.classify typeof<ProcessedDataTypes.FileManagementApi>
            Expect.equal (budgetShapes cls "UploadFile") [ 30, 60 ] "UploadFile is capped at 30/min"
            Expect.equal (budgetShapes cls "ListFiles") [] "read-only ListFiles carries no budget"
        }

        test "expensive forge SDK methods carry a [<RateLimit>] annotation (source pins)" {
            // AI.Core / KnowledgeBase.Core records aren't directly
            // referenced as typeof targets from this pack, so pin the
            // annotation by source presence — removal fails the pack.
            let ai = srcSource [ "ToolUp.AI.Core"; "Shared"; "AITypes.fs" ]
            let kb = srcSource [ "ToolUp.KnowledgeBase.Core"; "Shared"; "SharedTypes.fs" ]

            let platformKb =
                srcSource [ "ToolUp.KnowledgeBase.Core"; "Shared"; "PlatformKnowledgeApi.fs" ]

            Expect.stringContains
                ai
                "[<RateLimit(30, RateLimitSeconds.perMinute)>]"
                "AIAssistantApi.SubmitMessage is budgeted"

            Expect.stringContains
                kb
                "[<RateLimit(20, RateLimitSeconds.perMinute)>]"
                "KnowledgeApi.UploadDocument is budgeted"

            Expect.stringContains
                platformKb
                "[<RateLimit(20, RateLimitSeconds.perMinute)>]"
                "IPlatformKnowledgeApi.UploadPlatformDocument is budgeted"
        }

        // ── Dispatcher pre-flight ordering + denial emission (source pins) ──
        test "auth and idempotency-replay both precede the rate-limit pre-flight" {
            let pipeline = serverSource [ "Remoting"; "Stages"; "Pipeline.fs" ]

            let authIdx = pipeline.IndexOf "Authorize.stage"
            let replayIdx = pipeline.IndexOf "Idempotency.stage"
            let rateLimitIdx = pipeline.IndexOf "RateLimit.stage"

            Expect.isGreaterThan authIdx -1 "the authorize stage must be in the pipeline"
            Expect.isGreaterThan replayIdx -1 "the idempotency stage must be in the pipeline"
            Expect.isGreaterThan rateLimitIdx -1 "the rate-limit stage must be in the pipeline"

            Expect.isLessThan
                authIdx
                rateLimitIdx
                "auth runs before rate-limit — an unauthorised call must not spend the budget"

            Expect.isLessThan
                replayIdx
                rateLimitIdx
                "idempotency replay precedes rate-limit — a replayed call must not spend the budget"
        }

        test "the rate-limit denial branch settles RateLimited telemetry and a RateLimitExceeded audit row" {
            let stage = serverSource [ "Remoting"; "Stages"; "RateLimit.fs" ]

            let deniedIdx = stage.IndexOf "| ToolUp.Platform.Admission.Deny retryAfter ->"
            let telemetryIdx = stage.IndexOf "MethodOutcome.RateLimited retryAfter"
            let auditIdx = stage.IndexOf "Kind = AuditKind.RateLimitExceeded"

            Expect.isGreaterThan deniedIdx -1 "the denial branch must exist"
            Expect.isGreaterThan telemetryIdx deniedIdx "denial telemetry is settled inside the denial branch"

            Expect.isGreaterThan
                auditIdx
                deniedIdx
                "the RateLimitExceeded audit row is emitted inside the denial branch"
        }
        // ── Phase 870 — atomic admit under a simultaneous burst ──
        test "N callers released at once against one remaining slot: exactly one is admitted" {
            // Budget K with K - 1 already admitted leaves exactly one slot.
            // N callers are released together through one barrier — the
            // burst the gate exists for. A check-then-act store lets
            // several of them read "one slot left" before any records its
            // admission.
            //
            // The gap between that read and that write is a few
            // instructions, so what widens it is preemption: callers are
            // four per core, and a caller descheduled inside the gap lets
            // the others through. The same threads run every round (one
            // barrier phase per round), so 500 rounds cost thread start-up
            // once. Measured against the unfixed (check-then-act) store on a
            // 16-core machine: red in 8 runs of 8, over-admitting 7 to 62 of
            // the 500 rounds per run, in about half a second.
            let budget = 5
            let callers = 4 * max 4 Environment.ProcessorCount
            let rounds = 500
            let window = TimeSpan.FromMinutes 1.0
            let key = "subject:burst"

            let stores =
                Array.init rounds (fun _ ->
                    let store =
                        InMemoryRateLimitStore() :> ToolUp.Remoting.Server.IRemotingRateLimitStore

                    for _ in 1 .. budget - 1 do
                        run (store.TryAcquire(key, budget, window)) |> ignore

                    store)

            let admitted = Array.zeroCreate<int> rounds
            use gate = new Barrier(callers)

            let caller () =
                for round in 0 .. rounds - 1 do
                    gate.SignalAndWait()

                    match run (stores[round].TryAcquire(key, budget, window)) with
                    | Admission.Admit _ -> Interlocked.Increment(&admitted[round]) |> ignore
                    | Admission.Deny _ -> ()

            let threads = Array.init callers (fun _ -> Thread(caller))
            threads |> Array.iter _.Start()
            threads |> Array.iter _.Join()

            let overAdmitted =
                admitted |> Array.indexed |> Array.filter (fun (_, n) -> n <> 1) |> Array.toList

            Expect.isEmpty
                overAdmitted
                $"every round admits exactly the one remaining slot (round, admitted) — %d{overAdmitted.Length} of %d{rounds} rounds over-admitted"
        }
    ]

// ─── Phase 1068 — the Phase 56 inbound middleware's refusal shapes ────

let private quietLogger =
    { new ToolUp.Platform.ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Drive `InboundRateLimitMiddleware` once against `store` for a policy on
/// `/limited`, returning the response and whether `next` ran.
let private invokeMiddleware
    (store: ToolUp.Platform.Infra.IRateLimitStore)
    (policy: ToolUp.Platform.RouteLimit)
    (audit: IAuditLog)
    =
    let ctx = Microsoft.AspNetCore.Http.DefaultHttpContext()
    ctx.Request.Path <- Microsoft.AspNetCore.Http.PathString "/limited"
    let mutable reached = false

    let next =
        Microsoft.AspNetCore.Http.RequestDelegate(fun _ ->
            reached <- true
            System.Threading.Tasks.Task.CompletedTask)

    let middleware =
        ToolUp.Platform.Infra.RateLimitMiddleware.InboundRateLimitMiddleware(next, store, quietLogger, audit)

    let config = {
        ToolUp.Platform.ServerConfig.defaults with
            RateLimits = [ policy ]
    }

    middleware.InvokeAsync(ctx, config).GetAwaiter().GetResult()
    ctx.Response, reached

/// A Phase 56 store whose write side always fails.
type private FailingInboundStore() =
    interface ToolUp.Platform.Infra.IRateLimitStore with
        member _.GetCurrent(_, _) = async { return 0 }

        member _.IncrementAndCheck(_, _, _) = async {
            return Error(ToolUp.Platform.Infra.StoreUnavailable "simulated outage")
        }

        member _.GetRecentDecisions(_, _) = async { return [] }

/// An `IAuditLog` that keeps what it is handed.
type private RecordingAudit() =
    let rows =
        System.Collections.Concurrent.ConcurrentQueue<string * ToolUp.Platform.AuditEvent>()

    member _.Rows = rows |> Seq.toList

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { rows.Enqueue(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

[<Tests>]
let middlewareTests =
    testList "Phase 1068 — inbound rate-limit middleware" [
        test "a route declares FailOpen unless it says otherwise" {
            Expect.equal
                (RouteLimit.perIpPerMinute "/limited" 1).StorePosture
                StorePosture.FailOpen
                "a deployment that declares nothing keeps today's behaviour (GP 11)"
        }

        test "FailOpen: a failing store admits the request and writes no audit row" {
            let audit = RecordingAudit()
            let policy = RouteLimit.perIpPerMinute "/limited" 1

            let response, reached =
                invokeMiddleware (FailingInboundStore()) policy (audit :> IAuditLog)

            Expect.isTrue reached "the request reaches the handler"
            Expect.equal response.StatusCode 200 "the response is the handler's"
            Expect.isEmpty audit.Rows "an admission is not audited"
        }

        test "FailClosed: a failing store refuses the request with 503 and a RateLimitRefused audit row" {
            let audit = RecordingAudit()

            let policy = {
                RouteLimit.perIpPerMinute "/limited" 1 with
                    StorePosture = StorePosture.FailClosed
            }

            let response, reached =
                invokeMiddleware (FailingInboundStore()) policy (audit :> IAuditLog)

            Expect.isFalse reached "the request does not reach the handler"
            Expect.equal response.StatusCode 503 "a store that cannot count refuses under FailClosed"

            match audit.Rows with
            | [ scopeId, ToolUp.Platform.AuditEvent.RateLimitRefused row ] ->
                Expect.equal scopeId "_platform" "an inbound policy resolves no caller scope"
                Expect.equal row.Provider "inbound:/limited" "names the route whose budget could not be counted"
                Expect.isNone row.SubKey "no caller key reaches the trail"
                Expect.stringContains row.Reason "simulated outage" "carries the store's reason"
            | other -> failtestf "expected one RateLimitRefused row, got %A" other
        }

        test "RateLimit-Reset counts the exact seconds to the window end at a known instant" {
            let at h m sec =
                DateTimeOffset(2026, 11, 9, h, m, sec, TimeSpan.Zero)

            let reset = ToolUp.Platform.Infra.RateLimitMiddleware.secondsUntilWindowEnd

            Expect.equal (reset (at 10 30 15) PerDay) 48585 "10:30:15 is 13:29:45 from midnight"
            Expect.equal (reset (at 0 0 0) PerDay) 86400 "midnight is a whole day from the next"
            Expect.equal (reset (at 23 59 59) PerDay) 1 "one second before midnight"
            Expect.equal (reset (at 10 30 15) PerHour) 1785 "10:30:15 is 29:45 from 11:00"
            Expect.equal (reset (at 10 30 15) PerMinute) 45 "15 s into the minute leaves 45"
        }

        test "a Return429 refusal still carries its RateLimit-* and Retry-After headers" {
            let store = ToolUp.Platform.Infra.InMemoryRateLimitStore.create ()
            let policy = RouteLimit.perIpPerMinute "/limited" 1
            invokeMiddleware store policy (RecordingAudit() :> IAuditLog) |> ignore

            let response, reached =
                invokeMiddleware store policy (RecordingAudit() :> IAuditLog)

            Expect.isFalse reached "the over-budget call does not reach the handler"
            Expect.equal response.StatusCode 429 "Return429 answers 429"
            Expect.isTrue (response.Headers.ContainsKey "Retry-After") "the caller is told when to come back"
            Expect.equal (string response.Headers["RateLimit-Remaining"]) "0" "and that nothing is left"
        }

        test "a DenySilently refusal is a bare 204 — no RateLimit-* and no Retry-After header" {
            // The DU says the caller "should not be told they hit the cap";
            // until 1068 the middleware stamped `RateLimit-Remaining: 0` and
            // `Retry-After` on the 204, telling them exactly that.
            let store = ToolUp.Platform.Infra.InMemoryRateLimitStore.create ()

            let policy = {
                ToolUp.Platform.RouteLimit.perIpPerMinute "/limited" 1 with
                    OnExceeded = ToolUp.Platform.DenySilently
            }

            invokeMiddleware store policy (RecordingAudit() :> IAuditLog) |> ignore

            let response, reached =
                invokeMiddleware store policy (RecordingAudit() :> IAuditLog)

            Expect.isFalse reached "the over-budget call does not reach the handler"
            Expect.equal response.StatusCode 204 "DenySilently answers 204"

            let leaked =
                response.Headers.Keys
                |> Seq.filter (fun h ->
                    h.StartsWith("RateLimit-", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(h, "Retry-After", StringComparison.OrdinalIgnoreCase))
                |> Seq.toList

            Expect.isEmpty leaked "a silent denial carries no rate-limit header"
        }
    ]
module ToolUp.Platform.Tests.InProcess.RateLimitTests

open System
open System.IO
open Expecto
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
            let store = InMemoryRateLimitStore() :> IRateLimitStore
            let budgets = [ RateLimitAttribute(2, RateLimitWindow.perMinute) ]

            let d1 = run (RateLimit.evaluate store "subject:a" "Capped" budgets)
            let d2 = run (RateLimit.evaluate store "subject:a" "Capped" budgets)
            let d3 = run (RateLimit.evaluate store "subject:a" "Capped" budgets)

            Expect.equal d1 RateLimitAllowed "first call within budget"
            Expect.equal d2 RateLimitAllowed "second call within budget"

            match d3 with
            | RateLimitDenied retryAfter ->
                Expect.isGreaterThan retryAfter TimeSpan.Zero "denial carries a positive RetryAfter"
            | RateLimitAllowed -> failtest "third call should exceed the budget of 2"
        }

        test "multi-budget AND: the tightest budget denies even when a wider one has room" {
            let store = InMemoryRateLimitStore() :> IRateLimitStore
            // 1 call/second AND 100 calls/hour. The per-second cap bites
            // on the second call within the same second; the hourly cap
            // (99 remaining) is irrelevant — AND semantics.
            let budgets = [
                RateLimitAttribute(1, RateLimitWindow.perSecond)
                RateLimitAttribute(100, RateLimitWindow.perHour)
            ]

            let d1 = run (RateLimit.evaluate store "subject:a" "Burst" budgets)
            let d2 = run (RateLimit.evaluate store "subject:a" "Burst" budgets)

            Expect.equal d1 RateLimitAllowed "first call passes both budgets"

            match d2 with
            | RateLimitDenied _ -> ()
            | RateLimitAllowed -> failtest "second call must be denied by the per-second budget despite hourly room"
        }

        test "per-subject isolation: one subject's exhaustion does not affect another" {
            let store = InMemoryRateLimitStore() :> IRateLimitStore
            let budgets = [ RateLimitAttribute(1, RateLimitWindow.perMinute) ]

            run (RateLimit.evaluate store "subject:a" "Capped" budgets) |> ignore
            let aSecond = run (RateLimit.evaluate store "subject:a" "Capped" budgets)
            let bFirst = run (RateLimit.evaluate store "subject:b" "Capped" budgets)

            match aSecond with
            | RateLimitDenied _ -> ()
            | RateLimitAllowed -> failtest "subject:a's second call should be denied"

            Expect.equal
                bFirst
                RateLimitAllowed
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
            let adapter = serverSource [ "Remoting"; "Giraffe"; "GiraffeAdapter.fs" ]

            let authIdx = adapter.IndexOf "AuthClassifier.evaluate methodCls"
            let replayIdx = adapter.IndexOf "x-idempotency-replay"
            let rateLimitIdx = adapter.IndexOf "Phase 69g — rate-limit pre-flight"

            Expect.isGreaterThan authIdx -1 "the auth evaluation must exist"
            Expect.isGreaterThan replayIdx -1 "the idempotency replay path must exist"
            Expect.isGreaterThan rateLimitIdx -1 "the rate-limit pre-flight must exist"

            Expect.isLessThan
                authIdx
                rateLimitIdx
                "auth runs before rate-limit — an unauthorised call must not spend the budget"

            Expect.isLessThan
                replayIdx
                rateLimitIdx
                "idempotency replay precedes rate-limit — a replayed call must not spend the budget"
        }

        test "the rate-limit denial branch emits denial telemetry and a RateLimitExceeded audit row" {
            let adapter = serverSource [ "Remoting"; "Giraffe"; "GiraffeAdapter.fs" ]

            let deniedIdx = adapter.IndexOf "| RateLimitDenied retryAfter ->"
            let telemetryIdx = adapter.IndexOf "MethodOutcome.RateLimited retryAfter"
            let auditIdx = adapter.IndexOf "Kind = AuditKind.RateLimitExceeded"

            Expect.isGreaterThan deniedIdx -1 "the denial branch must exist"
            Expect.isGreaterThan telemetryIdx deniedIdx "denial telemetry is emitted inside the denial branch"

            Expect.isGreaterThan
                auditIdx
                deniedIdx
                "the RateLimitExceeded audit row is emitted inside the denial branch"
        }
    ]
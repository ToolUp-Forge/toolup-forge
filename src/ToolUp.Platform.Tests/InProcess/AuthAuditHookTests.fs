module ToolUp.Platform.Tests.InProcess.AuthAuditHookTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AuthAuditHook

// ─── Phase 120 — IAuthAuditHook default-impl contract ────────────────
//
// Pins the write-side behaviour the emission points and the
// /dev/auth-denials rollup rely on:
//   * one structured AuthorizationDenied row per denial decision,
//   * sanitised subject (kind + id only — no PII beyond the id),
//   * the per-(route,subject) flood guard coalesces a probing burst into
//     bounded rows whose DedupCount sums to the true total, and
//   * distinct (route,subject) keys each emit independently.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Capturing `IAuditLog` recording every `(scopeId, AuthorizationDeniedPayload)`.
/// Non-AuthorizationDenied events are ignored (none are emitted in these tests).
type private CapturingAuditLog() =
    let rows = ConcurrentQueue<string * AuthorizationDeniedPayload>()

    member _.Rows = rows |> List.ofSeq

    interface IAuditLog with
        member _.Record(scopeId, audit) = async {
            match audit with
            | AuthorizationDenied p -> rows.Enqueue(scopeId, p)
            | _ -> ()
        }

        member _.GetAuditTrail(_, _, _) = async { return [] }

/// Mutable clock the tests advance to drive window rollovers deterministically.
type private FakeClock(start: DateTimeOffset) =
    let mutable nowRef = start
    member _.Now() = nowRef
    member _.Advance(span: TimeSpan) = nowRef <- nowRef + span

let private baseDenial = {
    Route = "POST /api/Thing/Do"
    Subject = AuthenticatedUser "user-1"
    Requirement = SurfaceDenialRequirement
    Verdict = "user_subject_not_admitted"
    Reason = "surface-enforcement denied /api/Thing/Do"
    ScopeId = Some "user-1"
    CorrelationId = Some "corr-1"
}

let private mkHook (audit: CapturingAuditLog) (clock: FakeClock) =
    AuthAuditHook(audit :> IAuditLog, silentLogger, TimeSpan.FromSeconds 60.0, clock.Now) :> IAuthAuditHook

[<Tests>]
let tests =
    testList "AuthAuditHook" [
        test "single denial emits exactly one AuthorizationDenied row with the sanitised fields" {
            let audit = CapturingAuditLog()
            let clock = FakeClock(DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero))
            let hook = mkHook audit clock

            hook.RecordDenial baseDenial |> Async.RunSynchronously

            Expect.hasLength audit.Rows 1 "one row for one denial"
            let scopeId, p = audit.Rows.Head
            Expect.equal scopeId "user-1" "written under the caller scope"
            Expect.equal p.Route "POST /api/Thing/Do" "route preserved"
            Expect.equal p.Requirement "surface" "requirement serialised via AuthDenialRequirement.toString"
            Expect.equal p.SubjectKind "user" "subject kind sanitised"
            Expect.equal p.SubjectId (Some "user-1") "subject id carried"
            Expect.equal p.Verdict "user_subject_not_admitted" "verdict carried"
            Expect.equal p.DedupCount 1 "leading-edge row counts one denial"
        }

        test "anonymous subject leaks no id" {
            let audit = CapturingAuditLog()
            let clock = FakeClock(DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero))
            let hook = mkHook audit clock

            hook.RecordDenial {
                baseDenial with
                    Subject = AnonymousSession "sess-xyz"
                    ScopeId = None
            }
            |> Async.RunSynchronously

            let scopeId, p = audit.Rows.Head
            Expect.equal scopeId "_platform" "scope-less denial written under _platform"
            Expect.equal p.SubjectKind "anonymous" "anonymous kind"
            Expect.equal p.SubjectId None "no subject id for anonymous (no PII)"
        }

        test "flood guard coalesces a probing burst into bounded rows with an accurate count" {
            let audit = CapturingAuditLog()
            let clock = FakeClock(DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero))
            let hook = mkHook audit clock :?> AuthAuditHook

            // 100 denials on the same (route, subject) within the 60s window.
            for _ in 1..100 do
                (hook :> IAuthAuditHook).RecordDenial baseDenial |> Async.RunSynchronously

            // Leading-edge row emitted immediately; the rest suppressed.
            Expect.hasLength audit.Rows 1 "burst coalesced to a single leading-edge row during the window"
            Expect.equal (audit.Rows.Head |> snd |> _.DedupCount) 1 "leading-edge DedupCount is 1"

            // Flush the suppressed tail (what the /dev/auth-denials read path does).
            hook.FlushPending() |> Async.RunSynchronously

            Expect.hasLength audit.Rows 2 "flush adds exactly one summary row — bounded"
            let total = audit.Rows |> List.sumBy (snd >> _.DedupCount)
            Expect.equal total 100 "sum of DedupCount equals the true denial total"
        }

        test "window rollover flushes the prior suppressed tail then opens a fresh leading edge" {
            let audit = CapturingAuditLog()
            let clock = FakeClock(DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero))
            let hook = mkHook audit clock

            // 3 denials in window 1: 1 leading + 2 suppressed.
            for _ in 1..3 do
                hook.RecordDenial baseDenial |> Async.RunSynchronously

            Expect.hasLength audit.Rows 1 "window 1 leading row only"

            // Advance past the window; the next denial rolls over.
            clock.Advance(TimeSpan.FromSeconds 61.0)
            hook.RecordDenial baseDenial |> Async.RunSynchronously

            // Rollover emits the prior window's suppressed summary (count 2)
            // and a fresh leading edge (count 1).
            Expect.hasLength audit.Rows 3 "rollover adds summary + new leading"
            let total = audit.Rows |> List.sumBy (snd >> _.DedupCount)
            Expect.equal total 4 "all 4 denials accounted for (1+2 summary +1)"
        }

        test "distinct (route, subject) keys each emit independently" {
            let audit = CapturingAuditLog()
            let clock = FakeClock(DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero))
            let hook = mkHook audit clock

            hook.RecordDenial baseDenial |> Async.RunSynchronously

            hook.RecordDenial {
                baseDenial with
                    Subject = AuthenticatedUser "user-2"
                    ScopeId = Some "user-2"
            }
            |> Async.RunSynchronously

            hook.RecordDenial {
                baseDenial with
                    Route = "POST /api/Other/Do"
            }
            |> Async.RunSynchronously

            Expect.hasLength audit.Rows 3 "three distinct keys → three leading rows, none coalesced"
        }

        test "AuthDenialRequirement.toString covers every case" {
            Expect.equal (AuthDenialRequirement.toString SurfaceDenialRequirement) "surface" "surface"
            Expect.equal (AuthDenialRequirement.toString RoleDenialRequirement) "role" "role"
            Expect.equal (AuthDenialRequirement.toString ShareTokenDenialRequirement) "share-token" "share-token"
            Expect.equal (AuthDenialRequirement.toString SseIdentityDenialRequirement) "sse-identity" "sse-identity"

            Expect.equal
                (AuthDenialRequirement.toString ModulePermissionDenialRequirement)
                "module-permission"
                "module-permission"

            Expect.equal
                (AuthDenialRequirement.toString KbDestructiveDenialRequirement)
                "kb-destructive"
                "kb-destructive"
        }
    ]
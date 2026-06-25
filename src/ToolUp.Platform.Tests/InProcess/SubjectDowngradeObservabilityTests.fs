module ToolUp.Platform.Tests.InProcess.SubjectDowngradeObservabilityTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Middleware

// Phase 246 — two adjacent scope-resolution behaviours:
//   1. `resolverDowngradeSignal` — the authorization-shaped resolver
//      `Error` (`NotTeamMember` / `UnsupportedSubject`) is a distinct,
//      named signal; `SubjectResolutionFailed` is suppressed (the
//      resolver already logged + A1 audited it).
//   2. `StorageScopeDerivation.fromSubject` — an undeclared subject kind
//      now fails CLOSED (Persist = false) instead of fail-open persistent,
//      and emits a one-time-per-kind diagnostic Warn.

let private cfg (surfaces: SurfaceProfile list) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = surfaces
}

/// Captures `Warn` lines so the once-per-kind diagnostic is assertable.
type private CapturingLogger() =
    let warns = System.Collections.Generic.List<string>()
    member _.Warns = List.ofSeq warns

    interface ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(m: string) = warns.Add m
        member _.Error(_: string, _: exn option) = ()

[<Tests>]
let tests =
    testList "Phase 246 — subject-resolution downgrade observability" [

        // ── resolverDowngradeSignal (distinct named signal + suppression) ──

        test "NotTeamMember → a distinct named signal" {
            match resolverDowngradeSignal (SubjectResolutionError.NotTeamMember "team-42") with
            | Some(label, detail) ->
                Expect.equal label "NotTeamMember" "names the bridged case"
                Expect.stringContains detail "team-42" "carries the team id"
            | None -> failtest "expected a signal for NotTeamMember"
        }

        test "UnsupportedSubject → a distinct named signal" {
            match resolverDowngradeSignal (SubjectResolutionError.UnsupportedSubject UserKind) with
            | Some(label, detail) ->
                Expect.equal label "UnsupportedSubject" "names the bridged case"
                Expect.stringContains detail "UserKind" "names the unsupported kind"
            | None -> failtest "expected a signal for UnsupportedSubject"
        }

        test "SubjectResolutionFailed → suppressed (resolver already logged + A1 audited)" {
            Expect.isNone
                (resolverDowngradeSignal (SubjectResolutionError.SubjectResolutionFailed "store unreachable"))
                "must not duplicate the infra-failure signal"
        }

        // ── fail-closed storage scope for an undeclared subject kind ──

        test "Undeclared Anonymous kind → fails closed (Persist = false)" {
            // Surfaces declares only AuthenticatedUser; an AnonymousSession
            // fallback the resolver can still produce is undeclared.
            let scope =
                StorageScopeDerivation.fromSubject None (cfg Surfaces.individual) (AnonymousSession "sess-1")

            Expect.isFalse scope.Persist "undeclared kind must not silently persist"
            Expect.equal scope.Container "session-sess-1" "scope shape otherwise unchanged"
        }

        test "Undeclared Team kind → fails closed (Persist = false)" {
            let scope =
                StorageScopeDerivation.fromSubject None (cfg Surfaces.individual) (TeamMember("u1", "t1"))

            Expect.isFalse scope.Persist "undeclared team kind fails closed"
        }

        test "Declared persistent kind is unchanged (Persist = true)" {
            let scope =
                StorageScopeDerivation.fromSubject None (cfg Surfaces.individual) (Subject.AuthenticatedUser "u1")

            Expect.isTrue scope.Persist "a declared persistent surface still persists"
        }

        test "Declared ephemeral kind is unchanged (Persist = false, by config not by fail-closed)" {
            let scope =
                StorageScopeDerivation.fromSubject None (cfg Surfaces.trial) (Subject.AuthenticatedUser "u1")

            Expect.isFalse scope.Persist "trial surface is ephemeral by declaration"
        }

        test "Declared persistent Anonymous surface persists (proves fail-closed only fires when undeclared)" {
            let scope =
                StorageScopeDerivation.fromSubject
                    None
                    (cfg [ SurfaceProfile.anonymousPersistent ])
                    (AnonymousSession "sess-2")

            Expect.isTrue scope.Persist "a declared persistent Anonymous surface is honoured"
        }

        // ── one-time-per-kind diagnostic ──

        test "Undeclared kind emits at most one Warn across repeated derivations (no per-request spam)" {
            let logger = CapturingLogger()
            let config = cfg Surfaces.individual
            // Two derivations of the same undeclared kind.
            StorageScopeDerivation.fromSubject (Some(logger :> ILogger)) config (AnonymousSession "a")
            |> ignore

            StorageScopeDerivation.fromSubject (Some(logger :> ILogger)) config (AnonymousSession "b")
            |> ignore

            let anonWarns =
                logger.Warns
                |> List.filter (fun m -> m.Contains "Anonymous" && m.Contains "EPHEMERAL")

            Expect.isLessThanOrEqual
                (List.length anonWarns)
                1
                "the fail-closed diagnostic is once-per-kind-per-process, never per request"
        }
    ]
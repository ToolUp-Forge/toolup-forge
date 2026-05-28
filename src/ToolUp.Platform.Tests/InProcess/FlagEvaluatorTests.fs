module ToolUp.Platform.Tests.InProcess.FlagEvaluatorTests

open System
open System.Collections.Concurrent
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.FeatureFlagStore

// ─── Test doubles ────────────────────────────────────────────────

/// In-memory logger that records every warning. Used to assert the
/// "unknown-key reads log a warning" rule — this is deliberate typo
/// protection.
type private RecordingLogger() =
    let warnings = ConcurrentBag<string>()

    member _.Warnings = warnings |> Seq.toList

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn msg = warnings.Add msg
        member _.Error(_, _) = ()

// ─── Helpers ─────────────────────────────────────────────────────

let private makeStore () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-flageval-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
    BlobFeatureFlagStore(storage) :> IFeatureFlagStore

let private boolFlag key defaultValue = {
    Key = key
    DefaultValue = FlagValue.Bool defaultValue
    Description = "test flag"
    Owner = None
}

let private variantFlag key options defaultValue = {
    Key = key
    DefaultValue = FlagValue.Variant(options, defaultValue)
    Description = "test variant flag"
    Owner = None
}

/// Convenience — set a flag or fail the test. Saves wrapping every
/// arrangement step in a match.
let private setFlag (store: IFeatureFlagStore) scope key value = async {
    match! store.SetFlag(scope, key, value) with
    | Ok() -> return ()
    | Error e -> return failtestf "SetFlag failed: %s" e
}

// ─── Tests ───────────────────────────────────────────────────────

let tests =
    testList "FlagEvaluator" [
        // ── Declared-default fallback ────────────────────────────
        testCaseAsync "IsEnabled — declared default used when no scope set"
        <| async {
            let store = makeStore ()
            let decl = [ boolFlag "m.f" true ]
            let eval = FlagEvaluator.create store decl None
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "alice")
            let! v = eval.IsEnabled "m.f" ctx
            Expect.isTrue v "declared default true flows through"
        }

        testCaseAsync "ResolveVariant — declared default used when no scope set"
        <| async {
            let store = makeStore ()
            let decl = [ variantFlag "m.theme" [ "legacy"; "new" ] "legacy" ]
            let eval = FlagEvaluator.create store decl None
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "alice")
            let! v = eval.ResolveVariant "m.theme" ctx
            Expect.equal v "legacy" "declared default variant flows through"
        }

        // ── Unknown-key typo protection ──────────────────────────
        testCaseAsync "Unknown key — IsEnabled returns false + logs warning"
        <| async {
            let store = makeStore ()
            let logger = RecordingLogger()
            let eval = FlagEvaluator.create store [] (Some(logger :> ILogger))
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "alice")
            let! v = eval.IsEnabled "typo.key" ctx
            Expect.isFalse v "undeclared key defaults to false"

            Expect.isTrue
                (logger.Warnings |> List.exists (fun w -> w.Contains "typo.key"))
                "warning mentions the typo'd key"
        }

        // ── Scope-walk precedence ────────────────────────────────
        testCaseAsync "Team override wins over Platform"
        <| async {
            let store = makeStore ()
            let decl = [ boolFlag "m.f" false ]
            let eval = FlagEvaluator.create store decl None

            do! setFlag store FlagScope.Platform "m.f" (FlagValue.Bool false)
            do! setFlag store (FlagScope.Team "teamA") "m.f" (FlagValue.Bool true)

            let ctx = AccessContext.unrestricted (Subject.TeamMember("alice", "teamA"))
            let! v = eval.IsEnabled "m.f" ctx
            Expect.isTrue v "team override beats platform"
        }

        testCaseAsync "User override wins over Team"
        <| async {
            let store = makeStore ()
            let decl = [ boolFlag "m.f" false ]
            let eval = FlagEvaluator.create store decl None

            do! setFlag store (FlagScope.Team "teamA") "m.f" (FlagValue.Bool false)
            do! setFlag store (FlagScope.User "alice") "m.f" (FlagValue.Bool true)

            let ctx = AccessContext.unrestricted (Subject.TeamMember("alice", "teamA"))
            let! v = eval.IsEnabled "m.f" ctx
            Expect.isTrue v "user override beats team"
        }

        testCaseAsync "Platform override applied when no User/Team set"
        <| async {
            let store = makeStore ()
            let decl = [ boolFlag "m.f" false ]
            let eval = FlagEvaluator.create store decl None

            do! setFlag store FlagScope.Platform "m.f" (FlagValue.Bool true)

            let ctx = AccessContext.unrestricted (Subject.TeamMember("bob", "teamB"))
            let! v = eval.IsEnabled "m.f" ctx
            Expect.isTrue v "platform value applied when neither user nor team overrode"
        }

        testCaseAsync "Anonymous mode — only Platform is read; User/Team scopes ignored"
        <| async {
            let store = makeStore ()
            let decl = [ boolFlag "m.f" false ]
            let eval = FlagEvaluator.create store decl None

            // Hostile case: a value under `User "anonymous"` must NOT
            // win in Anonymous mode. A production deployment should
            // never persist under anonymous user ids, but the walk
            // rules must not rely on that promise.
            do! setFlag store (FlagScope.User "anonymous") "m.f" (FlagValue.Bool true)
            do! setFlag store FlagScope.Platform "m.f" (FlagValue.Bool false)

            let ctx = AccessContext.unrestricted (Subject.AnonymousSession "anonymous")
            let! v = eval.IsEnabled "m.f" ctx
            Expect.isFalse v "Anonymous mode uses only platform; user/team writes ignored"
        }

        testCaseAsync "Individual mode — skips Team even if a team flag exists"
        <| async {
            let store = makeStore ()
            let decl = [ boolFlag "m.f" false ]
            let eval = FlagEvaluator.create store decl None

            do! setFlag store (FlagScope.Team "teamA") "m.f" (FlagValue.Bool true)

            // Individual mode: no team concept, even if some stale
            // Team document exists in storage.
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "alice")
            let! v = eval.IsEnabled "m.f" ctx
            Expect.isFalse v "Individual mode does not consult Team scope"
        }

        testCaseAsync "Team mode with no active team — still walks User + Platform"
        <| async {
            let store = makeStore ()
            let decl = [ boolFlag "m.f" false ]
            let eval = FlagEvaluator.create store decl None

            do! setFlag store (FlagScope.User "alice") "m.f" (FlagValue.Bool true)

            // Authenticated user without active team (e.g. user signed
            // in but hasn't picked a team yet — Phase 66 collapses the
            // pre-team-resolution shape to `AuthenticatedUser`). User
            // override must still apply; Team scope is quietly skipped.
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "alice")
            let! v = eval.IsEnabled "m.f" ctx
            Expect.isTrue v "User scope still consulted when Team mode has no active team"
        }

        // ── TryEvaluate primitive ────────────────────────────────
        testCaseAsync "TryEvaluate returns None when no scope has set the key"
        <| async {
            let store = makeStore ()
            let eval = FlagEvaluator.create store [ boolFlag "m.f" true ] None
            let ctx = AccessContext.unrestricted (Subject.TeamMember("alice", "teamA"))
            let! v = eval.TryEvaluate "m.f" ctx
            Expect.isNone v "no override set → None (caller applies declared default)"
        }

        testCaseAsync "TryEvaluate surfaces the winning scope's value verbatim"
        <| async {
            let store = makeStore ()
            let eval = FlagEvaluator.create store [ variantFlag "m.v" [ "a"; "b" ] "a" ] None
            let value = FlagValue.Variant([ "a"; "b" ], "b")
            do! setFlag store (FlagScope.Team "teamA") "m.v" value

            let ctx = AccessContext.unrestricted (Subject.TeamMember("alice", "teamA"))
            let! v = eval.TryEvaluate "m.v" ctx
            Expect.equal v (Some value) "full FlagValue returned, not coerced"
        }

        // ── Schema drift ─────────────────────────────────────────
        testCaseAsync "IsEnabled on a Variant-typed override falls back + warns"
        <| async {
            let store = makeStore ()
            let logger = RecordingLogger()
            let decl = [ boolFlag "m.f" true ]
            let eval = FlagEvaluator.create store decl (Some(logger :> ILogger))

            // Schema drift: declared Bool, but someone stored a Variant
            // override (admin UI bug or manual blob edit).
            do! setFlag store FlagScope.Platform "m.f" (FlagValue.Variant([ "x" ], "x"))

            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "alice")
            let! v = eval.IsEnabled "m.f" ctx
            Expect.isTrue v "falls back to declared default"

            Expect.isNonEmpty logger.Warnings "schema-drift warning recorded"
        }
    ]
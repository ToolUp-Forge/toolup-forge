module ToolUp.Platform.Tests.InProcess.SubjectWildcardAnalyzerTests

open System.IO
open Expecto
open ToolUp.Platform.Tests.Support.SubjectWildcardAnalyzer

// ─── SubjectWildcardAnalyzer fixture contract ────────────────────
//
// Pins the heuristic against the canonical §4.1 E3 leak pair from
// `toolup-forge/docs/design/mixed-mode-platform.md` plus the live
// `_ -> return Ok()` wildcard idiom that appears across every
// write-gate handler. Origin: TIDY-UP Unbundled item "Handler
// `match Subject` wildcard-fallback linter for Phase 66 cross-shape
// safety" — Phase 66 §6.3 OQ3.

// ─── Fixtures ─────────────────────────────────────────────────────

/// The §4.1 E3 SAFE pair: wildcard returns a benign error string.
let private fixtureSafeErrorWildcard =
    """module Demo

let handle (ctx: AccessContext) =
    match ctx.Subject with
    | TeamMember (uid, tid) -> doTeamThing uid tid
    | _ -> Error "Not in team scope"
"""

/// The §4.1 E3 LEAK pair (verbatim shape from design doc).
let private fixtureE3Leak =
    """module Demo

let handle (ctx: AccessContext) =
    match ctx.Subject with
    | TeamMember (uid, tid) -> doTeamThing uid tid
    | _ -> doTeamThing "anonymous" "default"
"""

/// Exhaustive enumeration of all four Subject kinds — no wildcard.
let private fixtureExhaustiveFourArm =
    """module Demo

let handle (ctx: AccessContext) =
    match ctx.Subject with
    | AnonymousSession sid -> Ok sid
    | AuthenticatedUser uid -> Ok uid
    | TeamMember (uid, _) -> Ok uid
    | ClaimBearer _ -> Ok "claim"
"""

/// Live `_ -> return Ok()` write-gate idiom (mirrors ConfigHandler /
/// JobApiHandler / FormApiHandler etc.).
let private fixtureLiveWriteGate =
    """module Demo

let ensureWriteAllowed (ctx: AccessContext) = async {
    match ctx.Subject with
    | TeamMember(userId, teamId) ->
        let! role = teamStore.GetMemberRole(teamId, userId)
        return
            match role with
            | Some r when TeamRoles.canWriteTeamConfig r -> Ok()
            | _ -> Error "not owner / admin"
    | _ -> return Ok()
}
"""

/// Wildcard returning `()` — no-op pass-through.
let private fixtureUnitWildcard =
    """module Demo

let log (ctx: AccessContext) =
    match ctx.Subject with
    | TeamMember(uid, tid) -> recordTeamAccess uid tid
    | _ -> ()
"""

/// Different DU constructor (AuthenticatedUser), same cross-shape leak
/// shape — the heuristic must generalise beyond TeamMember.
let private fixtureAuthLeak =
    """module Demo

let lookup (ctx: AccessContext) =
    match ctx.Subject with
    | AuthenticatedUser uid -> queryUserDb uid
    | _ -> queryUserDb "anonymous"
"""

/// Match on a non-Subject discriminant — must not trip the analyser
/// even when the wildcard body shares identifiers with sibling arms.
let private fixtureUnrelatedMatch =
    """module Demo

let handle role =
    match role with
    | Some r -> apply r
    | _ -> apply "default"
"""

/// Multi-line constructor pattern (`AnonymousSession _ | AuthenticatedUser _ ->`).
/// The first `|` line has no `->`; the analyser splits it into a
/// no-body arm but the overall match still classifies correctly.
let private fixtureMultiPatternArm =
    """module Demo

let scope (ctx: AccessContext) =
    match ctx.Subject with
    | AnonymousSession _
    | ClaimBearer _ -> []
    | AuthenticatedUser uid -> [ scopeFor uid ]
    | TeamMember(uid, tid) -> [ scopeFor uid; teamScopeFor tid ]
"""

// ─── Helpers ──────────────────────────────────────────────────────

let private lint fixture = lintSource "fixture.fs" fixture

let private expectClean fixture description =
    let diags = lint fixture

    if not (List.isEmpty diags) then
        let detail = diags |> List.map formatDiagnostic |> String.concat "\n"

        failtestf "%s — expected no diagnostics, got:\n%s" description detail

let private expectFlagged fixture description =
    let diags = lint fixture
    Expect.isNonEmpty diags (sprintf "%s — expected at least one diagnostic" description)

// ─── Test cases ──────────────────────────────────────────────────

let private fixtureTests =
    testList "fixture-based heuristic" [
        test "exhaustive 4-arm Subject match passes clean (acceptance §1)" {
            expectClean fixtureExhaustiveFourArm "exhaustive 4-arm enumeration"
        }

        test "§4.1 E3 safe wildcard (returns Error string) passes clean" {
            expectClean fixtureSafeErrorWildcard "safe Error wildcard"
        }

        test "§4.1 E3 leak pattern is flagged (acceptance §2)" {
            let diags = lint fixtureE3Leak

            Expect.isNonEmpty diags "§4.1 E3 demonstrative leak must be flagged"

            let hasDoTeamThing = diags |> List.exists (fun d -> d.SharedIdent = "doTeamThing")

            Expect.isTrue hasDoTeamThing (sprintf "expected `doTeamThing` to be the shared identifier; got: %A" diags)
        }

        test "live `_ -> return Ok()` write-gate idiom passes clean" {
            expectClean fixtureLiveWriteGate "live write-gate idiom"
        }

        test "wildcard returning unit `()` passes clean" { expectClean fixtureUnitWildcard "unit wildcard" }

        test "AuthenticatedUser-vs-anonymous leak is flagged (heuristic generalises)" {
            expectFlagged fixtureAuthLeak "AuthenticatedUser cross-shape leak"
        }

        test "match on a non-Subject discriminant is ignored" { expectClean fixtureUnrelatedMatch "non-Subject match" }

        test "multi-pattern arm (`A _ | B _ ->`) parses cleanly" {
            expectClean fixtureMultiPatternArm "multi-pattern arm + safe wildcards"
        }
    ]

// ─── Live-source self-audit ──────────────────────────────────────
//
// Walk every `.fs` file under `toolup-forge/src/` that pattern-matches
// on `Subject` and assert the analyser surfaces zero findings.
// Today's live wildcards (Config/Job/Form/Maintenance/WebhookApi
// `ensureWriteAllowed`, etc.) return `Ok()` / `true` / `Error "..."`
// and therefore should not trip the heuristic. The audit is the
// ratchet: any future commit that introduces a real cross-shape
// leak fails the test pack and is caught at PR review.
//
// If a finding surfaces here later, the workspace `CLAUDE.md`
// "Close-out sequence" mandates: file the offending file:line as a
// new TIDY-UP entry, do not silently fix it inside the analyser
// commit (scope creep), and let the analyser's catch be the trigger
// for a focused follow-up.

let private repoRoot () =
    let asmLoc = System.Reflection.Assembly.GetExecutingAssembly().Location
    let asmDir = Path.GetDirectoryName asmLoc
    // bin/Debug/net10.0 → ToolUp.Platform.Tests → src → toolup-forge
    Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."))

let private liveSubjectMatchFiles () =
    let srcRoot = Path.Combine(repoRoot (), "src")

    if not (Directory.Exists srcRoot) then
        []
    else
        Directory.EnumerateFiles(srcRoot, "*.fs", SearchOption.AllDirectories)
        |> Seq.filter (fun path ->
            // Skip the analyser's own fixture file — it embeds the
            // demonstrative leak as a test fixture string.
            let normalised = path.Replace('\\', '/')
            not (normalised.EndsWith("SubjectWildcardAnalyzerTests.fs")))
        |> Seq.filter (fun path ->
            // Cheap pre-filter: only read files whose text contains
            // both `match` and `Subject`. Saves reading every .fs in
            // the SDK on every test-pack run.
            try
                let text = File.ReadAllText path
                text.Contains "Subject" && text.Contains "match"
            with _ ->
                false)
        |> List.ofSeq

let private liveAuditTest = test "live forge source has no Subject wildcard-fallback leaks" {
    let candidates = liveSubjectMatchFiles ()

    let allDiagnostics =
        candidates
        |> List.collect (fun path ->
            let text = File.ReadAllText path

            let relative =
                path.Replace(repoRoot () + Path.DirectorySeparatorChar.ToString(), "").Replace('\\', '/')

            lintSource relative text)

    if not (List.isEmpty allDiagnostics) then
        let detail = allDiagnostics |> List.map formatDiagnostic |> String.concat "\n"

        failtestf
            "Subject wildcard-fallback analyser flagged %d site(s). File each as a new TIDY-UP entry per the close-out sequence; do not silently fix here.\n%s"
            (List.length allDiagnostics)
            detail
}

[<Tests>]
let tests = testList "SubjectWildcardAnalyzer" [ fixtureTests; liveAuditTest ]
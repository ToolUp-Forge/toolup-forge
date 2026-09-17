// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.V1ReadinessTests

open System.IO
open System.Reflection
open Expecto
open ToolUp.Forge
open ToolUp.Forge.V1Readiness

// ─── Phase 257 — the v1.0 readiness scorecard ─────────────────────────
//
// `V1Readiness` runs at a release, against a checkout, a git history and
// an adoption matrix that lives in another repository. This pack runs on
// every commit against data, and it is where each row's RULE is decided:
// a known-good fixture scores all-green, and one fixture per cause scores
// exactly that row red — a stale baseline, a pending adoption cell, an
// undecided rename, an unpacked seam, an undocumented subject, an open
// deprecation — so the day the scorecard first turns green is not the
// first time its failure paths were exercised. The last group pins the
// parsers against the COMMITTED inputs, so a reformat of the decision
// table or the sidecar reddens here rather than silently reading as a
// pass or a `not yet`.

/// Repo root (`toolup-forge`) from the running test assembly, the same
/// walk `ConformanceCoverageTests` and `SdkManifestTests` use.
let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

// ─── Fixtures ─────────────────────────────────────────────────────────

let private goodSidecar =
    "# Public XML-doc coverage floor — Phase 261.\n# comment\nToolUp.Alpha 10/10\nToolUp.Beta 5/5\n"

let private shortSidecar = "ToolUp.Alpha 10/10\nToolUp.Beta 3/5\n"

let private decisionTable (decision: string) =
    "# Phase 256 — doc\n\n## The parked renames — decided\n\n"
    + "| # | Item | Decision | Rationale |\n|---|---|---|---|\n"
    + "| 1 | `Foo.Api` lacks the `I` prefix | **deferred-to-2.0** | convention is mixed |\n"
    + sprintf "| 2 | `Bar.Base` accessor leak | %s | shape of the ladder |\n" decision
    + "\nNothing is left undecided.\n"

let private matrix (appCell: string) =
    "## Matrix — forge → consumers\n\n_Generated._\n\n"
    + "| Refactor | app-b | app-a | app-c |\n|---|---|---|---|\n"
    + sprintf "| 0.10.0 | 🟡 | %s | 🟡 |\n" appCell
    + "| 214 — Generated config reference | ⛔ N-A | ✅ `2ecd056` | 🟡 |\n"
    + "| 16b | 🟡 | ⏸ deferred — reason | 🟡 |\n"
    + "\n## Matrix — other → consumers\n\n"
    + "| Refactor | app-b | app-a | app-c |\n|---|---|---|---|\n"
    + "| 330 — Interaction-id | 🟡 | 🟡 | 🟡 |\n"

let private baselineWith (markers: int) =
    let body =
        [
            for i in 1..markers do
                yield sprintf "Demo.T.Alpha%d() : System.Int32" i
                yield sprintf "Demo.T.Alpha%d() : System.Int32  (obsolete)" i
        ]
        |> String.concat "\n"

    "# header\nDemo.T (class)\n" + body + "\nDemo.T.Beta() : System.String\n"

/// The all-green inputs, one per row.
let private greenRows = [
    baselineStability 2 (Available [ "v1.0.0-rc.2", true; "v1.0.0-rc.1", true; "v0.99.0", false ])
    conformanceCoverage 100.0 (Available(4, 4))
    docCoverage 100.0 (Available goodSidecar)
    undecidedRenames (Available(decisionTable "**resolved — not a defect**"))
    adoptionPending [ "app-a" ] (Available(matrix "✅ `abc1234`"))
    openDeprecations 0 (Available [ "ToolUp.Alpha.approved.txt", baselineWith 0 ])
]

let private verdictOf (id: string) (rows: Row list) =
    (rows |> List.find (fun r -> r.Id = id)).Verdict

let tests =
    testList "V1Readiness" [
        // ── the all-green fixture ──
        testCase "a known-good fixture scores every row green and reads READY" (fun _ ->
            for r in greenRows do
                Expect.equal r.Verdict Pass (sprintf "%s: %s" r.Id r.Note)

            Expect.isTrue (ready greenRows) "ready"
            let doc = render "2026-01-01" "abc1234" "`v1-readiness.json`" greenRows
            Expect.stringContains doc "**Verdict: READY" "verdict line"

            Expect.equal
                (doc.Split('\n') |> Array.filter (fun l -> l.StartsWith "| ") |> Array.length)
                7
                "header + six rows"

            Expect.stringContains doc "on 2026-01-01 at `abc1234`" "the stamp the target supplies")

        testCase "an empty scorecard is not ready" (fun _ -> Expect.isFalse (ready []) "no rows is not a pass")

        // ── one red per cause ──
        testCase "a stale baseline (the newest release differs) scores baseline-stable red" (fun _ ->
            let row = baselineStability 2 (Available [ "v0.23.0", false; "v0.22.0", true ])
            Expect.equal row.Verdict Fail "verdict"
            Expect.equal row.Measured "0 release(s)" "measured"
            Expect.stringContains row.Note "v0.23.0" "names the release the surface moved since"

            let rowsWithStale =
                greenRows |> List.map (fun r -> if r.Id = "baseline-stable" then row else r)

            Expect.isFalse (ready rowsWithStale) "one red row fails the scorecard")

        testCase
            "stability counts consecutive identical releases from the newest and stops at the first difference"
            (fun _ ->
                Expect.equal (stableReleaseCount [ "c", true; "b", true; "a", false; "z", true ]) 2 "stops at a"
                Expect.equal (stableReleaseCount [ "c", false; "b", true ]) 0 "newest differs"
                Expect.equal (stableReleaseCount []) 0 "no releases"

                let short = baselineStability 3 (Available [ "c", true; "b", true; "a", false ])
                Expect.equal short.Verdict Fail "2 < 3"
                Expect.stringContains short.Note "moved at a" "names where it moved")

        testCase "an unpacked seam scores conformance-coverage red with the count" (fun _ ->
            let row = conformanceCoverage 100.0 (Available(3, 4))
            Expect.equal row.Verdict Fail "verdict"
            Expect.equal row.Measured "3 / 4 (75.0%)" "measured"
            Expect.stringContains row.Note "1 seam(s) carry no pack" "count"
            Expect.equal (conformanceCoverage 75.0 (Available(3, 4))).Verdict Pass "a lowered threshold admits it")

        testCase "zero derived seams is not a pass" (fun _ ->
            let row = conformanceCoverage 100.0 (Available(0, 0))
            Expect.equal row.Verdict Fail "0/0 is not 100%"
            Expect.stringContains row.Note "not a pass" "says why")

        testCase "an undocumented subject scores doc-coverage red with the sum over assemblies" (fun _ ->
            let row = docCoverage 100.0 (Available shortSidecar)
            Expect.equal row.Verdict Fail "verdict"
            Expect.equal row.Measured "13 / 15 (86.7%)" "summed"
            Expect.stringContains row.Note "2 public subject(s) across 2 assemblies" "the shortfall"
            Expect.equal (docCoverage 80.0 (Available shortSidecar)).Verdict Pass "threshold")

        testCase "the sidecar parser refuses a line it cannot read rather than skipping it" (fun _ ->
            match parseDocCoverage "ToolUp.Alpha 10/10\nToolUp.Beta ten of twelve\n" with
            | Error e -> Expect.stringContains e "ToolUp.Beta ten of twelve" "names the line"
            | Ok _ -> failtest "an unreadable line was skipped"

            match parseDocCoverage "# only comments\n" with
            | Error e -> Expect.stringContains e "no assembly lines" "empty"
            | Ok _ -> failtest "an empty sidecar parsed"

            let row = docCoverage 100.0 (Available "garbage")
            Expect.equal row.Verdict NotYet "an unparseable sidecar is a not-yet row, not a crash")

        testCase "an undecided rename scores undecided-renames red and names the row" (fun _ ->
            for cell in [ "undecided"; ""; "TBD"; "escalated to the operator"; "open" ] do
                let row = undecidedRenames (Available(decisionTable cell))
                Expect.equal row.Verdict Fail (sprintf "cell '%s'" cell)
                Expect.equal row.Measured "1 of 2 undecided" (sprintf "measured for '%s'" cell)
                Expect.stringContains row.Note "`Bar.Base` accessor leak" (sprintf "names the item for '%s'" cell)

            for cell in [ "**deferred-to-2.0**"; "resolved"; "shipped in 0.24"; "kept" ] do
                Expect.equal (undecidedRenames (Available(decisionTable cell))).Verdict Pass (sprintf "cell '%s'" cell))

        testCase "a doc with no decision table is a not-yet row" (fun _ ->
            let row = undecidedRenames (Available "# Phase 256\n\nProse only.\n")
            Expect.equal row.Verdict NotYet "verdict"
            Expect.stringContains row.Note "no table" "why")

        testCase
            "a pending adoption cell scores adoption-pending red, counting only the declared consumer's column"
            (fun _ ->
                let row = adoptionPending [ "app-a" ] (Available(matrix "🟡"))
                Expect.equal row.Verdict Fail "verdict"

                Expect.equal
                    row.Measured
                    "1 pending across 1 declared consumer(s)"
                    "one pending cell — deferred and adopted cells do not count"

                Expect.isFalse
                    (row.Measured.Contains "app-a" || row.Note.Contains "app-a")
                    "the row never names a consumer"

                Expect.equal (pendingCells "forge" "app-c" (matrix "🟡")) (Ok 3) "another column, forge grid only"

                Expect.equal
                    (pendingCells "other" "app-a" (matrix "🟡"))
                    (Ok 1)
                    "the other producer's grid is separate"

                Expect.equal
                    (pendingPerConsumer [ "app-a"; "app-c" ] (matrix "🟡"))
                    [ "app-a", Ok 1; "app-c", Ok 3 ]
                    "the per-consumer split the target prints")

        testCase "widening the consumer set is a declaration, and every declared consumer is measured" (fun _ ->
            let row = adoptionPending [ "app-a"; "app-c" ] (Available(matrix "✅ `abc`"))
            Expect.equal row.Verdict Fail "app-c is pending"
            Expect.equal row.Measured "3 pending across 2 declared consumer(s)" "summed over the declaration"
            Expect.equal (parseConsumers "app-a; app-c,app-a ,, ") [ "app-a"; "app-c" ] "the declaration parser"
            Expect.equal (parseConsumers null) [] "unset"

            let undeclared = adoptionPending [] (Available(matrix "🟡"))
            Expect.equal undeclared.Verdict NotYet "no consumer declared is a not-yet row"
            Expect.stringContains undeclared.Note consumersEnvVar "names the variable to set")

        testCase "a consumer the matrix does not score is a not-yet row, never zero pending" (fun _ ->
            let row = adoptionPending [ "nobody" ] (Available(matrix "🟡"))
            Expect.equal row.Verdict NotYet "verdict"
            Expect.stringContains row.Note "no column for consumer #1" "the position, never the name"
            Expect.isFalse (row.Note.Contains "nobody") "the row never names a consumer"

            let noGrid =
                adoptionPending [ "app-a" ] (Available "## Nothing here\n\n| a | b |\n|---|---|\n| 1 | 2 |\n")

            Expect.equal noGrid.Verdict NotYet "no forge grid"
            Expect.stringContains noGrid.Note "no `Refactor` grid" "why")

        testCase "an open deprecation scores open-deprecations red with the per-baseline count" (fun _ ->
            let row =
                openDeprecations
                    0
                    (Available [
                        "ToolUp.Alpha.approved.txt", baselineWith 2
                        "ToolUp.Beta.approved.txt", baselineWith 0
                        "ToolUp.Gamma.approved.txt", baselineWith 1
                    ])

            Expect.equal row.Verdict Fail "verdict"
            Expect.equal row.Measured "3 open" "measured"
            Expect.stringContains row.Note "ToolUp.Alpha: 2, ToolUp.Gamma: 1" "per baseline, silent ones omitted"
            Expect.stringContains row.Note "openDeprecations" "names the knob a carry decision moves"

            Expect.equal
                (openDeprecations 3 (Available [ "a.approved.txt", baselineWith 3 ])).Verdict
                Pass
                "an allowance admits it")

        testCase "the marker count reads only the sanctioned two-line shape" (fun _ ->
            Expect.equal (countObsoleteMarkers (baselineWith 2)) 2 "two markers"

            Expect.equal
                (countObsoleteMarkers "Demo.T.Alpha() : System.Int32 [obsolete]\n")
                0
                "the forbidden in-place shape is not a marker"

            Expect.equal (countObsoleteMarkers "Demo.T.Alpha() : System.Int32  (obsolete)\r\n") 1 "CRLF")

        // ── graceful degradation ──
        testCase "every unavailable input renders as an explicit not-yet row that counts as failing" (fun _ ->
            let rows = [
                baselineStability 2 (Unavailable "git could not be read")
                baselineStability 2 (Available [])
                conformanceCoverage 100.0 (Unavailable "no Contracts directory")
                docCoverage 100.0 (Unavailable "sidecar absent")
                undecidedRenames (Unavailable "migration doc absent")
                adoptionPending [ "app-a" ] (Unavailable "TOOLUP_ADOPTION_MATRIX is not set")
                openDeprecations 0 (Unavailable "api-baselines/ is absent")
                openDeprecations 0 (Available [])
            ]

            for r in rows do
                Expect.equal r.Verdict NotYet (sprintf "%s: %s" r.Id r.Note)
                Expect.equal r.Measured "—" (sprintf "%s measured" r.Id)
                Expect.isNotEmpty r.Note (sprintf "%s says why" r.Id)

            Expect.isFalse (ready rows) "not-yet is not a pass"
            let doc = render "2026-01-01" "abc1234" "the built-in defaults" rows
            Expect.stringContains doc "⏳ not yet" "renders distinctly"
            Expect.stringContains doc "**Verdict: NOT READY — 8 of 8" "counted as failing")

        // ── thresholds ──
        testCase "an empty thresholds object reads as the defaults, and each key overrides one field" (fun _ ->
            Expect.equal (parseThresholds "{}") (Ok defaults) "defaults"

            let parsed =
                parseThresholds """{ "stableReleases": 3, "docCoveragePct": 80.5, "openDeprecations": 7 }"""

            Expect.equal
                parsed
                (Ok {
                    defaults with
                        StableReleases = 3
                        DocCoveragePct = 80.5
                        OpenDeprecations = 7
                })
                "overrides")

        testCase "the thresholds parser refuses an unknown key and a wrong type" (fun _ ->
            let refuses (json: string) (fragment: string) =
                match parseThresholds json with
                | Error e -> Expect.stringContains e fragment (sprintf "refusal for %s" json)
                | Ok t -> failtestf "%s parsed as %A" json t

            refuses """{ "stableRelease": 2 }""" "unknown key(s) stableRelease"
            refuses """{ "stableReleases": "two" }""" "must be an integer"
            refuses """{ "consumers": ["app-a"] }""" "unknown key(s) consumers"
            refuses """[1]""" "must be a JSON object"
            refuses """{ not json""" "not valid JSON")

        // ── the committed inputs ──
        testCase "the committed v1-readiness.json parses" (fun _ ->
            let path = Path.Combine(repoRoot (), thresholdsFileName)
            Expect.isTrue (File.Exists path) path

            match parseThresholds (File.ReadAllText path) with
            | Ok t -> Expect.isGreaterThan t.StableReleases 0 "declares a stability window"
            | Error e -> failtest e)

        testCase "the committed Phase 256 migration doc's decision table parses and every row is decided" (fun _ ->
            let path =
                Path.Combine(repoRoot (), "docs", "migrations", "256-public-surface-minimization.md")

            let row = undecidedRenames (Available(File.ReadAllText path))
            Expect.equal row.Verdict Pass row.Note

            match parseRenameDecisions (File.ReadAllText path) with
            | Ok rows -> Expect.equal rows.Length 5 "the five parked Tier-4 items"
            | Error e -> failtest e)

        testCase "the committed doc-coverage sidecar parses, one line per baseline" (fun _ ->
            let root = repoRoot ()
            let sidecar = Path.Combine(root, "api-baselines", "doc-coverage.approved.txt")

            match parseDocCoverage (File.ReadAllText sidecar) with
            | Ok rows ->
                let baselines =
                    Directory.GetFiles(Path.Combine(root, "api-baselines"), "*.approved.txt")
                    |> Array.filter (fun p -> Path.GetFileName p <> "doc-coverage.approved.txt")

                Expect.isGreaterThan rows.Length 0 "rows"
                Expect.isLessThanOrEqual rows.Length baselines.Length "no more coverage lines than baselines"
            | Error e -> failtest e)
    ]
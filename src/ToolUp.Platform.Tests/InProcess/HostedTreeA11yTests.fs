module ToolUp.Platform.Tests.InProcess.HostedTreeA11yTests

open System.IO
open Expecto
open ToolUp.Platform.Testing
open Toolup.Samples.ToyTreeBinding.ToyNode

// ─── Phase 277 — hosted-tree a11y conformance harness ──────────────────
//
// The harness runs under `VerifyAll` (via this pack), so a hosted-tree
// a11y regression fails the build, not a screen reader. Four proofs:
//   1. The a11y-clean fixture passes.
//   2. Each seeded violation class fails with a readable diagnostic
//      (unlabelled control, missing role, focus-order break, heading skip).
//   3. The Phase 202 `ToyNode` witness — a non-Fuaran stranger tree
//      language — lowers to a fragment the harness checks (clean tree
//      passes; an event-wrapped tree trips MissingRole).
//   4. OSS grep-guard.

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private hasViolation (predicate: HostedTreeA11y.A11yViolation -> bool) (result: HostedTreeA11y.A11yResult) =
    match result with
    | HostedTreeA11y.Conformant -> false
    | HostedTreeA11y.Violations vs -> List.exists predicate vs

// ─── 1 + 2. Shipped fixtures ──────────────────────────────────────────

let private fixtureTests =
    testList "Phase 277 — shipped fixtures" [
        testCase "the a11y-clean fixture is Conformant"
        <| fun _ ->
            Expect.equal
                (HostedTreeA11y.check HostedTreeA11y.conformantFixture.Html)
                HostedTreeA11y.Conformant
                "a well-formed outline with a labelled control passes"

        testCase "each seeded violation fixture fails"
        <| fun _ ->
            for fixture in HostedTreeA11y.violationFixtures do
                match HostedTreeA11y.check fixture.Html with
                | HostedTreeA11y.Conformant -> failtestf "fixture '%s' must fail a11y but passed" fixture.Name
                | HostedTreeA11y.Violations vs ->
                    Expect.isNonEmpty vs (sprintf "fixture '%s' names ≥1 violation" fixture.Name)
                    // every violation renders a readable diagnostic
                    for v in vs do
                        Expect.isNotEmpty (HostedTreeA11y.describe v) "violation has a readable diagnostic"

        testCase "an unlabelled control is named specifically"
        <| fun _ ->
            let result = HostedTreeA11y.check "<div><button></button></div>"
            Expect.isTrue (result |> hasViolation _.IsUnlabelledControl) "an empty button is an unlabelled control"

        testCase "a heading skip is named specifically"
        <| fun _ ->
            let result = HostedTreeA11y.check "<h1>Title</h1><h3>Sub</h3>"
            Expect.isTrue (result |> hasViolation _.IsHeadingSkip) "h1 → h3 skips a level"

        testCase "a labelled control (aria-label) passes"
        <| fun _ ->
            let result = HostedTreeA11y.check """<button aria-label="Close"></button>"""
            Expect.equal result HostedTreeA11y.Conformant "an aria-label supplies the accessible name"

        testCase "a well-ordered positive tabindex sequence passes"
        <| fun _ ->
            let result =
                HostedTreeA11y.check """<button tabindex="1">a</button><button tabindex="2">b</button>"""

            Expect.equal result HostedTreeA11y.Conformant "ascending tabindex is not a break"
    ]

// ─── 3. ToyNode witness (Phase 202 — a non-Fuaran stranger tree) ──────

let private witnessTests =
    testList "Phase 277 — ToyNode witness" [
        testCase "a clean toy tree lowers to a conformant fragment"
        <| fun _ ->
            // Headings + text + a labelled semantic button, no event sites.
            let tree =
                Element(
                    "section",
                    [
                        Element("h1", [ Text "Report" ])
                        Element("h2", [ Text "Summary" ])
                        Element("button", [ Text "Export" ])
                    ]
                )

            Expect.equal
                (HostedTreeA11y.check (lowerToHtml tree))
                HostedTreeA11y.Conformant
                "a stranger tree language's clean output passes the harness"

        testCase "the toy's default event lowering (bare span) trips MissingRole"
        <| fun _ ->
            // ToyNode lowers an OnClick to `<span data-toy-event=...>` — an
            // interaction site with no role. The harness catches it, so the
            // toy's own a11y gap is a build signal, not a silent defect.
            let tree = OnClick(NavigateTo "home", Text "Go")

            Expect.isTrue
                (HostedTreeA11y.check (lowerToHtml tree) |> hasViolation _.IsMissingRole)
                "an event-wrapped span with no role is flagged"
    ]

// ─── 4. OSS grep-guard ────────────────────────────────────────────────

let private ossTests =
    testList "Phase 277 — OSS boundary" [
        testCase "the harness source carries no banned OSS vocabulary"
        <| fun _ ->
            let path =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Testing", "Testing", "HostedTreeA11y.fs")

            Expect.isTrue (File.Exists path) (sprintf "expected the harness at %s" path)
            let contents = (File.ReadAllText path).ToLowerInvariant()
            Expect.isFalse (contents.Contains "fuaran") "the harness must name no private layer (GP 1)"
    ]

let tests =
    testList "HostedTreeA11y (Phase 277)" [ fixtureTests; witnessTests; ossTests ]
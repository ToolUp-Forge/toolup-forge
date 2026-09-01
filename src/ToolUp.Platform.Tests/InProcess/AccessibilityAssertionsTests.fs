module ToolUp.Platform.Tests.InProcess.AccessibilityAssertionsTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open Giraffe.ViewEngine
open ToolUp.BrandKit
open ToolUp.Elmish
open ToolUp.Platform.Testing

module A11y = ToolUp.Platform.Testing.Accessibility

// ─── Phase 180 — accessibility assertions in the module harness ────────
//
// The a11y floor runs under `VerifyAll` via this pack, so an SDK-side
// regression fails the build rather than a screen reader. Seven proofs:
//
//   1. Each shipped fixture behaves — the clean tree passes both
//      profiles, each seeded violation names its rule + element path.
//   2. Every rule fires on its own defect and stays silent on the clean
//      tree (no cross-talk between rules).
//   3. `Minimal` vs `Strict` — the Warning-class heuristics (heading
//      order, colour-only state) run and are fatal ONLY under `Strict`.
//   4. `assertAccessible` / the phase-named `assert` entry throw with the
//      consolidated finding list, and the message names both rule and
//      element path.
//   5. `ModuleHarness.AssertAccessible` chains fluently alongside
//      `AssertModel` / `AssertCmd`, and fails a defective view.
//   6. The SDK's OWN stock components — the `ToolUp.BrandKit` SSR
//      primitives, the only forge-shipped components a .NET runner can
//      actually render — pass `Minimal` through the `ofHtml` seam.
//   7. The ARIA allowlist is a superset of the blessed `AriaProp.fs`
//      helper set, so the two cannot drift apart.

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private firedRule (rule: string) (findings: A11y.A11yFinding list) =
    findings |> List.exists (fun f -> f.Rule = rule)

// ─── 1. Shipped fixtures ──────────────────────────────────────────────

let private fixtureTests =
    testList "shipped fixtures" [
        testCase "the clean fixture passes under both profiles"
        <| fun _ ->
            Expect.isEmpty (A11y.check A11y.Minimal A11y.cleanFixture.Node) "clean view has no Minimal findings"
            Expect.isEmpty (A11y.check A11y.Strict A11y.cleanFixture.Node) "clean view has no Strict findings either"

        testCase "each seeded violation fixture produces a finding naming a rule and a path"
        <| fun _ ->
            for fixture in A11y.violationFixtures do
                let findings = A11y.check fixture.Profile fixture.Node

                Expect.isNonEmpty findings (sprintf "fixture '%s' must produce at least one finding" fixture.Name)

                for f in findings do
                    Expect.isNotEmpty f.Rule (sprintf "fixture '%s': every finding names its rule" fixture.Name)
                    Expect.isNotEmpty f.Path (sprintf "fixture '%s': every finding names an element path" fixture.Name)
                    Expect.isNotEmpty f.Message (sprintf "fixture '%s': every finding carries a message" fixture.Name)

        testCase "the clean fixture declares itself clean and the violation fixtures do not"
        <| fun _ ->
            Expect.isTrue A11y.cleanFixture.ExpectClean "cleanFixture.ExpectClean"

            for fixture in A11y.violationFixtures do
                Expect.isFalse fixture.ExpectClean (sprintf "'%s' is a violation fixture" fixture.Name)
    ]

// ─── 2. Per-rule proofs ───────────────────────────────────────────────

let private ruleTests =
    testList "rules" [

        // everyInteractiveHasAccessibleName

        testCase "a button with no text and no aria-label fails everyInteractiveHasAccessibleName"
        <| fun _ ->
            let tree =
                A11y.el "div" [ "id", "toolbar" ] [ A11y.el "button" [ "class", "icon-only" ] [] ]

            let findings = A11y.everyInteractiveHasAccessibleName tree

            Expect.hasLength findings 1 "exactly one unlabelled interactive element"
            Expect.equal findings[0].Rule "everyInteractiveHasAccessibleName" "rule name"
            Expect.equal findings[0].Path "div#toolbar > button" "element path locates the button"
            Expect.equal findings[0].Severity A11y.A11yError "table-stakes failures are Error-class"

        testCase "a button named by text, aria-label, title or a child img alt passes"
        <| fun _ ->
            let named =
                A11y.el "div" [] [
                    A11y.el "button" [] [ A11y.text "Export" ]
                    A11y.el "button" [ "aria-label", "Close" ] []
                    A11y.el "button" [ "title", "Refresh" ] []
                    A11y.el "button" [] [ A11y.elem "img" [ "src", "/svg/x.svg"; "alt", "Dismiss" ] ]
                    A11y.el "a" [ "href", "/home" ] [ A11y.text "Home" ]
                ]

            Expect.isEmpty (A11y.everyInteractiveHasAccessibleName named) "every naming route satisfies the rule"

        testCase "an unlabelled element with an interactive role fails; aria-hidden excuses it"
        <| fun _ ->
            let bad = A11y.el "div" [ "role", "button" ] []
            Expect.hasLength (A11y.everyInteractiveHasAccessibleName bad) 1 "role=button needs a name"

            let hidden = A11y.el "div" [ "role", "button"; "aria-hidden", "true" ] []

            Expect.isEmpty (A11y.everyInteractiveHasAccessibleName hidden) "aria-hidden removes it from the a11y tree"

        // everyImageHasAlt

        testCase "an img with no alt attribute fails everyImageHasAlt"
        <| fun _ ->
            let tree = A11y.el "figure" [] [ A11y.elem "img" [ "src", "/svg/chart.svg" ] ]
            let findings = A11y.everyImageHasAlt tree

            Expect.hasLength findings 1 "one alt-less image"
            Expect.equal findings[0].Rule "everyImageHasAlt" "rule name"
            Expect.equal findings[0].Path "figure > img" "element path"
            Expect.stringContains findings[0].Message "alt" "the message names the missing attribute"

        testCase "alt=\"\" passes — it declares the image decorative"
        <| fun _ ->
            let tree =
                A11y.el "figure" [] [ A11y.elem "img" [ "src", "/svg/rule.svg"; "alt", "" ] ]

            Expect.isEmpty (A11y.everyImageHasAlt tree) "an explicit empty alt is a valid declaration"

        // everyControlHasLabel

        testCase "an input with no label fails everyControlHasLabel"
        <| fun _ ->
            let tree = A11y.el "form" [] [ A11y.elem "input" [ "type", "text"; "name", "q" ] ]
            let findings = A11y.everyControlHasLabel tree

            Expect.hasLength findings 1 "one unlabelled control"
            Expect.equal findings[0].Rule "everyControlHasLabel" "rule name"
            Expect.equal findings[0].Severity A11y.A11yError "unlabelled controls are Error-class"

        testCase "a labelled input passes — via label[for], a wrapping label, or aria-label"
        <| fun _ ->
            let byFor =
                A11y.el "form" [] [
                    A11y.el "label" [ "for", "q" ] [ A11y.text "Query" ]
                    A11y.elem "input" [ "id", "q"; "type", "text" ]
                ]

            Expect.isEmpty (A11y.everyControlHasLabel byFor) "<label for> matching the input id"

            let wrapped =
                A11y.el "form" [] [
                    A11y.el "label" [] [ A11y.text "Query"; A11y.elem "input" [ "type", "text" ] ]
                ]

            Expect.isEmpty (A11y.everyControlHasLabel wrapped) "a wrapping <label>"

            let byAria =
                A11y.el "form" [] [ A11y.elem "input" [ "type", "text"; "aria-label", "Query" ] ]

            Expect.isEmpty (A11y.everyControlHasLabel byAria) "aria-label on the control"

        testCase "hidden and button-like inputs are exempt from everyControlHasLabel"
        <| fun _ ->
            let tree =
                A11y.el "form" [] [
                    A11y.elem "input" [ "type", "hidden"; "name", "csrf" ]
                    A11y.elem "input" [ "type", "submit"; "value", "Save" ]
                ]

            Expect.isEmpty (A11y.everyControlHasLabel tree) "neither carries a user-visible label requirement"

        // ariaRolesAndPropsValid

        testCase "an invalid ARIA role is caught"
        <| fun _ ->
            let tree = A11y.el "div" [ "role", "clickable" ] [ A11y.text "Go" ]
            let findings = A11y.ariaRolesAndPropsValid tree

            Expect.hasLength findings 1 "one invalid role"
            Expect.equal findings[0].Rule "ariaRolesAndPropsValid" "rule name"
            Expect.stringContains findings[0].Message "clickable" "the message quotes the offending role"

        testCase "an invented aria-* property is caught; every blessed one passes"
        <| fun _ ->
            Expect.hasLength
                (A11y.ariaRolesAndPropsValid (A11y.el "div" [ "aria-clicked", "true" ] []))
                1
                "invented property"

            let valid =
                A11y.el "div" [ "role", "tablist" ] [
                    A11y.el "button" [ "role", "tab"; "aria-selected", "true"; "aria-controls", "p1" ] [
                        A11y.text "One"
                    ]
                    A11y.el "div" [ "id", "p1"; "role", "tabpanel"; "aria-labelledby", "t1" ] [ A11y.text "Panel" ]
                ]

            Expect.isEmpty (A11y.ariaRolesAndPropsValid valid) "a well-formed tablist carries only valid ARIA"

        // headingOrderIsMonotonic

        testCase "h1 followed by h3 fails headingOrderIsMonotonic as a Warning"
        <| fun _ ->
            let tree =
                A11y.el "section" [] [ A11y.el "h1" [] [ A11y.text "Title" ]; A11y.el "h3" [] [ A11y.text "Sub" ] ]

            let findings = A11y.headingOrderIsMonotonic tree

            Expect.hasLength findings 1 "one skipped level"
            Expect.equal findings[0].Rule "headingOrderIsMonotonic" "rule name"
            Expect.equal findings[0].Severity A11y.A11yWarning "heading order is heuristic — Warning-class"
            Expect.stringContains findings[0].Message "h1" "the message names the level it skipped from"
            Expect.stringContains findings[0].Message "h3" "and the level it skipped to"

        testCase "a monotonic outline passes, and climbing back out is not a skip"
        <| fun _ ->
            let tree =
                A11y.el "section" [] [
                    A11y.el "h1" [] [ A11y.text "A" ]
                    A11y.el "h2" [] [ A11y.text "B" ]
                    A11y.el "h3" [] [ A11y.text "C" ]
                    A11y.el "h2" [] [ A11y.text "D" ]
                ]

            Expect.isEmpty (A11y.headingOrderIsMonotonic tree) "descending one level at a time, ascending freely"

        // noColourOnlyState

        testCase "a bare state-coloured element with no text or aria equivalent is flagged"
        <| fun _ ->
            let tree = A11y.el "div" [] [ A11y.elem "span" [ "class", "dot dot-error" ] ]
            let findings = A11y.noColourOnlyState tree

            Expect.hasLength findings 1 "one colour-only state signal"
            Expect.equal findings[0].Rule "noColourOnlyState" "rule name"
            Expect.equal findings[0].Severity A11y.A11yWarning "best-effort heuristic — Warning-class"

        testCase "a state-coloured element with text, an aria equivalent or role=status passes"
        <| fun _ ->
            let withText = A11y.el "span" [ "class", "dot dot-error" ] [ A11y.text "Failed" ]
            Expect.isEmpty (A11y.noColourOnlyState withText) "text carries the state"

            let withAria = A11y.elem "span" [ "class", "dot dot-error"; "aria-label", "Failed" ]
            Expect.isEmpty (A11y.noColourOnlyState withAria) "aria-label carries the state"

            let withRole = A11y.elem "span" [ "class", "dot dot-error"; "role", "status" ]
            Expect.isEmpty (A11y.noColourOnlyState withRole) "role=status announces it"

        testCase "no rule fires on the clean fixture"
        <| fun _ ->
            for name, rule in A11y.rulesFor A11y.Strict do
                Expect.isEmpty (rule A11y.cleanFixture.Node) (sprintf "%s must stay silent on a clean tree" name)
    ]

// ─── 3. Profiles ──────────────────────────────────────────────────────

let private profileTests =
    testList "profiles" [
        testCase "Minimal runs the four Error-class rules; Strict adds the two heuristics"
        <| fun _ ->
            let minimal = A11y.rulesFor A11y.Minimal |> List.map fst
            let strict = A11y.rulesFor A11y.Strict |> List.map fst

            Expect.equal
                minimal
                [
                    "everyInteractiveHasAccessibleName"
                    "everyImageHasAlt"
                    "everyControlHasLabel"
                    "ariaRolesAndPropsValid"
                ]
                "the Minimal table-stakes set"

            Expect.equal
                strict
                (minimal @ [ "headingOrderIsMonotonic"; "noColourOnlyState" ])
                "Strict is Minimal plus the Warning-class heuristics"

        testCase "h1 to h3 passes Minimal and fails Strict"
        <| fun _ ->
            let tree =
                A11y.el "section" [] [ A11y.el "h1" [] [ A11y.text "Title" ]; A11y.el "h3" [] [ A11y.text "Sub" ] ]

            Expect.isEmpty (A11y.check A11y.Minimal tree) "heading order is not part of the Minimal bar"

            Expect.isTrue (firedRule "headingOrderIsMonotonic" (A11y.check A11y.Strict tree)) "Strict surfaces it"

            A11y.assertAccessible A11y.Minimal tree |> ignore

            Expect.throws
                (fun () -> A11y.assertAccessible A11y.Strict tree |> ignore)
                "under Strict a Warning-class finding is fatal"

        testCase "Error-class findings are fatal under both profiles"
        <| fun _ ->
            let tree = A11y.el "div" [] [ A11y.el "button" [] [] ]

            Expect.throws
                (fun () -> A11y.assertAccessible A11y.Minimal tree |> ignore)
                "Minimal fails on an unlabelled button"

            Expect.throws (fun () -> A11y.assertAccessible A11y.Strict tree |> ignore) "so does Strict"

        testCase "isFatal: Error always, Warning only under Strict"
        <| fun _ ->
            let warning: A11y.A11yFinding = {
                Rule = "r"
                Path = "p"
                Message = "m"
                Severity = A11y.A11yWarning
            }

            let error = {
                warning with
                    Severity = A11y.A11yError
            }

            Expect.isFalse (A11y.isFatal A11y.Minimal warning) "a warning is tolerated by Minimal"
            Expect.isTrue (A11y.isFatal A11y.Strict warning) "and fatal under Strict"
            Expect.isTrue (A11y.isFatal A11y.Minimal error) "an error is always fatal"
            Expect.isTrue (A11y.isFatal A11y.Strict error) "under either profile"
    ]

// ─── 4. The standalone entry ──────────────────────────────────────────

let private standaloneTests =
    testList "standalone Accessibility.assert entry" [
        testCase "a clean view returns an empty finding list without throwing"
        <| fun _ ->
            let findings = A11y.``assert`` A11y.Minimal A11y.cleanFixture.Node
            Expect.isEmpty findings "clean view passes with an empty finding list"

        testCase "the failure message names every finding's rule and element path"
        <| fun _ ->
            let tree =
                A11y.el "section" [ "id", "panel" ] [
                    A11y.el "button" [] []
                    A11y.elem "img" [ "src", "/svg/chart.svg" ]
                ]

            Expect.throwsC (fun () -> A11y.``assert`` A11y.Minimal tree |> ignore) (fun ex ->
                Expect.stringContains ex.Message "everyInteractiveHasAccessibleName" "names the interactive rule"
                Expect.stringContains ex.Message "everyImageHasAlt" "names the image-alt rule"
                Expect.stringContains ex.Message "section#panel > button" "names the button's path"
                Expect.stringContains ex.Message "section#panel > img" "names the image's path")

        testCase "Minimal does not run the Warning-class rules at all"
        <| fun _ ->
            let tree =
                A11y.el "section" [] [ A11y.el "h1" [] [ A11y.text "A" ]; A11y.el "h3" [] [ A11y.text "B" ] ]

            Expect.isEmpty (A11y.assertAccessible A11y.Minimal tree) "no findings collected under Minimal"
            Expect.hasLength (A11y.check A11y.Strict tree) 1 "Strict collects it"

        testCase "ofHtml parses a rendered fragment and the rules run over it"
        <| fun _ ->
            let clean =
                """<section id="r"><h1>Report</h1><img src="/c.svg" alt="Chart"><button>Export</button></section>"""

            Expect.isEmpty (A11y.checkHtml A11y.Strict clean) "a clean fragment passes"

            let dirty =
                """<section id="r"><button class="icon"></button><img src="/c.svg"></section>"""

            let findings = A11y.checkHtml A11y.Minimal dirty

            Expect.isTrue (firedRule "everyInteractiveHasAccessibleName" findings) "unlabelled button caught"
            Expect.isTrue (firedRule "everyImageHasAlt" findings) "alt-less void img caught"

            Expect.throws
                (fun () -> A11y.assertHtml A11y.Minimal dirty |> ignore)
                "assertHtml throws on the same fragment"

        testCase "ofHtml tolerates unclosed and stray tags without throwing"
        <| fun _ ->
            let ragged = "<div><p>one<p>two</span></div><br>"
            Expect.isEmpty (A11y.checkHtml A11y.Minimal ragged) "a ragged fragment parses and passes"

        testCase "the Phase 277 hosted-tree conformant fixture also passes this floor"
        <| fun _ ->
            // Cross-harness coherence: two a11y harnesses that disagreed
            // about the same fragment would be worse than one.
            Expect.isEmpty
                (A11y.checkHtml A11y.Strict HostedTreeA11y.conformantFixture.Html)
                "HostedTreeA11y's clean fixture is clean here too"
    ]

// ─── 5. ModuleHarness integration ─────────────────────────────────────

type private PanelModel = { Label: string; ShowIcon: bool }

type private PanelMsg =
    | Rename of string
    | HideIcon

let private panelInit () : PanelModel * Cmd<PanelMsg> =
    { Label = "Export"; ShowIcon = false }, Cmd.none

let private panelUpdate (msg: PanelMsg) (model: PanelModel) : PanelModel * Cmd<PanelMsg> =
    match msg with
    | Rename label -> { model with Label = label }, Cmd.none
    | HideIcon -> { model with ShowIcon = false }, Cmd.none

/// A well-behaved render: the button is named by its label text.
let private panelRender (model: PanelModel) (_: PanelMsg -> unit) : A11y.A11yNode =
    A11y.el "section" [ "id", "panel" ] [
        A11y.el "h1" [] [ A11y.text "Panel" ]
        A11y.el "button" [ "type", "button" ] [ A11y.text model.Label ]
    ]

/// A defective render: an icon-only button with no accessible name.
let private defectiveRender (_: PanelModel) (_: PanelMsg -> unit) : A11y.A11yNode =
    A11y.el "section" [ "id", "panel" ] [ A11y.el "button" [ "class", "icon-only" ] [] ]

/// The same well-behaved view lowered to an HTML fragment — the SSR /
/// `outerHTML` seam.
let private panelRenderHtml (model: PanelModel) (_: PanelMsg -> unit) : string =
    sprintf """<section id="panel"><h1>Panel</h1><button type="button">%s</button></section>""" model.Label

let private harnessTests =
    testList "ModuleHarness.AssertAccessible" [
        testCase "chains fluently alongside AssertModel / AssertCmd"
        <| fun _ ->
            let final =
                (ModuleHarness.fromUnitInit panelInit panelUpdate)
                    .AssertModel(fun m -> m.Label = "Export")
                    .AssertAccessible(panelRender)
                    .Dispatch(Rename "Download")
                    .AssertAccessible(panelRender)
                    .AssertModel(fun m -> m.Label = "Download")
                    .AssertNoCmd()

            Expect.equal final.Model.Label "Download" "the chain returns a harness carrying the updated model"

        testCase "the default profile is Minimal; Strict is opt-in"
        <| fun _ ->
            let h = ModuleHarness.fromUnitInit panelInit panelUpdate

            h.AssertAccessible(panelRender) |> ignore
            h.AssertAccessible(panelRender, A11y.Strict) |> ignore

        testCase "a defective view fails the assertion, naming the rule and path"
        <| fun _ ->
            let h = ModuleHarness.fromUnitInit panelInit panelUpdate

            Expect.throwsC (fun () -> h.AssertAccessible(defectiveRender) |> ignore) (fun ex ->
                Expect.stringContains ex.Message "everyInteractiveHasAccessibleName" "names the rule"
                Expect.stringContains ex.Message "section#panel > button" "names the element path")

        testCase "AssertAccessibleHtml runs the same floor over a rendered fragment"
        <| fun _ ->
            let final =
                (ModuleHarness.fromUnitInit panelInit panelUpdate)
                    .AssertAccessibleHtml(panelRenderHtml, A11y.Strict)
                    .Dispatch(Rename "Download")
                    .AssertAccessibleHtml(panelRenderHtml)

            Expect.equal final.Model.Label "Download" "the HTML-shaped assertion chains identically"

        testCase "the existing harness API is unchanged"
        <| fun _ ->
            // Phase 11a's surface must still behave exactly as before —
            // `AssertAccessible` is additive, not a replacement.
            let final =
                (ModuleHarness.fromUnitInit panelInit panelUpdate)
                    .Dispatch(Rename "A")
                    .AssertModelWith("renamed", (fun m -> m.Label = "A"))
                    .AssertCmd(List.isEmpty)
                    .DispatchAll([ HideIcon ])
                    .AssertNoCmd()

            Expect.isFalse final.Model.ShowIcon "the pre-existing fluent chain is untouched"
    ]

// ─── 6. The SDK's own stock components ────────────────────────────────

let private renderNode (node: XmlNode) : string = RenderView.AsString.htmlNode node

/// The forge-shipped components a .NET runner can actually render: the
/// `ToolUp.BrandKit` SSR primitives. The Fable-tier Feliz shell cannot be
/// evaluated here (see the `Accessibility` module header), so this is the
/// honest extent of the SDK's own in-process regression guard.
let private stockComponentTests =
    testList "SDK stock components pass Minimal" [
        testCase "BrandKit page header (monogram + nav + right slot)"
        <| fun _ ->
            let header: PageChrome.HeaderSpec = {
                Brand = PageChrome.Monogram("/svg/mark.svg", "Acme home")
                Nav = [ { Label = "Docs"; Href = "/docs" }; { Label = "Pricing"; Href = "/pricing" } ]
                Right = [ Card.card [ Text.eyebrow "BETA" ] ]
            }

            let findings =
                PageChrome.pageHeader header |> renderNode |> A11y.checkHtml A11y.Minimal

            Expect.isEmpty findings (sprintf "pageHeader findings: %A" findings)

        testCase "BrandKit page header (wordmark lockup)"
        <| fun _ ->
            let header: PageChrome.HeaderSpec = {
                Brand =
                    PageChrome.Wordmark {
                        Stem = "Acme-"
                        Emphasis = "CO"
                        EmphasisColour = "#6B5FBF"
                        Tail = Some "rp"
                    }
                Nav = [ { Label = "Home"; Href = "/" } ]
                Right = []
            }

            let findings =
                PageChrome.pageHeader header |> renderNode |> A11y.checkHtml A11y.Minimal

            Expect.isEmpty findings (sprintf "pageHeader (wordmark) findings: %A" findings)

        testCase "BrandKit page footer"
        <| fun _ ->
            let footer: PageChrome.FooterSpec = {
                Copyright = "(c) 2026 Acme"
                Links = [
                    { Label = "Terms"; Href = "/terms" }
                    { Label = "Privacy"; Href = "/privacy" }
                ]
            }

            let findings =
                PageChrome.pageFooter footer |> renderNode |> A11y.checkHtml A11y.Minimal

            Expect.isEmpty findings (sprintf "pageFooter findings: %A" findings)

        testCase "BrandKit surface + text + pill primitives"
        <| fun _ ->
            let surface =
                Card.card [
                    Text.displayLarge "Report"
                    Text.hRule
                    Card.cardTight [ Pill.pill "NEW"; Pill.pillOn "ACTIVE"; Pill.pillWithDot "PRIORITY" ]
                ]

            let findings = surface |> renderNode |> A11y.checkHtml A11y.Minimal

            Expect.isEmpty findings (sprintf "card / text / pill findings: %A" findings)

        testCase "BrandKit icon shell"
        <| fun _ ->
            let icon: Icon.IconSpec = {
                Paths = [ "M4 4 L20 20" ]
                Dots = Some [ (12.0, 12.0, 2.0) ]
            }

            let findings = Icon.iconSvg icon |> renderNode |> A11y.checkHtml A11y.Minimal

            Expect.isEmpty findings (sprintf "iconSvg findings: %A" findings)
    ]

// ─── 7. ARIA allowlist ⇄ AriaProp.fs coherence ────────────────────────

let private allowlistTests =
    testList "ARIA allowlist" [
        testCase "every attribute AriaProp.fs blesses is in the allowlist"
        <| fun _ ->
            let ariaPropPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.UI", "AriaProp.fs")

            Expect.isTrue (File.Exists ariaPropPath) (sprintf "expected AriaProp.fs at %s" ariaPropPath)

            // Strip line comments before scanning — the module header
            // cites `prop.custom ("aria-…", …)` for documentation, and
            // that ellipsis is not an attribute name. Same guard
            // `AriaPropTests.onlyAriaOrRoleTest` applies.
            let source =
                File.ReadAllText ariaPropPath
                |> _.Split('\n')
                |> Array.map (fun line ->
                    let trimmed = line.TrimStart()
                    if trimmed.StartsWith "//" then "" else line)
                |> String.concat "\n"

            let declared = [
                for m in Regex.Matches(source, "prop\\.custom\\s*\\(\\s*\"(aria-[^\"]+)\"") do
                    yield m.Groups[1].Value
            ]

            Expect.isNonEmpty declared "AriaProp.fs declares at least one aria-* helper"

            let missing =
                declared
                |> List.filter (A11y.ariaAttributeAllowlist.Contains >> not)
                |> List.distinct

            Expect.isEmpty
                missing
                "every aria-* attribute the SDK's own blessed helpers emit must validate against the allowlist"

        testCase "the WAI-ARIA vocabularies are non-trivial and case-folded"
        <| fun _ ->
            Expect.isGreaterThan (Set.count A11y.ariaAttributeAllowlist) 40 "the ARIA 1.2 state/property set"
            Expect.isGreaterThan (Set.count A11y.roleAllowlist) 60 "the ARIA 1.2 role set"

            for name in A11y.ariaAttributeAllowlist do
                Expect.equal name (name.ToLowerInvariant()) "attribute names are matched case-folded"

            for role in A11y.roleAllowlist do
                Expect.equal role (role.ToLowerInvariant()) "role names are matched case-folded"
    ]

let tests =
    testList "Phase 180 — accessibility assertions (ToolUp.Platform.Testing)" [
        fixtureTests
        ruleTests
        profileTests
        standaloneTests
        harnessTests
        stockComponentTests
        allowlistTests
    ]
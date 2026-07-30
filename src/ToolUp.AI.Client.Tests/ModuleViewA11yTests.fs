// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.ModuleViewA11yTests

// ─── The Phase 180 a11y floor over an ordinary MODULE view ────────────
//
// Phase 180 shipped `ModuleHarness.AssertAccessible`, and Phase 610 built
// a DOM harness that mounts a real Feliz view and runs those rules over
// the markup — but only the SHELL's sidebar rail was ever put through it.
// No ordinary module view was, and `AssertAccessible` still asked the
// caller to hand-supply a render function: for a Fable module the only
// tree it HAS is a `ReactElement`, so "supply a render" meant hand-writing
// a second description of the view, which then drifts from the view
// silently and in the direction of passing.
//
// This pack closes that. It drives a real SDK module —
// `UsageDashboard`, a `ClientModule` the shell injects into every
// non-Anonymous deployment — through its real `init` / `update`, and at
// each model state runs its real `view` through the Phase 180 rules with
// ONE line and no hand-written tree:
//
//     fromUnitInit UsageDashboard.init UsageDashboard.update
//         |> _.Dispatch(UsageDashboard.AggregateLoaded(Ok rows))
//         |> _.AssertAccessibleView(ViewMount.mount, UsageDashboard.view)
//
// ── Where the seam is, and why jsdom is not in the shipped package ──
// `ModuleHarness.AssertAccessibleView` takes a `mount: 'View -> string`.
// It names no renderer, no DOM and no npm package — `ToolUp.Platform.
// Testing` stays BCL-only. The jsdom implementation ships beside it as
// `Testing/ViewMount.fs`: `Content`-packed under `fable/` like every
// other file in that project, but deliberately NOT in its `<Compile>`
// list, so it is absent from the compiled assembly. A consumer opts in
// with one `<Compile Include>` (exactly as this project already does for
// `AccessibilityAssertions.fs`) plus a `jsdom` devDependency; a consumer
// that does not emits nothing from it and needs neither (GP 13). An SSR
// consumer passes its own string renderer and never touches a DOM.
//
// ── Why here and not in the .NET pack ──
// The same constraint every Fable-tier pack in this project records, and
// one more on top: `UsageDashboard` holds a module-level `Api.makeProxy`
// whose reflection-shaped builder raises under .NET reflection at
// static-init time, so `update` cannot even be CALLED there — and
// rendering React needs a JS runtime regardless.
//
// ── What it does NOT prove ──
// CSS, focus rings, contrast, the browser's real accessibility tree. And
// a state is covered only if it is in `viewStates` below.

open Feliz
open ToolUp.Elmish
open ToolUp.Platform.Testing
open ToolUp.Platform.Testing.ModuleHarness
open ToolUp.Platform.Usage
open ToolUp.AI.Client.Tests.NodeTest

// ─── The module's states ─────────────────────────────────────────────

let private sampleRows: UsageAggregateRow list = [
    {
        Bucket = "ai.tokens"
        Quantity = 128500M
    }
    {
        Bucket = "storage.bytes"
        Quantity = 4294967296M
    }
    {
        Bucket = "ingestion.rows"
        Quantity = 0M
    }
]

/// One state of the dashboard: how the harness got there, and the
/// accessible names that must be reachable once it has.
///
/// `MustName` is per-state rather than "every control the view can ever
/// draw" for the same reason Phase 610's is: which controls exist is a
/// property of the STATE (the export button renames itself mid-flight,
/// the table is absent until rows arrive), and writing it out is what
/// makes a silently-vanished control a failure rather than a pass. A rule
/// reports what is present and unnamed; it never reports what is absent.
type private ViewState = {
    Name: string
    Harness: ModuleHarness<UsageDashboard.Model, UsageDashboard.Msg>
    MustName: string list
}

/// The module's own `init` — including the `Cmd` it emits, which the
/// harness captures and does not run. That is what makes the initial
/// state honest: the real dashboard IS in `Loading` while its first
/// `Aggregate` call is in flight.
let private initial () =
    fromUnitInit UsageDashboard.init UsageDashboard.update

/// Every state driven through the module's REAL `update`. Nothing here
/// constructs a `Model` literally — a hand-built model is a state the
/// module might never reach, and the point of running the floor through
/// the harness is that every state it checks is one a user can get to.
let private viewStates: ViewState list = [
    {
        Name = "initial aggregate load in flight"
        Harness = initial ()
        MustName = [ "Refresh"; "Export CSV" ]
    }

    {
        Name = "aggregate rows loaded"
        Harness = (initial ()).Dispatch(UsageDashboard.AggregateLoaded(Ok sampleRows))
        MustName = [ "Refresh"; "Export CSV" ]
    }

    {
        // The empty-state branch — a different render arm, and the one a
        // brand-new team sees first.
        Name = "no usage records for this scope"
        Harness = (initial ()).Dispatch(UsageDashboard.AggregateLoaded(Ok []))
        MustName = [ "Refresh"; "Export CSV" ]
    }

    {
        Name = "the aggregate query failed"
        Harness = (initial ()).Dispatch(UsageDashboard.AggregateLoaded(Error "the usage service is unreachable"))
        MustName = [ "Refresh"; "Export CSV" ]
    }

    {
        // The export button renames itself and goes `disabled` while the
        // CSV is being built. A disabled control is still announced, so it
        // still has to be named — and its name CHANGED, which is exactly
        // the kind of transient state a single-tree fixture never sees.
        Name = "a CSV export in flight"
        Harness =
            (initial ()).Dispatch(UsageDashboard.AggregateLoaded(Ok sampleRows)).Dispatch(UsageDashboard.ExportCsv)
        MustName = [ "Refresh"; "Exporting…" ]
    }

    {
        // Re-grouping drops back to `Loading` and re-issues the query;
        // asserted because it is the one transition a user drives from
        // inside the view rather than from the shell.
        Name = "regrouped by user, reload in flight"
        Harness = (initial ()).Dispatch(UsageDashboard.SetGrouping ByUser)
        MustName = [ "Refresh"; "Export CSV" ]
    }
]

// ─── Known, tracked gaps ─────────────────────────────────────────────

/// A finding that is REAL, is not this pack's to fix, and is pinned so it
/// cannot be mistaken for coverage — while a NEW finding still fails.
/// Same shape as Phase 610's, keyed off the offending element rather than
/// its document path so it survives unrelated markup moving around it.
type private KnownGap = {
    Rule: string
    /// Why the finding stands, and where the fix belongs.
    Why: string
    Matches: Accessibility.ElementRef -> bool
}

/// The floor found a real defect on its first ordinary module view, which
/// is the outcome that justifies wiring it: `UsageDashboard.renderControls`
/// draws its grouping `<select>` beside a `<label>` that neither wraps it
/// nor carries a `for`, so the control has no programmatic label at all —
/// a screen reader announces an unnamed combo box, and voice control has
/// nothing to say. The visible "Group by" text makes it look labelled to
/// everyone who can see it, which is why it survived to here.
///
/// Pinned rather than fixed because the fix is a one-line edit in
/// `ToolUp.Platform.Client/Client/UsageDashboard.fs` (give the select an
/// `id` and the label a `for`, or an `aria-label`), which is a different
/// lease from this one. Filed as a Tidy-Up item in the same pass.
///
/// The guard below refuses to carry a pin that has stopped firing, so
/// when that edit lands this entry FAILS the pack until it is deleted.
/// Empty, and that is the point. This pack shipped with one pin —
/// `UsageDashboard`'s grouping `<select>`, labelled only by a sibling
/// `<label>` with no `for`, so an unnamed combo box in all six of the
/// module's states. It was the FIRST defect the module-view floor found on
/// the first ordinary module view it was pointed at, which is the argument
/// for the floor existing.
///
/// Fixed in `UsageDashboard.renderControls` (the select now carries its own
/// `aria-label`, bound to the same string as the visible label so the two
/// cannot drift). The pin was deleted in that same commit — required, not
/// tidy-up: the `every pinned known gap still fires somewhere` case below
/// goes RED the moment a pinned finding stops firing, because an exemption
/// that no longer fires is one that has silently stopped checking a class
/// that is now clean.
///
/// The machinery stays for the next genuine not-mine-to-fix finding.
let private knownGaps: KnownGap list = []

let private classify (node: Accessibility.A11yNode) (findings: Accessibility.A11yFinding list) =
    findings
    |> List.partition (fun f ->
        match Accessibility.locate node f with
        | None -> false
        | Some e -> knownGaps |> List.exists (fun g -> g.Rule = f.Rule && g.Matches e))

let private inState (state: string) (finding: Accessibility.A11yFinding) : Accessibility.StateFinding = {
    State = state
    Finding = finding
}

// ─── A falsifier module — a view that can lose its name on demand ────

type private ToyModel = { Named: bool }

type private ToyMsg = StripTheName

let private toyInit () : ToyModel * Cmd<ToyMsg> = { Named = true }, Cmd.none

let private toyUpdate (_: ToyMsg) (model: ToyModel) : ToyModel * Cmd<ToyMsg> = { model with Named = false }, Cmd.none

/// An icon-only button — the exact shape Phase 609 found unnamed in the
/// shipped rail. With `Named = false` its `aria-label` is gone and there
/// is no text to fall back on, so its accessible name is nothing at all.
let private toyView (model: ToyModel) (_: ToyMsg -> unit) : ReactElement =
    let naming = if model.Named then [ prop.ariaLabel "Delete row" ] else []

    Html.div [
        prop.children [
            Html.button (naming @ [ prop.children [ Svg.svg [ svg.viewBox (0, 0, 24, 24) ] ] ])
        ]
    ]

let private expectThrows (body: unit -> unit) (message: string) : string =
    let caught =
        try
            body ()
            None
        with e ->
            Some e.Message

    match caught with
    | Some m -> m
    | None ->
        failwith (
            message
            + " — but it returned normally. A floor that cannot fail is the failure this whole line \
               of work exists to prevent."
        )

// ─── Cases ───────────────────────────────────────────────────────────

let tests =
    testList "the Phase 180 a11y floor over a consumer module view" [

        // The floor itself, over every reachable state of a real module.
        testCase "every UsageDashboard state passes the Phase 180 Strict rule set" (fun () ->
            let failures =
                viewStates
                |> List.collect (fun st ->
                    let node =
                        st.Harness.RenderViewHtml(ViewMount.mount, UsageDashboard.view)
                        |> Accessibility.ofHtml

                    let _, real = classify node (Accessibility.check Accessibility.Strict node)
                    real |> List.map (inState st.Name))

            if not (List.isEmpty failures) then
                failwith (
                    Accessibility.reportStates Accessibility.Strict failures
                    + "\n\nEach finding is a control `UsageDashboard.view` renders in that state with \
                       no accessible name, no alt text, no label, or invalid ARIA. The rules are \
                       Phase 180's, unchanged; what this pack adds is running them over a MODULE's \
                       own view instead of a hand-built tree. Fix the control in \
                       `ToolUp.Platform.Client/Client/UsageDashboard.fs`."
                ))

        // The half a rule cannot do: a rule reports what is present and
        // unnamed, never what is absent, so a control that vanished from
        // the view is indistinguishable from a view with nothing wrong.
        testCase "every state's expected controls are reachable BY NAME" (fun () ->
            let missing = [
                for st in viewStates do
                    let node =
                        st.Harness.RenderViewHtml(ViewMount.mount, UsageDashboard.view)
                        |> Accessibility.ofHtml

                    let names = Accessibility.interactiveNames node |> List.choose snd

                    for expected in st.MustName do
                        if not (List.contains expected names) then
                            yield st.Name, expected, names
            ]

            if not (List.isEmpty missing) then
                let detail = [
                    for stateName, expected, found in missing do
                        yield
                            sprintf
                                "  state \"%s\": no control is reachable as \"%s\" — the names actually exposed were [%s]"
                                stateName
                                expected
                                (found |> List.map (sprintf "\"%s\"") |> String.concat "; ")
                ]

                failwith (
                    sprintf "%d expected control(s) are not reachable by name:\n" missing.Length
                    + (detail |> String.concat "\n")
                    + "\n\nEither the control lost its accessible name, or it is no longer rendered \
                       in that state at all, or the expectation in `viewStates` is stale (fix it \
                       HERE, and say why in the same commit — this list is the contract)."
                ))

        // Non-vacuity. A mount that silently produced "" would leave every
        // case above passing, which is indistinguishable from a clean
        // module — and is the precise failure mode this line of work
        // exists to close.
        testCase "the mount really renders the module's markup" (fun () ->
            for st in viewStates do
                let html = st.Harness.RenderViewHtml(ViewMount.mount, UsageDashboard.view)

                Expect.isTrue
                    (html.Contains "<button")
                    ("state \""
                     + st.Name
                     + "\" rendered no <button> at all — the mount is not mounting")

                Expect.isTrue
                    (html.Contains "Per-team consumption")
                    ("state \""
                     + st.Name
                     + "\" is missing the view's own copy — the markup captured is \
                      not `UsageDashboard.view`'s")

            // And the state-dependent arms really differ, so the states
            // are not six captures of one tree.
            let loaded =
                viewStates
                |> List.find (fun s -> s.Name = "aggregate rows loaded")
                |> fun s -> s.Harness.RenderViewHtml(ViewMount.mount, UsageDashboard.view)

            let empty =
                viewStates
                |> List.find (fun s -> s.Name = "no usage records for this scope")
                |> fun s -> s.Harness.RenderViewHtml(ViewMount.mount, UsageDashboard.view)

            Expect.isTrue (loaded.Contains "<table") "the loaded state renders the aggregate table"
            Expect.isFalse (empty.Contains "<table") "the empty state renders the empty copy, not a table")

        // The pins have to be load-bearing in both directions: a pinned
        // gap that has been FIXED is a stale pin, and a stale pin is how a
        // whole class quietly stops being checked.
        testCase "every pinned known gap still fires somewhere" (fun () ->
            for gap in knownGaps do
                let fires =
                    viewStates
                    |> List.exists (fun st ->
                        let node =
                            st.Harness.RenderViewHtml(ViewMount.mount, UsageDashboard.view)
                            |> Accessibility.ofHtml

                        let pinned, _ = classify node (Accessibility.check Accessibility.Strict node)
                        not (List.isEmpty pinned))

                Expect.isTrue
                    fires
                    ("the known gap pinned for rule `"
                     + gap.Rule
                     + "` no longer fires in any UsageDashboard state. If it was fixed, DELETE the "
                     + "pin — leaving it in place exempts an element class that is now clean, and "
                     + "the next real instance of it would pass. The pin reads: "
                     + gap.Why))

        // The floor has to be able to FAIL, and through the harness path
        // rather than by calling the rules directly — what is under test
        // here is the WIRING (view → mount → parse → rules → throw), not
        // the Phase 180 rules, which have their own pack.
        testCase "AssertAccessibleView rejects a module view that lost a control's name" (fun () ->
            let named = fromUnitInit toyInit toyUpdate

            named.AssertAccessibleView(ViewMount.mount, toyView, Accessibility.Strict)
            |> ignore

            let stripped = named.Dispatch StripTheName

            let message =
                expectThrows
                    (fun () ->
                        stripped.AssertAccessibleView(ViewMount.mount, toyView, Accessibility.Strict)
                        |> ignore)
                    "removing the only accessible name from a module view's button must fail \
                     AssertAccessibleView"

            Expect.isTrue
                (message.Contains "everyInteractiveHasAccessibleName")
                ("the failure must NAME the rule that fired, so the reader knows what broke. Got: "
                 + message)

            Expect.isTrue
                (message.Contains "button")
                ("the failure must NAME the offending element, so the fix has a target. Got: "
                 + message))

        // `CheckAccessibleView` is the non-throwing half the pinning above
        // runs on; if it ever stopped agreeing with the throwing one, the
        // pins would be classifying a different finding set than the gate
        // fails on.
        testCase "CheckAccessibleView reports exactly what AssertAccessibleView fails on" (fun () ->
            let stripped = (fromUnitInit toyInit toyUpdate).Dispatch StripTheName

            let findings =
                stripped.CheckAccessibleView(ViewMount.mount, toyView, Accessibility.Strict)

            Expect.equal
                (findings |> List.map _.Rule)
                [ "everyInteractiveHasAccessibleName" ]
                "the unnamed icon-only button produces exactly the accessible-name finding — if this \
                 is empty the rules are inspecting nothing and every other case here is vacuous"

            Expect.isEmpty
                ((fromUnitInit toyInit toyUpdate).CheckAccessibleView(ViewMount.mount, toyView, Accessibility.Strict))
                "and the same view WITH its aria-label is clean, so the finding tracks the name and \
                 not the shape")
    ]
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.SidebarRailA11yTests

// ─── Phase 610 — the Phase 180 a11y floor over the shell rail's STATES ──
//
// [Phase 180](180-a11y-assertions-module-harness.md) already shipped the
// rule that would have caught the [Phase
// 609](609-accessible-names-for-reserved-rail-rows-in-the-narrow-rail.md)
// defect in one line — `everyInteractiveHasAccessibleName` — and the shell
// was already run through the `Minimal` profile. The rule never fired
// because it never saw the STATE where the names went missing: in the
// narrow (w-20) icon-only rail no row renders text, so every row was an
// unnamed `<button>`, while the SAME markup in the hover-expanded rail is
// named by each row's visible `<span>`. The floor existed; its fixture set
// was one tree where the surface is a family of them.
//
// This pack widens the fixtures. It introduces NO new rule vocabulary: it
// renders the real component in each state and runs the Phase 180 rules
// over the result.
//
// ── What this pack proves, and what it does not ──
// It renders `Toolup.Sidebar.Sidebar` — the actual shipped component, not
// a model of it — into a real DOM with `react-dom/client`, drives the
// hover that expands the rail with a real `mouseover`, captures the mounted
// node's `outerHTML`, and hands that to `Accessibility.ofHtml`. So the tree
// the rules judge IS the markup the browser would get, attribute for
// attribute; there is no hand-maintained projection of `renderRow` that
// could drift away from `renderRow`. Deleting `prop.ariaLabel` from a rail
// row fails this pack, which is the property the phase is for.
//
// It does NOT prove anything about CSS, focus rings, contrast, or the
// browser's real accessibility tree — `ofHtml` reads DOM/ARIA shape, and
// computed style is invisible to it. And a state is covered only if it is
// in `railStates` below.
//
// ── Why a DOM, and why that is not new test infrastructure ──
// The rail's narrow-vs-expanded axis is component-local `React.useState`
// flipped by `onMouseEnter`. A string renderer
// (`react-dom/server.renderToStaticMarkup`, which needs no DOM and was
// tried first) therefore only ever produces the NARROW rail — it cannot
// reach the expanded one, and with it the section headers, the row name
// spans, the pin / hide affordances and the hidden-items reveal list. So
// the pack mounts instead. `react-dom` was already installed here; `jsdom`
// is the one added devDependency, and it is the one this project's own
// `.fsproj` header reserved for "future view-level tests (Feliz
// components, useState interaction) … adding JSDOM as a devDep when such a
// test concretely needs it". The RUNNER is still `node:test` with no
// transitive deps. Nothing ships: `IsPackable=false`, zero runtime cost
// (GP 13).
//
// ── Why here and not in the .NET pack ──
// `Toolup.Sidebar`'s module initialiser reaches
// `importDefault "../icons/toolup-forge-dark.png"`, and F# emits a
// static-init check on module function entry, so touching ANY member of
// that module from the .NET Expecto runner throws "You've hit dummy code
// used for Fable bindings". Phase 611 measured that rather than assuming
// it: its pack was written .NET-side first and all ten cases errored on
// exactly this. Rendering React needs a JS runtime regardless.

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open Toolup.Sidebar
open SidebarPreferences
open ToolUp.Platform.Testing
open ToolUp.AI.Client.Tests.NodeTest

// ─── DOM harness ─────────────────────────────────────────────────────

[<Import("JSDOM", from = "jsdom")>]
[<AllowNullLiteral>]
type private JSDOM(html: string, options: obj) =
    member _.window: obj = jsNative

[<Import("createRoot", from = "react-dom/client")>]
let private createRoot (container: obj) : obj = jsNative

/// React's own render-flush wrapper. Every mutation of a mounted tree —
/// the initial render and the hover — runs inside it, so React has
/// committed before the markup is read; without it the capture races the
/// commit and intermittently reads the pre-render DOM.
[<Import("act", from = "react")>]
let private act (body: unit -> unit) : unit = jsNative

/// `globalThis.<name> = value`, via `defineProperty` because several of
/// the names below (`navigator` above all) are getter-only on the Node
/// global object and a plain assignment throws.
[<Emit("Object.defineProperty(globalThis, $0, { value: $1, configurable: true, writable: true })")>]
let private defineGlobal (name: string) (value: obj) : unit = jsNative

[<Emit("new $0($1, $2)")>]
let private newMouseEvent (ctor: obj) (kind: string) (init: obj) : obj = jsNative

/// The globals React 19 and dnd-kit read off the ambient scope. Installed
/// from the jsdom window per render — `react-dom/client` reads them when
/// `createRoot` runs, not at import time, so ordinary static imports above
/// are fine.
let private installGlobals (window: obj) =
    for name in
        [
            "window"
            "document"
            "HTMLElement"
            "Element"
            "Node"
            "Event"
            "MouseEvent"
            "SVGElement"
            "getComputedStyle"
            "requestAnimationFrame"
            "cancelAnimationFrame"
            "navigator"
        ] do
        defineGlobal name (window?(name))

    // React refuses `act` outside an act-environment; this is the flag it
    // reads to know it is in a test.
    defineGlobal "IS_REACT_ACT_ENVIRONMENT" true

// ─── The rail states — 610.A, and THE LIST IS THE CONTRACT ───────────

/// One state of the shell rail: the module set the shell would hand
/// `buildSections`, the persisted preferences, and whether the pointer is
/// over the rail (which is the whole of the narrow-vs-expanded axis).
///
/// `MustName` is the accessible names that MUST be reachable in this
/// state, keyed by the row id they belong to. It is deliberately a
/// per-state list rather than "every row in `Modules`": in the narrow rail
/// a collapsed group renders ONE icon and none of its members, and the
/// hidden-items section is omitted entirely — so "which rows are nameable
/// here" is a property of the state, not of the module set, and writing it
/// out is what makes a silently-vanished row a failure rather than a pass.
type private RailState = {
    Name: string
    Modules: SidebarModuleView list
    Prefs: UserSidebarPreferences
    Selected: string
    /// `true` = the hover-expanded (w-64) rail; `false` = the narrow
    /// icon-only (w-20) rail.
    Hovered: bool
    MustName: (string * string) list
}

// ─── Row fixtures ────────────────────────────────────────────────────

/// A stand-in for a module's icon. The shipped icons are svgr-imported
/// React components that inline an `<svg>`; an `<svg>` contributes no text
/// to an accessible name, so an empty one is faithful for this purpose and
/// keeps the fixture free of the asset pipeline.
let private stubIcon = Svg.svg [ svg.viewBox (0, 0, 24, 24) ]

let private row (id: string) (name: string) (group: string option) (placement: SidebarPlacement option) = {
    Id = id
    Name = name
    Icon = stubIcon
    HasData = false
    Group = group
    Pages = []
    Placement = placement
}

let private pagedRow (id: string) (name: string) (group: string option) (pages: (string * string) list) = {
    row id name group None with
        Pages =
            pages
            |> List.map (fun (pid, pname) -> {
                Id = pid
                Name = pname
                Icon = stubIcon
            })
}

/// The four SDK reserved rows, with the ids, names and declared slots the
/// shell really builds them with — the two landings via
/// `ClientModule.withPlacement` (`Home.create` names it "Home",
/// `AdminHome.create` "Administration"), the two Phase 567 area switchers
/// at their literal construction site in `sidebarSections`. Restated here
/// rather than driven through those factories, which reach `Icons` and the
/// shell's `ClientConfig`; the cost is the same one `SidebarPlacementTests`
/// records — a NEW reserved row is a deliberate two-file edit, and the
/// .NET-side Phase 609 pins hold the naming rule itself.
let private productLanding = row HomeId "Home" None (Some LeadingSlot)
let private adminLanding = row AdminHomeId "Administration" None (Some LeadingSlot)

let private adminSwitcher =
    row AdminAreaId "Administration" None (Some TrailingSlot)

let private productSwitcher =
    row ProductAreaId "Back to app" None (Some TrailingSlot)

/// The no-active-team landing (`NoActiveTeamLanding.moduleId`), as the
/// visibility fold leaves it: ungrouped, undeclared, and — for a caller
/// who is not a platform admin — the ONLY row besides the landing.
let private awaitingTeam = row "AwaitingTeam" "No team yet" None None

let private fresh = UserSidebarPreferences.empty

let private expanding (keys: string list) = {
    fresh with
        ExpandedGroups = Set.ofList keys
}

// ─── The state list ──────────────────────────────────────────────────

/// Every shell rail state the floor covers. Adding a rail state means
/// adding a row HERE — nothing else in this pack enumerates states, and a
/// state absent from this list is a state the floor does not hold for.
///
/// The seven axes Phase 610.A names, and where each is covered:
///
/// | axis | states |
/// |---|---|
/// | expanded rail | 1, 3, 5, 7, 9 |
/// | narrow icon-only rail | 2, 4, 6, 8 |
/// | Product area | 1, 2 |
/// | Administration area | 3, 4 |
/// | a collapsed group | 5, 6 (and 8, where `_other` is the collapsed one) |
/// | the hidden-items section | 7 |
/// | the no-active-team collapse | 8, 9 |
let private railStates: RailState list = [
    // 1 + 2 — the Product area, both rail widths. `SeparateArea` with a
    // platform admin in the product area: the landing, two grouped
    // modules, an ungrouped one, and the role-gated switcher into
    // administration. Groups expanded, so every row is on screen and every
    // row has to be named.
    let productModules = [
        productLanding
        row "sales" "Sales" (Some "Analytics") None
        row "reports" "Reports" (Some "Analytics") None
        row "scratch" "Scratch" None None
        adminSwitcher
    ]

    let productNames = [
        HomeId, "Home"
        "sales", "Sales"
        "reports", "Reports"
        "scratch", "Scratch"
        AdminAreaId, "Administration"
    ]

    {
        Name = "expanded rail — Product area"
        Modules = productModules
        Prefs = expanding [ "Analytics"; OtherKey ]
        Selected = "sales"
        Hovered = true
        MustName = productNames
    }

    {
        // The Phase 609 state. No row renders text here, so every name in
        // `MustName` can only come from an `aria-label`.
        Name = "narrow icon-only rail — Product area"
        Modules = productModules
        Prefs = expanding [ "Analytics"; OtherKey ]
        Selected = "sales"
        Hovered = false
        MustName = productNames
    }

    // 3 + 4 — the Phase 567 Administration area, both widths: the admin
    // landing, a multi-page admin module with its subtree open, and the
    // switcher back out. The page children only render in the expanded
    // rail, so only state 3 demands their names.
    let adminModules = [
        adminLanding
        pagedRow "admin.tenants" "Tenants" (Some "Administration") [
            "admin.tenants/list", "All tenants"
            "admin.tenants/new", "New tenant"
        ]
        productSwitcher
    ]

    let adminPrefs = {
        expanding [ "Administration" ] with
            ExpandedModules = Set.ofList [ "admin.tenants" ]
    }

    {
        Name = "expanded rail — Administration area"
        Modules = adminModules
        Prefs = adminPrefs
        Selected = "admin.tenants/list"
        Hovered = true
        MustName = [
            AdminHomeId, "Administration"
            "admin.tenants", "Tenants"
            "admin.tenants/list", "All tenants"
            "admin.tenants/new", "New tenant"
            ProductAreaId, "Back to app"
        ]
    }

    {
        Name = "narrow icon-only rail — Administration area"
        Modules = adminModules
        Prefs = adminPrefs
        Selected = "admin.tenants/list"
        Hovered = false
        MustName = [
            AdminHomeId, "Administration"
            "admin.tenants", "Tenants"
            ProductAreaId, "Back to app"
        ]
    }

    // 5 + 6 — a collapsed group, on a fresh profile (which is when every
    // group is collapsed). In the narrow rail the whole group becomes ONE
    // button carrying the group's own name; in the expanded rail it becomes
    // a header button. Either way the members are not rendered, and the
    // affordance that reveals them is the thing that must be named — the
    // Phase 609 sweep found this one resolving to an EMPTY name.
    let collapsedModules = [
        productLanding
        row "sales" "Sales" (Some "Analytics") None
        row "reports" "Reports" (Some "Analytics") None
    ]

    {
        Name = "expanded rail — a collapsed group"
        Modules = collapsedModules
        Prefs = fresh
        Selected = HomeId
        Hovered = true
        MustName = [ HomeId, "Home"; "Analytics", "Analytics" ]
    }

    {
        Name = "narrow icon-only rail — a collapsed group"
        Modules = collapsedModules
        Prefs = fresh
        Selected = HomeId
        Hovered = false
        MustName = [ HomeId, "Home"; "Analytics", "Analytics" ]
    }

    // 7 — the Phase 572 hidden-items reveal section. Expanded rail only:
    // the narrow rail omits it deliberately ("a list of things the user
    // chose not to see, rendered as an anonymous icon, is worse than
    // absent"), which is itself the reason there is no narrow twin here.
    // The restore row and the eye affordance both need names.
    {
        Name = "expanded rail — the hidden-items section"
        Modules = [
            productLanding
            row "sales" "Sales" (Some "Analytics") None
            row "reports" "Reports" (Some "Analytics") None
            row "scratch" "Scratch" None None
        ]
        Prefs = {
            expanding [ "Analytics"; OtherKey; HiddenKey ] with
                HiddenEntryIds = [ "reports" ]
        }
        Selected = "sales"
        Hovered = true
        MustName = [
            HomeId, "Home"
            "sales", "Sales"
            "scratch", "Scratch"
            HiddenKey, "Hidden items"
            "reports", "Restore Reports"
        ]
    }

    // 8 + 9 — the no-active-team collapse: the post-sign-in / pre-team-pick
    // window, where `SidebarVisibility`'s stage-4 filter strips the rail to
    // the landing module alone. This is the state that makes `_other` the
    // only bucketed section, which is when `buildSections` DROPS its
    // "Other" title — and an untitled collapsed section is exactly the
    // shape whose narrow-rail icon used to resolve to an empty accessible
    // name (`Option.defaultValue ""`), reachable in the shipped product
    // and fixed by Phase 609 falling back to the lead module's name.
    let awaitingModules = [ productLanding; awaitingTeam ]

    {
        Name = "narrow icon-only rail — the no-active-team collapse"
        Modules = awaitingModules
        Prefs = fresh
        Selected = HomeId
        Hovered = false
        // "No team yet" is the LEAD MODULE's name standing in for the
        // untitled section — the Phase 609 fallback, asserted rather than
        // described.
        MustName = [ HomeId, "Home"; OtherKey, "No team yet" ]
    }

    {
        // The expanded twin demands only the landing. An untitled collapsed
        // section renders no header in the expanded rail, and therefore no
        // row and no chevron either — so "No team yet" is genuinely absent
        // here rather than unnamed, which is a reachability question for
        // the renderer and not an a11y-name one. Noted rather than
        // asserted: this pack's subject is names.
        Name = "expanded rail — the no-active-team collapse"
        Modules = awaitingModules
        Prefs = fresh
        Selected = HomeId
        Hovered = true
        MustName = [ HomeId, "Home" ]
    }
]

// ─── Render ──────────────────────────────────────────────────────────

/// Mount one rail state and return the markup the browser would have.
let private render (state: RailState) : string =
    let dom =
        JSDOM("<!doctype html><html><body></body></html>", createObj [ "pretendToBeVisual" ==> true ])

    installGlobals dom.window

    let document = dom.window?document
    let host = document?createElement "div"
    document?body?appendChild host

    let sections = buildSections state.Modules state.Prefs

    let element =
        Sidebar "Demo app" "/logo.svg" sections state.Selected ignore ignore ignore ignore ignore (fun _ _ -> ())

    let root = createRoot host
    act (fun () -> root?render element |> ignore)

    if state.Hovered then
        // The rail expands on pointer-enter and nothing else. React
        // synthesises `onMouseEnter` from the native `mouseover` /
        // `mouseout` pair, so this is the event that drives it; a
        // `relatedTarget` of null means "the pointer came from outside the
        // document", which is what an enter is.
        let aside = host?querySelector "aside"

        let event =
            newMouseEvent
                (dom.window?MouseEvent)
                "mouseover"
                (createObj [ "bubbles" ==> true; "relatedTarget" ==> null ])

        act (fun () -> aside?dispatchEvent event |> ignore)

    // The whole host, not just the <aside>: dnd-kit renders its keyboard
    // instructions and its live region as siblings of the rail, and those
    // are part of what assistive tech sees.
    host?innerHTML

/// The rail width the markup actually rendered at, read from the `<aside>`
/// class list. Asserted per state, because a hover that silently failed to
/// register would leave every "expanded rail" case checking the narrow one
/// — a whole half of the fixture set passing while testing the wrong tree.
let private isWideRail (html: string) = html.Contains "w-64"

// ─── Known, tracked gaps ─────────────────────────────────────────────

/// A finding that is REAL, is not this phase's to fix, and is pinned so it
/// cannot be mistaken for coverage — while a NEW finding still fails.
///
/// The pin is keyed by the offending element's own attributes rather than
/// by its document path, so it survives unrelated markup moving around it
/// and cannot accidentally start covering a different element.
type private KnownGap = {
    Rule: string
    /// Why the finding stands, and where it belongs.
    Why: string
    /// Recognises the offending element from its attributes.
    Matches: Map<string, string> -> bool
}

/// Empty, and that is the point: the one gap this pack shipped with —
/// dnd-kit's drag wrapper, an unnamed focusable `role="button"` around every
/// row because `SortableItem` splats `useSortable`'s `attributes` (including
/// `tabindex=0`) onto a bare <div> — was closed by Phase 612
/// (`toolup-forge@ac6f533`), which gave the wrapper an `aria-label` and folded
/// it into the rail's roving tabindex.
///
/// Its removal was NOT bookkeeping. The `every pinned known gap still fires
/// somewhere` case below failed the moment 612 landed, which is the guard
/// working exactly as designed: a pin that has stopped firing is an exemption
/// that has silently stopped checking anything, and the pack refuses to carry
/// one. Leaving the entry here would have suppressed a real future regression
/// of the same class.
///
/// The machinery stays for the next genuine not-mine-to-fix finding.
let private knownGaps: KnownGap list = []

/// Split a state's findings into the pinned gaps and the ones that must
/// fail the pack.
let private classify (node: Accessibility.A11yNode) (findings: Accessibility.A11yFinding list) =
    findings
    |> List.partition (fun f ->
        match Accessibility.locate node f with
        | None -> false
        | Some e -> knownGaps |> List.exists (fun g -> g.Rule = f.Rule && g.Matches e.Attrs))

// ─── Cases ───────────────────────────────────────────────────────────

/// `Accessibility.StateFinding`, built with the state name in front. The
/// return annotation is what lets the fields go unqualified.
let private inState (state: string) (finding: Accessibility.A11yFinding) : Accessibility.StateFinding = {
    State = state
    Finding = finding
}

/// Every accessible name exposed by an interactive element in a tree.
let private namesIn (node: Accessibility.A11yNode) =
    Accessibility.interactiveNames node |> List.choose snd

let tests =
    testList "Phase 610 — a11y floor over the shell rail states" [

        // 610.B — the Phase 180 rules, verbatim, over every state. `Strict`
        // rather than `Minimal` because it runs the full table: the four
        // error-class rules (accessible names, image alt, labelled
        // controls, valid ARIA) PLUS heading order and colour-only state,
        // which is the set Phase 610.B names, and it makes every finding
        // fatal.
        testCase "every rail state passes the Phase 180 Strict rule set" (fun () ->
            let failures =
                railStates
                |> List.collect (fun st ->
                    let html = render st
                    let node = Accessibility.ofHtml html

                    Expect.equal
                        (isWideRail html)
                        st.Hovered
                        ("state \""
                         + st.Name
                         + "\" rendered at the wrong rail width. The hover that expands the rail is a \
                            real `mouseover` on the <aside>; if it stopped registering, every \
                            expanded-rail state would silently check the narrow tree instead.")

                    let _, real = classify node (Accessibility.check Accessibility.Strict node)
                    real |> List.map (inState st.Name))

            if not (List.isEmpty failures) then
                failwith (
                    Accessibility.reportStates Accessibility.Strict failures
                    + "\n\nEach finding is a control the shell renders in that state with no \
                       accessible name, no alt text, or invalid ARIA. The rules are Phase 180's, \
                       unchanged; what this pack adds is the STATES. Fix the control in \
                       `Sidebar.fs` — a rail row inherits the naming rule by going through \
                       `renderRow`, and a new icon-only control sets both `prop.ariaLabel` and \
                       `prop.title` (see `rowAccessibleName`)."
                ))

        // 610.C — a violation names the offending row id and the state, not
        // a boolean. This is the half a RULE cannot do: a rule reports what
        // is present and unnamed, never what is absent entirely, and a row
        // that vanished from the rail is indistinguishable from a rail with
        // nothing wrong.
        testCase "every state's expected rows are reachable BY NAME" (fun () ->
            let missing = [
                for st in railStates do
                    let names = namesIn (Accessibility.ofHtml (render st))

                    for rowId, expected in st.MustName do
                        if not (List.contains expected names) then
                            yield st.Name, rowId, expected, names
            ]

            if not (List.isEmpty missing) then
                let detail = [
                    for stateName, rowId, expected, found in missing do
                        yield
                            sprintf
                                "  state \"%s\": row `%s` should be reachable as \"%s\" — the names actually exposed were [%s]"
                                stateName
                                rowId
                                expected
                                (found |> List.map (sprintf "\"%s\"") |> String.concat "; ")
                ]

                failwith (
                    sprintf "%d rail row(s) are not reachable by name:\n" missing.Length
                    + (detail |> String.concat "\n")
                    + "\n\nEither the row lost its accessible name (fix the control — see \
                       `rowAccessibleName` in `Sidebar.fs`), or it is no longer rendered in that \
                       state at all (fix `buildSections` / the render arm), or the expectation in \
                       `railStates` is stale (fix it HERE, and say why in the same commit — this \
                       list is the contract)."
                ))

        // Phase 609's own naming rule, asserted STRUCTURALLY rather than
        // textually — which is what makes this pack "the structural home"
        // its 609.D guard pointed forward to.
        //
        // This is NOT a new a11y rule and does not belong in the
        // `Accessibility` vocabulary: it is the SHELL's convention, one
        // notch stricter than the standard. The standard rule accepts
        // `title` as an accessible name, so a rail row that carries a
        // tooltip and nothing else passes `everyInteractiveHasAccessibleName`
        // — measured, not assumed: deleting `prop.ariaLabel` from
        // `renderRow` while leaving `prop.title` in place leaves every case
        // above green. But `title` is the weakest source in the name
        // computation, several assistive technologies skip it, and it does
        // not exist on touch at all. Phase 609 therefore set `aria-label` on
        // every control and added `title` only ON TOP of it. That is a
        // decision the standard cannot express, so the pack states it.
        testCase "every rail <button> carries an explicit aria-label (Phase 609's rule)" (fun () ->
            let offenders = [
                for st in railStates do
                    let node = Accessibility.ofHtml (render st)

                    for e in Accessibility.elements node do
                        if e.Tag = "button" then
                            let label =
                                Map.tryFind "aria-label" e.Attrs
                                |> Option.map _.Trim()
                                |> Option.filter (fun v -> v <> "")

                            if label.IsNone then
                                yield st.Name, e.Path, Accessibility.accessibleName e
            ]

            if not (List.isEmpty offenders) then
                let detail = [
                    for stateName, path, fallback in offenders do
                        yield
                            sprintf
                                "  state \"%s\": <button> at %s has no aria-label (it would announce as %s)"
                                stateName
                                path
                                (match fallback with
                                 | Some n -> sprintf "\"%s\", from its title or its text — which identifies the row" n
                                 | None -> "NOTHING AT ALL")
                ]

                failwith (
                    sprintf
                        "%d rail <button>(s) carry no aria-label:
"
                        offenders.Length
                    + (detail
                       |> String.concat
                           "
")
                    + "

Every interactive control in `Sidebar.fs` sets `prop.ariaLabel` — that is                        the accessible name, and the only source that survives both a screen reader                        and a touch device. `prop.title` is added ON TOP of it where the control has                        no visible text, and is never a substitute: an element named only by its                        title passes the standard rule above while still being unusable by voice                        control and skipped by some AT. See `rowAccessibleName`'s doc-comment for                        the rule a new control follows."
                ))

        // The pins have to be load-bearing in both directions: a pinned gap
        // that has been FIXED is a stale pin, and a stale pin is how a
        // whole class quietly stops being checked.
        testCase "every pinned known gap still fires somewhere" (fun () ->
            for gap in knownGaps do
                let fires =
                    railStates
                    |> List.exists (fun st ->
                        let node = Accessibility.ofHtml (render st)
                        let pinned, _ = classify node (Accessibility.check Accessibility.Strict node)
                        not (List.isEmpty pinned))

                Expect.isTrue
                    fires
                    ("the known gap pinned for rule `"
                     + gap.Rule
                     + "` no longer fires in any rail state. If it was fixed, DELETE the pin — "
                     + "leaving it in place exempts an element class that is now clean, and the "
                     + "next real instance of it would pass. The pin reads: "
                     + gap.Why))

        // The guard has to be able to fail, and the cheapest proof is to
        // feed it a tree that is wrong in exactly the Phase 609 way. Without
        // this the pack could be inspecting nothing and would look
        // identical — which is the failure mode Phase 610 exists to close,
        // so it would be a poor one to reproduce.
        testCase "the floor rejects a rail row stripped of its name" (fun () ->
            let named =
                """<aside><div class="relative group"><button aria-label="Administration" title="Administration"><svg></svg></button></div></aside>"""

            let stripped =
                """<aside><div class="relative group"><button><svg></svg></button></div></aside>"""

            Expect.isEmpty
                (Accessibility.check Accessibility.Strict (Accessibility.ofHtml named))
                "a narrow-rail row carrying aria-label + title is clean — the shape Phase 609 \
                 established"

            let findings =
                Accessibility.check Accessibility.Strict (Accessibility.ofHtml stripped)

            Expect.equal
                (findings |> List.map _.Rule)
                [ "everyInteractiveHasAccessibleName" ]
                "removing the name must produce exactly the accessible-name finding — if this is \
                 empty the rules are inspecting nothing, and every other case in this pack is \
                 vacuous"

            let reported =
                Accessibility.reportStates
                    Accessibility.Strict
                    (findings |> List.map (inState "narrow icon-only rail — Product area"))

            Expect.isTrue
                (reported.Contains "narrow icon-only rail — Product area")
                "and the report must name the STATE — that is the part a reader cannot recover from \
                 the element path (610.C)")
    ]
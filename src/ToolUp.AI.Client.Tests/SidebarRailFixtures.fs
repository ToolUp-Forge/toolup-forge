// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.SidebarRailFixtures

// ─── The shell rail's states, and the DOM harness that renders them ───
//
// Extracted verbatim from `SidebarRailA11yTests` at
// [Phase 613](613-structural-snapshot-gate-for-the-composed-shell.md),
// whose 613.B says to REUSE the
// [Phase 610](610-extend-the-phase-180-a11y-floor-to-the-shell-rail-states.md)
// state enumeration rather than define a second one. Two packs now render
// the same nine states — the a11y floor and the structural snapshot gate —
// and a second list would have been the defect both of them exist to
// catch: a state covered by one gate and invisible to the other looks
// exactly like a state that is fine.
//
// So `railStates` below is THE list. Adding a rail state means adding a
// row here, and both packs widen together.
//
// ── What the harness does ──
// It renders `Toolup.Sidebar.Sidebar` — the actual shipped component, not
// a model of it — into a real DOM with `react-dom/client`, drives the
// hover that expands the rail with a real `mouseover`, and returns the
// mounted host's markup. So the tree its consumers judge IS the markup a
// browser would get, attribute for attribute; there is no hand-maintained
// projection of `renderRow` that could drift away from `renderRow`.
//
// It does NOT model CSS, focus rings, contrast, or the browser's real
// accessibility tree — computed style is invisible here.
//
// ── Why a DOM, and why that is not new test infrastructure ──
// The rail's narrow-vs-expanded axis is component-local `React.useState`
// flipped by `onMouseEnter`. A string renderer
// (`react-dom/server.renderToStaticMarkup`, which needs no DOM and was
// tried first at Phase 610) therefore only ever produces the NARROW rail
// — it cannot reach the expanded one, and with it the section headers, the
// row name spans, the pin / hide affordances and the hidden-items reveal
// list. So the harness mounts instead. `react-dom` was already installed
// here; `jsdom` is the one added devDependency, and it is the one this
// project's own `.fsproj` header reserved for "future view-level tests
// (Feliz components, useState interaction) … adding JSDOM as a devDep when
// such a test concretely needs it". The RUNNER is still `node:test` with
// no transitive deps. Nothing ships: `IsPackable=false`, zero runtime cost
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
///
/// `MustName` is read only by the Phase 610 a11y pack; the Phase 613
/// snapshot pack captures the whole rendered shape and needs no per-state
/// expectation. It stays on this record rather than moving beside 610
/// because one list of states with one row per state is the property worth
/// keeping — the two gates disagreeing about which states exist is the
/// failure mode extraction was for.
type RailState = {
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

/// Every shell rail state the gates cover. Adding a rail state means
/// adding a row HERE — nothing else enumerates states, and a state absent
/// from this list is a state neither the Phase 610 a11y floor nor the
/// Phase 613 snapshot gate holds for.
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
let railStates: RailState list = [
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
        // asserted: the Phase 610 pack's subject is names. The Phase 613
        // snapshot records the absence literally — see the "known defects
        // these baselines encode" note in `SidebarRailShapeSnapshotTests`.
        Name = "expanded rail — the no-active-team collapse"
        Modules = awaitingModules
        Prefs = fresh
        Selected = HomeId
        Hovered = true
        MustName = [ HomeId, "Home" ]
    }
]

// ─── Render ──────────────────────────────────────────────────────────

/// The sections the shell would hand the renderer for this state — the
/// same fold the component itself runs, exposed so a caller can compare a
/// pure projection (e.g. `railStops`) against the rendered markup.
let sectionsFor (state: RailState) = buildSections state.Modules state.Prefs

/// Mount one rail state and return the markup the browser would have.
let render (state: RailState) : string =
    let dom =
        JSDOM("<!doctype html><html><body></body></html>", createObj [ "pretendToBeVisual" ==> true ])

    installGlobals dom.window

    let document = dom.window?document
    let host = document?createElement "div"
    document?body?appendChild host

    let sections = sectionsFor state

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
/// class list. Asserted per state by both consuming packs, because a hover
/// that silently failed to register would leave every "expanded rail" case
/// checking the narrow one — a whole half of the fixture set passing while
/// testing the wrong tree.
let isWideRail (html: string) = html.Contains "w-64"
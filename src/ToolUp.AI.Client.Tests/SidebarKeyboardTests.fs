// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.SidebarKeyboardTests

// ─── Phase 612 — keyboard navigation for the sidebar rail (Fable tier) ─
//
// Phase 571 gave the shell a keyboard-first way to JUMP to a page whose
// name the user already knows, and said outright that it routes *around*
// the rail. This pack covers the other half: BROWSING the rail, the case a
// search box structurally cannot serve because it needs a term.
//
// What is asserted here is the model, not the DOM: `railStops` (the one
// traversal order), `railKey` (the bindings), `railTabIndex` (the roving
// single tab stop) and `resolveActiveStop` (the "never zero tab stops"
// guard) are all pure functions of the section list the renderer is about
// to walk. That is deliberate — the renderer's job is then only to stamp
// each control with the key the model already named, and a binding can be
// pinned without a JSDOM.
//
// **Why these run here and not in the .NET pack.** Same constraint Phase
// 611's pack documents at length: `Toolup.Sidebar`'s module initialiser
// reaches `importDefault "../icons/toolup-forge-dark.png"`, and F# emits a
// static-init check on module function entry, so the FIRST call into the
// module from a .NET harness throws "You've hit dummy code used for Fable
// bindings" — even for a function that reads no module-level value. So the
// executable half lives here, beside `SidebarPlacementTests` /
// `SidebarNestingTests` / `SidebarHidingTests`, which pin the same fold.
//
// **Migration — DONE; nothing here is waiting to move.** Phase 612.E asked
// for the focus-order rule to be folded into Phase 610's a11y fixtures "if
// that has landed". It had not when this pack was written (610 was in
// flight in a parallel session), so this header used to name two
// invariants to promote once a rendered-DOM fixture set existed. Phase 613
// took BOTH when it built the structural snapshot gate, and they live in
// `SidebarRailShapeSnapshotTests` over the nine rendered states of
// `SidebarRailFixtures.railStates`:
//
//   * "every rendered rail stop is in `railStops`, and every stop is
//     rendered" — the iff invariant this pure pack can only assert one
//     side of, because `railStops` is a projection of the section data
//     and knows nothing about what the renderer DREW.
//   * "each rendered rail state has exactly one tab stop" — counted over
//     elements carrying `tabindex=0` in the real markup, which is the
//     half `railTabIndex` cannot reach: it returning 0 for exactly one
//     KEY proves nothing about how many ELEMENTS were given that key.
//
// Do not restate either of them here. The cases below are the pure-model
// ones and are the right shape for this pack: `railStops`' own ordering,
// the `railKey` bindings, `railTabIndex`, and `resolveActiveStop` — all
// pure functions of the section list, all cheaper without a JSDOM.

open Toolup.Sidebar
open ToolUp.AI.Client.Tests.NodeTest

// ─── Fixtures ────────────────────────────────────────────────────────

let private stubIcon: Fable.React.ReactElement = unbox null

let private leaf (id: string) : SidebarModule = {
    Id = id
    Name = id
    Icon = stubIcon
    HasData = false
    IsPinned = false
    Pages = []
    IsExpanded = false
}

let private pageOf (id: string) : SidebarPage = {
    Id = id
    Name = id
    Icon = stubIcon
    IsPinned = false
}

/// A multi-page parent: one collapsible row whose pages nest under it.
let private parentOf (id: string) (isOpen: bool) (pageIds: string list) : SidebarModule = {
    leaf id with
        Pages = pageIds |> List.map pageOf
        IsExpanded = isOpen
}

let private sectionOf
    (key: string)
    (title: string option)
    (isCollapsed: bool)
    (modules: SidebarModule list)
    : SidebarSection =
    {
        Key = key
        Title = title
        IsCollapsed = isCollapsed
        IsPinnedSection = false
        Modules = modules
    }

/// The leading placed section, as `buildSections` builds it: untitled and
/// never collapsed.
let private placed (key: string) (modules: SidebarModule list) = sectionOf key None false modules

let private noSelection = ""

/// Every focus key the rail exposes — each stop's primary control plus its
/// further controls. This is the set `railTabIndex` is applied to, so it is
/// the set the single-tab-stop claim has to be measured over.
let private allKeys (stops: RailStop list) =
    stops |> List.collect (fun s -> s.Key :: s.Controls)

let private keysOf (stops: RailStop list) = stops |> List.map _.Key

/// A `RailKeyOutcome` flattened to a comparable string, so a failing
/// assertion names the difference instead of dumping a stop record.
let private outcome (o: RailKeyOutcome) =
    match o with
    | MoveFocus key -> "move:" + key
    | Disclose stop -> "disclose:" + stop.Key
    | Consumed -> "consumed"
    | PassThrough -> "passthrough"

let private press (stops: RailStop list) (activeKey: string) (key: string) =
    outcome (railKey stops activeKey key false)

let private pressWithModifier (stops: RailStop list) (activeKey: string) (key: string) =
    outcome (railKey stops activeKey key true)

// ─── Cases ───────────────────────────────────────────────────────────

let tests =
    testList "Phase 612 — rail keyboard navigation" [

        // ── 612.A — the traversal order ──────────────────────────────

        testCase "the wide rail's order is section header, then rows, then a parent's pages" (fun () ->
            // The order is the rendered order, top to bottom: the sections
            // in `buildSections` order, each titled section's header ahead
            // of its rows, and each multi-page parent immediately followed
            // by its own pages rather than by the next sibling row.
            let sections = [
                placed HomeKey [ leaf HomeId ]
                sectionOf "Analytics" (Some "Analytics") false [
                    leaf "Sales"
                    parentOf "Reports" true [ "Reports/q1" ]
                ]
                placed TrailingKey [ leaf AdminAreaId ]
            ]

            let stops = railStops true noSelection sections

            Expect.equal
                (keysOf stops)
                [
                    RailFocus.rowKey HomeKey HomeId
                    RailFocus.headerKey "Analytics"
                    RailFocus.rowKey "Analytics" "Sales"
                    RailFocus.rowKey "Analytics" "Reports"
                    RailFocus.rowKey "Analytics" "Reports/q1"
                    RailFocus.rowKey TrailingKey AdminAreaId
                ]
                "the traversal order must read down the rendered rail: the untitled leading placed \
                 section's row, the titled group's header, its rows, the parent's page nested \
                 immediately after its parent, then the trailing placed section")

        testCase "a collapsed group is ANNOUNCED, not skipped" (fun () ->
            // The decision this phase had to make and state. A closed
            // section keeps its header in the order and drops only its
            // rows, because that header is the sole control that opens the
            // section — traversal that stepped over it would leave the
            // group permanently unreachable without a pointer, and on a
            // fresh profile EVERY grouped section is closed.
            let sections = [
                placed HomeKey [ leaf HomeId ]
                sectionOf "Analytics" (Some "Analytics") true [ leaf "Sales"; leaf "Marketing" ]
            ]

            let stops = railStops true noSelection sections

            Expect.equal
                (keysOf stops)
                [ RailFocus.rowKey HomeKey HomeId; RailFocus.headerKey "Analytics" ]
                "a collapsed section must contribute its header and none of its rows. Skipping the \
                 section outright reads tidier and is wrong: nothing else opens it."

            match stops |> List.tryFind (fun s -> s.Key = RailFocus.headerKey "Analytics") with
            | None -> Expect.isTrue false "the collapsed group's header is missing from the order"
            | Some header ->
                Expect.isTrue header.IsDisclosure "the header is the disclosure `ArrowRight` opens"
                Expect.isFalse header.IsOpen "…and it reports itself closed")

        testCase "an untitled section contributes no header stop" (fun () ->
            // The placed sections and a lone `_other` render header-less
            // (Phase 611), so there is no chevron to focus. A stop for a
            // control that is not drawn is the mirror of the defect this
            // phase fixes.
            let stops =
                railStops true noSelection [
                    placed HomeKey [ leaf HomeId ]
                    sectionOf OtherKey None false [ leaf "Scratch" ]
                ]

            Expect.equal
                (keysOf stops)
                [ RailFocus.rowKey HomeKey HomeId; RailFocus.rowKey OtherKey "Scratch" ]
                "an untitled section renders no header button, so it offers no header stop")

        testCase "the narrow rail collapses a closed group to one stop and drops the reveal list" (fun () ->
            // Three narrow-rail rules at once: no headers (none are
            // rendered), a closed group becomes its single group icon, and
            // `_hidden` is absent entirely — a list of things the user chose
            // not to see, rendered as an anonymous icon, is worse than gone.
            let sections = [
                placed HomeKey [ leaf HomeId ]
                sectionOf "Analytics" (Some "Analytics") true [ leaf "Sales"; leaf "Marketing" ]
                sectionOf "Reports" (Some "Reports") false [ leaf "Finance" ]
                sectionOf HiddenKey (Some "Hidden items") false [ leaf "Buried" ]
            ]

            let stops = railStops false noSelection sections

            Expect.equal
                (keysOf stops)
                [
                    RailFocus.rowKey HomeKey HomeId
                    RailFocus.groupKey "Analytics"
                    RailFocus.rowKey "Reports" "Finance"
                ]
                "narrow: the placed row stays visible, the CLOSED group shrinks to one group-icon \
                 stop, the OPEN group shows its rows, and the Hidden items section contributes \
                 nothing because the narrow rail does not render it")

        testCase "a parent that owns the active page is traversable even when its stored state says closed" (fun () ->
            // The renderer force-expands the parent owning the active page
            // regardless of the persisted state, so a deep-linked page is
            // always on screen. If the focus model read only `IsExpanded`
            // that page would be visible and unreachable.
            let sections = [
                sectionOf "Analytics" (Some "Analytics") false [
                    parentOf "Reports" false [ "Reports/q1"; "Reports/q2" ]
                ]
            ]

            let stops = railStops true "Reports/q2" sections

            Expect.equal
                (keysOf stops)
                [
                    RailFocus.headerKey "Analytics"
                    RailFocus.rowKey "Analytics" "Reports"
                    RailFocus.rowKey "Analytics" "Reports/q1"
                    RailFocus.rowKey "Analytics" "Reports/q2"
                ]
                "the force-expanded parent's pages must be in the order — they are rendered, so \
                 they are reachable"

            Expect.equal
                (keysOf (railStops true noSelection sections))
                [ RailFocus.headerKey "Analytics"; RailFocus.rowKey "Analytics" "Reports" ]
                "…and with no page selected the same parent is closed, contributing only itself")

        testCase "a parent is a disclosure in the wide rail only" (fun () ->
            // Narrow, clicking a parent navigates to its first page: there
            // is no subtree on screen, so `ArrowRight` has nothing to open
            // and must not claim to.
            let sections = [ sectionOf OtherKey None false [ parentOf "Reports" false [ "Reports/q1" ] ] ]

            let wide = railStops true noSelection sections |> List.head
            let narrow = railStops false noSelection sections |> List.head

            Expect.isTrue wide.IsDisclosure "wide: the parent discloses its page subtree"
            Expect.isFalse narrow.IsDisclosure "narrow: the parent navigates instead, so it discloses nothing")

        // ── 612.A — the roving tabindex ──────────────────────────────

        testCase "exactly one control in the rail is a tab stop" (fun () ->
            // The whole point of the roving model. Before it, every row,
            // every pin, every hide and every dnd-kit sortable wrapper was
            // its own tab stop — dozens of stops between the chrome above
            // the rail and the page content below, with no way past.
            let sections = [
                placed HomeKey [ leaf HomeId ]
                sectionOf "Analytics" (Some "Analytics") false [
                    leaf "Sales"
                    parentOf "Reports" true [ "Reports/q1" ]
                ]
            ]

            let stops = railStops true noSelection sections
            let keys = allKeys stops

            Expect.isTrue (List.length keys > 6) "the fixture really does expose several controls"

            for activeKey in keys do
                let tabStops = keys |> List.filter (fun k -> railTabIndex activeKey k = 0)

                Expect.equal
                    tabStops
                    [ activeKey ]
                    ("with the roving stop at "
                     + activeKey
                     + " exactly one control may carry `tabIndex 0`; every other control in the \
                        rail must be -1, reachable only from inside by arrow key"))

        testCase "the rail never ends up with zero tab stops" (fun () ->
            // The failure this guard exists for: a remembered key naming a
            // control that no longer exists — its section collapsed, the
            // rail changed width, the entry was hidden — would leave NOTHING
            // carrying `tabIndex 0`, i.e. a rail unreachable by keyboard at
            // all. That is worse than the defect the phase fixes.
            let sections = [
                placed HomeKey [ leaf HomeId ]
                sectionOf "Analytics" (Some "Analytics") false [ leaf "Sales" ]
            ]

            let stops = railStops true noSelection sections
            let stale = RailFocus.rowKey "Analytics" "AGroupThatWasRemoved"

            Expect.equal
                (resolveActiveStop stops noSelection (Some stale))
                (RailFocus.rowKey HomeKey HomeId)
                "a stale remembered key must fall back to a live stop, not leave the rail without \
                 a tab stop"

            Expect.equal
                (resolveActiveStop stops "Sales" None)
                (RailFocus.rowKey "Analytics" "Sales")
                "with nothing remembered, `Tab` enters the rail at the SELECTED row — where the \
                 user already is — rather than at the top"

            Expect.equal
                (resolveActiveStop stops noSelection (Some(RailFocus.hideKey "Analytics" "Sales")))
                (RailFocus.hideKey "Analytics" "Sales")
                "a remembered key naming a row's trailing control is live too, so `Tab` returns to \
                 the control the user left, not to its row"

            Expect.equal (resolveActiveStop [] noSelection None) "" "an empty rail resolves to no key at all")

        testCase "the Tab entry point is a row, so it survives the rail changing width" (fun () ->
            // A row's focus key is identical in both rail widths; a section
            // header's and a group icon's are not. Since the rail is narrow
            // at rest, the resolved stop is also the element `Tab` enters
            // through — and entering it WIDENS the rail. Were the entry
            // element a collapsed-group icon, widening would unmount the
            // element that had just been focused, and the resulting
            // null-`relatedTarget` blur would collapse the rail again: `Tab`
            // would flap the rail open and drop focus to the body.
            let sections = [
                sectionOf "Analytics" (Some "Analytics") true [ leaf "Sales" ]
                placed HomeKey [ leaf HomeId ]
            ]

            let narrowEntry =
                resolveActiveStop (railStops false noSelection sections) noSelection None

            let wideEntry =
                resolveActiveStop (railStops true noSelection sections) noSelection None

            Expect.equal
                narrowEntry
                (RailFocus.rowKey HomeKey HomeId)
                "narrow: the entry stop must be the placed ROW, not the collapsed group's icon — \
                 which is first in the order but vanishes the instant focus widens the rail"

            Expect.equal
                wideEntry
                narrowEntry
                "…and it must be the SAME key once the rail is wide, which is the whole property: \
                 the element focus landed on is still there after the width change"

            // The nicer consequence of the same rule, in the ordinary rail:
            // Tab lands on a destination rather than on a section chevron.
            let titledOnly = [ sectionOf "Analytics" (Some "Analytics") false [ leaf "Sales" ] ]

            Expect.equal
                (resolveActiveStop (railStops true noSelection titledOnly) noSelection None)
                (RailFocus.rowKey "Analytics" "Sales")
                "with only a titled section, Tab enters at its first row; the header is one ArrowUp \
                 away rather than in the way")

        // ── 612.B — the bindings ─────────────────────────────────────

        testCase "Down and Up walk the order and stop at the ends" (fun () ->
            let stops =
                railStops true noSelection [
                    placed HomeKey [ leaf HomeId ]
                    sectionOf "Analytics" (Some "Analytics") false [ leaf "Sales" ]
                ]

            let home = RailFocus.rowKey HomeKey HomeId
            let header = RailFocus.headerKey "Analytics"
            let sales = RailFocus.rowKey "Analytics" "Sales"

            Expect.equal (press stops home "ArrowDown") ("move:" + header) "Down steps to the next stop"
            Expect.equal (press stops header "ArrowDown") ("move:" + sales) "…and the next"
            Expect.equal (press stops sales "ArrowUp") ("move:" + header) "Up steps back"

            Expect.equal
                (press stops home "ArrowUp")
                "consumed"
                "Up at the top of the rail goes nowhere — arrow traversal does not wrap, because a \
                 silent wrap is indistinguishable from nothing happening when you cannot see the \
                 rail. The keystroke is still CONSUMED so a held arrow does not start scrolling \
                 the page instead."

            Expect.equal (press stops sales "ArrowDown") "consumed" "…and likewise at the bottom"

            Expect.equal (press stops home "Home") ("move:" + home) "Home jumps to the first stop"
            Expect.equal (press stops home "End") ("move:" + sales) "End jumps to the last")

        testCase "Right opens a closed disclosure and Left closes an open one" (fun () ->
            let openSections = [ sectionOf "Analytics" (Some "Analytics") false [ leaf "Sales" ] ]
            let closedSections = [ sectionOf "Analytics" (Some "Analytics") true [ leaf "Sales" ] ]

            let header = RailFocus.headerKey "Analytics"
            let closed = railStops true noSelection closedSections
            let opened = railStops true noSelection openSections

            Expect.equal
                (press closed header "ArrowRight")
                ("disclose:" + header)
                "Right on a CLOSED section opens it — the WAI-ARIA disclosure convention, and the \
                 keyboard route into a collapsed group"

            Expect.equal (press opened header "ArrowLeft") ("disclose:" + header) "Left on an OPEN section closes it"

            Expect.equal
                (press closed header "ArrowLeft")
                "consumed"
                "Left on an already-closed section has nothing to do — a section header has no \
                 further controls to step back through"

            // The narrow rail's equivalent: one group icon standing for the
            // whole closed section.
            let groupIcon = RailFocus.groupKey "Analytics"

            Expect.equal
                (press (railStops false noSelection closedSections) groupIcon "ArrowRight")
                ("disclose:" + groupIcon)
                "narrow: Right on the collapsed group's icon opens the section, which is the only \
                 keyboard route into its rows at that width")

        testCase "Right on an OPEN disclosure walks the row's controls instead of moving to its first child" (fun () ->
            // A deliberate deviation from the APG tree pattern, documented
            // on `railKey`: the first child is the very NEXT stop by
            // construction, so `ArrowDown` already reaches it — whereas the
            // pin / hide / reorder controls have no other keyboard route
            // that does not reintroduce a second tab stop. Reaching them is
            // 612.C; duplicating `ArrowDown` is not.
            let stops =
                railStops true noSelection [
                    sectionOf OtherKey None false [ parentOf "Reports" true [ "Reports/q1" ] ]
                ]

            let parentKey = RailFocus.rowKey OtherKey "Reports"
            let firstPage = RailFocus.rowKey OtherKey "Reports/q1"

            Expect.equal
                (press stops parentKey "ArrowDown")
                ("move:" + firstPage)
                "Down from an open parent already lands on its first page"

            Expect.equal
                (press stops parentKey "ArrowRight")
                ("move:" + RailFocus.pinKey OtherKey "Reports")
                "…so Right is spent on the row's own controls rather than repeating that")

        testCase "Enter and Space are not bound here at all" (fun () ->
            // Every stop is a real `<button>`, so both keys already activate
            // it natively and fire the exact `onClick` a pointer click
            // fires. Intercepting them would duplicate the activation path
            // and risk double-firing.
            let stops =
                railStops true noSelection [ sectionOf OtherKey None false [ leaf "Sales" ] ]

            let sales = RailFocus.rowKey OtherKey "Sales"

            for key in [ "Enter"; " "; "Spacebar"; "Escape"; "Tab"; "a" ] do
                Expect.equal
                    (press stops sales key)
                    "passthrough"
                    ("`"
                     + key
                     + "` must pass straight through. Enter and Space are the row button's own \
                        native activation; Tab is how the single tab stop is left; Escape belongs \
                        to whatever overlay is open."))

        // ── 612.C — reaching the per-row controls ────────────────────

        testCase "Right reaches pin, hide and reorder; Left comes back" (fun () ->
            // 612.C in one lane. Each control is a stop on the row's
            // horizontal axis, so all three are reachable without adding a
            // second tab stop — which is what would otherwise happen, since
            // each is a `<button>` (and the reorder handle a dnd-kit
            // `role="button"` wrapper).
            let stops =
                railStops true noSelection [ sectionOf OtherKey None false [ leaf "Sales" ] ]

            let row = RailFocus.rowKey OtherKey "Sales"
            let pin = RailFocus.pinKey OtherKey "Sales"
            let hide = RailFocus.hideKey OtherKey "Sales"
            let reorder = RailFocus.reorderKey OtherKey "Sales"

            Expect.equal (press stops row "ArrowRight") ("move:" + pin) "Right from the row reaches its pin control"
            Expect.equal (press stops pin "ArrowRight") ("move:" + hide) "…then hide"
            Expect.equal (press stops hide "ArrowRight") ("move:" + reorder) "…then the drag handle"

            Expect.equal
                (press stops reorder "ArrowRight")
                "consumed"
                "and there the lane ends — the drag handle is the row's last control"

            Expect.equal (press stops reorder "ArrowLeft") ("move:" + hide) "Left steps back along the lane"
            Expect.equal (press stops pin "ArrowLeft") ("move:" + row) "…and returns to the row itself")

        testCase "Down from a trailing control lands on the NEXT row, not on its own row" (fun () ->
            // Down / Up are the vertical axis: they always land on a stop's
            // primary control, so a user who stepped sideways to a pin does
            // not have to step back before continuing down the rail.
            let stops =
                railStops true noSelection [ sectionOf OtherKey None false [ leaf "Sales"; leaf "Marketing" ] ]

            Expect.equal
                (press stops (RailFocus.pinKey OtherKey "Sales") "ArrowDown")
                ("move:" + RailFocus.rowKey OtherKey "Marketing")
                "Down from the first row's pin control lands on the SECOND row's own button"

            Expect.equal
                (press stops (RailFocus.hideKey OtherKey "Marketing") "ArrowUp")
                ("move:" + RailFocus.rowKey OtherKey "Sales")
                "…and Up from the second row's hide control lands on the first row's button")

        testCase "a row offers exactly the controls the renderer draws for it" (fun () ->
            // The controls a stop advertises have to match what is on
            // screen, in both directions: an advertised control that is not
            // drawn strands the roving focus on nothing, and a drawn control
            // that is not advertised is unreachable — the very defect this
            // phase fixes, one level deeper.
            let sections = [
                // A placed row: never pinnable (its position is declared, so
                // pinning it would move nothing — Phase 611) and never
                // hideable (losing it strands the user — Phase 572).
                placed HomeKey [ leaf HomeId ]
                sectionOf OtherKey (Some "Other") false [ leaf "Sales" ]
                // The reveal list: rendered outside the sortable machinery,
                // and the pin control is suppressed by the pin/hide rule.
                sectionOf HiddenKey (Some "Hidden items") false [ leaf "Buried" ]
            ]

            let stops = railStops true noSelection sections

            let controlsOf key =
                stops |> List.tryFind (fun s -> s.Key = key) |> Option.map _.Controls

            Expect.equal
                (controlsOf (RailFocus.rowKey HomeKey HomeId))
                (Some [ RailFocus.reorderKey HomeKey HomeId ])
                "a placed row offers neither pin nor hide — both controls are suppressed for it, so \
                 neither may be a stop"

            Expect.equal
                (controlsOf (RailFocus.rowKey OtherKey "Sales"))
                (Some [
                    RailFocus.pinKey OtherKey "Sales"
                    RailFocus.hideKey OtherKey "Sales"
                    RailFocus.reorderKey OtherKey "Sales"
                ])
                "an ordinary grouped row offers all three, in the order Right walks them"

            Expect.equal
                (controlsOf (RailFocus.rowKey HiddenKey "Buried"))
                (Some [ RailFocus.hideKey HiddenKey "Buried" ])
                "a row in the reveal list offers only its restore control: pinning a hidden entry is \
                 refused by `togglePinned`, and the section is rendered outside the sortable \
                 machinery so there is no drag handle either")

        testCase "the narrow rail advertises no pin or hide control" (fun () ->
            // Neither renders at that width, so neither may be a stop —
            // which is exactly why the rail expands on FOCUS as well as
            // hover. Without that there would be no state in which a
            // keyboard user could reach them at all.
            let stops =
                railStops false noSelection [ sectionOf OtherKey None false [ leaf "Sales" ] ]

            Expect.equal
                (stops |> List.collect _.Controls)
                [ RailFocus.reorderKey OtherKey "Sales" ]
                "narrow: only the drag handle survives, because only the drag handle is rendered")

        // ── 612.D — no collision with the Phase 571 palette ──────────

        testCase "any modifier passes the keystroke through — Ctrl+K stays the palette's" (fun () ->
            // The palette binds `Ctrl+K` / `Cmd+K` on a DOCUMENT keydown
            // listener, which fires wherever focus sits, including on a rail
            // row. Bailing on every modifier rather than special-casing "k"
            // is the stronger guard: it also leaves Ctrl+Home, Shift+Tab and
            // any chord a browser or consumer binds later alone, and it
            // cannot go stale if the palette's chord ever changes.
            let stops =
                railStops true noSelection [ sectionOf OtherKey None false [ leaf "Sales" ] ]

            let sales = RailFocus.rowKey OtherKey "Sales"

            for key in [ "k"; "K"; "ArrowDown"; "ArrowRight"; "Home"; "End" ] do
                Expect.equal
                    (pressWithModifier stops sales key)
                    "passthrough"
                    ("a modified `"
                     + key
                     + "` must not be the rail's. The palette's opener is `Ctrl+K` / `Cmd+K` on a \
                        document listener; a rail handler that consumed a modified key would \
                        shadow it from inside the one surface the palette exists to complement.")

            // The unmodified forms are still the rail's — the bail-out is on
            // the modifier, not on the letter.
            Expect.equal
                (press stops sales "ArrowDown")
                "consumed"
                "…while the UNMODIFIED arrow is still handled: this guard keys off the modifier, \
                 not off the key name"

            Expect.equal (press stops sales "k") "passthrough" "and a bare letter was never the rail's anyway")
    ]
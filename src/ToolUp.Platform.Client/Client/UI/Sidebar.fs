// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Toolup.Sidebar

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open SidebarPreferences

// ─── Types ────────────────────────────────────────────────────────

/// Inbound description of a single page of a multi-page module. `Id` is
/// the composite sidebar id (`"{moduleId}{pageRoute}"`) the shell emits —
/// the same id that round-trips through `parseSidebarId` on click, so
/// routing and deep-linking are unchanged by the nested presentation.
type SidebarPageView = {
    Id: string
    Name: string
    Icon: ReactElement
}

/// Inbound description of a module the sidebar should render. The
/// caller (shell) resolves `HasData` against its own processed-data
/// store and `Group` from the module declaration; the sidebar never
/// touches `ErasedModule` directly so it stays UI-focused.
///
/// `Pages` is empty for single-page (and legacy) modules — they render
/// as one leaf entry. A multi-page module carries one `SidebarPageView`
/// per page and renders as a single collapsible parent entry that
/// expands to its pages, rather than one flat rail entry per page.
type SidebarModuleView = {
    Id: string
    Name: string
    Icon: ReactElement
    HasData: bool
    Group: string option
    Pages: SidebarPageView list
}

/// A page of a multi-page module as rendered inside its parent's
/// subtree. `IsPinned` mirrors the user's pinned overlay for this
/// specific page's composite id.
type SidebarPage = {
    Id: string
    Name: string
    Icon: ReactElement
    IsPinned: bool
}

/// A module as rendered inside a section. `IsPinned` mirrors the
/// user's pinned overlay — true for any module whose id appears in
/// `UserSidebarPreferences.PinnedModuleIds`, whether it's rendered
/// inside the pinned section or its home group.
///
/// `Pages` is empty for a leaf (single-page / legacy) module; non-empty
/// for a multi-page parent, whose page children render nested under it.
/// `IsExpanded` is the resolved per-module expand state (from
/// `UserSidebarPreferences.ExpandedModules`); it is meaningful only when
/// `Pages` is non-empty. Pinned pages are lifted into the pinned section
/// and suppressed from `Pages`, mirroring how a pinned module is lifted
/// out of its group.
type SidebarModule = {
    Id: string
    Name: string
    Icon: ReactElement
    HasData: bool
    IsPinned: bool
    Pages: SidebarPage list
    IsExpanded: bool
}

/// A section of the sidebar. `Key` is the stable identifier used
/// for collapse lookups and reorder persistence. Reserved keys:
/// `"_pinned"` (the pinned overlay — always first when non-empty)
/// and `"_other"` (ungrouped modules — always last). Any other
/// value is a module-declared group name.
type SidebarSection = {
    Key: string
    Title: string option
    IsCollapsed: bool
    IsPinnedSection: bool
    Modules: SidebarModule list
}

// ─── Section construction ─────────────────────────────────────────

[<Literal>]
let PinnedKey = "_pinned"

[<Literal>]
let OtherKey = "_other"

/// Reserved key for the always-visible Home section.
[<Literal>]
let HomeKey = "_home"

/// The SDK Home module's reserved id (see `Home.create` →
/// `ClientModule.withId "_sdk.home"`). The sidebar lifts it into a
/// dedicated leading section that is always visible and never
/// collapsed, rather than letting it fall into `_other` at the bottom.
[<Literal>]
let HomeId = "_sdk.home"

/// Phase 567 — reserved sidebar ids for the two-surface area switcher.
/// These flow through the normal `onSelect` (`ModuleSelected`) path; the
/// shell intercepts them to flip `Model.CurrentArea` rather than navigate.
[<Literal>]
let AdminAreaId = "_area.admin"

[<Literal>]
let ProductAreaId = "_area.product"

// Vite's default-export of a non-`?react` asset import is the file's
// built URL — usable directly as an `<img src>`. The shell sidebar
// is permanently dark (slate-900-ish background), so we ship the
// dark-surface brand-mark variant from `Brand Assets/logos/iconset/repo/`
// (`icon-mark-256.png` — light brackets + purple chevron on the brand's
// own dark `#1a1a1a` square; 256×256, ~73 KB; downsamples cleanly to
// the 20px footer slot). The transparent-bg `toolup-forge.png` is kept
// alongside for future light-surface use; once a transparent-bg /
// light-ink variant is authored upstream, this swap can collapse to a
// single `<picture>`-style pair like `Attribution.poweredByBadge`.
let private toolupForgeLogoUrl: string =
    importDefault "../icons/toolup-forge-dark.png"

/// Map an inbound page view to a rendered page, resolving its pinned
/// overlay by the page's composite id.
let private toSidebarPage (pinned: Set<string>) (p: SidebarPageView) : SidebarPage = {
    Id = p.Id
    Name = p.Name
    Icon = p.Icon
    IsPinned = pinned.Contains p.Id
}

/// Map an inbound module view to a rendered module. Individually-pinned
/// pages are filtered out of the parent's subtree — they surface in the
/// pinned section instead, mirroring how a pinned module is lifted out
/// of its home group. `IsExpanded` is resolved from the user's per-module
/// expand overlay.
let private toSidebarModule
    (pinned: Set<string>)
    (expandedModules: Set<string>)
    (m: SidebarModuleView)
    : SidebarModule =
    {
        Id = m.Id
        Name = m.Name
        Icon = m.Icon
        HasData = m.HasData
        IsPinned = pinned.Contains m.Id
        Pages =
            m.Pages
            |> List.filter (fun p -> not (pinned.Contains p.Id))
            |> List.map (toSidebarPage pinned)
        IsExpanded = expandedModules.Contains m.Id
    }

/// Reorder a group's modules by the user's saved order. Modules
/// listed in `order` come first in that order; any remaining modules
/// (newly-registered, or never touched by the user) stay in their
/// original registration order at the tail.
let private applyOrder (order: string list) (modules: SidebarModule list) : SidebarModule list =
    let byId = modules |> List.map (fun m -> m.Id, m) |> Map.ofList
    let ordered = order |> List.choose (fun id -> byId |> Map.tryFind id)
    let remaining = modules |> List.filter (fun m -> not (List.contains m.Id order))
    ordered @ remaining

/// Build the sidebar sections from the modules visible to this user
/// and their personal overlay (pinned / collapsed / ordering).
///
/// Section order:
/// 1. Pinned (only when non-empty) — modules / pages in the user's
///    pinned order, suppressed from their home groups to avoid
///    duplicates.
/// 2. Declared groups — in first-occurrence order across `modules`.
/// 3. `_other` — ungrouped modules, always last.
///
/// A multi-page module renders as one collapsible parent entry (its
/// pages nest under it); single-page modules render as a leaf. Pinning
/// resolves against both bare module ids and composite page ids, so an
/// individually-pinned page (a composite `PinnedModuleIds` entry) still
/// surfaces as its own leaf in the pinned section.
let buildSections (modules: SidebarModuleView list) (prefs: UserSidebarPreferences) : SidebarSection list =
    let pinnedSet = Set.ofList prefs.PinnedModuleIds
    let expandedModules = prefs.ExpandedModules

    // Home is special — always-visible, never grouped, pinned, or
    // collapsed. Lift it into a dedicated leading section before any
    // bucketing so it can't fall into `_other` or hide behind a
    // collapsed group. Everything else feeds the buckets below.
    let homeSection =
        modules
        |> List.tryFind (fun m -> m.Id = HomeId)
        |> Option.map (fun home -> {
            Key = HomeKey
            Title = None
            IsCollapsed = false
            IsPinnedSection = false
            Modules = [ toSidebarModule pinnedSet expandedModules home ]
        })
        |> Option.toList

    let groupable = modules |> List.filter (fun m -> m.Id <> HomeId)
    let byId = groupable |> List.map (fun m -> m.Id, m) |> Map.ofList

    // Composite-page index — maps each multi-page module's page id to
    // its (parent, page) pair, so a pinned individual page (a composite
    // id, not a bare module id) resolves to a synthesized leaf below.
    let pageIndex =
        groupable
        |> List.collect (fun m -> m.Pages |> List.map (fun p -> p.Id, (m, p)))
        |> Map.ofList

    // Resolve one pinned id to a leaf `SidebarModule`. A bare module id
    // resolves to the module rendered flat (no nested expansion inside
    // the pinned section — a multi-page pin navigates to its first
    // page); a composite page id resolves to a synthesized page leaf.
    let resolvePinned (id: string) : SidebarModule option =
        match Map.tryFind id byId with
        | Some m ->
            Some {
                toSidebarModule pinnedSet expandedModules m with
                    Pages = []
                    IsExpanded = false
            }
        | None ->
            match Map.tryFind id pageIndex with
            | Some(parent, page) ->
                Some {
                    Id = page.Id
                    Name = page.Name
                    Icon = page.Icon
                    HasData = parent.HasData
                    IsPinned = true
                    Pages = []
                    IsExpanded = false
                }
            | None -> None

    let pinnedSection =
        let pinnedModules = prefs.PinnedModuleIds |> List.choose resolvePinned

        if List.isEmpty pinnedModules then
            []
        else
            [
                {
                    Key = PinnedKey
                    Title = Some "Pinned"
                    IsCollapsed = not (prefs.ExpandedGroups.Contains PinnedKey)
                    IsPinnedSection = true
                    Modules = pinnedModules
                }
            ]

    // Non-pinned modules — everything not in the pinned set. This is
    // the set that gets partitioned into declared groups + "_other".
    let nonPinned = groupable |> List.filter (fun m -> not (pinnedSet.Contains m.Id))

    // Preserve first-occurrence order of group names across `modules`.
    // Set-based dedupe would lose ordering; fold keeps it.
    let groupOrder =
        nonPinned
        |> List.fold
            (fun (seen: string list, added: Set<string>) m ->
                match m.Group with
                | Some g when not (added.Contains g) -> seen @ [ g ], added.Add g
                | _ -> seen, added)
            ([], Set.empty)
        |> fst

    let declaredSections =
        groupOrder
        |> List.map (fun groupName ->
            let groupModules =
                nonPinned
                |> List.filter (fun m -> m.Group = Some groupName)
                |> List.map (toSidebarModule pinnedSet expandedModules)

            let ordered =
                prefs.ModuleOrder
                |> Map.tryFind groupName
                |> Option.map (fun o -> applyOrder o groupModules)
                |> Option.defaultValue groupModules

            {
                Key = groupName
                Title = Some groupName
                IsCollapsed = not (prefs.ExpandedGroups.Contains groupName)
                IsPinnedSection = false
                Modules = ordered
            })

    let otherSection =
        let others =
            nonPinned
            |> List.filter (fun m -> m.Group = None)
            |> List.map (toSidebarModule pinnedSet expandedModules)

        if List.isEmpty others then
            []
        else
            let ordered =
                prefs.ModuleOrder
                |> Map.tryFind OtherKey
                |> Option.map (fun o -> applyOrder o others)
                |> Option.defaultValue others

            // Suppress the "Other" header when it's the only section —
            // nothing to contrast against. The render layer uses this
            // `Title` directly.
            let title =
                if List.isEmpty pinnedSection && List.isEmpty declaredSections then
                    None
                else
                    Some "Other"

            [
                {
                    Key = OtherKey
                    Title = title
                    IsCollapsed = not (prefs.ExpandedGroups.Contains OtherKey)
                    IsPinnedSection = false
                    Modules = ordered
                }
            ]

    homeSection @ pinnedSection @ declaredSections @ otherSection

/// Flatten a section list to the ordered entry sequence — used by
/// consumers that need to resolve the currently-selected id (e.g. the
/// shell looking up the selected surface's name/icon for the header).
/// Each multi-page parent contributes itself followed by one synthesized
/// leaf per page, so a composite page id resolves to that page's
/// name/icon (the header shows the active *page*, not just its module).
let flatten (sections: SidebarSection list) : SidebarModule list =
    sections
    |> List.collect _.Modules
    |> List.collect (fun m ->
        let pageLeaves =
            m.Pages
            |> List.map (fun p -> {
                Id = p.Id
                Name = p.Name
                Icon = p.Icon
                HasData = m.HasData
                IsPinned = p.IsPinned
                Pages = []
                IsExpanded = false
            })

        m :: pageLeaves)

// ─── dnd-kit bindings ─────────────────────────────────────────────

// Drag-reorder is backed by @dnd-kit — its `useSortable` hook wires
// pointer/keyboard sensors to the wrapped element, and `arrayMove`
// computes the new order on drop. Bindings are kept minimal: only
// the members we actually call are surfaced; anything else is reached
// via the dynamic `?` operator at the call site.
module private DndKit =
    let private core: obj = importAll "@dnd-kit/core"
    let private sortable: obj = importAll "@dnd-kit/sortable"
    let private utilities: obj = importAll "@dnd-kit/utilities"

    let DndContext: obj = core?DndContext
    let PointerSensor: obj = core?PointerSensor
    let KeyboardSensor: obj = core?KeyboardSensor
    let closestCenter: obj = core?closestCenter

    let SortableContext: obj = sortable?SortableContext
    let verticalListSortingStrategy: obj = sortable?verticalListSortingStrategy
    let sortableKeyboardCoordinates: obj = sortable?sortableKeyboardCoordinates

    /// `useSensor(ClassRef, options)` — options is a plain JS object
    /// (e.g. `{| activationConstraint = {| distance = 5 |} |}`). The
    /// returned descriptor is passed to `useSensors`.
    let useSensor (sensorClass: obj) (options: obj) : obj = core?useSensor (sensorClass, options)

    /// `useSensors(sensor1, sensor2)` — dnd-kit takes variadic args.
    /// We only ever pass two (pointer + keyboard), so a fixed-arity
    /// binding is fine.
    let useSensors (sensor1: obj) (sensor2: obj) : obj = core?useSensors (sensor1, sensor2)

    /// `useSortable({ id })` — returns an object with `attributes`,
    /// `listeners`, `setNodeRef`, `transform`, `transition`,
    /// `isDragging`. Called once per sortable item.
    let useSortable (options: obj) : obj = sortable?useSortable (options)

    /// `arrayMove(arr, from, to)` — pure, returns a new array.
    let arrayMove (arr: string[]) (fromIdx: int) (toIdx: int) : string[] =
        sortable?arrayMove (arr, fromIdx, toIdx)

    /// CSS helper namespace. `CSS.Transform.toString(transform)` takes
    /// the `transform` object dnd-kit hands back (or null) and produces
    /// the inline `transform: translate3d(...)` string used on the
    /// dragging element.
    let transformToString (transform: obj) : string =
        utilities?CSS?Transform?toString (transform)

/// Merge dnd-kit's spread-style `attributes` and `listeners` objects
/// onto a plain props object alongside the sortable ref, the inline
/// style, and a className. Object.assign preserves dnd-kit's own
/// `role` / `tabIndex` / event handlers while our explicit keys win
/// last (ref, style, className). Needed because Feliz's `Html.div`
/// has no native "spread arbitrary props" affordance.
[<Emit("Object.assign({}, $0, $1, { ref: $2, style: $3, className: $4 })")>]
let private mergeSortableProps (attributes: obj) (listeners: obj) (setRef: obj) (style: obj) (className: string) : obj =
    jsNative

// ─── Rendering ────────────────────────────────────────────────────

/// Chevron glyph for collapsible section headers. Rotated 90° when
/// collapsed so it points right instead of down.
let private chevron (isCollapsed: bool) =
    Svg.svg [
        svg.className [
            "w-3 h-3 transition-transform"
            if isCollapsed then
                "-rotate-90"
        ]
        svg.fill "none"
        svg.stroke "currentColor"
        svg.viewBox (0, 0, 24, 24)
        svg.children [
            Svg.path [
                svg.custom ("strokeLinecap", "round")
                svg.custom ("strokeLinejoin", "round")
                svg.strokeWidth 2
                svg.d "M19 9l-7 7-7-7"
            ]
        ]
    ]

/// Pin glyph. Filled when pinned, outlined when not — the fill/stroke
/// pair keeps both states crisp at small sizes without a second path.
let private pinIcon (isPinned: bool) =
    Svg.svg [
        svg.className "w-4 h-4"
        svg.fill (if isPinned then "currentColor" else "none")
        svg.stroke "currentColor"
        svg.viewBox (0, 0, 24, 24)
        svg.children [
            Svg.path [
                svg.custom ("strokeLinecap", "round")
                svg.custom ("strokeLinejoin", "round")
                svg.strokeWidth 2
                svg.d
                    "M12 17v5M5 17h14v-1.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V6h1a2 2 0 0 0 0-4H8a2 2 0 0 0 0 4h1v4.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24Z"
            ]
        ]
    ]

/// One clickable sidebar row — the shared button + hover-revealed pin
/// affordance used by leaf modules, multi-page parents, and page
/// children alike. `leading` is an optional glyph rendered before the
/// icon (the expand chevron on a multi-page parent, shown only when the
/// sidebar is hover-expanded); `indent` insets a page child under its
/// parent; `pinnable = false` suppresses the pin control (Home). The pin
/// affordance is the sole unpin control in the pinned section: it shows
/// the filled glyph + "Unpin" and stays visible, and clicking it fires
/// before the unpin re-render, so the row dropping back to its home
/// group is the expected result, not a lost click.
let private renderRow
    (isExpanded: bool)
    (isSelected: bool)
    (hasData: bool)
    (pinnable: bool)
    (isPinned: bool)
    (indent: bool)
    (leading: ReactElement option)
    (icon: ReactElement)
    (name: string)
    (onActivate: unit -> unit)
    (onPinToggled: unit -> unit)
    =
    Html.div [
        prop.className "relative group"
        prop.children [
            Html.button [
                prop.className [
                    // `bg-transparent` is load-bearing: a consumer app's
                    // global `button { background: … }` in its index.css
                    // (element selector) would otherwise paint every
                    // sidebar entry, since these buttons carry no other
                    // `bg-*` class. A Tailwind class selector outranks an
                    // element selector, so this keeps the sidebar flush
                    // on `bg-sidebar` regardless of consumer CSS. Don't
                    // remove it.
                    "w-full flex items-center py-3 text-white transition-colors rounded-[var(--radius)] bg-transparent"
                    "hover:bg-white/5"
                    if isExpanded then
                        (if indent then "pl-8 pr-3" else "px-3")
                    else
                        "justify-center"
                    if isSelected then
                        "border-2 border-brand"
                    else
                        "border-2 border-transparent"
                ]
                prop.onClick (fun _ -> onActivate ())
                prop.children [
                    // Expand chevron (multi-page parent) — only in the
                    // hover-expanded rail; the narrow rail has no room.
                    match leading with
                    | Some glyph when isExpanded -> glyph
                    | _ -> Html.none
                    Html.div [
                        // Icons are vite-plugin-svgr-imported React components.
                        // Their stroke uses `currentColor`, so the parent's
                        // CSS color cascades — `text-brand` when the module
                        // has data, `text-white` otherwise. The `[&>svg]`
                        // child selector pins the inlined `<svg>` to 32×32
                        // regardless of the SVG source's intrinsic
                        // dimensions (svgr's `dimensions: false` option
                        // strips XML width/height too).
                        prop.className [
                            "w-8 flex items-center justify-center flex-shrink-0"
                            "[&>svg]:w-8 [&>svg]:h-8"
                            if hasData then "text-brand" else "text-white"
                        ]
                        prop.children [ icon ]
                    ]
                    if isExpanded then
                        Html.span [
                            prop.className "ml-3 text-base font-medium leading-tight flex-1 text-left"
                            prop.text name
                        ]
                ]
            ]

            // Pin toggle — hover-revealed when the sidebar is expanded.
            // Absolute-positioned so it overlays the button without
            // shifting layout; pointer-events-auto inside a pointer-
            // events-none parent would also work but this is simpler.
            if isExpanded && pinnable then
                Html.button [
                    prop.className [
                        // `bg-transparent` — same consumer-global-button
                        // defence as the row above.
                        "absolute right-2 top-1/2 -translate-y-1/2 p-1 rounded bg-transparent"
                        "text-white/40 hover:text-white hover:bg-white/10"
                        // Always visible once pinned; hover-only otherwise
                        // so the sidebar stays visually uncluttered.
                        if isPinned then
                            "opacity-100"
                        else
                            "opacity-0 group-hover:opacity-100 transition-opacity"
                    ]
                    prop.title (if isPinned then "Unpin" else "Pin")
                    prop.onClick (fun e ->
                        e.stopPropagation ()
                        onPinToggled ())
                    prop.children [ pinIcon isPinned ]
                ]
        ]
    ]

/// Render a module entry. A leaf (single-page / legacy) module is one
/// clickable row. A multi-page module is a parent row that, in the
/// hover-expanded rail, toggles its page subtree open/closed and reveals
/// one child row per page; in the narrow (w-20) rail — where there is no
/// room for a subtree — clicking the parent navigates to its first page
/// (the composite id round-trips through the shell's `parseSidebarId`,
/// so routing is unchanged). The module owning the active page is
/// force-expanded so a deep-linked page is always visible; while
/// expanded the active child carries the selection border, so the parent
/// only takes the border in the narrow rail (where children are hidden).
let private renderModuleButton
    (isExpanded: bool)
    (selectedModule: string)
    (onModuleSelected: string -> unit)
    (onPinToggled: string -> unit)
    (onModuleToggled: string -> unit)
    (m: SidebarModule)
    =
    let pinnable = m.Id <> HomeId

    if List.isEmpty m.Pages then
        renderRow
            isExpanded
            (m.Id = selectedModule)
            m.HasData
            pinnable
            m.IsPinned
            false
            None
            m.Icon
            m.Name
            (fun () -> onModuleSelected m.Id)
            (fun () -> onPinToggled m.Id)
    else
        let containsSelected = m.Pages |> List.exists (fun p -> p.Id = selectedModule)
        // Auto-expand the module that owns the active page so it's always
        // reachable, regardless of the persisted collapse state.
        let effectiveExpanded = m.IsExpanded || containsSelected
        // In the wide rail the active child shows the border; only the
        // narrow rail (children hidden) highlights the parent for a
        // contained selection.
        let parentSelected = (m.Id = selectedModule) || (containsSelected && not isExpanded)

        let parentActivate () =
            if isExpanded then
                onModuleToggled m.Id
            else
                onModuleSelected m.Id

        Html.div [
            prop.children [
                renderRow
                    isExpanded
                    parentSelected
                    m.HasData
                    pinnable
                    m.IsPinned
                    false
                    (Some(chevron (not effectiveExpanded)))
                    m.Icon
                    m.Name
                    parentActivate
                    (fun () -> onPinToggled m.Id)

                if isExpanded && effectiveExpanded then
                    for p in m.Pages do
                        Html.div [
                            prop.key p.Id
                            prop.children [
                                renderRow
                                    isExpanded
                                    (p.Id = selectedModule)
                                    m.HasData
                                    true
                                    p.IsPinned
                                    true
                                    None
                                    p.Icon
                                    p.Name
                                    (fun () -> onModuleSelected p.Id)
                                    (fun () -> onPinToggled p.Id)
                            ]
                        ]
            ]
        ]

/// Sortable wrapper for a single module row. Calls `useSortable` to
/// register the element with the enclosing `SortableContext`, then
/// splats dnd-kit's `attributes` / `listeners` onto a div so pointer
/// and keyboard events drive the drag. Hooks run inside a component
/// so their order stays stable when the parent re-renders with a
/// reordered module list.
///
/// Click-vs-drag is disambiguated by the `PointerSensor`'s
/// `activationConstraint: { distance: 5 }` — a drag doesn't start
/// until the pointer has moved 5px, so clicks on the row (module
/// selection) and on the nested pin button fire normally.
[<ReactComponent>]
let private SortableItem (id: string) (child: ReactElement) =
    let sortable = DndKit.useSortable (createObj [ "id" ==> id ])

    let transform = sortable?transform
    let transition: string = sortable?transition
    let isDragging: bool = sortable?isDragging

    let style =
        createObj [
            "transform" ==> DndKit.transformToString transform
            "transition" ==> transition
            "opacity" ==> (if isDragging then 0.5 else 1.0)
            "zIndex" ==> (if isDragging then 50 else 0)
            "position" ==> "relative"
        ]

    let props =
        mergeSortableProps sortable?attributes sortable?listeners sortable?setNodeRef style "cursor-grab"

    ReactLegacy.createElement ("div", props, [| child |])

/// Section header — clickable, toggles collapse. Rendered only when
/// the sidebar is expanded; the narrow (w-20) form is icons-only so
/// headers would have nowhere meaningful to live. Pinned section
/// shows a filled pin glyph instead of a text title.
let private renderSectionHeader (onGroupToggled: string -> unit) (section: SidebarSection) (title: string) =
    Html.button [
        prop.key (section.Key + "__header")
        prop.className [
            // `bg-transparent` — section headers are <button> too;
            // same consumer-global-button defence as the module rows.
            "w-full flex items-center gap-2 px-2 pt-3 pb-1 bg-transparent"
            "text-white/50 hover:text-white/80 text-xs uppercase tracking-wider font-semibold"
            "transition-colors"
        ]
        prop.onClick (fun _ -> onGroupToggled section.Key)
        prop.children [
            chevron section.IsCollapsed
            if section.IsPinnedSection then
                Html.span [ prop.className "w-3 h-3 text-brand"; prop.children [ pinIcon true ] ]
            Html.span [ prop.text title ]
        ]
    ]

/// Sidebar. Renders the always-visible Home entry first, then a pinned
/// overlay (if any pinned modules exist), each declared group as a
/// collapsible section, and the "_other" section last. Section headers
/// appear only when the sidebar is hover-expanded; in narrow mode (w-20)
/// the sidebar is icons-only and mirrors per-section collapse state — a
/// collapsed group shows a single default icon (its lead module's,
/// click-to-expand) instead of its full icon set, while Home and the
/// pinned overlay always stay fully visible.
///
/// Drag-to-reorder is scoped per-section and operates on top-level
/// modules only: each section wraps its module rows in a
/// `SortableContext` keyed by bare module id, and dropping across
/// sections is ignored (the `onDragEnd` handler looks up both ids in the
/// same section or no-ops). A multi-page module's page children are not
/// sortable items — page order is fixed. The full sidebar sits inside a
/// single `DndContext` so there's one set of sensors for the whole
/// surface.
///
/// Multi-page modules render as one collapsible parent entry whose pages
/// nest beneath it (`onModuleToggled` persists the per-module expand
/// state); single-page modules render as a leaf.
[<ReactComponent>]
let Sidebar
    (appName: string)
    (appLogo: string)
    (sections: SidebarSection list)
    (selectedModule: string)
    (onModuleSelected: string -> unit)
    (onGroupToggled: string -> unit)
    (onPinToggled: string -> unit)
    (onModuleToggled: string -> unit)
    (onReorder: string -> string list -> unit)
    =
    let isExpanded, setIsExpanded = React.useState false

    // Pointer sensor with a 5px activation threshold so short clicks
    // still fire module selection and pin-toggle handlers — dnd-kit
    // only starts a drag once the pointer has moved that far. Keyboard
    // sensor makes reordering reachable without a mouse.
    let pointerSensor =
        DndKit.useSensor DndKit.PointerSensor (createObj [ "activationConstraint" ==> createObj [ "distance" ==> 5 ] ])

    let keyboardSensor =
        DndKit.useSensor DndKit.KeyboardSensor (createObj [ "coordinateGetter" ==> DndKit.sortableKeyboardCoordinates ])

    let sensors = DndKit.useSensors pointerSensor keyboardSensor

    // Resolve which section contains a given id (or None). Pinned
    // modules live only in the pinned section; declared-group
    // modules live only in their group. No duplicates across sections.
    let sectionContaining (id: string) =
        sections
        |> List.tryFind (fun s -> s.Modules |> List.exists (fun m -> m.Id = id))

    let handleDragEnd (event: obj) =
        let active = event?active
        let over = event?over

        if not (isNullOrUndefined over) then
            let activeId: string = active?id
            let overId: string = over?id

            if activeId <> overId then
                match sectionContaining activeId, sectionContaining overId with
                | Some activeSection, Some overSection when activeSection.Key = overSection.Key ->
                    let ids = activeSection.Modules |> List.map _.Id
                    let idsArr = List.toArray ids
                    let fromIdx = Array.findIndex ((=) activeId) idsArr
                    let toIdx = Array.findIndex ((=) overId) idsArr
                    let reordered = DndKit.arrayMove idsArr fromIdx toIdx
                    onReorder activeSection.Key (Array.toList reordered)
                | _ -> ()

    let dndContextProps =
        createObj [
            "sensors" ==> sensors
            "collisionDetection" ==> DndKit.closestCenter
            "onDragEnd" ==> handleDragEnd
        ]

    let renderSectionModules (section: SidebarSection) =
        let sortableContextProps =
            createObj [
                "items" ==> (section.Modules |> List.map _.Id |> List.toArray)
                "strategy" ==> DndKit.verticalListSortingStrategy
            ]

        let items =
            section.Modules
            |> List.map (fun m ->
                let button =
                    renderModuleButton isExpanded selectedModule onModuleSelected onPinToggled onModuleToggled m

                React.KeyedFragment(m.Id, [ SortableItem m.Id button ]))

        let inner = Html.div [ prop.className "space-y-1"; prop.children items ]

        ReactLegacy.createElement (unbox<ReactElement> DndKit.SortableContext, sortableContextProps, [| inner |])

    // Single representative icon for a collapsed group in the narrow
    // (at-rest) rail — the group's "default icon", derived from its lead
    // module so each group reads distinctly. Clicking toggles the group
    // open (also the only way to drive collapse state on touch, where
    // there's no hover-to-expand). Rendered muted so it reads as a group
    // affordance rather than an active module entry.
    let renderCollapsedGroupIcon (section: SidebarSection) =
        match section.Modules with
        | [] -> Html.none
        | lead :: _ ->
            Html.button [
                prop.key (section.Key + "__groupicon")
                prop.className [
                    "w-full flex items-center justify-center py-3 transition-colors rounded-[var(--radius)] bg-transparent"
                    "text-white/60 hover:text-white hover:bg-white/5 border-2 border-transparent"
                ]
                prop.title (section.Title |> Option.defaultValue "")
                prop.onClick (fun _ -> onGroupToggled section.Key)
                prop.children [
                    Html.div [
                        prop.className "w-8 flex items-center justify-center flex-shrink-0 [&>svg]:w-8 [&>svg]:h-8"
                        prop.children [ lead.Icon ]
                    ]
                ]
            ]

    let moduleList =
        Html.div [
            prop.className "flex-1 py-2 px-4 overflow-y-auto"
            prop.children [
                for section in sections do
                    if isExpanded then
                        // Expanded: section header (when titled) + the
                        // module rows when the section isn't collapsed.
                        match section.Title with
                        | Some title -> renderSectionHeader onGroupToggled section title
                        | None -> ()

                        if not section.IsCollapsed then
                            React.KeyedFragment(section.Key + "__body", [ renderSectionModules section ])
                    else
                        // Narrow (at-rest) rail. Home + the pinned overlay
                        // stay fully visible; every other group collapses to
                        // its single default icon, expanding to its module
                        // icons when the user opens it.
                        let alwaysVisible = section.IsPinnedSection || section.Key = HomeKey

                        if alwaysVisible || not section.IsCollapsed then
                            React.KeyedFragment(section.Key + "__body", [ renderSectionModules section ])
                        else
                            React.KeyedFragment(section.Key + "__collapsed", [ renderCollapsedGroupIcon section ])
            ]
        ]

    let wrappedList =
        ReactLegacy.createElement (unbox<ReactElement> DndKit.DndContext, dndContextProps, [| moduleList |])

    Html.aside [
        prop.className [
            "h-full bg-sidebar transition-all duration-300 flex flex-col absolute left-0 top-0 z-50"
            if isExpanded then "w-64 shadow-2xl" else "w-20"
        ]
        prop.onMouseEnter (fun _ -> setIsExpanded true)
        prop.onMouseLeave (fun _ -> setIsExpanded false)
        prop.children [
            // Logo section at top
            Html.div [
                prop.className [
                    "h-16 flex items-center border-b border-white/10"
                    if isExpanded then "px-7" else "justify-center"
                ]
                prop.children [
                    Html.div [
                        prop.className "w-8 h-8 flex-shrink-0"
                        prop.children [ Html.img [ prop.src appLogo ] ]
                    ]
                    if isExpanded then
                        Html.span [
                            // Unquoted arbitrary font family. The quoted form
                            // `font-['Umami']` compiles through Fable to a JS
                            // string literal whose inner single quotes get
                            // backslash-escaped (`font-[\'Umami\']`); Tailwind
                            // v4's `@source` scanner reads the raw .js bytes
                            // and the escaped quotes defeat its arbitrary-value
                            // extractor, so the rule is never generated and the
                            // logo silently falls back to the body font. The
                            // unquoted form has no quotes to escape, so it
                            // scans reliably regardless of how Fable emits the
                            // string. Single-word family only — multi-word
                            // would need underscores (`font-[Some_Font]`).
                            prop.className "ml-3 text-white text-xl font-bold font-[Umami]"
                            prop.text appName
                        ]
                ]
            ]

            // Module list — sections rendered in order with optional
            // headers, per-section collapse, and cross-section DndContext.
            wrappedList

            // Powered by ToolUp-Forge — click-through to https://toolup-forge.io
            Html.a [
                prop.href "https://toolup-forge.io"
                prop.target "_blank"
                prop.rel "noopener noreferrer"
                prop.className [
                    "py-3 border-t border-white/10 flex items-center hover:bg-white/5 transition-colors"
                    if isExpanded then "px-7" else "justify-center"
                ]
                prop.children [
                    Html.img [
                        prop.src toolupForgeLogoUrl
                        prop.alt "ToolUp-Forge"
                        prop.className "w-5 h-5 flex-shrink-0 object-contain"
                        // Defensive inline sizing — guarantees the 20×20
                        // footer slot even when a consumer's Tailwind
                        // purge config drops `w-5` / `h-5`. The 1024×1024
                        // master downsamples cleanly to 20px via
                        // `object-contain`.
                        prop.style [
                            style.custom ("width", "20px")
                            style.custom ("height", "20px")
                            style.custom ("maxWidth", "20px")
                            style.custom ("maxHeight", "20px")
                        ]
                    ]
                    if isExpanded then
                        Html.span [
                            prop.className "ml-2 text-white/40 text-xs"
                            prop.text "Powered by ToolUp-Forge"
                        ]
                ]
            ]
        ]
    ]
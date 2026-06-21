// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Toolup.Sidebar

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open SidebarPreferences

// ─── Types ────────────────────────────────────────────────────────

/// Inbound description of a module the sidebar should render. The
/// caller (shell) resolves `HasData` against its own processed-data
/// store and `Group` from the module declaration; the sidebar never
/// touches `ErasedModule` directly so it stays UI-focused.
type SidebarModuleView = {
    Id: string
    Name: string
    Icon: ReactElement
    HasData: bool
    Group: string option
}

/// A module as rendered inside a section. `IsPinned` mirrors the
/// user's pinned overlay — true for any module whose id appears in
/// `UserSidebarPreferences.PinnedModuleIds`, whether it's rendered
/// inside the pinned section or its home group.
type SidebarModule = {
    Id: string
    Name: string
    Icon: ReactElement
    HasData: bool
    IsPinned: bool
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

let private toSidebarModule (pinned: Set<string>) (m: SidebarModuleView) : SidebarModule = {
    Id = m.Id
    Name = m.Name
    Icon = m.Icon
    HasData = m.HasData
    IsPinned = pinned.Contains m.Id
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
/// 1. Pinned (only when non-empty) — modules in the user's pinned
///    order, suppressed from their home groups to avoid duplicates.
/// 2. Declared groups — in first-occurrence order across `modules`.
/// 3. `_other` — ungrouped modules, always last.
let buildSections (modules: SidebarModuleView list) (prefs: UserSidebarPreferences) : SidebarSection list =
    let pinnedSet = Set.ofList prefs.PinnedModuleIds
    let byId = modules |> List.map (fun m -> m.Id, m) |> Map.ofList

    let pinnedSection =
        let pinnedModules =
            prefs.PinnedModuleIds
            |> List.choose (fun id -> byId |> Map.tryFind id)
            |> List.map (toSidebarModule pinnedSet)

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
    let nonPinned = modules |> List.filter (fun m -> not (pinnedSet.Contains m.Id))

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
                |> List.map (toSidebarModule pinnedSet)

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
            |> List.map (toSidebarModule pinnedSet)

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

    pinnedSection @ declaredSections @ otherSection

/// Flatten a section list to the ordered module id sequence — used
/// by consumers that need to resolve the currently-selected id
/// (e.g. the shell looking up the selected module's name/icon).
let flatten (sections: SidebarSection list) : SidebarModule list = sections |> List.collect _.Modules

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

/// Render a single module button. Shared between the pinned section
/// and declared groups. The pin affordance renders in every section,
/// the pinned one included: a pinned module renders *only* in the
/// pinned section (declared groups filter the pinned set out), so the
/// affordance there is the sole unpin control. In that section the
/// button shows the filled glyph + "Unpin" title and is always visible;
/// clicking it fires before the unpin re-render, so the row dropping
/// back to its home group is the expected result, not a lost click.
let private renderModuleButton
    (isExpanded: bool)
    (selectedModule: string)
    (onModuleSelected: string -> unit)
    (onPinToggled: string -> unit)
    (m: SidebarModule)
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
                    if isExpanded then "px-3" else "justify-center"
                    if m.Id = selectedModule then
                        "border-2 border-brand"
                    else
                        "border-2 border-transparent"
                ]
                prop.onClick (fun _ -> onModuleSelected m.Id)
                prop.children [
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
                            if m.HasData then "text-brand" else "text-white"
                        ]
                        prop.children [ m.Icon ]
                    ]
                    if isExpanded then
                        Html.span [
                            prop.className "ml-3 text-base font-medium leading-tight flex-1 text-left"
                            prop.text m.Name
                        ]
                ]
            ]

            // Pin toggle — hover-revealed when the sidebar is expanded.
            // Absolute-positioned so it overlays the button without
            // shifting layout; pointer-events-auto inside a pointer-
            // events-none parent would also work but this is simpler.
            if isExpanded then
                Html.button [
                    prop.className [
                        // `bg-transparent` — same consumer-global-button
                        // defence as the module row above.
                        "absolute right-2 top-1/2 -translate-y-1/2 p-1 rounded bg-transparent"
                        "text-white/40 hover:text-white hover:bg-white/10"
                        // Always visible once pinned; hover-only otherwise
                        // so the sidebar stays visually uncluttered.
                        if m.IsPinned then
                            "opacity-100"
                        else
                            "opacity-0 group-hover:opacity-100 transition-opacity"
                    ]
                    prop.title (if m.IsPinned then "Unpin" else "Pin")
                    prop.onClick (fun e ->
                        e.stopPropagation ()
                        onPinToggled m.Id)
                    prop.children [ pinIcon m.IsPinned ]
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

/// Sidebar. Renders a pinned overlay (if any pinned modules exist),
/// each declared group as a collapsible section, and the "_other"
/// section last. Section headers appear only when the sidebar is
/// hover-expanded; in narrow mode (w-20) the sidebar is icons-only
/// but still respects per-section collapse state.
///
/// Drag-to-reorder is scoped per-section: each section wraps its
/// modules in a `SortableContext`, and dropping across sections is
/// ignored (the `onDragEnd` handler looks up both ids in the same
/// section or no-ops). The full sidebar sits inside a single
/// `DndContext` so there's one set of sensors for the whole surface.
[<ReactComponent>]
let Sidebar
    (appName: string)
    (appLogo: string)
    (sections: SidebarSection list)
    (selectedModule: string)
    (onModuleSelected: string -> unit)
    (onGroupToggled: string -> unit)
    (onPinToggled: string -> unit)
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
                    renderModuleButton isExpanded selectedModule onModuleSelected onPinToggled m

                React.KeyedFragment(m.Id, [ SortableItem m.Id button ]))

        let inner = Html.div [ prop.className "space-y-1"; prop.children items ]

        ReactLegacy.createElement (unbox<ReactElement> DndKit.SortableContext, sortableContextProps, [| inner |])

    let moduleList =
        Html.div [
            prop.className "flex-1 py-2 px-4 overflow-y-auto"
            prop.children [
                for section in sections do
                    // Header — only visible when sidebar is expanded
                    // and the section declares a title.
                    match isExpanded, section.Title with
                    | true, Some title -> renderSectionHeader onGroupToggled section title
                    | _ -> ()

                    // Modules — hidden entirely when section is
                    // collapsed, even in narrow mode (consistency
                    // across widths avoids surprise when the user
                    // expands the sidebar and sees a different set).
                    if not section.IsCollapsed then
                        React.KeyedFragment(section.Key + "__body", [ renderSectionModules section ])
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
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

/// Phase 611 — **where a rail row sits, as a declared value.** The single
/// definition site for rail placement; `ClientModule.withPlacement` is how
/// a module declares one, and `SidebarModuleView.Placement` carries it in.
///
/// Placement used to be an *implicit consequence of a field that means
/// something else*: `Group = None` was read by `buildSections` as "bottom of
/// the rail, inside the collapsed `_other` catch-all". Nothing declared that
/// — it fell out of the bucketing, and the two landings escaped it only
/// because the fold special-cased their literal ids. The Phase 567
/// area-switcher rows never got that special case, so the only route *into*
/// the administration area sat in a section that renders last and is
/// collapsed on a fresh profile: the one row whose whole purpose is to be
/// found. Every reserved row added since would have inherited the same
/// fallback silently.
///
/// **`Group = None` no longer implies a position.** It means what it says —
/// no declared group — and therefore the `_other` bucket *within* the
/// grouped slot. Where a row sits is this value and nothing else.
///
/// The three slots, in render order:
///
/// * `LeadingSlot` — the always-visible leading section (`HomeKey`),
///   rendered first, never collapsed, in both rail widths. The two landing
///   modules declare it: a landing is where "you are here" starts.
/// * `GroupedSlot` — ordinary bucketing: the row's declared `Group`, or the
///   `OtherKey` catch-all when it declares none. Per-section collapse,
///   pinning, hiding and drag-reorder all apply. **An undeclared row
///   resolves here**, so a composition that declares nothing behaves exactly
///   as it did before this type existed (GP 11).
/// * `TrailingSlot` — the always-visible trailing section (`TrailingKey`),
///   rendered after every grouped section, never collapsed, in both rail
///   widths. For a row that must stay reachable but is not a destination:
///   the two area switchers declare it, because a mode switch belongs at the
///   foot of the rail rather than competing with the landing at its head.
///
/// A row in a placed slot cannot be relocated by a user preference — that
/// is the point of declaring it — so `buildSections` never lifts one into
/// the pinned overlay, and the render layer suppresses the pin affordance
/// for it rather than offering a click that would do nothing.
type SidebarPlacement =
    | LeadingSlot
    | GroupedSlot
    | TrailingSlot

/// Inbound description of a module the sidebar should render. The
/// caller (shell) resolves `HasData` against its own processed-data
/// store and `Group` / `Placement` from the module declaration; the
/// sidebar never touches `ErasedModule` directly so it stays UI-focused.
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
    /// Phase 611 — the row's declared rail slot. `None` ⇒ `GroupedSlot`,
    /// which is the bucketing every row got before this field existed, so an
    /// existing composition is unchanged (GP 11). See `SidebarPlacement`.
    Placement: SidebarPlacement option
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
/// `"_home"` (the leading placed section), `"_pinned"` (the pinned
/// overlay — first among the grouped sections when non-empty),
/// `"_other"` (ungrouped modules — last of the grouped sections),
/// `"_trailing"` (the trailing placed section) and `"_hidden"` (the
/// reveal list, rendered after every rail section). Any other value
/// is a module-declared group name.
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

/// Reserved key for the always-visible Home section — the section
/// `SidebarPlacement.LeadingSlot` resolves to.
[<Literal>]
let HomeKey = "_home"

/// Phase 611 — reserved key for the always-visible trailing section, the
/// one `SidebarPlacement.TrailingSlot` resolves to. Rendered after every
/// grouped section (and before the `HiddenKey` reveal list, which stays
/// last), never collapsed, present in both rail widths. Untitled, like
/// `HomeKey`: a one-or-two-row placed section has nothing to head.
[<Literal>]
let TrailingKey = "_trailing"

/// Phase 572 — reserved key for the "Hidden items" section: the reveal
/// surface listing the entries this user has hidden, each restorable in
/// one click. Rendered last and only when non-empty, collapsed unless the
/// user opens it (like any other section, via `ExpandedGroups`), and
/// absent from the narrow rail — a list whose whole purpose is to be read
/// has nothing to say as an icon.
[<Literal>]
let HiddenKey = "_hidden"

/// The SDK Home module's reserved id (see `Home.create` →
/// `ClientModule.withId "_sdk.home"`). The sidebar lifts it into a
/// dedicated leading section that is always visible and never
/// collapsed, rather than letting it fall into `_other` at the bottom.
[<Literal>]
let HomeId = "_sdk.home"

/// Phase 573 — the Administration area's landing module id (see
/// `AdminHome.create`). Lifted into the SAME leading section as `HomeId`
/// below, which is what makes it first in the admin rail without anyone
/// maintaining a sort order. At most one of the two is ever in the rail
/// set at a time: under `AdminSurface = SeparateArea` the shell renders
/// one area's modules, and this module belongs to the other one.
[<Literal>]
let AdminHomeId = "_sdk.admin-home"

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

/// Ids that can never be hidden, whatever the persisted blob says: the
/// two landing modules (the shell's guaranteed landing in each area) and
/// the two area-switcher rows (the only way back out of the
/// administration area). A hand-edited localStorage blob naming one of
/// these would otherwise strand the user with no route home and no
/// visible way to restore it — the hidden-items section itself lives in
/// the rail these ids anchor.
///
/// Phase 611 note: hideability and placement are deliberately separate
/// axes, which is why this id set survived the placement model. A row
/// declaring `LeadingSlot` / `TrailingSlot` says where it renders; this
/// says it may not be removed from the rail at all. A consumer row may
/// well want the second without the first (a placed row it is happy for
/// the user to hide), so deriving one from the other would take a choice
/// away rather than remove a special case.
let private isHideableId (id: string) =
    id <> HomeId && id <> AdminHomeId && id <> AdminAreaId && id <> ProductAreaId

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
/// Section order (Phase 611 — the first and fourth are the *declared*
/// slots; the middle three are the grouped slot's internal arrangement):
/// 1. `_home` — rows declaring `LeadingSlot`, always visible, never
///    collapsed.
/// 2. Pinned (only when non-empty) — modules / pages in the user's
///    pinned order, suppressed from their home groups to avoid
///    duplicates.
/// 3. Declared groups — in first-occurrence order across `modules`.
/// 4. `_other` — grouped modules declaring no group, last of the
///    grouped sections.
/// 5. `_trailing` — rows declaring `TrailingSlot`, always visible, never
///    collapsed.
/// 6. `_hidden` — the reveal list, after every rail section.
///
/// A multi-page module renders as one collapsible parent entry (its
/// pages nest under it); single-page modules render as a leaf. Pinning
/// resolves against both bare module ids and composite page ids, so an
/// individually-pinned page (a composite `PinnedModuleIds` entry) still
/// surfaces as its own leaf in the pinned section.
///
/// **Hiding (Phase 572) is applied here, and only here.** `modules` is
/// the ALREADY access-filtered set — `SidebarVisibility.visible` ran at
/// the shell's call site — so removing a hidden entry at this point
/// cannot widen anything: a personal preference subtracts from what
/// access already allowed and can never add to it. That ordering is the
/// whole safety argument, and it is why hiding lives in this fold rather
/// than in `SidebarVisibility`, which stays a pure access decision that
/// the route guard and the command palette also derive from. A hidden
/// entry keeps its route and its palette listing; it loses only its row.
let buildSections (modules: SidebarModuleView list) (prefs: UserSidebarPreferences) : SidebarSection list =
    let pinnedSet = Set.ofList prefs.PinnedModuleIds
    let expandedModules = prefs.ExpandedModules

    // Null-coerced (a legacy blob's missing list deserialises to `null`)
    // and stripped of the never-hideable reserved ids.
    let hiddenSet = hiddenIds prefs |> Set.filter isHideableId

    // Index the FULL inbound set BEFORE hiding subtracts from it: the
    // hidden-items section has to name what it is offering to restore,
    // and a hidden entry is by definition absent from the rail set below.
    let allById = modules |> List.map (fun m -> m.Id, m) |> Map.ofList

    let allPageIndex =
        modules
        |> List.collect (fun m -> m.Pages |> List.map (fun p -> p.Id, (m, p)))
        |> Map.ofList

    // The rail set — the access-filtered modules minus this user's hidden
    // entries, at both granularities: a hidden bare module id drops the
    // whole module, a hidden composite page id drops that page from its
    // parent's subtree and leaves its siblings.
    let rail =
        modules
        |> List.filter (fun m -> not (hiddenSet.Contains m.Id))
        |> List.map (fun m -> {
            m with
                Pages = m.Pages |> List.filter (fun p -> not (hiddenSet.Contains p.Id))
        })

    // Phase 611 — a row's slot is DECLARED, not inferred. `None` resolves
    // to `GroupedSlot`, which is what every row got before the field
    // existed, so an undeclared composition buckets exactly as before
    // (GP 11). This total function is the whole of the placement decision:
    // the fold names no id, so a reserved row added tomorrow cannot
    // silently inherit the `_other` fallback the way the Phase 567 area
    // switchers did — it either declares a slot or it is grouped, and
    // either way that is visible at its construction site.
    let slotOf (m: SidebarModuleView) =
        m.Placement |> Option.defaultValue GroupedSlot

    // The two placed sections are lifted out before any bucketing, so
    // neither can fall into `_other` or hide behind a collapsed group.
    //
    // Phase 573 — there are two landings, one per navigation area, and
    // they share the leading section: under `AdminSurface = SeparateArea`
    // the shell hands this fold one area's modules at a time, so the list
    // is usually a single member; under `InlineGroups` the admin landing is
    // never registered at all. Filtering (rather than `tryFind`-ing one
    // row) keeps that an observation about the caller rather than an
    // invariant this fold would break if it changed.
    let placedSection (key: string) (slot: SidebarPlacement) =
        match rail |> List.filter (fun m -> slotOf m = slot) with
        | [] -> []
        | placed -> [
            {
                Key = key
                Title = None
                IsCollapsed = false
                IsPinnedSection = false
                Modules = placed |> List.map (toSidebarModule pinnedSet expandedModules)
            }
          ]

    let homeSection = placedSection HomeKey LeadingSlot
    let trailingSection = placedSection TrailingKey TrailingSlot

    // Everything not placed feeds the buckets below. A placed row is
    // therefore absent from `byId` / `pageIndex`, which is what makes its
    // position immune to a user preference: `resolvePinned` cannot resolve
    // it, so pinning it (from a hand-edited blob, say) moves nothing.
    let groupable = rail |> List.filter (fun m -> slotOf m = GroupedSlot)
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

    // The reveal surface (572.B). Each hidden id is resolved back to a
    // flat leaf against the pre-hiding index — a bare module id to the
    // module, a composite id to its page — so the row can carry the
    // entry's own name and icon rather than the raw id. An id that no
    // longer resolves (the module was removed from the deployment, or
    // access to it was revoked) is simply not listed: it stays in the
    // stored preference, costing nothing, and reappears in this list if
    // the module ever comes back. Ordered by id, which is stable across
    // renders and independent of the order things were hidden in.
    let hiddenSection =
        let resolveHidden (id: string) : SidebarModule option =
            match Map.tryFind id allById with
            | Some m ->
                Some {
                    Id = m.Id
                    Name = m.Name
                    Icon = m.Icon
                    HasData = m.HasData
                    IsPinned = false
                    Pages = []
                    IsExpanded = false
                }
            | None ->
                match Map.tryFind id allPageIndex with
                | Some(parent, page) ->
                    Some {
                        Id = page.Id
                        Name = page.Name
                        Icon = page.Icon
                        HasData = parent.HasData
                        IsPinned = false
                        Pages = []
                        IsExpanded = false
                    }
                | None -> None

        let entries = hiddenSet |> Set.toList |> List.choose resolveHidden

        if List.isEmpty entries then
            []
        else
            [
                {
                    Key = HiddenKey
                    Title = Some "Hidden items"
                    IsCollapsed = not (prefs.ExpandedGroups.Contains HiddenKey)
                    IsPinnedSection = false
                    Modules = entries
                }
            ]

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
                if
                    List.isEmpty pinnedSection
                    && List.isEmpty declaredSections
                    && List.isEmpty hiddenSection
                then
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

    homeSection
    @ pinnedSection
    @ declaredSections
    @ otherSection
    @ trailingSection
    @ hiddenSection

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

/// Eye glyph for the hide / restore affordance. Struck through when the
/// action is "hide" (the entry is on the rail and the click removes it);
/// plain when the action is "restore" (the row is in the Hidden items
/// section and the click puts it back). One glyph, two states — the same
/// fill/stroke economy `pinIcon` uses.
let private eyeIcon (isHidden: bool) =
    Svg.svg [
        svg.className "w-4 h-4"
        svg.fill "none"
        svg.stroke "currentColor"
        svg.viewBox (0, 0, 24, 24)
        svg.children [
            Svg.path [
                svg.custom ("strokeLinecap", "round")
                svg.custom ("strokeLinejoin", "round")
                svg.strokeWidth 2
                svg.d
                    "M2.04 12.32a1 1 0 0 1 0-.64C3.42 7.51 7.36 4.5 12 4.5s8.58 3.01 9.96 7.18a1 1 0 0 1 0 .64C20.58 16.49 16.64 19.5 12 19.5s-8.58-3.01-9.96-7.18Z"
            ]
            Svg.path [
                svg.custom ("strokeLinecap", "round")
                svg.custom ("strokeLinejoin", "round")
                svg.strokeWidth 2
                svg.d "M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z"
            ]
            if not isHidden then
                Svg.path [
                    svg.custom ("strokeLinecap", "round")
                    svg.custom ("strokeLinejoin", "round")
                    svg.strokeWidth 2
                    svg.d "M3 3l18 18"
                ]
        ]
    ]

/// The trailing affordances of one sidebar row, gathered so the row
/// renderer keeps a readable signature as the control set grows (it was
/// eleven positional arguments, four of them booleans, before the hide
/// control landed).
///
/// `Pinnable` / `Hideable` suppress a control entirely (Home and the area
/// switchers are neither); `IsHidden` is true only for rows rendered
/// inside the Hidden items section, where the pin control is suppressed
/// by the pin/hide rule and the eye reads "Restore".
type private RowControls = {
    Pinnable: bool
    IsPinned: bool
    Hideable: bool
    IsHidden: bool
    OnPinToggled: unit -> unit
    OnHideToggled: unit -> unit
}

/// Phase 609 — the accessible name of one rail row, and the single place
/// the shell's naming rule is written down.
///
/// **The mechanism, stated once.** Every interactive control in this file
/// carries `prop.ariaLabel`: that is the *accessible name* — what a screen
/// reader announces, what voice control targets, and the only one of the
/// two attributes that exists on a touch device. `prop.title` is added ON
/// TOP of it wherever the control has no visible text label — the whole
/// narrow (w-20) rail, plus the pin / hide affordances in both widths —
/// because a hover tooltip is the sighted pointer user's equivalent
/// affordance. `title` alone is NOT a substitute: it is the weakest source
/// in the accessible-name computation, several assistive technologies skip
/// it, and it never appears on touch. In the hover-expanded rail the row's
/// own `<span>` is the visible label, so the row keeps the `aria-label`
/// (mirroring that text, so the name survives the span being hidden) and
/// takes no tooltip — a tooltip repeating adjacent visible text is noise.
///
/// A new *row* inherits all of this by going through `renderRow`. A new
/// icon-only *control* sets both attributes; if it has visible text, it
/// sets `aria-label` only.
///
/// The `rowId` fallbacks exist because a row whose display name is blank
/// would render as an unnamed button — the same defect one level deeper.
/// The four reserved ids are named explicitly because they are the rows
/// whose loss strands the user: the two landings, and the
/// [Phase 567](567-admin-area-two-surface-sidebar-navigation.md) area
/// switchers (the switcher is the only route *into* the administration
/// area, the landing the only guaranteed way back).
let private rowAccessibleName (rowId: string) (displayName: string) : string =
    if not (System.String.IsNullOrWhiteSpace displayName) then
        displayName.Trim()
    else
        match rowId with
        | HomeId -> "Home"
        | AdminHomeId -> "Administration home"
        | AdminAreaId -> "Administration"
        | ProductAreaId -> "Back to app"
        | other when not (System.String.IsNullOrWhiteSpace other) -> other.Trim()
        | _ -> "Unnamed entry"

/// One clickable sidebar row — the shared button + hover-revealed pin and
/// hide affordances used by leaf modules, multi-page parents, and page
/// children alike. `leading` is an optional glyph rendered before the
/// icon (the expand chevron on a multi-page parent, shown only when the
/// sidebar is hover-expanded); `indent` insets a page child under its
/// parent; `controls` carries the trailing affordance state. The pin
/// affordance is the sole unpin control in the pinned section: it shows
/// the filled glyph + "Unpin" and stays visible, and clicking it fires
/// before the unpin re-render, so the row dropping back to its home
/// group is the expected result, not a lost click. The hide affordance
/// behaves the same way in reverse from the Hidden items section.
///
/// `rowId` is the row's sidebar id (bare module id or composite page id).
/// It is carried purely so the row can always resolve an accessible name —
/// see `rowAccessibleName` for the naming rule this renderer applies.
let private renderRow
    (isExpanded: bool)
    (isSelected: bool)
    (hasData: bool)
    (controls: RowControls)
    (indent: bool)
    (leading: ReactElement option)
    (icon: ReactElement)
    (rowId: string)
    (name: string)
    (onActivate: unit -> unit)
    =
    let accessibleName = rowAccessibleName rowId name

    // The trailing affordances keep their bare-verb tooltips (no visual
    // change), but their accessible names name the row they act on: a rail
    // of a dozen buttons all called "Pin" is unusable by voice control and
    // useless read aloud.
    let pinTooltip = if controls.IsPinned then "Unpin" else "Pin"
    let hideTooltip = if controls.IsHidden then "Restore" else "Hide"

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
                // Phase 609 — the row's own name, never its section's. In
                // the narrow rail this button has no visible text at all,
                // so before this it exposed no accessible name and no
                // tooltip; the two landings and the two area switchers were
                // the worst instances of that.
                prop.ariaLabel accessibleName
                if not isExpanded then
                    prop.title accessibleName
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
            if isExpanded && controls.Pinnable then
                Html.button [
                    prop.className [
                        // `bg-transparent` — same consumer-global-button
                        // defence as the row above.
                        "absolute right-2 top-1/2 -translate-y-1/2 p-1 rounded bg-transparent"
                        "text-white/40 hover:text-white hover:bg-white/10"
                        // Always visible once pinned; hover-only otherwise
                        // so the sidebar stays visually uncluttered.
                        if controls.IsPinned then
                            "opacity-100"
                        else
                            "opacity-0 group-hover:opacity-100 transition-opacity"
                    ]
                    prop.title pinTooltip
                    prop.ariaLabel $"{pinTooltip} {accessibleName}"
                    prop.onClick (fun e ->
                        e.stopPropagation ()
                        controls.OnPinToggled())
                    prop.children [ pinIcon controls.IsPinned ]
                ]

            // Hide / restore toggle (Phase 572) — the same hover-revealed
            // treatment as the pin control, sitting immediately left of it
            // when both are present. In the Hidden items section it is the
            // restore control and stays visible, because it is the only
            // affordance on a row whose entire purpose is to be put back.
            if isExpanded && controls.Hideable then
                Html.button [
                    prop.className [
                        "absolute top-1/2 -translate-y-1/2 p-1 rounded bg-transparent"
                        if controls.Pinnable then "right-9" else "right-2"
                        "text-white/40 hover:text-white hover:bg-white/10"
                        if controls.IsHidden then
                            "opacity-100"
                        else
                            "opacity-0 group-hover:opacity-100 transition-opacity"
                    ]
                    prop.title hideTooltip
                    prop.ariaLabel $"{hideTooltip} {accessibleName}"
                    prop.onClick (fun e ->
                        e.stopPropagation ()
                        controls.OnHideToggled())
                    prop.children [ eyeIcon controls.IsHidden ]
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
    (inHiddenSection: bool)
    (inPlacedSection: bool)
    (selectedModule: string)
    (onModuleSelected: string -> unit)
    (onPinToggled: string -> unit)
    (onModuleToggled: string -> unit)
    (onHideToggled: string -> unit)
    (m: SidebarModule)
    =
    // A hidden row is never pinnable — the pin/hide rule is enforced in
    // `SidebarPreferences.togglePinned`, and suppressing the control here
    // keeps the UI from offering a click that would do nothing.
    //
    // Phase 611 — nor is a row in a *placed* section (`_home` /
    // `_trailing`), for the same reason and now for the whole class: a
    // placed row's position is declared, so `buildSections` leaves it out
    // of the pinning index and pinning it moves nothing. This used to read
    // `m.Id <> HomeId`, which covered one of the four reserved rows by
    // name — the admin landing and both area switchers each offered a pin
    // control whose click was already inert.
    let pinnable = not inPlacedSection && not inHiddenSection
    let hideable = isHideableId m.Id

    let controlsFor (id: string) (isPinned: bool) = {
        Pinnable = pinnable
        IsPinned = isPinned
        Hideable = hideable
        IsHidden = inHiddenSection
        OnPinToggled = fun () -> onPinToggled id
        OnHideToggled = fun () -> onHideToggled id
    }

    // In the Hidden items section the row's primary action is restore —
    // clicking a hidden entry to navigate to it would leave the user on a
    // page with no rail entry, which reads as a broken click rather than
    // as the deliberate "still reachable by route" property.
    let activate (id: string) =
        if inHiddenSection then
            fun () -> onHideToggled id
        else
            fun () -> onModuleSelected id

    if List.isEmpty m.Pages then
        renderRow
            isExpanded
            (m.Id = selectedModule)
            m.HasData
            (controlsFor m.Id m.IsPinned)
            false
            None
            m.Icon
            m.Id
            m.Name
            (activate m.Id)
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
                    (controlsFor m.Id m.IsPinned)
                    false
                    (Some(chevron (not effectiveExpanded)))
                    m.Icon
                    m.Id
                    m.Name
                    parentActivate

                if isExpanded && effectiveExpanded then
                    for p in m.Pages do
                        Html.div [
                            prop.key p.Id
                            prop.children [
                                renderRow
                                    isExpanded
                                    (p.Id = selectedModule)
                                    m.HasData
                                    {
                                        Pinnable = not inHiddenSection
                                        IsPinned = p.IsPinned
                                        Hideable = true
                                        IsHidden = inHiddenSection
                                        OnPinToggled = fun () -> onPinToggled p.Id
                                        OnHideToggled = fun () -> onHideToggled p.Id
                                    }
                                    true
                                    None
                                    p.Icon
                                    p.Id
                                    p.Name
                                    (activate p.Id)
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
        // Mirrors the visible text (Phase 609) so the header's accessible
        // name stays stable as glyphs accumulate inside the button — it
        // already carries a chevron and, in the pinned section, a pin.
        // No tooltip: the label is right there.
        prop.ariaLabel (rowAccessibleName section.Key title)
        prop.onClick (fun _ -> onGroupToggled section.Key)
        prop.children [
            chevron section.IsCollapsed
            if section.IsPinnedSection then
                Html.span [ prop.className "w-3 h-3 text-brand"; prop.children [ pinIcon true ] ]
            Html.span [ prop.text title ]
        ]
    ]

/// Sidebar. Renders the sections in the order `buildSections` produced
/// them: the leading placed section, a pinned overlay (if any pinned
/// modules exist), each declared group as a collapsible section, "_other",
/// then the trailing placed section. Section headers appear only when the
/// sidebar is hover-expanded; in narrow mode (w-20) the sidebar is
/// icons-only and mirrors per-section collapse state — a collapsed group
/// shows a single default icon (its lead module's, click-to-expand)
/// instead of its full icon set, while the two placed sections and the
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
///
/// `onHideToggled` (Phase 572) carries the sidebar id — bare module id or
/// composite page id — the user hid or restored. It is the same message
/// in both directions, fired by the row's eye affordance and by a click
/// on a row inside the "Hidden items" section.
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
    (onHideToggled: string -> unit)
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
        let inHiddenSection = section.Key = HiddenKey
        // Phase 611 — the two placed sections, derived from the reserved
        // section keys rather than from any row id.
        let inPlacedSection = section.Key = HomeKey || section.Key = TrailingKey

        let renderOne (m: SidebarModule) =
            renderModuleButton
                isExpanded
                inHiddenSection
                inPlacedSection
                selectedModule
                onModuleSelected
                onPinToggled
                onModuleToggled
                onHideToggled
                m

        // The Hidden items section is a restore list, not part of the
        // rail's arrangement — there is no ordering to persist for
        // entries that are not on the rail, so it is rendered outside the
        // sortable machinery rather than writing a `ModuleOrder` key that
        // nothing ever reads.
        if inHiddenSection then
            Html.div [
                prop.className "space-y-1"
                prop.children (
                    section.Modules
                    |> List.map (fun m -> React.KeyedFragment(m.Id, [ renderOne m ]))
                )
            ]
        else

            let sortableContextProps =
                createObj [
                    "items" ==> (section.Modules |> List.map _.Id |> List.toArray)
                    "strategy" ==> DndKit.verticalListSortingStrategy
                ]

            let items =
                section.Modules
                |> List.map (fun m -> React.KeyedFragment(m.Id, [ SortableItem m.Id (renderOne m) ]))

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
            // Phase 609 — this used to resolve an untitled section to
            // `Option.defaultValue ""`: an empty tooltip and no accessible
            // name at all, which is reachable today (`_other` drops its
            // title when it is the only section, and it is collapsed by
            // default). Falling back to the lead module's name is honest —
            // the icon rendered below IS that module's icon.
            let groupLabel =
                section.Title |> Option.defaultValue lead.Name |> rowAccessibleName section.Key

            Html.button [
                prop.key (section.Key + "__groupicon")
                prop.className [
                    "w-full flex items-center justify-center py-3 transition-colors rounded-[var(--radius)] bg-transparent"
                    "text-white/60 hover:text-white hover:bg-white/5 border-2 border-transparent"
                ]
                prop.ariaLabel groupLabel
                prop.title groupLabel
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
                    else if
                        // Narrow (at-rest) rail. The Hidden items section is
                        // omitted entirely: a list of things the user chose
                        // not to see, rendered as an anonymous icon, is worse
                        // than absent. It returns the moment the rail expands.
                        section.Key = HiddenKey
                    then
                        ()
                    else
                        // The placed sections + the pinned overlay stay fully
                        // visible; every other group collapses to its single
                        // default icon, expanding to its module icons when the
                        // user opens it.
                        //
                        // Phase 611 — `TrailingKey` joins `HomeKey` here. Both
                        // are built with `IsCollapsed = false`, so the
                        // `not section.IsCollapsed` arm below would already
                        // render them; naming them is the point — the
                        // guarantee is "a placed row is reachable in BOTH rail
                        // widths without expanding anything", and it should not
                        // rest on a value computed three hundred lines away.
                        let alwaysVisible =
                            section.IsPinnedSection || section.Key = HomeKey || section.Key = TrailingKey

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
                        // Phase 609 sweep — the app mark carried no `alt`,
                        // so in the narrow rail (where the app-name span
                        // below is not rendered) the shell's only branding
                        // was invisible to assistive tech and announced as
                        // a bare image. The app name is the right text: it
                        // is exactly what the adjacent span says when the
                        // rail is expanded.
                        prop.children [ Html.img [ prop.src appLogo; prop.alt appName ] ]
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
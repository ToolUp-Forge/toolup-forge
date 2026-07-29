// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.SidebarPlacementTests

// ─── Phase 611 — rail placement as declared data (Fable tier) ─────────
//
// A row's position used to be an implicit consequence of a field that
// means something else: `Group = None` was read by `buildSections` as
// "bottom of the rail, inside the collapsed `_other` catch-all". Nothing
// declared it — it fell out of the bucketing — and the two landings
// escaped it only because the fold special-cased their literal ids. The
// Phase 567 area-switcher rows, added later with `Group = None` and no
// special case, therefore bucketed into `_other`: last in the rail and
// collapsed on a fresh profile. Observed live in a `SeparateArea`
// deployment, where a platform admin had to expand "Other" to find the
// only route into the administration area (Phase 608, superseded by 611).
//
// **Why these run here and not in the .NET pack.** `buildSections` reads
// no module-level value of `Toolup.Sidebar`, so it looks callable from the
// .NET harness — but F# emits a static-init check on module function
// entry, so the first call fires the file's initialiser, which reaches
// `importDefault "../icons/toolup-forge-dark.png"` and throws "You've hit
// dummy code used for Fable bindings". That was measured, not assumed:
// the pack below was first written .NET-side and every case errored on
// exactly that. So the executable half lives here, beside
// `SidebarNestingTests` / `SidebarHidingTests`, which pin the same fold;
// the .NET pack keeps the textual pins that hold the code SHAPE (the
// declaration is read, no id is named, the narrow rail keeps both placed
// sections visible) — see the Phase 611 arm of
// `ToolUp.Platform.Tests/InProcess/SidebarVisibilityContractTests.fs`.

open Toolup.Sidebar
open SidebarPreferences
open ToolUp.AI.Client.Tests.NodeTest

// ─── Fixtures ────────────────────────────────────────────────────────

let private stubIcon: Fable.React.ReactElement = unbox null

/// One inbound rail row. Every axis the placement decision can read is a
/// parameter, so each case states its whole input.
let private railRow (id: string) (group: string option) (placement: SidebarPlacement option) : SidebarModuleView = {
    Id = id
    Name = id
    Icon = stubIcon
    HasData = false
    Group = group
    Pages = []
    Placement = placement
}

/// A consumer module as every composition written before Phase 611 shaped
/// one: a group or no group, and no placement declaration at all.
let private undeclared (id: string) (group: string option) = railRow id group None

/// The four SDK reserved rows, declared exactly as the shipped SDK
/// declares them — the two landings via `ClientModule.withPlacement`
/// (`Home.create` / `AdminHome.create`), the two switchers at their
/// literal construction site in the shell's `sidebarSections` binding.
/// Restated here rather than driven through the built-ins' `create`
/// because those reach `Icons` bindings and the shell's `ClientConfig`;
/// the cost is that a new reserved row is a deliberate two-file edit —
/// add it here too, or this pack pins a set the shell no longer builds.
let private productLanding = railRow HomeId None (Some LeadingSlot)
let private adminLanding = railRow AdminHomeId None (Some LeadingSlot)
let private adminSwitcher = railRow AdminAreaId None (Some TrailingSlot)
let private productSwitcher = railRow ProductAreaId None (Some TrailingSlot)

let private freshProfile = UserSidebarPreferences.empty

let private keysOf (sections: SidebarSection list) = sections |> List.map _.Key

/// The comparable shape of a fold result: section order, each section's
/// collapse state, and its row ids. Compared instead of the raw records
/// because a failure then names the difference rather than dumping two
/// trees of React elements.
let private shapeOf (sections: SidebarSection list) =
    sections |> List.map (fun s -> s.Key, s.IsCollapsed, s.Modules |> List.map _.Id)

let private idsIn (sections: SidebarSection list) (key: string) =
    sections
    |> List.tryFind (fun s -> s.Key = key)
    |> Option.map (fun s -> s.Modules |> List.map _.Id)
    |> Option.defaultValue []

let private sectionOf (sections: SidebarSection list) (rowId: string) =
    sections
    |> List.tryFind (fun s -> s.Modules |> List.exists (fun m -> m.Id = rowId))

// ─── Cases ───────────────────────────────────────────────────────────

let tests =
    testList "Phase 611 — declared rail placement" [

        testCase "an undeclared row buckets exactly as before (GP 11)" (fun () ->
            // The backward-compatibility floor: a composition that declares
            // no placement must produce the pre-611 arrangement — declared
            // groups in first-occurrence order, then `_other` for the
            // ungrouped, and nothing else.
            let views = [
                undeclared "Sales" (Some "Analytics")
                undeclared "Marketing" (Some "Analytics")
                undeclared "Scratch" None
                undeclared "Finance" (Some "Reports")
            ]

            let sections = buildSections views freshProfile

            Expect.equal
                (keysOf sections)
                [ "Analytics"; "Reports"; OtherKey ]
                "an all-undeclared composition must produce declared groups in first-occurrence \
                 order followed by `_other` — no placed section appears, because no row asked for one"

            Expect.equal (idsIn sections "Analytics") [ "Sales"; "Marketing" ] "group members keep registration order"

            Expect.equal
                (idsIn sections OtherKey)
                [ "Scratch" ]
                "an ungrouped row still lands in the `_other` catch-all"

            Expect.isTrue
                (sections |> List.forall _.IsCollapsed)
                "and every one of those sections is still collapsed on a fresh profile — the \
                 pre-611 behaviour this phase deliberately did NOT change for grouped rows")

        testCase "declaring `GroupedSlot` is indistinguishable from declaring nothing" (fun () ->
            // The other half of the absent-means-today default: `None` and
            // `Some GroupedSlot` are the same slot, so a consumer can state
            // the default explicitly without changing the output.
            let shape (placement: SidebarPlacement option) =
                shapeOf (
                    buildSections
                        [
                            railRow "Sales" (Some "Analytics") placement
                            railRow "Scratch" None placement
                        ]
                        freshProfile
                )

            Expect.equal
                (shape (Some GroupedSlot))
                (shape None)
                "`Some GroupedSlot` must fold identically to `None` — otherwise the default is not \
                 really the default")

        testCase "a declared row lands in its slot, first and last" (fun () ->
            let views = [
                adminSwitcher
                undeclared "Scratch" None
                productLanding
                undeclared "Sales" (Some "Analytics")
            ]

            let sections = buildSections views freshProfile

            // Placement, not input order, decides position: the switcher was
            // registered first and renders last.
            Expect.equal
                (keysOf sections)
                [ HomeKey; "Analytics"; OtherKey; TrailingKey ]
                "the leading placed section renders first and the trailing one last, whatever order \
                 the rows arrived in"

            Expect.equal (idsIn sections HomeKey) [ HomeId ] "the landing declared `LeadingSlot`"
            Expect.equal (idsIn sections TrailingKey) [ AdminAreaId ] "the switcher declared `TrailingSlot`"

            Expect.equal
                (idsIn sections OtherKey)
                [ "Scratch" ]
                "`_other` holds only the ungrouped GROUPED row — a placed row is lifted out before \
                 any bucketing")

        testCase "no reserved row can resolve to `_other`" (fun () ->
            // The class this phase closes, in both areas. `_other` is
            // populated in each case (an ungrouped consumer module is
            // present), so a reserved row landing there would be a positive
            // result rather than an absent section.
            let areas = [
                "administration area", [ adminLanding; productSwitcher; undeclared "AdminScratch" None ]
                "product area", [ productLanding; adminSwitcher; undeclared "Scratch" None ]
            ]

            for area, views in areas do
                let sections = buildSections views freshProfile

                Expect.isFalse (List.isEmpty (idsIn sections OtherKey)) (area + ": the `_other` bucket is populated")

                for reserved in [ HomeId; AdminHomeId; AdminAreaId; ProductAreaId ] do
                    Expect.isFalse
                        (idsIn sections OtherKey |> List.contains reserved)
                        (area
                         + ": reserved row "
                         + reserved
                         + " resolved into the `_other` catch-all. `_other` renders last and is \
                            collapsed on a fresh profile, so a reserved row there is unreachable \
                            unless the user thinks to expand a section called \"Other\" — the Phase \
                            608 defect. Declare the row's slot at its construction site \
                            (`ClientModule.withPlacement`, or the `Placement` field where the shell \
                            builds the row directly).")

                    match sectionOf sections reserved with
                    | None -> ()
                    | Some section ->
                        Expect.isFalse
                            section.IsCollapsed
                            (area
                             + ": reserved row "
                             + reserved
                             + " sits in section \""
                             + section.Key
                             + "\", which is COLLAPSED on a fresh profile. Placement moved the row \
                                out of `_other` but not out of reach."))

        testCase "the fresh-profile switcher is reachable with no persisted state (Phase 608)" (fun () ->
            // The reproduction, as a test. `SeparateArea` + platform admin in
            // the product area: the shell hands the fold that area's modules
            // plus the role-gated switcher, and the user has never expanded
            // anything, so `ExpandedGroups` is empty.
            Expect.isEmpty freshProfile.ExpandedGroups "the fixture really is a fresh profile"
            Expect.isEmpty freshProfile.PinnedModuleIds "…with nothing pinned"

            let sections =
                buildSections [ productLanding; undeclared "Sales" (Some "Analytics"); adminSwitcher ] freshProfile

            match sectionOf sections AdminAreaId with
            | None -> Expect.isTrue false "the administration switcher is absent from the rail entirely"
            | Some section ->
                Expect.equal
                    section.Key
                    TrailingKey
                    "the switcher must sit in the trailing placed section, not in a bucket"

                Expect.isFalse
                    section.IsCollapsed
                    "…and that section must be expanded on a fresh profile. This is the whole defect: \
                     a platform admin had to expand a collapsed \"Other\" to find the only route into \
                     the administration area."

                Expect.isFalse
                    section.IsPinnedSection
                    "it is reachable because it is PLACED, not because the user happened to pin it"

            // The reverse trip, from inside the administration area.
            match sectionOf (buildSections [ adminLanding; productSwitcher ] freshProfile) ProductAreaId with
            | None -> Expect.isTrue false "the way back out to the app is absent from the administration rail"
            | Some section ->
                Expect.equal section.Key TrailingKey "the return switcher is placed the same way"
                Expect.isFalse section.IsCollapsed "…and is equally reachable on a fresh profile")

        testCase "a persisted preference cannot relocate a placed row" (fun () ->
            // A placed row is absent from the pinning index, so a stale (or
            // hand-edited) blob naming one moves nothing — the property that
            // already held for the product landing, now for the whole class.
            // Without it, "declared placement" would be advisory.
            let prefs = {
                freshProfile with
                    PinnedModuleIds = [ AdminAreaId; HomeId; "Sales" ]
            }

            let sections =
                buildSections [ productLanding; undeclared "Sales" (Some "Analytics"); adminSwitcher ] prefs

            Expect.equal
                (idsIn sections PinnedKey)
                [ "Sales" ]
                "only the grouped row is liftable into the pinned overlay"

            Expect.equal (idsIn sections HomeKey) [ HomeId ] "the landing stayed in its declared slot"
            Expect.equal (idsIn sections TrailingKey) [ AdminAreaId ] "so did the switcher")

        testCase "a placed row survives a hostile hidden-entry blob" (fun () ->
            // Phase 572's never-hideable rule, re-pinned here because
            // placement now decides where those rows render: the exemption
            // and the placement are separate axes, and both have to hold for
            // the row to be on the rail at all.
            let prefs = {
                freshProfile with
                    HiddenEntryIds = [ HomeId; AdminAreaId; "Sales" ]
            }

            let sections =
                buildSections [ productLanding; undeclared "Sales" (Some "Analytics"); adminSwitcher ] prefs

            Expect.equal (idsIn sections HomeKey) [ HomeId ] "the landing is not hideable"
            Expect.equal (idsIn sections TrailingKey) [ AdminAreaId ] "nor is the switcher"
            Expect.equal (idsIn sections HiddenKey) [ "Sales" ] "the ordinary row is, and is offered back")

        testCase "placement is derived from the declaration, never from a row id" (fun () ->
            // The behavioural half of 611.B, in both directions: a
            // reserved-LOOKING row that declares nothing is grouped like any
            // other (the id special-case really is gone), and a consumer row
            // that DOES declare is placed like any reserved one (the model is
            // general, not a rename of the special case).
            let sections =
                buildSections [ undeclared HomeId None; undeclared AdminAreaId None ] freshProfile

            Expect.equal
                (keysOf sections)
                [ OtherKey ]
                "a reserved-looking row that declares nothing is grouped — the fold reads the \
                 declaration, not the id"

            let consumerSections =
                buildSections
                    [
                        undeclared "Sales" (Some "Analytics")
                        railRow "Settings" None (Some TrailingSlot)
                        railRow "Dashboard" (Some "Analytics") (Some LeadingSlot)
                    ]
                    freshProfile

            Expect.equal
                (keysOf consumerSections)
                [ HomeKey; "Analytics"; TrailingKey ]
                "a consumer module may declare either placed slot; the SDK's own rows are the first \
                 consumers of the model, not the only permitted ones"

            Expect.equal
                (idsIn consumerSections HomeKey)
                [ "Dashboard" ]
                "a declared slot wins over a declared group — placement says WHERE, the group says \
                 which bucket if the row is bucketed at all"

            Expect.equal (idsIn consumerSections "Analytics") [ "Sales" ] "…so the group keeps only its grouped member")

        testCase "the placed sections are untitled and never collapsible" (fun () ->
            // Both render header-less, which is also why they cannot be
            // collapsed: the header (and the narrow rail's group icon) are the
            // only things that fire `onGroupToggled`, and `IsCollapsed` is
            // fixed at construction regardless of what `ExpandedGroups` says.
            // A title here would introduce a chevron, and with it a way to
            // hide the rows again.
            let noisyPrefs = {
                freshProfile with
                    ExpandedGroups = Set.ofList [ HomeKey; TrailingKey; OtherKey ]
            }

            for prefs in [ freshProfile; noisyPrefs ] do
                let sections = buildSections [ productLanding; adminSwitcher ] prefs

                for key in [ HomeKey; TrailingKey ] do
                    match sections |> List.tryFind (fun s -> s.Key = key) with
                    | None -> Expect.isTrue false ("the " + key + " section is missing")
                    | Some section ->
                        Expect.isNone section.Title (key + " carries no header title")
                        Expect.isFalse section.IsCollapsed (key + " is never collapsed, whatever the blob says"))

        testCase "the trailing section renders after every rail section but before the reveal list" (fun () ->
            // Order matters in both directions: after `_other`, so the
            // switcher reads as rail chrome rather than a destination, and
            // before `_hidden`, so the Phase 572 invariant ("rendered after
            // every rail section") still holds for the reveal list.
            let prefs = {
                freshProfile with
                    HiddenEntryIds = [ "Marketing" ]
            }

            let views = [
                productLanding
                undeclared "Sales" (Some "Analytics")
                undeclared "Marketing" (Some "Analytics")
                undeclared "Scratch" None
                adminSwitcher
            ]

            Expect.equal
                (keysOf (buildSections views prefs))
                [ HomeKey; "Analytics"; OtherKey; TrailingKey; HiddenKey ]
                "the full section order, with every reserved section populated at once")
    ]
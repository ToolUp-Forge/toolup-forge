// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.SidebarHidingContractTests

open Expecto
open ToolUp.Platform

// ─── Phase 572 — per-user sidebar entry hiding: the pure algebra ─────
//
// Hiding is the fourth verb on `UserSidebarPreferences` (after pin,
// collapse, reorder) and the only one with a rule tying it to another:
// hiding a pinned entry unpins it, and a hidden entry cannot be pinned
// until it is restored (572.C). This pack pins that rule, the
// hide/restore round-trip, and the legacy-blob null coercion — the three
// things that live in `SidebarPreferences` and are therefore reachable
// from the .NET harness.
//
// **What is NOT here, and why.** The `buildSections` exclusion and the
// "Hidden items" reveal section live in `Toolup.Sidebar`, whose module
// initialiser reaches `importDefault "../icons/toolup-forge-dark.png"` —
// a Fable binding that throws outside a Fable compilation, the same
// constraint `SidebarVisibilityContractTests` and `ModuleParityValidator`
// document for `ClientConfig`. Those assertions live beside the existing
// nested-sidebar pack in the Fable-tier harness
// (`ToolUp.AI.Client.Tests/SidebarHidingTests.fs`), which runs the real
// `load` path against a real localStorage as well.
//
// **The palette arm below is the acceptance criterion "hiding never
// affects any access decision", stated as a test.** It is deliberately
// structural: `CommandPaletteNav.candidates` takes no preferences
// parameter at all, so the assertion is that a destination survives in
// the palette while the user's preferences say it is hidden. If someone
// ever threads preferences into that fold, this file stops compiling
// before it stops passing.

// ─── Fixtures ─────────────────────────────────────────────────────────

let private hidden (ids: string list) : SidebarPreferences.UserSidebarPreferences = {
    SidebarPreferences.UserSidebarPreferences.empty with
        HiddenEntryIds = ids
}

/// A preferences record as a legacy blob deserialises it: the field that
/// did not exist when the blob was written arrives as `null`, even though
/// F#'s type system says a `string list` never is. `Unchecked.defaultof`
/// is the only way to construct that state in-process, and it is exactly
/// the state `Json.parseAs` produces for an absent property.
let private legacyBlobShaped: SidebarPreferences.UserSidebarPreferences = {
    PinnedModuleIds = [ "KeepMe" ]
    ModuleOrder = Map.ofList [ "Analytics", [ "KeepMe" ] ]
    ExpandedGroups = Set.ofList [ "Analytics" ]
    ExpandedModules = Unchecked.defaultof<Set<string>>
    HiddenEntryIds = Unchecked.defaultof<string list>
}

// ─── Hide / restore round-trip (572.A + 572.D) ────────────────────────

let hideRestoreTests =
    testList "Phase 572 — hide / restore round-trip" [
        testCase "hide records the entry, restore removes it" (fun () ->
            let afterHide =
                SidebarPreferences.hide "Sales" SidebarPreferences.UserSidebarPreferences.empty

            Expect.equal afterHide.HiddenEntryIds [ "Sales" ] "hide records the id"
            Expect.isTrue (SidebarPreferences.isHidden "Sales" afterHide) "isHidden agrees"

            let afterRestore = SidebarPreferences.restore "Sales" afterHide

            Expect.isEmpty afterRestore.HiddenEntryIds "restore removes the id"

            Expect.equal
                afterRestore
                SidebarPreferences.UserSidebarPreferences.empty
                "hide then restore round-trips to the original preferences exactly")

        testCase "toggleHidden drives both directions" (fun () ->
            let once =
                SidebarPreferences.toggleHidden "Sales" SidebarPreferences.UserSidebarPreferences.empty

            Expect.isTrue (SidebarPreferences.isHidden "Sales" once) "first toggle hides"

            let twice = SidebarPreferences.toggleHidden "Sales" once
            Expect.isFalse (SidebarPreferences.isHidden "Sales" twice) "second toggle restores")

        testCase "hide is idempotent — no duplicate ids" (fun () ->
            let twice =
                SidebarPreferences.UserSidebarPreferences.empty
                |> SidebarPreferences.hide "Sales"
                |> SidebarPreferences.hide "Sales"

            Expect.equal twice.HiddenEntryIds [ "Sales" ] "hiding an already-hidden id changes nothing")

        testCase "restoring a visible entry is a no-op" (fun () ->
            let prefs = hidden [ "Sales" ]
            let after = SidebarPreferences.restore "Reports" prefs
            Expect.equal after.HiddenEntryIds [ "Sales" ] "an unrelated restore leaves the list alone")

        testCase "a hidden PAGE does not hide its module" (fun () ->
            // Composite ids are what make both granularities expressible
            // in one list: `Sales/sku` is a page, `Sales` is the module.
            let prefs =
                SidebarPreferences.hide "Sales/sku" SidebarPreferences.UserSidebarPreferences.empty

            Expect.isTrue (SidebarPreferences.isHidden "Sales/sku" prefs) "the page is hidden"
            Expect.isFalse (SidebarPreferences.isHidden "Sales" prefs) "its module is not"
            Expect.isFalse (SidebarPreferences.isHidden "Sales/trend" prefs) "nor is a sibling page")

        testCase "hidden entries accumulate in the order they were hidden" (fun () ->
            let prefs =
                SidebarPreferences.UserSidebarPreferences.empty
                |> SidebarPreferences.hide "A"
                |> SidebarPreferences.hide "B"
                |> SidebarPreferences.hide "C"

            Expect.equal prefs.HiddenEntryIds [ "A"; "B"; "C" ] "append order, not prepend")
    ]

// ─── Pin/hide interplay (572.C) ───────────────────────────────────────

let pinHideRuleTests =
    testList "Phase 572.C — pin/hide interplay" [
        testCase "hiding a pinned entry unpins it" (fun () ->
            let prefs =
                SidebarPreferences.UserSidebarPreferences.empty
                |> SidebarPreferences.togglePinned "Sales"
                |> SidebarPreferences.togglePinned "Reports"

            Expect.equal prefs.PinnedModuleIds [ "Sales"; "Reports" ] "both pinned to begin with"

            let afterHide = SidebarPreferences.hide "Sales" prefs

            Expect.equal afterHide.PinnedModuleIds [ "Reports" ] "the hidden entry left the pinned list"
            Expect.isTrue (SidebarPreferences.isHidden "Sales" afterHide) "and is hidden"

            Expect.equal
                afterHide.HiddenEntryIds
                [ "Sales" ]
                "the OTHER pinned entry is untouched — hiding is per-entry")

        testCase "a hidden entry cannot be pinned until restored" (fun () ->
            let prefs =
                SidebarPreferences.hide "Sales" SidebarPreferences.UserSidebarPreferences.empty

            let attempted = SidebarPreferences.togglePinned "Sales" prefs

            Expect.isEmpty attempted.PinnedModuleIds "pinning a hidden entry is a no-op"
            Expect.equal attempted prefs "the whole record is unchanged, not just the pinned list")

        testCase "restoring re-enables pinning" (fun () ->
            let prefs =
                SidebarPreferences.UserSidebarPreferences.empty
                |> SidebarPreferences.hide "Sales"
                |> SidebarPreferences.restore "Sales"
                |> SidebarPreferences.togglePinned "Sales"

            Expect.equal prefs.PinnedModuleIds [ "Sales" ] "pinning works again once restored")

        testCase "hide does not restore a previously-pinned position" (fun () ->
            // Stated as a test because it is a deliberate trade, not an
            // oversight: a re-pin is one click, an entry that refuses to
            // disappear is a bug report.
            let prefs =
                SidebarPreferences.UserSidebarPreferences.empty
                |> SidebarPreferences.togglePinned "Sales"
                |> SidebarPreferences.hide "Sales"
                |> SidebarPreferences.restore "Sales"

            Expect.isEmpty prefs.PinnedModuleIds "the pin is not remembered across a hide/restore cycle")

        testCase "pinning an ordinary entry still works while something else is hidden" (fun () ->
            let prefs = hidden [ "Sales" ] |> SidebarPreferences.togglePinned "Reports"

            Expect.equal prefs.PinnedModuleIds [ "Reports" ] "the guard is per-id, not a global block")
    ]

// ─── Legacy-blob null coercion (572.A) ────────────────────────────────

let legacyBlobTests =
    testList "Phase 572.A — legacy prefs blob loads as nothing-hidden" [
        testCase "normalise coerces a null HiddenEntryIds to empty" (fun () ->
            let normalised = SidebarPreferences.normalise legacyBlobShaped

            Expect.equal normalised.HiddenEntryIds [] "the missing field becomes a usable empty list"
            Expect.isFalse (isNull (box normalised.HiddenEntryIds)) "and is genuinely non-null")

        testCase "normalise preserves everything the legacy blob did carry" (fun () ->
            let normalised = SidebarPreferences.normalise legacyBlobShaped

            Expect.equal normalised.PinnedModuleIds [ "KeepMe" ] "pins survive"
            Expect.equal normalised.ExpandedGroups (Set.ofList [ "Analytics" ]) "expanded groups survive"

            Expect.equal
                (normalised.ModuleOrder |> Map.tryFind "Analytics")
                (Some [ "KeepMe" ])
                "the ordering overlay survives — this is the reset path's whole cost")

        testCase "normalise also coerces the older additive field" (fun () ->
            let normalised = SidebarPreferences.normalise legacyBlobShaped

            Expect.isEmpty normalised.ExpandedModules "a null ExpandedModules becomes an empty set"
            Expect.isFalse (isNull (box normalised.ExpandedModules)) "and is genuinely non-null")

        testCase "normalise is idempotent" (fun () ->
            let once = SidebarPreferences.normalise legacyBlobShaped
            let twice = SidebarPreferences.normalise once
            Expect.equal twice once "re-normalising a clean record changes nothing")

        testCase "the hidden-id readers survive a null field without normalise" (fun () ->
            // The coercion is at the store read AND every consumer fold,
            // because a preferences value can reach a fold without ever
            // passing through `load` — a hand-built record, a future
            // server-side sync. `Set.ofList null` and `List.contains` on a
            // null list both throw; these must not.
            Expect.isEmpty (SidebarPreferences.hiddenIds legacyBlobShaped) "hiddenIds coerces on the way out"

            Expect.isFalse
                (SidebarPreferences.isHidden "Sales" legacyBlobShaped)
                "isHidden reports nothing hidden rather than throwing")

        testCase "hiding onto a legacy-shaped record produces a clean list" (fun () ->
            let after = SidebarPreferences.hide "Sales" legacyBlobShaped

            Expect.equal after.HiddenEntryIds [ "Sales" ] "the null list was coerced before the append")
    ]

// ─── Hiding is not an access decision (572.B acceptance) ──────────────

let private paletteFacts (id: string) (name: string) (pages: (string * string) list) =
    {
        Nav = {
            Id = id
            Group = Some "Analytics"
            NavRole = None
            Visibility = Visibility.visibleToAll
        }
        Name = name
        Pages =
            pages
            |> List.map (fun (route, title) -> {
                CommandPaletteNav.Route = route
                Title = title
            })
        HasPageViews = not (List.isEmpty pages)
    }
    : CommandPaletteNav.PaletteModuleFacts

let paletteParityTests =
    testList "Phase 572 — a hidden entry stays reachable" [
        testCase "the command palette still lists a hidden module" (fun () ->
            let facts = [ paletteFacts "Sales" "Sales" []; paletteFacts "Reports" "Reports" [] ]

            let prefs =
                SidebarPreferences.hide "Sales" SidebarPreferences.UserSidebarPreferences.empty

            Expect.isTrue (SidebarPreferences.isHidden "Sales" prefs) "the user hid Sales from their rail"

            let destinations =
                CommandPaletteNav.candidates id SidebarVisibility.defaults facts
                |> List.map _.SidebarId

            Expect.equal
                destinations
                [ "Sales"; "Reports" ]
                "the palette's destination list is unchanged — hiding is a rail preference, not access")

        testCase "the command palette still lists a hidden PAGE" (fun () ->
            let facts = [ paletteFacts "Sales" "Sales" [ "/overview", "Overview"; "/sku", "SKU" ] ]

            let prefs =
                SidebarPreferences.hide "Sales/sku" SidebarPreferences.UserSidebarPreferences.empty

            Expect.isTrue (SidebarPreferences.isHidden "Sales/sku" prefs) "the user hid one page"

            let destinations =
                CommandPaletteNav.candidates id SidebarVisibility.defaults facts
                |> List.map _.SidebarId

            Expect.equal
                destinations
                [ "Sales/overview"; "Sales/sku" ]
                "both pages remain palette destinations — the hidden one keeps its route")

        testCase "hiding leaves the access fold's answer untouched" (fun () ->
            // `SidebarVisibility.visible` is the access decision the route
            // guard (Phase 569) and the palette (571) both derive from.
            // Hiding must not appear in it at all: this asserts the same
            // module set before and after a hide, over the SAME inputs.
            let facts = [ paletteFacts "Sales" "Sales" []; paletteFacts "Reports" "Reports" [] ]

            let before =
                facts
                |> SidebarVisibility.visible _.Nav SidebarVisibility.defaults
                |> List.map _.Nav.Id

            let _ =
                SidebarPreferences.hide "Sales" SidebarPreferences.UserSidebarPreferences.empty

            let after =
                facts
                |> SidebarVisibility.visible _.Nav SidebarVisibility.defaults
                |> List.map _.Nav.Id

            Expect.equal after before "the visibility fold has no preferences input to be affected by"
            Expect.contains after "Sales" "and the hidden module is still accessible")
    ]
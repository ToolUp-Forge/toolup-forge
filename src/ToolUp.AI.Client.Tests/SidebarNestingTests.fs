// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.SidebarNestingTests

// ─── Nested multi-page module entries in the sidebar ─────────────────
//
// Pins `Toolup.Sidebar.buildSections` / `flatten` — the pure section
// builder behind the shell's nested-module presentation — plus the
// `SidebarPreferences` module-expand + legacy-blob helpers.
//
// A multi-page module (a `SidebarModuleView` carrying `Pages`) collapses
// into ONE parent entry with its pages as children, instead of one flat
// rail entry per page. Single-page modules are unchanged. Routing is
// untouched: page children keep their composite `{moduleId}{pageRoute}`
// ids, so `flatten` still resolves a composite id to the page's
// name/icon for the header.

open Fable.Core
open Fable.Core.JsInterop
open Toolup.Sidebar
open SidebarPreferences
open ToolUp.AI.Client.Tests.NodeTest

// ─── localStorage stub (for the legacy-blob load path) ───────────────

[<Emit("""(() => {
    globalThis.window = globalThis.window || {};
    if (!globalThis.window.localStorage) {
        const store = new Map();
        globalThis.window.localStorage = {
            getItem: (k) => (store.has(k) ? store.get(k) : null),
            setItem: (k, v) => { store.set(k, String(v)); },
            removeItem: (k) => { store.delete(k); },
        };
    }
})()""")>]
let private ensureLocalStorage () : unit = jsNative

/// Delete the `ExpandedModules` property from the stored prefs blob,
/// emulating a preferences blob written before that field existed.
[<Emit("""(() => {
    const k = 'toolup-sidebar-prefs';
    const obj = JSON.parse(globalThis.window.localStorage.getItem(k));
    delete obj.ExpandedModules;
    globalThis.window.localStorage.setItem(k, JSON.stringify(obj));
})()""")>]
let private stripExpandedModulesFromStore () : unit = jsNative

[<Emit("globalThis.window.localStorage.removeItem('toolup-sidebar-prefs')")>]
let private clearStore () : unit = jsNative

// ─── Fixtures ────────────────────────────────────────────────────────

let private stubIcon: Fable.React.ReactElement = unbox null

let private page (id: string) (name: string) : SidebarPageView = {
    Id = id
    Name = name
    Icon = stubIcon
}

let private moduleView
    (id: string)
    (name: string)
    (group: string option)
    (pages: SidebarPageView list)
    : SidebarModuleView =
    {
        Id = id
        Name = name
        Icon = stubIcon
        HasData = false
        Group = group
        Pages = pages
        // Phase 611 — declares no rail slot, so the fold buckets it by
        // `Group` exactly as it did before placement was declarable.
        Placement = None
    }

let private singlePage id name group = moduleView id name group []

let private multiPage id name group pageRoutes =
    moduleView id name group (pageRoutes |> List.map (fun (r, t) -> page (id + r) t))

// Find a section's top-level module by id.
let private moduleIn (sections: SidebarSection list) (sectionKey: string) (moduleId: string) : SidebarModule option =
    sections
    |> List.tryFind (fun s -> s.Key = sectionKey)
    |> Option.bind (fun s -> s.Modules |> List.tryFind (fun m -> m.Id = moduleId))

let tests =
    testList "Sidebar nested multi-page modules" [

        testList "buildSections — nesting shape" [
            testCase "a multi-page module is ONE parent entry with N page children" (fun () ->
                let views = [
                    multiPage "Sales" "Sales" (Some "Analytics") [
                        "/overview", "Overview"
                        "/sku", "SKU"
                        "/trend", "Trend"
                    ]
                ]

                let sections = buildSections views UserSidebarPreferences.empty

                match moduleIn sections "Analytics" "Sales" with
                | Some m ->
                    Expect.equal m.Id "Sales" "the parent entry uses the bare module id"
                    Expect.equal m.Pages.Length 3 "one child per page"

                    Expect.equal
                        (m.Pages |> List.map _.Id)
                        [ "Sales/overview"; "Sales/sku"; "Sales/trend" ]
                        "page children keep their composite {moduleId}{route} ids (routing unchanged)"
                | None -> failwith "expected the Sales module in the Analytics section")

            testCase "a single-page module is a leaf with no children" (fun () ->
                let views = [ singlePage "Reports" "Reports" (Some "Analytics") ]
                let sections = buildSections views UserSidebarPreferences.empty

                match moduleIn sections "Analytics" "Reports" with
                | Some m -> Expect.isEmpty m.Pages "single-page module has no page children"
                | None -> failwith "expected the Reports module")
        ]

        testList "buildSections — expand state" [
            testCase "a multi-page module is collapsed by default" (fun () ->
                let views = [ multiPage "Sales" "Sales" None [ "/a", "A"; "/b", "B" ] ]
                let sections = buildSections views UserSidebarPreferences.empty
                let m = moduleIn sections OtherKey "Sales"

                Expect.isTrue
                    (m |> Option.map (fun x -> not x.IsExpanded) |> Option.defaultValue false)
                    "default collapsed")

            testCase "an ExpandedModules entry flips IsExpanded" (fun () ->
                let views = [ multiPage "Sales" "Sales" None [ "/a", "A"; "/b", "B" ] ]

                let prefs = {
                    UserSidebarPreferences.empty with
                        ExpandedModules = Set.ofList [ "Sales" ]
                }

                let sections = buildSections views prefs
                let m = moduleIn sections OtherKey "Sales"
                Expect.isTrue (m |> Option.map _.IsExpanded |> Option.defaultValue false) "expanded when in the set")
        ]

        testList "buildSections — pinning" [
            testCase "an individually-pinned PAGE surfaces as its own pinned leaf" (fun () ->
                let views = [ multiPage "Sales" "Sales" (Some "Analytics") [ "/a", "A"; "/b", "B" ] ]

                let prefs = {
                    UserSidebarPreferences.empty with
                        PinnedModuleIds = [ "Sales/b" ]
                }

                let sections = buildSections views prefs

                match moduleIn sections PinnedKey "Sales/b" with
                | Some leaf ->
                    Expect.isTrue leaf.IsPinned "the pinned page leaf is marked pinned"
                    Expect.isEmpty leaf.Pages "the pinned page renders as a flat leaf"
                    Expect.equal leaf.Name "B" "resolves the page's own name"
                | None -> failwith "expected the pinned page leaf in the Pinned section")

            testCase "the pinned page is suppressed from its module's subtree (lifted out)" (fun () ->
                let views = [ multiPage "Sales" "Sales" (Some "Analytics") [ "/a", "A"; "/b", "B" ] ]

                let prefs = {
                    UserSidebarPreferences.empty with
                        PinnedModuleIds = [ "Sales/b" ]
                }

                let sections = buildSections views prefs

                match moduleIn sections "Analytics" "Sales" with
                | Some m ->
                    Expect.equal
                        (m.Pages |> List.map _.Id)
                        [ "Sales/a" ]
                        "the pinned page no longer appears under its parent"
                | None -> failwith "expected the Sales module still in its group")

            testCase "a pinned whole MODULE surfaces as a flat pinned leaf (no subtree)" (fun () ->
                let views = [ multiPage "Sales" "Sales" (Some "Analytics") [ "/a", "A"; "/b", "B" ] ]

                let prefs = {
                    UserSidebarPreferences.empty with
                        PinnedModuleIds = [ "Sales" ]
                }

                let sections = buildSections views prefs

                match moduleIn sections PinnedKey "Sales" with
                | Some leaf ->
                    Expect.isEmpty leaf.Pages "a pinned module in the pinned section is flat (navigates to first page)"
                | None -> failwith "expected the pinned module leaf")
        ]

        testList "flatten — composite page ids resolve for the header" [
            testCase "flatten yields the parent plus one leaf per page" (fun () ->
                let views = [ multiPage "Sales" "Sales" None [ "/a", "Alpha"; "/b", "Beta" ] ]
                let flat = buildSections views UserSidebarPreferences.empty |> flatten
                let ids = flat |> List.map _.Id
                Expect.isTrue (List.contains "Sales" ids) "the parent is present"
                Expect.isTrue (List.contains "Sales/a" ids) "page A resolves"
                Expect.isTrue (List.contains "Sales/b" ids) "page B resolves"

                let beta = flat |> List.tryFind (fun m -> m.Id = "Sales/b")
                Expect.equal (beta |> Option.map _.Name) (Some "Beta") "a composite id resolves to the page's own name")
        ]

        testList "SidebarPreferences — module expand + legacy blob" [
            testCase "toggleModuleExpanded adds then removes" (fun () ->
                let once = toggleModuleExpanded "Sales" UserSidebarPreferences.empty
                Expect.isTrue (once.ExpandedModules.Contains "Sales") "first toggle expands"
                let twice = toggleModuleExpanded "Sales" once
                Expect.isFalse (twice.ExpandedModules.Contains "Sales") "second toggle collapses")

            testCase "toggleModuleExpanded is independent of ExpandedGroups" (fun () ->
                let p = toggleModuleExpanded "Sales" UserSidebarPreferences.empty
                Expect.isEmpty p.ExpandedGroups "module expand must not touch the group-collapse overlay")

            testCase "a legacy blob missing ExpandedModules loads without nuking the rest" (fun () ->
                // Write a modern blob, then strip the new field to emulate a
                // pre-nesting blob. `load` must parse it (a missing field
                // deserialises to null; an F# Set is not null) and coerce
                // ExpandedModules -> empty via `normalise`, WITHOUT falling
                // into the parse-failure reset (which would drop PinnedModuleIds).
                ensureLocalStorage ()

                save {
                    UserSidebarPreferences.empty with
                        PinnedModuleIds = [ "KeepMe" ]
                        ExpandedModules = Set.ofList [ "Sales" ]
                }

                stripExpandedModulesFromStore ()

                let loaded = load ()

                Expect.equal loaded.PinnedModuleIds [ "KeepMe" ] "the rest of the blob survived (not the reset path)"
                Expect.isFalse (loaded.ExpandedModules.Contains "Sales") "the missing field coerced to an empty set"
                Expect.isEmpty loaded.ExpandedModules "ExpandedModules is a usable empty set, not null"

                clearStore ())
        ]
    ]
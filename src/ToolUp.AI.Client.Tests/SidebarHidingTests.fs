// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.SidebarHidingTests

// ─── Phase 572 — per-user sidebar entry hiding (Fable tier) ──────────
//
// The half of Phase 572 that only runs under Fable: the `buildSections`
// exclusion, the "Hidden items" reveal section, and the real `load` path
// against a real localStorage blob missing the new field. `Toolup.Sidebar`
// cannot be touched from the .NET harness (its module initialiser reaches
// `importDefault "../icons/toolup-forge-dark.png"`), and the legacy-blob
// backfill is an `[<Emit>]` that is JS by construction — so both belong
// here, beside `SidebarNestingTests`, which pins the nested-entry shape
// this builds on.
//
// The preference algebra itself (hide/restore round-trip, the pin/hide
// rule, `normalise`'s null coercion) is pinned in the .NET pack at
// `ToolUp.Platform.Tests/InProcess/SidebarHidingContractTests.fs` — pure
// F# with no Fable dependency, so it runs in the canonical suite.

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

/// Delete the `HiddenEntryIds` property from the stored prefs blob,
/// emulating a preferences blob written before Phase 572 existed.
[<Emit("""(() => {
    const k = 'toolup-sidebar-prefs';
    const obj = JSON.parse(globalThis.window.localStorage.getItem(k));
    delete obj.HiddenEntryIds;
    globalThis.window.localStorage.setItem(k, JSON.stringify(obj));
})()""")>]
let private stripHiddenEntryIdsFromStore () : unit = jsNative

/// Set `HiddenEntryIds` to an explicit JSON `null` — the OTHER shape of
/// the same defect, and the one that caught this phase out: `Json.parseAs`
/// throws on a `null` where it expects an array exactly as it does on an
/// absent key, so a blob in this shape reset the user's whole overlay
/// until `backfillMissingFields` started testing `== null` rather than
/// `=== undefined`. Post-parse coercion cannot reach it — the parse has
/// already thrown.
[<Emit("""(() => {
    const k = 'toolup-sidebar-prefs';
    const obj = JSON.parse(globalThis.window.localStorage.getItem(k));
    obj.HiddenEntryIds = null;
    globalThis.window.localStorage.setItem(k, JSON.stringify(obj));
})()""")>]
let private nullHiddenEntryIdsInStore () : unit = jsNative

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
    }

let private singlePage id name group = moduleView id name group []

let private multiPage id name group pageRoutes =
    moduleView id name group (pageRoutes |> List.map (fun (r, t) -> page (id + r) t))

/// Every top-level entry id the sidebar would render, across all
/// sections EXCEPT the Hidden items reveal list — i.e. what is actually
/// on the rail.
let private railIds (sections: SidebarSection list) : string list =
    sections
    |> List.filter (fun s -> s.Key <> HiddenKey)
    |> List.collect _.Modules
    |> List.map _.Id

let private sectionKeys (sections: SidebarSection list) = sections |> List.map _.Key

let private moduleIn (sections: SidebarSection list) (sectionKey: string) (moduleId: string) : SidebarModule option =
    sections
    |> List.tryFind (fun s -> s.Key = sectionKey)
    |> Option.bind (fun s -> s.Modules |> List.tryFind (fun m -> m.Id = moduleId))

let private hiding (ids: string list) = {
    UserSidebarPreferences.empty with
        HiddenEntryIds = ids
}

let tests =
    testList "Sidebar per-user entry hiding" [

        testList "buildSections — hidden entries leave the rail" [
            testCase "a hidden MODULE is absent from every rail section" (fun () ->
                let views = [
                    singlePage "Sales" "Sales" (Some "Analytics")
                    singlePage "Reports" "Reports" (Some "Analytics")
                ]

                let sections = buildSections views (hiding [ "Sales" ])

                Expect.equal (railIds sections) [ "Reports" ] "only the un-hidden module is on the rail"

                Expect.isTrue
                    ((moduleIn sections "Analytics" "Sales").IsNone)
                    "the hidden module is gone from its home group")

            testCase "a hidden PAGE leaves its siblings alone" (fun () ->
                let views = [
                    multiPage "Sales" "Sales" (Some "Analytics") [
                        "/overview", "Overview"
                        "/sku", "SKU"
                        "/trend", "Trend"
                    ]
                ]

                let sections = buildSections views (hiding [ "Sales/sku" ])

                match moduleIn sections "Analytics" "Sales" with
                | Some m ->
                    Expect.equal
                        (m.Pages |> List.map _.Id)
                        [ "Sales/overview"; "Sales/trend" ]
                        "the hidden page is dropped; its siblings and its parent stay"
                | None -> failwith "expected the Sales module still on the rail")

            testCase "hiding a whole multi-page module takes its pages with it" (fun () ->
                let views = [ multiPage "Sales" "Sales" (Some "Analytics") [ "/a", "A"; "/b", "B" ] ]
                let sections = buildSections views (hiding [ "Sales" ])
                Expect.isEmpty (railIds sections) "no rail entries left")

            testCase "an empty HiddenEntryIds list changes nothing" (fun () ->
                let views = [
                    singlePage "Sales" "Sales" (Some "Analytics")
                    singlePage "Reports" "Reports" None
                ]

                let before = buildSections views UserSidebarPreferences.empty
                let after = buildSections views (hiding [])
                Expect.equal (railIds after) (railIds before) "byte-for-byte the pre-572 rail (GP 11)")

            testCase "Home and the area switchers can never be hidden" (fun () ->
                // A hand-edited blob naming a reserved id must not strand
                // the user with no route home.
                let views = [
                    singlePage HomeId "Home" None
                    singlePage AdminAreaId "Administration" None
                    singlePage "Sales" "Sales" None
                ]

                let sections = buildSections views (hiding [ HomeId; AdminAreaId; "Sales" ])
                let ids = railIds sections

                Expect.isTrue (List.contains HomeId ids) "Home survives a hostile blob"
                Expect.isTrue (List.contains AdminAreaId ids) "the area switcher survives too"
                Expect.isFalse (List.contains "Sales" ids) "an ordinary module still hides"

                Expect.isTrue
                    ((moduleIn sections HiddenKey HomeId).IsNone)
                    "and the reserved id is not offered for restore either")
        ]

        testList "buildSections — the Hidden items reveal section" [
            testCase "no hidden entries means no Hidden items section" (fun () ->
                let views = [ singlePage "Sales" "Sales" None ]
                let sections = buildSections views UserSidebarPreferences.empty

                Expect.isFalse (sectionKeys sections |> List.contains HiddenKey) "the section is absent, not empty")

            testCase "a hidden entry is listed for restore, with its own name" (fun () ->
                let views = [ singlePage "Sales" "Sales" (Some "Analytics") ]
                let sections = buildSections views (hiding [ "Sales" ])

                match moduleIn sections HiddenKey "Sales" with
                | Some entry ->
                    Expect.equal entry.Name "Sales" "the row carries the entry's display name, not the raw id"
                    Expect.isFalse entry.IsPinned "a hidden entry is never pinned (the 572.C rule)"
                    Expect.isEmpty entry.Pages "the reveal list is flat"
                | None -> failwith "expected the hidden entry in the Hidden items section")

            testCase "a hidden PAGE resolves to its page name, not its module's" (fun () ->
                let views = [ multiPage "Sales" "Sales" (Some "Analytics") [ "/a", "Alpha"; "/b", "Beta" ] ]
                let sections = buildSections views (hiding [ "Sales/b" ])

                match moduleIn sections HiddenKey "Sales/b" with
                | Some entry -> Expect.equal entry.Name "Beta" "the composite id resolved through the page index"
                | None -> failwith "expected the hidden page in the Hidden items section")

            testCase "the Hidden items section is last, and collapsed by default" (fun () ->
                let views = [
                    singlePage "Sales" "Sales" (Some "Analytics")
                    singlePage "Reports" "Reports" (Some "Analytics")
                ]

                let sections = buildSections views (hiding [ "Sales" ])
                let keys = sectionKeys sections

                Expect.equal (List.last keys) HiddenKey "rendered after every rail section"

                match sections |> List.tryFind (fun s -> s.Key = HiddenKey) with
                | Some s ->
                    Expect.isTrue s.IsCollapsed "collapsed until the user opens it"
                    Expect.equal s.Title (Some "Hidden items") "titled"
                | None -> failwith "expected the Hidden items section")

            testCase "opening the section is the ordinary ExpandedGroups toggle" (fun () ->
                let views = [ singlePage "Sales" "Sales" None ]

                let prefs = {
                    hiding [ "Sales" ] with
                        ExpandedGroups = Set.ofList [ HiddenKey ]
                }

                match buildSections views prefs |> List.tryFind (fun s -> s.Key = HiddenKey) with
                | Some s -> Expect.isFalse s.IsCollapsed "expanded via the same overlay every section uses"
                | None -> failwith "expected the Hidden items section")

            testCase "an id that no longer resolves is silently not listed" (fun () ->
                // The module was removed from the deployment, or access to
                // it was revoked. The preference is kept (it costs
                // nothing and reappears if the module does), but there is
                // nothing to render.
                let views = [ singlePage "Sales" "Sales" None ]
                let sections = buildSections views (hiding [ "Ghost" ])

                Expect.isFalse
                    (sectionKeys sections |> List.contains HiddenKey)
                    "nothing resolvable to reveal ⇒ no section")

            testCase "hidden entries do NOT appear twice" (fun () ->
                let views = [
                    singlePage "Sales" "Sales" (Some "Analytics")
                    singlePage "Reports" "Reports" (Some "Analytics")
                ]

                let sections = buildSections views (hiding [ "Sales" ])
                let everyId = sections |> List.collect _.Modules |> List.map _.Id

                Expect.equal
                    (everyId |> List.filter ((=) "Sales") |> List.length)
                    1
                    "the hidden entry is in the reveal list only — never also on the rail")
        ]

        testList "hidden entries stay reachable" [
            testCase "flatten still resolves a hidden entry's name and icon" (fun () ->
                // `Layout.AppShell` resolves the page header from
                // `flatten`. A user who hides the page they are currently
                // reading must not lose the header — the page is still
                // theirs to read, it just has no row.
                let views = [ multiPage "Sales" "Sales" None [ "/a", "Alpha"; "/b", "Beta" ] ]
                let flat = buildSections views (hiding [ "Sales/b" ]) |> flatten
                let ids = flat |> List.map _.Id

                Expect.isTrue (List.contains "Sales/b" ids) "the hidden page still resolves for the header"

                Expect.equal
                    (flat |> List.tryFind (fun m -> m.Id = "Sales/b") |> Option.map _.Name)
                    (Some "Beta")
                    "with its own name")

            testCase "hiding does not touch the inbound (access-filtered) module set" (fun () ->
                // `buildSections` receives the already access-filtered
                // list. Hiding subtracts from the rail it builds and never
                // rewrites its input, which is what keeps the route guard
                // and the palette — both derived upstream — unaffected.
                let views = [ singlePage "Sales" "Sales" None; singlePage "Reports" "Reports" None ]

                let sections = buildSections views (hiding [ "Sales" ])
                Expect.equal (railIds sections) [ "Reports" ] "the rail shrank"

                Expect.equal
                    (views |> List.map _.Id)
                    [ "Sales"; "Reports" ]
                    "the caller's own list is untouched — hiding is a projection, not a mutation")
        ]

        testList "SidebarPreferences — legacy blob through the real load path" [
            testCase "a blob missing HiddenEntryIds loads as nothing-hidden" (fun () ->
                ensureLocalStorage ()

                save {
                    UserSidebarPreferences.empty with
                        PinnedModuleIds = [ "KeepMe" ]
                        ExpandedModules = Set.ofList [ "Sales" ]
                        HiddenEntryIds = [ "Sales" ]
                }

                stripHiddenEntryIdsFromStore ()

                let loaded = load ()

                Expect.equal loaded.PinnedModuleIds [ "KeepMe" ] "the rest of the blob survived (not the reset path)"
                Expect.isEmpty loaded.HiddenEntryIds "nothing is hidden for a pre-572 user"

                Expect.isTrue
                    (loaded.ExpandedModules.Contains "Sales")
                    "and the previous additive field still round-trips"

                clearStore ())

            testCase "a legacy blob's loaded prefs hide nothing in buildSections" (fun () ->
                ensureLocalStorage ()

                save {
                    UserSidebarPreferences.empty with
                        PinnedModuleIds = [ "Reports" ]
                }

                stripHiddenEntryIdsFromStore ()

                let loaded = load ()

                let views = [
                    singlePage "Sales" "Sales" (Some "Analytics")
                    singlePage "Reports" "Reports" (Some "Analytics")
                ]

                let sections = buildSections views loaded

                Expect.isTrue (railIds sections |> List.contains "Sales") "the whole rail is intact"

                Expect.isFalse (sectionKeys sections |> List.contains HiddenKey) "and no Hidden items section appeared"

                clearStore ())

            testCase "an explicit null HiddenEntryIds does not reset the overlay" (fun () ->
                // The repair has to happen in the JSON, before the parse:
                // `Json.parseAs` throws on a null-where-an-array-is-
                // expected, and the catch discards every other preference
                // with it. This test failed on the first run of the phase
                // and is the reason `backfillMissingFields` tests
                // `== null` (undefined OR null) rather than `=== undefined`.
                ensureLocalStorage ()

                save {
                    UserSidebarPreferences.empty with
                        PinnedModuleIds = [ "KeepMe" ]
                }

                nullHiddenEntryIdsInStore ()

                let loaded = load ()

                Expect.isEmpty loaded.HiddenEntryIds "a null list became an empty one"
                Expect.equal loaded.PinnedModuleIds [ "KeepMe" ] "without falling into the reset path"
                Expect.isFalse (isHidden "Sales" loaded) "and the consumer reader agrees"

                clearStore ())

            testCase "hide survives a save/load round-trip" (fun () ->
                ensureLocalStorage ()

                UserSidebarPreferences.empty |> hide "Sales/sku" |> hide "Reports" |> save

                let loaded = load ()

                Expect.equal
                    loaded.HiddenEntryIds
                    [ "Sales/sku"; "Reports" ]
                    "both granularities persist across a reload, in order"

                clearStore ())
        ]
    ]
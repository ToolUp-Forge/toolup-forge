// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module SidebarPreferences

open Fable.Core
open Fable.Core.JsInterop
open Fable.SimpleJson
open ToolUp.Platform

let private log = Logger.forCategory "client.preferences"

/// Per-user sidebar arrangement overlay. Groups are module-declared
/// taxonomy (see `ErasedModule.Group`); this record is the *user's*
/// overlay on top — what's pinned, how the list within each section
/// is ordered, which groups are expanded. Persisted to localStorage
/// per browser; cross-device sync is a future extension via
/// `IConfigStore` at a user scope.
type UserSidebarPreferences = {
    /// Module ids the user has pinned, in display order. Pinned modules
    /// surface in a dedicated section above the grouped list.
    PinnedModuleIds: string list
    /// User-defined module ordering within each group (including the
    /// pinned section, which is keyed `"_pinned"`). The map key is the
    /// group name (or `"_pinned"` / `"_other"`); the value is the
    /// module ids in the user's chosen order. Groups not present in
    /// the map fall back to registration order.
    ModuleOrder: Map<string, string list>
    /// Groups the user has explicitly expanded. Values are group names;
    /// the pinned section uses `"_pinned"` and ungrouped modules use
    /// `"_other"`. Groups absent from this set render collapsed —
    /// collapsed-by-default keeps a fresh sidebar visually quiet,
    /// nudging users toward Pinned for the entries they actually use.
    ExpandedGroups: Set<string>
    /// Multi-page modules the user has explicitly expanded, keyed by the
    /// bare module id (never a composite `{moduleId}{pageRoute}` id — a
    /// module owns its whole page subtree). A multi-page module absent
    /// from this set renders collapsed to its single parent entry;
    /// present, it reveals its page children. Single-page modules ignore
    /// it entirely. The shell force-expands whichever module owns the
    /// active page regardless of this set, so a deep-linked page is
    /// always visible.
    ///
    /// Additive field (post-nested-sidebar). `Json.parseAs` requires every
    /// record field and throws on a missing one, so a preferences blob
    /// written before this field existed is upgraded in place by `load` —
    /// `backfillMissingFields` seeds the absent property with its empty
    /// JSON default (a `Set` serialises as `[]`) before typed parsing, so
    /// a legacy blob keeps the user's pins / order rather than resetting.
    ExpandedModules: Set<string>
}

module UserSidebarPreferences =
    let empty: UserSidebarPreferences = {
        PinnedModuleIds = []
        ModuleOrder = Map.empty
        ExpandedGroups = Set.empty
        ExpandedModules = Set.empty
    }

/// Key used for both localStorage and (eventually) server-side blob
/// lookup. Namespaced under `toolup-` so it never collides with the
/// app's own localStorage usage.
let private storageKey = "toolup-sidebar-prefs"

/// Backfill any record field absent from a stored blob with its empty
/// JSON default. `Json.parseAs` requires every field of the target record
/// and THROWS on a missing one (`Could not find the required key …`), so
/// a blob written by an older app version — before a field existed —
/// would otherwise trip the parse-failure reset and wipe the user's whole
/// overlay (pins / order / expanded groups), not just default the new
/// field. Backfilling first lets a legacy blob upgrade in place.
///
/// Field JSON shapes (from `save`'s `Json.serialize`): `string list` and
/// `Set<string>` serialise as a JSON array; `Map<string,_>` as a JSON
/// object. Fable-only (raw JS-object munging); `load` never runs on .NET.
[<Emit("""(() => {
    const o = JSON.parse($0);
    if (o.PinnedModuleIds === undefined) o.PinnedModuleIds = [];
    if (o.ModuleOrder === undefined) o.ModuleOrder = {};
    if (o.ExpandedGroups === undefined) o.ExpandedGroups = [];
    if (o.ExpandedModules === undefined) o.ExpandedModules = [];
    return JSON.stringify(o);
})()""")>]
let private backfillMissingFields (json: string) : string = jsNative

/// Load preferences from localStorage. Returns `empty` if no entry
/// exists or if parsing fails — on parse failure we log a warning and
/// start fresh rather than trapping the user with corrupted state. A blob
/// missing a newer field (older app version) is upgraded in place via
/// `backfillMissingFields` rather than reset.
let load () : UserSidebarPreferences =
    match Browser.Dom.window.localStorage.getItem storageKey with
    | null
    | "" -> UserSidebarPreferences.empty
    | json ->
        try
            Json.parseAs<UserSidebarPreferences> (backfillMissingFields json)
        with ex ->
            log.Warn $"Failed to parse sidebar preferences: {ex.Message}. Resetting."
            Browser.Dom.window.localStorage.removeItem storageKey
            UserSidebarPreferences.empty

/// Persist preferences to localStorage. Called on every mutation
/// (pin/unpin/reorder/collapse) — write volume is negligible relative
/// to user interaction speed, so no debouncing.
let save (prefs: UserSidebarPreferences) : unit =
    let json = Json.serialize prefs
    Browser.Dom.window.localStorage.setItem (storageKey, json)

/// Toggle a module's pinned state. Returns the updated preferences
/// (caller decides when to persist). Newly pinned modules land at the
/// end of the pinned list; unpinning preserves relative order of the
/// rest.
let togglePinned (moduleId: string) (prefs: UserSidebarPreferences) : UserSidebarPreferences =
    if List.contains moduleId prefs.PinnedModuleIds then
        {
            prefs with
                PinnedModuleIds = prefs.PinnedModuleIds |> List.filter ((<>) moduleId)
        }
    else
        {
            prefs with
                PinnedModuleIds = prefs.PinnedModuleIds @ [ moduleId ]
        }

/// Toggle a group's expanded state.
let toggleExpanded (groupKey: string) (prefs: UserSidebarPreferences) : UserSidebarPreferences =
    if prefs.ExpandedGroups.Contains groupKey then
        {
            prefs with
                ExpandedGroups = prefs.ExpandedGroups.Remove groupKey
        }
    else
        {
            prefs with
                ExpandedGroups = prefs.ExpandedGroups.Add groupKey
        }

/// Toggle a multi-page module's expanded (page-subtree) state. Keyed by
/// the bare module id. Independent of `toggleExpanded` (which toggles a
/// section/group): a module lives inside a group, so the two collapse
/// levels compose.
let toggleModuleExpanded (moduleId: string) (prefs: UserSidebarPreferences) : UserSidebarPreferences =
    if prefs.ExpandedModules.Contains moduleId then
        {
            prefs with
                ExpandedModules = prefs.ExpandedModules.Remove moduleId
        }
    else
        {
            prefs with
                ExpandedModules = prefs.ExpandedModules.Add moduleId
        }

/// Replace the ordering for a single group. The caller supplies the
/// full list of module ids in the user's chosen order — the shell
/// applies it by stable-sorting group members by their position in
/// this list, with any unknown ids falling back to registration
/// order at the tail.
let setOrder (groupKey: string) (orderedIds: string list) (prefs: UserSidebarPreferences) : UserSidebarPreferences = {
    prefs with
        ModuleOrder = prefs.ModuleOrder |> Map.add groupKey orderedIds
}

/// Replace the order of pinned modules. The pinned section's ordering
/// lives on `PinnedModuleIds` directly (not `ModuleOrder`) because the
/// list defines both membership and order — a single source of truth.
let setPinnedOrder (orderedIds: string list) (prefs: UserSidebarPreferences) : UserSidebarPreferences = {
    prefs with
        PinnedModuleIds = orderedIds
}
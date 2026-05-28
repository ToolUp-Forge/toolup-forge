// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module SidebarPreferences

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
}

module UserSidebarPreferences =
    let empty: UserSidebarPreferences = {
        PinnedModuleIds = []
        ModuleOrder = Map.empty
        ExpandedGroups = Set.empty
    }

/// Key used for both localStorage and (eventually) server-side blob
/// lookup. Namespaced under `toolup-` so it never collides with the
/// app's own localStorage usage.
let private storageKey = "toolup-sidebar-prefs"

/// Load preferences from localStorage. Returns `empty` if no entry
/// exists or if parsing fails — on parse failure we log a warning and
/// start fresh rather than trapping the user with corrupted state.
let load () : UserSidebarPreferences =
    match Browser.Dom.window.localStorage.getItem storageKey with
    | null
    | "" -> UserSidebarPreferences.empty
    | json ->
        try
            Json.parseAs<UserSidebarPreferences> json
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
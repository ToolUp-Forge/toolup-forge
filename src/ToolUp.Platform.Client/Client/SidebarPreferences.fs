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
    /// Sidebar ids the user has hidden from their own rail (Phase 572).
    /// An entry is either a bare module id — hiding the whole module,
    /// pages and all — or a composite `{moduleId}{pageRoute}` page id,
    /// hiding that one page and leaving its siblings; the composite ids
    /// the nested sidebar already emits are what makes both granularities
    /// expressible in one list.
    ///
    /// **Pure preference, never an access decision.** Hiding removes an
    /// entry from ONE user's rail. It changes no permission, no team
    /// exposure, and no route: a hidden page is still reachable by URL
    /// (deep link, bookmark, `NavigationRequest`) and still listed by the
    /// command palette, whose candidate set is derived from
    /// `SidebarVisibility.visible` and never reads this record. The
    /// access filters are `SidebarVisibility`'s; this is the personal
    /// overlay applied strictly after them.
    ///
    /// Additive field (Phase 572) — same legacy-blob mechanism as
    /// `ExpandedModules`, plus `normalise`: a blob written before this
    /// field existed deserialises the missing list to `null`, and F# `[]`
    /// is NOT null, so every read coerces (`load` via `normalise`, and
    /// each consumer fold via `hiddenIds`) rather than trusting the
    /// record's declared type.
    HiddenEntryIds: string list
}

module UserSidebarPreferences =
    let empty: UserSidebarPreferences = {
        PinnedModuleIds = []
        ModuleOrder = Map.empty
        ExpandedGroups = Set.empty
        ExpandedModules = Set.empty
        HiddenEntryIds = []
    }

// ─── Null coercion for additively-introduced collection fields ────────
//
// A persisted blob predating a field deserialises that field to `null`.
// F#'s type system says `string list` / `Set<string>` / `Map<_,_>` are
// never null, so nothing downstream defends against it: `List.contains`
// on a null list, `Set.ofList null`, and `Map.tryFind` on a null map all
// throw, and under Fable they fail as `Cannot read properties of null`
// deep inside a render. Coerce once at the boundary instead.

let private orEmptyList (xs: string list) : string list = if isNull (box xs) then [] else xs

let private orEmptySet (xs: Set<string>) : Set<string> =
    if isNull (box xs) then Set.empty else xs

let private orEmptyMap (m: Map<string, string list>) : Map<string, string list> =
    if isNull (box m) then Map.empty else m

/// Coerce every collection field of a loaded preferences record to a
/// usable empty value when the wire handed us `null`. Applied by `load`
/// to whatever `Json.parseAs` produced; total, allocation-cheap, and
/// idempotent, so calling it on an already-clean record is free.
let normalise (prefs: UserSidebarPreferences) : UserSidebarPreferences = {
    PinnedModuleIds = orEmptyList prefs.PinnedModuleIds
    ModuleOrder = orEmptyMap prefs.ModuleOrder
    ExpandedGroups = orEmptySet prefs.ExpandedGroups
    ExpandedModules = orEmptySet prefs.ExpandedModules
    HiddenEntryIds = orEmptyList prefs.HiddenEntryIds
}

/// The user's hidden-entry ids as a set, null-coerced.
///
/// **Every consumer fold reads hidden ids through this**, not off the
/// record directly: a preferences value can reach a fold without ever
/// passing through `load` (a hand-built record in a test, a future
/// server-side sync via `IConfigStore`, a partially-migrated blob), and
/// `Set.ofList null` throws where an empty set is the honest answer.
let hiddenIds (prefs: UserSidebarPreferences) : Set<string> =
    prefs.HiddenEntryIds |> orEmptyList |> Set.ofList

/// Whether a given sidebar id (bare module id or composite page id) is
/// hidden on this user's rail.
let isHidden (id: string) (prefs: UserSidebarPreferences) : bool =
    prefs.HiddenEntryIds |> orEmptyList |> List.contains id

/// Key used for both localStorage and (eventually) server-side blob
/// lookup. Namespaced under `toolup-` so it never collides with the
/// app's own localStorage usage.
let private storageKey = "toolup-sidebar-prefs"

/// Backfill any record field absent from — or `null` in — a stored blob
/// with its empty JSON default. `Json.parseAs` requires every field of
/// the target record and THROWS on a missing one (`Could not find the
/// required key …`), so a blob written by an older app version — before a
/// field existed — would otherwise trip the parse-failure reset and wipe
/// the user's whole overlay (pins / order / expanded groups), not just
/// default the new field. Backfilling first lets a legacy blob upgrade in
/// place.
///
/// **The `== null` comparisons are deliberate, not sloppy.** Loose
/// equality catches `undefined` (the property is absent) AND `null` (the
/// property is present but empty — a hand-edited blob, or a writer that
/// serialised an unset field explicitly). Phase 572 found the second case
/// the hard way: `Json.parseAs` throws on a `null` where it expects an
/// array just as it does on an absent key, so a strict `=== undefined`
/// check let a null-valued field reach the parser and reset the whole
/// overlay — the exact outcome this function exists to prevent. Repairing
/// the JSON before the parse is the only layer that can save that blob;
/// `normalise` afterwards is for values that never came through here.
///
/// Field JSON shapes (from `save`'s `Json.serialize`): `string list` and
/// `Set<string>` serialise as a JSON array; `Map<string,_>` as a JSON
/// object. Fable-only (raw JS-object munging); `load` never runs on .NET.
[<Emit("""(() => {
    const o = JSON.parse($0);
    if (o.PinnedModuleIds == null) o.PinnedModuleIds = [];
    if (o.ModuleOrder == null) o.ModuleOrder = {};
    if (o.ExpandedGroups == null) o.ExpandedGroups = [];
    if (o.ExpandedModules == null) o.ExpandedModules = [];
    if (o.HiddenEntryIds == null) o.HiddenEntryIds = [];
    return JSON.stringify(o);
})()""")>]
let private backfillMissingFields (json: string) : string = jsNative

/// Load preferences from localStorage. Returns `empty` if no entry
/// exists or if parsing fails — on parse failure we log a warning and
/// start fresh rather than trapping the user with corrupted state. A blob
/// missing a newer field (older app version) is upgraded in place via
/// `backfillMissingFields` rather than reset, then null-coerced by
/// `normalise`. Two layers, because they defend different stretches of
/// the path: the backfill repairs the JSON BEFORE `Json.parseAs`, which
/// is the only point at which an absent-or-null field can be saved (the
/// parser throws on both, and the `with` below then discards the whole
/// overlay); `normalise` covers the record AFTER parsing, for any value
/// that reaches a consumer without coming through this function at all.
let load () : UserSidebarPreferences =
    match Browser.Dom.window.localStorage.getItem storageKey with
    | null
    | "" -> UserSidebarPreferences.empty
    | json ->
        try
            Json.parseAs<UserSidebarPreferences> (backfillMissingFields json) |> normalise
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
///
/// **Pin/hide rule, half 2 of 2 (Phase 572.C): a hidden entry cannot be
/// pinned until it is restored** — pinning is a no-op on a hidden id.
/// Pin and hide are opposite intents ("keep this in front of me" vs "take
/// this off my rail"), so the states are mutually exclusive by
/// construction rather than by a rendering rule: were a hidden entry
/// pinnable, `buildSections` would have to decide which overlay wins for
/// an entry that is both, and either answer surprises the user who set
/// the other one. Enforced HERE, in the one place both lists are written,
/// so no call site can produce the contradictory state — the UI
/// suppresses the pin affordance in the hidden section as well, but that
/// is a courtesy, not the guard.
let togglePinned (moduleId: string) (prefs: UserSidebarPreferences) : UserSidebarPreferences =
    let pinned = orEmptyList prefs.PinnedModuleIds

    if isHidden moduleId prefs then
        prefs
    elif List.contains moduleId pinned then
        {
            prefs with
                PinnedModuleIds = pinned |> List.filter ((<>) moduleId)
        }
    else
        {
            prefs with
                PinnedModuleIds = pinned @ [ moduleId ]
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

/// Hide a sidebar entry from this user's rail (Phase 572). `id` is a bare
/// module id (the whole module leaves the rail) or a composite
/// `{moduleId}{pageRoute}` page id (that page alone leaves it).
///
/// **Pin/hide rule, half 1 of 2 (Phase 572.C): hiding a pinned entry
/// unpins it.** The pinned section is a rail section like any other, so a
/// still-pinned hidden entry would keep rendering in the very rail the
/// user just removed it from. Unpinning here means "hide" always means
/// what it says, at the cost of the pin position not being restored on
/// reveal — a re-pin is one click, whereas an entry that refuses to
/// disappear is a bug report.
///
/// Idempotent: hiding an already-hidden id changes nothing.
let hide (id: string) (prefs: UserSidebarPreferences) : UserSidebarPreferences =
    let hidden = orEmptyList prefs.HiddenEntryIds

    {
        prefs with
            HiddenEntryIds = if List.contains id hidden then hidden else hidden @ [ id ]
            PinnedModuleIds = orEmptyList prefs.PinnedModuleIds |> List.filter ((<>) id)
    }

/// Restore a hidden entry to the rail. It returns to its home group (or
/// to its parent module's page subtree) in registration order — hiding
/// does not remember a pinned position, per the rule on `hide`.
/// Idempotent: restoring a visible id changes nothing.
let restore (id: string) (prefs: UserSidebarPreferences) : UserSidebarPreferences = {
    prefs with
        HiddenEntryIds = orEmptyList prefs.HiddenEntryIds |> List.filter ((<>) id)
}

/// Toggle an entry's hidden state — the single message the shell's hide
/// affordance and the hidden-items restore control both drive, so the
/// two directions cannot drift apart.
let toggleHidden (id: string) (prefs: UserSidebarPreferences) : UserSidebarPreferences =
    if isHidden id prefs then
        restore id prefs
    else
        hide id prefs
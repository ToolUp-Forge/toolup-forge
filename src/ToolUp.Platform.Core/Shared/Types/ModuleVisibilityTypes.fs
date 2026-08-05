// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 637 — module-visibility profiles ──────────────────────────
//
// An operator-curated, SERVER-AUTHORITATIVE selection of which
// registered modules a deployment surfaces. It is the fourth mechanism
// in the visibility pipeline, and it exists because the other three
// answer different questions and none of them answers this one:
//
//   * `NavRole` (Phase 568) is RBAC-shape — "may this ROLE see it".
//     A profile is not about roles: every member of a team can hold
//     the same role and still be shown a curated subset.
//   * `NeedsData` is data-presence — "is there anything to show".
//   * `UserSidebarPreferences.HiddenEntryIds` (Phase 572) is a
//     client-local localStorage overlay whose own contract says
//     "pure preference, never an access decision".
//
// The gap that leaves: an app composed with several ALTERNATIVE module
// families — where a deployment commits to one and the rest are noise —
// has no way to say so. Every registered module competes for sidebar
// space and invites the user into a surface the operator never intended
// them to use.
//
// **Not entitlements.** "Which modules did this customer buy" is a
// separate concern with commercial machinery of its own; this is
// visibility curation. Route AUTHORIZATION also stays where it is — the
// server-side per-route guards remain the enforcement (GP 12). A profile
// shapes surfacing, and OPTIONALLY 404s an excluded module's declared
// route prefixes as a hardening opt-in
// (`ServerConfig.ModuleVisibility = EnforcedModuleVisibility`).

/// What a profile says about the registered module set at one scope.
///
/// Two shapes rather than one, because the two express different
/// operator intents and each is dishonest when written as the other:
/// an allowlist is a COMMITMENT ("this deployment is these modules"),
/// so a module added to the composition later is excluded until someone
/// decides otherwise; a deny-list is a SUBTRACTION ("everything except
/// these"), so a newly-composed module surfaces by default. Forcing
/// either into the other's shape would make the answer for a module
/// nobody has considered yet depend on a modelling accident.
[<RequireQualifiedAccess>]
type ModuleVisibilityRule =
    /// Surface ONLY these module ids, in this order. Ids naming a module
    /// this deployment does not register are ignored (a composition can
    /// legitimately shrink under a profile that outlives it).
    | Allow of moduleIds: string list
    /// Surface everything registered EXCEPT these module ids.
    | Deny of moduleIds: string list

/// One scope's declaration. Stored server-side, one document per
/// `FlagScope`, and resolved along the same `User → Team → Platform`
/// walk the feature-flag substrate uses.
type ModuleVisibilityProfile = {
    /// The scope this profile governs. `FlagScope` (rather than a bare
    /// string) for the same reason the flag store uses it: a team id and
    /// a user id are both blob-named, and a collision between them would
    /// be a silent team-isolation bug (GP 4).
    Scope: FlagScope
    /// The selection over registered module ids.
    Rule: ModuleVisibilityRule
    /// Optional per-PAGE narrowing on top of the module selection. Each
    /// entry reuses Phase 572's composite sidebar-entry id shape — a
    /// bare module id (the whole module leaves the rail) or a composite
    /// `{moduleId}{pageRoute}` page id (that page alone leaves it) — so
    /// an operator can curate at the same granularity a user already
    /// can, and the two overlays are expressed in one vocabulary.
    ///
    /// Deliberately additive-only: an inner scope may add exclusions,
    /// never remove one an outer scope declared. See `resolve`.
    ExcludedEntryIds: string list
    /// Free-text operator note — why this profile exists. Carried so the
    /// next operator reads a decision rather than inferring one from a
    /// list of ids. Never interpreted.
    Note: string option
}

/// The resolved answer for one caller, as the client shell consumes it.
///
/// **`GovernedModuleIds` is load-bearing, not diagnostic.** A profile is
/// stated over the deployment's REGISTERED modules
/// (`ServerConfig.ModuleNames`), and the SDK's own admin built-ins carry
/// `_sdk.` ids that are deliberately absent from that list — exactly as
/// they are absent from the RBAC `Managed` list. Without the universe
/// travelling alongside the selection, an allowlist would read as
/// "everything else is excluded" and the first profile an operator saved
/// would hide the Platform Management surface they saved it from. So the
/// admission test is "not governed, OR selected" — the same shape stage 1
/// of the sidebar fold has always used against `Managed` / `Accessible`.
type ModuleVisibilityResolution = {
    /// The registered-module universe the profile was resolved against.
    /// A module id absent from this list is NOT governed by any profile
    /// and is admitted unconditionally.
    GovernedModuleIds: string list
    /// The governed module ids that survived the walk, in the order the
    /// narrowest `Allow` declared them (registration order otherwise).
    /// The order is carried because an allowlist IS an ordered curation;
    /// the visibility fold reads membership only, so a consumer wanting
    /// the operator's ordering has it without a second surface.
    SelectedModuleIds: string list
    /// Composite sidebar-entry ids excluded on top of the module
    /// selection — the union across every layer that declared one.
    ExcludedEntryIds: string list
    /// The scopes that actually contributed a profile, outermost-first.
    /// Empty is impossible: `resolve` returns `None` rather than an
    /// inert resolution, so "no profile anywhere" and "a profile that
    /// happens to exclude nothing" stay distinguishable.
    ContributingScopes: FlagScope list
}

/// Deployment-level selection for the module-visibility substrate
/// (`ServerConfig.ModuleVisibility`). Three states rather than a
/// boolean, because "are profiles a thing here" and "do profiles gate
/// ROUTES" are separate decisions and collapsing them would force an
/// operator who wants curated navigation to also accept 404s.
type ModuleVisibilityMode =
    /// Default. No `IModuleVisibilityStore` in DI, no admin API mounted
    /// (its routes 404), no profile read on the accessible-modules path,
    /// and `AccessibleModulesResponse.VisibilityProfile` is always
    /// `None`. An existing deployment that upgrades is byte-for-byte
    /// identical until it opts in, and pays nothing for the subsystem —
    /// not even the one blob read a resolution would cost (GP 11 + 13).
    | NoModuleVisibility
    /// Profiles are stored, resolved, and shape **surfacing** — sidebar,
    /// command palette, admin tiles, and the client route guard's
    /// navigation decision. Routes are untouched: an excluded module
    /// remains reachable by URL, the same posture Phase 572's per-user
    /// hide documents for itself. This is the honest default for
    /// curation, because a profile is a presentation decision and the
    /// server-side per-route guards are still the security boundary
    /// (GP 12).
    | SurfacingModuleVisibility
    /// Surfacing, plus opt-in **route hardening**: an `/api/*` request
    /// whose path falls under a route prefix declared by a module the
    /// resolved profile excludes is answered `404`.
    ///
    /// 404 rather than 403 is deliberate — the profile's claim is "this
    /// module is not part of this deployment's surface for you", and a
    /// 403 would instead confirm the module exists and merely refuse it.
    ///
    /// **Scope, stated plainly:** hardening can only reach routes a
    /// module DECLARES, via `ServerModule.RoutePrefixes`. A module that
    /// declares no prefixes is unaffected — its endpoints are
    /// indistinguishable from any other module's at the path level. That
    /// is a real limit, not an oversight, and it is why this mode is
    /// hardening rather than enforcement: authorization stays with the
    /// per-route guards.
    | EnforcedModuleVisibility

module ModuleVisibility =

    /// Apply one rule to the currently-selected ids.
    ///
    /// Both arms can only REMOVE. `Allow` intersects (preserving the
    /// rule's declared order, which is the operator's curation);
    /// `Deny` subtracts (preserving the incoming order).
    let applyRule (rule: ModuleVisibilityRule) (selected: string list) : string list =
        match rule with
        | ModuleVisibilityRule.Allow ids -> ids |> List.filter (fun id -> List.contains id selected)
        | ModuleVisibilityRule.Deny ids -> selected |> List.filter (fun id -> not (List.contains id ids))

    /// Resolve a caller's profile layers into one answer.
    ///
    /// `layers` is ordered **outermost-first** — `Platform`, then `Team`,
    /// then `User` — and every layer NARROWS the one before it.
    ///
    /// **This is deliberately not the flag walk's semantics, and the
    /// difference is the whole safety property.** `FlagEvaluator` walks
    /// `User → Team → Platform` and the FIRST hit wins: an inner scope
    /// OVERRIDES an outer one, which is right for a flag (a user opting
    /// into a beta the platform left off is the feature). Applied to
    /// visibility it would mean a user-scoped profile could re-admit a
    /// module the deployment excluded, i.e. the innermost, least
    /// authoritative layer could widen the operator's curation. So the
    /// layers compose monotonically instead: outer authority is a
    /// ceiling, inner scopes narrow beneath it, and "per-user narrowing
    /// layered on top" means exactly what it says.
    ///
    /// Returns `None` when no layer declared a profile — the unconfigured
    /// deployment, which must be byte-for-byte unchanged (GP 11). An
    /// inert-but-present resolution would be indistinguishable at the
    /// call site from a real one, and the fold would then have to decide
    /// what an empty selection means; `None` removes the question.
    let resolve
        (registeredModuleIds: string list)
        (layers: ModuleVisibilityProfile list)
        : ModuleVisibilityResolution option =
        match layers with
        | [] -> None
        | _ ->
            let selected =
                layers
                |> List.fold (fun acc layer -> applyRule layer.Rule acc) registeredModuleIds

            let excluded = layers |> List.collect _.ExcludedEntryIds |> List.distinct

            Some {
                GovernedModuleIds = registeredModuleIds
                SelectedModuleIds = selected
                ExcludedEntryIds = excluded
                ContributingScopes = layers |> List.map _.Scope
            }

    /// Does the resolved profile admit this module id?
    ///
    /// "Not governed, OR selected" — see `ModuleVisibilityResolution`'s
    /// `GovernedModuleIds` for why the first disjunct is not a loophole
    /// but the thing that keeps an operator from locking themselves out
    /// of the surface they administer profiles from.
    let admitsModule (resolution: ModuleVisibilityResolution) (moduleId: string) : bool =
        not (List.contains moduleId resolution.GovernedModuleIds)
        || List.contains moduleId resolution.SelectedModuleIds

    /// Does the resolved profile admit this composite sidebar-entry id
    /// (a bare module id or a `{moduleId}{pageRoute}` page id)? Page
    /// exclusions are stated positively as a removal list, so an entry
    /// nobody named is admitted.
    let admitsEntry (resolution: ModuleVisibilityResolution) (entryId: string) : bool =
        not (List.contains entryId resolution.ExcludedEntryIds)

    /// `admitsModule` lifted over the optional resolution — the shape
    /// every consumer actually has, since `None` is the unconfigured
    /// deployment and must admit everything.
    let admitsModuleOpt (resolution: ModuleVisibilityResolution option) (moduleId: string) : bool =
        match resolution with
        | None -> true
        | Some r -> admitsModule r moduleId

    /// `admitsEntry` lifted over the optional resolution.
    let admitsEntryOpt (resolution: ModuleVisibilityResolution option) (entryId: string) : bool =
        match resolution with
        | None -> true
        | Some r -> admitsEntry r entryId

// ─── Audit ────────────────────────────────────────────────────────────

/// Discriminates a profile being saved from a profile being cleared in
/// a `ModuleVisibilityChangedPayload`. Mirrors `FlagChangeAction`.
///
/// `RequireQualifiedAccess` for the same reason `FlagChangeAction` needs
/// it — a bare `Set` case would shadow the built-in `Set<'T>` collection
/// constructor at call sites.
[<RequireQualifiedAccess>]
type ModuleVisibilityChangeAction =
    | Saved of ModuleVisibilityProfile
    | Cleared

/// Audit-event payload recorded in `IEventStore` whenever a
/// module-visibility profile is saved or cleared (GP 6 — an operator
/// changing what a whole team can see is precisely the class of act that
/// must leave a trail). Serialised with `FableConverters`, like every
/// other DU-carrying SDK event payload. The event's `ScopeId` is the
/// profile scope's slug, so team-scoped audit queries stay isolated.
type ModuleVisibilityChangedPayload = {
    /// Scope at which the change was made. Mirrors the event's `ScopeId`
    /// but preserved in the payload for consumers replaying across
    /// scopes.
    Scope: FlagScope
    /// What happened — saved (with the new profile) or cleared.
    Action: ModuleVisibilityChangeAction
    /// `AccessContext.UserId` of the caller who made the change.
    /// Attribution is audit-only; the SDK gates no future read on it.
    ChangedBy: string
}

module ModuleVisibilityEvents =
    /// Reserved `ModuleEvent.SourceModule` for module-visibility audit
    /// events. Namespaced under `_sdk` so it cannot collide with an
    /// app's registered module names.
    [<Literal>]
    let SourceModule = "_sdk.ModuleVisibility"

    /// Canonical `ModuleEvent.EventType` value for profile changes.
    /// Downstream consumers match on this literal.
    [<Literal>]
    let ProfileChanged = "ModuleVisibilityProfileChanged"
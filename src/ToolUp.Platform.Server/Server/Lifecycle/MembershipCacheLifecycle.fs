module ToolUp.Platform.MembershipCacheLifecycle

open System
open ToolUp.Platform
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.TeamManagement

// ─── Phase 54 — first-party membership-cache lifecycle hook ──────────
//
// On `OnDeprovisioned`, evicts the offboarded team's active-team cache
// entries so a removed member can never resolve back into the dead
// scope from a warm cache. Resolves the active `IStorageScopeResolver`
// + `ITeamStore` from DI per call (GP 12 rule 4):
//   * `TeamScopeResolver` present — enumerate the team's members via
//     `ITeamStore.GetTeamMembers` and `InvalidateUser` each (which
//     publishes a `MembershipChanged` event so every subscriber —
//     this resolver and any cross-instance companion — evicts uniformly,
//     reusing the Phase 5d event-driven invalidation path).
//   * any other resolver (Individual / Anonymous / ephemeral modes) —
//     `Skipped`, there is no team membership cache to invalidate.
//
// `OnProvisioned` is a no-op `Skipped`: the active-team cache is
// populated lazily on a member's first request, so provisioning has
// nothing to evict.

/// The team the offboarded scope names: a `team-{id}` scope's id, or the
/// scope id itself (the hook accepts a bare team id too).
let private teamIdOf (scopeId: string) =
    match ScopeId.parse scopeId with
    | ScopeKind.Team teamId -> teamId
    | _ -> scopeId

/// The first-party membership-cache offboard hook, as a value. Its
/// substrate is the team scope resolver (the only resolver with a team
/// membership cache) and the team store that enumerates the members.
let hook: TenantHook<TeamScopeResolver * ITeamStore> = {
    Name = "membership-cache"
    Requires = "a TeamScopeResolver with an ITeamStore (team membership cache)"
    Resolve =
        fun services ->
            match services.GetService(typeof<IStorageScopeResolver>), services.GetService(typeof<ITeamStore>) with
            | (:? TeamScopeResolver as resolver), (:? ITeamStore as store) -> Some(resolver, store)
            | _ -> None
    // The active-team cache is populated lazily on a member's first
    // request, so provisioning has nothing to evict.
    OnProvisioned = None
    OnDeprovisioned =
        Some(fun (resolver, store) scopeId _ -> async {
            let! members = store.GetTeamMembers(teamIdOf scopeId)

            // InvalidateUser publishes a MembershipChanged event per
            // member; the resolver (and any cross-instance companion)
            // evicts the affected user's active-team cache entry.
            for m in members do
                resolver.InvalidateUser m.UserId

            return LifecycleHookResult.Completed
        })
    // Mutation-free preview: the team's members (the cache entries that
    // WOULD be invalidated), via the read-only GetTeamMembers.
    Preview =
        Some(fun (_, store) scopeId _ -> async {
            let! members = store.GetTeamMembers(teamIdOf scopeId)
            return members.Length, sprintf "%d member cache entr(y/ies) would be invalidated" members.Length
        })
}

/// Construct the first-party membership-cache lifecycle hook. Resolves
/// the active scope resolver + team store from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle = TenantHook.asLifecycle hook services
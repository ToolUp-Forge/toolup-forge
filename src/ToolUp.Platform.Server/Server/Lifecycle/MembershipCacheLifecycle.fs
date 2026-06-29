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

/// Strip the `team-` container prefix if present so the hook accepts
/// either the bare team id or the `team-{id}` container form as the
/// scope identifier.
let private teamIdOf (scopeId: string) =
    if scopeId.StartsWith("team-", StringComparison.Ordinal) then
        scopeId.Substring 5
    else
        scopeId

type MembershipCacheLifecycle(services: IServiceProvider) =
    interface ITenantLifecycle with
        member _.Name = "membership-cache"

        member _.OnProvisioned(_scopeId, _actorUserId) = async {
            return
                LifecycleHookResult.Skipped
                    "no provisioning action — the active-team cache is populated lazily on first resolve"
        }

        member _.OnDeprovisioned(scopeId, _actorUserId) = async {
            let resolver = services.GetService(typeof<IStorageScopeResolver>)
            let teamStore = services.GetService(typeof<ITeamStore>)

            match resolver, teamStore with
            | (:? TeamScopeResolver as teamResolver), (:? ITeamStore as store) ->
                let teamId = teamIdOf scopeId
                let! members = store.GetTeamMembers teamId

                // InvalidateUser publishes a MembershipChanged event per
                // member; the resolver (and any cross-instance companion)
                // evicts the affected user's active-team cache entry.
                for m in members do
                    teamResolver.InvalidateUser m.UserId

                return LifecycleHookResult.Completed
            | _ ->
                return
                    LifecycleHookResult.Skipped
                        "scope resolver is not TeamScopeResolver — no team membership cache to invalidate"
        }

    // Phase 54c — mutation-free preview: count the team's members (the
    // cache entries that WOULD be invalidated) via the read-only
    // GetTeamMembers, without calling InvalidateUser.
    interface ITenantLifecyclePreview with
        member _.OnDeprovisionPreview(scopeId, _actorUserId) = async {
            let resolver = services.GetService(typeof<IStorageScopeResolver>)
            let teamStore = services.GetService(typeof<ITeamStore>)

            match resolver, teamStore with
            | (:? TeamScopeResolver), (:? ITeamStore as store) ->
                let teamId = teamIdOf scopeId
                let! members = store.GetTeamMembers teamId

                return
                    LifecyclePreviewItem.affecting
                        "membership-cache"
                        members.Length
                        (sprintf "%d member cache entr(y/ies) would be invalidated" members.Length)
            | _ ->
                return
                    LifecyclePreviewItem.affecting
                        "membership-cache"
                        0
                        "not a TeamScopeResolver — no membership cache to invalidate"
        }

/// Construct the first-party membership-cache lifecycle hook. Resolves
/// the active scope resolver + team store from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle =
    MembershipCacheLifecycle(services) :> ITenantLifecycle
module ToolUp.Platform.Compose.MembershipCacheLifecycle

open System
open ToolUp.Platform
open ToolUp.Platform.Scope
open ToolUp.Platform.Config.TeamManagement

// ─── First-party membership-cache lifecycle hook ────────────────────
//
// On `OnDeprovisioned`, evicts the offboarded team's active-team cache
// entries so a removed member can never resolve back into the dead scope
// from a warm cache. The one membership cache is the deployment's resolver
// (`DefaultSubjectResolver`); the hook resolves it, the `ITeamStore` and
// the notification channel from DI per call (GP 12 rule 4), enumerates the
// team's members and publishes a `MembershipChanged` (`ActiveTeamSet`) per
// member on the reserved topic, so every subscriber — the resolver and any
// cross-instance companion — evicts uniformly. A deployment whose resolver
// holds no team cache (no team store) is `Skipped`.
//
// `OnProvisioned` is a no-op `Skipped`: the active-team cache is
// populated lazily on a member's first request, so provisioning has
// nothing to evict.

/// The team the offboarded scope names: a `team-{id}` scope's id, or the
/// scope id itself (the hook accepts a bare team id too).
let private teamIdOf (scopeId: string) =
    match ScopeId.parse scopeId with
    | ScopeKind.Team teamId -> teamId
    | _ -> TeamId scopeId

/// The first-party membership-cache offboard hook, as a value. Its
/// substrate is the deployment's resolver (the one membership cache), the
/// team store that enumerates the members and the channel the eviction is
/// published on.
let hook: TenantHook<INotificationChannel * ITeamStore> = {
    Name = "membership-cache"
    Requires = "a DefaultSubjectResolver with an ITeamStore and an INotificationChannel (team membership cache)"
    Resolve =
        fun services ->
            match
                services.GetService(typeof<ISubjectResolver>),
                services.GetService(typeof<ITeamStore>),
                services.GetService(typeof<INotificationChannel>)
            with
            | (:? DefaultSubjectResolver.DefaultSubjectResolver),
              (:? ITeamStore as store),
              (:? INotificationChannel as channel) -> Some(channel, store)
            | _ -> None
    // The active-team cache is populated lazily on a member's first
    // request, so provisioning has nothing to evict.
    OnProvisioned = None
    OnDeprovisioned =
        Some(fun (channel, store) scopeId _ -> async {
            let! members = store.GetTeamMembers(TeamId((teamIdOf scopeId).Value))

            for m in members do
                do!
                    channel.Publish(
                        NotificationKind.PlatformReservedScope,
                        MembershipChanged {
                            TeamId = teamIdOf scopeId
                            AffectedUserId = UserId m.UserId
                            ChangeKind = MembershipChangeKind.ActiveTeamSet
                            PublishedAt = DateTime.UtcNow
                        }
                    )

            return LifecycleHookResult.Completed
        })
    // Mutation-free preview: the team's members (the cache entries that
    // WOULD be invalidated), via the read-only GetTeamMembers.
    Preview =
        Some(fun (_, store) scopeId _ -> async {
            let! members = store.GetTeamMembers(TeamId((teamIdOf scopeId).Value))
            return members.Length, sprintf "%d member cache entr(y/ies) would be invalidated" members.Length
        })
}

/// Construct the first-party membership-cache lifecycle hook. Resolves
/// the resolver, team store and channel from `services` on every call.
let create (services: IServiceProvider) : ITenantLifecycle = TenantHook.asLifecycle hook services
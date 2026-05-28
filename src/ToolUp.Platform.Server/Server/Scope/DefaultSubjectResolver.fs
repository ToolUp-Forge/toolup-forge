module ToolUp.Platform.DefaultSubjectResolver

open System
open Microsoft.Extensions.Caching.Memory
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.TeamManagement

// ─── DefaultSubjectResolver — Phase 66 Stream A.3 ────────────────────
//
// Single implementation of `ISubjectResolver` covering the four-step
// resolution algorithm (design §3.1). Composes the per-mode behaviour
// of the retiring `AnonymousScopeResolver` / `AuthenticatedScopeResolver`
// / `TeamScopeResolver` into one stateless resolver whose decisions are
// driven by the deployment's `Surfaces: SurfaceProfile list`.
//
// Active-team caching preserved from `TeamScopeResolver`: 5-minute
// sliding-TTL `IMemoryCache` entry per `userId`, invalidated event-
// driven by subscribing to `NotificationKind.PlatformReservedScope`
// for `MembershipChanged.Removed` / `.ActiveTeamSet` (the two kinds
// that move the active-team pointer).
//
// `IDisposable` unsubscribes on shutdown — DI registers the resolver
// as a singleton, so disposal runs once on app teardown.
//
// **Additive deployment (Phase 66 Stream A.3 only):** this resolver
// is authored alongside the existing `IStorageScopeResolver` dispatch
// in `StorageScopeResolver.fs`. Stream A.6 rewires
// `ScopeResolutionMiddleware` to call `ISubjectResolver` instead;
// until then this type compiles and is unit-testable but is not yet
// wired into the request pipeline.

/// Default in-process resolver implementing the four-step algorithm
/// from design §3.1. Stateless except for the active-team
/// `IMemoryCache` whose lifecycle is event-driven (subscribes to
/// `NotificationKind.PlatformReservedScope`).
type DefaultSubjectResolver
    (
        surfaces: SurfaceProfile list,
        teamStore: ITeamStore option,
        cache: IMemoryCache,
        notifications: INotificationChannel,
        ?logger: ILogger
    ) =
    let supportsAnonymous =
        surfaces
        |> List.exists (function
            | SurfaceProfile.Anonymous _ -> true
            | _ -> false)

    let supportsUser =
        surfaces
        |> List.exists (function
            | SurfaceProfile.AuthenticatedUser _ -> true
            | _ -> false)

    let supportsTeam =
        surfaces
        |> List.exists (function
            | SurfaceProfile.Team _ -> true
            | _ -> false)

    let cacheKey userId = $"active-team:{userId}"

    let logError (msg: string) (ex: exn) =
        match logger with
        | Some l -> l.Error(msg, Some ex)
        | None -> ()

    let handle (envelope: NotificationEnvelope) =
        match envelope.Notification with
        | MembershipChanged payload ->
            match payload.ChangeKind with
            | MembershipChangeKind.Removed
            | MembershipChangeKind.ActiveTeamSet -> cache.Remove(cacheKey payload.AffectedUserId) |> ignore
            | MembershipChangeKind.Added
            | MembershipChangeKind.RoleChanged -> ()
        | _ -> ()

    // Composition-time only: constructor runs once at DI singleton
    // activation before HTTP binds, no ambient sync context to
    // deadlock against. Mirrors `TeamScopeResolver`'s pattern.
    let subscriptionId =
        notifications.Subscribe(NotificationKind.PlatformReservedScope, handle)
        |> Async.RunSynchronously

    /// Step 2a — read the cached active-team pointer or fault in via
    /// `ITeamStore.GetActiveTeam`. A storage failure surfaces as
    /// `SubjectResolutionFailed` (matching `TeamScopeResolver`'s
    /// posture — never collapse "storage outage" into "user has no
    /// active team").
    // `teamStore.Value` is safe inside this body: callers guard
    // entry on `supportsTeam`, which is `true` only when at least
    // one `SurfaceProfile.Team _` is in `surfaces`; the composition
    // root's invariant (validated by `SurfaceCoherenceValidator`
    // once Stream B.2 ships) is "Team in Surfaces → IShareTokenStore-
    // free `ITeamStore` registered". Defence-in-depth: an explicit
    // failure here beats a silent `None` propagating into the cache.
    let getActiveTeam userId = async {
        match teamStore with
        | None ->
            return
                Result.Error(
                    exn
                        "DefaultSubjectResolver.getActiveTeam invoked with no ITeamStore wired — composition-root invariant violated."
                )
        | Some store ->
            let key = cacheKey userId

            match cache.TryGetValue<string option> key with
            | true, cached -> return Ok cached
            | false, _ ->
                try
                    let! activeTeam = store.GetActiveTeam userId
                    let entry = cache.CreateEntry key
                    entry.SlidingExpiration <- TimeSpan.FromMinutes 5.0
                    entry.Value <- activeTeam
                    entry.Dispose()
                    return Ok activeTeam
                with ex ->
                    return Result.Error ex
    }

    interface IDisposable with
        // Shutdown only: DI registers the resolver as a singleton,
        // so Dispose runs once on app teardown — not from a
        // request thread.
        member _.Dispose() =
            notifications.Unsubscribe subscriptionId |> Async.RunSynchronously

    interface ISubjectResolver with
        member _.Resolve(request) = async {
            // Step 1 — share-token claim dominates.
            match request.Claim with
            | Some claim -> return Ok(Subject.ClaimBearer claim)
            | None ->
                match request.User with
                | Some user when not (AuthenticatedUser.isAnonymous user) ->
                    // Step 2 — authenticated subject.
                    if supportsTeam then
                        let! activeTeamResult = getActiveTeam user.UserId

                        match activeTeamResult with
                        | Result.Error ex ->
                            logError
                                $"DefaultSubjectResolver: ITeamStore.GetActiveTeam failed for user '{user.UserId}' — surfacing SubjectResolutionFailed instead of degrading to UnsupportedSubject."
                                ex

                            return Error(SubjectResolutionError.SubjectResolutionFailed ex.Message)
                        | Ok None ->
                            // No active team selected — graceful
                            // step 2b fall-through when User is
                            // in Surfaces; otherwise step 2c.
                            if supportsUser then
                                return Ok(Subject.AuthenticatedUser user.UserId)
                            else
                                return Error(SubjectResolutionError.UnsupportedSubject UserKind)
                        | Ok(Some teamId) ->
                            // Active-team pointer present —
                            // verify the user is STILL a
                            // member. Not cached: removal must
                            // take effect on the next request.
                            match teamStore with
                            | None ->
                                return
                                    Error(
                                        SubjectResolutionError.SubjectResolutionFailed
                                            "DefaultSubjectResolver: supportsTeam=true but no ITeamStore wired."
                                    )
                            | Some store ->
                                try
                                    let! role = store.GetMemberRole(teamId, user.UserId)

                                    match role with
                                    | Some _ -> return Ok(Subject.TeamMember(user.UserId, teamId))
                                    | None -> return Error(SubjectResolutionError.NotTeamMember teamId)
                                with ex ->
                                    return Error(SubjectResolutionError.SubjectResolutionFailed ex.Message)
                    elif supportsUser then
                        // Step 2b — Team not in Surfaces, User is.
                        return Ok(Subject.AuthenticatedUser user.UserId)
                    else
                        // Step 2c — deployment admits no
                        // authenticated-user-shape subject.
                        return Error(SubjectResolutionError.UnsupportedSubject UserKind)
                | _ ->
                    // Step 3 — anonymous session.
                    if supportsAnonymous then
                        let sessionId =
                            match request.SessionId with
                            | Some id when id <> "" -> id
                            | _ -> Guid.NewGuid().ToString()

                        return Ok(Subject.AnonymousSession sessionId)
                    else
                        return Error(SubjectResolutionError.UnsupportedSubject AnonymousKind)
        }
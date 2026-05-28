module ToolUp.Platform.StorageScopeResolver

open System
open Microsoft.Extensions.Caching.Memory
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.TeamManagement

/// Resolves storage scope from a pre-extracted `ScopeResolutionRequest`.
/// The interface has no `HttpContext` coupling — request extraction happens
/// upstream (typically in a Saturn middleware) so this contract is portable
/// across runtimes (Guiding Principle 12).
///
/// Implementations choose which request fields they read based on the
/// platform mode they serve. All return paths are async and return
/// `Result<StorageScope, ScopeResolutionError>` — no `failwith`, no exceptions
/// for expected error cases (unauthenticated, no active team, etc.).
type IStorageScopeResolver =
    abstract Resolve: ScopeResolutionRequest -> Async<Result<StorageScope, ScopeResolutionError>>

/// Anonymous mode resolver: session-scoped, no persistence.
/// Uses `SessionId` (from X-User-Id header) for the session identifier;
/// generates a fresh GUID if none provided.
type AnonymousScopeResolver() =
    interface IStorageScopeResolver with
        member _.Resolve(request) = async {
            let sessionId =
                match request.SessionId with
                | Some id when id <> "" -> id
                | _ -> Guid.NewGuid().ToString()

            return
                Ok {
                    ScopeId = sessionId
                    Container = $"session-{sessionId}"
                    Persist = false
                }
        }

/// AuthenticatedEphemeral mode: auth required, in-memory only.
/// Useful for trial accounts, compliance-sensitive analysis, pre-subscription.
type AuthenticatedEphemeralScopeResolver() =
    interface IStorageScopeResolver with
        member _.Resolve(request) = async {
            return
                match request.User with
                | Some user when not (AuthenticatedUser.isAnonymous user) ->
                    Ok {
                        ScopeId = user.UserId
                        Container = $"user-{user.UserId}"
                        Persist = false
                    }
                | _ -> Error NotAuthenticated
        }

/// Individual mode: auth required, data persists per-user.
type AuthenticatedScopeResolver() =
    interface IStorageScopeResolver with
        member _.Resolve(request) = async {
            return
                match request.User with
                | Some user when not (AuthenticatedUser.isAnonymous user) ->
                    Ok {
                        ScopeId = user.UserId
                        Container = $"user-{user.UserId}"
                        Persist = true
                    }
                | _ -> Error NotAuthenticated
        }

/// Team mode: auth required, data scoped to the user's active team, persistent.
/// Uses `IMemoryCache` to avoid hitting `ITeamStore` on every request for the
/// active-team pointer. Membership is validated on every resolve (not
/// cached) as a defense-in-depth measure — a user removed from their
/// active team sees `NotTeamMember` on the next request regardless of
/// cache state.
///
/// Cache invalidation is event-driven: the resolver subscribes to
/// `MembershipChanged` on the reserved `_platform` topic and evicts the
/// affected user's active-team cache entry on `Removed` and `ActiveTeamSet`
/// kinds (the only kinds that change *which* team is active for the
/// caller). `Added` and `RoleChanged` don't move the active-team pointer,
/// so they're informational here. `InvalidateUser` is preserved as a
/// thin shim that publishes a synthetic `ActiveTeamSet` event — callers
/// (and tests) that pre-date the event flow keep working.
///
/// `IDisposable` unsubscribes the channel handle on shutdown — DI
/// registers the resolver as a singleton, so disposal runs once on
/// app teardown.
type TeamScopeResolver
    (teamStore: ITeamStore, cache: IMemoryCache, notifications: INotificationChannel, ?logger: ILogger) =
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

    // Composition-time only: constructor runs once at DI singleton activation
    // before HTTP binds, no ambient sync context to deadlock against.
    let subscriptionId =
        notifications.Subscribe(NotificationKind.PlatformReservedScope, handle)
        |> Async.RunSynchronously

    /// Backwards-compatible shim. Pre-Phase-5d callers used this
    /// directly; now it publishes a synthetic `MembershipChanged`
    /// event so every subscriber (this resolver, downstream caches,
    /// and any future cross-instance companions) reacts uniformly.
    member _.InvalidateUser(userId: string) =
        let payload: MembershipChangedPayload = {
            TeamId = ""
            AffectedUserId = userId
            ChangeKind = MembershipChangeKind.ActiveTeamSet
            PublishedAt = DateTime.UtcNow
        }

        // Backwards-compat shim only — kept sync because pre-Phase-5d callers
        // (and the test pack) invoke it synchronously. No production caller
        // remains on the request thread.
        notifications.Publish(NotificationKind.PlatformReservedScope, MembershipChanged payload)
        |> Async.RunSynchronously

    interface IDisposable with
        // Shutdown only: DI registers the resolver as a singleton, so Dispose
        // runs once on app teardown — not from a request thread.
        member _.Dispose() =
            notifications.Unsubscribe subscriptionId |> Async.RunSynchronously

    interface IStorageScopeResolver with
        member _.Resolve(request) = async {
            match request.User with
            | None -> return Error NotAuthenticated
            | Some user when AuthenticatedUser.isAnonymous user -> return Error NotAuthenticated
            | Some user ->
                let key = cacheKey user.UserId

                // Step 1: resolve the user's active team (cache → store).
                // A store failure must NOT collapse into `None` — that
                // is indistinguishable from "user has no active team"
                // (a user-shaped error) and leaves the operator blind
                // to a storage outage. Surface it as
                // `ScopeResolutionFailed` (matching Step 2's posture)
                // and log at Error.
                let! activeTeamResult = async {
                    match cache.TryGetValue<string option> key with
                    | true, cached -> return Ok cached
                    | false, _ ->
                        try
                            let! activeTeam = teamStore.GetActiveTeam user.UserId
                            let entry = cache.CreateEntry key
                            entry.SlidingExpiration <- TimeSpan.FromMinutes 5.0
                            entry.Value <- activeTeam
                            entry.Dispose()
                            return Ok activeTeam
                        with ex ->
                            return Result.Error ex
                }

                match activeTeamResult with
                | Result.Error ex ->
                    logError
                        $"TeamScopeResolver: ITeamStore.GetActiveTeam failed for user '{user.UserId}' — surfacing ScopeResolutionFailed instead of degrading to NoActiveTeam (a storage outage must not look like a user-config error)."
                        ex

                    return Error(ScopeResolutionFailed ex.Message)
                | Ok None -> return Error NoActiveTeam
                | Ok(Some teamId) ->
                    // Step 2: verify the user is STILL a member. Not
                    // cached — RemoveMember must take effect on the
                    // next request. One blob read per team-scoped
                    // request; acceptable cost for the guarantee.
                    try
                        let! role = teamStore.GetMemberRole(teamId, user.UserId)

                        match role with
                        | Some _ ->
                            return
                                Ok {
                                    ScopeId = teamId
                                    Container = $"team-{teamId}"
                                    Persist = true
                                }
                        | None -> return Error(NotTeamMember teamId)
                    with ex ->
                        return Error(ScopeResolutionFailed ex.Message)
        }

/// Helpers for extracting `ScopeResolutionRequest` from an ASP.NET Core
/// `HttpContext`. These live in server code (not on the `IStorageScopeResolver`
/// interface) so the interface itself stays runtime-agnostic — Guiding
/// Principle 12.
module ScopeRequestExtractor =
    open Microsoft.AspNetCore.Http

    let private sessionIdFrom (ctx: HttpContext) =
        match ctx.Request.Headers.TryGetValue "X-User-Id" with
        | true, values when values.Count > 0 -> Some(string values[0])
        | _ -> None

    let private headersOf (ctx: HttpContext) =
        ctx.Request.Headers
        |> Seq.map (fun kv -> kv.Key.ToLowerInvariant(), string kv.Value)
        |> Map.ofSeq

    /// Build a `ScopeResolutionRequest` from the current `HttpContext`.
    /// Uses `IAuthProvider.GetUser` (lenient: returns anonymous on no creds).
    /// Authenticated-mode resolvers detect anonymous users and return
    /// `NotAuthenticated`.
    let fromHttpContext (ctx: HttpContext) (authProvider: IAuthProvider) = async {
        // Phase 11.C.5 Tier 3 — wrap `HttpContext` into `RequestContext`
        // at the single IAuthProvider boundary.
        let! user = authProvider.GetUser(ToolUp.Platform.RequestContextBuilder.ofHttpContext ctx)

        return {
            User = Some user
            SessionId = sessionIdFrom ctx
            Headers = headersOf ctx
        }
    }

    /// Convenience: resolve scope for the current request using DI-resolved
    /// `IAuthProvider` and `IStorageScopeResolver`. Returns the full Result so
    /// callers can translate `ScopeResolutionError` into HTTP responses.
    let resolveForContext (ctx: HttpContext) = async {
        let authProv =
            ctx.RequestServices.GetService(typeof<IAuthProvider>) :?> IAuthProvider

        let resolver =
            ctx.RequestServices.GetService(typeof<IStorageScopeResolver>) :?> IStorageScopeResolver

        let! request = fromHttpContext ctx authProv
        return! resolver.Resolve request
    }

    /// Resolve just the userId for the current request (lenient; returns
    /// `"anonymous"` if no credentials or any error). Replaces the old
    /// synchronous `resolveUserId` helper.
    let userId (ctx: HttpContext) = async {
        try
            let authProv =
                ctx.RequestServices.GetService(typeof<IAuthProvider>) :?> IAuthProvider

            let! user = authProv.GetUser(ToolUp.Platform.RequestContextBuilder.ofHttpContext ctx)
            return user.UserId
        with _ ->
            return
                match ctx.Request.Headers.TryGetValue "X-User-Id" with
                | true, values when values.Count > 0 -> string values[0]
                | _ -> "anonymous"
    }
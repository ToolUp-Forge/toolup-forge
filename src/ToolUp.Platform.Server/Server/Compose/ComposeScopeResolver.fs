module ToolUp.Platform.ComposeScopeResolver

open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.SurfaceEnforcement

// ─── compose phase: scope + access-context wiring ────────────────────
//
// Resolves the four substrate values that drive per-request scope /
// subject / surface / migrator wiring (`IStorageScopeResolver`,
// `ISubjectResolver`, `SurfaceRequirementRegistry`,
// `IAnonymousSessionMigrator`) and registers them alongside the
// scoped `AccessContext` factory + `IHttpContextAccessor` + session.
// Extracted from `compose` for the per-concern subdivision (Phase 15e
// follow-up tail). Takes the exact substrate values the inline
// definition captured and returns the same shape. Zero behaviour
// change.

/// Build + register the per-request scope-resolution substrate:
///
///   - `IStorageScopeResolver` — picked from the deployment's
///     `Surfaces`: `TeamScopeResolver` over the shared `ITeamStore`
///     when any `Team` surface is declared; otherwise the dominant
///     `AuthenticatedUser` surface's `Persistence` chooses
///     `AuthenticatedEphemeralScopeResolver` / `AuthenticatedScopeResolver`;
///     otherwise `AnonymousScopeResolver`. Byte-identical mapping to
///     the retiring `ServerConfig.legacyMode` 5-arm dispatch for every
///     single-surface deployment.
///   - `ISubjectResolver` — `DefaultSubjectResolver` over
///     `config.Surfaces`. Post-Phase-66-Stream-A.2 the canonical shape
///     lives on `ServerConfig` directly (no per-Mode bridge derivation).
///   - `SurfaceRequirementRegistry` — bridge entries from
///     `fromServerConfig` overlaid with per-module
///     `DefaultSurfaceRequirement` × `RoutePrefixes`
///     (`moduleSurfaceDefaults`) and per-route exact overrides
///     (`routeSurfaceOverrides`). Modules predating B.3 contribute
///     empty overlays, so the bridge stays the only contributor —
///     byte-identical behaviour.
///   - `IAnonymousSessionMigrator` — `NoOpAnonymousSessionMigrator` by
///     default; custom migrators arrive via
///     `ServerApp.withSubjectMigrator` and compose multiple per-module
///     migrators with `AnonymousSessionMigrator.compose` upstream.
///
/// Then registers the scoped `AccessContext` factory that reads the
/// pre-resolved user / scope / permissions / subject from
/// `HttpContext.Items` (populated by `ScopeResolutionMiddleware`
/// earlier in the pipeline), plus
/// `IHttpContextAccessor` + ASP.NET Core `Session`.
let registerScopeResolution
    (services: IServiceCollection)
    (config: ServerConfig)
    (teamStoreOpt: TeamStore option)
    (resolvedNotificationChannel: INotificationChannel)
    (resolvedLogger: ILogger)
    (moduleSurfaceDefaults: (string * SurfaceRequirement) list)
    (routeSurfaceOverrides: ((string * string) * SurfaceRequirement) list)
    (subjectMigratorOverride: IAnonymousSessionMigrator option)
    : unit =

    let scopeResolver: IStorageScopeResolver =
        if DeploymentConfig.hasTeamScope config then
            new TeamScopeResolver(
                teamStoreOpt.Value :> ITeamStore,
                new MemoryCache(MemoryCacheOptions()),
                resolvedNotificationChannel,
                resolvedLogger
            )
        else
            let pickUserPersistence =
                config.Surfaces
                |> List.tryPick (function
                    | SurfaceProfile.AuthenticatedUser cfg -> Some cfg.Persistence
                    | _ -> None)

            match pickUserPersistence with
            | Some Ephemeral -> AuthenticatedEphemeralScopeResolver()
            | Some Persistent -> AuthenticatedScopeResolver()
            | None -> AnonymousScopeResolver()

    // Phase 66 Stream A.6 + A.2 — `ISubjectResolver` +
    // `SurfaceRequirementRegistry` wiring. Both Singletons sit
    // alongside the retiring `IStorageScopeResolver` until Stream A.7
    // migrates the remaining handlers (`DevDiagnosticsHandler` /
    // `DiagnosticBundleHandler`) and the legacy registration retires.
    //
    // The resolver reads `config.Surfaces` directly post Stream A.2
    // — the per-Mode bridge derivation that A.6 stood up is gone
    // because the canonical shape now lives on `ServerConfig`.
    let subjectResolver: ISubjectResolver =
        new DefaultSubjectResolver.DefaultSubjectResolver(
            config.Surfaces,
            teamStoreOpt |> Option.map (fun ts -> ts :> ITeamStore),
            new MemoryCache(MemoryCacheOptions()),
            resolvedNotificationChannel,
            resolvedLogger
        )
        :> ISubjectResolver

    // **Surface-requirement registry.** Two-layer merge:
    //   1. `fromServerConfig` produces the bridge entries the SDK has
    //      historically required (CSRF token path, SSE carve-outs when
    //      `SseAuthMode = QueryParamFallback`, peer-route prefixes,
    //      anonymous-deployment `/api/` catch-all) — Stream B.4 retires
    //      most of these in favour of per-module declarations.
    //   2. `merge` overlays the per-module `DefaultSurfaceRequirement` ×
    //      `RoutePrefixes` (`moduleSurfaceDefaults`) and per-route
    //      exact overrides (`routeSurfaceOverrides`) accumulated by
    //      `ServerApp.addModule` from each `ServerModule`'s Phase 66
    //      Stream B.3 declarations. Module exact overrides supersede
    //      base exact entries; module prefix defaults append to the
    //      base `ModulePrefixes` list and participate in
    //      longest-prefix-wins resolution.
    //
    // Modules predating B.3 declare neither `RoutePrefixes` nor
    // `RouteSurfaceRequirements`, so the overlay is empty for them and
    // the bridge stays the only contributor — byte-identical behaviour.
    let bridgeRegistry: SurfaceRequirementRegistry =
        SurfaceRequirementRegistry.fromServerConfig config
        |> SurfaceRequirementRegistry.merge moduleSurfaceDefaults routeSurfaceOverrides

    // Phase 66 Stream C.1 (substrate) — resolve the
    // `IAnonymousSessionMigrator` singleton. `None` (the default)
    // registers `NoOpAnonymousSessionMigrator` so the trigger-detection
    // path in the middleware (deferred — lands in the C.1 continuation)
    // reads the singleton uniformly without a None-branch. Custom
    // migrators arrive via `ServerApp.withSubjectMigrator`; compose
    // multiple per-module migrators with `AnonymousSessionMigrator.compose`
    // upstream of the call.
    let subjectMigrator: IAnonymousSessionMigrator =
        subjectMigratorOverride
        |> Option.defaultWith (fun () -> NoOpAnonymousSessionMigrator() :> IAnonymousSessionMigrator)

    services
        .AddSingleton<ISubjectResolver>(subjectResolver)
        .AddSingleton<SurfaceRequirementRegistry>(bridgeRegistry)
        .AddSingleton<IStorageScopeResolver>(scopeResolver)
        .AddSingleton<IAnonymousSessionMigrator>(subjectMigrator)
        .AddScoped<AccessContext>(fun sp ->
            // Read pre-resolved user, scope, and permissions from
            // HttpContext.Items (populated by ScopeResolutionMiddleware).
            // The factory itself stays synchronous — any async
            // resolution (auth, team lookup, permission fetch)
            // ran earlier in the request pipeline.
            let httpCtxAccessor =
                sp.GetService(typeof<IHttpContextAccessor>) :?> IHttpContextAccessor

            match httpCtxAccessor.HttpContext with
            | null ->
                // Out-of-request fallback (e.g. resolved during
                // startup, hosted-service composition). No HttpContext
                // means no resolved Subject, so synthesise the
                // anonymous floor directly — there is no caller to
                // attribute, and an unrestricted anonymous context is
                // the safe default for the rare out-of-request resolve.
                AccessContext.unrestricted (AnonymousSession "anonymous")
            | ctx ->
                let userId =
                    match ctx.Items.TryGetValue "ToolUp.UserId" with
                    | true, (:? string as id) -> id
                    | _ -> "anonymous"

                let teamId =
                    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                    | true, (:? StorageScope as scope) when scope.Container.StartsWith "team-" -> Some scope.ScopeId
                    | _ -> None

                let modulePermissions =
                    match ctx.Items.TryGetValue "ToolUp.ModulePermissions" with
                    | true, (:? Map<string, ModulePermission list> as perms) -> perms
                    | _ -> Map.empty

                let hiddenModules =
                    match ctx.Items.TryGetValue "ToolUp.HiddenModules" with
                    | true, (:? Set<string> as hidden) -> hidden
                    | _ -> Set.empty

                let platformRole =
                    match ctx.Items.TryGetValue "ToolUp.PlatformRole" with
                    | true, (:? PlatformRole as role) -> Some role
                    | _ -> None

                // Phase 66 A.6 stashed a resolved `Subject` on
                // `HttpContext.Items["ToolUp.Subject"]` upstream
                // (`ScopeResolutionMiddleware` → `ISubjectResolver`).
                // Prefer that when present; otherwise reconstruct the
                // Subject from the per-request `userId` / `teamId` we
                // already resolved above, branched on the deployment's
                // surfaces. `hasTeamScope` + `Some teamId` ⇒ `TeamMember`,
                // `requiresAnyAuth` ⇒ `AuthenticatedUser`, else
                // `AnonymousSession`. For a mixed-mode deployment the
                // per-request `teamId` decides team membership, not the
                // deployment-wide dominant surface.
                let subject =
                    match ctx.Items.TryGetValue "ToolUp.Subject" with
                    | true, (:? Subject as s) -> s
                    | _ ->
                        match teamId with
                        | Some tid when DeploymentConfig.hasTeamScope config -> TeamMember(userId, tid)
                        | _ when DeploymentConfig.requiresAnyAuth config -> AuthenticatedUser userId
                        | _ -> AnonymousSession userId

                {
                    UserId = userId
                    TeamId = teamId
                    Subject = subject
                    ModulePermissions = modulePermissions
                    HiddenModules = hiddenModules
                    PlatformRole = platformRole
                })
        .AddHttpContextAccessor()
        .AddSession()
    |> ignore
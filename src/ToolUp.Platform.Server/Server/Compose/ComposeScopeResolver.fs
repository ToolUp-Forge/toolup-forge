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
///   - `IStorageScopeResolver` — picked from `ServerConfig.legacyMode`:
///     `AnonymousScopeResolver` / `AuthenticatedEphemeralScopeResolver`
///     / `AuthenticatedScopeResolver` for the no-team modes, or a
///     `TeamScopeResolver` over the shared `ITeamStore` for the team
///     modes.
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
        match ServerConfig.legacyMode config with
        | Anonymous -> AnonymousScopeResolver()
        | AuthenticatedEphemeral -> AuthenticatedEphemeralScopeResolver()
        | Individual -> AuthenticatedScopeResolver()
        | Team
        | MultiTeam ->
            new TeamScopeResolver(
                teamStoreOpt.Value :> ITeamStore,
                new MemoryCache(MemoryCacheOptions()),
                resolvedNotificationChannel,
                resolvedLogger
            )

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
                // startup, hosted-service composition). Synthesise a
                // `Subject` via the transitional `legacyMode` bridge
                // until Stream B.5 finishes rewriting downstream
                // consumers to read `Subject` directly without the
                // PlatformMode-shaped intermediary.
                AccessContext.unrestricted (Subject.fromLegacyMode (ServerConfig.legacyMode config) "anonymous" None)
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

                let platformRole =
                    match ctx.Items.TryGetValue "ToolUp.PlatformRole" with
                    | true, (:? PlatformRole as role) -> Some role
                    | _ -> None

                // Phase 66 A.6 stashed a resolved `Subject` on
                // `HttpContext.Items["ToolUp.Subject"]` upstream
                // (`ScopeResolutionMiddleware` → `ISubjectResolver`).
                // Prefer that when present; otherwise fall back to
                // the legacy-shape bridge driven by the dominant
                // `PlatformMode` derived from `Surfaces` (Stream B.5
                // rewrites the remaining call sites).
                let subject =
                    match ctx.Items.TryGetValue "ToolUp.Subject" with
                    | true, (:? Subject as s) -> s
                    | _ -> Subject.fromLegacyMode (ServerConfig.legacyMode config) userId teamId

                {
                    UserId = userId
                    TeamId = teamId
                    Subject = subject
                    ModulePermissions = modulePermissions
                    PlatformRole = platformRole
                })
        .AddHttpContextAccessor()
        .AddSession()
    |> ignore
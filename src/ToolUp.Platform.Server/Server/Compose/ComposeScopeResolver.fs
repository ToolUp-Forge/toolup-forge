module ToolUp.Platform.Compose.ComposeScopeResolver

open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Scope.StorageScopeResolver
open ToolUp.Platform.Config.TeamManagement
open ToolUp.Platform.Scope.SurfaceEnforcement
open ToolUp.Platform.Config
open ToolUp.Platform.Scope
open ToolUp.Platform.Jobs

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

/// The `AccessContext` a request carries: the principal
/// `ScopeResolutionMiddleware` stamped ONCE under `AccessContext.ItemsKey`
///, or `None` when no principal was resolved — the middleware did
/// not run, or a fault before its first stamp fell through. Nothing is
/// substituted for a stamp the middleware did not write. The scoped factory
/// below is this function over the request's context; it is separate so the
/// reading is testable without composing a server.
let internal accessContextOf (ctx: HttpContext) : AccessContext option = AccessContext.ofItems ctx.Items

/// Build + register the per-request scope-resolution substrate:
///
///   - `IStorageScopeResolver` — picked from the deployment's
///     `Surfaces`: `TeamScopeResolver` over the shared `ITeamStore`
///     when any `Team` surface is declared; otherwise the dominant
///     `AuthenticatedUser` surface's `Persistence` chooses
///     `AuthenticatedEphemeralScopeResolver` / `AuthenticatedScopeResolver`;
///     otherwise `AnonymousScopeResolver`. Byte-identical mapping to
///     the retiring ServerConfig.legacyMode 5-arm dispatch for every
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
    (moduleRoutePrefixes: (string * string) list)
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

    // Phase 637 — the module-route attribution registry, built from the
    // SAME `ServerModule.RoutePrefixes` declarations `moduleSurfaceDefaults`
    // is derived from, but keyed by module id: opt-in visibility route
    // hardening has to answer "which module owns this path", and the
    // surface registry has already discarded that by the time it is built.
    // Registered unconditionally (it is inert data, and a deployment whose
    // modules declare no prefixes gets `Prefixes = []`); the MIDDLEWARE
    // that reads it is registered only under `EnforcedModuleVisibility`.
    let moduleRouteRegistry =
        ModuleVisibilityRoutes.ModuleRouteRegistry.create config.ModuleNames moduleRoutePrefixes

    services
        .AddSingleton<ISubjectResolver>(subjectResolver)
        .AddSingleton<SurfaceRequirementRegistry>(bridgeRegistry)
        .AddSingleton<ModuleVisibilityRoutes.ModuleRouteRegistry>(moduleRouteRegistry)
        .AddSingleton<IStorageScopeResolver>(scopeResolver)
        .AddSingleton<DeclaredPrincipalResolver>(DeclaredPrincipalResolver(scopeResolver.Resolve)) // Phase 1005
        .AddSingleton<IAnonymousSessionMigrator>(subjectMigrator)
        .AddScoped<AccessContext>(fun sp ->
            // The principal ScopeResolutionMiddleware stamped,
            // or NO context: the factory yields null (which `GetService`
            // returns as no service) for a request with no resolved
            // principal and for a resolve outside any request. It never
            // synthesises a default; `AccessContext.tryResolve` is the
            // typed read, and work outside a request names
            // `AccessContext.system` / `AccessContext.carrying` instead.
            let httpCtxAccessor =
                sp.GetService(typeof<IHttpContextAccessor>) :?> IHttpContextAccessor

            match httpCtxAccessor.HttpContext with
            | null -> Unchecked.defaultof<AccessContext>
            | ctx ->
                match accessContextOf ctx with
                | Some access -> access
                | None -> Unchecked.defaultof<AccessContext>)
        .AddHttpContextAccessor()
        .AddSession()
    |> ignore
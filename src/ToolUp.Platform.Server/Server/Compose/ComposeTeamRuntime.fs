module ToolUp.Platform.ComposeTeamRuntime

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.TeamManagement

// ─── compose phase: team / invite / permission stores + validators ───
//
// Per-tenant scope substrate (`ITeamStore`, `IPendingInviteStore`,
// `IPermissionStore`) plus the post-`PlatformAdminStore` config
// validators that need a constructed admin store / team store before
// they can register, and the one-shot `BootstrapTeam` runtime that
// seeds an initial team from `TOOLUP_INITIAL_TEAM_NAME`. Extracted
// from `compose` for the per-concern subdivision (Phase 15e follow-up
// tail). Zero behaviour change.

/// Create + register `ITeamStore`, `IPendingInviteStore`,
/// `IPermissionStore`. The team store is shared with the
/// `TeamScopeResolver` downstream (extracted to
/// `ComposeScopeResolver`) — the same instance is used both places so
/// `SetActiveTeam`'s cache invalidation lands in the right cache.
/// `IPendingInviteStore` defaults to `InMemoryPendingInviteStore` over
/// the resolved `IBlobStorage`; a custom implementation supplied via
/// `ServerApp.withPendingInviteStore` replaces it. `IPermissionStore`
/// is the Phase 4 RBAC backing — registered unconditionally so
/// request handlers can resolve it regardless of mode.
///
/// Returns the optional `TeamStore` instance so the downstream
/// `BootstrapTeam` runtime, scope-resolver registration, and per-Mode
/// surface validators can share the same object.
let registerTeamPermissionStores
    (services: IServiceCollection)
    (config: ServerConfig)
    (resolvedBlobStorage: IBlobStorage)
    (resolvedNotificationChannel: INotificationChannel)
    (resolvedLogger: ILogger)
    (auditLog: IAuditLog)
    (pendingInviteStoreOverride: IPendingInviteStore option)
    : TeamStore option =

    let teamStoreOpt =
        if DeploymentConfig.hasTeamScope config then
            Some(TeamStore(resolvedBlobStorage, resolvedNotificationChannel))
        else
            None

    match teamStoreOpt with
    | Some ts ->
        // Phase 131 — wrap in the id-sanitising decorator so every
        // team/user id that becomes a blob-key segment is validated on
        // writes (path-traversal / reserved-scope rejection), including
        // for a consumer-supplied store.
        services.AddSingleton<ITeamStore>(StoreIdSanitising.SanitisingTeamStore(ts :> ITeamStore) :> ITeamStore)
        |> ignore
    | None -> ()

    // Phase 5h — register `IPendingInviteStore`. Default
    // `InMemoryPendingInviteStore` over the resolved `IBlobStorage`
    // preserves the single-instance blob+lock+cache impl carried
    // forward from Phase 3d; a custom implementation supplied via
    // `ServerApp.withPendingInviteStore` replaces it. Registered
    // unconditionally so `TeamInvitationHandler` resolves the
    // interface from DI without a mode-conditional fallback —
    // non-team modes never call into the store.
    // Phase 547 — pass the resolved `IAuditLog` so the default store emits
    // `TeamInviteExpired` under `team-{TeamId}` scope on every expiry sweep
    // (GP 6). A consumer-supplied override owns its own audit wiring; the
    // default single-instance store gets the log via its 3-arg constructor.
    let resolvedPendingInviteStore: IPendingInviteStore =
        pendingInviteStoreOverride
        |> Option.defaultWith (fun () ->
            ToolUp.Platform.Teams.InMemoryPendingInviteStore(resolvedBlobStorage, resolvedLogger, Some auditLog)
            :> IPendingInviteStore)

    services.AddSingleton<IPendingInviteStore>(resolvedPendingInviteStore) |> ignore

    // PermissionStore backs Phase 4 RBAC. Registered unconditionally
    // so request handlers can resolve it regardless of mode; the
    // per-request middleware only populates `ModulePermissions` for
    // Team-scoped requests, so non-Team modes behave as unrestricted
    // (opt-in RBAC).
    // Phase 131 — wrap the permission store in the id-sanitising
    // decorator (same write-seam rejection as the team store above).
    // Phase 551 — and, outside that, the grant-policy write guard, which
    // refuses a grant that does not satisfy the target module's declared
    // `GrantPolicy`.
    //
    // Registered through a FACTORY rather than an eager instance for one
    // reason: the `ModuleGrantPolicyRegistry` is built from the composed
    // module set in `ServerApp.run`, which lands its registration through
    // the same `ServiceConfig` hook but not necessarily before this line
    // runs. A factory resolves at first request, by which point both are
    // present.
    //
    // When no module declares a policy the registry is never registered,
    // the lookup misses, and the UNDECORATED store is returned — so the
    // decorator does not exist at all in a deployment that does not use
    // it (GP 13), rather than existing and always answering yes.
    //
    // Phase 555 — dual control (the two-person rule) sits BETWEEN the
    // sanitising store and the Phase 551 grant-policy guard, and that
    // position is the whole of 555.D's "module policy refusal pre-empts
    // queueing": a write reaches the module's declared `GrantPolicy`
    // FIRST, so one the module would never admit is refused outright
    // rather than parked awaiting an approval that could not have applied
    // it. Reversing the two would queue writes guaranteed to fail at the
    // end of a ceremony two administrators performed.
    //
    // Registered only under `DualControl` (GP 13): a `SingleAdmin`
    // deployment builds no approval store, wraps no decorator and reads
    // no `admin-mutations/` blob — the chain below is the exact one that
    // shipped before this phase.
    let resolveGrantRegistry (sp: System.IServiceProvider) =
        match sp.GetService(typeof<GrantPolicyGuard.ModuleGrantPolicyRegistry>) with
        | :? GrantPolicyGuard.ModuleGrantPolicyRegistry as r -> r
        | _ -> GrantPolicyGuard.ModuleGrantPolicyRegistry.empty

    let sanitisedPermissionStore () =
        StoreIdSanitising.SanitisingPermissionStore(PermissionStore(resolvedBlobStorage, resolvedLogger))
        :> IPermissionStore

    /// The acting administrator for the live request. `None` when no
    /// identity can be resolved — the decorator then REFUSES the gated
    /// write rather than parking it, because a two-person rule whose
    /// first person is unknown cannot prove distinctness.
    let resolveProposer (sp: System.IServiceProvider) () =
        match sp.GetService(typeof<Microsoft.AspNetCore.Http.IHttpContextAccessor>) with
        | :? Microsoft.AspNetCore.Http.IHttpContextAccessor as accessor when not (isNull (box accessor.HttpContext)) ->
            match accessor.HttpContext.Items.TryGetValue "ToolUp.AccessContext" with
            | true, (:? AccessContext as ac) when not (System.String.IsNullOrWhiteSpace ac.UserId) -> Some ac.UserId
            | _ ->
                match accessor.HttpContext.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) when not (System.String.IsNullOrWhiteSpace id) -> Some id
                | _ -> None
        | _ -> None

    match config.AdminMutationPolicy with
    | AdminMutationPolicy.SingleAdmin -> ()
    | AdminMutationPolicy.DualControl settings ->
        let approvals =
            AdminMutationApproval.BlobAdminMutationApprovalStore(resolvedBlobStorage, resolvedLogger)
            :> AdminMutationApproval.IAdminMutationApprovalStore

        services.AddSingleton<AdminMutationApproval.IAdminMutationApprovalStore>(approvals)
        |> ignore

        // Registered as the CONCRETE type as well as inside the
        // `IPermissionStore` chain, and resolved from there rather than
        // constructed twice, so an admin approval surface drives the same
        // instance the write path parked into.
        services.AddSingleton<AdminMutationApproval.DualControlPermissionStore>(fun (sp: System.IServiceProvider) ->
            let registry = resolveGrantRegistry sp

            let isPolicyBearing moduleName =
                GrantPolicyGuard.ModuleGrantPolicyRegistry.resolve registry moduleName
                <> GrantPolicy.AdminDiscretion

            AdminMutationApproval.DualControlPermissionStore(
                sanitisedPermissionStore (),
                settings,
                approvals,
                isPolicyBearing,
                resolveProposer sp,
                (fun () -> System.DateTimeOffset.UtcNow),
                auditLog
            ))
        |> ignore

    services.AddSingleton<IPermissionStore>(fun (sp: System.IServiceProvider) ->
        let inner =
            match config.AdminMutationPolicy with
            | AdminMutationPolicy.SingleAdmin -> sanitisedPermissionStore ()
            | AdminMutationPolicy.DualControl _ ->
                match sp.GetService(typeof<AdminMutationApproval.DualControlPermissionStore>) with
                | :? AdminMutationApproval.DualControlPermissionStore as d -> d :> IPermissionStore
                // Unreachable — the registration above runs whenever the
                // mode is `DualControl`. Falling back to the undecorated
                // store would silently disable the control, so the miss is
                // logged rather than passed over in silence.
                | _ ->
                    resolvedLogger.Error(
                        "ComposeTeamRuntime: AdminMutationPolicy is DualControl but no DualControlPermissionStore is registered; permission writes are NOT under dual control.",
                        None
                    )

                    sanitisedPermissionStore ()

        let registry = resolveGrantRegistry sp

        if GrantPolicyGuard.ModuleGrantPolicyRegistry.isEmpty registry then
            inner
        else
            let guarded =
                GrantPolicyGuard.GrantPolicyPermissionStore(inner, registry, auditLog) :> IPermissionStore

            // Phase 556 — the grant-event notice loop, OUTSIDE the write
            // guard because it must observe what was actually written: a
            // grant 551 refused, or 555 parked, is not a grant anybody
            // should be told they hold. It observes only — it never
            // refuses, never rewrites a result, and swallows every
            // delivery failure, because a notification outage must not
            // fail a permission write.
            //
            // Composed only when a channel is resolvable, on top of the
            // non-empty-registry condition that already guards this whole
            // branch: an `AdminDiscretion`-only deployment reaches neither
            // decorator and pays for neither (GP 13).
            match sp.GetService(typeof<INotificationChannel>) with
            | :? INotificationChannel as channel when not (isNull (box channel)) ->
                let settings =
                    match sp.GetService(typeof<GrantNotification.GrantNotificationSettings>) with
                    | :? GrantNotification.GrantNotificationSettings as s -> s
                    | _ -> GrantNotification.GrantNotificationSettings.defaults

                GrantNotification.GrantNotificationObserver(
                    guarded,
                    registry,
                    channel,
                    settings,
                    resolvedLogger,
                    resolveProposer sp,
                    (fun () -> System.DateTimeOffset.UtcNow),
                    Async.Start
                )
                :> IPermissionStore
            | _ -> guarded)
    |> ignore

    teamStoreOpt

/// Phase 5f + Phase 3d + Phase 66 Stream B.2 — post-`PlatformAdminStore`
/// configuration validators. Registered here (rather than inside
/// `registerFirstPartyConfigValidators` upstream) because they need
/// references constructed in this part of `compose` (the bootstrapped
/// `platformAdminStore`, the original `authProvider: IAuthProvider
/// option`, the accumulated `moduleSurfaceDefaults` /
/// `routeSurfaceOverrides` / decorator-chain length). The aggregator
/// collects every `AddSingleton<IConfigValidator>` regardless of
/// registration site, so they run in the same preflight pass as the
/// first-party set.
let registerPostBootstrapValidators
    (services: IServiceCollection)
    (config: ServerConfig)
    (authProvider: IAuthProvider option)
    (platformAdminStore: IPlatformAdminStore)
    (moduleSurfaceDefaults: (string * SurfaceRequirement) list)
    (routeSurfaceOverrides: ((string * string) * SurfaceRequirement) list)
    (shareTokenStoreDecoratorCount: int)
    : unit =
    services.AddSingleton<ConfigValidation.IConfigValidator>(
        TeamCreationPolicyValidator.TeamCreationPolicyValidator(config, platformAdminStore)
        :> ConfigValidation.IConfigValidator
    )
    |> ignore

    services.AddSingleton<ConfigValidation.IConfigValidator>(
        BootstrapTeamValidator.BootstrapTeamValidator() :> ConfigValidation.IConfigValidator
    )
    |> ignore

    // Phase 3d / Cluster A4 — PendingInviteStore single-instance
    // enforcement. Single-instance-only by design (process-local
    // SemaphoreSlim + full-blob overwrite + per-process 30s read
    // cache); ReplicaCount > 1 deployments using the
    // IssuePendingInviteByEmail surface silently lose updates +
    // double-apply auto-joins. Warning (not Error) because the
    // link-based invitation flow is unaffected — only the
    // pending-by-email surface corrupts.
    services.AddSingleton<ConfigValidation.IConfigValidator>(
        ToolUp.Platform.Teams.PendingInviteStoreInstanceValidator.PendingInviteStoreInstanceValidator(config)
        :> ConfigValidation.IConfigValidator
    )
    |> ignore

    // Phase 66 Stream B.2 — surface coherence validator. Needs the
    // ORIGINAL `authProvider: IAuthProvider option` (to fire Rule 8
    // on "no provider declared" vs the resolved-with-default `auth`
    // the upstream registration captured) plus the accumulated
    // `moduleSurfaceDefaults` / `routeSurfaceOverrides` /
    // decorator-chain length.
    services.AddSingleton<ConfigValidation.IConfigValidator>(
        SurfaceCoherenceValidator.SurfaceCoherenceValidator(
            config,
            authProvider,
            moduleSurfaceDefaults,
            routeSurfaceOverrides,
            shareTokenStoreDecoratorCount
        )
        :> ConfigValidation.IConfigValidator
    )
    |> ignore

/// Phase 5f — one-shot bootstrap of the initial team from
/// `TOOLUP_INITIAL_TEAM_NAME` (+ optional `TOOLUP_INITIAL_TEAM_ID`).
/// Runs only when an `ITeamStore` is registered (Team / MultiTeam
/// modes) AND the env var is set. Idempotent against an
/// already-populated team store. Must run AFTER the admin bootstrap
/// (in `ComposeAuth.registerPlatformAdminStore`) so the bootstrap
/// admin user-id (re-read from `TOOLUP_INITIAL_PLATFORM_ADMIN` inside
/// `BootstrapTeam.bootstrap`) exists by the time we add it as Owner.
/// Synchronous wait keeps the first request from racing the
/// bootstrap; failures log at `Error` and do not abort startup.
let runBootstrapTeam (teamStoreOpt: TeamStore option) (resolvedLogger: ILogger) (auditLog: IAuditLog) : unit =
    match teamStoreOpt with
    | Some ts ->
        try
            BootstrapTeam.bootstrap resolvedLogger auditLog (ts :> ITeamStore)
            |> Async.RunSynchronously
        with ex ->
            resolvedLogger.Error(sprintf "[BootstrapTeam] Bootstrap aborted: %s" ex.Message, Some ex)
    | None -> ()
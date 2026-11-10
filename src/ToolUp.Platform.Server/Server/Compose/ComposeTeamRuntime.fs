module ToolUp.Platform.Compose.ComposeTeamRuntime

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Scope.PermissionStore
open ToolUp.Platform.Config.TeamManagement
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Scope
open ToolUp.Platform.Scope.Teams
open ToolUp.Platform.Jobs

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
/// `IPermissionStore`. The team store is shared with the subject resolver
/// downstream (`ComposeScopeResolver`) — the same instance is used both
/// places so `SetActiveTeam`'s cache invalidation lands in the right cache.
/// `IPendingInviteStore` defaults to `InMemoryPendingInviteStore` over
/// the resolved `IBlobStorage` for a single replica, and (Phase 5h) to
/// the ETag-based `BlobPendingInviteStore` for `ReplicaCount > 1` when
/// that storage implements `IConditionalBlobStorage`; a custom
/// implementation supplied via `ServerApp.withPendingInviteStore`
/// replaces either. The `PendingInviteStoreInstanceValidator` is
/// registered here too, over the store actually resolved. `IPermissionStore`
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

    // Phase 547.C — opt-in inviter notification on pending-invite
    // expiry. Wired only when `ServerConfig.NotifyInviterOnInviteExpiry`
    // is `true` (default `false`, GP 13); the store guards each
    // invocation, so a transport failure degrades to a `Warn` and never
    // fails a sweep. Publishes a `TransactionalEmail` addressed to the
    // inviter under the team scope — with no Email-kind
    // `INotificationSink` composed the envelope reaches no sink
    // (transactional kinds are SSE-filtered) and the publish is a no-op.
    let inviteExpiryNotifier: ((string * PendingInviteByEmail) list -> Async<unit>) option =
        if config.NotifyInviterOnInviteExpiry then
            Some(fun expired -> async {
                for email, entry in expired do
                    let! teamName = async {
                        match teamStoreOpt with
                        | Some ts ->
                            match! (ts :> ITeamStore).GetTeam(TeamId entry.TeamId) with
                            | Some info -> return info.Name
                            | None -> return entry.TeamId
                        | None -> return entry.TeamId
                    }

                    let subject = sprintf "Your invitation to %s expired unconsumed" teamName

                    let body =
                        sprintf
                            "Your invitation for %s to join %s as %s expired on %s without being used. \
                             Re-issue it from the Pending Invites panel if it is still wanted."
                            email
                            teamName
                            (TeamRoles.displayName entry.Role)
                            (entry.ExpiresAt.ToString "yyyy-MM-dd")

                    do!
                        resolvedNotificationChannel.Publish(
                            ScopeKey.container (ScopeKey.Team(TeamId entry.TeamId)),
                            TransactionalEmail {
                                Recipients = [ RecipientId.User(UserId entry.InviterUserId) ]
                                Content = InlineEmail(subject, body, None)
                                CorrelationId = Some $"invite-expiry:{entry.TeamId}:{email}:{entry.ExpiresAt.Ticks}"
                            }
                        )
            })
        else
            None

    // Phase 5h — register `IPendingInviteStore`. A custom implementation
    // supplied via `ServerApp.withPendingInviteStore` always wins.
    // Otherwise the default is auto-selected on the same gate the
    // validator reads: `ReplicaCount > 1` picks the ETag-based
    // `BlobPendingInviteStore` — but ONLY when the resolved `IBlobStorage`
    // implements `IConditionalBlobStorage` (the local, Azure, S3 and GCS
    // backends do; a consumer-supplied backend may not). A backend
    // without conditional writes falls back to the single-instance
    // `InMemoryPendingInviteStore` and `PendingInviteStoreInstanceValidator`
    // below says so at preflight: never silently pick a store that
    // cannot be correct, and never silently pick one that merely looks
    // distributed. A single replica keeps the in-memory blob+lock+cache
    // impl carried forward from Phase 3d (its read cache is a real
    // throughput win there). Registered unconditionally so
    // `TeamInvitationHandler` resolves the interface from DI without a
    // mode-conditional fallback — non-team modes never call into the
    // store.
    // Phase 547 — pass the resolved `IAuditLog` so either default store
    // emits `TeamInviteExpired` under the team audit stream on every
    // expiry sweep (GP 6). A consumer-supplied override owns its own
    // audit wiring; the defaults get the log (and the Phase 547.C
    // notifier, when opted in) via their 4-arg constructors.
    let resolvedPendingInviteStore: IPendingInviteStore =
        pendingInviteStoreOverride
        |> Option.defaultWith (fun () ->
            let blobDefault =
                if config.MultiInstance.ReplicaCount > 1 then
                    ToolUp.Platform.Scope.Teams.BlobPendingInviteStore.TryCreate(
                        resolvedBlobStorage,
                        resolvedLogger,
                        auditLog,
                        inviteExpiryNotifier
                    )
                else
                    None

            match blobDefault with
            | Some store ->
                resolvedLogger.Info(
                    sprintf
                        "[ComposeTeamRuntime] event=pending_invite_store_selected store=BlobPendingInviteStore ServerConfig.MultiInstance.ReplicaCount = %d and the composed IBlobStorage implements IConditionalBlobStorage — pending-by-email invitations use ETag-guarded writes across replicas."
                        config.MultiInstance.ReplicaCount
                )

                store
            | None ->
                ToolUp.Platform.Scope.Teams.InMemoryPendingInviteStore(
                    resolvedBlobStorage,
                    resolvedLogger,
                    auditLog,
                    inviteExpiryNotifier
                )
                :> IPendingInviteStore)

    services.AddSingleton<IPendingInviteStore>(resolvedPendingInviteStore) |> ignore

    // Phase 3d / Cluster A4 → Phase 5h — PendingInviteStore single-instance
    // enforcement, registered HERE (not with the other post-bootstrap
    // validators) because it reads the store this function just resolved:
    // the Warning fires only when that store is `InMemoryPendingInviteStore`
    // under `ReplicaCount > 1` — i.e. the auto-selection above could not
    // pick the blob store, or a consumer supplied the in-memory one — and
    // the `AcceptPendingInviteStoreInMultiInstance` escape hatch still
    // silences it. Warning (not Error) because the link-based invitation
    // flow is unaffected — only the pending-by-email surface corrupts. The
    // aggregator collects every `AddSingleton<IConfigValidator>` regardless
    // of registration site, and it requires an INSTANCE, which is why the
    // validator cannot be registered before the store is in hand.
    services.AddSingleton<ConfigValidation.IConfigValidator>(
        ToolUp.Platform.Scope.Teams.PendingInviteStoreInstanceValidator.PendingInviteStoreInstanceValidator(
            config,
            Some resolvedPendingInviteStore
        )
        :> ConfigValidation.IConfigValidator
    )
    |> ignore

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
    // Dual control (the two-person rule) sits between the sanitising store
    // and the grant-policy guard; `PermissionWrites.asPrincipal` is the one
    // function that orders them. Registered only under `DualControl`
    // (GP 13): a `SingleAdmin` deployment builds no approval store and
    // reads no `admin-mutations/` blob.
    let resolveGrantRegistry (sp: System.IServiceProvider) =
        match sp.GetService(typeof<GrantPolicyGuard.ModuleGrantPolicyRegistry>) with
        | :? GrantPolicyGuard.ModuleGrantPolicyRegistry as r -> r
        | _ -> GrantPolicyGuard.ModuleGrantPolicyRegistry.empty

    let sanitisedPermissionStore () =
        StoreIdSanitising.SanitisingPermissionStore(PermissionStore(resolvedBlobStorage, resolvedLogger))
        :> IPermissionStore

    match config.AdminMutationPolicy with
    | AdminMutationPolicy.SingleAdmin -> ()
    | AdminMutationPolicy.DualControl _ ->
        services.AddSingleton<AdminMutationApproval.IAdminMutationApprovalStore>(
            AdminMutationApproval.BlobAdminMutationApprovalStore(resolvedBlobStorage, resolvedLogger)
            :> AdminMutationApproval.IAdminMutationApprovalStore
        )
        |> ignore

    // Phase 1107 — in-platform publication reader grants, beside the
    // permission store and on the same blob storage. A deployment that
    // grants no reader holds no grant blob, so admission and every page
    // gate are unchanged.
    services.AddSingleton<IPublicationReaderGrantStore>(
        BlobPublicationReaderGrantStore(resolvedBlobStorage, Some resolvedLogger) :> IPublicationReaderGrantStore
    )
    |> ignore

    // The write chain without its caller. A handler wraps it once per
    // request with `PermissionWrites.asPrincipal`, which fixes the order
    // (grant policy outside dual control, the notice loop outside both).
    services.AddSingleton<AdminMutationApproval.PermissionWrites>(fun (sp: System.IServiceProvider) ->
        let registry = resolveGrantRegistry sp

        let dualControl: AdminMutationApproval.DualControlLayer option =
            match
                config.AdminMutationPolicy, sp.GetService(typeof<AdminMutationApproval.IAdminMutationApprovalStore>)
            with
            | AdminMutationPolicy.SingleAdmin, _ -> None
            | AdminMutationPolicy.DualControl settings,
              (:? AdminMutationApproval.IAdminMutationApprovalStore as approvals) ->
                Some {
                    AdminMutationApproval.DualControlLayer.Settings = settings
                    Approvals = approvals
                    IsPolicyBearing =
                        fun moduleName ->
                            GrantPolicyGuard.ModuleGrantPolicyRegistry.resolve registry moduleName
                            <> GrantPolicy.AdminDiscretion
                    Now = fun () -> System.DateTimeOffset.UtcNow
                    AuditLog = auditLog
                }
            | AdminMutationPolicy.DualControl _, _ ->
                // Unreachable — the approval store is registered above
                // whenever the mode is `DualControl`. Passing writes over
                // undecided would silently disable the control.
                invalidOp
                    "ComposeTeamRuntime: AdminMutationPolicy is DualControl but no IAdminMutationApprovalStore is registered."

        // An `AdminDiscretion`-only deployment reaches neither the grant
        // guard nor the notice loop and pays for neither (GP 13).
        let grantPolicy, observe =
            if GrantPolicyGuard.ModuleGrantPolicyRegistry.isEmpty registry then
                None, None
            else
                // The counterparty oracle, over whatever consent registry
                // `registerGrantConsentStore` composed; with none it
                // answers `false` for everything.
                let consentOracle =
                    let consentStore =
                        match sp.GetService(typeof<GrantConsentStore.IGrantConsentStore>) with
                        | :? GrantConsentStore.IGrantConsentStore as s -> Some s
                        | _ -> None

                    let verifier =
                        match sp.GetService(typeof<GrantConsentStore.IGrantConsentVerifier>) with
                        | :? GrantConsentStore.IGrantConsentVerifier as v -> v
                        | _ -> GrantConsentStore.denyingVerifier

                    GrantConsentStore.StoreCounterpartyConsentOracle(
                        consentStore,
                        verifier,
                        (fun () -> System.DateTimeOffset.UtcNow),
                        auditLog
                    )
                    :> GrantPolicyGuard.CounterpartyConsentOracle

                // The grant-event notice loop observes what was actually
                // written and never refuses; composed only when a channel
                // is resolvable.
                let observe =
                    match sp.GetService(typeof<INotificationChannel>) with
                    | :? INotificationChannel as channel when not (isNull (box channel)) ->
                        let settings =
                            match sp.GetService(typeof<GrantNotification.GrantNotificationSettings>) with
                            | :? GrantNotification.GrantNotificationSettings as s -> s
                            | _ -> GrantNotification.GrantNotificationSettings.defaults

                        Some(fun (principal: AccessContext) (guarded: IPermissionStore) ->
                            GrantNotification.GrantNotificationObserver(
                                guarded,
                                registry,
                                channel,
                                settings,
                                resolvedLogger,
                                principal,
                                (fun () -> System.DateTimeOffset.UtcNow)
                            )
                            :> IPermissionStore)
                    | _ -> None

                let layer: AdminMutationApproval.GrantPolicyLayer = {
                    Registry = registry
                    Consent = consentOracle
                    AuditLog = auditLog
                }

                Some layer, observe

        let writes: AdminMutationApproval.PermissionWrites = {
            Store = sanitisedPermissionStore ()
            DualControl = dualControl
            GrantPolicy = grantPolicy
            Observe = observe
        }

        writes)
    |> ignore

    // The store every reader and every caller-less writer resolves: the same
    // chain under a principal that names no person, so a gated write through
    // it is refused as unattributed rather than parked or attributed to
    // nobody. A request's writes go through `PermissionWrites.asPrincipal`.
    services.AddSingleton<IPermissionStore>(fun (sp: System.IServiceProvider) ->
        sp.GetService(typeof<AdminMutationApproval.PermissionWrites>) :?> AdminMutationApproval.PermissionWrites
        |> AdminMutationApproval.PermissionWrites.asPrincipal (AccessContext.system "permission-store"))
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

    // Phase 3d / Cluster A4 — the PendingInviteStore single-instance
    // validator used to register here; since Phase 5h it registers in
    // `registerTeamPermissionStores` above, beside the store it inspects.

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

/// Phase 1051 — the remedy a team-bootstrap refusal names.
[<Literal>]
let private bootstrapTeamRemedy =
    "The team store could not be read or written while bootstrapping TOOLUP_INITIAL_TEAM_NAME; restore access to the storage, then restart (the bootstrap is idempotent, so a restart retries it)."

/// Phase 1051 — the team bootstrap's one blocking run: the exception a
/// storage fault threw, or `None`.
let private tryBootstrapTeam
    (teamStoreOpt: TeamStore option)
    (resolvedLogger: ILogger)
    (auditLog: IAuditLog)
    : exn option =
    match teamStoreOpt with
    | Some ts ->
        try
            BootstrapTeam.bootstrap resolvedLogger auditLog (ts :> ITeamStore)
            |> Async.RunSynchronously

            None
        with ex ->
            Some ex
    | None -> None

/// Phase 5f — one-shot bootstrap of the initial team from
/// `TOOLUP_INITIAL_TEAM_NAME` (+ optional `TOOLUP_INITIAL_TEAM_ID`).
/// Runs only when an `ITeamStore` is registered (Team / MultiTeam
/// modes) AND the env var is set. Idempotent against an
/// already-populated team store. Must run AFTER the admin bootstrap
/// (in `ComposeAuth.registerPlatformAdminStore`) so the bootstrap
/// admin user-id (re-read from `TOOLUP_INITIAL_PLATFORM_ADMIN` inside
/// `BootstrapTeam.bootstrap`) exists by the time we add it as Owner.
/// Synchronous wait keeps the first request from racing the
/// bootstrap. A failure the helper reports (no Owner configured, a
/// refused `CreateTeam`) logs at `Error` and does not abort startup; a
/// storage fault that THROWS refuses the boot at preflight with
/// `ComposeAuth.BootstrapRefused` (Phase 1051) — it used to be logged and
/// the boot continued with no team.
let bootstrapTeam
    (services: IServiceCollection)
    (teamStoreOpt: TeamStore option)
    (resolvedLogger: ILogger)
    (auditLog: IAuditLog)
    : unit =
    tryBootstrapTeam teamStoreOpt resolvedLogger auditLog
    |> Option.iter (ComposeAuth.refuseAtPreflight services resolvedLogger "BootstrapTeam" bootstrapTeamRemedy)

/// Phase 5f — the team bootstrap for a caller holding no service
/// collection. A storage fault that throws refuses where it happens, by
/// raising `ComposeAuth.BootstrapRefused`, because there is no preflight
/// to carry it to (Phase 1051; it used to be logged and the boot continued).
[<System.Obsolete("Use ComposeTeamRuntime.bootstrapTeam, which carries a storage fault to the preflight so the storage validator's diagnosis is read first. Removed in 1.0.")>]
let runBootstrapTeam (teamStoreOpt: TeamStore option) (resolvedLogger: ILogger) (auditLog: IAuditLog) : unit =
    tryBootstrapTeam teamStoreOpt resolvedLogger auditLog
    |> Option.iter (fun ex -> raise (ComposeAuth.BootstrapRefused("BootstrapTeam", bootstrapTeamRemedy, ex)))
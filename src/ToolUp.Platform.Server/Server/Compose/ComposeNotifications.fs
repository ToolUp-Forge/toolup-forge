module ToolUp.Platform.ComposeNotifications

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tracing

// ─── compose phase: notification stack + audit log + share tokens ────
//
// Builds the notification substrate that downstream services consume:
// SSE connection manager, base notification channel, ConfigStore,
// IAuditLog, IShareTokenStore (opt-in), TransactionalDispatcher
// (opt-in), the dispatching-wrapped notification channel surfaced to
// consumers, and the persistent narrative store.
//
// AuditLog sits in this file because it sits *between* the event store
// and the transactional dispatcher in the construction chain — every
// downstream notification-side substrate (transactional dispatcher,
// share-token store) depends on it, and the alternative (splitting it
// into its own helper) would require threading the same dependency
// graph through two helpers.
//
// Extracted from `compose` for the per-concern subdivision (Phase 15e
// follow-up). Takes the exact substrate values the inline definition
// captured and returns the same shape. Zero behaviour change.

/// Aggregate of the notification-stack substrate values built before DI
/// registration. The downstream DI block reads these to register the
/// concrete instances. Holding them as a record (rather than a tuple)
/// keeps the call site readable when the count grows.
type NotificationStack = {
    SseConnectionManager: SSEConnectionManager
    BaseNotificationChannel: INotificationChannel
    ConfigStoreInstance: IConfigStore
    AuditLog: IAuditLog
    ShareTokenStoreInstance: IShareTokenStore option
    TransactionalDispatcher: TransactionalDispatcher.TransactionalDispatcher option
    /// The actual notification channel surfaced to consumers. When any
    /// transactional sink is registered we wrap with
    /// `DispatchingNotificationChannel` so transactional kinds bypass
    /// the wire transport and ride the dispatcher's queue instead.
    /// Non-transactional kinds pass through unchanged.
    ResolvedNotificationChannel: INotificationChannel
    /// Phase 442 — opt-in presence + soft-lock substrate. `None` when
    /// `ServerConfig.Presence = NoPresence` (the default): no DI
    /// registration, no allocation, byte-for-byte unchanged (GP 13).
    PresenceSubstrate: (IPresenceTracker * IEntityLockStore) option
    NarrativeStore: INarrativeStore
}

/// Build the notification-stack substrate.
///
/// Construction order is load-bearing: `configStoreInstance` is built
/// first (Phase 5a — also needs to be visible to the Phase 6f
/// transactional dispatcher pre-DI); `auditLog` follows because the
/// transactional dispatcher's delivery-outcome audit and the share-token
/// store's emission path both depend on it; `shareTokenStoreInstance`
/// follows so the `let`-binding is visible to the bulk DI registration
/// block in `compose`; `transactionalDispatcher` constructs over
/// `auditLog`; `resolvedNotificationChannel` is the dispatcher-wrapped
/// version of the base channel.
let buildNotificationStack
    (config: ServerConfig)
    (effectiveNotifications: NotificationMode)
    (notificationChannel: INotificationChannel option)
    (resolvedBlobStorage: IBlobStorage)
    (secretStore: Secrets.ISecretStore)
    (eventStore: IEventStore)
    (resolvedLogger: ILogger)
    (resolvedActivitySink: IActivitySink)
    (logger: ILogger option)
    (transactionalSinks: INotificationSink list)
    // Phase 114 — deferred `IMetricsSink` accessor for the audit log's
    // write-failure counter. Deferred (rather than the resolved sink)
    // because `compose` resolves the metrics sink AFTER this stack is
    // built; the cell-reader pattern (same as the rate-limiter /
    // job-scheduler cells) hands the audit log the real sink by the time
    // any write actually fails at runtime.
    (metricsSinkLookup: unit -> Metrics.IMetricsSink)
    : NotificationStack =

    // Shared SSE transport. Used by the generic notification channel's
    // `/api/notifications` endpoint and by the AI companion's
    // `/api/ai/events` endpoint (resolved from DI in
    // `ToolUp.AI.AICompose`). Registering it here — in core — ensures
    // both transports share the same connection registry and
    // zombie-cleanup semantics.
    //
    // Phase 6l.D — pass `MaxSseConnectionsPerScope` so `Add` enforces
    // the per-scope cap. None = unbounded (legacy); default config value
    // is `Some 10`.
    let sseConnectionManager =
        match config.MaxSseConnectionsPerScope with
        | Some cap -> new SSEConnectionManager(cap)
        | None -> new SSEConnectionManager()

    // Notification channel. Apps pass a distributed implementation
    // (Redis pub/sub via `src/NotificationChannels/Redis`, future NATS /
    // Orleans streams) through the `notificationChannel` parameter; when
    // `None`, `compose` derives one from `effectiveNotifications`:
    // `NoNotifications` → `NoOpNotificationChannel` (registered so
    // consumers don't crash on a missing dependency, but `Publish` is a
    // no-op and `/api/notifications` is not mounted), otherwise the
    // in-process `InMemoryNotificationChannel`.
    let baseNotificationChannel: INotificationChannel =
        notificationChannel
        |> Option.defaultWith (fun () ->
            match effectiveNotifications with
            | NoNotifications
            | NoNotificationsExplicit -> NotificationChannel.NoOpNotificationChannel() :> _
            | _ -> NotificationChannel.InMemoryNotificationChannel(logger) :> _)

    // Phase 5a config store — built pre-DI so consumers needing it
    // before `app.Build()` (Phase 6f transactional dispatcher) get the
    // same instance that DI hands out. Registered into DI further down;
    // `services.AddSingleton(this)` on the same reference avoids
    // double-construction.
    // Logger threaded so the store's fallback paths (corrupt blob /
    // decode failure → silent defaults) surface as Warn lines.
    let configStoreInstance =
        ConfigStore.createWithLogger resolvedBlobStorage resolvedLogger

    // Phase 9 / Phase 1g SDK-wide audit log. `EventStoreAuditLog` wraps
    // the DI-registered `IEventStore` (which is itself the
    // webhook-hooked store when webhooks are enabled), so audit writes
    // flow through the same retention policy and webhook fan-out as
    // every other platform event. The `NoAuditLog` mode (Phase 1g
    // default) registers `NoOpAuditLog` instead — emission sites
    // (TeamStore, SessionFileStore, PermissionStore,
    // ScopeResolutionMiddleware, TransactionalDispatcher) still call
    // `Record` unconditionally; the no-op makes them free at runtime so
    // callsites stay clean across mode changes.
    //
    // Constructed here (above the transactional dispatcher) so Phase 6f's
    // audit emission has its `IAuditLog` instance to inject.
    // Phase 9t — the `DegradeToFile` spill store, constructed only when
    // the policy selects it (GP 13). The replay service (registered
    // later, where `services` is in scope) constructs its own instance
    // over the same root — the store is stateless over the directory.
    let auditFallbackStore =
        match config.AuditLog, config.AuditFailurePolicy with
        | EnabledAuditLog, DegradeToFile ->
            let root =
                config.AuditFallbackDirectory
                |> Option.defaultValue (AuditFallbackStore.defaultDirectory ())

            Some(AuditFallbackStore.AuditFallbackStore(root, AuditFallbackStore.DefaultMaxBytes, resolvedLogger))
        | _ -> None

    let auditLog: IAuditLog =
        match config.AuditLog with
        | NoAuditLog -> AuditLog.NoOpAuditLog() :> _
        | EnabledAuditLog ->
            AuditLog.EventStoreAuditLog(
                eventStore,
                resolvedLogger,
                metricsSinkLookup,
                config.AuditFailurePolicy,
                ?fallbackStore = auditFallbackStore
            )
            :> _

    // Phase 21b — opt-in share-token substrate. `NoShareTokenStore`
    // (default) leaves `shareTokenStoreInstance = None`, no DI
    // registration, no `_platform/share-tokens/` blob layout, no signing
    // key resolved from the secret store. Apps that resolve
    // `IShareTokenStore` from DI then receive `null` and must handle
    // absence explicitly. Audit emission flows through the configured
    // `IAuditLog`; the `NoOpAuditLog` default makes share-token audit
    // emission free at runtime when the deployment runs
    // `AuditLog = NoAuditLog`. Built here (before `services` is in
    // scope) so the let-binding is visible to the bulk DI registration
    // block further down — mirrors the JobScheduler / EntityStore
    // patterns.
    // Phase 66 Stream A.7 — auto-promote `ShareTokenStore` to
    // `EnabledShareTokenStore` when `config.Surfaces` declares a
    // `ClaimBearer` profile but the operator left the explicit
    // selection at the `NoShareTokenStore` default. The
    // `ClaimBearer` surface is unreachable without a share-token
    // store to validate the claims it carries, so the auto-promotion
    // closes the door on a startup-shape that has no working
    // request path. `SurfaceCoherenceValidator` (Stream B.2) raises
    // the inverse warning — store wired but no `ClaimBearer` in
    // `Surfaces` — so the two together cover both halves of the
    // coherence rule.
    let effectiveShareTokenStore =
        match config.ShareTokenStore with
        | EnabledShareTokenStore -> EnabledShareTokenStore
        | NoShareTokenStore when DeploymentConfig.hasClaimBearer config ->
            // Phase 66 Stream B.2 — log the auto-promotion at Info level
            // so operators see it in startup output (per design §3.8
            // "Logged at info level so operators can see the
            // auto-promotion in startup output"). The matching
            // `SurfaceCoherenceValidator` Rule 5 surfaces a Warning so
            // operators relying on auto-promotion are nudged toward an
            // explicit `EnabledShareTokenStore` setting.
            resolvedLogger.Info(
                "[ShareTokenStore] auto-promoted NoShareTokenStore → EnabledShareTokenStore: ServerConfig.Surfaces declares SurfaceProfile.ClaimBearer. Set ServerConfig.ShareTokenStore = EnabledShareTokenStore explicitly to silence this notice."
            )

            EnabledShareTokenStore
        | NoShareTokenStore -> NoShareTokenStore

    let shareTokenStoreInstance: IShareTokenStore option =
        match effectiveShareTokenStore with
        | NoShareTokenStore -> None
        | EnabledShareTokenStore ->
            // Phase 131 (remainder) — wrap the blob store in the
            // id-sanitising decorator so every caller-supplied `scopeId`
            // / `tokenId` that becomes a
            // `_platform/share-tokens/{scopeId}/{tokenId}…` path segment
            // (write) or `List` prefix (read) is validated before it
            // reaches the key-construction sink. Innermost wrap (mirrors
            // the team/permission store seam in ComposeTeamRuntime) so
            // both external calls and any consumer
            // `withShareTokenStoreDecorator` decorator route through the
            // guard.
            Some(
                StoreIdSanitising.SanitisingShareTokenStore(
                    ShareTokenStore.create resolvedBlobStorage secretStore (Some auditLog) resolvedLogger
                )
                :> IShareTokenStore
            )

    // Phase 6f — transactional dispatcher. Constructed only when at
    // least one `INotificationSink` is registered. Validates duplicate
    // `Kind` registrations at compose time so a misconfiguration fails
    // the deployment rather than running with half its delivery
    // silently dropped. Hosted as `IHostedService` further down so
    // cancellation flows through ASP.NET Core's shutdown sequence.
    let transactionalDispatcher: TransactionalDispatcher.TransactionalDispatcher option =
        if List.isEmpty transactionalSinks then
            None
        else
            TransactionalDispatcher.validateSinkRegistration transactionalSinks

            Some(
                new TransactionalDispatcher.TransactionalDispatcher(
                    transactionalSinks,
                    configStoreInstance,
                    auditLog,
                    resolvedLogger,
                    TransactionalRetryPolicy.defaults,
                    resolvedActivitySink
                )
            )

    let resolvedNotificationChannel: INotificationChannel =
        match transactionalDispatcher with
        | None -> baseNotificationChannel
        | Some dispatcher ->
            TransactionalDispatcher.DispatchingNotificationChannel(baseNotificationChannel, dispatcher) :> _

    // Phase 442 — opt-in presence + soft-lock collaboration substrate.
    // `EnabledPresence` constructs the in-memory `IPresenceTracker` /
    // `IEntityLockStore` defaults over the resolved (dispatcher-wrapped)
    // notification channel so join / move / leave and lock events fan out
    // on the same per-scope SSE pipeline every other server event rides.
    // Registration into DI happens in `registerPresenceSubstrate` further
    // down (mirrors the share-token store split). The SDK registers the
    // substrate only — a deployment exposes its own (module-owned)
    // presence / lock API over the resolved services and mounts
    // `PresenceContext.provider` client-side; both in-memory defaults are
    // single-instance (a multi-instance deployment supplies distributed
    // implementations, per the impl file headers).
    let presenceSubstrate: (IPresenceTracker * IEntityLockStore) option =
        match config.Presence with
        | NoPresence -> None
        | EnabledPresence ->
            Some(
                InMemoryPresenceTracker(resolvedNotificationChannel) :> IPresenceTracker,
                InMemoryEntityLockStore(resolvedNotificationChannel) :> IEntityLockStore
            )

    // Narrative store. Default to the blob-backed persistent
    // implementation so narratives survive process restarts — the
    // in-memory variant lost everything on every reboot, which left AI
    // tools (`list_narratives`, `get_narrative`) blind to anything the
    // user generated in earlier sessions even though the same
    // narrative-commit also persists into the KnowledgeBase. Persists
    // through the resolved IBlobStorage (LocalFileStorage in dev, cloud
    // storage in production); distributed deployments inherit durability
    // from whatever storage backend they wire in. Retention is governed
    // by `ServerConfig.NarrativeRetention`; the default policy caps each
    // scope at 100 entries with no age limit.
    //
    // Wrap the persistent store in `NotifyingNarrativeStore` so every
    // successful write fans an SSE event out under
    // `NarrativeNotifications.PublishedKey` /  `ScopeResetKey`.
    // Subscribers (UI list views, custom dashboards) receive the
    // notification through the same `INotificationChannel` SSE pipeline
    // every other server-driven event uses; scope routing is preserved
    // by the channel's per-scope topic.
    let narrativeStore: INarrativeStore =
        let baseStore =
            PersistentNarrativeStore.PersistentNarrativeStore(resolvedBlobStorage, config.NarrativeRetention)
            :> INarrativeStore

        NotifyingNarrativeStore(baseStore, resolvedNotificationChannel) :> _

    {
        SseConnectionManager = sseConnectionManager
        BaseNotificationChannel = baseNotificationChannel
        ConfigStoreInstance = configStoreInstance
        AuditLog = auditLog
        ShareTokenStoreInstance = shareTokenStoreInstance
        TransactionalDispatcher = transactionalDispatcher
        ResolvedNotificationChannel = resolvedNotificationChannel
        PresenceSubstrate = presenceSubstrate
        NarrativeStore = narrativeStore
    }

/// Phase 21b — register the share-token store when enabled. The instance
/// was constructed earlier (so the let-binding is visible to other
/// earlier construction sites that may want to depend on it); only the
/// DI registration happens here.
let registerShareTokenStore (services: IServiceCollection) (shareTokenStoreInstance: IShareTokenStore option) : unit =
    match shareTokenStoreInstance with
    | None -> ()
    | Some store -> services.AddSingleton<IShareTokenStore>(store) |> ignore

/// Phase 442 — register the presence + soft-lock substrate when
/// `ServerConfig.Presence = EnabledPresence`. The pair was constructed in
/// `buildNotificationStack` (over the resolved notification channel);
/// only the DI registration happens here. `NoPresence` registers nothing
/// — a module that resolves either interface then receives `null` and
/// must handle absence explicitly (same contract as `IShareTokenStore`).
let registerPresenceSubstrate
    (services: IServiceCollection)
    (presenceSubstrate: (IPresenceTracker * IEntityLockStore) option)
    : unit =
    match presenceSubstrate with
    | None -> ()
    | Some(tracker, lockStore) ->
        services.AddSingleton<IPresenceTracker>(tracker) |> ignore
        services.AddSingleton<IEntityLockStore>(lockStore) |> ignore

/// Phase 6f — transactional dispatcher hosted service. Registered only
/// when at least one `INotificationSink` was supplied (the dispatcher is
/// `None` otherwise). Sinks themselves are registered as
/// `INotificationSink` singletons so they can be resolved by tests +
/// diagnostics, even though the live dispatch path holds them through
/// the captured constructor list.
///
/// Phase 16 — when `ServerConfig.ServerlessHost = ServerlessHost`, the
/// dispatcher's `IHostedService` is NOT registered. The dispatcher
/// singleton still registers so handlers can resolve it and enqueue
/// outbound notifications; a sibling worker silo (`ProcessProfile =
/// WorkerOnly` or `DispatcherOnly`) drains the queue.
let registerTransactionalDispatcher
    (services: IServiceCollection)
    (config: ServerConfig)
    (transactionalDispatcher: TransactionalDispatcher.TransactionalDispatcher option)
    (transactionalSinks: INotificationSink list)
    : unit =
    match transactionalDispatcher with
    | None -> ()
    | Some dispatcher ->
        services.AddSingleton<TransactionalDispatcher.TransactionalDispatcher>(dispatcher)
        |> ignore

        // Phase 16 + 16a — gate transactional-dispatcher BackgroundService
        // on the centralised matrix. ServerlessHost / WebOnly skip;
        // AllInOne / WorkerOnly / DispatcherOnly register.
        if ProcessProfileGate.shouldRegisterBackgroundService config TransactionalDispatcherSubsystem then
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                dispatcher :> Microsoft.Extensions.Hosting.IHostedService
            )
            |> ignore

        for sink in transactionalSinks do
            services.AddSingleton<INotificationSink>(sink) |> ignore

/// Phase 9t — the `DegradeToFile` audit-spill replay drain. Registered
/// only when the deployment opted into the policy (and audit is on) —
/// every other policy pays nothing (GP 13). Gated by
/// `AuditFallbackReplaySubsystem` on the centralised process-profile
/// matrix: AllInOne / WorkerOnly run it; WebOnly / DispatcherOnly /
/// ServerlessHost skip (a sibling worker drains the shared spill only
/// when the fallback directory is shared — the default working-dir
/// root is per-silo, matching the per-silo spill writer).
let registerAuditFallbackReplay
    (services: IServiceCollection)
    (config: ServerConfig)
    (eventStore: IEventStore)
    (resolvedLogger: ILogger)
    : unit =
    match config.AuditLog, config.AuditFailurePolicy with
    | EnabledAuditLog, DegradeToFile when
        ProcessProfileGate.shouldRegisterBackgroundService config AuditFallbackReplaySubsystem
        ->
        let root =
            config.AuditFallbackDirectory
            |> Option.defaultValue (AuditFallbackStore.defaultDirectory ())

        let drainStore =
            AuditFallbackStore.AuditFallbackStore(root, AuditFallbackStore.DefaultMaxBytes, resolvedLogger)

        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
            AuditFallbackReplayService.AuditFallbackReplayService(drainStore, eventStore, resolvedLogger)
            :> Microsoft.Extensions.Hosting.IHostedService
        )
        |> ignore
    | _ -> ()
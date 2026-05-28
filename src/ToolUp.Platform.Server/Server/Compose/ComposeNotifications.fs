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
    let configStoreInstance = ConfigStore.create resolvedBlobStorage

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
    let auditLog: IAuditLog =
        match config.AuditLog with
        | NoAuditLog -> AuditLog.NoOpAuditLog() :> _
        | EnabledAuditLog -> AuditLog.EventStoreAuditLog(eventStore, resolvedLogger) :> _

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
            Some(ShareTokenStore.create resolvedBlobStorage secretStore (Some auditLog) resolvedLogger)

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

    // Narrative store. Default to the blob-backed persistent
    // implementation so narratives survive process restarts — the
    // in-memory variant lost everything on every reboot, which left AI
    // tools (`list_narratives`, `get_narrative`) blind to anything the
    // user generated in earlier sessions even though the same
    // narrative-commit also persists into the KnowledgeBase. Persists
    // through the resolved IBlobStorage (LocalFileStorage in dev, cloud
    // storage in production); distributed deployments inherit durability
    // from whatever storage backend they wire in.
    let narrativeStore: INarrativeStore =
        PersistentNarrativeStore.PersistentNarrativeStore(resolvedBlobStorage) :> _

    {
        SseConnectionManager = sseConnectionManager
        BaseNotificationChannel = baseNotificationChannel
        ConfigStoreInstance = configStoreInstance
        AuditLog = auditLog
        ShareTokenStoreInstance = shareTokenStoreInstance
        TransactionalDispatcher = transactionalDispatcher
        ResolvedNotificationChannel = resolvedNotificationChannel
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
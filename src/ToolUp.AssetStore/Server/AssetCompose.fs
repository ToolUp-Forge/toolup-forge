// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AssetStore.AssetCompose

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.Metrics
open ToolUp.Platform.Server

// ─── The asset-store companion: `AssetStoreCompanionOptions` + `withAssetStore`
//
// The asset store is a companion composed onto a `ServerApp`, not a record
// that wraps one. `AssetStoreCompanionOptions` holds the asset-store-specific
// compose-time registrations (renderer, derivative profiles, options);
// `withAssetStore options` registers the companion with
// `ServerApp.withCompanion`, so it is applied once, when the deployment
// resolves, to the deployment as every builder left it.
//
// **Strip-imports guarantee**: when the resolved
// `ServerConfig.AssetStore = NoAssetStore`, the companion contributes
// nothing — no DI registrations, no handlers, no hosted services. The
// deployment is byte-for-byte the one it would be without the call.

/// The asset-store companion's identity: the slot `withAssetStore`
/// composes under and the owner of its contributions.
let assetStoreCompanion: ComponentId =
    ComponentId.forCompanionSlot "ToolUp.AssetStore"

/// What the asset-store companion composes.
type AssetStoreCompanionOptions = {
    /// Compose-time options. `AssetStoreOptions.defaults`
    /// covers the common case; deployments tune `MaxBytes` /
    /// `AcceptedMimeTypes` / `AltTextMaxChars` / `EmitAudit` via
    /// `withOptions`.
    Options: AssetStoreOptions
    /// Optional override for the `IDerivativeRenderer`. When
    /// `None`, the companion constructs the default
    /// `SkiaSharpDerivativeRenderer`. Deployments wanting wider
    /// format coverage register the optional
    /// `ImageMagickDerivativeRenderer` companion here.
    RendererOverride: IDerivativeRenderer option
    /// Derivative-profile registry. Defaults to
    /// `withWebDefault` (one profile, `web-default`, covering
    /// the responsive thumbnail / medium / OG / webp-medium
    /// shapes). Deployments add more via `withDerivativeProfile`.
    Profiles: DerivativeProfileRegistry
    /// Optional override for the `IAssetStore` impl. When
    /// `None`, the companion constructs the default `DefaultAssetStore`
    /// over the SDK's configured `IBlobStorage`. Deployments
    /// substituting Cloudinary / Imgix / S3-direct-CDN supply
    /// their own impl here.
    StoreOverride: IAssetStore option
    /// Phase 127 — renderers for general (arbitrary-MIME) profile
    /// entries, keyed by `GeneralDerivativeSpec.RendererKey`.
    /// Empty by default; image profiles never consult it.
    MimeRenderers: MimeRendererRegistry
    /// Phase 127 — opt-in async job-backed derivation. `None`
    /// (default) registers no job handler and emits no channel
    /// traffic (GP 13); async-mode profile entries fail typed.
    AsyncDerivation: AsyncDerivationOptions option
    /// Phase 207 — opt-in dead-letter + retry observability over the
    /// async pipeline. `None` (default) is exactly the Phase 127
    /// behaviour: no dead-letter blob, no failure notification, no
    /// counters, no `IMetricsSink` resolution (GP 11 / GP 13).
    /// Ignored without `AsyncDerivation` — there is no background
    /// derivation to observe.
    DerivativeDlq: DerivativeDlqOptions option
}

/// Compose-time options for the async derivation mode (Phase 127).
and AsyncDerivationOptions = {
    /// Retry behaviour the scheduler applies to derivation jobs
    /// (GP 12 rule 3 — retry as data).
    RetryPolicy: JobRetryPolicy
}

/// Compose-time options for the Phase 207 dead-letter + retry
/// observability surface. Supplied through
/// `AssetStoreCompanionOptions.withDerivativeDlq`; a deployment that
/// never calls it constructs none of this.
and DerivativeDlqOptions = {
    /// Optional bounded-retry override. `None` keeps the budget
    /// `AsyncDerivationOptions.RetryPolicy` already declares; `Some`
    /// replaces it wholesale, so a deployment can widen the budget
    /// and name a dead-letter destination in one place. The override
    /// reaches both the handler's exhaustion test and the
    /// registration the coordinator stamps, so the two can never
    /// disagree about how many attempts there are.
    RetryPolicy: JobRetryPolicy option
    /// Persist a `DerivativeDeadLetterRecord` on terminal failure.
    RecordDeadLetters: bool
    /// Publish a `DerivativeFailedNotification` on terminal failure,
    /// under `DerivativeJobs.DerivativeFailedNotificationKey`.
    NotifyOnFailure: bool
    /// Emit the `DerivativeJobs.RetryMetric` /
    /// `DerivativeJobs.FailedMetric` counters against the
    /// deployment's registered `IMetricsSink`. Costs nothing when
    /// metrics are disabled — the SDK default sink is a no-op.
    EmitMetrics: bool
}

module AsyncDerivationOptions =
    let defaults: AsyncDerivationOptions = {
        RetryPolicy = {
            MaxAttempts = 3
            InitialBackoff = TimeSpan.FromSeconds 30.0
            MaxBackoff = TimeSpan.FromMinutes 10.0
            DeadLetterDestination = None
        }
    }

module DerivativeDlqOptions =
    /// All three surfaces on, retry budget inherited. Opting in at
    /// all is the deliberate act (GP 11); once opted in, an operator
    /// asking for a dead-letter queue wants the record, the
    /// notification and the counters.
    let defaults: DerivativeDlqOptions = {
        RetryPolicy = None
        RecordDeadLetters = true
        NotifyOnFailure = true
        EmitMetrics = true
    }

module AssetStoreCompanionOptions =

    /// The defaults: `AssetStoreOptions.defaults`, the web-default
    /// derivative profile, the SDK's renderer and store.
    let create () : AssetStoreCompanionOptions = {
        Options = AssetStoreOptions.defaults
        RendererOverride = None
        Profiles = DerivativeProfileRegistry.withWebDefault
        StoreOverride = None
        MimeRenderers = MimeRendererRegistry.empty
        AsyncDerivation = None
        DerivativeDlq = None
    }

    /// Override `AssetStoreOptions`. Defaults cover the common
    /// case; deployments tune `MaxBytes` / `AcceptedMimeTypes` /
    /// `AltTextMaxChars` / `EmitAudit`.
    let withOptions
        (storeOptions: AssetStoreOptions)
        (options: AssetStoreCompanionOptions)
        : AssetStoreCompanionOptions =
        { options with Options = storeOptions }

    /// Supply an explicit `IDerivativeRenderer`. Default
    /// (`None`) constructs `SkiaSharpDerivativeRenderer`.
    /// Deployments wanting wider format coverage register the
    /// optional `ImageMagickDerivativeRenderer` companion here.
    let withRenderer
        (renderer: IDerivativeRenderer)
        (options: AssetStoreCompanionOptions)
        : AssetStoreCompanionOptions =
        {
            options with
                RendererOverride = Some renderer
        }

    /// Register a derivative profile. `web-default` is seeded
    /// already; deployments add `podcast-card` /
    /// `directory-listing` / `og-twitter` shapes via this
    /// helper. Re-registering an existing id replaces it.
    let withDerivativeProfile
        (id: DerivativeProfileId)
        (specs: DerivativeSpec list)
        (options: AssetStoreCompanionOptions)
        : AssetStoreCompanionOptions =
        {
            options with
                Profiles = DerivativeProfileRegistry.register id specs options.Profiles
        }

    /// Supply an explicit `IAssetStore` impl. Default (`None`)
    /// constructs `DefaultAssetStore` over the configured
    /// `IBlobStorage`. Deployments substituting Cloudinary /
    /// Imgix / S3-direct-CDN supply their own impl here.
    let withAssetStore (store: IAssetStore) (options: AssetStoreCompanionOptions) : AssetStoreCompanionOptions = {
        options with
            StoreOverride = Some store
    }

    /// Phase 127 — register a profile whose entries mix image and
    /// general (arbitrary-MIME) derivatives. Re-registering an
    /// existing id replaces it; `withDerivativeProfile` remains the
    /// image-only shorthand.
    let withDerivativeProfileEntries
        (id: DerivativeProfileId)
        (entries: ProfileEntry list)
        (options: AssetStoreCompanionOptions)
        : AssetStoreCompanionOptions =
        {
            options with
                Profiles = DerivativeProfileRegistry.registerEntries id entries options.Profiles
        }

    /// Phase 127 — register a renderer for general (arbitrary-MIME)
    /// profile entries under `key`
    /// (`GeneralDerivativeSpec.RendererKey` names it). Renderer
    /// implementations carrying vendor dependencies ship as
    /// companions (GP 1).
    let withMimeRenderer
        (key: string)
        (renderer: IMimeDerivativeRenderer)
        (options: AssetStoreCompanionOptions)
        : AssetStoreCompanionOptions =
        {
            options with
                MimeRenderers = MimeRendererRegistry.register key renderer options.MimeRenderers
        }

    /// Phase 127 — opt into async job-backed derivation for
    /// `DerivationMode.AsyncJob` profile entries: derivations run on
    /// the deployment's `IJobScheduler`, completion surfaces over
    /// the notification channel
    /// (`DerivativeJobs.DerivativeReadyNotificationKey`), and the
    /// content-hash cache serves instantly thereafter. Without this
    /// opt-in nothing changes (GP 13): no job-handler registration,
    /// no channel traffic; async-mode entries fail typed.
    let withAsyncDerivation
        (value: AsyncDerivationOptions)
        (options: AssetStoreCompanionOptions)
        : AssetStoreCompanionOptions =
        {
            options with
                AsyncDerivation = Some value
        }

    /// Phase 207 — opt into dead-letter + retry observability for the
    /// async derivation pipeline: a derivation that exhausts its
    /// bounded retry budget persists a `DerivativeDeadLetterRecord`
    /// an operator can sweep, publishes a
    /// `DerivativeFailedNotification` on its own notification key,
    /// and moves the `DerivativeJobs.RetryMetric` /
    /// `DerivativeJobs.FailedMetric` counters. `DerivativeDlqOptions.defaults`
    /// turns all three on and inherits the composed retry budget.
    ///
    /// Without this call nothing changes (GP 11 / GP 13): the handler
    /// is constructed with `DerivativeObservability.disabled`, so no
    /// dead-letter blob is written, no failure notification is
    /// published, and no `IMetricsSink` is resolved. Requires
    /// `AssetStoreCompanionOptions.withAsyncDerivation` — without a
    /// background pipeline there is nothing to observe, and the
    /// option is ignored.
    let withDerivativeDlq
        (value: DerivativeDlqOptions)
        (options: AssetStoreCompanionOptions)
        : AssetStoreCompanionOptions =
        {
            options with
                DerivativeDlq = Some value
        }

/// The companion's `apply`. When the resolved
/// `ServerConfig.AssetStore = NoAssetStore`, contributes nothing. When
/// `EnabledAssetStore`, registers the DI singletons and contributes the
/// ToolUp.Remoting handler at `/api/assets/` and the multipart upload
/// handler at `/api/assets/upload`.
let private composeAssetStore (companion: AssetStoreCompanionOptions) (app: ServerApp) : ServerApp =
    match (ServerApp.effectiveConfig app).AssetStore with
    | NoAssetStore -> app
    | EnabledAssetStore ->
        let options = companion.Options
        let profiles = companion.Profiles
        let explicitRenderer = companion.RendererOverride
        let explicitStore = companion.StoreOverride
        let mimeRenderers = companion.MimeRenderers
        let asyncDerivation = companion.AsyncDerivation
        let derivativeDlq = companion.DerivativeDlq

        let asLogger =
            app.Logger
            |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

        // Phase 207 — the DLQ surface observes the async
        // pipeline; without one there is nothing to observe. Say
        // so at compose time rather than leaving the operator to
        // wonder why no dead-letter record ever appears.
        if derivativeDlq.IsSome && asyncDerivation.IsNone then
            asLogger.Warn(
                "[AssetStore] withDerivativeDlq set without withAsyncDerivation — no background derivation to observe; the option is ignored"
            )

        // ─── DI registrations ────────────────────────────────
        let assetStoreServiceConfig (services: IServiceCollection) =
            services
                .AddSingleton<IDerivativeRenderer>(
                    System.Func<System.IServiceProvider, IDerivativeRenderer>(fun _sp ->
                        match explicitRenderer with
                        | Some r -> r
                        | None -> SkiaSharpDerivativeRenderer() :> IDerivativeRenderer)
                )
                .AddSingleton<DerivativeProfileRegistry>(profiles)
                .AddSingleton<IAssetStore>(
                    System.Func<System.IServiceProvider, IAssetStore>(fun sp ->
                        match explicitStore with
                        | Some s -> s
                        | None ->
                            let blob = sp.GetService(typeof<IBlobStorage>) :?> IBlobStorage
                            let renderer = sp.GetService(typeof<IDerivativeRenderer>) :?> IDerivativeRenderer
                            let audit = sp.GetService(typeof<IAuditLog>) :?> IAuditLog

                            // Phase 127 — async derivation wiring,
                            // only when opted in (GP 13). The
                            // factory runs once (singleton), so the
                            // handler registration is once-per-
                            // process and precedes the first
                            // enqueue (the first request resolves
                            // this factory before it can request a
                            // derivative).
                            let coordinator =
                                asyncDerivation
                                |> Option.bind (fun asyncOptions ->
                                    match sp.GetService(typeof<IJobScheduler>) with
                                    | :? IJobScheduler as scheduler ->
                                        let channel =
                                            match sp.GetService(typeof<INotificationChannel>) with
                                            | :? INotificationChannel as c -> Some c
                                            | _ -> None

                                        // Phase 207 — the DLQ opt-in
                                        // may replace the retry
                                        // budget. Resolve it ONCE and
                                        // use it for both the
                                        // handler's exhaustion test
                                        // and the registration the
                                        // coordinator stamps, so the
                                        // two cannot disagree.
                                        let retryPolicy =
                                            derivativeDlq
                                            |> Option.bind _.RetryPolicy
                                            |> Option.defaultValue asyncOptions.RetryPolicy

                                        // Not opted in ⇒ the Phase 127
                                        // constructor, unchanged.
                                        let handler =
                                            match derivativeDlq with
                                            | None ->
                                                DerivativeJobHandler(
                                                    blob,
                                                    profiles,
                                                    mimeRenderers,
                                                    channel,
                                                    asLogger,
                                                    retryPolicy.MaxAttempts
                                                )
                                            | Some dlqOptions ->
                                                let metrics =
                                                    if dlqOptions.EmitMetrics then
                                                        match sp.GetService(typeof<IMetricsSink>) with
                                                        | :? IMetricsSink as sink -> Some sink
                                                        | _ -> None
                                                    else
                                                        None

                                                DerivativeJobHandler(
                                                    blob,
                                                    profiles,
                                                    mimeRenderers,
                                                    channel,
                                                    asLogger,
                                                    retryPolicy.MaxAttempts,
                                                    {
                                                        RecordDeadLetters = dlqOptions.RecordDeadLetters
                                                        NotifyOnFailure = dlqOptions.NotifyOnFailure
                                                        Metrics = metrics
                                                        DeadLetterDestination = retryPolicy.DeadLetterDestination
                                                    }
                                                )

                                        scheduler.RegisterHandler(DerivativeJobs.HandlerName, handler)

                                        Some(DerivativeJobCoordinator(blob, scheduler, retryPolicy, asLogger))
                                    | _ ->
                                        asLogger.Warn(
                                            "[AssetStore] withAsyncDerivation set but no IJobScheduler is registered — async-mode derivatives will fail typed"
                                        )

                                        None)

                            DefaultAssetStore(
                                blob,
                                renderer,
                                profiles,
                                audit,
                                asLogger,
                                options,
                                mimeRenderers,
                                coordinator
                            )
                            :> IAssetStore)
                )

        // ─── ToolUp.Remoting handler ──────────────────────────
        let assetApi (ctx: HttpContext) : IAssetApi =
            let store = ctx.RequestServices.GetService(typeof<IAssetStore>) :?> IAssetStore

            let scopeContainer () =
                match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                | true, (:? StorageScope as s) -> s.Container
                | _ -> failwith "No active scope — IAssetApi requires authentication"

            {
                GetAsset =
                    fun rawId -> async {
                        let container = scopeContainer ()
                        return! store.Get(container, AssetId rawId)
                    }
                ListAssets =
                    fun (prefix, page) -> async {
                        let container = scopeContainer ()
                        return! store.List(container, prefix, page)
                    }
                GetDerivative =
                    fun (rawId, derivativeName) -> async {
                        let container = scopeContainer ()
                        return! store.GetDerivative(container, AssetId rawId, derivativeName)
                    }
                DeleteAsset =
                    fun rawId -> async {
                        let container = scopeContainer ()
                        return! store.Delete(container, AssetId rawId)
                    }
            }

        let assetApiHandler: HttpHandler =
            Remoting.createApi ()
            |> Remoting.withRouteBuilder AssetApi.routeBuilder
            |> Remoting.fromContext assetApi
            |> Remoting.buildHttpHandler

        let uploadHandler = AssetUploadHandler.uploadHandler options

        app
        |> ServerApp.addContribution (Contribution.routes assetStoreCompanion [ uploadHandler; assetApiHandler ])
        |> ServerApp.addContribution (Contribution.service assetStoreCompanion assetStoreServiceConfig)

/// **Sets** the asset-store companion: the store, renderer, derivative
/// profiles and handlers `options` describes are composed once, when the
/// deployment resolves, onto the deployment as every builder left it — so a
/// `ServerApp` builder applied after this call still reaches it. Contributes
/// nothing unless the resolved `ServerConfig.AssetStore` is
/// `EnabledAssetStore`. A second call replaces the first.
///
///     ServerApp.empty
///     |> ServerApp.withConfig { config with AssetStore = EnabledAssetStore }
///     |> ServerApp.withStorage storage
///     |> AssetCompose.withAssetStore (AssetStoreCompanionOptions.create ())
///     |> ServerApp.run
let withAssetStore (options: AssetStoreCompanionOptions) (app: ServerApp) : ServerApp =
    ServerApp.withCompanion assetStoreCompanion (composeAssetStore options) app
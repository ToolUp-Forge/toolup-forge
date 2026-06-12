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
open ToolUp.Platform.Server

// ─── Phase 39 — AssetStoreServerApp composition root ────────────
//
// Mirrors `PublicRenderingServerApp` / `FormsServerApp` /
// `AIServerApp` shape: wraps a base `ServerApp` and adds
// `with*` helpers for asset-store-specific compose-time
// registrations (renderer, derivative profiles, options).
//
// **Strip-imports guarantee**: when
// `ServerConfig.AssetStore = NoAssetStore`, `run` short-
// circuits to `ServerApp.run app.Base` — no DI registrations,
// no handlers, no hosted services. Byte-for-byte equivalent to
// a base `ServerApp.run` of the same `Base`.

type AssetStoreServerApp = {
    Base: ServerApp
    /// Compose-time options. `AssetStoreOptions.defaults`
    /// covers the common case; deployments tune `MaxBytes` /
    /// `AcceptedMimeTypes` / `AltTextMaxChars` / `EmitAudit` via
    /// `withOptions`.
    Options: AssetStoreOptions
    /// Optional override for the `IDerivativeRenderer`. When
    /// `None`, `run` constructs the default
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
    /// `None`, `run` constructs the default `DefaultAssetStore`
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
}

/// Compose-time options for the async derivation mode (Phase 127).
and AsyncDerivationOptions = {
    /// Retry behaviour the scheduler applies to derivation jobs
    /// (GP 12 rule 3 — retry as data).
    RetryPolicy: JobRetryPolicy
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

module AssetStoreServerApp =

    let create () : AssetStoreServerApp = {
        Base = ServerApp.empty
        Options = AssetStoreOptions.defaults
        RendererOverride = None
        Profiles = DerivativeProfileRegistry.withWebDefault
        StoreOverride = None
        MimeRenderers = MimeRendererRegistry.empty
        AsyncDerivation = None
    }

    // ─── Delegating helpers (mirror every `ServerApp.with*`) ─────

    let withConfig (c: ServerConfig) (app: AssetStoreServerApp) : AssetStoreServerApp = {
        app with
            Base = ServerApp.withConfig c app.Base
    }

    let withAuth (a: IAuthProvider) (app: AssetStoreServerApp) : AssetStoreServerApp = {
        app with
            Base = ServerApp.withAuth a app.Base
    }

    let withLogger (l: ILogger) (app: AssetStoreServerApp) : AssetStoreServerApp = {
        app with
            Base = ServerApp.withLogger l app.Base
    }

    let withStorage (s: IBlobStorage) (app: AssetStoreServerApp) : AssetStoreServerApp = {
        app with
            Base = ServerApp.withStorage s app.Base
    }

    let withNotifications (n: INotificationChannel) (app: AssetStoreServerApp) : AssetStoreServerApp = {
        app with
            Base = ServerApp.withNotifications n app.Base
    }

    let withHealthCheck (check: HealthChecks.IHealthCheck) (app: AssetStoreServerApp) : AssetStoreServerApp = {
        app with
            Base = ServerApp.withHealthCheck check app.Base
    }

    let withEncryptedBlobStorage
        (resolver: IBlobEncryptionKeyResolver)
        (app: AssetStoreServerApp)
        : AssetStoreServerApp =
        {
            app with
                Base = ServerApp.withEncryptedBlobStorage resolver app.Base
        }

    let addModule (m: ServerModule) (app: AssetStoreServerApp) : AssetStoreServerApp = {
        app with
            Base = ServerApp.addModule m app.Base
    }

    let addModules (modules: ServerModule list) (app: AssetStoreServerApp) : AssetStoreServerApp = {
        app with
            Base = ServerApp.addModules modules app.Base
    }

    // ─── AssetStore-specific helpers ─────────────────────────────

    /// Override `AssetStoreOptions`. Defaults cover the common
    /// case; deployments tune `MaxBytes` / `AcceptedMimeTypes` /
    /// `AltTextMaxChars` / `EmitAudit`.
    let withOptions (options: AssetStoreOptions) (app: AssetStoreServerApp) : AssetStoreServerApp = {
        app with
            Options = options
    }

    /// Supply an explicit `IDerivativeRenderer`. Default
    /// (`None`) constructs `SkiaSharpDerivativeRenderer`.
    /// Deployments wanting wider format coverage register the
    /// optional `ImageMagickDerivativeRenderer` companion here.
    let withRenderer (renderer: IDerivativeRenderer) (app: AssetStoreServerApp) : AssetStoreServerApp = {
        app with
            RendererOverride = Some renderer
    }

    /// Register a derivative profile. `web-default` is seeded
    /// already; deployments add `podcast-card` /
    /// `directory-listing` / `og-twitter` shapes via this
    /// helper. Re-registering an existing id replaces it.
    let withDerivativeProfile
        (id: DerivativeProfileId)
        (specs: DerivativeSpec list)
        (app: AssetStoreServerApp)
        : AssetStoreServerApp =
        {
            app with
                Profiles = DerivativeProfileRegistry.register id specs app.Profiles
        }

    /// Supply an explicit `IAssetStore` impl. Default (`None`)
    /// constructs `DefaultAssetStore` over the configured
    /// `IBlobStorage`. Deployments substituting Cloudinary /
    /// Imgix / S3-direct-CDN supply their own impl here.
    let withAssetStore (store: IAssetStore) (app: AssetStoreServerApp) : AssetStoreServerApp = {
        app with
            StoreOverride = Some store
    }

    /// Phase 127 — register a profile whose entries mix image and
    /// general (arbitrary-MIME) derivatives. Re-registering an
    /// existing id replaces it; `withDerivativeProfile` remains the
    /// image-only shorthand.
    let withDerivativeProfileEntries
        (id: DerivativeProfileId)
        (entries: ProfileEntry list)
        (app: AssetStoreServerApp)
        : AssetStoreServerApp =
        {
            app with
                Profiles = DerivativeProfileRegistry.registerEntries id entries app.Profiles
        }

    /// Phase 127 — register a renderer for general (arbitrary-MIME)
    /// profile entries under `key`
    /// (`GeneralDerivativeSpec.RendererKey` names it). Renderer
    /// implementations carrying vendor dependencies ship as
    /// companions (GP 1).
    let withMimeRenderer
        (key: string)
        (renderer: IMimeDerivativeRenderer)
        (app: AssetStoreServerApp)
        : AssetStoreServerApp =
        {
            app with
                MimeRenderers = MimeRendererRegistry.register key renderer app.MimeRenderers
        }

    /// Phase 127 — opt into async job-backed derivation for
    /// `DerivationMode.AsyncJob` profile entries: derivations run on
    /// the deployment's `IJobScheduler`, completion surfaces over
    /// the notification channel
    /// (`DerivativeJobs.DerivativeReadyNotificationKey`), and the
    /// content-hash cache serves instantly thereafter. Without this
    /// opt-in nothing changes (GP 13): no job-handler registration,
    /// no channel traffic; async-mode entries fail typed.
    let withAsyncDerivation (options: AsyncDerivationOptions) (app: AssetStoreServerApp) : AssetStoreServerApp = {
        app with
            AsyncDerivation = Some options
    }

    /// Drive the final composition. When
    /// `ServerConfig.AssetStore = NoAssetStore`, short-circuits
    /// to `ServerApp.run` — byte-for-byte the same shape as the
    /// pre-Phase-39 base path. When `EnabledAssetStore`,
    /// registers DI singletons, mounts the Fable.Remoting
    /// handler at `/api/assets/` and the multipart upload
    /// handler at `/api/assets/upload`, and delegates to
    /// `ServerApp.run`.
    let run (app: AssetStoreServerApp) : int =
        match app.Base.Config.AssetStore with
        | NoAssetStore -> ServerApp.run app.Base
        | EnabledAssetStore ->
            let options = app.Options
            let profiles = app.Profiles
            let explicitRenderer = app.RendererOverride
            let explicitStore = app.StoreOverride
            let mimeRenderers = app.MimeRenderers
            let asyncDerivation = app.AsyncDerivation

            let asLogger =
                app.Base.Logger
                |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

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

                                            scheduler.RegisterHandler(
                                                DerivativeJobs.HandlerName,
                                                DerivativeJobHandler(
                                                    blob,
                                                    profiles,
                                                    mimeRenderers,
                                                    channel,
                                                    asLogger,
                                                    asyncOptions.RetryPolicy.MaxAttempts
                                                )
                                            )

                                            Some(
                                                DerivativeJobCoordinator(
                                                    blob,
                                                    scheduler,
                                                    asyncOptions.RetryPolicy,
                                                    asLogger
                                                )
                                            )
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

            // ─── Fable.Remoting handler ──────────────────────────
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

            let baseExt = app.Base.Extensions

            let mergedExt: ComposeExtensions = {
                baseExt with
                    Handlers = baseExt.Handlers @ [ uploadHandler; assetApiHandler ]
                    ServiceConfig =
                        match baseExt.ServiceConfig with
                        | None -> Some assetStoreServiceConfig
                        | Some baseFn -> Some(fun s -> assetStoreServiceConfig (baseFn s))
            }

            let final = { app.Base with Extensions = mergedExt }

            ServerApp.run final
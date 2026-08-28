// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.MediaLibrary.MediaCompose

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Server

// ─── Phase 88 — MediaLibraryServerApp composition root ────────────────
//
// Mirrors `AssetStoreServerApp` / `PublicRenderingServerApp`: wraps a
// base `ServerApp` and adds media-specific `with*` helpers.
//
// **Strip-imports guarantee (GP 13)**: when
// `ServerConfig.MediaLibrary = NoMediaLibrary`, `run` short-circuits to
// `ServerApp.run app.Base` — no DI registrations, no range handlers, no
// URL-signing key, no health probe. Byte-for-byte equivalent to a base
// `ServerApp.run` of the same `Base`.

type MediaLibraryServerApp = {
    Base: ServerApp
    Options: MediaLibraryOptions
    /// Override for the poster / probe hook. `None` → `NoopMediaDerivation`
    /// (no transcode dependency). Install `ToolUp.Media.FFmpeg` here.
    DerivationOverride: IMediaDerivation option
    /// Override for the HLS transcode hook. `None` → `NoopMediaTranscoder`
    /// (single-file progressive download).
    TranscoderOverride: IMediaTranscoder option
    /// Override for the `IMediaLibrary` impl. `None` → `DefaultMediaLibrary`
    /// over the configured `IBlobStorage`.
    StoreOverride: IMediaLibrary option
    /// Phase 472 — the CDN / edge cache media publication and deletion
    /// purge through. `None` (default) → no edge fan-out at all and no
    /// DI registration; the library behaves byte-for-byte pre-472
    /// (GP 11 / GP 13).
    EdgeCache: IEdgeCache option
    /// Phase 472 — a CDN-native URL signer. `None` (default) keeps
    /// `IMediaLibrary.SignedUrl` on the origin HMAC path. When composed,
    /// minting delegates to it; the origin route and its verification
    /// stay mounted either way.
    DelegatedUrlSigner: SignedUrl.IDelegatedUrlSigner option
}

module MediaLibraryServerApp =

    let create () : MediaLibraryServerApp = {
        Base = ServerApp.empty
        Options = MediaLibraryOptions.defaults
        DerivationOverride = None
        TranscoderOverride = None
        StoreOverride = None
        EdgeCache = None
        DelegatedUrlSigner = None
    }

    // ─── Delegating helpers (mirror every `ServerApp.with*`) ─────

    let withConfig (c: ServerConfig) (app: MediaLibraryServerApp) : MediaLibraryServerApp = {
        app with
            Base = ServerApp.withConfig c app.Base
    }

    let withAuth (a: IAuthProvider) (app: MediaLibraryServerApp) : MediaLibraryServerApp = {
        app with
            Base = ServerApp.withAuth a app.Base
    }

    let withLogger (l: ILogger) (app: MediaLibraryServerApp) : MediaLibraryServerApp = {
        app with
            Base = ServerApp.withLogger l app.Base
    }

    let withStorage (s: IBlobStorage) (app: MediaLibraryServerApp) : MediaLibraryServerApp = {
        app with
            Base = ServerApp.withStorage s app.Base
    }

    let withNotifications (n: INotificationChannel) (app: MediaLibraryServerApp) : MediaLibraryServerApp = {
        app with
            Base = ServerApp.withNotifications n app.Base
    }

    let addModule (m: ServerModule) (app: MediaLibraryServerApp) : MediaLibraryServerApp = {
        app with
            Base = ServerApp.addModule m app.Base
    }

    let addModules (modules: ServerModule list) (app: MediaLibraryServerApp) : MediaLibraryServerApp = {
        app with
            Base = ServerApp.addModules modules app.Base
    }

    // ─── MediaLibrary-specific helpers ───────────────────────────

    let withOptions (options: MediaLibraryOptions) (app: MediaLibraryServerApp) : MediaLibraryServerApp = {
        app with
            Options = options
    }

    /// Supply a poster / probe provider (the FFmpeg sub-companion).
    let withDerivation (derivation: IMediaDerivation) (app: MediaLibraryServerApp) : MediaLibraryServerApp = {
        app with
            DerivationOverride = Some derivation
    }

    /// Supply an HLS transcode provider (the FFmpeg / cloud sub-companion).
    let withTranscoder (transcoder: IMediaTranscoder) (app: MediaLibraryServerApp) : MediaLibraryServerApp = {
        app with
            TranscoderOverride = Some transcoder
    }

    /// Supply an explicit `IMediaLibrary` impl (a CDN-direct or
    /// cloud-native store). Default constructs `DefaultMediaLibrary`.
    let withMediaLibrary (store: IMediaLibrary) (app: MediaLibraryServerApp) : MediaLibraryServerApp = {
        app with
            StoreOverride = Some store
    }

    /// Phase 472 — compose the CDN / edge cache this deployment sits
    /// behind. An upload and a delete then purge the item's derived
    /// prefix and its two original-serving paths, fire-and-forget, so a
    /// CDN outage never fails the operation that triggered it (GP 7).
    ///
    /// Registered as an `IEdgeCache` DI singleton as well as being
    /// handed to the default library, so a deployment's own handlers
    /// resolve the SAME edge rather than composing a second one.
    ///
    /// Note this is *not* consulted when `withMediaLibrary` supplies a
    /// custom `IMediaLibrary`: a custom store owns its own write path
    /// and therefore its own invalidation. The DI registration still
    /// happens, so that store can resolve the edge itself.
    let withEdgeCache (edgeCache: IEdgeCache) (app: MediaLibraryServerApp) : MediaLibraryServerApp = {
        app with
            EdgeCache = Some edgeCache
    }

    /// Phase 472 — compose a CDN-native URL signer. `SignedUrl` then
    /// mints an absolute, edge-verified URL instead of an origin-relative
    /// `/media/signed/{id}?token=` one. The origin HMAC route stays
    /// mounted and keeps verifying, so previously-minted tokens work
    /// until they expire and removing the signer restores the prior
    /// behaviour exactly.
    let withDelegatedUrlSigner
        (signer: SignedUrl.IDelegatedUrlSigner)
        (app: MediaLibraryServerApp)
        : MediaLibraryServerApp =
        {
            app with
                DelegatedUrlSigner = Some signer
        }

    /// Build the `IMediaApi` Fable.Remoting contract from request context.
    let private mediaApi (options: MediaLibraryOptions) (ctx: HttpContext) : IMediaApi =
        let lib = ctx.RequestServices.GetService(typeof<IMediaLibrary>) :?> IMediaLibrary

        let sessions =
            ctx.RequestServices.GetService(typeof<IUploadSessionStore>) :?> IUploadSessionStore

        let scope () =
            match ctx.Items.TryGetValue "ToolUp.StorageScope" with
            | true, (:? StorageScope as s) -> s
            | _ -> failwith "No active scope — IMediaApi requires authentication"

        {
            GetMedia =
                fun raw -> async {
                    let s = scope ()
                    return! lib.Get(s.Container, MediaId raw)
                }
            ListMedia =
                fun (prefix, page) -> async {
                    let s = scope ()
                    return! lib.List(s.Container, prefix, page)
                }
            DeleteMedia =
                fun raw -> async {
                    let s = scope ()
                    return! lib.Delete(s.Container, MediaId raw)
                }
            GetSignedUrl =
                fun (raw, ttlSeconds) -> async {
                    let s = scope ()
                    return! lib.SignedUrl(MediaId raw, s, TimeSpan.FromSeconds(float ttlSeconds))
                }

            // Phase 469 — the declaration is smart-constructed HERE,
            // against this deployment's options, from wire primitives.
            // `uploadedBy` comes from the resolved scope, never the
            // wire, so an upload cannot be attributed elsewhere.
            BeginUpload =
                fun (filename, mimeType, declaredSize, caption) -> async {
                    let s = scope ()

                    match MediaUploadDeclaration.create options filename mimeType declaredSize s.ScopeId caption with
                    | Error e -> return Error(InvalidDeclaration e)
                    | Ok declaration ->
                        match! sessions.BeginUpload(s.Container, declaration) with
                        | Error e -> return Error e
                        | Ok sessionId -> return Ok(UploadSessionId.value sessionId)
                }
            AppendChunk =
                fun (rawSession, offset, chunk) -> async {
                    let s = scope ()
                    return! sessions.AppendChunk(s.Container, UploadSessionId rawSession, offset, chunk)
                }
            CommitUpload =
                fun rawSession -> async {
                    let s = scope ()
                    return! sessions.CommitUpload(s.Container, UploadSessionId rawSession)
                }
            AbortUpload =
                fun rawSession -> async {
                    let s = scope ()
                    return! sessions.AbortUpload(s.Container, UploadSessionId rawSession)
                }
        }

    /// Drive the final composition. `NoMediaLibrary` short-circuits to
    /// `ServerApp.run`; `EnabledMediaLibrary` registers the signer +
    /// `IMediaLibrary` + `IUploadSessionStore` DI singletons, mounts the
    /// range-serving handlers and the Fable.Remoting `IMediaApi`, adds
    /// the readiness probe + options validator, and delegates to
    /// `ServerApp.run`.
    let run (app: MediaLibraryServerApp) : int =
        match app.Base.Config.MediaLibrary with
        | NoMediaLibrary -> ServerApp.run app.Base
        | EnabledMediaLibrary ->
            let options = app.Options
            let notifications = app.Base.Notifications

            let asLogger =
                app.Base.Logger
                |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

            let derivationOverride = app.DerivationOverride
            let transcoderOverride = app.TranscoderOverride
            let storeOverride = app.StoreOverride
            let edgeCache = app.EdgeCache
            let delegatedSigner = app.DelegatedUrlSigner

            // ─── DI registrations ────────────────────────────────
            let mediaServiceConfig (services: IServiceCollection) =
                let services =
                    // Phase 472 — the composed options, so the range
                    // handlers can read this deployment's declared edge
                    // cacheability. Registered under
                    // `EnabledMediaLibrary` only; the handlers fall back
                    // to `MediaLibraryOptions.defaults` (which declares
                    // nothing) when it is absent, so a hand-built host
                    // that never registers it emits exactly the headers
                    // it emitted before this phase.
                    services.AddSingleton<MediaLibraryOptions>(options)

                let services =
                    // Phase 472 — the edge cache, registered so a
                    // deployment's own handlers (and a custom
                    // `IMediaLibrary`) resolve the SAME edge the library
                    // publishes through. Nothing is registered when none
                    // is composed (GP 13).
                    match edgeCache with
                    | Some edge -> services.AddSingleton<IEdgeCache>(edge)
                    | None -> services

                let services =
                    match delegatedSigner with
                    | Some ds -> services.AddSingleton<SignedUrl.IDelegatedUrlSigner>(ds)
                    | None -> services

                services
                    .AddSingleton<SignedUrl.MediaUrlSigner>(
                        System.Func<System.IServiceProvider, SignedUrl.MediaUrlSigner>(fun sp ->
                            let secrets = sp.GetService(typeof<ISecretStore>) :?> ISecretStore
                            SignedUrl.MediaUrlSigner(secrets))
                    )
                    // Phase 471 — the AES-128 HLS key store. Registered
                    // under `EnabledMediaLibrary` only, over the
                    // `ISecretStore` the URL signer already needs, so
                    // there is no second substrate to provision and a
                    // disabled deployment registers nothing (GP 13).
                    // Resolved by BOTH the library (to mint at transcode
                    // time) and the key endpoint (to serve), so the two
                    // are looking at one store by construction.
                    .AddSingleton<HlsKeyDelivery.MediaHlsKeyStore>(
                        System.Func<System.IServiceProvider, HlsKeyDelivery.MediaHlsKeyStore>(fun sp ->
                            let secrets = sp.GetService(typeof<ISecretStore>) :?> ISecretStore
                            HlsKeyDelivery.MediaHlsKeyStore(secrets, asLogger, options))
                    )
                    .AddSingleton<IMediaLibrary>(
                        System.Func<System.IServiceProvider, IMediaLibrary>(fun sp ->
                            match storeOverride with
                            | Some s -> s
                            | None ->
                                let blob = sp.GetService(typeof<IBlobStorage>) :?> IBlobStorage

                                let signer =
                                    sp.GetService(typeof<SignedUrl.MediaUrlSigner>) :?> SignedUrl.MediaUrlSigner

                                let derivation =
                                    derivationOverride
                                    |> Option.defaultWith (fun () -> NoopMediaDerivation.create ())

                                let transcoder =
                                    transcoderOverride
                                    |> Option.defaultWith (fun () -> NoopMediaTranscoder.create ())

                                let hlsKeys =
                                    sp.GetService(typeof<HlsKeyDelivery.MediaHlsKeyStore>)
                                    :?> HlsKeyDelivery.MediaHlsKeyStore

                                // Phase 740 — carry the deployment's
                                // metrics sink on the edge so purge
                                // outcomes are counted at the choke
                                // point. Resolved here because the
                                // container does not exist when
                                // `withEdgeCache` declared the edge.
                                // Identity when no live sink is
                                // registered, so an unmetered
                                // deployment composes exactly what it
                                // composed before.
                                let meteredEdge = edgeCache |> Option.map (EdgeCache.withMetricsFrom sp)

                                DefaultMediaLibrary(
                                    blob,
                                    signer,
                                    derivation,
                                    transcoder,
                                    notifications,
                                    options,
                                    asLogger,
                                    Option.ofObj hlsKeys,
                                    meteredEdge,
                                    delegatedSigner
                                )
                                :> IMediaLibrary)
                    )
                    // Phase 469 — the resumable-upload seam. Registered
                    // only under `EnabledMediaLibrary`, over the same
                    // `IBlobStorage` and the composed `IMediaLibrary`,
                    // so there is no second substrate to provision and
                    // a disabled deployment registers nothing (GP 13).
                    .AddSingleton<IUploadSessionStore>(
                        System.Func<System.IServiceProvider, IUploadSessionStore>(fun sp ->
                            let blob = sp.GetService(typeof<IBlobStorage>) :?> IBlobStorage
                            let lib = sp.GetService(typeof<IMediaLibrary>) :?> IMediaLibrary

                            BlobUploadSessionStore(blob, lib, notifications, options, asLogger) :> IUploadSessionStore)
                    )

            let mediaApiHandler =
                Remoting.createApi ()
                |> Remoting.withRouteBuilder MediaApi.routeBuilder
                |> Remoting.fromContext (mediaApi options)
                |> Remoting.buildHttpHandler

            let baseExt = app.Base.Extensions

            let mergedExt: ComposeExtensions = {
                baseExt with
                    // Phase 473 — the playback beacon rides the same
                    // handler list as the serving routes. It is mounted
                    // only under `EnabledMediaLibrary`, so a deployment
                    // that composes no media library has no beacon
                    // endpoint at all (GP 13).
                    Handlers =
                        baseExt.Handlers
                        @ RangeHandler.handlers
                        @ [ PlaybackTelemetry.beaconHandler; mediaApiHandler ]
                    ServiceConfig =
                        match baseExt.ServiceConfig with
                        | None -> Some mediaServiceConfig
                        | Some baseFn -> Some(fun s -> mediaServiceConfig (baseFn s))
            }

            let withExtensions = { app.Base with Extensions = mergedExt }

            // Phase 473 — declare the two telemetry series so a composed
            // metrics sink pre-allocates them and the emissions flow
            // rather than being dropped as unregistered. Declaring costs
            // nothing when no sink is composed: `NoOpMetricsSink` reads
            // no registrations.
            let withMetrics =
                ServerApp.withMetricRegistrations PlaybackTelemetry.registrations withExtensions

            let withValidator =
                ServerApp.withConfigValidator (MediaConfigValidator.create options) withMetrics

            let final =
                match app.Base.Storage with
                | Some blob ->
                    withValidator
                    |> ServerApp.withHealthCheck (MediaHealthCheck.create blob)
                    // Phase 468 — advisory (never an error) when the
                    // composed store refuses ranged reads, so range
                    // serving falls back to whole-object slicing.
                    |> ServerApp.withConfigValidator (MediaConfigValidator.createRangeProbe blob)
                    // Phase 741 — advisory (never an error) when the
                    // composed store cannot compose stored parts, so
                    // resumable commit falls back to materialised
                    // assembly.
                    |> ServerApp.withConfigValidator (MediaConfigValidator.createComposeProbe blob)
                | None -> withValidator

            ServerApp.run final
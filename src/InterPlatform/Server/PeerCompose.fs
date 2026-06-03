// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.InterPlatform.PeerCompose

open System.Net.Http
open System.Net.Http.Headers
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.Secrets
open ToolUp.Platform.Server

// ─── Phase 18 — PeerServerApp composition root ───────────────────────
//
// `PeerServerApp` mirrors the `PublicRenderingServerApp` companion shape:
// it wraps a base `ServerApp` and adds `with*` helpers for the peer-
// substrate-specific registrations (the local deployment identity and
// the contracts this deployment exposes to peers). The companion brings
// its own DI singletons and mounts `JsonRpcPeerHost.routes` onto the
// SDK's route chain via `ComposeExtensions.Handlers`.
//
// **Required substrate**: nothing beyond what `ServerApp` itself needs.
// Long-running contract methods additionally require
// `ServerConfig.JobScheduler = InProcessJobScheduler` (or a distributed
// scheduler companion) — without it the `PeerJobFusion` singleton is not
// registered and long-running dispatch returns a clear "not enabled"
// error. `IPeerAuthProvider` reads its signing keys from the resolved
// `ISecretStore`; the peer directory + job-result store ride on the
// resolved `IBlobStorage`.
//
// **Strip-imports guarantee**: when
// `ServerConfig.PeerSubstrate = NoPeerSubstrate`, `run` short-circuits to
// `ServerApp.run app.Base` — no DI registrations, no `/peer/v1`
// handlers, no peer audit. Byte-for-byte equivalent to a base
// `ServerApp.run` of the same `Base` (GP 13 — zero cost when unused).

/// Record form of the peer compose arguments. Wraps a base `ServerApp`
/// and carries the local deployment identity (used by the outbound
/// client / handshake) plus the contracts this deployment hosts. Like
/// `PublicRenderingServerApp`, the strip-imports gate is read from
/// `ServerConfig.PeerSubstrate` (set via `withConfig`), not a field on
/// this record, so a single `ServerConfig` override controls it.
type PeerServerApp = {
    Base: ServerApp
    /// The local deployment's own peer identity, vouched for by the
    /// outbound `IPeerClient` / `IPeerHandshake`. `None` for a host-only
    /// deployment that exposes contracts but never calls peers — the
    /// calling-side singletons are still registered but fail closed
    /// (no signing key for an empty id) if actually used.
    LocalPeer: PeerIdentity option
    /// Contract builders, each turning the resolved `PeerJobFusion option`
    /// into a `PeerContractHost`. Run once at first `IPlatformPeer`
    /// resolution: each host's `Registration` is registered on the peer
    /// and its long-running `JobHandlers` on the scheduler. The fusion is
    /// threaded in so `LongRunning` methods can schedule background jobs.
    Contracts: (PeerJobFusion option -> PeerContractHost) list
}

module PeerServerApp =

    let create () : PeerServerApp = {
        Base = ServerApp.empty
        LocalPeer = None
        Contracts = []
    }

    // ─── Delegating helpers (mirror every `ServerApp.with*`) ─────

    let withConfig (c: ServerConfig) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.withConfig c app.Base
    }

    let withAuth (a: IAuthProvider) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.withAuth a app.Base
    }

    let withLogger (l: ILogger) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.withLogger l app.Base
    }

    let withStorage (s: IBlobStorage) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.withStorage s app.Base
    }

    let withNotifications (n: INotificationChannel) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.withNotifications n app.Base
    }

    let withTransactionalSink (sink: INotificationSink) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.withTransactionalSink sink app.Base
    }

    let withHealthCheck (check: HealthChecks.IHealthCheck) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.withHealthCheck check app.Base
    }

    let withConfigValidator (validator: ConfigValidation.IConfigValidator) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.withConfigValidator validator app.Base
    }

    let withEncryptedBlobStorage (resolver: IBlobEncryptionKeyResolver) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.withEncryptedBlobStorage resolver app.Base
    }

    let withEntity<'T> (registration: EntityTypes.EntityRegistration<'T>) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.withEntity registration app.Base
    }

    let withPreMiddleware (f: IApplicationBuilder -> IApplicationBuilder) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.withPreMiddleware f app.Base
    }

    let withPostMiddleware (f: IApplicationBuilder -> IApplicationBuilder) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.withPostMiddleware f app.Base
    }

    let addModule (m: ServerModule) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.addModule m app.Base
    }

    let addModules (modules: ServerModule list) (app: PeerServerApp) : PeerServerApp = {
        app with
            Base = ServerApp.addModules modules app.Base
    }

    // ─── Peer-substrate-specific helpers ─────────────────────────

    /// Set the local deployment's own peer identity. Required when this
    /// deployment *calls* peers (the outbound token vouches for this id,
    /// and the signing key is read from `ISecretStore` under
    /// `peers/{peerId}/signing-key`). A host-only deployment that just
    /// exposes contracts can omit it.
    let withLocalPeer (identity: PeerIdentity) (app: PeerServerApp) : PeerServerApp = {
        app with
            LocalPeer = Some identity
    }

    /// Register a contract this deployment exposes to peers. Author the
    /// host with `JsonRpcPeerHost.contract<'TApi>`; the fusion the SDK
    /// resolves is threaded into the builder so `LongRunning` methods can
    /// schedule background jobs. Multiple calls accumulate; re-registering
    /// the same contract id overwrites the previous binding at dispatch
    /// time.
    let withContract (builder: PeerJobFusion option -> PeerContractHost) (app: PeerServerApp) : PeerServerApp = {
        app with
            Contracts = app.Contracts @ [ builder ]
    }

    /// Drive the final composition. When `ServerConfig.PeerSubstrate` is
    /// `NoPeerSubstrate`, short-circuits to `ServerApp.run` — byte-for-
    /// byte the same shape as a base `ServerApp.run`. When
    /// `EnabledPeerSubstrate`, registers the peer DI singletons, mounts
    /// `JsonRpcPeerHost.routes`, and delegates to `ServerApp.run`.
    let run (app: PeerServerApp) : int =
        match app.Base.Config.PeerSubstrate with
        | NoPeerSubstrate ->
            // Strip-imports path: zero contribution to the base
            // `ServerApp.run`. Same shape as if the peer substrate were
            // never imported.
            ServerApp.run app.Base
        | EnabledPeerSubstrate ->
            let contracts = app.Contracts
            let schedulerEnabled = app.Base.Config.JobScheduler <> NoJobScheduler

            let localIdentity =
                app.LocalPeer |> Option.defaultValue { PeerId = ""; DisplayName = "" }

            // One process-lifetime client shared by the outbound
            // transport and the handshake's capability fetch. Lives until
            // process exit (the documented single-long-lived-client
            // pattern); never disposed.
            let sharedHttpClient = new HttpClient()

            // The handshake's outbound capability fetch. The receiver's
            // `GET /peer/v1/capabilities` answers with a bare
            // `CapabilityList` (not a JSON-RPC envelope), so this reads it
            // directly. Mints a per-call token vouching for the local
            // identity; a transport failure is `HandshakeUnreachable`, an
            // auth / non-2xx refusal is `HandshakeRejected`.
            let fetchRemote
                (auth: IPeerAuthProvider)
                (target: TargetPeer)
                : Async<Result<CapabilityList, PeerHandshakeError>> =
                async {
                    let! tokenResult = auth.IssuePeerToken(localIdentity, target.Peer, Anonymous)

                    match tokenResult with
                    | Error e -> return Error(HandshakeRejected(JsonRpc.errorMessage e))
                    | Ok token ->
                        try
                            use request =
                                new HttpRequestMessage(HttpMethod.Get, $"{target.BaseUrl}/peer/v1/capabilities")

                            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                            let! response = sharedHttpClient.SendAsync request |> Async.AwaitTask
                            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                            if response.IsSuccessStatusCode then
                                return Ok(JsonRpc.deserialize<CapabilityList> body)
                            else
                                return Error(HandshakeRejected body)
                        with ex ->
                            return Error(HandshakeUnreachable ex.Message)
                }

            let peerServiceConfig (services: IServiceCollection) =
                services
                    .AddSingleton<IPeerAuthProvider>(
                        System.Func<System.IServiceProvider, IPeerAuthProvider>(fun sp ->
                            let secrets = sp.GetService(typeof<ISecretStore>) :?> ISecretStore
                            JwtPeerAuthProvider(secrets) :> IPeerAuthProvider)
                    )
                    .AddSingleton<IPeerJobResultStore>(
                        System.Func<System.IServiceProvider, IPeerJobResultStore>(fun sp ->
                            let blobs = sp.GetService(typeof<IBlobStorage>) :?> IBlobStorage
                            BlobPeerJobResultStore(blobs) :> IPeerJobResultStore)
                    )
                    .AddSingleton<IPeerRegistry>(
                        System.Func<System.IServiceProvider, IPeerRegistry>(fun sp ->
                            let blobs = sp.GetService(typeof<IBlobStorage>) :?> IBlobStorage
                            BlobPeerRegistry(blobs) :> IPeerRegistry)
                    )
                    .AddSingleton<IPlatformPeer>(
                        // Built once on first resolution (the first inbound
                        // peer request). Constructing it registers every
                        // contract on the peer and every long-running job
                        // handler on the scheduler, so handlers are present
                        // before any `LongRunning` dispatch schedules a job.
                        System.Func<System.IServiceProvider, IPlatformPeer>(fun sp ->
                            let peer = DefaultPlatformPeer() :> IPlatformPeer

                            let fusion =
                                sp.GetService(typeof<PeerJobFusion>)
                                |> Option.ofObj
                                |> Option.map (fun x -> x :?> PeerJobFusion)

                            let scheduler =
                                sp.GetService(typeof<IJobScheduler>)
                                |> Option.ofObj
                                |> Option.map (fun x -> x :?> IJobScheduler)

                            for builder in contracts do
                                let host = builder fusion
                                peer.RegisterContract host.Registration

                                match scheduler with
                                | Some s ->
                                    host.JobHandlers
                                    |> List.iter (fun (name, handler) -> s.RegisterHandler(name, handler))
                                | None -> ()

                            peer)
                    )
                    .AddSingleton<IPeerClient>(
                        System.Func<System.IServiceProvider, IPeerClient>(fun sp ->
                            let auth = sp.GetService(typeof<IPeerAuthProvider>) :?> IPeerAuthProvider
                            HttpPeerClient(sharedHttpClient, auth, localIdentity) :> IPeerClient)
                    )
                    .AddSingleton<IPeerHandshake>(
                        System.Func<System.IServiceProvider, IPeerHandshake>(fun sp ->
                            let peer = sp.GetService(typeof<IPlatformPeer>) :?> IPlatformPeer
                            let auth = sp.GetService(typeof<IPeerAuthProvider>) :?> IPeerAuthProvider
                            InMemoryPeerHandshake(peer, fetchRemote auth) :> IPeerHandshake)
                    )
                |> fun s ->
                    // Long-running fusion is registered only when the job
                    // substrate is enabled; absent, the `IPlatformPeer`
                    // factory resolves `None` and long-running methods
                    // report "not enabled" (GP 13 — zero cost when unused).
                    if schedulerEnabled then
                        s.AddSingleton<PeerJobFusion>(
                            System.Func<System.IServiceProvider, PeerJobFusion>(fun sp ->
                                let scheduler = sp.GetService(typeof<IJobScheduler>) :?> IJobScheduler
                                let resultStore = sp.GetService(typeof<IPeerJobResultStore>) :?> IPeerJobResultStore

                                {
                                    Scheduler = scheduler
                                    ResultStore = resultStore
                                })
                        )
                    else
                        s

            let baseExt = app.Base.Extensions

            let mergedExt: ComposeExtensions = {
                baseExt with
                    Handlers = baseExt.Handlers @ [ JsonRpcPeerHost.routes ]
                    ServiceConfig =
                        match baseExt.ServiceConfig with
                        | None -> Some peerServiceConfig
                        | Some baseFn -> Some(fun s -> peerServiceConfig (baseFn s))
            }

            let final = { app.Base with Extensions = mergedExt }

            ServerApp.run final
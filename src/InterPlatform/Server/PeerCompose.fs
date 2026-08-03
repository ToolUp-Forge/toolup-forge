// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.InterPlatform.PeerCompose

open System.Net.Http
open System.Net.Http.Headers
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
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
    /// Phase 18a — when `true`, the SDK-shipped audit-transparency
    /// contract (`PeerAudit.contractId`) is registered alongside the
    /// author contracts, letting calling peers read back the receiver's
    /// audit rows for their *own* calls (scoped to the validated caller).
    /// Off by default — opt in with `withPeerAuditTransparency` (GP 13).
    AuditTransparency: bool
    /// Phase 18d — author-declared per-method capability profiles, keyed
    /// by contract id. Aggregated over the live contract table by the
    /// `IPeerProfileProvider` and served at
    /// `GET /peer/v1/capabilities/profile`. A contract without a declared
    /// profile is still advertised (versions only, no per-method
    /// lifecycle). Build entries with `PeerCapabilityNegotiation.profileFor`.
    ContractProfiles: ContractProfile list
    /// Phase 590 — the peer contracts this deployment *consumes* (calls
    /// on counterpart instances), declared at compose time so the
    /// `PeerSurface` descriptor derives the initiating half of the wire
    /// face from the composition record. Purely descriptive: dispatch
    /// still goes through `JsonRpcPeerClient.create`; `run` reads nothing
    /// from this list. Declare entries with `PeerSurface.consumes<'TApi>`
    /// (typed) or a literal `ConsumedContract`.
    ConsumedContracts: ConsumedContract list
    /// Phase 309 — when `true`, a composition that hosts peer contracts
    /// without a `LocalPeer` identity FAILS at `run` instead of logging
    /// the advisory. Off by default, so an existing host-only deployment
    /// keeps starting exactly as it did (GP 11); opt in with
    /// `withStrictAudienceBinding`. Purely a compose-time gate — it
    /// changes no registration and reaches no request path.
    StrictAudienceBinding: bool
    /// Phase 343 — when `true`, a non-2xx answer to the outbound
    /// capability-*profile* fetch degrades to the bare capability list
    /// (every contract advertised, no method lifecycle) instead of
    /// failing the handshake. **Off by default, which is a deliberate
    /// change of behaviour**: the degrade drops exactly the `Deprecated`
    /// / `Removed` declarations negotiation exists to read, and it fires
    /// on a status code the answering side chooses. Opt in with
    /// `withLegacyProfileFallback` when federating with a peer that
    /// genuinely predates `GET /peer/v1/capabilities/profile`. See
    /// `PeerRemoteProfile`.
    LegacyProfileFallback: bool
    /// Phase 315 — the receiver-side wire limits the `/peer/v1/*`
    /// handlers enforce. `PeerWireLimits.defaults` unless a composition
    /// says otherwise; registered as a DI singleton so the parameterless
    /// `JsonRpcPeerHost.routes` value can read it per request. Set it
    /// with `withWireLimits`.
    WireLimits: PeerWireLimits
    /// Phase 331 — the receiver-side cascade ceilings the contract route
    /// derives the trusted `PeerCallContext` under.
    /// `PeerCascadePolicy.defaults` unless a composition says otherwise;
    /// registered as a DI singleton so the parameterless
    /// `JsonRpcPeerHost.routes` value can read it per request. `run`
    /// fills its `LocalPeerId` in from the composed `LocalPeer` — a
    /// composition never sets that field by hand, because the whole
    /// point is that the receiver's own identity is the one thing it
    /// does not take from the caller. Set the ceilings with
    /// `withCascadePolicy`.
    CascadePolicy: PeerCascadePolicy
}

/// Phase 309 — a composition's audience-binding posture, classified at
/// compose time. `JwtPeerAuthProvider` binds an inbound token's `aud`
/// claim to `localIdentity.PeerId`, and that id is `""` whenever no
/// `LocalPeer` was composed — so the Phase 130 confused-deputy defence is
/// silently *off* on exactly the host-only deployments documented as
/// omitting it. This DU makes the posture a value rather than an
/// inference, so a deployment (or a test) asserts on data instead of
/// scraping a log line — the same shape `ModuleLoadOutcome` takes for
/// module-load observability.
type PeerAudienceBinding =
    /// `ServerConfig.PeerSubstrate = NoPeerSubstrate` — no peer token is
    /// validated here at all, so there is nothing to bind.
    | AudienceBindingOff
    /// A usable `LocalPeer` is composed: every inbound token must carry
    /// `aud` equal to `receiverId`, fixed-time compared.
    | AudienceBindingEnforced of receiverId: string
    /// **The composition defect.** Contracts are hosted (or the
    /// audit-transparency contract is), but no usable `LocalPeer` id was
    /// composed, so no `aud` check fires on the exposed surface.
    | AudienceBindingMissing
    /// No `LocalPeer`, and nothing is hosted — the deployment exposes no
    /// contract for a mis-addressed token to be spent against, so the
    /// absent binding costs nothing.
    | AudienceBindingIdle

module PeerServerApp =

    let create () : PeerServerApp = {
        Base = ServerApp.empty
        LocalPeer = None
        Contracts = []
        AuditTransparency = false
        ContractProfiles = []
        ConsumedContracts = []
        StrictAudienceBinding = false
        LegacyProfileFallback = false
        WireLimits = PeerWireLimits.defaults
        CascadePolicy = PeerCascadePolicy.defaults
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

    /// Phase 18a — register the SDK-shipped cross-deployment audit-
    /// transparency contract. Once composed, a calling peer can build a
    /// `JsonRpcPeerClient.create<IPeerAuditApi>` proxy against this
    /// deployment and read back the rows this deployment logged for the
    /// *caller's own* calls (scoped to the validated caller id — a peer
    /// can never see another peer's rows). The contract reads the
    /// resolved `IAuditLog`; a deployment with `AuditLog = NoAuditLog`
    /// still registers it but answers with an empty trail. Off by default
    /// (GP 13).
    let withPeerAuditTransparency (app: PeerServerApp) : PeerServerApp = { app with AuditTransparency = true }

    /// Phase 18d — declare a contract's per-method capability profile so
    /// callers can negotiate individual methods (deprecation windows,
    /// breaking-change announcements) at handshake time. Build the profile
    /// with `PeerCapabilityNegotiation.profileFor<'TApi>` (reflection
    /// auto-populates the method list as `Active`; an overlay marks
    /// specific `(method, version)` pairs `Deprecated` / `Removed`).
    /// Multiple calls accumulate; the profile is served at
    /// `GET /peer/v1/capabilities/profile` and read by `NegotiateMethod`.
    let withContractProfile (profile: ContractProfile) (app: PeerServerApp) : PeerServerApp = {
        app with
            ContractProfiles = app.ContractProfiles @ [ profile ]
    }

    /// Phase 590 — declare a peer contract this deployment *consumes*
    /// (calls on a counterpart instance). Purely descriptive: the
    /// declaration drives the `PeerSurface` descriptor's `Consumes` half
    /// — it registers no dispatch machinery and `run` ignores it. Build
    /// the declaration with `PeerSurface.consumes<'TApi>` so it stays
    /// tied to a real contract type. Multiple calls accumulate.
    let withConsumedContract (consumed: ConsumedContract) (app: PeerServerApp) : PeerServerApp = {
        app with
            ConsumedContracts = app.ConsumedContracts @ [ consumed ]
    }

    /// Phase 18f — register a commutative-cipher backend and the two-party
    /// private-set-intersection protocol over it, so contract handlers and
    /// modules can resolve `ICommutativeCipher` / `IPrivateSetIntersection`
    /// from DI.
    ///
    /// **Absent by default, and this is why it is a `with*` call rather
    /// than a default singleton beside `ICleanRoomBroker`.** There is no
    /// defensible SDK default here: `InMemoryCommutativeCipher` is a
    /// reference backend nobody should reach by accident, and silently
    /// picking the curve backend for every peer-enabled deployment would
    /// hand a cryptographic choice to whoever forgot to make one. A
    /// deployment that never calls this registers nothing and pays nothing
    /// — no singleton, no allocation, byte-for-byte unchanged (GP 11 /
    /// GP 13).
    ///
    /// Registered through the base `ServerApp`'s `ServiceConfig` seam, not
    /// the peer branch, so PSI is available to a deployment that uses the
    /// primitive without hosting peer contracts. `TryAdd`, so a deployment
    /// that registered its own implementation earlier keeps it.
    let withCommutativeCipher (cipher: ICommutativeCipher) (app: PeerServerApp) : PeerServerApp =
        let register (services: IServiceCollection) =
            services.TryAddSingleton<ICommutativeCipher>(cipher)
            services.TryAddSingleton<IPrivateSetIntersection>(PrivateSetIntersection.create cipher)
            services

        let extensions = app.Base.Extensions

        {
            app with
                Base = {
                    app.Base with
                        Extensions = {
                            extensions with
                                ServiceConfig =
                                    match extensions.ServiceConfig with
                                    | None -> Some register
                                    | Some existing -> Some(existing >> register)
                        }
                }
        }

    /// Phase 309 — refuse to start when this deployment hosts peer
    /// contracts without a `LocalPeer` identity, instead of logging the
    /// advisory and carrying on with audience binding disabled.
    ///
    /// **Opt-in, and deliberately not the default.** The advisory is
    /// enough for an operator who is watching; the hard gate is for a
    /// deployment that wants the composition defect to be impossible to
    /// ship rather than merely visible. Defaulting to failure would
    /// break every existing host-only composition on upgrade (GP 11) —
    /// the exact deployments the advisory is written for.
    ///
    /// Compose-time only: it registers nothing, decides nothing per
    /// request, and a composition that never calls it is byte-for-byte
    /// unchanged (GP 13).
    let withStrictAudienceBinding (app: PeerServerApp) : PeerServerApp = {
        app with
            StrictAudienceBinding = true
    }

    /// Phase 343 — restore the pre-343 degrade on the outbound
    /// capability-*profile* fetch: a non-2xx answer falls back to the
    /// bare `GET /peer/v1/capabilities` list instead of failing the
    /// handshake.
    ///
    /// **This accepts a real downgrade, and naming it is the point.** The
    /// bare list carries no per-method lifecycle, so a `Deprecated` or
    /// `Removed` method the receiver actually declared negotiates as
    /// `MethodNotAdvertised` — which the negotiation contract documents
    /// as "fall back to contract-version negotiation", i.e. call it
    /// anyway. Because the fallback keys off a status code the answering
    /// side (or anything on the path) chooses, leaving it on by default
    /// made lifecycle masking a one-response trick that logged nothing.
    ///
    /// Compose it when — and only when — this deployment federates with a
    /// peer that genuinely predates the profile route. Purely a
    /// compose-time flag read by the handshake's fetch; it registers
    /// nothing, and a composition that never calls it never issues the
    /// second request (GP 13). Full rationale in `PeerRemoteProfile`.
    let withLegacyProfileFallback (app: PeerServerApp) : PeerServerApp = {
        app with
            LegacyProfileFallback = true
    }

    /// Phase 315 — set the receiver-side wire limits the `/peer/v1/*`
    /// handlers enforce (today: the inbound contract-body ceiling).
    ///
    /// A tunable, not a switch: the ceiling is always in force, and a
    /// composition that never calls this runs under
    /// `PeerWireLimits.defaults` (8 MiB), which is far above anything
    /// the substrate is shaped to carry — so an existing deployment is
    /// unaffected (GP 11). Raise it for a federation that genuinely
    /// exchanges larger argument payloads; lower it for a tighter
    /// boundary with a peer set whose call shapes are known.
    ///
    ///     app |> PeerServerApp.withWireLimits (PeerWireLimits.defaults
    ///                                          |> PeerWireLimits.withMaxRequestBytes (32L * 1024L * 1024L))
    ///
    /// The limit is per-receiver policy, not a wire-format term: the two
    /// peers need not agree on it, and a caller that exceeds it learns
    /// the ceiling from the structured `PeerRequestTooLarge` refusal.
    let withWireLimits (limits: PeerWireLimits) (app: PeerServerApp) : PeerServerApp = { app with WireLimits = limits }

    /// Phase 331 — set the receiver-side cascade ceilings the contract
    /// route derives the trusted call context under (the hop budget it
    /// will carry forward from a wire assertion, the deepest route it
    /// accepts, and the longest correlation id / route entry).
    ///
    /// A tunable, not a switch: the derivation is always in force, and a
    /// composition that never calls this runs under
    /// `PeerCascadePolicy.defaults` — ceilings set far above the
    /// documented `HopBudget` guidance, so an existing deployment is
    /// unaffected (GP 11).
    ///
    ///     app |> PeerServerApp.withCascadePolicy (PeerCascadePolicy.defaults
    ///                                             |> PeerCascadePolicy.withMaxHopsRemaining 4)
    ///
    /// `LocalPeerId` on the supplied policy is ignored — `run` overwrites
    /// it with the composed `LocalPeer`'s id. The receiver's own identity
    /// is the one field this policy must not let anyone else name.
    let withCascadePolicy (policy: PeerCascadePolicy) (app: PeerServerApp) : PeerServerApp = {
        app with
            CascadePolicy = policy
    }

    /// Phase 309 — classify this composition's audience-binding posture.
    /// Pure and total; exposed so a deployment can assert its own posture
    /// in its own tests without booting a server, and so the advisory /
    /// strict paths cannot drift from what the provider will actually do.
    ///
    /// A `LocalPeer` whose `PeerId` is blank counts as **absent**, not
    /// present: `PeerJwt.checkAudience` short-circuits on an empty
    /// expected audience, so `withLocalPeer { PeerId = ""; … }` binds
    /// nothing while looking composed.
    let auditAudienceBinding (app: PeerServerApp) : PeerAudienceBinding =
        match app.Base.Config.PeerSubstrate with
        | NoPeerSubstrate -> AudienceBindingOff
        | EnabledPeerSubstrate ->
            match app.LocalPeer with
            | Some identity when not (System.String.IsNullOrWhiteSpace identity.PeerId) ->
                AudienceBindingEnforced identity.PeerId
            | _ ->
                if List.isEmpty app.Contracts && not app.AuditTransparency then
                    AudienceBindingIdle
                else
                    AudienceBindingMissing

    /// Phase 309 — the diagnosis shared by the advisory log line and the
    /// strict refusal, so the two can never disagree about what is wrong
    /// or which lever fixes it.
    let audienceBindingDiagnosis =
        "peer-audience-binding: this deployment hosts peer contracts but composed no LocalPeer identity, so JwtPeerAuthProvider validates inbound tokens against an empty expected audience and the 'aud' check never fires. A token another peer minted for a DIFFERENT receiver is accepted here whenever that receiver trusts the same issuer — the confused-deputy / cross-receiver replay the 'aud' claim exists to stop. Compose PeerServerApp.withLocalPeer with this deployment's own peer id to activate the check."

    /// The advisory emitted at `run` when the binding is missing.
    let audienceBindingAdvisory =
        $"{audienceBindingDiagnosis} (Advisory only — PeerServerApp.withStrictAudienceBinding turns this into a compose-time failure.)"

    /// The refusal raised at `run` when the binding is missing and
    /// `withStrictAudienceBinding` is composed.
    let audienceBindingRefusal =
        $"{audienceBindingDiagnosis} Refusing to start: PeerServerApp.withStrictAudienceBinding is composed."

    /// Phase 309 — apply the posture. Loud `Warn` by default, `failwith`
    /// under `withStrictAudienceBinding`, silence in every other state.
    /// Called by `run` before any peer registration; exposed so a
    /// deployment can run the same gate from its own preflight.
    ///
    /// The enforcement lives HERE, at the composition seam, and not in
    /// `JwtPeerAuthProvider`: the provider is a stateless, policy-free
    /// validator that is handed an expected audience and has no way to
    /// know whether an empty one is a host-only deployment's deliberate
    /// posture or a composition oversight (GP 12 rule 4). The compose
    /// root is the only place that can tell the two apart, because it is
    /// the only place that can see the hosted-contract set.
    let enforceAudienceBinding (app: PeerServerApp) : unit =
        match auditAudienceBinding app with
        | AudienceBindingOff
        | AudienceBindingEnforced _
        | AudienceBindingIdle -> ()
        | AudienceBindingMissing ->
            if app.StrictAudienceBinding then
                failwith audienceBindingRefusal
            else
                app.Base.Logger
                |> Option.iter (fun logger -> logger.Warn audienceBindingAdvisory)

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
            // Phase 309 — surface the audience-binding posture before
            // anything registers. A host-only deployment that never
            // composed `LocalPeer` gets a startup `Warn`; under
            // `withStrictAudienceBinding` it does not start at all. Kept
            // inside this branch so the `NoPeerSubstrate` short-circuit
            // above stays byte-for-byte a bare `ServerApp.run` (GP 13).
            enforceAudienceBinding app

            let contracts = app.Contracts
            let auditTransparency = app.AuditTransparency
            let contractProfiles = app.ContractProfiles
            let wireLimits = app.WireLimits
            let schedulerEnabled = app.Base.Config.JobScheduler <> NoJobScheduler

            let localIdentity =
                app.LocalPeer |> Option.defaultValue { PeerId = ""; DisplayName = "" }

            // Phase 331 — the composition owns the ceilings; the compose
            // root owns the identity. A blank `LocalPeerId` (no
            // `LocalPeer` composed) leaves only the receiver-on-route arm
            // of the loop guard dormant — the same posture, and the same
            // single cause, `enforceAudienceBinding` above has already
            // surfaced as an advisory or a refusal.
            let cascadePolicy = {
                app.CascadePolicy with
                    LocalPeerId = localIdentity.PeerId
            }

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

            // Phase 18d — the handshake's outbound *profile* fetch. Reads
            // `GET /peer/v1/capabilities/profile` (a bare `PeerProfile`).
            //
            // Phase 343 — the non-2xx branch is no longer a silent degrade
            // to the bare capability list. That fallback drops every
            // per-method lifecycle declaration and fires on a status code
            // the answering side chooses, so it was a one-response way to
            // mask a `Deprecated` / `Removed` method. It now fails closed
            // unless `withLegacyProfileFallback` is composed. The whole
            // fetch lives in `PeerRemoteProfile` so the status handling is
            // testable against a stub `HttpMessageHandler` rather than only
            // reachable through a live compose.
            let profileFallback =
                if app.LegacyProfileFallback then
                    PeerRemoteProfile.LegacyCapabilityFallback
                else
                    PeerRemoteProfile.FailClosedProfile

            let fetchRemoteProfile (auth: IPeerAuthProvider) (target: TargetPeer) =
                PeerRemoteProfile.fetch sharedHttpClient profileFallback (fetchRemote auth) auth localIdentity target

            let peerServiceConfig (services: IServiceCollection) =
                services
                    // Phase 315 — the composed wire limits, resolved
                    // per-request by the host handlers. Registered
                    // unconditionally inside the enabled branch: the
                    // host falls back to `PeerWireLimits.defaults` when
                    // nothing is registered, so this only ever makes an
                    // explicit `withWireLimits` reachable, and the
                    // `NoPeerSubstrate` short-circuit above still
                    // registers nothing at all (GP 13).
                    .AddSingleton<PeerWireLimits>(wireLimits)
                    // Phase 331 — the composed cascade ceilings plus this
                    // deployment's own peer id, resolved per-request by
                    // the contract route's context derivation. Registered
                    // on the same terms as the wire limits: the host
                    // falls back to `PeerCascadePolicy.defaults` when
                    // nothing is registered, so this only ever carries an
                    // explicit `withCascadePolicy` and the receiver's
                    // identity through.
                    .AddSingleton<PeerCascadePolicy>(cascadePolicy)
                    .AddSingleton<IPeerAuthProvider>(
                        System.Func<System.IServiceProvider, IPeerAuthProvider>(fun sp ->
                            let secrets = sp.GetService(typeof<ISecretStore>) :?> ISecretStore
                            // Phase 130 — bind inbound tokens' `aud` to this
                            // receiver's own peer id. Empty when no
                            // `LocalPeer` was composed (host-only-without-
                            // identity): audience binding stays off, matching
                            // the pre-130 behaviour (GP 11). Phase 309 —
                            // `enforceAudienceBinding` (above) has already
                            // surfaced that case as an advisory, or refused
                            // to start under `withStrictAudienceBinding`, so
                            // an empty audience reaching here is a posture
                            // the operator was told about rather than a
                            // silent default.
                            JwtPeerAuthProvider(secrets, localIdentity.PeerId) :> IPeerAuthProvider)
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
                            // Phase 331 — the receiver's own id, so the
                            // loop guard can refuse a route that already
                            // names this deployment.
                            let peer = DefaultPlatformPeer(localIdentity.PeerId) :> IPlatformPeer

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

                            // Phase 18a — register the audit-transparency
                            // contract when opted in. Reads the resolved
                            // `IAuditLog`; absent in a partial host (no
                            // `IAuditLog` registered) the contract is
                            // skipped rather than failing closed at compose.
                            if auditTransparency then
                                match sp.GetService(typeof<IAuditLog>) with
                                | null -> ()
                                | svc -> peer.RegisterContract(PeerAuditContractHost.registration (svc :?> IAuditLog))

                            peer)
                    )
                    .AddSingleton<IPeerClient>(
                        System.Func<System.IServiceProvider, IPeerClient>(fun sp ->
                            let auth = sp.GetService(typeof<IPeerAuthProvider>) :?> IPeerAuthProvider
                            HttpPeerClient(sharedHttpClient, auth, localIdentity) :> IPeerClient)
                    )
                    .AddSingleton<IPeerProfileProvider>(
                        // Phase 18d — aggregates the author-declared
                        // contract profiles over the live capability table.
                        System.Func<System.IServiceProvider, IPeerProfileProvider>(fun sp ->
                            let peer = sp.GetService(typeof<IPlatformPeer>) :?> IPlatformPeer
                            DefaultPeerProfileProvider(peer, contractProfiles) :> IPeerProfileProvider)
                    )
                    .AddSingleton<IPeerHandshake>(
                        System.Func<System.IServiceProvider, IPeerHandshake>(fun sp ->
                            let peer = sp.GetService(typeof<IPlatformPeer>) :?> IPlatformPeer
                            let auth = sp.GetService(typeof<IPeerAuthProvider>) :?> IPeerAuthProvider

                            let profileProvider =
                                sp.GetService(typeof<IPeerProfileProvider>) :?> IPeerProfileProvider

                            InMemoryPeerHandshake(
                                peer,
                                fetchRemote auth,
                                profileProvider.LocalProfile,
                                fetchRemoteProfile auth
                            )
                            :> IPeerHandshake)
                    )
                |> fun s ->
                    // Long-running fusion is registered only when the job
                    // substrate is enabled; absent, the `IPlatformPeer`
                    // factory resolves `None` and long-running methods
                    // report "not enabled" (GP 13 — zero cost when unused).
                    let withFusion =
                        if schedulerEnabled then
                            s.AddSingleton<PeerJobFusion>(
                                System.Func<System.IServiceProvider, PeerJobFusion>(fun sp ->
                                    let scheduler = sp.GetService(typeof<IJobScheduler>) :?> IJobScheduler

                                    let resultStore =
                                        sp.GetService(typeof<IPeerJobResultStore>) :?> IPeerJobResultStore

                                    {
                                        Scheduler = scheduler
                                        ResultStore = resultStore
                                    })
                            )
                        else
                            s

                    // Phase 18c — federation orchestration seams (`IPeerFanout`
                    // scatter + `IPeerCascade` next-hop bookkeeping) and Phase
                    // 18b — clean-room privacy-gate broker (`ICleanRoomBroker`).
                    // Stateless default singletons. `TryAdd` so a deployment
                    // that registers its own implementation via the base
                    // `ServerApp`'s ServiceConfig (which runs first) keeps it —
                    // the SDK default only fills the gap when nothing else is
                    // registered. Present only when the peer substrate is
                    // enabled; a singleton allocation is the whole cost (GP 13).
                    withFusion.TryAddSingleton<IPeerFanout>(DefaultPeerFanout() :> IPeerFanout)
                    withFusion.TryAddSingleton<IPeerCascade>(DefaultPeerCascade() :> IPeerCascade)
                    withFusion.TryAddSingleton<ICleanRoomBroker>(DefaultCleanRoomBroker() :> ICleanRoomBroker)
                    withFusion

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
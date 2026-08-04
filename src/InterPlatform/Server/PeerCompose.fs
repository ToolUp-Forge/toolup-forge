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
    /// Phase 311 — the clean-room templates this deployment enforces,
    /// keyed by the hosted contract id they gate. A contract named here
    /// has its registration wrapped by `CleanRoomGate.wrap` before it is
    /// registered, so the privacy floor is applied by the substrate on
    /// every answer whether or not the handler calls the broker itself.
    /// Empty unless a composition calls `withCleanRoomTemplate`, and an
    /// empty list wraps nothing (GP 13).
    CleanRoomTemplates: (string * CleanRoomTemplate) list
    /// Phase 312 — the per-call deadline the composed outbound
    /// `IPeerClient` issues every request under.
    /// `PeerTransportPolicy.defaults` (100 s — the bound the shared
    /// client already inherited from the BCL) unless a composition says
    /// otherwise. Set it with `withTransportPolicy`.
    ///
    /// Phase 339 — the same record now also carries the outbound TLS
    /// posture (`AllowInsecureTransport`, default `false`: https
    /// anywhere, http to loopback only). It lives here rather than as a
    /// field of its own because a `TargetPeer` can arrive from the
    /// registry at runtime, so the posture has to travel with the
    /// transport that dials it, not with the composition record. Opt out
    /// with `withInsecurePeerTransport`.
    TransportPolicy: PeerTransportPolicy
    /// Phase 480 — the bilateral template-approval posture: the registry
    /// holding signed propose / review / approve / revoke records, plus
    /// the clock-skew tolerance their validity windows are read under.
    ///
    /// When composed, two things happen. Every clean-room gate installed
    /// by `withCleanRoomTemplate` additionally requires a LIVE bilateral
    /// approval of the exact template version it is about to enforce —
    /// checked per dispatch, before the handler runs. And the reserved
    /// approval-handshake contract is registered, so a counterparty can
    /// submit its own signed records and read back what this deployment
    /// holds about agreements it is a party to.
    ///
    /// `None` unless a composition calls `withTemplateApprovals`, and
    /// `None` is byte-for-byte the pre-480 gate (GP 11 / GP 13) — no
    /// registry read, no contract, no allocation.
    TemplateApprovals: TemplateApprovalPolicy option
    /// Phase 591 — the pinned counterparty labels this deployment
    /// validates its federation edges against, plus the pin-age and
    /// trust-posture policy read alongside them.
    ///
    /// You cannot introspect another organisation's deployment, so the
    /// preflight validates against what each counterparty *published*: a
    /// hash-stamped `PeerSurface` export (Phase 590), pinned here with
    /// `withPinnedCounterparty`. `run` registers the structural-class
    /// `FederationPreflightValidator` over this store, so a consumed
    /// contract nothing serves — or a trust facet a counterparty never
    /// declared — is refused before traffic rather than discovered at
    /// call time.
    ///
    /// `FederationPinStore.empty` (the default) leaves every rule
    /// dormant and registers no validator, so a composition that pins
    /// nothing is byte-for-byte a pre-591 composition (GP 11 / GP 13).
    FederationPins: FederationPinStore
    /// Phase 190 — the cumulative ε accounting every clean-room gate
    /// this deployment installs runs its answers through: a ledger, the
    /// declared ceiling / charge schedule, and the clock the epoch is
    /// derived from.
    ///
    /// The gate applies a floor to ONE answer; a budget is what stops a
    /// series of individually-compliant answers exhausting the
    /// protection unobserved. Composed here rather than per template
    /// because a ledger is substrate a deployment wires once, exactly as
    /// `TemplateApprovals` is — the per-template axis is already in the
    /// scope key.
    ///
    /// `None` unless a composition calls `withPrivacyBudget`, and `None`
    /// is byte-for-byte the pre-190 gate (GP 11 / GP 13) — no ledger
    /// read, no reservation, no allocation.
    PrivacyBudget: PrivacyBudgetMeter option
    /// Phase 481 — the calibrated-noise posture per gated contract id:
    /// which released quantities carry noise, and with what ε, δ,
    /// sensitivity and lattice.
    ///
    /// This is what makes the Phase 190 ledger's ε a privacy loss rather
    /// than a query quota. Until a policy is composed the gate's only
    /// release tool is suppression, and suppression is deterministic, so
    /// summing ε over it bounds nothing formally — the point
    /// `PrivacyBudgetLedger.fs`'s header makes at length.
    ///
    /// Keyed by contract id rather than folded into `CleanRoomTemplate`
    /// so an existing template value is unchanged (GP 11), and resolved
    /// last-wins exactly as `CleanRoomTemplates` is. Empty unless a
    /// composition calls `withNoisedRelease`, and empty registers no
    /// `INoiseMechanism` and takes no branch on the release path
    /// (GP 13).
    NoisedReleases: (string * NoisedReleasePolicy) list
    /// Phase 316, composed by Phase 629 — the retention policy the
    /// composed `IPeerJobResultStore` enforces over parked long-running
    /// peer results.
    ///
    /// `PeerJobRetentionPolicy.default'` (30-day TTL, no delete-on-read)
    /// unless a composition says otherwise, which is exactly the value
    /// `BlobPeerJobResultStore(blobs)` already selected for itself — so
    /// this field carries the pre-629 behaviour byte-for-byte (GP 11)
    /// and merely makes it a lever. Set it with `withJobRetention`.
    JobRetention: PeerJobRetentionPolicy
    /// Phase 483, composed by Phase 629 — whether the multi-round
    /// protocol orchestrator (`IRoundOrchestrator` + `IRoundStateStore`
    /// + `IRoundObserver`) is registered from the already-present
    /// substrate (`IPeerFanout` / `IBlobStorage` / `IAuditLog` /
    /// `INotificationChannel`).
    ///
    /// `false` by default: an orchestrator nobody drives is three
    /// singletons and a blob-store dependency a non-orchestrating
    /// federation should not carry (GP 13). Opt in with
    /// `withRoundOrchestrator`.
    RoundOrchestration: bool
    /// Phase 338, composed by Phase 629 — the receiver-side token policy
    /// `JwtPeerAuthProvider` applies on top of signature / `exp` / `nbf`
    /// / `aud`: the replay seen-set and the contract binding.
    ///
    /// `PeerTokenPolicy.unscoped` unless a composition says otherwise —
    /// no `jti` examined, no store consulted, no `cid` checked — which
    /// is precisely what the two-argument `JwtPeerAuthProvider`
    /// constructor supplied before this phase (GP 11 / GP 13). Set it
    /// with `withTokenPolicy`, or one axis at a time with
    /// `withReplayGuard` / `withContractBoundCalls`.
    TokenPolicy: PeerTokenPolicy
    /// Phase 630 — the durable map a `PeerGateway` mints group job
    /// handles into, so the group can front a member's *long-running*
    /// methods and not only its immediate ones.
    ///
    /// `None` by default, and that absence is the whole GP 11 story: a
    /// gateway composed without one fronts exactly what Phase 595's did
    /// (the invoke leg), registers no extra singleton, and derives the
    /// identical surface. Set it with `withGroupJobMap` — typically
    /// `BlobPeerGroupJobMap(blobs)` over the same `IBlobStorage` the
    /// deployment already composes — and `PeerGateway.withAggregate`
    /// picks it up.
    ///
    /// It lives here rather than as an argument to `withAggregate`
    /// because `PeerGateway.surface` has to read it: a gateway that
    /// cannot resolve a group handle must not advertise
    /// `LongRunningEnabled`, and the composed app is the only thing that
    /// knows which of the two shapes was built.
    GroupJobMap: IPeerGroupJobMap option
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

/// Phase 480 — a composition's bilateral-approval posture, classified at
/// compose time. The same shape `PeerAudienceBinding` takes and for the
/// same reason: a deployment (or a test) asserts on data rather than
/// scraping a log line, and the audit / enforcement paths cannot drift
/// from what `run` will actually wire.
type TemplateApprovalPosture =
    /// No registry composed (or the peer substrate is off) — clean-room
    /// gates behave exactly as they did before this phase.
    | TemplateApprovalOff
    /// A registry is composed and this deployment gates contracts, so
    /// every named contract's answers additionally require a live
    /// bilateral approval of the exact template version.
    | TemplateApprovalEnforced of receiverId: string * gatedContracts: string list
    /// A registry is composed but this deployment gates nothing. **Not
    /// a defect**: recording and serving approvals is a legitimate
    /// posture on its own — a deployment approves the *counterparty's*
    /// template so the counterparty can enforce it, and holds the trail
    /// for its own auditors.
    | TemplateApprovalRecordingOnly
    /// **The composition defect.** Contracts are gated and a registry is
    /// composed, but no usable `LocalPeer` id was composed — so this
    /// deployment has no identity to be one half of a bilateral
    /// agreement, every approval check resolves against an empty peer
    /// id, and every gated call is withheld forever.
    | TemplateApprovalUnidentified

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
        CleanRoomTemplates = []
        TransportPolicy = PeerTransportPolicy.defaults
        TemplateApprovals = None
        FederationPins = FederationPinStore.empty
        PrivacyBudget = None
        NoisedReleases = []
        JobRetention = PeerJobRetentionPolicy.default'
        RoundOrchestration = false
        TokenPolicy = PeerTokenPolicy.unscoped
        GroupJobMap = None
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

    /// Phase 311 — enforce a clean-room privacy floor on every answer the
    /// named contract gives, by wrapping its registration rather than by
    /// asking its handlers to behave.
    ///
    ///     app
    ///     |> PeerServerApp.withContract (JsonRpcPeerHost.contract<IReachApi> "reach" [ v1 ] >> id)
    ///     |> PeerServerApp.withCleanRoomTemplate "reach" reachTemplate
    ///
    /// **This is the documented default for a clean-room contract, and
    /// the manual `ICleanRoomBroker.Enforce` call is not.** Phase 18b's
    /// broker was a mechanism a handler had to remember to invoke, and a
    /// handler that forgot returned row-level data with a passing build.
    /// A contract composed here has the floor applied by the substrate on
    /// the way out: the handler's answer travels down `CleanRoomGate`'s
    /// dispatch wrapper, which is the only route `IPlatformPeer` has to
    /// the wire, so there is nothing for a handler to forget. The raw
    /// `ICleanRoomBroker` API stays for bespoke callers — a
    /// caller-requested gate, a non-peer surface — and is unchanged.
    ///
    /// Every method the contract exposes is gated; the template's
    /// `AllowedMethods` decides which are answerable at all, so adding a
    /// method to a gated contract cannot quietly add an ungated one. An
    /// answer that is not a `CohortResult` is withheld rather than
    /// released unchecked, and a withhold reaches the caller as
    /// `PeerCleanRoomWithheld` carrying the template id and no
    /// quantitative detail (the full reason is recorded receiver-side as
    /// a `PeerCleanRoomDecision` audit row — see `PeerCleanRoomWithheld`
    /// for why the wire stays quiet).
    ///
    /// The gate runs over the resolved `ICleanRoomBroker`, so a
    /// deployment that registered its own mechanism keeps it. The
    /// composed floor still binds either way: the wrapper re-checks a
    /// released answer against `template.Floor` and overrides a broker
    /// that released below it.
    ///
    /// Naming a contract id this deployment does not host is a
    /// composition defect and `run` REFUSES TO START on it — an inert
    /// privacy gate that looks composed is the exact failure this phase
    /// exists to remove, so it is not something to discover from a
    /// missing audit row later. Calling this twice for one contract id
    /// applies the LAST template (composition is a list, and a
    /// last-wins overwrite matches `withContract`'s own re-registration
    /// rule).
    ///
    /// A composition that never calls this wraps nothing and is
    /// byte-for-byte a pre-311 composition (GP 11 / GP 13).
    let withCleanRoomTemplate (contractId: string) (template: CleanRoomTemplate) (app: PeerServerApp) : PeerServerApp = {
        app with
            CleanRoomTemplates = app.CleanRoomTemplates @ [ contractId, template ]
    }

    /// Phase 312 — set the per-call deadline the composed outbound
    /// `IPeerClient` issues every peer request under.
    ///
    ///     app |> PeerServerApp.withTransportPolicy (PeerTransportPolicy.defaults
    ///                                               |> PeerTransportPolicy.withCallTimeout (TimeSpan.FromSeconds 5.0))
    ///
    /// A tunable, not a switch: the deadline is always in force, and a
    /// composition that never calls this runs under
    /// `PeerTransportPolicy.defaults` — 100 s, which is exactly the
    /// bound a `Timeout`-less `HttpClient` already imposed, so an
    /// existing deployment is unaffected (GP 11).
    ///
    /// **The deadline is per-call, not `HttpClient.Timeout`, and the
    /// distinction is the point.** The composed client is shared with
    /// the capability handshake and the profile fetch, whose latency
    /// profiles are not the contract transport's; and a client-level
    /// timeout raises the same `TaskCanceledException` a caller's own
    /// cancellation does, so the transport could not report "this peer
    /// is slow" separately from "my caller went away". Lower it for a
    /// latency-sensitive federation; `PeerTransportPolicy.unbounded`
    /// removes the deadline for calls bounded by something else (a
    /// receiver-side budget, a caller-held token), which still leaves
    /// them fully cancellable.
    let withTransportPolicy (policy: PeerTransportPolicy) (app: PeerServerApp) : PeerServerApp = {
        app with
            TransportPolicy = policy
    }

    /// Phase 339 — restore the pre-339 transport posture: send peer
    /// bearer tokens to whatever scheme a peer's `BaseUrl` names,
    /// cleartext included.
    ///
    /// **This accepts a real downgrade, and naming it is the point.**
    /// Every outbound peer leg carries a freshly-minted HS256 bearer
    /// that vouches for the whole deployment, so one observation on the
    /// path is peer impersonation until the signing key rotates. From
    /// 339 the substrate refuses to send one over `http://` unless the
    /// host is loopback — which covers the dev inner loop, the in-repo
    /// suites and a two-container compose file without anyone opting
    /// out of anything.
    ///
    /// Compose this when — and only when — a peer is genuinely reachable
    /// only over cleartext AND the path between the two deployments is
    /// already trusted by other means (a private link, a service mesh
    /// terminating TLS at a sidecar). It is not a substitute for a
    /// certificate the platform will not accept: nothing here disables,
    /// relaxes, or offers a knob to relax chain validation or hostname
    /// verification, and a private trust anchor belongs in the host's
    /// certificate store where it is auditable.
    ///
    /// A composition that never calls this is unaffected; a deployment
    /// that does gets one `Warn` line per start naming the posture.
    ///
    /// **Ordering matters** — this sets a field on `TransportPolicy`, so
    /// a later `withTransportPolicy` replaces the whole record and
    /// discards it. Call `withTransportPolicy` first, or build the
    /// policy with `PeerTransportPolicy.allowInsecureTransport` and pass
    /// it whole.
    let withInsecurePeerTransport (app: PeerServerApp) : PeerServerApp = {
        app with
            TransportPolicy = PeerTransportPolicy.allowInsecureTransport app.TransportPolicy
    }

    /// Phase 480 — require every clean-room template this deployment
    /// enforces to be **bilaterally approved**: signed off by both this
    /// deployment and the calling counterparty, for the exact template
    /// content, within a live validity window.
    ///
    ///     app
    ///     |> PeerServerApp.withContract (JsonRpcPeerHost.contract<IReachApi> "reach" [ v1 ])
    ///     |> PeerServerApp.withCleanRoomTemplate "reach" reachTemplate
    ///     |> PeerServerApp.withTemplateApprovals (
    ///            TemplateApprovalPolicy.forRegistry (BlobTemplateApprovalRegistry(blobs, signer)))
    ///
    /// **What this adds to Phase 311.** The gate already applies the
    /// floor by construction; what it could not say is who agreed to the
    /// floor. A template was a deployment-configured value, asserted by
    /// one side. With a registry composed, the same structural path
    /// additionally refuses any answer whose template version lacks a
    /// live counterparty approval — so an unapproved template is not
    /// merely undocumented, it is unusable, and through the same
    /// dispatch closure the handler has no say in.
    ///
    /// **An edit invalidates prior approvals by construction.** A
    /// template's version is the SHA-256 of its canonical content
    /// (`TemplateCanonical.version`), and the dispatch check asks for
    /// approvals of the version it is about to enforce. Editing the
    /// floor, the method surface, or the id yields a different version
    /// for which no approval exists. Nothing has to remember to
    /// invalidate anything.
    ///
    /// It also registers the reserved approval-handshake contract
    /// (`TemplateApprovalContract.contractId`), so a counterparty can
    /// submit its signed records and read back the ones it is a party
    /// to. That contract is registered whether or not this deployment
    /// gates anything — approving a counterparty's template while gating
    /// none of your own is a legitimate posture, and the trail is the
    /// artefact a regulated buyer asks for.
    ///
    /// Composing a registry while gating contracts **without** a
    /// `LocalPeer` identity is a composition defect and `run` REFUSES TO
    /// START on it, for the reason Phase 311 refuses an unbound
    /// template: this deployment would have no identity to be one half
    /// of a bilateral agreement, so every gated call would be withheld
    /// forever with no composed lever to fix it.
    ///
    /// A composition that never calls this is byte-for-byte a pre-480
    /// composition (GP 11 / GP 13).
    let withTemplateApprovals (policy: TemplateApprovalPolicy) (app: PeerServerApp) : PeerServerApp = {
        app with
            TemplateApprovals = Some policy
    }

    /// Phase 190 — account a cumulative ε budget across every answer a
    /// gated contract gives, so a series of individually-compliant
    /// queries cannot exhaust the privacy protection unobserved.
    ///
    ///     app
    ///     |> PeerServerApp.withContract (JsonRpcPeerHost.contract&lt;IReachApi&gt; "reach" [ v1 ])
    ///     |> PeerServerApp.withCleanRoomTemplate "reach" reachTemplate
    ///     |> PeerServerApp.withPrivacyBudget (
    ///            PrivacyBudgetMeter.create
    ///                (BlobPrivacyBudgetLedger blobs)
    ///                (PrivacyBudgetPolicy.create 50m 1m))
    ///
    /// **What this adds to Phase 311.** The gate already refuses an
    /// answer below the floor. What no per-query check can see is that
    /// two in-floor cohorts differing in one record recover that record
    /// — cohort floors do not compose. The ledger bounds the SERIES:
    /// once the declared ceiling is reached every further answer under
    /// that template is withheld, through the same dispatch closure the
    /// handler has no say in, and the remaining budget is readable for
    /// the audit trail.
    ///
    /// **Read `PrivacyBudgetLedger.fs`'s header before telling anyone
    /// this is differential privacy.** It is an accounting control: the
    /// composed `ICleanRoomBroker` suppresses and refuses, it does not
    /// randomise, and ε summed over deterministic answers bounds nothing
    /// formally. It bounds how many questions a counterparty may ask
    /// under a declared schedule, enforced and auditable — which is the
    /// control a regulated clean-room buyer asks for, described
    /// honestly.
    ///
    /// The ledger arrives built (like `withTemplateApprovals`' registry)
    /// rather than resolved from DI, because which storage backs it is
    /// the deployment's call and `BlobPrivacyBudgetLedger` refuses a
    /// backend without conditional writes at construction — a failure an
    /// operator should see when they wire it, not at the first peer call.
    ///
    /// Applies to every contract gated by `withCleanRoomTemplate`;
    /// budgets are keyed per (template, counterparty, epoch), so one
    /// meter serves any number of templates. Calling this twice applies
    /// the LAST meter, matching `withCleanRoomTemplate`'s own last-wins
    /// rule.
    ///
    /// A composition that never calls this reads no ledger and is
    /// byte-for-byte a pre-190 composition (GP 11 / GP 13).
    let withPrivacyBudget (meter: PrivacyBudgetMeter) (app: PeerServerApp) : PeerServerApp = {
        app with
            PrivacyBudget = Some meter
    }

    /// Phase 481 — release CALIBRATED-NOISE aggregates from a gated
    /// contract instead of only suppressing what falls below the floor.
    ///
    ///     app
    ///     |> PeerServerApp.withContract (JsonRpcPeerHost.contract&lt;IReachApi&gt; "reach" [ v1 ])
    ///     |> PeerServerApp.withCleanRoomTemplate "reach" reachTemplate
    ///     |> PeerServerApp.withNoisedRelease "reach" (
    ///            NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.5m))
    ///     |> PeerServerApp.withPrivacyBudget (
    ///            PrivacyBudgetMeter.create
    ///                (BlobPrivacyBudgetLedger blobs)
    ///                (PrivacyBudgetPolicy.create 50m 1m))
    ///
    /// **This is what makes the Phase 190 ε mean something.** That
    /// phase's header is explicit that it accounts a number called ε over
    /// answers nothing randomises, so summing the charges bounds nothing
    /// formally — it bounds how many questions may be asked. Compose a
    /// policy here and the released cells carry a draw from a named,
    /// cited mechanism (discrete Laplace or discrete Gaussian, sampled
    /// exactly over a CSPRNG — see `NoiseMechanism.fs`), so the charge
    /// the ledger books is the mechanism's real privacy loss.
    ///
    /// **Sensitivity is yours and getting it wrong voids the guarantee**
    /// (GP 1). Forge ships the sampler and holds no view on what one
    /// subject's presence can move your answer by. That is why the policy
    /// carries a spec per target — a count's sensitivity is not a sum's,
    /// and one shared number would silently be wrong for one of them.
    ///
    /// Applies to the named contract's gate, last-wins, and only where a
    /// `withCleanRoomTemplate` gate exists to apply it: naming a contract
    /// with no template refuses to start, because a noise policy that
    /// nothing applies is the "composed and inert" shape Phase 311
    /// exists to make impossible. The mechanism is resolved from DI, so
    /// a deployment with an accredited implementation registers its own
    /// `INoiseMechanism`; the SDK default is the exact discrete sampler
    /// over `RandomNumberGenerator`.
    ///
    /// A composition that never calls this draws nothing, registers no
    /// mechanism, and is byte-for-byte a pre-481 composition (GP 11 /
    /// GP 13).
    let withNoisedRelease (contractId: string) (policy: NoisedReleasePolicy) (app: PeerServerApp) : PeerServerApp = {
        app with
            NoisedReleases = app.NoisedReleases @ [ contractId, policy ]
    }

    /// Phase 591 — pin a counterparty's published `PeerSurface` label, so
    /// this deployment's federation edges are validated against what the
    /// counterparty *claimed to serve* before any traffic flows.
    ///
    ///     let pin =
    ///         FederationPin.ofExportJson "seller-ssp" "peers/seller-ssp.surface.json"
    ///             agreedHash DateTimeOffset.UtcNow document
    ///
    ///     app
    ///     |> PeerServerApp.withConsumedContract (PeerSurface.consumes<IReachApi> "reach" [ v1 ] "seller")
    ///     |> PeerServerApp.withPinnedCounterparty pin
    ///
    /// **Labels, never compositions.** Nothing here inspects, requires,
    /// or reasons about a counterparty's internals — that is what makes
    /// contract-level checking safe across a heterogeneous federation.
    /// Only the pinned wire faces have to agree, and the wire face is the
    /// surface both sides already publish.
    ///
    /// The first pin arms the preflight: `run` registers the
    /// structural-class `FederationPreflightValidator`, which
    /// `SkipPreflight` cannot bypass. A composition that never calls this
    /// registers nothing, checks nothing, and is byte-for-byte a pre-591
    /// composition (GP 11 / GP 13).
    let withPinnedCounterparty (pin: PinnedPeerSurface) (app: PeerServerApp) : PeerServerApp = {
        app with
            FederationPins = FederationPinStore.withPin pin app.FederationPins
    }

    /// Phase 591 — declare how long a pinned counterparty label stays
    /// acceptable before `peer-surface-stale` reports it.
    ///
    /// Reports, never refuses: an aged pin is the absence of fresh
    /// evidence rather than evidence of drift, and taking a deployment
    /// down over a clock is not what an operator asked for by declaring a
    /// refresh cadence. Undeclared (the default) the rule is dormant —
    /// forge cannot know a federation's cadence and will not invent one.
    let withPinnedSurfaceMaxAge (maxAge: System.TimeSpan) (app: PeerServerApp) : PeerServerApp = {
        app with
            FederationPins = FederationPinStore.withMaxPinAge maxAge app.FederationPins
    }

    /// Phase 591 — require a trust facet of every pinned counterparty
    /// this deployment consumes contracts from.
    ///
    ///     app |> PeerServerApp.withRequiredPeerTrust PeerTrustRequirement.audienceBound
    ///
    /// Checked against the counterparty's published label and nothing
    /// else — a posture claim is exactly what a label IS, so there is
    /// nothing further to ask it for. A facet the label omits asserts
    /// nothing about that facet and fails the requirement; an aggregate
    /// group's `mixed:a|b` facet (Phase 595) fails it too, because a
    /// counterparty reading `mixed:` may rely on neither stance.
    ///
    /// Multiple calls accumulate. A composition that declares none leaves
    /// `peer-trust-mismatch` dormant (GP 13).
    let withRequiredPeerTrust (requirement: PeerTrustRequirement) (app: PeerServerApp) : PeerServerApp = {
        app with
            FederationPins = FederationPinStore.withRequiredTrust requirement app.FederationPins
    }

    /// Phase 316, composed by Phase 629 — bound how long the parked
    /// result of a long-running peer call stays readable.
    ///
    ///     app |> PeerServerApp.withJobRetention (
    ///                PeerJobRetentionPolicy.default'
    ///                |> PeerJobRetentionPolicy.withTtl (TimeSpan.FromDays 1.0)
    ///                |> PeerJobRetentionPolicy.withDeleteOnRead (TimeSpan.FromMinutes 5.0))
    ///
    /// **Phase 316 shipped the mechanism with no lever.** The policy was
    /// a constructor argument on `BlobPeerJobResultStore`, and the
    /// compose path called the one-argument overload — so the only way
    /// to change retention was to register a whole store of your own,
    /// which is a strange price for a `TimeSpan`. These documents hold
    /// the *typed* federated results of a peer call, so retention is a
    /// data-protection lever (GP 4) before it is a storage-growth one.
    ///
    /// A tunable, not a switch: retention is always in force, and a
    /// composition that never calls this runs under
    /// `PeerJobRetentionPolicy.default'` — the exact value the store
    /// already chose for itself, so an existing deployment is
    /// byte-for-byte unaffected (GP 11).
    ///
    /// Applies to the SDK-composed `BlobPeerJobResultStore` only. A
    /// deployment that registers its own `IPeerJobResultStore` through
    /// the base `ServiceConfig` keeps it, and honours whatever policy it
    /// was built with — `IPeerJobResultStore.Retention` is the value it
    /// reports (GP 12 rule 3).
    let withJobRetention (retention: PeerJobRetentionPolicy) (app: PeerServerApp) : PeerServerApp = {
        app with
            JobRetention = retention
    }

    /// Phase 630 — give a `PeerGateway` the durable map it mints group job
    /// handles into, so the group fronts its members' **long-running**
    /// methods as well as their immediate ones.
    ///
    ///     app
    ///     |> PeerServerApp.withGroupJobMap (BlobPeerGroupJobMap blobs)
    ///     |> PeerGateway.withAggregate client members exposure
    ///
    /// The map is passed in rather than assembled from DI because the
    /// gateway's forwarding dispatch closures are built at compose time,
    /// before any service provider exists — the same reason
    /// `withAggregate` already takes its `IPeerClient` explicitly. A
    /// deployment that composes a gateway already resolves an
    /// `IBlobStorage`, so `BlobPeerGroupJobMap(blobs)` is the one-liner.
    ///
    /// Composing this on a deployment that never calls
    /// `PeerGateway.withAggregate` does nothing at all: the field is read
    /// only there, so it registers no singleton and allocates nothing
    /// beyond the map the caller constructed (GP 13).
    let withGroupJobMap (map: IPeerGroupJobMap) (app: PeerServerApp) : PeerServerApp = {
        app with
            GroupJobMap = Some map
    }

    /// Phase 483, composed by Phase 629 — register the multi-round
    /// protocol orchestrator, so a deployment can drive a resumable
    /// cross-party round protocol by resolving `IRoundOrchestrator`
    /// rather than by hand-building one.
    ///
    ///     app
    ///     |> PeerServerApp.withLocalPeer me
    ///     |> PeerServerApp.withRoundOrchestrator
    ///
    /// Three singletons, all assembled from substrate the peer
    /// composition already resolves and all `TryAdd` so a deployment
    /// that registered its own through the base `ServiceConfig` (which
    /// runs first) keeps it:
    ///
    ///   * `IRoundStateStore` → `BlobRoundStateStore` over the resolved
    ///     `IBlobStorage`. The durable one deliberately —
    ///     `InMemoryRoundStateStore` loses its state with the process,
    ///     which defeats the resume the orchestrator exists for.
    ///   * `IRoundObserver` → `PlatformRoundObserver` over the resolved
    ///     `IAuditLog` and `INotificationChannel`, each optional
    ///     independently, so a partial host still observes whichever
    ///     half it has.
    ///   * `IRoundOrchestrator` → `DefaultRoundOrchestrator` over the
    ///     composed `IPeerFanout` plus the two above.
    ///
    /// **Off by default, and this is a GP 13 call rather than a safety
    /// one.** Nothing here is dangerous; it is simply three singletons
    /// and a `_platform` blob prefix that a federation running no round
    /// protocol has no use for. `RoundOrchestrator.create ()` remains
    /// the un-composed escape hatch for a caller that wants one without
    /// the DI registration.
    let withRoundOrchestrator (app: PeerServerApp) : PeerServerApp = { app with RoundOrchestration = true }

    /// Phase 338, composed by Phase 629 — set the receiver-side token
    /// policy: the replay seen-set and the contract binding.
    ///
    ///     app |> PeerServerApp.withTokenPolicy (
    ///                PeerTokenPolicy.unscoped
    ///                |> PeerTokenPolicy.withReplayGuard (BlobPeerReplayGuard blobs))
    ///
    /// **Phase 338 shipped both seams with no composition path**, which
    /// is the shape Phase 330's unreferenced `VerifyDelegation` took: a
    /// defence a deployment can only reach by registering its own
    /// `IPeerAuthProvider` is a defence nobody turns on. This is the
    /// lever.
    ///
    /// Applies to the SDK-composed `JwtPeerAuthProvider`. A deployment
    /// that registers its own provider through the base `ServiceConfig`
    /// keeps it, and is responsible for its own policy.
    ///
    /// **Ordering matters** — this replaces the whole record, so a later
    /// `withTokenPolicy` discards an earlier `withReplayGuard` /
    /// `withContractBoundCalls`. Call it first, or build the policy
    /// whole and pass it (the same caveat `withInsecurePeerTransport`
    /// carries against `withTransportPolicy`).
    ///
    /// A composition that never calls this runs under
    /// `PeerTokenPolicy.unscoped` — byte-for-byte the pre-338 validation
    /// path, consulting no store (GP 11 / GP 13).
    let withTokenPolicy (policy: PeerTokenPolicy) (app: PeerServerApp) : PeerServerApp = {
        app with
            TokenPolicy = policy
    }

    /// Phase 338, composed by Phase 629 — enforce single-use peer tokens
    /// against `guard`, so a captured token is spendable once rather
    /// than being a bearer capability for its whole 300 s + skew
    /// lifetime.
    ///
    ///     app |> PeerServerApp.withReplayGuard (BlobPeerReplayGuard blobs)
    ///
    /// **Off by default, and the rollout order is why.** Every minted
    /// token has carried a `jti` since Phase 338 precisely so a fleet
    /// can upgrade first and switch enforcement on second; a receiver
    /// that starts enforcing before its counterparties mint nonces
    /// refuses every one of their calls with `missing 'jti'`. Compose
    /// this only once the peers calling this deployment are known to be
    /// on a post-338 substrate — see the migration doc's rollout order.
    ///
    /// The guard is a store and it fails CLOSED:
    /// `ReplayGuardUnavailable` refuses the call rather than admitting
    /// an unchecked token, which is the whole reason it is not a cache.
    /// `BlobPeerReplayGuard` is the distributed-ready choice (it REFUSES
    /// an `IBlobStorage` without conditional writes, at construction);
    /// `InMemoryPeerReplayGuard` is correct only for a single-instance
    /// receiver, and says so through `IsDistributed = false`.
    ///
    /// Additive over whatever `TokenPolicy` the composition already
    /// carries, so it composes with `withContractBoundCalls` in either
    /// order.
    let withReplayGuard (guard: IPeerReplayGuard) (app: PeerServerApp) : PeerServerApp = {
        app with
            TokenPolicy = PeerTokenPolicy.withReplayGuard guard app.TokenPolicy
    }

    /// Phase 338, composed by Phase 629 — bind every inbound peer token
    /// to the ONE contract it is spent against, through the `cid` claim.
    ///
    ///     app |> PeerServerApp.withContractBoundCalls
    ///
    /// The host validates a contract call through
    /// `IPeerCallScopedAuth.ValidateScopedPeerToken` for the contract id
    /// the request addressed (Phase 629), so composing this is all a
    /// deployment has to do — there is no longer a validation path a
    /// deployment must implement itself for the binding to bite.
    ///
    /// **Both ends must move, and this end refuses loudly if they have
    /// not.** Under `ContractBoundCalls` a token with no `cid` is
    /// refused: the receiver was told to require a binding and cannot
    /// honour "the issuer did not say". So the rollout is: every peer
    /// that calls this deployment mints through
    /// `IssueScopedPeerToken` under a `ContractBoundCalls` policy
    /// FIRST, then this deployment composes this. Off by default for
    /// exactly that reason (GP 11).
    ///
    /// Additive over the composition's existing `TokenPolicy`, so it
    /// composes with `withReplayGuard` in either order.
    let withContractBoundCalls (app: PeerServerApp) : PeerServerApp = {
        app with
            TokenPolicy = PeerTokenPolicy.withContractBinding app.TokenPolicy
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

    /// Phase 339 — the advisory emitted at `run` when a composition has
    /// opted out of peer-transport TLS enforcement. Emitted every start,
    /// deliberately: a deployment that sends deployment-vouching bearer
    /// tokens over cleartext should say so in its own logs rather than
    /// have it be readable only from its source.
    let insecureTransportAdvisory =
        "peer-transport-tls: PeerTransportPolicy.AllowInsecureTransport is set, so this deployment will send peer bearer tokens to a cleartext http:// peer on any host, not just loopback. A peer token vouches for the whole deployment, so one observation on the path is peer impersonation until the signing key rotates. Drop PeerServerApp.withInsecurePeerTransport once every peer is reachable over https (loopback peers need no opt-out — http://localhost is accepted by default)."

    /// Phase 339 — apply the posture. Advisory only: unlike Phase 309's
    /// audience gate there is nothing to refuse here, because the
    /// insecure posture is a deliberate composition act rather than an
    /// oversight, and the enforcement that matters happens per call in
    /// `PeerTransportSecurity`. Exposed so a deployment can run the same
    /// check from its own preflight.
    let enforceTransportSecurity (app: PeerServerApp) : unit =
        match app.Base.Config.PeerSubstrate with
        | NoPeerSubstrate -> ()
        | EnabledPeerSubstrate ->
            if app.TransportPolicy.AllowInsecureTransport then
                app.Base.Logger
                |> Option.iter (fun logger -> logger.Warn insecureTransportAdvisory)

    /// Phase 311 — the effective template per contract id, last-wins.
    /// Exposed so a deployment can assert its own gating posture without
    /// booting a server, and so the compose path and any preflight read
    /// the same resolution rule.
    let cleanRoomTemplateMap (app: PeerServerApp) : Map<string, CleanRoomTemplate> =
        app.CleanRoomTemplates
        |> List.fold (fun acc (contractId, template) -> Map.add contractId template acc) Map.empty

    /// Phase 311 — the contract ids this composition would register.
    /// Materialises each `withContract` builder exactly the way
    /// `PeerSurface.describe` does; `None` fusion is sufficient because a
    /// contract's id does not depend on the job substrate.
    let private hostedContractIds (app: PeerServerApp) : Set<string> =
        let authored =
            app.Contracts
            |> List.map (fun builder -> (builder None).Registration.ContractId)

        let transparency =
            if app.AuditTransparency then
                [ PeerAudit.contractId ]
            else
                []

        // Phase 480 — the approval-handshake contract is registered
        // alongside the author contracts whenever a registry is
        // composed, so a template naming it binds rather than reading as
        // unbound (an odd thing to gate, but the seam must not have an
        // exception in it).
        let approvals =
            if app.TemplateApprovals.IsSome then
                [ TemplateApprovalContract.contractId ]
            else
                []

        Set.ofList (authored @ transparency @ approvals)

    /// Phase 311 — the contract ids named by `withCleanRoomTemplate` that
    /// no hosted contract answers to. Empty on a healthy composition.
    ///
    /// An unbound template is a gate that will never run while looking
    /// entirely composed — the shape Phase 330's unreferenced
    /// `VerifyDelegation` took, and the reason this phase exists at all.
    /// Detection is data, not a log line, so a deployment can assert it
    /// in its own preflight (the same posture `auditAudienceBinding`
    /// takes for Phase 309).
    ///
    /// Materialising the contract builders is not free, so it happens
    /// only when at least one template is composed: a deployment that
    /// gates nothing runs no probe (GP 13). A `NoPeerSubstrate`
    /// composition reports nothing — it registers no contracts at all,
    /// and the strip-imports path must stay byte-for-byte a bare
    /// `ServerApp.run`.
    let auditCleanRoomTemplates (app: PeerServerApp) : string list =
        match app.Base.Config.PeerSubstrate, app.CleanRoomTemplates with
        | NoPeerSubstrate, _
        | _, [] -> []
        | EnabledPeerSubstrate, templates ->
            let hosted = hostedContractIds app

            templates
            |> List.map fst
            |> List.distinct
            |> List.filter (fun contractId -> not (Set.contains contractId hosted))

    /// The refusal raised at `run` when a composed clean-room template
    /// binds to no hosted contract.
    let cleanRoomTemplateRefusal (unbound: string list) =
        let named = String.concat ", " unbound

        $"peer-clean-room-gate: PeerServerApp.withCleanRoomTemplate names contract(s) [{named}] that this deployment does not host, so the privacy floor declared for them would never run — a gate that looks composed and enforces nothing. Refusing to start: register the contract with PeerServerApp.withContract under the same id, or drop the template."

    /// Phase 311 — apply the posture. Unlike the Phase 309 audience gate
    /// there is no advisory mode: an unbound template cannot be an
    /// existing deployment's deliberate posture, because the helper that
    /// creates one did not exist before this phase. Every reachable case
    /// is a defect in code written after the gate shipped, so failing is
    /// both safe (GP 11 — nothing pre-311 can trip it) and correct.
    let enforceCleanRoomTemplates (app: PeerServerApp) : unit =
        match auditCleanRoomTemplates app with
        | [] -> ()
        | unbound -> failwith (cleanRoomTemplateRefusal unbound)

    /// Phase 481 — the effective noised-release policy per contract id,
    /// last-wins. Exposed on the same terms as `cleanRoomTemplateMap`:
    /// so a deployment can assert its own calibration posture without
    /// booting a server, and so the compose path and any preflight read
    /// one resolution rule.
    let noisedReleaseMap (app: PeerServerApp) : Map<string, NoisedReleasePolicy> =
        app.NoisedReleases
        |> List.fold (fun acc (contractId, policy) -> Map.add contractId policy acc) Map.empty

    /// Phase 481 — everything wrong with this composition's calibrated-
    /// noise posture, as data. Empty on a healthy composition.
    ///
    /// Two families, both refusals rather than warnings:
    ///
    ///   * A policy naming a contract with **no clean-room template**.
    ///     Nothing would apply it — the noise lives on the gate, and an
    ///     ungated contract has no gate. That is the "composed and
    ///     inert" shape Phase 311 exists to make unreachable, and it is
    ///     worse here than there: an operator reading the composition
    ///     would believe answers were being randomised when they were
    ///     being released raw.
    ///   * A spec that is not calibratable at all (`NoiseSpec.validate`
    ///     / `NoisedReleasePolicy.validate`) — an unbounded sensitivity,
    ///     a non-positive ε, a Gaussian with no δ. Every one of those
    ///     would throw on the first call, and a privacy parameter that
    ///     is wrong is wrong at compose time.
    let auditNoisedRelease (app: PeerServerApp) : string list =
        match app.Base.Config.PeerSubstrate, app.NoisedReleases with
        | NoPeerSubstrate, _
        | _, [] -> []
        | EnabledPeerSubstrate, _ ->
            let gated = app.CleanRoomTemplates |> List.map fst |> Set.ofList

            noisedReleaseMap app
            |> Map.toList
            |> List.collect (fun (contractId, policy) ->
                let ungated =
                    if Set.contains contractId gated then
                        []
                    else
                        [
                            $"contract '{contractId}' has a noised-release policy but no clean-room template, so nothing would apply the noise"
                        ]

                ungated
                @ (NoisedReleasePolicy.validate policy
                   |> List.map (fun reason -> $"contract '{contractId}': {reason}")))

    /// The refusal raised at `run` when the calibrated-noise posture
    /// cannot be honoured.
    let noisedReleaseRefusal (problems: string list) =
        let named = String.concat "; " problems

        $"peer-noised-release: PeerServerApp.withNoisedRelease was composed with a posture that cannot be honoured — {named}. Refusing to start: a noise policy that nothing applies, or one whose calibration is invalid, is a deployment that looks like it randomises its answers and does not."

    /// Phase 481 — apply the posture. No advisory mode, on Phase 311's
    /// argument: `withNoisedRelease` did not exist before this phase, so
    /// every reachable case is a defect in code written after it and
    /// nothing pre-481 can trip it (GP 11).
    let enforceNoisedRelease (app: PeerServerApp) : unit =
        match auditNoisedRelease app with
        | [] -> ()
        | problems -> failwith (noisedReleaseRefusal problems)

    /// Phase 480 — classify this composition's bilateral-approval
    /// posture. Pure and total; exposed so a deployment can assert its
    /// own posture in its own tests without booting a server, and so the
    /// refusal path cannot drift from what `run` will wire.
    ///
    /// A `LocalPeer` whose `PeerId` is blank counts as **absent**, on
    /// the same argument `auditAudienceBinding` makes: an empty peer id
    /// is not an identity, it just looks composed.
    let auditTemplateApprovals (app: PeerServerApp) : TemplateApprovalPosture =
        match app.Base.Config.PeerSubstrate, app.TemplateApprovals with
        | NoPeerSubstrate, _
        | _, None -> TemplateApprovalOff
        | EnabledPeerSubstrate, Some _ ->
            let gated = app.CleanRoomTemplates |> List.map fst |> List.distinct |> List.sort

            if List.isEmpty gated then
                TemplateApprovalRecordingOnly
            else
                match app.LocalPeer with
                | Some identity when not (System.String.IsNullOrWhiteSpace identity.PeerId) ->
                    TemplateApprovalEnforced(identity.PeerId, gated)
                | _ -> TemplateApprovalUnidentified

    /// The refusal raised at `run` when a composition gates contracts
    /// under an approval registry without a `LocalPeer` identity.
    let templateApprovalRefusal (gated: string list) =
        let named = String.concat ", " gated

        $"peer-template-approval: PeerServerApp.withTemplateApprovals is composed and contract(s) [{named}] are clean-room gated, but this deployment composed no LocalPeer identity — so it has no peer id to be one half of a bilateral agreement, no counterparty can address an approval to it, and every gated call would be withheld forever with no composed lever to fix it. Refusing to start: compose PeerServerApp.withLocalPeer with this deployment's own peer id."

    /// Phase 480 — apply the posture. No advisory mode, on Phase 311's
    /// argument: `withTemplateApprovals` did not exist before this
    /// phase, so every reachable case is a defect in code written after
    /// it, and nothing pre-480 can trip it (GP 11).
    let enforceTemplateApprovals (app: PeerServerApp) : unit =
        match auditTemplateApprovals app with
        | TemplateApprovalOff
        | TemplateApprovalEnforced _
        | TemplateApprovalRecordingOnly -> ()
        | TemplateApprovalUnidentified ->
            let gated = app.CleanRoomTemplates |> List.map fst |> List.distinct |> List.sort
            failwith (templateApprovalRefusal gated)

    /// Phase 591 — the federation facts the preflight reads, derived from
    /// the composition rather than hand-listed: this deployment's own peer
    /// id, what it declared it consumes, and the pinned counterparty
    /// labels it validates those declarations against.
    ///
    /// `now` is a parameter and not a read of the ambient clock, so the
    /// stale rule is a pure function of its inputs and a test can age a
    /// pin without waiting. `run` supplies `DateTimeOffset.UtcNow` at the
    /// moment the preflight actually runs.
    let federationPreflightInput (now: System.DateTimeOffset) (app: PeerServerApp) : FederationPreflightInput = {
        LocalPeerId = app.LocalPeer |> Option.map _.PeerId |> Option.defaultValue ""
        Consumes = app.ConsumedContracts
        Pins = app.FederationPins
        Now = now
    }

    /// Phase 591 — classify this composition's federation graph against
    /// its pinned counterparty labels. Pure and total; exposed so a
    /// deployment can assert its own edges in its own tests without
    /// booting a server (the posture `auditAudienceBinding` /
    /// `auditCleanRoomTemplates` / `auditTemplateApprovals` take), and so
    /// the preflight validator cannot drift from what a deployment can
    /// check for itself.
    ///
    /// Empty on a composition that pinned nothing, whatever it consumes
    /// (GP 13).
    let auditFederationGraph (now: System.DateTimeOffset) (app: PeerServerApp) : CompositionDefect list =
        FederationPreflight.check (federationPreflightInput now app)

    /// Phase 629 — this deployment's own peer identity, defaulted the way
    /// `run` defaults it (a blank id when no `LocalPeer` was composed).
    /// One definition, so a factory below and the compose path cannot
    /// disagree about which identity the singletons are built against.
    let private composedIdentity (app: PeerServerApp) : PeerIdentity =
        app.LocalPeer |> Option.defaultValue { PeerId = ""; DisplayName = "" }

    /// Phase 629 — the `IPeerJobResultStore` this composition would
    /// register, over `blobs`. `run`'s DI factory is a one-line call to
    /// this, so the retention knob is assertable without booting a server
    /// (the posture `auditAudienceBinding` / `auditCleanRoomTemplates`
    /// take for their own postures).
    let jobResultStore (app: PeerServerApp) (blobs: IBlobStorage) : IPeerJobResultStore =
        BlobPeerJobResultStore(blobs, app.JobRetention) :> IPeerJobResultStore

    /// Phase 629 — the `IPeerAuthProvider` this composition would
    /// register, over `secrets`. Carries the composed `TokenPolicy`
    /// (Phase 338's replay guard + contract binding) and this
    /// deployment's own id as the bound audience (Phase 130). Exposed on
    /// the same terms as `jobResultStore`.
    let peerAuthProvider (app: PeerServerApp) (secrets: ISecretStore) : IPeerAuthProvider =
        JwtPeerAuthProvider(secrets, (composedIdentity app).PeerId, app.TokenPolicy) :> IPeerAuthProvider

    /// Phase 629 — the Phase 483 round-orchestration registrations, or
    /// `services` untouched when `withRoundOrchestrator` was not composed.
    ///
    /// A module-level function rather than three lines inside `run` so a
    /// deployment (or a test) can apply it to a bare `IServiceCollection`
    /// and resolve exactly what a composition would get, without booting
    /// a server. Every registration is `TryAdd`: a deployment that
    /// registered its own `IRoundStateStore` / `IRoundObserver` /
    /// `IRoundOrchestrator` through the base `ServiceConfig` (which runs
    /// first) keeps it, and the SDK default only fills the gap.
    ///
    /// The substrate is resolved lazily per singleton, so the order this
    /// runs in relative to the rest of the peer graph does not matter —
    /// `IPeerFanout` is `TryAdd`ed by the same compose pass and is
    /// resolved at first orchestrator use, not here.
    let roundOrchestrationServices (app: PeerServerApp) (services: IServiceCollection) : IServiceCollection =
        if not app.RoundOrchestration then
            services
        else
            services.TryAddSingleton<IRoundStateStore>(
                System.Func<System.IServiceProvider, IRoundStateStore>(fun sp ->
                    let blobs = sp.GetService(typeof<IBlobStorage>) :?> IBlobStorage
                    BlobRoundStateStore(blobs) :> IRoundStateStore)
            )

            services.TryAddSingleton<IRoundObserver>(
                System.Func<System.IServiceProvider, IRoundObserver>(fun sp ->
                    // Both halves optional and independently so, exactly
                    // as `PlatformRoundObserver` is written for: a
                    // deployment with `AuditLog = NoAuditLog` still gets
                    // progress, and a partial host with neither observes
                    // nothing rather than failing to compose.
                    let auditLog =
                        sp.GetService(typeof<IAuditLog>)
                        |> Option.ofObj
                        |> Option.map (fun x -> x :?> IAuditLog)

                    let channel =
                        sp.GetService(typeof<INotificationChannel>)
                        |> Option.ofObj
                        |> Option.map (fun x -> x :?> INotificationChannel)

                    PlatformRoundObserver(auditLog, channel) :> IRoundObserver)
            )

            services.TryAddSingleton<IRoundOrchestrator>(
                System.Func<System.IServiceProvider, IRoundOrchestrator>(fun sp ->
                    let fanout = sp.GetService(typeof<IPeerFanout>) :?> IPeerFanout
                    let store = sp.GetService(typeof<IRoundStateStore>) :?> IRoundStateStore
                    let observer = sp.GetService(typeof<IRoundObserver>) :?> IRoundObserver
                    DefaultRoundOrchestrator(fanout, store, observer) :> IRoundOrchestrator)
            )

            services

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

            // Phase 311 — refuse a clean-room template that binds to no
            // hosted contract, before anything registers. Costs one
            // builder pass and only when a template is composed; a
            // composition that gates nothing does not run it (GP 13).
            enforceCleanRoomTemplates app

            // Phase 481 — refuse a calibrated-noise policy that no gate
            // would apply, or one whose calibration is not valid. Runs
            // only when a policy is composed (GP 13).
            enforceNoisedRelease app

            // Phase 480 — refuse a bilateral-approval posture that can
            // never resolve (gated contracts, a composed registry, no
            // local identity), before anything registers. Pure over the
            // compose record; a composition with no registry short-
            // circuits on the first match arm (GP 13).
            enforceTemplateApprovals app

            // Phase 339 — one line per start when the composition has
            // opted out of peer-transport TLS enforcement. Silent in
            // every other state, and inside this branch so the
            // `NoPeerSubstrate` short-circuit stays byte-for-byte a bare
            // `ServerApp.run` (GP 13).
            enforceTransportSecurity app

            let contracts = app.Contracts
            let cleanRoomTemplates = cleanRoomTemplateMap app
            let templateApprovals = app.TemplateApprovals
            let privacyBudget = app.PrivacyBudget
            let noisedReleases = noisedReleaseMap app
            let auditTransparency = app.AuditTransparency
            let contractProfiles = app.ContractProfiles
            let wireLimits = app.WireLimits
            let transportPolicy = app.TransportPolicy
            // Phase 591 — captured from the FINAL composition, so the
            // preflight reads every `withConsumedContract` /
            // `withPinnedCounterparty` declaration regardless of the order
            // a composition made them in.
            let federationPins = app.FederationPins
            let consumedContracts = app.ConsumedContracts
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
            //
            // Phase 312 — deliberately left on the BCL's default
            // `Timeout`. The contract transport's deadline rides
            // `PeerTransportPolicy` and is applied per request, so
            // lowering it does not silently re-bound the handshake and
            // profile fetches that share this client, and an expiry
            // stays distinguishable from a caller's cancellation. See
            // `PeerServerApp.withTransportPolicy`.
            let sharedHttpClient = new HttpClient()

            // The handshake's outbound capability fetch. The receiver's
            // `GET /peer/v1/capabilities` answers with a bare
            // `CapabilityList` (not a JSON-RPC envelope), so this reads it
            // directly. Mints a per-call token vouching for the local
            // identity; a transport failure is `HandshakeUnreachable`, an
            // auth / non-2xx refusal is `HandshakeRejected`.
            //
            // Phase 339 — gated on the same https-or-loopback rule as
            // the contract transport, and refused BEFORE the token is
            // minted. This leg carries the same deployment-vouching
            // bearer, and it is usually the FIRST call made to a newly
            // configured peer, so it is where a cleartext `BaseUrl` is
            // most likely to be noticed.
            let fetchRemote
                (auth: IPeerAuthProvider)
                (target: TargetPeer)
                : Async<Result<CapabilityList, PeerHandshakeError>> =
                async {
                    match PeerTransportSecurity.check transportPolicy target.BaseUrl with
                    | Error _ -> return Error(HandshakeRejected(PeerTransportSecurity.refusalMessage target.BaseUrl))
                    | Ok() ->
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
                PeerRemoteProfile.fetch
                    sharedHttpClient
                    transportPolicy
                    profileFallback
                    (fetchRemote auth)
                    auth
                    localIdentity
                    target

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
                            //
                            // Phase 629 — built through `peerAuthProvider`
                            // so the composed `TokenPolicy` (Phase 338's
                            // replay guard + contract binding) rides it.
                            // The default `PeerTokenPolicy.unscoped` is
                            // exactly what the two-argument constructor
                            // supplied before this phase, so an existing
                            // composition validates identically (GP 11).
                            peerAuthProvider app secrets)
                    )
                    .AddSingleton<IPeerJobResultStore>(
                        System.Func<System.IServiceProvider, IPeerJobResultStore>(fun sp ->
                            let blobs = sp.GetService(typeof<IBlobStorage>) :?> IBlobStorage
                            // Phase 629 — carries the composed
                            // `JobRetention`. Its default is the value
                            // `BlobPeerJobResultStore(blobs)` already
                            // chose for itself (GP 11).
                            jobResultStore app blobs)
                    )
                    .AddSingleton<IPeerRegistry>(
                        System.Func<System.IServiceProvider, IPeerRegistry>(fun sp ->
                            let blobs = sp.GetService(typeof<IBlobStorage>) :?> IBlobStorage
                            // Phase 339 — the directory refuses to
                            // RECORD a cleartext peer under the same
                            // policy the transport refuses to CALL one.
                            // Reads stay ungated, so a pre-339 directory
                            // still resolves (see `BlobPeerRegistry`).
                            BlobPeerRegistry(blobs, transportPolicy) :> IPeerRegistry)
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

                            // Phase 311 — the clean-room gate is installed
                            // HERE, at the single seam every contract
                            // registration passes through, so a gated
                            // contract cannot be registered ungated: the
                            // wrapper owns the dispatch closure
                            // `IPlatformPeer` will call, and the handler
                            // is never consulted about whether the floor
                            // applies. An id with no composed template is
                            // registered exactly as it was before this
                            // phase — same value, no branch on the call
                            // path (GP 13).
                            // Phase 480 — the bilateral-approval check
                            // the gate runs as invariant 0, built once
                            // per composition and closed over this
                            // deployment's own peer id. `None` when no
                            // registry is composed, which is the
                            // pre-480 gate exactly.
                            let approvalCheck =
                                templateApprovals
                                |> Option.map (fun policy -> TemplateApprovalGate.check policy localIdentity.PeerId)

                            let gate (registration: PeerContractRegistration) =
                                match Map.tryFind registration.ContractId cleanRoomTemplates with
                                | None -> registration
                                | Some template ->
                                    let broker = sp.GetService(typeof<ICleanRoomBroker>) :?> ICleanRoomBroker

                                    // Best-effort, like every other peer
                                    // audit path: a partial host with no
                                    // `IAuditLog` still gates, it just
                                    // records nothing.
                                    let sink =
                                        match sp.GetService(typeof<IAuditLog>) with
                                        | null -> CleanRoomGate.noAudit
                                        | svc ->
                                            let auditLog = svc :?> IAuditLog

                                            fun payload ->
                                                auditLog.Record(PeerJob.Scope, PeerCleanRoomDecision payload)

                                    // Phase 481 — the calibrated-noise
                                    // posture for THIS contract, paired
                                    // with the resolved mechanism. `None`
                                    // for a composition that declared no
                                    // policy, which is the pre-481 gate
                                    // exactly. The mechanism is resolved
                                    // per gated contract rather than once
                                    // per composition so a deployment
                                    // registering its own `INoiseMechanism`
                                    // through the base `ServiceConfig` is
                                    // honoured on the same terms the
                                    // broker is.
                                    let noise =
                                        Map.tryFind registration.ContractId noisedReleases
                                        |> Option.map (fun policy ->
                                            sp.GetService(typeof<INoiseMechanism>) :?> INoiseMechanism, policy)

                                    // Phase 190 — the composed ε meter,
                                    // or `None` for a composition that
                                    // declared no budget (the pre-190
                                    // gate exactly).
                                    (CleanRoomGate.wrapNoised
                                        broker
                                        template
                                        approvalCheck
                                        privacyBudget
                                        noise
                                        sink
                                        registration)
                                        .Registration

                            for builder in contracts do
                                let host = builder fusion
                                peer.RegisterContract(gate host.Registration)

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
                                // Gated on the same terms as an author
                                // contract: a template naming the
                                // reserved audit id is unusual, but the
                                // seam must not have an exception in it.
                                | svc ->
                                    peer.RegisterContract(
                                        gate (PeerAuditContractHost.registration (svc :?> IAuditLog))
                                    )

                            // Phase 480 — the approval handshake. Needs
                            // no substrate beyond the composed registry
                            // (which already carries its own storage +
                            // signer), so unlike the audit contract it
                            // cannot be skipped for a missing
                            // dependency. Through `gate` on the same
                            // terms: the seam must not have an
                            // exception in it.
                            match templateApprovals with
                            | None -> ()
                            | Some policy ->
                                peer.RegisterContract(
                                    gate (TemplateApprovalContract.registration localIdentity.PeerId policy.Registry)
                                )

                            peer)
                    )
                    .AddSingleton<IPeerClient>(
                        System.Func<System.IServiceProvider, IPeerClient>(fun sp ->
                            let auth = sp.GetService(typeof<IPeerAuthProvider>) :?> IPeerAuthProvider
                            // Phase 312 — the composed per-call deadline
                            // travels with the transport, not with the
                            // shared client.
                            HttpPeerClient(sharedHttpClient, auth, localIdentity, transportPolicy) :> IPeerClient)
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

                                    // Phase 310 — the execution side's audit
                                    // log. Optional on the same terms the
                                    // request path treats it: a partial host
                                    // without one records nothing rather
                                    // than failing to compose.
                                    let auditLog =
                                        sp.GetService(typeof<IAuditLog>)
                                        |> Option.ofObj
                                        |> Option.map (fun x -> x :?> IAuditLog)

                                    {
                                        Scheduler = scheduler
                                        ResultStore = resultStore
                                        AuditLog = auditLog
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

                    // Phase 481 — the calibrated-noise mechanism, and
                    // ONLY when a policy was composed. Unlike the three
                    // above it is not a default the substrate wants
                    // present-but-idle: a mechanism that exists without a
                    // policy draws nothing, and registering it anyway
                    // would put a CSPRNG-backed sampler in every peer
                    // deployment's container for no reachable caller
                    // (GP 13). `TryAdd` so a deployment that registered
                    // an accredited implementation through the base
                    // `ServiceConfig` keeps it.
                    if not (List.isEmpty app.NoisedReleases) then
                        withFusion.TryAddSingleton<INoiseMechanism>(NoiseMechanism.create ())

                    // Phase 591 — the federation-graph preflight, folded
                    // into the Phase 9m preflight set as a
                    // structural-class validator (so `SkipPreflight`
                    // cannot bypass it: every rule is a pure sweep over
                    // declared data already in memory, reaching no
                    // counterparty). Registered ONLY when the composition
                    // pinned at least one counterparty label — a
                    // deployment that declared no federation graph pays
                    // not even a singleton (GP 13), and can never be
                    // refused for edges it never declared (GP 11).
                    //
                    // The input is a thunk so the pin ages are measured
                    // when the preflight runs, not when the composition
                    // was built.
                    if not federationPins.Pins.IsEmpty then
                        withFusion.AddSingleton<ConfigValidation.IConfigValidator>(
                            FederationPreflight.FederationPreflightValidator(fun () -> {
                                LocalPeerId = localIdentity.PeerId
                                Consumes = consumedContracts
                                Pins = federationPins
                                Now = System.DateTimeOffset.UtcNow
                            })
                            :> ConfigValidation.IConfigValidator
                        )
                        |> ignore

                    // Phase 483 / 629 — the multi-round orchestrator
                    // trio, and ONLY when `withRoundOrchestrator` was
                    // composed. A federation that runs no round protocol
                    // registers nothing and reaches no blob prefix
                    // (GP 13). See `roundOrchestrationServices`.
                    roundOrchestrationServices app withFusion |> ignore

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
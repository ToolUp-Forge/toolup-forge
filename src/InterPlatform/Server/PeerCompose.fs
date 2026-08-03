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
            let auditTransparency = app.AuditTransparency
            let contractProfiles = app.ContractProfiles
            let wireLimits = app.WireLimits
            let transportPolicy = app.TransportPolicy
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

                                    (CleanRoomGate.wrapApproved broker template approvalCheck sink registration)
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
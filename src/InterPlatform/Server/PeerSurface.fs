// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Security.Cryptography
open System.Text
open Microsoft.FSharp.Reflection
open ToolUp.Platform
open ToolUp.InterPlatform.PeerCompose

// ─── Phase 590 — PeerSurface: a deployment's cross-instance face ─────
//
// Emits a deployment's **wire-facing face** as data: what it *serves*
// (peer contract ids + versions, the mounted `/peer/v1` endpoints, and
// the long-running routines fused onto the job substrate), what it
// *consumes* (peer contracts it calls on counterpart instances, with the
// expected counterpart role), its trust posture (auth profile, audience
// binding, delegation / replay / transport stance), and its cascade
// budget shape. The instance-seam rung of the introspection ladder — the
// per-module and per-composition descriptors cover what an app is made
// of; this covers what an *instance* offers and requires **across
// instances**.
//
// **Derived from the composed peer registrations by construction, never
// hand-listed.** `describe` reads the same `PeerServerApp` record
// `PeerCompose.run` composes from: the served contracts come from
// materialising the very `Contracts` builders the compose path runs
// (so a new `withContract` registration surfaces here with zero
// descriptor edits), the consumed contracts from the
// `withConsumedContract` declarations, the audit-transparency contract
// from the same `PeerAudit` constants its host registers under, and the
// posture flags from the compose inputs (`LocalPeer`, `ServerConfig`).
//
// **The export is the instance's label.** `exportJson` renders the
// surface as canonical JSON stamped with a format version and a SHA-256
// hash, so a counterparty (or an external federation-composition tool)
// can pin the surface of an instance it can never introspect live and
// detect staleness by re-hashing — the handshake endpoints answer "what
// do you serve *right now*"; the export answers "what did the instance I
// validated against look like". Determinism is by construction: every
// list is sorted before serialisation and records serialise in
// declaration order, so the same composition always yields the same
// bytes and the same hash, regardless of registration order.
//
// **Generic substrate (GP 1); zero cost when unused (GP 11 / GP 13).**
// Pure + on demand — nothing is built until a caller asks; a deployment
// on `NoPeerSubstrate` yields the empty surface without running a single
// contract builder, and a deployment that never calls `describe` is
// byte-for-byte unchanged.

/// One contract this deployment serves, derived from its
/// `withContract` registration (or the audit-transparency opt-in).
type ServedContract = {
    /// The contract id the receiver hosts at `/peer/v1/{contractId}`.
    ContractId: string
    /// The wire versions the registration advertises (highest mutual
    /// wins at handshake). Sorted ascending.
    Versions: ContractVersion list
    /// The long-running routine handler names the contract fuses onto
    /// the job substrate (`_platform.peer.{contractId}.{method}`),
    /// exactly as the compose path registers them. Empty when the
    /// contract has no long-running methods — or when the deployment
    /// composes no job substrate, mirroring what is actually
    /// dispatchable. Sorted ascending.
    Routines: string list
}

/// The serving half of the wire face: the contracts this instance hosts
/// plus the `/peer/v1` endpoint templates they are reachable through.
type PeerServes = {
    /// Every contract the composition registers, one entry per contract
    /// id (a re-registered id keeps the last registration, mirroring
    /// `IPlatformPeer.RegisterContract`'s overwrite semantics). Sorted
    /// by contract id.
    Contracts: ServedContract list
    /// The wire endpoint templates the substrate mounts — the invoke,
    /// capability-handshake, and long-running poll/aggregation routes
    /// of the v1 wire face (`JsonRpcPeerHost.routes`).
    Endpoints: string list
}

/// The trust posture the composition wires by construction: how inbound
/// peers are authenticated and what replay / delegation / transport
/// stance a counterparty may rely on.
type PeerTrustPosture = {
    /// The auth mechanism the compose path registers: fail-closed HS256
    /// bearer JWTs with per-call signing-key reads (`JwtPeerAuthProvider`).
    AuthProfile: string
    /// Whether inbound tokens' `aud` claim is bound to this deployment's
    /// own peer id — true exactly when the composition declares a
    /// `LocalPeer` identity (the Phase 130 confused-deputy guard).
    AudienceBound: bool
    /// How multi-hop `Delegated` assertions are verified: signature
    /// check against the immediate delegating peer's trust anchor
    /// (`IPeerAuthProvider.VerifyDelegation`).
    DelegationVerification: string
    /// The replay stance: token freshness window (`exp` / `nbf` with
    /// bounded clock skew) — no per-token replay cache.
    ReplayStance: string
    /// The transport-security stance: the substrate calls whatever
    /// scheme a `TargetPeer.BaseUrl` carries; TLS termination is the
    /// deployment's hosting concern, not enforced at this seam.
    TransportSecurity: string
}

/// The cascade budget shape: what bounds a federated (multi-hop) call
/// fan-out on this instance.
type PeerBudgetShape = {
    /// The cascade guard semantics: a per-call hop budget decremented at
    /// each hop plus route loop detection — enforced authoritatively on
    /// the receiver (`IPlatformPeer.Handle`) and pre-checked on the
    /// caller (`IPeerCascade`).
    CascadeGuard: string
    /// Whether long-running routines are actually dispatchable — true
    /// exactly when the composition enables a job scheduler.
    LongRunningEnabled: bool
}

/// A deployment's cross-instance face as data: what it serves, what it
/// consumes, and the trust / budget posture a counterparty may rely on
/// without seeing inside. `TrustPosture` / `Budgets` are `None` exactly
/// when the peer substrate is off — a non-federating deployment has no
/// wire face.
type PeerSurface = {
    /// Whether the peer substrate is enabled
    /// (`ServerConfig.PeerSubstrate = EnabledPeerSubstrate`).
    Enabled: bool
    /// The deployment's own declared peer id (`withLocalPeer`), or
    /// `None` for a host-only deployment that never calls peers.
    LocalPeerId: string option
    Serves: PeerServes
    /// The contracts this instance calls on counterparts, from the
    /// `withConsumedContract` declarations. Sorted by contract id.
    Consumes: ConsumedContract list
    TrustPosture: PeerTrustPosture option
    Budgets: PeerBudgetShape option
    /// Phase 594 — the data-vocabulary packs this instance pins
    /// (`ServerConfig.PinnedVocabularyPacks`), each as `(PackId, Version,
    /// Hash)`. Federated data means the same thing across a contract edge
    /// only when both instances pin a compatible pack version — the Phase 591
    /// federation preflight reads these to require it. A deployment that pins
    /// no pack surfaces `[]`; sorted by pack id then version for a
    /// registration-order-independent hash.
    PinnedVocabulary: VocabularyPackPin list
    /// Phase 642 — what a remote peer may see of this deployment's data,
    /// as the stable label of a `PeerDataVisibilityLevel`
    /// (`"AggregatesOnly"` | `"ViewOnly"` | `"Full"`).
    ///
    /// **It belongs in the stamped surface on the §8 inclusion test:** a
    /// counterparty acts differently on it. A modeller that needs bounded
    /// views cannot use a data host that grants aggregates only, and the
    /// difference is exactly the kind of thing a pin should invalidate —
    /// which is why it is here and not in the local operational policy
    /// the surface deliberately excludes.
    ///
    /// **A string rather than the DU, deliberately.** A published label
    /// is somebody else's document: it may omit this member (every label
    /// published before Phase 642 does) or name a level a later version
    /// invented, and a typed member would turn either into a parse
    /// failure that refuses the whole label. Carrying the raw declaration
    /// and reading it through `PeerSurface.dataVisibility` — which reads
    /// absent, empty and unrecognised alike as `AggregatesOnly` — makes
    /// the fail-closed default a property of the READER, where a
    /// counterparty cannot affect it.
    DataVisibility: string
    /// Phase 644 — the registry lifecycle transitions this deployment
    /// admits from a peer, as stable `ModelArtifactStatus` labels, sorted
    /// ordinally. `[]` — the default — admits none.
    ///
    /// **A second declaration beside `DataVisibility`, not a rung on it.**
    /// That one says what a peer may SEE; this says what it may DO, and a
    /// deployment will routinely want them at different settings — a
    /// consortium partner that may approve models and must never see a row
    /// is an ordinary arrangement, and one ladder could not express it.
    ///
    /// **In the stamped surface on the §8 inclusion test**, for the reason
    /// the level is: a counterparty acts on it. A modelling deployment
    /// whose whole workflow ends in a cross-peer approval cannot use a data
    /// host that admits none, and finding that out at the first approval
    /// means finding out with a fitted model and no way to promote it.
    ///
    /// Labels rather than a typed set, for the reason the level is a
    /// string: a published declaration is somebody else's document, and it
    /// may omit the member or name a status a later version invented.
    /// `PeerSurface.transitionAuthority` is the fail-closed read.
    TransitionAuthority: string list
}

/// The pinnable export envelope: the surface plus a format version and a
/// SHA-256 stamp over its canonical JSON, so a pinned counterparty copy
/// detects staleness by re-hashing.
type PeerSurfaceExport = {
    /// The export-format version (bumped if the descriptor vocabulary
    /// changes), distinct from any contract's own wire version.
    FormatVersion: int
    /// Lowercase hex SHA-256 over the surface's canonical JSON
    /// (`PeerSurface.toJson`).
    SurfaceHash: string
    Surface: PeerSurface
}

/// Derives a deployment's `PeerSurface` from its composed
/// `PeerServerApp` and renders the hash-stamped canonical-JSON export.
module PeerSurface =

    /// Current export-format version.
    [<Literal>]
    let formatVersion = 1

    /// The v1 wire endpoint templates `JsonRpcPeerHost.routes` mounts.
    /// Protocol constants of the wire face — the route set is a static
    /// Giraffe handler that cannot be enumerated, so the templates are
    /// pinned here and covered by the derivation-guard tests.
    let endpoints: string list = [
        "POST /peer/v1/{contractId}"
        "GET /peer/v1/capabilities"
        "GET /peer/v1/capabilities/profile"
        "GET /peer/v1/{contractId}/jobs/{jobId}"
    ]

    /// Any probe-fusion invocation is a defect: materialising a contract
    /// host only *captures* the fusion inside dispatch closures and job
    /// handlers — nothing calls the scheduler or the result store until a
    /// real dispatch, which never happens during `describe`.
    let private probeUnreachable (memberName: string) : 'T =
        failwith
            $"PeerSurface probe fusion: {memberName} must never be invoked — the probe only materialises contract hosts for description"

    /// A never-invoked `PeerJobFusion` that lets `describe` materialise
    /// the same `PeerContractHost` the compose path builds — including
    /// the long-running job-handler names — without a composed job
    /// substrate. Every *operation* raises (see `probeUnreachable`); the
    /// data-only `Retention` property reports `keepForever`, since the
    /// probe stores nothing there is nothing to retain.
    let private probeFusion: PeerJobFusion = {
        Scheduler =
            { new IJobScheduler with
                member _.RegisterHandler(_, _) = probeUnreachable "RegisterHandler"
                member _.RegisterHandlerAsync(_, _) = probeUnreachable "RegisterHandlerAsync"
                member _.Schedule _ = probeUnreachable "Schedule"
                member _.Cancel(_, _) = probeUnreachable "Cancel"
                member _.Disable(_, _) = probeUnreachable "Disable"
                member _.Enable(_, _) = probeUnreachable "Enable"
                member _.Get(_, _) = probeUnreachable "Get"
                member _.ListJobs _ = probeUnreachable "ListJobs"
                member _.GetRecentRuns(_, _, _) = probeUnreachable "GetRecentRuns"
                member _.TriggerOnce(_, _, _) = probeUnreachable "TriggerOnce"
                member _.NotifyEventWritten(_, _, _) = probeUnreachable "NotifyEventWritten"
            }
        ResultStore =
            { new IPeerJobResultStore with
                member _.Retention = PeerJobRetentionPolicy.keepForever
                member _.SaveResult(_, _, _, _) = probeUnreachable "SaveResult"
                member _.TryGetResult(_, _) = probeUnreachable "TryGetResult"
            }
        // Describing a surface materialises handlers; it never runs one,
        // so there is no terminal outcome to record.
        AuditLog = None
    }

    /// Materialise one `withContract` registration into its served
    /// shape, running the same builder the compose path runs. `fusion`
    /// mirrors the compose decision: `Some probeFusion` when the
    /// deployment enables a job scheduler (long-running methods
    /// contribute their handler names), `None` when it does not
    /// (long-running methods are not dispatchable, so no routine is
    /// advertised) — the surface reports what the instance actually
    /// offers.
    let private servedContract
        (fusion: PeerJobFusion option)
        (builder: PeerJobFusion option -> PeerContractHost)
        : ServedContract =
        let host = builder fusion

        {
            ContractId = host.Registration.ContractId
            Versions = host.Registration.Versions |> List.sort
            Routines = host.JobHandlers |> List.map fst |> List.sort
        }

    /// The empty surface — what a non-federating deployment
    /// (`NoPeerSubstrate`) presents: nothing served, nothing consumed,
    /// no trust posture, no budget shape.
    let empty: PeerSurface = {
        Enabled = false
        LocalPeerId = None
        Serves = { Contracts = []; Endpoints = [] }
        Consumes = []
        TrustPosture = None
        Budgets = None
        PinnedVocabulary = []
        // Phase 642 — a deployment with no wire face grants nothing, and
        // the narrowest level is how "nothing" is spelled. Stated rather
        // than left empty: an empty string would read as the default
        // anyway, and saying so is what makes the empty label's own
        // answer to "what may I see" legible instead of inferred.
        DataVisibility = PeerDataVisibilityLevel.label PeerDataVisibilityLevel.default'
        // Phase 644 — a deployment with no wire face admits no
        // transition. The empty list is the grant of nothing, and it is
        // what an undeclared deployment publishes too: silence is not a
        // grant, so there is nothing to distinguish here.
        TransitionAuthority = []
    }

    /// **The fail-closed read of a surface's declared authority level**,
    /// and the only route any consumer should take to it.
    ///
    /// A label that omits the member (every label published before Phase
    /// 642), carries an empty value, or names a level this build cannot
    /// enforce all read as `AggregatesOnly`. Each arm is the same claim
    /// from a different direction: a counterparty's silence is not a
    /// grant, and a word this deployment cannot act on is not one either.
    let dataVisibility (surface: PeerSurface) : PeerDataVisibilityLevel =
        PeerDataVisibilityLevel.ofLabelOrDefault surface.DataVisibility

    /// Phase 644 — **the fail-closed read of a surface's declared
    /// transition grant**, and the only route any consumer should take
    /// to it.
    ///
    /// A label that omits the member (every label published before this
    /// phase), carries `null`, or names a status this build does not know
    /// yields the empty grant or a grant without that entry. Duplicates
    /// collapse and the result is ordinally sorted, so two readers of one
    /// label agree on the bytes as well as on the set.
    let transitionAuthority (surface: PeerSurface) : string list =
        PeerTransition.readDeclaration surface.TransitionAuthority

    /// Typed consumed-contract declaration for
    /// `PeerServerApp.withConsumedContract`: ties the declaration to a
    /// real record-of-functions contract type (the same shape
    /// `JsonRpcPeerClient.create<'TApi>` proxies), so a declaration
    /// cannot name a contract that has no type behind it.
    let consumes<'TApi>
        (contractId: string)
        (versions: ContractVersion list)
        (counterpartRole: string)
        : ConsumedContract =
        let apiType = typeof<'TApi>

        if not (FSharpType.IsRecord apiType) then
            failwithf "PeerSurface.consumes requires a record contract type; %s is not a record" apiType.Name

        {
            ContractId = contractId
            Versions = versions
            CounterpartRole = counterpartRole
        }

    /// Assemble the deployment's cross-instance face from its composed
    /// `PeerServerApp`. Pure + on demand (GP 13): a `NoPeerSubstrate`
    /// composition short-circuits to `empty` without running a single
    /// contract builder; an enabled composition materialises exactly the
    /// registrations `PeerCompose.run` would compose, so a new
    /// registration surfaces with zero descriptor edits.
    let describe (app: PeerServerApp) : PeerSurface =
        match app.Base.Config.PeerSubstrate with
        | NoPeerSubstrate -> empty
        | EnabledPeerSubstrate ->
            // Mirror PeerCompose.run's scheduler gate: routines are
            // advertised only when they are actually dispatchable.
            let fusion =
                if app.Base.Config.JobScheduler <> NoJobScheduler then
                    Some probeFusion
                else
                    None

            let authored = app.Contracts |> List.map (servedContract fusion)

            // Phase 18a — the audit-transparency opt-in registers the
            // SDK-shipped contract under the same reserved id + version
            // constants read here (`PeerAuditContractHost.registration`).
            let auditContract =
                if app.AuditTransparency then
                    [
                        {
                            ContractId = PeerAudit.contractId
                            Versions = [ PeerAudit.v1 ]
                            Routines = []
                        }
                    ]
                else
                    []

            // Phase 480 — the bilateral-approval opt-in registers the
            // reserved handshake contract under the same id + version
            // constants read here
            // (`TemplateApprovalContract.registration`). Projected on
            // exactly the Phase 18a argument: it is a contract a
            // counterparty CALLS, so it belongs in the face. The
            // approval *state* is live registry data, not a compose-time
            // registration, and is read through the contract itself.
            let approvalContract =
                if app.TemplateApprovals.IsSome then
                    [
                        {
                            ContractId = TemplateApprovalContract.contractId
                            Versions = [ TemplateApprovalContract.v1 ]
                            Routines = []
                        }
                    ]
                else
                    []

            // A re-registered contract id overwrites the previous
            // binding at dispatch time (`IPlatformPeer.RegisterContract`
            // is idempotent-overwrite), so the last registration wins
            // here too.
            let served =
                authored @ auditContract @ approvalContract
                |> List.rev
                |> List.distinctBy _.ContractId
                |> List.sortBy _.ContractId

            {
                Enabled = true
                LocalPeerId = app.LocalPeer |> Option.map _.PeerId
                Serves = {
                    Contracts = served
                    Endpoints = endpoints
                }
                Consumes = app.ConsumedContracts |> List.distinct |> List.sortBy _.ContractId
                TrustPosture =
                    Some {
                        AuthProfile = "jwt-hs256-bearer"
                        AudienceBound = app.LocalPeer.IsSome
                        DelegationVerification = "per-peer-trust-anchor"
                        ReplayStance = "freshness-window"
                        TransportSecurity = "deployment-managed"
                    }
                Budgets =
                    Some {
                        CascadeGuard = "hop-budget-decrement-with-route-loop-detection"
                        LongRunningEnabled = app.Base.Config.JobScheduler <> NoJobScheduler
                    }
                // Phase 594 — the pinned data-vocabulary packs, hashed +
                // sorted so the export is order-independent. A counterparty
                // pins these to require compatible data semantics across the
                // contract edge; empty when the deployment pins no pack.
                PinnedVocabulary =
                    app.Base.Config.PinnedVocabularyPacks
                    |> List.map DataVocabulary.pin
                    |> List.sortBy (fun p -> p.PackId, p.Version.Major, p.Version.Minor)
                // Phase 642 — the composed declaration
                // (`withDataVisibility`), which defaults to
                // `AggregatesOnly`. Derived from the composition like
                // every other member of this descriptor: a deployment
                // states its grant once, at compose time, and the
                // published label cannot disagree with what the seam
                // enforces because both read this one value.
                DataVisibility = PeerDataVisibilityLevel.label app.DataVisibility
                // Phase 644 — the composed grant
                // (`withPeerTransitionAuthority`), which defaults to
                // nothing. Derived from the composition like every other
                // member, so the labels a counterparty pins and the
                // targets the seam admits cannot disagree — both read
                // this one value.
                TransitionAuthority = ModelTransitionAuthority.labels app.TransitionAuthority
            }

    /// Canonical JSON of a surface: the universal `FableConverters` set,
    /// deterministic because every list is sorted at build time and
    /// record fields serialise in declaration order.
    let toJson (surface: PeerSurface) : string = JsonRpc.serialize surface

    /// Lowercase hex SHA-256 over a canonical-JSON document.
    let private sha256Hex (canonicalJson: string) : string =
        SHA256.HashData(Encoding.UTF8.GetBytes canonicalJson)
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    /// Stamp a surface into its pinnable export envelope: format
    /// version + SHA-256 over the surface's canonical JSON.
    let export (surface: PeerSurface) : PeerSurfaceExport = {
        FormatVersion = formatVersion
        SurfaceHash = sha256Hex (toJson surface)
        Surface = surface
    }

    /// The hash-stamped canonical-JSON export — what a counterparty (or
    /// an external federation-composition tool) pins and later
    /// re-validates against a freshly-described surface.
    let exportJson (surface: PeerSurface) : string = JsonRpc.serialize (export surface)
// Ambient context for `src/InterPlatform/README.md`.
//
// The page teaches a two-deployment federation, so almost every block is
// an excerpt from one side of a composition the page never shows whole.
// Three families of name are read without being built:
//
//   * the deployment's own substrate — `config`, `authProvider`,
//     `secrets`, `blobs`, `httpPeerClient`, the composed `app`, and the
//     `PeerIdentity` values the two ends agree on out of band;
//   * the worked example's own contract — `DirectoryContract` and the
//     `ReportRequest` / `Report` domain records it carries, plus the
//     implementation, the host builder and the long-running job handle
//     the receiver supplies. These belong to the DEPLOYMENT, not to the
//     substrate: `JsonRpcPeerHost.contract<'TApi>` reflects over
//     whatever record-of-functions it is handed;
//   * the counterparty-facing values a federation-preflight block pins —
//     the agreed surface hash and the published export document.
//
// Declaring them here keeps the blocks copy-clean and — unlike a
// `skip=fragment` marker — keeps every `PeerServerApp.*` /
// `JsonRpcPeerClient.*` / `JsonRpcPeerHost.*` / `CleanRoomTemplate` /
// `FederationPin.*` name in them under the gate. That matters more here
// than on most pages: this substrate's composition surface is the thing
// a federation partner integrates against.
//
// The page's own first block declares `open ToolUp.InterPlatform` and
// `open ToolUp.InterPlatform.PeerCompose`, which carry forward to every
// later block; they are repeated here because the ambient file is
// inlined AHEAD of the carried opens and its own declarations need them.
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose

[<AutoOpen>]
module PageAmbient =

    // ── The deployment's own substrate ────────────────────────────

    /// The composed `ServerConfig` — `PeerSubstrate = EnabledPeerSubstrate`
    /// in every block below the "How to enable" section.
    let config: ServerConfig = failwith "ambient"

    /// The deployment's identity provider, blob store and secret store,
    /// built by the composition root before the peer pipeline runs.
    let authProvider: IAuthProvider = failwith "ambient"

    let blobs: IBlobStorage = failwith "ambient"

    let secrets: ISecretStore = failwith "ambient"

    /// The out-of-band-agreed HS256 signing key seeded for a trusted peer.
    let sharedKey: string = failwith "ambient"

    /// The composed peer pipeline, at the point a later block adds one
    /// more `with*` to it.
    let app: PeerServerApp = failwith "ambient"

    // ── The two ends of the federation ────────────────────────────

    let sellerId: PeerIdentity = failwith "ambient"

    let buyerId: PeerIdentity = failwith "ambient"

    let thisPeerId: PeerIdentity = failwith "ambient"

    let nextPeerId: PeerIdentity = failwith "ambient"

    let localIdentity: PeerIdentity = failwith "ambient"

    /// The negotiated contract versions. The capability-negotiation block
    /// declares its own `v1, v2, v3` and shadows these.
    let v1: ContractVersion = { Major = 1; Minor = 0 }

    let v2: ContractVersion = { Major = 2; Minor = 0 }

    /// The composed outbound transport and version handshake, resolved
    /// from DI by `PeerServerApp.run`.
    let httpPeerClient: IPeerClient = failwith "ambient"

    let handshake: IPeerHandshake = failwith "ambient"

    /// The target a caller-side block negotiates or calls against.
    let target: TargetPeer = failwith "ambient"

    /// The `PeerCallContext` this deployment is currently serving, in the
    /// handler the `forward` block is excerpted from.
    let inbound: PeerCallContext = failwith "ambient"

    /// The deployment's own diagnostic sink, in the negotiation block.
    let log (message: string) : unit = failwith "ambient"

    // ── The worked example's own contracts ────────────────────────

    /// The domain records `DirectoryContract`'s long-running method
    /// carries. Deployment-owned — the substrate serialises whatever the
    /// contract declares.
    type ReportRequest = { Since: System.DateTimeOffset }

    type Report = { Rows: int }

    /// The example contract, declared once and shared by both peers. The
    /// "How to author a contract" block declares it again and shadows
    /// this, exactly as a reader reading the page top to bottom would.
    type DirectoryContract = {
        GetCapabilities: unit -> Async<string list>
        BuildReport: ReportRequest -> Async<PeerJobHandle<Report>>
    }

    /// Contracts a federation-preflight / surface-descriptor block names
    /// as consumed. `PeerSurface.consumes<'TApi>` demands a record type,
    /// so these are records rather than abbreviations.
    type UpstreamRegistryContract = {
        ListPeers: unit -> Async<string list>
    }

    type IReachApi = { EstimateReach: string -> Async<int> }

    /// The receiver's implementation and the job handle its long-running
    /// method answers with.
    let directoryImpl: DirectoryContract = failwith "ambient"

    let reportJobHandle (req: ReportRequest) : PeerJobHandle<Report> = failwith "ambient"

    /// The deployment's composed job fusion — `Some` only when a job
    /// scheduler is composed, which is why `withContract` threads it into
    /// the host builder rather than the builder capturing one.
    let fusion: PeerJobFusion option = failwith "ambient"

    /// The two host builders the compose blocks register. Both have the
    /// shape `PeerServerApp.withContract` takes.
    let directoryHost: PeerJobFusion option -> PeerContractHost = failwith "ambient"

    let reachHost: PeerJobFusion option -> PeerContractHost = failwith "ambient"

    /// The clean-room template declared in the "Clean-room gate" block and
    /// re-composed in the privacy-budget block below it.
    let reachTemplate: CleanRoomTemplate = failwith "ambient"

    // ── Counterparty evidence ─────────────────────────────────────

    /// The surface hash agreed with the counterparty out of band, and the
    /// export document it published — the two halves a federation pin is
    /// verified against.
    let agreedHash: string = failwith "ambient"

    let document: string = failwith "ambient"
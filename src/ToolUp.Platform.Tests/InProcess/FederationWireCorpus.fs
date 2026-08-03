module ToolUp.Platform.Tests.InProcess.FederationWireCorpus

// ─── Phase 596 — federation-seam conformance corpus (the emitter) ─────
//
// The executable half of `docs/interplatform/FEDERATION_WIRE.md`. This
// module holds the reference *values* of every specified shape family,
// renders them through the **live** emitters (`PeerSurface.exportJson`,
// `AggregatePeerSurface.derive`, `HostEnvelope.toJson`,
// `JsonRpc.serialize`, `TemplateCanonical.recordId`), and writes the
// result to `docs/interplatform/wire-fixtures/` as the corpus an
// implementation in any language certifies against.
//
// **The corpus is emitted, never hand-authored.** A fixture is whatever
// the emitters produce for a reference value; nobody edits one by hand.
// `FederationWireConformanceTests` re-emits in memory on every run and
// compares byte-for-byte against the committed files, so an emitter
// shape change that is not accompanied by a regenerated corpus fails the
// gate in the same commit — the forward-coupling rule the specification
// states, enforced rather than asked for. Regenerate with:
//
//   $env:TOOLUP_EMIT_WIRE_FIXTURES = "1"
//   dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
//   $env:TOOLUP_EMIT_WIRE_FIXTURES = $null
//
// **Two judgements, recorded here because they shape what the corpus can
// pin:**
//
//   * The peer-surface and aggregate families are emitted from a real
//     `PeerSurface.describe` / `AggregatePeerSurface.derive` over a
//     reference composition, so the corpus pins the derivation as well
//     as the encoding. Both are deterministic functions of the
//     composition and carry no build-varying field.
//   * The host-envelope family is emitted from a reference `HostEnvelope`
//     **value**, not from `HostEnvelope.describe` of a live composition.
//     A described envelope carries the platform assembly version and the
//     whole composable-slot / config-knob universe, so its bytes move
//     whenever anything unrelated to this seam is added to `ServerApp` or
//     `ServerConfig`. A wire corpus is a statement about the document's
//     shape, not about one deployment's inventory, and a fixture that
//     goes red for an unrelated slot is a gate nobody will keep. The
//     record's own shape is still pinned: a field added to `HostEnvelope`
//     changes this value's encoding (and usually fails to compile first).
//
// Test-tier only — zero shipped code, and a consumer deployment is
// byte-for-byte unchanged (GP 13).

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open ToolUp.Platform
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose

// ─── Corpus location ─────────────────────────────────────────────────

/// Repo root derived from the running test assembly:
/// bin/<Config>/net10.0/ToolUp.Platform.Tests.dll → up 5 = repo root.
let repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

/// `docs/interplatform/wire-fixtures/` — the committed corpus.
let corpusDir () =
    Path.Combine(repoRoot (), "docs", "interplatform", "wire-fixtures")

/// The manifest is the corpus's own enumeration — the count authority.
let manifestPath () =
    Path.Combine(corpusDir (), "manifest.json")

/// Regeneration path: `TOOLUP_EMIT_WIRE_FIXTURES=1` rewrites the corpus
/// instead of comparing against it.
let emitModeOn () =
    match Environment.GetEnvironmentVariable "TOOLUP_EMIT_WIRE_FIXTURES" with
    | null
    | "" -> false
    | v -> v = "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)

// ─── Vector model ────────────────────────────────────────────────────

/// The conformance profile a vector belongs to, via its family. A
/// profile is cumulative: `gateway` runs everything `participant` runs.
type WireProfile =
    | Participant
    | Gateway
    | ModuleHost

[<RequireQualifiedAccess>]
module WireProfile =

    let name =
        function
        | Participant -> "participant"
        | Gateway -> "gateway"
        | ModuleHost -> "module-host"

    /// The families a profile's implementer must certify against — the
    /// per-profile partition of the corpus. Cumulative: a gateway is a
    /// participant that also fronts a group, and a module host is a
    /// gateway that also runs somebody else's module.
    let rec families (profile: WireProfile) : string list =
        match profile with
        | Participant -> [ "peer-surface"; "pinned-exchange"; "attestation"; "contract-invocation" ]
        | Gateway -> families Participant @ [ "aggregate-surface" ]
        | ModuleHost -> families Gateway @ [ "host-envelope" ]

/// What certifying against a vector means.
type WireVectorKind =
    /// The document parses into the specified shape and re-serialises to
    /// the identical bytes.
    | RoundTrip
    /// Round-trip, plus: the document's own hash stamp is reproduced by
    /// recomputing it from the document's content.
    | Hash
    /// The document MUST be refused, with the named refusal class.
    | Reject

[<RequireQualifiedAccess>]
module WireVectorKind =

    let name =
        function
        | RoundTrip -> "round-trip"
        | Hash -> "hash"
        | Reject -> "reject"

/// One corpus entry. `Document` is the exact file content — for a
/// round-trip or hash vector the canonical JSON an emitter produces; for
/// a reject vector the document an implementation must refuse.
type WireVector = {
    Id: string
    Family: string
    Profile: WireProfile
    Kind: WireVectorKind
    Description: string
    /// Relative to the corpus root, forward-slashed.
    File: string
    Document: string
    /// The stable refusal class a `Reject` vector must produce.
    Reject: string option
    /// The out-of-band hash a pinned-exchange vector is verified against.
    /// `None` means "the document's own stamp" — the honest default when
    /// the exchange channel itself was the trusted one.
    AgreedHash: string option
    /// The expected digest for a `Hash` vector whose stamp is NOT carried
    /// inside the document — a signed record's signing-input digest.
    Digest: string option
}

/// The corpus's own format version — bumped when the manifest's shape
/// changes, and deliberately distinct from any document's format version.
[<Literal>]
let corpusFormatVersion = 1

/// Lowercase-hex SHA-256, the digest presentation every stamp in this
/// seam already uses.
let sha256Hex (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

let private digestOf (document: string) =
    sha256Hex (Encoding.UTF8.GetBytes document)

// ─── Reference values — peer surface (participant profile) ───────────

/// A served contract with one immediate and one long-running method.
/// NOT `private`: the host reflects via `FSharpType.IsRecord`, which
/// does not see a private representation.
type ReferenceOrderContract = {
    PlaceOrder: string -> Async<string>
    ReconcileLedger: string -> Async<PeerJobHandle<int>>
}

/// A served contract with immediate methods only.
type ReferenceCatalogueContract = {
    ListItems: unit -> Async<string list>
}

/// A contract the reference deployment calls on a counterpart.
type ReferenceDirectoryContract = {
    Lookup: string -> Async<string option>
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }
let private v11: ContractVersion = { Major = 1; Minor = 1 }

let private orderImpl: ReferenceOrderContract = {
    PlaceOrder = fun order -> async { return $"placed:{order}" }
    ReconcileLedger =
        fun _ -> async {
            return {
                JobId = Guid.Empty
                Poll = fun () -> async { return Completed 0 }
            }
        }
}

let private catalogueImpl: ReferenceCatalogueContract = {
    ListItems = fun () -> async { return [ "widget" ] }
}

/// The reference federated deployment: two served contracts (one with a
/// long-running routine), a declared local identity, and one consumed
/// contract. Deliberately generic vocabulary — an implementer reading the
/// corpus should see a protocol example, not this platform's furniture.
let private referenceInstance () =
    PeerServerApp.create ()
    |> PeerServerApp.withConfig {
        ServerConfig.defaults with
            PeerSubstrate = EnabledPeerSubstrate
            JobScheduler = InProcessJobScheduler
    }
    |> PeerServerApp.withLocalPeer {
        PeerId = "seller-ssp"
        DisplayName = "Seller supply-side"
    }
    |> PeerServerApp.withContract (fun fusion ->
        JsonRpcPeerHost.contract<ReferenceOrderContract> "example.orders" [ v1; v11 ] fusion orderImpl)
    |> PeerServerApp.withContract (fun fusion ->
        JsonRpcPeerHost.contract<ReferenceCatalogueContract> "example.catalogue" [ v1 ] fusion catalogueImpl)
    |> PeerServerApp.withConsumedContract (
        PeerSurface.consumes<ReferenceDirectoryContract> "example.directory" [ v1 ] "hub"
    )

let private instanceSurface () =
    PeerSurface.describe (referenceInstance ())

// ─── Reference values — aggregate surface (gateway profile) ──────────

/// A member surface authored as a value rather than described from a
/// composition — the corpus's own demonstration that a hand-authored
/// surface over a non-platform service is a first-class participant
/// (the label-assertion posture; see the specification).
let private memberSurface
    (peerId: string)
    (contracts: ServedContract list)
    (transportSecurity: string)
    (pins: VocabularyPackPin list)
    : PeerSurface =
    {
        Enabled = true
        LocalPeerId = Some peerId
        Serves = {
            Contracts = contracts
            Endpoints = PeerSurface.endpoints
        }
        Consumes = []
        TrustPosture =
            Some {
                AuthProfile = "jwt-hs256-bearer"
                AudienceBound = true
                DelegationVerification = "per-peer-trust-anchor"
                ReplayStance = "freshness-window"
                TransportSecurity = transportSecurity
            }
        Budgets =
            Some {
                CascadeGuard = "hop-budget-decrement-with-route-loop-detection"
                LongRunningEnabled = true
            }
        PinnedVocabulary = pins
    }

let private sharedPin: VocabularyPackPin = {
    PackId = "example.retail"
    Version = { Major = 2; Minor = 0 }
    Hash = "3f786850e387550fdab836ed7e6dc881de23001b"
}

let private divergentPin: VocabularyPackPin = {
    PackId = "example.logistics"
    Version = { Major = 1; Minor = 0 }
    Hash = "89e6c98d92887913cadf06b2adb97f26cde4849b"
}

let private groupMembers () : AggregateMember list = [
    {
        Target = {
            Peer = {
                PeerId = "member-north"
                DisplayName = "Northern site"
            }
            BaseUrl = "https://north.example"
        }
        Surface =
            memberSurface
                "member-north"
                [
                    {
                        ContractId = "example.orders"
                        Versions = [ v1; v11 ]
                        Routines = [ "_platform.peer.example.orders.ReconcileLedger" ]
                    }
                ]
                "deployment-managed"
                [ sharedPin; divergentPin ]
    }
    {
        Target = {
            Peer = {
                PeerId = "member-south"
                DisplayName = "Southern site"
            }
            BaseUrl = "https://south.example"
        }
        Surface =
            memberSurface
                "member-south"
                [
                    {
                        ContractId = "example.catalogue"
                        Versions = [ v1 ]
                        Routines = []
                    }
                ]
                // Divergent on exactly one facet — the aggregate must
                // report it as `mixed:`, not pick a winner.
                "tls-required"
                [ sharedPin ]
    }
    {
        Target = {
            Peer = {
                PeerId = "member-internal"
                DisplayName = "Internal-only site"
            }
            BaseUrl = "https://internal.example"
        }
        Surface =
            memberSurface
                "member-internal"
                [
                    {
                        ContractId = "example.settlement"
                        Versions = [ v1 ]
                        Routines = []
                    }
                ]
                "deployment-managed"
                []
    }
]

/// Two of the three members' contracts are exposed; the third stays
/// internal and contributes no posture, no pin and no consumption.
let private groupExposure: AggregateExposure = {
    Group = {
        PeerId = "consortium-gateway"
        DisplayName = "Consortium gateway"
    }
    Contracts = [
        {
            ContractId = "example.orders"
            Owner = None
        }
        {
            ContractId = "example.catalogue"
            Owner = Some "member-south"
        }
    ]
}

/// A group whose single exposing member agrees with the gateway edge on
/// every facet — the degenerate case an implementer needs in order to
/// tell "the floor collapsed to unanimity" from "the floor was never
/// applied", and the case where a pack pinned by one member does carry.
let private soloExposure: AggregateExposure = {
    Group = {
        PeerId = "solo-gateway"
        DisplayName = "Solo gateway"
    }
    Contracts = [
        {
            ContractId = "example.orders"
            Owner = Some "member-north"
        }
    ]
}

let private derived (exposure: AggregateExposure) : PeerSurface =
    match AggregatePeerSurface.derive (groupMembers (), exposure) with
    | Ok surface -> surface
    | Error errors -> failwithf "the reference aggregate exposure must resolve; it did not: %A" errors

// ─── Reference values — pinned exchange (participant profile) ────────

let private pinnedAt = DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)

let private referencePin () =
    FederationPin.ofSurface "seller-ssp" "peers/seller-ssp.surface.json" pinnedAt (instanceSurface ())

/// A published export whose stamp does not match a recomputation of its
/// own surface — corrupt or edited after stamping, and refused rather
/// than carried as a stale pin.
let private tamperedStampDocument () =
    let export = PeerSurface.export (instanceSurface ())

    JsonRpc.serialize {
        export with
            SurfaceHash = String.replicate 64 "0"
    }

/// A published export at a format version this vocabulary cannot read.
/// A half-read label would pass a trust requirement by omission.
let private futureFormatDocument () =
    let export = PeerSurface.export (instanceSurface ())

    JsonRpc.serialize {
        export with
            FormatVersion = export.FormatVersion + 1
    }

// ─── Reference values — attestation (participant profile) ────────────

let private issuedAt = DateTimeOffset(2026, 7, 16, 9, 30, 0, TimeSpan.Zero)

let private approvalRecord: TemplateApprovalRecord = {
    TemplateId = "example.cohort-report"
    TemplateVersion = "sha256:4a44dc15364204a80fe80e9039455cc1608281820fe2b24f1e5233ade6af1dd5"
    ActingPeerId = "seller-ssp"
    CounterpartyPeerId = "buyer-acme"
    Action = TemplateApproved
    IssuedAt = issuedAt
    NotBefore = issuedAt
    ExpiresAt = None
    Signature = "c2lnbmF0dXJlLXBsYWNlaG9sZGVy"
}

let private revocationRecord: TemplateApprovalRecord = {
    approvalRecord with
        Action = TemplateRevoked
        IssuedAt = issuedAt.AddDays 30.0
        NotBefore = issuedAt.AddDays 30.0
        ExpiresAt = Some(issuedAt.AddDays 90.0)
        Signature = "cmV2b2NhdGlvbi1wbGFjZWhvbGRlcg"
}

// ─── Reference values — contract invocation (participant profile) ────

let private callContext: PeerCallContext = {
    Peer = {
        PeerId = "buyer-acme"
        DisplayName = "Acme demand-side"
    }
    User =
        Direct {
            Subject = "user-1874"
            Issuer = "buyer-acme"
            DisplayName = Some "Ada Lovelace"
        }
    ContractVersion = v11
    Route = [ "buyer-acme" ]
    RootRequestId = "0f9a4c22-6b1e-4d3a-9d61-2f0c8b7a5e11"
    ParentRequestId = None
    HopsRemaining = 4
}

let private invocationRequest () =
    JsonRpc.request callContext.RootRequestId "PlaceOrder" {
        Context = callContext
        Arguments = """["order-42",{"Quantity":3}]"""
    }

/// Built field-by-field rather than through `JsonRpc.success`, for the
/// same reason the receiver builds it that way: the method's result is
/// ALREADY a serialised document, and handing it to the helper would
/// encode it a second time.
let private invocationResponse () : JsonRpcResponse = {
    JsonRpc = JsonRpc.version
    Result = Some """{"OrderId":"order-42","Accepted":true}"""
    Error = None
    Id = callContext.RootRequestId
}

/// Every structured failure the seam can put on the wire, one response
/// per case — so an implementer's error mapping is pinned by the corpus
/// and not inferred from the two cases it happened to hit.
let private invocationErrors () : JsonRpcResponse list =
    [
        PeerUnauthorized "missing bearer token"
        PeerContractNotFound "example.unknown"
        PeerMethodNotFound "NoSuchMethod"
        PeerVersionMismatch(v11, [ v1 ])
        PeerLoopDetected [ "buyer-acme"; "broker-mid"; "buyer-acme" ]
        PeerHopLimitExceeded
        PeerTransport "connection reset"
        PeerHandler "downstream ledger unavailable"
        PeerDeserialization "unexpected end of JSON input"
        PeerRequestTooLarge 8388608L
        PeerCleanRoomWithheld "example.cohort-report"
    ]
    |> List.map (JsonRpc.failure callContext.RootRequestId)

/// The three terminal states of the long-running poll leg, in the same
/// `Result`-carried shape the poll route emits.
let private jobPollStatuses () : PeerJobStatus<string> list = [
    PeerJobStatus.Pending
    PeerJobStatus.Completed """{"Reconciled":118}"""
    PeerJobStatus.Failed(PeerHandler "ledger snapshot expired")
]

// ─── Reference values — host envelope (module-host profile) ──────────

/// A reference envelope value. See the file header for why this family
/// is emitted from a value rather than from `HostEnvelope.describe`.
let private referenceEnvelope: HostEnvelope = {
    EnvelopeSchemaVersion = HostEnvelope.CurrentSchemaVersion
    EnvelopePlatform = {
        Package = "example.host"
        Version = "1.0.0.0"
        Assembly = "example.host, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
    }
    EnvelopeCapabilities = [
        {
            LayerKind = "module"
            LayerCount = 1
            LayerIds = [ "module:orders" ]
        }
        {
            LayerKind = "datatype"
            LayerCount = 1
            LayerIds = [ "datatype:SalesData" ]
        }
    ]
    EnvelopeSlots = [
        {
            OfferSlot = ComponentId "companion:IAuditSink"
            OfferInterface = "IAuditSink"
            OfferCardinality = MultiImpl
            OfferState = FilledSlot
            OfferImpls = [ "archive" ]
        }
        {
            OfferSlot = ComponentId "companion:IVectorStore"
            OfferInterface = "IVectorStore"
            OfferCardinality = SingleImpl
            OfferState = OpenSlot
            OfferImpls = []
        }
    ]
    EnvelopeModules = [
        {
            Module = "Orders"
            Component = ComponentId "module:orders"
            Provides = [
                {
                    Field = "DataTypes"
                    Kind = "datatype"
                    Key = "SalesData"
                    Label = "Sales data"
                    Slot = Some(ComponentId "datatype:SalesData")
                }
            ]
            Needs = [
                {
                    Field = "NeedsData"
                    Kind = "substrate"
                    Key = "IDataObjectStore"
                    Label = ""
                    Slot = None
                }
            ]
            Opaque = [
                {
                    Field = "Routes"
                    Kind = "route"
                    Count = 2
                    Reason = "handler composition is opaque to reflection"
                }
            ]
            Coverage = [
                {
                    Field = "DataTypes"
                    Origin = "server"
                    Facet = ProvidesFacet
                }
                {
                    Field = "NeedsData"
                    Origin = "server"
                    Facet = NeedsFacet
                }
            ]
            Unclassified = []
            Stale = []
            ClientDescribed = false
        }
    ]
    EnvelopeKnobs = [
        {
            KnobName = "PeerSubstrate"
            KnobAdmissible = [ "EnabledPeerSubstrate"; "NoPeerSubstrate" ]
            KnobResolved = "EnabledPeerSubstrate"
        }
    ]
    EnvelopeRoutes = [
        {
            RouteKey = "/api/orders/"
            RouteOwner = "Orders"
            RouteAdmits = "UserKind"
            RouteExact = false
        }
    ]
}

// ─── The corpus ──────────────────────────────────────────────────────

let private vector id family profile kind description file document : WireVector = {
    Id = id
    Family = family
    Profile = profile
    Kind = kind
    Description = description
    File = file
    Document = document
    Reject = None
    AgreedHash = None
    Digest = None
}

/// Every conformance vector, in manifest order. Emitting and certifying
/// both walk this list, so the corpus cannot contain a fixture no test
/// runs, or a test with no fixture behind it.
let vectors () : WireVector list =
    let instance = instanceSurface ()

    [
        // ── peer surface ──────────────────────────────────────────────
        vector
            "peer-surface/instance"
            "peer-surface"
            Participant
            Hash
            "A federating deployment's hash-stamped export: two served contracts (one carrying a long-running routine), one consumed contract, trust posture and cascade budget."
            "peer-surface/instance.json"
            (PeerSurface.exportJson instance)

        vector
            "peer-surface/empty"
            "peer-surface"
            Participant
            Hash
            "A deployment with no federation surface at all — the honest empty label, which is a conformant document and not an error."
            "peer-surface/empty.json"
            (PeerSurface.exportJson PeerSurface.empty)

        // ── aggregate surface ─────────────────────────────────────────
        vector
            "aggregate-surface/group"
            "aggregate-surface"
            Gateway
            Hash
            "A three-member group fronting two contracts: posture floored across the exposing members (one facet divergent, reported as a sorted `mixed:` marker), vocabulary pins carried only on unanimity, the unexposed member contributing nothing."
            "aggregate-surface/group.json"
            (PeerSurface.exportJson (derived groupExposure))

        vector
            "aggregate-surface/solo"
            "aggregate-surface"
            Gateway
            Hash
            "A group with one exposing member that agrees with the gateway edge on every facet — the control that separates a floor which collapsed to unanimity from a floor that was never applied, and the case where every pack the member pins does carry."
            "aggregate-surface/solo.json"
            (PeerSurface.exportJson (derived soloExposure))

        // ── pinned exchange ───────────────────────────────────────────
        vector
            "pinned-exchange/pin"
            "pinned-exchange"
            Participant
            RoundTrip
            "The projection a consumer holds of a counterparty's published export: what it claimed to serve, its posture as name/value facets, and the stamp both sides quote."
            "pinned-exchange/pin.json"
            (JsonRpc.serialize (referencePin ()))

        {
            vector
                "pinned-exchange/reject-stamp-mismatch"
                "pinned-exchange"
                Participant
                Reject
                "An export whose stamp does not match a recomputation over its own surface. Corrupt or edited after stamping — refused at pinning time, never held as a stale pin."
                "pinned-exchange/reject-stamp-mismatch.json"
                (tamperedStampDocument ()) with
                Reject = Some "pin-stamp-mismatch"
        }

        {
            vector
                "pinned-exchange/reject-format-version"
                "pinned-exchange"
                Participant
                Reject
                "An export declaring a format version the reader cannot interpret. A half-read label would satisfy a trust requirement by omission, so it is refused rather than truncated."
                "pinned-exchange/reject-format-version.json"
                (futureFormatDocument ()) with
                Reject = Some "pin-format-version-unreadable"
        }

        {
            vector
                "pinned-exchange/reject-malformed"
                "pinned-exchange"
                Participant
                Reject
                "A document that is not a well-formed export at all."
                "pinned-exchange/reject-malformed.json"
                "{\"FormatVersion\": 1, \"SurfaceHash\": " with
                Reject = Some "pin-unparseable"
        }

        {
            vector
                "pinned-exchange/reject-unagreed-hash"
                "pinned-exchange"
                Participant
                Reject
                "An internally consistent export whose stamp is not the one agreed out of band — which a substituted document also is. Internal consistency is not provenance."
                "pinned-exchange/reject-unagreed-hash.json"
                (PeerSurface.exportJson instance) with
                Reject = Some "pin-hash-not-agreed"
                AgreedHash = Some(String.replicate 64 "a")
        }

        // ── attestation ───────────────────────────────────────────────
        {
            vector
                "attestation/approval"
                "attestation"
                Participant
                Hash
                "A signed lifecycle record agreeing one exact content-addressed template version, with no end date. The digest is over the length-prefixed signing input, not over this JSON."
                "attestation/approval.json"
                (JsonRpc.serialize approvalRecord) with
                Digest = Some(TemplateCanonical.recordId approvalRecord)
        }

        {
            vector
                "attestation/revocation"
                "attestation"
                Participant
                Hash
                "The same agreement withdrawn, and carrying an end date — the two fields whose encoding a signer must get right for an expiry to be verifiable."
                "attestation/revocation.json"
                (JsonRpc.serialize revocationRecord) with
                Digest = Some(TemplateCanonical.recordId revocationRecord)
        }

        // ── contract invocation ───────────────────────────────────────
        vector
            "contract-invocation/request"
            "contract-invocation"
            Participant
            RoundTrip
            "A contract call: the JSON-RPC request envelope carrying the propagated call context and the method's positional arguments."
            "contract-invocation/request.json"
            (JsonRpc.serialize (invocationRequest ()))

        vector
            "contract-invocation/response"
            "contract-invocation"
            Participant
            RoundTrip
            "The success response to that call, with the method's result riding as an embedded document."
            "contract-invocation/response.json"
            (JsonRpc.serialize (invocationResponse ()))

        vector
            "contract-invocation/errors"
            "contract-invocation"
            Participant
            RoundTrip
            "One failure response per structured error the seam defines, pinning the whole code mapping rather than the cases an implementation happened to hit."
            "contract-invocation/errors.json"
            (JsonRpc.serialize (invocationErrors ()))

        vector
            "contract-invocation/job-poll"
            "contract-invocation"
            Participant
            RoundTrip
            "The three terminal states of the long-running poll leg, in the shape the poll response carries them."
            "contract-invocation/job-poll.json"
            (JsonRpc.serialize (jobPollStatuses ()))

        // ── host envelope ─────────────────────────────────────────────
        vector
            "host-envelope/envelope"
            "host-envelope"
            ModuleHost
            RoundTrip
            "What a host offers a module it will run: composed capability layers, filled and open companion slots, each module's surface, config knobs with resolved values, and occupied routes."
            "host-envelope/envelope.json"
            (HostEnvelope.toJson referenceEnvelope)

        vector
            "host-envelope/stamp"
            "host-envelope"
            ModuleHost
            Hash
            "The sidecar stamp a consumer pins beside a generated module, so it can tell later whether the host moved underneath it."
            "host-envelope/stamp.json"
            (JsonRpc.serialize (HostEnvelope.stampOf referenceEnvelope))
    ]

// ─── Manifest ────────────────────────────────────────────────────────

/// JSON string escaping, applied to the manifest's own values. The
/// fixture documents are written verbatim; only this hand-built manifest
/// needs it.
let private escape (value: string) : string =
    let sb = StringBuilder()

    for ch in value do
        match ch with
        | '"' -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | c when c < ' ' -> sb.Append(sprintf "\\u%04x" (int c)) |> ignore
        | c -> sb.Append c |> ignore

    sb.ToString()

/// Render the manifest. Hand-built with explicit `\n` rather than
/// serialised: it is the one file in the corpus a human reads first, and
/// an indenting serialiser would emit the platform's line ending, which
/// would make the corpus hash differently per checkout.
let renderManifest (vectors: WireVector list) : string =
    let sb = StringBuilder()
    let line (text: string) = sb.Append(text).Append('\n') |> ignore

    line "{"
    line "  \"specification\": \"federation-seam-wire\","
    line $"  \"formatVersion\": {corpusFormatVersion},"
    line "  \"families\": ["

    let families = vectors |> List.map _.Family |> List.distinct

    families
    |> List.iteri (fun i family ->
        let comma = if i = families.Length - 1 then "" else ","
        line $"    \"{escape family}\"{comma}")

    line "  ],"
    line "  \"profiles\": {"

    let profiles = [ Participant; Gateway; ModuleHost ]

    profiles
    |> List.iteri (fun i profile ->
        let comma = if i = profiles.Length - 1 then "" else ","

        let names =
            WireProfile.families profile
            |> List.map (fun f -> $"\"{escape f}\"")
            |> String.concat ", "

        line $"    \"{WireProfile.name profile}\": [ {names} ]{comma}")

    line "  },"
    line "  \"vectors\": ["

    vectors
    |> List.iteri (fun i v ->
        let comma = if i = vectors.Length - 1 then "" else ","
        line "    {"
        line $"      \"id\": \"{escape v.Id}\","
        line $"      \"family\": \"{escape v.Family}\","
        line $"      \"profile\": \"{WireProfile.name v.Profile}\","
        line $"      \"kind\": \"{WireVectorKind.name v.Kind}\","
        line $"      \"file\": \"{escape v.File}\","
        line $"      \"sha256\": \"{digestOf v.Document}\","

        match v.Reject with
        | Some reason -> line $"      \"reject\": \"{escape reason}\","
        | None -> ()

        match v.AgreedHash with
        | Some hash -> line $"      \"agreedHash\": \"{escape hash}\","
        | None -> ()

        match v.Digest with
        | Some digest -> line $"      \"digest\": \"{escape digest}\","
        | None -> ()

        line $"      \"description\": \"{escape v.Description}\""
        line $"    }}{comma}")

    line "  ]"
    line "}"
    sb.ToString()

// ─── Emit + read ─────────────────────────────────────────────────────

/// Write the corpus to disk. Documents are written verbatim with no
/// trailing newline, so a fixture's bytes ARE the canonical document and
/// its `sha256` needs no framing convention to interpret.
let emit () : unit =
    let root = corpusDir ()
    let all = vectors ()

    for v in all do
        let path = Path.Combine(root, v.File.Replace('/', Path.DirectorySeparatorChar))
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, v.Document, UTF8Encoding false)

    File.WriteAllText(manifestPath (), renderManifest all, UTF8Encoding false)

/// Read a committed fixture. Line endings are normalised because only
/// the manifest carries any, and a CRLF checkout must not read as drift.
let readCommitted (relativePath: string) : string =
    let path =
        Path.Combine(corpusDir (), relativePath.Replace('/', Path.DirectorySeparatorChar))

    File.ReadAllText(path).Replace("\r\n", "\n")

/// Every `.json` file under the corpus root, corpus-relative and
/// forward-slashed — the disk's own answer to "what is in the corpus",
/// against which the manifest's enumeration is checked.
let filesOnDisk () : string list =
    let root = corpusDir ()

    Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
    |> Array.map (fun path -> Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
    |> Array.sort
    |> Array.toList
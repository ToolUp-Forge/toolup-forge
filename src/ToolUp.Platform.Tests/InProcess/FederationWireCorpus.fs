module ToolUp.Platform.Tests.InProcess.FederationWireCorpus

// ─── Phase 596 — federation-seam conformance corpus (the emitter) ─────
//
// The executable half of the federation-seam wire specification. This
// module holds the reference *values* of every specified shape family,
// renders them through the **live** emitters (`PeerSurface.exportJson`,
// `AggregatePeerSurface.derive`, `HostEnvelope.toJson`,
// `JsonRpc.serialize`, `TemplateCanonical.recordId`), and writes the
// result to the conformance corpus in the federation-seam specification
// home — a separate public repository this one does not own — as the
// corpus an implementation in any language certifies against.
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
open ToolUp.Platform.Tests.Support
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose

// ─── Corpus location ─────────────────────────────────────────────────

/// Repo root derived from the running test assembly:
/// bin/<Config>/net10.0/ToolUp.Platform.Tests.dll → up 5 = repo root.
let repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

/// The name of the specification home's directory. The DIRECTORY name is
/// the interface here, not the repository name — a consumer resolves the
/// corpus by this name, so renaming the repository never reaches one.
[<Literal>]
let SpecDirName = "fuaran-federation-spec"

/// Environment override naming the specification home directly. CI and any
/// checkout that does not sit where the search below looks set this.
[<Literal>]
let SpecDirVariable = "TOOLUP_FEDERATION_SPEC_DIR"

/// Opt out of the conformance leg when the specification home is absent.
///
/// It is deliberately an OPT-OUT rather than a silent skip. A conformance
/// suite that quietly does nothing when its corpus is missing is the exact
/// shape that reads as covered while certifying nothing — so the default is
/// a loud failure naming how to fix it, and declining the leg has to be an
/// explicit act somebody wrote down. CI never sets this.
[<Literal>]
let SpecOptionalVariable = "TOOLUP_FEDERATION_SPEC_OPTIONAL"

let private envDir (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some v

/// Does `dir` look like the specification home? Keyed on the corpus's own
/// enumeration, so a directory of the right NAME but the wrong contents is
/// not mistaken for it.
let private isSpecHome (dir: string) =
    File.Exists(Path.Combine(dir, "wire-fixtures", "manifest.json"))

/// Bounded search for the specification home: each ancestor of the
/// anchor, then up to three levels beneath each. A SEARCH rather than a
/// relative path on purpose — a hard-coded `../../<...>/<...>` would encode
/// one particular checkout layout into a repository that is cloned
/// standalone, and would be wrong for everybody else.
///
/// The anchor is the repository's MAIN working tree when this checkout is
/// a linked git worktree (see `Support.CorpusAnchor`), so a worktree of a
/// checkout that sits inside a wider workspace finds the same spec home
/// the checkout itself would — and the walk never enters a DIFFERENT
/// working tree of this repository, whose transiently-present contents
/// would otherwise make resolution non-deterministic between runs.
let private searchForSpecHome (anchoring: CorpusAnchor.Anchoring) =
    let skip (name: string) =
        name.StartsWith '.'
        || name = "node_modules"
        || name = "bin"
        || name = "obj"
        || name = "packages"

    let rec descend (dir: string) (depth: int) =
        if depth > 3 || CorpusAnchor.excluded anchoring dir then
            None
        else
            let candidate = Path.Combine(dir, SpecDirName)

            if
                Directory.Exists candidate
                && isSpecHome candidate
                && not (CorpusAnchor.excluded anchoring candidate)
            then
                Some candidate
            else
                try
                    Directory.EnumerateDirectories dir
                    |> Seq.filter (fun d -> not (skip (Path.GetFileName d)))
                    |> Seq.tryPick (fun d -> descend d (depth + 1))
                with _ ->
                    None

    let rec ascend (dir: string) (levels: int) =
        if levels > 4 || String.IsNullOrEmpty dir then
            None
        else
            match descend dir 1 with
            | Some found -> Some found
            | None ->
                match Path.GetDirectoryName dir with
                | null -> None
                | parent -> ascend parent (levels + 1)

    ascend anchoring.Anchor 0

/// The federation-seam specification home, or `None`.
///
/// **This repository does not own the specification or its corpus.** Both
/// live in their own public home; this repository is an emitter that
/// certifies against them. That direction is the point rather than an
/// accident of where files ended up — a specification owned by one of its
/// implementations cannot be conformed to by the others on equal terms.
///
/// The search anchor and the working trees the walk must refuse to enter.
/// Resolved once — it shells git.
let private anchoring = lazy (CorpusAnchor.resolve (repoRoot ()))

/// Resolved ONCE. Every fixture read goes through here, and the fallback is
/// a directory walk — resolving it per read would turn ~50 reads into ~50
/// filesystem searches.
let private resolvedSpecHome =
    lazy
        (match envDir SpecDirVariable with
         // An explicitly-named directory is still CHECKED — taking it on trust
         // turns a typo into `File not found` on the first fixture read, which
         // reads as a corpus problem rather than as the pointer being wrong.
         | Some explicitDir when isSpecHome explicitDir -> Some explicitDir

         // And when it is set but wrong, that is the ANSWER — the search is not
         // run as a fallback. Falling back would silently certify against some
         // other corpus than the one the caller named, which is worse than
         // finding none: the run goes green having measured the wrong thing,
         // and the pointer that was wrong is never mentioned.
         | Some _ -> None
         | None ->
             // An in-repo checkout first: it is what CI clones, and it is
             // the cheapest thing to look for — checked in this checkout
             // and then in the main working tree, so a linked worktree of
             // a checkout carrying an in-repo clone still resolves it.
             [ repoRoot (); anchoring.Value.Anchor ]
             |> List.distinct
             |> List.tryPick (fun root ->
                 let inRepo = Path.Combine(root, SpecDirName)

                 if Directory.Exists inRepo && isSpecHome inRepo then
                     Some inRepo
                 else
                     None)
             |> Option.orElseWith (fun () -> searchForSpecHome anchoring.Value))

let specHome () : string option = resolvedSpecHome.Value

/// How to obtain the specification home — the whole of the remedy, in the
/// failure message rather than in a document the reader is not looking at.
let specHomeMissingMessage =
    $"ENVIRONMENTAL FAILURE — the federation-seam specification home was not found, so there is no
       corpus to certify against; this is the checkout's arrangement, not a defect in the code under test.
       This repository is an EMITTER of that specification, not its owner: the normative text and the
       conformance corpus live in their own public repository.
       Fix it in one of three ways:
         1. clone it into this repository (what CI does; the path is gitignored):
              git clone https://github.com/fuaran-ui/{SpecDirName}.git {SpecDirName}
         2. set {SpecDirVariable} to an existing checkout of it — if it is already set, it does
            not point at one (a spec home contains wire-fixtures/manifest.json), or
         3. place a checkout named '{SpecDirName}' at or near this repository's parent — for a
            linked git worktree that means near the MAIN working tree, which is where the search
            runs from; a checkout under a different worktree of this repository is never accepted.
       To run without the conformance leg, set {SpecOptionalVariable}=1 — which declines the leg
       deliberately rather than skipping it silently."

/// The conformance corpus inside the specification home.
let corpusDir () =
    match specHome () with
    | Some dir -> Path.Combine(dir, "wire-fixtures")
    | None ->
        let worktreeNote =
            match CorpusAnchor.mainWorkingTree (repoRoot ()) with
            | Some main -> $" (This checkout is a linked git worktree; its main working tree is '{main}'.)"
            | None -> ""

        failwith (specHomeMissingMessage + worktreeNote)

/// Is the conformance leg being declined deliberately? Only ever true when
/// the specification home is absent AND somebody said so explicitly.
let specLegDeclined () =
    match specHome () with
    | Some _ -> false
    | None ->
        match Environment.GetEnvironmentVariable SpecOptionalVariable with
        | null
        | "" -> false
        | v -> v = "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)

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
    /// Phase 638 — the deployment that holds the datasets and runs the
    /// fits.
    | DataHost
    /// Phase 638 — the deployment that authors the specs and submits
    /// them.
    | Modeller

[<RequireQualifiedAccess>]
module WireProfile =

    let name =
        function
        | Participant -> "participant"
        | Gateway -> "gateway"
        | ModuleHost -> "module-host"
        | DataHost -> "participant-data-host"
        | Modeller -> "participant-modeller"

    /// The families a profile's implementer must certify against — the
    /// per-profile partition of the corpus. Cumulative: a gateway is a
    /// participant that also fronts a group, and a module host is a
    /// gateway that also runs somebody else's module.
    ///
    /// The two model-execution roles are cumulative over `participant`
    /// and require the identical family. They are separate profiles
    /// because a conformance claim has to say which SIDE of each shape
    /// the implementation emits: the refusals a data host must PRODUCE
    /// are the ones a modeller must UNDERSTAND, and an implementation
    /// that only ever produces them has certified half the seam.
    let rec families (profile: WireProfile) : string list =
        match profile with
        | Participant -> [ "peer-surface"; "pinned-exchange"; "attestation"; "contract-invocation" ]
        | Gateway -> families Participant @ [ "aggregate-surface" ]
        | ModuleHost -> families Gateway @ [ "host-envelope" ]
        | DataHost
        | Modeller -> families Participant @ [ "model-execution" ]

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

/// Phase 642 — the same reference deployment, declaring a
/// data-visibility authority level other than the default.
///
/// A separate vector rather than a change to the one above, because the
/// two pin different things and both are worth pinning: the instance
/// vector shows what a deployment that declares nothing publishes (the
/// fail-closed `"AggregatesOnly"`, present rather than omitted), and this
/// one shows a declared grant. An implementation that hard-coded the
/// default would pass the first and fail this.
let private authoritySurface () =
    referenceInstance ()
    |> PeerServerApp.withDataVisibility PeerDataVisibilityLevel.ViewOnly
    |> PeerSurface.describe

/// Phase 644 — the same reference deployment declaring a TRANSITION
/// grant, and nothing else.
///
/// A third surface vector rather than a fourth member on the second,
/// because the two declarations are on different axes and pinning them
/// together would let an implementation that fused them into one ladder
/// pass. This one publishes the default visibility level beside a
/// non-empty grant, which is the arrangement the phase exists to make
/// expressible: a counterparty that may approve models and must never
/// see a row.
///
/// The grant is declared out of ordinal order on purpose — the emitter
/// owns the sort, not whoever typed the list.
let private transitionGrantSurface () =
    referenceInstance ()
    |> PeerServerApp.withPeerTransitionAuthority (ModelTransitionAuthority.ofTargets [ "Retired"; "Approved" ])
    |> PeerSurface.describe

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
        // Phase 642 — every reference member grants the default, so the
        // aggregate's authority floor collapses to it. The floor's
        // behaviour under DIVERGENCE is pinned by the unit tests rather
        // than here: a divergent member would move the group fixture's
        // bytes for a reason unrelated to what that fixture is about
        // (posture `mixed:` markers and pin unanimity), and a fixture
        // that pins two unrelated rules at once is one nobody can read.
        DataVisibility = PeerDataVisibilityLevel.label PeerDataVisibilityLevel.default'
        // Phase 644 — a member declaring no transition grant, for the
        // reason it declares the default level: the aggregate fixture is
        // about posture floors and pin unanimity, and a member declaring
        // a grant here would drag a second, unrelated derivation into a
        // vector nobody could then read. The intersection floor is
        // asserted in `AggregatePeerSurfaceTests` instead, where it is
        // the only thing under test.
        TransitionAuthority = []
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

// ─── Reference values — model execution (data-host / modeller) ───────

/// The scope the reference peer binding addresses. One reject vector
/// asserts a different one, which is the whole of the scope-widening
/// case: the value is refused, never routed on.
[<Literal>]
let modelExecutionBoundScope = "consortium-north"

/// What the reference data host admits: the whole submitter surface and
/// all three declared diagnostics. The reject vectors are read against
/// exactly this, so "undeclared" means undeclared by a deployment that
/// declared everything the profile defines.
let referenceAdmission =
    ModelExecutionAdmission.create ModelExecutionProfile.diagnostics

/// Phase 642 — the reference data host's peer id, used as the outermost
/// scope of every authority walk below.
[<Literal>]
let modelExecutionPeerId = "buyer-acme"

/// Phase 642 — an admission at a declared ceiling with no narrowing.
let private admissionAt (ceiling: PeerDataVisibilityLevel) =
    referenceAdmission
    |> ModelExecutionAdmission.withAuthority (
        PeerVisibility.resolve (PeerVisibilityBinding.ofCeiling ceiling) modelExecutionPeerId
    )

/// Phase 642 — a `ViewOnly` ceiling with a team layer narrowing back to
/// `AggregatesOnly`. The binding the narrowing reject vector is read
/// against: the ceiling WOULD have admitted the request, and the layer
/// beneath it does not.
let private narrowedAdmission =
    referenceAdmission
    |> ModelExecutionAdmission.withAuthority (
        PeerVisibility.resolve
            (PeerVisibilityBinding.ofCeiling PeerDataVisibilityLevel.ViewOnly
             |> PeerVisibilityBinding.withNarrowing
                 (TeamNarrowing "north-analysts")
                 PeerDataVisibilityLevel.AggregatesOnly)
            modelExecutionPeerId
    )

// The state each model-execution reject vector is judged against is one
// keyed record, `modelExecutionStateFor`, declared once every reference
// value it names is in scope — see the model-execution vector state
// below.

let private referenceVintage: ModelExecutionPeerVintage = {
    DatasetId = "weekly-panel"
    Version = 7
}

/// Gates declared out of order — the emitter, not the author, owns the
/// ordinal sort.
let private referenceSubmission: ModelExecutionPeerSubmission = {
    Vintage = referenceVintage
    SpecPayload = """{"link":"log","terms":["price","promo"]}"""
    SpecHash = "sha256:1b4f0e9851971998e732078544c96b36c3d01cedf7caa332359d6f1d83567014"
    ProviderKind = "reference-regression"
    Seed = 20260716L
    Gates = [
        {
            Name = "vif-max"
            Threshold = 5.0
            Direction = "AtMost"
        }
        {
            Name = "holdout-r2"
            Threshold = 0.6
            Direction = "AtLeast"
        }
    ]
    // Phase 451 — the declared submitter class rides the peer wire as a
    // stable lowercase label (interface-plan decision D5).
    SubmitterClass = "agent"
}

/// A registered outcome in the SUBMITTER surface's own shape, projected
/// onto the profile's by the live `toWireOutcome` — so the corpus pins
/// the projection as well as the encoding.
let private referenceOutcome: ModelExecutionOutcome = {
    CompositeKeyHash = "sha256:60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752"
    SpecHash = referenceSubmission.SpecHash
    DatasetVersion = "consortium-north/weekly-panel@v7"
    Seed = referenceSubmission.Seed
    ProviderId = "reference-regression"
    ProviderVersion = "1.4.0"
    Artifact =
        Some {
            ArtifactId = "artifact-8821"
            ContentHash = "sha256:fcde2b2edba56bf408601fb721fe9b5c338d10ee429ea04fae5511b68fbf8fb9"
            Format = None
        }
    Diagnostics = Map.ofList [ "aic", 812.5; "holdout-r2", 0.71; "vif-max", 3.25 ]
    GateVerdicts = [
        {
            Name = "vif-max"
            Threshold = 5.0
            Direction = "AtMost"
            Observed = 3.25
            Passed = true
        }
        {
            Name = "holdout-r2"
            Threshold = 0.6
            Direction = "AtLeast"
            Observed = 0.71
            Passed = true
        }
    ]
    Status = "Approved"
    Annotations = Map.ofList [ "batch", "wave-3" ]
    // Phase 640 — the outcome carries timing and cost. Neither rides the
    // federation profile at this version, so they are set here only to
    // build the submitter-shaped value the projection reads from.
    Timing = {
        SubmittedAt = DateTimeOffset(2026, 7, 16, 10, 15, 0, TimeSpan.Zero)
        StartedAt = None
        CompletedAt = None
        DurationMs = None
    }
    Cost = None
    RegisteredAt = DateTimeOffset(2026, 7, 16, 10, 15, 0, TimeSpan.Zero)
}

/// The three declared governed diagnostics, each an aggregate
/// projection the clean-room gate can evaluate. Every cell carries a
/// cohort count, which is what the floor binds on; no cell carries a
/// row, and none could.
let private referenceDiagnostics: CohortResult list = [
    {
        Shape = Histogram
        Cells = [
            {
                Label = "price|promo"
                Count = 182
                Value = Some 0.42
            }
            {
                Label = "price|seasonality"
                Count = 182
                Value = Some 0.18
            }
        ]
    }
    {
        Shape = Aggregate
        Cells = [
            {
                Label = "observed-weeks"
                Count = 182
                Value = Some 0.97
            }
        ]
    }
    {
        Shape = Histogram
        Cells = [
            {
                Label = "adstock-decay-0.3"
                Count = 182
                Value = Some 0.55
            }
            {
                Label = "adstock-decay-0.6"
                Count = 182
                Value = Some 0.31
            }
        ]
    }
]

// ─── Reference values — Phase 643 bounded views ──────────────────────

/// The reference deployment's declared views.
///
/// Two of them, declared OUT of ordinal order, so the fixture pins the
/// sort `PeerView.list` applies rather than the order somebody happened
/// to type. Their bounds are the whole offer: a request outside any of
/// them is refused with the class that names which one it left.
let referenceViewDeclarations: PeerViewDeclaration list = [
    {
        ViewId = "spend-vs-response"
        DatasetId = "weekly-panel"
        Title = "Weekly spend against response"
        Kind = "line"
        Series = [ "promo-spend"; "search-clicks" ]
        Resolutions = [ "day"; "week" ]
        MaxWindowDays = 90
        MaxSeriesPerRequest = 2
        MaxPointsPerSeries = 26
        MaxRendersPerWindow = 20
        RenderWindowSeconds = 3600
    }
    {
        ViewId = "coverage-by-week"
        DatasetId = "weekly-panel"
        Title = "Observed coverage by week"
        Kind = "bar"
        Series = [ "observed-weeks" ]
        Resolutions = [ "week" ]
        MaxWindowDays = 365
        MaxSeriesPerRequest = 1
        MaxPointsPerSeries = 52
        MaxRendersPerWindow = 5
        RenderWindowSeconds = 3600
    }
]

let private referenceViewWindow: PeerViewWindow = {
    From = DateTimeOffset(2026, 4, 20, 0, 0, 0, TimeSpan.Zero)
    To = DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero)
}

/// A render request inside every declared bound. Series named out of
/// ordinal order on purpose — the emitter owns the sort.
let referenceViewRequest: PeerViewRequest = {
    ViewId = "spend-vs-response"
    DatasetVersion = 7
    Series = [ "search-clicks"; "promo-spend" ]
    Window = referenceViewWindow
    Resolution = "week"
}

/// The reference deployment's rendered artifact, as a VALUE.
///
/// **Not the output of a live renderer, and that is the same judgement
/// the host-envelope family records above.** What a deployment's chart
/// grammar draws is its own business — §5.6's posture, and §5.7.10 says
/// so explicitly — while the wire contract is the document the artifact
/// rides in: a declared media type, base64 content, a hash over the
/// bytes, and the metadata naming what was shown. Pinning a live render
/// would make this fixture go red on a cosmetic change to an SVG
/// attribute, which is a gate nobody would keep, and it would pin
/// nothing the specification actually states.
///
/// The ENCODING rules are still triangulated: `emit.mjs` derives the
/// base64 and the hash from the same reference bytes by its own route.
[<Literal>]
let private referenceArtifactSvg =
    "<svg viewBox=\"0 0 320.0 160.0\" role=\"img\"></svg>"

let private referenceArtifactBytes = Encoding.UTF8.GetBytes referenceArtifactSvg

let private referenceArtifact: PeerViewArtifact = {
    ViewId = referenceViewRequest.ViewId
    MediaType = "image/svg+xml"
    Content = Convert.ToBase64String referenceArtifactBytes
    ContentHash = "sha256:" + sha256Hex referenceArtifactBytes
    Series = [ "promo-spend"; "search-clicks" ]
    Window = referenceViewWindow
    Resolution = referenceViewRequest.Resolution
    RenderedPoints = 26
}

/// A request whose window is wider than the view declares. Well-formed,
/// names a declared view, declared series and a declared resolution —
/// the ONE thing wrong with it is the bound, which is what makes it a
/// test of the bound rather than of the parser.
let private overBoundWindowRequest: PeerViewRequest = {
    referenceViewRequest with
        Window = {
            referenceViewWindow with
                From = DateTimeOffset(2025, 7, 13, 0, 0, 0, TimeSpan.Zero)
        }
}

/// A request naming a series this view does not carry. Also well-formed,
/// and also refused for exactly one reason.
let private undeclaredSeriesRequest: PeerViewRequest = {
    referenceViewRequest with
        Series = [ "promo-spend"; "margin-per-unit" ]
}

// ─── Reference values — Phase 644 lifecycle transitions ──────────────

/// The reference peer's declared grant: it may approve, and it may not
/// retire. Deliberately a PROPER SUBSET of the lifecycle rather than
/// none, so an unauthorized-transition vector is refused by a peer that
/// holds a grant — the case that separates "checked the grant" from
/// "checked whether a grant exists".
let referenceTransitionGrant = ModelTransitionAuthority.ofTargets [ "Approved" ]

/// The artifact the reference invocation names, and the status it holds.
[<Literal>]
let private referenceArtifactKey =
    "4d0f2b8c9e7a5613f8c2a94d0e1b7635c8f4a209d3e6b1758c0a2f9d4e63b7a1"

/// A well-formed invocation inside the grant: promote a fitted artifact,
/// with a stated reason.
let referenceTransitionInvocation: PeerTransitionInvocation = {
    ArtifactKey = referenceArtifactKey
    Target = "Approved"
    ActorId = "r.okafor"
    Rationale = Some "holdout MAPE within tolerance on three vintages"
}

/// The attributed record the admitted invocation produced.
///
/// A reference VALUE rather than the output of a live `invoke`, for the
/// reason the rendered artifact above is one: the wire contract is the
/// document, and standing up a registry, an audit log and a clock inside
/// the corpus would pin this deployment's storage arrangements rather
/// than the shape the specification states. The FIELDS are still derived
/// from the live projection — `PeerTransition.toWireRecord` renders it —
/// so a member added to the seam's record fails here rather than
/// wherever it is next noticed.
let private referenceTransitionRecord: PeerTransitionRecord =
    PeerTransition.toWireRecord {
        ArtifactKey = referenceArtifactKey
        FromStatus = "Fitted"
        ToStatus = "Approved"
        Channel = "peer"
        AuthorKind = "peer"
        AuthorId = "consortium-north/r.okafor"
        Rationale = referenceTransitionInvocation.Rationale
        RecordedAt = DateTimeOffset(2026, 7, 16, 10, 15, 0, TimeSpan.Zero)
        Version = 2
    }

/// An invocation naming an artifact this scope does not hold.
let private unknownArtifactInvocation: PeerTransitionInvocation = {
    referenceTransitionInvocation with
        ArtifactKey = "0000000000000000000000000000000000000000000000000000000000000000"
}

/// An invocation asking for an edge the lifecycle graph forbids —
/// `Retired` is terminal, so nothing leaves it. Well-formed, granted
/// (the reject state below hands this one a full grant), and refused on
/// the single thing wrong with it.
let private invalidTransitionInvocation: PeerTransitionInvocation = {
    referenceTransitionInvocation with
        Target = "Fitted"
}

/// An invocation for a legal edge this peer's grant does not admit. The
/// grant is real and admits `Approved`; it does not admit `Retired`.
let private unauthorizedTransitionInvocation: PeerTransitionInvocation = {
    referenceTransitionInvocation with
        Target = "Retired"
}

// ─── Reference values — Phase 646 promotion transfers ────────────────

/// The exploration record a modelling tool kept beside the fit — the
/// canonical example of what an attachment IS.
///
/// **Its content is deliberately somebody else's schema.** Nothing in
/// forge reads this; the corpus carries it as bytes with a digest, which
/// is exactly the claim the slot makes. A vector whose payload forge could
/// have parsed would prove the opposite of what it exists to prove.
let private referenceExplorationRecord =
    """{"candidates":["price","promo","seasonality"],"kept":["price","promo"],"dropped":{"seasonality":"vif 8.4"}}"""

/// A second record under a different media type, so the vector carries
/// more than one attachment and the ordinal sort over content hashes is
/// exercised rather than asserted.
let private referenceRunLog =
    "fit 1/3 converged\nfit 2/3 converged\nfit 3/3 converged\n"

let private referenceExplorationAttachment =
    ProvenanceAttachment.ofText "application/json" referenceExplorationRecord

let private referenceRunLogAttachment =
    ProvenanceAttachment.ofText "text/plain" referenceRunLog

/// The spec payload the promoted artifact was fit from — the same opaque
/// document the submission vector carries, because it is the same fit seen
/// from the other end of its life.
let private referencePromotedSpecPayload = referenceSubmission.SpecPayload

/// The composite identity of the promoted artifact. Deliberately the same
/// key the transition vectors name: a data host that received this
/// artifact by transfer is the same host a peer then transitions it on,
/// and the corpus says so by reusing the id rather than by asserting it.
let private referencePromotedKey: FitCompositeKey = {
    SpecHash = referenceSubmission.SpecHash
    DatasetVersion = "consortium-north/weekly-panel@v7"
    Seed = referenceSubmission.Seed
    ProviderId = "reference-regression"
    ProviderVersion = "1.4.0"
    Hash = referenceArtifactKey
}

/// A final artifact as the BUILDER hands it over: identity, the opaque
/// spec, the gate verdicts it passed, and the evidence beside it.
///
/// Gate verdicts and attachments are declared out of ordinal order on
/// purpose — the emitter owns the sorts, not whoever typed the value.
let referencePromotedArtifact: PromotedArtifact = {
    Outcome = {
        CompositeKey = referencePromotedKey
        ArtifactRef = {
            ArtifactId = "artifact-8821"
            ContentHash = "sha256:fcde2b2edba56bf408601fb721fe9b5c338d10ee429ea04fae5511b68fbf8fb9"
            ByteLength = 4096L
        }
        Diagnostics = Map [ "aic", 812.5; "holdout-r2", 0.71; "vif-max", 3.25 ]
        GateVerdicts = [
            {
                Name = "vif-max"
                Threshold = 5.0
                Direction = GateDirection.AtMost
                Observed = 3.25
                Passed = true
            }
            {
                Name = "holdout-r2"
                Threshold = 0.6
                Direction = GateDirection.AtLeast
                Observed = 0.71
                Passed = true
            }
        ]
        // The building deployment's own compute self-report is not part of
        // what a data host is asked to hold, so the profile does not carry
        // it and the reference value states zero rather than a number the
        // wire would drop.
        DurationMs = 0L
        CostUnits = 0.0
    }
    SpecPayload = referencePromotedSpecPayload
    Attachments = [ referenceRunLogAttachment; referenceExplorationAttachment ]
    Target = ModelArtifactStatus.Approved
    Author = PeerActor("consortium-north", "r.okafor")
    Rationale = Some "holdout MAPE within tolerance on three vintages"
}

/// The transfer as it crosses the seam — built by the SHIPPED projection,
/// so a member added to `PromotedArtifact` fails here rather than wherever
/// it is next noticed.
let referencePromotionTransfer =
    PeerPromotion.ofPromoted referencePromotedArtifact "r.okafor"

/// Every attachment the artifact holds once the transfer lands: the two it
/// carried plus the spec payload, which the receiver folds into the same
/// append-only slot under its reserved media type.
let private referencePromotionAttachmentHashes =
    ModelPromotion.arriving referencePromotedArtifact |> List.map _.ContentHash

/// The acceptance signature.
///
/// The JWS is a reference VALUE — an ECDSA signature is not deterministic,
/// and standing up a signer inside the corpus would pin this deployment's
/// key material rather than the shape the specification states. The
/// **signing-input digest is not**: it is recomputed here from the
/// canonical form, and `emit.mjs` rebuilds that form independently and
/// must agree. That is the member worth triangulating — a verifier in
/// another language has to reconstruct exactly these bytes, and it is the
/// one part of a signature an independent implementation can get wrong
/// without any key at all.
let private referencePromotionSignature: ModelArtifactSignature = {
    DetachedJws =
        "eyJhbGciOiJFUzI1NiIsImtpZCI6ImRhdGEtaG9zdC0yMDI2LTA3In0..MEUCIQDdemo0promotion0signature0value0only0not0verifiable"
    SigningKeyId = "data-host-2026-07"
    SigningKeyUrl = "/_platform/signing-key/data-host-2026-07"
    SignedInputHash =
        ProvenanceAttachment.hashOf (
            ModelPromotionSigningInput.bytes
                referencePromotedKey
                ModelArtifactStatus.Approved
                referencePromotionAttachmentHashes
        )
}

/// The receipt an accepted transfer produced, through the shipped
/// projection for the reason the transition record goes through its own.
let private referencePromotionRecord: PeerPromotionRecord =
    PeerPromotion.toWireRecord {
        ArtifactKey = referenceArtifactKey
        Status = "Approved"
        AttachmentHashes = referencePromotionAttachmentHashes
        Signature = Some referencePromotionSignature
        Channel = "peer"
        AuthorKind = "peer"
        AuthorId = "consortium-north/r.okafor"
        Replayed = false
        RecordedAt = DateTimeOffset(2026, 7, 16, 10, 15, 0, TimeSpan.Zero)
        Version = 2
    }

/// A transfer whose attachment declares a digest its bytes do not produce.
/// The one thing a receiver can check about a payload it is forbidden to
/// read, and therefore the one integrity claim this seam makes.
let private hashMismatchTransfer = {
    referencePromotionTransfer with
        Attachments =
            referencePromotionTransfer.Attachments
            |> List.map (fun a ->
                if a.MediaType = "text/plain" then
                    {
                        a with
                            ContentHash = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
                    }
                else
                    a)
}

// ─── Model-execution vector state ────────────────────────────────────

/// Everything a model-execution reject vector is judged against, keyed
/// by vector id.
///
/// **One record rather than a lookup per reader.** Each reader the family
/// grew — the envelope, the transition judge, the transfer judge — arrived
/// needing a different slice of the same per-vector state, and each was
/// first served by its own parallel lookup over the same ids. Three such
/// lookups agreeing on which vector is which is a coincidence maintained
/// by hand: a vector added to one and forgotten in another reads with the
/// wrong state and still passes, because the default arm answers. A single
/// keyed record makes a vector's state one thing to state and one thing to
/// get wrong, and a fourth reader adds a FIELD rather than a fourth
/// lookup.
///
/// **The state is supplied rather than read out of a store because the
/// judges are pure** — `ModelTransition.judge` and `ModelPromotion.judge`
/// take the status, cap and incumbent as arguments, which is exactly the
/// property that lets the harness certify against the shipped functions
/// rather than a test-local reimplementation of them. A vector's state is
/// part of the vector.
type ModelExecutionVectorState = {
    /// The grant the request envelope is read against. Per-vector because
    /// an authority refusal is a statement about a GRANT and a grant is
    /// per-peer: three vectors share one document and differ only in this,
    /// which is the whole property the levels have to hold — the same
    /// request answered at one level, refused at another, and refused for a
    /// different reason under a narrowing.
    Admission: ModelExecutionAdmission
    /// The lifecycle status the scope holds for the artifact a transition
    /// names, or `None` where it holds nothing.
    ArtifactStatus: ModelArtifactStatus option
    /// The peer's lifecycle grant, read by the transition and transfer
    /// judges alike.
    TransitionGrant: ModelTransitionAuthority
    /// The artifact the scope already holds under the transferred key, for
    /// the conflict case.
    Incumbent: ModelArtifact option
    /// The RECEIVER's declared attachment bounds.
    AttachmentLimits: ProvenanceAttachmentLimits
}

/// The state every vector reads at unless it names otherwise. Each arm
/// below overrides only the fields its own refusal is about, so a vector's
/// entry says what is special about it and nothing else.
let private referenceVectorState: ModelExecutionVectorState = {
    Admission = referenceAdmission
    ArtifactStatus = Some ModelArtifactStatus.Fitted
    TransitionGrant = referenceTransitionGrant
    Incumbent = None
    AttachmentLimits = ProvenanceAttachmentLimits.default'
}

/// The state a given model-execution reject vector is judged against.
///
/// Every pre-642 vector maps to the reference state and reads as it always
/// did. The mapping lives here so the harness reads it rather than
/// reconstructing it.
let modelExecutionStateFor (vectorId: string) : ModelExecutionVectorState =
    match vectorId with
    // Phase 642 — the authority family. The document is identical across
    // these three; the grant is the vector.
    | "model-execution/reject-view-at-aggregates" -> {
        referenceVectorState with
            Admission = admissionAt PeerDataVisibilityLevel.AggregatesOnly
      }
    | "model-execution/reject-full-at-view" -> {
        referenceVectorState with
            Admission = admissionAt PeerDataVisibilityLevel.ViewOnly
      }
    | "model-execution/reject-narrowed" -> {
        referenceVectorState with
            Admission = narrowedAdmission
      }
    // Phase 643 — the bound vectors are read at `ViewOnly` and with the
    // view operations DECLARED, because both are preconditions of
    // reaching a bounds check at all. Read at the reference admission
    // they would be refused one check earlier, for a reason that has
    // nothing to do with what they are vectors for.
    | "model-execution/reject-view-over-bound-window"
    | "model-execution/reject-view-undeclared-series" -> {
        referenceVectorState with
            Admission =
                admissionAt PeerDataVisibilityLevel.ViewOnly
                |> ModelExecutionAdmission.withViews
      }
    // Phase 644 — the transition vectors are read with the operation
    // DECLARED and at the reference level, which is `AggregatesOnly`. The
    // second half of that is the claim: a transition carries no data, so
    // it needs no level above the floor, and a vector read at a raised
    // level would let an implementation that fused the two authority axes
    // pass.
    //
    // The scope holds nothing under the key this one names, which is the
    // whole of the unknown-artifact case.
    | "model-execution/reject-transition-unknown-artifact" -> {
        referenceVectorState with
            Admission = referenceAdmission |> ModelExecutionAdmission.withTransitions
            ArtifactStatus = None
      }
    // A FULL grant, so the refusal cannot be mistaken for an authority
    // one: this vector's whole content is that no grant makes a
    // terminal state leavable.
    | "model-execution/reject-transition-invalid" -> {
        referenceVectorState with
            Admission = referenceAdmission |> ModelExecutionAdmission.withTransitions
            ArtifactStatus = Some ModelArtifactStatus.Retired
            TransitionGrant = ModelTransitionAuthority.full
      }
    | "model-execution/reject-transition-unauthorized" -> {
        referenceVectorState with
            Admission = referenceAdmission |> ModelExecutionAdmission.withTransitions
      }
    // Phase 646 — the transfer vectors are read with the transfer
    // DECLARED and at the reference level, which is `AggregatesOnly`,
    // for the reason the transition vectors are: a transfer carries data
    // INBOUND and answers a receipt, so there is nothing for a
    // data-visibility level to govern and a vector read at a raised level
    // would let an implementation that fused the two authority axes pass.
    | "model-execution/reject-promotion-hash-mismatch" -> {
        referenceVectorState with
            Admission = referenceAdmission |> ModelExecutionAdmission.withPromotions
      }
    // A cap of ONE against a transfer carrying three (two records plus the
    // spec payload). The vector's whole content is that the bound is the
    // RECEIVER's declared one — nothing in the document is wrong, and a
    // deployment at the default cap answers the identical bytes.
    | "model-execution/reject-promotion-cap-exceeded" -> {
        referenceVectorState with
            Admission = referenceAdmission |> ModelExecutionAdmission.withPromotions
            AttachmentLimits = {
                ProvenanceAttachmentLimits.default' with
                    MaxAttachments = 1
            }
      }
    // The scope already holds this composite key, fit from different
    // parameters. A key names one artifact; two under one key would leave
    // every downstream citation ambiguous about which it meant.
    | "model-execution/reject-promotion-conflict" -> {
        referenceVectorState with
            Admission = referenceAdmission |> ModelExecutionAdmission.withPromotions
            Incumbent =
                Some {
                    CompositeKey = referencePromotedKey
                    ScopeId = modelExecutionBoundScope
                    ArtifactRef = {
                        ArtifactId = "artifact-8821"
                        ContentHash = "sha256:1111111111111111111111111111111111111111111111111111111111111111"
                        ByteLength = 4096L
                    }
                    Diagnostics = Map.empty
                    GateVerdicts = []
                    Status = ModelArtifactStatus.Fitted
                    Annotations = Map.empty
                    Notes = ""
                    Attachments = []
                    Signature = None
                    RegisteredBy = "local-fitter"
                    RegisteredAt = DateTimeOffset(2026, 7, 16, 9, 0, 0, TimeSpan.Zero)
                    Version = 1
                }
      }
    | _ -> referenceVectorState

/// One refusal per class the profile defines — so a modeller's mapping
/// is pinned by the corpus rather than inferred from the two classes it
/// happened to trip.
let private referenceRefusals: ModelExecutionPeerAnswer list =
    [
        ModelExecutionPeerRefusal.ProfileVersionUnsupported(2, 1)
        ModelExecutionPeerRefusal.RowAccessRefused "ReadPage"
        ModelExecutionPeerRefusal.UndeclaredDiagnostic "Leverage"
        ModelExecutionPeerRefusal.ScopeWideningRefused "other-tenant"
        ModelExecutionPeerRefusal.PeerUnbound "buyer-acme"
        ModelExecutionPeerRefusal.RequestUnreadable "unexpected end of JSON input"
        ModelExecutionPeerRefusal.SubmitterRefused(ModelExecutionRefusal.UnknownProvider "reference-regression")
        // Phase 642 — the authority family. Included here rather than in
        // a fixture of their own because this vector's contract is "one
        // answer per refusal class the profile defines", and a class
        // added to the profile that is not added here is a class a
        // modeller's mapping was never pinned against.
        ModelExecutionPeerRefusal.AuthorityLevelExceeded("RenderView", "ViewOnly", "AggregatesOnly")
        ModelExecutionPeerRefusal.AuthorityNarrowingRefused(
            "RenderView",
            "ViewOnly",
            "AggregatesOnly",
            "team:north-analysts"
        )
        ModelExecutionPeerRefusal.EgressWithheld "Coverage"
        // Phase 643 — the bounded-view family, carried through with its
        // own vocabulary nested inside the seam's passthrough case. All
        // eight are here for the reason the authority three are: the
        // vector's contract is one answer per class the profile defines,
        // and a class this list omits is one nobody's mapping was pinned
        // against.
        ModelExecutionPeerRefusal.ViewRefused(PeerViewRefusal.UndeclaredView "spend-by-region")
        ModelExecutionPeerRefusal.ViewRefused(PeerViewRefusal.UndeclaredSeries("spend-vs-response", "margin-per-unit"))
        ModelExecutionPeerRefusal.ViewRefused(PeerViewRefusal.NoSeriesRequested "spend-vs-response")
        ModelExecutionPeerRefusal.ViewRefused(PeerViewRefusal.SeriesBudgetExceeded("spend-vs-response", 3, 2))
        ModelExecutionPeerRefusal.ViewRefused(PeerViewRefusal.WindowUnordered "spend-vs-response")
        ModelExecutionPeerRefusal.ViewRefused(PeerViewRefusal.WindowBudgetExceeded("spend-vs-response", 365, 90))
        ModelExecutionPeerRefusal.ViewRefused(PeerViewRefusal.UndeclaredResolution("spend-vs-response", "hour"))
        ModelExecutionPeerRefusal.ViewRefused(PeerViewRefusal.RenderBudgetExhausted("spend-vs-response", 20, 3600))
        // Phase 644 — the transition family. Three, and all three, for
        // the reason the eight above are all here: the vector's contract
        // is one answer per class the profile defines.
        ModelExecutionPeerRefusal.TransitionRefused(
            ModelTransitionRefusal.UnknownArtifact "0000000000000000000000000000000000000000000000000000000000000000"
        )
        ModelExecutionPeerRefusal.TransitionRefused(
            ModelTransitionRefusal.InvalidTransition(referenceArtifactKey, "Retired", "Fitted")
        )
        ModelExecutionPeerRefusal.TransitionRefused(
            ModelTransitionRefusal.InsufficientAuthority(referenceArtifactKey, "Retired", "consortium-north/r.okafor")
        )
        // Phase 646 — the promotion family. FOUR entries for THREE reject
        // vectors, and the asymmetry is the point: a signing failure is a
        // property of the receiver's own arrangements rather than of any
        // document, so no vector can be built for it from bytes — and a
        // caller still has to enumerate the class. The lifecycle arm is
        // deliberately absent from this list: a promotion refused on the
        // edge or the grant carries §5.7.11's classes unchanged, which are
        // the three already above, and a fifth entry restating one of them
        // would suggest a class a modeller has to handle separately.
        ModelExecutionPeerRefusal.PromotionRefused(
            ModelPromotionRefusal.AttachmentRefused(
                referenceArtifactKey,
                ProvenanceAttachmentRefusal.HashMismatch(
                    "sha256:0000000000000000000000000000000000000000000000000000000000000000",
                    "sha256:9d3e1a55a4d4dd1b6b9f3d70e0e0a0e5bbd2f9b3d1c7a5f0e2b8c4d6a1937f5e"
                )
            )
        )
        ModelExecutionPeerRefusal.PromotionRefused(
            ModelPromotionRefusal.AttachmentRefused(
                referenceArtifactKey,
                ProvenanceAttachmentRefusal.CapExceeded("count", 3, 1)
            )
        )
        ModelExecutionPeerRefusal.PromotionRefused(
            ModelPromotionRefusal.PayloadConflict(referenceArtifactKey, "ArtifactRef.ContentHash")
        )
        ModelExecutionPeerRefusal.PromotionRefused(
            ModelPromotionRefusal.SigningFailed(referenceArtifactKey, "the signing key is unavailable")
        )
    ]
    |> List.map ModelExecutionPeerAnswer.Refused

/// The submitter's request envelope, built through the live constructor
/// so the profile version cannot drift out of the corpus.
let private submissionRequest () =
    ModelExecutionPeerContract.submissionRequest referenceSubmission

/// The answer envelope carrying the registered outcome — the fit's
/// terminal result as a modeller collects it.
let private outcomeAnswer () =
    ModelExecutionPeerAnswer.Answered(JsonRpc.serialize (ModelExecutionPeerContract.toWireOutcome referenceOutcome))

let private diagnosticAnswers () =
    referenceDiagnostics
    |> List.map (fun result -> ModelExecutionPeerAnswer.Answered(JsonRpc.serialize result))

/// A request naming a row-level read. The profile serves no such
/// surface, so the document exists only to be refused.
let private rowReadRequest () =
    ModelExecutionPeerContract.request "ReadPage" referenceVintage

/// A request naming a projection nobody declared. Declaration is the
/// only route onto the diagnostics surface.
let private undeclaredDiagnosticRequest () =
    ModelExecutionPeerContract.diagnosticRequest "Leverage" {
        Vintage = referenceVintage
        Terms = [ "promo"; "price" ]
    }

/// A well-formed request that asserts a scope other than the one this
/// peer binding addresses.
let private scopeWideningRequest () =
    ModelExecutionPeerContract.requestAsserting "other-tenant" "GetOutcome" referenceOutcome.CompositeKeyHash

/// Phase 642 — a bounded-view request. Well-formed, names an operation
/// the profile CLASSIFIES, and is refused at `AggregatesOnly` as an
/// authority question rather than as an unknown operation.
let private viewRequest () =
    ModelExecutionPeerContract.request "RenderView" referenceVintage

/// Phase 643 — the `RenderView` request envelope, built through the live
/// constructor so the ordinal series sort and the profile version cannot
/// drift out of the corpus.
let private renderViewRequest () =
    ModelExecutionPeerContract.viewRequest referenceViewRequest

/// Phase 643 — the three view answers a data host gives, in the order a
/// modeller meets them: the declared offer, one view's declaration, and
/// the rendered artifact.
///
/// `ListViews` goes through the live `PeerView.list`, so the fixture pins
/// the ordinal sort as well as the encoding. The deps it needs a reader
/// and a renderer for are stubs that are never reached — listing an offer
/// touches neither — and the render itself is a reference VALUE for the
/// reason stated beside it.
let private viewAnswers () =
    let deps: PeerViewDeps = {
        Declarations = fun _ -> async { return referenceViewDeclarations }
        ReadSeries = fun _ _ -> async { return [] }
        Renderer = {
            MediaType = "image/svg+xml"
            Render = fun _ -> Array.empty
        }
        Rate = PeerViewRateGuard.inProcess (fun () -> DateTimeOffset(2026, 7, 16, 10, 15, 0, TimeSpan.Zero))
    }

    let listed = PeerView.list deps "consortium-north" |> Async.RunSynchronously

    [
        ModelExecutionPeerAnswer.Answered(JsonRpc.serialize listed)
        ModelExecutionPeerAnswer.Answered(JsonRpc.serialize referenceViewDeclarations.Head)
        ModelExecutionPeerAnswer.Answered(JsonRpc.serialize referenceArtifact)
    ]

/// Phase 642 — a raw-series request, refused at `ViewOnly`. Deliberately
/// NOT one of the row-access probe names: those are refused identically
/// at every level because the profile serves no row surface at all,
/// whereas this names an operation the profile reserves to `Full`.
let private fullOnlyRequest () =
    ModelExecutionPeerContract.request "ReadVintageSeries" referenceVintage

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

        vector
            "peer-surface/authority-declared"
            "peer-surface"
            Participant
            Hash
            "The same deployment declaring a data-visibility authority level other than the default — the grant a counterparty pins before it calls. The instance vector above publishes the fail-closed `AggregatesOnly` because it declares nothing; this one shows a declared `ViewOnly`, so an implementation that hard-coded the default passes that vector and fails this."
            "peer-surface/authority-declared.json"
            (PeerSurface.exportJson (authoritySurface ()))

        vector
            "peer-surface/transition-grant"
            "peer-surface"
            Participant
            Hash
            "The same deployment declaring which registry lifecycle transitions it admits from a peer, at the DEFAULT data-visibility level. Two authority axes, and this vector is what separates them: an implementation that folded the transition grant into the visibility ladder would have to publish a raised level here, and does not. The grant is declared out of ordinal order and published sorted."
            "peer-surface/transition-grant.json"
            (PeerSurface.exportJson (transitionGrantSurface ()))

        // ── aggregate surface ─────────────────────────────────────────
        vector
            "aggregate-surface/group"
            "aggregate-surface"
            Gateway
            Hash
            "A three-member group fronting two contracts: posture floored across the exposing members (one facet divergent, reported as a sorted `mixed:` marker), vocabulary pins carried only on unanimity, the unexposed member contributing nothing, and `LongRunningEnabled` floored across the exposing members (every exposing member here dispatches long-running work, so the group does)."
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

        {
            vector
                "contract-invocation/reject-malformed"
                "contract-invocation"
                Participant
                Reject
                "A request envelope that is not well-formed. A receiver refuses it before dispatch as a decode failure rather than attempting a partial read of a call it cannot understand."
                "contract-invocation/reject-malformed.json"
                "{\"JsonRpc\":\"2.0\",\"Method\":\"PlaceOrder\",\"Params\":" with
                Reject = Some "invocation-unparseable"
        }

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

        // ── model execution ───────────────────────────────────────────
        vector
            "model-execution/submission"
            "model-execution"
            DataHost
            RoundTrip
            "A fit submission in the profile's versioned envelope: the vintage named scope-relatively, the opaque provider spec beside the submitter-minted hash the host stores verbatim, and the requested gates in ordinal order."
            "model-execution/submission.json"
            (JsonRpc.serialize (submissionRequest ()))

        vector
            "model-execution/outcome"
            "model-execution"
            DataHost
            RoundTrip
            "The answer envelope carrying a registered outcome — composite key, gate verdicts, aggregate diagnostics and artifact reference. Every member is metadata or an aggregate scalar; there is no member a row could ride in."
            "model-execution/outcome.json"
            (JsonRpc.serialize (outcomeAnswer ()))

        vector
            "model-execution/diagnostics"
            "model-execution"
            DataHost
            RoundTrip
            "The three declared governed diagnostics — collinearity, coverage and a transform preview — each an aggregate projection in the gate-checkable shape, so an answer that is not one is withheld rather than released."
            "model-execution/diagnostics.json"
            (JsonRpc.serialize (diagnosticAnswers ()))

        vector
            "model-execution/refusals"
            "model-execution"
            Modeller
            RoundTrip
            "One answer per refusal class the profile defines, including the submitter surface's own typed refusal carried through unchanged — so a modeller's mapping is pinned by the corpus rather than inferred from the classes it happened to trip."
            "model-execution/refusals.json"
            (JsonRpc.serialize referenceRefusals)

        vector
            "model-execution/view-request"
            "model-execution"
            Modeller
            RoundTrip
            "A bounded-view render request: the view (which binds the dataset, so the request cannot name one), the pinned version, the series in ordinal order, the window and the resolution. Every member is inside a bound the data host published, which is the only shape that gets answered."
            "model-execution/view-request.json"
            (JsonRpc.serialize (renderViewRequest ()))

        vector
            "model-execution/view"
            "model-execution"
            DataHost
            RoundTrip
            "The three answers of the bounded-view surface: the declared offer with its bounds, one view's declaration, and a rendered artifact. The artifact carries base64 bytes under a declared media type and a hash over them — there is no member a row, a series or a point could ride in, which is what makes a view not an export route."
            "model-execution/view.json"
            (JsonRpc.serialize (viewAnswers ()))

        vector
            "model-execution/transition-request"
            "model-execution"
            Modeller
            RoundTrip
            "A registry lifecycle transition invoked across the seam: the artifact key, the target status as its stable label, the calling deployment's own actor claim, and an optional rationale. No scope member and no role member — the binding decides the scope and the receiver's declared grant decides the authority, so neither can be widened by anything the caller sends."
            "model-execution/transition-request.json"
            (JsonRpc.serialize (ModelExecutionPeerContract.transitionRequest referenceTransitionInvocation))

        vector
            "model-execution/transition"
            "model-execution"
            DataHost
            RoundTrip
            "The attributed record an admitted transition produced: the edge it took, the channel it arrived on, the author that took it, and the artifact version it minted. Every member is metadata about a state change the data host has already committed — there is no member an artifact's parameters or any dataset row could ride in, which is why a transition needs no visibility level above the floor."
            "model-execution/transition.json"
            (JsonRpc.serialize (ModelExecutionPeerAnswer.Answered(JsonRpc.serialize referenceTransitionRecord)))

        // Phase 644 — the three transition reject vectors. Like the two
        // bound vectors below them, each is admitted by every envelope
        // check: current profile version, declared operation, no asserted
        // scope, and a granted `AggregatesOnly` (a transition needs no
        // more). They are refused by `ModelTransition.judge`, the pure
        // author-agnostic judge — a THIRD reader in the corpus, and the
        // one that decides identically for a local action and a policy
        // verdict.
        {
            vector
                "model-execution/reject-transition-unknown-artifact"
                "model-execution"
                DataHost
                Reject
                "An invocation naming an artifact this scope does not hold. Refused before the lifecycle graph is consulted at all: with no artifact there is no current status, and every later check needs one."
                "model-execution/reject-transition-unknown-artifact.json"
                (JsonRpc.serialize (ModelExecutionPeerContract.transitionRequest unknownArtifactInvocation)) with
                Reject = Some PeerTransition.UnknownArtifactClass
        }

        {
            vector
                "model-execution/reject-transition-invalid"
                "model-execution"
                DataHost
                Reject
                "An invocation asking a retired artifact to become fitted again. Judged against a FULL grant on purpose: no grant makes a terminal state leavable, so refusing this as an authority question would send the caller to negotiate for something no agreement can provide. Legality is judged before authority for exactly that reason."
                "model-execution/reject-transition-invalid.json"
                (JsonRpc.serialize (ModelExecutionPeerContract.transitionRequest invalidTransitionInvocation)) with
                Reject = Some PeerTransition.InvalidTransitionClass
        }

        {
            vector
                "model-execution/reject-transition-unauthorized"
                "model-execution"
                DataHost
                Reject
                "An invocation for a legal edge this peer's grant does not admit. The peer HOLDS a grant — it may approve — and this is the case that separates checking what a grant admits from checking whether one exists. The one refusal in the family whose remedy is a conversation between the two organisations, which is why it maps to `Forbidden` in the submitter face."
                "model-execution/reject-transition-unauthorized.json"
                (JsonRpc.serialize (ModelExecutionPeerContract.transitionRequest unauthorizedTransitionInvocation)) with
                Reject = Some PeerTransition.InsufficientAuthorityClass
        }

        vector
            "model-execution/promotion-request"
            "model-execution"
            Modeller
            RoundTrip
            "A promotion transfer: a final artifact's identity, the opaque spec payload it was fit from, its gate verdicts, and the opaque provenance records that justified it — each with a declared media type and a digest over its base64 content. Gate verdicts and attachments are declared out of ordinal order and cross sorted, because two builders promoting the same artifact must produce the same document or the transfer's idempotency claim means nothing. No scope member, no role member and no grant member: the receiver's own per-peer declaration decides the authority, so nothing a sender writes can widen it."
            "model-execution/promotion-request.json"
            (JsonRpc.serialize (ModelExecutionPeerContract.promotionRequest referencePromotionTransfer))

        vector
            "model-execution/promotion"
            "model-execution"
            DataHost
            RoundTrip
            "The receipt an accepted transfer produced: the status the artifact now holds, the digest of every attachment it carries — including the spec payload, which the receiver folds into the same append-only slot — and the data host's own detached signature over the canonical acceptance input. The signing-input digest is recomputed from that canonical form rather than quoted, because a verifier in another language has to rebuild exactly those bytes and it is the part of a signature an independent implementation can get wrong with no key at all."
            "model-execution/promotion.json"
            (JsonRpc.serialize (ModelExecutionPeerAnswer.Answered(JsonRpc.serialize referencePromotionRecord)))

        // Phase 646 — the three transfer reject vectors. Each is admitted
        // by every envelope check (current profile version, declared
        // operation, no asserted scope, a granted `AggregatesOnly`) and
        // refused by `ModelPromotion.judge` — a FOURTH reader in the
        // corpus, and one that reaches `ModelTransition.judge` for the
        // lifecycle half so a transfer and a bare transition cannot come
        // to disagree about whether a peer may approve something.
        {
            vector
                "model-execution/reject-promotion-hash-mismatch"
                "model-execution"
                DataHost
                Reject
                "A transfer whose attachment declares a digest its own bytes do not produce. The receiver is forbidden to read the content, so this is the ONE integrity claim it can make about it — and it is checked before the cap, before identity and before the lifecycle, because a payload that did not survive transport is not a question about size or authority and answering it as one would send the sender to fix the wrong thing."
                "model-execution/reject-promotion-hash-mismatch.json"
                (JsonRpc.serialize (ModelExecutionPeerContract.promotionRequest hashMismatchTransfer)) with
                Reject = Some PeerPromotion.HashMismatchClass
        }

        {
            vector
                "model-execution/reject-promotion-cap-exceeded"
                "model-execution"
                DataHost
                Reject
                "The reference transfer verbatim, against a receiver whose declared attachment cap is one. Read closely: nothing in the document distinguishes it from a transfer that would be accepted, and a deployment at the default cap answers these exact bytes. The bound is the RECEIVER's, which is why it is published (§5.7.12) rather than discovered by hitting it."
                "model-execution/reject-promotion-cap-exceeded.json"
                (JsonRpc.serialize (ModelExecutionPeerContract.promotionRequest referencePromotionTransfer)) with
                Reject = Some PeerPromotion.CapExceededClass
        }

        {
            vector
                "model-execution/reject-promotion-conflict"
                "model-execution"
                DataHost
                Reject
                "The same document again, against a scope that already holds this composite key with a different fitted artifact. A key names ONE artifact — it is the hash of the spec, vintage, seed and provider that produced it — so two transfers under one key disagree about something the key asserts they share, and reconciling that quietly would leave every downstream citation ambiguous about which it meant. This is also what makes the family idempotent rather than merely tolerant: the IDENTICAL transfer at this key is accepted and writes nothing."
                "model-execution/reject-promotion-conflict.json"
                (JsonRpc.serialize (ModelExecutionPeerContract.promotionRequest referencePromotionTransfer)) with
                Reject = Some PeerPromotion.ConflictClass
        }

        {
            vector
                "model-execution/reject-row-read"
                "model-execution"
                DataHost
                Reject
                "A request naming a row-level read. The profile serves no row-level surface, and the refusal names that specifically rather than reporting an unrecognised operation — a probe and a typo are different things to find in a log."
                "model-execution/reject-row-read.json"
                (JsonRpc.serialize (rowReadRequest ())) with
                Reject = Some "model-execution-row-read-refused"
        }

        {
            vector
                "model-execution/reject-undeclared-diagnostic"
                "model-execution"
                DataHost
                Reject
                "A request naming a projection this deployment has not declared. Declaration is the only route onto the diagnostics surface, so the refusal happens before anything is computed."
                "model-execution/reject-undeclared-diagnostic.json"
                (JsonRpc.serialize (undeclaredDiagnosticRequest ())) with
                Reject = Some "model-execution-undeclared-diagnostic"
        }

        {
            vector
                "model-execution/reject-scope-widening"
                "model-execution"
                DataHost
                Reject
                "A well-formed request asserting a scope other than the one the peer binding addresses. The host never routes on an asserted scope; it refuses a disagreement, which is the difference between a diagnostic aid and an impersonation vector."
                "model-execution/reject-scope-widening.json"
                (JsonRpc.serialize (scopeWideningRequest ())) with
                Reject = Some "model-execution-scope-widening"
        }

        {
            vector
                "model-execution/reject-malformed"
                "model-execution"
                DataHost
                Reject
                "A truncated request envelope. It is refused whole rather than read as far as it parses — a member the reader has no value for would satisfy an admission check by omission."
                "model-execution/reject-malformed.json"
                "{\"ProfileVersion\":1,\"Operation\":\"GetOutcome\"," with
                Reject = Some "model-execution-request-unreadable"
        }

        {
            vector
                "model-execution/reject-view-at-aggregates"
                "model-execution"
                DataHost
                Reject
                "A bounded-view request against a peer granted aggregates only. Refused as an AUTHORITY question, not as an unknown operation: the deployment implements the classification, it has not granted it, and the two have different remedies — one is a phone call, the other is abandoning the call."
                "model-execution/reject-view-at-aggregates.json"
                (JsonRpc.serialize (viewRequest ())) with
                Reject = Some "model-execution-authority-level-exceeded"
        }

        {
            vector
                "model-execution/reject-full-at-view"
                "model-execution"
                DataHost
                Reject
                "A raw-series request against a peer granted bounded views. The same refusal class one level up, which is what makes the levels a ladder rather than a pair of special cases — and the vector is deliberately not a row-access probe, because those are refused identically at every level."
                "model-execution/reject-full-at-view.json"
                (JsonRpc.serialize (fullOnlyRequest ())) with
                Reject = Some "model-execution-authority-level-exceeded"
        }

        {
            vector
                "model-execution/reject-narrowed"
                "model-execution"
                DataHost
                Reject
                "The identical bounded-view document against a peer whose CEILING admits it and whose team-scope narrowing does not. Read closely: nothing in the document distinguishes it from a request that would be answered, and the refusal names the narrowing layer — a ceiling refusal is a question for the two organisations, a narrowing refusal is a question for one deployment's own configuration."
                "model-execution/reject-narrowed.json"
                (JsonRpc.serialize (viewRequest ())) with
                Reject = Some "model-execution-authority-narrowed"
        }

        // Phase 643 — the two bound vectors. Both are admitted by every
        // check the authority vectors above test: the profile version is
        // current, the operation is declared, the scope is not asserted,
        // and the peer is granted `ViewOnly`. They are refused by the
        // VIEW's own declared bounds, which is a different reader — the
        // one a data host must run after admission and before it reads
        // anything. A harness that stopped at the envelope certifies the
        // three above and neither of these.
        {
            vector
                "model-execution/reject-view-over-bound-window"
                "model-execution"
                DataHost
                Reject
                "A render request covering a wider window than the view declares. Well-formed, granted, and refused on the one thing wrong with it — the class names the bound and the description carries the declared limit, so a modeller narrows and re-sends rather than guessing."
                "model-execution/reject-view-over-bound-window.json"
                (JsonRpc.serialize (ModelExecutionPeerContract.viewRequest overBoundWindowRequest)) with
                Reject = Some "model-execution-view-window-budget"
        }

        {
            vector
                "model-execution/reject-view-undeclared-series"
                "model-execution"
                DataHost
                Reject
                "A render request naming a series the view does not carry. Refused before anything is read: the declaration is the whole offer, so a series absent from it is one nobody agreed to render — and the refusal names only what the caller itself sent."
                "model-execution/reject-view-undeclared-series.json"
                (JsonRpc.serialize (ModelExecutionPeerContract.viewRequest undeclaredSeriesRequest)) with
                Reject = Some "model-execution-view-series-undeclared"
        }
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

    let profiles = [ Participant; Gateway; ModuleHost; DataHost; Modeller ]

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
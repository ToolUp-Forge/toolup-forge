// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text
open System.Text.Json
open System.Security.Cryptography
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.ArtefactSigning

// ─── Grounding certificates (Phase 565) ──────────────────────────────
//
// A **grounding certificate** seals an answer's provenance chain (Phase
// 524) into a signed, third-party-checkable artifact: "this number came
// from these facts, computed by these methods, under these disclosure
// policies" — verifiable without access to the deployment ("proof-carrying
// data"). It rides the shipped artefact-signing substrate (Phase 40's
// `IArtefactSigner` / `IArtefactVerifier`, GP 1): the certificate body is
// a canonical, versioned JSON shape; the seal is a detached JWS over its
// exact bytes; verification needs only the certificate + the deployment's
// public key (no store access, offline-checkable).
//
// **Selective, by construction + by predicate.** The certificate discloses
// chain *structure* — node ids, kinds, method identities, disclosure
// stances, policy refs, and per-node hashes — and never a fact's *value*
// (the chain carries no values in the first place). Beyond that structural
// guarantee, every fact node passes the one disclosure predicate (Phase
// 525) at the `FactExport` egress surface, like any other egress: a
// **disclosable** fact contributes its method identity (and, for an
// imported fact, its deployment-independent certificate ref); a **withheld**
// fact collapses to id + policy ref + stance only, so even the method that
// produced it (which can name a principal) is redacted. Disclosure applies
// to certificate content exactly as it applies at every other door.
//
// **Open interchange format.** The body shape (`GroundingCertificateBody`)
// is a stable, versioned wire type (`Format = "grounding-certificate/v1"`),
// so a verifying party — a regulator, an auditor, an operated trust anchor
// — validates it with the public key alone. The anchor needs no forge
// closure: the format is open and the anchor is a key registry.

/// What a certificate is issued over. Identity by value (GP 12):
///  - `AnswerCertificate` — a grounded conversation message plus the fact
///    ids the answer cited; the chain is the answer's upstream (message →
///    plan → facts → results → data), plus any recorded answer plans.
///  - `FactCertificate` — a single fact id; the chain is that fact's
///    upstream lineage.
type CertificateSubject =
    | AnswerCertificate of messageId: string * citedFactIds: string list
    | FactCertificate of factId: string

/// One node in a sealed grounding certificate — a provenance-chain node,
/// projected to structure only. For a **disclosable** fact node `Method`
/// carries its method identity (and `CertificateRef` an imported fact's
/// deployment-independent cert ref); for a **withheld** fact node those are
/// `None` and `Withheld` is `true` — only `Id` + `Disclosure` (the stance /
/// policy ref) survive. Non-fact nodes carry neither. A fact's *value*
/// never appears on any node (the certificate is structure, not content).
type CertificateNode = {
    /// Underlying store identity (fact id, result id, data-object version,
    /// message id, answer-plan id).
    Id: string
    /// Canonical node-kind string (`DataObjectVersion` / `AnalysisResult` /
    /// `Fact` / `NarrativeDocument` / `ConversationMessage` / `AnswerPlan`).
    Kind: string
    /// Disclosure stance for a fact node (`Surfaceable` / `Internal` /
    /// `Restricted(policy)`); `None` for non-fact nodes.
    Disclosure: string option
    /// Method identity of a **disclosable** fact node (`computed:op:ver:hash`
    /// / `asserted:principal` / `imported:cert`). `None` for a non-fact node
    /// or a withheld fact (whose method could itself leak identity).
    Method: string option
    /// An imported fact's own certificate ref — its deployment-independent
    /// identity (Phase 520 `Imported`). `None` unless the disclosable fact's
    /// method is `Imported`.
    CertificateRef: string option
    /// SHA-256 over the node's certificate representation — a per-node
    /// tamper anchor a verifier can spot-check (the whole body is signed
    /// too).
    Hash: string
    /// `true` when the disclosure predicate withheld this fact's structure
    /// (only id + policy ref + stance are present).
    Withheld: bool
}

/// A directed edge in a sealed grounding certificate — the same shape a
/// `ProvenanceEdge` carries, with the edge kind rendered to its canonical
/// string.
type CertificateEdge = {
    From: string
    To: string
    /// Canonical edge-kind string (`DerivedFrom` / `EvidenceFor` /
    /// `CitesFact` / `Supersedes` / `PlannedBy`).
    Kind: string
}

/// The canonical, versioned certificate body — the exact shape the detached
/// JWS is computed over. Immutable value type (GP 5); its lists are
/// deterministically ordered by `GroundingCertificate.canonicalise` so the
/// signed bytes are reproducible.
type GroundingCertificateBody = {
    /// Format discriminator — the open interchange version string.
    Format: string
    /// The answer / fact id this certificate is issued over (the chain
    /// root).
    Root: string
    /// When the certificate was issued (UTC).
    IssuedAt: DateTimeOffset
    /// The deployment signing-key id — the anchor a verifier resolves the
    /// public key for. Bound into the signed body, not only the signature.
    DeploymentKeyId: string
    /// The chain's nodes, structure only, ordered canonically.
    Nodes: CertificateNode list
    /// The chain's edges, ordered canonically.
    Edges: CertificateEdge list
    /// The distinct disclosure policy refs the certificate was sealed under
    /// — one per withheld node's policy, sorted. Empty when nothing was
    /// withheld.
    PolicyRefs: string list
}

/// A sealed grounding certificate — the canonical body plus the detached
/// JWS over its exact bytes. Verifiable offline against the deployment's
/// public key (`GroundingCertificate.verify`).
type GroundingCertificate = {
    Body: GroundingCertificateBody
    Signature: ArtefactSignature
}

/// Why an issue failed.
type CertificateError =
    /// No `IArtefactSigner` is composed — a deployment without the signing
    /// substrate cannot issue certificates (GP 13; issuance refuses, never
    /// throws).
    | SigningUnavailable
    /// The signer returned an error (`SigningError.describe`).
    | SigningFailed of reason: string
    /// The subject resolved to an empty provenance chain — nothing to
    /// certify.
    | EmptyChain

module CertificateError =
    let describe =
        function
        | SigningUnavailable -> "no signing substrate composed (IArtefactSigner); certificates cannot be issued"
        | SigningFailed reason -> sprintf "signing failed: %s" reason
        | EmptyChain -> "the subject resolved to an empty provenance chain; nothing to certify"

/// Server-side issue surface (Phase 565.C). Issues a signed certificate
/// over an answer or a fact id; a deployment without a composed
/// `IArtefactSigner` refuses with `SigningUnavailable` (GP 13). Registered
/// in DI by `FactsCompose.withFactStore` whenever the fact store is
/// composed.
type IGroundingCertificateIssuer =
    abstract Issue:
        scopeId: string * principal: string * subject: CertificateSubject * depth: int ->
            Async<Result<GroundingCertificate, CertificateError>>

module GroundingCertificate =

    /// The open interchange format version. Do not change without a
    /// compatibility note — a verifier keys on it.
    [<Literal>]
    let Format = "grounding-certificate/v1"

    let private jsonOptions = FableConverters.create ()

    let private sha256Hex (s: string) : string =
        use sha = SHA256.Create()

        sha.ComputeHash(Encoding.UTF8.GetBytes s)
        |> Array.map (sprintf "%02x")
        |> String.concat ""

    /// Canonical node-kind string. Total over `ProvenanceNodeKind`.
    let private nodeKindString (k: ProvenanceNodeKind) : string =
        match k with
        | DataObjectVersion -> "DataObjectVersion"
        | AnalysisResult -> "AnalysisResult"
        | FactNode -> "Fact"
        | NarrativeDocument -> "NarrativeDocument"
        | ConversationMessage -> "ConversationMessage"
        | AnswerPlanNode -> "AnswerPlan"
        // Phase 646 — a promoted model artifact and the opaque provenance
        // records it carries. A certificate that names one of these is
        // making the claim the promotion transfer exists to support: the
        // number was produced by THIS artifact, whose spec payload and
        // exploration record resolve from this deployment's own stores,
        // with no reference to the deployment that fitted it.
        | ModelArtifactNode -> "ModelArtifact"
        | ProvenanceAttachmentNode -> "ProvenanceAttachment"

    /// Canonical edge-kind string. Total over `ProvenanceEdgeKind`.
    let private edgeKindString (k: ProvenanceEdgeKind) : string =
        match k with
        | DerivedFrom -> "DerivedFrom"
        | EvidenceFor -> "EvidenceFor"
        | CitesFact -> "CitesFact"
        | Supersedes -> "Supersedes"
        | PlannedBy -> "PlannedBy"
        | HasAttachment -> "HasAttachment"

    let private nodeHash
        (id: string)
        (kind: string)
        (disclosure: string option)
        (method: string option)
        (certRef: string option)
        (withheld: bool)
        : string =
        [
            id
            kind
            defaultArg disclosure ""
            defaultArg method ""
            defaultArg certRef ""
            (if withheld then "withheld" else "disclosed")
        ]
        |> String.concat "|"
        |> sha256Hex

    /// Deterministically order a body's lists so the signed bytes are
    /// reproducible (nodes by kind+id, edges by kind+from+to, policy refs
    /// distinct+sorted). Idempotent — verification re-canonicalises before
    /// re-serialising, so a body signed here canonicalises to itself.
    let canonicalise (body: GroundingCertificateBody) : GroundingCertificateBody = {
        body with
            Nodes = body.Nodes |> List.sortBy (fun n -> n.Kind, n.Id)
            Edges = body.Edges |> List.sortBy (fun e -> e.Kind, e.From, e.To)
            PolicyRefs = body.PolicyRefs |> List.distinct |> List.sort
    }

    /// The exact bytes the detached JWS is computed over — canonical JSON of
    /// the (canonicalised) body. Both issue and verify go through this, so
    /// the byte stream matches on both sides.
    let canonicalBytes (body: GroundingCertificateBody) : byte[] =
        JsonSerializer.Serialize(canonicalise body, jsonOptions)
        |> Encoding.UTF8.GetBytes

    /// Verify a certificate **offline** (Phase 565.C): needs only the
    /// certificate and a verifier that can resolve the public key for the
    /// signature's key id — no fact store, no deployment access. `Ok ()`
    /// means every byte of the body is intact and the key was discoverable;
    /// any change to the body fails as `Tampered`.
    let verify
        (verifier: IArtefactVerifier)
        (certificate: GroundingCertificate)
        : Async<Result<unit, VerificationError>> =
        verifier.Verify(canonicalBytes certificate.Body, certificate.Signature)

    // ── issuer ──────────────────────────────────────────────────────────

    /// The one issuer implementation. Materialises the subject's provenance
    /// chain (Phase 524), applies the disclosure predicate to fact nodes at
    /// the `FactExport` surface (Phase 525), projects to a structure-only
    /// body, and seals it with the composed `IArtefactSigner`.
    type private DefaultIssuer
        (
            graph: IProvenanceGraph,
            store: IFactStore,
            gate: IFactDisclosureGate,
            events: IEventStore,
            signer: IArtefactSigner option,
            clock: unit -> DateTime
        ) =

        // Materialise the chain for the subject. For an answer, the recorded
        // answer plan(s) (Phase 560) are stitched in as `AnswerPlanNode`s
        // with `PlannedBy` / `CitesFact` edges, and both the answer's own
        // citations and the plans' cited facts are walked upstream.
        let materialise (scopeId: string) (subject: CertificateSubject) (depth: int) : Async<string * ProvenanceChain> = async {
            match subject with
            | FactCertificate factId ->
                let! chain = graph.GetChain(scopeId, FactRef factId, Upstream, depth)
                return factId, chain
            | AnswerCertificate(messageId, citedFactIds) ->
                let! plans = AnswerPlanProvenance.recordedFor events scopeId messageId
                let planCited = plans |> List.collect AnswerPlan.citedFactIds
                let allCited = (citedFactIds @ planCited) |> List.distinct
                let! baseChain = graph.GetChainForMessage(scopeId, messageId, allCited, depth)

                let planNodes: ProvenanceNode list =
                    plans
                    |> List.map (fun plan -> {
                        Id = plan.PlanId
                        Kind = AnswerPlanNode
                        Disclosure = None
                        Label = sprintf "answer plan: %s" plan.Question
                    })

                let planEdges: ProvenanceEdge list =
                    plans
                    |> List.collect (fun plan ->
                        {
                            From = messageId
                            To = plan.PlanId
                            Kind = PlannedBy
                        }
                        :: (AnswerPlan.citedFactIds plan
                            |> List.map (fun factId -> {
                                From = plan.PlanId
                                To = factId
                                Kind = CitesFact
                            })))

                let chain = {
                    baseChain with
                        Nodes = planNodes @ baseChain.Nodes
                        Edges = planEdges @ baseChain.Edges
                }

                return messageId, chain
        }

        // Project one provenance node to a certificate node, consulting the
        // disclosure verdicts for fact nodes. Returns the node + the policy
        // ref it was withheld under (when any), so the caller can collect
        // the sealed-under policy set.
        let projectNode
            (scopeId: string)
            (verdicts: Map<string, FactDisclosureVerdict>)
            (node: ProvenanceNode)
            : Async<CertificateNode * string option> =
            async {
                let kind = nodeKindString node.Kind

                match node.Kind with
                | FactNode ->
                    match verdicts.TryFind node.Id with
                    | Some FactDisclosable ->
                        let! fact = store.Get(scopeId, node.Id)

                        let method, certRef, stance =
                            match fact with
                            | Some f ->
                                let certRef =
                                    match f.Method with
                                    | Imported cert -> Some cert
                                    | _ -> None

                                Some(Fact.methodIdentity f.Method), certRef, Some(Disclosure.toString f.Disclosure)
                            // Verdict said disclosable but the id no longer
                            // resolves — degrade to structure only, never
                            // fabricate a method.
                            | None -> None, None, node.Disclosure

                        return
                            {
                                Id = node.Id
                                Kind = kind
                                Disclosure = stance |> Option.orElse node.Disclosure
                                Method = method
                                CertificateRef = certRef
                                Hash =
                                    nodeHash node.Id kind (stance |> Option.orElse node.Disclosure) method certRef false
                                Withheld = false
                            },
                            None
                    | verdict ->
                        // Withheld: id + policy ref + stance only. The
                        // method (which can name a principal) is redacted.
                        let policyRef =
                            match verdict with
                            | Some(FactNotDisclosable p) -> p
                            | _ -> "unknown-fact"

                        return
                            {
                                Id = node.Id
                                Kind = kind
                                Disclosure = node.Disclosure
                                Method = None
                                CertificateRef = None
                                Hash = nodeHash node.Id kind node.Disclosure None None true
                                Withheld = true
                            },
                            Some policyRef
                | _ ->
                    return
                        {
                            Id = node.Id
                            Kind = kind
                            Disclosure = None
                            Method = None
                            CertificateRef = None
                            Hash = nodeHash node.Id kind None None None false
                            Withheld = false
                        },
                        None
            }

        interface IGroundingCertificateIssuer with
            member _.Issue(scopeId, principal, subject, depth) = async {
                match signer with
                | None -> return Error SigningUnavailable
                | Some signer ->
                    let! root, chain = materialise scopeId subject depth

                    if List.isEmpty chain.Nodes then
                        return Error EmptyChain
                    else
                        // The disclosure predicate over the fact nodes at
                        // the export egress surface (Phase 525): one gate
                        // call, fact ids only.
                        let factIds =
                            chain.Nodes
                            |> List.choose (fun n ->
                                match n.Kind with
                                | FactNode -> Some n.Id
                                | _ -> None)

                        let! verdicts =
                            if List.isEmpty factIds then
                                async { return Map.empty }
                            else
                                gate.Check(scopeId, principal, FactExport, factIds)

                        let! projected = chain.Nodes |> List.map (projectNode scopeId verdicts) |> Async.Parallel

                        let nodes = projected |> Array.map fst |> Array.toList

                        let policyRefs =
                            projected |> Array.choose snd |> Array.toList |> List.distinct |> List.sort

                        let edges =
                            chain.Edges
                            |> List.map (fun e -> {
                                From = e.From
                                To = e.To
                                Kind = edgeKindString e.Kind
                            })

                        let body =
                            canonicalise {
                                Format = Format
                                Root = root
                                IssuedAt = DateTimeOffset(DateTime.SpecifyKind(clock (), DateTimeKind.Utc))
                                DeploymentKeyId = signer.KeyId()
                                Nodes = nodes
                                Edges = edges
                                PolicyRefs = policyRefs
                            }

                        match! signer.Sign(canonicalBytes body) with
                        | Error e -> return Error(SigningFailed(SigningError.describe e))
                        | Ok signature -> return Ok { Body = body; Signature = signature }
            }

    /// Construct the issuer over the composed collaborators. `signer` is
    /// `None` when no `IArtefactSigner` is composed — issuance then refuses
    /// with `SigningUnavailable` (GP 13). `clock` stamps `IssuedAt`.
    let createIssuerWithClock
        (graph: IProvenanceGraph)
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (events: IEventStore)
        (signer: IArtefactSigner option)
        (clock: unit -> DateTime)
        : IGroundingCertificateIssuer =
        DefaultIssuer(graph, store, gate, events, signer, clock) :> IGroundingCertificateIssuer

    /// The issuer with a UTC wall-clock — the composition default.
    let createIssuer
        (graph: IProvenanceGraph)
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (events: IEventStore)
        (signer: IArtefactSigner option)
        : IGroundingCertificateIssuer =
        createIssuerWithClock graph store gate events signer (fun () -> DateTime.UtcNow)
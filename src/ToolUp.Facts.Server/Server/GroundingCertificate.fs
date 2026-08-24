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

// ─── Certificates on the application signing seam ────────────────────────
//
// The certificate above signs through `IArtefactSigner` — the byte-level
// primitive — because that was the only signing surface when it was
// written. The signature is sound, and it leaves one thing unstated that a
// relying party needs: what the signing key's custody entitles the
// signature to CLAIM. "Valid provenance, signed by a key that may have sat
// in a file beside the process" and "valid provenance, signed by a key the
// signing host could not read" are different assertions, and a signature
// over bytes alone cannot tell them apart.
//
// The application signing seam carries that claim and binds it into the
// signed bytes. The types below are the certificate issued through it.
// They are ADDITIVE: `GroundingCertificate`, its issuer, its verifier and
// its wire format are untouched, and a deployment composing the direct
// path is byte-for-byte what it was (GP 11). Both paths seal the SAME
// canonical body over the SAME bytes, so the interchange format has not
// forked — what differs is only what travels alongside the signature.

/// A grounding certificate sealed through the application signing seam.
///
/// The body is the identical `GroundingCertificateBody` the direct path
/// produces, canonicalised the same way and signed over the same bytes.
/// What changes is the seal: an envelope carrying the purpose the
/// certificate was signed AS and the attestation level the signature
/// claims, both framed into the signed bytes rather than recorded beside
/// them.
///
/// **Key id and level are both inside the signature.** The key id is in
/// the body (`DeploymentKeyId`), which is the signed payload; the level is
/// in the seam's framing. Editing either fails verification, so neither is
/// a label a holder has to take on trust.
type AttestedGroundingCertificate = {
    Body: GroundingCertificateBody
    Envelope: SignedPayloadEnvelope
}

/// What verifying an attested certificate found.
///
/// The subject case is separate from the rejection case on purpose. "A
/// correctly-signed certificate about a different answer" and "this does
/// not verify" send a holder to entirely different places — the first is a
/// filing error somewhere upstream, the second is tampering — and a
/// verdict that flattened them would send half of its readers to the wrong
/// one.
type AttestedCertificateVerdict =
    /// The signature verifies and, where a root was expected, the
    /// certificate is about it.
    | AttestedCertificateValid
    /// The application signing seam refused: a purpose replay, a revoked
    /// key, or a signature that does not verify over the body.
    | AttestedCertificateRejected of PayloadVerificationError
    /// Correctly signed, and about something else. Reachable only AFTER
    /// the signature verified — see `verifyAttestedFor`.
    | AttestedCertificateSubjectMismatch of expected: string * actual: string

module AttestedCertificateVerdict =
    let describe =
        function
        | AttestedCertificateValid -> "certificate verified"
        | AttestedCertificateRejected e -> PayloadVerificationError.describe e
        | AttestedCertificateSubjectMismatch(expected, actual) ->
            $"certificate is validly signed but issued over '{actual}', not '{expected}'"

/// Issues certificates sealed through the application signing seam. A
/// deployment with no composed `IApplicationSigner` refuses with
/// `SigningUnavailable`, exactly as the direct issuer does with no
/// `IArtefactSigner` (GP 13).
type IAttestedGroundingCertificateIssuer =
    abstract Issue:
        scopeId: string * principal: string * subject: CertificateSubject * depth: int ->
            Async<Result<AttestedGroundingCertificate, CertificateError>>

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

    /// Everything an issuer does BEFORE it signs: materialises the
    /// subject's provenance chain (Phase 524), applies the disclosure
    /// predicate to fact nodes at the `FactExport` surface (Phase 525), and
    /// projects it to a structure-only canonical body.
    ///
    /// Shared by both issue paths, and shared rather than duplicated for a
    /// reason that outlives the convenience: the disclosure projection is
    /// the part of certificate issuance that can leak, so two copies of it
    /// would be two places a withheld fact's method could start surviving
    /// into a certificate. One body builder, two seals.
    type private CertificateBuilder
        (
            graph: IProvenanceGraph,
            store: IFactStore,
            gate: IFactDisclosureGate,
            events: IEventStore,
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

        /// The canonical body for `subject`, ready to seal. `keyId` is the
        /// id the sealing key will sign under, bound into the body so it is
        /// covered by whichever signature follows.
        member _.BuildBody
            (scopeId: string, principal: string, subject: CertificateSubject, depth: int, keyId: string)
            : Async<Result<GroundingCertificateBody, CertificateError>> =
            async {
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

                    return
                        Ok(
                            canonicalise {
                                Format = Format
                                Root = root
                                IssuedAt = DateTimeOffset(DateTime.SpecifyKind(clock (), DateTimeKind.Utc))
                                DeploymentKeyId = keyId
                                Nodes = nodes
                                Edges = edges
                                PolicyRefs = policyRefs
                            }
                        )
            }

    /// The direct-path issuer: seals the built body with the composed
    /// `IArtefactSigner`. Behaviour is unchanged from before the body
    /// builder was extracted — same body, same bytes, same signature.
    type private DefaultIssuer(builder: CertificateBuilder, signer: IArtefactSigner option) =

        interface IGroundingCertificateIssuer with
            member _.Issue(scopeId, principal, subject, depth) = async {
                match signer with
                | None -> return Error SigningUnavailable
                | Some signer ->
                    match! builder.BuildBody(scopeId, principal, subject, depth, signer.KeyId()) with
                    | Error e -> return Error e
                    | Ok body ->
                        match! signer.Sign(canonicalBytes body) with
                        | Error e -> return Error(SigningFailed(SigningError.describe e))
                        | Ok signature -> return Ok { Body = body; Signature = signature }
            }

    /// The attested issuer: seals the built body through the application
    /// signing seam, so the purpose and the signer's attestation level are
    /// framed into the signed bytes alongside the body.
    type private AttestedIssuer(builder: CertificateBuilder, signer: IApplicationSigner option) =

        interface IAttestedGroundingCertificateIssuer with
            member _.Issue(scopeId, principal, subject, depth) = async {
                match signer with
                | None -> return Error SigningUnavailable
                | Some signer ->
                    match! builder.BuildBody(scopeId, principal, subject, depth, signer.ActiveKeyId()) with
                    | Error e -> return Error e
                    | Ok body ->
                        match! signer.SignPayload(Format, canonicalBytes body) with
                        | Error e -> return Error(SigningFailed(SigningError.describe e))
                        | Ok envelope -> return Ok { Body = body; Envelope = envelope }
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
        DefaultIssuer(CertificateBuilder(graph, store, gate, events, clock), signer) :> IGroundingCertificateIssuer

    /// The issuer with a UTC wall-clock — the composition default.
    let createIssuer
        (graph: IProvenanceGraph)
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (events: IEventStore)
        (signer: IArtefactSigner option)
        : IGroundingCertificateIssuer =
        createIssuerWithClock graph store gate events signer (fun () -> DateTime.UtcNow)

    // ── the attested path ───────────────────────────────────────────────

    /// The purpose an attested certificate is signed as. Deliberately the
    /// format discriminator itself: the purpose exists to stop a signature
    /// minted for one kind of payload being replayed as another, and the
    /// format string is precisely the name of this kind of payload.
    /// Inventing a second string would be one more thing that could drift
    /// out of step with the first.
    let AttestationPurpose = Format

    /// Construct the attested issuer. `signer` is `None` when no
    /// `IApplicationSigner` is composed — issuance then refuses with
    /// `SigningUnavailable` (GP 13). `clock` stamps `IssuedAt`.
    let createAttestedIssuerWithClock
        (graph: IProvenanceGraph)
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (events: IEventStore)
        (signer: IApplicationSigner option)
        (clock: unit -> DateTime)
        : IAttestedGroundingCertificateIssuer =
        AttestedIssuer(CertificateBuilder(graph, store, gate, events, clock), signer)
        :> IAttestedGroundingCertificateIssuer

    /// The attested issuer with a UTC wall-clock — the composition default.
    let createAttestedIssuer
        (graph: IProvenanceGraph)
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (events: IEventStore)
        (signer: IApplicationSigner option)
        : IAttestedGroundingCertificateIssuer =
        createAttestedIssuerWithClock graph store gate events signer (fun () -> DateTime.UtcNow)

    /// Verify an attested certificate's seal.
    ///
    /// Covers, in the seam's own order: the envelope was minted as a
    /// grounding certificate and not replayed from another use; the signing
    /// key has not been revoked; the body's exact bytes verify under the
    /// key the envelope NAMES — which may be a key that has since been
    /// rotated out, and still verifies, because the key that signed is
    /// resolved by id rather than assumed to be the active one. That last
    /// property is what makes a certificate outlive a rotation.
    ///
    /// This says nothing about WHICH answer the certificate is about. When
    /// the caller holds an expectation, use `verifyAttestedFor`.
    let verifyAttested
        (signer: IApplicationSigner)
        (certificate: AttestedGroundingCertificate)
        : Async<AttestedCertificateVerdict> =
        async {
            match! signer.VerifyPayload(AttestationPurpose, canonicalBytes certificate.Body, certificate.Envelope) with
            | Ok() -> return AttestedCertificateValid
            | Error e -> return AttestedCertificateRejected e
        }

    /// Verify an attested certificate against the root the caller
    /// independently holds.
    ///
    /// **The signature is checked FIRST, then the subject**, and the order
    /// is load-bearing rather than incidental. `AttestedCertificateSubjectMismatch`
    /// claims "a correctly-signed certificate about a different answer" —
    /// a claim that would be false if the root were compared before anyone
    /// established the certificate was signed at all. A holder told their
    /// certificate is about the wrong answer draws a very different
    /// conclusion from one told it does not verify, and an unsigned
    /// document can be made to say anything about anything.
    let verifyAttestedFor
        (signer: IApplicationSigner)
        (expectedRoot: string)
        (certificate: AttestedGroundingCertificate)
        : Async<AttestedCertificateVerdict> =
        async {
            match! verifyAttested signer certificate with
            | AttestedCertificateValid ->
                if certificate.Body.Root = expectedRoot then
                    return AttestedCertificateValid
                else
                    return AttestedCertificateSubjectMismatch(expectedRoot, certificate.Body.Root)
            | verdict -> return verdict
        }
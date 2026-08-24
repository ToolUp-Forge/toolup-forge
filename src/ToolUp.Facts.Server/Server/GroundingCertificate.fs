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

// ─── Issuance transparency (Phase 685) ───────────────────────────────────
//
// Both issuers above produce a document that verifies in the holder's hand
// and is otherwise unlisted. That leaves one question unanswerable and
// another one answerable dishonestly: an assessor cannot ask *what has this
// deployment certified?*, and a deployment that issued a certificate it
// later regrets can behave as though it never did — there is nothing to
// contradict it. Verification and enumeration are different properties, and
// only the first was ever built.
//
// The audit trail closes it, by reuse rather than by new machinery. Each
// issuance appends one `CertificateIssued` row — digest, subject, key id,
// seal — and under the chained audit ledger that trail is tamper-evident,
// so a *suppressed* issuance is not an absence but a chain break the
// verifier positions. The deployment's own log is the first rung of the
// registry the certificate format was always designed for: open format,
// key registry, no closure needed at either end.
//
// **The inclusion check is additive and stays that way.** `verify` is
// untouched and needs no log, because a certificate that only verifies
// against a reachable issuance log is not offline-verifiable at all — and
// offline verifiability is the property that lets a certificate outlive the
// deployment that issued it. Inclusion answers a *second* question, for a
// holder who has log access: not "is this genuine" but "does the issuer
// admit to it".

/// One recorded certificate issuance, as an enumerator reads it back.
///
/// Identity by value (GP 12 rule 1) and identifiers only — the same four
/// fields the audit row carries. There is deliberately no accessor from
/// here back to the certificate body: the log knows that a document with
/// this digest was issued, and nothing about what it said.
type CertificateIssuance = {
    /// Lowercase-hex SHA-256 over the certificate's canonical signed bytes.
    Digest: string
    /// The answer message id or fact content id at the certificate's root.
    Subject: string
    /// The signing-key id bound into the signed body.
    KeyId: string
    /// `"detached-jws"` or `"application-seal"`.
    Seal: string
    /// The certificate's own `IssuedAt` stamp — the row records the
    /// document's time, not the log write's, so the two never disagree.
    IssuedAt: DateTimeOffset
}

/// What an inclusion check found.
///
/// **Three verdicts, and the third is not a variety of the second.** "The
/// log says nothing was issued" and "the log could not be trusted to say"
/// send a holder to completely different places: the first is evidence
/// against the certificate, the second is evidence against the log. A
/// check that collapsed them would let a deployment answer an
/// inconvenient inclusion query by breaking its own ledger, and read as
/// though the certificate were the forgery.
type CertificateInclusionVerdict =
    /// The issuance is on the log, and here is the row.
    | CertificateIncluded of issuance: CertificateIssuance
    /// The log verified and carries no issuance with this digest. Under a
    /// chained ledger this is a real negative; under a plain audit trail
    /// it is only as strong as the trail.
    | CertificateNotIssued
    /// The log's own integrity could not be established, so it has no
    /// standing to say either way.
    | IssuanceLogUnverifiable of reason: string

module CertificateInclusionVerdict =
    let describe =
        function
        | CertificateIncluded issuance -> $"issuance recorded at {issuance.IssuedAt:o} under key '{issuance.KeyId}'"
        | CertificateNotIssued -> "no issuance with this digest is recorded on the log"
        | IssuanceLogUnverifiable reason -> $"the issuance log could not be verified: {reason}"

/// The log an inclusion check reads, and the enumeration surface.
///
/// Deliberately narrower than `IAuditLog` and deliberately fallible. One
/// read-only operation returning a `Result`, because "the log is intact and
/// empty" and "the log cannot be trusted" are the two answers a caller must
/// be able to tell apart, and a bare list cannot express the second.
///
/// **The integrity half is a seam, not an implementation.** Tamper evidence
/// belongs to whatever ledger a deployment composed — the chained audit
/// ledger is the shipped one — and this tier takes no dependency on it (GP
/// 1). `GroundingCertificate.auditTrailLog` is the honest floor: it reads
/// the ordinary audit path and claims no integrity beyond it.
/// `auditTrailLogWithIntegrity` is where a deployment supplies its ledger's
/// own verifier.
type ICertificateIssuanceLog =
    /// Every issuance recorded in `scopeId`, most recent first, or the
    /// reason the log's integrity could not be established.
    abstract Issued: scopeId: string -> Async<Result<CertificateIssuance list, string>>

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

    // ── issuance transparency (Phase 685) ───────────────────────────────

    /// The seal discriminator for the direct `IArtefactSigner` path.
    [<Literal>]
    let DetachedJwsSeal = "detached-jws"

    /// The seal discriminator for the application-signing-seam path, whose
    /// envelope frames the purpose and attestation level into the signed
    /// bytes alongside the body.
    [<Literal>]
    let ApplicationSeal = "application-seal"

    /// The certificate's content-addressed digest — SHA-256 over the exact
    /// canonical bytes the seal covers, lowercase hex.
    ///
    /// The same value on both issue paths, because both seal the same
    /// canonical body, and the same value a holder recomputes from the
    /// document they hold. That is what lets an inclusion check run with
    /// nothing supplied by the issuer, and it is the identity the import
    /// door's `cert:sha256:` ref already names.
    let certificateDigest (body: GroundingCertificateBody) : string =
        DsseEnvelope.sha256Hex (canonicalBytes body)

    /// Append the issuance row. Called on the success path of both
    /// issuers, and only there: a certificate that failed to seal was never
    /// issued, so recording it would put documents on the log that no
    /// holder can ever present.
    ///
    /// `None` records nothing — a deployment with no composed `IAuditLog`
    /// is byte-for-byte what it was (GP 11 / GP 13). `OccurredAt` is the
    /// certificate's OWN `IssuedAt`, not the log write's clock, so the row
    /// and the document can never disagree about when the thing happened.
    let private recordIssuance
        (audit: IAuditLog option)
        (scopeId: string)
        (seal: string)
        (body: GroundingCertificateBody)
        : Async<unit> =
        async {
            match audit with
            | None -> return ()
            | Some audit ->
                return!
                    audit.Record(
                        scopeId,
                        AuditEvent.CertificateIssued {
                            Digest = certificateDigest body
                            Subject = body.Root
                            KeyId = body.DeploymentKeyId
                            Seal = seal
                            Format = body.Format
                            OccurredAt = body.IssuedAt
                        }
                    )
        }

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
    ///
    /// Phase 685: `audit` is where the issuance row lands. Both issuers
    /// carry it because both ARE issue choke points — the attested path is
    /// not composed by `FactsCompose` today (GP 13), and an issuance that
    /// happened to travel the path a composition root wired by hand would
    /// otherwise be the one issuance the log could not see.
    type private DefaultIssuer(builder: CertificateBuilder, signer: IArtefactSigner option, audit: IAuditLog option) =

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
                        | Ok signature ->
                            do! recordIssuance audit scopeId DetachedJwsSeal body
                            return Ok { Body = body; Signature = signature }
            }

    /// The attested issuer: seals the built body through the application
    /// signing seam, so the purpose and the signer's attestation level are
    /// framed into the signed bytes alongside the body.
    type private AttestedIssuer(builder: CertificateBuilder, signer: IApplicationSigner option, audit: IAuditLog option)
        =

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
                        | Ok envelope ->
                            do! recordIssuance audit scopeId ApplicationSeal body
                            return Ok { Body = body; Envelope = envelope }
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
        DefaultIssuer(CertificateBuilder(graph, store, gate, events, clock), signer, None)
        :> IGroundingCertificateIssuer

    /// The issuer with a UTC wall-clock — the composition default.
    let createIssuer
        (graph: IProvenanceGraph)
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (events: IEventStore)
        (signer: IArtefactSigner option)
        : IGroundingCertificateIssuer =
        createIssuerWithClock graph store gate events signer (fun () -> DateTime.UtcNow)

    /// The direct-path issuer, logging every issuance to `audit` (Phase
    /// 685). Identical in every other respect to `createIssuerWithClock`:
    /// same body, same bytes, same signature, same refusals.
    ///
    /// A SEPARATE entry point rather than an optional argument on the one
    /// above, and that is a compatibility decision rather than a stylistic
    /// one — an added optional constructor argument folds the existing
    /// arity away and reads to the public-surface gate as a removal, which
    /// is exactly what it would be for anyone calling it.
    let createIssuerWithClockAudited
        (graph: IProvenanceGraph)
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (events: IEventStore)
        (signer: IArtefactSigner option)
        (audit: IAuditLog)
        (clock: unit -> DateTime)
        : IGroundingCertificateIssuer =
        DefaultIssuer(CertificateBuilder(graph, store, gate, events, clock), signer, Some audit)
        :> IGroundingCertificateIssuer

    /// The logging direct-path issuer with a UTC wall-clock — what
    /// `FactsCompose.withFactStore` composes when the deployment has an
    /// `IAuditLog`.
    let createIssuerAudited
        (graph: IProvenanceGraph)
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (events: IEventStore)
        (signer: IArtefactSigner option)
        (audit: IAuditLog)
        : IGroundingCertificateIssuer =
        createIssuerWithClockAudited graph store gate events signer audit (fun () -> DateTime.UtcNow)

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
        AttestedIssuer(CertificateBuilder(graph, store, gate, events, clock), signer, None)
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

    /// The attested issuer, logging every issuance to `audit` (Phase 685).
    ///
    /// The attested path is not composed by `FactsCompose` — that is Phase
    /// 682's deliberate posture (GP 13), and this phase does not disturb
    /// it. A composition root that wires the attested issuer by hand gets
    /// the logging entry point here, so the log's claim to enumerate
    /// issuance holds on both paths rather than only the composed one.
    let createAttestedIssuerWithClockAudited
        (graph: IProvenanceGraph)
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (events: IEventStore)
        (signer: IApplicationSigner option)
        (audit: IAuditLog)
        (clock: unit -> DateTime)
        : IAttestedGroundingCertificateIssuer =
        AttestedIssuer(CertificateBuilder(graph, store, gate, events, clock), signer, Some audit)
        :> IAttestedGroundingCertificateIssuer

    /// The logging attested issuer with a UTC wall-clock.
    let createAttestedIssuerAudited
        (graph: IProvenanceGraph)
        (store: IFactStore)
        (gate: IFactDisclosureGate)
        (events: IEventStore)
        (signer: IApplicationSigner option)
        (audit: IAuditLog)
        : IAttestedGroundingCertificateIssuer =
        createAttestedIssuerWithClockAudited graph store gate events signer audit (fun () -> DateTime.UtcNow)

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

    // ── the issuance log: enumeration + inclusion (Phase 685) ───────────

    /// Project the recorded rows for one scope. Reads only
    /// `CertificateIssued` events, so a caller handing over an unfiltered
    /// trail gets the same answer as one whose store honoured the type
    /// filter.
    let private issuancesOf (events: AuditEvent list) : CertificateIssuance list =
        events
        |> List.choose (function
            | AuditEvent.CertificateIssued p ->
                Some {
                    Digest = p.Digest
                    Subject = p.Subject
                    KeyId = p.KeyId
                    Seal = p.Seal
                    IssuedAt = p.OccurredAt
                }
            | _ -> None)

    /// The issuance log over the ordinary audit read path, scope-filtered
    /// exactly as every other audit query is (GP 4 — the scope isolation
    /// is the store's, not a filter this tier remembers to apply).
    ///
    /// **It claims no integrity, and says so by always returning `Ok`.**
    /// A plain audit trail is a record, not a proof: it can enumerate what
    /// was issued and it cannot demonstrate that nothing was removed. That
    /// is a real and useful answer — it is what closes the "issued and
    /// later denied" gap for a cooperative deployment — but a holder
    /// verifying against an ADVERSARIAL one needs the tamper evidence, and
    /// for that the deployment composes a chained ledger and supplies its
    /// verifier through `auditTrailLogWithIntegrity`.
    let auditTrailLog (audit: IAuditLog) : ICertificateIssuanceLog =
        { new ICertificateIssuanceLog with
            member _.Issued(scopeId) = async {
                let! events = audit.GetAuditTrail(scopeId, None, Some "CertificateIssued")
                return Ok(issuancesOf events)
            }
        }

    /// The issuance log over the audit read path, gated on the
    /// deployment's own ledger-integrity check.
    ///
    /// `integrity` is whatever verifies the trail this log reads — for the
    /// shipped chained audit ledger, a call to its verifier mapping a
    /// break or an untrusted head to `Error`. The seam is a function
    /// rather than a package reference on purpose: tamper evidence belongs
    /// to the sink that owns the chain, and the fact tier taking a
    /// dependency on one would nail every deployment to that choice (GP 1).
    ///
    /// **Integrity is checked BEFORE the rows are read, and a failure
    /// short-circuits.** The order is load-bearing: a not-found verdict
    /// derived from rows a broken log supplied would be an assertion about
    /// the certificate drawn from evidence already known to be worthless —
    /// precisely the confusion the third verdict exists to prevent.
    let auditTrailLogWithIntegrity
        (audit: IAuditLog)
        (integrity: unit -> Async<Result<unit, string>>)
        : ICertificateIssuanceLog =
        { new ICertificateIssuanceLog with
            member _.Issued(scopeId) = async {
                match! integrity () with
                | Error reason -> return Error reason
                | Ok() ->
                    let! events = audit.GetAuditTrail(scopeId, None, Some "CertificateIssued")
                    return Ok(issuancesOf events)
            }
        }

    /// **The enumeration surface**: every certificate this deployment has
    /// issued in `scopeId`, most recent first — digest and subject, with
    /// the key id and seal that produced them.
    ///
    /// This is the question a certificate could not answer before: not
    /// "is the document in my hand genuine" but "what has this deployment
    /// certified". Scope-filtered by the ordinary audit read path, so an
    /// enumerator sees exactly the scopes their audit access already
    /// covers and no more.
    let listIssued (log: ICertificateIssuanceLog) (scopeId: string) : Async<Result<CertificateIssuance list, string>> =
        log.Issued scopeId

    /// Is a certificate with this digest on the issuance log?
    ///
    /// The digest-only form, for a holder who has already computed it (or
    /// read it off an `Imported` fact's `cert:sha256:` ref) and does not
    /// hold the document.
    let checkInclusionOfDigest
        (log: ICertificateIssuanceLog)
        (scopeId: string)
        (digest: string)
        : Async<CertificateInclusionVerdict> =
        async {
            match! log.Issued scopeId with
            | Error reason -> return IssuanceLogUnverifiable reason
            | Ok issuances ->
                match issuances |> List.tryFind (fun i -> i.Digest = digest) with
                | Some issuance -> return CertificateIncluded issuance
                | None -> return CertificateNotIssued
        }

    /// **The optional inclusion check.** Given log access, confirm this
    /// certificate's issuance is recorded.
    ///
    /// Strictly additive to `verify`, which is untouched and still needs
    /// nothing but the certificate and a public key. The two answer
    /// different questions and a holder wants both where both are
    /// available: `verify` says the document is intact and genuinely
    /// sealed by the named key; this says the issuer's own log admits to
    /// having sealed it.
    ///
    /// **Never call this INSTEAD of verifying.** Inclusion is computed
    /// from a digest over bytes nobody has checked a signature on, so on
    /// its own it establishes only that a document with these bytes was
    /// issued — which is not a claim about the document in your hand until
    /// the seal has been verified.
    let checkInclusion
        (log: ICertificateIssuanceLog)
        (scopeId: string)
        (certificate: GroundingCertificate)
        : Async<CertificateInclusionVerdict> =
        checkInclusionOfDigest log scopeId (certificateDigest certificate.Body)

    /// The inclusion check for a certificate sealed through the
    /// application signing seam. Same digest, because both paths seal the
    /// same canonical body — so one log serves both, and a deployment that
    /// migrated from one seal to the other has one issuance history rather
    /// than two.
    let checkInclusionAttested
        (log: ICertificateIssuanceLog)
        (scopeId: string)
        (certificate: AttestedGroundingCertificate)
        : Async<CertificateInclusionVerdict> =
        checkInclusionOfDigest log scopeId (certificateDigest certificate.Body)
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open ToolUp.Platform
open ToolUp.ArtefactSigning

// ─── Certificate-verified fact import (Phase 683) ────────────────────────
//
// The fact model has carried `MethodRef.Imported of certificateRef` since
// the store shipped, and grounding certificates have embedded imported-cert
// refs since they shipped — but nothing verified a certificate AT IMPORT.
// An imported fact was therefore testimony wearing a provenance field: the
// ref named a document nobody had checked, asserted by whoever called
// `Assert`. A fact base that launders unverified imports has no grounding
// claim left, because the weakest fact in a chain is what the chain is
// worth.
//
// This module is the door. It takes a peer's offered fact and the
// certificate sealing it, and in one pass:
//
//   1. **verifies the certificate offline** against key material supplied
//      for THAT peer — the standard-statement path, so the check is the one
//      any conforming implementation performs. Both published projections
//      are read: the direct certificate and the levels-bound attested one,
//      routed on the predicate type the document declares. Where the
//      document claims an attestation level, that level is an ADMISSION
//      input — the anchor declares which levels it accepts, and a level it
//      does not is a refusal of its own rather than a disclosure
//      adjustment;
//   2. **re-derives the content-addressed fact id** from the offered
//      identity tuple and compares it to the root the certificate is issued
//      over. This is the load-bearing step, and it is only possible because
//      a `FactId` is deployment-independent by construction: the peer
//      cannot hand over a fact whose identity tuple differs from the one
//      their certificate covers, because the id would not match;
//   3. **maps the peer's disclosure stance conservatively**, taking the
//      floor of what the peer SEALED into the certificate and whatever
//      ceiling this deployment imposes on that peer. An import narrows or
//      leaves alone; it can never widen;
//   4. **asserts with `Imported` provenance** carrying the verified
//      certificate's content-addressed ref.
//
// Every other outcome is a typed refusal, audited, with **nothing written to
// the store** — the assert is the last act on the only path that reaches it.
// Unverifiable input is never asserted as a lower-confidence fact: there is
// no such thing here, and inventing one would move the failure from a
// refusal an operator sees to a number an answer quotes.
//
// **The transport is deliberately out of scope.** The door takes a
// certificate, not a connection. How the document arrived — a file, an
// operator paste, a peer call — is the caller's business, and keeping it so
// is what lets the same door serve all three without a network dependency.
//
// **Why the standard-statement form, and only it.** The certificate's own
// detached-JWS seal verifies through `IArtefactVerifier`, which resolves
// keys out of the deployment's own secret store — that is exactly the
// ambient trust store this door exists to avoid. Key material here arrives
// as DATA, per peer, at composition: a public key, named, held for one peer
// and no other. No key is discoverable, so no key is implicitly trusted, and
// a deployment can state precisely whose facts it will accept by reading its
// own composition root.
//
// **Nothing here is reachable unless a deployment composes it**
// (`FactsCompose.withFactImport`). A deployment that imports nothing pays
// nothing (GP 13), and one that composes only the fact store is
// byte-for-byte what it was (GP 11).

/// The key material a deployment holds for one peer, the most permissive
/// stance an import from that peer may reach, and the attestation levels it
/// will admit from them.
///
/// **Identity by value (GP 12 rule 1)** — a public key, a name, a stance
/// and a set of levels. No handle, no resolver, no callback into a key
/// service and no predicate function: an anchor is a value a composition
/// root can be read off the page, and every one of its fields can be
/// printed. A policy expressed as a callback would be readable only by
/// running it.
type PeerTrustAnchor = {
    /// The name this deployment knows the peer by. The caller states it at
    /// import; it is never read out of the offered document, so a document
    /// cannot nominate the anchor it would like to be checked against.
    PeerId: string
    /// The peer's public key — the whole of what verification needs.
    PublicKey: PublicKeyMetadata
    /// The ceiling on any import from this peer. `Surfaceable` (the
    /// default) imposes none, so the peer's own sealed stance governs;
    /// anything else can only narrow, since the effective stance is the
    /// floor of the two (`Disclosure.floor`).
    DisclosureCeiling: Disclosure
    /// The attestation levels an import from this peer may carry — a SET
    /// this deployment declares, never a bar a level is compared against.
    ///
    /// **Why a set and not a threshold.** `AttestationLevel` is not
    /// totally ordered. `Reserved of label` exists so a level this build
    /// does not understand round-trips rather than failing to parse, and
    /// the type's own doc comment says plainly that carrying such a label
    /// is NOT evidence for the claim it names. Any `>=` comparison would
    /// therefore admit `reserved:anything` above `IsolatedSigner` on the
    /// strength of a string a peer chose — inverting the one rule the type
    /// states about itself. A set cannot express that mistake: a level is
    /// admitted because this deployment named it, or it is not admitted.
    ///
    /// **Orthogonal to `DisclosureCeiling`, deliberately.** The ceiling
    /// governs how widely an imported fact may be surfaced once it is here;
    /// the level governs whether the peer's document is admitted at all.
    /// Folding one into the other — "an `Attribution` fact is forced to
    /// `Restricted`" — would make the effective stance the product of two
    /// lattices that do not compose, and would encode a policy nothing in
    /// this substrate has a basis for.
    AdmissibleLevels: Set<AttestationLevel>
}

module PeerTrustAnchor =

    /// The levels an anchor admits when it declares none of its own:
    /// `Attribution` and `IsolatedSigner`, the two this build understands.
    ///
    /// **Not a widening relative to the path that already works.** A direct
    /// certificate carries no level at all and is admitted unconditionally,
    /// so an `Attribution`-level document — which says at least as much,
    /// bound into its signed bytes — cannot be the stricter case. A
    /// deployment that wants the higher bar declares
    /// `withAdmissibleLevels [ IsolatedSigner ]`; that is the opt-in, and a
    /// deployment that composes no import door is untouched either way
    /// (GP 11 / GP 13).
    let defaultAdmissibleLevels: Set<AttestationLevel> =
        Set.ofList [ Attribution; IsolatedSigner ]

    /// An anchor with no ceiling of its own — the peer's sealed stance
    /// governs. The identity of `Disclosure.floor`, so this is the
    /// no-op-ceiling case rather than a permissive one.
    let create (peerId: string) (publicKey: PublicKeyMetadata) : PeerTrustAnchor = {
        PeerId = peerId
        PublicKey = publicKey
        DisclosureCeiling = Surfaceable
        AdmissibleLevels = defaultAdmissibleLevels
    }

    /// Impose a ceiling on imports from this peer. Can only narrow what
    /// the peer declared — a ceiling of `Surfaceable` over a peer's
    /// `Internal` fact still yields `Internal`.
    let withCeiling (ceiling: Disclosure) (anchor: PeerTrustAnchor) : PeerTrustAnchor = {
        anchor with
            DisclosureCeiling = ceiling
    }

    /// Declare exactly which attestation levels an import from this peer
    /// may carry. Replaces the default set rather than adding to it, so the
    /// composition root states the whole policy in one place.
    ///
    /// A `Reserved` label passed here is inert: `admits` refuses every
    /// reserved level unconditionally, so naming one neither admits it nor
    /// makes the anchor admit nothing.
    let withAdmissibleLevels (levels: AttestationLevel list) (anchor: PeerTrustAnchor) : PeerTrustAnchor = {
        anchor with
            AdmissibleLevels = Set.ofList levels
    }

    /// Whether this anchor admits a certificate claiming `level`.
    ///
    /// **A `Reserved` label is refused before the set is consulted**, under
    /// every policy including one that names it. A reserved level is a
    /// claim this build cannot evaluate, and admitting it would be admitting
    /// a string — the exact failure a threshold comparison makes silently
    /// and this check makes impossible.
    let admits (level: AttestationLevel) (anchor: PeerTrustAnchor) : bool =
        match level with
        | Reserved _ -> false
        | known -> anchor.AdmissibleLevels.Contains known

/// A peer's offered fact: the identity tuple the certificate's root is
/// derived from, plus the value.
///
/// **What is NOT here is as deliberate as what is.** The offer carries no
/// disclosure stance and no `AsOf` — the stance is read out of the SIGNED
/// certificate (a stance asserted beside a document is a stance the peer can
/// edit), and the transaction time is stamped by the importing store, since
/// it records when the fact entered THIS store and no peer can speak to
/// that. It carries no `ResultRef` or `TriggerRef` either: those are opaque
/// handles into the peer's own stores, and the fact model already states
/// that an imported fact has none.
///
/// `InputHashes` DO travel, because they are content hashes — meaningful in
/// any deployment — and because they participate in the content address, so
/// a wrong set fails the id check rather than passing silently.
type ImportedFactOffer = {
    Subject: SubjectRef
    Metric: MetricRef
    Value: FactValue
    Period: TemporalExtent
    /// The method the PEER produced the fact under, unchanged. Part of the
    /// content address, so it is re-derived and checked — never trusted,
    /// and never rewritten to look like a local computation.
    Method: MethodRef
    /// Content hashes of the peer's inputs.
    InputHashes: string list
    Confidence: Confidence option
}

module ImportedFactOffer =

    /// The offer an exporting deployment sends for one of its own facts —
    /// the peer-side projection, kept here so both ends of the wire read
    /// one definition of what travels.
    let ofFact (fact: Fact) : ImportedFactOffer = {
        Subject = fact.Subject
        Metric = fact.Metric
        Value = fact.Value
        Period = fact.Period
        Method = fact.Method
        InputHashes = fact.Evidence.InputHashes
        Confidence = fact.Confidence
    }

    /// The content-addressed id this offer claims — the value compared
    /// against the certificate's root. Pure, and identical to what the
    /// peer's own store computed, which is the whole point of a
    /// deployment-independent identity.
    let derivedFactId (offer: ImportedFactOffer) : string =
        let evidence = {
            ResultRef = None
            InputHashes = offer.InputHashes
            TriggerRef = None
        }

        Fact.compute
            offer.Subject
            offer.Metric
            offer.Period
            offer.Method
            (Fact.effectiveInputHashes offer.Method evidence offer.Value)

/// Why an import was refused. Nothing landed in the store on any of these.
///
/// The cases are kept apart because they send an operator to entirely
/// different places, and a caller must never have to read a string to tell
/// them apart. "I could not check this document" (`Unverifiable`), "this
/// document is about a different fact than the one offered"
/// (`ContentIdMismatch`), "the peer's own export door withheld this"
/// (`WithheldByPeer`) and "I hold no key for this peer" (`UntrustedPeer`)
/// are four faults with four remedies, and only one of them implicates the
/// bytes.
type FactImportRefusal =
    /// No key material is composed for the named peer. A refusal, never a
    /// fallback to some other key: the absence of an anchor is the whole
    /// statement this deployment makes about that peer.
    | ImportUntrustedPeer of peerId: string
    /// The certificate did not verify: the signature failed over the
    /// envelope, the document could not be read, or it is a statement of a
    /// different shape. Carries the envelope's own verdict verbatim, so the
    /// distinction between "cannot check" and "checked and wrong" survives
    /// the boundary rather than being flattened into one word here.
    | ImportUnverifiable of verdict: EnvelopeVerdict
    /// The certificate verified, and the id re-derived from the offered
    /// identity tuple is not the root the certificate is issued over: a
    /// genuine certificate offered as cover for a different fact. Names
    /// both, because which of the two is wrong is not knowable from here.
    | ImportContentIdMismatch of certificateRoot: string * derived: string
    /// The certificate verifies and is issued over this id, but the node it
    /// roots at is not a fact node — an answer certificate offered as a
    /// fact's provenance, or a chain whose root is missing entirely.
    | ImportRootNotAFact of kind: string
    /// The peer's OWN disclosure gate withheld this fact when it issued the
    /// certificate: only the id, the stance and a policy ref survive in the
    /// document. Importing it would be the widest widening available — the
    /// peer declined to disclose it at their export door, and this door
    /// does not overrule that.
    | ImportWithheldByPeer of policyRef: string
    /// The stance sealed into the certificate is one this build cannot
    /// read. Refused rather than defaulted: a default is either a widening
    /// or a misreport of what the peer actually said.
    | ImportStanceUnreadable of stance: string
    /// The document declares a predicate type neither published projection
    /// uses, so there is no reader for it here. Distinct from
    /// `ImportUnverifiable` carrying a predicate-type mismatch: that verdict
    /// names an expectation, and naming one of two arbitrarily would tell a
    /// holder their statement was the wrong one of a pair it is not a member
    /// of. Quotes the type it declared.
    | ImportUnknownProjection of predicateType: string
    /// The certificate verified, is genuine, and claims an attestation level
    /// this deployment does not admit from this peer.
    ///
    /// Deliberately NOT `ImportUnverifiable`: "your key's custody does not
    /// meet my bar" and "this did not verify" send an operator to entirely
    /// different places — the first to a policy declaration or a peer's
    /// signing arrangements, the second to the bytes — which is the
    /// distinction this whole DU exists to keep. Names the level offered and
    /// the ones admitted, because neither side is knowable from the other.
    | ImportLevelNotAdmitted of level: string * admitted: string list
    /// The certificate verified and the id matched, and the store refused
    /// the assertion. Carries the store's own reason.
    | ImportRejectedByStore of reason: string

module FactImportRefusal =
    let describe =
        function
        | ImportUntrustedPeer peerId -> $"no key material is composed for peer '{peerId}'; nothing was imported"
        | ImportUnverifiable verdict -> $"the peer's certificate did not verify: {EnvelopeVerdict.describe verdict}"
        | ImportContentIdMismatch(root, derived) ->
            $"the offered fact re-derives to '{derived}' but the certificate is issued over '{root}'"
        | ImportRootNotAFact kind ->
            $"the certificate's root node is a '{kind}', not a fact; it certifies something else"
        | ImportWithheldByPeer policyRef ->
            $"the peer withheld this fact at its own export door under policy '{policyRef}'; it was not offered"
        | ImportStanceUnreadable stance ->
            $"the certificate declares a disclosure stance this build cannot read: '{stance}'"
        | ImportUnknownProjection predicateType ->
            $"the document declares predicate type '{predicateType}', which is neither certificate projection this door reads"
        | ImportLevelNotAdmitted(level, admitted) ->
            let admittedText =
                match admitted with
                | [] -> "none"
                | levels -> String.Join(", ", levels)

            $"the certificate claims attestation level '{level}', which this deployment does not admit from this peer (admitted: {admittedText})"
        | ImportRejectedByStore reason -> $"the fact store refused the assertion: {reason}"

/// The import door. One method, because an import is one indivisible act:
/// verify, re-derive, map, assert. Splitting it would hand a caller a
/// verified certificate and the opportunity to assert something else.
///
/// **Six portability rules (GP 12).** Identity by value throughout (peer
/// names, ids, a public key, JSON text); async at the boundary; failure as
/// data (`Result`, no callbacks); stateless between calls — every input
/// arrives as a parameter and the anchors are fixed at composition; scoped
/// by `scopeId`, with no ordering promised across scopes; no timing
/// primitive.
type IFactImportDoor =
    /// Import `offer` from `peerId` under the DSSE-wrapped certificate in
    /// `certificateJson` — either published projection, routed on the
    /// predicate type the document itself declares. Returns the asserted
    /// local fact, or a typed refusal — in which case nothing was written.
    abstract Import:
        scopeId: string * peerId: string * offer: ImportedFactOffer * certificateJson: string ->
            Async<Result<Fact, FactImportRefusal>>

module FactImport =

    /// The prefix on a certificate reference. Present so a ref is never
    /// mistaken for a fact id — both are SHA-256 hex, and a bare digest in
    /// a provenance field would be ambiguous exactly where ambiguity is
    /// most expensive.
    [<Literal>]
    let CertificateRefPrefix = "cert:sha256:"

    /// The content-addressed reference for a verified certificate: a
    /// SHA-256 over the exact bytes its own seal covers.
    ///
    /// Deployment-independent and **recomputable by anyone holding the
    /// certificate**, which is what makes it a join key rather than a local
    /// handle: a third party given the imported fact and the certificate
    /// can check for themselves that the ref names that document. The key
    /// id is inside those bytes (`DeploymentKeyId` is part of the signed
    /// body), so the ref binds who sealed it without saying so twice.
    let private refOfBody (body: GroundingCertificateBody) : string =
        CertificateRefPrefix
        + DsseEnvelope.sha256Hex (GroundingCertificate.canonicalBytes body)

    let certificateRef (certificate: GroundingCertificate) : string = refOfBody certificate.Body

    /// The content-addressed reference for a verified ATTESTED certificate.
    ///
    /// The same digest the direct projection's ref takes over the same body,
    /// because both seals cover byte-identical bodies — a property this
    /// inherits rather than re-establishes. So a fact imported under either
    /// document joins to one ref, and a third party holding either can check
    /// that the ref names it.
    let attestedCertificateRef (certificate: AttestedGroundingCertificate) : string = refOfBody certificate.Body

    /// The certificate's node for its own root, when the chain carries one.
    let private rootNode (body: GroundingCertificateBody) : CertificateNode option =
        body.Nodes |> List.tryFind (fun n -> n.Id = body.Root)

    /// The peer's sealed stance for the root fact, or the refusal that
    /// stands in its place. Reads only from the SIGNED body — which is the
    /// same body on both projections, so this is one reader and not two.
    let private declaredStance (body: GroundingCertificateBody) : Result<Disclosure, FactImportRefusal> =
        match rootNode body with
        | None -> Error(ImportRootNotAFact "absent")
        | Some node when node.Kind <> "Fact" -> Error(ImportRootNotAFact node.Kind)
        | Some node when node.Withheld ->
            // A withheld node keeps its stance and its policy ref and loses
            // everything else. The policy ref is in `PolicyRefs`; the node
            // itself does not name which one applied, so report the stance,
            // which is the field that does.
            Error(ImportWithheldByPeer(node.Disclosure |> Option.defaultValue "unknown"))
        | Some node ->
            match node.Disclosure with
            | None -> Error(ImportStanceUnreadable "")
            | Some stance ->
                match Disclosure.tryParse stance with
                | Some d -> Ok d
                | None -> Error(ImportStanceUnreadable stance)

    /// The audit payload for one import attempt. Built incrementally as the
    /// door learns things, so a refusal records everything established
    /// before it and claims nothing after.
    let private emptyPayload (peerId: string) (anchorKeyId: string) (offer: ImportedFactOffer) (derived: string) = {
        PeerId = peerId
        PeerKeyId = anchorKeyId
        CertificateRoot = ""
        CertificateRef = ""
        DerivedFactId = derived
        ImportedFactId = ""
        Subject = SubjectRef.toString offer.Subject
        Metric = offer.Metric.Value
        DeclaredDisclosure = ""
        EffectiveDisclosure = ""
        // Absent until a document that CLAIMS a level has been read and
        // verified. Never seeded with a level nobody offered.
        AttestationLevel = ""
        Reason = ""
        OccurredAt = DateTimeOffset.UtcNow
    }

    /// The door over a fact store, a set of per-peer anchors, and an audit
    /// log.
    ///
    /// `anchors` is fixed at construction, which is the point: the set of
    /// peers this deployment will accept facts from is a composition-time
    /// declaration, readable in one place, not a store something can be
    /// added to at runtime.
    type private DefaultDoor(store: IFactStore, anchors: Map<string, PeerTrustAnchor>, audit: IAuditLog) =

        interface IFactImportDoor with
            member _.Import(scopeId, peerId, offer, certificateJson) = async {
                let derived = ImportedFactOffer.derivedFactId offer

                // Every refusal path ends here, and none of them has
                // touched the store: the assert below is the last act on
                // the only path that reaches it.
                let refuse (payload: FactImportPayload) (refusal: FactImportRefusal) = async {
                    do!
                        audit.Record(
                            scopeId,
                            AuditEvent.FactImportRefused {
                                payload with
                                    Reason = FactImportRefusal.describe refusal
                            }
                        )

                    return Error refusal
                }

                match anchors.TryFind peerId with
                | None ->
                    let payload = emptyPayload peerId "" offer derived
                    return! refuse payload (ImportUntrustedPeer peerId)
                | Some anchor ->
                    let payload = emptyPayload peerId anchor.PublicKey.KeyId offer derived

                    // (1) Route on the projection the document declares.
                    //
                    // The caller nominates nothing and there is no
                    // fall-back leg: one document, one route, one verdict.
                    // This reads a field out of the UNVERIFIED payload,
                    // against the surrounding discipline that a document
                    // never nominates how it is checked — safe only because
                    // each leg then verifies the signature over the PAE
                    // first and re-checks the predicate type inside the
                    // signed statement, so a document that lies about its
                    // own shape is routed to a reader that refuses it.
                    match CertificateEnvelope.declaredProjection certificateJson with
                    | Error verdict -> return! refuse payload (ImportUnverifiable verdict)
                    | Ok(CertificateEnvelope.UnknownProjection predicateType) ->
                        return! refuse payload (ImportUnknownProjection predicateType)
                    | Ok projection ->
                        // (2) Verify offline against this peer's key alone,
                        // on the leg the projection selected.
                        //
                        // No subject expectation is passed on either leg.
                        // The envelope helpers would happily check one, but
                        // folding the id comparison into the signature
                        // check would collapse two answers a holder acts on
                        // differently — "this document does not verify" and
                        // "this document is genuine and about a different
                        // fact" — into a single verdict. The comparison
                        // happens below, AFTER the signature is
                        // established, which is what makes the mismatch
                        // verdict's claim true.
                        //
                        // The attested leg's reader also reconciles every
                        // surfaced field against the seal, so a document
                        // whose published level disagrees with the sealed
                        // one is refused HERE, before any level policy is
                        // consulted. A policy must never be applied to a
                        // level the signature does not cover.
                        let verified =
                            match projection with
                            | CertificateEnvelope.AttestedProjection ->
                                CertificateEnvelope.verifyAndReadAttestedJson anchor.PublicKey None certificateJson
                                |> Result.map (fun c -> c.Body, Some c.Envelope.Level, attestedCertificateRef c)
                            | _ ->
                                CertificateEnvelope.verifyAndReadJson anchor.PublicKey None certificateJson
                                |> Result.map (fun c -> c.Body, None, certificateRef c)

                        match verified with
                        | Error verdict -> return! refuse payload (ImportUnverifiable verdict)
                        | Ok(body, level, ref') ->
                            let payload = {
                                payload with
                                    CertificateRoot = body.Root
                                    CertificateRef = ref'
                                    AttestationLevel =
                                        level |> Option.map AttestationLevel.name |> Option.defaultValue ""
                            }

                            // (3) Admission on the attestation level. A
                            // property of the document and this peer, not
                            // of the offer, so it is decided as soon as the
                            // seal is established and before anything about
                            // the offered fact is compared.
                            //
                            // The direct projection claims no level and is
                            // not measured against one: a level nobody
                            // asserted cannot be admitted or refused, and
                            // inventing one to compare would be exactly the
                            // default this door refuses to make elsewhere.
                            match level with
                            | Some claimed when not (PeerTrustAnchor.admits claimed anchor) ->
                                return!
                                    refuse
                                        payload
                                        (ImportLevelNotAdmitted(
                                            AttestationLevel.name claimed,
                                            anchor.AdmissibleLevels |> Set.toList |> List.map AttestationLevel.name
                                        ))
                            | _ ->

                                // (4) Re-derive and compare. The content
                                // address covers the subject, the metric,
                                // the period, the peer's method identity
                                // AND the input hashes, so a single altered
                                // field in the offer lands here rather than
                                // in the store.
                                if body.Root <> derived then
                                    return! refuse payload (ImportContentIdMismatch(body.Root, derived))
                                else
                                    // The envelope's own statement subject
                                    // must name the root its predicate
                                    // carries. Both are inside the signed
                                    // bytes, so this catches a
                                    // self-inconsistent statement rather
                                    // than a tampered one — cheap,
                                    // crypto-free, and the last structural
                                    // thing left to check. The expectation
                                    // is the one THIS projection publishes,
                                    // never the other's.
                                    let selfConsistent =
                                        match CertificateEnvelope.expectationFor projection (Some derived) with
                                        | None -> Error(EnvelopeMalformed "no reader is published for this projection")
                                        | Some expectation ->
                                            DsseEnvelope.parse certificateJson
                                            |> Result.mapError EnvelopeMalformed
                                            |> Result.bind (DsseEnvelope.checkShape expectation)

                                    match selfConsistent with
                                    | Error verdict -> return! refuse payload (ImportUnverifiable verdict)
                                    | Ok _ ->
                                        match declaredStance body with
                                        | Error refusal -> return! refuse payload refusal
                                        | Ok declared ->
                                            // (5) The conservative floor. An
                                            // anchor with no ceiling leaves
                                            // the peer's stance exactly as
                                            // sealed. Orthogonal to the
                                            // level: the stance decides how
                                            // widely the fact may be
                                            // surfaced, the level decided
                                            // whether the document was
                                            // admitted at all.
                                            let effective = Disclosure.floor declared anchor.DisclosureCeiling

                                            let payload = {
                                                payload with
                                                    DeclaredDisclosure = Disclosure.toString declared
                                                    EffectiveDisclosure = Disclosure.toString effective
                                            }

                                            // (6) Assert with `Imported`
                                            // provenance. The method changes
                                            // — an imported fact is this
                                            // deployment's assertion that a
                                            // peer computed something, not a
                                            // claim to have computed it — so
                                            // the local id differs from the
                                            // peer's by construction, and the
                                            // certificate ref is what joins
                                            // them.
                                            let draft: FactDraft = {
                                                Subject = offer.Subject
                                                Metric = offer.Metric
                                                Value = offer.Value
                                                Period = offer.Period
                                                Method = Imported ref'
                                                Evidence = {
                                                    ResultRef = None
                                                    InputHashes = offer.InputHashes
                                                    TriggerRef = None
                                                }
                                                Confidence = offer.Confidence
                                                Disclosure = effective
                                            }

                                            match! store.Assert(scopeId, draft) with
                                            | Error reason -> return! refuse payload (ImportRejectedByStore reason)
                                            | Ok fact ->
                                                do!
                                                    audit.Record(
                                                        scopeId,
                                                        AuditEvent.FactImportAccepted {
                                                            payload with
                                                                ImportedFactId = fact.FactId
                                                        }
                                                    )

                                                return Ok fact
            }

    /// Construct the import door over the composed fact store, the peer
    /// anchors this deployment declares, and the audit log every refusal is
    /// recorded through.
    ///
    /// A duplicate `PeerId` in `anchors` is resolved last-wins rather than
    /// refused, matching every other keyed compose-time registration in the
    /// fact tier.
    let create (store: IFactStore) (anchors: PeerTrustAnchor list) (audit: IAuditLog) : IFactImportDoor =
        let byPeer = anchors |> List.map (fun a -> a.PeerId, a) |> Map.ofList
        DefaultDoor(store, byPeer, audit) :> IFactImportDoor
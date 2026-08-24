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
//      any conforming implementation performs;
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

/// The key material a deployment holds for one peer, plus the most
/// permissive stance an import from that peer may reach.
///
/// **Identity by value (GP 12 rule 1)** — a public key, a name, and a
/// stance. No handle, no resolver, no callback into a key service: an
/// anchor is a value a composition root can be read off the page.
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
}

module PeerTrustAnchor =

    /// An anchor with no ceiling of its own — the peer's sealed stance
    /// governs. The identity of `Disclosure.floor`, so this is the
    /// no-op-ceiling case rather than a permissive one.
    let create (peerId: string) (publicKey: PublicKeyMetadata) : PeerTrustAnchor = {
        PeerId = peerId
        PublicKey = publicKey
        DisclosureCeiling = Surfaceable
    }

    /// Impose a ceiling on imports from this peer. Can only narrow what
    /// the peer declared — a ceiling of `Surfaceable` over a peer's
    /// `Internal` fact still yields `Internal`.
    let withCeiling (ceiling: Disclosure) (anchor: PeerTrustAnchor) : PeerTrustAnchor = {
        anchor with
            DisclosureCeiling = ceiling
    }

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
    /// `certificateJson`. Returns the asserted local fact, or a typed
    /// refusal — in which case nothing was written.
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
    let certificateRef (certificate: GroundingCertificate) : string =
        CertificateRefPrefix
        + DsseEnvelope.sha256Hex (GroundingCertificate.canonicalBytes certificate.Body)

    /// The certificate's node for its own root, when the chain carries one.
    let private rootNode (certificate: GroundingCertificate) : CertificateNode option =
        certificate.Body.Nodes |> List.tryFind (fun n -> n.Id = certificate.Body.Root)

    /// The peer's sealed stance for the root fact, or the refusal that
    /// stands in its place. Reads only from the SIGNED body.
    let private declaredStance (certificate: GroundingCertificate) : Result<Disclosure, FactImportRefusal> =
        match rootNode certificate with
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

                    // (1) Verify offline against this peer's key alone.
                    //
                    // No subject expectation is passed. The envelope helper
                    // would happily check one, but folding the id
                    // comparison into the signature check would collapse
                    // two answers a holder acts on differently — "this
                    // document does not verify" and "this document is
                    // genuine and about a different fact" — into a single
                    // verdict. The comparison happens below, AFTER the
                    // signature is established, which is what makes the
                    // mismatch verdict's claim true.
                    match CertificateEnvelope.verifyAndReadJson anchor.PublicKey None certificateJson with
                    | Error verdict -> return! refuse payload (ImportUnverifiable verdict)
                    | Ok certificate ->
                        let ref' = certificateRef certificate

                        let payload = {
                            payload with
                                CertificateRoot = certificate.Body.Root
                                CertificateRef = ref'
                        }

                        // (2) Re-derive and compare. The content address
                        // covers the subject, the metric, the period, the
                        // peer's method identity AND the input hashes, so
                        // a single altered field in the offer lands here
                        // rather than in the store.
                        if certificate.Body.Root <> derived then
                            return! refuse payload (ImportContentIdMismatch(certificate.Body.Root, derived))
                        else
                            // The envelope's own statement subject must
                            // name the root its predicate carries. Both are
                            // inside the signed bytes, so this catches a
                            // self-inconsistent statement rather than a
                            // tampered one — cheap, crypto-free, and the
                            // last structural thing left to check.
                            match
                                DsseEnvelope.parse certificateJson
                                |> Result.mapError EnvelopeMalformed
                                |> Result.bind (
                                    DsseEnvelope.checkShape (CertificateEnvelope.expectation (Some derived))
                                )
                            with
                            | Error verdict -> return! refuse payload (ImportUnverifiable verdict)
                            | Ok _ ->
                                match declaredStance certificate with
                                | Error refusal -> return! refuse payload refusal
                                | Ok declared ->
                                    // (3) The conservative floor. An
                                    // anchor with no ceiling leaves the
                                    // peer's stance exactly as sealed.
                                    let effective = Disclosure.floor declared anchor.DisclosureCeiling

                                    let payload = {
                                        payload with
                                            DeclaredDisclosure = Disclosure.toString declared
                                            EffectiveDisclosure = Disclosure.toString effective
                                    }

                                    // (4) Assert with `Imported`
                                    // provenance. The method changes — an
                                    // imported fact is this deployment's
                                    // assertion that a peer computed
                                    // something, not a claim to have
                                    // computed it — so the local id
                                    // differs from the peer's by
                                    // construction, and the certificate
                                    // ref is what joins them.
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
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Remoting.Json.SystemTextJson

// ─── The joined evidence chain as a signed statement ────────────────────
//
// The walk exists and produces a chain; the chain is a value inside one
// deployment. A counterparty cannot hold it, cannot address it, and can
// check it only by asking the deployment that produced it — which is the
// one party whose word the counterparty is trying not to have to take.
//
// This file is the **exporter**: the chain packaged as an
// `EvidenceBundle` (declared in `Platform.Core`, with the reasoning about
// its claim boundary and its two rulings), addressed by a content id over
// a canonical framing, and wrapped as a DSSE-signed in-toto Statement
// through the envelope family this SDK already ships. Plus the offline
// reader and the one-command verify entry a party with no platform
// running at all can use.
//
// **No new cryptography (GP 2), and none of it lives here.** The only
// primitive this file touches is SHA-256, which the envelope module
// already uses for its own digests. Signing is `IStatementEnvelopeSigner`
// — the same seam the certificate exporter signs through, filled by the
// signing companion from the same key material and key id. Signature
// VERIFICATION is likewise the companion's; this file deliberately stops
// at the structural half, and the split is not an omission:
//
//   * the format half is **crypto-free**, so a consumer can read a
//     bundle's shape, its chain, and its claim boundary with nothing but
//     a JSON parser and a hash — no verifier, no key, no toolchain; and
//   * the signature half is a standard DSSE check, which means an
//     unmodified cosign-class tool does it, and there is nothing
//     bespoke for a counterparty to trust or install.
//
// **A structural pass is not a signature pass, and the two names say
// so.** `verifyDocument` returns `BundleIntegrity` and its documentation
// is explicit that it establishes nothing about the outer signature. A
// holder that wants both runs the stock DSSE verification and this; a
// holder that runs only this has checked that the document is
// self-consistent, which is a real and useful property and is not the
// same as knowing who wrote it.
//
// **Nothing here is reachable unless a deployment asks for it.** No
// composition changes, no DI registration, no hosted service, no route: a
// deployment that never exports a bundle pays nothing (GP 13), and one
// that never composes a chain walker is untouched.

/// The bundle exporter: projection, statement wrapping, offline reading,
/// and the verify command.
[<RequireQualifiedAccess>]
module EvidenceBundleExport =

    /// The versioned predicate type URI — the open interchange identifier
    /// for a joined evidence chain carried as an in-toto predicate.
    ///
    /// **Its own type, deliberately.** A bundle is a different claim from
    /// a certificate and a different claim from a ledger segment, even
    /// where the three quote overlapping records: a certificate says what
    /// a value was grounded in, a segment says what a party's slice of a
    /// chain contains, and a bundle says which records this deployment
    /// observed and how they join. A verifier keys on `predicateType` to
    /// decide what shape it is about to read, so publishing two shapes
    /// under one URI would make that key meaningless — which is exactly
    /// what `EnvelopePredicateTypeMismatch` exists to report.
    [<Literal>]
    let PredicateType = "https://toolup-forge.io/attestations/evidence-chain-bundle/v1"

    let private jsonOptions = FableConverters.create ()

    /// Lowercase-hex SHA-256 over a canonical form's UTF-8 bytes.
    ///
    /// Server-side because `System.Security.Cryptography` is not
    /// Fable-compilable; the canonical forms it hashes are declared in
    /// `Platform.Core`, so any host recomputes the same values from the
    /// same document. This is the function a caller hands to
    /// `EvidenceBundle.verifyWith`.
    let digest (canonical: string) : string =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes canonical))

    // ── The claim boundary ──────────────────────────────────────────────

    /// What a bundle does NOT prove, carried as data on every bundle.
    ///
    /// **The point of the whole artefact, and the reason these are values
    /// rather than prose in a migration note.** A bundle that enumerated
    /// only what it checked would invite its reader to believe the
    /// complement was checked too — and the complement here is large: a
    /// chain is a statement about RECORDS, and every one of them was
    /// written by the deployment now offering them.
    ///
    /// Statements are appended, never inserted, for the reason the
    /// canonical form is order-sensitive: inserting among them would move
    /// the lines of every statement after it, and a reader diffing two
    /// canonical forms across an upgrade could not tell a re-ordering
    /// from a re-statement.
    ///
    /// `Narrowing` is resolved against the chain in hand, and narrowing
    /// is never closing: a complete chain shrinks the first statement to
    /// name what the joins do cover, and leaves the rest standing whole.
    let notProvedFor (chain: EvidenceChain) : DeploymentVerification.NotProvedStatement list =
        let linked =
            chain.Hops
            |> List.filter (fun hop -> EvidenceLink.isLinked hop.Link)
            |> List.length

        [
            {
                Id = "records-not-truth"
                Statement =
                    "This bundle attests that these records, carrying these digests, joined in this way, were observed by the named deployment at the named time. It does not attest that the records are true. Every digest it quotes was computed over something the deployment itself recorded."
                Narrowing =
                    if linked > 0 then
                        Some
                            $"{linked} of the {List.length chain.Hops} hops resolve, so those joins are re-derivable by a holder from the references quoted — which establishes that the two records agree with each other, and still nothing about whether either is true"
                    else
                        None
            }
            {
                Id = "work-quality-not-claimed"
                Statement =
                    "A resolved upstream work record names the work the deployment's sources are attributed to. Nothing here speaks to whether that work was done well, reviewed, or done by whom it says."
                Narrowing = None
            }
            {
                Id = "uncomposed-substrate-is-silent"
                Statement =
                    "A hop reading absent means nothing in this deployment joins the two facts it spans. That is a bound on the bundle, not a clean bill: a substrate that was never composed produces no evidence and no absence anywhere except the hop that names it."
                Narrowing = None
            }
            {
                Id = "code-never-composed-behaved"
                Statement =
                    "Every check behind these hops compares recorded artefacts to each other. None of them observes running behaviour, so a composition that matches its seal and a composition that behaved as declared are two different claims, and only the first is made here."
                Narrowing = None
            }
            {
                Id = "signature-binds-the-document"
                Statement =
                    "The signature over this bundle binds this document to the key that signed it. It does not extend to the inner attestations the chain quotes: those carry their own signatures, verifiable against their own keys, and this bundle carries them verbatim rather than re-asserting them."
                Narrowing = None
            }
        ]

    // ── A — the bundle projection ───────────────────────────────────────

    /// Package a walked chain as a bundle, addressed by its content id.
    ///
    /// The single construction point, so a bundle's content id has
    /// exactly one definition and cannot be reached by another path.
    /// `observer` is the deployment's own opaque id and `observedAt` its
    /// clock; neither reaches the content id (see the type's header), so
    /// two bundles taken from an unchanged deployment are addressed
    /// identically however far apart they were taken.
    let bundleWith
        (qualifiers: BundleClaimQualifier list)
        (observer: string)
        (observedAt: DateTime)
        (chain: EvidenceChain)
        : EvidenceBundle =
        let unaddressed = {
            SchemaVersion = EvidenceBundle.SchemaVersion
            NestedAttestationDisposition = EvidenceBundle.CarriedVerbatim
            Observer = observer
            ObservedAt = observedAt
            Chain = chain
            NotProved = notProvedFor chain
            Qualifiers = qualifiers
            ContentId = ""
        }

        {
            unaddressed with
                ContentId = digest (EvidenceBundle.canonicalForm unaddressed)
        }

    /// Package a walked chain as a bundle stating its own
    /// enumeration-completeness verdict — the shape a deployment exports.
    ///
    /// **The verdict is stated on every bundle, including a `complete`
    /// one.** A qualifier that appeared only where the walk enumerated
    /// less than its linkage named would be a caveat nobody reads, and
    /// its absence would be ambiguous between "this walk was complete"
    /// and "this producer does not measure completeness" — which is the
    /// silence the claim exists to break.
    ///
    /// It rides as a qualifier rather than in the chain's own canonical
    /// form because the chain's verdict digest names the LINK SET, and
    /// this is a statement about the enumeration behind the links. The
    /// bundle's content id covers qualifiers, so the verdict is bound
    /// here; and the structural verifier refuses a document whose
    /// qualifier and chain disagree, so it cannot be swapped for a
    /// friendlier one without re-addressing the bundle.
    let bundleOf (observer: string) (observedAt: DateTime) (chain: EvidenceChain) : EvidenceBundle =
        bundleWith [ EvidenceBundle.enumerationQualifier chain.Enumeration ] observer observedAt chain

    /// The canonical bytes the content id addresses.
    ///
    /// **This is the artefact a stock DSSE tool hashes.** Written beside
    /// the envelope, these bytes are the "blob" whose SHA-256 must equal
    /// the statement's subject digest — which is what lets an unmodified
    /// cosign-class verifier check the subject claim as well as the
    /// signature, with nothing that understands this SDK.
    let canonicalBytes (bundle: EvidenceBundle) : byte[] =
        Encoding.UTF8.GetBytes(EvidenceBundle.canonicalForm bundle)

    // ── B — statement wrapping ──────────────────────────────────────────

    /// The in-toto subject for a bundle: its content id under the
    /// `sha256` digest key, which is what the id genuinely is.
    let subjectFor (bundle: EvidenceBundle) : InTotoSubject = {
        Name = EvidenceBundle.SubjectName
        Digest = [ "sha256", bundle.ContentId ]
    }

    /// The predicate JSON for a bundle — the bundle record itself, in the
    /// versioned shape this substrate publishes as its interchange
    /// format. The chain rides inside it verbatim, seal references
    /// included, per the transcode ruling.
    let predicateJson (bundle: EvidenceBundle) : string =
        JsonSerializer.Serialize(bundle, jsonOptions)

    /// The in-toto statement JSON for a bundle, unsigned. Exposed for
    /// tests and for a caller that signs through its own path.
    let statementJson (bundle: EvidenceBundle) : string =
        DsseEnvelope.statementJson [ subjectFor bundle ] PredicateType (predicateJson bundle)

    /// Export a bundle as a signed DSSE envelope. **One** signature, over
    /// the pre-authentication encoding of the assembled statement, from
    /// the deployment's own key.
    ///
    /// One and not two: the inner attestations the chain quotes are
    /// carried, not re-asserted, so there is nothing for a second
    /// signature to say that the first does not.
    let export (signer: IStatementEnvelopeSigner) (bundle: EvidenceBundle) : Async<Result<DsseEnvelope, string>> =
        DsseEnvelope.sign signer [ subjectFor bundle ] PredicateType (predicateJson bundle)

    /// What a holder requires of a bundle envelope. `expectedContentId`
    /// is the id the holder independently possesses; `None` skips the
    /// subject check, which is only right when the caller has no
    /// independent handle on the artefact.
    let expectation (expectedContentId: string option) : EnvelopeExpectation = {
        PredicateType = PredicateType
        SubjectDigest = expectedContentId
    }

    // ── D — reading and verifying, offline ──────────────────────────────

    /// Read a bundle out of a predicate. Used on a predicate that has
    /// already been signature-verified (the companion's
    /// `verify` returns exactly that string), and by the crypto-free
    /// document reader below.
    let readBundle (predicate: string) : Result<EvidenceBundle, EnvelopeVerdict> =
        try
            let bundle = JsonSerializer.Deserialize<EvidenceBundle>(predicate, jsonOptions)

            if obj.ReferenceEquals(bundle.NestedAttestationDisposition, null) then
                Error(EnvelopeMalformed "predicate is not an evidence bundle (no nested-attestation disposition)")
            elif obj.ReferenceEquals(bundle.Chain, null) then
                Error(EnvelopeMalformed "predicate is not an evidence bundle (no chain)")
            else
                // A list field absent from persisted JSON deserialises to
                // `null` on this converter set, and a null F# list throws
                // on every list operation. Coerced here rather than at
                // each read site, so a stripped document reaches the
                // verifier as the empty list it claims to be and is
                // refused by name.
                let coalesce value fallback =
                    if obj.ReferenceEquals(value, null) then fallback else value

                Ok {
                    bundle with
                        NotProved = coalesce bundle.NotProved []
                        Qualifiers = coalesce bundle.Qualifiers []
                        Chain = {
                            bundle.Chain with
                                Hops = coalesce bundle.Chain.Hops []
                        }
                }
        with ex ->
            Error(EnvelopeMalformed $"predicate is not a readable evidence bundle: {ex.Message}")

    /// Verify a bundle structurally, with this host's SHA-256.
    ///
    /// The `EvidenceBundle.verifyWith` contract in full — including what
    /// it deliberately does not establish — is on that function.
    let verifyBundle (bundle: EvidenceBundle) : BundleIntegrity = EvidenceBundle.verifyWith digest bundle

    /// Read a bundle out of a DSSE document **without checking its
    /// signature**, and report its structural integrity.
    ///
    /// **Named for the hazard.** This reads a payload nobody has
    /// authenticated, and that is legitimate here for one reason only:
    /// the answer it produces makes no claim about authorship. It says
    /// whether the document is internally consistent — whether the hops
    /// are the walk's hops, the digests are digests of what they claim,
    /// and the claim boundary is present. A tampered document fails it; a
    /// wholly fabricated one passes it, and passes it honestly, because
    /// "this is a well-formed bundle" and "this bundle is yours" are
    /// different questions and only the second needs a key.
    ///
    /// A holder that wants both runs the stock DSSE signature check
    /// alongside. The predicate type is still enforced here: a statement
    /// of another shape is `BrokenAt` rather than parsed hopefully.
    let verifyDocument (json: string) : BundleIntegrity =
        match DsseEnvelope.parse json with
        | Error reason ->
            BundleIntegrity.BrokenAt("document/envelope", $"the DSSE envelope could not be read: {reason}")
        | Ok envelope ->
            if envelope.PayloadType <> DsseEnvelope.InTotoPayloadType then
                BundleIntegrity.BrokenAt(
                    "document/payloadType",
                    $"the envelope declares payload type '{envelope.PayloadType}' where an in-toto statement is '{DsseEnvelope.InTotoPayloadType}'"
                )
            else
                match DsseEnvelope.readStatement envelope with
                | Error verdict -> BundleIntegrity.BrokenAt("document/statement", EnvelopeVerdict.describe verdict)
                | Ok statement ->
                    if statement.PredicateType <> PredicateType then
                        BundleIntegrity.BrokenAt(
                            "document/predicateType",
                            $"the statement declares predicate type '{statement.PredicateType}', which is not the evidence-bundle type '{PredicateType}' — a reader is told what it is holding rather than what it is not"
                        )
                    else
                        match readBundle statement.PredicateJson with
                        | Error verdict ->
                            BundleIntegrity.BrokenAt("document/predicate", EnvelopeVerdict.describe verdict)
                        | Ok bundle ->
                            match verifyBundle bundle with
                            | BundleIntegrity.BrokenAt(position, reason) -> BundleIntegrity.BrokenAt(position, reason)
                            | BundleIntegrity.Intact ->
                                // The subject is the holder's claim check
                                // and it is checked LAST, so a document
                                // that is internally broken is reported
                                // as broken where it broke rather than as
                                // a subject mismatch.
                                if statement.SubjectDigests |> List.contains bundle.ContentId then
                                    BundleIntegrity.Intact
                                else
                                    let published = statement.SubjectDigests |> String.concat ", "

                                    BundleIntegrity.BrokenAt(
                                        "document/subject",
                                        $"the statement publishes subject digest(s) '{published}' and the bundle inside it is addressed '{bundle.ContentId}' — a correctly-shaped statement about a different bundle"
                                    )

    // ── The one-command entry ───────────────────────────────────────────

    /// What one verify invocation produced: the verdict, the text a
    /// caller prints, and the process exit code.
    ///
    /// Mirrors the deployment verification report's CI shape on purpose —
    /// a party checking a bundle and an operator checking a deployment
    /// should not have to learn two conventions.
    type BundleVerificationRun = {
        Integrity: BundleIntegrity
        /// Operator-facing text: the verdict, then — on a readable
        /// document — the bundle itself, claim boundary included.
        Report: string
        /// Zero when intact, one otherwise. A broken bundle exits
        /// non-zero because it is the state tampering produces, and a
        /// zero there would make altering a bundle cheaper than
        /// re-exporting one.
        ExitCode: int
    }

    /// Verify one DSSE document and produce the run.
    ///
    /// **The whole input is the document.** No deployment, no store, no
    /// network, no configuration and no key — which is what makes this
    /// runnable by a counterparty who has only the file, and runnable in
    /// a CI job that has only the file. A composition root invokes the
    /// same function; there is one verification path and it is this one.
    ///
    /// The claim boundary is printed on a PASS as well as a failure. A
    /// reader who runs this and sees "intact" is told, in the same
    /// output, what intact does not mean.
    let verifyCommand (json: string) : BundleVerificationRun =
        let integrity = verifyDocument json

        // The document is re-read for rendering rather than threaded out
        // of the verifier, so the verifier's answer stays a verdict and
        // gains no reason to hand back a payload it may have refused.
        // Unreadable at this point simply means the verdict above already
        // said why.
        let bundle =
            match DsseEnvelope.parse json with
            | Error _ -> None
            | Ok envelope ->
                match DsseEnvelope.readStatement envelope with
                | Error _ -> None
                | Ok statement ->
                    match readBundle statement.PredicateJson with
                    | Error _ -> None
                    | Ok bundle -> Some bundle

        {
            Integrity = integrity
            // Rendered by `Platform.Core`, so a party checking this
            // document offline with nothing but those source files
            // reaches the same bytes.
            Report = EvidenceBundle.verificationReport integrity bundle
            ExitCode = if BundleIntegrity.isIntact integrity then 0 else 1
        }

    // ── Composition-root convenience ────────────────────────────────────

    /// Walk this deployment's chain and package it, in one call — the
    /// entry a composition root reaches for.
    ///
    /// Goes through `EvidenceChainWalker.run`, so the audited read is
    /// recorded exactly as it is for any other walk: producing evidence
    /// leaves evidence, and exporting it does not become a way to walk
    /// the chain unobserved. A refused walk returns the refusal
    /// unchanged rather than bundling a partial answer.
    let bundleFor
        (services: IServiceProvider)
        (mode: EvidenceChainWalkerMode)
        (request: EvidenceChainRequest)
        (observer: string)
        : Async<Result<EvidenceBundle, EvidenceChainError>> =
        async {
            match! EvidenceChainWalker.run services mode request with
            | Result.Error error -> return Result.Error error
            | Result.Ok chain -> return Result.Ok(bundleOf observer DateTime.UtcNow chain)
        }
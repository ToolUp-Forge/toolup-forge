// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FactImportTests

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json.Nodes
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.ArtefactSigning
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts

// ─── Certificate-verified fact import ────────────────────────────────────
//
// The consuming half of the imported-fact provenance case. An imported fact
// used to be testimony wearing a provenance field: the certificate ref named
// a document nobody had checked. This pack holds the door to the four claims
// it makes.
//
// It is an INTEGRATION pack by necessity. The pieces are unit-tested where
// they live — the envelope encoder against reference vectors, the signing
// seams in their own packs — and what cannot be checked there is that TWO
// deployments, each with their own key and their own store, exchange a fact
// whose identity survives the crossing. So the probes run a real issuing
// deployment against a real importing one, and the only thing that passes
// between them is a document and a public key.
//
// The refusal probes all assert the same second thing: that the importing
// store is still EMPTY afterwards. "Refused" and "refused, and nothing
// landed" are different claims, and only the second one is worth anything.

/// Minimal in-memory `ISecretStore`. Each deployment gets its own — which
/// is the point: no key material is shared, and the importing side never
/// sees anything but a public key.
type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

/// Records what the import door audited, so the trail can be asserted on
/// rather than assumed.
type private RecordingAuditLog() =
    let recorded = ConcurrentQueue<AuditEvent>()

    member _.Events = recorded |> List.ofSeq

    member this.OfType(name: string) =
        this.Events |> List.filter (fun e -> AuditEvent.eventTypeName e = name)

    interface IAuditLog with
        member _.Record(_scopeId, audit) = async { recorded.Enqueue audit }

        member _.GetAuditTrail(_scopeId, _dateRange, _eventType) = async { return recorded |> List.ofSeq }

let private scopeA = "team-issuer"
let private scopeB = "team-importer"

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draft metric value disclosure : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ "uk"; "north" ]
    }
    Metric = MetricRef metric
    Value = Scalar value
    Period = q2
    Method = Computed("rollup", "3", "p7")
    Evidence = {
        ResultRef = None
        InputHashes = [ "sha256:input-a"; "sha256:input-b" ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = disclosure
}

/// One deployment: its own key, its own stores, its own certificate issuer.
type private Deployment = {
    KeyId: string
    Secrets: ISecretStore
    Store: IFactStore
    Issuer: IGroundingCertificateIssuer
    PublicKey: PublicKeyMetadata
    EnvelopeSigner: IStatementEnvelopeSigner
    /// The byte-level seal, exposed so a probe can hand-build a certificate
    /// body the issuer would never produce and seal it GENUINELY — the only
    /// way to reach a refusal that guards against a malformed-but-authentic
    /// document.
    Signer: IArtefactSigner
}

/// Build a deployment. `gate` is supplied so a probe can give the issuing
/// side a policy vocabulary that permits a `Restricted` fact at its export
/// door — which is what makes the peer's own restriction observable on the
/// importing side.
let private deploymentWith (keyId: string) (gateFor: IFactStore -> IEventStore -> IFactDisclosureGate) = async {
    let secrets = InMemorySecretStore() :> ISecretStore
    let audit = AuditLog.NoOpAuditLog() :> IAuditLog
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage.InMemoryBlobStorage()) events
    let gate = gateFor store events
    let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

    let graph =
        ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create store)

    let signer = DefaultArtefactSigner.createSystem secrets audit keyId Ed25519
    let! publicKey = signer.VerifyKey()

    return {
        KeyId = keyId
        Secrets = secrets
        Store = store
        Issuer = GroundingCertificate.createIssuer graph store gate events (Some signer)
        PublicKey = publicKey
        EnvelopeSigner = DsseEnvelopeSigning.fromSecretStore secrets keyId Ed25519
        Signer = signer
    }
}

let private deployment (keyId: string) =
    deploymentWith keyId (fun store events -> FactDisclosureGate.create store events)

let private seed (d: Deployment) scopeId metric value disclosure = async {
    match! d.Store.Assert(scopeId, draft metric value disclosure) with
    | Ok fact -> return fact
    | Error e -> return failtestf "seeding the fact store failed: %s" e
}

/// Issue a certificate over `factId` and export it as the signed statement
/// a peer receives — the whole of what crosses between deployments.
let private certificateJsonFor (d: Deployment) scopeId (factId: string) = async {
    match! d.Issuer.Issue(scopeId, "exporter", FactCertificate factId, 5) with
    | Error e -> return failtestf "certificate issue must succeed: %s" (CertificateError.describe e)
    | Ok certificate ->
        match! CertificateEnvelope.export d.EnvelopeSigner certificate with
        | Error e -> return failtestf "envelope export must succeed: %s" e
        | Ok envelope -> return certificate, DsseEnvelope.toJson envelope
}

/// Genuinely seal a hand-built certificate body and export it, so a probe
/// can present a document that is authentic in every cryptographic sense
/// and structurally wrong in exactly one way. Nothing about these is
/// forged — which is the point: the refusals below are not the signature
/// check doing the work a second time.
let private sealBody (d: Deployment) (body: GroundingCertificateBody) = async {
    let canonical = GroundingCertificate.canonicalise body

    match! d.Signer.Sign(GroundingCertificate.canonicalBytes canonical) with
    | Error e -> return failtestf "sealing the fixture body must succeed: %s" (SigningError.describe e)
    | Ok signature ->
        match!
            CertificateEnvelope.export d.EnvelopeSigner {
                Body = canonical
                Signature = signature
            }
        with
        | Error e -> return failtestf "exporting the fixture envelope must succeed: %s" e
        | Ok envelope -> return DsseEnvelope.toJson envelope
}

/// A minimal, well-formed body rooted at `root` carrying `nodes`.
let private bodyWith (keyId: string) (root: string) (nodes: CertificateNode list) : GroundingCertificateBody = {
    Format = GroundingCertificate.Format
    Root = root
    IssuedAt = DateTimeOffset(DateTime(2026, 8, 23, 9, 0, 0, DateTimeKind.Utc))
    DeploymentKeyId = keyId
    Nodes = nodes
    Edges = []
    PolicyRefs = []
}

/// An importing deployment's door, over its own store.
let private doorFor (d: Deployment) (anchors: PeerTrustAnchor list) (audit: RecordingAuditLog) =
    FactImport.create d.Store anchors (audit :> IAuditLog)

/// Nothing landed: the definitive half of every refusal probe.
let private expectStoreEmpty (d: Deployment) = async {
    let! facts =
        d.Store.Query(
            scopeB,
            {
                FactQuery.all with
                    IncludeSuperseded = true
            }
        )

    Expect.isEmpty facts "a refused import must leave the store untouched"
}

/// Re-encode an envelope's payload after rewriting the statement text —
/// a tamper that is genuine (the bytes the signature covers change) and
/// meaningful (it re-points the certificate at another fact).
let private tamperStatement (find: string) (replace: string) (json: string) =
    match DsseEnvelope.parse json with
    | Error e -> failtestf "the exported envelope must parse: %s" e
    | Ok envelope ->
        let statement = Convert.FromBase64String envelope.Payload |> Encoding.UTF8.GetString

        Expect.stringContains statement find "the tamper must actually find its target"

        DsseEnvelope.toJson {
            envelope with
                Payload =
                    statement.Replace(find, replace)
                    |> Encoding.UTF8.GetBytes
                    |> Convert.ToBase64String
        }

// ── the attested projection at the door ─────────────────────────────────
//
// The peer running the levels-bound seal exports the SAME body through a
// different projection. Every fixture below therefore builds the direct
// certificate first and re-seals its body through the application signing
// seam: byte-identical bodies is the property this leans on, not one it
// re-establishes, and building the body twice would be testing the fixture.

/// An `IApplicationSigner` over a deployment's own secrets at a chosen
/// attestation level.
///
/// The in-process provider fixes the level at `Attribution`, which is the
/// honest claim for a key the signing process can read. Overriding it is
/// how a test reaches the other levels — the custody has not changed, so
/// this is a fixture and never a pattern for a composition root.
let private applicationSignerAt (d: Deployment) (level: AttestationLevel) : IApplicationSigner =
    let audit = AuditLog.NoOpAuditLog() :> IAuditLog

    {
        ApplicationSigning.inProcess d.Secrets audit d.KeyId Ed25519 "system" with
            Level = level
    }
    |> ApplicationSigning.create

/// Re-seal `body` through the application signing seam at `level` and
/// export it as the attested projection's signed statement.
let private attestedJsonOf (d: Deployment) (level: AttestationLevel) (body: GroundingCertificateBody) = async {
    let signer = applicationSignerAt d level
    let canonical = GroundingCertificate.canonicalise body

    match!
        signer.SignPayload(GroundingCertificate.AttestationPurpose, GroundingCertificate.canonicalBytes canonical)
    with
    | Error e -> return failtestf "sealing the attested certificate must succeed: %s" (SigningError.describe e)
    | Ok envelope ->
        let certificate: AttestedGroundingCertificate = {
            Body = canonical
            Envelope = envelope
        }

        match! CertificateEnvelope.exportAttested d.EnvelopeSigner certificate with
        | Error e -> return failtestf "exporting the attested envelope must succeed: %s" e
        | Ok exported -> return certificate, DsseEnvelope.toJson exported
}

/// Issue a certificate over `factId` and hand back its ATTESTED projection
/// — the document a peer running the levels-bound seal offers.
let private attestedCertificateJsonFor (d: Deployment) scopeId (factId: string) (level: AttestationLevel) = async {
    match! d.Issuer.Issue(scopeId, "exporter", FactCertificate factId, 5) with
    | Error e -> return failtestf "certificate issue must succeed: %s" (CertificateError.describe e)
    | Ok certificate -> return! attestedJsonOf d level certificate.Body
}

let private restrictedPolicy = "policy:partner-terms"

/// A gate whose vocabulary permits `restrictedPolicy` at the export door,
/// so a `Restricted` fact is disclosed in the certificate rather than
/// withheld — the only way the peer's own restriction can be observed on
/// the far side.
let private exportPermittingGate store events =
    FactDisclosureGate.createWithTaint
        (DisclosureTaintConfig.ofLists [
            {
                PolicyRef = restrictedPolicy
                Mode = Plain
                PermitSurfaces = [ FactExport ]
                ContributorScope = None
            }
        ] [])
        store
        events

let tests =
    testList "Phase 683 — certificate-verified fact import" [

        // ── the disclosure lattice, on its own ──────────────────────────

        testList "the conservative floor" [
            test "Surfaceable is the identity, so a boundary with no ceiling changes nothing" {
                Expect.equal (Disclosure.floor Surfaceable Surfaceable) Surfaceable "top meets top"

                Expect.equal
                    (Disclosure.floor (Restricted "p") Surfaceable)
                    (Restricted "p")
                    "a ceiling of Surfaceable narrows nothing"

                Expect.equal (Disclosure.floor Internal Surfaceable) Internal "and widens nothing"
            }

            test "Internal is the bottom — it is denied at every surface unconditionally" {
                Expect.equal (Disclosure.floor Internal (Restricted "p")) Internal "below any policy"
                Expect.equal (Disclosure.floor Surfaceable Internal) Internal "and below Surfaceable"
            }

            test "two policies that are not the same policy meet at Internal" {
                // Nothing tells this deployment that satisfying one
                // satisfies the other. Picking either would assert a
                // permission neither source granted.
                Expect.equal (Disclosure.floor (Restricted "p") (Restricted "q")) Internal "incomparable ⇒ bottom"

                Expect.equal
                    (Disclosure.floor (Restricted "p") (Restricted "p"))
                    (Restricted "p")
                    "the same policy meets at itself"
            }

            test "the floor is commutative — neither side of a boundary is privileged" {
                let stances = [ Surfaceable; Internal; Restricted "p"; Restricted "q" ]

                for a in stances do
                    for b in stances do
                        Expect.equal
                            (Disclosure.floor a b)
                            (Disclosure.floor b a)
                            $"floor {Disclosure.toString a} {Disclosure.toString b} must not depend on order"
            }

            test "a stance round-trips through the rendering a certificate carries" {
                for stance in [ Surfaceable; Internal; Restricted restrictedPolicy ] do
                    Expect.equal
                        (Disclosure.tryParse (Disclosure.toString stance))
                        (Some stance)
                        "toString and tryParse are one wire shape, not two"

                Expect.isNone (Disclosure.tryParse "Public") "an unrecognised stance is never defaulted"
                Expect.isNone (Disclosure.tryParse "Restricted(") "nor is a malformed one"
            }
        ]

        // ── the round trip: issue on A, import on B, re-certify on B ────

        testCaseAsync "a fact issued on one deployment imports on another and re-certifies naming the imported ref"
        <| async {
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let! certificate, json = certificateJsonFor a scopeA original.FactId

            let door = doorFor b [ PeerTrustAnchor.create "deployment-a" a.PublicKey ] audit

            match! door.Import(scopeB, "deployment-a", ImportedFactOffer.ofFact original, json) with
            | Error refusal -> failtestf "a sound import must succeed: %s" (FactImportRefusal.describe refusal)
            | Ok imported ->
                // The identity tuple survived the crossing intact — which
                // is the only reason the certificate could be checked
                // against it at all.
                Expect.equal imported.Subject original.Subject "the subject crossed unchanged"
                Expect.equal imported.Metric original.Metric "and the metric"
                Expect.equal imported.Value original.Value "and the value"
                Expect.equal imported.Period original.Period "and the period"

                let expectedRef = FactImport.certificateRef certificate

                match imported.Method with
                | Imported ref' -> Expect.equal ref' expectedRef "the import carries the verified certificate's own ref"
                | other -> failtestf "an imported fact must carry Imported provenance; got %A" other

                // The local id differs by construction: the method is part
                // of the content address, and this deployment is asserting
                // that a peer computed something, not that it did.
                Expect.notEqual imported.FactId original.FactId "the local assertion has its own identity"

                Expect.equal
                    imported.Disclosure
                    Surfaceable
                    "with no ceiling declared, the peer's own sealed stance governs"

                // A ref is recomputable by anyone holding the certificate,
                // which is what makes it a join key rather than a handle.
                Expect.stringStarts expectedRef FactImport.CertificateRefPrefix "the ref says what it is"

                Expect.equal
                    (FactImport.certificateRef certificate)
                    expectedRef
                    "and is content-addressed, so it is stable"

                // Re-certify on B. The chain the new certificate seals must
                // name the imported certificate — the join back to the
                // deployment the number actually came from.
                match! b.Issuer.Issue(scopeB, "auditor", FactCertificate imported.FactId, 5) with
                | Error e -> failtestf "the re-issue must succeed: %s" (CertificateError.describe e)
                | Ok reissued ->
                    Expect.equal reissued.Body.Root imported.FactId "rooted at the imported fact"

                    let rootNode = reissued.Body.Nodes |> List.tryFind (fun n -> n.Id = imported.FactId)

                    match rootNode with
                    | None -> failtest "the re-issued chain must carry a node for its own root"
                    | Some node ->
                        Expect.equal node.Kind "Fact" "the root is a fact node"

                        Expect.equal
                            node.CertificateRef
                            (Some expectedRef)
                            "and the chain names the certificate the fact was imported under"

                        Expect.equal
                            node.Method
                            (Some(Fact.methodIdentity (Imported expectedRef)))
                            "the method identity states the origin rather than laundering it"

                // One accepted row, recording both stances.
                match audit.OfType "FactImportAccepted" with
                | [ FactImportAccepted payload ] ->
                    Expect.equal payload.PeerId "deployment-a" "the row names the peer the caller stated"
                    Expect.equal payload.PeerKeyId a.KeyId "and the key its anchor holds"
                    Expect.equal payload.CertificateRoot original.FactId "the root the certificate covers"
                    Expect.equal payload.DerivedFactId original.FactId "which the door re-derived for itself"
                    Expect.equal payload.ImportedFactId imported.FactId "and the local id it asserted"
                    Expect.equal payload.DeclaredDisclosure "Surfaceable" "the stance the peer sealed"
                    Expect.equal payload.EffectiveDisclosure "Surfaceable" "and the one it landed under"
                    Expect.equal payload.Reason "" "an accepted import states no reason"
                | other -> failtestf "expected exactly one accepted row; got %A" other

                Expect.isEmpty (audit.OfType "FactImportRefused") "and no refusal beside it"
        }

        // ── disclosure mapping, both directions ─────────────────────────

        testCaseAsync "the importer's ceiling narrows a peer's Surfaceable fact"
        <| async {
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let! _, json = certificateJsonFor a scopeA original.FactId

            let anchor =
                PeerTrustAnchor.create "deployment-a" a.PublicKey
                |> PeerTrustAnchor.withCeiling (Restricted "policy:third-party")

            match!
                (doorFor b [ anchor ] audit).Import(scopeB, "deployment-a", ImportedFactOffer.ofFact original, json)
            with
            | Error refusal -> failtestf "the import must succeed: %s" (FactImportRefusal.describe refusal)
            | Ok imported ->
                Expect.equal
                    imported.Disclosure
                    (Restricted "policy:third-party")
                    "the importing deployment's ceiling narrows what a peer offered freely"

                match audit.OfType "FactImportAccepted" with
                | [ FactImportAccepted payload ] ->
                    Expect.equal payload.DeclaredDisclosure "Surfaceable" "the trail records what the peer declared"

                    Expect.equal
                        payload.EffectiveDisclosure
                        "Restricted(policy:third-party)"
                        "beside what it landed under, so the narrowing is visible from the row alone"
                | other -> failtestf "expected one accepted row; got %A" other
        }

        testCaseAsync "a peer's own restriction survives an importer that imposes no ceiling"
        <| async {
            // The other direction, and the one that matters: the importing
            // deployment declares nothing, so the ONLY thing standing
            // between the peer's restriction and a Surfaceable local fact
            // is that the stance is read out of the signed certificate.
            let! a = deploymentWith "deployment-a-v1" exportPermittingGate
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "margin" 42m (Restricted restrictedPolicy)
            let! _, json = certificateJsonFor a scopeA original.FactId

            match!
                (doorFor b [ PeerTrustAnchor.create "deployment-a" a.PublicKey ] audit)
                    .Import(scopeB, "deployment-a", ImportedFactOffer.ofFact original, json)
            with
            | Error refusal -> failtestf "the import must succeed: %s" (FactImportRefusal.describe refusal)
            | Ok imported ->
                Expect.equal
                    imported.Disclosure
                    (Restricted restrictedPolicy)
                    "an import cannot widen: the peer's policy ref crosses intact"

                // And the local gate, which knows nothing of that policy,
                // denies it — the conservative resolution, unchanged.
                let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
                let gate = FactDisclosureGate.create b.Store events
                let! verdicts = gate.Check(scopeB, "reader", FactExport, [ imported.FactId ])

                match verdicts.TryFind imported.FactId with
                | Some(FactNotDisclosable policyRef) ->
                    Expect.equal policyRef restrictedPolicy "an unknown policy ref denies, naming the policy"
                | other -> failtestf "an imported restricted fact must not disclose freely; got %A" other
        }

        // ── refusal honesty ─────────────────────────────────────────────

        testCaseAsync "an unknown peer is refused, and no other key is tried"
        <| async {
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let! _, json = certificateJsonFor a scopeA original.FactId

            // The anchor exists, under a different name. A door that
            // searched its anchors for one that happened to verify would
            // pass here — and would have no answer to "whose fact is this".
            let door = doorFor b [ PeerTrustAnchor.create "deployment-a" a.PublicKey ] audit

            match! door.Import(scopeB, "deployment-unknown", ImportedFactOffer.ofFact original, json) with
            | Error(ImportUntrustedPeer peerId) -> Expect.equal peerId "deployment-unknown" "the refusal names the peer"
            | Error other -> failtestf "expected an untrusted-peer refusal; got %s" (FactImportRefusal.describe other)
            | Ok _ -> failtest "a peer with no composed key material must never import"

            do! expectStoreEmpty b
            Expect.equal (List.length (audit.OfType "FactImportRefused")) 1 "the refusal is audited"
        }

        testCaseAsync "a certificate signed by another deployment's key is refused for this peer"
        <| async {
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let! c = deployment "deployment-c-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let! _, json = certificateJsonFor a scopeA original.FactId

            // B holds C's key under the name it knows C by; A's document is
            // offered as C's.
            let door = doorFor b [ PeerTrustAnchor.create "deployment-c" c.PublicKey ] audit

            match! door.Import(scopeB, "deployment-c", ImportedFactOffer.ofFact original, json) with
            | Error(ImportUnverifiable(EnvelopeUnsignedForKey keyId)) ->
                Expect.equal keyId c.KeyId "the refusal names the key that was expected and absent"
            | Error other -> failtestf "expected an unverifiable refusal; got %s" (FactImportRefusal.describe other)
            | Ok _ -> failtest "a document not signed for this peer must never import"

            do! expectStoreEmpty b
        }

        testCaseAsync "a certificate re-pointed at another fact fails the signature, not the id check"
        <| async {
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let! other = seed a scopeA "cost" 900m Surfaceable
            let! _, json = certificateJsonFor a scopeA original.FactId

            // Editing the root inside the signed statement is the obvious
            // forgery. It must present as tampering, because that is what
            // it is — the id check never gets a say.
            let tampered = tamperStatement original.FactId other.FactId json

            let door = doorFor b [ PeerTrustAnchor.create "deployment-a" a.PublicKey ] audit

            match! door.Import(scopeB, "deployment-a", ImportedFactOffer.ofFact other, tampered) with
            | Error(ImportUnverifiable EnvelopeSignatureInvalid) -> ()
            | Error other' -> failtestf "expected a signature failure; got %s" (FactImportRefusal.describe other')
            | Ok _ -> failtest "an edited certificate must never import"

            do! expectStoreEmpty b
        }

        testCaseAsync "a genuine certificate offered as cover for a different fact is an id mismatch"
        <| async {
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! certified = seed a scopeA "revenue" 1250m Surfaceable
            let! uncertified = seed a scopeA "cost" 900m Surfaceable
            let! _, json = certificateJsonFor a scopeA certified.FactId

            // Perfectly signed, and about something else. A holder told
            // this does not verify would go hunting for tampering that
            // never happened.
            let door = doorFor b [ PeerTrustAnchor.create "deployment-a" a.PublicKey ] audit

            match! door.Import(scopeB, "deployment-a", ImportedFactOffer.ofFact uncertified, json) with
            | Error(ImportContentIdMismatch(root, derived)) ->
                Expect.equal root certified.FactId "the verdict names the fact the certificate covers"
                Expect.equal derived uncertified.FactId "and the one that was offered"
            | Error other -> failtestf "expected an id mismatch; got %s" (FactImportRefusal.describe other)
            | Ok _ -> failtest "a certificate about another fact must never import"

            do! expectStoreEmpty b
        }

        testCaseAsync "altering any field of the offered fact breaks the re-derived id"
        <| async {
            // The content address covers subject, metric, period, method
            // identity and input hashes. Each one, altered alone, must land
            // as a mismatch rather than in the store.
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let! _, json = certificateJsonFor a scopeA original.FactId
            let honest = ImportedFactOffer.ofFact original

            let door = doorFor b [ PeerTrustAnchor.create "deployment-a" a.PublicKey ] audit

            let mutations = [
                "the metric",
                {
                    honest with
                        Metric = MetricRef "profit"
                }
                "the subject",
                {
                    honest with
                        Subject = {
                            honest.Subject with
                                Path = [ "uk"; "south" ]
                        }
                }
                "the period",
                {
                    honest with
                        Period = { q2 with To = q2.To.AddDays 1.0 }
                }
                "the method",
                {
                    honest with
                        Method = Computed("rollup", "4", "p7")
                }
                "the input hashes",
                {
                    honest with
                        InputHashes = [ "sha256:input-a" ]
                }
            ]

            for label, mutated in mutations do
                match! door.Import(scopeB, "deployment-a", mutated, json) with
                | Error(ImportContentIdMismatch(root, derived)) ->
                    Expect.equal root original.FactId $"altering {label} leaves the certificate's root alone"
                    Expect.notEqual derived original.FactId $"but re-derives to a different id"
                | Error other ->
                    failtestf "altering %s must be an id mismatch; got %s" label (FactImportRefusal.describe other)
                | Ok _ -> failtestf "altering %s must never import" label

            do! expectStoreEmpty b

            Expect.equal
                (List.length (audit.OfType "FactImportRefused"))
                (List.length mutations)
                "every attempt is audited, one row each"
        }

        testCaseAsync "a value the peer's own export door withheld is not an offer"
        <| async {
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            // The peer classified this Internal, so its own gate withheld
            // the node when it issued the certificate: id, stance and
            // policy ref survive, the method does not. Importing it would
            // be the widest widening available.
            let! original = seed a scopeA "headcount" 37m Internal
            let! certificate, json = certificateJsonFor a scopeA original.FactId

            let rootNode =
                certificate.Body.Nodes |> List.tryFind (fun n -> n.Id = original.FactId)

            match rootNode with
            | Some node -> Expect.isTrue node.Withheld "the fixture depends on the peer having withheld it"
            | None -> failtest "the certificate must carry a node for its root"

            let door = doorFor b [ PeerTrustAnchor.create "deployment-a" a.PublicKey ] audit

            match! door.Import(scopeB, "deployment-a", ImportedFactOffer.ofFact original, json) with
            | Error(ImportWithheldByPeer stance) ->
                Expect.equal stance "Internal" "the refusal names the stance the peer sealed"
            | Error other -> failtestf "expected a withheld refusal; got %s" (FactImportRefusal.describe other)
            | Ok _ -> failtest "a fact the peer withheld must never import"

            do! expectStoreEmpty b
        }

        testCaseAsync "a genuinely-signed certificate that certifies something other than a fact is refused"
        <| async {
            // Authentic in every cryptographic sense, and rooted at an id
            // that IS the offered fact's — but the node it roots at is not
            // a fact node, so there is no sealed stance to read and nothing
            // this door is entitled to import.
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let offer = ImportedFactOffer.ofFact original
            let root = ImportedFactOffer.derivedFactId offer
            Expect.equal root original.FactId "the offer re-derives to the fact it was built from"

            let! json =
                sealBody
                    a
                    (bodyWith a.KeyId root [
                        {
                            Id = root
                            Kind = "AnswerPlan"
                            Disclosure = None
                            Method = None
                            CertificateRef = None
                            Hash = "aa"
                            Withheld = false
                        }
                    ])

            let door = doorFor b [ PeerTrustAnchor.create "deployment-a" a.PublicKey ] audit

            match! door.Import(scopeB, "deployment-a", offer, json) with
            | Error(ImportRootNotAFact kind) ->
                Expect.equal kind "AnswerPlan" "the refusal names what was certified instead"
            | Error other -> failtestf "expected a root-not-a-fact refusal; got %s" (FactImportRefusal.describe other)
            | Ok _ -> failtest "a certificate over a non-fact must never import a fact"

            do! expectStoreEmpty b

            // And a chain with no node for its own root — the same guard,
            // reached the other way.
            let! headless = sealBody a (bodyWith a.KeyId root [])

            match! door.Import(scopeB, "deployment-a", offer, headless) with
            | Error(ImportRootNotAFact "absent") -> ()
            | Error other -> failtestf "a rootless chain must be refused; got %s" (FactImportRefusal.describe other)
            | Ok _ -> failtest "a certificate carrying no node for its root must never import"

            do! expectStoreEmpty b
        }

        testCaseAsync "a stance this build cannot read is refused, never defaulted"
        <| async {
            // Every default here is either a widening (unsafe) or a
            // misreport of what the peer said. A newer producer's stance
            // vocabulary must therefore stop at the door rather than be
            // guessed at — the posture the envelope's own
            // unrecognised-scheme refusal takes.
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let offer = ImportedFactOffer.ofFact original
            let root = ImportedFactOffer.derivedFactId offer

            let! json =
                sealBody
                    a
                    (bodyWith a.KeyId root [
                        {
                            Id = root
                            Kind = "Fact"
                            Disclosure = Some "PartnerVisible"
                            Method = Some(Fact.methodIdentity offer.Method)
                            CertificateRef = None
                            Hash = "aa"
                            Withheld = false
                        }
                    ])

            let door = doorFor b [ PeerTrustAnchor.create "deployment-a" a.PublicKey ] audit

            match! door.Import(scopeB, "deployment-a", offer, json) with
            | Error(ImportStanceUnreadable stance) ->
                Expect.equal stance "PartnerVisible" "the refusal quotes what it could not read"
            | Error other ->
                failtestf "expected an unreadable-stance refusal; got %s" (FactImportRefusal.describe other)
            | Ok _ -> failtest "an unreadable stance must never resolve to a default"

            do! expectStoreEmpty b
        }

        testCaseAsync "a document that is not a readable envelope is refused as unreadable, never as a forgery"
        <| async {
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable

            let door = doorFor b [ PeerTrustAnchor.create "deployment-a" a.PublicKey ] audit

            for label, document in
                [
                    "empty", ""
                    "not JSON", "{ this is not json"
                    "an unrelated object", """{"hello":1}"""
                ] do
                match! door.Import(scopeB, "deployment-a", ImportedFactOffer.ofFact original, document) with
                | Error(ImportUnverifiable(EnvelopeMalformed _)) -> ()
                | Error other ->
                    failtestf
                        "%s must be refused as unreadable, not as a bad signature; got %s"
                        label
                        (FactImportRefusal.describe other)
                | Ok _ -> failtestf "%s must never import" label

            do! expectStoreEmpty b
        }

        // ── composition (GP 11 / GP 13) ─────────────────────────────────

        testCase "a deployment with no fact store composes no import door"
        <| fun () ->
            let before = ServerApp.empty
            let after = FactsCompose.withFactImport [] before

            Expect.isTrue
                (obj.ReferenceEquals(before.Extensions.ServiceConfig, after.Extensions.ServiceConfig))
                "a NoFactStore app is returned untouched — not merely equivalent"

        testCaseAsync "a door composed with no anchors trusts nobody, which is not the same as no door"
        <| async {
            // The distinction is the whole reason the empty list is legal:
            // a deployment that has declared it accepts nothing produces a
            // refusal row, and one that never composed a door produces
            // nothing at all. An auditor can tell them apart.
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let! _, json = certificateJsonFor a scopeA original.FactId

            match! (doorFor b [] audit).Import(scopeB, "deployment-a", ImportedFactOffer.ofFact original, json) with
            | Error(ImportUntrustedPeer _) -> ()
            | Error other -> failtestf "expected an untrusted-peer refusal; got %s" (FactImportRefusal.describe other)
            | Ok _ -> failtest "an anchorless door must import nothing"

            do! expectStoreEmpty b

            match audit.OfType "FactImportRefused" with
            | [ FactImportRefused payload ] ->
                Expect.equal payload.ImportedFactId "" "a refusal claims no local fact"
                Expect.stringContains payload.Reason "no key material" "and states why"
            | other -> failtestf "expected exactly one refusal row; got %A" other
        }

        // ── the attested projection at the door ─────────────────────────
        //
        // A peer running the levels-bound seal offers a document of a
        // different shape over the same body. The door reads both, routes
        // on what the document declares about itself, and treats the level
        // as an admission input — never as a disclosure modifier, and never
        // as something to compare with `>=`.

        testCaseAsync "an attested certificate imports end-to-end with its level visible in the trail"
        <| async {
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let! certificate, json = attestedCertificateJsonFor a scopeA original.FactId IsolatedSigner

            let door = doorFor b [ PeerTrustAnchor.create "deployment-a" a.PublicKey ] audit

            match! door.Import(scopeB, "deployment-a", ImportedFactOffer.ofFact original, json) with
            | Error refusal -> failtestf "an attested import must succeed: %s" (FactImportRefusal.describe refusal)
            | Ok imported ->
                Expect.equal imported.Value original.Value "the value crossed unchanged"

                Expect.equal
                    imported.Disclosure
                    Surfaceable
                    "the stance is still read out of the signed body, not out of the level"

                // Both projections seal byte-identical bodies, so the ref a
                // fact carries is the same digest either document yields.
                // That is what keeps one join key rather than two.
                let expectedRef = FactImport.attestedCertificateRef certificate

                match imported.Method with
                | Imported ref' -> Expect.equal ref' expectedRef "the import carries the verified certificate's own ref"
                | other -> failtestf "an imported fact must carry Imported provenance; got %A" other

                match audit.OfType "FactImportAccepted" with
                | [ FactImportAccepted payload ] ->
                    Expect.equal
                        payload.AttestationLevel
                        "isolated-signer"
                        "the level the seal claims reaches the audit row, in its stable wire name"

                    Expect.equal payload.CertificateRef expectedRef "beside the ref the fact records"

                    Expect.equal
                        payload.DeclaredDisclosure
                        "Surfaceable"
                        "and the stance, which the level did not touch"
                | other -> failtestf "expected exactly one accepted row; got %A" other
        }

        testCaseAsync "a direct certificate records no level, rather than the weakest one"
        <| async {
            // The direct projection makes no claim about the signing key's
            // custody at all. Recording `attribution` here would put an
            // assertion in the trail that no signature covers — the same
            // defaulting this door refuses everywhere else.
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let! _, json = certificateJsonFor a scopeA original.FactId

            // And the strictest level policy available does not touch it:
            // a level nobody claimed cannot be measured against one.
            let anchor =
                PeerTrustAnchor.create "deployment-a" a.PublicKey
                |> PeerTrustAnchor.withAdmissibleLevels [ IsolatedSigner ]

            match!
                (doorFor b [ anchor ] audit).Import(scopeB, "deployment-a", ImportedFactOffer.ofFact original, json)
            with
            | Error refusal ->
                failtestf "the direct path must be unchanged by a level policy: %s" (FactImportRefusal.describe refusal)
            | Ok _ ->
                match audit.OfType "FactImportAccepted" with
                | [ FactImportAccepted payload ] ->
                    Expect.equal payload.AttestationLevel "" "a document claiming no level records none"
                | other -> failtestf "expected exactly one accepted row; got %A" other
        }

        testCaseAsync "each known level is admitted by the policy that names it"
        <| async {
            let! a = deployment "deployment-a-v1"

            let! original = seed a scopeA "revenue" 1250m Surfaceable

            for level, policy in
                [
                    Attribution, [ Attribution ]
                    IsolatedSigner, [ IsolatedSigner ]
                    Attribution, [ Attribution; IsolatedSigner ]
                    IsolatedSigner, [ Attribution; IsolatedSigner ]
                ] do
                // A fresh importer per arm, so "nothing landed" stays a
                // statement about this arm alone.
                let! b = deployment "deployment-b-v1"
                let audit = RecordingAuditLog()
                let! _, json = attestedCertificateJsonFor a scopeA original.FactId level

                let anchor =
                    PeerTrustAnchor.create "deployment-a" a.PublicKey
                    |> PeerTrustAnchor.withAdmissibleLevels policy

                match!
                    (doorFor b [ anchor ] audit).Import(scopeB, "deployment-a", ImportedFactOffer.ofFact original, json)
                with
                | Error refusal ->
                    failtestf
                        "level %s must be admitted by a policy naming it: %s"
                        (AttestationLevel.name level)
                        (FactImportRefusal.describe refusal)
                | Ok _ -> ()
        }

        testCaseAsync "a level the anchor does not name is refused on its own terms, and nothing lands"
        <| async {
            let! a = deployment "deployment-a-v1"

            let! original = seed a scopeA "revenue" 1250m Surfaceable

            for level, policy in
                [
                    Attribution, [ IsolatedSigner ]
                    IsolatedSigner, [ Attribution ]
                    Attribution, []
                    IsolatedSigner, []
                ] do
                let! b = deployment "deployment-b-v1"
                let audit = RecordingAuditLog()
                let! _, json = attestedCertificateJsonFor a scopeA original.FactId level

                let anchor =
                    PeerTrustAnchor.create "deployment-a" a.PublicKey
                    |> PeerTrustAnchor.withAdmissibleLevels policy

                match!
                    (doorFor b [ anchor ] audit).Import(scopeB, "deployment-a", ImportedFactOffer.ofFact original, json)
                with
                | Error(ImportLevelNotAdmitted(offered, admitted)) ->
                    // Not `ImportUnverifiable`: the document verified, and
                    // an operator sent hunting for tampering here would find
                    // none. Both halves are named because neither is
                    // knowable from the other.
                    Expect.equal offered (AttestationLevel.name level) "the refusal names the level offered"

                    Expect.equal
                        admitted
                        (policy |> List.map AttestationLevel.name)
                        "and the ones this deployment admits"
                | Error other -> failtestf "expected a level refusal; got %s" (FactImportRefusal.describe other)
                | Ok _ -> failtestf "level %s must not be admitted here" (AttestationLevel.name level)

                do! expectStoreEmpty b

                match audit.OfType "FactImportRefused" with
                | [ FactImportRefused payload ] ->
                    Expect.equal
                        payload.AttestationLevel
                        (AttestationLevel.name level)
                        "a refusal on level grounds audits the level that was offered"

                    Expect.equal payload.ImportedFactId "" "and claims no local fact"
                | other -> failtestf "expected exactly one refusal row; got %A" other
        }

        testCaseAsync "a reserved label is refused under every policy, including one that names it"
        <| async {
            // `AttestationLevel`'s own doc comment says a reserved label is
            // NOT evidence. A threshold comparison would admit
            // `reserved:anything` above `IsolatedSigner` on the strength of
            // a string the peer chose; the set cannot express that, and the
            // admission check refuses a reserved level before the set is
            // even consulted — so naming one is inert rather than
            // permissive.
            let! a = deployment "deployment-a-v1"
            let reserved = Reserved "hardware-quote"

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let! _, json = attestedCertificateJsonFor a scopeA original.FactId reserved

            for policy in
                [
                    [ Attribution; IsolatedSigner ]
                    [ reserved ]
                    [ Attribution; IsolatedSigner; reserved ]
                    []
                ] do
                let! b = deployment "deployment-b-v1"
                let audit = RecordingAuditLog()

                let anchor =
                    PeerTrustAnchor.create "deployment-a" a.PublicKey
                    |> PeerTrustAnchor.withAdmissibleLevels policy

                match!
                    (doorFor b [ anchor ] audit).Import(scopeB, "deployment-a", ImportedFactOffer.ofFact original, json)
                with
                | Error(ImportLevelNotAdmitted(offered, _)) ->
                    Expect.equal offered "reserved:hardware-quote" "the refusal quotes the label verbatim"
                | Error other -> failtestf "expected a level refusal; got %s" (FactImportRefusal.describe other)
                | Ok _ -> failtest "a reserved label must never be admitted, under any policy"

                do! expectStoreEmpty b
        }

        testCaseAsync "a statement of some other shape is refused as an unknown projection"
        <| async {
            // Genuinely signed by the peer's own key, and about the right
            // subject — and not a certificate at all. Refusing it as a
            // predicate-type MISMATCH would have to name one of two
            // expectations, telling a holder their statement is the wrong
            // one of a pair it is not a member of.
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let offer = ImportedFactOffer.ofFact original
            let root = ImportedFactOffer.derivedFactId offer
            let foreignType = "https://example.invalid/attestations/something-else/v1"

            let! signed =
                DsseEnvelope.sign
                    a.EnvelopeSigner
                    [ CertificateEnvelope.subjectFor root ]
                    foreignType
                    """{"hello":"world"}"""

            let json =
                match signed with
                | Ok envelope -> DsseEnvelope.toJson envelope
                | Error e -> failtestf "the fixture statement must sign: %s" e

            let door = doorFor b [ PeerTrustAnchor.create "deployment-a" a.PublicKey ] audit

            match! door.Import(scopeB, "deployment-a", offer, json) with
            | Error(ImportUnknownProjection predicateType) ->
                Expect.equal predicateType foreignType "the refusal quotes the type the document declared"
            | Error other ->
                failtestf "expected an unknown-projection refusal; got %s" (FactImportRefusal.describe other)
            | Ok _ -> failtest "a statement of an unpublished shape must never import"

            do! expectStoreEmpty b
        }

        testCaseAsync "a surfaced level contradicting the seal is refused before any level policy is consulted"
        <| async {
            // The document publishes `isolated-signer` beside a seal that
            // claims `attribution`, and is signed perfectly by the peer's
            // own envelope key. The anchor admits `Attribution` and not
            // `IsolatedSigner`, so a door that read the SURFACED level
            // would refuse on level grounds — a plausible-looking answer
            // that would have accepted the same document under a laxer
            // policy. The honest answer is that the document says two
            // incompatible things and cannot be read at all.
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! original = seed a scopeA "revenue" 1250m Surfaceable
            let offer = ImportedFactOffer.ofFact original
            let root = ImportedFactOffer.derivedFactId offer
            let! certificate, _ = attestedCertificateJsonFor a scopeA original.FactId Attribution

            let predicate =
                JsonNode.Parse(CertificateEnvelope.attestedPredicateJson certificate)

            predicate[CertificateEnvelope.AttestationLevelField] <- JsonValue.Create "isolated-signer"

            let! forged =
                DsseEnvelope.sign
                    a.EnvelopeSigner
                    [ CertificateEnvelope.subjectFor root ]
                    CertificateEnvelope.AttestedPredicateType
                    (predicate.ToJsonString())

            let doctored =
                match forged with
                | Ok envelope ->
                    // The envelope signature itself is sound — which is the
                    // whole point of the case.
                    Expect.equal
                        (DsseEnvelopeSigning.verifySignature a.PublicKey envelope)
                        EnvelopeValid
                        "the doctored statement is genuinely signed"

                    DsseEnvelope.toJson envelope
                | Error e -> failtestf "could not sign the doctored statement: %s" e

            let anchor =
                PeerTrustAnchor.create "deployment-a" a.PublicKey
                |> PeerTrustAnchor.withAdmissibleLevels [ Attribution ]

            match! (doorFor b [ anchor ] audit).Import(scopeB, "deployment-a", offer, doctored) with
            | Error(ImportUnverifiable(EnvelopeMalformed reason)) ->
                Expect.stringContains
                    reason
                    CertificateEnvelope.AttestationLevelField
                    "the refusal names the field that disagreed with the seal"
            | Error other ->
                failtestf "expected the reconcile guard to refuse first; got %s" (FactImportRefusal.describe other)
            | Ok _ -> failtest "a document contradicting its own seal must never import"

            do! expectStoreEmpty b

            match audit.OfType "FactImportRefused" with
            | [ FactImportRefused payload ] ->
                Expect.equal
                    payload.AttestationLevel
                    ""
                    "no level is recorded: none was established, and a surfaced one is not a claim"
            | other -> failtestf "expected exactly one refusal row; got %A" other
        }

        testCaseAsync "the signature is established before the shape on both legs"
        <| async {
            // The same document, tampered the same way, on each projection.
            // Both refusals must be the signature one. A subject or
            // level answer here would tell a holder their document is
            // authentic and merely misfiled, which is precisely what it is
            // not — and on the attested leg it would also mean a policy had
            // been applied to a level no signature covered.
            let! a = deployment "deployment-a-v1"
            let! b = deployment "deployment-b-v1"
            let audit = RecordingAuditLog()

            let! certified = seed a scopeA "revenue" 1250m Surfaceable
            let! other = seed a scopeA "cost" 900m Surfaceable

            let! _, directJson = certificateJsonFor a scopeA certified.FactId
            let! _, attestedJson = attestedCertificateJsonFor a scopeA certified.FactId Attribution

            // An anchor that admits nothing, so a level check running early
            // would be visible as a different verdict.
            let anchor =
                PeerTrustAnchor.create "deployment-a" a.PublicKey
                |> PeerTrustAnchor.withAdmissibleLevels []

            let door = doorFor b [ anchor ] audit

            for label, json in [ "the direct", directJson; "the attested", attestedJson ] do
                // Re-point the statement at the other fact: the JSON stays
                // well formed, so routing still classifies it, and the
                // offered fact would satisfy the subject check if anything
                // reached it.
                let tampered = tamperStatement certified.FactId other.FactId json

                match! door.Import(scopeB, "deployment-a", ImportedFactOffer.ofFact other, tampered) with
                | Error(ImportUnverifiable EnvelopeSignatureInvalid) -> ()
                | Error refusal ->
                    failtestf
                        "%s leg must answer with the signature verdict; got %s"
                        label
                        (FactImportRefusal.describe refusal)
                | Ok _ -> failtestf "%s leg must never import a tampered document" label

            do! expectStoreEmpty b
        }

        // ── the admissible-level policy, on its own ─────────────────────

        testCase "the default policy admits both known levels and no reserved label"
        <| fun () ->
            let anchor =
                PeerTrustAnchor.create "peer" {
                    KeyId = "k"
                    Algorithm = Ed25519
                    Pem = ""
                    Jwk = ""
                }

            Expect.isTrue (PeerTrustAnchor.admits Attribution anchor) "attribution is admitted by default"
            Expect.isTrue (PeerTrustAnchor.admits IsolatedSigner anchor) "and so is an isolated signer"

            Expect.isFalse
                (PeerTrustAnchor.admits (Reserved "isolated-signer") anchor)
                "and a reserved label spelled like a known level is still not one"

            // The set is a value a composition root can be read off the
            // page — including reading it back.
            Expect.equal
                (anchor.AdmissibleLevels |> Set.toList)
                [ Attribution; IsolatedSigner ]
                "the declared policy is inspectable, not a predicate"

        testCase "no policy admits a reserved label, because a label is not evidence"
        <| fun () ->
            let anchor =
                PeerTrustAnchor.create "peer" {
                    KeyId = "k"
                    Algorithm = Ed25519
                    Pem = ""
                    Jwk = ""
                }

            for policy in
                [
                    []
                    [ Attribution ]
                    [ Attribution; IsolatedSigner ]
                    [ Reserved "hardware-quote" ]
                ] do
                let declared = anchor |> PeerTrustAnchor.withAdmissibleLevels policy

                Expect.isFalse
                    (PeerTrustAnchor.admits (Reserved "hardware-quote") declared)
                    "a reserved level is refused before the declared set is consulted"

            // And declaring one neither admits it nor empties the anchor of
            // meaning: the other levels it names still hold.
            let mixed =
                anchor
                |> PeerTrustAnchor.withAdmissibleLevels [ Attribution; Reserved "hardware-quote" ]

            Expect.isTrue (PeerTrustAnchor.admits Attribution mixed) "a named known level is still admitted"
            Expect.isFalse (PeerTrustAnchor.admits IsolatedSigner mixed) "one the policy omits is not"
    ]
module ToolUp.Platform.Tests.InProcess.GroundingCertificateTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.Secrets
open ToolUp.Facts
open ToolUp.ArtefactSigning
open ToolUp.Platform.Tests.Contracts

// ─── Phase 565 — grounding certificates ──────────────────────────────
//
// Seeds a real fact store (`BlobFactStore`) with a disclosable + a
// withheld fact, builds the provenance graph over it, and issues a signed
// grounding certificate through the composed artefact signer. Covers the
// acceptance bar: issue→verify round-trip, tamper detection on any byte
// change, the disclosure predicate withholding a fact's structure (and its
// value never appearing), and the GP-13 no-signing-substrate refusal.

/// Minimal in-memory `ISecretStore` — the `DefaultArtefactSigner`
/// auto-provisions its signing key into it on first use, and the
/// `DefaultArtefactVerifier` resolves the public key back out of the same
/// store (the "offline against the deployment public key" path).
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

let private scope = "team-cert"

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draft metric disclosure inputHash value : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ "uk" ]
    }
    Metric = MetricRef metric
    Value = Scalar value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ inputHash ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = disclosure
}

/// The distinctive value carried by the *withheld* fact — must never appear
/// anywhere in the certificate (the certificate is structure, not content).
let private withheldValue = 987654321m

/// A composed issuer over a freshly-seeded store, with `signer` supplied or
/// not, plus the disclosable fact id it seeded.
let private issuerWith (signer: IArtefactSigner option) : Async<IGroundingCertificateIssuer * string> = async {
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage.InMemoryBlobStorage()) events
    let gate = FactDisclosureGate.create store events
    let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

    let graph =
        ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create store)

    let! disclosable = store.Assert(TestScope.resolved scope, draft "revenue" Disclosure.Surfaceable "h1" 100m)

    let factId =
        match disclosable with
        | Ok(f: Fact) -> f.FactId
        | Error e -> failtestf "seed assert failed: %s" (FactStoreError.describe e)

    return GroundingCertificate.createIssuer graph store gate events signer, factId
}

// ── grounding-certificate/v2: the admissions are inside the signature ──

/// A v1 certificate, as issued and serialised BEFORE the v2 format existed,
/// and kept here byte for byte: it must verify for as long as v1 does. It
/// certifies an answer citing one disclosable and one withheld fact.
let private storedV1Certificate =
    """{"Body":{"Format":"grounding-certificate/v1","Root":"msg-fixture","IssuedAt":"2026-10-31T14:32:33.3613795+00:00","DeploymentKeyId":"grounding-v1-fixture","Nodes":[{"Id":"msg-fixture","Kind":"ConversationMessage","Disclosure":null,"Method":null,"CertificateRef":null,"Hash":"ca43556ec0b2ece4f1ce5390dcd05400d66e87aeaf530a7b932d755f183a2716","Withheld":false},{"Id":"h1","Kind":"DataObjectVersion","Disclosure":null,"Method":null,"CertificateRef":null,"Hash":"e5d533e76f9bea313e522f8b5995cfe765c858f59cb2f530dce1a1bde8284d3f","Withheld":false},{"Id":"h2","Kind":"DataObjectVersion","Disclosure":null,"Method":null,"CertificateRef":null,"Hash":"34033c60900625fd2598e9111c2dc839e8494cd73afb934fc177d2a43ad352a2","Withheld":false},{"Id":"40599c96bf027b39abb4e2dc891bd7e0b906ce4e5ca0a7bc780e8f77a3ce31ae","Kind":"Fact","Disclosure":"Internal","Method":null,"CertificateRef":null,"Hash":"b5cec9243f6057573fd3d57c617c2f941507a5fcf2ece1056a7f7bea28481c35","Withheld":true},{"Id":"80b183cdd63c2a78320506389485eee28f1207e7b6cd5838f29d41dff754d705","Kind":"Fact","Disclosure":"Surfaceable","Method":"computed:rollup:1:p0","CertificateRef":null,"Hash":"792bd8882a1e0c7371d79b61bc60d56d51ac9eab47eaa3d72910cafa55321e17","Withheld":false}],"Edges":[{"From":"msg-fixture","To":"40599c96bf027b39abb4e2dc891bd7e0b906ce4e5ca0a7bc780e8f77a3ce31ae","Kind":"CitesFact"},{"From":"msg-fixture","To":"80b183cdd63c2a78320506389485eee28f1207e7b6cd5838f29d41dff754d705","Kind":"CitesFact"},{"From":"40599c96bf027b39abb4e2dc891bd7e0b906ce4e5ca0a7bc780e8f77a3ce31ae","To":"h2","Kind":"DerivedFrom"},{"From":"80b183cdd63c2a78320506389485eee28f1207e7b6cd5838f29d41dff754d705","To":"h1","Kind":"DerivedFrom"}],"PolicyRefs":["Internal"]},"Signature":{"KeyId":"grounding-v1-fixture","Algorithm":"Ed25519","SignedAt":"2026-10-31T14:32:33.5491718+00:00","DetachedJws":"eyJhbGciOiJFZERTQSIsImtpZCI6Imdyb3VuZGluZy12MS1maXh0dXJlIiwidHlwIjoiSk9TRSJ9..PjidHvPCffUlb3PuxbSA3MnF9k7srrsCna92Inb_UlIaQA3Bdc0R7Ml2z-2Yw1Kdm6vuo-q-79OEP98EdCqHBQ"}}"""

/// The exact bytes the stored v1 certificate's signature was computed over.
let private storedV1SignedBytes =
    """{"Format":"grounding-certificate/v1","Root":"msg-fixture","IssuedAt":"2026-10-31T14:32:33.3613795+00:00","DeploymentKeyId":"grounding-v1-fixture","Nodes":[{"Id":"msg-fixture","Kind":"ConversationMessage","Disclosure":null,"Method":null,"CertificateRef":null,"Hash":"ca43556ec0b2ece4f1ce5390dcd05400d66e87aeaf530a7b932d755f183a2716","Withheld":false},{"Id":"h1","Kind":"DataObjectVersion","Disclosure":null,"Method":null,"CertificateRef":null,"Hash":"e5d533e76f9bea313e522f8b5995cfe765c858f59cb2f530dce1a1bde8284d3f","Withheld":false},{"Id":"h2","Kind":"DataObjectVersion","Disclosure":null,"Method":null,"CertificateRef":null,"Hash":"34033c60900625fd2598e9111c2dc839e8494cd73afb934fc177d2a43ad352a2","Withheld":false},{"Id":"40599c96bf027b39abb4e2dc891bd7e0b906ce4e5ca0a7bc780e8f77a3ce31ae","Kind":"Fact","Disclosure":"Internal","Method":null,"CertificateRef":null,"Hash":"b5cec9243f6057573fd3d57c617c2f941507a5fcf2ece1056a7f7bea28481c35","Withheld":true},{"Id":"80b183cdd63c2a78320506389485eee28f1207e7b6cd5838f29d41dff754d705","Kind":"Fact","Disclosure":"Surfaceable","Method":"computed:rollup:1:p0","CertificateRef":null,"Hash":"792bd8882a1e0c7371d79b61bc60d56d51ac9eab47eaa3d72910cafa55321e17","Withheld":false}],"Edges":[{"From":"msg-fixture","To":"40599c96bf027b39abb4e2dc891bd7e0b906ce4e5ca0a7bc780e8f77a3ce31ae","Kind":"CitesFact"},{"From":"msg-fixture","To":"80b183cdd63c2a78320506389485eee28f1207e7b6cd5838f29d41dff754d705","Kind":"CitesFact"},{"From":"40599c96bf027b39abb4e2dc891bd7e0b906ce4e5ca0a7bc780e8f77a3ce31ae","To":"h2","Kind":"DerivedFrom"},{"From":"80b183cdd63c2a78320506389485eee28f1207e7b6cd5838f29d41dff754d705","To":"h1","Kind":"DerivedFrom"}],"PolicyRefs":["Internal"]}"""

/// The stored v1 certificate's verification key: its public half only (a
/// verifier never reads the private half, so the fixture does not carry it).
let private storedV1Key =
    """{"alg":"Ed25519","private":"","public":"a24aWvBveFl5ZQ285/gfPCTL4NmmkLknzsWGH7jkiqE=","createdAt":"2026-10-31T14:32:33.5452753+00:00"}"""

let private storedV1KeyId = "grounding-v1-fixture"

/// A verifier that knows only the stored v1 key.
let private storedV1Verifier () : Async<IArtefactVerifier> = async {
    let secrets = InMemorySecretStore() :> ISecretStore
    let! _ = secrets.SetSecret(ScopeKey.PlatformContainer, "signing/" + storedV1KeyId, storedV1Key)
    return DefaultArtefactVerifier.create secrets
}

let private readCertificate (json: string) : GroundingCertificate =
    System.Text.Json.JsonSerializer.Deserialize<GroundingCertificate>(
        json,
        ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()
    )

let private instructionEcho: CertificateAdmission =
    CertificateAdmission.ofCitation "section 'summary' › element 1 › span 1" (NarrativeCitation.instruction 2 "summary")

let private structuralCount: CertificateAdmission =
    CertificateAdmission.ofCitation
        "section 'summary' › element 2 › span 1"
        (NarrativeCitation.structuralCount "section 'summary' › element 2 › span 1")

let private v2Tests =
    testList "grounding-certificate/v2 — every admission is under the seal" [

        testCaseAsync "a stored v1 certificate verifies, over exactly the bytes it was signed over"
        <| async {
            let certificate = readCertificate storedV1Certificate
            let! verifier = storedV1Verifier ()

            Expect.equal certificate.Body.Format GroundingCertificate.FormatV1 "the fixture is a v1 certificate"
            Expect.isNone certificate.Body.Admissions "a v1 body carries no admissions"

            Expect.equal
                (GroundingCertificate.canonicalBytes certificate.Body
                 |> Text.Encoding.UTF8.GetString)
                storedV1SignedBytes
                "v1's canonical bytes are unchanged, byte for byte"

            match! GroundingCertificate.verify verifier certificate with
            | Ok() -> ()
            | Error e -> failtestf "a stored v1 certificate must still verify: %s" (VerificationError.describe e)
        }

        testCaseAsync "admissions added to a stored v1 certificate fail its verification"
        <| async {
            let certificate = readCertificate storedV1Certificate
            let! verifier = storedV1Verifier ()

            let injected = {
                certificate with
                    Body = {
                        certificate.Body with
                            Admissions = Some [ instructionEcho ]
                    }
            }

            match! GroundingCertificate.verify verifier injected with
            | Error Tampered -> ()
            | other -> failtestf "an unsigned admission must not verify; got %A" other
        }

        testCaseAsync "a v2 body that admitted nothing signs v1's bytes apart from the format string"
        <| async {
            let certificate = readCertificate storedV1Certificate

            let asV2 = {
                certificate.Body with
                    Format = GroundingCertificate.FormatV2
                    Admissions = Some []
            }

            Expect.equal
                (GroundingCertificate.canonicalBytes asV2 |> Text.Encoding.UTF8.GetString)
                (storedV1SignedBytes.Replace(GroundingCertificate.FormatV1, GroundingCertificate.FormatV2))
                "an empty admissions array is omitted"
        }

        testCaseAsync "a narrative certificate seals its admissions in document order, and any edit to one fails"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let audit = AuditLog.NoOpAuditLog() :> IAuditLog
            let signer = DefaultArtefactSigner.createSystem secrets audit "grounding-v2" Ed25519
            let verifier = DefaultArtefactVerifier.create secrets
            let! issuer, factId = issuerWith (Some signer)

            // Document order, which is not the order a sort would give.
            let admissions = [ structuralCount; instructionEcho ]

            match!
                issuer.Issue(
                    TestScope.resolved scope,
                    "auditor",
                    AdmittedNarrativeCertificate("narrative-1", [ factId ], admissions),
                    5
                )
            with
            | Error e -> failtestf "issue must succeed: %s" (CertificateError.describe e)
            | Ok cert ->
                Expect.equal cert.Body.Format GroundingCertificate.FormatV2 "issued as v2"
                Expect.equal cert.Body.Admissions (Some admissions) "sealed in document order"

                let json =
                    GroundingCertificate.canonicalBytes cert.Body |> Text.Encoding.UTF8.GetString

                Expect.stringContains json "\"Admissions\":[" "the admissions are in the signed bytes"

                match! GroundingCertificate.verify verifier cert with
                | Ok() -> ()
                | Error e -> failtestf "a v2 certificate must verify: %s" (VerificationError.describe e)

                let withBody (field: string) (body: GroundingCertificateBody) = field, { cert with Body = body }

                let withAdmissions (field: string) (changed: CertificateAdmission list option) =
                    withBody field { cert.Body with Admissions = changed }

                let editFirst (change: CertificateAdmission -> CertificateAdmission) =
                    Some(change (List.head admissions) :: List.tail admissions)

                let cases = [
                    withAdmissions
                        "Kind"
                        (editFirst (fun a -> {
                            a with
                                Kind = NarrativeCitation.InstructionKind
                        }))
                    withAdmissions
                        "Reference"
                        (editFirst (fun a -> {
                            a with
                                Reference = "structural-count:elsewhere"
                        }))
                    withAdmissions
                        "Location"
                        (editFirst (fun a -> {
                            a with
                                Location = "section 'summary' › element 9 › span 1"
                        }))
                    withAdmissions
                        "Attributes"
                        (Some [
                            structuralCount
                            {
                                instructionEcho with
                                    Attributes = [
                                        {
                                            Name = CertificateAdmission.WordingVersionAttribute
                                            Values = [ "3" ]
                                        }
                                    ]
                            }
                        ])
                    withAdmissions "order" (Some(List.rev admissions))
                    withAdmissions "removal of one" (Some [ instructionEcho ])
                    withAdmissions "removal of all" None
                    withBody "Format" {
                        cert.Body with
                            Format = GroundingCertificate.FormatV1
                    }
                ]

                for field, tampered in cases do
                    match! GroundingCertificate.verify verifier tampered with
                    | Error Tampered -> ()
                    | other -> failtestf "editing the admissions' %s must fail verification; got %A" field other
        }

        test "the formats a verifier reads, the one the issuer stamps, and the attested purpose" {
            Expect.isTrue (GroundingCertificate.isKnownFormat GroundingCertificate.FormatV1) "v1"
            Expect.isTrue (GroundingCertificate.isKnownFormat GroundingCertificate.FormatV2) "v2"
            Expect.isFalse (GroundingCertificate.isKnownFormat "grounding-certificate/v3") "not v3"
            Expect.equal GroundingCertificate.Format GroundingCertificate.FormatV2 "the issuer stamps v2"

            Expect.equal
                GroundingCertificate.AttestationPurpose
                GroundingCertificate.FormatV1
                "the attested purpose does not move with the format"
        }
    ]

let tests =
    testList "Phase 565 — grounding certificates" [

        testCaseAsync "issue over a fact id → verifies offline against the deployment public key"
        <| async {
            // One shared secret store binds signer + verifier (the
            // deployment key + its public component).
            let secrets = InMemorySecretStore() :> ISecretStore
            let audit = AuditLog.NoOpAuditLog() :> IAuditLog
            let signer = DefaultArtefactSigner.createSystem secrets audit "grounding-v1" Ed25519
            let verifier = DefaultArtefactVerifier.create secrets

            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage.InMemoryBlobStorage()) events
            let gate = FactDisclosureGate.create store events
            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

            let graph =
                ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create store)

            let issuer = GroundingCertificate.createIssuer graph store gate events (Some signer)

            let! seeded = store.Assert(TestScope.resolved scope, draft "revenue" Disclosure.Surfaceable "h1" 100m)

            let factId =
                match seeded with
                | Ok f -> f.FactId
                | Error e -> failtestf "seed failed: %s" (FactStoreError.describe e)

            match! issuer.Issue(TestScope.resolved scope, "auditor", FactCertificate factId, 5) with
            | Error e -> failtestf "issue must succeed: %s" (CertificateError.describe e)
            | Ok cert ->
                Expect.equal cert.Body.Format GroundingCertificate.Format "carries the versioned format discriminator"
                Expect.equal cert.Body.Root factId "rooted at the certified fact"
                Expect.equal cert.Body.DeploymentKeyId "grounding-v1" "binds the deployment signing-key id"
                Expect.isTrue (cert.Body.Nodes |> List.exists (fun n -> n.Id = factId)) "the fact node is present"

                match! GroundingCertificate.verify verifier cert with
                | Ok() -> ()
                | Error e -> failtestf "offline verification must succeed: %s" (VerificationError.describe e)
        }

        testCaseAsync "any byte change to the sealed body fails verification (tamper detection)"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let audit = AuditLog.NoOpAuditLog() :> IAuditLog
            let signer = DefaultArtefactSigner.createSystem secrets audit "grounding-v1" Ed25519
            let verifier = DefaultArtefactVerifier.create secrets

            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage.InMemoryBlobStorage()) events
            let gate = FactDisclosureGate.create store events
            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

            let graph =
                ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create store)

            let issuer = GroundingCertificate.createIssuer graph store gate events (Some signer)

            let! seeded = store.Assert(TestScope.resolved scope, draft "revenue" Disclosure.Surfaceable "h1" 100m)

            let factId =
                match seeded with
                | Ok f -> f.FactId
                | Error e -> failtestf "seed failed: %s" (FactStoreError.describe e)

            match! issuer.Issue(TestScope.resolved scope, "auditor", FactCertificate factId, 5) with
            | Error e -> failtestf "issue must succeed: %s" (CertificateError.describe e)
            | Ok cert ->
                // Tamper: swap the certified root to a different id. The
                // signature was computed over the original body's bytes.
                let tampered = {
                    cert with
                        Body = { cert.Body with Root = "forged-root" }
                }

                match! GroundingCertificate.verify verifier tampered with
                | Ok() -> failtest "a tampered body must NOT verify"
                | Error Tampered -> ()
                | Error other -> failtestf "expected Tampered, got %A" other
        }

        testCaseAsync
            "a withheld fact discloses id + policy only; its value never appears; a disclosable fact carries its method"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let audit = AuditLog.NoOpAuditLog() :> IAuditLog
            let signer = DefaultArtefactSigner.createSystem secrets audit "grounding-v1" Ed25519

            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage.InMemoryBlobStorage()) events
            let gate = FactDisclosureGate.create store events
            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

            let graph =
                ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create store)

            let issuer = GroundingCertificate.createIssuer graph store gate events (Some signer)

            let! disclosable = store.Assert(TestScope.resolved scope, draft "revenue" Disclosure.Surfaceable "h1" 100m)
            let! withheld = store.Assert(TestScope.resolved scope, draft "cost" Disclosure.Internal "h2" withheldValue)

            let idOf =
                function
                | Ok(f: Fact) -> f.FactId
                | Error e -> failtestf "seed failed: %s" (FactStoreError.describe e)

            let disclosableId = idOf disclosable
            let withheldId = idOf withheld
            let messageId = "msg-answer"

            match!
                issuer.Issue(
                    TestScope.resolved scope,
                    "auditor",
                    AnswerCertificate(messageId, [ disclosableId; withheldId ]),
                    5
                )
            with
            | Error e -> failtestf "issue must succeed: %s" (CertificateError.describe e)
            | Ok cert ->
                let nodeFor id =
                    cert.Body.Nodes
                    |> List.tryFind (fun n -> n.Id = id)
                    |> function
                        | Some n -> n
                        | None -> failtestf "node %s missing from the certificate" id

                let withheldNode = nodeFor withheldId
                Expect.isTrue withheldNode.Withheld "the Internal fact is withheld"
                Expect.equal withheldNode.Method None "a withheld fact's method identity is redacted"
                Expect.equal withheldNode.CertificateRef None "a withheld fact carries no method-derived refs"
                Expect.equal withheldNode.Disclosure (Some "Internal") "the withheld node still names its policy/stance"

                let disclosableNode = nodeFor disclosableId
                Expect.isFalse disclosableNode.Withheld "the Surfaceable fact is disclosable"

                Expect.isTrue
                    (disclosableNode.Method |> Option.exists (fun m -> m.Contains "computed"))
                    "a disclosable fact carries its method identity"

                // The withheld classification is recorded as a sealed-under
                // policy ref.
                Expect.contains cert.Body.PolicyRefs "Internal" "the withheld policy ref is recorded"

                // The withheld fact's value never appears anywhere in the
                // sealed certificate bytes.
                let json =
                    GroundingCertificate.canonicalBytes cert.Body
                    |> System.Text.Encoding.UTF8.GetString

                Expect.isFalse
                    (json.Contains(string withheldValue))
                    "the withheld fact's value must never appear in the certificate"
        }

        testCaseAsync "an answer's recorded plan joins its certificate exactly as it joins the answer chain"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let audit = AuditLog.NoOpAuditLog() :> IAuditLog
            let signer = DefaultArtefactSigner.createSystem secrets audit "grounding-v1" Ed25519

            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage.InMemoryBlobStorage()) events
            let gate = FactDisclosureGate.create store events
            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

            let graph =
                ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create store)

            let issuer = GroundingCertificate.createIssuer graph store gate events (Some signer)
            let messageId = "msg-planned"

            let plan = {
                PlanId = "plan-1"
                Question = "revenue?"
                CompiledAt = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
                Steps = []
                Refusal = None
            }

            let planner = AnswerPlanner.create store gate None events AnswerPlanner.noCompiler

            do! planner.Record(TestScope.resolved scope, messageId, plan)

            let! chain = AnswerPlanProvenance.chainForMessage graph events (TestScope.resolved scope) messageId 5

            match! issuer.Issue(TestScope.resolved scope, "auditor", AnswerCertificate(messageId, []), 5) with
            | Error e -> failtestf "issue must succeed: %s" (CertificateError.describe e)
            | Ok cert ->
                Expect.equal
                    (cert.Body.Nodes |> List.map _.Id |> List.sort)
                    (chain.Nodes |> List.map _.Id |> List.sort)
                    "the certificate holds the answer chain's nodes, the plan node among them"

                Expect.equal
                    (cert.Body.Edges |> List.map (fun e -> e.From, e.To) |> List.sort)
                    (chain.Edges |> List.map (fun e -> e.From, e.To) |> List.sort)
                    "and its edges, message to plan among them"

                Expect.contains (cert.Body.Nodes |> List.map _.Id) "plan-1" "the plan is a node"
        }

        testCaseAsync "no signing substrate composed → issuance refuses (GP 13), never throws"
        <| async {
            let! issuer, factId = issuerWith None

            match! issuer.Issue(TestScope.resolved scope, "auditor", FactCertificate factId, 5) with
            | Error SigningUnavailable -> ()
            | Error other -> failtestf "expected SigningUnavailable, got %A" other
            | Ok _ -> failtest "must not issue a certificate without a composed signer"
        }
        v2Tests
    ]
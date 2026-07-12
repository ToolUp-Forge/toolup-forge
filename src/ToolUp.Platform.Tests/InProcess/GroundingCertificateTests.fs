module ToolUp.Platform.Tests.InProcess.GroundingCertificateTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
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

    let! disclosable = store.Assert(scope, draft "revenue" Disclosure.Surfaceable "h1" 100m)

    let factId =
        match disclosable with
        | Ok(f: Fact) -> f.FactId
        | Error e -> failtestf "seed assert failed: %s" e

    return GroundingCertificate.createIssuer graph store gate events signer, factId
}

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

            let! seeded = store.Assert(scope, draft "revenue" Disclosure.Surfaceable "h1" 100m)

            let factId =
                match seeded with
                | Ok f -> f.FactId
                | Error e -> failtestf "seed failed: %s" e

            match! issuer.Issue(scope, "auditor", FactCertificate factId, 5) with
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

            let! seeded = store.Assert(scope, draft "revenue" Disclosure.Surfaceable "h1" 100m)

            let factId =
                match seeded with
                | Ok f -> f.FactId
                | Error e -> failtestf "seed failed: %s" e

            match! issuer.Issue(scope, "auditor", FactCertificate factId, 5) with
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

            let! disclosable = store.Assert(scope, draft "revenue" Disclosure.Surfaceable "h1" 100m)
            let! withheld = store.Assert(scope, draft "cost" Disclosure.Internal "h2" withheldValue)

            let idOf =
                function
                | Ok(f: Fact) -> f.FactId
                | Error e -> failtestf "seed failed: %s" e

            let disclosableId = idOf disclosable
            let withheldId = idOf withheld
            let messageId = "msg-answer"

            match! issuer.Issue(scope, "auditor", AnswerCertificate(messageId, [ disclosableId; withheldId ]), 5) with
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

        testCaseAsync "no signing substrate composed → issuance refuses (GP 13), never throws"
        <| async {
            let! issuer, factId = issuerWith None

            match! issuer.Issue(scope, "auditor", FactCertificate factId, 5) with
            | Error SigningUnavailable -> ()
            | Error other -> failtestf "expected SigningUnavailable, got %A" other
            | Ok _ -> failtest "must not issue a certificate without a composed signer"
        }
    ]
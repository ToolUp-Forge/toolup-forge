// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GroundingSigningConvergenceTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.AuditSinks
open ToolUp.Platform.AuditSinks.ChainedLedger
open ToolUp.ArtefactSigning
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts

// ─── Grounding-tier signing convergence ──────────────────────────────────
//
// Three signing paths grew independently, each correct on its own terms:
// the byte-level `IArtefactSigner` a grounding certificate is sealed with,
// the application signing seam that binds a purpose and an attestation
// level into the signed bytes, and the audit ledger's deliberately-local
// head signer. Independently shippable, and three key stories where a
// deployment wanted one — three places a key is configured, three
// rotations to remember, and no single answer to what any given signature
// claims about the environment that produced it.
//
// This pack is the convergence probe, and it is deliberately an
// INTEGRATION one: the adapters are unit-tested where they live, and what
// cannot be checked there is that ONE composed signer really does cover all
// three artefact kinds, that a rotation leaves every one of them
// verifiable, and that a signature cannot be carried from one to another.
// Those are properties of the composition, so they are asserted over a real
// fact store, a real chained ledger, and one `IApplicationSigner`.

/// Minimal in-memory `ISecretStore`. The signer auto-provisions its key
/// into it and the verifier resolves the public half back out — the same
/// store standing for the deployment's key material throughout, which is
/// the point: one store, one key story.
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

let private scope = "team-convergence"

let private utf8 (s: string) = Encoding.UTF8.GetBytes s

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draft metric inputHash value : FactDraft = {
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
    Disclosure = Disclosure.Surfaceable
}

/// An application signer over `secrets` under `keyId`. Composing a second
/// one over the SAME store under a new id is what a rotation looks like.
let private applicationSigner (secrets: ISecretStore) (keyId: string) : IApplicationSigner =
    ApplicationSigning.inProcess secrets (AuditLog.NoOpAuditLog() :> IAuditLog) keyId EcdsaP256 "system"
    |> ApplicationSigning.create

/// A seeded fact store plus the collaborators a certificate issuer needs.
let private factSubstrate () = async {
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage.InMemoryBlobStorage()) events
    let gate = FactDisclosureGate.create store events
    let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

    let graph =
        ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create store)

    return graph, store, gate, events
}

let private seed (store: IFactStore) metric inputHash value = async {
    match! store.Assert(scope, draft metric inputHash value) with
    | Ok(f: Fact) -> return f.FactId
    | Error e -> return failtestf "seeding the fact store failed: %s" e
}

let private ledgerSettings: ChainedLedgerSettings = {
    Container = "audit-ledger"
    PathPrefix = Some "convergence"
}

let private newLedgerStorage () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-signing-convergence", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

let private auditEvent (offset: float) : AuditEnvelope =
    AuditEnvelope.fromScopeId
        scope
        (DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc).AddSeconds offset)
        (UserLoggedIn {
            UserId = sprintf "user-%d" (int offset)
            AuthProvider = "Header"
        })

/// The ledger-head seam, filled from an application signer.
let private headSigner (signer: IApplicationSigner) =
    ApplicationKeyedSigning.signer "audit.ledger.head" signer
    |> LedgerHeadSigning.ofKeyedSigner

let private headVerifier (signer: IApplicationSigner) =
    ApplicationKeyedSigning.verifier "audit.ledger.head" signer
    |> LedgerHeadSigning.verifierOfKeyed

let tests =
    testList "Phase 682 — grounding-tier signing convergence" [

        // The headline acceptance: one composed signer, three artefact
        // kinds, one key id and one attestation level across all of them.
        testCaseAsync "one composed signer seals a deploy record, a certificate, and a ledger head"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let signer = applicationSigner secrets "deployment-key-v1"

            // (1) A deploy record — an arbitrary application payload
            // through the seam directly.
            let deployRecord = utf8 """{"release":"2026.8.22","modules":["facts","audit"]}"""

            match! signer.SignPayload("deploy.record", deployRecord) with
            | Error e -> failtestf "deploy-record signing must succeed: %s" (SigningError.describe e)
            | Ok envelope ->
                Expect.equal envelope.Signature.KeyId "deployment-key-v1" "the deploy record names the deployment key"
                Expect.equal envelope.Level Attribution "and carries the level the provider's custody supports"

                match! signer.VerifyPayload("deploy.record", deployRecord, envelope) with
                | Ok() -> ()
                | Error e -> failtestf "deploy-record verification: %s" (PayloadVerificationError.describe e)

            // (2) A grounding certificate.
            let! graph, store, gate, events = factSubstrate ()

            let issuer =
                GroundingCertificate.createAttestedIssuer graph store gate events (Some signer)

            let! factId = seed store "revenue" "h1" 100m

            match! issuer.Issue(scope, "auditor", FactCertificate factId, 5) with
            | Error e -> failtestf "certificate issue must succeed: %s" (CertificateError.describe e)
            | Ok certificate ->
                Expect.equal
                    certificate.Body.DeploymentKeyId
                    "deployment-key-v1"
                    "the certificate body binds the same key id"

                Expect.equal certificate.Envelope.Level Attribution "and the seal claims the same level"

                Expect.equal
                    certificate.Envelope.Purpose
                    GroundingCertificate.AttestationPurpose
                    "sealed as a grounding certificate, not as anything else"

                match! GroundingCertificate.verifyAttestedFor signer factId certificate with
                | AttestedCertificateValid -> ()
                | verdict -> failtestf "certificate verification: %s" (AttestedCertificateVerdict.describe verdict)

            // (3) A ledger head.
            let storage = newLedgerStorage ()
            let sink = createSigned "convergence" ledgerSettings storage (headSigner signer)

            match! sink.Deliver [ auditEvent 0.0; auditEvent 1.0 ] with
            | Error e -> failtestf "ledger delivery must succeed: %s" e
            | Ok() ->
                match! verify ledgerSettings storage (Some(headVerifier signer)) with
                | Ok(LedgerVerified(count, _, HeadSignatureValid(keyId, algorithm))) ->
                    Expect.equal count 2L "both records are in the chain"
                    Expect.equal keyId "deployment-key-v1" "the head names the same deployment key"

                    Expect.equal
                        algorithm
                        (ApplicationKeyedSigning.scheme Attribution)
                        "and records the scheme, which names the level the head signature claims"
                | Ok other -> failtestf "the signed head must verify; got %A" other
                | Error e -> failtestf "ledger verification failed: %s" e
        }

        // ── the certificate's signed bytes ──────────────────────────────

        testCaseAsync "editing the certificate's key id or its claimed level fails verification"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let signer = applicationSigner secrets "deployment-key-v1"
            let! graph, store, gate, events = factSubstrate ()

            let issuer =
                GroundingCertificate.createAttestedIssuer graph store gate events (Some signer)

            let! factId = seed store "revenue" "h1" 100m

            match! issuer.Issue(scope, "auditor", FactCertificate factId, 5) with
            | Error e -> failtestf "issue must succeed: %s" (CertificateError.describe e)
            | Ok certificate ->
                // The key id lives in the signed body.
                let repointed = {
                    certificate with
                        Body = {
                            certificate.Body with
                                DeploymentKeyId = "some-other-key"
                        }
                }

                match! GroundingCertificate.verifyAttested signer repointed with
                | AttestedCertificateRejected(PayloadVerificationError.SignatureRejected _) -> ()
                | verdict ->
                    failtestf "an edited key id must fail; got %s" (AttestedCertificateVerdict.describe verdict)

                // The level lives in the seam's framing. Claiming more
                // than the signer could is the failure mode that matters:
                // "valid provenance from an under-isolated builder".
                let upgraded = {
                    certificate with
                        Envelope = {
                            certificate.Envelope with
                                Level = IsolatedSigner
                        }
                }

                match! GroundingCertificate.verifyAttested signer upgraded with
                | AttestedCertificateRejected(PayloadVerificationError.SignatureRejected _) -> ()
                | verdict ->
                    failtestf
                        "an upgraded attestation level must fail; got %s"
                        (AttestedCertificateVerdict.describe verdict)
        }

        testCaseAsync "a certificate about another answer is reported as such, not as a forgery"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let signer = applicationSigner secrets "deployment-key-v1"
            let! graph, store, gate, events = factSubstrate ()

            let issuer =
                GroundingCertificate.createAttestedIssuer graph store gate events (Some signer)

            let! wanted = seed store "revenue" "h1" 100m
            let! other = seed store "cost" "h2" 55m

            match! issuer.Issue(scope, "auditor", FactCertificate other, 5) with
            | Error e -> failtestf "issue must succeed: %s" (CertificateError.describe e)
            | Ok certificate ->
                // Perfectly signed, and about the wrong thing. A holder
                // told this does not verify would go hunting for tampering
                // that never happened.
                match! GroundingCertificate.verifyAttestedFor signer wanted certificate with
                | AttestedCertificateSubjectMismatch(expected, actual) ->
                    Expect.equal expected wanted "the verdict names the root the holder asked about"
                    Expect.equal actual other "and the one the certificate is issued over"
                | verdict ->
                    failtestf "expected a subject mismatch; got %s" (AttestedCertificateVerdict.describe verdict)

                // Without an expectation the seal itself is still sound —
                // which is exactly why the two answers must stay apart.
                match! GroundingCertificate.verifyAttested signer certificate with
                | AttestedCertificateValid -> ()
                | verdict ->
                    failtestf "the seal itself must be sound; got %s" (AttestedCertificateVerdict.describe verdict)
        }

        // ── rotation continuity across all three ────────────────────────

        testCaseAsync "a rotation leaves certificates and ledger heads signed under the retired key verifiable"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let oldSigner = applicationSigner secrets "deployment-key-v1"

            let! graph, store, gate, events = factSubstrate ()
            let! factId = seed store "revenue" "h1" 100m

            let oldIssuer =
                GroundingCertificate.createAttestedIssuer graph store gate events (Some oldSigner)

            let! issued = oldIssuer.Issue(scope, "auditor", FactCertificate factId, 5)

            let certificate =
                match issued with
                | Ok c -> c
                | Error e -> failtestf "issue must succeed: %s" (CertificateError.describe e)

            let storage = newLedgerStorage ()

            let oldSink =
                createSigned "convergence" ledgerSettings storage (headSigner oldSigner)

            match! oldSink.Deliver [ auditEvent 0.0 ] with
            | Error e -> failtestf "ledger delivery must succeed: %s" e
            | Ok() -> ()

            // Rotate. A new active key, the old one retired — retirement
            // is rotation, not distrust, so nothing signed before it is
            // invalidated.
            let newSigner = applicationSigner secrets "deployment-key-v2"
            let ledger = SecretStoreSigningKeyLedger.create secrets

            do!
                ApplicationSigning.activate ledger "operator" "deployment-key-v2"
                |> Async.Ignore

            do! ApplicationSigning.retire ledger "operator" "deployment-key-v1" |> Async.Ignore

            Expect.equal (newSigner.ActiveKeyId()) "deployment-key-v2" "new signatures are minted under the new key"

            // The certificate, verified through the ROTATED signer: the
            // key that signed is resolved by the id the seal names.
            match! GroundingCertificate.verifyAttestedFor newSigner factId certificate with
            | AttestedCertificateValid -> ()
            | verdict ->
                failtestf
                    "a certificate sealed under a retired key must still verify; got %s"
                    (AttestedCertificateVerdict.describe verdict)

            // Same for the stored ledger head.
            match! verify ledgerSettings storage (Some(headVerifier newSigner)) with
            | Ok(LedgerVerified(_, _, HeadSignatureValid(keyId, _))) ->
                Expect.equal keyId "deployment-key-v1" "the stored head still names the key that signed it"
            | Ok other -> failtestf "a head signed under a retired key must still verify; got %A" other
            | Error e -> failtestf "ledger verification failed: %s" e

            // And an append after the rotation signs under the new key,
            // while the chain it extends is the one the old key headed.
            let newSink =
                createSigned "convergence" ledgerSettings storage (headSigner newSigner)

            match! newSink.Deliver [ auditEvent 1.0 ] with
            | Error e -> failtestf "post-rotation delivery must succeed: %s" e
            | Ok() ->
                match! verify ledgerSettings storage (Some(headVerifier newSigner)) with
                | Ok(LedgerVerified(count, _, HeadSignatureValid(keyId, _))) ->
                    Expect.equal count 2L "the chain continued across the rotation"
                    Expect.equal keyId "deployment-key-v2" "and its head is now signed under the new key"
                | Ok other -> failtestf "the post-rotation head must verify; got %A" other
                | Error e -> failtestf "ledger verification failed: %s" e
        }

        testCaseAsync "a ledger-head signature offered as a certificate seal is a replay, not a forgery"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let signer = applicationSigner secrets "deployment-key-v1"
            let! graph, store, gate, events = factSubstrate ()

            let issuer =
                GroundingCertificate.createAttestedIssuer graph store gate events (Some signer)

            let! factId = seed store "revenue" "h1" 100m

            match! issuer.Issue(scope, "auditor", FactCertificate factId, 5) with
            | Error e -> failtestf "issue must succeed: %s" (CertificateError.describe e)
            | Ok certificate ->
                // One key signs certificates and ledger heads alike. What
                // keeps them apart is the purpose, framed into the bytes:
                // re-seal the certificate body under the ledger-head
                // purpose and offer it as a certificate.
                match!
                    signer.SignPayload("audit.ledger.head", GroundingCertificate.canonicalBytes certificate.Body)
                with
                | Error e -> failtestf "signing must succeed: %s" (SigningError.describe e)
                | Ok headEnvelope ->
                    let transplanted = {
                        certificate with
                            Envelope = headEnvelope
                    }

                    match! GroundingCertificate.verifyAttested signer transplanted with
                    | AttestedCertificateRejected(PayloadVerificationError.PurposeMismatch(expected, actual)) ->
                        Expect.equal
                            expected
                            GroundingCertificate.AttestationPurpose
                            "the refusal names the use it was offered for"

                        Expect.equal actual "audit.ledger.head" "and the use it was minted for"
                    | verdict ->
                        failtestf
                            "a cross-purpose transplant must be refused as a replay; got %s"
                            (AttestedCertificateVerdict.describe verdict)
        }

        // ── unconverged compositions are untouched (GP 11 / GP 13) ──────

        testCaseAsync "the direct certificate path is unchanged, and needs no application signer"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let audit = AuditLog.NoOpAuditLog() :> IAuditLog

            let byteSigner =
                DefaultArtefactSigner.createSystem secrets audit "grounding-v1" Ed25519

            let verifier = DefaultArtefactVerifier.create secrets

            let! graph, store, gate, events = factSubstrate ()

            let issuer =
                GroundingCertificate.createIssuer graph store gate events (Some byteSigner)

            let! factId = seed store "revenue" "h1" 100m

            match! issuer.Issue(scope, "auditor", FactCertificate factId, 5) with
            | Error e -> failtestf "the direct path must still issue: %s" (CertificateError.describe e)
            | Ok certificate ->
                Expect.equal certificate.Body.Root factId "rooted at the certified fact, as before"
                Expect.equal certificate.Body.DeploymentKeyId "grounding-v1" "and binding the byte-level signer's key"

                match! GroundingCertificate.verify verifier certificate with
                | Ok() -> ()
                | Error e -> failtestf "the direct path must still verify: %s" (VerificationError.describe e)
        }

        testCaseAsync "both issuers over one substrate produce the identical body"
        <| async {
            // The body builder is shared, so the two paths must not have
            // forked the interchange format. Same clock, same key id, same
            // subject: the canonical bytes must match exactly.
            let secrets = InMemorySecretStore() :> ISecretStore
            let audit = AuditLog.NoOpAuditLog() :> IAuditLog
            let frozen = DateTime(2026, 8, 22, 10, 30, 0, DateTimeKind.Utc)
            let clock () = frozen

            let byteSigner =
                DefaultArtefactSigner.createSystem secrets audit "deployment-key-v1" EcdsaP256

            let appSigner = applicationSigner secrets "deployment-key-v1"

            let! graph, store, gate, events = factSubstrate ()
            let! factId = seed store "revenue" "h1" 100m

            let direct =
                GroundingCertificate.createIssuerWithClock graph store gate events (Some byteSigner) clock

            let attested =
                GroundingCertificate.createAttestedIssuerWithClock graph store gate events (Some appSigner) clock

            let! a = direct.Issue(scope, "auditor", FactCertificate factId, 5)
            let! b = attested.Issue(scope, "auditor", FactCertificate factId, 5)

            match a, b with
            | Ok directCert, Ok attestedCert ->
                Expect.equal
                    (GroundingCertificate.canonicalBytes directCert.Body)
                    (GroundingCertificate.canonicalBytes attestedCert.Body)
                    "the two seals cover byte-identical bodies — the format has not forked"
            | _ -> failtest "both issuers must succeed"
        }

        testCaseAsync "an unsigned ledger still reports an unsigned head"
        <| async {
            let storage = newLedgerStorage ()
            let sink = create "convergence" ledgerSettings storage

            match! sink.Deliver [ auditEvent 0.0 ] with
            | Error e -> failtestf "delivery must succeed: %s" e
            | Ok() ->
                match! verify ledgerSettings storage None with
                | Ok(LedgerVerified(1L, _, HeadUnsigned)) -> ()
                | Ok other -> failtestf "an unsigned ledger must report HeadUnsigned; got %A" other
                | Error e -> failtestf "ledger verification failed: %s" e
        }

        testCaseAsync "a deployment with no composed application signer refuses to issue"
        <| async {
            let! graph, store, gate, events = factSubstrate ()
            let issuer = GroundingCertificate.createAttestedIssuer graph store gate events None
            let! factId = seed store "revenue" "h1" 100m

            match! issuer.Issue(scope, "auditor", FactCertificate factId, 5) with
            | Error SigningUnavailable -> ()
            | Error other -> failtestf "expected SigningUnavailable; got %s" (CertificateError.describe other)
            | Ok _ -> failtest "issuance without a signer must refuse, never fabricate a seal"
        }
    ]
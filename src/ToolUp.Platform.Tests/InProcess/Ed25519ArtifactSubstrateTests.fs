module ToolUp.Platform.Tests.InProcess.Ed25519ArtifactSubstrateTests

open System
open System.Collections.Concurrent
open Expecto
open Org.BouncyCastle.Crypto.Parameters
open Org.BouncyCastle.Security
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// Phase 30a — in-process binding for the default Ed25519 artefact
// substrate. Binds `IArtifactSignerContract` + `IArtifactVerifierContract`
// to `Ed25519ArtifactSigner` + `Ed25519ArtifactVerifier` + the blob-
// backed `BlobBackedPublisherKeyStore`. Adds a thin set of
// substrate-specific assertions: blob-backed key-store round-trip + the
// audit-emission contract (PublisherKeyId recorded; private key never
// in payload).

// ─── Test fixtures ─────────────────────────────────────────────────────

let private mkPublisherKeyPair () : PublisherKeyId * byte[] * byte[] =
    let random = SecureRandom()
    let privateKey = Array.zeroCreate<byte> 32
    random.NextBytes(privateKey)
    let publicKey = Ed25519ArtifactSigner.derivePublicKey privateKey
    PublisherKeyId "test-publisher", publicKey, privateKey

type private CapturingAuditLog() =
    let recorded = ConcurrentQueue<string * AuditEvent>()
    member _.Recorded = recorded |> Seq.toList

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Enqueue(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

// ─── Contract-pack bindings ───────────────────────────────────────────

let private signerFactory () : IArtifactSigner =
    let pub, _, priv = mkPublisherKeyPair ()
    Ed25519ArtifactSigner.create pub priv

let private verifierFactory () : IArtifactVerifierContract.ArtifactVerifierFixture =
    let publisherId, publicKey, privateKey = mkPublisherKeyPair ()
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let keyStore = BlobBackedPublisherKeyStore.create blobs
    let audit = CapturingAuditLog() :> IAuditLog
    let verifier = Ed25519ArtifactVerifier.create keyStore audit "_platform"
    let signer = Ed25519ArtifactSigner.create publisherId privateKey

    {
        Verifier = verifier
        KeyStore = keyStore
        Signer = signer
        TrustedPublisherId = publisherId
        TrustedPublicKey = publicKey
    }

// ─── Substrate-specific tests beyond the contract packs ───────────────

let private sampleManifest (publisherId: PublisherKeyId) : ArtifactManifest = {
    ModuleId = "test.module"
    Version = "0.1.0"
    SdkVersionRange = {
        MinInclusive = "0.4.0"
        MaxExclusive = "0.5.0"
    }
    CodeHash = ContentHash "aaaa"
    SchemaHash = ContentHash "bbbb"
    Dependencies = []
    PublisherKeyId = publisherId
}

let private substrateSpecificTests =
    testList "Ed25519 artefact substrate — implementation specifics" [
        testCaseAsync "BlobBackedPublisherKeyStore — round-trip + list + remove"
        <| async {
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let store = BlobBackedPublisherKeyStore.create blobs
            let id = PublisherKeyId "publisher-A"
            let key = Array.create 32 0xAAuy

            do! store.AddTrustedKey(id, key)
            let! resolved = store.TryGetPublicKey id
            Expect.equal resolved (Some key) "Round-trip must return the same key bytes"

            let! listed = store.ListTrustedKeyIds()
            Expect.contains listed id "ListTrustedKeyIds must include the added id"

            do! store.RemoveTrustedKey id
            let! afterRemove = store.TryGetPublicKey id
            Expect.equal afterRemove None "After remove, key must be absent"
        }

        testCaseAsync "BlobBackedPublisherKeyStore — RemoveTrustedKey is idempotent"
        <| async {
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let store = BlobBackedPublisherKeyStore.create blobs
            // Remove an id that was never added — must not throw.
            do! store.RemoveTrustedKey(PublisherKeyId "never-added")
        }

        testCaseAsync "Verifier emits ModuleArtefactVerified audit on Ok (publisher key id recorded; no key bytes)"
        <| async {
            let publisherId, publicKey, privateKey = mkPublisherKeyPair ()
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let keyStore = BlobBackedPublisherKeyStore.create blobs
            let audit = CapturingAuditLog()

            let verifier =
                Ed25519ArtifactVerifier.create keyStore (audit :> IAuditLog) "_platform"

            let signer = Ed25519ArtifactSigner.create publisherId privateKey

            do! keyStore.AddTrustedKey(publisherId, publicKey)

            let manifest = sampleManifest publisherId
            let payload = [| 1uy; 2uy; 3uy |]
            let! signed = signer.Sign(manifest, payload)
            let! result = verifier.Verify signed

            Expect.equal result ArtifactValidation.Ok "Round-trip must verify"

            let verifiedEvents =
                audit.Recorded
                |> List.choose (fun (_, evt) ->
                    match evt with
                    | ModuleArtefactVerified p -> Some p
                    | _ -> None)

            Expect.equal verifiedEvents.Length 1 "Exactly one ModuleArtefactVerified event must be recorded"
            let recorded = verifiedEvents[0]

            Expect.equal
                recorded.PublisherKeyId
                (PublisherKeyId.value publisherId)
                "Audit payload must carry the publisher key id"

            Expect.equal recorded.ModuleId manifest.ModuleId "Audit payload must carry the module id"

            Expect.equal recorded.ArtifactVersion manifest.Version "Audit payload must carry the artefact version"
        }

        testCaseAsync "Verifier emits ModuleArtefactRejected audit on untrusted publisher (reason verbatim)"
        <| async {
            let publisherId, _publicKey, privateKey = mkPublisherKeyPair ()
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let keyStore = BlobBackedPublisherKeyStore.create blobs
            let audit = CapturingAuditLog()

            let verifier =
                Ed25519ArtifactVerifier.create keyStore (audit :> IAuditLog) "_platform"

            let signer = Ed25519ArtifactSigner.create publisherId privateKey
            // Do NOT add the key to the store.
            let manifest = sampleManifest publisherId
            let! signed = signer.Sign(manifest, [| 0uy |])
            let! result = verifier.Verify signed

            Expect.equal
                result
                (ArtifactValidation.Error "untrusted publisher")
                "Untrusted publisher must produce the exact refusal string"

            let rejectedEvents =
                audit.Recorded
                |> List.choose (fun (_, evt) ->
                    match evt with
                    | ModuleArtefactRejected p -> Some p
                    | _ -> None)

            Expect.equal rejectedEvents.Length 1 "Exactly one ModuleArtefactRejected event must be recorded"

            Expect.equal
                rejectedEvents[0].Reason
                "untrusted publisher"
                "Audit reason must match the ArtifactValidation.Error reason verbatim"
        }

        testCaseAsync "Ed25519ArtifactSigner constructor rejects non-32-byte private keys"
        <| async {
            let publisherId = PublisherKeyId "p"

            Expect.throws
                (fun () -> Ed25519ArtifactSigner.create publisherId (Array.zeroCreate 16) |> ignore)
                "16-byte key must throw"

            Expect.throws
                (fun () -> Ed25519ArtifactSigner.create publisherId (Array.zeroCreate 64) |> ignore)
                "64-byte key must throw (Ed25519 private key is 32 bytes)"
        }

        testCaseAsync "BlobBackedPublisherKeyStore rejects path-separator key ids"
        <| async {
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let store = BlobBackedPublisherKeyStore.create blobs

            Expect.throws
                (fun () ->
                    store.AddTrustedKey(PublisherKeyId "evil/id", Array.zeroCreate 32)
                    |> Async.RunSynchronously)
                "Forward-slash key id must throw"

            Expect.throws
                (fun () ->
                    store.AddTrustedKey(PublisherKeyId "evil\\id", Array.zeroCreate 32)
                    |> Async.RunSynchronously)
                "Backslash key id must throw"
        }
    ]

let tests =
    testList "Ed25519 artefact substrate" [
        IArtifactSignerContract.tests "Ed25519ArtifactSigner" signerFactory
        IArtifactVerifierContract.tests "Ed25519ArtifactVerifier" verifierFactory
        substrateSpecificTests
    ]
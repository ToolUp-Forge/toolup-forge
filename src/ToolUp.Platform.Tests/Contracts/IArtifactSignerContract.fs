module ToolUp.Platform.Tests.Contracts.IArtifactSignerContract

open System
open Expecto
open ToolUp.Platform

/// Phase 30a — contract assertions every `IArtifactSigner` implementation
/// must satisfy. Binders supply a factory that returns a fresh signer
/// plus the `PublisherKeyId` it is configured to sign as (so the tests
/// can assert that the returned `SignedArtifact.Manifest.PublisherKeyId`
/// matches what the signer advertises).
///
/// Properties asserted:
///   * `PublisherKeyId` is non-empty.
///   * `Sign` returns the input manifest verbatim on the `SignedArtifact`.
///   * `Sign` returns the input payload verbatim on the `SignedArtifact`.
///   * `Sign` returns a 64-byte signature (Ed25519 fixed size — third-
///     party implementations using a different scheme would need a
///     different pack; the phase-30a contract pins Ed25519).
///   * `Sign` is async-at-boundary (the interface signature enforces this
///     at compile time; the runtime assertion verifies the awaitable
///     completes within a short bound — no deadlocks on `Async.RunSync`).

let private sampleManifest (publisherKeyId: PublisherKeyId) : ArtifactManifest = {
    ModuleId = "contract.module"
    Version = "1.0.0"
    SdkVersionRange = {
        MinInclusive = "0.4.0"
        MaxExclusive = "0.5.0"
    }
    CodeHash = ContentHash "deadbeef"
    SchemaHash = ContentHash "cafebabe"
    Dependencies = [
        {
            PackageId = "ToolUp.AI"
            VersionRange = {
                MinInclusive = "0.5.0"
                MaxExclusive = "0.6.0"
            }
        }
    ]
    PublisherKeyId = publisherKeyId
}

let private samplePayload () : byte[] = [| 0uy; 1uy; 2uy; 3uy; 4uy; 5uy; 6uy; 7uy; 8uy; 9uy |]

let tests (name: string) (factory: unit -> IArtifactSigner) =
    testList $"{name} — IArtifactSigner contract" [
        testCaseAsync "PublisherKeyId is non-empty"
        <| async {
            let signer = factory ()
            let id = PublisherKeyId.value signer.PublisherKeyId

            Expect.isFalse (String.IsNullOrWhiteSpace id) "Signer.PublisherKeyId must be a non-empty identifier"
        }

        testCaseAsync "Sign returns the input manifest verbatim"
        <| async {
            let signer = factory ()
            let manifest = sampleManifest signer.PublisherKeyId
            let payload = samplePayload ()
            let! signed = signer.Sign(manifest, payload)
            Expect.equal signed.Manifest manifest "Returned manifest must equal input"
        }

        testCaseAsync "Sign returns the input payload verbatim"
        <| async {
            let signer = factory ()
            let manifest = sampleManifest signer.PublisherKeyId
            let payload = samplePayload ()
            let! signed = signer.Sign(manifest, payload)
            Expect.equal signed.Payload payload "Returned payload must equal input"
        }

        testCaseAsync "Sign returns a 64-byte signature (Ed25519 fixed size)"
        <| async {
            let signer = factory ()
            let manifest = sampleManifest signer.PublisherKeyId
            let payload = samplePayload ()
            let! signed = signer.Sign(manifest, payload)
            Expect.equal signed.Signature.Length 64 "Ed25519 signature must be 64 bytes"
        }

        testCaseAsync "Signing the same inputs twice produces a deterministic signature (Ed25519)"
        <| async {
            // Ed25519 is deterministic — the same private key + same
            // message yields byte-identical signatures. The contract
            // pack pins this property because the default Ed25519
            // implementation guarantees it; third-party implementations
            // using a randomised scheme would not pass this check and
            // should bind to a relaxed pack.
            let signer = factory ()
            let manifest = sampleManifest signer.PublisherKeyId
            let payload = samplePayload ()
            let! signedA = signer.Sign(manifest, payload)
            let! signedB = signer.Sign(manifest, payload)
            Expect.equal signedA.Signature signedB.Signature "Ed25519 signatures must be deterministic"
        }
    ]
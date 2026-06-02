module ToolUp.Platform.Tests.Contracts.IArtifactVerifierContract

open System
open Expecto
open ToolUp.Platform

/// Phase 30a — contract assertions every `IArtifactVerifier` implementation
/// must satisfy. The harness takes a factory that returns:
///   * a configured `IArtifactVerifier`,
///   * the matching `IPublisherKeyStore` (so the test can seed trust),
///   * an `IArtifactSigner` aligned with a publisher id, AND
///   * the trusted publisher id + its public-key bytes (so the harness
///     can seed `IPublisherKeyStore`).
///
/// Properties asserted (matching Phase 30a acceptance criteria):
///   * Round-trip on bytes the manifest covers passes verification.
///   * Mutated payload bytes fail verification.
///   * Untrusted publisher key produces
///     `ArtifactValidation.Error "untrusted publisher"`.
///   * Mutated signature length fails verification.

type ArtifactVerifierFixture = {
    Verifier: IArtifactVerifier
    KeyStore: IPublisherKeyStore
    Signer: IArtifactSigner
    TrustedPublisherId: PublisherKeyId
    TrustedPublicKey: byte[]
}

let private sampleManifest (publisherKeyId: PublisherKeyId) : ArtifactManifest = {
    ModuleId = "contract.module"
    Version = "1.2.3"
    SdkVersionRange = {
        MinInclusive = "0.4.0"
        MaxExclusive = "0.5.0"
    }
    CodeHash = ContentHash "feedface"
    SchemaHash = ContentHash "1234abcd"
    Dependencies = []
    PublisherKeyId = publisherKeyId
}

let private samplePayload () : byte[] = [| for i in 0..31 -> byte i |]

let tests (name: string) (factory: unit -> ArtifactVerifierFixture) =
    testList $"{name} — IArtifactVerifier contract" [
        testCaseAsync "Round-trip on signed bytes the manifest covers — Ok"
        <| async {
            let fx = factory ()

            do! fx.KeyStore.AddTrustedKey(fx.TrustedPublisherId, fx.TrustedPublicKey)

            let manifest = sampleManifest fx.TrustedPublisherId

            let payload = samplePayload ()
            let! signed = fx.Signer.Sign(manifest, payload)
            let! result = fx.Verifier.Verify signed

            match result with
            | ArtifactValidation.Ok -> ()
            | ArtifactValidation.Error reason -> failtestf "Round-trip must verify; got Error: %s" reason
        }

        testCaseAsync "Mutated payload bytes — Error"
        <| async {
            let fx = factory ()

            do! fx.KeyStore.AddTrustedKey(fx.TrustedPublisherId, fx.TrustedPublicKey)

            let manifest = sampleManifest fx.TrustedPublisherId

            let payload = samplePayload ()
            let! signed = fx.Signer.Sign(manifest, payload)

            let mutatedPayload = Array.copy signed.Payload

            mutatedPayload[0] <- mutatedPayload[0] ^^^ 0xFFuy

            let tampered = { signed with Payload = mutatedPayload }

            let! result = fx.Verifier.Verify tampered

            match result with
            | ArtifactValidation.Ok -> failtest "Mutated payload must fail verification"
            | ArtifactValidation.Error _ -> ()
        }

        testCaseAsync "Mutated manifest fields — Error"
        <| async {
            let fx = factory ()

            do! fx.KeyStore.AddTrustedKey(fx.TrustedPublisherId, fx.TrustedPublicKey)

            let manifest = sampleManifest fx.TrustedPublisherId

            let payload = samplePayload ()
            let! signed = fx.Signer.Sign(manifest, payload)

            let tampered = {
                signed with
                    Manifest = {
                        signed.Manifest with
                            ModuleId = "contract.module.mutated"
                    }
            }

            let! result = fx.Verifier.Verify tampered

            match result with
            | ArtifactValidation.Ok -> failtest "Mutated manifest must fail verification"
            | ArtifactValidation.Error _ -> ()
        }

        testCaseAsync "Untrusted publisher key — Error \"untrusted publisher\""
        <| async {
            let fx = factory ()
            // Deliberately DO NOT add the publisher key to the store.
            let manifest = sampleManifest fx.TrustedPublisherId

            let payload = samplePayload ()
            let! signed = fx.Signer.Sign(manifest, payload)
            let! result = fx.Verifier.Verify signed

            match result with
            | ArtifactValidation.Ok -> failtest "Untrusted publisher must be refused"
            | ArtifactValidation.Error reason ->
                Expect.equal
                    reason
                    "untrusted publisher"
                    "Refusal reason must be 'untrusted publisher' verbatim per Phase 30a acceptance"
        }

        testCaseAsync "Invalid signature length — Error"
        <| async {
            let fx = factory ()

            do! fx.KeyStore.AddTrustedKey(fx.TrustedPublisherId, fx.TrustedPublicKey)

            let manifest = sampleManifest fx.TrustedPublisherId

            let payload = samplePayload ()
            let! signed = fx.Signer.Sign(manifest, payload)

            let tampered = {
                signed with
                    Signature = [| 1uy; 2uy; 3uy |]
            }

            let! result = fx.Verifier.Verify tampered

            match result with
            | ArtifactValidation.Ok -> failtest "Short signature must be refused"
            | ArtifactValidation.Error _ -> ()
        }
    ]
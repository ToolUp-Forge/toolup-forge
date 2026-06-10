// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.DefaultArtefactSignerTests

open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.ArtefactSigning
open ToolUp.ArtefactSigning.Tests.Support.InMemoryStores
open ToolUp.ArtefactSigning.Tests.Contracts

/// Build a contract fixture for the in-process default signer + verifier
/// against a shared in-memory secret store, for the given algorithm.
let private fixtureFor (algorithm: SigningAlgorithm) () : IArtefactSignerContract.ArtefactSignerFixture =
    let secrets = InMemorySecretStore()
    let audit = InMemoryAuditLog()
    let keyId = $"signing-{SigningAlgorithm.name algorithm}-v1"

    {
        Signer = DefaultArtefactSigner.createSystem secrets audit keyId algorithm
        Verifier = DefaultArtefactVerifier.create secrets
    }

let private inProcessSpecificTests =
    testList "DefaultArtefactSigner — in-process specifics" [
        testCaseAsync "Sign emits an ArtefactSigned audit event with the artefact SHA-256"
        <| async {
            let secrets = InMemorySecretStore()
            let audit = InMemoryAuditLog()

            let signer =
                DefaultArtefactSigner.create secrets audit "signing-audit-v1" EcdsaP256 "operator-7"

            let payload = Encoding.UTF8.GetBytes "audit-pack-contents"
            let! _ = signer.Sign payload

            // SHA-256 of the payload, lowercase hex.
            use sha = System.Security.Cryptography.SHA256.Create()
            let expectedHash = sha.ComputeHash payload |> System.Convert.ToHexStringLower

            let signedEvents =
                audit.AllEvents
                |> List.choose (fun (_, e) ->
                    match e with
                    | AuditEvent.ArtefactSigned p -> Some p
                    | _ -> None)

            Expect.equal signedEvents.Length 1 "exactly one ArtefactSigned event"
            let p = signedEvents[0]
            Expect.equal p.Actor "operator-7" "actor stamped"
            Expect.equal p.KeyId "signing-audit-v1" "key id stamped"
            Expect.equal p.Algorithm "EcdsaP256" "algorithm name stamped"
            Expect.equal p.ArtefactSha256 expectedHash "artefact SHA-256 recorded, not the bytes"
        }

        testCaseAsync "Key rotation: signatures under a rotated-out key still verify"
        <| async {
            let secrets = InMemorySecretStore()
            let audit = InMemoryAuditLog()

            // v1 signs an artefact.
            let signerV1 =
                DefaultArtefactSigner.createSystem secrets audit "signing-rot-v1" EcdsaP256

            let payload = Encoding.UTF8.GetBytes "report-2026-Q2"
            let! v1Result = signerV1.Sign payload

            let v1Sig =
                match v1Result with
                | Ok s -> s
                | Error e -> failtestf "v1 sign failed: %s" (SigningError.describe e)

            // v2 becomes the active signing key; v1's key blob remains.
            let signerV2 =
                DefaultArtefactSigner.createSystem secrets audit "signing-rot-v2" Ed25519

            let! v2Result = signerV2.Sign payload

            let v2Sig =
                match v2Result with
                | Ok s -> s
                | Error e -> failtestf "v2 sign failed: %s" (SigningError.describe e)

            Expect.notEqual v1Sig.KeyId v2Sig.KeyId "v2 signs under a new key id"

            // The verifier resolves by key id, so the old v1 signature
            // still verifies against the (rotated-out) v1 public key.
            let verifier = DefaultArtefactVerifier.create secrets
            let! v1Verify = verifier.Verify(payload, v1Sig)
            let! v2Verify = verifier.Verify(payload, v2Sig)

            match v1Verify with
            | Ok() -> ()
            | Error e -> failtestf "rotated-out v1 signature must still verify; got %s" (VerificationError.describe e)

            match v2Verify with
            | Ok() -> ()
            | Error e -> failtestf "active v2 signature must verify; got %s" (VerificationError.describe e)
        }

        testCaseAsync "Read-only store with no pre-seeded key — Sign returns KeyUnavailable"
        <| async {
            let audit = InMemoryAuditLog()

            let signer =
                DefaultArtefactSigner.createSystem (ReadOnlySecretStore()) audit "signing-ro-v1" EcdsaP256

            let! result = signer.Sign(Encoding.UTF8.GetBytes "x")

            match result with
            | Error(KeyUnavailable _) -> ()
            | Error other -> failtestf "Expected KeyUnavailable; got %s" (SigningError.describe other)
            | Ok _ -> failtest "read-only store with no key must not produce a signature"
        }

        testCaseAsync "signedJsonEnvelope round-trips through verifyJsonEnvelope"
        <| async {
            let secrets = InMemorySecretStore()
            let audit = InMemoryAuditLog()

            let signer =
                DefaultArtefactSigner.createSystem secrets audit "signing-env-v1" EcdsaP256

            let verifier = DefaultArtefactVerifier.create secrets
            let payload = """{"amount":1234,"currency":"GBP","note":"audit"}"""
            let! envelopeResult = ArtefactSigning.signedJsonEnvelope signer payload

            match envelopeResult with
            | Error e -> failtestf "envelope sign failed: %s" (SigningError.describe e)
            | Ok envelope ->
                Expect.stringContains envelope "\"payload\"" "envelope carries payload"
                Expect.stringContains envelope "\"signature\"" "envelope carries signature"
                let! verifyResult = ArtefactSigning.verifyJsonEnvelope verifier envelope

                match verifyResult with
                | Ok _ -> ()
                | Error e -> failtestf "envelope must verify; got %s" (VerificationError.describe e)
        }

        testCaseAsync "signAndEmbed leaves the artefact unchanged and yields a JWS sidecar"
        <| async {
            let secrets = InMemorySecretStore()
            let audit = InMemoryAuditLog()

            let signer =
                DefaultArtefactSigner.createSystem secrets audit "signing-sidecar-v1" Ed25519

            let verifier = DefaultArtefactVerifier.create secrets
            let payload = Encoding.UTF8.GetBytes "the artefact bytes"
            let! result = ArtefactSigning.signAndEmbed signer payload

            match result with
            | Error e -> failtestf "signAndEmbed failed: %s" (SigningError.describe e)
            | Ok sidecar ->
                Expect.equal sidecar.Artefact payload "artefact returned unchanged (detached)"
                let jwsFromSidecar = Encoding.UTF8.GetString sidecar.Sidecar
                Expect.equal jwsFromSidecar sidecar.Signature.DetachedJws "sidecar bytes are the detached JWS"
                let! verifyResult = verifier.Verify(payload, sidecar.Signature)

                match verifyResult with
                | Ok() -> ()
                | Error e -> failtestf "sidecar signature must verify; got %s" (VerificationError.describe e)
        }
    ]

[<Tests>]
let tests =
    testList "ToolUp.ArtefactSigning" [
        IArtefactSignerContract.tests "ECDSA P-256 (in-process)" (fixtureFor EcdsaP256)
        IArtefactSignerContract.tests "Ed25519 (in-process)" (fixtureFor Ed25519)
        inProcessSpecificTests
    ]
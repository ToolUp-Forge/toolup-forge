// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.JwsBuilderTests

open System
open System.Security.Cryptography
open Expecto
open ToolUp.Platform.Secrets
open ToolUp.ArtefactSigning
open ToolUp.ArtefactSigning.Tests.Support.InMemoryStores

// ─── Phase 40 / 22a — JwsBuilder offline tests ──────────────────────────
//
// The AWS-KMS-backed signer (ToolUp.ArtefactSigning.AwsKms) relies on the
// public `JwsBuilder` surface: it asks KMS to sign the digest (KMS returns
// an ASN.1 DER ECDSA signature), converts it to raw r‖s, and assembles a
// detached JWS the shipped `DefaultArtefactVerifier` validates. KMS has no
// offline emulator, so these tests exercise the substrate the companion
// depends on WITHOUT a live KMS — a local ECDSA key stands in for the KMS
// key (it produces the same DER signature shape over the same digest).

let private rnd = Random(20260615)

let private randomBytes (n: int) : byte[] =
    let b = Array.zeroCreate n
    rnd.NextBytes b
    b

/// JSON shape of a StoredSigningKey (SigningInternals) — seeded directly
/// so `DefaultArtefactVerifier` can resolve the public key for the
/// KMS-stand-in key id without going through the in-process signer.
let private storedKeyJson (pkcs8: byte[]) (spki: byte[]) : string =
    let priv = Convert.ToBase64String pkcs8
    let pub = Convert.ToBase64String spki
    let created = DateTimeOffset.UtcNow.ToString("O")
    $"""{{"alg":"EcdsaP256","private":"{priv}","public":"{pub}","createdAt":"{created}"}}"""

let tests =
    testList "Phase 40 / 22a — JwsBuilder (KMS-signing substrate)" [
        testCaseAsync "derEcdsaToP1363 converts a DER ECDSA signature to a verifiable raw r‖s"
        <| async {
            use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            let input = randomBytes 128

            // DER SEQUENCE { r, s } — the shape AWS KMS Sign returns.
            let der =
                ec.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)

            match JwsBuilder.derEcdsaToP1363 der 32 with
            | Error e -> failtestf "conversion must succeed; got %s" e
            | Ok p1363 ->
                Expect.equal p1363.Length 64 "P-256 P1363 signature is 64 bytes (r‖s, 32 each)"

                let ok =
                    ec.VerifyData(
                        input,
                        p1363,
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation
                    )

                Expect.isTrue ok "the converted P1363 signature verifies against the same key"
        }

        testCaseAsync "derEcdsaToP1363 rejects a malformed DER blob"
        <| async {
            match JwsBuilder.derEcdsaToP1363 [| 0x01uy; 0x02uy; 0x03uy |] 32 with
            | Error _ -> ()
            | Ok _ -> failtest "a non-SEQUENCE blob must not convert"
        }

        testCaseAsync "A KMS-style detached JWS (sign-the-digest → DER → P1363) verifies with DefaultArtefactVerifier"
        <| async {
            // Local ECDSA key stands in for the KMS key — same DER-over-
            // digest shape the companion drives through KMS.Sign.
            use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            let pkcs8 = ec.ExportPkcs8PrivateKey()
            let spki = ec.ExportSubjectPublicKeyInfo()
            let keyId = "kms-test-v1"

            let secrets = InMemorySecretStore()

            do!
                (secrets :> ISecretStore).SetSecret("_platform", $"signing/{keyId}", storedKeyJson pkcs8 spki)
                |> Async.Ignore

            let artefact = randomBytes 256

            // Reproduce the companion's Sign path exactly.
            let encodedHeader = JwsBuilder.protectedHeaderEncoded EcdsaP256 keyId
            let input = JwsBuilder.signingInput encodedHeader artefact
            let digest = SHA256.HashData input
            let der = ec.SignHash(digest, DSASignatureFormat.Rfc3279DerSequence)

            let jws =
                match JwsBuilder.derEcdsaToP1363 der 32 with
                | Ok raw -> JwsBuilder.assembleDetachedJws encodedHeader raw
                | Error e -> failtestf "conversion failed: %s" e

            let signature = {
                KeyId = keyId
                Algorithm = EcdsaP256
                SignedAt = DateTimeOffset.UtcNow
                DetachedJws = jws
            }

            let verifier = DefaultArtefactVerifier.create secrets

            match! verifier.Verify(artefact, signature) with
            | Ok() -> ()
            | Error e -> failtestf "KMS-style JWS must verify; got %s" (VerificationError.describe e)

            // Tampered artefact must fail.
            let tampered = Array.copy artefact
            tampered[0] <- tampered[0] ^^^ 0xFFuy

            match! verifier.Verify(tampered, signature) with
            | Error Tampered -> ()
            | Ok() -> failtest "a tampered artefact must not verify"
            | Error other -> failtestf "expected Tampered; got %s" (VerificationError.describe other)
        }
    ]
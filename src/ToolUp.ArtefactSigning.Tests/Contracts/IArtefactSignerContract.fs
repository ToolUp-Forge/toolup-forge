// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.Contracts.IArtefactSignerContract

open Expecto
open ToolUp.ArtefactSigning

/// Phase 40 — contract assertions every `IArtefactSigner` +
/// `IArtefactVerifier` pair must satisfy, in-process or KMS-backed. The
/// harness takes a factory producing a signer + a verifier that resolve
/// the same key material (so a round-trip signed by the signer is
/// verifiable by the verifier).
///
/// Asserted (matching Phase 40 acceptance criteria):
///   * sign/verify round-trip on the signed bytes — Ok;
///   * tampered artefact bytes — Error Tampered;
///   * tampered signature segment — Error;
///   * unknown key id — Error UnknownKey;
///   * empty artefact round-trips;
///   * `VerifyKey` yields well-formed PEM + JWK with a matching kid;
///   * `KeyId` is stamped onto the produced signature.
type ArtefactSignerFixture = {
    Signer: IArtefactSigner
    Verifier: IArtefactVerifier
}

let private samplePayload () : byte[] = [| for i in 0..63 -> byte (i * 7 % 256) |]

let tests (name: string) (factory: unit -> ArtefactSignerFixture) =
    testList $"{name} — IArtefactSigner contract" [
        testCaseAsync "Round-trip on signed bytes — Ok"
        <| async {
            let fx = factory ()
            let payload = samplePayload ()
            let! signResult = fx.Signer.Sign payload

            match signResult with
            | Error e -> failtestf "Sign must succeed; got %s" (SigningError.describe e)
            | Ok sig_ ->
                let! verifyResult = fx.Verifier.Verify(payload, sig_)

                match verifyResult with
                | Ok() -> ()
                | Error e -> failtestf "Round-trip must verify; got %s" (VerificationError.describe e)
        }

        testCaseAsync "Signature stamps the signer's KeyId"
        <| async {
            let fx = factory ()
            let! signResult = fx.Signer.Sign(samplePayload ())

            match signResult with
            | Error e -> failtestf "Sign must succeed; got %s" (SigningError.describe e)
            | Ok sig_ -> Expect.equal sig_.KeyId (fx.Signer.KeyId()) "ArtefactSignature.KeyId must match signer KeyId"
        }

        testCaseAsync "Tampered artefact bytes — Error Tampered"
        <| async {
            let fx = factory ()
            let payload = samplePayload ()
            let! signResult = fx.Signer.Sign payload

            match signResult with
            | Error e -> failtestf "Sign must succeed; got %s" (SigningError.describe e)
            | Ok sig_ ->
                let mutated = Array.copy payload
                mutated[0] <- mutated[0] ^^^ 0xFFuy
                let! verifyResult = fx.Verifier.Verify(mutated, sig_)

                match verifyResult with
                | Ok() -> failtest "Mutated artefact must fail verification"
                | Error Tampered -> ()
                | Error other -> failtestf "Expected Tampered; got %s" (VerificationError.describe other)
        }

        testCaseAsync "Tampered signature segment — Error"
        <| async {
            let fx = factory ()
            let payload = samplePayload ()
            let! signResult = fx.Signer.Sign payload

            match signResult with
            | Error e -> failtestf "Sign must succeed; got %s" (SigningError.describe e)
            | Ok sig_ ->
                // Flip a character in the signature segment of the
                // detached JWS (`header..signature`).
                let parts = sig_.DetachedJws.Split('.')
                let sigSeg = parts[2]

                let flipped =
                    (if sigSeg[0] = 'A' then 'B' else 'A').ToString() + sigSeg.Substring(1)

                let tamperedJws = parts[0] + ".." + flipped
                let tampered = { sig_ with DetachedJws = tamperedJws }
                let! verifyResult = fx.Verifier.Verify(payload, tampered)

                match verifyResult with
                | Ok() -> failtest "Tampered signature must fail verification"
                | Error _ -> ()
        }

        testCaseAsync "Unknown key id — Error UnknownKey"
        <| async {
            let fx = factory ()
            let payload = samplePayload ()
            let! signResult = fx.Signer.Sign payload

            match signResult with
            | Error e -> failtestf "Sign must succeed; got %s" (SigningError.describe e)
            | Ok sig_ ->
                let bogus = {
                    sig_ with
                        KeyId = "no-such-key-id-xyz"
                }

                let! verifyResult = fx.Verifier.Verify(payload, bogus)

                match verifyResult with
                | Ok() -> failtest "Unknown key id must be refused"
                | Error(UnknownKey k) -> Expect.equal k "no-such-key-id-xyz" "UnknownKey carries the requested id"
                | Error other -> failtestf "Expected UnknownKey; got %s" (VerificationError.describe other)
        }

        testCaseAsync "Empty artefact round-trips"
        <| async {
            let fx = factory ()
            let! signResult = fx.Signer.Sign [||]

            match signResult with
            | Error e -> failtestf "Sign of empty artefact must succeed; got %s" (SigningError.describe e)
            | Ok sig_ ->
                let! verifyResult = fx.Verifier.Verify([||], sig_)

                match verifyResult with
                | Ok() -> ()
                | Error e -> failtestf "Empty-artefact round-trip must verify; got %s" (VerificationError.describe e)
        }

        testCaseAsync "VerifyKey yields well-formed PEM + JWK with matching kid"
        <| async {
            let fx = factory ()
            let! meta = fx.Signer.VerifyKey()
            Expect.equal meta.KeyId (fx.Signer.KeyId()) "PublicKeyMetadata.KeyId must match signer KeyId"

            Expect.stringContains meta.Pem "-----BEGIN PUBLIC KEY-----" "PEM must carry the SPKI header"
            Expect.stringContains meta.Pem "-----END PUBLIC KEY-----" "PEM must carry the SPKI footer"
            Expect.stringContains meta.Jwk "\"kid\"" "JWK must carry a kid"
            Expect.stringContains meta.Jwk (fx.Signer.KeyId()) "JWK kid must be the signer key id"
        }
    ]
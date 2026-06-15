// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning.AwsKms

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Amazon.KeyManagementService
open Amazon.KeyManagementService.Model
open ToolUp.ArtefactSigning

// ─── Phase 40 / 22a — AWS KMS-backed IArtefactSigner ───────────────────
//
// Signs artefacts with an AWS KMS asymmetric ECDSA P-256 key (KeySpec
// ECC_NIST_P256). The private key never enters process memory: the signer
// hashes the JWS signing input locally (SHA-256) and asks KMS to sign the
// digest, then converts the DER signature KMS returns into the raw r‖s
// shape JWS ES256 requires (`JwsBuilder.derEcdsaToP1363`). The detached
// JWS is byte-identical to the in-process `DefaultArtefactSigner` output,
// so the shipped `DefaultArtefactVerifier` (or any JWS verifier holding
// the public key) validates it.
//
// **ECDSA P-256 only.** AWS KMS asymmetric signing offers RSA + ECC
// (P-256/384/521) + SM2 — not Ed25519. The Phase 40 `EdDSA` flavour stays
// in-process; the KMS arm covers `ES256`, the JWS shape compliance
// auditors expect for non-repudiation.

/// AWS KMS-backed `IArtefactSigner`. Pass an `IAmazonKeyManagementService`
/// (constructed by the deployment with its region + credentials) and the
/// id/ARN of an asymmetric ECC_NIST_P256 signing key.
type AwsKmsArtefactSigner(kms: IAmazonKeyManagementService, keyId: string) =

    let pemWrap (label: string) (der: byte[]) : string =
        let b64 = Convert.ToBase64String der
        let sb = StringBuilder()
        sb.Append("-----BEGIN ").Append(label).Append("-----\n") |> ignore
        let mutable i = 0

        while i < b64.Length do
            let len = min 64 (b64.Length - i)
            sb.Append(b64.Substring(i, len)).Append('\n') |> ignore
            i <- i + 64

        sb.Append("-----END ").Append(label).Append("-----\n") |> ignore
        sb.ToString()

    interface IArtefactSigner with
        member _.KeyId() = keyId

        member _.Sign(artefact: byte[]) : Async<Result<ArtefactSignature, SigningError>> = async {
            try
                let encodedHeader = JwsBuilder.protectedHeaderEncoded EcdsaP256 keyId
                let input = JwsBuilder.signingInput encodedHeader artefact
                let digest = SHA256.HashData input

                let req =
                    SignRequest(
                        KeyId = keyId,
                        Message = new MemoryStream(digest),
                        MessageType = MessageType.DIGEST,
                        SigningAlgorithm = SigningAlgorithmSpec.ECDSA_SHA_256
                    )

                let! resp = kms.SignAsync req |> Async.AwaitTask

                match JwsBuilder.derEcdsaToP1363 (resp.Signature.ToArray()) 32 with
                | Error e -> return Error(CryptoFailure $"DER→P1363 conversion failed: {e}")
                | Ok raw ->
                    return
                        Ok {
                            KeyId = keyId
                            Algorithm = EcdsaP256
                            SignedAt = DateTimeOffset.UtcNow
                            DetachedJws = JwsBuilder.assembleDetachedJws encodedHeader raw
                        }
            with
            | :? NotFoundException as ex -> return Error(KeyUnavailable ex.Message)
            | :? DisabledException as ex -> return Error(KeyUnavailable ex.Message)
            | :? KMSInvalidStateException as ex -> return Error(KeyUnavailable ex.Message)
            | ex -> return Error(CryptoFailure ex.Message)
        }

        member _.VerifyKey() : Async<PublicKeyMetadata> = async {
            let! resp = kms.GetPublicKeyAsync(GetPublicKeyRequest(KeyId = keyId)) |> Async.AwaitTask
            let spki = resp.PublicKey.ToArray()

            use ec = ECDsa.Create()
            ec.ImportSubjectPublicKeyInfo(ReadOnlySpan<byte>(spki)) |> ignore
            let p = ec.ExportParameters false

            let jwk = JsonObject()
            jwk["kty"] <- JsonValue.Create "EC"
            jwk["crv"] <- JsonValue.Create "P-256"
            jwk["x"] <- JsonValue.Create(JwsBuilder.base64UrlEncode p.Q.X)
            jwk["y"] <- JsonValue.Create(JwsBuilder.base64UrlEncode p.Q.Y)
            jwk["alg"] <- JsonValue.Create "ES256"
            jwk["use"] <- JsonValue.Create "sig"
            jwk["kid"] <- JsonValue.Create keyId

            return {
                KeyId = keyId
                Algorithm = EcdsaP256
                Pem = pemWrap "PUBLIC KEY" spki
                Jwk = jwk.ToJsonString()
            }
        }

module AwsKmsArtefactSigner =
    /// Construct an AWS-KMS-backed `IArtefactSigner` over an asymmetric
    /// ECC_NIST_P256 signing key. The private key never leaves KMS — every
    /// `Sign` is a KMS `Sign` API call over the local SHA-256 digest.
    let create (kms: IAmazonKeyManagementService) (keyId: string) : IArtefactSigner =
        AwsKmsArtefactSigner(kms, keyId) :> IArtefactSigner
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning.GoogleCloudKms

open System
open System.Security.Cryptography
open System.Text.Json.Nodes
open Google.Cloud.Kms.V1
open Google.Protobuf
open Grpc.Core
open ToolUp.ArtefactSigning

// ─── Phase 160 / 40 / 22a — GCP Cloud KMS-backed IArtefactSigner ────────
//
// Signs artefacts with a GCP Cloud KMS asymmetric EC key
// (`EC_SIGN_P256_SHA256`). The private key never enters process memory:
// the signer hashes the JWS signing input locally (SHA-256) and asks KMS
// to sign the digest via `AsymmetricSign`, then converts the DER
// signature KMS returns into the raw `r‖s` shape JWS ES256 requires
// (`JwsBuilder.derEcdsaToP1363`). The detached JWS is byte-identical to
// the in-process `DefaultArtefactSigner` output, so the shipped
// `DefaultArtefactVerifier` (or any JWS verifier holding the public key)
// validates it.
//
// **EC-P256 / SHA-256 only.** This companion covers `ES256`, the JWS
// shape compliance auditors expect for non-repudiation. The Phase 40
// `EdDSA` flavour stays in-process (`DefaultArtefactSigner`). The KMS key
// must be created with purpose `ASYMMETRIC_SIGN` and algorithm
// `EC_SIGN_P256_SHA256`. Like AWS KMS (and unlike Azure Key Vault, which
// returns P1363 directly), GCP `AsymmetricSign` returns an ASN.1 DER
// signature, so the DER→P1363 conversion is required here.

/// GCP Cloud KMS-backed `IArtefactSigner`. `client` is constructed by the
/// deployment (`KeyManagementServiceClient.Create()` picks up ADC);
/// `keyVersionName` is the asymmetric-signing key *version* resource name
/// (`projects/p/locations/l/keyRings/r/cryptoKeys/k/cryptoKeyVersions/v`),
/// stamped as the JWS `kid` and the `ArtefactSignature.KeyId`.
type GoogleCloudKmsArtefactSigner(client: KeyManagementServiceClient, keyVersionName: CryptoKeyVersionName) =

    let keyId = keyVersionName.ToString()

    interface IArtefactSigner with
        member _.KeyId() = keyId

        member _.Sign(artefact: byte[]) : Async<Result<ArtefactSignature, SigningError>> = async {
            try
                let encodedHeader = JwsBuilder.protectedHeaderEncoded EcdsaP256 keyId
                let input = JwsBuilder.signingInput encodedHeader artefact
                let digest = SHA256.HashData input

                let req =
                    AsymmetricSignRequest(
                        CryptoKeyVersionName = keyVersionName,
                        Digest = Digest(Sha256 = ByteString.CopyFrom digest)
                    )

                let! resp = client.AsymmetricSignAsync req |> Async.AwaitTask

                // KMS returns an ASN.1 DER ECDSA signature; JWS ES256 wants
                // raw r‖s (32 bytes each for P-256).
                match JwsBuilder.derEcdsaToP1363 (resp.Signature.ToByteArray()) 32 with
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
            | :? RpcException as rpc when rpc.StatusCode = StatusCode.NotFound ->
                return Error(KeyUnavailable rpc.Message)
            | :? RpcException as rpc when
                rpc.StatusCode = StatusCode.FailedPrecondition
                || rpc.StatusCode = StatusCode.PermissionDenied
                ->
                return Error(KeyUnavailable rpc.Message)
            | ex -> return Error(CryptoFailure ex.Message)
        }

        member _.VerifyKey() : Async<PublicKeyMetadata> = async {
            let! pub = client.GetPublicKeyAsync keyVersionName |> Async.AwaitTask
            // KMS hands back the public key as a SubjectPublicKeyInfo PEM;
            // parse it for the EC point and re-emit the JWK in the same
            // shape the AWS / Azure arms produce (kty/crv/x/y/alg/use/kid).
            use ec = ECDsa.Create()
            ec.ImportFromPem(pub.Pem) |> ignore
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
                Pem = pub.Pem
                Jwk = jwk.ToJsonString()
            }
        }

module GoogleCloudKmsArtefactSigner =
    /// Construct a GCP-KMS-backed `IArtefactSigner` over an asymmetric
    /// `EC_SIGN_P256_SHA256` key version. The private key never leaves KMS
    /// — every `Sign` is a KMS `AsymmetricSign` API call over the local
    /// SHA-256 digest. `keyVersionName` is the key-version resource name
    /// (`projects/p/locations/l/keyRings/r/cryptoKeys/k/cryptoKeyVersions/v`).
    let create (client: KeyManagementServiceClient) (keyVersionName: CryptoKeyVersionName) : IArtefactSigner =
        GoogleCloudKmsArtefactSigner(client, keyVersionName) :> IArtefactSigner

    /// As `create`, taking the key-version resource name as a string.
    let createFromName (client: KeyManagementServiceClient) (keyVersionName: string) : IArtefactSigner =
        GoogleCloudKmsArtefactSigner(client, CryptoKeyVersionName.Parse keyVersionName) :> IArtefactSigner
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning.AzureKeyVault

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Azure
open Azure.Core
open Azure.Security.KeyVault.Keys
open Azure.Security.KeyVault.Keys.Cryptography
open ToolUp.ArtefactSigning

// ─── Phase 160 / 40 / 22a — Azure Key Vault-backed IArtefactSigner ──────
//
// Signs artefacts with an Azure Key Vault (or Managed HSM) asymmetric
// EC-P256 key (`ES256`). The private key never enters process memory: the
// signer hashes the JWS signing input locally (SHA-256) and asks Key
// Vault to sign the digest via `CryptographyClient.Sign`, then assembles
// the detached JWS. The result is byte-identical to the in-process
// `DefaultArtefactSigner` output, so the shipped `DefaultArtefactVerifier`
// (or any JWS verifier holding the public key) validates it.
//
// **Signature format — no DER→P1363 conversion needed (unlike the AWS /
// GCP arms).** Azure Key Vault returns ECDSA signatures already in IEEE
// P1363 fixed-width `r‖s` form — the exact JWS ES256 shape — because Key
// Vault follows the JOSE/JWA conventions. The AWS KMS arm and the GCP KMS
// arm both receive an ASN.1 DER signature and must run
// `JwsBuilder.derEcdsaToP1363`; the Azure signature segment is passed
// straight to `assembleDetachedJws`.
//
// **EC-P256 only.** This companion covers `ES256`, the JWS shape
// compliance auditors expect for non-repudiation. The Phase 40 `EdDSA`
// flavour stays in-process (`DefaultArtefactSigner`). The Key Vault key
// must be created as EC with curve `P-256` and key operations including
// `sign`.

/// Azure Key Vault-backed `IArtefactSigner`. `crypto` signs the digest
/// against the EC-P256 KEK; `keyClient` serves the public key for
/// `VerifyKey`; `keyName` is the key's vault name, stamped as the JWS
/// `kid` and the `ArtefactSignature.KeyId`.
type AzureKeyVaultArtefactSigner(crypto: CryptographyClient, keyClient: KeyClient, keyName: string) =

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

    /// Convenience ctor — build both clients from a credential + vault key
    /// identifier. `keyId` is the full key URI
    /// (`https://my-vault.vault.azure.net/keys/<name>` or a versioned id);
    /// the vault uri + key name are derived from it.
    new(credential: TokenCredential, keyId: Uri) =
        // keyId path is `/keys/<name>[/<version>]`; the vault uri is the
        // authority. CryptographyClient signs against the (optionally
        // versioned) key id; KeyClient resolves the public key by name.
        let vaultUri = Uri(keyId.GetLeftPart(UriPartial.Authority))
        let segments = keyId.AbsolutePath.Trim('/').Split('/')

        let keyName =
            if segments.Length >= 2 then
                segments[1]
            else
                segments[segments.Length - 1]

        AzureKeyVaultArtefactSigner(CryptographyClient(keyId, credential), KeyClient(vaultUri, credential), keyName)

    interface IArtefactSigner with
        member _.KeyId() = keyName

        member _.Sign(artefact: byte[]) : Async<Result<ArtefactSignature, SigningError>> = async {
            try
                let encodedHeader = JwsBuilder.protectedHeaderEncoded EcdsaP256 keyName
                let input = JwsBuilder.signingInput encodedHeader artefact
                let digest = SHA256.HashData input

                // Key Vault signs the digest with the KEK and returns the
                // signature already in IEEE P1363 r‖s form — the JWS ES256
                // shape, so no DER→P1363 conversion (the AWS/GCP arms need
                // one; Key Vault does not).
                let! result = crypto.SignAsync(SignatureAlgorithm.ES256, digest) |> Async.AwaitTask

                return
                    Ok {
                        KeyId = keyName
                        Algorithm = EcdsaP256
                        SignedAt = DateTimeOffset.UtcNow
                        DetachedJws = JwsBuilder.assembleDetachedJws encodedHeader result.Signature
                    }
            with
            | :? RequestFailedException as rfe when rfe.Status = 404 -> return Error(KeyUnavailable rfe.Message)
            | :? RequestFailedException as rfe when rfe.Status = 403 -> return Error(KeyUnavailable rfe.Message)
            | ex -> return Error(CryptoFailure ex.Message)
        }

        member _.VerifyKey() : Async<PublicKeyMetadata> = async {
            let! key = keyClient.GetKeyAsync keyName |> Async.AwaitTask
            // The public EC point comes back as the JWK x/y coordinates;
            // export an SPKI for the PEM and re-emit the JWK in the same
            // shape the AWS arm produces (kty/crv/x/y/alg/use/kid).
            use ec = key.Value.Key.ToECDsa(false)
            let spki = ec.ExportSubjectPublicKeyInfo()
            let p = ec.ExportParameters false

            let jwk = JsonObject()
            jwk["kty"] <- JsonValue.Create "EC"
            jwk["crv"] <- JsonValue.Create "P-256"
            jwk["x"] <- JsonValue.Create(JwsBuilder.base64UrlEncode p.Q.X)
            jwk["y"] <- JsonValue.Create(JwsBuilder.base64UrlEncode p.Q.Y)
            jwk["alg"] <- JsonValue.Create "ES256"
            jwk["use"] <- JsonValue.Create "sig"
            jwk["kid"] <- JsonValue.Create keyName

            return {
                KeyId = keyName
                Algorithm = EcdsaP256
                Pem = pemWrap "PUBLIC KEY" spki
                Jwk = jwk.ToJsonString()
            }
        }

module AzureKeyVaultArtefactSigner =
    /// Construct an Azure-Key-Vault-backed `IArtefactSigner` over an
    /// asymmetric EC-P256 signing key. The private key never leaves Key
    /// Vault — every `Sign` is a Key Vault `Sign` API call over the local
    /// SHA-256 digest. `keyId` is the full key identifier URI
    /// (`https://my-vault.vault.azure.net/keys/<name>` or a versioned id).
    let create (credential: TokenCredential) (keyId: Uri) : IArtefactSigner =
        AzureKeyVaultArtefactSigner(credential, keyId) :> IArtefactSigner
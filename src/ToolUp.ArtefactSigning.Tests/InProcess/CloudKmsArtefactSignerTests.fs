// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.CloudKmsArtefactSignerTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Expecto
open ToolUp.Platform.Secrets
open ToolUp.ArtefactSigning
open ToolUp.ArtefactSigning.Tests.Support.InMemoryStores
open ToolUp.ArtefactSigning.Tests.Contracts

// ─── Phase 160 — Azure Key Vault + GCP KMS artefact-signer arms ─────────
//
// The two cloud-KMS signing arms (ToolUp.ArtefactSigning.AzureKeyVault /
// .GoogleCloudKms) drive the public `JwsBuilder` surface: hash the JWS
// signing input locally, ask the cloud KEK to sign the digest, shape the
// returned signature into the JWS ES256 form, and assemble a detached JWS
// the shipped `DefaultArtefactVerifier` validates. The clouds have no
// offline emulator and their crypto clients are not practically mockable,
// so — exactly as the AWS arm is covered offline (JwsBuilderTests) — these
// tests bind the full `IArtefactSignerContract` against a *stand-in* that
// reproduces each arm's Sign path with a local EC-P256 key. The stand-in
// differs from the real companion only in WHERE the digest is signed (a
// local key vs the cloud KEK), so it exercises the byte-shape contract end
// to end without a live cloud.
//
// The two arms differ in ONE step — the signature format the cloud
// returns, which is the parameter of the fixture:
//   * Azure Key Vault returns the signature already in IEEE P1363 r‖s
//     form (the JWS ES256 shape) — passed straight through.
//   * GCP KMS (like AWS KMS) returns an ASN.1 DER signature — converted
//     via `JwsBuilder.derEcdsaToP1363`.

/// Maps a SHA-256 digest to the raw JWS signature segment, standing in for
/// the cloud KEK's `Sign`. The format mirrors what each cloud returns.
type private RawSignatureShaper = byte[] -> byte[]

/// Offline stand-in for a cloud-KMS `IArtefactSigner`. Reproduces the
/// real companion's Sign path (header → SHA-256 digest → cloud signature
/// → `assembleDetachedJws`) and VerifyKey output, signing the digest with
/// a local EC-P256 key instead of a cloud KEK.
type private CloudStandInSigner(ec: ECDsa, keyId: string, shapeRawSignature: RawSignatureShaper) =

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
                let raw = shapeRawSignature digest

                return
                    Ok {
                        KeyId = keyId
                        Algorithm = EcdsaP256
                        SignedAt = DateTimeOffset.UtcNow
                        DetachedJws = JwsBuilder.assembleDetachedJws encodedHeader raw
                    }
            with ex ->
                return Error(CryptoFailure ex.Message)
        }

        member _.VerifyKey() : Async<PublicKeyMetadata> = async {
            let spki = ec.ExportSubjectPublicKeyInfo()
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

/// Seed the EC public key under the verifier's `signing/{keyId}` slot so
/// `DefaultArtefactVerifier` resolves it (the StoredSigningKey JSON shape;
/// only the public component is read on the verify side).
let private seedPublicKey (secrets: ISecretStore) (keyId: string) (pkcs8: byte[]) (spki: byte[]) =
    let priv = Convert.ToBase64String pkcs8
    let pub = Convert.ToBase64String spki
    let created = DateTimeOffset.UtcNow.ToString("O")

    let json =
        $"""{{"alg":"EcdsaP256","private":"{priv}","public":"{pub}","createdAt":"{created}"}}"""

    secrets.SetSecret("_platform", $"signing/{keyId}", json)
    |> Async.RunSynchronously
    |> ignore

/// Build a contract fixture for a cloud-KMS arm: a stand-in signer driving
/// the given signature-shaper, plus a `DefaultArtefactVerifier` resolving
/// the matching public key.
let private fixtureFor
    (label: string)
    (shaper: ECDsa -> RawSignatureShaper)
    ()
    : IArtefactSignerContract.ArtefactSignerFixture =
    let ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    let keyId = $"cloud-{label}-v1"
    let secrets = InMemorySecretStore()
    seedPublicKey secrets keyId (ec.ExportPkcs8PrivateKey()) (ec.ExportSubjectPublicKeyInfo())

    {
        Signer = CloudStandInSigner(ec, keyId, shaper ec)
        Verifier = DefaultArtefactVerifier.create secrets
    }

/// Azure Key Vault arm: the KEK returns the signature already in IEEE
/// P1363 r‖s form — no DER→P1363 conversion (passed straight to the JWS).
let private azureShaper (ec: ECDsa) : RawSignatureShaper =
    fun digest -> ec.SignHash(digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

/// GCP KMS arm: `AsymmetricSign` returns an ASN.1 DER signature (like AWS
/// KMS) — converted via `JwsBuilder.derEcdsaToP1363`, the companion's path.
let private gcpShaper (ec: ECDsa) : RawSignatureShaper =
    fun digest ->
        let der = ec.SignHash(digest, DSASignatureFormat.Rfc3279DerSequence)

        match JwsBuilder.derEcdsaToP1363 der 32 with
        | Ok raw -> raw
        | Error e -> failwithf "DER→P1363 conversion failed: %s" e

[<Tests>]
let tests =
    testList "Phase 160 — cloud-KMS artefact-signer arms" [
        IArtefactSignerContract.tests "Azure Key Vault (ES256, P1363 stand-in)" (fixtureFor "azure" azureShaper)
        IArtefactSignerContract.tests "GCP Cloud KMS (ES256, DER→P1363 stand-in)" (fixtureFor "gcp" gcpShaper)
    ]
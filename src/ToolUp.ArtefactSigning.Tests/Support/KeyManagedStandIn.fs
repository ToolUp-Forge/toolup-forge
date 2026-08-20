// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.Support.KeyManagedStandIn

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open ToolUp.Platform.Secrets
open ToolUp.ArtefactSigning

// ─── Offline stand-in for a key-management-backed signer ───────────────
//
// A key-management service has no offline emulator and its crypto client
// is not practically mockable, so the shipped cloud-signing arms are
// certified against a stand-in that reproduces their Sign path with a
// local key: build the JWS protected header, hash the signing input,
// hand the DIGEST to the key holder, receive a signature back, and shape
// it into the JWS form. The stand-in differs from a real key-managed
// signer in exactly one respect — WHERE the digest is signed — so it
// exercises the byte shape and the seam end to end.
//
// The signature is returned in ASN.1 DER and converted through
// `JwsBuilder.derEcdsaToP1363`, which is the shape the DER-returning
// services use. The pre-existing byte-level pack covers the alternative
// (already-P1363) shape.

let private pemWrap (label: string) (der: byte[]) : string =
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

type private StandInSigner(ec: ECDsa, keyId: string) =

    interface IArtefactSigner with
        member _.KeyId() = keyId

        member _.Sign(artefact: byte[]) : Async<Result<ArtefactSignature, SigningError>> = async {
            try
                let encodedHeader = JwsBuilder.protectedHeaderEncoded EcdsaP256 keyId
                let input = JwsBuilder.signingInput encodedHeader artefact
                // The digest leaves the process; the key never enters it.
                let digest = SHA256.HashData input
                let der = ec.SignHash(digest, DSASignatureFormat.Rfc3279DerSequence)

                match JwsBuilder.derEcdsaToP1363 der 32 with
                | Error e -> return Error(CryptoFailure e)
                | Ok raw ->
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

/// Mint a stand-in key-managed signer for `keyId` and publish its PUBLIC
/// component where the shipped verifier resolves keys. Only the public
/// half is needed to verify; the private half stays inside the stand-in,
/// which is the property the level `IsolatedSigner` names.
let create (secrets: ISecretStore) (keyId: string) : IArtefactSigner =
    let ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    let pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo())
    let created = DateTimeOffset.UtcNow.ToString "O"

    // The verifier reads only the public component from this blob. The
    // private slot is filled with the public bytes rather than the real
    // private key, so a stand-in that accidentally signed locally through
    // the in-process path would fail rather than quietly pass.
    let json =
        $"""{{"alg":"EcdsaP256","private":"{pub}","public":"{pub}","createdAt":"{created}"}}"""

    secrets.SetSecret("_platform", $"signing/{keyId}", json)
    |> Async.RunSynchronously
    |> ignore

    StandInSigner(ec, keyId) :> IArtefactSigner
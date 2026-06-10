// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Org.BouncyCastle.Crypto.Parameters
open Org.BouncyCastle.Crypto.Signers
open Org.BouncyCastle.Crypto.Generators
open Org.BouncyCastle.Security
open ToolUp.Platform.Secrets

// ─── Phase 40 — signing internals ──────────────────────────────────────
//
// base64url helpers, the detached-JWS encode/decode shape, the in-process
// ECDSA P-256 / Ed25519 crypto primitives, and the `ISecretStore`-backed
// key-material persistence used by the default signer + verifier + the
// signing-key endpoint. Nothing here is part of the public companion
// surface beyond what the signer/verifier re-expose.

/// base64url (RFC 4648 §5, no padding) — the JWS segment encoding.
module internal Base64Url =
    let encode (bytes: byte[]) : string =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let decode (s: string) : byte[] =
        let padded =
            match s.Length % 4 with
            | 2 -> s + "=="
            | 3 -> s + "="
            | _ -> s

        Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'))

/// The persisted shape of a signing key in `ISecretStore`. Stored as
/// JSON under scope `_platform`, key `signing/{keyId}`. Carries both the
/// private component (for signing) and the public component (so the
/// verifier + endpoint can resolve a public key without re-deriving it
/// from the private key, and so a rotated-out key remains verifiable as
/// long as its blob survives).
type internal StoredSigningKey = {
    Algorithm: SigningAlgorithm
    /// ECDSA: PKCS#8 DER. Ed25519: raw 32-byte seed.
    PrivateKey: byte[]
    /// ECDSA: SubjectPublicKeyInfo DER. Ed25519: raw 32-byte public key.
    PublicKey: byte[]
    CreatedAt: DateTimeOffset
}

module internal SigningKeyMaterial =

    /// Reserved `ISecretStore` scope for platform signing keys.
    [<Literal>]
    let SecretScope = "_platform"

    let secretKey (keyId: string) = $"signing/{keyId}"

    // ── JSON (de)serialisation via System.Text.Json.Nodes ──────────────

    let private toJson (k: StoredSigningKey) : string =
        let o = JsonObject()
        o["alg"] <- JsonValue.Create(SigningAlgorithm.name k.Algorithm)
        o["private"] <- JsonValue.Create(Convert.ToBase64String k.PrivateKey)
        o["public"] <- JsonValue.Create(Convert.ToBase64String k.PublicKey)
        o["createdAt"] <- JsonValue.Create(k.CreatedAt.ToString("O"))
        o.ToJsonString()

    let private fromJson (json: string) : StoredSigningKey option =
        try
            let node = JsonNode.Parse(json)
            let alg = node["alg"].GetValue<string>() |> SigningAlgorithm.tryParse

            match alg with
            | None -> None
            | Some a ->
                Some {
                    Algorithm = a
                    PrivateKey = node["private"].GetValue<string>() |> Convert.FromBase64String
                    PublicKey = node["public"].GetValue<string>() |> Convert.FromBase64String
                    CreatedAt = DateTimeOffset.Parse(node["createdAt"].GetValue<string>())
                }
        with _ ->
            None

    // ── key generation ─────────────────────────────────────────────────

    /// SPKI DER prefix for an Ed25519 public key (RFC 8410): the
    /// AlgorithmIdentifier (id-Ed25519 = 1.3.101.112) + BIT STRING
    /// header, immediately followed by the 32 raw public-key bytes.
    let private ed25519SpkiPrefix = [|
        0x30uy
        0x2auy
        0x30uy
        0x05uy
        0x06uy
        0x03uy
        0x2buy
        0x65uy
        0x70uy
        0x03uy
        0x21uy
        0x00uy
    |]

    let generate (algorithm: SigningAlgorithm) : StoredSigningKey =
        match algorithm with
        | EcdsaP256 ->
            use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)

            {
                Algorithm = EcdsaP256
                PrivateKey = ec.ExportPkcs8PrivateKey()
                PublicKey = ec.ExportSubjectPublicKeyInfo()
                CreatedAt = DateTimeOffset.UtcNow
            }
        | Ed25519 ->
            let gen = Ed25519KeyPairGenerator()
            gen.Init(Ed25519KeyGenerationParameters(SecureRandom()))
            let pair = gen.GenerateKeyPair()
            let priv = pair.Private :?> Ed25519PrivateKeyParameters
            let pub = pair.Public :?> Ed25519PublicKeyParameters

            {
                Algorithm = Ed25519
                PrivateKey = priv.GetEncoded()
                PublicKey = pub.GetEncoded()
                CreatedAt = DateTimeOffset.UtcNow
            }

    // ── persistence (load-or-create) ───────────────────────────────────

    /// Load the stored key for `keyId`, or `None` if absent / undecodable.
    let tryLoad (secrets: ISecretStore) (keyId: string) : Async<StoredSigningKey option> = async {
        let! raw = secrets.GetSecret(SecretScope, secretKey keyId)
        return raw |> Option.bind fromJson
    }

    /// Persist a key under `keyId`. Returns the write result so callers
    /// can decide whether auto-provisioning succeeded.
    let save (secrets: ISecretStore) (keyId: string) (key: StoredSigningKey) : Async<Result<unit, string>> =
        secrets.SetSecret(SecretScope, secretKey keyId, toJson key)

    /// Load the key for `keyId`, generating + persisting a fresh
    /// `algorithm` key pair on first use. Returns `Error` only when the
    /// key is absent AND the store rejected the write (read-only store).
    let loadOrCreate
        (secrets: ISecretStore)
        (keyId: string)
        (algorithm: SigningAlgorithm)
        : Async<Result<StoredSigningKey, string>> =
        async {
            match! tryLoad secrets keyId with
            | Some existing -> return Ok existing
            | None ->
                let fresh = generate algorithm

                match! save secrets keyId fresh with
                | Ok() -> return Ok fresh
                | Error e -> return Error e
        }

    // ── public-key export (PEM + JWK) ──────────────────────────────────

    let private pemWrap (label: string) (der: byte[]) : string =
        let b64 = Convert.ToBase64String(der)
        let sb = StringBuilder()
        sb.Append("-----BEGIN ").Append(label).Append("-----\n") |> ignore

        let mutable i = 0

        while i < b64.Length do
            let len = min 64 (b64.Length - i)
            sb.Append(b64.Substring(i, len)).Append('\n') |> ignore
            i <- i + 64

        sb.Append("-----END ").Append(label).Append("-----\n") |> ignore
        sb.ToString()

    /// SubjectPublicKeyInfo PEM for the stored key's public component.
    let publicKeyPem (key: StoredSigningKey) : string =
        match key.Algorithm with
        | EcdsaP256 ->
            // key.PublicKey is already SPKI DER.
            pemWrap "PUBLIC KEY" key.PublicKey
        | Ed25519 ->
            // Wrap the raw 32-byte public key in an Ed25519 SPKI.
            let der = Array.append ed25519SpkiPrefix key.PublicKey
            pemWrap "PUBLIC KEY" der

    /// Compact JWK (RFC 7517) for the stored key's public component.
    let publicKeyJwk (keyId: string) (key: StoredSigningKey) : string =
        let o = JsonObject()

        match key.Algorithm with
        | EcdsaP256 ->
            use ec = ECDsa.Create()
            ec.ImportSubjectPublicKeyInfo(ReadOnlySpan<byte>(key.PublicKey)) |> ignore
            let p = ec.ExportParameters(false)
            o["kty"] <- JsonValue.Create("EC")
            o["crv"] <- JsonValue.Create("P-256")
            o["x"] <- JsonValue.Create(Base64Url.encode p.Q.X)
            o["y"] <- JsonValue.Create(Base64Url.encode p.Q.Y)
        | Ed25519 ->
            o["kty"] <- JsonValue.Create("OKP")
            o["crv"] <- JsonValue.Create("Ed25519")
            o["x"] <- JsonValue.Create(Base64Url.encode key.PublicKey)

        o["alg"] <- JsonValue.Create(SigningAlgorithm.jwsAlg key.Algorithm)
        o["use"] <- JsonValue.Create("sig")
        o["kid"] <- JsonValue.Create(keyId)
        o.ToJsonString()

    let metadata (keyId: string) (key: StoredSigningKey) : PublicKeyMetadata = {
        KeyId = keyId
        Algorithm = key.Algorithm
        Pem = publicKeyPem key
        Jwk = publicKeyJwk keyId key
    }

/// Detached-JWS encode/decode + the raw sign/verify primitives over a
/// signing input. Kept separate from key persistence so the crypto is
/// unit-testable in isolation.
module internal Jws =

    let private utf8 (s: string) = Encoding.UTF8.GetBytes s

    /// Protected-header JSON for the algorithm + key id.
    let private headerJson (algorithm: SigningAlgorithm) (keyId: string) : string =
        let o = JsonObject()
        o["alg"] <- JsonValue.Create(SigningAlgorithm.jwsAlg algorithm)
        o["kid"] <- JsonValue.Create(keyId)
        o["typ"] <- JsonValue.Create("JOSE")
        o.ToJsonString()

    /// `base64url(header) + "." + base64url(artefact)` — the bytes the
    /// signature is computed over.
    let private signingInput (encodedHeader: string) (artefact: byte[]) : byte[] =
        utf8 (encodedHeader + "." + Base64Url.encode artefact)

    // ── raw crypto ─────────────────────────────────────────────────────

    let private signEcdsa (pkcs8: byte[]) (input: byte[]) : byte[] =
        use ec = ECDsa.Create()
        ec.ImportPkcs8PrivateKey(ReadOnlySpan<byte>(pkcs8)) |> ignore
        // IEEE P1363 = raw R‖S (64 bytes), the JWS ES256 signature shape.
        ec.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

    let private verifyEcdsa (spki: byte[]) (input: byte[]) (signature: byte[]) : bool =
        use ec = ECDsa.Create()
        ec.ImportSubjectPublicKeyInfo(ReadOnlySpan<byte>(spki)) |> ignore
        ec.VerifyData(input, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

    let private signEd25519 (rawPriv: byte[]) (input: byte[]) : byte[] =
        let priv = Ed25519PrivateKeyParameters(rawPriv, 0)
        let signer = Ed25519Signer()
        signer.Init(true, priv)
        signer.BlockUpdate(input, 0, input.Length)
        signer.GenerateSignature()

    let private verifyEd25519 (rawPub: byte[]) (input: byte[]) (signature: byte[]) : bool =
        let pub = Ed25519PublicKeyParameters(rawPub, 0)
        let verifier = Ed25519Signer()
        verifier.Init(false, pub)
        verifier.BlockUpdate(input, 0, input.Length)
        verifier.VerifySignature(signature)

    // ── public encode/decode ───────────────────────────────────────────

    /// Produce a detached JWS over `artefact` using the stored key.
    let sign (key: StoredSigningKey) (keyId: string) (artefact: byte[]) : Result<string, SigningError> =
        try
            let encodedHeader = Base64Url.encode (utf8 (headerJson key.Algorithm keyId))
            let input = signingInput encodedHeader artefact

            let signature =
                match key.Algorithm with
                | EcdsaP256 -> signEcdsa key.PrivateKey input
                | Ed25519 -> signEd25519 key.PrivateKey input

            // Detached JWS: empty payload segment.
            Ok(encodedHeader + ".." + Base64Url.encode signature)
        with ex ->
            Error(CryptoFailure ex.Message)

    /// Parsed view of a detached JWS.
    type DecodedJws = {
        EncodedHeader: string
        Algorithm: SigningAlgorithm
        Signature: byte[]
    }

    /// Parse + structurally validate a detached JWS string.
    let decode (detachedJws: string) : Result<DecodedJws, VerificationError> =
        match detachedJws.Split('.') with
        | [| encodedHeader; payload; encodedSig |] when payload = "" ->
            try
                let headerJson = Encoding.UTF8.GetString(Base64Url.decode encodedHeader)
                let node = JsonNode.Parse(headerJson)
                let alg = node["alg"].GetValue<string>()

                match SigningAlgorithm.tryParse alg with
                | None -> Error(VerificationError.UnsupportedAlgorithm alg)
                | Some a ->
                    Ok {
                        EncodedHeader = encodedHeader
                        Algorithm = a
                        Signature = Base64Url.decode encodedSig
                    }
            with ex ->
                Error(MalformedSignature ex.Message)
        | _ -> Error(MalformedSignature "expected detached JWS with empty payload segment")

    /// Verify a decoded JWS against `artefact` using the resolved public
    /// key. `publicKey` is SPKI DER for ECDSA, raw 32 bytes for Ed25519.
    let verify (decoded: DecodedJws) (publicKey: byte[]) (artefact: byte[]) : Result<unit, VerificationError> =
        try
            let input = signingInput decoded.EncodedHeader artefact

            let ok =
                match decoded.Algorithm with
                | EcdsaP256 -> verifyEcdsa publicKey input decoded.Signature
                | Ed25519 -> verifyEd25519 publicKey input decoded.Signature

            if ok then Ok() else Error Tampered
        with ex ->
            Error(MalformedSignature ex.Message)
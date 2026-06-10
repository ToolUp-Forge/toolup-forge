// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System

// ─── Phase 40 — artefact-signing substrate, shared types ───────────────
//
// General-purpose cryptographic signing of arbitrary deployment
// artefacts (audit packs, exported reports, model documentation) using
// per-deployment ECDSA P-256 / Ed25519 signing keys. The signature is a
// detached JWS — the artefact bytes are not embedded in the signature,
// so the JWS can travel as a sidecar `.sig` file or a JSON envelope
// field while the artefact itself stays untouched.
//
// **Distinct from Phase 30a's `IArtifactSigner`** (note spelling:
// "Artefact" here vs "Artifact" there). Phase 30a signs *module
// distribution artefacts* against an `ArtifactManifest` for the
// marketplace publish/install trust path, with raw Ed25519 over a
// canonical manifest. Phase 40 signs *arbitrary byte payloads* for
// compliance non-repudiation, with a public verification-key endpoint
// and a standards-shaped detached JWS. Different concern, different
// namespace (`ToolUp.ArtefactSigning`), no type collision.

/// Supported signing algorithms for the in-process default signer.
/// Both map onto a JWS `alg` header value:
///   * `EcdsaP256` → `"ES256"` (ECDSA over the NIST P-256 curve with
///     SHA-256; signature is raw R‖S per IEEE P1363, the JWS shape).
///   * `Ed25519`   → `"EdDSA"` (Edwards-curve 25519; deterministic).
type SigningAlgorithm =
    | EcdsaP256
    | Ed25519

module SigningAlgorithm =
    /// JWS `alg` header value for the algorithm.
    let jwsAlg =
        function
        | EcdsaP256 -> "ES256"
        | Ed25519 -> "EdDSA"

    /// Stable case-name string for audit payloads + config persistence.
    /// Do not rename without a compatibility note — persisted signing
    /// keys store this value alongside the key material.
    let name =
        function
        | EcdsaP256 -> "EcdsaP256"
        | Ed25519 -> "Ed25519"

    /// Parse the persisted/case-name string back to the DU. Returns
    /// `None` for an unrecognised value (forward-compat: a key persisted
    /// by a newer SDK with an algorithm this build doesn't know).
    let tryParse (s: string) : SigningAlgorithm option =
        match s with
        | "EcdsaP256"
        | "ES256" -> Some EcdsaP256
        | "Ed25519"
        | "EdDSA" -> Some Ed25519
        | _ -> None

/// A detached-JWS signature over an artefact's bytes. The artefact
/// itself is NOT carried here — verification requires the original
/// bytes plus this record. `DetachedJws` is the compact JWS with an
/// empty payload segment (`base64url(header)..base64url(signature)`),
/// so a verifying party can validate independently given the artefact.
type ArtefactSignature = {
    /// Per-deployment signing-key identifier. The public verification
    /// key for this id is fetchable from the signing-key endpoint even
    /// after the key has been rotated out of active signing use.
    KeyId: string
    /// Algorithm the signature was produced with.
    Algorithm: SigningAlgorithm
    /// Wall-clock at signing time.
    SignedAt: DateTimeOffset
    /// Detached JWS: `base64url(protectedHeader) + ".." +
    /// base64url(signature)`. The middle (payload) segment is empty —
    /// the artefact bytes are signed but not embedded.
    DetachedJws: string
}

/// Failure modes for `IArtefactSigner.Sign`.
type SigningError =
    /// The signing key material could not be loaded and could not be
    /// auto-provisioned (e.g. a read-only `ISecretStore`).
    | KeyUnavailable of reason: string
    /// The configured algorithm is not supported by this signer build
    /// (forward-compat guard for a key persisted under an unknown alg).
    | UnsupportedAlgorithm of alg: string
    /// The underlying crypto operation threw.
    | CryptoFailure of reason: string

module SigningError =
    let describe =
        function
        | KeyUnavailable r -> $"signing key unavailable: {r}"
        | UnsupportedAlgorithm a -> $"unsupported signing algorithm: {a}"
        | CryptoFailure r -> $"crypto failure: {r}"

/// Failure modes for `IArtefactVerifier.Verify`.
type VerificationError =
    /// The artefact bytes (or manifest fields the signature covers) were
    /// modified after signing — the signature does not validate.
    | Tampered
    /// No public verification key is discoverable for the signature's
    /// `KeyId` (never issued, or the key's secret was destroyed).
    | UnknownKey of keyId: string
    /// The `DetachedJws` could not be parsed into header + signature.
    | MalformedSignature of reason: string
    /// The algorithm declared in the JWS header is not supported.
    | UnsupportedAlgorithm of alg: string

module VerificationError =
    let describe =
        function
        | Tampered -> "artefact does not match signature (tampered)"
        | UnknownKey k -> $"no verification key for key id: {k}"
        | MalformedSignature r -> $"malformed signature: {r}"
        | UnsupportedAlgorithm a -> $"unsupported signature algorithm: {a}"

/// Public verification-key metadata returned by the signing-key
/// endpoint and `IArtefactSigner.VerifyKey`. Carries the public key in
/// two interchange formats so verifying parties can pick whichever their
/// tooling consumes.
type PublicKeyMetadata = {
    KeyId: string
    Algorithm: SigningAlgorithm
    /// SubjectPublicKeyInfo PEM (`-----BEGIN PUBLIC KEY-----`).
    Pem: string
    /// Compact JSON Web Key (RFC 7517) — EC for `EcdsaP256`, OKP for
    /// `Ed25519`. Includes `kid` + `alg`.
    Jwk: string
}
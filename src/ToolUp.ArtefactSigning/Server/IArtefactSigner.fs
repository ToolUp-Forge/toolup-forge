// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System

/// Phase 40 — cryptographic signer for arbitrary deployment artefacts.
/// Produces a detached-JWS `ArtefactSignature` over raw bytes using a
/// per-deployment signing key, with the public verification key exposed
/// for independent validation.
///
/// **Default implementation** — `DefaultArtefactSigner` (BCL crypto for
/// ECDSA P-256 via `System.Security.Cryptography`; BouncyCastle for
/// Ed25519, mirroring the Phase 30a artefact substrate's crypto choice).
/// Private key material is read from `ISecretStore` per call (never
/// cached in the signer), so key rotation through the store takes effect
/// immediately and the signer holds no long-lived secret. KMS / HSM
/// deployments wire an alternative implementation (Phase 22a signing
/// flavour) whose `Sign` makes a KMS API call and never sees the private
/// key bytes.
///
/// **Six portability rules (GP 12).**
/// 1. *Identity by value* — `KeyId` is a string; artefacts are `byte[]`;
///    no live handles cross the boundary.
/// 2. *Async at every boundary* — `Sign` / `VerifyKey` return `Async<_>`.
/// 3. *Retry as data* — failures surface as `Result.Error SigningError`,
///    not exceptions or callbacks. Local crypto has no built-in retry;
///    KMS-backed implementations carry their own `RetryPolicy`.
/// 4. *Stateless between invocations* — each `Sign` re-reads key
///    material from `ISecretStore`; no in-memory state survives a call.
/// 5. *No cross-shard ordering* — signing is a pure function of inputs.
/// 6. *Precision at the lower bound* — no timing/clock precision
///    boundary (signatures are deterministic for Ed25519; ECDSA uses a
///    per-call nonce but verification is order-independent).
type IArtefactSigner =
    /// Sign `artefact` with this signer's active key. Returns a detached
    /// `ArtefactSignature`; the artefact bytes are not embedded.
    abstract Sign: artefact: byte[] -> Async<Result<ArtefactSignature, SigningError>>

    /// Public verification key for this signer's active key id — what
    /// the `/_platform/signing-key/{keyId}` endpoint serves.
    abstract VerifyKey: unit -> Async<PublicKeyMetadata>

    /// The active signing-key id. Recorded on every `ArtefactSigned`
    /// audit emission and stamped into the `ArtefactSignature`.
    abstract KeyId: unit -> string
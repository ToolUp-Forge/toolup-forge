// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

/// Phase 30a — hub-side signer for module artefacts. Produces a
/// `SignedArtifact` from an `ArtifactManifest` + payload bytes; the
/// signature is the Ed25519 output over `canonical(Manifest) || Payload`
/// using the publisher's private key (resolved via the implementation's
/// own key material, NOT via `IPublisherKeyStore` — that store carries
/// only public keys at the edge).
///
/// **Default implementation** — `Ed25519ArtifactSigner` (BCL crypto,
/// `System.Security.Cryptography`, no paid deps per GP 2). Holds the
/// private key in process memory; production hub deployments may wire
/// an alternative implementation that pulls the private key from KMS /
/// HSM on each call.
///
/// **Six portability rules (GP 12).**
/// 1. *Identity by value* — `PublisherKeyId` is a string newtype;
///    `ModuleId` / `Version` are strings; no live handles cross the
///    boundary.
/// 2. *Async at every boundary* — `Sign` returns `Async<SignedArtifact>`.
/// 3. *Retry as data* — implementation failures throw; callers wrap as
///    they see fit. `IArtifactSigner` has no built-in retry policy
///    because signing is local crypto, not a network round-trip.
/// 4. *Stateless handlers between invocations* — each `Sign` call
///    derives its result from arguments + private key material;
///    no in-memory state survives between calls.
/// 5. *No cross-shard ordering* — signing is a pure function of inputs;
///    ordering is irrelevant.
/// 6. *Precision at the lower bound* — Ed25519 is deterministic; no
///    timing / clock precision boundary.
///
/// **Audit contract.** Implementations call `IAuditLog.Record` with
/// `AuditEvent.ModuleArtefactSigned` after a successful sign. The payload
/// carries the publisher key id (NEVER the private key bytes).
type IArtifactSigner =
    /// Sign `payload` against `manifest`. Returns a `SignedArtifact`
    /// whose `Signature` is the 64-byte Ed25519 output over
    /// `canonical(manifest) || payload`. Throws if the signer's private
    /// key material is unavailable.
    abstract Sign: manifest: ArtifactManifest * payload: byte[] -> Async<SignedArtifact>

    /// The publisher key id this signer is configured to sign as.
    /// Recorded on every `ModuleArtefactSigned` audit emission.
    abstract PublisherKeyId: PublisherKeyId
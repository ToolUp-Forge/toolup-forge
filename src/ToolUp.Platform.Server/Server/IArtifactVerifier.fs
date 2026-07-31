// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

/// Phase 30a — edge-side verifier for module artefacts. Resolves the
/// signed artefact's `PublisherKeyId` against `IPublisherKeyStore`,
/// re-canonicalises the manifest, and Ed25519-verifies the signature
/// against `canonical(Manifest) || Payload`.
///
/// **Result discipline.** `Verify` returns `ArtifactValidation.Ok` only
/// when (a) the publisher key is present in `IPublisherKeyStore` AND
/// (b) the Ed25519 signature validates. Any other outcome returns
/// `ArtifactValidation.Error reason` — never throws on a verification
/// failure (a throw would conflate transport-level bugs with refusal
/// signal).
///
/// **Default implementation** — `Ed25519ArtifactVerifier` (BCL crypto,
/// no paid deps per GP 2). Edge instances install it via
/// `ServerApp.withArtifactVerifier` (or accept the default registered
/// in `SDK.Server.fs`).
///
/// **Six portability rules (GP 12).**
/// 1. *Identity by value* — `PublisherKeyId` / `ModuleId` are strings.
/// 2. *Async at every boundary* — `Verify` returns
///    `Async<ArtifactValidation>`.
/// 3. *Retry as data* — refusals surface as `ArtifactValidation.Error
///    reason`; callers decide whether to retry, escalate, or audit.
/// 4. *Stateless handlers between invocations* — each `Verify` call
///    re-resolves the publisher key from `IPublisherKeyStore`; no
///    in-memory cache survives between calls.
/// 5. *No cross-shard ordering* — verification is a pure function of
///    inputs + the publisher-key-store state at call time.
/// 6. *Precision at the lower bound* — Ed25519 is deterministic; no
///    clock precision boundary.
///
/// **Audit contract.** Implementations call `IAuditLog.Record` with
/// `AuditEvent.ModuleArtefactVerified` on `Ok` and `AuditEvent.ModuleArtefactRejected`
/// on `Error`. The rejection payload includes the operator-readable
/// reason verbatim.
type IArtifactVerifier =
    /// Verify `artefact` against the trusted-publisher set. Returns
    /// `ArtifactValidation.Ok` when the signature validates and the
    /// publisher key is trusted at the edge; returns
    /// `ArtifactValidation.Error reason` for every refusal path
    /// (`"untrusted publisher"`, `"signature mismatch"`,
    /// `"manifest hash mismatch"`, etc.).
    abstract Verify: artefact: SignedArtifact -> Async<ArtifactValidation>
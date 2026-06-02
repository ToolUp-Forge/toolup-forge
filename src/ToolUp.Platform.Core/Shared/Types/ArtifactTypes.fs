// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Signed module artefacts ─────────────────────────────────────
//
// Phase 30a — portable signed-artefact format for ToolUp module
// packages. A hub signs a module package (manifest + payload bytes);
// an edge instance verifies the signature against a trusted publisher
// key before installation.
//
// Ed25519 over `canonical-manifest-bytes || payload-bytes`. BCL crypto
// (System.Security.Cryptography), no paid dependencies (GP 2).
//
// The wire shape lives here so it is shared across the Server-tier
// signer / verifier implementations and the contract test packs without
// pinning either side to the other's implementation.

/// Inclusive lower-bound, exclusive upper-bound SDK version range the
/// artefact is compatible with. Both versions are SemVer strings —
/// identity-by-value (GP 12 rule 1), no live handles.
type SdkVersionRange = {
    /// Inclusive lower bound, e.g. `"0.4.0"`.
    MinInclusive: string
    /// Exclusive upper bound, e.g. `"0.5.0"`.
    MaxExclusive: string
}

/// SHA-256 hex digest of an artefact's bytes. Embedded in the signed
/// manifest so verification re-computes against the wire payload and
/// rejects on mismatch — see `Ed25519ArtifactVerifier.Verify`.
type ContentHash = ContentHash of hex: string

module ContentHash =
    /// Project the hex string.
    let value (ContentHash hex) = hex

/// Required-companion dependency in an artefact manifest. `PackageId`
/// is a `ToolUp.*` nupkg identifier; the version range uses the same
/// shape as the SDK range.
type ArtifactDependency = {
    PackageId: string
    VersionRange: SdkVersionRange
}

/// Identifier for a publisher's Ed25519 signing key. Public-key bytes
/// live in `IPublisherKeyStore` at the edge under
/// `_platform/trusted-publishers/{keyId}.pub`; manifests carry only the
/// key id so the wire payload stays key-material-free.
type PublisherKeyId = PublisherKeyId of id: string

module PublisherKeyId =
    /// Project the id string.
    let value (PublisherKeyId id) = id

/// Signed-artefact manifest. The signature covers the canonical bytes
/// of the manifest (Signature field zeroed) plus the payload bytes —
/// `Ed25519ArtifactVerifier.Verify` re-canonicalises the manifest, then
/// recomputes Ed25519 over `canonical(manifest) || payload` and refuses
/// on mismatch.
type ArtifactManifest = {
    /// Module identifier (matches the runtime `ModuleId` the artefact
    /// installs as).
    ModuleId: string
    /// SemVer version of the artefact.
    Version: string
    /// SDK versions this artefact is compatible with.
    SdkVersionRange: SdkVersionRange
    /// SHA-256 hex of the artefact's code bytes (DLL / source bundle /
    /// wasm). Distinct from `SchemaHash` so a schema-only diff between
    /// two artefact versions is observable in the audit trail without
    /// re-reading the code blob.
    CodeHash: ContentHash
    /// SHA-256 hex of the artefact's declared schemas (DataType /
    /// EntityType / ConfigSchema declarations).
    SchemaHash: ContentHash
    /// Required companion packages — the hub declares these so the
    /// edge instance can pre-flight that its SDK composition can host
    /// the module.
    Dependencies: ArtifactDependency list
    /// Identifier for the publisher's signing key. The verifier resolves
    /// the matching public key via `IPublisherKeyStore`; the manifest
    /// itself never carries key material.
    PublisherKeyId: PublisherKeyId
}

/// A manifest + Ed25519 signature + the artefact's raw payload bytes.
/// The signature is the 64-byte Ed25519 output over
/// `canonical(Manifest) || Payload`; see Ed25519 implementations for
/// the canonicalisation contract.
type SignedArtifact = {
    /// The artefact manifest.
    Manifest: ArtifactManifest
    /// Raw artefact payload bytes (the DLL / source / wasm bundle).
    Payload: byte[]
    /// Ed25519 signature over `canonical(Manifest) || Payload`. Always
    /// 64 bytes (Ed25519 fixed signature size); verifiers refuse other
    /// lengths.
    Signature: byte[]
}

/// Result of verifying a signed artefact. `Ok` means the signature
/// validates AND the publisher key is trusted at the edge.
///
/// `RequireQualifiedAccess` — `Ok` / `Error` would otherwise shadow F#'s
/// built-in `Result` constructors at call sites.
[<RequireQualifiedAccess>]
type ArtifactValidation =
    /// Signature valid, publisher key trusted.
    | Ok
    /// Verification refused. `reason` is operator-readable:
    /// `"untrusted publisher"` (publisher key not in
    /// `IPublisherKeyStore`), `"signature mismatch"` (Ed25519 verify
    /// returned false), `"manifest hash mismatch"` (payload re-hash
    /// disagrees with `CodeHash`), or a sink-specific message.
    | Error of reason: string

/// Reserved `SourceModule` for artefact-signing audit events. Filter
/// `IEventStore.ReadBySource` on this constant for the artefact
/// signing / verification audit trail.
module ArtifactsSourceModule =
    [<Literal>]
    let value = "_platform.artefacts"
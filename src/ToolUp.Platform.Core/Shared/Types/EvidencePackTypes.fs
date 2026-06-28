// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 187 — compliance evidence-pack substrate, shared types ──────
//
// The tamper-evident audit-pack a model-risk-management / compliance team
// actually consumes. Phases 40 (artefact signing) / 41 (field
// classification) / 9 (audit trail) / 9h.A (DSR pipeline) each shipped a
// *part*; nothing composed them into the artefact a reviewer reads. The
// generator (server tier) is pure orchestration over those seams — it adds
// no new infrastructure. These Core types are the request / manifest /
// result shapes that cross the seam (so the `IEvidencePackGenerator`
// interface + its `NoEvidencePackGenerator` default live in Core, the
// `DefaultEvidencePackGenerator` implementation in `ToolUp.Platform.Server`).
//
// The signature is carried as a Core `ExportSignature` (the same neutral
// detached-JWS wire type Phase 162 introduced), NOT a
// `ToolUp.ArtefactSigning.ArtefactSignature` — the SDK core stays
// signer-free (GP 1); the generator signs through the neutral
// `IExportEnvelopeSigner` seam.

/// What an evidence pack should assemble. The generator slices the audit
/// trail by `AuditSourceModules`, attaches a classification sidecar for
/// `EntityNames`, and — when `SubjectUserId` is `Some` — includes the DSR
/// export segments for that subject.
type EvidencePackRequest = {
    /// Scope (team / `_platform`) the pack is assembled for. Every audit
    /// slice + DSR read is scoped to this id (GP 4 isolation).
    ScopeId: string
    /// Audit `SourceModule`s to slice into the pack (e.g.
    /// `[ "_platform.audit"; "_platform.classification" ]`). Empty ⇒ no
    /// audit segment.
    AuditSourceModules: string list
    /// Entity-type names whose `FieldClassification` tags become the
    /// pack's classification sidecar. Empty ⇒ no sidecar.
    EntityNames: string list
    /// When `Some`, the pack includes the DSR (Article-15-shape) export
    /// segments for this subject, gathered from the registered
    /// `IDataExporter`s. `None` ⇒ no DSR segment.
    SubjectUserId: string option
    /// Admin actor who requested the pack. Audit attribution; stamped into
    /// the manifest.
    RequestedBy: string
    /// Free-text rationale (ticket id, regulator inquiry reference, …).
    /// Stamped into the manifest.
    Reason: string
}

/// One classification tag in the pack's sidecar — the
/// `FieldClassification` projected to its value-free wire shape (entity +
/// field path + level name).
type EvidencePackClassification = {
    EntityName: string
    FieldPath: string
    /// `ClassificationLevel.name` of the field.
    Level: string
}

/// One content-addressed entry in the signed manifest. Names a segment in
/// the pack and pins its SHA-256 + size, so a verifier can confirm the
/// segment bytes match the signed manifest without re-signing.
type EvidencePackEntry = {
    /// Path of the segment inside the pack (e.g.
    /// `audit/_platform.audit.json`). The manifest sorts on this for
    /// deterministic bytes.
    Name: string
    MimeType: string
    /// Lower-case hex SHA-256 of the segment bytes.
    Sha256: string
    SizeBytes: int
}

/// The signed manifest — the load-bearing artefact. Deterministic for a
/// fixed request + fixed clock + fixed underlying data (entries +
/// classifications are sorted), so the same inputs sign to the same bytes.
type EvidencePackManifest = {
    ScopeId: string
    /// Assembly time — injected via the generator's clock so the manifest
    /// is reproducible under test and across a retry.
    GeneratedAt: DateTimeOffset
    RequestedBy: string
    Reason: string
    /// Content-addressed entries for every segment, sorted by `Name`.
    Entries: EvidencePackEntry list
    /// Classification sidecar, sorted by `(EntityName, FieldPath)`.
    Classifications: EvidencePackClassification list
}

/// The assembled pack. `ManifestBytes` are the exact canonical JSON bytes
/// the signature (when present) covers; `Segments` are the pack contents
/// the manifest entries pin.
type EvidencePackResult = {
    Manifest: EvidencePackManifest
    /// Canonical JSON of `Manifest` — the exact bytes signed. A verifier
    /// re-checks `Signature` against these.
    ManifestBytes: byte[]
    /// The pack's content segments (audit slices, classification sidecar,
    /// DSR export). Each is pinned by an `EvidencePackEntry`.
    Segments: ExportSegment list
    /// Detached-JWS signature over `ManifestBytes`, or `None` when no
    /// `IExportEnvelopeSigner` was composed (unsigned pack — still a valid
    /// bundle, just not tamper-evident).
    Signature: ExportSignature option
}

/// Failure modes for `IEvidencePackGenerator.Generate`.
type EvidencePackError =
    /// The generator is the `NoEvidencePackGenerator` default — no
    /// generator was composed, so a pack cannot be produced.
    | GeneratorDisabled
    /// A substrate read (audit slice, classification lookup, DSR export)
    /// failed while assembling the pack.
    | AssemblyFailed of reason: string
    /// The manifest could not be signed (the composed signer returned an
    /// error).
    | SigningFailed of reason: string

module EvidencePackError =
    let describe =
        function
        | GeneratorDisabled -> "evidence-pack generator not configured (NoEvidencePackGenerator)"
        | AssemblyFailed r -> $"evidence-pack assembly failed: {r}"
        | SigningFailed r -> $"evidence-pack signing failed: {r}"

/// Phase 187 — assembles a tamper-evident compliance evidence pack by
/// composing already-shipped substrate (audit trail + field classification
/// + DSR export + artefact signing). Pure orchestration; introduces no new
/// infrastructure.
///
/// **Six portability rules (GP 12).** Identity by value (the request /
/// manifest / result are immutable records of strings + `byte[]`); async
/// at the boundary; failure as `Result.Error EvidencePackError` (no
/// exceptions / callbacks across the seam); stateless between calls (every
/// substrate seam is re-read per `Generate`); no cross-shard ordering; no
/// timing primitive (the clock is injected into the implementation).
type IEvidencePackGenerator =
    /// Assemble the evidence pack described by `request`. The default
    /// `NoEvidencePackGenerator` returns `Error GeneratorDisabled`.
    abstract Generate: request: EvidencePackRequest -> Async<Result<EvidencePackResult, EvidencePackError>>

/// SDK default — registers nothing and produces no pack. A deployment that
/// never composes a real generator is byte-for-byte unchanged (GP 13): no
/// route, no service, no allocation.
type NoEvidencePackGenerator() =
    interface IEvidencePackGenerator with
        member _.Generate(_: EvidencePackRequest) = async { return Error GeneratorDisabled }
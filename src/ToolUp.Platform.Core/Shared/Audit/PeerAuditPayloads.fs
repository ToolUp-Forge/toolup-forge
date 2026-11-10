// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of AuditTypes.fs: the peer lane — inter-platform
// contract calls, peer jobs, clean-room decisions, multi-round federation,
// artefact signing and data-classification payload records.
// Declaration order is unchanged from the monolith; same namespace, same
// record names — only the file boundary moved. The `AuditEvent` union that
// registers these payloads stays in AuditTypes.fs, append-only.

// ─── Phase 483 — multi-round federation-run audit payloads ─────────────
//
// Emitted by `ToolUp.InterPlatform`'s `IRoundOrchestrator` as an
// iterative cross-party protocol (split-learning rounds, multi-round PSI,
// federated aggregation) advances. Reserved `SourceModule =
// "_platform.peer"`, the same family as `PeerCallCompleted`, because a
// round is a federation event and an operator reconstructs a run from the
// same trail as the calls it fanned out.
//
// The three cases carry the `Federation` qualifier at the F# surface even
// though the phase names them `RoundCompleted` / `ParticipantDropped` /
// `RunAborted`: `RoundEvent` in `ToolUp.InterPlatform` uses those bare
// names for the observer stream, and two DUs one `open` apart sharing
// case names is exactly how a call site silently binds the wrong one.
// The emitted `EventType` discriminators carry the qualifier too — these
// are new events, so there is no pinned legacy wire name to preserve.
//
// All three are PII-free: run / peer ids plus counts and a reason label,
// never a protocol payload (GP 1 — forge owns the round mechanics and
// never reads the content, so it could not audit it even if it wanted to).

// ─── Phase 40 — artefact-signing substrate audit payloads ──────────────
//
// Emitted by the `ToolUp.ArtefactSigning` companion's
// `DefaultArtefactSigner` for the general-purpose detached-JWS signing
// path. Reserved `SourceModule = "_platform.signing"`. Distinct from the
// Phase 30a `_platform.artefacts` family (`ModuleArtefactSigned` /
// `ModuleArtefactVerified` / `ModuleArtefactRejected`), which signs
// module-distribution artefacts against an `ArtifactManifest` — this
// family signs arbitrary byte payloads for compliance non-repudiation.
// Payloads carry the key id + a SHA-256 of the artefact, NEVER the
// artefact bytes or the private-key material.
//
// Phase 625 renamed the 30a family to carry the `Module` qualifier; it
// and this family were previously one letter apart
// (`ArtifactSigned` / `ArtefactSigned`). This family is UNCHANGED — it
// already used the house `artefact` spelling.

module SigningSourceModule =
    /// Reserved `SourceModule` for `ToolUp.ArtefactSigning` audit events.
    /// Filter `IEventStore.ReadBySource` on this constant for the
    /// artefact-signing audit trail.
    [<Literal>]
    let value = "_platform.signing"

// ─── Phase 41 — data-classification audit payloads ─────────────────────
//
// Emitted by `ClassificationGate` when a classified field is read
// (`AuditOnRead = true`) or written. Reserved `SourceModule =
// "_platform.classification"`. Payload carries entity + field-path +
// classification level + caller, NEVER the field value — the audit trail
// proves "this caller touched this classified field" without itself
// becoming a sink for the sensitive data it guards.

module ClassificationSourceModule =
    /// Reserved `SourceModule` for `IFieldClassifier` / `ClassificationGate`
    /// audit events.
    [<Literal>]
    let value = "_platform.classification"

/// A classified field was read by a caller (emitted when the field's
/// `AuditOnRead` is set). Value-free by design.
type ClassifiedFieldReadPayload = {
    /// Acting `AccessContext.UserId`.
    UserId: UserId
    /// Entity type the field belongs to.
    EntityName: string
    /// Dotted field path that was read.
    FieldPath: string
    /// `ClassificationLevel.name` of the field.
    Level: string
    /// `true` when the gate's policy redacted the value for this caller;
    /// `false` when the caller was allowed to read it. Lets a reviewer
    /// distinguish "saw the data" from "was denied the data".
    Redacted: bool
}

/// A classified field was written by a caller. Value-free by design.
type ClassifiedFieldWrittenPayload = {
    UserId: UserId
    EntityName: string
    FieldPath: string
    Level: string
}

/// Phase 188 — a classified field was redacted or blocked at an egress
/// boundary (export payload / RPC response / audit-or-log sink) by the
/// `EgressGate` because the active `EgressPolicy` returned a non-`Allow`
/// decision for its `ClassificationLevel`. Reserved
/// `SourceModule = "_platform.classification"` (same module as the
/// read/write gate). Value-free by design — records *that* a classified
/// field was stopped at egress, never the field value. One row per
/// non-`Allow` decision, so a deny is observable and never silent
/// (GP 12).
type EgressBlockedPayload = {
    /// Acting subject the egress was destined for (`EgressContext.Actor`)
    /// — a recipient user id, peer id, or sink name.
    Actor: string
    /// Entity type the field belongs to.
    EntityName: string
    /// Dotted field path that was redacted / dropped.
    FieldPath: string
    /// `ClassificationLevel.name` of the field.
    Level: string
    /// The gate's decision — `"Redact"` or `"Block"` (`Allow` is never
    /// audited).
    Decision: string
    /// The egress boundary the field was leaving — `"ExportPayload"` /
    /// `"RpcResponse"` / `"AuditSink"` / `"LogSink"` / a custom label.
    Boundary: string
    /// Optional concrete destination label (`EgressContext.Destination`)
    /// — a sink name, a peer URL, a file path. `None` when unspecified.
    Destination: string option
}

/// Phase 772 — an outbound HTTP call was refused by the server-side
/// `IEgressPolicy` before its socket opened. Recorded under the reserved
/// `_platform` scope by the platform client factory's handler. Carries
/// the ORIGIN and the component, never the path or the query string — a
/// denial that named the URL would put onto the audit trail exactly the
/// bytes the policy exists to keep in-process. One row per denial, so a
/// refused call is observable and never silent (GP 12).
type EgressDeniedPayload = {
    /// The calling component's stable `ComponentId` value — the module
    /// that claimed the async chain, or `module:_platform` when none did.
    Component: string
    /// The refused destination's origin — `scheme://host[:port]`.
    Origin: string
    /// `EgressSurface.label` of the client that made the call.
    Surface: string
    /// The policy's own sentence for the refusal.
    Reason: string
}
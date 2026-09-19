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

/// Phase 18 — a typed inter-platform peer contract call resolved on the
/// receiver (the host dispatched it to a terminal outcome). Emitted once
/// per inbound call by the peer host's contract handler. Reserved
/// `SourceModule = "_platform.peer"`. PII-free: identities are peer ids
/// plus a correlation id — never end-user payload.
type PeerCallCompletedPayload = {
    /// The hosted `contractId` the call targeted.
    ContractId: string
    /// The contract method name dispatched.
    MethodName: string
    /// `PeerId` of the validated *calling* peer, taken from the
    /// authenticated `PeerPrincipal` — never the self-asserted wire body.
    CallerPeerId: string
    /// The cascade-wide correlation id shared across every hop, so a
    /// federated call is reconstructable end to end from audit alone.
    RootRequestId: string
    /// `true` when dispatch returned `Ok`; `false` on a `PeerError`.
    Succeeded: bool
    /// Short outcome label: `"ok"` on success, else the `PeerError` DU
    /// case name (e.g. `"PeerMethodNotFound"`). Operator dashboards group
    /// peer-call failures by this label without reading message detail.
    Outcome: string
    /// Wall-clock time the call resolved.
    OccurredAt: DateTimeOffset
}

/// Phase 310 — a *long-running* peer call reached its terminal outcome on
/// the receiver. Emitted once per finished job by `PeerJobHandler.Execute`,
/// after the typed result has been parked in the `IPeerJobResultStore`.
/// Reserved `SourceModule = "_platform.peer"`, the same family as
/// `PeerCallCompleted`. PII-free on the same terms: peer ids, a correlation
/// id, and a short outcome label — never the computed result.
///
/// **Why a distinct case rather than a field on `PeerCallCompletedPayload`.**
/// A long-running call already emits one `PeerCallCompleted` row, and it is
/// emitted at *schedule* time: `peer.Handle` returns `Ok jobId`, so that row
/// records `Succeeded = true, Outcome = "ok"` however the background
/// computation later ends. The two rows answer different questions — "the
/// receiver accepted the call" versus "the receiver's computation finished
/// like this" — and they land minutes apart. Marking the phase on the
/// existing payload would have changed the wire shape of every immediate
/// call's row for the sake of the minority that are long-running; a new case
/// leaves that payload byte-for-byte identical (GP 11) and lets an operator
/// query terminal outcomes without scanning schedule-time noise.
///
/// The pair is correlated by `RootRequestId` — the cascade-wide id the
/// receiver *derived* (Phase 331), threaded to the execution side on the job
/// payload so the terminal row is filed under the same correlation as the
/// schedule-time row rather than a freshly-minted one.
///
/// **Expiry is deliberately not a terminal outcome here.** Phase 316's
/// retention can retire a parked result before anyone polls it, but that is
/// the lifetime of the *record*, not the outcome of the *call*: this row is
/// written when the computation finishes, so the trail stays truthful
/// whether or not the result is ever collected. Auditing expiry would also
/// mean emitting from `IPeerJobResultStore.TryGetResult`, i.e. from inside
/// the poll route's read path — a write in a read seam, and the shape the
/// estate's post-response side-effect hazard lives in.
type PeerJobCompletedPayload = {
    /// The hosted `contractId` the long-running call targeted.
    ContractId: string
    /// The contract method name whose job resolved.
    MethodName: string
    /// `PeerId` of the peer that *scheduled* the call, taken from the
    /// validated `PeerCallContext` at dispatch time and carried on the job
    /// payload — never re-derived on the execution side, which has no
    /// request and therefore no principal to read.
    CallerPeerId: string
    /// The same cascade-wide correlation id the schedule-time
    /// `PeerCallCompleted` row carries, so the two rows line up.
    RootRequestId: string
    /// Substrate `JobId` of the backing job — the id the caller polls with,
    /// so an operator can join this row to a poll trace.
    JobId: Guid
    /// `true` when the job resolved `Completed`; `false` on `Failed`.
    Succeeded: bool
    /// Short outcome label: `"ok"` on `Completed`, else the `PeerError` DU
    /// case name (e.g. `"PeerHandler"`). Same vocabulary as
    /// `PeerCallCompletedPayload.Outcome`, so a dashboard groups both rows
    /// on one axis.
    Outcome: string
    /// Wall-clock time the job's terminal status was recorded.
    OccurredAt: DateTimeOffset
}

/// Phase 311 — the receiver's composed clean-room gate reached a decision
/// over one contract answer. Emitted once per gated dispatch by
/// `CleanRoomGate`, whichever way the decision went. Reserved
/// `SourceModule = "_platform.peer"`, the same family as
/// `PeerCallCompleted`, because a suppression is a federation event and an
/// operator reconstructs a call from one trail.
///
/// **Why a distinct case rather than fields on `PeerCallCompletedPayload`.**
/// Same argument Phase 310 made for `PeerJobCompleted`: the call-completed
/// row answers "the receiver dispatched this call and it ended like this",
/// and a gate decision answers "and this is what the privacy floor did to
/// the answer". Widening the existing payload would change the wire shape
/// of every immediate peer call's row for the sake of the minority that are
/// gated; a new case leaves it byte-for-byte identical (GP 11) and lets an
/// operator query suppressions without scanning every call.
///
/// **This is the ONLY place the withhold reason is recorded.** The wire
/// refusal (`PeerCleanRoomWithheld`) carries the template id and nothing
/// else on purpose: the broker's reasons are quantitative ("released cohort
/// 7 is below the k-anonymity floor 10") and a caller able to read them back
/// while varying its query has a counting oracle over the protected data.
/// Receiver-side audit is where that detail belongs, and the Phase 18a
/// audit-transparency contract is the deliberate, caller-scoped opt-in for
/// exposing any of it.
///
/// PII-free on the same terms as the rest of the family: peer ids, a
/// correlation id, an outcome flag, and `SuppressedCells` — which are the
/// *author-chosen bucket labels* of a histogram ("age-25-34", "region-north"),
/// never a cell's value and never an end-user identifier. A deployment whose
/// bucket labels would themselves be identifying has authored a clean-room
/// template that leaks with or without this row.
type PeerCleanRoomDecisionPayload = {
    /// The hosted `contractId` the gated call targeted.
    ContractId: string
    /// The contract method name whose answer was gated.
    MethodName: string
    /// `TemplateId` of the `CleanRoomTemplate` composed for this contract.
    TemplateId: string
    /// `PeerId` of the validated *calling* peer, taken from the derived
    /// `PeerCallContext` — never the self-asserted wire body.
    CallerPeerId: string
    /// The cascade-wide correlation id, so this row joins the
    /// `PeerCallCompleted` row the same call produces.
    RootRequestId: string
    /// `true` when the answer was released (possibly with cells
    /// suppressed); `false` when the whole answer was withheld.
    Released: bool
    /// Labels of the cells dropped by per-cell suppression. Empty on a
    /// withhold (nothing was released) and on an untouched release.
    SuppressedCells: string list
    /// The gate's own explanation — the broker's `Withheld` reason, or the
    /// substrate's reason for overriding a release. Empty on a clean
    /// release. Recorded here and never sent on the wire.
    Reason: string
    /// Wall-clock time the gate decided.
    OccurredAt: DateTimeOffset
}

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

/// Phase 483 — one round of a multi-round federated run reached its
/// barrier and its responses were folded. Emitted once per completed
/// round, whatever the dropout outcome.
type FederationRoundCompletedPayload = {
    /// Caller-assigned stable id of the run this round belongs to.
    RunId: string
    /// 1-based round number within the run.
    RoundNumber: int
    /// Participants the round was fanned out to.
    ParticipantCount: int
    /// Participants that answered before the round's effective deadline.
    RespondedCount: int
    /// Participants recorded as dropped for this round.
    DroppedCount: int
    /// Wall-clock time the round's barrier resolved.
    OccurredAt: DateTimeOffset
}

/// Phase 483 — a participant failed to answer a round before its
/// effective deadline (or answered with an error) and the run's
/// `DropoutPolicy` classified it as dropped. One row per dropped
/// participant per round, so every dropout decision is auditable
/// individually rather than as a count.
type FederationParticipantDroppedPayload = {
    /// Caller-assigned stable id of the run.
    RunId: string
    /// 1-based round number the participant dropped out of.
    RoundNumber: int
    /// `PeerId` of the dropped participant.
    PeerId: string
    /// Short, PII-free explanation — the `PeerError` DU case name or the
    /// substrate's own deadline label.
    Reason: string
    /// Wall-clock time the dropout was decided.
    OccurredAt: DateTimeOffset
}

/// Phase 483 — a multi-round run terminated without reaching its
/// completion condition: the dropout policy refused to continue, the
/// consumer's fold aborted, or the run was cancelled. The persisted
/// `RoundState` survives, so an aborted run is resumable.
type FederationRunAbortedPayload = {
    /// Caller-assigned stable id of the run.
    RunId: string
    /// The round the run was in when it aborted (0 before the first
    /// round completed).
    RoundNumber: int
    /// Short, PII-free explanation of the abort.
    Reason: string
    /// Wall-clock time the run aborted.
    OccurredAt: DateTimeOffset
}

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

/// `IArtefactSigner.Sign` succeeded. Reserved `SourceModule =
/// "_platform.signing"`. PII-free + secret-free: only the key id,
/// algorithm name, and a SHA-256 digest of the signed artefact travel —
/// never the artefact bytes nor any key material.
type ArtefactSignedPayload = {
    /// Actor who invoked the signer. `"system"` for automated signing
    /// pipelines; the authenticated user's id for operator-initiated
    /// signs.
    Actor: string
    /// Active signing-key id the artefact was signed under.
    KeyId: string
    /// `SigningAlgorithm.name` — `"EcdsaP256"` or `"Ed25519"`.
    Algorithm: string
    /// Lowercase-hex SHA-256 of the signed artefact bytes. Lets a
    /// compliance audit prove "this exact artefact was signed under this
    /// key" without the bytes entering the audit trail.
    ArtefactSha256: string
}

/// A new signing key became active for `IArtefactSigner.Sign`, rotating
/// out a prior key (whose public component remains discoverable for
/// archival verification). Reserved `SourceModule = "_platform.signing"`.
/// Emitted by the rotation helper; the in-process default does not
/// auto-rotate, so this fires only on an explicit operator rotation.
type SigningKeyRotatedPayload = {
    /// Actor who triggered the rotation.
    Actor: string
    /// Key id rotated out of active signing. `None` for the very first
    /// key activation (no predecessor).
    OldKeyId: string option
    /// Key id now active for signing.
    NewKeyId: string
    /// `SigningAlgorithm.name` of the new active key.
    Algorithm: string
}

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
    UserId: string
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
    UserId: string
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
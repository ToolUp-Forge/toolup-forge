// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The inter-platform peer companion's audit events: a peer call completed, a
/// peer job completed, a clean-room decision, and a federation round's
/// completion, dropped participants and abort. Registered from
/// `PeerCompose.withPeer`; Core never names them.
module ToolUp.InterPlatform.PeerAuditEvents

open System
open ToolUp.Platform
open ToolUp.Platform.Audit.AuditLog

/// a typed inter-platform peer contract call resolved on the
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

/// a *long-running* peer call reached its terminal outcome on
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
/// receiver *derived*, threaded to the execution side on the job
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

/// the receiver's composed clean-room gate reached a decision
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

/// one round of a multi-round federated run reached its
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

/// a participant failed to answer a round before its
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

/// a multi-round run terminated without reaching its
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

/// Every audit event the ToolUp.InterPlatform companion records.
type PeerAuditEvent =
    /// a typed inter-platform peer contract call resolved on
    /// the receiver. Emitted once per inbound call by the peer host's
    /// contract handler after dispatch reaches a terminal outcome.
    /// Reserved `SourceModule = "_platform.peer"`.
    | PeerCallCompleted of PeerCallCompletedPayload
    /// a long-running peer call reached its terminal outcome.
    /// Emitted by `PeerJobHandler.Execute` once the backing job has
    /// resolved and its typed result is parked. Distinct from
    /// `PeerCallCompleted`, which for a long-running method records only
    /// that the call was *accepted and scheduled*. Reserved
    /// `SourceModule = "_platform.peer"`.
    | PeerJobCompleted of PeerJobCompletedPayload
    /// the receiver's composed clean-room gate decided over one
    /// contract answer (released, possibly with cells suppressed, or
    /// withheld whole). Emitted by `CleanRoomGate` once per gated dispatch.
    /// Reserved `SourceModule = "_platform.peer"`.
    | PeerCleanRoomDecision of PeerCleanRoomDecisionPayload
    /// one round of a multi-round federated run reached its
    /// barrier and its responses were folded. Emitted by
    /// `IRoundOrchestrator` once per completed round. Reserved
    /// `SourceModule = "_platform.peer"`.
    | FederationRoundCompleted of FederationRoundCompletedPayload
    /// a participant was classified as dropped for a round by
    /// the run's `DropoutPolicy`. One row per dropped participant per
    /// round. Reserved `SourceModule = "_platform.peer"`.
    | FederationParticipantDropped of FederationParticipantDroppedPayload
    /// a multi-round run terminated without reaching its
    /// completion condition. Reserved `SourceModule = "_platform.peer"`.
    | FederationRunAborted of FederationRunAbortedPayload

module PeerAuditEvent =
    [<Literal>]
    let Registrant = "ToolUp.InterPlatform"

    let private kind name : AuditEventKindInfo = {
        Kind = name
        Registrant = Registrant
        Severity = None
    }

    /// The ToolUp.InterPlatform family: its kinds and its codec.
    let family: AuditEventFamily<PeerAuditEvent> = {
        Registrant = Registrant
        Kinds = [
            kind "PeerCallCompleted"
            kind "PeerJobCompleted"
            kind "PeerCleanRoomDecision"
            kind "FederationRoundCompleted"
            kind "FederationParticipantDropped"
            kind "FederationRunAborted"
        ]
        Encode =
            function
            | PeerCallCompleted p -> "PeerCallCompleted", AuditPayloadJson.write p
            | PeerJobCompleted p -> "PeerJobCompleted", AuditPayloadJson.write p
            | PeerCleanRoomDecision p -> "PeerCleanRoomDecision", AuditPayloadJson.write p
            | FederationRoundCompleted p -> "FederationRoundCompleted", AuditPayloadJson.write p
            | FederationParticipantDropped p -> "FederationParticipantDropped", AuditPayloadJson.write p
            | FederationRunAborted p -> "FederationRunAborted", AuditPayloadJson.write p
        Decode =
            fun kind payload ->
                match kind with
                | "PeerCallCompleted" -> PeerCallCompleted(AuditPayloadJson.read payload)
                | "PeerJobCompleted" -> PeerJobCompleted(AuditPayloadJson.read payload)
                | "PeerCleanRoomDecision" -> PeerCleanRoomDecision(AuditPayloadJson.read payload)
                | "FederationRoundCompleted" -> FederationRoundCompleted(AuditPayloadJson.read payload)
                | "FederationParticipantDropped" -> FederationParticipantDropped(AuditPayloadJson.read payload)
                | "FederationRunAborted" -> FederationRunAborted(AuditPayloadJson.read payload)
                | other -> failwithf "not a ToolUp.InterPlatform audit kind: %s" other
    }

    /// The `AuditEvent` one of these events is recorded as.
    let event (value: PeerAuditEvent) : AuditEvent = AuditEventFamily.event family value

    /// The event an `AuditEvent` carries, if it carries one of these.
    let tryRead (audit: AuditEvent) : PeerAuditEvent option = AuditEventFamily.tryRead family audit

/// Matches an `AuditEvent` carrying one of the ToolUp.InterPlatform events.
let (|PeerAudit|_|) (audit: AuditEvent) : PeerAuditEvent option = PeerAuditEvent.tryRead audit
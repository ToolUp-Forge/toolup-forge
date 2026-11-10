// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The AI assistant companion's audit events: a conversation exported, a
/// fast-path beacon rejected, a conversation replayed, an answer verified or
/// flagged, a cross-module read, and a retention purge. Registered from
/// `AICompose.withAI`; Core never names them.
module ToolUp.AI.AIAuditEvents

open System
open ToolUp.Platform
open ToolUp.Platform.Audit.AuditLog

/// An AI conversation was exported (markdown / JSON
/// download) from the chat side panel. Metadata only by design: the
/// payload never carries conversation content or tool payloads (which
/// may contain PII), only *which* conversation, *whether* the user
/// opted into tool-detail inclusion, and *who* exported it — enough
/// for an admin to detect cross-team export patterns without the audit
/// trail itself becoming a PII sink.
type ConversationExportPayload = {
    ConversationId: string
    /// `true` when the user ticked "Include tool details" (the export
    /// file then contains raw `ToolCalls`); `false` for the sanitised
    /// default (tool calls stripped from the download).
    IncludeToolDetails: bool
    ExportedBy: string
}

/// the AI fast-path beacon (or the equivalent
/// `SubmitMessage`) was rejected by the ownership gate. Emitted when
/// the first persisted message of a shared-container conversation
/// (`team-{teamId}`) records a `CreatedBy` that does not match the
/// caller's `AccessContext.UserId`. Surfaces cross-user history-
/// forgery / prompt-injection attempts before the synthetic turns
/// reach the provider-history blob the LLM reads next.
///
/// `Caller` is the `UserId` that attempted the append; `Owner` is the
/// `CreatedBy` recorded on the first persisted message. `Surface`
/// distinguishes which entry point fired the gate (`"beacon"` for the
/// fast-path POST, `"submit"` for `IAIAssistantApi.SubmitMessage`),
/// so forensics can tell whether the cross-user write came in via the
/// fire-and-forget beacon or the full agent-loop path.
///
/// PII-free: ids only — neither the instruction nor the conversation
/// content travels through the audit trail. Conversation owner is
/// already recorded by the deployment under the persisted blob; the
/// audit row exists so cross-user attempts are visible regardless of
/// blob-storage retention.
type BeaconRejectedPayload = {
    /// Conversation id whose append was refused. The blob (if any
    /// existed) is unchanged — the gate fires before the save.
    ConversationId: Guid
    /// The caller's `AccessContext.UserId` at the moment of refusal.
    /// Empty string when no identity was resolved (the handler also
    /// refuses unauthenticated callers).
    Caller: string
    /// The owner recorded on the first persisted message
    /// (`existing[0].CreatedBy`). Empty when the rejection was for a
    /// reason other than ownership mismatch (defensive — the field
    /// is shaped so a future use of `BeaconRejected` for non-ownership
    /// reasons doesn't break the wire shape).
    Owner: string
    /// `"beacon"` for the fast-path POST, `"submit"` for
    /// `IAIAssistantApi.SubmitMessage`. Pin-points the surface so
    /// operator triage can correlate against per-surface metrics.
    Surface: string
}

/// `ConversationReplay.replay` ran. Links the original to its replay
/// via the two `ConversationId`s + records the operator-supplied
/// override labels (per `ConversationReplayOptions`). `Delta` is
/// the human-readable comparison summary from `ConversationReplayResult.Delta`.
type ConversationReplayedPayload = {
    OriginalConversationId: string
    ReplayConversationId: string
    /// SHA-256 hex of the override system prompt, if supplied;
    /// `None` when the replay re-used the original prompt.
    NewSystemPromptDigest: string option
    /// Provider label override, if supplied; `None` when the replay
    /// re-used the original provider.
    NewProvider: string option
    /// Free-form SDK label the operator stamped on the replay;
    /// `None` when the replay re-used the original SDK version.
    SdkAnnotation: string option
    /// Operator-readable summary from `ConversationReplayResult.Delta`.
    /// Today: turn-count + per-turn token deltas. Do not machine-parse.
    Delta: string
}

/// the answer-verification verdict for one served answer, and
/// the joins from that runtime row to the provenance the answer stands on.
///
/// **Recorded on the affirmative verdict too**, the Phase 657 discipline:
/// a row written only when a figure went unverified cannot distinguish a
/// clean answer from an answer the gate never saw, and those are the two
/// states an auditor most needs to tell apart. One row per verified answer,
/// so the volume is bounded by answered turns rather than by tokens.
///
/// **Emitted BESIDE the existing `IEventStore` trail, not instead of it.**
/// The per-unmatched-token `IEventStore` records remain the module-scoped
/// query surface; this row is the one that rides `IAuditLog`, so whichever
/// sinks a deployment composed — a hash-chained ledger among them — record
/// it, and the answer path depends on none of them.
///
/// **Every join field is optional, and absence is honest.** A deployment
/// that composes no certificates and starts from no sealed composition
/// records `None` for both rather than a placeholder; a placeholder would
/// be a claim, and this row makes none it cannot support.
type AnswerVerificationPayload = {
    TaskId: Guid
    ConversationId: Guid
    /// Gate mode in force: `"Annotate"` or `"Strict"`. An `Off` gate runs
    /// no verification and records no row at all.
    Mode: string
    /// Numeric tokens that matched a retrieved fact.
    Verified: int
    /// Numeric tokens with no matching fact while facts WERE in scope —
    /// the anti-hallucination signal.
    Unmatched: int
    /// Numeric tokens the turn had no facts to check against.
    Unverifiable: int
    /// How many facts were in scope for the turn. `0` is why a token can be
    /// unverifiable without being unmatched.
    FactsInScope: int
    /// Per-token verdicts, in the answer's reading order.
    Tokens: AnswerVerificationTokenAudit list
    /// The distinct fact ids this answer's verified figures cite, sorted.
    /// The walk from this row into the fact tier.
    CitedFactIds: string list
    /// SHA-256 over the canonical join of `CitedFactIds` — a
    /// deployment-independent head for the provenance chain this answer
    /// stands on, recomputable by anyone holding the ids. `None` when the
    /// answer verified against no fact.
    ProvenanceChainHead: string option
    /// The grounding certificate covering this answer's chain, when the
    /// deployment holds one. `None` when it issues no certificates.
    CertificateRef: string option
    /// The sealed-composition identity this process affirmed at boot, when
    /// it started under a verified profile. `None` under an unsealed start
    /// or a non-affirmative verdict — naming a seal for a composition the
    /// boot check declined to affirm would assert exactly what it refused.
    CompositionSealId: string option
    ProviderName: string
    ProviderModel: string
    OccurredAt: DateTimeOffset
}

/// one invocation of the built-in cross-module AI tool
/// family (`_platform.ai.*`), recorded whatever its outcome.
///
/// **Why this is a typed union case rather than an AI-tier
/// `ModuleEvent`.** Phase 45's `ToolAllowlistDenied` and Phase 36.D's
/// `AIConsentGranted` / `AIConsentDenied` write raw `ModuleEvent`s under
/// their own `SourceModule` (`_platform.ai.*`), which reads like the
/// precedent to follow. It cannot be followed here: the Phase 9g
/// replicator admits an event only when
/// `SourceModule = AuditSourceModule.value`, so a row minted that way
/// never reaches an external sink. This family's whole point is that a
/// read of one module's data from a conversation about another is
/// reviewable — including by a reviewer reading the deploying
/// organisation's own SIEM rather than this deployment's store. So it is
/// typed, replicated, and queryable through `IAuditLog.GetAuditTrail`
/// like every other audit row.
///
/// **One row per invocation, on every outcome path.** A refusal is the
/// row that matters most, so `Allowed = false` rows are emitted for the
/// RBAC / grant / opt-in / consent refusals alike, with `Outcome`
/// naming which gate spoke. Counting rows — rather than checking that
/// one exists — is what makes "the trail captures the attempt" mean
/// something.
///
/// **PII envelope.** Identifiers and a query discriminator; never the
/// tool arguments (model-authored, unbounded) and never any of the data
/// the read returned. `ResultBytes` is the SIZE of the rendered result,
/// which is the one thing about the payload an operator needs in order
/// to see an exfiltration-shaped read.
type CrossModuleReadPayload = {
    /// The conversation the read belonged to. `None` when the tool was
    /// invoked outside a live turn (a contract-pack call, a probe) — the
    /// same condition under which Phase 36.D's consent gate abstains.
    ConversationId: Guid option
    /// The caller the agent loop resolved.
    UserId: UserId
    /// The module the conversation was active in when the read was made
    /// — the "from" half of "cross-module". `None` when the user was on
    /// no module's page, which is an ordinary state and not a defect.
    SourceConvActiveModule: string option
    /// The module whose data was read — the grouping axis of the
    /// `/dev/ai-cross-module` rollup. `Some m` exactly when the read
    /// resolved to ONE module; `None` when it resolved to none or to
    /// several, both of which `_platform.ai.query_entity` can genuinely
    /// do (it names an entity type, and the data catalogue may attribute
    /// that type to zero producers or to many). `TargetModules` carries
    /// the full set either way, so nothing is lost — the option exists
    /// so a grouping key is never a guess or a synthesised join of
    /// several module names.
    TargetModule: string option
    /// Every module this read would have touched, resolved the same way
    /// the Phase 36.C queryability gate and the Phase 36.D consent gate
    /// resolve it. Empty when the catalogue attributes the entity type
    /// to no producer — the documented attribution hole both of those
    /// phases record, surfaced here rather than papered over with a
    /// fabricated module name.
    TargetModules: string list
    /// The `_platform.ai.*` tool that performed the read.
    ToolName: string
    /// the stable component id of the component that
    /// performed the read, i.e. `ComponentId.forTool ToolName`. Rides the
    /// payload so a trail joins with the `component_id` telemetry
    /// dimension on the one key that survives a rename.
    ComponentId: string
    /// The read's discriminator within the target: a `query_module`
    /// query key, a `get_latest_result` result type, a `query_entity`
    /// entity type. `None` for the tools that take neither.
    QueryKey: string option
    /// Wall clock for the WHOLE tool invocation, gates included — not
    /// just the store call. A read that suspended on a Phase 36.D
    /// consent prompt therefore carries the time the user took to
    /// answer, because from the conversation's point of view that is how
    /// long the read took. Segment on `Outcome` when that distinction
    /// matters.
    LatencyMs: float
    /// Size in bytes of the rendered tool result, refusals included
    /// (a refusal is small, which is itself the signal).
    ResultBytes: int
    /// Did the read reach the data. `false` for every refusal, whichever
    /// gate produced it.
    Allowed: bool
    /// Why, in the tool family's own vocabulary: `"ok"` on the success
    /// path, otherwise the `error` discriminator the tool rendered to
    /// the model (`"PermissionDenied"` / `"UnqueryableModule"` /
    /// `"UserDenied"` / `"NotFound"` / `"InvalidArguments"` / …). A
    /// string rather than a DU because it mirrors a wire vocabulary the
    /// tools render for a model, which a closed union here could only
    /// fall behind.
    Outcome: string
}

/// the conversation retention sweep purged one or more AI
/// conversations from a scope. One row per run that removed something;
/// a run that expired nothing writes no row (a purge trail records
/// deletions, not the absence of them), mirroring
/// `KnowledgeDocumentsPurged`.
///
/// The conversation ids ARE recorded, unlike `ConversationErased`: a
/// retention purge is a scope-level housekeeping act keyed by
/// conversation id, not by data subject, and a count alone cannot be
/// reconciled against the store afterwards. The ids are opaque GUIDs
/// and name no user.
type ConversationsPurgedPayload = {
    /// Scope whose conversations were swept (GP 4: one scope per run).
    ScopeId: string
    /// Conversation ids removed by this run, oldest activity first.
    ConversationIds: string list
    /// `ConversationIds.Length`, denormalised so a sink can aggregate
    /// without parsing the list.
    PurgedCount: int
    /// The age limit in whole seconds that selected them, when the
    /// policy set one — the row carries the policy that produced it.
    MaxAgeSeconds: int64 option
    /// The count limit, when the policy set one.
    MaxCount: int option
    /// Conversations the sweep selected but could not fully remove
    /// (a sibling blob's delete failed). Non-zero means the next sweep
    /// retries them; the trail carries the same loud signal the
    /// operator log does (GP 9).
    FailedCount: int
}

/// Every audit event the ToolUp.AI companion records.
type AIAuditEvent =
    /// An AI conversation was exported from the chat side
    /// panel. Metadata-only payload (no conversation content / tool
    /// payloads) so the audit trail can record export activity without
    /// itself leaking PII.
    | ConversationExported of ConversationExportPayload
    /// the fast-path beacon or `SubmitMessage` was refused
    /// by the conversation-ownership gate (caller's `UserId` did not
    /// match the first persisted message's `CreatedBy`). Distinct from
    /// the `_platform.ai.fastpath` / `FastPathRejected` event the
    /// beacon handler already emits for malformed / oversized / scope-
    /// resolution-missing inputs — this case is specifically for cross-
    /// user history-forgery attempts in shared-container modes.
    | BeaconRejected of BeaconRejectedPayload
    /// `ConversationReplay.replay` produced a new
    /// `Conversation`. Links the original + replay ids; records the
    /// operator's chosen override labels.
    | ConversationReplayed of ConversationReplayedPayload
    /// an answer was verified and every numeric figure it
    /// carried matched a retrieved fact (or the turn had no facts to check
    /// against). The affirmative row; it exists so that the absence of a
    /// row stays a different fact from a clean one.
    | AnswerVerificationPassed of AnswerVerificationPayload
    /// an answer carried at least one numeric figure that
    /// matched no fact in the turn's retrieved set while facts WERE in
    /// scope. The grounding refusal, on the same chained path as every
    /// other audited refusal.
    | AnswerVerificationFlagged of AnswerVerificationPayload
    /// one invocation of the built-in cross-module AI tool
    /// family, allowed or refused. The read-side twin of Phase 36.D's
    /// consent decisions: those record what the user ALLOWED, this
    /// records what was actually reached, and only the two together
    /// answer "what did the agent read out of modules nobody named".
    | CrossModuleRead of CrossModuleReadPayload
    /// the AI conversation retention sweep purged one or
    /// more conversations from a scope (age and/or count expiry). Every
    /// sibling blob of each purged conversation is gone when this row
    /// is written. Recorded under the swept scope.
    | ConversationsPurged of ConversationsPurgedPayload

module AIAuditEvent =
    [<Literal>]
    let Registrant = "ToolUp.AI"

    let private kind name : AuditEventKindInfo = {
        Kind = name
        Registrant = Registrant
        Severity = None
    }

    /// The ToolUp.AI family: its kinds and its codec.
    let family: AuditEventFamily<AIAuditEvent> = {
        Registrant = Registrant
        Kinds = [
            kind "ConversationExported"
            kind "BeaconRejected"
            kind "ConversationReplayed"
            kind "AnswerVerificationPassed"
            kind "AnswerVerificationFlagged"
            kind "CrossModuleRead"
            kind "ConversationsPurged"
        ]
        Encode =
            function
            | ConversationExported p -> "ConversationExported", AuditPayloadJson.write p
            | BeaconRejected p -> "BeaconRejected", AuditPayloadJson.write p
            | ConversationReplayed p -> "ConversationReplayed", AuditPayloadJson.write p
            | AnswerVerificationPassed p -> "AnswerVerificationPassed", AuditPayloadJson.write p
            | AnswerVerificationFlagged p -> "AnswerVerificationFlagged", AuditPayloadJson.write p
            | CrossModuleRead p -> "CrossModuleRead", AuditPayloadJson.write p
            | ConversationsPurged p -> "ConversationsPurged", AuditPayloadJson.write p
        Decode =
            fun kind payload ->
                match kind with
                | "ConversationExported" -> ConversationExported(AuditPayloadJson.read payload)
                | "BeaconRejected" -> BeaconRejected(AuditPayloadJson.read payload)
                | "ConversationReplayed" -> ConversationReplayed(AuditPayloadJson.read payload)
                | "AnswerVerificationPassed" -> AnswerVerificationPassed(AuditPayloadJson.read payload)
                | "AnswerVerificationFlagged" -> AnswerVerificationFlagged(AuditPayloadJson.read payload)
                | "CrossModuleRead" -> CrossModuleRead(AuditPayloadJson.read payload)
                | "ConversationsPurged" -> ConversationsPurged(AuditPayloadJson.read payload)
                | other -> failwithf "not a ToolUp.AI audit kind: %s" other
    }

    /// The `AuditEvent` one of these events is recorded as.
    let event (value: AIAuditEvent) : AuditEvent = AuditEventFamily.event family value

    /// The event an `AuditEvent` carries, if it carries one of these.
    let tryRead (audit: AuditEvent) : AIAuditEvent option = AuditEventFamily.tryRead family audit

/// Matches an `AuditEvent` carrying one of the ToolUp.AI events.
let (|AIAudit|_|) (audit: AuditEvent) : AIAuditEvent option = AIAuditEvent.tryRead audit
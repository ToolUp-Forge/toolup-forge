// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of AuditTypes.fs: schema-first user authoring,
// external callback, composition verification, answer verification, fact
// import and grounding-envelope payload records.
// Declaration order is unchanged from the monolith; same namespace, same
// record names — only the file boundary moved. The `AuditEvent` union that
// registers these payloads stays in AuditTypes.fs, append-only.

// ─── Phase 7b — schema-first user-authoring audit payloads ───────────
//
// Emitted by the `IUserSchemaApi` handler + `BlobUserSchemaStore` on
// schema lifecycle transitions. Reserved source-module label
// `_platform.user_schema`. PII-free: identifiers + cardinality + version
// provenance only — never the schema's field values or instance data.

/// Reserved `SourceModule` for schema-first user-authoring audit events.
/// Filter `IEventStore.ReadBySource` on this constant for the trail.
module UserSchemaSourceModule =
    [<Literal>]
    let value = "_platform.user_schema"

/// Phase 7b — the AI proposed a candidate user-authored schema for a
/// scope, surfaced for human review. Emitted by the AI-propose flow
/// (which lives in the consuming application, not the substrate); the
/// substrate ships the payload so an approved proposal's provenance is
/// durable end-to-end.
type SchemaProposedPayload = {
    /// Actor the proposal is attributed to (the user in the conversation).
    UserId: UserId
    /// Scope the proposed schema targets.
    ScopeId: string
    /// Schema id of the proposal.
    SchemaId: string
    /// Human-facing version label of the proposal.
    VersionLabel: string
    /// Conversation the AI proposal originated from — the proposal→approval
    /// trace anchor.
    ConversationId: string
    /// Number of fields in the proposal. Cardinality only.
    FieldCount: int
}

/// Phase 7b — a user approved a committed schema version whose provenance
/// was `AuthoredBy.AIWithApproval`. Emitted by the store at commit time.
type SchemaApprovedPayload = {
    /// Approving actor.
    UserId: UserId
    /// Scope the schema belongs to.
    ScopeId: string
    SchemaId: string
    /// Store-assigned numeric version of the approved commit.
    Version: int
    VersionLabel: string
    /// `AuthoredBy` projected to a string (`"Human"` /
    /// `"AIWithApproval:{conversationId}"`).
    ProposedBy: string
    /// Originating conversation id when the commit was AI-proposed.
    ConversationId: string option
}

/// Phase 7b — a user-authored schema version was created, updated,
/// migrated, or deleted. Emitted by the store on every committed state
/// change (GP 6). Identifiers + cardinality only.
type SchemaChangedPayload = {
    /// Actor who triggered the change (`"system"` for job-driven runs).
    UserId: UserId
    ScopeId: string
    SchemaId: string
    /// Store-assigned numeric version after the change (`0` for a delete).
    Version: int
    VersionLabel: string
    /// One of `"Created"` / `"Updated"` / `"Migrated"` / `"Deleted"`.
    ChangeKind: string
    /// The predecessor schema id when this is an evolution; `None` for a
    /// fresh authoring or a non-evolution edit.
    EvolvedFrom: string option
    /// Number of migration steps applied (0 for a plain save).
    MigrationsApplied: int
    /// Number of stored instances transformed by a migration (0 for a
    /// plain save).
    InstancesMigrated: int
}

/// Phase 320 — an external-compute completion callback was accepted and
/// its handle resolved (or found already resolved). Emitted on **every**
/// resolution, including the idempotent duplicate (GP 6): "this handle
/// was resolved twice and the second was a no-op" is exactly the fact an
/// incident reconstruction needs, and an audit trail that records only
/// the first cannot distinguish a well-behaved retrying backend from a
/// forged replay.
///
/// Carries no secret and no payload — identifiers, the outcome label, and
/// what the platform did with it.
type ExternalCallbackResolvedPayload = {
    /// `ExternalHandle.HandleId` the callback named.
    HandleId: string
    /// `ExternalHandle.Backend` from the stored record — the platform's
    /// own view of which backend owns the work, never the caller's claim.
    Backend: string
    /// Scope the handle was submitted under, from the stored record.
    ScopeId: string
    /// `JobRun.RunId` the handle routed to.
    JobRunId: string
    /// Terminal outcome the callback reported (`ExternalOutcome.label`).
    Outcome: string
    /// What the platform did: `"resolved"` (this callback won the
    /// terminal claim and drove the run), `"already-resolved"` (a
    /// duplicate, or the reconciliation poll got there first — no-op),
    /// `"no-awaiting-run"`, `"scope-mismatch"`, `"sink-not-configured"`.
    Resolution: string
    /// Terminal run status written, when this callback drove the run
    /// (`"succeeded"` / `"failed"` / `"dead-lettered"` /
    /// `"externally-cancelled"`); `None` for every non-`"resolved"`
    /// resolution.
    RunStatus: string option
    /// Phase 486 — the **verified** worker identity that produced this
    /// outcome. `None` when no signature was presented (or the deployment
    /// has not composed signed-outcome verification), so the field
    /// distinguishes "unattributed" from "attributed to X" and never
    /// asserts an unverified claim: a presented-but-unverified signature
    /// refuses the callback rather than reaching this event.
    ///
    /// This is where per-worker attribution is *recorded* — the audit
    /// trail is the queryable surface, since `IExternalCompletionSink`
    /// cannot gain a field without breaking every implementation of it.
    WorkerId: string option
    /// Phase 486 — which of that worker's registered keys signed. Present
    /// exactly when `WorkerId` is.
    WorkerKeyId: string option
    /// Phase 486 — the algorithm the signature verified under
    /// (`WorkerKeyAlgorithm.label`), taken from the REGISTERED key and
    /// never from the request.
    SignatureAlgorithm: string option
    /// Phase 486 — the verified digest of the outcome the worker signed
    /// (`SignedOutcomeVerifier.artifactHash`). The provenance link between
    /// this audit row and the artefact the worker committed to.
    ArtifactHash: string option
    OccurredAt: DateTimeOffset
}

/// Phase 320 — an external-compute completion callback was REFUSED.
///
/// A distinct event from `ExternalCallbackResolved` rather than a
/// `Resolution` value on it, because the two answer different questions
/// and are read by different people: resolutions are operational history,
/// refusals are a **forged-callback signal** an operator wants to alert
/// on. Folding them into one kind means the alert query has to filter on
/// a payload field, and the same reasoning gave `BeaconRejected` its own
/// case rather than a flag on the beacon event.
///
/// `HandleId` is a `string option` because the most suspicious refusals
/// are the ones whose body did not parse at all.
type ExternalCallbackRejectedPayload = {
    /// The handle the caller named, when the body parsed far enough to
    /// carry one.
    HandleId: string option
    /// Why, internally: `"malformed-body"`, `"missing-secret"`,
    /// `"unknown-handle"`, `"secret-mismatch"`, `"scope-mismatch"`,
    /// `"non-terminal-status"`, `"throttled"` — plus, from Phase 486's
    /// signed-outcome gate, the `"signature-*"` family
    /// (`SignedOutcomeRejection.label`: `"signature-required"`,
    /// `"signature-malformed-envelope"`, `"signature-unknown-key"`,
    /// `"signature-key-not-approved"`, `"signature-key-revoked"`,
    /// `"signature-artifact-mismatch"`,
    /// `"signature-unparseable-timestamp"`,
    /// `"signature-stale-timestamp"`, `"signature-invalid"`). The HTTP
    /// response is uniform — this field is the part that is not (the Phase
    /// 232 encryption-admin posture).
    ///
    /// `"signature-artifact-mismatch"` is the one to alert on hardest: it
    /// means a signature that may well be genuine arrived over a result it
    /// does not cover, which is what a substituting relay looks like.
    Reason: string
    /// Remote address the refusal came from, for correlation with the
    /// rate-limited warning. `"unknown"` when the connection reports
    /// none.
    ClientIp: string
    OccurredAt: DateTimeOffset
}

/// Phase 657 — the verdict the boot-time composition verification reached,
/// recorded once per process start.
///
/// **Recorded on every verdict, including the affirmative one.** A record
/// written only when something is wrong cannot distinguish "verified" from
/// "the check never ran" after the fact, and those are the two states an
/// operator most needs to tell apart. The row is one per start, so the
/// volume is bounded by restarts rather than by traffic.
///
/// **PII-free by construction.** Every field is a composition fact — a
/// component id, a config-knob name, a digest — none of which carries user
/// data. Findings are the substrate's own rendered strings, never
/// caller-supplied text.
type CompositionVerificationRecordedPayload = {
    /// Stable verdict label: `"verified"`, `"unverified"`, `"unsealed"`,
    /// or `"drifted"`. Machine-readable; the free-text account is
    /// `Summary`.
    Verdict: string
    /// Composition profile the deployment started under: `"standard"` or
    /// `"verified"`.
    Profile: string
    /// The policy that decided what a non-affirmative verdict does:
    /// `"log-and-serve"` or `"refuse-on-drift"`.
    Policy: string
    /// Whether this verdict refused the process a start. `false` under the
    /// log-and-serve default even when the verdict is not `"verified"` —
    /// which is exactly the pair of facts an operator rolling the policy
    /// forward wants to read together.
    RefusedStart: bool
    /// One rendered line per finding, each naming what moved or what
    /// failed. Empty on an affirmative verdict.
    Findings: string list
    /// One-line human-readable account of the verdict.
    Summary: string
    OccurredAt: DateTimeOffset
}

/// Phase 657 — a composed component was refused a capability beyond the
/// envelope its composition declared.
///
/// The refusal the mandatory capability gate produces under the verified
/// composition profile. Emitted through `IAuditLog` like any other event,
/// so whichever sinks a deployment composed record it and the substrate
/// takes no dependency on which those are.
type CompositionCapabilityRefusedPayload = {
    /// The composed component that attempted the access — the raw
    /// `ComponentId` value.
    Component: string
    /// The capability the attempted operation required, rendered as
    /// `effect/determinism/readiness`.
    Required: string
    /// The envelope the component declared, same rendering. The identity
    /// (`pure/deterministic/distributed-ready`) for an undeclared
    /// component — which is what makes an undeclared component's effecting
    /// access a refusal rather than a pass.
    Declared: string
    /// The gate's own reason, verbatim: the component, the axes it
    /// exceeded, and the remedy.
    Reason: string
    /// Composition profile in force when the refusal happened.
    Profile: string
    OccurredAt: DateTimeOffset
}

/// Phase 680 — one numeric token from a verified answer, with the
/// fact-match status the answer-verification gate reached for it.
///
/// PII-free by construction: the token is a figure the answer already
/// stated, `Canonical` is that figure normalised, and `MatchedFactId` is a
/// content-addressed fact id. No prose, no principal, no free text.
type AnswerVerificationTokenAudit = {
    /// The numeric token exactly as it appeared in the answer.
    Token: string
    /// The canonical decimal value it normalised to (invariant string).
    /// Empty when the token carried no parseable numeric core.
    Canonical: string
    /// The verdict reached for this token: `"verified"`, `"unmatched"`, or
    /// `"no-facts-in-scope"`.
    Verdict: string
    /// The fact this token verified against. `Some` only on a `"verified"`
    /// token whose matching fact carried an id.
    MatchedFactId: string option
}

/// Phase 680 — the deployment-side anchors an answer-verification audit
/// row joins to.
///
/// Neither anchor is derivable inside the answer path: the composition seal
/// is a boot-time fact, and a certificate is issued by a substrate the
/// answer tier holds no dependency on. Both therefore arrive as data
/// through this seam, which **nothing composes by default** — absent, both
/// anchors resolve to `None` and the recorded row says so (GP 11 / GP 13).
///
/// **GP 12.** Identity by value (ids and strings, never live handles);
/// async at the boundary that may do I/O; stateless between calls — every
/// input arrives as a parameter.
type IAnswerProvenanceAnchors =
    /// The sealed-composition identity this process affirmed at boot.
    /// A property rather than a call: it is a process constant, fixed
    /// before the first answer is served.
    abstract CompositionSealId: string option

    /// The certificate ref covering this answer's provenance chain, when
    /// the deployment already holds one. Implementations REPORT what
    /// exists; issuing a certificate here would put a signing round-trip
    /// on every answered turn. `None` whenever none was issued.
    abstract TryCertificateRef:
        scopeId: string * conversationId: Guid * citedFactIds: string list -> Async<string option>

/// Phase 684 — one grounding-envelope mutation that landed through the
/// audited choke point.
///
/// **Before and after, as digests, on the same row.** A mutation record
/// carrying only the new state proves nothing about what it replaced, so a
/// chain of them cannot be walked. With both digests present, `seal +
/// recorded chain ⇒ current envelope` is a computation an auditor can
/// perform from the trail alone — which is the whole point of routing the
/// mutation through a door rather than trusting that nobody moved.
///
/// **Recorded on the clean mutation too**, the Phase 657 / 680 / 683
/// discipline: a trail carrying only the anomalous mutations cannot
/// distinguish a deployment whose grounding envelope moved lawfully from
/// one whose door was never composed.
///
/// **Identifiers only.** Component ids, facet labels, digests, and the
/// operator principal the audit trail already records elsewhere. No
/// declared value, no fact value, no caller-supplied data rides this row.
type GroundingEnvelopeMutatedPayload = {
    /// Which facet of the declared grounding envelope moved:
    /// `"metric-registration"`, `"subject-registration"`,
    /// `"purpose-declaration"`, `"canonical-method"`, or
    /// `"disclosure-policy"`.
    Facet: string
    /// The declaration's subject — the metric id, subject-hierarchy id,
    /// purpose id, or egress-surface name the mutation concerned.
    Subject: string
    /// Lowercase-hex digest of the canonical grounding envelope as it
    /// stood BEFORE this mutation.
    BeforeDigest: string
    /// Lowercase-hex digest of the envelope AFTER it.
    AfterDigest: string
    /// Position of this mutation in the chain, counting from 1. The
    /// position a continuity divergence is reported at.
    Sequence: int
    /// Composition profile in force: `"standard"` or `"verified"`.
    Profile: string
    /// The principal that asked for the mutation.
    Principal: string
    /// The reason the caller stated. Free-form operator text.
    Reason: string
    /// Findings that WOULD have refused this mutation under the verified
    /// profile, recorded rather than enforced because the deployment is
    /// running `standard`. Empty on a clean in-path mutation, and always
    /// empty under `verified` — there the mutation was refused instead.
    /// The same log-then-refuse adoption ladder Phase 657's
    /// `LogAndServe` → `RefuseOnDrift` policy offers.
    Observations: string list
    OccurredAt: DateTimeOffset
}

/// Phase 684 — a grounding-envelope mutation was refused at the choke
/// point and nothing moved.
///
/// Its own event type rather than a flag on the mutation row, for the
/// reason Phase 683's import pair records: the discriminator is what a
/// SIEM rule and a chained-ledger query cut on, and folding it into the
/// payload puts that cut where neither can reach it without decoding
/// every row.
type GroundingMutationRefusedPayload = {
    /// The facet the refused mutation claimed. Same vocabulary as
    /// `GroundingEnvelopeMutatedPayload.Facet`.
    Facet: string
    /// The subject the refused mutation claimed.
    Subject: string
    /// The digest the recorded chain proves the envelope should stand at.
    ChainedDigest: string
    /// The digest actually observed — of the live envelope for an
    /// out-of-path drift, or of the baseline the caller presented for a
    /// stale request.
    ObservedDigest: string
    /// One rendered line per refusal reason, each naming its subject.
    Reasons: string list
    /// Composition profile in force. Always `"verified"` — the standard
    /// profile records observations on the mutation row and refuses
    /// nothing.
    Profile: string
    /// The principal whose mutation was refused.
    Principal: string
    OccurredAt: DateTimeOffset
}
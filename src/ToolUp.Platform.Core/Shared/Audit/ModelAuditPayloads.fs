// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of AuditTypes.fs: model-fit, model-registry,
// model-scoring, model-evaluation, promotion-policy and dataset payload
// records.
// Declaration order is unchanged from the monolith; same namespace, same
// record names — only the file boundary moved. The `AuditEvent` union that
// registers these payloads stays in AuditTypes.fs, append-only.

// ─── Phase 449 — model-fit envelope audit payloads ─────────────────────
//
// Every fit run is audited under `_platform.audit` (GP 6) carrying the
// composite key (`CompositeKeyHash`), so an operator can reconstruct the
// full lifecycle of a reproducible fit — started, completed, and any gate
// failures — from the trail alone. PII-free: identity + cardinality only;
// no dataset rows, no artifact bytes, no opaque spec payload travel.

/// A fit run began — the provider was resolved and the composite identity
/// computed. Reserved `SourceModule = "_platform.audit"`.
type ModelFitStartedPayload = {
    /// SHA-256 hex of the run's composite identity (plan D5). Correlates
    /// the started / completed / gate-failed rows for one fit.
    CompositeKeyHash: string
    /// SHA-256 hex of the opaque model spec.
    SpecHash: string
    /// `{scopeId}/{datasetId}@v{version}` of the vintage the fit read.
    DatasetVersion: string
    /// Seed making the fit reproducible.
    Seed: int64
    /// Resolved provider `Kind`.
    ProviderId: string
    /// Resolved provider version — a component of the composite identity.
    ProviderVersion: string
    /// Scope the fit ran under.
    ScopeId: string
}

/// A fit run produced an outcome — diagnostics + gate verdicts persisted.
/// Emitted whether or not gates passed (a failed gate is a verdict, not a
/// failure of the run). Reserved `SourceModule = "_platform.audit"`.
type ModelFitCompletedPayload = {
    CompositeKeyHash: string
    ProviderId: string
    ProviderVersion: string
    /// Number of diagnostics the provider reported. Cardinality only.
    DiagnosticCount: int
    /// Number of gates evaluated against the diagnostics.
    GatesEvaluated: int
    /// Number of those gates that failed (`0` on a clean pass).
    GatesFailed: int
    /// SHA-256 hex of the produced artifact — cross-references the outcome
    /// without the bytes travelling.
    ArtifactHash: string
    ScopeId: string
}

/// One or more diagnostic gates failed on an otherwise-completed fit. A
/// typed, audited verdict — never an exception (acceptance). One row per
/// run with at least one failed gate; the names travel so an operator can
/// see which gates failed without re-reading the outcome. Reserved
/// `SourceModule = "_platform.audit"`.
type ModelFitGateFailedPayload = {
    CompositeKeyHash: string
    ProviderId: string
    /// Names of the gates that failed (the `GateSpec.Name` diagnostic keys).
    FailedGates: string list
    ScopeId: string
}

/// Phase 599 — a fit batch was submitted: N per-item fit jobs enqueued under
/// one correlation id. The single batch-level audit row; each item's run
/// then emits its own Phase 449 fit rows carrying the same batch id in its
/// registration annotations. Reserved `SourceModule = "_platform.audit"`.
type ModelFitBatchSubmittedPayload = {
    /// Caller-supplied batch correlation id — the value per-item outcomes
    /// carry in their `batch.id` registration annotation.
    BatchId: string
    /// Number of fit requests in the batch.
    ItemCount: int
    /// Actor who submitted the batch.
    SubmittedBy: string
    ScopeId: string
}

// ─── Phase 453 — model-registry audit payloads ─────────────────────────
//
// Every registry lifecycle event is audited under `_platform.audit` (GP 6)
// carrying the artifact's composite-key hash (plan D5), so an operator can
// reconstruct an evidence base's governance history — registered, promoted,
// retired, and any refused promotion — from the trail alone. PII-free:
// identity + status only; no diagnostics, no parameter bytes, no opaque spec
// payload travel.

/// A model artifact was registered from a completed fit (plan Stage 4). The
/// first-seen registration only — an idempotent re-register of an existing
/// composite key emits nothing (no state changed). Reserved
/// `SourceModule = "_platform.audit"`.
type ModelArtifactRegisteredPayload = {
    /// SHA-256 hex of the artifact's composite identity (plan D5). Correlates
    /// the registered / transitioned rows for one artifact.
    CompositeKeyHash: string
    /// SHA-256 hex of the opaque model spec.
    SpecHash: string
    /// `{scopeId}/{datasetId}@v{version}` of the vintage the fit read.
    DatasetVersion: string
    /// Resolved provider `Kind`.
    ProviderId: string
    /// Resolved provider version — a component of the composite identity.
    ProviderVersion: string
    /// Lifecycle status at registration (always `"Fitted"` today, carried as
    /// data so a future pre-fit `Draft` path stays wire-compatible).
    Status: string
    /// Actor who registered the artifact.
    RegisteredBy: string
    /// Scope the artifact lives under.
    ScopeId: string
}

/// A model artifact's lifecycle status transitioned (plan Stage 4). Emitted
/// after the new version is persisted. Reserved
/// `SourceModule = "_platform.audit"`.
type ModelArtifactTransitionedPayload = {
    CompositeKeyHash: string
    /// Status the artifact held before the transition.
    FromStatus: string
    /// Status the artifact entered.
    ToStatus: string
    /// Actor who performed the transition.
    ActorUserId: string
    ScopeId: string
}

/// A model artifact lifecycle transition was refused (plan Stage 4 / GP 4).
/// A denied `Approved` promotion from a non-Owner/Admin, or an edge the
/// lifecycle graph forbids — the refusal is itself audit-worthy (repeated
/// denials are a governance-gate signal, like `TeamCreationDenied`).
/// Reserved `SourceModule = "_platform.audit"`.
type ModelArtifactTransitionDeniedPayload = {
    CompositeKeyHash: string
    /// Status the caller attempted to move the artifact into.
    AttemptedStatus: string
    /// Actor whose transition was refused.
    ActorUserId: string
    /// Why the transition was refused (`"requires Owner/Admin"` /
    /// `"illegal transition Fitted → Approved"` / …).
    Reason: string
    ScopeId: string
}

/// Phase 644 — a lifecycle transition JUDGED at the author-agnostic seam,
/// with the author and the channel it arrived on. Reserved
/// `SourceModule = "_platform.audit"`.
///
/// **Why this is a third row and not two more fields on the two above.**
/// Those two are written by the registry, which knows the actor id it was
/// handed and nothing else: it has no way to learn whether the call came
/// from a local admin screen, from a peer deployment across a federation
/// edge, or from a promotion policy, because none of that is in its
/// signature and widening the signature would break every
/// `IModelRegistry` implementation. This row is written by the seam, which
/// is the only place all three are known — so the attribution is recorded
/// where it EXISTS rather than inferred where it does not.
///
/// It is written for an admitted transition **and for a refused one**, so
/// the attributed trail is complete on its own: "which peer tried to
/// approve what, and was told no" is answerable from this event type
/// alone, without joining it to a refusal the registry never saw (a
/// transition refused at the seam never reaches the registry at all).
type ModelArtifactTransitionAttributedPayload = {
    CompositeKeyHash: string
    /// Status the artifact held when the seam judged. Present even on a
    /// refusal, except an `UnknownArtifact` one where there is no
    /// artifact to have a status — `""` there.
    FromStatus: string
    /// Status the author asked the artifact to enter.
    ToStatus: string
    /// Where the invocation entered this deployment: `"local"` or
    /// `"peer"`. A closed two-value vocabulary — a policy verdict is
    /// authored data-side, so it arrives on the local channel and is
    /// distinguished by `AuthorKind`, not by a third channel.
    Channel: string
    /// What kind of author judged: `"user"` / `"peer"` / `"policy"`.
    AuthorKind: string
    /// The author's identity, in the form its kind implies — a user id, a
    /// `{peerId}/{actorId}` pair, or a policy id.
    AuthorId: string
    /// The author's stated reason. `""` when none was given; a rationale
    /// is optional on the wire and this trail does not invent one.
    Rationale: string
    /// Did the transition land? `false` carries `Refusal`.
    Admitted: bool
    /// The seam's refusal, described. `""` on an admitted transition.
    Refusal: string
    ScopeId: string
}

/// Phase 646 — opaque provenance attachments were appended to a model
/// artifact, and (where a promotion was accepted) the acceptance signature
/// recorded. Reserved `SourceModule = "_platform.audit"`.
///
/// **Hashes and media types, never bytes.** The attachment content is
/// opaque by construction — forge does not read it, so an audit trail that
/// carried it would be publishing a payload this deployment cannot
/// characterise into a store with a different retention policy from the
/// artifact's. The digest is what a later investigation actually needs: it
/// resolves to the attachment, or it does not resolve at all, and either
/// answer is the one being asked for.
type ModelArtifactProvenanceAttachedPayload = {
    CompositeKeyHash: string
    /// Digests of the attachments this call ADDED. Empty when the call
    /// only recorded a signature.
    AttachmentHashes: string list
    /// The distinct media types added, in arrival order.
    MediaTypes: string list
    /// How many attachments the artifact holds after the append, and how
    /// many bytes — the two dimensions the declared cap bounds, recorded so
    /// an operator can see an artifact approaching one.
    TotalAttachments: int
    TotalBytes: int
    /// The signing-key id of the acceptance signature recorded by this
    /// call, or the one already held. `""` when the artifact carries none
    /// — an artifact this deployment fitted itself, or a promotion
    /// accepted with no signer composed.
    SigningKeyId: string
    ScopeId: string
}

/// Phase 646 — a promotion transfer JUDGED at the transfer seam: a final
/// artifact, its spec payload and its provenance attachments landing in
/// this deployment's registry as one recorded act. Reserved
/// `SourceModule = "_platform.audit"`.
///
/// **Why this is its own row rather than the attributed transition row
/// plus an attachment row.** A promotion is one act with one outcome, and
/// the question it has to answer later is "did this data host accept this
/// artifact from this peer, and does it still hold what it accepted". Read
/// off two rows written by two layers, that question needs a join on a key
/// neither row was designed to correlate on — and a refused transfer writes
/// no attachment row at all, so the join would silently lose exactly the
/// cases worth finding.
///
/// Written for an accepted transfer AND for a refused one, for the reason
/// `ModelArtifactTransitionAttributedPayload` is: a transfer refused at the
/// seam never reaches the registry, so a trail of successful writes could
/// not answer which peer tried to promote what.
type ModelArtifactPromotionPayload = {
    CompositeKeyHash: string
    /// The lifecycle status the transfer asked the artifact to hold.
    TargetStatus: string
    /// Where the transfer entered this deployment: `"local"` or `"peer"`.
    Channel: string
    /// `"user"` / `"peer"` / `"policy"` — `ModelTransitionAuthor.kind`.
    AuthorKind: string
    AuthorId: string
    /// Digests of every attachment the transfer carried.
    AttachmentHashes: string list
    /// The signing-key id of the acceptance signature. `""` when the
    /// transfer was refused, or accepted with no signer composed.
    SigningKeyId: string
    /// Did the transfer land? `false` carries `Refusal`.
    Accepted: bool
    /// The identical transfer was already held; nothing was written. An
    /// accepted replay, which is a different fact from a first acceptance
    /// and is the one an idempotency question is about.
    Replayed: bool
    /// The seam's refusal, described. `""` on an accepted transfer.
    Refusal: string
    ScopeId: string
}

// ─── Phase 454 — model-scoring audit payloads ──────────────────────────
//
// A scoring run applies a governed artifact (Phase 453) to a new dataset
// vintage (Phase 448), landing predictions as a NEW dataset version. Both
// the success and the typed refusals are audited under `_platform.audit`
// (GP 6) carrying the artifact's composite-key hash, so an operator can
// reconstruct which artifact scored which vintage into which output — and
// which scores were refused and why — from the trail alone. PII-free:
// identity + cardinality only; no dataset rows, no artifact bytes, no
// prediction values travel.

/// A scoring run produced predictions — a new dataset version whose
/// provenance names the scoring artifact + input vintage. Reserved
/// `SourceModule = "_platform.audit"`.
type ModelScoredPayload = {
    /// SHA-256 hex of the scoring artifact's composite identity (plan D5).
    CompositeKeyHash: string
    /// Resolved provider `Kind` that produced the predictions.
    ProviderId: string
    /// Provider version — a component of the artifact's composite identity.
    ProviderVersion: string
    /// `{scopeId}/{datasetId}@v{version}` of the input vintage scored.
    InputVersion: string
    /// `{scopeId}/{datasetId}@v{version}` of the predictions dataset version
    /// the run wrote.
    OutputVersion: string
    /// Number of prediction rows written. Cardinality only.
    RowCount: int64
    /// Scope the score ran under.
    ScopeId: string
}

/// A scoring run was refused as typed data — the approved-only guard
/// rejected a non-`Approved` artifact (task C / GP 4), the input schema
/// lacked a provider-required column, the input vintage was unavailable, or
/// the provider raised. The refusal is itself audit-worthy (repeated denials
/// are a governance-gate signal, like `ModelArtifactTransitionDenied`).
/// Reserved `SourceModule = "_platform.audit"`.
type ModelScoreRefusedPayload = {
    CompositeKeyHash: string
    /// Resolved provider `Kind` from the artifact's composite identity.
    ProviderId: string
    /// `{scopeId}/{datasetId}@v{version}` of the input vintage the caller
    /// asked to score.
    InputVersion: string
    /// Why the score was refused (the `ScoreError` case name + detail).
    Reason: string
    ScopeId: string
}

// ─── Phase 456 — model-evaluation audit payload ────────────────────────
//
// A holdout-evaluation run scores an artifact (Phase 454) against a holdout
// vintage and stores the provider-computed metric map against the artifact
// (plan Stage 6). The run is audited under `_platform.audit` (GP 6) carrying
// the artifact's composite-key hash + both vintage keys, so an operator can
// reconstruct the out-of-time track record's provenance from the trail
// alone. PII-free: identity + cardinality only; no metric values, no
// dataset rows travel (forge stores metrics in the run record — the audit
// row names the run, it never re-states provider numbers).

/// A holdout-evaluation run stored a provider-computed metric map against a
/// model artifact (plan Stage 6). Reserved `SourceModule = "_platform.audit"`.
type ModelEvaluatedPayload = {
    /// SHA-256 hex of the evaluated artifact's composite identity (plan D5).
    CompositeKeyHash: string
    /// Resolved provider `Kind` that computed the metrics.
    ProviderId: string
    /// Provider version — a component of the artifact's composite identity.
    ProviderVersion: string
    /// `{scopeId}/{datasetId}@v{version}` of the holdout vintage evaluated.
    HoldoutVersion: string
    /// `{scopeId}/{datasetId}@v{version}` of the predictions vintage the
    /// scoring leg wrote.
    PredictionsVersion: string
    /// Number of metrics the provider reported. Cardinality only — the
    /// values live in the stored `EvaluationRun`, never on the audit row.
    MetricCount: int
    /// Scope the evaluation ran under.
    ScopeId: string
}

// ─── Phase 645 — promotion-policy audit payloads ───────────────────────
//
// A declared promotion policy judged a newly registered artifact and either
// promoted it, held it for human curation, or refused it. Both rows are
// emitted under `_platform.audit` (GP 6) and together they ARE the
// subscription surface for promotion events — a consumer that wants to
// react attaches an `IAuditSink` rather than a bespoke pub/sub.
//
// PII-free, and metric-value-free: identity, the verdict, and cardinality
// only. The per-metric numbers a verdict rested on live in the stored
// `PromotionDecision`, exactly as `ModelEvaluated`'s live in the stored
// `EvaluationRun` — an audit row names the judgment, it never re-states
// provider numbers.

/// Phase 645 — a promotion policy reached a verdict for a model artifact.
/// Written for EVERY verdict, including a queue (which moves nothing and so
/// leaves no transition row of its own) and including the fail-safe "no
/// policy governed this artifact" case. Reserved
/// `SourceModule = "_platform.audit"`.
type ModelPromotionPolicyEvaluatedPayload = {
    /// SHA-256 hex of the judged artifact's composite identity (plan D5).
    CompositeKeyHash: string
    /// The policy that judged. `""` when no declared policy governed the
    /// artifact — the honest value, rather than a policy id it never had.
    PolicyId: string
    /// The judging policy's declared version. `0` when none governed.
    PolicyVersion: int
    /// Where the judged metrics came from: `"diagnostics"` /
    /// `"latest-evaluation"`, or `""` when no policy governed.
    MetricSource: string
    /// `"AutoPromote"` / `"QueueForCuration"` / `"Reject"`.
    Verdict: string
    /// One-line reason, always populated.
    Reason: string
    /// The currently-approved artifact the candidate was judged against.
    /// `""` when there was no incumbent.
    IncumbentKeyHash: string
    /// How many declared tolerances were evaluated. Cardinality only — the
    /// per-tolerance evidence lives in the stored decision record.
    ToleranceCount: int
    /// Did the verdict's transition land? `false` for a queue (which drives
    /// none) and for one the transition seam refused.
    TransitionApplied: bool
    /// Scope the decision was made under.
    ScopeId: string
}

/// Phase 645 — an auto-promotion displaced a previously promoted artifact.
///
/// **A separate row because supersession is a separate fact.** The promotion
/// itself is already an attributed transition (Phase 644) and the retirement
/// is another, but neither says the two are the same act — and "no refresh
/// ever silently changes what a consumer resolves" is a claim about exactly
/// that link. Reserved `SourceModule = "_platform.audit"`.
type ModelArtifactSupersededPayload = {
    /// The newly promoted artifact's composite-key hash.
    SupersedingKeyHash: string
    /// The artifact it displaced.
    SupersededKeyHash: string
    /// The policy whose verdict justified the supersession.
    PolicyId: string
    /// How many metrics had both an observed and an incumbent value — the
    /// deltas that justified it. Cardinality only; the values live in the
    /// stored decision record.
    MetricCount: int
    /// Did the displaced artifact actually retire? `false` when the
    /// retirement was refused, which leaves two approved artifacts and is
    /// precisely the state an operator must be told about.
    Retired: bool
    ScopeId: string
}

/// Phase 651 — a registration observer raised, and the failure was isolated.
///
/// **The row exists because the isolation is otherwise invisible.** An
/// observer runs after the artifact is durably registered, so its failure
/// changes nothing the registrar can see: the registration returns `Ok`, the
/// caller carries on, and whatever the observer existed to do — apply a
/// promotion policy, notify a downstream — silently did not happen. Swallowing
/// that quietly would make "observe, don't gate" indistinguishable from
/// "observers sometimes do not run". Reserved
/// `SourceModule = "_platform.audit"`.
type ModelRegistrationObserverFailedPayload = {
    /// SHA-256 hex of the registered artifact's composite identity (plan D5).
    /// The registration itself stands — this row is about the observer.
    CompositeKeyHash: string
    /// `IModelRegistrationObserver.Name` of the observer that raised. Naming
    /// it is the whole value of the row: "an observer failed" is not
    /// actionable when several are composed.
    Observer: string
    /// The exception's message, one line. Type + message only — no stack, no
    /// payload values.
    Reason: string
    /// Scope the registration was made under.
    ScopeId: string
}

// --- Phase 482 / 487 — dataset provenance & virtual-spill audit payloads --
//
// Emitted under `_platform.audit`. Identity + cardinality only — no dataset
// rows, no label content values beyond the closed DU shape travel.

/// Phase 487 — an ephemeral materialisation ("spill") of a **virtual**
/// dataset version was written to a retention-bounded scratch blob for
/// compute handoff. Virtual versions read through to the deployment's own
/// stores with no durable copy; a spill is the declared, observable
/// exception to zero-copy — always audited so the copy is never silent.
type DatasetSpillCreatedPayload = {
    /// Actor that requested the handoff materialisation.
    Actor: string
    ScopeId: string
    /// Scratch dataset id the spill landed under.
    SpillDatasetId: string
    /// The virtual version's watermark — its vintage identity.
    Watermark: string
    /// UTC instant after which the spill is eligible for deletion.
    ExpiresAt: DateTime
    /// Rows spilled. Cardinality only.
    RowCount: int64
}

/// Phase 487 — a spill blob was deleted (TTL reached, or explicit cleanup).
/// Closes the spill lifecycle in the trail so a leaked scratch copy is
/// visible by its absence of a matching delete row.
type DatasetSpillDeletedPayload = {
    Actor: string
    ScopeId: string
    SpillDatasetId: string
    /// Why deleted — `"ttl-expired"` / `"explicit"`.
    Reason: string
}

/// Phase 482 — privacy-provenance labels were removed from a dataset version
/// by an explicit admin act (the **only** removal path; labels are otherwise
/// immutable provenance). Declassification writes a new, unlabelled version;
/// this row records the actor, both version numbers, and the justification so
/// the removal is accountable (GP 4 / GP 6).
type DatasetDeclassifiedPayload = {
    /// Owner / Admin who declassified.
    Actor: string
    ScopeId: string
    DatasetId: string
    /// The labelled version whose labels were cleared.
    FromVersion: int
    /// The new, unlabelled version created by the declassify.
    ToVersion: int
    /// Count of labels removed. Cardinality only.
    LabelCount: int
    /// Operator-supplied justification.
    Reason: string
}

/// Phase 482 — a label-carrying dataset version was refused a dispatch or a
/// raw export by an enabled data-provenance policy (GP 4 / GP 6). A typed,
/// audited denial — repeated denials are a governance-gate signal, like
/// `ModelArtifactTransitionDenied`.
type DatasetPolicyDeniedPayload = {
    ScopeId: string
    DatasetId: string
    Version: int
    /// Which policy fired — `"dispatch"` (labelled data needs Isolated
    /// compute) or `"export"` (raw export of label-carrying content).
    Policy: string
    /// Human-readable refusal reason.
    Reason: string
}

/// Phase 601 — an assembly re-vintage ran: the spec recorded on a produced
/// version was re-executed against current sources, landing new immutable
/// version(s). One row per replay carrying the spec ref + the produced
/// versions (GP 6). Reserved `SourceModule = "_platform.audit"`.
type DatasetRevintagedPayload = {
    /// SHA-256 hex of the re-bound spec that actually ran.
    SpecHash: string
    /// `{scopeId}/{datasetId}@v{version}` key of the spec-carrying version
    /// the replay was triggered from.
    SourceVersion: string
    /// `{scopeId}/{datasetId}@v{version}` keys of the produced version(s),
    /// one per subset.
    ProducedVersions: string list
    /// Actor (or the job's system principal) that requested the replay.
    RequestedBy: string
    ScopeId: string
}
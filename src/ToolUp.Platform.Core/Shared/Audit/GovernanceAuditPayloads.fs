// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of AuditTypes.fs: signed module artefact, synthetic
// sample, schema-only access, grant policy, dual-control admin mutation and
// consented-grant registry payload records.
// Declaration order is unchanged from the monolith; same namespace, same
// record names — only the file boundary moved. The `AuditEvent` union that
// registers these payloads stays in AuditTypes.fs, append-only.

// ─── Phase 30a — signed module artefact audit payloads ───────────────
//
// Emitted by `IArtifactSigner.Sign` (hub-side) and
// `IArtifactVerifier.Verify` (edge-side). Reserved `SourceModule =
// "_platform.artefacts"` (see `ArtifactsSourceModule.value` in
// `Shared/Types/ArtifactTypes.fs`). PII-free — payloads carry the
// publisher key id (NEVER the private key bytes), the module id +
// artefact version (manifest-supplied), and a reason on `Rejected`.
//
// Phase 625 — these three payloads and their `AuditEvent` cases were
// renamed `Artifact*` -> `ModuleArtefact*`. They were a one-letter
// homograph of the Phase 40 `_platform.signing` family
// (`ArtefactSignedPayload` / `AuditEvent.ArtefactSigned`), which is a
// DIFFERENT event about a DIFFERENT subject, and neither the compiler
// nor a reviewer distinguishes `Artifact`/`Artefact` reliably. The
// `Module` qualifier makes the two families a word apart rather than a
// vowel apart, and adopts the estate's `artefact` house spelling.
//
// The WIRE IS UNCHANGED: `AuditEvent.eventTypeName` still returns the
// historical `"ArtifactSigned"` / `"ArtifactVerified"` /
// `"ArtifactRejected"` discriminators. See the decision record at
// `auditEventCodecs` in `Server/AuditLog.fs`. Record FIELD names are
// likewise untouched (they ARE serialised); only the F#-facing type and
// case identifiers moved.

/// `IArtifactSigner.Sign` succeeded. Records who signed (the actor that
/// invoked the signer), which publisher key was used (id only — never
/// the private key), and the manifest's module / version identity.
///
/// Wire `EventType` is the historical `"ArtifactSigned"` (Phase 625).
type ModuleArtefactSignedPayload = {
    /// Actor who invoked the signer (typically `"_hub"` for an
    /// automated publish pipeline; the authenticated user's id for
    /// operator-initiated signs).
    Actor: string
    /// `PublisherKeyId.value` of the key used to sign. The signer NEVER
    /// records the private key bytes in the audit trail.
    PublisherKeyId: string
    /// `ArtifactManifest.ModuleId` — the module identity the signed
    /// artefact installs as.
    ModuleId: string
    /// `ArtifactManifest.Version` — SemVer string.
    ArtifactVersion: string
}

/// `IArtifactVerifier.Verify` returned `ArtifactValidation.Ok` (signature
/// valid + publisher key trusted at the edge).
///
/// Wire `EventType` is the historical `"ArtifactVerified"` (Phase 625).
type ModuleArtefactVerifiedPayload = {
    /// `PublisherKeyId.value` of the publisher whose signature was
    /// validated.
    PublisherKeyId: string
    /// `ArtifactManifest.ModuleId`.
    ModuleId: string
    /// `ArtifactManifest.Version`.
    ArtifactVersion: string
}

/// `IArtifactVerifier.Verify` returned `ArtifactValidation.Error reason`.
/// Recorded as a separate case from `ModuleArtefactVerified` so operator
/// dashboards can target refusal rates without scanning every verify
/// row.
///
/// Wire `EventType` is the historical `"ArtifactRejected"` (Phase 625) —
/// deliberately pinned, because `CefFormatter`'s `highEvents` severity
/// set and operator-owned SIEM rules key on that exact string.
type ModuleArtefactRejectedPayload = {
    /// `PublisherKeyId.value` from the manifest. `None` when the
    /// rejection happened before the key id could be parsed (corrupt
    /// manifest, decode failure).
    PublisherKeyId: string option
    /// `ArtifactManifest.ModuleId` when the manifest decoded; empty
    /// string when the rejection happened before the manifest could be
    /// read.
    ModuleId: string
    /// `ArtifactManifest.Version` when the manifest decoded; empty
    /// string otherwise.
    ArtifactVersion: string
    /// Operator-readable refusal reason. Mirrors the
    /// `ArtifactValidation.Error reason` string verbatim:
    /// `"untrusted publisher"`, `"signature mismatch"`,
    /// `"manifest hash mismatch"`, or a sink-specific message.
    Reason: string
}

/// Phase 30d — a `ModulePermission.SchemaOnly` partner-sandbox caller
/// invoked `IDataCatalog.GetSyntheticSample` successfully. Recorded
/// under `_platform.audit` with `SourceModule` derived per call site.
/// Volume note: high-cardinality for chatty partner integrations
/// (each iteration loop call emits a row), so the payload is
/// metadata-only — no synthetic bytes travel through the trail. The
/// `Seed` field IS recorded so a deployment can prove an
/// exfiltration-style "partner generated thousands of differing
/// seeds" pattern after the fact.
type SyntheticSampleGeneratedPayload = {
    /// Acting `AccessContext.UserId` at the moment of the call. Pinned
    /// to a real principal — anonymous callers should never reach this
    /// path (the gating layer refuses them earlier).
    UserId: string
    /// `DataTypeId` the sample was generated for.
    TypeId: string
    /// Number of rows requested. May exceed the configured per-partner
    /// cap; in that case the actual emitted row count is
    /// `min(requested, cap)` (see `EmittedCount`).
    RequestedCount: int
    /// Actual rows emitted after the per-partner cap clamped the
    /// request. Always `<= RequestedCount`.
    EmittedCount: int
    /// Seed passed by the caller. Recorded verbatim so forensic
    /// review can detect "thousands of differing seeds in one
    /// session" exfiltration patterns.
    Seed: int
    /// Per-partner cap that was applied (from
    /// `_platform.notification_prefs.schemaOnly.maxSampleRows` or the
    /// SDK default when unset).
    AppliedMaxRows: int
}

/// Phase 30d — a `ModulePermission.SchemaOnly` caller attempted to
/// access a real-row API path; the substrate refused before any real
/// data was read. Emitted by every shield site so a deployment can
/// dashboard "refusal rate by partner" as a leading-indicator metric
/// for credential leak / misconfiguration / hostile activity. Distinct
/// from `SurfaceDenied` (Phase 66 `SurfaceEnforcementMiddleware` —
/// surface-level deny) because the SchemaOnly refusal happens at the
/// substrate / handler layer, not at the route surface.
type SchemaOnlyAccessAttemptedPayload = {
    /// Acting `AccessContext.UserId`. Required for forensics —
    /// anonymous callers do not reach this path (the surface refuses
    /// them earlier).
    UserId: string
    /// Module name the caller was attempting to read. Mirrors the
    /// `IPermissionStore` key shape so admin queries can correlate
    /// refusals with team-permission grants.
    ModuleName: string
    /// Short stable label for the substrate path that fired the
    /// refusal — `"IDataObjectStore.Get"`, `"IDataObjectStore.ListObjects"`,
    /// `"IDataCatalog.ListObjects"`, `"IFileManagementApi.GetFileContent"`,
    /// etc. Operator dashboards group refusals by call site so a
    /// regressed shield is visible immediately.
    AttemptedPath: string
    /// Best-effort identifier for the requested resource (object id,
    /// file name, scope id, etc.). Empty string when the refusal
    /// happened before any identifier could be resolved.
    AttemptedResource: string
}

/// Phase 551 — a grant WRITE was refused because it did not satisfy the
/// target module's declared `GrantPolicy`. Emitted by the grant-policy
/// write guard before anything is persisted, so the refusal is visible
/// even though no state changed (GP 6). Its dispatch-time twin is
/// `UnconsentedGrantRefused` — the two are deliberately separate events
/// because they answer different questions: this one says an admin tried
/// to create authority the module does not admit, that one says
/// authority already recorded is not being honoured.
type GrantPolicyRefusedPayload = {
    /// The administrator attempting the grant.
    ActorId: string
    /// The subject the grant was being written for. Empty when the write
    /// was a whole-document replacement with no single subject.
    SubjectId: string
    /// Module the grant targeted. Mirrors the `IPermissionStore` /
    /// `AccessContext.ModulePermissions` key shape — the SAME key the
    /// module declared its policy under, so no second naming axis exists
    /// to drift.
    ModuleName: string
    /// The module's declared policy, as its stable wire token.
    DeclaredPolicy: string
    /// Stable refusal discriminator (`GrantRefusal.code`) — the field an
    /// operator dashboard groups by.
    RefusalCode: string
}

/// Phase 551 — a request was refused at DISPATCH because the caller's
/// permission entry on a policy-bearing module carried no live grant
/// record. This is the control that survives a grant row written
/// straight into the store: the write guard can be bypassed, the
/// dispatch check cannot (Phase 311 lesson). Distinct from
/// `SurfaceDenied` (route surface) and from `SchemaOnlyAccessAttempted`
/// (substrate read) — this fires at the module-access gate.
type UnconsentedGrantRefusedPayload = {
    /// Acting `AccessContext.UserId`.
    UserId: string
    /// Module whose routes were refused.
    ModuleName: string
    /// The module's currently declared policy, as its stable wire token.
    DeclaredPolicy: string
    /// Why the grant was inert — `"no-grant-record"`,
    /// `"awaiting-subject-consent"`, `"evidence-below-declared-policy"`,
    /// or `"counterparty-approval-unavailable"`
    /// (`GrantPolicy.inertReason`). A dashboard separating the first from
    /// the second separates suspected injection from ordinary pending
    /// consent.
    InertReason: string
}

/// Phase 730 — a grant on a policy-bearing module was RECORDED. The
/// success twin of `GrantPolicyRefused`, and the reason it exists is that
/// Phase 551 shipped only the refusal half: every grant a policy turned
/// down was dashboardable and every grant it ADMITTED was invisible, so
/// the audit trail could answer "what was blocked" and not "who was given
/// access to what" — which is the question a grant trail is for (GP 6).
///
/// Emitted from the same choke point as its refusal twin — the
/// `GrantPolicyPermissionStore` decorator, on the delta of a write that
/// SUCCEEDED — so no caller has to remember to emit it and no write path
/// can acquire authority silently.
///
/// **Scope, stated because the narrower reading is deliberate.** This
/// fires only for modules declaring a policy stricter than
/// `AdminDiscretion`, exactly like the refusal. A deployment that declares
/// no policy composes no decorator and its audit stream is byte-for-byte
/// its pre-730 self (GP 11). Ordinary `AdminDiscretion` permission changes
/// remain the business of `PermissionChanged`; this event is about the
/// governed set, and widening it would bury the governed rows in the
/// volume of routine ones.
type GrantRecordedPayload = {
    /// The administrator who performed the grant.
    ActorId: string
    /// The subject who now holds (or is recorded pending on) the module.
    SubjectId: string
    /// Module granted. The same key the module declared its policy under
    /// and the same key `AccessContext.ModulePermissions` uses — one
    /// naming axis, so nothing can drift.
    ModuleName: string
    /// The module's declared policy, as its stable wire token. Joins this
    /// row to its refusal twin.
    DeclaredPolicy: string
    /// `GrantState.toToken` — `"active"` or `"pending-consent"`. **The
    /// load-bearing field.** "Authority now exists" and "authority is
    /// recorded and confers nothing until the subject accepts" are
    /// different facts with different urgency, and without this they are
    /// the same row.
    State: string
    /// The permissions granted, as their stable tokens, comma-separated in
    /// a stable order. A grant of `Admin` and a grant of `Read` are not the
    /// same event to a reviewer.
    Permissions: string
    /// The justification the policy demanded. Recorded because a grant
    /// trail without the stated reason answers "who" and not "why", and
    /// the whole point of `RequiresAcknowledgement` is that a reason was
    /// given. Admin-authored, like `AdminMutationProposedPayload.Summary`;
    /// empty under a policy that demands none.
    Justification: string
}

// ─── Phase 555 — dual control for sensitive admin mutations ──────────
//
// Five events, one per act in the ceremony, because an operator asking
// "what is queued", "who approved what", "what was turned down", "what
// was attempted and structurally refused" and "what lapsed unreviewed"
// is asking five different questions with five different responses. A
// single `AdminMutationDecided` row carrying an outcome field would make
// the fourth question — the one that is a security signal rather than an
// operations signal — a filter over the others.
//
// Every row carries `RequestId` and (except the refusal, where the
// payload may not be readable) `Fingerprint`, so the propose→decide pair
// joins on an identity that binds to the exact bytes proposed rather
// than to a mutable id.

/// Phase 555 — a gated admin mutation was captured as a pending record
/// and did NOT apply. The first half of the two-person ceremony: this
/// row means authority was proposed, not created.
type AdminMutationProposedPayload = {
    /// The pending record's opaque identifier — the string an approver
    /// names, and the join key to the decision row.
    RequestId: string
    /// The team whose permission document the mutation targets.
    TeamId: string
    /// The administrator who proposed it. Never empty: an unattributable
    /// write is refused rather than parked, so no proposal row can be
    /// anonymous.
    ProposerId: string
    /// `AdminMutationKind.toToken` — what class of write is queued.
    MutationKind: string
    /// SHA-256 over the captured mutation. Binds this row to the exact
    /// payload, so a decision row naming the same fingerprint provably
    /// decided the same change.
    Fingerprint: string
    /// The operator-facing one-liner the approver will be shown.
    Summary: string
    /// When the proposal lapses if nobody decides it.
    ExpiresAtUtc: DateTimeOffset
}

/// Phase 555 — a second, distinct administrator approved a pending
/// mutation. `Applied` distinguishes "approved and the underlying write
/// succeeded" from "approved and the underlying store then refused it" —
/// a distinction an approver cannot see and an auditor must.
type AdminMutationApprovedPayload = {
    RequestId: string
    TeamId: string
    /// The administrator who proposed it.
    ProposerId: string
    /// The administrator who approved it. Structurally never equal to
    /// `ProposerId` — that is the control.
    ApproverId: string
    MutationKind: string
    Fingerprint: string
    /// Did the approved mutation actually land? `false` means the
    /// approval was valid and the underlying store refused the write
    /// (storage failure, or a Phase 551 grant-policy refusal evaluated
    /// against a document that moved since the proposal).
    Applied: bool
}

/// Phase 555 — a second administrator deliberately turned a pending
/// mutation down. Distinct from `AdminMutationApprovalRefused`: this is a
/// decision, that is a refused attempt.
type AdminMutationRejectedPayload = {
    RequestId: string
    TeamId: string
    ProposerId: string
    /// The administrator who rejected it.
    ApproverId: string
    MutationKind: string
    Fingerprint: string
    /// The reason the rejecting administrator gave. May be empty.
    Reason: string
}

/// Phase 555 — an approval ATTEMPT was structurally refused. The
/// security-signal row of the family: a proposer trying to approve their
/// own proposal, an attempt on a lapsed record, or an attempt on a
/// request that does not exist all land here rather than being invisible
/// because nothing changed.
type AdminMutationApprovalRefusedPayload = {
    RequestId: string
    TeamId: string
    /// The proposer, where the record was readable. Empty when the
    /// request was not found.
    ProposerId: string
    /// Who attempted the approval.
    AttemptedApproverId: string
    /// `AdminMutationKind.toToken`, or empty when the record was not
    /// readable.
    MutationKind: string
    /// Stable refusal discriminator (`AdminMutationRefusal.code`) — the
    /// field an operator dashboard groups by. `self-approval-refused` is
    /// the one worth alerting on.
    RefusalCode: string
}

/// Phase 555 — a pending mutation lapsed without a decision and was
/// swept. Emitted at the moment the record is discarded, so the trail
/// shows a proposal ending rather than merely stopping.
type AdminMutationExpiredPayload = {
    RequestId: string
    TeamId: string
    ProposerId: string
    MutationKind: string
    Fingerprint: string
    /// When it lapsed.
    ExpiredAtUtc: DateTimeOffset
}

// ─── Phase 552 — the consented-grant registry ────────────────────────
//
// Four events, and the split is the same one the record type makes
// (`ConsentDenial.isTrustFailure`): three lifecycle acts, and ONE alert.
//
// The three acts are separate rather than one `GrantConsentChanged` row
// with a status field because they are three different reviews. "What
// has a counterparty been asked to approve" is an operations queue;
// "what did a counterparty approve, and over what bytes" is the evidence
// an assurance reader samples; "what has been withdrawn" is the one that
// must be reconcilable against access that stopped. A status field would
// make each a filter over the other two's volume.
//
// The fourth — `GrantConsentVerificationDenied` — fires ONLY on a trust
// failure: a signature that does not validate, a key nobody registered,
// an algorithm downgrade, a record filed against the wrong subject or
// party. It deliberately does NOT fire on an ordinary lifecycle denial
// (revoked, expired, not yet approved), because those are already fully
// described by the `UnconsentedGrantRefused` row the dispatch refusal
// emits, and drowning a forgery alert in the volume of ordinary
// revocations is exactly how a forgery alert stops being read.

/// Phase 552 — a consent record was lodged in the registry awaiting the
/// counterparty. Authority was REQUESTED, not created: a proposal
/// confers nothing at dispatch.
type GrantConsentProposedPayload = {
    /// The lodged record's opaque id — the join key to the approval or
    /// revocation that later supersedes it.
    ConsentId: string
    TeamId: string
    /// The principal the grant would be for.
    SubjectId: string
    /// The module the grant targets. Mirrors the `IPermissionStore` /
    /// `AccessContext.ModulePermissions` key, so a consent row joins to a
    /// grant row with no second naming axis to drift.
    ModuleName: string
    /// The counterparty whose approval the module's declared
    /// `GrantPolicy` requires (`PartyRef.value`).
    Party: string
    /// The signing key id the record presents. Recorded even on a
    /// proposal so a later "which key signed what" review needs no
    /// payload reads.
    KeyId: string
    /// Who lodged the record with the deployment — never the signer. The
    /// signer is proved by the signature under `Party`.
    RecordedBy: string
    /// When an eventual approval would lapse, if the record carries an
    /// expiry.
    ExpiresAtUtc: DateTimeOffset option
}

/// Phase 552 — a counterparty's signed approval was accepted into the
/// registry. This is the row that says authority became grantable, and
/// it is the one an assurance reader samples: it names the exact record
/// approved and the proposal it supersedes.
type GrantConsentApprovedPayload = {
    ConsentId: string
    TeamId: string
    SubjectId: string
    ModuleName: string
    Party: string
    KeyId: string
    RecordedBy: string
    /// The `ConsentId` this approval answers. `""` when the approval was
    /// lodged without a preceding proposal (legitimate for an
    /// out-of-band agreement recorded in one act).
    Supersedes: string
    ExpiresAtUtc: DateTimeOffset option
}

/// Phase 552 — a consent was withdrawn. A revocation is a new record,
/// never a row delete, so this row and the approval it supersedes both
/// stand in the trail.
///
/// **Its operational meaning is immediate**: the dispatch check resolves
/// consent on use, so the next call against the affected module refuses.
/// There is no sweep to wait for and no cache to invalidate, which is
/// what makes this row reconcilable against access actually stopping.
type GrantConsentRevokedPayload = {
    ConsentId: string
    TeamId: string
    SubjectId: string
    ModuleName: string
    Party: string
    KeyId: string
    RecordedBy: string
    /// The `ConsentId` being withdrawn.
    Supersedes: string
}

/// Phase 552 — something presenting itself as consent failed VERIFICATION
/// (`ConsentDenial.isTrustFailure`). The security signal of the family.
///
/// Distinct from `UnconsentedGrantRefused`, which fires at the module
/// access gate for every inert grant including the ordinary ones. This
/// one fires only where a record exists and is not what it claims: a
/// signature that does not validate over the canonical payload, a key id
/// nobody registered for the party, a declared algorithm disagreeing
/// with the registered key's, or a record filed against a different
/// subject or party. Any of those on a production deployment means a
/// forged or replayed artifact, not a policy outcome.
type GrantConsentVerificationDeniedPayload = {
    /// The record's id, or `""` when nothing readable was presented.
    ConsentId: string
    TeamId: string
    SubjectId: string
    ModuleName: string
    /// The party the MODULE requires approval from — the expectation,
    /// not the record's self-assertion.
    Party: string
    /// The key id the record presented. `""` when unreadable.
    KeyId: string
    /// The algorithm the record DECLARED. Recorded because a value
    /// disagreeing with the registered key's is the downgrade attempt
    /// itself, and it is invisible once the refusal is reduced to a code.
    DeclaredAlgorithm: string
    /// Stable denial discriminator (`ConsentDenial.code`).
    DenialCode: string
}
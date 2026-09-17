// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of AuditTypes.fs: certificate, deployment
// verification, evidence-chain, gated-media key delivery, blob-storage auth,
// cross-module AI read and backup / restore-drill payload records.
// Declaration order is unchanged from the monolith; same namespace, same
// record names — only the file boundary moved. The `AuditEvent` union that
// registers these payloads stays in AuditTypes.fs, append-only.

/// Phase 685 — one grounding certificate was issued.
///
/// **The issuance log is what makes a certificate enumerable.** A holder
/// has always been able to verify the certificate in their hand; nobody
/// could ask the other question — *what has this deployment certified?* —
/// and a certificate that was issued and then quietly disowned left no
/// trace at all. One row per issuance turns the audit trail into the
/// deployment's own certificate log, and under a chained ledger that log
/// is tamper-evident: a suppressed issuance is a break the chain verifier
/// positions, not an absence nobody can see.
///
/// **Identifiers only — never the body.** A digest, the subject content
/// id, the signing-key id, and which seal was used. That is deliberate
/// and it is the whole reason this row is safe to keep: the certificate
/// body carries a provenance chain filtered through the disclosure
/// predicate, and copying any of it onto an audit row would move that
/// content to a surface the predicate never ran at. The digest is
/// sufficient for inclusion — a holder recomputes it from the bytes they
/// hold — and insufficient for anything else, which is exactly the
/// property wanted.
type CertificateIssuedPayload = {
    /// Lowercase-hex SHA-256 over the certificate's canonical signed bytes
    /// — the same digest a holder recomputes from the document they hold,
    /// so an inclusion check needs nothing from the issuer to run.
    Digest: string
    /// The subject the certificate is issued over: the answer message id
    /// or the fact content id at the chain root.
    Subject: string
    /// The signing-key id bound into the signed body. Names WHICH key
    /// sealed it, so a rotation leaves the log still readable.
    KeyId: string
    /// Which issue path sealed it: `"detached-jws"` (the direct
    /// `IArtefactSigner` path) or `"application-seal"` (the attested path,
    /// whose envelope also carries the purpose and attestation level).
    /// A discriminator rather than a lookup, because the two make
    /// different claims and an enumerator should not have to fetch the
    /// document to tell them apart.
    Seal: string
    /// The certificate interchange format version the body declared.
    Format: string
    OccurredAt: DateTimeOffset
}

/// Phase 686 — one run of the deployment verification report.
///
/// **Verification leaves a trace without mutating anything.** The report
/// reads five verifiers and writes nothing back; this row is the only
/// artefact it produces. Recording it turns "who has checked this
/// deployment, and what did it say when they did" into a question the
/// trail answers — and under a chained ledger the answer is
/// tamper-evident, so a run whose findings someone would rather nobody
/// saw cannot be quietly removed.
///
/// **The digest, not the report.** The row carries the verdict digest
/// and the per-section verdict labels, never the section detail. That is
/// deliberate: the detail names ledger positions, envelope digests and
/// certificate counts — a deployment-wide evidence summary — and the
/// audit trail has its own readership and its own export paths. The
/// digest is sufficient to prove two runs said the same thing, and
/// insufficient to be a second copy of the report.
///
/// **Recorded on every outcome, including the clean one.** A row written
/// only when something failed cannot distinguish a deployment nobody
/// checked from one that was checked and was fine — the Phase 657
/// discipline, and those are the two states an assessor most needs to
/// tell apart.
type DeploymentVerifiedPayload = {
    /// Who ran the report.
    Actor: string
    /// Top-line outcome label: `"nothing-composed"`,
    /// `"all-composed-verified"`, `"partially-verified"` or
    /// `"failures-present"`.
    Outcome: string
    /// SHA-256 over the report's canonical form — the verdict SET, with
    /// the clock and the actor excluded, so two runs against an unchanged
    /// deployment produce the same digest and drift is visible as a
    /// change rather than inferred from prose.
    VerdictDigest: string
    /// One `"<section-id>=<verdict-label>"` entry per section, in report
    /// order. The shape a SIEM rule cuts on without parsing the report.
    Sections: string list
    /// The process exit code this run would return in CI: non-zero when
    /// any composed section was failed or unreadable.
    ExitCode: int
    OccurredAt: DateTimeOffset
}

/// Phase 713 — one walk of the evidence chain.
///
/// **Producing evidence itself leaves evidence.** The walk reads seven
/// joins and writes nothing back (GP 6); this row is the only artefact
/// it produces. Recording it turns "who has traced this deployment back
/// to the work that authored it, and what did the chain say when they
/// did" into a question the trail answers — and under a chained ledger
/// the answer is tamper-evident, so a walk whose breaks someone would
/// rather nobody saw cannot be quietly removed.
///
/// **The digest and the link labels, never the chain.** The row carries
/// the verdict digest and one label per hop, never the hop detail. The
/// detail names record ids, closure digests and ledger positions — a
/// deployment-wide evidence summary — and the audit trail has its own
/// readership and its own export paths. The digest is sufficient to
/// prove two walks said the same thing, and insufficient to be a second
/// copy of the chain.
///
/// **Recorded on every outcome, including the complete one.** A row
/// written only when a hop broke cannot distinguish a deployment nobody
/// traced from one that was traced and was whole — and those are the two
/// states a reader most needs to tell apart.
type EvidenceChainWalkedPayload = {
    /// Who walked.
    Actor: string
    /// Top-line outcome label: `"chain-unrecorded"`, `"chain-complete"`,
    /// `"chain-partial"` or `"chain-broken"`.
    Outcome: string
    /// SHA-256 over the chain's canonical form — the LINK SET, with the
    /// clock and the actor excluded, so two walks against an unchanged
    /// deployment produce the same digest and drift is visible as a
    /// change rather than inferred from prose.
    VerdictDigest: string
    /// One `"<hop-id>=<link-label>"` entry per hop, in walk order. The
    /// shape a SIEM rule cuts on without parsing the chain. Always the
    /// same length, whatever the deployment composes.
    Hops: string list
    OccurredAt: DateTimeOffset
}

/// Phase 739 — the decryption key for a gated HLS media item was handed
/// over. The grant twin of the `AuthorizationDenied` row Phase 471
/// already emits at the same endpoint.
///
/// **Why this exists.** Phase 471 gated the key and audited every
/// REFUSAL as a queryable row, leaving every GRANT as a structured log
/// line. So a deployment could answer "who was turned away from this
/// media" from the audit store and could not answer "who fetched the
/// key for it" — the wrong half of the trail to have, because the key
/// is the entire protection on segments that are, by design, cached at
/// an edge and exported to disk. Once it is out, it is out; the row is
/// the only record that it was (GP 6).
///
/// **Volume is bounded by construction.** An HLS client fetches the key
/// once per playback session (or per key rotation, which is a
/// re-transcode), never per segment. So this is one row per viewing, not
/// one per second of video, and it is deliberately NOT deduplicated or
/// sampled: two viewings of the same item by the same subject are two
/// facts, and a trail that collapses them cannot answer when access
/// happened. A deployment wanting quieter trails filters at the sink.
///
/// **Emitted after the key is resolved and before it is written to the
/// wire**, at the same choke point as the denial row, so no caller has
/// to remember it. That direction is deliberate: a released key that
/// went unrecorded is a worse failure than a recorded key whose transfer
/// the client then abandoned.
type MediaKeyDeliveredPayload = {
    /// The media item whose key was handed over — `MediaId.value`. The
    /// axis "who fetched the key for media X" is queried on.
    MediaId: string
    /// Subject kind, from `AuditSubject.sanitise` — `"anonymous"` /
    /// `"user"` / `"team"` / `"claim"`. The SAME projection
    /// `AuthorizationDenied` carries, so the grant and refusal halves
    /// join on one key rather than on two spellings.
    SubjectKind: string
    /// Subject id, or `None` for an anonymous session. Anonymous is a
    /// legitimate outcome here rather than an anomaly: a signed-URL
    /// fetch carries no session at all, which is the whole point of the
    /// signed route — `AdmissionRoute` is what names the authority in
    /// that case.
    SubjectId: string option
    /// The scope container the key was resolved FROM — always one the
    /// gate derived (a resolved scope, or the container bound into the
    /// signed token), never one the caller supplied (GP 4).
    ScopeContainer: string
    /// The admitting route, verbatim from `HlsKeyAccess.KeyAccessGranted`:
    /// `"scope"` (an ordinary resolved session scope) or `"signature"` (a
    /// valid, unexpired signed URL bound to this media id). Carried
    /// unmapped, from the one place the gate decides it — a second
    /// spelling here would be a translation table that can drift silently
    /// from the decision it claims to report.
    AdmissionRoute: string
    /// When the key left the origin.
    At: DateTime
}

/// Phase 2c — a cloud-storage companion's call to its backing store was
/// rejected with `401 Unauthorized` or `403 Forbidden`, i.e. the
/// credential the companion is holding is no longer accepted.
///
/// **Why this row exists.** The Phase 2c health probes turn a revoked or
/// rotated credential into an `Unhealthy` reading within one probe
/// cycle, which tells an operator that the deployment is unwell. It does
/// not tell them *when it started*, *which store*, or *what was being
/// attempted* — and before this row those facts were recoverable only by
/// noticing, later, that retrievals had gaps. The probe is the alarm;
/// this is the trail (GP 6).
///
/// **Emitted on the failure path only, once per rejected call**, from
/// the companion's own `with` handler, immediately before the same
/// `Error` the caller has always received is returned. Nothing is
/// swallowed and no error text changes: a deployment that composes no
/// audit sink behaves byte-for-byte as it did (GP 11 + GP 13).
///
/// **Volume is bounded by the outage, not by traffic shape** — during a
/// rotation window every storage call fails, so this row is deliberately
/// NOT coalesced: two failed calls are two facts, and a trail that
/// collapses them cannot answer how long the gap ran or which operations
/// were lost. A deployment wanting quieter trails filters at the sink,
/// which is where `MediaKeyDelivered` puts the same decision.
///
/// **No credential ever reaches the row.** `Reason` is passed through
/// `BlobStorageAuthAudit.sanitiseReason`, which redacts `name=value`
/// pairs whose name reads as secret-bearing (an Azure connection string
/// echoed in an SDK message is the motivating case) and truncates.
type BlobStorageAuthFailedPayload = {
    /// Which companion was rejected, in the SAME spelling its health
    /// probe uses — `"aws-s3"` / `"azure"` / `"gcs"`, i.e. the suffix of
    /// `blob_storage:{companion}`. One spelling, so the probe row and
    /// the audit row join without a translation table.
    Companion: string
    /// The backing store the credential was rejected FOR: the S3 /GCS
    /// bucket name, or the Azure root container. Names the resource an
    /// operator has to re-grant, which is not always the one they think
    /// when a deployment holds several.
    Container: string
    /// The `IBlobStorage` member that was attempting the call —
    /// `"Upload"` / `"Download"` / `"List"` / … . Carried because a
    /// credential can lose one permission and keep others (an IAM policy
    /// edit that drops `s3:PutObject` reads exactly like a full
    /// revocation from any single failing write).
    Operation: string
    /// The rejecting HTTP status: `401` or `403`. Kept as the transport
    /// status rather than mapped to a verdict DU — the two answer
    /// different operator questions (no credential presented vs. a
    /// credential that authenticated and was refused) and the SDKs
    /// already agree on the numbers.
    StatusCode: int
    /// The SDK's own message, sanitised and truncated. Diagnostic only —
    /// the queryable axes are the four fields above.
    Reason: string
    /// When the call was rejected.
    At: DateTime
}

/// Phase 36.E — one invocation of the built-in cross-module AI tool
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
    UserId: string
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
    /// Phase 283 — the stable component id of the component that
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

/// Phase 445 — a platform snapshot completed and its manifest was
/// written to the backup target. The row an operator (or a Compliance
/// Edition auditor asking "show me your last backup") reads to learn
/// what was captured and whether it was captured atomically.
type BackupCompletedPayload = {
    /// The snapshot's identifier — sortable, and the manifest's blob name
    /// under the backup target's `_backups/` container.
    SnapshotId: string
    /// The scope-derived containers the snapshot walked (`_platform`,
    /// `team-{id}`, …).
    Containers: string list
    /// Blobs copied — the manifest's entry count.
    BlobCount: int
    /// Bytes copied, summed over every entry.
    TotalBytes: int64
    /// `"Consistent"` when the event-store head did not move during the
    /// walk, `"Fuzzy"` when it did (the snapshot is then a point-in-
    /// interval, not a point-in-time, and the manifest says why).
    Consistency: string
    /// Encryption key ids the snapshot depends on (from the Phase 22
    /// envelope headers of the ciphertext it copied). Empty when no
    /// copied blob was enveloped. Key MATERIAL is never captured.
    KeyIds: string list
    /// When the walk started.
    StartedAt: DateTime
    /// When the manifest was written.
    CompletedAt: DateTime
}

/// Phase 445 — a snapshot did not complete. The manifest was not written,
/// so nothing downstream (a drill, a restore) can pick the attempt up by
/// mistake; the row is the only trace of it.
type BackupFailedPayload = {
    /// The containers the failed attempt was asked to walk.
    Containers: string list
    /// Why — a storage failure reading the source or writing the target.
    /// Diagnostic only; carries no blob content.
    Reason: string
    /// When the attempt started.
    StartedAt: DateTime
    /// When it gave up.
    FailedAt: DateTime
}

/// Phase 445 — a restore drill restored the latest snapshot into a scratch
/// prefix, verified every hash and the store-level invariants, and passed.
/// The row behind the "show me your restore drill" question.
type RestoreDrillPassedPayload = {
    /// The snapshot the drill rehearsed.
    SnapshotId: string
    /// The scratch restore's identifier (its staging prefix is erased
    /// once the drill completes).
    RestoreId: string
    /// Blobs whose SHA-256 matched the manifest before any write.
    HashesVerified: int
    /// Store-level invariants that held (`"event-replay"`,
    /// `"entity-index-round-trip"`, …).
    Invariants: string list
    /// When the drill started.
    StartedAt: DateTime
    /// When it finished.
    CompletedAt: DateTime
}

/// Phase 445 — a restore drill did not pass: no snapshot to rehearse, a
/// preflight refusal (hash mismatch, missing blob, destroyed key), or an
/// invariant that did not hold over the restored copy. The health probe
/// beside it reports `Degraded` until the next drill passes.
type RestoreDrillFailedPayload = {
    /// The snapshot the drill rehearsed, when one was found.
    SnapshotId: string option
    /// The scratch restore's identifier, when the drill got as far as
    /// restoring.
    RestoreId: string option
    /// Which stage failed — `"no-snapshot"` / `"preflight"` /
    /// `"restore"` / `"invariant"` / `"exception"`.
    Stage: string
    /// Why, in the coordinator's own words. Diagnostic only.
    Reason: string
    /// Invariants that did NOT hold, by name. Empty unless `Stage` is
    /// `"invariant"`.
    FailedInvariants: string list
    /// When the drill started.
    StartedAt: DateTime
    /// When it gave up.
    FailedAt: DateTime
}
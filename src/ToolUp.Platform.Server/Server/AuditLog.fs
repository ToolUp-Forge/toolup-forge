module ToolUp.Platform.Audit.AuditLog

open System
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.FSharp.Reflection
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Infra
open ToolUp.Platform.BlobStorage

// ─── Audit log default implementation ────────────────────────────
//
// `EventStoreAuditLog` is the SDK-default `IAuditLog` implementation.
// It is a thin adapter over the configured `IEventStore`:
//
//   * `Record` serialises the `AuditEvent` payload via
//     `FableConverters`, builds a `ModuleEvent` with
//     `SourceModule = AuditSourceModule.value`, and calls
//     `IEventStore.Write`. Failures are logged at `Warn`, counted
//     against the `toolup.audit.write_failures_total` metric
//     (Phase 114), and swallowed — audit emission must never fail the
//     primary operation. Same idiom as the existing audit emission in
//     `WebhookApiHandler.emitAudit`.
//
//   * `GetAuditTrail` calls `IEventStore.ReadBySource` (filtered
//     to the audit module by construction), deserialises each
//     payload back into the matching `AuditEvent` case via the
//     Phase 114 codec registry, and applies the optional date and
//     event-type filters in memory. Cross-scope reads are
//     structurally impossible — the underlying store is keyed by
//     `scopeId`.
//
// `FableConverters` is the canonical STJ converter set for non-Remoting
// JSON crossing the server/Fable boundary (PascalCase, DU-friendly).
// We use it for audit payloads even though they are server-only
// today, so any future client-facing audit query surface (e.g. an
// admin UI) can deserialise without re-deriving the wire shape.

let private auditJsonOptions = PlatformJson.options

/// The one payload serialisation every audit kind's codec uses — the
/// platform's and every companion family's — so a kind's bytes do not
/// depend on which assembly declares it.
module AuditPayloadJson =
    /// A payload record's JSON, as stored.
    let write (value: 'T) : string =
        JsonSerializer.Serialize(value, auditJsonOptions)

    /// A stored payload back to its record.
    let read<'T> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, auditJsonOptions)

let private toAuditJson (value: 'T) = AuditPayloadJson.write value

let private fromAuditJson<'T> (json: string) = AuditPayloadJson.read<'T> json

// Phase 1006 — a publication reader's `SurfaceDenied` row carries two
// optional fields (`RouteClass`, `ReaderRoles`) that are `None` for every
// other subject. They are omitted from the row when `None`, so a
// non-reader row stays byte-identical to its pre-1006 form; decoding a row
// without them yields `None`.
let private encodeSurfaceDenied (payload: SurfaceDeniedPayload) =
    let node = JsonSerializer.SerializeToNode(payload, auditJsonOptions).AsObject()

    for field in [ "RouteClass"; "ReaderRoles" ] do
        if isNull node[field] then
            node.Remove field |> ignore

    node.ToJsonString auditJsonOptions

// Phase 62 — `PremiumGranted` / `PremiumRevoked` are tuple-shaped DU
// cases; the serialised form is an anonymous record on the write
// path. This typed alias round-trips it back to a tuple.
type private PremiumAuditPayload = {
    SubjectUserId: UserId
    Grantor: string
    Reason: string option
    OccurredAt: DateTimeOffset
}

// Phase 61 — `AdSlotConfigCreated` / `AdSlotConfigUpdated` /
// `AdSlotConfigDeleted` are tuple-shaped DU cases (slotId, actor,
// occurredAt). Same round-trip-via-record pattern as Premium.
type private AdSlotConfigAuditPayload = {
    SlotId: string
    Actor: string
    OccurredAt: DateTimeOffset
}

/// Phase 114 — audit-write failure metric names. Defined here (rather
/// than in `MetricsMiddleware.StandardMetrics`, which compiles after
/// this file) so the failure path can reference the literal; the
/// matching `MetricDefinition` registration lives in
/// `StandardMetrics.registrations`.
module AuditMetrics =
    /// Counter incremented whenever `EventStoreAuditLog.Record` fails to
    /// persist an audit event — the event-store write threw, or (the
    /// exhaustiveness-test-guaranteed-impossible case) the event has no
    /// codec-registry entry. Promotes the formerly Warn-only loss signal
    /// to an alertable metric so silent audit loss is dashboard-visible.
    /// Tagged by `event_type` (the `AuditEvent` case name).
    [<Literal>]
    let WriteFailuresTotal = "toolup.audit.write_failures_total"

    /// Phase 553 — counter incremented when a permission-class audit
    /// write could not read its scope's hash-chain head, so the record
    /// was written UNCHAINED (or refused, under `RefuseAction`).
    ///
    /// A separate counter rather than a tag on `WriteFailuresTotal`
    /// because it is a different event with a different remedy: the
    /// audit row still landed, and what was lost is the tamper-evidence
    /// link. Folding it into the write-failure counter would inflate the
    /// "audit rows are being dropped" alert with events where nothing
    /// was dropped. Tagged by `reason` — `read_failed` (the store read
    /// threw), `forked` (more than one head), `unanchored` (chained
    /// records with no head at all) — because the three have genuinely
    /// different causes: the first is an outage, the other two are
    /// findings about the chain.
    [<Literal>]
    let ChainHeadUnreadableTotal = "toolup.audit.chain_head_unreadable_total"

// ─── The platform's audit-event codec ───────────────────────────
//
// The platform is one registrant of the open audit-event registry
// (`AuditEventRegistry` in Core): its kinds are the cases of the
// `AuditEvent` union, its encoder is one compiler-checked `match` over
// them, and its decoders are a table keyed by wire kind. Every other kind
// is a companion's or a module's, and arrives here as `Opaque` (kind +
// payload bytes): encoded verbatim, decoded to an `Opaque` value that
// carries its id and its raw payload, never a failure. The write path,
// the `GetAuditTrail` read path and the replicator all share the one
// codec, and the wire corpus in the test pack holds every kind's bytes.
//
// ─── Phase 625 — the `Artifact` / `Artefact` decision record ──────
//
// `AuditEvent` carried a one-letter homograph pair: the Phase 30a
// module-distribution family (`ArtifactSigned` / `ArtifactVerified` /
// `ArtifactRejected`, `_platform.artefacts`) and the Phase 40
// detached-JWS family (`ArtefactSigned`, `_platform.signing`). Two
// different security events, one vowel apart, both public API. Neither
// the compiler nor a reviewer reliably tells them apart, so a match
// arm, a dashboard query, or an alert rule can silently address the
// wrong one.
//
// WHAT WE MEASURED FIRST (the question that decided the shape). Is the
// `EventType` string matched anywhere downstream? Yes — in four places,
// three of them outside forge's reach:
//
//   1. `CefFormatter.highEvents` contains the literal "ArtifactRejected"
//      and grades it `CefHigh`. That set is membership-based, not a
//      match over the DU, so a renamed case looks exactly like a NEW
//      case to it and silently falls through to the `CefLow` default —
//      a security-severity downgrade with no compile error.
//   2. Every audit sink projects `AuditEvent.eventTypeName` verbatim as
//      the external discriminator: `EventType` in the S3 / GCS / Azure
//      Blob archive records, `eventTypeName` in the Splunk HEC and
//      Datadog Logs payloads, `cat` in the CEF record. These land in
//      customer-owned SIEMs whose saved searches, correlation rules and
//      retention policies key on the string. Forge can neither migrate
//      nor enumerate them.
//   3. The `toolup.audit.write_failures_total` metric is tagged
//      `event_type` from the same function, so operator dashboards and
//      alert thresholds key on it too.
//   4. This platform's own append-only store round-trips it: a changed
//      string makes every archived row of the family decode to
//      `Error "unknown event type ..."` and routes it to
//      `AuditEventDecodeFailed` — the audit trail for that family goes
//      dark rather than failing loudly.
//
// THE DECISION. Rename at the F# surface; pin the wire. The three 30a
// cases and their payload types became `ModuleArtefact*` — house
// `artefact` spelling, and a word apart from `ArtefactSigned` rather
// than a vowel — while `eventTypeName` continues to emit the historical
// `"Artifact*"` discriminators. Record FIELD names are untouched (those
// ARE serialised); only F#-facing identifiers moved.
//
// WHAT THAT COSTS, stated plainly rather than asserted away:
//   * It is a BREAKING source change. Consumers pattern-matching these
//     cases or constructing these payloads get a compile error. That is
//     the intended failure mode — loud, located, and mechanical to fix
//     (see docs/migrations/625-module-artefact-audit-cases.md).
//   * It leaves the homograph AT THE QUERY SURFACE unfixed: an operator
//     reading a Splunk archive still sees "ArtifactSigned" next to
//     "ArtefactSigned". This is accepted deliberately. The only way to
//     fix it is to change the emitted string, and that breaks
//     already-correct operator alert rules SILENTLY, with no upgrade
//     signal — strictly worse than a confusing-but-stable name in a
//     security-telemetry surface, and a direct GP 11 violation ("an
//     existing deployment that upgrades stays byte-for-byte identical
//     until it opts in").
//   * It makes case identifier != wire string for exactly three rows,
//     which is a new (smaller) hazard: a future author may "tidy" the
//     strings to match. That is why the pin is asserted by a test in
//     `AuditEventRegistryTests` rather than merely commented here.
//
// REJECTED: "rename fully and migrate the archives" — impossible, not
// merely expensive. Audit archives are append-only by design and are
// already replicated to third-party sinks; you cannot migrate what has
// left the building. REJECTED: "leave it and document" — that was the
// status quo (the Phase 40 case already carried a disambiguating doc
// comment) and it did not prevent the confusion that raised this phase.

/// The platform's encoder: one compiler-checked `match` over the union,
/// answering a case's payload JSON. The union's tag switch IS the lookup by
/// kind, so nothing is scanned. An `Opaque` event's payload is written as
/// it was received, byte-for-byte: a companion's kind, or a stored kind
/// this deployment no longer registers, is re-exportable unchanged.
let internal serialiseAuditEvent (audit: AuditEvent) : string =
    match audit with
    | UserLoggedIn p -> toAuditJson p
    | TeamCreated p -> toAuditJson p
    | MemberAdded p -> toAuditJson p
    | MemberRemoved p -> toAuditJson p
    | MemberRoleChanged p -> toAuditJson p
    | FileUploaded p -> toAuditJson p
    | FileDeleted p -> toAuditJson p
    | FileReprocessed p -> toAuditJson p
    | DataStoreReset p -> toAuditJson p
    | SessionStoreReset p -> toAuditJson p
    | AnalysisRun p -> toAuditJson p
    | PermissionChanged p -> toAuditJson p
    | NotificationSent p -> toAuditJson p
    | NotificationDeliveryFailed p -> toAuditJson p
    | HealthStateChanged p -> toAuditJson p
    | EncryptionKeyCreated p -> toAuditJson p
    | EncryptionKeyRotated p -> toAuditJson p
    | EncryptionKeyDestroyed p -> toAuditJson p
    | EncryptionKeyDestroyAcknowledged p -> toAuditJson p
    | EncryptionKeyUnreadable p -> toAuditJson p
    | EntityCreated p -> toAuditJson p
    | EntityUpdated p -> toAuditJson p
    | EntityDeleted p -> toAuditJson p
    | AuditSinkDelivered p -> toAuditJson p
    | AuditSinkFailed p -> toAuditJson p
    | AuditSinkDeadLettered p -> toAuditJson p
    | AuditEventDecodeFailed p -> toAuditJson p
    | NotificationSilentlySkipped p -> toAuditJson p
    | OAuthConnected p -> toAuditJson p
    | OAuthDisconnected p -> toAuditJson p
    | OAuthRefreshFailed p -> toAuditJson p
    | OAuth1aConnected p -> toAuditJson p
    | OAuth1aDisconnected p -> toAuditJson p
    | OAuth1aSigningFailed p -> toAuditJson p
    | OAuthTokenRefreshed p -> toAuditJson p
    | OAuthTokenRefreshFailed p -> toAuditJson p
    | OAuthRefreshTokenInvalidated p -> toAuditJson p
    | OAuthRefreshDeadLettered p -> toAuditJson p
    | PlatformAdminAssigned p -> toAuditJson p
    | PlatformAdminRevoked p -> toAuditJson p
    | ShareTokenIssued p -> toAuditJson p
    | ShareTokenUsed p -> toAuditJson p
    | ShareTokenRevoked p -> toAuditJson p
    | ServiceAccountCreated p -> toAuditJson p
    | ServiceAccountPermissionsChanged p -> toAuditJson p
    | ServiceAccountTokenMinted p -> toAuditJson p
    | ServiceAccountTokenRevoked p -> toAuditJson p
    | ServiceAccountStatusChanged p -> toAuditJson p
    | SessionRevoked p -> toAuditJson p
    | AllSessionsRevoked p -> toAuditJson p
    | ConfigDrift p -> toAuditJson p
    | DiagnosticBundleAccessed p -> toAuditJson p
    | RateLimitWaited p -> toAuditJson p
    | RateLimitRefused p -> toAuditJson p
    | ComputeBudgetDenied p -> toAuditJson p
    | ComputeBudgetWarning p -> toAuditJson p
    | DataSubjectRequest p -> toAuditJson p
    | ConversationStarted p -> toAuditJson p
    | ConversationTurnAppended p -> toAuditJson p
    | ConversationCompleted p -> toAuditJson p
    | ConversationErased p -> toAuditJson p
    | TeamCreationDenied p -> toAuditJson p
    | TeamArchived p -> toAuditJson p
    | TeamRestored p -> toAuditJson p
    | TeamDeleted p -> toAuditJson p
    | TeamOwnershipTransferred p -> toAuditJson p
    | TeamInviteIssued p -> toAuditJson p
    | TeamInviteAccepted p -> toAuditJson p
    | TeamInviteAcceptedFromPending p -> toAuditJson p
    | TeamInviteAcceptedFromPendingFailed p -> toAuditJson p
    | TeamInviteRevoked p -> toAuditJson p
    | TeamInviteRedeemed p -> toAuditJson p
    | TeamInviteExpired p -> toAuditJson p
    | ConsentRecorded p -> toAuditJson p
    | AdImpressionRecorded p -> toAuditJson p
    | AdClickRecorded p -> toAuditJson p
    | PremiumGranted(subjectUserId, grantor, reason, occurredAt) ->
        toAuditJson {
            SubjectUserId = subjectUserId
            Grantor = grantor
            Reason = reason
            OccurredAt = occurredAt
        }
    | PremiumRevoked(subjectUserId, grantor, reason, occurredAt) ->
        toAuditJson {
            SubjectUserId = subjectUserId
            Grantor = grantor
            Reason = reason
            OccurredAt = occurredAt
        }
    | AdSlotConfigCreated(slotId, actor, occurredAt) ->
        toAuditJson {
            SlotId = slotId
            Actor = actor
            OccurredAt = occurredAt
        }
    | AdSlotConfigUpdated(slotId, actor, occurredAt) ->
        toAuditJson {
            SlotId = slotId
            Actor = actor
            OccurredAt = occurredAt
        }
    | AdSlotConfigDeleted(slotId, actor, occurredAt) ->
        toAuditJson {
            SlotId = slotId
            Actor = actor
            OccurredAt = occurredAt
        }
    | AnonymousSessionMigrated p -> toAuditJson p
    | AuthScopeResolutionFailed p -> toAuditJson p
    | SurfaceDenied p -> encodeSurfaceDenied p
    | ModuleArtefactSigned p -> toAuditJson p
    | ModuleArtefactVerified p -> toAuditJson p
    | ModuleArtefactRejected p -> toAuditJson p
    | SyntheticSampleGenerated p -> toAuditJson p
    | SchemaOnlyAccessAttempted p -> toAuditJson p
    | GrantPolicyRefused p -> toAuditJson p
    | UnconsentedGrantRefused p -> toAuditJson p
    | GrantRecorded p -> toAuditJson p
    | AdminMutationProposed p -> toAuditJson p
    | AdminMutationApproved p -> toAuditJson p
    | AdminMutationRejected p -> toAuditJson p
    | AdminMutationApprovalRefused p -> toAuditJson p
    | AdminMutationExpired p -> toAuditJson p
    | GrantConsentProposed p -> toAuditJson p
    | GrantConsentApproved p -> toAuditJson p
    | GrantConsentRevoked p -> toAuditJson p
    | GrantConsentVerificationDenied p -> toAuditJson p
    | ClassifiedFieldRead p -> toAuditJson p
    | ClassifiedFieldWritten p -> toAuditJson p
    | TenantProvisioned p -> toAuditJson p
    | TenantDeprovisioned p -> toAuditJson p
    | TenantLifecycleHookFailed p -> toAuditJson p
    | TenantDataExported p -> toAuditJson p
    | TenantOffboardConfirmationRequested p -> toAuditJson p
    | TenantOffboardConfirmationApproved p -> toAuditJson p
    | TenantOffboardConfirmationRefused p -> toAuditJson p
    | TenantDeprovisionScheduled p -> toAuditJson p
    | TenantDeprovisionCancelled p -> toAuditJson p
    | RemotingMethodAudited p -> toAuditJson p
    | AuthorizationDenied p -> toAuditJson p
    | HostActionDispatched p -> toAuditJson p
    | EgressBlocked p -> toAuditJson p
    | EgressDenied p -> toAuditJson p
    | KnowledgeIndexLoadFailed p -> toAuditJson p
    | OrphanedContentBlobReclaimed p -> toAuditJson p
    | OrphanSweepCompleted p -> toAuditJson p
    | ModelFitStarted p -> toAuditJson p
    | ModelFitCompleted p -> toAuditJson p
    | ModelFitGateFailed p -> toAuditJson p
    | ModelFitBatchSubmitted p -> toAuditJson p
    | ModelArtifactRegistered p -> toAuditJson p
    | ModelArtifactTransitioned p -> toAuditJson p
    | ModelArtifactTransitionDenied p -> toAuditJson p
    | ModelArtifactTransitionAttributed p -> toAuditJson p
    | ModelArtifactProvenanceAttached p -> toAuditJson p
    | ModelArtifactPromoted p -> toAuditJson p
    | ModelScored p -> toAuditJson p
    | ModelScoreRefused p -> toAuditJson p
    | ModelEvaluated p -> toAuditJson p
    | ModelPromotionPolicyEvaluated p -> toAuditJson p
    | ModelArtifactSuperseded p -> toAuditJson p
    | ModelRegistrationObserverFailed p -> toAuditJson p
    | DatasetSpillCreated p -> toAuditJson p
    | DatasetSpillDeleted p -> toAuditJson p
    | DatasetRevintaged p -> toAuditJson p
    | DatasetDeclassified p -> toAuditJson p
    | DatasetPolicyDenied p -> toAuditJson p
    | SchemaProposed p -> toAuditJson p
    | SchemaApproved p -> toAuditJson p
    | SchemaChanged p -> toAuditJson p
    | ExternalCallbackResolved p -> toAuditJson p
    | ExternalCallbackRejected p -> toAuditJson p
    | CompositionVerificationRecorded p -> toAuditJson p
    | CompositionCapabilityRefused p -> toAuditJson p
    | GroundingEnvelopeMutated p -> toAuditJson p
    | GroundingMutationRefused p -> toAuditJson p
    | DeploymentVerified p -> toAuditJson p
    | EvidenceChainWalked p -> toAuditJson p
    | MediaKeyDelivered p -> toAuditJson p
    | BlobStorageAuthFailed p -> toAuditJson p
    | BackupCompleted p -> toAuditJson p
    | BackupFailed p -> toAuditJson p
    | RestoreDrillPassed p -> toAuditJson p
    | RestoreDrillFailed p -> toAuditJson p
    | ContactCreated p -> toAuditJson p
    | ContactUpdated p -> toAuditJson p
    | ContactDeleted p -> toAuditJson p
    | ContactOptInRecorded p -> toAuditJson p
    | ContactOptInWithdrawn p -> toAuditJson p
    | NotificationDeliveryRefused p -> toAuditJson p
    | PublicationReaderSignedIn p -> toAuditJson p
    | PublicationReaderGranted p -> toAuditJson p
    | PublicationReaderRevoked p -> toAuditJson p
    | PageSaved p -> toAuditJson p
    | PageReseeded p -> toAuditJson p
    | PageDetached p -> toAuditJson p
    | PageWriteRefused p -> toAuditJson p
    | Opaque o -> o.Payload

/// The platform's decoders, keyed by wire kind. A kind's wire id is NOT
/// always its F# case identifier: the three `ModuleArtefact*` cases keep
/// their historical `Artifact*` strings (the decision record above).
let internal auditDecoderByType: Map<string, string -> AuditEvent> =
    Map.ofList [
        "UserLoggedIn", (fun j -> UserLoggedIn(fromAuditJson<UserLoggedInPayload> j))
        "TeamCreated", (fun j -> TeamCreated(fromAuditJson<TeamCreatedPayload> j))
        "MemberAdded", (fun j -> MemberAdded(fromAuditJson<MemberAddedPayload> j))
        "MemberRemoved", (fun j -> MemberRemoved(fromAuditJson<MemberRemovedPayload> j))
        "MemberRoleChanged", (fun j -> MemberRoleChanged(fromAuditJson<MemberRoleChangedPayload> j))
        "FileUploaded", (fun j -> FileUploaded(fromAuditJson<FileUploadedPayload> j))
        "FileDeleted", (fun j -> FileDeleted(fromAuditJson<FileDeletedPayload> j))
        "FileReprocessed", (fun j -> FileReprocessed(fromAuditJson<FileReprocessedPayload> j))
        "DataStoreReset", (fun j -> DataStoreReset(fromAuditJson<DataStoreResetPayload> j))
        "SessionStoreReset", (fun j -> SessionStoreReset(fromAuditJson<SessionStoreResetPayload> j))
        "AnalysisRun", (fun j -> AnalysisRun(fromAuditJson<AnalysisRunPayload> j))
        "PermissionChanged", (fun j -> PermissionChanged(fromAuditJson<PermissionChangedPayload> j))
        "NotificationSent", (fun j -> NotificationSent(fromAuditJson<NotificationSentPayload> j))
        "NotificationDeliveryFailed",
        (fun j -> NotificationDeliveryFailed(fromAuditJson<NotificationDeliveryFailedPayload> j))
        "HealthStateChanged", (fun j -> HealthStateChanged(fromAuditJson<HealthStateChangedPayload> j))
        "EncryptionKeyCreated", (fun j -> EncryptionKeyCreated(fromAuditJson<EncryptionKeyEventPayload> j))
        "EncryptionKeyRotated", (fun j -> EncryptionKeyRotated(fromAuditJson<EncryptionKeyEventPayload> j))
        "EncryptionKeyDestroyed", (fun j -> EncryptionKeyDestroyed(fromAuditJson<EncryptionKeyEventPayload> j))
        "EncryptionKeyDestroyAcknowledged",
        (fun j -> EncryptionKeyDestroyAcknowledged(fromAuditJson<EncryptionKeyDestroyAckPayload> j))
        "EncryptionKeyUnreadable", (fun j -> EncryptionKeyUnreadable(fromAuditJson<EncryptionKeyUnreadablePayload> j))
        "EntityCreated", (fun j -> EntityCreated(fromAuditJson<EntityLifecycleEventPayload> j))
        "EntityUpdated", (fun j -> EntityUpdated(fromAuditJson<EntityLifecycleEventPayload> j))
        "EntityDeleted", (fun j -> EntityDeleted(fromAuditJson<EntityLifecycleEventPayload> j))
        "AuditSinkDelivered", (fun j -> AuditSinkDelivered(fromAuditJson<AuditSinkDeliveredPayload> j))
        "AuditSinkFailed", (fun j -> AuditSinkFailed(fromAuditJson<AuditSinkFailedPayload> j))
        "AuditSinkDeadLettered", (fun j -> AuditSinkDeadLettered(fromAuditJson<AuditSinkDeadLetteredPayload> j))
        "AuditEventDecodeFailed", (fun j -> AuditEventDecodeFailed(fromAuditJson<AuditEventDecodeFailedPayload> j))
        "NotificationSilentlySkipped",
        (fun j -> NotificationSilentlySkipped(fromAuditJson<NotificationSilentlySkippedPayload> j))
        "OAuthConnected", (fun j -> OAuthConnected(fromAuditJson<OAuthConnectedPayload> j))
        "OAuthDisconnected", (fun j -> OAuthDisconnected(fromAuditJson<OAuthDisconnectedPayload> j))
        "OAuthRefreshFailed", (fun j -> OAuthRefreshFailed(fromAuditJson<OAuthRefreshFailedPayload> j))
        "OAuth1aConnected", (fun j -> OAuth1aConnected(fromAuditJson<OAuth1aConnectedPayload> j))
        "OAuth1aDisconnected", (fun j -> OAuth1aDisconnected(fromAuditJson<OAuth1aDisconnectedPayload> j))
        "OAuth1aSigningFailed", (fun j -> OAuth1aSigningFailed(fromAuditJson<OAuth1aSigningFailedPayload> j))
        "OAuthTokenRefreshed", (fun j -> OAuthTokenRefreshed(fromAuditJson<OAuthTokenRefreshedPayload> j))
        "OAuthTokenRefreshFailed", (fun j -> OAuthTokenRefreshFailed(fromAuditJson<OAuthTokenRefreshFailedPayload> j))
        "OAuthRefreshTokenInvalidated",
        (fun j -> OAuthRefreshTokenInvalidated(fromAuditJson<OAuthRefreshTokenInvalidatedPayload> j))
        "OAuthRefreshDeadLettered",
        (fun j -> OAuthRefreshDeadLettered(fromAuditJson<OAuthRefreshDeadLetteredPayload> j))
        "PlatformAdminAssigned", (fun j -> PlatformAdminAssigned(fromAuditJson<PlatformAdminAssignedPayload> j))
        "PlatformAdminRevoked", (fun j -> PlatformAdminRevoked(fromAuditJson<PlatformAdminRevokedPayload> j))
        "ShareTokenIssued", (fun j -> ShareTokenIssued(fromAuditJson<ShareTokenIssuedPayload> j))
        "ShareTokenUsed", (fun j -> ShareTokenUsed(fromAuditJson<ShareTokenUsedPayload> j))
        "ShareTokenRevoked", (fun j -> ShareTokenRevoked(fromAuditJson<ShareTokenRevokedPayload> j))
        "ServiceAccountCreated", (fun j -> ServiceAccountCreated(fromAuditJson<ServiceAccountCreatedPayload> j))
        "ServiceAccountPermissionsChanged",
        (fun j -> ServiceAccountPermissionsChanged(fromAuditJson<ServiceAccountPermissionsChangedPayload> j))
        "ServiceAccountTokenMinted",
        (fun j -> ServiceAccountTokenMinted(fromAuditJson<ServiceAccountTokenMintedPayload> j))
        "ServiceAccountTokenRevoked",
        (fun j -> ServiceAccountTokenRevoked(fromAuditJson<ServiceAccountTokenRevokedPayload> j))
        "ServiceAccountStatusChanged",
        (fun j -> ServiceAccountStatusChanged(fromAuditJson<ServiceAccountStatusChangedPayload> j))
        "SessionRevoked", (fun j -> SessionRevoked(fromAuditJson<SessionRevokedPayload> j))
        "AllSessionsRevoked", (fun j -> AllSessionsRevoked(fromAuditJson<AllSessionsRevokedPayload> j))
        "ConfigDrift", (fun j -> ConfigDrift(fromAuditJson<ConfigDriftPayload> j))
        "DiagnosticBundleAccessed",
        (fun j -> DiagnosticBundleAccessed(fromAuditJson<DiagnosticBundleAccessedPayload> j))
        "RateLimitWaited", (fun j -> RateLimitWaited(fromAuditJson<RateLimitWaitedPayload> j))
        "RateLimitRefused", (fun j -> RateLimitRefused(fromAuditJson<RateLimitRefusedPayload> j))
        "ComputeBudgetDenied", (fun j -> ComputeBudgetDenied(fromAuditJson<ComputeBudgetDeniedPayload> j))
        "ComputeBudgetWarning", (fun j -> ComputeBudgetWarning(fromAuditJson<ComputeBudgetWarningPayload> j))
        "DataSubjectRequest", (fun j -> DataSubjectRequest(fromAuditJson<DataSubjectRequestAuditPayload> j))
        "ConversationStarted", (fun j -> ConversationStarted(fromAuditJson<ConversationStartedPayload> j))
        "ConversationTurnAppended",
        (fun j -> ConversationTurnAppended(fromAuditJson<ConversationTurnAppendedPayload> j))
        "ConversationCompleted", (fun j -> ConversationCompleted(fromAuditJson<ConversationCompletedPayload> j))
        "ConversationErased", (fun j -> ConversationErased(fromAuditJson<ConversationErasedPayload> j))
        "TeamCreationDenied", (fun j -> TeamCreationDenied(fromAuditJson<TeamCreationDeniedPayload> j))
        "TeamArchived", (fun j -> TeamArchived(fromAuditJson<TeamArchivedPayload> j))
        "TeamRestored", (fun j -> TeamRestored(fromAuditJson<TeamRestoredPayload> j))
        "TeamDeleted", (fun j -> TeamDeleted(fromAuditJson<TeamDeletedPayload> j))
        "TeamOwnershipTransferred",
        (fun j -> TeamOwnershipTransferred(fromAuditJson<TeamOwnershipTransferredPayload> j))
        "TeamInviteIssued", (fun j -> TeamInviteIssued(fromAuditJson<TeamInviteIssuedPayload> j))
        "TeamInviteAccepted", (fun j -> TeamInviteAccepted(fromAuditJson<TeamInviteAcceptedPayload> j))
        "TeamInviteAcceptedFromPending",
        (fun j -> TeamInviteAcceptedFromPending(fromAuditJson<TeamInviteAcceptedFromPendingPayload> j))
        "TeamInviteAcceptedFromPendingFailed",
        (fun j -> TeamInviteAcceptedFromPendingFailed(fromAuditJson<TeamInviteAcceptedFromPendingFailedPayload> j))
        "TeamInviteRevoked", (fun j -> TeamInviteRevoked(fromAuditJson<TeamInviteRevokedPayload> j))
        "TeamInviteRedeemed", (fun j -> TeamInviteRedeemed(fromAuditJson<TeamInviteRedeemedPayload> j))
        "TeamInviteExpired", (fun j -> TeamInviteExpired(fromAuditJson<TeamInviteExpiredPayload> j))
        "ConsentRecorded", (fun j -> ConsentRecorded(fromAuditJson<ConsentEvent> j))
        "AdImpressionRecorded", (fun j -> AdImpressionRecorded(fromAuditJson<AdImpression> j))
        "AdClickRecorded", (fun j -> AdClickRecorded(fromAuditJson<AdClick> j))
        "PremiumGranted",
        (fun j ->
            let p = fromAuditJson<PremiumAuditPayload> j
            PremiumGranted(p.SubjectUserId, p.Grantor, p.Reason, p.OccurredAt))
        "PremiumRevoked",
        (fun j ->
            let p = fromAuditJson<PremiumAuditPayload> j
            PremiumRevoked(p.SubjectUserId, p.Grantor, p.Reason, p.OccurredAt))
        "AdSlotConfigCreated",
        (fun j ->
            let p = fromAuditJson<AdSlotConfigAuditPayload> j
            AdSlotConfigCreated(p.SlotId, p.Actor, p.OccurredAt))
        "AdSlotConfigUpdated",
        (fun j ->
            let p = fromAuditJson<AdSlotConfigAuditPayload> j
            AdSlotConfigUpdated(p.SlotId, p.Actor, p.OccurredAt))
        "AdSlotConfigDeleted",
        (fun j ->
            let p = fromAuditJson<AdSlotConfigAuditPayload> j
            AdSlotConfigDeleted(p.SlotId, p.Actor, p.OccurredAt))
        "AnonymousSessionMigrated",
        (fun j -> AnonymousSessionMigrated(fromAuditJson<AnonymousSessionMigratedPayload> j))
        "AuthScopeResolutionFailed", (fun j -> AuthScopeResolutionFailed(fromAuditJson<ScopeResolutionFailedPayload> j))
        "SurfaceDenied", (fun j -> SurfaceDenied(fromAuditJson<SurfaceDeniedPayload> j))
        "ArtifactSigned", (fun j -> ModuleArtefactSigned(fromAuditJson<ModuleArtefactSignedPayload> j))
        "ArtifactVerified", (fun j -> ModuleArtefactVerified(fromAuditJson<ModuleArtefactVerifiedPayload> j))
        "ArtifactRejected", (fun j -> ModuleArtefactRejected(fromAuditJson<ModuleArtefactRejectedPayload> j))
        "SyntheticSampleGenerated",
        (fun j -> SyntheticSampleGenerated(fromAuditJson<SyntheticSampleGeneratedPayload> j))
        "SchemaOnlyAccessAttempted",
        (fun j -> SchemaOnlyAccessAttempted(fromAuditJson<SchemaOnlyAccessAttemptedPayload> j))
        "GrantPolicyRefused", (fun j -> GrantPolicyRefused(fromAuditJson<GrantPolicyRefusedPayload> j))
        "UnconsentedGrantRefused", (fun j -> UnconsentedGrantRefused(fromAuditJson<UnconsentedGrantRefusedPayload> j))
        "GrantRecorded", (fun j -> GrantRecorded(fromAuditJson<GrantRecordedPayload> j))
        "AdminMutationProposed", (fun j -> AdminMutationProposed(fromAuditJson<AdminMutationProposedPayload> j))
        "AdminMutationApproved", (fun j -> AdminMutationApproved(fromAuditJson<AdminMutationApprovedPayload> j))
        "AdminMutationRejected", (fun j -> AdminMutationRejected(fromAuditJson<AdminMutationRejectedPayload> j))
        "AdminMutationApprovalRefused",
        (fun j -> AdminMutationApprovalRefused(fromAuditJson<AdminMutationApprovalRefusedPayload> j))
        "AdminMutationExpired", (fun j -> AdminMutationExpired(fromAuditJson<AdminMutationExpiredPayload> j))
        "GrantConsentProposed", (fun j -> GrantConsentProposed(fromAuditJson<GrantConsentProposedPayload> j))
        "GrantConsentApproved", (fun j -> GrantConsentApproved(fromAuditJson<GrantConsentApprovedPayload> j))
        "GrantConsentRevoked", (fun j -> GrantConsentRevoked(fromAuditJson<GrantConsentRevokedPayload> j))
        "GrantConsentVerificationDenied",
        (fun j -> GrantConsentVerificationDenied(fromAuditJson<GrantConsentVerificationDeniedPayload> j))
        "ClassifiedFieldRead", (fun j -> ClassifiedFieldRead(fromAuditJson<ClassifiedFieldReadPayload> j))
        "ClassifiedFieldWritten", (fun j -> ClassifiedFieldWritten(fromAuditJson<ClassifiedFieldWrittenPayload> j))
        "TenantProvisioned", (fun j -> TenantProvisioned(fromAuditJson<TenantProvisionedPayload> j))
        "TenantDeprovisioned", (fun j -> TenantDeprovisioned(fromAuditJson<TenantDeprovisionedPayload> j))
        "TenantLifecycleHookFailed",
        (fun j -> TenantLifecycleHookFailed(fromAuditJson<TenantLifecycleHookFailedPayload> j))
        "TenantDataExported", (fun j -> TenantDataExported(fromAuditJson<TenantDataExportedPayload> j))
        "TenantOffboardConfirmationRequested",
        (fun j -> TenantOffboardConfirmationRequested(fromAuditJson<TenantOffboardConfirmationRequestedPayload> j))
        "TenantOffboardConfirmationApproved",
        (fun j -> TenantOffboardConfirmationApproved(fromAuditJson<TenantOffboardConfirmationApprovedPayload> j))
        "TenantOffboardConfirmationRefused",
        (fun j -> TenantOffboardConfirmationRefused(fromAuditJson<TenantOffboardConfirmationRefusedPayload> j))
        "TenantDeprovisionScheduled",
        (fun j -> TenantDeprovisionScheduled(fromAuditJson<TenantDeprovisionScheduledPayload> j))
        "TenantDeprovisionCancelled",
        (fun j -> TenantDeprovisionCancelled(fromAuditJson<TenantDeprovisionCancelledPayload> j))
        "RemotingMethodAudited", (fun j -> RemotingMethodAudited(fromAuditJson<RemotingMethodAuditedPayload> j))
        "AuthorizationDenied", (fun j -> AuthorizationDenied(fromAuditJson<AuthorizationDeniedPayload> j))
        "HostActionDispatched", (fun j -> HostActionDispatched(fromAuditJson<HostActionDispatchedPayload> j))
        "EgressBlocked", (fun j -> EgressBlocked(fromAuditJson<EgressBlockedPayload> j))
        "EgressDenied", (fun j -> EgressDenied(fromAuditJson<EgressDeniedPayload> j))
        "KnowledgeIndexLoadFailed",
        (fun j -> KnowledgeIndexLoadFailed(fromAuditJson<KnowledgeIndexLoadFailedPayload> j))
        "OrphanedContentBlobReclaimed",
        (fun j -> OrphanedContentBlobReclaimed(fromAuditJson<OrphanedContentBlobReclaimedPayload> j))
        "OrphanSweepCompleted", (fun j -> OrphanSweepCompleted(fromAuditJson<OrphanSweepCompletedPayload> j))
        "ModelFitStarted", (fun j -> ModelFitStarted(fromAuditJson<ModelFitStartedPayload> j))
        "ModelFitCompleted", (fun j -> ModelFitCompleted(fromAuditJson<ModelFitCompletedPayload> j))
        "ModelFitGateFailed", (fun j -> ModelFitGateFailed(fromAuditJson<ModelFitGateFailedPayload> j))
        "ModelFitBatchSubmitted", (fun j -> ModelFitBatchSubmitted(fromAuditJson<ModelFitBatchSubmittedPayload> j))
        "ModelArtifactRegistered", (fun j -> ModelArtifactRegistered(fromAuditJson<ModelArtifactRegisteredPayload> j))
        "ModelArtifactTransitioned",
        (fun j -> ModelArtifactTransitioned(fromAuditJson<ModelArtifactTransitionedPayload> j))
        "ModelArtifactTransitionDenied",
        (fun j -> ModelArtifactTransitionDenied(fromAuditJson<ModelArtifactTransitionDeniedPayload> j))
        "ModelArtifactTransitionAttributed",
        (fun j -> ModelArtifactTransitionAttributed(fromAuditJson<ModelArtifactTransitionAttributedPayload> j))
        "ModelArtifactProvenanceAttached",
        (fun j -> ModelArtifactProvenanceAttached(fromAuditJson<ModelArtifactProvenanceAttachedPayload> j))
        "ModelArtifactPromoted", (fun j -> ModelArtifactPromoted(fromAuditJson<ModelArtifactPromotionPayload> j))
        "ModelScored", (fun j -> ModelScored(fromAuditJson<ModelScoredPayload> j))
        "ModelScoreRefused", (fun j -> ModelScoreRefused(fromAuditJson<ModelScoreRefusedPayload> j))
        "ModelEvaluated", (fun j -> ModelEvaluated(fromAuditJson<ModelEvaluatedPayload> j))
        "ModelPromotionPolicyEvaluated",
        (fun j -> ModelPromotionPolicyEvaluated(fromAuditJson<ModelPromotionPolicyEvaluatedPayload> j))
        "ModelArtifactSuperseded", (fun j -> ModelArtifactSuperseded(fromAuditJson<ModelArtifactSupersededPayload> j))
        "ModelRegistrationObserverFailed",
        (fun j -> ModelRegistrationObserverFailed(fromAuditJson<ModelRegistrationObserverFailedPayload> j))
        "DatasetSpillCreated", (fun j -> DatasetSpillCreated(fromAuditJson<DatasetSpillCreatedPayload> j))
        "DatasetSpillDeleted", (fun j -> DatasetSpillDeleted(fromAuditJson<DatasetSpillDeletedPayload> j))
        "DatasetRevintaged", (fun j -> DatasetRevintaged(fromAuditJson<DatasetRevintagedPayload> j))
        "DatasetDeclassified", (fun j -> DatasetDeclassified(fromAuditJson<DatasetDeclassifiedPayload> j))
        "DatasetPolicyDenied", (fun j -> DatasetPolicyDenied(fromAuditJson<DatasetPolicyDeniedPayload> j))
        "SchemaProposed", (fun j -> SchemaProposed(fromAuditJson<SchemaProposedPayload> j))
        "SchemaApproved", (fun j -> SchemaApproved(fromAuditJson<SchemaApprovedPayload> j))
        "SchemaChanged", (fun j -> SchemaChanged(fromAuditJson<SchemaChangedPayload> j))
        "ExternalCallbackResolved",
        (fun j -> ExternalCallbackResolved(fromAuditJson<ExternalCallbackResolvedPayload> j))
        "ExternalCallbackRejected",
        (fun j -> ExternalCallbackRejected(fromAuditJson<ExternalCallbackRejectedPayload> j))
        "CompositionVerificationRecorded",
        (fun j -> CompositionVerificationRecorded(fromAuditJson<CompositionVerificationRecordedPayload> j))
        "CompositionCapabilityRefused",
        (fun j -> CompositionCapabilityRefused(fromAuditJson<CompositionCapabilityRefusedPayload> j))
        "GroundingEnvelopeMutated",
        (fun j -> GroundingEnvelopeMutated(fromAuditJson<GroundingEnvelopeMutatedPayload> j))
        "GroundingMutationRefused",
        (fun j -> GroundingMutationRefused(fromAuditJson<GroundingMutationRefusedPayload> j))
        "DeploymentVerified", (fun j -> DeploymentVerified(fromAuditJson<DeploymentVerifiedPayload> j))
        "EvidenceChainWalked", (fun j -> EvidenceChainWalked(fromAuditJson<EvidenceChainWalkedPayload> j))
        "MediaKeyDelivered", (fun j -> MediaKeyDelivered(fromAuditJson<MediaKeyDeliveredPayload> j))
        "BlobStorageAuthFailed", (fun j -> BlobStorageAuthFailed(fromAuditJson<BlobStorageAuthFailedPayload> j))
        "BackupCompleted", (fun j -> BackupCompleted(fromAuditJson<BackupCompletedPayload> j))
        "BackupFailed", (fun j -> BackupFailed(fromAuditJson<BackupFailedPayload> j))
        "RestoreDrillPassed", (fun j -> RestoreDrillPassed(fromAuditJson<RestoreDrillPassedPayload> j))
        "RestoreDrillFailed", (fun j -> RestoreDrillFailed(fromAuditJson<RestoreDrillFailedPayload> j))
        "ContactCreated", (fun j -> ContactCreated(fromAuditJson<ContactCreatedPayload> j))
        "ContactUpdated", (fun j -> ContactUpdated(fromAuditJson<ContactUpdatedPayload> j))
        "ContactDeleted", (fun j -> ContactDeleted(fromAuditJson<ContactDeletedPayload> j))
        "ContactOptInRecorded", (fun j -> ContactOptInRecorded(fromAuditJson<ContactOptInRecordedPayload> j))
        "ContactOptInWithdrawn", (fun j -> ContactOptInWithdrawn(fromAuditJson<ContactOptInWithdrawnPayload> j))
        "NotificationDeliveryRefused",
        (fun j -> NotificationDeliveryRefused(fromAuditJson<NotificationDeliveryRefusedPayload> j))
        "PublicationReaderSignedIn",
        (fun j -> PublicationReaderSignedIn(fromAuditJson<PublicationReaderSignedInPayload> j))
        "PublicationReaderGranted",
        (fun j -> PublicationReaderGranted(fromAuditJson<PublicationReaderGrantChangedPayload> j))
        "PublicationReaderRevoked",
        (fun j -> PublicationReaderRevoked(fromAuditJson<PublicationReaderGrantChangedPayload> j))
        "PageSaved", (fun j -> PageSaved(fromAuditJson<PageSavedPayload> j))
        "PageReseeded", (fun j -> PageReseeded(fromAuditJson<PageReseededPayload> j))
        "PageDetached", (fun j -> PageDetached(fromAuditJson<PageDetachedPayload> j))
        "PageWriteRefused", (fun j -> PageWriteRefused(fromAuditJson<PageWriteRefusedPayload> j))
    ]

/// The platform's own kinds, as the registry records them. The platform
/// does not grade its kinds (`Severity = None`): a sink's own
/// classification stays authoritative for them.
let platformAuditRegistration: AuditEventRegistration = {
    Registrant = AuditEventRegistry.PlatformRegistrant
    Kinds =
        auditDecoderByType
        |> Map.toList
        |> List.map (fun (kind, _) -> {
            Kind = kind
            Registrant = AuditEventRegistry.PlatformRegistrant
            Severity = None
        })
}


/// The property a persisted row carries its actor in, beside the event's
/// own fields. Every reader of a payload ignores a property its record does
/// not declare, so the rows it is added to read exactly as before.
[<Literal>]
let internal ActorProperty = "_actor"

/// The persisted payload of a row: the event's JSON with the actor appended
/// as `_actor`. The event's own bytes are kept verbatim — the actor is
/// spliced in before the closing brace, never re-serialised around it.
let internal withActor (actor: AuditSubject) (eventJson: string) : string =
    let body = eventJson.TrimEnd()

    if not (body.EndsWith "}") then
        failwithf "an audit payload is not a JSON object: %s" body

    let open' = body.Substring(0, body.Length - 1).TrimEnd()
    let separator = if open'.EndsWith "{" then "" else ","
    let actorJson = JsonSerializer.Serialize(actor, auditJsonOptions)
    $"{open'}{separator}\"{ActorProperty}\":{actorJson}}}"

/// The actor a persisted row carries. A row written before rows carried one
/// — or one whose actor no longer decodes — answers
/// `AuditSubject.unattributed`: nothing is guessed from the stream key.
let internal decodeActor (payload: string) : AuditSubject =
    if isNull payload || not (payload.Contains("\"" + ActorProperty + "\"")) then
        AuditSubject.unattributed
    else
        try
            use doc = JsonDocument.Parse payload

            match doc.RootElement.TryGetProperty ActorProperty with
            | true, actor -> JsonSerializer.Deserialize<AuditSubject>(actor.GetRawText(), auditJsonOptions)
            | _ -> AuditSubject.unattributed
        with _ ->
            AuditSubject.unattributed

/// A stored payload without the `_actor` property `withActor` appended:
/// the event's own bytes. Only the property `withActor` splices in at the
/// end is removed — one whose value is exactly the row's actor JSON
/// followed by the closing brace — so a payload is never re-serialised.
let internal withoutActor (payload: string) : string =
    let marker = "\"" + ActorProperty + "\":"

    if isNull payload then
        payload
    else
        let body = payload.TrimEnd()
        let at = body.LastIndexOf marker

        if at < 0 then
            payload
        else
            try
                use doc = JsonDocument.Parse body

                match doc.RootElement.TryGetProperty ActorProperty with
                | true, actor when body.Substring(at + marker.Length) = actor.GetRawText() + "}" ->
                    let head = body.Substring(0, at).TrimEnd()

                    let head =
                        if head.EndsWith "," then
                            head.Substring(0, head.Length - 1)
                        else
                            head

                    head + "}"
                | _ -> payload
            with _ ->
                payload

/// Decode a persisted audit `(eventType, payload)` pair. A platform kind
/// decodes to its case; a malformed payload for one is an `Error` naming
/// why (the replicator records it). Any other kind decodes to `Opaque`,
/// carrying its id and its payload without the row's `_actor`: a
/// companion reads it back through its family, and a kind no registrant
/// on this deployment declares stays readable and re-exportable.
let internal tryDecodeAuditEvent (eventType: string) (payload: string) : Result<AuditEvent, string> =
    match Map.tryFind eventType auditDecoderByType with
    | None ->
        Ok(
            Opaque {
                Kind = eventType
                Payload = withoutActor payload
            }
        )
    | Some decode ->
        try
            Ok(decode payload)
        with ex ->
            Error(sprintf "payload decode error: %s" ex.Message)

/// Decode a persisted `ModuleEvent` whose `SourceModule` is
/// `AuditSourceModule.value` back into an `AuditEvent`. Returns
/// `None` only for a malformed payload of a platform kind, so a single
/// corrupt blob doesn't break the whole query; every other kind reads.
let internal decodeAuditEvent (evt: ModuleEvent) : AuditEvent option =
    match tryDecodeAuditEvent evt.EventType evt.Payload with
    | Ok audit -> Some audit
    | Error _ -> None

/// Whole-event audit JSON: what an archive, a sink or a ledger writes for an
/// `AuditEvent`. A platform case keeps the shape `PlatformJson.options`
/// gives a union case (`{"Case": payload}`); an `Opaque` event is written in
/// that SAME shape under its kind (`{"<Kind>": payload}`), with its payload
/// bytes verbatim. So a companion's event archives exactly as it did when
/// it was a case of the union, and reading one back answers `Opaque`.
module AuditEventJson =
    let private platformCases: Set<string> =
        FSharpType.GetUnionCases typeof<AuditEvent>
        |> Array.map _.Name
        |> Array.filter (fun name -> name <> "Opaque")
        |> Set.ofArray

    type private AuditEventConverter() =
        inherit JsonConverter<AuditEvent>()

        override _.Write(writer: Utf8JsonWriter, value: AuditEvent, _options: JsonSerializerOptions) =
            match value with
            | Opaque o ->
                writer.WriteStartObject()
                writer.WritePropertyName o.Kind
                writer.WriteRawValue(o.Payload, skipInputValidation = false)
                writer.WriteEndObject()
            | platform -> JsonSerializer.Serialize(writer, platform, PlatformJson.options)

        override _.Read(reader: byref<Utf8JsonReader>, _typeToConvert: Type, _options: JsonSerializerOptions) =
            use doc = JsonDocument.ParseValue &reader
            let root = doc.RootElement

            let single =
                if root.ValueKind = JsonValueKind.Object then
                    match List.ofSeq (root.EnumerateObject()) with
                    | [ only ] when not (platformCases.Contains only.Name) -> Some only
                    | _ -> None
                else
                    None

            match single with
            | Some kind ->
                Opaque {
                    Kind = kind.Name
                    Payload = kind.Value.GetRawText()
                }
            | None -> JsonSerializer.Deserialize<AuditEvent>(root.GetRawText(), PlatformJson.options)

    /// `PlatformJson.options` with the whole-event audit shape in front.
    /// Every audit sink serialises its envelopes with these.
    let options: JsonSerializerOptions =
        let o = JsonSerializerOptions(PlatformJson.options)
        o.Converters.Insert(0, AuditEventConverter())
        o

/// SDK-default `IAuditLog`. Wraps the DI-registered `IEventStore`
/// so audit events flow through the same retention policy, blob
/// layout, and webhook hooks as every other platform event.
///
/// Phase 114 — `metricsLookup` is an optional deferred accessor for the
/// `IMetricsSink` (deferred because the sink is resolved later in
/// `compose` than this log is constructed — same `*Cell` pattern as the
/// rate limiter / job scheduler). The failure path reads it lazily, by
/// which point `compose` has populated the cell with the real sink.
/// Omitted by test call sites and the replicator's self-audit log, where
/// it defaults to a no-op sink.
///
/// `failurePolicy` decides, here at the seam, what a row that did not land
/// means to the caller (see `IAuditLog`): `LogAndContinue` (default) and
/// `DegradeToFile` (spill to `fallbackStore`; the replay service drains it
/// back once the store recovers) answer `Ok`, `RefuseAction` answers the
/// `AuditWriteError`. A serialisation failure under `DegradeToFile` has no
/// envelope to spill and is counted and logged as lost.
///
/// `chainHeadStorage` holds the permission chain heads as documents (one
/// guarded write per permission row); without it the head is derived from
/// the stream on every permission row.
type EventStoreAuditLog
    (
        eventStore: IEventStore,
        logger: ILogger,
        ?metricsLookup: unit -> Metrics.IMetricsSink,
        ?failurePolicy: AuditFailurePolicy,
        ?fallbackStore: AuditFallbackStore.AuditFallbackStore,
        ?chainHeadStorage: IBlobStorage
    ) =

    let resolveMetrics =
        defaultArg metricsLookup (fun () -> Metrics.NoOpMetricsSink() :> Metrics.IMetricsSink)

    let policy = defaultArg failurePolicy LogAndContinue

    /// Phase 114 counter + the standing Warn — the common prefix of
    /// every failure branch.
    let countAndWarn (scopeId: string) (eventTypeName: string) (reason: string) =
        (resolveMetrics ()).Increment(AuditMetrics.WriteFailuresTotal, Map [ "event_type", eventTypeName ])

        logger.Warn(sprintf "[AuditLog] write failed scope=%s eventType=%s: %s" scopeId eventTypeName reason)

    /// The permission chain head as a document: `audit-chain/{streamKey}.json`
    /// in the platform container, holding the content hash of the stream's
    /// last chained record. Advanced by one guarded write per permission row,
    /// so a second writer — another instance — chains onto the first rather
    /// than forking the stream. Absent without a blob storage, when the head
    /// is derived from the stream itself.
    let heads =
        chainHeadStorage
        |> Option.map (fun storage ->
            BlobDocumentStore<string>(
                storage,
                {
                    Encode = fun head -> JsonSerializer.SerializeToUtf8Bytes {| head = head |}
                    Decode =
                        fun bytes ->
                            try
                                let doc = JsonDocument.Parse(bytes: byte[])

                                match doc.RootElement.TryGetProperty "head" with
                                | true, h when h.ValueKind = JsonValueKind.String -> Ok(h.GetString())
                                | _ -> Error "the chain head document carries no head"
                            with ex ->
                                Error ex.Message
                },
                logger
            ))

    let headPath (scopeId: string) =
        DocPath.ofString $"audit-chain/{scopeId}.json"

    /// Phase 553.A — the stream's head derived from the stream itself: one
    /// `ReadBySource` over the scope's audit trail. The head of a stream with
    /// no head document yet (every record chained before the document
    /// existed), and the whole answer for a log composed without blob storage.
    ///
    /// `Error (reason, detail)` means the head is UNREADABLE and the caller
    /// takes the failure policy rather than guess a predecessor: a record
    /// chained onto a stale or invented head forks the stream silently.
    let readStreamHead (scopeId: string) = async {
        match! Async.Catch(eventStore.ReadBySource(scopeId, AuditSourceModule.value)) with
        | Choice2Of2 ex -> return Error("read_failed", ex.Message)
        | Choice1Of2 events ->
            let payloads =
                events
                |> List.filter (fun e -> e.EventType = PermissionAuditChain.chainedEventType)
                |> List.choose decodeAuditEvent
                |> PermissionAuditChain.permissionPayloads

            match PermissionAuditChain.readHead payloads with
            | PermissionAuditChain.HeadEmpty -> return Ok PermissionAuditChain.genesisHash
            | PermissionAuditChain.HeadAt head -> return Ok head
            | PermissionAuditChain.HeadForked candidates ->
                return
                    Error(
                        "forked",
                        sprintf "%d competing chain heads: %s" (List.length candidates) (String.concat ", " candidates)
                    )
            | PermissionAuditChain.HeadUnanchored ->
                return Error("unanchored", "every chained record is referenced as a predecessor — no head exists")
    }

    /// Link `payload` onto the stream's head and advance the head past it.
    /// With a head document this is one compare-and-swap (the store replays
    /// the link on a lost precondition, within its bounded retry); the stream
    /// is read only to seed a head document that does not exist yet.
    let advanceChainHead (scopeId: string) (payload: PermissionChangedPayload) = async {
        match heads with
        | None ->
            match! readStreamHead scopeId with
            | Ok prev -> return Ok(PermissionAuditChain.link scopeId prev payload)
            | Error e -> return Error e
        | Some docs ->
            let! seed = async {
                match! docs.Read(ScopeKey.platform, headPath scopeId) with
                | Ok(Some _) -> return Ok None
                | Ok None ->
                    match! readStreamHead scopeId with
                    | Ok streamHead -> return Ok(Some streamHead)
                    | Error e -> return Error e
                | Error e -> return Error("head_unreadable", BlobMapStoreError.describe e)
            }

            match seed with
            | Error e -> return Error e
            | Ok seedHead ->
                let! advanced =
                    docs.Update(
                        ScopeKey.platform,
                        headPath scopeId,
                        fun current ->
                            let prev =
                                current
                                |> Option.orElse seedHead
                                |> Option.defaultValue PermissionAuditChain.genesisHash

                            let linked = PermissionAuditChain.link scopeId prev payload
                            let head = linked.Chain |> Option.map _.ContentHash |> Option.defaultValue prev
                            BlobUpdate.Write(head, linked)
                    )

                match advanced with
                | Ok linked -> return Ok linked
                | Error(BlobMapStoreError.Contended _ as e) -> return Error("contended", BlobMapStoreError.describe e)
                | Error e -> return Error("head_unwritable", BlobMapStoreError.describe e)
    }

    /// Undo a head advance whose record did not persist anywhere, so the next
    /// row does not chain onto a record that does not exist. Conditional on
    /// the head still being the one this write put there; best-effort — a
    /// head that cannot be restored leaves an orphan the verifier reports.
    let restoreChainHead (scopeId: string) (linked: PermissionChangedPayload) = async {
        match heads, linked.Chain with
        | Some docs, Some link ->
            let! _ =
                docs.Update(
                    ScopeKey.platform,
                    headPath scopeId,
                    fun current ->
                        if current = Some link.ContentHash then
                            BlobUpdate.Write(link.PrevHash, ())
                        else
                            BlobUpdate.Keep()
                )

            return ()
        | _ -> return ()
    }

    /// The write path proper. Answers per the composed policy: `Ok ()` once
    /// the row lands, or — when it does not — `Ok ()` under `LogAndContinue`
    /// and `DegradeToFile` (counted, logged, and under `DegradeToFile`
    /// spilled), and the `Error` under `RefuseAction`. Never raises.
    let record (scope: AuditScope) (actor: AuditSubject) (audit: AuditEvent) : Async<Result<unit, AuditWriteError>> = async {
        let scopeId = AuditScope.streamKey scope
        let eventTypeName = AuditEvent.eventTypeName audit

        /// A row that did not land: counted and logged under every policy;
        /// the policy then decides what the caller hears.
        let failed (error: AuditWriteError) (spill: (unit -> Async<unit>) option) = async {
            countAndWarn scopeId eventTypeName (AuditWriteError.describe error)

            match policy with
            | RefuseAction -> return Error error
            | LogAndContinue -> return Ok()
            | DegradeToFile ->
                match spill with
                | Some spillIt -> do! spillIt ()
                | None -> ()

                return Ok()
        }

        // Phase 553.A — chain the permission stream. The link is formed HERE
        // because this is the only place that holds the stream's head; a
        // call site that supplies its own link (the fallback replay path
        // re-writing an already-chained record) is left alone.
        let linkedHere = ref None

        let! chained = async {
            match audit with
            | PermissionChanged payload when Option.isNone payload.Chain ->
                match! advanceChainHead scopeId payload with
                | Ok linked ->
                    linkedHere.Value <- Some linked
                    return Ok(PermissionChanged linked)
                | Error(reason, detail) ->
                    (resolveMetrics ()).Increment(AuditMetrics.ChainHeadUnreadableTotal, Map [ "reason", reason ])

                    logger.Warn(
                        sprintf
                            "[AuditLog] permission chain head unavailable scope=%s reason=%s: %s"
                            scopeId
                            reason
                            detail
                    )

                    // A deployment that would rather fail the action than
                    // complete it un-audited would equally rather fail than
                    // complete it un-EVIDENCED. Under the other policies the
                    // row is recorded unchained rather than dropped; the
                    // verifier counts every unchained record.
                    if policy = RefuseAction then
                        return Error(AuditWriteError.ChainHeadConflict scopeId)
                    else
                        return Ok audit
            | _ -> return Ok audit
        }

        match chained with
        | Error e -> return Error e
        | Ok audit ->
            // Serialisation is separated from the store write so the
            // DegradeToFile branch has an envelope to spill — a record that
            // fails to SERIALISE has nothing spillable.
            let envelope =
                try
                    Ok(
                        Events.create
                            scopeId
                            AuditSourceModule.value
                            eventTypeName
                            (serialiseAuditEvent audit |> withActor actor)
                    )
                with ex ->
                    Error ex.Message

            match envelope with
            | Error reason -> return! failed (AuditWriteError.StoreUnavailable reason) None
            | Ok evt ->
                let! written = async {
                    match! Async.Catch(eventStore.Write evt) with
                    | Choice1Of2(Ok()) -> return Ok()
                    | Choice1Of2(Error e) -> return Error(EventStoreError.describe e)
                    | Choice2Of2 ex -> return Error ex.Message
                }

                match written with
                | Ok() -> return Ok()
                | Error reason ->
                    // A spilled record keeps its link and is replayed with it;
                    // a record that persisted nowhere must not stay the head.
                    if policy <> DegradeToFile then
                        match linkedHere.Value with
                        | Some linked -> do! restoreChainHead scopeId linked
                        | None -> ()

                    let spill () = async {
                        match fallbackStore with
                        | Some fb ->
                            match! fb.Append evt with
                            | Ok() ->
                                logger.Info
                                    $"[AuditLog] event=spilled_to_fallback scope=%s{scopeId} eventType={eventTypeName} — replay service re-ingests on recovery"
                            | Error msg ->
                                logger.Error(
                                    $"[AuditLog] event=fallback_spill_failed scope=%s{scopeId} eventType={eventTypeName}: {msg} — audit record LOST",
                                    None
                                )
                        | None ->
                            logger.Error(
                                $"[AuditLog] event=fallback_missing scope=%s{scopeId} eventType={eventTypeName} — DegradeToFile with no fallback store wired; audit record LOST",
                                None
                            )
                    }

                    return! failed (AuditWriteError.StoreUnavailable reason) (Some spill)
    }

    interface IAuditLog with
        member _.Record(scope, actor, audit) = record scope actor audit

        member _.GetAuditTrail(scope, dateRange, eventType) = async {
            let! events = eventStore.ReadBySource(AuditScope.streamKey scope, AuditSourceModule.value)

            let filtered =
                events
                |> List.filter (fun e ->
                    match eventType with
                    | Some et -> e.EventType = et
                    | None -> true)
                |> List.filter (fun e ->
                    match dateRange with
                    | Some(fromTs, toTs) -> e.OccurredAt >= fromTs && e.OccurredAt <= toTs
                    | None -> true)
                |> List.sortByDescending _.OccurredAt

            return filtered |> List.choose decodeAuditEvent
        }
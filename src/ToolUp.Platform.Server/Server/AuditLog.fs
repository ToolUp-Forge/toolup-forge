module ToolUp.Platform.AuditLog

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

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
//     `WebhookApiHandler.emitAudit` (`WebhookApiHandler.fs:139`).
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

let private auditJsonOptions = FableConverters.create ()

let private toAuditJson (value: 'T) =
    JsonSerializer.Serialize(value, auditJsonOptions)

let private fromAuditJson<'T> (json: string) =
    JsonSerializer.Deserialize<'T>(json, auditJsonOptions)

// Phase 62 — `PremiumGranted` / `PremiumRevoked` are tuple-shaped DU
// cases; the serialised form is an anonymous record on the write
// path. This typed alias round-trips it back to a tuple.
type private PremiumAuditPayload = {
    SubjectUserId: string
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

// ─── Phase 114 — audit-event codec registry ──────────────────────
//
// One declarative table mapping every `AuditEvent` union case to its
// wire `EventType` discriminator + payload encode/decode. This is the
// single source of truth the three formerly-hand-maintained mirrors all
// consume:
//
//   * `serialiseAuditEvent` (the write path — was `EventStoreAuditLog.serialise`)
//   * `decodeAuditEvent` (the `GetAuditTrail` read path)
//   * `AuditReplicator`'s batch decode (was a 21-case inline match that
//     dropped 59 of ~80 event types into a silent `| _ -> None`)
//
// Before this phase the three lists drifted: `serialise` was missing 7
// cases (every emission threw + was logged-and-lost), `decodeAuditEvent`
// silently dropped 11, and the replicator dropped 59 from external SIEM
// sinks with no signal. The reflection-based exhaustiveness test in
// `ToolUp.Platform.Tests` (`AuditEventRegistryTests`) enumerates the
// union via `FSharpType.GetUnionCases` and fails the build if any case
// lacks a registry entry — adding a new `AuditEvent` case without a row
// here is a test failure, not a runtime loss (GP 6 + GP 9).
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

/// One row of the audit-event codec registry: the wire `EventType`
/// discriminator plus the encode/decode pair for that case's payload.
/// `internal` so the replicator (same assembly) and the test pack
/// (InternalsVisibleTo) share the one table.
type internal AuditEventCodec = {
    /// Wire-format `EventType` discriminator. Always equal to
    /// `AuditEvent.eventTypeName` for this entry's case — that identity
    /// is what the decode lookup and the exhaustiveness gate rest on.
    /// It is NOT necessarily the F# case identifier: the three Phase 625
    /// `ModuleArtefact*` rows deliberately emit their historical
    /// `"Artifact*"` strings (see the decision record above).
    EventType: string
    /// Serialise THIS case's payload to JSON. Returns `Some json` when
    /// `audit` is this entry's case, `None` otherwise — the table is
    /// scanned with `List.tryPick` so the first matching entry wins.
    TryEncode: AuditEvent -> string option
    /// Decode a persisted payload JSON back into this entry's
    /// `AuditEvent` case. May throw on malformed JSON; callers wrap in
    /// `try/with` so a single corrupt blob never breaks a whole query or
    /// replication batch.
    Decode: string -> AuditEvent
}

/// The registry. Order is irrelevant to correctness (encode scans by
/// `tryPick`, decode is keyed by `EventType`); kept in `eventTypeName`
/// order for diff-friendliness against the union definition.
let internal auditEventCodecs: AuditEventCodec list = [
    {
        EventType = "UserLoggedIn"
        TryEncode =
            (function
            | UserLoggedIn p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> UserLoggedIn(fromAuditJson<UserLoggedInPayload> j)
    }
    {
        EventType = "TeamCreated"
        TryEncode =
            (function
            | TeamCreated p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TeamCreated(fromAuditJson<TeamCreatedPayload> j)
    }
    {
        EventType = "MemberAdded"
        TryEncode =
            (function
            | MemberAdded p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> MemberAdded(fromAuditJson<MemberAddedPayload> j)
    }
    {
        EventType = "MemberRemoved"
        TryEncode =
            (function
            | MemberRemoved p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> MemberRemoved(fromAuditJson<MemberRemovedPayload> j)
    }
    {
        EventType = "MemberRoleChanged"
        TryEncode =
            (function
            | MemberRoleChanged p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> MemberRoleChanged(fromAuditJson<MemberRoleChangedPayload> j)
    }
    {
        EventType = "FileUploaded"
        TryEncode =
            (function
            | FileUploaded p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> FileUploaded(fromAuditJson<FileUploadedPayload> j)
    }
    {
        EventType = "FileDeleted"
        TryEncode =
            (function
            | FileDeleted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> FileDeleted(fromAuditJson<FileDeletedPayload> j)
    }
    {
        EventType = "FileReprocessed"
        TryEncode =
            (function
            | FileReprocessed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> FileReprocessed(fromAuditJson<FileReprocessedPayload> j)
    }
    {
        EventType = "DataStoreReset"
        TryEncode =
            (function
            | DataStoreReset p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> DataStoreReset(fromAuditJson<DataStoreResetPayload> j)
    }
    {
        EventType = "AnalysisRun"
        TryEncode =
            (function
            | AnalysisRun p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> AnalysisRun(fromAuditJson<AnalysisRunPayload> j)
    }
    {
        EventType = "PermissionChanged"
        TryEncode =
            (function
            | PermissionChanged p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> PermissionChanged(fromAuditJson<PermissionChangedPayload> j)
    }
    {
        EventType = "NotificationSent"
        TryEncode =
            (function
            | NotificationSent p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> NotificationSent(fromAuditJson<NotificationSentPayload> j)
    }
    {
        EventType = "NotificationDeliveryFailed"
        TryEncode =
            (function
            | NotificationDeliveryFailed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> NotificationDeliveryFailed(fromAuditJson<NotificationDeliveryFailedPayload> j)
    }
    {
        EventType = "HealthStateChanged"
        TryEncode =
            (function
            | HealthStateChanged p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> HealthStateChanged(fromAuditJson<HealthStateChangedPayload> j)
    }
    {
        EventType = "EncryptionKeyCreated"
        TryEncode =
            (function
            | EncryptionKeyCreated p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> EncryptionKeyCreated(fromAuditJson<EncryptionKeyEventPayload> j)
    }
    {
        EventType = "EncryptionKeyRotated"
        TryEncode =
            (function
            | EncryptionKeyRotated p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> EncryptionKeyRotated(fromAuditJson<EncryptionKeyEventPayload> j)
    }
    {
        EventType = "EncryptionKeyDestroyed"
        TryEncode =
            (function
            | EncryptionKeyDestroyed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> EncryptionKeyDestroyed(fromAuditJson<EncryptionKeyEventPayload> j)
    }
    {
        EventType = "EncryptionKeyDestroyAcknowledged"
        TryEncode =
            (function
            | EncryptionKeyDestroyAcknowledged p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> EncryptionKeyDestroyAcknowledged(fromAuditJson<EncryptionKeyDestroyAckPayload> j)
    }
    {
        EventType = "EntityCreated"
        TryEncode =
            (function
            | EntityCreated p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> EntityCreated(fromAuditJson<EntityLifecycleEventPayload> j)
    }
    {
        EventType = "EntityUpdated"
        TryEncode =
            (function
            | EntityUpdated p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> EntityUpdated(fromAuditJson<EntityLifecycleEventPayload> j)
    }
    {
        EventType = "EntityDeleted"
        TryEncode =
            (function
            | EntityDeleted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> EntityDeleted(fromAuditJson<EntityLifecycleEventPayload> j)
    }
    {
        EventType = "FormSubmitted"
        TryEncode =
            (function
            | FormSubmitted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> FormSubmitted(fromAuditJson<FormSubmittedPayload> j)
    }
    {
        EventType = "FormSubmissionUpdated"
        TryEncode =
            (function
            | FormSubmissionUpdated p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> FormSubmissionUpdated(fromAuditJson<FormSubmissionUpdatedPayload> j)
    }
    {
        EventType = "WorkflowTransitioned"
        TryEncode =
            (function
            | WorkflowTransitioned p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> WorkflowTransitioned(fromAuditJson<WorkflowTransitionedPayload> j)
    }
    {
        EventType = "AuditSinkDelivered"
        TryEncode =
            (function
            | AuditSinkDelivered p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> AuditSinkDelivered(fromAuditJson<AuditSinkDeliveredPayload> j)
    }
    {
        EventType = "AuditSinkFailed"
        TryEncode =
            (function
            | AuditSinkFailed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> AuditSinkFailed(fromAuditJson<AuditSinkFailedPayload> j)
    }
    {
        EventType = "AuditSinkDeadLettered"
        TryEncode =
            (function
            | AuditSinkDeadLettered p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> AuditSinkDeadLettered(fromAuditJson<AuditSinkDeadLetteredPayload> j)
    }
    {
        EventType = "AuditEventDecodeFailed"
        TryEncode =
            (function
            | AuditEventDecodeFailed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> AuditEventDecodeFailed(fromAuditJson<AuditEventDecodeFailedPayload> j)
    }
    {
        EventType = "NotificationSilentlySkipped"
        TryEncode =
            (function
            | NotificationSilentlySkipped p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> NotificationSilentlySkipped(fromAuditJson<NotificationSilentlySkippedPayload> j)
    }
    {
        EventType = "OAuthConnected"
        TryEncode =
            (function
            | OAuthConnected p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> OAuthConnected(fromAuditJson<OAuthConnectedPayload> j)
    }
    {
        EventType = "OAuthDisconnected"
        TryEncode =
            (function
            | OAuthDisconnected p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> OAuthDisconnected(fromAuditJson<OAuthDisconnectedPayload> j)
    }
    {
        EventType = "OAuthRefreshFailed"
        TryEncode =
            (function
            | OAuthRefreshFailed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> OAuthRefreshFailed(fromAuditJson<OAuthRefreshFailedPayload> j)
    }
    {
        EventType = "OAuth1aConnected"
        TryEncode =
            (function
            | OAuth1aConnected p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> OAuth1aConnected(fromAuditJson<OAuth1aConnectedPayload> j)
    }
    {
        EventType = "OAuth1aDisconnected"
        TryEncode =
            (function
            | OAuth1aDisconnected p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> OAuth1aDisconnected(fromAuditJson<OAuth1aDisconnectedPayload> j)
    }
    {
        EventType = "OAuth1aSigningFailed"
        TryEncode =
            (function
            | OAuth1aSigningFailed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> OAuth1aSigningFailed(fromAuditJson<OAuth1aSigningFailedPayload> j)
    }
    {
        EventType = "OAuthTokenRefreshed"
        TryEncode =
            (function
            | OAuthTokenRefreshed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> OAuthTokenRefreshed(fromAuditJson<OAuthTokenRefreshedPayload> j)
    }
    {
        EventType = "OAuthTokenRefreshFailed"
        TryEncode =
            (function
            | OAuthTokenRefreshFailed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> OAuthTokenRefreshFailed(fromAuditJson<OAuthTokenRefreshFailedPayload> j)
    }
    {
        EventType = "OAuthRefreshTokenInvalidated"
        TryEncode =
            (function
            | OAuthRefreshTokenInvalidated p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> OAuthRefreshTokenInvalidated(fromAuditJson<OAuthRefreshTokenInvalidatedPayload> j)
    }
    {
        EventType = "OAuthRefreshDeadLettered"
        TryEncode =
            (function
            | OAuthRefreshDeadLettered p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> OAuthRefreshDeadLettered(fromAuditJson<OAuthRefreshDeadLetteredPayload> j)
    }
    {
        EventType = "PlatformAdminAssigned"
        TryEncode =
            (function
            | PlatformAdminAssigned p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> PlatformAdminAssigned(fromAuditJson<PlatformAdminAssignedPayload> j)
    }
    {
        EventType = "PlatformAdminRevoked"
        TryEncode =
            (function
            | PlatformAdminRevoked p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> PlatformAdminRevoked(fromAuditJson<PlatformAdminRevokedPayload> j)
    }
    {
        EventType = "PlatformDocumentUploaded"
        TryEncode =
            (function
            | PlatformDocumentUploaded p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> PlatformDocumentUploaded(fromAuditJson<PlatformDocumentUploadedPayload> j)
    }
    {
        EventType = "PlatformDocumentDeleted"
        TryEncode =
            (function
            | PlatformDocumentDeleted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> PlatformDocumentDeleted(fromAuditJson<PlatformDocumentDeletedPayload> j)
    }
    {
        EventType = "ShareTokenIssued"
        TryEncode =
            (function
            | ShareTokenIssued p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ShareTokenIssued(fromAuditJson<ShareTokenIssuedPayload> j)
    }
    {
        EventType = "ShareTokenUsed"
        TryEncode =
            (function
            | ShareTokenUsed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ShareTokenUsed(fromAuditJson<ShareTokenUsedPayload> j)
    }
    {
        EventType = "ShareTokenRevoked"
        TryEncode =
            (function
            | ShareTokenRevoked p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ShareTokenRevoked(fromAuditJson<ShareTokenRevokedPayload> j)
    }
    {
        EventType = "ConversationExported"
        TryEncode =
            (function
            | ConversationExported p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ConversationExported(fromAuditJson<ConversationExportPayload> j)
    }
    {
        EventType = "BeaconRejected"
        TryEncode =
            (function
            | BeaconRejected p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> BeaconRejected(fromAuditJson<BeaconRejectedPayload> j)
    }
    {
        EventType = "ConfigDrift"
        TryEncode =
            (function
            | ConfigDrift p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ConfigDrift(fromAuditJson<ConfigDriftPayload> j)
    }
    {
        EventType = "DiagnosticBundleAccessed"
        TryEncode =
            (function
            | DiagnosticBundleAccessed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> DiagnosticBundleAccessed(fromAuditJson<DiagnosticBundleAccessedPayload> j)
    }
    {
        EventType = "RateLimitWaited"
        TryEncode =
            (function
            | RateLimitWaited p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> RateLimitWaited(fromAuditJson<RateLimitWaitedPayload> j)
    }
    {
        EventType = "RateLimitRefused"
        TryEncode =
            (function
            | RateLimitRefused p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> RateLimitRefused(fromAuditJson<RateLimitRefusedPayload> j)
    }
    {
        EventType = "DataSubjectRequest"
        TryEncode =
            (function
            | DataSubjectRequest p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> DataSubjectRequest(fromAuditJson<DataSubjectRequestAuditPayload> j)
    }
    {
        EventType = "ConversationStarted"
        TryEncode =
            (function
            | ConversationStarted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ConversationStarted(fromAuditJson<ConversationStartedPayload> j)
    }
    {
        EventType = "ConversationTurnAppended"
        TryEncode =
            (function
            | ConversationTurnAppended p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ConversationTurnAppended(fromAuditJson<ConversationTurnAppendedPayload> j)
    }
    {
        EventType = "ConversationCompleted"
        TryEncode =
            (function
            | ConversationCompleted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ConversationCompleted(fromAuditJson<ConversationCompletedPayload> j)
    }
    {
        EventType = "ConversationErased"
        TryEncode =
            (function
            | ConversationErased p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ConversationErased(fromAuditJson<ConversationErasedPayload> j)
    }
    {
        EventType = "ConversationReplayed"
        TryEncode =
            (function
            | ConversationReplayed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ConversationReplayed(fromAuditJson<ConversationReplayedPayload> j)
    }
    {
        EventType = "AssetUploaded"
        TryEncode =
            (function
            | AssetUploaded p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> AssetUploaded(fromAuditJson<AssetUploadedPayload> j)
    }
    {
        EventType = "AssetDeleted"
        TryEncode =
            (function
            | AssetDeleted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> AssetDeleted(fromAuditJson<AssetDeletedPayload> j)
    }
    {
        EventType = "TeamCreationDenied"
        TryEncode =
            (function
            | TeamCreationDenied p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TeamCreationDenied(fromAuditJson<TeamCreationDeniedPayload> j)
    }
    {
        EventType = "TeamArchived"
        TryEncode =
            (function
            | TeamArchived p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TeamArchived(fromAuditJson<TeamArchivedPayload> j)
    }
    {
        EventType = "TeamRestored"
        TryEncode =
            (function
            | TeamRestored p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TeamRestored(fromAuditJson<TeamRestoredPayload> j)
    }
    {
        EventType = "TeamDeleted"
        TryEncode =
            (function
            | TeamDeleted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TeamDeleted(fromAuditJson<TeamDeletedPayload> j)
    }
    {
        EventType = "TeamOwnershipTransferred"
        TryEncode =
            (function
            | TeamOwnershipTransferred p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TeamOwnershipTransferred(fromAuditJson<TeamOwnershipTransferredPayload> j)
    }
    {
        EventType = "TeamInviteIssued"
        TryEncode =
            (function
            | TeamInviteIssued p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TeamInviteIssued(fromAuditJson<TeamInviteIssuedPayload> j)
    }
    {
        EventType = "TeamInviteAccepted"
        TryEncode =
            (function
            | TeamInviteAccepted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TeamInviteAccepted(fromAuditJson<TeamInviteAcceptedPayload> j)
    }
    {
        EventType = "TeamInviteAcceptedFromPending"
        TryEncode =
            (function
            | TeamInviteAcceptedFromPending p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TeamInviteAcceptedFromPending(fromAuditJson<TeamInviteAcceptedFromPendingPayload> j)
    }
    {
        EventType = "TeamInviteAcceptedFromPendingFailed"
        TryEncode =
            (function
            | TeamInviteAcceptedFromPendingFailed p -> Some(toAuditJson p)
            | _ -> None)
        Decode =
            fun j -> TeamInviteAcceptedFromPendingFailed(fromAuditJson<TeamInviteAcceptedFromPendingFailedPayload> j)
    }
    {
        EventType = "TeamInviteRevoked"
        TryEncode =
            (function
            | TeamInviteRevoked p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TeamInviteRevoked(fromAuditJson<TeamInviteRevokedPayload> j)
    }
    {
        EventType = "TeamInviteRedeemed"
        TryEncode =
            (function
            | TeamInviteRedeemed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TeamInviteRedeemed(fromAuditJson<TeamInviteRedeemedPayload> j)
    }
    {
        EventType = "TeamInviteExpired"
        TryEncode =
            (function
            | TeamInviteExpired p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TeamInviteExpired(fromAuditJson<TeamInviteExpiredPayload> j)
    }
    {
        EventType = "WorkflowActionExecuted"
        TryEncode =
            (function
            | WorkflowActionExecuted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> WorkflowActionExecuted(fromAuditJson<WorkflowActionExecutedPayload> j)
    }
    {
        EventType = "ConsentRecorded"
        TryEncode =
            (function
            | ConsentRecorded p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ConsentRecorded(fromAuditJson<ConsentEvent> j)
    }
    {
        EventType = "AdImpressionRecorded"
        TryEncode =
            (function
            | AdImpressionRecorded p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> AdImpressionRecorded(fromAuditJson<AdImpression> j)
    }
    {
        EventType = "AdClickRecorded"
        TryEncode =
            (function
            | AdClickRecorded p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> AdClickRecorded(fromAuditJson<AdClick> j)
    }
    {
        EventType = "PremiumGranted"
        TryEncode =
            (function
            | PremiumGranted(subjectUserId, grantor, reason, occurredAt) ->
                Some(
                    toAuditJson {
                        SubjectUserId = subjectUserId
                        Grantor = grantor
                        Reason = reason
                        OccurredAt = occurredAt
                    }
                )
            | _ -> None)
        Decode =
            fun j ->
                let p = fromAuditJson<PremiumAuditPayload> j
                PremiumGranted(p.SubjectUserId, p.Grantor, p.Reason, p.OccurredAt)
    }
    {
        EventType = "PremiumRevoked"
        TryEncode =
            (function
            | PremiumRevoked(subjectUserId, grantor, reason, occurredAt) ->
                Some(
                    toAuditJson {
                        SubjectUserId = subjectUserId
                        Grantor = grantor
                        Reason = reason
                        OccurredAt = occurredAt
                    }
                )
            | _ -> None)
        Decode =
            fun j ->
                let p = fromAuditJson<PremiumAuditPayload> j
                PremiumRevoked(p.SubjectUserId, p.Grantor, p.Reason, p.OccurredAt)
    }
    {
        EventType = "AdSlotConfigCreated"
        TryEncode =
            (function
            | AdSlotConfigCreated(slotId, actor, occurredAt) ->
                Some(
                    toAuditJson {
                        SlotId = slotId
                        Actor = actor
                        OccurredAt = occurredAt
                    }
                )
            | _ -> None)
        Decode =
            fun j ->
                let p = fromAuditJson<AdSlotConfigAuditPayload> j
                AdSlotConfigCreated(p.SlotId, p.Actor, p.OccurredAt)
    }
    {
        EventType = "AdSlotConfigUpdated"
        TryEncode =
            (function
            | AdSlotConfigUpdated(slotId, actor, occurredAt) ->
                Some(
                    toAuditJson {
                        SlotId = slotId
                        Actor = actor
                        OccurredAt = occurredAt
                    }
                )
            | _ -> None)
        Decode =
            fun j ->
                let p = fromAuditJson<AdSlotConfigAuditPayload> j
                AdSlotConfigUpdated(p.SlotId, p.Actor, p.OccurredAt)
    }
    {
        EventType = "AdSlotConfigDeleted"
        TryEncode =
            (function
            | AdSlotConfigDeleted(slotId, actor, occurredAt) ->
                Some(
                    toAuditJson {
                        SlotId = slotId
                        Actor = actor
                        OccurredAt = occurredAt
                    }
                )
            | _ -> None)
        Decode =
            fun j ->
                let p = fromAuditJson<AdSlotConfigAuditPayload> j
                AdSlotConfigDeleted(p.SlotId, p.Actor, p.OccurredAt)
    }
    {
        EventType = "AnonymousSessionMigrated"
        TryEncode =
            (function
            | AnonymousSessionMigrated p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> AnonymousSessionMigrated(fromAuditJson<AnonymousSessionMigratedPayload> j)
    }
    {
        EventType = "AuthScopeResolutionFailed"
        TryEncode =
            (function
            | AuthScopeResolutionFailed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> AuthScopeResolutionFailed(fromAuditJson<ScopeResolutionFailedPayload> j)
    }
    {
        EventType = "SurfaceDenied"
        TryEncode =
            (function
            | SurfaceDenied p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> SurfaceDenied(fromAuditJson<SurfaceDeniedPayload> j)
    }
    // Phase 625 — the three rows below are the ONLY rows in this table
    // whose `EventType` differs from its `AuditEvent` case identifier.
    // That divergence is deliberate and pinned; see the decision record
    // in this file's registry header.
    {
        EventType = "ArtifactSigned"
        TryEncode =
            (function
            | ModuleArtefactSigned p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ModuleArtefactSigned(fromAuditJson<ModuleArtefactSignedPayload> j)
    }
    {
        EventType = "ArtifactVerified"
        TryEncode =
            (function
            | ModuleArtefactVerified p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ModuleArtefactVerified(fromAuditJson<ModuleArtefactVerifiedPayload> j)
    }
    {
        EventType = "ArtifactRejected"
        TryEncode =
            (function
            | ModuleArtefactRejected p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ModuleArtefactRejected(fromAuditJson<ModuleArtefactRejectedPayload> j)
    }
    {
        EventType = "SyntheticSampleGenerated"
        TryEncode =
            (function
            | SyntheticSampleGenerated p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> SyntheticSampleGenerated(fromAuditJson<SyntheticSampleGeneratedPayload> j)
    }
    {
        EventType = "SchemaOnlyAccessAttempted"
        TryEncode =
            (function
            | SchemaOnlyAccessAttempted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> SchemaOnlyAccessAttempted(fromAuditJson<SchemaOnlyAccessAttemptedPayload> j)
    }
    {
        EventType = "PeerCallCompleted"
        TryEncode =
            (function
            | PeerCallCompleted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> PeerCallCompleted(fromAuditJson<PeerCallCompletedPayload> j)
    }
    // Phase 310 — the terminal row for a long-running peer call. Its
    // schedule-time twin above records only that the call was accepted.
    {
        EventType = "PeerJobCompleted"
        TryEncode =
            (function
            | PeerJobCompleted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> PeerJobCompleted(fromAuditJson<PeerJobCompletedPayload> j)
    }
    // Phase 311 — the composed clean-room gate's decision over one gated
    // contract answer. The only place the withhold reason is recorded; the
    // wire refusal deliberately carries none.
    {
        EventType = "PeerCleanRoomDecision"
        TryEncode =
            (function
            | PeerCleanRoomDecision p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> PeerCleanRoomDecision(fromAuditJson<PeerCleanRoomDecisionPayload> j)
    }
    {
        EventType = "FederationRoundCompleted"
        TryEncode =
            (function
            | FederationRoundCompleted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> FederationRoundCompleted(fromAuditJson<FederationRoundCompletedPayload> j)
    }
    {
        EventType = "FederationParticipantDropped"
        TryEncode =
            (function
            | FederationParticipantDropped p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> FederationParticipantDropped(fromAuditJson<FederationParticipantDroppedPayload> j)
    }
    {
        EventType = "FederationRunAborted"
        TryEncode =
            (function
            | FederationRunAborted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> FederationRunAborted(fromAuditJson<FederationRunAbortedPayload> j)
    }
    {
        EventType = "ArtefactSigned"
        TryEncode =
            (function
            | ArtefactSigned p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ArtefactSigned(fromAuditJson<ArtefactSignedPayload> j)
    }
    {
        EventType = "SigningKeyRotated"
        TryEncode =
            (function
            | SigningKeyRotated p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> SigningKeyRotated(fromAuditJson<SigningKeyRotatedPayload> j)
    }
    {
        EventType = "ClassifiedFieldRead"
        TryEncode =
            (function
            | ClassifiedFieldRead p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ClassifiedFieldRead(fromAuditJson<ClassifiedFieldReadPayload> j)
    }
    {
        EventType = "ClassifiedFieldWritten"
        TryEncode =
            (function
            | ClassifiedFieldWritten p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ClassifiedFieldWritten(fromAuditJson<ClassifiedFieldWrittenPayload> j)
    }
    {
        EventType = "TenantProvisioned"
        TryEncode =
            (function
            | TenantProvisioned p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TenantProvisioned(fromAuditJson<TenantProvisionedPayload> j)
    }
    {
        EventType = "TenantDeprovisioned"
        TryEncode =
            (function
            | TenantDeprovisioned p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TenantDeprovisioned(fromAuditJson<TenantDeprovisionedPayload> j)
    }
    {
        EventType = "TenantLifecycleHookFailed"
        TryEncode =
            (function
            | TenantLifecycleHookFailed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TenantLifecycleHookFailed(fromAuditJson<TenantLifecycleHookFailedPayload> j)
    }
    // Phase 54j — export-then-erase archive marker (codec registration was
    // omitted when the case shipped; added here so the case round-trips
    // through the registry and external sinks, per the Phase 114 gate).
    {
        EventType = "TenantDataExported"
        TryEncode =
            (function
            | TenantDataExported p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TenantDataExported(fromAuditJson<TenantDataExportedPayload> j)
    }
    // Phase 54i — offboard confirmation-gate audit events (append-only).
    {
        EventType = "TenantOffboardConfirmationRequested"
        TryEncode =
            (function
            | TenantOffboardConfirmationRequested p -> Some(toAuditJson p)
            | _ -> None)
        Decode =
            fun j -> TenantOffboardConfirmationRequested(fromAuditJson<TenantOffboardConfirmationRequestedPayload> j)
    }
    {
        EventType = "TenantOffboardConfirmationApproved"
        TryEncode =
            (function
            | TenantOffboardConfirmationApproved p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TenantOffboardConfirmationApproved(fromAuditJson<TenantOffboardConfirmationApprovedPayload> j)
    }
    {
        EventType = "TenantOffboardConfirmationRefused"
        TryEncode =
            (function
            | TenantOffboardConfirmationRefused p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TenantOffboardConfirmationRefused(fromAuditJson<TenantOffboardConfirmationRefusedPayload> j)
    }
    // Phase 54f — scheduled / grace-period offboard audit events (append-only).
    {
        EventType = "TenantDeprovisionScheduled"
        TryEncode =
            (function
            | TenantDeprovisionScheduled p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TenantDeprovisionScheduled(fromAuditJson<TenantDeprovisionScheduledPayload> j)
    }
    {
        EventType = "TenantDeprovisionCancelled"
        TryEncode =
            (function
            | TenantDeprovisionCancelled p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> TenantDeprovisionCancelled(fromAuditJson<TenantDeprovisionCancelledPayload> j)
    }
    {
        EventType = "KnowledgeOriginalRetrieved"
        TryEncode =
            (function
            | KnowledgeOriginalRetrieved p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> KnowledgeOriginalRetrieved(fromAuditJson<KnowledgeOriginalRetrievedPayload> j)
    }
    {
        EventType = "KnowledgeOriginalRetrievalDenied"
        TryEncode =
            (function
            | KnowledgeOriginalRetrievalDenied p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> KnowledgeOriginalRetrievalDenied(fromAuditJson<KnowledgeOriginalRetrievalDeniedPayload> j)
    }
    {
        EventType = "RemotingMethodAudited"
        TryEncode =
            (function
            | RemotingMethodAudited p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> RemotingMethodAudited(fromAuditJson<RemotingMethodAuditedPayload> j)
    }
    // Phase 115 — KB scope wipe fan-out outcome (append-only registration).
    {
        EventType = "KnowledgeScopeErased"
        TryEncode =
            (function
            | KnowledgeScopeErased p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> KnowledgeScopeErased(fromAuditJson<KnowledgeScopeErasedPayload> j)
    }
    // Phase 120 — uniform structured authz-denial row (append-only registration).
    {
        EventType = "AuthorizationDenied"
        TryEncode =
            (function
            | AuthorizationDenied p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> AuthorizationDenied(fromAuditJson<AuthorizationDeniedPayload> j)
    }
    // Phase 272 — hosted-tree action audit row (append-only registration).
    {
        EventType = "HostActionDispatched"
        TryEncode =
            (function
            | HostActionDispatched p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> HostActionDispatched(fromAuditJson<HostActionDispatchedPayload> j)
    }
    // Phase 188 — field-classification egress/DLP gate deny row (append-only registration).
    {
        EventType = "EgressBlocked"
        TryEncode =
            (function
            | EgressBlocked p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> EgressBlocked(fromAuditJson<EgressBlockedPayload> j)
    }
    // Phase 14v — RAG vector-index corrupt-load audit row (append-only registration).
    {
        EventType = "KnowledgeIndexLoadFailed"
        TryEncode =
            (function
            | KnowledgeIndexLoadFailed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> KnowledgeIndexLoadFailed(fromAuditJson<KnowledgeIndexLoadFailedPayload> j)
    }
    // Phase 303 — ingestion-queue drop audit row (append-only registration).
    {
        EventType = "KnowledgeIngestionDropped"
        TryEncode =
            (function
            | KnowledgeIngestionDropped p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> KnowledgeIngestionDropped(fromAuditJson<KnowledgeIngestionDroppedPayload> j)
    }
    // Phase 14x — KB upload content-hash dedup audit row (append-only registration).
    {
        EventType = "KnowledgeDocumentDeduplicated"
        TryEncode =
            (function
            | KnowledgeDocumentDeduplicated p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> KnowledgeDocumentDeduplicated(fromAuditJson<KnowledgeDocumentDeduplicatedPayload> j)
    }
    // Phase 512 — KB retention-sweep purge audit row (append-only registration).
    {
        EventType = "KnowledgeDocumentsPurged"
        TryEncode =
            (function
            | KnowledgeDocumentsPurged p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> KnowledgeDocumentsPurged(fromAuditJson<KnowledgeDocumentsPurgedPayload> j)
    }
    {
        EventType = "PasskeyCredentialRegistered"
        TryEncode =
            (function
            | PasskeyCredentialRegistered p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> PasskeyCredentialRegistered(fromAuditJson<PasskeyCredentialRegisteredPayload> j)
    }
    {
        EventType = "PasskeyCredentialRemoved"
        TryEncode =
            (function
            | PasskeyCredentialRemoved p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> PasskeyCredentialRemoved(fromAuditJson<PasskeyCredentialRemovedPayload> j)
    }
    // Phase 449 — model-fit envelope lifecycle audit rows (append-only registration).
    {
        EventType = "ModelFitStarted"
        TryEncode =
            (function
            | ModelFitStarted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ModelFitStarted(fromAuditJson<ModelFitStartedPayload> j)
    }
    {
        EventType = "ModelFitCompleted"
        TryEncode =
            (function
            | ModelFitCompleted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ModelFitCompleted(fromAuditJson<ModelFitCompletedPayload> j)
    }
    {
        EventType = "ModelFitGateFailed"
        TryEncode =
            (function
            | ModelFitGateFailed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ModelFitGateFailed(fromAuditJson<ModelFitGateFailedPayload> j)
    }
    // Phase 599 — batch fit submission audit row (append-only registration).
    {
        EventType = "ModelFitBatchSubmitted"
        TryEncode =
            (function
            | ModelFitBatchSubmitted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ModelFitBatchSubmitted(fromAuditJson<ModelFitBatchSubmittedPayload> j)
    }
    // Phase 453 — model-registry lifecycle audit rows (append-only registration).
    {
        EventType = "ModelArtifactRegistered"
        TryEncode =
            (function
            | ModelArtifactRegistered p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ModelArtifactRegistered(fromAuditJson<ModelArtifactRegisteredPayload> j)
    }
    {
        EventType = "ModelArtifactTransitioned"
        TryEncode =
            (function
            | ModelArtifactTransitioned p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ModelArtifactTransitioned(fromAuditJson<ModelArtifactTransitionedPayload> j)
    }
    {
        EventType = "ModelArtifactTransitionDenied"
        TryEncode =
            (function
            | ModelArtifactTransitionDenied p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ModelArtifactTransitionDenied(fromAuditJson<ModelArtifactTransitionDeniedPayload> j)
    }
    // Phase 454 — model-scoring lifecycle audit rows (append-only registration).
    {
        EventType = "ModelScored"
        TryEncode =
            (function
            | ModelScored p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ModelScored(fromAuditJson<ModelScoredPayload> j)
    }
    {
        EventType = "ModelScoreRefused"
        TryEncode =
            (function
            | ModelScoreRefused p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ModelScoreRefused(fromAuditJson<ModelScoreRefusedPayload> j)
    }
    // Phase 456 — model-evaluation audit row (append-only registration).
    {
        EventType = "ModelEvaluated"
        TryEncode =
            (function
            | ModelEvaluated p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> ModelEvaluated(fromAuditJson<ModelEvaluatedPayload> j)
    }
    // Phase 482 / 487 — dataset provenance & virtual-spill audit rows (append-only registration).
    {
        EventType = "DatasetSpillCreated"
        TryEncode =
            (function
            | DatasetSpillCreated p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> DatasetSpillCreated(fromAuditJson<DatasetSpillCreatedPayload> j)
    }
    {
        EventType = "DatasetSpillDeleted"
        TryEncode =
            (function
            | DatasetSpillDeleted p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> DatasetSpillDeleted(fromAuditJson<DatasetSpillDeletedPayload> j)
    }
    // Phase 601 — assembly re-vintage audit row (append-only registration).
    {
        EventType = "DatasetRevintaged"
        TryEncode =
            (function
            | DatasetRevintaged p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> DatasetRevintaged(fromAuditJson<DatasetRevintagedPayload> j)
    }
    {
        EventType = "DatasetDeclassified"
        TryEncode =
            (function
            | DatasetDeclassified p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> DatasetDeclassified(fromAuditJson<DatasetDeclassifiedPayload> j)
    }
    {
        EventType = "DatasetPolicyDenied"
        TryEncode =
            (function
            | DatasetPolicyDenied p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> DatasetPolicyDenied(fromAuditJson<DatasetPolicyDeniedPayload> j)
    }
    {
        EventType = "SchemaProposed"
        TryEncode =
            (function
            | SchemaProposed p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> SchemaProposed(fromAuditJson<SchemaProposedPayload> j)
    }
    {
        EventType = "SchemaApproved"
        TryEncode =
            (function
            | SchemaApproved p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> SchemaApproved(fromAuditJson<SchemaApprovedPayload> j)
    }
    {
        EventType = "SchemaChanged"
        TryEncode =
            (function
            | SchemaChanged p -> Some(toAuditJson p)
            | _ -> None)
        Decode = fun j -> SchemaChanged(fromAuditJson<SchemaChangedPayload> j)
    }
]

/// Decode lookup keyed by wire `EventType`. Built once at module init.
let internal auditDecoderByType: Map<string, string -> AuditEvent> =
    auditEventCodecs |> List.map (fun c -> c.EventType, c.Decode) |> Map.ofList

/// Serialise an `AuditEvent` payload to JSON via the registry. Total
/// over the union when the `AuditEventRegistryTests` exhaustiveness gate
/// passes; the `failwithf` is an invariant guard for the unreachable
/// case where a union case has no registry row. Unlike the pre-114
/// throw-and-Warn behaviour, this throw lands inside `Record`'s
/// try/with, so even the impossible case is counted against
/// `audit_write_failures_total` rather than silently advancing.
let internal serialiseAuditEvent (audit: AuditEvent) : string =
    match auditEventCodecs |> List.tryPick (fun c -> c.TryEncode audit) with
    | Some json -> json
    | None ->
        failwithf
            "AuditEvent case '%s' has no codec-registry entry (Phase 114 — add it to AuditLog.auditEventCodecs)"
            (AuditEvent.eventTypeName audit)

/// Decode a persisted audit `(eventType, payload)` pair via the
/// registry, surfacing WHY a decode failed so the replicator can record
/// it. `Error` covers both an unrecognised / future `eventType` and a
/// malformed payload for a known type — Phase 114 routes both into the
/// `AuditEventDecodeFailed` summary row instead of advancing the cursor
/// silently (Gap C1). Shared by the replicator (same assembly) and the
/// `GetAuditTrail` read path below.
let internal tryDecodeAuditEvent (eventType: string) (payload: string) : Result<AuditEvent, string> =
    match Map.tryFind eventType auditDecoderByType with
    | None -> Error(sprintf "unknown event type '%s'" eventType)
    | Some decode ->
        try
            Ok(decode payload)
        with ex ->
            Error(sprintf "payload decode error: %s" ex.Message)

/// Decode a persisted `ModuleEvent` whose `SourceModule` is
/// `AuditSourceModule.value` back into an `AuditEvent`. Returns
/// `None` for unknown `EventType` strings or malformed payloads —
/// callers filter `None`s out so a single corrupt blob doesn't
/// break the whole query (audit trails are append-only and the
/// payload format is owned by the SDK, but defensive decoding
/// keeps the trail readable across SDK upgrades). `internal` (was
/// `private`) per Phase 114 so the replicator shares the one decoder.
let internal decodeAuditEvent (evt: ModuleEvent) : AuditEvent option =
    match tryDecodeAuditEvent evt.EventType evt.Payload with
    | Ok audit -> Some audit
    | Error _ -> None

/// Phase 9t — raised out of `EventStoreAuditLog.Record` when
/// `AuditFailurePolicy = RefuseAction` and the audit write fails: the
/// user's action must NOT complete un-audited, so the failure
/// propagates through the action handler and surfaces as a 500 with a
/// clear "audit unavailable" message rather than a silent compliance
/// gap.
type AuditWriteRefusedException(scopeId: string, eventType: string, inner: exn) =
    inherit
        Exception(
            $"audit unavailable — refusing the action rather than completing it un-audited (scope={scopeId}, eventType={eventType}): {inner.Message}",
            inner
        )

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
/// Phase 9t — `failurePolicy` selects what a write failure does beyond
/// the Phase 114 counter: `LogAndContinue` (default — prior behaviour
/// byte-for-byte), `RefuseAction` (raise `AuditWriteRefusedException`),
/// or `DegradeToFile` (spill to `fallbackStore`; the replay service
/// drains it back once the store recovers). A serialisation failure
/// under `DegradeToFile` cannot spill (there is no envelope to write) —
/// it degrades to the `LogAndContinue` shape for that record.
type EventStoreAuditLog
    (
        eventStore: IEventStore,
        logger: ILogger,
        ?metricsLookup: unit -> Metrics.IMetricsSink,
        ?failurePolicy: AuditFailurePolicy,
        ?fallbackStore: AuditFallbackStore.AuditFallbackStore
    ) =

    let resolveMetrics =
        defaultArg metricsLookup (fun () -> Metrics.NoOpMetricsSink() :> Metrics.IMetricsSink)

    let policy = defaultArg failurePolicy LogAndContinue

    /// Phase 114 counter + the standing Warn — the common prefix of
    /// every failure branch.
    let countAndWarn (scopeId: string) (eventTypeName: string) (ex: exn) =
        (resolveMetrics ()).Increment(AuditMetrics.WriteFailuresTotal, Map [ "event_type", eventTypeName ])

        logger.Warn(sprintf "[AuditLog] write failed scope=%s eventType=%s: %s" scopeId eventTypeName ex.Message)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async {
            let eventTypeName = AuditEvent.eventTypeName audit

            // Serialisation is separated from the store write so the
            // DegradeToFile branch has an envelope to spill — a record
            // that fails to SERIALISE has nothing spillable and takes
            // the count+log shape under every policy except Refuse.
            let envelope =
                try
                    Ok(Events.create scopeId AuditSourceModule.value eventTypeName (serialiseAuditEvent audit))
                with ex ->
                    Error ex

            match envelope with
            | Error ex ->
                countAndWarn scopeId eventTypeName ex

                if policy = RefuseAction then
                    raise (AuditWriteRefusedException(scopeId, eventTypeName, ex))
            | Ok evt ->
                match! Async.Catch(eventStore.Write evt) with
                | Choice1Of2() -> ()
                | Choice2Of2 ex ->
                    // Phase 114 — promote the loss signal from Warn-only to
                    // an alertable counter so dropped audit rows are
                    // dashboard-visible. `event_type` tags the case so
                    // operators see WHICH events are being lost.
                    countAndWarn scopeId eventTypeName ex

                    match policy with
                    | LogAndContinue -> ()
                    | RefuseAction -> raise (AuditWriteRefusedException(scopeId, eventTypeName, ex))
                    | DegradeToFile ->
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

        member _.GetAuditTrail(scopeId, dateRange, eventType) = async {
            let! events = eventStore.ReadBySource(scopeId, AuditSourceModule.value)

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

/// `NoOp` `IAuditLog`. Registered by `compose` when
/// `ServerConfig.AuditLog = NoAuditLog` (Phase 1g lightweight default).
/// `Record` returns immediately without serialising or touching the
/// event store; `GetAuditTrail` returns an empty list. Emission sites
/// (`ScopeResolutionMiddleware`, `PlatformApi` handlers,
/// `fileManagementApi`) keep their unconditional `IAuditLog.Record`
/// calls — the no-op makes them free at runtime, and callsites stay
/// clean across mode changes.
type NoOpAuditLog() =
    interface IAuditLog with
        member _.Record(_scopeId, _audit) = async { return () }

        member _.GetAuditTrail(_scopeId, _dateRange, _eventType) = async { return [] }
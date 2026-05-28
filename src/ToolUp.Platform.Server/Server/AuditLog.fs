module ToolUp.Platform.AuditLog

open System
open Newtonsoft.Json
open Fable.Remoting.Json
open ToolUp.Platform

// ─── Audit log default implementation ────────────────────────────
//
// `EventStoreAuditLog` is the SDK-default `IAuditLog` implementation.
// It is a thin adapter over the configured `IEventStore`:
//
//   * `Record` serialises the `AuditEvent` payload via
//     `FableJsonConverter`, builds a `ModuleEvent` with
//     `SourceModule = AuditSourceModule.value`, and calls
//     `IEventStore.Write`. Failures are logged at `Warn` and
//     swallowed — audit emission must never fail the primary
//     operation. Same idiom as the existing audit emission in
//     `WebhookApiHandler.emitAudit` (`WebhookApiHandler.fs:139`).
//
//   * `GetAuditTrail` calls `IEventStore.ReadBySource` (filtered
//     to the audit module by construction), deserialises each
//     payload back into the matching `AuditEvent` case, and
//     applies the optional date and event-type filters in
//     memory. Cross-scope reads are structurally impossible —
//     the underlying store is keyed by `scopeId`.
//
// `FableJsonConverter` is the canonical converter for non-Remoting
// JSON crossing the server/Fable boundary (PascalCase, DU-friendly).
// We use it for audit payloads even though they are server-only
// today, so any future client-facing audit query surface (e.g. an
// admin UI) can deserialise without re-deriving the wire shape.

let private auditJsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

let private toAuditJson (value: 'T) =
    JsonConvert.SerializeObject(value, auditJsonSettings)

let private fromAuditJson<'T> (json: string) =
    JsonConvert.DeserializeObject<'T>(json, auditJsonSettings)

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

/// Decode a persisted `ModuleEvent` whose `SourceModule` is
/// `AuditSourceModule.value` back into an `AuditEvent`. Returns
/// `None` for unknown `EventType` strings or malformed payloads —
/// callers filter `None`s out so a single corrupt blob doesn't
/// break the whole query (audit trails are append-only and the
/// payload format is owned by the SDK, but defensive decoding
/// keeps the trail readable across SDK upgrades).
let private decodeAuditEvent (evt: ModuleEvent) : AuditEvent option =
    try
        match evt.EventType with
        | "UserLoggedIn" -> Some(UserLoggedIn(fromAuditJson<UserLoggedInPayload> evt.Payload))
        | "TeamCreated" -> Some(TeamCreated(fromAuditJson<TeamCreatedPayload> evt.Payload))
        | "MemberAdded" -> Some(MemberAdded(fromAuditJson<MemberAddedPayload> evt.Payload))
        | "MemberRemoved" -> Some(MemberRemoved(fromAuditJson<MemberRemovedPayload> evt.Payload))
        | "MemberRoleChanged" -> Some(MemberRoleChanged(fromAuditJson<MemberRoleChangedPayload> evt.Payload))
        | "FileUploaded" -> Some(FileUploaded(fromAuditJson<FileUploadedPayload> evt.Payload))
        | "FileDeleted" -> Some(FileDeleted(fromAuditJson<FileDeletedPayload> evt.Payload))
        | "AnalysisRun" -> Some(AnalysisRun(fromAuditJson<AnalysisRunPayload> evt.Payload))
        | "PermissionChanged" -> Some(PermissionChanged(fromAuditJson<PermissionChangedPayload> evt.Payload))
        | "NotificationSent" -> Some(NotificationSent(fromAuditJson<NotificationSentPayload> evt.Payload))
        | "NotificationDeliveryFailed" ->
            Some(NotificationDeliveryFailed(fromAuditJson<NotificationDeliveryFailedPayload> evt.Payload))
        | "HealthStateChanged" -> Some(HealthStateChanged(fromAuditJson<HealthStateChangedPayload> evt.Payload))
        | "EncryptionKeyCreated" -> Some(EncryptionKeyCreated(fromAuditJson<EncryptionKeyEventPayload> evt.Payload))
        | "EncryptionKeyRotated" -> Some(EncryptionKeyRotated(fromAuditJson<EncryptionKeyEventPayload> evt.Payload))
        | "EncryptionKeyDestroyed" -> Some(EncryptionKeyDestroyed(fromAuditJson<EncryptionKeyEventPayload> evt.Payload))
        | "EntityCreated" -> Some(EntityCreated(fromAuditJson<EntityLifecycleEventPayload> evt.Payload))
        | "EntityUpdated" -> Some(EntityUpdated(fromAuditJson<EntityLifecycleEventPayload> evt.Payload))
        | "EntityDeleted" -> Some(EntityDeleted(fromAuditJson<EntityLifecycleEventPayload> evt.Payload))
        | "FormSubmitted" -> Some(FormSubmitted(fromAuditJson<FormSubmittedPayload> evt.Payload))
        | "FormSubmissionUpdated" ->
            Some(FormSubmissionUpdated(fromAuditJson<FormSubmissionUpdatedPayload> evt.Payload))
        | "WorkflowTransitioned" -> Some(WorkflowTransitioned(fromAuditJson<WorkflowTransitionedPayload> evt.Payload))
        | "AuditSinkDelivered" -> Some(AuditSinkDelivered(fromAuditJson<AuditSinkDeliveredPayload> evt.Payload))
        | "AuditSinkFailed" -> Some(AuditSinkFailed(fromAuditJson<AuditSinkFailedPayload> evt.Payload))
        | "AuditSinkDeadLettered" ->
            Some(AuditSinkDeadLettered(fromAuditJson<AuditSinkDeadLetteredPayload> evt.Payload))
        | "AuditEventDecodeFailed" ->
            Some(AuditEventDecodeFailed(fromAuditJson<AuditEventDecodeFailedPayload> evt.Payload))
        | "NotificationSilentlySkipped" ->
            Some(NotificationSilentlySkipped(fromAuditJson<NotificationSilentlySkippedPayload> evt.Payload))
        | "OAuthConnected" -> Some(OAuthConnected(fromAuditJson<OAuthConnectedPayload> evt.Payload))
        | "OAuthDisconnected" -> Some(OAuthDisconnected(fromAuditJson<OAuthDisconnectedPayload> evt.Payload))
        | "OAuthRefreshFailed" -> Some(OAuthRefreshFailed(fromAuditJson<OAuthRefreshFailedPayload> evt.Payload))
        | "OAuthTokenRefreshed" -> Some(OAuthTokenRefreshed(fromAuditJson<OAuthTokenRefreshedPayload> evt.Payload))
        | "OAuthTokenRefreshFailed" ->
            Some(OAuthTokenRefreshFailed(fromAuditJson<OAuthTokenRefreshFailedPayload> evt.Payload))
        | "OAuthRefreshTokenInvalidated" ->
            Some(OAuthRefreshTokenInvalidated(fromAuditJson<OAuthRefreshTokenInvalidatedPayload> evt.Payload))
        | "OAuthRefreshDeadLettered" ->
            Some(OAuthRefreshDeadLettered(fromAuditJson<OAuthRefreshDeadLetteredPayload> evt.Payload))
        | "ShareTokenIssued" -> Some(ShareTokenIssued(fromAuditJson<ShareTokenIssuedPayload> evt.Payload))
        | "ShareTokenUsed" -> Some(ShareTokenUsed(fromAuditJson<ShareTokenUsedPayload> evt.Payload))
        | "ShareTokenRevoked" -> Some(ShareTokenRevoked(fromAuditJson<ShareTokenRevokedPayload> evt.Payload))
        | "PlatformAdminAssigned" ->
            Some(PlatformAdminAssigned(fromAuditJson<PlatformAdminAssignedPayload> evt.Payload))
        | "PlatformAdminRevoked" -> Some(PlatformAdminRevoked(fromAuditJson<PlatformAdminRevokedPayload> evt.Payload))
        | "PlatformDocumentUploaded" ->
            Some(PlatformDocumentUploaded(fromAuditJson<PlatformDocumentUploadedPayload> evt.Payload))
        | "PlatformDocumentDeleted" ->
            Some(PlatformDocumentDeleted(fromAuditJson<PlatformDocumentDeletedPayload> evt.Payload))
        | "ConversationExported" -> Some(ConversationExported(fromAuditJson<ConversationExportPayload> evt.Payload))
        | "BeaconRejected" -> Some(BeaconRejected(fromAuditJson<BeaconRejectedPayload> evt.Payload))
        | "ConfigDrift" -> Some(ConfigDrift(fromAuditJson<ConfigDriftPayload> evt.Payload))
        | "DiagnosticBundleAccessed" ->
            Some(DiagnosticBundleAccessed(fromAuditJson<DiagnosticBundleAccessedPayload> evt.Payload))
        | "RateLimitWaited" -> Some(RateLimitWaited(fromAuditJson<RateLimitWaitedPayload> evt.Payload))
        | "RateLimitRefused" -> Some(RateLimitRefused(fromAuditJson<RateLimitRefusedPayload> evt.Payload))
        | "ConversationStarted" -> Some(ConversationStarted(fromAuditJson<ConversationStartedPayload> evt.Payload))
        | "ConversationTurnAppended" ->
            Some(ConversationTurnAppended(fromAuditJson<ConversationTurnAppendedPayload> evt.Payload))
        | "ConversationCompleted" ->
            Some(ConversationCompleted(fromAuditJson<ConversationCompletedPayload> evt.Payload))
        | "ConversationErased" -> Some(ConversationErased(fromAuditJson<ConversationErasedPayload> evt.Payload))
        | "ConversationReplayed" -> Some(ConversationReplayed(fromAuditJson<ConversationReplayedPayload> evt.Payload))
        | "AssetUploaded" -> Some(AssetUploaded(fromAuditJson<AssetUploadedPayload> evt.Payload))
        | "AssetDeleted" -> Some(AssetDeleted(fromAuditJson<AssetDeletedPayload> evt.Payload))
        | "TeamCreationDenied" -> Some(TeamCreationDenied(fromAuditJson<TeamCreationDeniedPayload> evt.Payload))
        | "TeamInviteIssued" -> Some(TeamInviteIssued(fromAuditJson<TeamInviteIssuedPayload> evt.Payload))
        | "TeamInviteAccepted" -> Some(TeamInviteAccepted(fromAuditJson<TeamInviteAcceptedPayload> evt.Payload))
        | "TeamInviteAcceptedFromPending" ->
            Some(TeamInviteAcceptedFromPending(fromAuditJson<TeamInviteAcceptedFromPendingPayload> evt.Payload))
        | "TeamInviteRevoked" -> Some(TeamInviteRevoked(fromAuditJson<TeamInviteRevokedPayload> evt.Payload))
        | "TeamInviteRedeemed" -> Some(TeamInviteRedeemed(fromAuditJson<TeamInviteRedeemedPayload> evt.Payload))
        | "WorkflowActionExecuted" ->
            Some(WorkflowActionExecuted(fromAuditJson<WorkflowActionExecutedPayload> evt.Payload))
        | "ConsentRecorded" -> Some(ConsentRecorded(fromAuditJson<ConsentEvent> evt.Payload))
        | "AdImpressionRecorded" -> Some(AdImpressionRecorded(fromAuditJson<AdImpression> evt.Payload))
        | "AdClickRecorded" -> Some(AdClickRecorded(fromAuditJson<AdClick> evt.Payload))
        | "PremiumGranted" ->
            let p = fromAuditJson<PremiumAuditPayload> evt.Payload
            Some(PremiumGranted(p.SubjectUserId, p.Grantor, p.Reason, p.OccurredAt))
        | "PremiumRevoked" ->
            let p = fromAuditJson<PremiumAuditPayload> evt.Payload
            Some(PremiumRevoked(p.SubjectUserId, p.Grantor, p.Reason, p.OccurredAt))
        | "AdSlotConfigCreated" ->
            let p = fromAuditJson<AdSlotConfigAuditPayload> evt.Payload
            Some(AdSlotConfigCreated(p.SlotId, p.Actor, p.OccurredAt))
        | "AdSlotConfigUpdated" ->
            let p = fromAuditJson<AdSlotConfigAuditPayload> evt.Payload
            Some(AdSlotConfigUpdated(p.SlotId, p.Actor, p.OccurredAt))
        | "AdSlotConfigDeleted" ->
            let p = fromAuditJson<AdSlotConfigAuditPayload> evt.Payload
            Some(AdSlotConfigDeleted(p.SlotId, p.Actor, p.OccurredAt))
        | _ -> None
    with _ ->
        None

/// SDK-default `IAuditLog`. Wraps the DI-registered `IEventStore`
/// so audit events flow through the same retention policy, blob
/// layout, and webhook hooks as every other platform event.
type EventStoreAuditLog(eventStore: IEventStore, logger: ILogger) =

    let serialise (audit: AuditEvent) =
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
        | AnalysisRun p -> toAuditJson p
        | PermissionChanged p -> toAuditJson p
        | NotificationSent p -> toAuditJson p
        | NotificationDeliveryFailed p -> toAuditJson p
        | HealthStateChanged p -> toAuditJson p
        | EncryptionKeyCreated p -> toAuditJson p
        | EncryptionKeyRotated p -> toAuditJson p
        | EncryptionKeyDestroyed p -> toAuditJson p
        | EntityCreated p -> toAuditJson p
        | EntityUpdated p -> toAuditJson p
        | EntityDeleted p -> toAuditJson p
        | FormSubmitted p -> toAuditJson p
        | FormSubmissionUpdated p -> toAuditJson p
        | WorkflowTransitioned p -> toAuditJson p
        | AuditSinkDelivered p -> toAuditJson p
        | AuditSinkFailed p -> toAuditJson p
        | AuditSinkDeadLettered p -> toAuditJson p
        | AuditEventDecodeFailed p -> toAuditJson p
        | NotificationSilentlySkipped p -> toAuditJson p
        | OAuthConnected p -> toAuditJson p
        | OAuthDisconnected p -> toAuditJson p
        | OAuthRefreshFailed p -> toAuditJson p
        | OAuthTokenRefreshed p -> toAuditJson p
        | OAuthTokenRefreshFailed p -> toAuditJson p
        | OAuthRefreshTokenInvalidated p -> toAuditJson p
        | OAuthRefreshDeadLettered p -> toAuditJson p
        | ShareTokenIssued p -> toAuditJson p
        | ShareTokenUsed p -> toAuditJson p
        | ShareTokenRevoked p -> toAuditJson p
        | PlatformAdminAssigned p -> toAuditJson p
        | PlatformAdminRevoked p -> toAuditJson p
        | PlatformDocumentUploaded p -> toAuditJson p
        | PlatformDocumentDeleted p -> toAuditJson p
        | ConversationExported p -> toAuditJson p
        | BeaconRejected p -> toAuditJson p
        | ConfigDrift p -> toAuditJson p
        | DiagnosticBundleAccessed p -> toAuditJson p
        | RateLimitWaited p -> toAuditJson p
        | RateLimitRefused p -> toAuditJson p
        | DataSubjectRequest p -> toAuditJson p
        | ConversationStarted p -> toAuditJson p
        | ConversationTurnAppended p -> toAuditJson p
        | ConversationCompleted p -> toAuditJson p
        | ConversationErased p -> toAuditJson p
        | ConversationReplayed p -> toAuditJson p
        | AssetUploaded p -> toAuditJson p
        | AssetDeleted p -> toAuditJson p
        | TeamCreationDenied p -> toAuditJson p
        | TeamInviteIssued p -> toAuditJson p
        | TeamInviteAccepted p -> toAuditJson p
        | TeamInviteAcceptedFromPending p -> toAuditJson p
        | TeamInviteAcceptedFromPendingFailed p -> toAuditJson p
        | TeamInviteRevoked p -> toAuditJson p
        | TeamInviteRedeemed p -> toAuditJson p
        | WorkflowActionExecuted p -> toAuditJson p
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

    interface IAuditLog with
        member _.Record(scopeId, audit) = async {
            try
                let evt =
                    Events.create scopeId AuditSourceModule.value (AuditEvent.eventTypeName audit) (serialise audit)

                do! eventStore.Write evt
            with ex ->
                logger.Warn(
                    sprintf
                        "[AuditLog] write failed scope=%s eventType=%s: %s"
                        scopeId
                        (AuditEvent.eventTypeName audit)
                        ex.Message
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
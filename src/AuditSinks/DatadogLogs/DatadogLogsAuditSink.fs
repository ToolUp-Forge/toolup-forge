module ToolUp.Platform.AuditSinks.DatadogLogs

open System
open System.Net.Http
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Public surface ──────────────────────────────────────────────
//
// Phase 9g Datadog Logs `IAuditSink` companion. POSTs each batch to
// Datadog's `/api/v2/logs` endpoint with an API key from
// `ISecretStore`. No Datadog SDK dependency — BCL `HttpClient`.
//
// **Wire format.** Datadog's `/api/v2/logs` accepts a JSON array of
// log entries. Each entry has `ddsource` (free-form source label),
// `ddtags` (CSV tag list), `service`, `host`, and `message` (the
// payload — string OR object; we send the SDK's `AuditEvent` as a
// nested object).
//
// **Tags.** Audit events carry per-tenant scope metadata (the
// `BatchScopeId` / payload `ScopeId` field on the source event).
// We surface this as the `scope_id:<scopeId>` tag so Datadog
// queries can filter by tenant — `service:toolup
// scope_id:team-acme`. The `service` and `env` tags are pinned at
// the deployment level via `DatadogLogsSettings`.
//
// **Authentication.** `DD-API-KEY: <key>` header. Key resolved
// per-call via `ISecretStore.GetSecret` so rotated keys flow through
// immediately.
//
// **Batch size.** Datadog's `/api/v2/logs` accepts up to 5MB per
// request and 1000 entries per request. The dispatcher's default
// `MaxBatchSize = 100` produces requests well under both limits.

/// Connection settings for the Datadog Logs sink.
type DatadogLogsSettings = {
    /// Full Datadog Logs intake URL — typically
    /// `https://http-intake.logs.datadoghq.com/api/v2/logs`.
    /// Datadog has region-specific endpoints (US, EU, US3, US5, AP1);
    /// pick the one matching the deployment's Datadog organization.
    EndpointUrl: string
    /// `service` tag stamped on every entry. Conventionally
    /// `"toolup"` so Datadog dashboards can filter by service across
    /// the deployment.
    Service: string
    /// `env` tag — `"prod"` / `"staging"` / `"dev"`. Combined with
    /// `service` it gives Datadog the canonical `service.env`
    /// breakdown on its dashboards.
    Env: string
    /// `ddsource` field — label for the log source. Conventionally
    /// `"toolup_audit"` so Datadog routes audit logs through audit-
    /// specific pipelines (PII redaction, retention overrides).
    DdSource: string
    /// Optional `host` field stamped on every entry. `None` → omit
    /// (Datadog uses the requesting host's IP). Most deployments set
    /// this to the deployment / pod name.
    Host: string option
}

module DatadogLogsSettings =
    /// Default settings — US-region endpoint, conventional tags.
    /// Apps override per environment.
    let defaults: DatadogLogsSettings = {
        EndpointUrl = "https://http-intake.logs.datadoghq.com/api/v2/logs"
        Service = "toolup"
        Env = "dev"
        DdSource = "toolup_audit"
        Host = None
    }

let private logsJsonOptions = FableConverters.create ()

/// Phase 9g — best-effort scope-id extraction from an `AuditEvent`
/// payload. Pre-Phase-66, sinks introspected the payload per-case to
/// surface a tenant scope; Phase 66 Stream B.7 promotes `ScopeId` to a
/// first-class field on `AuditEnvelope` so this helper is retained only
/// for the rare event-payload-level scope that differs from the
/// recording scope (e.g. `TeamCreated.TeamId` may be the *new* team's
/// id, while the recording scope is `_platform`). Today every call site
/// reads `envelope.ScopeId` directly; this function is kept private so
/// future per-payload tag enrichment can opt in without re-deriving
/// the per-case lookup table.
let private extractEventScopeId (audit: AuditEvent) : string option =
    match audit with
    | UserLoggedIn _ -> None
    | TeamCreated p -> Some p.TeamId
    | MemberAdded p -> Some p.TeamId
    | MemberRemoved p -> Some p.TeamId
    | MemberRoleChanged p -> Some p.TeamId
    | FileUploaded _
    | FileDeleted _
    | FileReprocessed _
    | DataStoreReset _
    | AnalysisRun _ ->
        // These payloads carry the actor (UserId) and module-specific
        // detail; the per-tenant scope is the audit recording scope,
        // not a payload field. Datadog query users filter via the
        // EventStore-level scope instead.
        None
    | PermissionChanged p -> Some p.TeamId
    | NotificationSent p -> Some p.ScopeId
    | NotificationDeliveryFailed p -> Some p.ScopeId
    | HealthStateChanged _ -> None
    | EncryptionKeyCreated p -> Some p.ScopeId
    | EncryptionKeyRotated p -> Some p.ScopeId
    | EncryptionKeyDestroyed p -> Some p.ScopeId
    | EntityCreated _
    | EntityUpdated _
    | EntityDeleted _ -> None
    | FormSubmitted _
    | FormSubmissionUpdated _
    | WorkflowTransitioned _ -> None
    | AuditSinkDelivered p -> Some p.BatchScopeId
    | AuditSinkFailed p -> Some p.BatchScopeId
    | AuditSinkDeadLettered p -> Some p.BatchScopeId
    | AuditEventDecodeFailed p -> Some p.BatchScopeId
    | NotificationSilentlySkipped p -> Some p.ScopeId
    | ShareTokenIssued _
    | ShareTokenUsed _
    | ShareTokenRevoked _ ->
        // ShareToken payloads identify the token + resource but not the
        // issuing scope (that lives on the EventStore record, since
        // ShareTokenUsed has no `UserId` either). Datadog query users
        // filter via the EventStore-level scope instead, same as the
        // entity-lifecycle and form-submission events.
        None
    | OAuthConnected _
    | OAuthDisconnected _
    | OAuthRefreshFailed _
    | OAuthTokenRefreshed _
    | OAuthTokenRefreshFailed _
    | OAuthRefreshTokenInvalidated _
    | OAuthRefreshDeadLettered _ ->
        // Phase 10b / 10h OAuth payloads carry ScopeId, but mapping it
        // here belongs to a separate OAuth-audit pass — leaving this
        // as a None default for now keeps Datadog query behaviour
        // unchanged.
        None
    | PlatformAdminAssigned _
    | PlatformAdminRevoked _
    | PlatformDocumentUploaded _
    | PlatformDocumentDeleted _ ->
        // Phase 4b — deployment-wide events recorded under `_platform`
        // at the EventStore level. No tenant ScopeId in the payload;
        // Datadog query users filter via the EventStore-level scope.
        None
    | ConversationExported _ ->
        // ConversationExportPayload carries ConversationId + ExportedBy
        // but no tenant ScopeId. The exporting scope lives on the
        // EventStore record; Datadog query users filter via the
        // EventStore-level scope, same as the OAuth / share-token cases.
        None
    | BeaconRejected _ ->
        // Phase 6j.D — payload carries ConversationId + Caller + Owner +
        // Surface but no tenant ScopeId field. The recording scope
        // (`team-{teamId}` for shared-container rejections) rides on the
        // EventStore record via `IAuditLog.Record`'s scopeId arg; Datadog
        // queries filter via the EventStore-level scope, same posture as
        // ConversationExported.
        None
    | ConfigDrift _ ->
        // Phase 9q deployment-wide event recorded under `_platform`
        // at the EventStore level. No tenant ScopeId in the payload —
        // config drift is a per-deployment signal, not a per-tenant one.
        None
    | RateLimitWaited p -> Some p.ScopeId
    | RateLimitRefused p -> Some p.ScopeId
    | DataSubjectRequest _ ->
        // Phase 9h — DSR payload carries the SubjectUserId + Actor but
        // not a tenant ScopeId field. The scope the admin was acting
        // within rides on the EventStore-level record (per
        // IAuditLog.Record's first arg); Datadog query users filter via
        // the EventStore-level scope, same posture as the OAuth /
        // share-token / config-drift cases.
        None
    | DiagnosticBundleAccessed _ ->
        // Phase 9n deployment-wide event recorded under `_platform` at
        // the EventStore level. The payload carries the caller's
        // optional `ScopeId` (the scope they were querying audit-tail
        // from), not a tenant scope for the bundle itself. Datadog
        // queries filter by the EventStore-level `_platform` scope,
        // same posture as `ConfigDrift`.
        None
    | ConversationStarted p -> Some p.ScopeId
    | ConversationTurnAppended _
    | ConversationCompleted _
    | ConversationErased _
    | ConversationReplayed _ ->
        // Phase 53 — turn-level / lifecycle conversation audit events
        // don't carry a tenant ScopeId field. The recording scope
        // rides on the EventStore record (via `IAuditLog.Record`'s
        // scopeId arg); Datadog query users filter via the EventStore-
        // level scope, same posture as DSR / OAuth audit cases.
        None
    | AssetUploaded _
    | AssetDeleted _ ->
        // Phase 39 — asset-store payloads carry the AssetId + ContentHash
        // + UserId but no tenant ScopeId field. The recording scope
        // rides on the EventStore record (via `IAuditLog.Record`'s
        // scopeId arg); Datadog query users filter via the EventStore-
        // level scope, same posture as DSR / OAuth / share-token cases.
        None
    | TeamCreationDenied _ ->
        // Phase 5f — the denial fires before any team id is minted, so
        // the payload carries only the caller's UserId + the attempted
        // team name. Recorded under `_platform` scope at the EventStore
        // level (deployment-wide refusal trail); Datadog queries filter
        // there, same posture as `PlatformAdminAssigned`.
        None
    | TeamArchived p -> Some p.TeamId
    | TeamRestored p -> Some p.TeamId
    | TeamDeleted p -> Some p.TeamId
    | TeamInviteIssued p -> Some p.TeamId
    | TeamInviteAccepted p -> Some p.TeamId
    | TeamInviteAcceptedFromPending p -> Some p.TeamId
    | TeamInviteAcceptedFromPendingFailed p -> Some p.TeamId
    | TeamInviteRevoked p -> Some p.TeamId
    | TeamInviteRedeemed p -> Some p.TeamId
    | WorkflowActionExecuted _ ->
        // Phase 21d — payload carries SubmissionId + TransitionId +
        // ActionName + Status + Reason but no tenant ScopeId field.
        // The recording scope rides on the EventStore record (via
        // `IAuditLog.Record`'s scopeId arg); Datadog queries filter
        // via the EventStore-level scope, same posture as
        // `WorkflowTransitioned` and `FormSubmitted`.
        None
    | ConsentRecorded _
    | AdImpressionRecorded _
    | AdClickRecorded _
    | PremiumGranted _
    | PremiumRevoked _
    | AdSlotConfigCreated _
    | AdSlotConfigUpdated _
    | AdSlotConfigDeleted _
    | AnonymousSessionMigrated _
    // Auth-observability A1 + A2 — `_platform.auth` SourceModule.
    // No tenant ScopeId on these (deployment-wide observability
    // events; the request that triggered the denial had a
    // resolvable Subject, but the audit row is emitted under the
    // `_platform` scope so a per-tenant Datadog filter sees them
    // alongside other system-level rows).
    | AuthScopeResolutionFailed _
    | SurfaceDenied _ ->
        // Wave 10 + auth-observability — public-utility audit cases
        // carry anonymous-user / subject-user identity but no tenant
        // ScopeId. Datadog queries filter via the EventStore-level
        // scope, same posture as the Phase 39 asset cases.
        None
    | PasskeyCredentialRegistered _
    | PasskeyCredentialRemoved _ ->
        // Phase 443 — `_platform.auth.passkey` credential-lifecycle
        // events recorded under `_platform`; the payload carries the
        // UserId + a truncated credential id but no tenant ScopeId.
        None

/// Sanitise a tag value for Datadog's CSV tag list. Datadog reserves
/// `,` (tag separator) and `:` (key/value separator); we replace both
/// with `_` so derived tags from scope ids / handles / token ids never
/// produce malformed entries.
let private sanitizeTagValue (value: string) : string =
    if isNull value then
        ""
    else
        value.Replace(',', '_').Replace(':', '_')

/// Build the JSON-array body for a batch. One JSON object per audit
/// envelope with Datadog's `ddsource` / `ddtags` / `service` / `host` /
/// `message` fields. `message` is the SDK's serialised `AuditEvent`
/// — Datadog parses nested JSON automatically (`@message.Case`).
///
/// Phase 66 Stream B.7 — `ddtags` now carries `subject_kind` and the
/// subject-identity tag (`user_id` / `team_id` / `session_id` /
/// `claim_token_id`) derived from the envelope's `AuditSubject`. The
/// `scope_id` tag continues to ride on the envelope's recording-side
/// scope; per-payload scope (e.g. `TeamCreated.TeamId` for a freshly-
/// minted team) is no longer auto-emitted to keep the tag list
/// canonical — the recording scope is the unambiguous tenant axis.
let private buildSubjectTags (subject: AuditSubject) : string =
    match subject with
    | AnonymousAudit sid -> sprintf ",subject_kind:anonymous,session_id:%s" (sanitizeTagValue sid)
    | UserAudit uid -> sprintf ",subject_kind:user,user_id:%s" (sanitizeTagValue uid)
    | TeamAudit(uid, tid) ->
        sprintf ",subject_kind:team,user_id:%s,team_id:%s" (sanitizeTagValue uid) (sanitizeTagValue tid)
    | ClaimAudit(tokenId, _, resourceKind, resourceId) ->
        sprintf
            ",subject_kind:claim,claim_token_id:%s,claim_resource_kind:%s,claim_resource_id:%s"
            (sanitizeTagValue tokenId)
            (sanitizeTagValue resourceKind)
            (sanitizeTagValue resourceId)

let private serializeBatch (settings: DatadogLogsSettings) (batch: AuditEnvelope list) : string =
    let entries =
        batch
        |> List.map (fun envelope ->
            let payloadJson = JsonSerializer.Serialize(envelope.Event, logsJsonOptions)
            let eventTypeName = AuditEvent.eventTypeName envelope.Event
            let subjectTags = buildSubjectTags envelope.Subject

            let scopeTag =
                if System.String.IsNullOrWhiteSpace envelope.ScopeId then
                    ""
                else
                    sprintf ",scope_id:%s" (sanitizeTagValue envelope.ScopeId)

            let baseTags =
                sprintf
                    "env:%s,event_type:%s,schema_version:%d%s%s"
                    settings.Env
                    eventTypeName
                    AuditSchemaVersion.current
                    scopeTag
                    subjectTags

            let hostField =
                match settings.Host with
                | Some h -> sprintf ",\"host\":\"%s\"" h
                | None -> ""

            sprintf
                "{\"ddsource\":\"%s\",\"ddtags\":\"%s\",\"service\":\"%s\"%s,\"message\":%s}"
                settings.DdSource
                baseTags
                settings.Service
                hostField
                payloadJson)

    "[" + String.Join(",", entries) + "]"

[<Literal>]
let private SecretStoreScope = "_platform"

/// SDK Datadog Logs `IAuditSink`. One POST per batch. API key from
/// `ISecretStore` per call.
type DatadogLogsAuditSink
    (name: string, settings: DatadogLogsSettings, secretStore: ISecretStore, secretKey: string, httpClient: HttpClient)
    =

    interface IAuditSink with
        member _.Name = name

        member _.SchemaVersion = AuditSchemaVersion.current

        member _.Deliver(batch) = async {
            if List.isEmpty batch then
                return Ok()
            else
                try
                    let! keyResult = secretStore.GetSecret(SecretStoreScope, secretKey)

                    match keyResult with
                    | None ->
                        return Error(sprintf "DatadogLogs API key not found in ISecretStore at _platform/%s" secretKey)
                    | Some apiKey ->
                        let body = serializeBatch settings batch
                        let content = new StringContent(body, Encoding.UTF8, "application/json")

                        use request = new HttpRequestMessage(HttpMethod.Post, settings.EndpointUrl)

                        request.Content <- content
                        request.Headers.Add("DD-API-KEY", (apiKey: string))

                        let! response = httpClient.SendAsync(request) |> Async.AwaitTask

                        if response.IsSuccessStatusCode then
                            return Ok()
                        else
                            let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                            return Error(sprintf "DatadogLogs HTTP %d: %s" (int response.StatusCode) responseBody)
                with ex ->
                    return Error(sprintf "DatadogLogs sink threw: %s" ex.Message)
        }

/// Construct a Datadog Logs sink. `secretKey` references the Datadog
/// API key in `ISecretStore` under the `_platform` scope.
let create
    (name: string)
    (settings: DatadogLogsSettings)
    (secretStore: ISecretStore)
    (secretKey: string)
    (httpClient: HttpClient)
    : IAuditSink =
    DatadogLogsAuditSink(name, settings, secretStore, secretKey, httpClient) :> _
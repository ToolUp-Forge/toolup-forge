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

// Phase 9g shipped a 132-arm `extractEventScopeId` here, mapping each
// `AuditEvent` case to a payload-level scope id. Phase 66 Stream B.7
// promoted `ScopeId` to a first-class `AuditEnvelope` field and the
// call sites moved to `envelope.ScopeId` (see `deliver` below); the
// function kept compiling with no callers and silently fell 51 of 132
// arms behind before anyone noticed. Removed by Phase 626 — the
// per-case table is recoverable from git history if per-payload tag
// enrichment is ever actually wanted, and re-deriving it then is no
// more work than un-rotting a table nothing exercises. Every sibling
// sink (SplunkHec, S3Archive, GcsArchive, AzureBlobArchive, Cef) reads
// `envelope.ScopeId` and does no per-case introspection at all.

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
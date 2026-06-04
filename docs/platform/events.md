# Events + audit

The Platform ships append-only event storage with full audit-trail semantics, plus a replication substrate to mirror audit events to external sinks (Splunk, Datadog, S3 archive) for compliance.

## `IEventStore`

The fundamental abstraction:

```fsharp
type Event = {
    EventId: Guid
    SourceModule: string
    EventType: string
    ScopeId: string
    UserId: string option
    CorrelationId: Guid option
    Timestamp: DateTime
    Payload: string  // JSON, opaque to the store
}

type IEventStore =
    abstract Write: Event -> Async<unit>
    abstract ReadByType: SourceModule: string -> EventType: string -> Async<Event list>
    abstract ReadByCorrelation: CorrelationId: Guid -> Async<Event list>
    abstract ReadByScope: ScopeId: string -> Async<Event list>
```

Events are immutable: there's no `Update` or `Delete`. The store is the durable record of what happened, in order.

### Shipped implementations

- **`InMemoryEventStore`** — non-persistent. Lost on restart. Fine for dev / CI / contract testing.
- **`PersistentEventStore`** — blob-backed. Writes append-only JSON to `_platform/events/{scopeId}/{yyyy-mm-dd}/{hh-mm-ss-fffffff}-{eventId}.json`. Optional `EventRetentionPolicy`:

  ```fsharp
  type EventRetentionPolicy =
      | NoRetention
      | MaxAge of TimeSpan
      | MaxCountPerScope of int
  ```

  A background job (when `JobScheduler` is enabled) runs the retention policy nightly. Without the scheduler, retention is on-write only — over-quota events accumulate until the next write-time check.

Opt in via:

```fsharp
ServerConfig.EventStore = PersistentBlobBacked (MaxAge (TimeSpan.FromDays 90.))
```

### Module event emission

Modules can publish their own events via `IEventStore.Write`. Conventions:
- `SourceModule` matches the module's name (e.g. `"SalesAnalysis"`).
- `EventType` is a domain verb in `PascalCase` (e.g. `"AnalysisCompleted"`).
- `Payload` is JSON of a typed record; consumers parse with `System.Text.Json` using the F# converter set from `ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()`.
- `CorrelationId` links related events from a single user action.

Domain events flow through the same store as audit events; the `SourceModule` discriminator keeps them queryable separately.

## Audit log

The `IAuditLog` interface sits on top of `IEventStore` and records `AuditEvent` cases under `SourceModule = "_platform.audit"`:

```fsharp
type IAuditLog =
    abstract Record: AuditEvent -> Async<unit>
    abstract GetAuditTrail: scopeId: string -> from: DateTime option -> until: DateTime option -> Async<Event list>
```

Audit events come from the SDK's own bookkeeping, not from module code. Shipped events:

- **Authentication**: `UserLoggedIn` (first-seen-this-session)
- **Team operations**: `TeamCreated`, `TeamMemberAdded`, `TeamMemberRemoved`, `TeamMemberRoleChanged`, `ActiveTeamSet`
- **Permission changes**: `RoleAssigned`, `RoleRevoked`, `ModulePermissionChanged`
- **File operations**: `FileUploaded`, `FileDeleted`, `FileRecovered`
- **Encryption**: `EncryptionKeyCreated`, `EncryptionKeyRotated` (reserved), `EncryptionKeyDestroyed`
- **Jobs**: `JobRegistered`, `JobTriggered`, `JobSucceeded`, `JobFailed`, `JobDeadLettered`
- **Data ingestion**: `IngestionRunStarted`, `IngestionRunCompleted`, `IngestionRunFailed`
- **Entities**: `EntityCreated`, `EntityUpdated`, `EntityDeleted`
- **Notifications**: `NotificationSent`, `NotificationDeliveryFailed`
- **Audit replication**: `AuditSinkDelivered`, `AuditSinkFailed`, `AuditSinkDeadLettered`
- **Health**: `HealthStateChanged` (when state-tracking is enabled)

Every event carries the actor's userId, the affected userId (if different), the resource Id, and a server-side timestamp.

## External audit replication

The `IAuditSink` substrate mirrors every `_platform.audit` event to one or more external sinks the deploying organisation does not control — required for SOC 2 / HIPAA / GDPR Article 30 / SOX compliance.

```fsharp
type IAuditSink =
    abstract Name: string
    abstract Deliver: batch: Event list -> Async<Result<unit, AuditSinkError>>
```

Wiring:

```fsharp
ServerApp.empty
|> ServerApp.withAuditSink (S3Archive.create "compliance-archive" s3Settings blobStorage)
|> ServerApp.withAuditSink (SplunkHec.create "splunk-prod" splunkSettings secretStore "splunk-hec-token" httpClient)
|> ...
```

### How it works

- **Live hook**: `AuditReplicationHookedEventStore` decorator wraps `IEventStore` and feeds every `_platform.audit` write into a bounded `Channel` per sink. Sub-second steady-state.
- **Catch-up sweep**: `AuditReplicator` background service runs every N minutes (default 5) and re-reads from the persistent event store cursor forward, mopping up any events the live hook dropped (process restart, channel backpressure).
- **Cursor**: per-`(sinkName, scopeId)` cursor in `IBlobStorage` at `_platform/audit-cursors/{sinkName}/{scopeId}.txt`. Survives restart.
- **Anti-recursion**: the live hook filters by event type to skip events that the replicator itself emits (`AuditSinkDelivered` etc.) — without this, replicating an audit-sink-delivery event triggers another audit-sink-delivery event, ad infinitum.
- **At-most-once steady-state, at-least-once across restart**: the steady-state path uses a `SemaphoreSlim` per scope + cursor filter to deduplicate. The catch-up sweep can re-deliver after a process restart where the cursor was not yet advanced. Sinks must be batch-idempotent (use vendor dedup keys).

### Shipped sinks

**`ToolUp.AuditSinks.S3Archive`** — no paid deps. Writes gzipped JSONL batches through the abstract `IBlobStorage`. Blob layout: `{prefix}/{yyyy-MM-dd}/{HH-mm-ss-fffffff}-{sinkName}-{batchUuid}.jsonl.gz`. Production wires `AwsS3Storage` with bucket-level Object Lock for compliance-grade WORM. Dev wires `LocalFileStorage`. Idempotency via content-addressable blob naming.

**`ToolUp.AuditSinks.SplunkHec`** — BCL `HttpClient` POST to Splunk's `/services/collector/event` with `Authorization: Splunk <token>` header. Token resolved per-call from `ISecretStore` so rotation is transparent. Wire format: NDJSON, one event per line, `_meta.uuid` for Splunk-side dedup on retry.

**`ToolUp.AuditSinks.DatadogLogs`** — BCL `HttpClient` POST to Datadog's `/api/v2/logs` with `DD-API-KEY` header. Wire format: JSON array body, one entry per event with `ddsource` / `ddtags` (env + event_type + best-effort `scope_id:` tag) / `service` / `host` / `message`.

### Writing a new audit sink

A new vendor (Sumo, Elastic, Loki, custom SIEM) goes in `src/AuditSinks/<Vendor>/` with its own `.fsproj`, implementing `IAuditSink` (two members: `Name` + `Deliver`).

Rules:
- Batch-idempotent: the dispatcher retries the entire batch on `Result.Error`. Use vendor-specific dedup keys.
- API keys / tokens come through `ISecretStore` — never hardcode, never read env vars directly.
- Sinks read on every `Deliver`, so rotated tokens flow through immediately.
- Author an `IHealthCheck` for `/ready` participation.
- Author an `IConfigValidator` to verify the destination is reachable at preflight.

The dispatcher's batching / retry / cursor / cap logic is shared across all sinks; companions only implement the wire-format / vendor-specific bits.

## Webhook delivery

`IWebhookRegistry` + `WebhookDispatcher` provide outbound webhook delivery on event triggers — a complementary path to audit-sink replication. Sinks replicate the platform's internal audit trail to compliance archives; webhooks deliver domain events to customer-defined HTTP endpoints.

```fsharp
type WebhookEndpoint = {
    EndpointId: Guid
    ScopeId: string
    Url: string
    EventTypes: string list
    SecretKey: string  // HMAC-SHA256 signing key, stored via ISecretStore
    RetryPolicy: WebhookRetryPolicy
}
```

Webhooks emit a `X-ToolUp-Signature` HMAC-SHA256 over the JSON body. Consumers verify signatures to defeat replay / forgery. Retry loop mirrors the audit replicator; dead-letter triggers a `SystemMessage`-Warning notification to the scope's admins.

URL validation (`WebhookUrlValidator`) rejects:
- Loopback / private IP ranges.
- File / FTP / non-HTTP(S) schemes.
- HTTP (non-TLS) in production mode.

This prevents the most common SSRF-via-webhook pattern.

## Reading the audit trail

Admin-UI access:

```fsharp
// In a Platform Admin module
let! trail = auditLog.GetAuditTrail(scopeId, from, until)
```

The `/dev/inspect` endpoint surfaces a recent-events snapshot for the caller's scope (gated by `EnableDevEndpoints` + `PlatformRole.PlatformAdmin`).

External tooling (typical compliance workflow): query the audit-sink destination (Splunk, S3 archive). The SDK's local event store is the source-of-truth for short-term queries; the replicated archive is the long-term retention story.

## Configuration knobs

- `ServerConfig.EventStore = NoEventStore | InMemoryEventStore | PersistentBlobBacked of EventRetentionPolicy`
- `ServerConfig.AuditLogMode = NoAuditLog | EnabledAuditLog` (opt-in; default off — the audit-log subsystem registers a hooked event store wrapper that has small CPU cost)

Environment variables:
- `TOOLUP_EVENT_RETENTION_DAYS=90` — propagated into `MaxAge` policy when persistent store is enabled.

Audit-sink env vars:
- `TOOLUP_AUDIT_SINK_S3_PREFIX`, `TOOLUP_AUDIT_SINK_SPLUNK_URL`, `TOOLUP_AUDIT_SINK_DATADOG_REGION` — per-sink configuration.
- Token / secret references resolved via `ISecretStore` (never read directly).

## Activation patterns

The audit subsystem is opt-in at multiple layers:

```fsharp
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        EventStore = PersistentBlobBacked (MaxAge (TimeSpan.FromDays 90.))
        AuditLogMode = EnabledAuditLog
}
|> ServerApp.withAuditSink (S3Archive.create "compliance" s3Settings blobStorage)
// Adding more sinks: just chain another withAuditSink.
|> ...
```

Each opt-in is cheap if unused — the `AuditReplicator` background service skips entirely when no sinks are registered. Deployments that don't need replication run the same SDK build with one fewer config call.

## Caveats

- **Sub-second precision is best-effort**, not a guarantee. The `Timestamp` field is server-side wall clock; clock skew between nodes in a distributed setup limits temporal ordering across nodes.
- **`InMemoryEventStore` is not multi-process safe.** Two processes will see different views of events. Use only single-instance / dev.
- **Audit-sink delivery is at-least-once.** Sinks dedup by `_meta.uuid` / content hash. Two simultaneous catch-up sweeps after a restart can deliver the same batch twice; the vendor de-duplicates.
- **The audit subsystem cannot redact PII from emitted events.** Events with sensitive payloads are emitted as-is. The replication layer ships them to external sinks unmodified. The mitigation is: don't put PII in event payloads. The transactional notification sub-system (`INotificationSink`) intentionally keeps PII out of the audit trail by using out-of-band envelope dispatch.

For the full set of compliance considerations (data sovereignty, retention, right-to-erasure interaction with the immutable audit log), see [compliance.md](compliance.md). (Forthcoming.)

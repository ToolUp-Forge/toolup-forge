# Events + audit

The Platform ships append-only event storage with full audit-trail semantics, plus a replication substrate to mirror audit events to external sinks (Splunk, Datadog, S3 archive) for compliance.

## `IEventStore`

The fundamental abstraction:

```fsharp
type ModuleEvent = {
    Id: Guid
    OccurredAt: DateTime
    SourceModule: string
    EventType: string
    ScopeId: string
    Payload: string  // JSON, opaque to the store
}

type IEventStore =
    abstract Write: event: ModuleEvent -> Async<unit>
    // Every read is scope-first, so a caller cannot query across tenants
    // by accident (GP 4).
    abstract ReadAll: scopeId: string -> Async<ModuleEvent list>
    abstract ReadByType: scopeId: string * eventType: string -> Async<ModuleEvent list>
    abstract ReadBySource: scopeId: string * sourceModule: string -> Async<ModuleEvent list>
    abstract ListScopes: unit -> Async<string list>
```

Events are immutable: there is no `Update` and no general `Delete`. The store is the durable record
of what happened, in order. The one erasure path is `Erase`, which exists for GDPR subject-erasure
and is policy-gated rather than a general mutation seam — see [`data-subject-requests.md`](data-subject-requests.md).

### Shipped implementations

- **`InMemoryEventStore`** — non-persistent. Lost on restart. Fine for dev / CI / contract testing.
- **`PersistentEventStore`** — blob-backed. Writes append-only JSON to `_platform/events/{scopeId}/{yyyy-mm-dd}/{hh-mm-ss-fffffff}-{eventId}.json`. Optional `EventRetentionPolicy`:

  ```fsharp
  type EventRetentionPolicy = {
      MaxAge: TimeSpan option
      MaxCountPerScope: int option
  }
  ```

  A background job (when `JobScheduler` is enabled) runs the retention policy nightly. Without the scheduler, retention is on-write only — over-quota events accumulate until the next write-time check.

Opt in via:

```fsharp
let config = {
    ServerConfig.defaults with
        EventStore =
            PersistentBlobBacked {
                MaxAge = Some(TimeSpan.FromDays 90.)
                MaxCountPerScope = None
            }
}
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

Both members are scope-first and tupled, and the trail comes back as typed `AuditEvent`s rather than raw store rows:

```fsharp skip=signature
type IAuditLog =
    abstract Record: scopeId: string * audit: AuditEvent -> Async<unit>

    abstract GetAuditTrail:
        scopeId: string * dateRange: (DateTime * DateTime) option * eventType: string option ->
            Async<AuditEvent list>
```

Audit events come from the SDK's own bookkeeping, not from module code. The exhaustive inventory is
**[docs/reference/audit-event-reference.md](../reference/audit-event-reference.md)** — a generated projection
of the `AuditEvent` union and the codec registry, refreshed by
`dev-scripts/generate-audit-event-reference.ps1`. Do not keep a second copy of that list here: the summary
below names families and representatives only, and a test holds every name in it to the union.

<!-- audit-event-names:begin -->
- **Authentication + session**: `UserLoggedIn`, `PasskeyCredentialRegistered`, `PasskeyCredentialRemoved`, `AnonymousSessionMigrated`, `AuthScopeResolutionFailed`
- **Teams + membership**: `TeamCreated`, `MemberAdded`, `MemberRemoved`, `MemberRoleChanged`, `TeamOwnershipTransferred`, `TeamArchived`, `TeamRestored`, `TeamDeleted`, and the `TeamInvite*` family
- **Authorization + platform roles**: `PermissionChanged`, `AuthorizationDenied`, `SurfaceDenied`, `SchemaOnlyAccessAttempted`, `PlatformAdminAssigned`, `PlatformAdminRevoked`
- **File operations**: `FileUploaded`, `FileDeleted`, `FileReprocessed`, `DataStoreReset`, `AnalysisRun`
- **Entities**: `EntityCreated`, `EntityUpdated`, `EntityDeleted`
- **Encryption + artefact signing**: `EncryptionKeyCreated`, `EncryptionKeyRotated` (reserved), `EncryptionKeyDestroyed`, `EncryptionKeyDestroyAcknowledged` (one per other replica, on cross-replica shred fanout), `SigningKeyRotated`, `ArtefactSigned`, and the module-artefact trio `ModuleArtefactSigned` / `ModuleArtefactVerified` / `ModuleArtefactRejected`
- **OAuth credentials**: `OAuthConnected`, `OAuthDisconnected`, `OAuthRefreshFailed`, plus the `OAuth1a*` and background-refresh `OAuthToken*` families
- **Tenant lifecycle**: `TenantProvisioned`, `TenantDeprovisioned`, `TenantDataExported`, `TenantOffboardConfirmationRequested`, `TenantDeprovisionScheduled`
- **Notifications**: `NotificationSent`, `NotificationDeliveryFailed`, `NotificationSilentlySkipped`
- **Audit replication**: `AuditSinkDelivered`, `AuditSinkFailed`, `AuditSinkDeadLettered`, `AuditEventDecodeFailed`
- **Knowledge base**: `KnowledgeOriginalRetrieved`, `KnowledgeOriginalRetrievalDenied`, `KnowledgeScopeErased`, `KnowledgeIngestionDropped`, `KnowledgeDocumentsPurged`
- **Model lifecycle**: `ModelFitStarted`, `ModelFitCompleted`, `ModelArtifactRegistered`, `ModelArtifactPromoted`, `ModelScored`, `ModelEvaluated`
- **Datasets + schema**: `DatasetSpillCreated`, `DatasetDeclassified`, `DatasetPolicyDenied`, `SchemaProposed`, `SchemaApproved`, `SchemaChanged`
- **Grounding + verification**: `CompositionVerificationRecorded`, `CompositionCapabilityRefused`, `AnswerVerificationPassed`, `AnswerVerificationFlagged`, `FactImportAccepted`, `FactImportRefused`, `GroundingEnvelopeMutated`, `GroundingMutationRefused`, `CertificateIssued`, `DeploymentVerified`
- **Peer + federation**: `PeerCallCompleted`, `PeerJobCompleted`, `PeerCleanRoomDecision`, `FederationRoundCompleted`, `FederationParticipantDropped`, `FederationRunAborted`
- **Compliance + data protection**: `ConsentRecorded`, `DataSubjectRequest`, `ClassifiedFieldRead`, `ClassifiedFieldWritten`, `EgressBlocked`, `ContentScanned`
- **Operations**: `HealthStateChanged`, `ConfigDrift`, `DiagnosticBundleAccessed`, `RateLimitWaited`, `RateLimitRefused`, `ComputeBudgetDenied`, `ComputeBudgetWarning`
<!-- audit-event-names:end -->

Every event carries the actor's userId, the affected userId (if different), the resource Id, and a server-side timestamp.

> **Jobs and ingestion runs are NOT audit events.** The job scheduler and the data ingestor write to the same
> `IEventStore`, but under their own reserved source modules — `_platform.jobs` (`JobScheduled`, `JobStarted`,
> `JobCompleted`, `JobFailed`, `JobDeadLettered`, plus the `JobExternal*` reconciliation trio) and
> `_platform.dataingestion` (`IngestionRunCompleted`, `IngestionRunFailed`). They are domain events, so they do
> not pass through the audit codec registry, and the audit-sink replicator does not carry them — it filters on
> `_platform.audit`. Revisions of this section before the inventory was generated listed both families as
> shipped audit events, which they have never been. Wiring a SIEM alert for job failures means subscribing to
> the job source module, not the audit feed.

## External audit replication

The `IAuditSink` substrate mirrors every `_platform.audit` event to one or more external sinks the deploying organisation does not control — required for SOC 2 / HIPAA / GDPR Article 30 / SOX compliance.

A sink declares the schema version it emits, so a downstream consumer can tell a format change from a content change:

```fsharp skip=signature
type IAuditSink =
    abstract Name: string
    abstract SchemaVersion: int
    abstract Deliver: batch: AuditEnvelope list -> Async<Result<unit, string>>
```

`Deliver` takes `AuditEnvelope`s — the sink-facing projection, not the raw store `ModuleEvent` — and the whole batch is retried on `Error`, which is why an implementation must be batch-idempotent.

Wiring:

```fsharp skip=fragment
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
- **Exhaustive event coverage (registry-driven decode)**: every `AuditEvent` case is decoded for the sink through the single codec registry in `AuditLog.fs` (shared with `IAuditLog.GetAuditTrail`). A reflection-based exhaustiveness test fails the build if a new audit case is added without a registry entry — so external sinks can never silently miss an event type. Unrecognised (future) event types and malformed payloads both land in the per-batch `AuditEventDecodeFailed` summary row rather than advancing the cursor with no signal.

> **Backfill note (upgrading from a pre-registry SDK).** Builds before the audit-event registry shipped used a partial inline decoder in the replicator that delivered only ~21 of ~80 event types and silently dropped the rest (admin grants/revocations, surface denials, share-token use, OAuth connects, consent records, and more). Deployments that ran audit replication before upgrading therefore have **external trails missing those types for the pre-upgrade window**. The on-platform audit trail (`IAuditLog.GetAuditTrail`) is unaffected — only the replicated copy in external sinks is incomplete. To backfill an external sink after upgrading: delete (or reset to the empty state) the per-`(sinkName, scopeId)` cursor blobs under `_platform/audit-replicator/{sinkName}/{scopeId}.cursor`, then let the catch-up sweep re-read each scope's full audit history from the event store and re-deliver every event from the beginning. Sinks are batch-idempotent (vendor dedup keys), so events already present are deduplicated at the destination; only the previously-dropped types are net-new. Re-replication volume is proportional to retained audit history — schedule it off-peak for high-volume tenants.

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

Signing secrets live in `ISecretStore` and are resolved per delivery, so a rotation needs no redeploy. Across **several instances** it is eventually consistent rather than instant: a caching secret store would otherwise keep the superseded value on every instance that did not perform the rotation, so a successful rotation publishes a reference-only invalidation notification on the reserved platform topic and each instance drops its cached secret material for that scope. Convergence is bounded by the configured `INotificationChannel` companion's fanout latency — which means more than one instance needs a **distributed** channel, since the in-process default never leaves the publishing process. Full contract, including the unwired-rotation accounting: technical guide chapter 10, "Signing-secret rotation across instances".

## Reading the audit trail

Admin-UI access:

```fsharp skip=fragment
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

```fsharp skip=fragment
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

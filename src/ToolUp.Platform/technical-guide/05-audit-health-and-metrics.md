# ToolUp.Platform Technical Guide — 05. Audit, Health & Metrics

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 4. Data & Storage Substrate](04-data-and-storage-substrate.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 6. Background Jobs, Ingestion & Diagnostics →](06-jobs-ingestion-and-diagnostics.md)

---

## Audit trail (Phase 9)

Audit events ride the same `IEventStore` plumbing as module events. The `AuditEvent` DU (`Shared/AuditTypes.fs`) is the typed F# surface; the wire format is the existing `ModuleEvent` record with `SourceModule = "_platform.audit"` (constant `AuditTypes.AuditSourceModule`) and `EventType` set to the DU case name (`"UserLoggedIn"`, `"TeamCreated"`, …). Storage and query reuse `IEventStore.Write` / `ReadBySource` — no new persistence layer.

`IAuditLog` (`Server/AuditLog.fs`) is the SDK-facing surface:

```fsharp
type IAuditLog =
    abstract Record: scopeId: string * audit: AuditEvent -> Async<unit>
    abstract GetAuditTrail:
        scopeId: string *
        dateRange: (DateTime * DateTime) option *
        eventType: string option ->
            Async<AuditEvent list>
```

`Record` is best-effort **by default**: `EventStoreAuditLog` catches, counts (`toolup.audit.write_failures_total`, Phase 114), and logs `Warn` on failure rather than rolling back the primary operation — same semantics as `MembershipChanged` publication in `TeamStore`. Production deployments that need the audit log to be the system of record (regulated sectors) should layer the deferred `IAuditSink` companion — and select a stricter failure policy (below).

### Audit-write failure policy (Phase 9t)

`ServerConfig.AuditFailurePolicy` (fluent: `ServerApp.withAuditFailurePolicy`, mirrored on every superset; env: `TOOLUP_AUDIT_FAILURE_POLICY=log|refuse|degrade`) selects what a failed audit write does beyond the Phase 114 counter:

- **`LogAndContinue`** (default) — the pre-9t behaviour byte-for-byte: the action completes, the record is lost, the loss is counted + logged.
- **`RefuseAction`** — compliance-grade: `Record` raises `AuditWriteRefusedException` ("audit unavailable"), which propagates through the emission site so the user's action fails visibly rather than committing un-audited. SOC 2 / HIPAA / GDPR Art. 30 / SOX continuous-audit postures want this.
- **`DegradeToFile`** — availability-grade: the failed record (the full `ModuleEvent` envelope) spills to a bounded local directory (`audit-fallback/` under the working directory; `withAuditFallbackDirectory` to override; 64 MB cap, at-capacity drops are loud `Error` logs). Deliberately local disk, not `IBlobStorage` — when the blob-backed event store is down, blob storage generally is too. `AuditFallbackReplayService` (a `BackgroundService`, gated as `AuditFallbackReplaySubsystem` on the process-profile matrix) drains the spill back into the live `IEventStore` every 60 seconds once writes recover, oldest first, deleting each file only after its write succeeds; a corrupt spill file is quarantined as `*.json.poison` so it can never wedge the drain. Replayed events flow through the decorated store, so webhook fan-out and `OnEvent` job triggers fire for recovered records — late, but not lost.

The `AuditLogHealthCheck` probe (sentinel write + read) surfaces store degradation on the health endpoint and in `HealthMonitorUI` regardless of policy.

**Emission sites** (handler-layer, where the actor's `userId` is available via `ctx.Items["ToolUp.UserId"]`):

| Event | Emitted from |
|---|---|
| `UserLoggedIn` | `ScopeResolutionMiddleware` first-seen-this-session — tracked in `IMemoryCache` so per-request emission stays bounded |
| `TeamCreated` / `MemberAdded` / `MemberRemoved` / `MemberRoleChanged` | `teamApiHandler` after each successful `ITeamStore` mutation |
| `FileUploaded` / `FileDeleted` | `fileManagementApi` after each successful `SessionFileStore` write |
| `PermissionChanged` | `permissionApiHandler` after `SetMemberPermissions` (granular, per-module-per-user) and `SetTeamDefaults` (bulk; `AffectedUserId = ""` and `ModuleName = ""` denote the team-defaults map) |
| `AnalysisRun` | _Module code_ — the DU case ships in the SDK so module authors can emit consistently; the SDK doesn't name a module |

## Audit replication to external sinks (Phase 9g)

Phase 9 makes the audit log queryable through `IAuditLog.GetAuditTrail`. Phase 9g makes it **mirror-able** to a sink the deploying organisation does not also control — the load-bearing requirement for SOC 2, HIPAA, GDPR Article 30, SOX. Without external export, the deploying organisation owns both the audit emission AND the audit storage, which auditors typically reject as a separation-of-duties failure.

### Architecture

`IAuditSink` (`Server/IAuditSink.fs`) is the portable shape every sink implements:

```fsharp
type IAuditSink =
    abstract Name: string
    abstract Deliver: batch: AuditEvent list -> Async<Result<unit, string>>
```

`Name` is the deployment-unique sink identifier — the `AuditReplicator` keys cursors by it. `Deliver` is the single delivery primitive; the sink is a stateless transport, all batching / retry / dead-letter / cursor management lives in the dispatcher.

`AuditReplicator` (`Server/AuditReplicator.fs`) is the SDK-default `BackgroundService`. Constructed in `compose` only when `ServerApp.AuditSinks` is non-empty; deployments without external replication pay zero runtime cost (no DI registration, no decorator, no hosted service).

Two pumps feed the same per-batch delivery code:

1. **Live hook.** `AuditReplicationHookedEventStore` decorator wraps the inner `IEventStore`. On every `Write`, it filters via `shouldReplicate` (audit `SourceModule` and not one of the three replicator-self event types) and pushes onto a per-sink bounded `Channel` (capacity 1024, drop-write on overflow). One worker per sink drains the channel, accumulates micro-batches up to `BatchPolicy.MaxBatchSize` events or `BatchPolicy.LingerMs` ms, calls `Deliver`. Sub-second steady-state latency.

2. **Catch-up sweep.** Periodic timer (default 5 min, `CatchUpSweepInterval`) calls `IEventStore.ListScopes` and `ReadBySource` per scope, filters events strictly newer than the persisted cursor, delivers any missed batches. Recovers events dropped on bounded-channel overflow, sink-down windows that didn't trigger restart, or events written by a sibling silo. Set `TimeSpan.MaxValue` to disable (restart-only recovery).

Both pumps advance the same per-`(sinkName, scopeId)` cursor (`_platform/audit-replicator/{sinkName}/{scopeId}.cursor`), serialised by a per-scope `SemaphoreSlim` so they don't race for the same scope. `deliverBatch` re-loads the cursor and filters via `AuditReplicatorCursor.isAfter` before delivery, so the sweep + live-hook delivering the same event no longer double-delivers — the second invocation finds the cursor advanced past the batch and skips it.

### Wiring

```fsharp
let auditSink =
    S3Archive.create
        "s3-prod-audit"
        { Container = "acme-audit-prod"; PathPrefix = Some "v1" }
        blobStorage

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withStorage blobStorage
|> ServerApp.withAuditSink auditSink
|> ServerApp.run
```

The `compose` layer:
- Validates `Name` uniqueness across registered sinks; duplicate `Name` registrations fail the deployment loudly at startup (mirrors Phase 6f duplicate-kind validation on `INotificationSink`).
- Wraps `innerEventStore` with `AuditReplicationHookedEventStore` (the audit decorator becomes the new "effective inner" passed to the webhook subsystem and downstream decorator chain).
- Constructs `AuditReplicator` with the registered sinks + `BlobAuditReplicatorCursorStore` + `AuditReplicatorOptions`.
- Registers `AuditReplicator` as `IHostedService` so cancellation flows through ASP.NET Core's shutdown sequence.
- Registers each `IAuditSink` as a DI singleton for test / diagnostic visibility.

### Replicator self-emission

The replicator emits three audit events: `AuditSinkDelivered` (after every successful batch), `AuditSinkFailed` (per retryable failure), `AuditSinkDeadLettered` (on retry exhaustion). All three appear in `IAuditLog.GetAuditTrail` for operator visibility under the reserved `_platform` scope.

The replicator's hook decorator filters these three event types via `isReplicatorSelfEvent` so they do NOT loop back into the queue (anti-recursion guard). Side effect: replicator-self events route through a separate `EventStoreAuditLog` wrapping the **raw inner store**, bypassing the audit decorator AND the webhook hook. Operators relying on webhook-driven alerts for `AuditSinkDeadLettered` should poll `IAuditLog.GetAuditTrail` filtered to that event type instead, or watch the replicator's own `ILogger`.

### Tuning

`ServerApp.withAuditReplicatorOptions` overrides the defaults:

| Field | Default | Trade-off |
|---|---|---|
| `RetryPolicy.MaxAttempts` | 5 | Higher → longer dead-letter latency under sustained sink failure |
| `RetryPolicy.InitialBackoff` | 1.0s | Lower → faster recovery from transient blips |
| `RetryPolicy.MaxBackoff` | 60.0s | Cap on exponential growth |
| `BatchPolicy.MaxBatchSize` | 100 | Higher → fewer requests per second; trades latency for throughput |
| `BatchPolicy.LingerMs` | 1000 | Lower → freshness; higher → batch efficiency |
| `BatchPolicy.QueueCapacity` | 1024 | Bounded channel ceiling — drops events under sustained burst load |
| `CatchUpSweepInterval` | 5min | Lower → faster recovery from drops; higher → cheaper per-tick I/O |

### Six-rule portability audit verdict

| Rule | Verdict |
|---|---|
| 1 — Identity by value | `Name : string`, payload is `AuditEvent list`. ✓ |
| 2 — Async at every boundary | `Deliver: AuditEvent list -> Async<Result<unit, string>>`. ✓ |
| 3 — Retry as data | `AuditReplicatorRetryPolicy` record; no callbacks. ✓ |
| 4 — Stateless handlers | Cursor in `IBlobStorage`; per-sink `Channel` is in-process state but recoverable via cursor — Akka/Orleans companion replaces the channel without changing the interface. ✓ |
| 5 — No cross-shard ordering | Per-scope cursor; cross-scope ordering not promised. ✓ |
| 6 — Precision lower bound | Latency target "within seconds" (eventual consistency); no sub-second contract. ✓ |

Audit clean. Any future distributed companion (Akka.NET / Orleans / Hangfire) binds the `IAuditSinkContract` test pack against its own implementation without modifying `IAuditSink`.

### Reference companions

Three companions ship under `src/AuditSinks/`:

| Companion | Transport | Paid deps | Compliance role |
|---|---|---|---|
| `S3Archive` | `IBlobStorage.Upload` (gzipped JSONL) | None — uses the abstract `IBlobStorage` | WORM archive when bucket-level Object Lock is enabled (S3) / immutable storage policy (Azure) / retention policy (GCS) |
| `SplunkHec` | HTTP POST to Splunk HEC `/services/collector/event` | None — BCL `HttpClient` | SIEM intake; Splunk admins route by `sourcetype = toolup_audit` |
| `DatadogLogs` | HTTP POST to Datadog `/api/v2/logs` | None — BCL `HttpClient` | SIEM intake; Datadog filters by `service:toolup` + per-tenant `scope_id:` tags |

Each companion's README documents its wire format, secret-rotation model, and status-code handling. Adding a new vendor (Sumo, Elastic, custom SIEM) means a new `src/AuditSinks/<Vendor>/` companion implementing `IAuditSink` — the dispatcher is unchanged.

### Regulatory notes

| Regime | What it requires from the sink config |
|---|---|
| SOC 2 (Trust Services Criteria CC7.2 / CC7.3) | Continuous logging, retention, evidence the audit trail is independently maintained. S3Archive with Object Lock + 7-year retention is the canonical configuration. SplunkHec / DatadogLogs satisfy "independently maintained" via separate vendor account but rely on the vendor's retention policy. |
| HIPAA (45 CFR §164.312(b)) | "Implement hardware, software, and/or procedural mechanisms that record and examine activity in information systems that contain or use ePHI." Pair the audit replication with `_platform.audit` containing `EncryptionKey*` events so the audit trail records key destruction (crypto-shred for tenant offboarding). |
| GDPR Article 30 | Records of processing activities. The audit trail covers Article 30 (1)(a)–(g) — controller identity, purposes, categories of data subjects, recipients, transfers — by virtue of recording every state-change. The replicated trail satisfies the "demonstrable" requirement. |
| SOX (§404 internal controls) | Independent audit log retention. S3Archive WORM is the load-bearing control. |

Deploying organisations choose the policy, configure the sink, and accept legal liability for the configuration. The SDK provides the tools.

### Single-instance limitation

The bounded channel and per-scope semaphores are in-process. Running multiple silos with the same sink configuration each consume the post-write hook and double-deliver. The cursor write is monotonic so duplicates are limited to a small window, but at-most-once is not guaranteed for distributed deployments. The Phase 9c half 2 distributed companion (planned, gated on multi-node testing infrastructure) will resolve this via `IDistributedLock` (Phase 9i) leader election. Until then, run the audit-emitting tier as `replicas: 1` or accept bounded duplicates.

## Health, request timing, quotas, and rate limiting (Phase 9 + 9k)

**Health endpoints.** `app.MapHealthChecks("/health", …)` and `app.MapHealthChecks("/ready", …)` are mapped before `UseGiraffe` so ASP.NET Core's endpoint routing matches and short-circuits the Giraffe terminal middleware. `/health` uses `HealthCheckOptions(Predicate = fun reg -> reg.Tags.Contains "Liveness")` — runs only probes tagged `Liveness` (today: zero, response is `200 OK` vacuously when the process accepts requests). `/ready` runs every registered probe (both Liveness and Readiness) and returns the worst-status aggregate. Both endpoints use the Phase 9k `HealthCheckResponseWriter` which emits JSON: `{"status": "Healthy" | "Degraded" | "Unhealthy", "checks": [{"name": "...", "kind": "Readiness", "status": "...", "message": "..."}]}`. Unhealthy probes return `503`; Degraded probes stay at `200` with the message in the `checks` payload.

### Phase 9k — `IHealthCheck` extensibility

Companions self-register readiness probes by implementing the portable `ToolUp.Platform.HealthChecks.IHealthCheck` interface (`Name`, `Kind`, `Timeout`, `Check : unit -> Async<HealthResult>`) and exposing a `create` factory. The interface lives in `Shared/IHealthCheck.fs` (pure types: F# DUs + `TimeSpan` + `Async`) so companions referenced via plain `<ProjectReference>` (e.g. the storage backends) see it without `.Server.props` injection.

**Companion-authoring contract.** The probe constructor receives whatever the companion's main impl received — the existing factory (`RedisNotificationChannel.fromConnectionString`, `ClaudeAIProvider.create`, etc.) plus its dependencies (`IBlobStorage`, `ISecretStore`, vendor-specific settings). Wire the probe into the consumer's pipeline alongside the main impl:

```fsharp
// In Server.fs / composition root:
let multiplexer, channel =
    RedisNotificationChannel.connect connectionString (Some logger)
let redisHealth = RedisNotificationChannelHealth.create multiplexer

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withNotifications channel
|> ServerApp.withHealthCheck redisHealth   // ← Phase 9k seam
|> ServerApp.run
```

**Aggregator architecture.** `HealthCheckAggregator.register : IServiceCollection -> unit` runs near end-of-compose (after every companion has called `services.AddSingleton<IHealthCheck>(...)`). It walks the service collection for `IHealthCheck` descriptors, reads `ImplementationInstance` (companions MUST register via instance form, not constructor injection — the aggregator fails loudly on `AddSingleton<IHealthCheck, T>()` because falling back to a temporary `BuildServiceProvider` would create divergent singleton instances), and registers each as a BCL `HealthCheckRegistration` tagged with the probe's `Kind` for `/health` vs `/ready` partitioning. The `BclHealthCheckAdapter` wraps each ToolUp probe with:
- A `CancellationTokenSource` linked to the BCL token + the per-probe `Timeout`. Timeout → `Degraded("probe exceeded {ms}ms")` (NOT Unhealthy — slow ≠ down; Degraded does not flip `/ready` to 503).
- Exception → `Unhealthy("probe threw: " + truncate 500 ex.Message)`. The `"probe threw:"` prefix lets operators distinguish a clean Unhealthy signal from a probe-implementation bug; the 500-char truncation prevents the unauthenticated `/ready` JSON from leaking large stack traces or internal detail.

**Probe naming convention.** `Name` must be unique across registered probes — BCL `AddCheck` rejects duplicates. Companions that may register more than one instance suffix the instance id, e.g. `ai_provider:claude` and `ai_provider:openai`, `transactional_sink:smtp` and `transactional_sink:sendgrid`. First-party probes use bare names (`blob_storage`, `auth_provider`, `event_store`) since only one of each can exist.

**No upstream calls in probes.** AI provider probes (`ClaudeAIProviderHealth`, `OpenAIAIProviderHealth`), embedding provider probes, and transactional sink probes all verify the secret resolves to a non-empty value rather than calling the upstream API. `/ready` is polled on every load-balancer health-check interval, and even cheap upstream calls would generate per-deployment cost during normal operation. The signal is "is the configuration sane enough to attempt a call" not "will the call succeed". Bad keys surface at first-call time through the existing error path. The Redis probe is the exception (issues `PING` against the same multiplexer backing the channel) because Redis PING is credential-free, the network round-trip is a single-digit-ms cost, and the alternative (no probe) would silently miss Redis-disconnected state.

**Six-rule portability audit (Phase 9c — Guiding Principle 12).** Documented inline in `Shared/IHealthCheck.fs`:
1. **Identity by value** — `Name : string` is the registration key. No live framework handle on the surface.
2. **Async** — `Check : unit -> Async<HealthResult>`. No sync escape hatch.
3. **Retry as data** — no built-in retry. Probes are polled by the orchestrator (Kubernetes `failureThreshold`, AWS ALB `HealthyThresholdCount`); retry is the orchestrator's concern, not the SDK's. Documented as deliberate exclusion.
4. **Stateless handlers** — every `Check ()` invocation must read fresh dependency state. No assumption of cached / in-memory continuity between calls — Orleans / Akka.Persistence implementations may deactivate or restart between probes.
5. **No cross-shard ordering** — probes are independent; the aggregator runs them in parallel and reports per-probe outcomes. No probe-to-probe ordering claim.
6. **Precision at the lower bound** — `Timeout : TimeSpan` is per-probe (not per-aggregator). A Redis PING can declare 200ms while an OIDC discovery fetch declares 10s; the aggregator caps each at its own value. A hardcoded global 5s constant would have failed Rule 6 by hiding implementation-specific precision behind a flat ceiling.

**Test pack.** `IHealthCheckContract` (7 tests) covers the three-valued return shape, stable identity (Rule 1), concurrent-invocation safety (Rule 4), and timeout-budget compliance — parametrised over `factory : unit -> IHealthCheck` and an expected outcome variant. Three fake bindings (`HealthyHealthCheckTests`, `DegradedHealthCheckTests`, `UnhealthyHealthCheckTests`) exercise each code path. `HealthCheckAggregatorTests` (10 tests) covers adapter behaviour the contract pack can't: timeout → Degraded translation, exception → "probe threw:" Unhealthy with truncation, per-Kind tag partitioning, GP-13 zero-probe success path, rejection of constructor-injected registrations. Companion bindings (`RedisNotificationChannelHealthTests`, `AIProviderHealthTests`) reuse the contract pack with their own factories — env-gated where they need real backends.

**Deferred follow-ups.**
- **~~State-change audit events~~** — **shipped 2026-05-04 as Phase 9p `HealthStateTracker`** (see Phase 9p section below). Opt-in via `ServerConfig.HealthStateTracking` (default `false`, GP 13). Wall-clock-aligned 1-min ticks emit `HealthStateChanged` audit events under `_platform` after 3 consecutive observations of a new stable status — single-observation flaps from 1–10 Hz LB polling are absorbed.
- **Probe self-health** (does the aggregator itself work?): implicit in whether `/ready` returns at all — aggregator failure surfaces as 500 (unhandled), not 503. Orchestrators treat 5xx as down regardless. No probe needed.
- **Per-companion configurable `Timeout` overrides** beyond the per-impl default: not needed today (the per-impl `Timeout` field already covers the Redis-PING-200ms vs OIDC-10s spread). If needed later, plumb through `ServerConfig` as `HealthCheckTimeoutOverrides : Map<string, TimeSpan>`.

### Phase 9m — `IConfigValidator` startup preflight

`IHealthCheck` answers "is the dependency reachable right now?" on every `/ready` poll. `IConfigValidator` answers "is the dependency reachable at deploy time?" exactly once, before HTTP binds. The two interfaces share a near-identical companion-self-registration pattern but live in separate substrates because their lifecycles are orthogonal — preflight runs heavier probes (sentinel write/read/delete vs. cheap `Exists`) and aborts startup on failure rather than flipping `/ready` to 503.

**Companion-authoring contract.** A validator implements `ToolUp.Platform.ConfigValidation.IConfigValidator` (`Name`, `Timeout`, `Validate : unit -> Async<ValidationResult>`) where `ValidationResult = Ok | Warning of string | Error of string`. The interface lives in `Shared/IConfigValidator.fs` for the same `<ProjectReference>` reach as `IHealthCheck`. Validators expose a `create` factory (and optionally a `tryFromEnv` returning `Option` when the activating env var is unset — GP 13: a deployment without the dependency must not be punished for importing the companion's props):

```fsharp
// In Server.fs / composition root:
ServerApp.empty
|> ServerApp.withConfig config
|> (fun app ->
    OidcAuthValidator.tryFromEnv ()
    |> Option.fold (fun acc v -> ServerApp.withConfigValidator v acc) app)
|> ServerApp.run
```

When an explicit construction makes more sense (the deployment already built the dependency and wants the validator to share its handle — Redis multiplexer, SMTP `Settings`), use `create` directly:

```fsharp
let multiplexer, channel = RedisNotificationChannel.connect connectionString (Some logger)
let validator = RedisNotificationChannelValidator.create multiplexer
ServerApp.empty
|> ServerApp.withNotifications channel
|> ServerApp.withConfigValidator validator   // ← Phase 9m seam
```

**Aggregator architecture.** `ConfigValidatorAggregator.validate : IServiceCollection -> ILogger option -> bool -> ValidatorOutcome list` runs near end-of-compose (immediately before `HealthCheckAggregator.register` and `builder.Build()`). It walks `services.AddSingleton<IConfigValidator>` registrations, requires `ImplementationInstance` form (constructor-injected fails loudly with the same error as Phase 9k), runs all validators in parallel via `Async.Parallel`, and applies two timeouts:

1. **Per-validator `Timeout`** (default 5s) — clamped to the 10s aggregator budget. A validator declaring 60s effectively gets 10s; a validator declaring 200ms gets 200ms. Timeout fires `OperationCanceledException` → outcome `Error("validator exceeded timeout (Xms)")`.
2. **Global aggregator budget** (10s, file-private constant) — backstop so a misconfigured validator can't block startup indefinitely. Functions as the upper clamp on per-validator timeouts.

**Abort-vs-warn semantics.**
- `Ok` → `Info` log line (silent default for clean deploys).
- `Warning msg` → `Warn` log line, startup proceeds. Use for "dependency reachable but flagged" — Redis PING took 1.2s (returns Warning), OIDC discovery doc has a quirk that doesn't break us today.
- `Error msg` → `Error` log line + raise `ConfigPreflightFailedException(summary)` containing one `[ERROR] {Name}: {message}` line per failing validator (plus a `Warnings (non-blocking, included for context):` block when warnings also occurred). The exception propagates through Kestrel construction and crashes the process with a non-zero exit code — orchestrators (Kubernetes `imagePullBackOff` → `CrashLoopBackOff`, ECS `STOPPED` → `(taskDefinition) failed`, AWS App Runner `ROLLBACK_FAILED`) detect deploy failure on the non-zero exit. No `Environment.Exit` needed.
- Validator throws → the aggregator catches and translates to `Error("validator threw: " + truncate 500 ex.Message)`. Same 500-char truncation as `BclHealthCheckAdapter` (Phase 9k) so the abort summary doesn't leak large stack traces.

**`SkipPreflight` escape hatch.** `ServerConfig.SkipPreflight = true` short-circuits the aggregator: every registered validator is skipped, the snapshot is empty, no exception is thrown. For emergency boots where the operator knows a validator is wrong (broken issuer URL on the deploy critical path, third-party outage you want to ride through). Pair with explicit monitoring — the deployment will not fail loud on the dependency that preflight would have caught. Default `false`; the field deliberately sits next to `EnableDevEndpoints` in `ServerConfig` because both are operator escape hatches.

**Snapshot for `/dev/inspect`.** After `validate` returns, `compose` populates a `PreflightSnapshot` instance and registers it as `IPreflightSnapshot` so `DevDiagnosticsHandler.buildValidators` can surface the captured outcomes in the "Config preflight" panel. Snapshot, not live re-run — re-running validators on every dev-page hit would amplify side effects (sentinel writes, vendor handshakes). Operators see the most recent run's outcome per validator; restart the deployment to refresh.

**Six-rule portability audit (Phase 9c — Guiding Principle 12).** Documented inline in `Shared/IConfigValidator.fs`:
1. **Identity by value** — `Name : string`. No live framework handles.
2. **Async** — `Validate : unit -> Async<ValidationResult>`. No sync escape hatch.
3. **Retry as data** — no built-in retry. Deployment pipeline retries by re-running compose; a future cron-driven re-validate could layer on top via `IJobScheduler` without changing this surface.
4. **Stateless handlers** — validators run once per process; `Validate ()` reads fresh state. No in-memory continuity contract — Orleans / Akka.Persistence implementations may run validators on different nodes per restart.
5. **No cross-shard ordering** — validators run in parallel; outcomes are independent. No validator-to-validator ordering claim.
6. **Precision at the lower bound** — per-validator `Timeout : TimeSpan` (mirrors Phase 9k Rule 6 closure exactly). The 10s global budget is a backstop, not a contract — if a future implementation needs more headroom, the aggregator constant grows; the interface stays the same.

**First-party validators ship in `src/ToolUp.Platform/Server/ConfigValidator.fs`:**
- `BlobStorageValidator(IBlobStorage)` — sentinel write/read/delete at `_platform/preflight/sentinel.bin` with byte-for-byte readback verification. Heavier than Phase 9k's `BlobStorageHealthCheck` (`Exists` only) — the round-trip surfaces silent corruption / write-permission gaps that an existence probe misses.
- `SecretStoreValidator(ISecretStore)` — `GetSecret("_platform", "_platform_preflight_canary")`. `None` is `Ok` (sentinel optional); the call alone exercises the read path and catches permission / decryption / connectivity failures.

Both register automatically when their respective service is registered (always, since both are mandatory).

**Companion validators ship in their respective companion packages:**
- `OidcAuthValidator` (`src/AuthProviders/Oidc/`) — GET `{issuer}/.well-known/openid-configuration` + JSON-shape check for `authorization_endpoint` / `token_endpoint`. `tryFromEnv` reads `TOOLUP_OIDC_ISSUER`.
- `RedisNotificationChannelValidator` (`src/NotificationChannels/Redis/`) — `IConnectionMultiplexer.GetDatabase().PingAsync()` with a 500ms `Warning` threshold. `create` takes the same multiplexer the channel and Phase 9k health probe share.
- `SmtpNotificationSinkValidator` (`src/NotificationChannels/Email/Smtp/`) — TCP-connect to `settings.Host:Port`. No authentication or STARTTLS handshake (matches Phase 9k's reasoning: credential probes generate audit-log noise; TCP reachability is the load-bearing signal). `create` takes explicit `SmtpSettings`; `fromEnv` reads via `SmtpSettings.fromEnv`.

**Test pack.** `IConfigValidatorContract` (7 tests) covers the three-valued return shape, stable identity (Rule 1), concurrent-invocation safety (Rule 4), and timeout-budget compliance — parametrised over `factory : unit -> IConfigValidator` and an expected outcome variant. Three fake bindings (`OkConfigValidatorTests`, `WarningConfigValidatorTests`, `ErrorConfigValidatorTests`) exercise each code path. `ConfigValidatorAggregatorTests` (11 tests) covers aggregator behaviour the contract pack can't: parallel execution, abort-on-`Error`, `SkipPreflight` short-circuit, per-validator timeout enforcement, exception → `Error` translation with 500-char truncation, global budget clamp, snapshot capture, GP-13 zero-validator success path, rejection of constructor-injected registrations.

**Why a snapshot, not a per-request re-run? (Contrast with Phase 9k).** `IHealthCheck` re-runs probes on every `/dev/inspect` hit because health probes are designed to be cheap (`Exists`, PING) — re-running them is no more expensive than a single `/ready` call. Validators are heavier on purpose (sentinel write/read/delete, full-discovery-document fetch + parse). Re-running them on every dev-page reload would amplify side effects (extra writes to the sentinel container, extra OIDC issuer load) and produce inconsistent outputs (a transient network hiccup mid-page-load doesn't mean preflight failed; the deploy already succeeded). The right answer for "did this deploy pass preflight?" is "what did the most recent run show?" — exactly what the snapshot captures.

**Deferred follow-ups.**
- **`ConfigDrift` audit event** (preflight Ok → Error transitions across deploys, emitted under `_platform.audit`): needs the deploy bundle (Phase 9n) to compare runs. Today the aggregator emits to `IEventStore` only via the unstructured log; structured audit emission lands once the bundle has a "previous deployment's snapshot" reference.
- **Cron-driven re-validate** (rerun preflight every N hours via `IJobScheduler` so OIDC issuer rotations are caught between deploys): straightforward layering on Phase 9b once a deployment expresses interest. The interface itself supports re-runs (Rule 4 — stateless).
- **Per-validator severity overrides** (force a `Warning` to `Error`, or vice versa, via `ServerConfig` map) — not needed today. Companion authors choose the right severity at validator-implementation time; if a deployment disagrees with a built-in choice, they replace the validator with a custom one.

### Phase 9p — `HealthMonitorUI` admin module + debounced `HealthStateTracker`

`/dev/inspect` (Phase 9a) is the only place an operator can today see live `IHealthCheck` results or the most recent `IConfigValidator` preflight outcomes. It's runtime-gated by `ServerConfig.EnableDevEndpoints` (default `false`) — typically left off in production deployments and therefore useless for production operators who need to confirm "did this deploy pass preflight?" or "is Redis alive right now?" from a browser they're already signed into. Phase 9p ships the production-safe equivalent: a built-in SDK admin module (`HealthMonitorUI`) and a ToolUp.Remoting surface (`IHealthMonitorApi`) auto-injected for Owner/Admin in any non-Anonymous mode, plus a debounced `HealthStateTracker` that closes the Phase 9k deferred follow-up by emitting `HealthStateChanged` audit events on real probe transitions.

**Public surface.**
- `Shared/HealthMonitorApi.fs` — `IHealthMonitorApi` (`GetCurrentHealth: unit -> Async<Result<HealthSnapshot, string>>` live re-run + `GetPreflightSnapshot: unit -> Async<Result<PreflightSnapshotView, string>>` snapshot read), `HealthProbeView` / `PreflightOutcomeView` wire records (status as string at the boundary so the underlying DUs can evolve without breaking the API contract).
- `Server/HealthMonitorApiHandler.fs` — ToolUp.Remoting handler. RBAC: Anonymous returns `Error "Health monitor is not available in this mode."`; Team / MultiTeam require `TeamRoles.canWriteTeamConfig` (Owner / Admin); Individual / AuthenticatedEphemeral require any authenticated user. Mirrors `WebhookApiHandler.ensureWriteAllowed` with a "view" verb in the failure messages.
- `Server/HealthCheckRunner.fs` — extracted per-probe runner shared by `DevDiagnosticsHandler.buildHealthChecks`, `HealthMonitorApiHandler.GetCurrentHealth`, and `HealthStateTracker.runTick`. Single source of truth for per-probe `Timeout` enforcement (`Degraded` on expiry, not `Unhealthy`), exception → `"probe threw:"` translation with 500-char truncation, and elapsed-ms capture.
- `Client/HealthMonitorUI.fs` — built-in admin module. Reserved Id `_sdk.HealthMonitor`; "Admin" sidebar group alongside `_sdk.TeamConfig` and `_sdk.WebhookAdmin`. Two tabs (Live health, Preflight) as local React state; manual refresh button per tab; pulse-highlight (Tailwind `animate-pulse`) on rows whose status changed since the prior snapshot (via `Set<string>` diff in the Elmish model). `React.useState` for the per-tab "now refreshing" spinner — no per-keystroke Elmish dispatch (matches `UIToolkit.Forms.Input` pattern).
- `Server/HealthStateTracker.fs` — opt-in `BackgroundService` (default off via `ServerConfig.HealthStateTracking`).

**Auto-injection.** `ClientConfig.HealthMonitor: HealthMonitorMode` defaults to `DefaultHealthMonitor`; the client shell's `prepareModules` skips registration when `Mode = Anonymous` or `HealthMonitor = NoHealthMonitor`. `ConfiguredHealthMonitor of HealthMonitorConfig` overrides the default name / icon; `ExternalHealthMonitor of ErasedModule` swaps the SDK module for a deployment-provided custom one (mirrors `WebhookAdminMode` exactly).

The server-side handler is auto-mounted unconditionally (`HealthMonitorApiHandler.healthMonitorApi config.Mode` next to `configHandler` in `compose`'s router list) — the per-request RBAC gate inside the handler short-circuits Anonymous and Member-role callers, so unconditional registration is zero-cost when the deployment doesn't enable the sidebar entry. A deployment that ships only a custom client can still query the API directly.

**GP 4 carve-out — deployment-wide probe visibility.** Health probes are deployment-wide by nature (`blob_storage`, `redis-notification`, `oidc-auth`) — they describe the deployment's external dependencies, not any one team's data. Every Owner/Admin in every team in `MultiTeam` mode sees the same probe list. The API never returns per-tenant data; the surface is read-only and identical across tenants. This is the deliberate exception to GP 4 (team isolation) — documented inline in `HealthMonitorApiHandler.fs` so the choice is auditable. Member-role users without an RBAC denylist entry on `_sdk.HealthMonitor` see the sidebar entry but each tab renders an "only owners and admins" error banner; deployments that want the entry hidden entirely add the module Id to the Member denylist via the Configuration admin.

**Refresh model.** Manual click only — no auto-poll. Auto-polling on a 5s interval against a deployment with 13 probes × N admin tabs would saturate the probe surface (death by a thousand pings). SSE push for state-change events is a deferred follow-up. The "refresh button" pattern preserves operator agency — they refresh when they want a fresh read, the page doesn't drive load on its own.

**Why a snapshot for Preflight (not a live re-run)?** Same reasoning as Phase 9m's `IPreflightSnapshot`. Validators are heavier than health probes (sentinel writes, DNS resolution, full discovery-document fetches). Re-running them on every UI refresh would amplify side effects and produce inconsistent outputs (a transient network hiccup mid-fetch doesn't mean preflight failed; the deploy already succeeded). The Preflight tab's "Re-fetch" button reads `IPreflightSnapshot.LastRun` again — useful so a deployer can confirm "did the redeploy I just kicked off pass?" without a hard reload.

**`HealthStateTracker` algorithm.** Opt-in via `ServerConfig.HealthStateTracking: bool` (default `false`, GP 13). When `true`, `compose` registers the tracker as `IHostedService` with a factory that captures the local `auditLog` and `resolvedLogger` instances and resolves `IHealthCheck`s from the live `IServiceProvider` per tick.

Per tick (wall-clock-aligned 1-min cadence, mirroring `JobScheduler.InProcessJobScheduler.ExecuteAsync`):

1. Run every probe in parallel via `HealthCheckRunner.runOne`.
2. Convert each `ProbeRun.Status` string back into a `HealthResult` (`Healthy` / `Degraded msg` / `Unhealthy msg`) so the state machine compares structured values.
3. For each probe, append the new observation to a per-probe rolling buffer of length `RequiredConsecutive = 3` (oldest drops off the front).
4. Compute the buffer's "stable status" — if the buffer holds 3 identical statuses, that's the stable state; otherwise stable status is `None` ("no transition observable yet"; the prior committed stable state is unchanged).
5. If the new stable status differs from the tracked stable status, emit a `HealthStateChanged` audit event under `_platform` scope (`{ ProbeName; FromStatus; ToStatus; Message; ObservedAt }`) and update the tracked stable status.
6. First-ever stable state for a probe synthesises a transition from `"Healthy"` by convention so the trail records the *change* even when the very first three observations were Unhealthy. A first-ever stable state of `"Healthy"` is silent — already on the assumed baseline.

`RequiredConsecutive = 3` is hardcoded for Phase 9p; per-probe overrides via `ServerConfig` are deferred — three consecutive observations × 60s = 3 min to a settled stable state, which is the right shape for an audit-emitting layer.

**`AuditEvent.HealthStateChanged` payload.** `ProbeName : string`, `FromStatus : string`, `ToStatus : string` (status strings at the DU boundary, not the `HealthResult` DU, so future `HealthResult` changes don't ripple into persisted audit payloads), `Message : string` (last observation's message — empty for transitions into Healthy, carries failure detail for Degraded / Unhealthy), `ObservedAt : DateTime`. Serialised through `EventStoreAuditLog` like every other audit case.

**Six-rule portability audit (Phase 9c — Guiding Principle 12).**

| Rule | Verdict | Notes |
|------|---------|-------|
| 1 — Identity by value | Pass | `ProbeName : string`. No live framework handles cross the wire. |
| 2 — Async at every boundary | Pass | Both API methods return `Async<Result<_, string>>`. `IAuditLog.Record` is async. `BackgroundService.ExecuteAsync` is `Task`-returning by base contract. |
| 3 — Retry as data | Pass | No built-in retry on probe runs. The tracker's debounce is encoded as an in-memory rolling-window count, not a callback. |
| 4 — Stateless handlers | Pass with caveat | API handlers are stateless between calls. `HealthStateTracker`'s per-probe rolling buffer is in-memory state — flagged single-instance for the Phase 9c half 2 distributed companion alongside `JobScheduler` / `ClientToolDispatch` / `AICancellationRegistry`. The buffer reconstructs after restart from probe runs (3 obs × 60s = 3 min to settle) which is acceptable for an audit-emitting layer. |
| 5 — No cross-shard ordering | Pass | Probes are tracked independently; transitions emit one event per probe per stable-state change. No probe-to-probe ordering claim. |
| 6 — Precision at the lower bound | Pass | Tracker tick = `JobPrecision.Minute` (matches the SDK's documented floor). Per-probe `IHealthCheck.Timeout` is the per-probe contract, unchanged. |

**Test pack.** No new `IXxxContract` pack — `HealthMonitorApi` is a thin read-side aggregator over surfaces already covered by `IHealthCheckContract` + `IConfigValidatorContract`. Six in-process tests in `HealthStateTrackerTests.fs` cover the tracker's algorithm:

1. Tick emits no audit when buffer is shorter than `RequiredConsecutive`.
2. Single-observation flap (Healthy×3 → Unhealthy → Healthy×3) is absorbed by the debounce — zero events.
3. Three consecutive transitions emit exactly one audit event under `_platform` scope with the expected `From`/`To`/`Message` fields.
4. Stable state does not re-emit on subsequent identical observations (six more Unhealthy after the transition still emits only one event).
5. Per-probe isolation — two probes transitioning emit two distinct events keyed by `ProbeName`.
6. GP 13: `HealthStateTracking = false` does not register the BackgroundService; `= true` registers exactly one `HealthStateTrackerService`.

**`/dev/inspect` cross-link.** The existing Health checks and Config preflight panels each gain a one-line note pointing operators at the production-safe `Health Monitor` admin module. No new DTO fields — keeping the dev report and the user-facing UI structurally separate so `/dev/inspect` stays usable when the new module is broken.

**Deferred follow-ups.**
- **SSE-pushed state-change notifications** so the UI can reflect a flip without a manual refresh. The `HealthStateChanged` audit event is the right hook — wire a channel publisher next to the audit emit.
- **Per-probe `RequiredConsecutive` overrides** via `ServerConfig` (e.g. tighter debounce for probes with low cadence). Today's hardcoded 3 is deliberate — the right tuning emerges only from production data.
- **Probe-history trend graph** in the Live health tab (sparkline of last 60 minutes of stable states). Needs the tracker to persist its state map to blob storage so the graph survives a restart; a follow-up paired with the Phase 9c half-2 distributed companion which will need persistent state for the same reason.

### Phase 9q — startup-time config drift detector

`ConfigDriftDetector` (`Server/ConfigDriftDetector.fs`) catches "someone changed an env var without updating the deployment manifest" — the staging-vs-prod drift that turns into "why is the same build behaving differently?" at 2am. At the end of `compose` (after `builder.Build()` so the loaded-assembly enumeration sees the fully-bound `AppDomain`) the detector serialises the resolved `ServerConfig` with secrets redacted, hashes the active companion-assembly set, persists the snapshot to `_platform/_deploy/last-config.json`, and compares against the previous startup's snapshot. Differences emit one `Warn` log line summarising changed paths plus one `ConfigDrift` audit event under `_platform.audit` (payload: `Changes : ConfigDriftChange list`, `CompanionSetFrom`/`CompanionSetTo : string`, `SnapshotTakenAt : DateTime`). Pure observation — no abort, no rollback, no startup gate.

Opt-in via `ServerConfig.ConfigDriftDetection = NoConfigDriftDetection | EnabledConfigDriftDetection`. Default `NoConfigDriftDetection` (GP 13) — stock deployments do no read, no write, no compare, and the `_platform/_deploy/` blob layout is not touched. Operators pair `EnabledConfigDriftDetection` with `AuditLog = EnabledAuditLog` for the durable side of the emission; the `Warn` log fires regardless, so a "log-only" stance (drift detection enabled, audit log disabled) is supported and explicit.

**What counts as drift.** Any field-level difference in the resolved `ServerConfig` — the JSON walker recurses through nested records, DUs (case-name shape via `FableJsonConverter`), `Map`s, `Set`s, lists, and primitives. The diff produces dotted paths (`AuditLog`, `RateLimit.RequestsPerWindow`, `SecurityHeaders["X-Frame-Options"]`-style). Additions (`From = None`) and removals (`To = None`) surface separately from value changes. Any change in the SHA-256 hash of the active companion set is itself drift — a new `<PackageReference>`, a removed `.Server.props` import, or a NuGet bump on an existing companion all change the set of loaded `ToolUp.*` assemblies and flip the hash. The audit payload carries both `Changes` (per-field) and `CompanionSetFrom`/`CompanionSetTo` so operators see the structural change even when no `ServerConfig` field was touched.

**What doesn't.** The snapshot timestamp (`snapshotTakenAt`) travels in the persisted blob for forensic context but is not part of the diffed surface — it changes on every restart and would drown the signal. Any future build-commit / build-time / process-id field added to the snapshot follows the same convention: travels in the header, not in the comparison. The first startup on a fresh deployment finds no prior snapshot, writes the new one, and emits nothing — a synthetic "everything changed" event on first deploy would teach operators to ignore the channel.

**Failure mode.** Every step — blob read, blob write, JSON parse, audit emit — is wrapped in `try/with` and on failure logs at `Warn` and proceeds. The detector is a diagnostic aid, not a control-plane gate; a transient blob-store hiccup must not block startup.

**Request timing.** `RequestTimingMiddleware` sits between `AuthEnforcementMiddleware` and `RemotingBodyNormalizationMiddleware`. It wraps each request in a `Stopwatch` and logs `Warn` once `elapsed > ServerConfig.SlowRequestThreshold` (default 1s). The log call is wrapped in `try/with` so a logger failure never propagates to the request. No audit event — slow requests are an operational signal, not a state change.

**Storage quota.** `ServerConfig.DefaultTeamStorageQuotaBytes` is opt-in (`None` accepts any size). `compose` builds a `quotaResolver: scopeId -> Async<int64 option>` from the config value and registers it via `FileManagement.configureQuotaResolver`. `SessionFileStore.AddFile` consults the resolver, sums `files.Values |> Seq.sumBy SizeBytes`, and returns `Error "Storage quota exceeded: …"` before any persist or process work runs when `existingBytes + sizeBytes > limit`. **No audit event written, no file persisted** — rejection isn't a state change. The resolver shape supports a future per-team override read from `IConfigStore` (Phase 5a) without changing the public surface.

**Rate limiting.** `ServerConfig.RateLimit` is opt-in. The limiter is `Microsoft.AspNetCore.RateLimiting`'s built-in fixed-window, partitioned by team (`team-{teamId}` scope), user (other authenticated modes), or remote IP (Anonymous). On breach: `429 Too Many Requests` plus a `Retry-After` header equal to `WindowSeconds`. Bypassed paths: `/health`, `/ready`, `/api/notifications` (the long-lived SSE connection — counting it as one request per minute would saturate the bucket immediately). The middleware (`app.UseRateLimiter()`) is only inserted when `ServerConfig.RateLimit.IsSome`, so deployments that don't want a per-scope cap pay zero overhead.

### Phase 9o — post-deploy smoke-test endpoint (`/api/_internal/smoke`)

Different from `/ready` (Phase 9k), which is a per-component liveness/readiness probe polled on every load-balancer interval. The smoke endpoint exercises every wired companion path end-to-end (write/read a sentinel blob, publish + observe a sentinel notification, schedule + dispatch a sentinel job, …) against the reserved `_smoke` sentinel scope. Intended to run once per deploy as a pre-traffic gate.

**Deploy-pipeline integration.** After a blue/green flip — and **before** pointing the load balancer at the new instance — the deploy script issues:

```bash
curl -fsS -H "X-Smoke-Token: $TOOLUP_SMOKE_TOKEN" "$NEW_INSTANCE/api/_internal/smoke"
```

`200` = every wired companion's integration is healthy; the LB flip can proceed. `503` = at least one probe failed; the response body lists the failing tests with per-test messages, and the deploy script rolls back. `401` = `TOOLUP_SMOKE_TOKEN` is unset on the server side or the header doesn't match; the script treats this as "smoke endpoint not configured" and either configures the token or skips the gate per local policy. Idempotent: every run uses a unique nonce in its sentinel writes and cleans up after itself, so retries during a flaky deploy do not accumulate sentinel data.

**Sentinel-scope contract.** Every smoke test runs against `SmokeTest.SentinelScope = "_smoke"`. Implementations pass this literal to their backing interface's `scopeId`-shaped parameter (`IBlobStorage.Upload`, `IDataObjectStore.Save`, `IJobScheduler.Schedule`, `INotificationChannel.Publish`, `IAuditLog.Record`). A sentinel write to `"_smoke"` must not appear in any real tenant scope's listing — the scope-isolation contract every backing store already honours via `StorageScope` derivation and scope-prefixed event records. The reservation is one-way: `"_smoke"` is owned by the smoke endpoint; no real tenant has it.

**Cleanup invariant.** Tests that produce persistent state (blob, data object, scheduled job, subscription) MUST clean up before returning. The cleanup runs inside `try`/`finally` so a failing test still removes its sentinel state. Tests whose backing interface has no delete surface (event store, audit log) leave their sentinel record in scope `"_smoke"` — the sentinel scope itself absorbs the cost, and the cumulative footprint per deploy is one event + one audit row, bounded by the deploy cadence rather than tenant traffic. Operators auditing the trail filter on `scopeId = "_smoke"` to read the smoke chain without it polluting per-tenant queries.

**Opt-in.** `ServerConfig.SmokeTest = NoSmokeTest | EnabledSmokeTest` (default `NoSmokeTest`, GP 13). The default leaves the route unmounted and registers no first-party smoke tests; the surface 404s. `EnabledSmokeTest` mounts the route behind the token gate and registers six first-party probes (`BlobStorageSmoke`, `NotificationChannelSmoke`, `EventStoreSmoke`, `DataObjectStoreSmoke`, `AuditLogSmoke`, and `JobSchedulerSmoke` when `JobScheduler != NoJobScheduler`). Companion-contributed probes register via `ServerApp.withSmokeTest test` or directly through `services.AddSingleton<ISmokeTest>(...)`.

**Token gating.** Authentication is via the `X-Smoke-Token` request header against `TOOLUP_SMOKE_TOKEN` env var, compared in constant time. Two failure paths both surface as `401`: (1) the env var is unset (the operator gets a message naming the env var so the configuration gap is obvious from the deploy log), (2) the header is missing or mismatched (generic message, no echo of the supplied value). The token is read fresh from the env var on every request — rotation does not require a restart. The token never appears in the audit-event payload, never in the response body, never in any diagnostic surface; logging it is a defect.

**Audit emission.** One audit row per invocation under `SourceModule = "_platform.diagnostics"`, `EventType = "SmokeTestRun"`, `ScopeId = "_platform"`. The payload records the aggregate `Status` ("Pass" or "Fail") and per-test `(Name, Status, ElapsedMs)` triples — per-test failure messages are surfaced to the HTTP response only and stay out of the durable trail (they go to the deploy log, where the deploy operator already has context).

## Metrics and OpenTelemetry export (Phase 9e)

`ServerConfig.MetricsEndpoint = EnabledMetricsEndpoint` mounts `/metrics` and registers `PrometheusMetricsSink` as the singleton `IMetricsSink` consumers resolve. Default is `NoMetricsEndpoint` — `IMetricsSink` resolves to `NoOpMetricsSink` so emission sites in scheduler / SSE / DataObjectStore stay free at runtime, no middleware is added to the pipeline, no `/metrics` route exists, and SDK standard metrics are not registered.

### Pipeline placement

`MetricsMiddleware` sits BEFORE `RequestTimingMiddleware` in `compose`'s pipeline so the metrics histogram and the slow-request log warning observe the same downstream span. The two middlewares serve different audiences (operator dashboards vs. log lines) and stay independently togglable. The metrics middleware is only inserted when `MetricsEndpoint = EnabledMetricsEndpoint` so disabled-metrics deployments pay zero per-request overhead — not even a no-op call.

Bypass list mirrors `RequestTimingMiddleware`: SSE endpoints (`/api/notifications`, `/api/ai/events`) emit a different metric (`toolup.sse.active_connections` gauge from `SSEConnectionManager`) rather than a request-duration histogram; `/health` and `/ready` are excluded entirely (orchestrator-poll cadence makes them noisy in latency histograms); `/metrics` itself is excluded so scrape requests don't pollute their own metrics.

### Standard metrics

| Metric | Kind | Tags | Source |
|---|---|---|---|
| `toolup.requests.total` | Counter | `method`, `route_class`, `status_class` | `MetricsMiddleware` |
| `toolup.requests.latency_ms` | Histogram (5,10,25,50,100,250,500,1000,2500,5000,10000) | `method`, `route_class`, `status_class` | `MetricsMiddleware` |
| `toolup.errors.total` | Counter | `route_class`, `status_class` | `MetricsMiddleware` (status ≥ 400) |
| `toolup.sse.active_connections` | Gauge | `endpoint` | `SSEConnectionManager` (emission site is a small follow-up) |
| `toolup.jobs.queued` | Gauge | (none) | `JobScheduler` (emission site is a small follow-up) |
| `toolup.jobs.runs.total` | Counter | `handler`, `outcome` | `JobScheduler` (emission site is a small follow-up) |
| `toolup.storage.bytes_read` / `_written` | Counter | `container_class` | `DataObjectStore` (emission site is a small follow-up) |

`route_class` is the request path's two-segment prefix; `status_class` is the HTTP status range (`1xx` / `2xx` / `3xx` / `4xx` / `5xx` / `other`). This keeps tag cardinality bounded structurally — a 5xx response tagged with the literal status `503` would explode cardinality across implementations; `5xx` produces five values period.

### Cardinality cap

Two layers, both per-metric:

1. **Tag-key allowlist** — `MetricDefinition.Tags` enumerates the allowed keys; tags with any other key are silently dropped at the sink. Module authors declare the tags they emit and rogue caller-side tags can never widen the cardinality space. Cheap structural defence.
2. **Distinct-series ceiling** — `MetricsSinkConfig.MaxSeriesPerMetric` (default 1000). New `(tag-set)` combinations beyond the ceiling route to a single overflow series tagged `_overflow="true"`. First overflow logs `Warn` once per metric (with metric name + offending tag) so subsequent overflows don't spam the log.

Per-metric overrides via `MetricsSinkConfig.PerMetricMaxSeries: Map<string,int>` raise the ceiling on a single metric without raising the global default. Useful for known-high-cardinality metrics (per-team usage counters on a 5000-tenant deployment) where the default would falsely trigger.

### Auth surface

`/metrics` is exempt from `AuthEnforcementMiddleware` so vanilla Prometheus / Grafana Cloud scrapers without bearer tokens can read it. Deployments needing authn gate at the network layer (LB allowlist, monitoring-network CIDR). Information disclosed is intentional — route templates, tag values, traffic patterns; deployments that don't want that surface open keep `MetricsEndpoint = NoMetricsEndpoint`.

### Multi-sink fan-out

When `ServerApp.withMetricsSink companionSink` is called alongside `EnabledMetricsEndpoint`, `compose` folds the in-process `PrometheusMetricsSink` plus every registered companion sink into a `FanOutMetricsSink`. A single `Increment` call dispatches to every wrapped sink. Per-sink try/catch swallows exceptions with a `Warn` log so a misbehaving companion (network blip on the OTel exporter, exporter queue full) can't take out the in-process metrics path. The Prometheus sink is always at the head of the list so `/metrics` keeps returning current values even if a companion sink is failing.

### OpenTelemetry companion (`src/Metrics/OpenTelemetry/`)

`OtelMetricsSink` implements `IMetricsSink` over BCL `System.Diagnostics.Metrics.Meter` (the OTel-native primitive on .NET 10). The companion does NOT take an OpenTelemetry SDK NuGet dep — deployments that want OTLP export add the SDK (`OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`) to their server project's `paket.references` and call `MeterProviderBuilder.AddMeter("ToolUp")` in their startup code. The companion's `README.md` carries the four-line deployment recipe.

The split is deliberate: keeping the OTel SDK out of the companion lets deployments that don't need OTLP export pay only the cost of a few BCL `Counter<double>` / `Histogram<double>` instruments. Deployments that DO need OTLP export own the OTel SDK lifecycle (sampling, resource attrs, batch reader cadence, endpoint config) — those are deployment concerns, not SDK concerns.

**Default-on promotion audit (Phase 9y).** A Phase 9y proposal evaluated promoting `services.AddOpenTelemetry().WithMetrics(b => b.AddMeter("ToolUp")).WithTracing(b => b.AddSource("ToolUp"))` from companion-opt-in to default-on in `ToolUp.Platform.Server`. The portability audit concluded the promotion is NOT genuinely zero-cost: (a) `OpenTelemetry.Extensions.Hosting` and its transitive SDK closure would land in every consumer's dependency graph regardless of whether OTel is used (GP 1 violation); (b) attaching `MeterListener` / `ActivityListener` instances at provider build time flips BCL `Counter<double>.Add` and `ActivitySource.StartActivity` from their no-listener fast paths into live emission and `Activity` allocation, even with no exporter wired (GP 13 violation); (c) the `samples/HelloWorld/` cold-start budget shifts under the additional DLL-load cost. The promotion was dropped; the companion-opt-in shape documented above is the load-bearing design. See [`docs/migrations/09y-opentelemetry-default-on.md`](../../../docs/migrations/09y-opentelemetry-default-on.md) for the full finding-of-record (cost dimensions, re-evaluation conditions, consumer impact = none).

The companion's tag-key allowlist enforcement mirrors `PrometheusMetricsSink`. The cardinality cap is NOT enforced in the OTel companion — BCL `Meter` doesn't have a per-instrument series-count primitive, and the OTel SDK's `MeterProviderBuilder.SetMaxMetricStreams` is the natural place for an additional ceiling on the export side. The fan-out chain places `PrometheusMetricsSink` at the head, so emissions past the cap have already been folded into `_overflow="true"` before reaching the OTel companion.

### Phase 9c portability audit (cross-interface, IMetricsSink)

| Rule | Status | Notes |
|---|---|---|
| 1. Identity by value | ✓ | Metric `name : string`, tag keys/values strings. No live framework handle on the surface. |
| 2. Async exemption | ✓ documented | Sync `unit`-returning by deliberate design; mirrors `IAIProvider.SendMessage`'s `onStream` callback exemption. Hot path, write-only, no return to await — `Async<unit>` per emission would compound. Header docstring records the rationale. |
| 3. Retry as data | ✓ | Sinks are write-only fire-and-forget; failing emission swallowed inside impl + Warn log via `FanOutMetricsSink` wrapper. Operators monitor via the export side. |
| 4. Stateless boundary | ✓ | Each call carries `(name, value, tags)` in full. Accumulator state is impl detail. Distributed companions (StatsD push, OTLP relay) work with no in-memory continuity. |
| 5. No cross-shard ordering | ✓ | Metric points commutative; no ordering claimed. |
| 6. Precision documented | ✓ | Histogram bucket boundaries are floats in the unit declared on `MetricDefinition.Unit`. Latency histograms use ms (matches Phase 9 `SlowRequestThreshold`). |

The contract pack is `IMetricsSinkContract` (9 tests, of which 1 is `ptestCase` deferred): counter Increment + render round-trip, gauge SetGauge round-trip (latest write wins), histogram Record bucket attribution, tag allowlist drops unsanctioned tags, cardinality cap → `_overflow="true"` (with Warn-once assertion), per-metric override, concurrent emission (8 threads × 1000 increments), module namespace prefixing. Any future companion (StatsD, push-gateway relay) binds the same pack.

### Deferred sub-tasks

- **`ServerModule.withMetrics` fluent helper.** The `MetricRegistration.Module = Some name` substrate is in place — both sinks auto-namespace `Name = "foo.total"` to `toolup.{moduleName}.foo.total` at registration time. The fluent builder helper is the small follow-up; deferred so this phase shipped the load-bearing surface (sink + middleware + endpoint + companion) without bundling an unused fluent helper.
- **`SSEConnectionManager` / `JobScheduler` / `DataObjectStore` emission sites.** The four metric registrations exist (`toolup.sse.active_connections` gauge, `toolup.jobs.queued` / `_runs.total` counters, `toolup.storage.bytes_read/written` counters); adding `IMetricsSink.Increment` / `SetGauge` calls at those emission sites is a small follow-up. The current `/metrics` output shows these metrics with a value of zero on a fresh deployment until the emission sites are wired.
- **`JsonConsoleLogger` + `LoggerScope`.** Phase 9e.1 follow-up — pairs more naturally with Phase 9h distributed tracing for trace-id correlation than with metrics.

## Distributed tracing — `IActivitySink` (Phase 9l)

Peer to `IMetricsSink`. Lets deployments wire an OpenTelemetry-compatible exporter so the four detached SSE / job / dispatcher / bus surfaces stitch back into one trace per inbound request — "which webhook fired which dispatch which audit event for which user request" is one tree of spans in Honeycomb / Datadog APM / Jaeger / Application Insights instead of four orphaned log lines.

### Interface shape

`IActivitySink.StartActivity(name, parentContext) : Activity option`. The single method returns `Some` when an `ActivitySource` listener is registered (and the listener's sampler returns `AllDataAndRecorded` / `PropagationData`), `None` otherwise. The no-op default registered when no companion is wired returns `None` unconditionally so every instrumented seam elides at zero cost — no allocation, no listener wakeup, no async-local write. Callers dispose via `Option.iter (fun a -> a.Dispose())`.

The `parentContext` argument carries a `System.Diagnostics.ActivityContext` (BCL value type: 32-byte TraceId + 16-byte SpanId + flags + state). `None` lets the BCL pick `Activity.Current` as the implicit parent (the in-process async-local cursor); `Some ctx` explicitly re-parents the child under a context lifted from a wire boundary (HTTP `traceparent` header, `NotificationEnvelope.TraceContext` field).

### Auto-instrumented seams

Five SDK-controlled emission sites are instrumented at the compose level — module code stays unaware of tracing:

| Seam | Span name | Parent strategy |
|---|---|---|
| `ScopeResolutionMiddleware.InvokeAsync` | `HTTP {method} {path}` | Parse incoming `traceparent` header → `ActivityContext.TryParse` → pass as explicit parent. Becomes a root activity when the header is absent or malformed. |
| `JobScheduler.dispatchOne` | `job {handler}` | Implicit `Activity.Current` — `OnEvent` / `Manual` triggers inherit the request thread's ambient parent; `ScheduledByCron` starts a fresh trace. |
| `WebhookDispatcher.runDelivery` | `webhook {eventType}` | Implicit `Activity.Current` — the request that wrote the event still owns the async-local cursor when `Dispatch` enqueues. Span covers the full retry loop. |
| `TransactionalDispatcher.runDelivery` | `notify {kind}` | Explicit parent parsed from `NotificationEnvelope.TraceContext` (stamped by `InMemoryNotificationChannel.Publish` / `DispatchingNotificationChannel.Publish`). Falls back to implicit `Activity.Current` when the envelope was minted outside a request. |
| `InMemoryModuleQueryBus.Ask` | `query {targetModule}.{queryKey}` | Implicit `Activity.Current` — in-process queries ride the caller's async-local context. |

A request that triggers a job which fires a webhook which writes an audit event which publishes a notification produces a single trace spanning all five spans, exportable to any OTel-compliant collector. The acceptance bar for Phase 9l.

### Trace-context propagation

The bridge between the four detached surfaces is `NotificationEnvelope.TraceContext: string option` — a W3C `traceparent` value (`00-<32 hex traceId>-<16 hex spanId>-<2 hex flags>`) captured from the publisher's ambient `Activity.Current.Id` at `Publish` time. Subscribers re-parse it via `ActivityContextHelpers.tryParseTraceparent` (a thin wrapper over `ActivityContext.TryParse`) and pass the result as the explicit `parentContext` of their own `StartActivity` call. The W3C string round-trips losslessly across distributed transports (Redis pub/sub, Service Bus topics, Orleans streams) — when a future `INotificationChannel` companion lifts the SSE bridge onto a wire format, only the JSON converter list needs the field added; the trace linkage is already in the type.

Channels MUST NOT trust the caller-supplied `TraceContext` value for routing, authorisation, or partitioning — it is observability metadata only. A malicious or buggy publisher could stamp an arbitrary string; the subscriber's parser drops it silently on malformed input (the trace then starts a fresh root activity for that span tree, no error).

### How to add a custom span inside module code

Module-side code that wants to record a nested span — a long-running computation, a third-party SDK call worth a separate node — resolves `IActivitySink` from DI exactly like `IMetricsSink`:

```fsharp
open ToolUp.Platform.Tracing

let runWorkflow (ctx: HttpContext) =
    let sink = ctx.RequestServices.GetService(typeof<IActivitySink>) :?> IActivitySink
    let activityOpt = sink.StartActivity("MyModule.heavyComputation", None)
    try
        // ... work ...
    finally
        activityOpt |> Option.iter (fun a -> a.Dispose())
```

The `None` parent inherits the request span's `Activity.Current` automatically, so the custom span lands as a child of `HTTP GET /api/foo` in the OTel viewer with no extra wiring. Module code that needs to attach attributes / events to the span pattern-matches on `Some activity` and calls `activity.SetTag(key, value)` / `activity.AddEvent(...)` — those are BCL members on the returned `Activity`, no SDK indirection.

### OpenTelemetry companion (`src/Metrics/OpenTelemetry/`, Phase 9l addition)

The same `ToolUp.Platform.Metrics.OpenTelemetry` companion that ships `OtelMetricsSink` also exposes `OtelActivitySink`. The companion holds a single `ActivitySource` named `"ToolUp"`; deployments wanting OTLP export add `OpenTelemetry.Exporter.OpenTelemetryProtocol` to their server project's package references and call `TracerProviderBuilder.AddSource("ToolUp")` alongside the existing `MeterProviderBuilder.AddMeter("ToolUp")`. The companion does NOT take an OpenTelemetry SDK NuGet dep — same split as metrics, same rationale (the SDK's sampling / batch / exporter lifecycle is a deployment concern, not an SDK concern). The companion `README.md` carries the deployment recipe.

The Phase 9y default-on audit (documented in the metrics-side OpenTelemetry sub-section above and in [`docs/migrations/09y-opentelemetry-default-on.md`](../../../docs/migrations/09y-opentelemetry-default-on.md)) reached the same conclusion for the tracing surface — and more sharply: attaching an `ActivityListener` via `TracerProviderBuilder.AddSource("ToolUp")` flips every one of the five auto-instrumented seams above (`ScopeResolutionMiddleware`, `JobScheduler.dispatchOne`, `WebhookDispatcher.runDelivery`, `TransactionalDispatcher.runDelivery`, `InMemoryModuleQueryBus.Ask`) from "near-zero `None` round-trip" to "live `Activity` allocation per call" even when no exporter is wired. The opt-in shape preserves the GP 13 "deployments that don't use it pay nothing" promise on the hot path.

### Sampling

Sampling decisions belong to the OpenTelemetry SDK, not the sink. `ActivitySource.StartActivity` returns `null` when either no listener is registered or the listener's sampler declined; the contract pack's `Option.ofObj` collapses both cases into `None`, and the seam's `Option.iter` disposal elides cleanly. Apps that want head sampling configure `TracerProviderBuilder.SetSampler(...)`; tail sampling lives on the collector. The SDK imposes no default — out of the box, no listener is registered and every `StartActivity` returns `None`.

### Phase 9c portability audit (cross-interface, IActivitySink)

| Rule | Status | Notes |
|---|---|---|
| 1. Identity by value | ✓ | `name : string`; `parentContext : ActivityContext option` (BCL value type carrying TraceId / SpanId / flags as strings + byte spans). Returned `Activity` is the standard BCL identity carrier; distributed companions inspect `TraceId.ToString()` / `SpanId.ToString()`, never the wrapper identity. |
| 2. Async exemption | ✓ documented | `StartActivity` is sync `Activity option`-returning by design. Same exemption as `IMetricsSink`: hot path, fire-and-forget, no return to await. Header docstring records the rationale. |
| 3. Retry as data | ✓ | Sinks do not surface retry. Span export is owned by the exporter's batch processor (off-thread, lives in the companion's OTel SDK config). A span that cannot be exported drops silently — operators see degradation through the collector's monitors. |
| 4. Stateless boundary | ✓ | Each call carries `(name, parentContext)` in full. The companion's `ActivitySource` instance is impl detail; a distributed replacement (Orleans-grain-resident tracer) works without in-memory continuity. |
| 5. No cross-shard ordering | ✓ | Spans link by parent reference, not emission order. Sibling spans under one parent may export in any order; collectors stitch the tree by id. |
| 6. Precision documented | ✓ | Span timing is "best-effort wall-clock" via `Activity.StartTime` (BCL `DateTime.UtcNow` underneath; OS-tick precision). Spans short enough to fall under one tick collapse to zero duration. Latency-grade measurements use the metrics histograms instead. |

The contract pack is `IActivitySinkContract` (6 tests): no-op `None` round-trip, listener-attached `Some` + W3C-format id, independent root traces, parent-context inheritance across the wire boundary, 3-deep nested chain forming a connected trace tree (models the job → audit → notification acceptance criterion), `Dispose()` finalises duration. Bound to `OtelActivitySink` in `OtelActivitySinkTests`; any future companion (Application Insights direct, Zipkin native, custom in-house tracer) binds the same pack.


---

> [← Prev: 4. Data & Storage Substrate](04-data-and-storage-substrate.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 6. Background Jobs, Ingestion & Diagnostics →](06-jobs-ingestion-and-diagnostics.md)

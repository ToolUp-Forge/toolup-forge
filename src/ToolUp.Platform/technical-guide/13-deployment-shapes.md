# ToolUp.Platform Technical Guide — 13. Deployment Shapes

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 12. Hosting Models](12-hosting-models.md) · [Index ↑](../TECHNICAL_GUIDE.md) · Next: _(none)_

---

Chapter [12](12-hosting-models.md) is the reference for *where* the SDK runs — Kestrel vs the three host-adapter companions. This chapter is the reference for *how the silos partition* once you have picked Kestrel: which background subsystems live in which process, what persistence the silos must share, and how the cross-silo coordination contract is written.

The lever is `ServerConfig.ProcessProfile` ([`SDK.Shared.fs`](../../ToolUp.Platform.Core/Shared/SDK.Shared.fs)) — four cases, one decision per silo:

| `ProcessProfile` | HTTP pipeline | Background subsystems |
|---|---|---|
| `AllInOne` (default) | mounted | every subsystem ticks |
| `WebOnly` | mounted | none — every `IHostedService` is gated off |
| `WorkerOnly` | not mounted | every subsystem ticks |
| `DispatcherOnly` | mounted | only the outbound dispatchers (transactional + webhook) |

The matrix is centralised in [`ProcessProfileGate.fs`](../../ToolUp.Platform.Server/Server/Compose/ProcessProfileGate.fs); every gate-site in `compose` and the per-concern Compose helpers calls one helper rather than re-deriving the rule. The `/dev/inspect` payload's `ProcessProfile` section reports the resolved matrix decisions for the running silo so operators can confirm "this silo is `WorkerOnly`; the web tier mounts at `/api/*` elsewhere" without reading source — see [Operator visibility](#operator-visibility) below.

> **Scope split with chapter 12.** Chapter 12 covers the serverless-front-door deployment shapes (Azure Functions / AWS Lambda / Google Cloud Functions) and the [hybrid serverless + Kestrel worker silo](12-hosting-models.md#hybrid-serverless-front-door--kestrel-worker-silo). This chapter covers the pure-Kestrel deployment shapes (single-process / web+worker / web+worker+dispatcher) where every silo binds Kestrel and the partition lever is `ProcessProfile` only. The substrate contract and cross-silo coordination rules below apply identically to the hybrid case — read both.

## Three pure-Kestrel deployment shapes

The three shapes below cover the spectrum from "single binary, every concern" through "outbound-delivery isolation". They all run from the **same publish output** — the only difference is the value of `ServerConfig.ProcessProfile` (or the `TOOLUP_PROCESS_PROFILE` env var, when [`ServerConfigOverrides.fromEnv`](../../ToolUp.Platform.Server/Server/ServerConfigOverrides.fs) is wired) per silo.

### Shape 1 — single-process (`AllInOne`)

```
┌────────────────────────────────┐
│  one Kestrel process            │
│  ┌──────────────────────────┐  │
│  │ HTTP pipeline (/api/*)    │  │
│  │ Job scheduler             │  │
│  │ Webhook dispatcher        │  │
│  │ Transactional dispatcher  │  │
│  │ Audit replicator          │  │
│  │ Usage batch flusher       │  │
│  │ Health-state tracker      │  │
│  │ OAuth cleanup + recover   │  │
│  └──────────────────────────┘  │
└────────────────────────────────┘
```

**When it fits.** Today's default. Single-tenant deployments, small multi-tenant deployments, dev / staging environments, any deployment where the operational cost of two silos outweighs the latency gain of separating background work from request handling.

**Composition root.** `ProcessProfile = AllInOne` (which is also the default — omit the field to inherit it).

```fsharp
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        Mode = Team
        // ProcessProfile defaults to AllInOne; shown here for clarity:
        ProcessProfile = AllInOne
}
|> ServerApp.addModule (MyApp.Module.register ())
|> ServerApp.run
```

**Persistence.** Whatever fits the deployment — `InMemoryEventStore` for dev / staging, `PersistentBlobBacked` for any deployment with retention requirements. No cross-process coordination needed because there is only one process.

**Replica count.** `1` is the safe default. Two `AllInOne` replicas double-fire the scheduler / webhook timer / OAuth refresher — every single-leader concern fires on both. Lift to `ReplicaCount > 1` only after the [`IDistributedLock` coordination contract](#idistributedlock-deferred-to-phase-9i) ships.

### Shape 2 — web + worker (`WebOnly` + `WorkerOnly`)

```
┌─────────────────────────────┐    ┌─────────────────────────────┐
│  Web silo (Kestrel)          │    │  Worker silo (Kestrel)       │
│  ProcessProfile = WebOnly    │    │  ProcessProfile = WorkerOnly │
│  ┌───────────────────────┐  │    │  ┌───────────────────────┐  │
│  │ HTTP pipeline /api/*  │  │    │  │ Job scheduler         │  │
│  │ (no background tick)  │  │    │  │ Webhook dispatcher    │  │
│  └───────────────────────┘  │    │  │ Transactional         │  │
│                              │    │  │ Audit replicator      │  │
│  ReplicaCount ≥ 1            │    │  │ Usage flusher         │  │
│  (stateless horiz. scale)    │    │  │ Health-state tracker  │  │
└─────────────────────────────┘    │  │ OAuth refresh/cleanup │  │
              │                     │  └───────────────────────┘  │
              └──── shared ─────────►  ReplicaCount = 1            │
                  persistence       │  (until Phase 9i)            │
                  + Redis channel   └─────────────────────────────┘
```

**When it fits.** The default split when "we have any background work and we want to scale request handling horizontally without doubling the scheduler". Web silos scale freely (stateless under `WebOnly`); the worker silo is pinned to one replica until `IDistributedLock` ships.

**Composition root — web silo.** `ProcessProfile = WebOnly`; cloud-backed persistence; distributed notification channel.

```fsharp
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        Mode = Team
        ProcessProfile = WebOnly
        BlobStorage = AzureBlobStorage(Env.required "TOOLUP_AZURE_BLOB_CONNECTION")
        Notifications = RedisNotifications(Env.required "TOOLUP_REDIS_CONNECTION")
        JobScheduler = InProcessJobScheduler   // substrate registers; tick is gated off
        Webhooks = EnabledWebhooks             // substrate registers; dispatcher is gated off
}
|> ServerApp.addModule (MyApp.Module.register ())
|> ServerApp.run
```

The web silo still registers `IJobScheduler` and `IWebhookDispatcher` so module code can call `IJobScheduler.Schedule` and `IWebhookEvents.publish` — the gating only short-circuits the `IHostedService` tick, not the substrate. Jobs and webhook events land in the persistent `IJobStore` / event store and the worker silo's tick drains them.

**Composition root — worker silo.** `ProcessProfile = WorkerOnly`; same persistence + same Redis as the web silo.

```fsharp
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        Mode = Team
        ProcessProfile = WorkerOnly
        BlobStorage = AzureBlobStorage(Env.required "TOOLUP_AZURE_BLOB_CONNECTION")
        Notifications = RedisNotifications(Env.required "TOOLUP_REDIS_CONNECTION")
        JobScheduler = InProcessJobScheduler
        Webhooks = EnabledWebhooks
}
|> ServerApp.addModule (MyApp.Module.register ())
|> ServerApp.run
```

The worker silo's HTTP pipeline is not mounted (`ProcessProfileGate.shouldRegisterHttpPipeline = false`); Kestrel binds to its configured port but no Giraffe router responds. Sibling load balancers should not route HTTP at the worker silo until the deferred [`Host.CreateApplicationBuilder()` refactor](#follow-ups-deferred) lands.

**Replica count.**

| Silo | Replica count | Why |
|---|---|---|
| Web (`WebOnly`) | `≥ 1` — scale freely | Every `BackgroundService` is gated off; nothing to double-fire |
| Worker (`WorkerOnly`) | `= 1` until Phase 9i | Cron tick + webhook retry timer + OAuth refresher double-fire across replicas without [`IDistributedLock`](#idistributedlock-deferred-to-phase-9i) |

### Shape 3 — web + worker + dispatcher (`WebOnly` + `WorkerOnly` + `DispatcherOnly`)

```
┌──────────────┐  ┌──────────────┐  ┌──────────────────────────┐
│  Web silo     │  │  Worker silo │  │  Dispatcher silo          │
│  WebOnly      │  │  WorkerOnly  │  │  DispatcherOnly           │
│              │  │              │  │  ┌─────────────────────┐  │
│  /api/*      │  │  Scheduler   │  │  │ Transactional       │  │
│  (no tick)   │  │  Audit       │  │  │ Webhook dispatcher  │  │
│              │  │  Usage       │  │  └─────────────────────┘  │
│              │  │  Health      │  │  /api/* mounted          │  │
│              │  │  OAuth       │  │  (admin routes only —     │  │
│              │  │              │  │   no scheduler routes)    │  │
└──────────────┘  └──────────────┘  └──────────────────────────┘
        │                │                       │
        └────── shared persistence + Redis ──────┘
```

**When it fits.** Outbound-delivery isolation — a deployment where transactional email / SMS / push fan-out or webhook delivery has independent scaling, deployment, or blast-radius requirements from the rest of the background work. Common in marketplaces where outbound notification volume is large and bursty relative to scheduled-job throughput, and where shipping a separate notification team's silo without disturbing the scheduler is operationally cheap.

**Composition root — dispatcher silo.** `ProcessProfile = DispatcherOnly`; same persistence + same Redis.

```fsharp
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        Mode = Team
        ProcessProfile = DispatcherOnly
        BlobStorage = AzureBlobStorage(Env.required "TOOLUP_AZURE_BLOB_CONNECTION")
        Notifications = RedisNotifications(Env.required "TOOLUP_REDIS_CONNECTION")
        Webhooks = EnabledWebhooks
}
|> ServerApp.addModule (MyApp.Module.register ())
|> ServerApp.run
```

The dispatcher silo mounts the HTTP pipeline so its admin endpoints (`/api/_platform/health`, `/dev/inspect`, etc.) are reachable; the worker silo's scheduler routes still register because the substrate is the same. `ProcessProfileGate.shouldRegisterBackgroundService` returns `true` only for `TransactionalDispatcherSubsystem` and `WebhookDispatcherSubsystem` under `DispatcherOnly`; every other background subsystem is gated off.

**Replica count.** `= 1` on the dispatcher silo until Phase 9i ships — webhook retry uses an in-process timer that double-fires across replicas. The web silo is freely scalable (as in shape 2).

## Substrate contract — what every shape must share

The two- and three-silo shapes only work because every silo reaches the same persistence. The contract is identical to the [hybrid serverless + Kestrel worker silo substrate contract](12-hosting-models.md#hybrid-serverless-front-door--kestrel-worker-silo) in chapter 12 — that table applies verbatim. The summary view, with annotations for the pure-Kestrel shape:

| Substrate (`ServerConfig` field) | Required for | Coordination note |
|---|---|---|
| `BlobStorage` | Web + Worker (+ Dispatcher) | Cloud-backed (`ToolUp.Storage.Azure` / `.AwsS3` / `.GoogleCloud`); identical connection string env var across silos |
| `EventStore` | Web + Worker | `PersistentBlobBacked Retention.defaults` — the worker drains events the web silo writes; `InMemoryEventStore` per-silo defeats the partition |
| `RateLimitStore` | Web + (any silo serving `/api/*`) | Redis-backed when the web silo runs > 1 replica; per-instance `InMemoryRateLimitStore` lets a request burst evade the rate envelope by hitting a different replica |
| `Notifications` | Web + Worker (+ Dispatcher) | `RedisNotifications` — the in-memory channel is per-process; the worker's job-completion / ingestion events never reach the web silo's SSE subscribers without Redis pub/sub |
| `TeamStore` / `PermissionStore` | Web + Worker (+ Dispatcher) | Blob-backed (shared) — auth resolution reaches one source of truth; an in-memory team store on either silo cannot serve the other |
| `ConfigStore` | Web + Worker (+ Dispatcher) | Blob-backed — operator-set rate limits, kill switches; the `ConfigStoreInvalidator` `BackgroundService` only ticks on the worker, so the web silo re-reads on cache miss |
| `SecretStore` | Web + Worker (+ Dispatcher) | Provider-native (`AzureKeyVault` / `AwsSecretsManager` / `GcpSecretManager`); same secret scope; each silo's managed identity granted read |
| `JobScheduler` | Web + Worker | `InProcessJobScheduler` on both — the substrate registers in both silos (so module code can `Schedule`), only the tick is gated. Web silo schedules; worker silo drains |
| `Webhooks` | Web + Worker + Dispatcher | `EnabledWebhooks` on every silo that needs to publish via `IWebhookEvents.publish`; dispatcher silo (or worker, in shape 2) drains |
| `AuditLog` / `UsageMetering` / `HealthStateTracking` | Web + Worker | Write substrate registers on all silos; only the worker's replicator / flusher / tracker `BackgroundService` ticks. Events from every silo land in the shared event store |
| `OAuth` data-source connectors | Worker (refresh) + Web (initial auth) | Token refresher + state-cleanup `BackgroundService`s run only on the worker; the web silo handles the user-facing authorise / callback flow |

**Smoke check.** Both silos boot with identical `BlobStorage` / `Notifications` / `SecretStore` env vars. A silo that boots with a different connection string than its sibling produces split-brain — events land in two stores; jobs scheduled on one are never seen by the other. Verify by booting each silo and `curl`'ing `/dev/inspect` on the web silo (or each silo's local equivalent): the `Caller` section's `StorageScope.ScopeId` for a known team must round-trip identically across silos.

## Cross-silo coordination contract

The four background subsystems with single-leader semantics:

| Subsystem | What single-leader means | Coordination mechanism (today) |
|---|---|---|
| Job scheduler | Cron-due-job tick that dispatches a job at most once per due-time | `InProcessJobScheduler` uses an in-process timer; **not safe** across multiple `WorkerOnly` replicas without `IDistributedLock` |
| Webhook dispatcher | Retry timer that re-attempts failed deliveries on backoff | In-process timer; **not safe** across multiple replicas |
| OAuth refresher | Token-refresh polling that renews expiring tokens before they expire | In-process timer; **not safe** across multiple replicas |
| Audit replicator + usage flusher | Drains the event store / usage log into external sinks | Idempotent on the sink side (vendor dedup keys per `ToolUp.AuditSinks.*` README); safe across replicas if the sink contract is honoured |

The first three are why `ReplicaCount = 1` on the worker and dispatcher silos is non-negotiable until Phase 9i ships. The last one is the silver lining — the largest by data volume is already coordinated, just by a different mechanism (sink-side dedup, not in-process locking).

### `IDistributedLock` — deferred to Phase 9i

Phase 9i (`IDistributedLock` primitive) ships the cross-silo single-leader coordination layer. The intended shape:

```fsharp
type IDistributedLock =
    abstract TryAcquire :
        leaseId:string * ttl:TimeSpan -> Async<DistributedLeaseToken option>
    abstract Renew : token:DistributedLeaseToken -> Async<bool>
    abstract Release : token:DistributedLeaseToken -> Async<unit>
```

Once shipped, each single-leader subsystem (scheduler tick, webhook retry timer, OAuth refresher) acquires a lease before each tick; replicas that fail to acquire skip the tick and try again on the next interval. Lift the `ReplicaCount = 1` pin on the worker and dispatcher silos at that point.

**Until then:** `ReplicaCount = 1` on `WorkerOnly` and `DispatcherOnly` silos. Treat that as part of the deployment manifest, not as runtime config — the silo cannot detect the violation at compose time and a misconfigured `replicas = 2` will double-fire silently.

> The single-leader leasing layer is a tracked follow-up; the [migration doc](../../../docs/migrations/16a-process-profile-gating.md) carries the substrate detail today.

## Operator visibility

The `/dev/inspect` JSON and HTML reports (chapter [6](06-jobs-ingestion-and-diagnostics.md), Phase 9a) carry a `ProcessProfile` panel surfacing the matrix decisions for the running silo:

```jsonc
{
  "ProcessProfile": {
    "Profile": "WorkerOnly",
    "ServerlessHost": "KestrelHost",
    "HttpPipelineMounted": false,
    "Subsystems": [
      { "Name": "Job scheduler",            "Registered": true,
        "Reason": "WorkerOnly runs every background subsystem" },
      { "Name": "Webhook dispatcher",       "Registered": true,
        "Reason": "WorkerOnly runs every background subsystem" },
      { "Name": "Transactional dispatcher", "Registered": true,
        "Reason": "WorkerOnly runs every background subsystem" },
      { "Name": "Audit replicator",         "Registered": true,
        "Reason": "WorkerOnly runs every background subsystem" }
      // ...
    ]
  }
}
```

The HTML view renders the same data as a table. The `Reason` column makes the gating matrix readable in-place — an operator inspecting a `WorkerOnly` silo confirms "the scheduler ticks here, the web tier mounts `/api/*` elsewhere" without reading [`ProcessProfileGate.fs`](../../ToolUp.Platform.Server/Server/Compose/ProcessProfileGate.fs). The same panel under `ServerlessHost = ServerlessHost` reports every `Registered = false` with reason `ServerlessHost short-circuits every background subsystem` — useful when diagnosing "why is my scheduled job not firing in this Azure Functions deployment?".

`/dev/inspect` is dev-gated via `ServerConfig.EnableDevEndpoints = true`. Production operators with Owner/Admin role read the same `ServerConfig` shape through the Platform Admin's deployment-shape panel (Phase 61 follow-up — surface and the same data path landed alongside Phase 16a's tail).

## Follow-ups (deferred)

- **`IDistributedLock` (Phase 9i)** — cross-silo single-leader coordination. Until it lands, `ReplicaCount = 1` on `WorkerOnly` and `DispatcherOnly` silos.
- **`WorkerOnly` → `Host.CreateApplicationBuilder()`** — the silo binds no port at all. The [`IServerHost.createWorkerHost`](../../ToolUp.Platform.Server/Server/IServerHost.fs) helper supports it; the compose-side construction-branch refactor is the follow-up commit on the Phase 16a body. Until it lands, sibling load balancers should not route HTTP at a `WorkerOnly` deployment (Kestrel binds the port but no Giraffe router responds).
- **Per-silo Platform Admin surface (Phase 61)** — the production-safe equivalent of `/dev/inspect`'s `ProcessProfile` panel, accessible to Owner/Admin without enabling `EnableDevEndpoints`.

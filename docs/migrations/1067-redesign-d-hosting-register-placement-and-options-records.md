# Phase 1067 — every unit of background work declares where it runs; the scheduler and the webhook dispatcher take options records

**What changes for you: a hosted service is registered through `Hosting.register` with a declared `Placement`,
the in-process scheduler and the webhook dispatcher are each built by one factory over an options record, and
three companion units run on fewer profiles than before.** This is a breaking 0.x minor. The codemod
(`dotnet run --project Build.fsproj -- Codemod <your source dir>`, rules `1067-*`) rewrites `ProcessProfileGate.atStart` and reports
every other site below with the change to make. Nothing is `[<Obsolete>]`: the retired members are gone.

## 1. One placement rule for every hosted service

`ProcessProfileGate.shouldRegisterBackgroundService`, its `BackgroundSubsystem` union and the exemption shape
(`HostedServiceExemption`, `ProcessProfileGate.addExempt` / `exempt` / `addDeferredJobs` /
`SchedulerRegistrationReason`) are removed. A unit of background work is a value, and one function registers it:

```fsharp skip=fragment
Hosting.register services config (BackgroundUnit.create "my-sweep" SharedWork (fun sp -> MySweep(sp) :> IHostedService))
```

| `Placement` | Choose it for | Runs on |
|---|---|---|
| `SharedWork` | a loop draining shared durable state, which any one silo may do for all | `AllInOne`, `WorkerOnly` (and `DispatcherOnly` when tagged `BackgroundUnit.outbound`) |
| `ProcessLocal` | a loop over this process's own memory, files or HTTP requests | every Kestrel profile; never a serverless host |
| `OnceAtStart` | a one-shot at start, a scheduler registration, a stop hook — anything that runs no loop | every profile, a serverless host included |

`Hosting.register` always records the unit (`BackgroundUnitRecord`) and registers its hosted service only when the
profile admits the placement. `BackgroundUnit.ofInstance` takes an instance you constructed (the container does not
dispose it); `BackgroundUnit.create` takes a factory. A companion that holds a `ServerApp` may contribute a unit as a
value — `ServerApp.addContribution (Contribution.hosted owner unit)` — and it is admitted against the deployment's
final configuration.

**Upgrading each kind of site:**

- **A site gated by `shouldRegisterBackgroundService config X`** — drop the `if` and register the service as
  `SharedWork` (every subsystem the matrix named was shared work; the webhook and transactional dispatchers add
  `|> BackgroundUnit.outbound`). It registers on exactly the profiles it did.
- **An exempt site** (`addExempt label reason factory`) — `Hosting.register services config (BackgroundUnit.create
  "<name>" OnceAtStart factory)` when the reason was the scheduler-registration reason or a one-shot;
  `ProcessLocal` when the reason was "bound to this process".
- **`addDeferredJobs label declarations`** — `Hosting.register services config (Hosting.deferredJobs label declarations)`.
- **`ProcessProfileGate.atStart`** — `Hosting.atStart` (the codemod rewrites it).
- **An ungated `AddSingleton<IHostedService>` / `AddHostedService`** — choose a placement: `OnceAtStart` for a one-shot,
  `SharedWork` for a store-draining loop, `ProcessLocal` for a loop bound to the process.
- **`DeclaredDelivery.withDeclaredDataSources declared watchPoll services`** takes the deployment's `ServerConfig`
  first: `withDeclaredDataSources config declared watchPoll services`.

`ProcessProfileGate.shouldRegisterHttpPipeline` is unchanged: whether a profile mounts HTTP is a different question.

## 2. The placement change, per companion

Every platform unit registers on the profiles it did before. Three companion units move — the operator's decision
for this release — and IndexNow does not:

| Companion unit | Before | After | Why |
|---|---|---|---|
| RAG ingestion (`hosted:rag-ingestion`) | every profile | over a shared ingestion queue store (`RAGServerApp.withDurableIngestionQueue`): `SharedWork` — `AllInOne` / `WorkerOnly` only. Over the default in-process queue: `ProcessLocal` — every Kestrel profile, no serverless host | a shared queue lets a worker silo drain for the web silo; the in-process queue can be drained only by the process that accepted the upload |
| RAG re-embedding (`hosted:rag-reembedding`) | every profile | `SharedWork` — `AllInOne` / `WorkerOnly` only | it scans the shared vector store; N replicas used to re-embed the corpus N times |
| AI chat worker (`hosted:ai-chat-worker`) | every profile | `ProcessLocal` — every Kestrel profile, no serverless host | a serverless host keeps no loop alive; the handler already falls back to the unsupervised run when the worker is not running |
| IndexNow startup submission | every profile, once per replica start | unchanged (`OnceAtStart`) | it was already a one-shot, never a loop |
| RAG retrieval-store flush, recovery sweep, tombstone vacuum; every retention sweep, calendar job, report-rendering handler, fact handler registration, declared data sources | every profile | unchanged (`OnceAtStart`) | they run no loop |

**If you relied on RAG re-embedding running on a `WebOnly` silo** (or, over a shared queue, ingestion), run a
`WorkerOnly` (or `AllInOne`) silo beside it. A `WebOnly`-only fleet over the in-process ingestion queue keeps ingesting
as before. `/dev/inspect`'s `ProcessProfile` panel lists every unit with its placement and whether the silo admits it
(its `Subsystems` rows became `Units`: `Unit`, `Placement`, `Outbound`, `Admitted`, `Reason`).

## 3. The scheduler: one factory over `SchedulerOptions`

`JobScheduler.createWithCatchUp`, `createWithExternalCompute` and `createWithExternalCallback` are removed, and
`JobScheduler.create` and the `InProcessJobScheduler` constructor take a deps record and an options record:

```fsharp skip=fragment
JobScheduler.create
    { Store = jobStore; EventStore = eventStore; NotificationChannel = channel
      Config = config; Logger = logger; ActivitySink = sink }
    { JobScheduler.SchedulerOptions.defaults with
        TriggerWatermark = Some watermark        // was createWithCatchUp
        ExternalDispatcher = Some dispatcher     // was createWithExternalCompute
        ExternalHandleStore = Some handleStore   // was createWithExternalCallback
        DistributedLock = Some lock
        ProgressSink = JobScheduler.progressSinkFor deps
        ScopeCarrier = Some carrier }
```

Set only the fields the old factory took. Two behaviours moved out of the constructor:

- **The progress sink is no longer derived from `JobProgress` inside the constructor.** Pass
  `ProgressSink = JobScheduler.progressSinkFor deps` (it answers `None` under `NoJobProgress`). `compose` does this.
- **The deployment's scope carrier is bound at construction** (`ScopeCarrier`). `IScopeCarrierBinding` remains — it
  is how an adopted companion scheduler is bound and what the carrier conformance packs rebind through — but
  `compose` no longer binds the in-process scheduler after the fact.

## 4. The webhook dispatcher: one factory over `WebhookOptions`

The explicit secondary constructor, the old curried `create` and `createWithLock` are removed:

```fsharp skip=fragment
WebhookDispatcher.create
    { Registry = registry; DeliveryLog = deliveryLog; EventStore = eventStore; HttpClient = httpClient
      RetryPolicy = WebhookRetryPolicy.defaults; Logger = logger; ActivitySink = sink; SecretStore = secrets
      GetRateLimiter = rateLimiter; UrlPolicy = urlPolicy }
    { WebhookDispatcher.WebhookOptions.defaults with DistributedLock = Some lock }   // was createWithLock
```

## 5. The deployment's lock, composed first — the scheduler's replica pin lifts

`ServerApp.withDistributedLock lock` composes the deployment's `IDistributedLock`. `compose` resolves it before it
builds the job scheduler, the webhook dispatcher and the platform-admin store, and registers it as the
`IDistributedLock` DI hands out (the in-process lock when you compose none). With a shared lock (for example
`RedisDistributedLock`) the scheduler's cron tick is an election across replicas, and `JobSchedulerInstanceValidator`
admits `ReplicaCount > 1` for the in-process scheduler. The per-replica webhook queue is unchanged, so a second
dispatcher replica still adds no outbound throughput. A lock you registered from `ServiceConfig` still wins for DI
resolution but never reached the scheduler; move it to `withDistributedLock`.

`registerJobScheduler`, `buildWebhookSubsystem` and `ComposeAuth.registerPlatformAdminStore` take the lock as a
final parameter, and `compose` takes `distributedLockOverride` last.

## 6. Tenant-lifecycle hooks are values

The nine first-party hooks are `TenantHook<'dep>` values (`EncryptionKeyLifecycle.hook`, …) turned into
`ITenantLifecycle` by `TenantHook.asLifecycle`; each module's `create services` is unchanged, and the hook classes
(`EncryptionKeyLifecycle.EncryptionKeyLifecycle`, …) are removed. A hook whose substrate is not composed answers
`Skipped "<what it requires> is not composed"`. `ScopeId.parse` reads a lifecycle scope id into
`ScopeKind.Team | User | Other` through `ScopeKey`'s grammar. A consumer's own `ITenantLifecycle` is unaffected; it may
be expressed as a `TenantHook` instead.

## Verification

- `dotnet run --project Build.fsproj -- Codemod <your source dir>` reports no `1067-*` finding.
- `/dev/inspect` on each silo lists the units you expect as `Admitted`.
- A `WorkerOnly` fleet scaled past one replica boots only with `ServerApp.withDistributedLock` (or the explicit
  `AcceptInProcessSchedulerInMultiInstance`).

## Rollback

Pin the previous SDK version; the change carries no stored-data migration.

# ToolUp.Platform Technical Guide — 13. Deployment Shapes

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 12. Hosting Models](12-hosting-models.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 14. Docker Hosting →](14-docker-hosting.md)

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

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        Surfaces = Surfaces.team
        // ProcessProfile defaults to AllInOne; shown here for clarity:
        ProcessProfile = AllInOne
}
|> ServerApp.addModule (MyApp.Module.register ())
|> ServerApp.run
```

**Persistence.** Whatever fits the deployment — `InMemoryEventStore` for dev / staging, `PersistentBlobBacked` for any deployment with retention requirements. No cross-process coordination needed because there is only one process.

**Replica count.** `1` is the safe default. Two `AllInOne` replicas both run the scheduler's cron tick, so every due job is noticed twice; and each holds its own in-process webhook queue, so an event is dispatched only by whichever replica wrote it. Lift to `ReplicaCount > 1` only once the scheduler leases its tick and webhook events reach a durable queue — Phase 9i shipped the [`IDistributedLock` primitive](#idistributedlock--the-lease-primitive-phase-9i) and Phase 16a leased the webhook failure-state transition, but tick election is deliberately still out, so the pin stands.

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
                  persistence       │  (ticks not leased yet)      │
                  + Redis channel   └─────────────────────────────┘
```

**When it fits.** The default split when "we have any background work and we want to scale request handling horizontally without doubling the scheduler". Web silos scale freely (stateless under `WebOnly`); the worker silo is pinned to one replica until its ticks are leased (Phase 9i shipped the `IDistributedLock` primitive, not tick election).

**Composition root — web silo.** `ProcessProfile = WebOnly`; cloud-backed persistence; distributed notification channel.

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        Surfaces = Surfaces.team
        ProcessProfile = WebOnly
        JobScheduler = InProcessJobScheduler   // substrate registers; tick is gated off
        Webhooks = EnabledWebhooks             // substrate registers; dispatcher is gated off
}
// Cloud persistence and the distributed notification channel are wired as
// SUBSTRATE, not as ServerConfig fields.
|> ServerApp.withStorage (AzureBlobStorage.create azureConfig)
|> ServerApp.withNotifications redisChannel
|> ServerApp.addModule (MyApp.Module.register ())
|> ServerApp.run
```

The web silo still registers `IJobScheduler` and `IWebhookDispatcher` so module code can call `IJobScheduler.Schedule` and `IWebhookEvents.publish` — the gating only short-circuits the `IHostedService` tick, not the substrate. Jobs and webhook events land in the persistent `IJobStore` / event store and the worker silo's tick drains them.

**Composition root — worker silo.** `ProcessProfile = WorkerOnly`; same persistence + same Redis as the web silo.

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        Surfaces = Surfaces.team
        ProcessProfile = WorkerOnly
        JobScheduler = InProcessJobScheduler
        Webhooks = EnabledWebhooks
}
// Cloud persistence and the distributed notification channel are wired as
// SUBSTRATE, not as ServerConfig fields.
|> ServerApp.withStorage (AzureBlobStorage.create azureConfig)
|> ServerApp.withNotifications redisChannel
|> ServerApp.addModule (MyApp.Module.register ())
|> ServerApp.run
```

The worker silo's HTTP pipeline is not mounted (`ProcessProfileGate.shouldRegisterHttpPipeline = false`); Kestrel binds to its configured port but no Giraffe router responds. Sibling load balancers should not route HTTP at the worker silo until the deferred [`Host.CreateApplicationBuilder()` refactor](#follow-ups-deferred) lands.

**Replica count.**

| Silo | Replica count | Why |
|---|---|---|
| Web (`WebOnly`) | `≥ 1` — scale freely | Every `BackgroundService` is gated off; nothing to double-fire |
| Worker (`WorkerOnly`) | `= 1` until the cron tick is leased | Every replica notices the same due job (the OAuth refresher rides that same tick as a scheduled job); and each holds its own webhook queue, so events are owned per-replica. Phase 9i shipped the [`IDistributedLock` primitive](#idistributedlock--the-lease-primitive-phase-9i) and Phase 16a leased the webhook failure-state transition, but there is still no tick election and no durable queue |

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

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        Surfaces = Surfaces.team
        ProcessProfile = DispatcherOnly
        Webhooks = EnabledWebhooks
}
// Cloud persistence and the distributed notification channel are wired as
// SUBSTRATE, not as ServerConfig fields.
|> ServerApp.withStorage (AzureBlobStorage.create azureConfig)
|> ServerApp.withNotifications redisChannel
|> ServerApp.addModule (MyApp.Module.register ())
|> ServerApp.run
```

The dispatcher silo mounts the HTTP pipeline so its admin endpoints (`/api/_platform/health`, `/dev/inspect`, etc.) are reachable; the worker silo's scheduler routes still register because the substrate is the same. `ProcessProfileGate.shouldRegisterBackgroundService` returns `true` only for `TransactionalDispatcherSubsystem` and `WebhookDispatcherSubsystem` under `DispatcherOnly`; every other background subsystem is gated off.

**Replica count.** `= 1` on the dispatcher silo. Not because retries double-fire — Phase 16a leased the failure-state transition, so two dispatcher replicas now count failures and auto-disable correctly against one subscription (see [the lease primitive](#idistributedlock--the-lease-primitive-phase-9i)) — but because webhook and transactional event OWNERSHIP is still a per-replica in-process queue. A second dispatcher replica does not share the load; it sits idle for every event the first one dequeued, and receives nothing at all for events written on another silo. The web silo is freely scalable (as in shape 2).

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
| Webhook dispatcher | Two separate concerns — see below. The failure-state transition (`ConsecutiveFailures` + auto-disable) must apply once per dead-letter; event OWNERSHIP must fall to one replica | Failure-state transition is leased on `IDistributedLock` (Phase 16a). Event ownership is **not** coordinated — it needs a durable queue, not a lock |
| OAuth refresher | Token refresh that renews expiring tokens before they expire | Runs as a scheduled JOB (`OAuthRefreshJobHandler` on `IJobScheduler`), so it inherits the scheduler's row above — it owns no timer of its own |
| Audit replicator + usage flusher | Drains the event store / usage log into external sinks | Idempotent on the sink side (vendor dedup keys per `ToolUp.AuditSinks.*` README); safe across replicas if the sink contract is honoured |

**The webhook row was wrong until Phase 16a, in a way worth recording**, because "webhook retry timer" appears in this chapter's earlier revisions, in the Phase 16a task list, and in more than one plan drawn off them. There is no webhook retry timer. `WebhookDispatcherService` has no periodic sweep and no redelivery pass: it drains a bounded in-process `Channel` fed by the post-write hook on `IEventStore`, and the "retry" is an `Async.Sleep` inside the ladder for one event that only ever exists on the one replica that dequeued it. So two replicas do not double-fire a retry — there is no shared clock for them to double-fire off.

What IS shared is the subscription record the ladder writes at its terminal step, and that is what Phase 16a leases. The failure mode was not a duplicate delivery; it was a **lost update**: two ladders ending concurrently — two events to one failing subscription, or that subscription reached from a worker silo and a dispatcher silo — each read `ConsecutiveFailures = n` and each wrote `n + 1`. The counter stalls, so a persistently-failing receiver is never auto-disabled; and when both do cross the threshold, `WebhookSubscriptionAutoDisabled` is emitted twice for one transition. Same class as `BlobBackedPlatformAdminStore`'s write lock, and the same remedy: `toolup:webhook-failure-state:{scopeId}:{subscriptionId}`, taken with `DistributedLock.withLease`, with the counter re-read inside the lease so the increment is against current state rather than the dispatch-time snapshot. The healthy path acquires nothing (GP 13) — the reset is guarded on the snapshot before the lease, so a subscription already at zero pays no acquire, no re-read and no write.

The scheduler row is therefore the one remaining unleased tick, and the audit replicator is the silver lining — the largest by data volume is already coordinated, just by a different mechanism (sink-side dedup, not locking).

### `IDistributedLock` — the lease primitive (Phase 9i)

Phase 9i ships the **primitive**, not the adoption. Read the scope boundary before planning a replica count off this section:

- ✅ **Shipped** — `IDistributedLock` + `Lease`, the always-registered `InProcessDistributedLock` default, a `RedisDistributedLock` companion, an `IDistributedLockContract` conformance pack, and three migrated consumers: the job scheduler's per-`JobId` dispatch mutex, `BlobBackedPlatformAdminStore`'s write serialisation (both of which previously held a process-local `SemaphoreSlim` that stopped excluding anything the moment a second replica appeared), and — Phase 16a — the webhook dispatcher's failure-state read-modify-write, which held no `SemaphoreSlim` at all and was simply last-write-wins.
- ⛔ **Not shipped** — **tick election.** The scheduler's cron tick still runs on an in-process timer with no lease around the tick itself, and the webhook dispatcher's event ownership still rests on a per-replica in-process queue. **`ReplicaCount = 1` on `WorkerOnly` and `DispatcherOnly` silos therefore still stands.** Treat it as part of the deployment manifest, not as runtime config — the silo cannot detect the violation at compose time and a misconfigured `replicas = 2` still mis-fires silently.

**Read the two ⛔ items as different problems with different remedies**, because conflating them is what produced the phantom "webhook retry timer" above. Tick election is a LOCK problem: two replicas legitimately read the same due job from shared state and only one may act. Event ownership is a QUEUE problem: a webhook event enqueued in one replica's memory is invisible to every other, so no lease can hand it over — and, more sharply, an event written by a `WebOnly` silo is dispatched by nobody at all, because the web silo's dispatcher is gated off and the worker's queue never saw it. A durable, partition-keyed queue fixes that; a lease cannot.

The distinction is worth being pedantic about: the scheduler now leases *the dispatch of a given job*, which means two replicas cannot interleave two runs of the same job. It does not lease *the decision that a job is due*, so two replicas would each still notice the same due job and one would simply lose the dispatch lease — the work happens once, but only because the loser's lease acquire fails, and only after both replicas have already read the store. That is a de-duplication side-effect, not leader election, and it is not what the `ReplicaCount` pin is asking about.

#### The shipped shape

```fsharp
type Lease = {
    LockId: string
    FenceToken: int64
    AcquiredAt: DateTime
    ExpiresAt: DateTime
}

type IDistributedLock =
    abstract TryAcquire : lockId: string * ttl: TimeSpan -> Async<Lease option>
    abstract Renew      : lease: Lease -> Async<Lease>
    abstract Release    : lease: Lease -> Async<unit>
```

Registered unconditionally by `compose`, so any subsystem or module resolves an `IDistributedLock` from DI without first checking whether the deployment composed one. The default is `InProcessDistributedLock` — a `ConcurrentDictionary<string, Lease>` that is *correct* for a single instance and excludes nothing across replicas. A distributed deployment overrides it from `ComposeExtensions.ServiceConfig`:

```fsharp skip=fragment
let lck =
    DistributedLockSelection.fromEnv logger [ RedisDistributedLock.resolver ]

{ ComposeExtensions.empty with
    ServiceConfig = Some(fun s -> s.AddSingleton<IDistributedLock>(lck)) }
```

`DistributedLockSelection.fromEnv` reads **`TOOLUP_DISTRIBUTED_LOCK`** (`inprocess` — the default — or a resolver name such as `redis`, whose connection string comes from `TOOLUP_REDIS_CONNECTION`). Same shape as `NotificationChannel.fromEnv`, deliberately: a deployment wiring both distributed substrates writes the same two lines twice rather than learning two conventions. An unrecognised value or a missing connection string **warns and falls back to in-process** rather than failing startup, because the in-process lock is a correct answer for one instance; the multi-instance case is caught separately at preflight by `MultiInstanceAdminCoherenceValidator`, which is the right place for a fail-closed gate.

#### Lock semantics

**Acquire is fail-fast.** `TryAcquire` returns `None` immediately when another holder has the id — it never queues. That is the deliberate default: a caller that cannot proceed usually wants to *skip* (this tick already ran elsewhere; the admin write is already in flight) rather than have an unbounded wait imposed on it, and a poll loop on a store-backed lock costs one round-trip per interval. `DistributedLock.acquireBlocking` is available for migrating a `SemaphoreSlim.WaitAsync` call site whose semantics genuinely are "queue", and both migrated consumers use it precisely so their in-process behaviour is unchanged — but new code should prefer `TryAcquire` with an explicit `None` branch.

**The TTL is data, and it is a promise the store keeps, not the holder.** A lease lapses at `ExpiresAt` whether or not the holder finished. That is what stops a crashed holder deadlocking an id forever, and it is also the hazard: a TTL shorter than the worst-case critical section admits a second holder mid-work. Budget generously, or `Renew` on a heartbeat. The job scheduler takes the generous route with a one-hour dispatch lease, because its critical section spans a whole retry loop including the loop's backoff sleeps.

**`Release` and `Renew` are holder-checked and never throw on loss.** Both compare the caller's `FenceToken` against the current holder's, so a lease that lapsed and was re-taken by someone else is never released out from under its new holder and never renewed back into existence. `Release` is idempotent — a `finally` can call it unconditionally. `Renew` signals failure by **returning the lease unchanged** rather than raising, so the caller's own `Lease.isLive` check is the arbiter:

```fsharp skip=fragment
let! renewed = lck.Renew lease
if Lease.isLive renewed then keepWorking renewed else abandon ()
```

**Distinct ids never contend**, and no ordering is promised between two different lock ids (GP 12 rule 5). Namespace your ids — the shipped consumers use `toolup:job-dispatch:{jobId}` and `toolup:platform-admin:write` — so two subsystems sharing one Redis cannot collide.

#### What "held" means while you process under a lease

This is the part that bites, so state it plainly: **holding a lease is not a guarantee that you still hold it.** Between the `TryAcquire` that returned `Some` and any given line of your critical section, the lease may have lapsed — a long GC pause, a paused VM, a slow store round-trip, a machine that lost the network and came back. The store has by then handed the id to someone else, and *your* process has no way to have noticed. Every distributed lock has this property; an implementation claiming otherwise is claiming a synchronous global clock.

So "held" means exactly one thing: **at the instant of the acquire, no other holder had the id.** Everything you build on top of that must be one of:

1. **Short enough that lapsing is implausible**, with a TTL that dwarfs the section. Good for a blob read-modify-write (the Platform-Admin store's minute-long lease over a few hundred milliseconds of work).
2. **Renewed on a heartbeat**, with the work abandoned the moment a `Renew` comes back not-live. Right for a long, resumable job.
3. **Fenced at the write**, so a lapsed holder's late write is refused by the store rather than silently interleaved. The only option that is actually *safe* rather than merely unlikely to be wrong.

Option 3 is what `FenceToken` is for.

#### The fence-token usage pattern

`FenceToken` strictly increases per `LockId` across acquisitions, and is **stable across `Renew`** (renewing extends the same hold, it does not start a new one). The pattern is the standard one (Kleppmann's fencing tokens): the *downstream store* records the highest token it has seen for a resource and refuses any write carrying a lower one.

```fsharp skip=fragment
// The holder threads its token into the write:
match! lck.TryAcquire(lockId, ttl) with
| None -> ()              // someone else holds it — skip
| Some lease ->
    try
        do! store.WriteFenced(resourceId, lease.FenceToken, payload)
        // ^ refuses the write if it has already seen a HIGHER token,
        //   i.e. if this lease lapsed and someone else took over.
    finally
        DistributedLock.releaseDetached onError lck lease
```

Two things follow, and both are easy to get wrong:

- **The token is worthless if the write path ignores it.** A subsystem that acquires a lease and then writes unconditionally has bought contention *reduction*, not mutual exclusion. That is a legitimate and often sufficient trade — it is exactly what the two migrated consumers do today — but call it what it is, and do not describe such a subsystem as safe across replicas.
- **A monotonicity break is dangerous in the wrong direction.** If tokens ever repeat or decrease, a stale holder's write outranks a live holder's and the fence actively causes the corruption it exists to prevent. This is why the Redis impl derives tokens from `INCR` on a counter key that carries **no TTL** (an expired counter restarts at 1), and why the contract pack asserts strict increase rather than mere difference.

#### Interaction with retry policies

Leases and retries interact in three specific ways:

- **The lease must span the whole retry loop, not one attempt.** A per-attempt lease releases between attempts, which is precisely when a competing dispatcher slips in — you would have serialised the attempts and not the job. The job scheduler holds one lease across the entire `RetryPolicy` loop for this reason, which is also why its TTL is measured in hours rather than seconds: `MaxAttempts` × the backoff delays is the number the TTL has to beat.
- **A `None` from `TryAcquire` is not a failure and must not consume a retry attempt.** "Someone else is doing this" is a *skip*, not an error: counting it as an attempt burns the budget for real failures and can dead-letter a job that never actually ran. Keep the acquire outside the retry loop, as the migrated consumers do.
- **Backoff sleeps are inside the critical section, so they are inside the TTL.** This is the trap that makes a lock look fine in testing and lapse in production: the work is fast, but `RetryPolicy.delayFor` sleeps for minutes between attempts, and the lease is being held (and expiring) throughout. Either budget the TTL against the *sum* of the delays or renew across them.

`releaseDetached` is the helper for releasing from a synchronous `finally` or `Dispose`: best-effort and non-throwing, because a failed release costs only the lease's remaining TTL, which the next acquire reclaims. Never let a release failure surface as a dispatch failure — the work already succeeded.

#### Conformance

`IDistributedLockContract` (`src/ToolUp.Platform.Tests/Contracts/`) is the bar every implementation is held to: acquire, re-acquire-returns-`None`, TTL-expiry reclaims, fence-token strict increase, release-returns-the-id-immediately, plus distinct-ids-never-contend, release-is-idempotent, release-is-holder-checked, and renew-extends-live / refuses-lost. It is bound to the in-process default on every checkout and to `RedisDistributedLock` when `TOOLUP_REDIS_CONNECTION` is set (pending, not green, when it is unset). Running both in one job is how GP 12 portability is proven against a second backend rather than asserted.

**`RedisDistributedLock` is a single-Redis lease, not Redlock**, and its file header says so at length. One Redis (or one primary of a replicated pair): a failover to a replica that has not yet received the lock key hands the id to a second holder. Redlock's multi-master quorum addresses that at the cost of N independent Redis deployments and a correctness argument that is itself contested; for the subsystems this seam serves, a lease occasionally handed out twice during a failover degrades to the behaviour they had *before* any lock existed, and `FenceToken` is the path to real safety for anything that needs it. A deployment needing quorum semantics implements the interface over its own consensus store — which is the point of having a seam.

> The substrate detail behind the process-profile gating lives in the [migration doc](../../../docs/migrations/16a-process-profile-gating.md).

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

- **Tick election over `IDistributedLock`** — Phase 9i shipped the [lease primitive](#idistributedlock--the-lease-primitive-phase-9i) and migrated two read-modify-write consumers; Phase 16a migrated a third (the webhook failure-state transition). There is exactly ONE unleased tick left: the scheduler's cron due-job selection, which the OAuth refresher rides as a scheduled job. Note that the loser of a contended dispatch currently WAITS on `acquireBlocking` rather than skipping, and the re-read inside the lease checks status and outstanding external work but not whether the job is still due — so tick election is the fix, not an optimisation of it.
- **A durable webhook / transactional queue** — the other half of the multi-silo story, and not a lock problem at all (see the two ⛔ items above). Until it lands, a `WebOnly` silo's events are dispatched by nobody, and a second `WorkerOnly` / `DispatcherOnly` replica adds no outbound throughput.
- **`WorkerOnly` → `Host.CreateApplicationBuilder()`** — the silo binds no port at all. The [`IServerHost.createWorkerHost`](../../ToolUp.Platform.Server/Server/IServerHost.fs) helper supports it; the compose-side construction-branch refactor is the follow-up commit on the Phase 16a body. Until it lands, sibling load balancers should not route HTTP at a `WorkerOnly` deployment (Kestrel binds the port but no Giraffe router responds).
- **Per-silo Platform Admin surface (Phase 61)** — the production-safe equivalent of `/dev/inspect`'s `ProcessProfile` panel, accessible to Owner/Admin without enabling `EnableDevEndpoints`.

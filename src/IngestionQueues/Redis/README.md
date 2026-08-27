# ToolUp.IngestionQueues.Redis

Redis-backed `IIngestionQueueStore` for the `ToolUp.RAG` companion — a **durable, cross-replica** document ingestion queue.

| Queue | Shared across replicas | Survives restart | Multi-replica |
|---|---|---|---|
| in-memory `Channels` queue (default) | no | no | refused by `RagIngestionInstanceValidator` |
| **`ToolUp.IngestionQueues.Redis`** | **yes** | **yes** | allowed |

The shipped default hands documents from the upload handler to the background ingestion service over a `System.Threading.Channels` channel. That is correct for one replica and one process lifetime, and wrong outside it: a restart mid-ingestion loses every queued document (the index entry survives in a non-terminal status, the job does not), and only the replica that handled an upload can drain it — which is why the SDK's preflight *refuses* `ReplicaCount > 1` without an explicit escape hatch.

This companion moves the queue into Redis. Documents outlive the process, and N replicas drain one queue.

Licensed under Apache-2.0. `StackExchange.Redis` is the only vendor dependency, and it stays inside this package (GP 1).

## Composition

```fsharp skip=fragment
open ToolUp.RAG.IngestionQueues.Redis

// Simplest form — the companion connects and owns the multiplexer.
let store = RedisIngestionQueueStore.create connectionString (Some logger)

// Preferred when the deployment already runs Redis for notifications or
// the embedding cache: pass the SAME multiplexer, pay for one pool.
let store =
    RedisIngestionQueueStore.fromMultiplexer
        multiplexer
        RedisIngestionQueueOptions.defaults
        (Some logger)

app |> RAGServerApp.withDurableIngestionQueue store
```

The connection string comes from the deployment (`ISecretStore` / configuration) — the companion never reads environment variables itself. Deployments wiring Redis for several substrates conventionally source all of them from `TOOLUP_REDIS_CONNECTION`.

Register the health probe alongside it:

```fsharp skip=fragment
Health.create multiplexer
```

## Why two replicas do not process the same document

The claim is a single `LMOVE pending processing LEFT RIGHT`. `LMOVE` is atomic, so exactly one caller receives a given job id however many replicas race for it. Everything else — attempt counting, lease expiry, redelivery — is bookkeeping on top of that one guarantee.

Delivery is **at-least-once**, never at-most-once. A drainer that dies mid-document leaves its lease key to expire; the next `ReclaimExpired` sweep returns the job to the pending list. Ingestion is batch-idempotent (re-indexing a chunk overwrites the same vector-store id), so a redelivery costs embedding spend and never corpus corruption. Choosing at-most-once instead would mean a crashed replica silently loses documents, which is the exact failure this companion exists to remove.

## Options

| Field | Default | Notes |
|---|---|---|
| `KeyPrefix` | `toolup:ingestion` | Namespaced away from `toolup:embeddings:` / `toolup:notifications:` so one Redis instance can back several substrates. Glob metacharacters are refused. |
| `MaxDeliveryAttempts` | `3` | First delivery plus two redeliveries. A document that crashes its drainer every time is a poison message — redelivering it forever would keep one replica permanently busy failing. Exhausted jobs are dropped and counted (`Dropped`). |
| `Database` | `-1` | StackExchange.Redis' "whatever the connection string selected". |

## Key layout

All keys are `{KeyPrefix}:1:*` (`1` is the schema version — bumped only when the layout changes, so an old-layout queue is left alone rather than half-read by a new build).

| Key | Type | Purpose |
|---|---|---|
| `…:pending` | LIST | Job ids awaiting a drainer; head is next out. |
| `…:processing` | LIST | Job ids currently claimed. |
| `…:jobs` | HASH | Job id → framed payload. |
| `…:attempts` | HASH | Job id → delivery-attempt count. |
| `…:lease:{jobId}` | STRING + TTL | Its **existence** is the live lease. Absent while the id is still in `processing` means the drainer died. |
| `…:dropped` | STRING | Counter of jobs dropped after exhausting their attempt budget. |

Payloads carry an explicit `TUIQ1:` frame ahead of the JSON. A value without it is reported as foreign and dropped rather than parsed hopefully — the whole point is that a *different* process reads what this one wrote.

## Failure posture

Every Redis failure degrades rather than throwing:

- **Enqueue** reports `false` (i.e. "queue full"), which is the caller's existing backpressure path — the document is marked `Failed`, the drop-observability triple is emitted, and the uploader is told. A silent `true` would lose the document.
- **Claim / reclaim / depth** log at `Warn` and retry on the next poll.
- **Complete** logs and leaves the job in `processing`, so the lease expires and the document is redelivered. At-least-once is the contract.

The health probe reports `Degraded`, never `Unhealthy` — every replica looks at the same Redis, so failing readiness would empty the rotation rather than route around anything.

## Tests

`src/ToolUp.Platform.Tests/InProcess/DurableIngestionQueueTests.fs` runs two arms:

- **Structural** (always): the store contract — atomic claim, restart recovery, two concurrent drainers over one queue with no double-processing, attempt-capped redelivery — pinned against `InMemoryIngestionQueueStore`, the reference implementation of the same contract.
- **Live** (env-gated on `TOOLUP_TEST_REDIS`): the same contract against a real Redis. Reports `Pending`, not `Failed`, when the variable is unset, so a fresh checkout is green without a broker. No Docker requirement.

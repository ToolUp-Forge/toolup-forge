# RAG operations: the query policy owns the embedder's retries, lost traces are reported, the drain loop waits for work

**Ships in:** ToolUp.Platform.Server (`IEmbeddingProvider.fs`), ToolUp.RAG.Core (`IIngestionQueue`),
ToolUp.RAG.Server, ToolUp.EmbeddingProviders.OpenAI. Breaking for implementers of `IIngestionQueue`
only, on the 0.24.1 draft. Everything else is additive.

## What changes

**`IIngestionQueue` gains two abstract members (breaking for an implementer).**

```fsharp skip=signature
abstract WaitForWork: ct: CancellationToken -> Async<bool>
abstract TryDequeue: unit -> Async<IngestionLease option>
```

The ingestion drain loop now waits for work holding no worker permit, takes a permit, then takes the
job with `TryDequeue`. Before, it took a permit first and waited in `Dequeue`, so an idle loop held a
permit and a waking retry had to steal it by polling. `Dequeue` is unchanged and still part of the
interface.

The shipped `IngestionQueue` implements both members on both of its arms, so a deployment that
composes it (with or without an `IIngestionQueueStore`) needs no change. `IIngestionQueueStore` is
unchanged: the durable arm's `WaitForWork` probes the store's `Depth` at the queue's claim-poll
interval, the cadence `Dequeue` already claimed at.

If you implement `IIngestionQueue` yourself:

- `WaitForWork ct` completes with `true` when a job may be available, and with `false` when `ct` is
  cancelled. It must not take the job. A `true` is a hint: the drainer follows with `TryDequeue` and
  loops when that returns `None`.
- `TryDequeue ()` takes the head job under a lease if there is one, without waiting.

**A per-call override of an embedding provider's retries (additive).** `EmbedCallOverride`, the
optional capability `IEmbeddingProviderCallOverride`, and the `EmbedCallOverride` module
(`singleAttemptWithin`, `isSupported`, `generate`). `QueryEmbedGate` passes
`singleAttemptWithin policy.Timeout` on every attempt, so `QueryEmbedPolicy.MaxAttempts` is the
number of requests a query embed makes. The OpenAI provider implements the capability, and the
caching decorator forwards it. Ingestion calls are unchanged and keep the provider's configured
`EmbedderResilience`. A provider without the capability is called exactly as before.

**`/health/rag` reports lost retrieval traces (additive).** The composition registers its
`BackgroundRetrievalTracer`, and the endpoint adds `RetrievalTraces: { Lost; Written; Capacity }`
beside `IngestionQueueDrops`. The block is absent when `withRetrievalTraceQueueCapacity 0` writes traces
inline. As with the other counters, an `int64` is serialised as a JSON string.

## Verification

The `Phase 945` cases in `IngestionBackpressureTests` cover all three. Each was red on the tree before
the change. The `IEmbeddingProviderCallOverride` contract pack is bound to the OpenAI provider and the
caching decorator.

## Rollback

Revert the commits. A custom `IIngestionQueue` that implemented the two new members must drop them
again, since an interface implementation may not name a member the interface lacks.

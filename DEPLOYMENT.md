<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK) -->

# Deploying a ToolUp SDK server

Operator-facing reference for running a server built on the ToolUp platform
SDK. This document covers the runtime concerns that are decided at deploy time
rather than in application code — most notably the **network-exposure posture
of the built-in HTTP endpoints**. For how the composition root itself is
assembled (`ServerApp` / `AIServerApp` / `RAGServerApp`, `ServerConfig`,
surfaces), see `docs/platform/`.

## Health and metrics endpoints — authentication posture

The SDK mounts a small set of infrastructure endpoints that sit **outside the
per-route `SurfaceRequirement` authorization boundary** — they are reachable
without an authenticated `Subject`. This is deliberate (probes and scrapers
don't carry bearer tokens), but it means their exposure is a **deployment
decision, not a code decision**: the correct control point for every one of
them is the network layer (load-balancer allowlist, reverse-proxy rule, or
monitoring-network CIDR), and for two of them a `ServerConfig` switch that
removes the route entirely.

Each endpoint below lists what it discloses, whether it is always mounted or
gated, and the recommended proxy-layer rule. **Rule of thumb: only `/health`
and `/ready` should be reachable from the public internet; everything else
should be restricted to the operator / monitoring network or disabled.**

| Endpoint | Auth | Mounted when | Discloses | Recommended exposure |
|---|---|---|---|---|
| `/health` | None | Always | Liveness only — a bare status, no configuration or internal state. | **Public OK.** Point the load balancer's liveness probe here. Safe to leave open. |
| `/ready` | None | Always | Readiness — whether declared dependencies (stores, providers) have come up. No configuration values. | **Public OK.** Point the readiness probe here. Safe to leave open. |
| `/metrics` | None | `ServerConfig.MetricsEndpoint = EnabledMetricsEndpoint` | OpenMetrics/Prometheus text: route templates, tag values, request counts and latencies — i.e. your traffic shape. No secrets, no request bodies. | **Restrict to the monitoring network.** Allow only the Prometheus/OTel scraper's source range at the proxy/LB, or set `MetricsEndpoint = NoMetricsEndpoint` to remove the route (clean 404) if you don't scrape it. |
| `/health/rag` | None | RAG is composed (`RAGServerApp` / `withRAG`) | Aggregate RAG telemetry only — rolling-window counts and P50/P95 latencies. **No query plaintext, no per-team / per-user breakdown** (privacy contract). | **Restrict to the monitoring / operator network** at the proxy if you consider aggregate retrieval health sensitive. Lower-risk than `/metrics`, but still not user-facing. |
| `/dev/inspect` | None | `ServerConfig.EnableDevEndpoints = true` (default **`false`**) | Rich diagnostics: registered modules, resolved configuration, startup config-validator outcomes, provider/probe results. This is your deployment's internal shape. | **Never expose in production.** Leave `EnableDevEndpoints = false` on internet-facing deployments; enable it only on a locked-down staging box or behind an operator-only proxy rule. |

Notes:

- **`/health` and `/ready` are exempt from rate limiting, request-timing, and
  the metrics middleware by path prefix**, so probing them cheaply is fine and
  they never distort your latency histograms.
- **`/metrics` and `/dev/inspect` do not authenticate by design.** Do not rely
  on "nobody knows the URL" — treat the network rule (or the config switch) as
  the control. If a request can reach the port, it can read these unless the
  proxy blocks it.
- **`/health/rag` upholds the retrieval privacy contract**: even an operator
  reading it never sees query text — only hashed/aggregate signals. The same
  contract governs the `KnowledgeQueryRejected` audit emitted when a query
  exceeds `RAGServerApp.withMaxQueryChars` (query hash + length only, never
  plaintext).
- The startup config validators (visible in `/dev/inspect`'s Validators panel)
  will **warn** when an authenticated deployment runs without a rate limiter —
  see `RAGRateLimitConfiguredValidator` and `RateLimitModeValidator`. Rate
  limiting is a per-query cost control for retrieval, not just a connection
  guard; configure `ServerConfig.RateLimit` or accept the exposure explicitly.

## Steady-state storage cost

Two SDK subsystems produce residue that is **reclaimed only by a scheduled
job**. Neither is a leak in the "grows with traffic" sense — both grow with
*deletion* and *failure* — but both are unbounded over a deployment's lifetime,
and neither reclaims anything at all unless `ServerConfig.JobScheduler` is set
to `InProcessJobScheduler` (or a distributed scheduler companion). A deployment
that composes the schedule and leaves the scheduler off has declared a job that
can never fire; startup warns, and nothing else tells you.

| Residue | What produces it | Reclaimed by | Composed with | Default cadence |
|---|---|---|---|---|
| **Orphaned content blobs** — `{container}/objects/_content/{hash}.data` with no metadata referencing them | A `IDataObjectStore.Save` that wrote its content blob and then died (crash, pod kill, storage error) before writing its metadata blob | `platform.data-object-orphan-sweep` | `ServerApp.withDataObjectOrphanSweep` | Daily 02:00 UTC, 24h grace |
| **Vector-index tombstones** — soft-deleted chunks carrying `_deletedAt` | `IVectorStore.DeleteChunk`, i.e. every document deletion and re-ingestion | `IVectorStore.Vacuum` on a schedule | `RAGServerApp.withVacuumSchedule` | Daily 03:00 UTC, 7-day retention |

The two cadences are deliberately an hour apart so the reclaim passes do not
contend for the same backing store in the same minute.

### Orphaned content blobs

`IDataObjectStore.Save` writes the content blob **first** and the metadata blob
second — it must, because the metadata names a content hash that has to already
exist. If the process dies between the two writes, the content blob survives
with nothing referencing it. Nothing reclaims it on its own: the in-band orphan
GC runs only on `Delete` / `Evict` / `Erase`, and the object whose save died was
never created, so it is never deleted.

Two consequences, and the second is the one to weigh:

- **Storage cost.** Accrues at the rate of crash-during-save. Small per event,
  unbounded over time.
- **Erasure completeness.** A subject-erasure pass (`IDataObjectStore.Erase`,
  the DSR pipeline) walks *metadata* to decide what to remove or redact.
  Content whose metadata write never landed is invisible to it, so a subject's
  bytes can outlive the erasure that was meant to remove them.

```fsharp
ServerApp.empty
|> ServerApp.withConfig { config with JobScheduler = InProcessJobScheduler }
|> ServerApp.withDataObjectOrphanSweep (
    DataObjectOrphanSweepPolicy.forScopes scopeIds
    |> DataObjectOrphanSweepPolicy.withOrphanSweepSchedule "0 2 * * *"
    |> DataObjectOrphanSweepPolicy.withOrphanSweepGracePeriod (TimeSpan.FromHours 24.0))
```

- **`scopeIds` is explicit, and has to be.** `IBlobStorage` has no
  cross-container enumeration and the SDK does not enumerate tenants, so the
  sweep cannot discover the containers it should visit. Pass the deployment's
  own scope list. An empty list schedules nothing — which is honest, where
  silently defaulting to `_platform` would look composed while sweeping a
  container that holds no data objects.
- **The grace window is not tuning, it is correctness.** A content blob with no
  metadata is indistinguishable from an in-flight `Save` that has not reached
  its metadata write yet. Reclaiming eagerly deletes live content out from
  under a concurrent writer. `withOrphanSweepGracePeriod` therefore clamps
  upward to a 5-minute floor; shorten it only if you know your `Save` latency
  bound, and never to zero.
- **Each run reaches exactly one scope's container.** Reclaims emit one
  `OrphanedContentBlobReclaimed` audit row per blob (content hash, bytes, age)
  plus one `OrphanSweepCompleted` summary per run that removed something, so
  "what did the sweep take, and when" is answerable from the audit trail alone.
  A run that reclaimed nothing writes no rows.
- **A blob the store refused to delete stays listed** and the next run retries
  it; the job reports that run as a transient failure rather than a success.

If you accept the residue — a short-lived deployment, an ephemeral store, a
backing bucket with its own lifecycle rules — compose
`ServerApp.withDataObjectOrphanSweep DataObjectOrphanSweepPolicy.disabled`. It
schedules nothing and registers nothing but the acknowledgement, and it silences
the `data-object-orphan-sweep` preflight warning. Leaving the warning in place is
also a legitimate choice; it never blocks startup.

### Vector-index tombstones

`IVectorStore.DeleteChunk` soft-deletes. Without a scheduled vacuum, tombstones
are reclaimed only when an operator calls `IVectorStore.Vacuum` by hand, so a
long-running replica's memory grows without bound. See the `rag-tombstone-vacuum-schedule`
validator and `docs/rag/concepts.md` (Background services) for the full contract.

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

## Share-token signing key — an operator-managed secret

Every public share link (`/r/{token}`, publishable form submits, any
`IShareTokenStore` token) is HMAC-SHA256-signed with a single 32-byte key held
in the composed `ISecretStore` under the reserved `_platform` scope as
**`share_token_signing_key`**. Treat it exactly as you would a session-signing
key or an auth secret: **provision it, back it up, and know how to rotate it.**

### Pre-provision it before first boot

Set `share_token_signing_key` in the `_platform` container of whatever
`ISecretStore` the deployment composes, to a **base64url-encoded 32+ byte
random value**. On any production-shaped deployment this is not advisory — see
the refusal below.

If the key is absent, `BlobShareTokenStore` generates one with a CSPRNG and
persists it on first use. That convenience is fine for a laptop and wrong for a
deployment, because a key nobody chose is a key nobody knows to back up.

### What losing it costs

**Every outstanding share link stops working, permanently.** Verification is a
signature check against the current key, so a wiped, restored-from-blank, or
re-provisioned secret store causes the key to be silently re-minted and every
previously-issued token to fail as if tampered with. There is no recovery other
than restoring the original key value — the tokens themselves carry no
recoverable secret. Include `share_token_signing_key` in the same backup and
disaster-recovery scope as your database credentials.

### Rotation

Overwrite `share_token_signing_key` with a new base64url-encoded 32+ byte
value. Every process picks the new key up within the signing-key cache TTL
(10 minutes) — **no restart required**. All outstanding tokens then fail
verification. That is the intended effect, and it makes rotation a usable
"revoke every live share link at once" lever as well as a hygiene cadence.
There is no key-id field and no overlap window: one key per deployment, so plan
rotation against your share-link lifetimes (or re-issue the links you mean to
keep).

### Startup refusal, and the acknowledgement

`ShareTokenSigningKeyProvenanceValidator` (preflight name
`share-token-signing-key-provenance`) is **security-class**, so it runs even
under `SkipPreflight`. With a live share-token surface it reports:

| Deployment shape | Key state | Result |
|---|---|---|
| Any | Operator-provisioned | `Ok` — silent |
| `PublicBaseUrl` unset **and** `ReplicaCount = 1` | Anything | `Ok` — silent |
| `PublicBaseUrl` set **or** `ReplicaCount > 1` | Absent | **`Error` — startup refused** |
| …same, with the acknowledgement below | Absent | `Warning` naming the flag |
| …same | Present, but auto-generated by the SDK | `Warning` |

The acknowledgement is `ServerConfig.AcceptEphemeralShareTokenKey = true`, or
`TOOLUP_ACCEPT_EPHEMERAL_SHARE_TOKEN_KEY=1`. It is the **non-breaking upgrade
route** for a deployment that is already running: set it, boot, and provision
the key on your own schedule. It never goes silent — the finding is reported as
a `Warning` naming the flag as the reason nothing was refused, so an opt-out
nobody remembers making cannot masquerade as a correct configuration.

### Telling a provisioned key from an auto-generated one

When the SDK mints a key it writes a marker secret,
**`share_token_signing_key_origin` = `auto-generated`**, beside it. That marker
is the only thing distinguishing the two cases — the key values are
indistinguishable — and it is what the last row of the table above reads. So:

- To **adopt** an auto-generated key: read the current value, record it in your
  secret-management system as a managed secret, then **delete the
  `share_token_signing_key_origin` marker**. The warning clears and no share
  link is invalidated.
- To **replace** it: write your own value (rotation, above). Outstanding links
  are invalidated.

A key minted before this marker existed carries none, so it reads as
operator-provisioned. That is deliberate — the store records no origin for it,
and guessing would be worse than the honest classification.

### Multiple replicas

Replicas booting together against an empty secret store used to race: each
minted a key, the last write won, and a token signed by one replica could fail
on another. The store now serialises generation within a process, re-checks the
store inside that gate, and — after persisting — **re-reads and adopts whatever
the store holds**, so a replica never signs with a key the secret store does
not have.

That narrows the window to a single store round-trip; it is not a
compare-and-set, because `ISecretStore` exposes no conditional write. The
complete fix is pre-provisioning, which is why the validator now insists on it.

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

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

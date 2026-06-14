# Migration — Phase 153: SEO / crawler observability via `RenderMetrics`

**Status:** observability-only, opt-in via the metrics sink. Zero cost when no sink is composed (GP 13). No consumer action required.

## What changes

`RenderMetrics` (the Phase 93 SSR observability surface) gains three crawler-aware counters over the existing `IMetricsSink` seam, so the single operational metric that matters for a large indexed site — the Googlebot `304`-vs-`200` ratio — is queryable without bolting on bespoke logging:

| Metric | Kind | Tags | Meaning |
|---|---|---|---|
| `publicrendering.conditional_get` | counter | `outcome` = `"304"` \| `"200"` | per page serve; the `304`-rate is the ratio |
| `publicrendering.request_by_agent` | counter | `agent` = `"googlebot"` \| `"bingbot"` \| `"other-bot"` \| `"human"` | request bucketed by bounded crawler class |
| `publicrendering.page_not_found` | counter | _(none)_ | `PageNotFound` fall-throughs — a soft-404 / stale-link signal |

- **Bounded cardinality.** `RenderMetrics.classifyAgent` maps the raw User-Agent into the four-value `agent` set via a cheap case-insensitive substring scan (`googlebot` / `bingbot` before the generic `bot` / `spider` / `crawl` / `slurp` catch). The raw UA is **never** a tag value — the metrics-sink cardinality discipline.
- **Wired in `PublicPageHandler.handlerKeyed`** alongside the existing render metric: the conditional-GET counter is emitted for a real page response (`200` / `304`), the agent counter for every request, and the not-found counter on a `None` fall-through. The cached + uncached (incl. [Phase 147](147-conditional-get-hardening.md) conditional) paths all flow through this one emit point.
- **Zero-cost when unused (GP 13).** The new emits + the User-Agent read/classification are gated on the resolved sink being a real (non-`NoOpMetricsSink`) sink, so the substring scan never runs on the NoOp path. The render-path `Stopwatch` remains the only always-on cost (unchanged from Phase 93).

## Diff to apply

None. The counters emit automatically once a metrics sink is composed (e.g. the OpenTelemetry companion). Query the `304`-rate as:

```
sum(rate(publicrendering_conditional_get{outcome="304"}[5m]))
  / sum(rate(publicrendering_conditional_get[5m]))
```

and the Googlebot share / soft-404 trend from `publicrendering_request_by_agent{agent="googlebot"}` and `publicrendering_page_not_found`.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — green. New Expecto coverage in `PublicRenderingTests.fs`:
  - `classifyAgent` maps representative Googlebot / bingbot / generic-bot / Slurp / desktop-browser / empty UAs to the bounded set.
  - the emit helpers tag `conditional_get` / `request_by_agent` / `page_not_found` correctly.
  - the page handler, with a live recording sink, counts a fresh crawl (`200`), a conditional re-crawl (`304`), the agent class, and a missing-slug not-found.
  - under the NoOp sink the page still serves (the gated metrics path is free).

## Rollback

Revert the Phase 153 commit. The counters are additive emits behind the existing `IMetricsSink` seam; reverting removes the three counter names and the `classifyAgent` helper with no behaviour or wire-format change to the served pages.

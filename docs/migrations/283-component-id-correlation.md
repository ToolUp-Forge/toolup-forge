# Phase 283 — component-id telemetry / audit correlation (consumer migration)

**What changes.** Two additive surfaces thread the stable Phase 279 `ComponentId` into the telemetry
and audit paths so per-component metrics and audit trails correlate cleanly across a display-name
rename:

1. **`ComponentCorrelation`** (in `ToolUp.Platform.Metrics`, alongside `MetricsMiddleware`) — pure
   helpers that express the `component_id` metric dimension and the stable audit source label:
   - `ComponentCorrelation.ComponentIdTag` — the `"component_id"` tag key (one stable wire string).
   - `withComponentId : ComponentId -> Map<string,string> -> Map<string,string>` — merge the
     correlation tag into a metric's tag map at an emission site.
   - `permitComponentIdDimension : MetricDefinition -> MetricDefinition` — append `component_id` to a
     metric's tag allowlist (idempotent) so the sink accepts the id when emitted.
   - `auditSourceLabel : ComponentId -> string` — the stable id string to record as a component's
     audit `SourceModule` when it wants a rename-stable audit trail.
2. **`ServerApp.componentIdForModule : string -> ServerApp -> ComponentId option`** — resolve a
   composed module's stable id from its display name (the label its audit events carry as
   `SourceModule`, and the dimension its metrics namespace under). This is the correlation join.

`ServerApp.addModule` now runs every module metric definition through `permitComponentIdDimension`, so
module telemetry is id-correlatable by construction — the allowlist *accepts* `component_id` when an
emission carries it.

**Scope.** Purely additive (GP 11) and zero-cost when unused (GP 13). A module keeps its name-derived
metric namespace (`toolup.{name}.{metric}`) and its name-based audit `SourceModule`; permitting the id
dimension changes *nothing* in rendered output until an emission actually carries the tag, and the
request-level hot path (`MetricsMiddleware.InvokeAsync`) is untouched — it allocates no id dimension.
The correlation-across-rename guarantee holds for a module that declares an explicit id via
`ServerModule.withComponentId` (Phase 279); a name-derived id changes with the name, as before.

## Adopting the correlation

Nothing is required — existing deployments are byte-for-byte unchanged. To gain rename-stable
correlation:

```fsharp
open ToolUp.Platform.Metrics

// 1. Declare an explicit stable id on the module (Phase 279) so its identity
//    survives a display-name rename.
let orders =
    ServerModule.create "Orders"
    |> ServerModule.withComponentId "orders-service"
    |> ServerModule.withMetrics [ invoicesMetric ]

// 2. Emit module metrics with the id dimension merged in — the allowlist
//    already permits it (addModule ran permitComponentIdDimension).
let id = ServerApp.componentIdForModule "Orders" app |> Option.get
sink.Increment(
    "toolup.orders.invoices.total",
    Map.ofList [ "status", "paid" ] |> ComponentCorrelation.withComponentId id)

// 3. For a rename-stable audit trail, record audit under the stable id label
//    rather than the mutable display Name.
let auditSource = ComponentCorrelation.auditSourceLabel id   // "module:orders-service"
```

Renaming `"Orders"` → `"Sales"` (keeping `withComponentId "orders-service"`) moves the metric
*namespace* (`toolup.orders.*` → `toolup.sales.*`) but leaves the `component_id="module:orders-service"`
dimension and the `auditSourceLabel` unchanged — a dashboard grouping by `component_id`, and an audit
query filtering the stable source label, both join across the rename.

## Verification

- `InProcess/ComponentIdCorrelationTests.fs` in `ToolUp.Platform.Tests`: a module metric + audit
  source label carry the stable id; renaming the display name preserves id-keyed correlation; a
  name-derived id changes on rename; permitting the id dimension without emitting it renders
  byte-identical output (GP 11); `permitComponentIdDimension` is idempotent (GP 13).
- The Phase 175 public-API baseline test treats the new helpers + accessor as additive surface
  growth (allowed under SemVer-on-`0.x`) — no `.approved.txt` edit for a non-breaking addition.

## Rollback

Stop calling the `ComponentCorrelation` helpers / `componentIdForModule` — nothing else references
them and no behaviour changes when unused. Or revert the Phase 283 forge commit; no persisted state
is involved (the `component_id` tag is emission-side only).

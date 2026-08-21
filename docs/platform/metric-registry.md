# Metric & subject registry

*Phase 519.* A compose-time registry that lets modules declare the
**business quantities they compute** (metrics) and the **entity
hierarchies they compute them over** (subjects) — the way they already
declare `DataType`s. Without it, module outputs are stringly-typed and
incomparable across modules; with it, the platform gains an ontology-lite
that fact storage, retrieval planning, and capability discovery stand on.

> **Not the observability metrics.** This is unrelated to
> `ToolUp.Platform.Metrics.MetricDefinition` (the Prometheus
> counter/gauge/histogram signals wired through `ServerModule.withMetrics`
> and the `/metrics` endpoint). To keep the two vocabularies unambiguous,
> the grounding types live in the **`ToolUp.Platform.Grounding`**
> namespace — you reference them as `Grounding.MetricDefinition`,
> `Grounding.IMetricRegistry`, etc.

## What a metric declaration is

A `Grounding.MetricDefinition` describes one quantity:

| Field | Meaning |
|---|---|
| `Id` | Stable identity token (`"elasticity"`, `"share_of_voice"`). Lowercase, rename-stable, `ComponentId`-class — **the** key every fact, plan, and discovery tool references. Never the display name. |
| `Name` | Human-readable display name (`"Price Elasticity"`). Cosmetic. |
| `Unit` | Unit symbol (`"GBP"`, `"%"`, `"count"`, `"ratio"`). |
| `Dimensionality` | Coarse dimension label (`"currency"`, `"count"`, `"ratio"`, `"duration"`, `"dimensionless"`) — the axis a later comparability check reads. |
| `Direction` | `HigherIsBetter` / `LowerIsBetter` / `Neutral` — the polarity a dashboard colours a change by. |
| `DisplayFormat` | Canonical rendering format — a .NET numeric format string (`"N2"`, `"P1"`, `"C0"`) or `""` for verbatim. The numeric-fidelity gate canonicalises a quoted number through this before comparing it to a fact. |
| `Staleness` | `FreshFor of TimeSpan` / `UntilSuperseded` / `UntilUpstreamChange` — how a fact's freshness is *derived* (no mutable flag is ever stored). |
| `ProducingOperation` | Optional catalog-operation id that produces this metric. When present, a planner can map a missing fact → this operation → its input schema → the data catalog. |
| `CanonicalMethod` | Optional canonical-method selector (Phase 566). When several methods compute this metric over one (subject, period), a *method-less* fact query resolves to this method's lineage by default — matched against method-identity strings (`computed:op:ver:hash` / `asserted:principal` / `imported:cert`), exactly or as a `:`-boundary prefix (`"computed:rollup"` matches every version of `rollup`). `None` = no default; every competing head surfaces. Competitors stay queryable either way, and the query surface discloses them (GP 9). |
| `RecomputePolicy` | Optional reactive-recomputation policy (Phase 561). When an upstream data-object version a fact was computed from is superseded, the lineage walk marks the fact `InputsChanged` (derived, never a stored flag); this policy governs what executes — `Eager` enqueues a recompute job (through `IJobScheduler`) that re-asserts on the ordinary `Assert` path, `OnQuery` recomputes lazily at the next read, `Manual` surfaces the changed state only. `None` = `Manual` (nothing recomputes unbidden; the composition is byte-for-byte unchanged). |
| `RollUp` | Optional roll-up semantics (Phase 563). Declares how the metric aggregates across a subject hierarchy: `Additive tolerance` makes a parent's value comparable to the sum of its direct children's for the same metric and period, within `tolerance` (absolute, in the metric's unit), so the standing coherence check can flag a cross-level inconsistency — a mixed-vintage aggregate, a partial load, a unit slip. `NonAdditive` (a ratio, an average, a share, an index) excludes it: a parent is not the sum of its children, so there is no decidable relationship to test. `None` = the default, treated as non-additive, so a metric that declares nothing is never coherence-checked and the composition is byte-for-byte unchanged. Comparability is derived wholly from this declaration, never configured per fact. |

A `Grounding.SubjectDefinition` declares a hierarchy — a *dimension* of
the entity space:

| Field | Meaning |
|---|---|
| `Id` | Stable identity token (`"product_hierarchy"`, `"geography"`). |
| `Name` | Display name. |
| `Levels` | Ordered level labels root → leaf (`[ "market"; "brand"; "sku" ]`). A concrete subject *instance* is a path through these levels; the definition declares the shape, not the members. |
| `Calendar` | Optional calendar tag scoping period comparability *within* this hierarchy, so "Q2" is unambiguous within a subject and never silently compared across calendars. `None` = the deployment default. |

## Declaring from a module

Declare on a `ServerModule` the same way you attach data types or query
handlers — both declarations are optional and compose left-to-right:

```fsharp
open ToolUp.Platform
open ToolUp.Platform.Grounding

let serverModule =
    ServerModule.create "sales"
    |> ServerModule.declareMetrics [
        { Id = "revenue"
          Name = "Revenue"
          Unit = "GBP"
          Dimensionality = "currency"
          Direction = HigherIsBetter
          DisplayFormat = "C0"
          Staleness = FreshFor (System.TimeSpan.FromDays 1.0)
          ProducingOperation = Some "sales.rollup"
          CanonicalMethod = None
          RecomputePolicy = None
          RollUp = Some(Additive 0.01M) }
      ]
    |> ServerModule.declareSubjects [
        { Id = "product_hierarchy"
          Name = "Product Hierarchy"
          Levels = [ "brand"; "sku" ]
          Calendar = None }
      ]
```

`ServerApp.addModule` fans each module's declarations into the app-level
registry. When the composition contains at least one declaration, `run`
builds an `IMetricRegistry` and registers it as a DI singleton.

## Reading the registry

Server-side consumers resolve `Grounding.IMetricRegistry` from DI. Every
lookup is a pure in-memory read (the registry is a compose-time immutable
projection — no async, no store round-trip):

```fsharp
let registry = ctx.GetService<Grounding.IMetricRegistry>()

registry.Metrics                       // every registered metric
registry.TryGetMetric "revenue"        // MetricDefinition option
registry.MetricsByModule "sales"       // this module's metrics
registry.MetricsByOperation "sales.rollup"  // reverse index: op → metrics
registry.TryGetSubject "product_hierarchy"
```

## Duplicate rejection

Two modules declaring the **same metric id** (or the same subject id) is a
configuration error the platform refuses at compose — the collision would
break every planner and fact lookup that keys on the id. `run` fails fast
with a diagnostic naming the id and both modules:

```
Duplicate metric id 'revenue' declared by modules: sales, finance.
Every registered metric must resolve to a unique id — rename the metric
in one module to disambiguate.
```

A *single* module re-declaring the same id twice is idempotent (collapsed
to one entry), not a conflict.

## When *not* to register

Registration is a **gift to planners and discovery tooling, never an
obligation** (plan D17 / GP 13):

- **A module that computes nothing worth planning against declares
  nothing.** No metrics → no registry singleton → composition is
  byte-for-byte identical to a pre-519 build. The whole apparatus is
  dormant.
- **Register a quantity only when another part of the system needs to
  reason about it by id** — store facts under it, plan a computation to
  fill it, discover it via tooling, or hold it to a staleness policy. A
  purely internal intermediate a module computes and consumes itself does
  not need to be registered.
- **Don't register a metric whose module can't (yet) produce it.** A
  metric with no `ProducingOperation` and no fact behind it is a promise
  the closed loop can't keep. Declare the operation link when the
  producing routine exists.

## Registration audit

When a composition registers grounding vocabulary, `run` emits a
compose-time registration-audit log line enumerating the registered
metric and subject ids and their producing modules — so a
deploy-over-deploy change in *what the app can be planned against* is
visible in startup diagnostics (GP 6). A composition that declares
nothing logs nothing (GP 13).

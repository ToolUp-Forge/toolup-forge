# The fact store

*Phase 520 (`ToolUp.Facts`).* A **bitemporal, content-addressed fact
store** — the typed *memory* tier of a grounded application. A numeric
answer quotes facts verbatim; prose exists for comprehension, not recall.

> Companion package — a deployment that composes no fact store
> (`ServerConfig.FactStore = NoFactStore`, the default) is byte-for-byte
> unchanged (GP 13).

## What a fact is

A **fact** is a verified assertion about a `(subject, metric, period)`:

| Field | Meaning |
|---|---|
| `FactId` | Content-addressed identity — `hash(subject, metric, period, method, inputHashes)`. |
| `Subject` | A node in a registered subject hierarchy (`{ Hierarchy; Path }`). |
| `Metric` | A registered metric id (`MetricRef`). |
| `Value` | `Scalar` / `Interval` / `Series` (a data-object version ref) / `Distribution` / `Categorical` / `Absent`. |
| `Period` | **Valid time** — the extent the value describes. |
| `AsOf` | **Transaction time** — when the assertion entered the store. |
| `Method` | `Computed(op, ver, paramHash)` / `HumanAsserted(principal)` / `Imported(certRef)`. |
| `Evidence` | Provenance refs — result/fit-artifact id, input hashes, trigger. |
| `Confidence` | Optional attribution (cites the method's diagnostics). |
| `Supersedes` | The `FactId` this one superseded — **derived**, never supplied. |
| `Disclosure` | `Surfaceable` / `Internal` / `Restricted policy` — classified at birth. |

## The laws (all decidable)

- **L1 — append-only.** No fact is ever mutated or deleted; a correction
  is a new fact that supersedes its predecessor.
- **L2 — identity determinism.** `FactId` is a hash of the identity
  tuple, so asserting an identical tuple is idempotent — the store
  converges under replay and duplication.
- **L3 — supersession shape.** Supersession edges link only *within* a
  lineage `(subject, metric, period, method id)`, and a superseder's
  `AsOf` strictly exceeds its predecessor's — the chain is acyclic by
  construction.
- **L4 — AsOf visibility.** `visible t = { f | f.AsOf ≤ t ∧ ¬∃g.
  g.Supersedes = f.Id ∧ g.AsOf ≤ t }` — the one rule every reconstruction
  and cache key derives from. `Query` with `AsOf = Some t` reconstructs
  "what we knew at `t`".

Two consequences the model gives for free:

- **Forecasts are derived.** `Fact.isForecast f` is `f.Period.To >
  f.AsOf` — a fact whose valid time runs past its transaction time. No
  flag; the timestamps carry it.
- **Competing facts are never merged (D19).** Two *methods* computing one
  `(subject, metric, period)` produce two current, method-attributed
  facts — not a supersession. `FactQuery.Method` selects one lineage.

## Assert / query

```fsharp skip=fragment
open ToolUp.Facts

let store = BlobFactStore.create blobStorage eventStore   // IFactStore

// Assert is idempotent under the content address; a changed input
// supersedes the current lineage head with a derived edge.
let! fact = store.Assert(scopeId, { Subject = subject; Metric = MetricRef "revenue"; ... })

// Reconstruct the fact base as of a past transaction time (L4):
let! asOfMay3 =
    store.Query(scopeId, FactQuery.forSubjectMetric subject metric |> FactQuery.asOf may3)

// Walk a lineage:
let! chain = store.QuerySupersessionChain(scopeId, fact.FactId)
```

Every `Assert` (and each derived supersession) emits a durable audit
event to `IEventStore` under the reserved `_facts` source module
(`FactAsserted` / `FactSuperseded`, GP 6 — the same `ILineageStore`
pattern, queryable via `IEventStore.ReadBySource scope "_facts"`); an
idempotent re-assertion changes no state and emits nothing.

## Fact vs result vs model artifact

The fact store sits **above** the analysis-result and model-artifact
stores — see [which-store-for-what.md](which-store-for-what.md). The line:

| Store | Holds | Keyed by | Lifecycle |
|---|---|---|---|
| **Fact store** (`IFactStore`) | *assertions* — the numbers an answer quotes | content address `(subject, metric, period, method, inputs)` | append-only, bitemporal, superseded |
| Result store | *computation outputs* — a run's full result payload | result id | replaceable |
| Model registry / fit artifact | *evidence* — a fit's diagnostics + reproducibility key | the fit's composite key | governed artifact |

A fact is an assertion; an artifact is the record of the computation that
*produced* it. A `Fact.Method = Computed(...)` and its `Evidence.ResultRef`
point *at* the artifact — the fact never inlines it. Likewise a `Series`
value references a data-object *version* rather than inlining its points (a
fact is an assertion *about* data, not a copy of it).

## Disclosure at birth

Every fact carries a `Disclosure` from its first assertion (plan D14).
Defaults: a fact whose metric is a registry-declared metric is
`Surfaceable`; an undeclared intermediate is `Internal`. Classifying at
birth is what makes a retrofit (reclassifying every fact ever asserted)
unnecessary.

Enforcement at the egress choke points has since shipped. Five surfaces
are gated, enumerated by the `FactEgressSurface` DU and checked through
the single `IFactDisclosureGate` seam — no choke point re-implements the
predicate:

| Surface | What it gates | Shipped |
|---|---|---|
| `FactRetrieval` | fact resolution into retrieval results / prompt context — default-deny, so the model never *sees* a denied fact ("see but don't say" is not a mode) | Phase 525 |
| `FactToolResult` | a fact-reading AI tool returning facts to the model as a tool result | Phase 525 |
| `FactNarrativePublication` | committing / publishing a fact-referencing narrative to a surfaceable store (KB commit, public-page publication) | Phase 525 |
| `FactExport` | a rendered export leaving the deployment as a document (the Reporting render path); denied values are redacted to the policy-naming marker before rendering, and the output notes withheld refs — id + policy, never the value | Phase 564.B |
| `FactWebhook` | an outbound fact-event webhook payload — contract-first, so the surface and gate contract landed *before* the emitter and the emitter is born consulting the gate | Phase 564.C |

The gate is registered by `FactsCompose` so the tier cannot be composed
without its egress doors armed. Remaining doors (external write-back,
certificates) extend the DU additively, the same way exports and webhooks
did.

## Composition

`ServerConfig.FactStore` (default `NoFactStore`) is the introspectable
slot the composition manifest / composable-surface descriptor report as
the resolved fact-store kind. Compose the store today via
`BlobFactStore.create` (over any `IBlobStorage`, auditing to `IEventStore`). The default
is blob-backed, append-only, and stateless between calls — distributed-ready
by construction (the content-addressed id makes concurrent writes
idempotent); a large deployment swaps in an indexed implementation behind
the same six-rule-audited `IFactStore` contract.

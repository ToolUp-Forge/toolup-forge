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

## Point reads vs population reads

`IFactStore` has two shapes of read, and they answer different questions.

| | Point read (`Query`) | Population read (`QueryPopulation`) |
|---|---|---|
| Asks | "what is metric M for subject S in period P?" | "which subjects rank highest/lowest on M, and what does that population look like?" |
| Subject | one instance (or an open filter) | a **set** — hierarchy + optional depth + optional path prefix |
| Returns | every matching fact, unordered, unbounded | a **bounded ranking** plus a **summary** |
| Ordering | none | declared — registry direction, or the caller's explicit choice |
| History | `IncludeSuperseded` available | deliberately absent |

Reach for the point read when the answer *is* a number. Reach for the
population read when the answer is a *comparison* — a superlative, a
top-k, a count above a threshold, or "what is even in here".

```fsharp skip=fragment
open ToolUp.Facts

// "Which SKUs are the most elastic?" — direction comes from the metric
// registry, never from the caller's intuition.
let! result =
    store.QueryPopulation(
        scopeId,
        { PopulationQuery.create (MetricRef "elasticity") "product_hierarchy" with
            Level = Some 2          // leaf SKUs only, not the brand roll-ups
            Ordering = RegistryDirection
            TopK = 20 })

match result with
| Ok population ->
    population.Ranked                 // the top 20 facts, best first
    population.Stats.SubjectCount     // how many subjects were ranked over
    population.Stats.Mean             // ... without materialising them
| Error refusal ->
    // e.g. the metric is unregistered, or declares Neutral — the store
    // declines to invent a sort order rather than guessing one (GP 9).
    ()
```

### The ceiling is the contract, not a courtesy

`PopulationQuery.TopK` is clamped into `[1, PopulationQuery.MaxTopK]` by
every implementation. **A population read returns a ranking and a summary
— never the population.** That bound is deliberate on three counts:

- The caller that wants "all 300,000" wants a *different thing* — an
  export, a batch job, a report — and should say so through a surface
  built for it. Letting a read grow without bound turns every consumer
  (a tool result, an answer prompt, an HTTP response) into an accidental
  bulk channel.
- The summary is what makes the bound tolerable. `PopulationStats`
  answers "what does this population look like?" — cardinality, period
  coverage, extremes, mean, freshness distribution — so the questions a
  full listing would have been used for are answered without one.
- A bound that lives in the *contract* cannot be forgotten at a call
  site. `PopulationResult.EffectiveTopK` and `Truncated` report what was
  applied, so a caller is told about the ceiling rather than left to
  infer it from a suspiciously round result count.

### What ranks, and what is only counted

Only a `Scalar` value carries a single magnitude, so only `Scalar`
members are ranked. `Categorical`, `Absent`, `Series`, `Distribution` and
`Interval` members are part of the population and appear in
`PopulationStats.NonComparableCount` — counted, never ordered. An
`Interval` is the interesting case: it asserts *bounds*, and ranking it by
its low bound, its high bound, or its midpoint would each encode an
optimism the assertion never contained (GP 9 — ordering is declared, never
guessed).

Ordering itself is registry-resolved. `RegistryDirection` reads the
metric's `Grounding.DirectionOfBetter`, so "most elastic" on a
negatively-signed metric is a registry fact rather than a model judgment.
Two situations refuse rather than guess, with separate messages because
they have separate remedies: the metric is **unregistered** (register it,
or rank explicitly), or it declares **`Neutral`** (there is no better
direction — say `Ascending` or `Descending`). An explicit ordering always
works, registry or not.

The read resolves over the **current heads visible at `AsOf`** (law L4), so
a superseded value never ranks and an `AsOf` dated before a supersession
ranks the head that was current then. Competing methods (D19) default to
the metric's canonical method and surface every competitor on request
(`PopulationMethodSelection`) — a population that admitted every method by
default would rank one subject once per method.

The default `BlobFactStore` implements this by enumerating the scope's
heads: correct at any size, efficient at small. An indexed current-heads
read model behind the same contract is the scale path; the decidable parts
of the pipeline (the subject predicate, the threshold, the ordering
resolution, the ranking and the statistics fold) live in
`PopulationQueryTypes` and are shared, so an indexed implementation is
equivalent by construction rather than by a second reading of this page.

### How the blob store serves a point read (Phase 890)

Below a threshold, `BlobFactStore` answers a point read the way it always
has: list the scope's `_facts/` prefix, download every fact, filter. At or
above it (`FactIndexOptions.defaults`: 512 facts), a point read, the
lineage-head lookup inside `Assert` / `AssertBatch`, and
`QuerySupersessionChain` read the facts they concern and no others. Measured
over a 10,000-fact scope, counting downloads rather than timing them:

| Operation | Before | After (warm) |
|---|---|---|
| Point read — one subject, metric and period | 10,000 | 1 |
| `QuerySupersessionChain` of a one-fact lineage | 10,001 | 2 |
| `Assert` superseding one fact | 10,001 | 2 (the head it confirms, and the metric surface's own probe) |
| `Assert` of a new lineage | — | 1 (the surface probe; no fact is read) |

The same counts hold at 1,000 facts: an indexed read's cost follows the
facts it returns, not the scope it searches.

**The index.** One empty leaf per fact, in the Phase 9f blob-index layout,
under `_factindex/{subject-metric}/{lineage}/{asOf}_{factId}.ref` — so one
leaf set is both the by-subject-and-metric index (the first segment) and
the by-lineage index (the first two). It holds no fact data: the fact blobs
are the truth, and deleting every leaf loses nothing.

**When it answers.** Every consulting read lists the census (`_facts/`) and
the leaves, and the index answers only when every fact in the census has a
leaf. Anything else — a failed leaf write, a fact written behind the store's
back, a scope that has just crossed the threshold, an index someone deleted
— makes that one read enumerate, which is always right, and write the
missing leaves from the facts it just read. A failed index write therefore
costs one slower read and never a different answer; the listings are two
calls that fetch names, never blobs.

**What still enumerates.** A query naming neither a subject nor a metric
(`FactQuery.all`) is the whole-store walk by definition — its answer is every
fact. A query naming only one of the two enumerates too; a metric across
subjects is the population read's job, served by the metric surface.

**Operating it.** `RebuildIndex(scope)` on the `BlobFactStore` rewrites every
leaf, and `IndexConsistencyCheck(scope, sampleSize)` reports the Phase 9f
`IndexConsistencyEntry` (a fact with no leaf, a leaf whose fact is gone) —
the same two members the event and job stores expose. Neither is needed for
correctness. Choose the policy with `BlobFactStore.createWithIndex`:
`FactIndexOptions.disabled` for the pre-890 behaviour exactly, `always` to
index at every size. Every parallel blob read or write in the store runs at
most 16 at a time.

### How the blob store serves a population read (Phases 702, 891)

At or above `FactSurfaceOptions.MinimumHeads` (512 by default) a population
read answers from the **metric surface**: one derived snapshot blob per
(scope, metric) under `_factsurface/`, holding one row per current head and
nothing else. The fact log stays the truth — the surface is a derived cache, slow to
rebuild, never wrong, safe to delete — and only the top-k the ranking
returns are read as facts.

**Never wrong.** Every surface read lists the scope's `_facts/` prefix (the
census the enumeration takes anyway) and compares it with the census the
snapshot has folded in. Equal: answer. Behind: fold the missing facts in,
or rebuild from the log. So a failed maintenance write, a second replica's
write, a restore or an erasure costs a slower read and never a different
answer.

**What a snapshot records of the census (Phase 891).** A count, a digest
(the sum of each fact id's 64-bit key — the first eight bytes of its
SHA-256), and a fixed-width invertible Bloom lookup table over the same
keys, which decodes a small difference back into the ids to fold. Its width
is set by `FactSurfaceOptions.MaxIncrementalFold` — never by history — and a
difference too large to decode is a rebuild, as a difference larger than
`MaxIncrementalFold` always was. It rests on one assumption: two different
sets of fact ids never share a count and a digest (a 2^-64 accident, and a
decoded difference is re-checked against the log before it is trusted).
Because a deletion moves the digest even when an addition restores the
count, an out-of-band erasure is now detected and rebuilt without help;
`FactSurface.drop` remains the operator's flush.

**Linear folds.** A batch — `AssertBatch`'s maintenance, or the read-time
reconcile — retires superseded rows by key and walks the snapshot once,
however many heads it supersedes.

**The parse cache.** Each surface-enabled store keeps parsed snapshots in
process, keyed by scope, metric and the census value above, and serves one
only to a read whose census — taken from the log on that read — is equal.
A hit also checks that the snapshot blob still exists (a probe, not a
download), so `FactSurface.drop` flushes every replica's cache too. It is
bounded at 16 (scope, metric) entries and 64 MiB of estimated encoded
snapshot, evicts oldest first, and does not exist under
`FactSurfaceOptions.disabled`. A second replica, a restart or a cold cache
reads the blob exactly as before; the seam stays stateless (GP 12 rule 4).

Measured at 100,000 heads of one metric seeded into the log (in-memory
backend, on a loaded machine; before is the Phase 702 format):

| | Before (Phase 702) | After (Phase 891) |
|---|---|---|
| Census entries | 100,000 | 100,000 |
| Snapshot bytes | 13,288,933 | 13,420,039 (the fixed census table) |
| … after a second metric of 100,000 facts | 14,288,938 | 13,420,039 |
| Superseding batch folded over 100,000 rows | 8.2 ms **per fact** (4,000 facts: 33.5 s) | 100,000 facts in one fold, 200,000 row visits |
| Superseding fold, three runs | — | 177 / 293 / 386 ms |
| Census digest per surface read | — | 17–23 ms |
| Repeat population read, same log | download + parse every time | no snapshot download; 381–415 ms against 455–599 ms for the same read with a cold cache |

The id list the before-column carried was ten bytes an id in that test; a
real content-addressed id is about 68, so in a deployment the growth is
roughly seven times larger. The repeat-read saving is small here because the
backend is in memory: what the cache removes is one download and one parse
of a 13 MB blob, and against object storage the download is the larger half.
What remains of a cached read is the census listing, the digest, and the
decidable pipeline over the rows.

A snapshot written before Phase 891 (format version 1) reads as no surface
and is rebuilt on the first population read after the upgrade.

## The indexed store: `ToolUp.FactStores.Postgres` (Phase 888)

The implementation the paragraphs above have promised since Phase 520: an
opt-in companion package in which facts are rows and every read is a keyed
query, so the cost of a question scales with its answer rather than with the
scope's history. `BlobFactStore` stays the default; a deployment that does
not reference the package is unchanged, and `perf-budgets.json` lists the
package among the assemblies the minimal deployment must not contain.

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withStorage blob
|> FactsCompose.withFactStore
|> PostgresFactStoreCompose.withPostgresFactStore connectionString PostgresFactStoreOptions.defaults
|> ServerApp.run
```

`FactsCompose.withFactStoreImplementation` is the seam underneath: it
replaces the composed `IFactStore`, and every registration built over it —
the evidence source, the disclosure gate, the resolver, the provenance
graph, reactive recomputation, the fact tools — follows the replacement.

**What is keyed.** The content address is the primary key within a scope,
so a replayed assert is a no-op by constraint. Point reads use
(scope, subject, metric, period); the lineage-head lookup uses a unique
index on (scope, lineage) over current heads only; the population read uses
(scope, metric, current-head flag). Supersession clears the old head's flag
and inserts the successor in one transaction, and a batch is one
transaction, all-or-nothing. Concurrent writers of one lineage serialise on
advisory locks, and the unique index is the guarantee behind them: a writer
that loses a race rolls back and re-derives against the winner, so
supersession chains stay linear across replicas.

**AsOf from the same table.** A fact is visible at `t` when it was written
by `t` and no successor written by `t` names it — the current-head flag
answers the common case, and the predecessors of successors written after
`t` come from the transaction-time index. There is no separate read model,
so a head whose transaction time is ahead of the reading clock is simply
not yet visible, and its predecessor is.

**What the database decides, and what the shared pipeline decides.** The
subject set, the metric, the period, visibility, a single named method and
— when no canonical-method selection can apply — the value threshold run
in SQL, where the arithmetic is provably the pipeline's. Canonical
selection, the ranking (its decimal tie order), and every statistic (the
mean, the first-extreme minimum and maximum, the method mix, the freshness
histogram) run over the projected rows through `PopulationQueryTypes`, so
the answer is the blob store's by construction. Only the ranked top-k are
read in full.

**Held to the blob store.** Both `IFactStore` contract packs bind to it
unmodified, and `IFactStoreContract.differentialTests` asserts one seeded
fact base into both stores and compares every population shape of the
Phase 702 matrix and every point-read shape of Phase 890's, value for
value. At 300,000 subjects in one scope a subject-and-metric point read
touched 5 rows by its executed plan, with no sequential scan (Phase 888,
the live test arm; the same read enumerates the scope on the blob store
below its index threshold).

**The multi-replica guard.** With `ServerConfig.ReplicaCount > 1` and the
blob store composed, `FactsCompose.withFactStore` registers
`BlobFactStoreScaleValidator`: it counts each scope's fact census, warns
above `BlobFactStoreScale.WarnAboveFacts` (50,000) facts in one scope, and
warns again, in stronger terms, above `BlobFactStoreScale.RefuseAboveFacts`
(300,000), naming this companion as the remedy both times. **It is
warn-only in this release** (operator decision, 2026-09-29): Phase 888
shipped the upper threshold as a startup refusal, and it now reports a
warning instead — both thresholds and the advice to move to this companion
stand, the constant keeps its name so no caller breaks, and nothing the
guard reports stops a deployment starting. A single-replica deployment
registers nothing, and the guard stands down once another store is
composed.

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
idempotent), and it indexes its own point reads above a threshold (see
[How the blob store serves a point read](#how-the-blob-store-serves-a-point-read-phase-890)).
A large deployment swaps in the indexed implementation,
[`ToolUp.FactStores.Postgres`](#the-indexed-store-toolupfactstorespostgres-phase-888),
behind the same six-rule-audited `IFactStore` contract.

### Facts reach the model two ways, and only one needs the model's consent

Composing the store arms **both** doors, on that one knob:

- **The tool door.** `query_facts` / `query_metric_population` /
  `list_metric_coverage` — the model asks, and gets an answer. Reliable,
  but it fires only when the model thinks to reach for it.
- **The push door.** Each user turn is compiled into a
  `RetrievalRequest.FactClause` by the answer planner, resolved ahead of
  vector search, and merged into the prompt ahead of the similarity
  chunks under the verbatim-quoting contract. A question naming a
  registered metric and subject therefore arrives with its facts already
  in context — no tool round-trip, and nothing for the model to decide.

The push door refuses rather than guesses: unregistered vocabulary
compiles to a typed refusal, never a nearest match, so a question that
resolves nothing produces no clause and retrieval is exactly what it
would have been. The compile can cost a provider call, so it is bounded
(default 3 s) and degrades to no clause on overrun or fault — a turn is
never blocked by planning. Turn it off with
`RAGServerApp.withFactClausePlanning false` when the store is composed
for its tool surface alone.

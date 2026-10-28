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
| Subject | one instance, a branch (Phase 1123), both, or an open filter | a **set** — hierarchy + optional depth + optional path prefix |
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
back, an index someone deleted — makes that one read enumerate, which is
always right, and write the missing leaves from the facts it just read. A
failed index write therefore costs one slower read and never a different
answer; the listings are two calls that fetch names, never blobs.

**A batch that crosses the threshold indexes the scope (Phase 1122).** The
size test is taken on the scope as it will stand once the batch's writes
land, so an `AssertBatch` that takes a scope from below the threshold to at
or above it writes a leaf for every fact it stores, and for every fact
already there (which that batch has read anyway, to find its lineage
heads). Before 1122 the test was taken on the census the batch started
with: a first upload of thousands of facts into an empty scope — the shape
a table's first run takes — wrote no leaf, and the first point read after
it enumerated the scope. A consumer deployment measured that as 1,755 blob
downloads for a handful of fact-detail opens at 1,742 facts; the pinned
case reads 2,002 downloads for one point read and its chain after a
2,000-fact batch, and 3 after the fix.

**What still enumerates** is set out per read in
[What each read costs on the blob store](#what-each-read-costs-on-the-blob-store-phase-1122).

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

### How the blob store serves a series read (Phase 1122)

The fact browse's chart read (Phase 1113's `QuerySeries`) asks the store
for the current heads of one metric under one method over a period range,
naming no subject: `IFactStore.Query` with `Metric`, `Method` and
`PeriodOverlaps` set. The point-read index cannot serve that — its leaves
are keyed by (subject, metric) and there is no prefix that walks one metric
across subjects — so until Phase 1122 the query enumerated, and every chart
downloaded every fact in the scope. A consumer deployment on a one-core
host measured 5,011 blob downloads (745 s of blob time) for about three
chart loads at 1,742 facts, with requests of 15–31 s.

**It now rides the metric surface.** A current-heads query naming a metric
and no subject reads the metric's snapshot (from the parse cache when the
census matches), keeps the rows whose period overlaps the range and, unless
the caller asked for the competition indicator, whose method matches, and
downloads those heads and no others. They then run through the same query
pipeline the enumeration uses, in census order, so the two paths agree by
construction. Since Phase 1123 the browse's subject prefix and level travel
to the store as the query's `SubjectBranch`, and the rows narrow by it too
(see below); the disclosure gate still judges every point, and since Phase 1125 it
judges the points the read holds rather than reading them again.

**Why the surface, and not a by-metric leaf set beside the point-read
index.** The deciding case is a slice-scoped run that supersedes one period.
A leaf set gains a leaf per new fact and keeps the superseded fact's, so a
read through it downloads every fact of the metric in the range — the heads
and the history behind them — and grows with each revision. The surface
folds the run's facts into its snapshot when the batch is asserted (one
snapshot read and one write per metric the batch touches, which the
population read already pays), drops the superseded rows, and so a read
after the run downloads exactly the heads it returns. A replica that missed
the batch folds the difference it decodes from the census, one fact read
per unseen fact, and rebuilds only past `MaxIncrementalFold`. It also means
one read model, already maintained, rather than a second index to keep.

**When it declines, and the read enumerates.** A historical `AsOf`; full
history (`IncludeSuperseded`); a scope below `MinimumHeads`; an admitted
head stamped after the read's instant (law L4 would show its predecessor,
which a heads-only snapshot does not hold); an admitted head the census or a
download cannot produce. And the first read of a metric whose snapshot does
not exist yet builds it — one enumeration per metric, once, exactly as the
first population read of that metric does — unless the batch that wrote the
metric's facts already built it (Phase 1124, below).

Measured with the counting storage fake in `FactStoreTests` (in-memory
backend; nine in ten facts originals, every ninth lineage revised once, two
metrics alternating by subject; the chart is one metric over three months):

| Scope | Points | Before: downloads | Before: time | After, warm: downloads | After: time | Second replica | After a slice-scoped revision |
|---|---|---|---|---|---|---|---|
| 2,000 facts | 225 | 2,000 | 80.9 ms | 225 (+1 listing) | 6.4 ms | 226 | 225 |
| 20,000 facts | 2,250 | 20,000 | 276.0 ms | 2,250 (+1 listing) | 102.8 ms | 2,251 | 2,250 |

The cold first read of each size was the surface build plus the read: 2,226
and 22,251 downloads. Since Phase 1124 a first upload builds the surface as
it writes, so that cold read is now paid only by a metric whose snapshot is
absent and whose batch could not build it (see below). The in-memory times understate the change against
object storage, where each download is a round trip: at the 136–148 ms per
GET the deployment above measured, and 16 downloads in flight
(`BlobFanOut.Bound`), the 20,000-fact enumeration is about 1,250 sequential
rounds — roughly three minutes — and the warm 2,250-point read about 141
rounds, some twenty seconds. A chart's cost now follows its points: a
24-point chart is two rounds for the store, whatever the scope holds (and,
until Phase 1125, two more for the gate).

**A chart of one branch reads that branch (Phase 1123).** Until 1123 the
store read the metric's heads over the range across every subject and the
browse filtered its prefix and level afterwards, because `FactQuery` could
name one subject and nothing coarser: a 100-point chart of one brand in the
browse pack's 2,400-fact table read 1,200 heads and the gate's 100.
`FactQuery.SubjectBranch` now names a branch in the shape `PopulationQuery`
names its subject set — a hierarchy, an optional path prefix, an optional
depth — decided by the same predicate (`PopulationQuery.matchesSubject`,
through `SubjectBranch.matches`), so the two reads cannot disagree on what
"under this branch" means. The surface rows carry the subject, so the series
read narrows by the branch before it downloads anything; the enumerating and
indexed paths filter by it in the shared pipeline; the Postgres store binds it
into SQL with the population read's own clauses; the delegated store forwards
it to the inner store. A level the registry cannot resolve to a depth is the
one thing the browse still cannot express, and, as before, it is not applied.

Measured with the browse pack's counting storage (in-memory backend; one
table run of SKUs under twenty brands, two months, two columns; one brand
charted over a warm surface):

| Scope | Metric heads in range | Points | Before: downloads | After: downloads |
|---|---|---|---|---|
| 2,000 facts | 1,000 | 50 | 1,050 | 100 |
| 20,000 facts | 10,000 | 500 | 10,500 | 1,000 |

The after figure is the chart's points plus the gate's look at each; since
Phase 1125 the gate no longer looks again, and the chart costs its points
(50 and 500). A chart
of the whole table is unchanged: its branch is the hierarchy, which every head
lies in.

**`Subject` and `SubjectBranch` AND-combine and are never exclusive.** Both may
be named; a subject inside the branch reads as the subject alone, and a subject
outside it matches nothing, as any two disjoint clauses do.

### A chart is one download: the browse's series read answers from the rows (Phase 1126)

After Phase 1123 a chart still downloaded **one fact blob per point**: the
store found the candidate heads on the surface and then fetched each full
`Fact`, although the snapshot it had just read already held almost everything
a point needs. Since Phase 1126 the browse's `QuerySeries` takes the points
from the rows themselves, and judges them as rows, so a chart costs the
metric's one surface snapshot (nothing at all when the parse cache holds it)
and no fact blob.

**What a row now carries.** Each `FactSeriesPoint` field was decided against
the row (the record is in the `FactSurface.fs` header): the subject, period
bounds, fact id and `AsOf` were already there, freshness derives from `AsOf`,
and the withheld verdict comes from the classification the row has carried
since 702. The row gained the period `Label` and the head's value — the value
because a point's rendering renders the whole value under the metric's display
format, and a non-scalar head (a table run's `Absent` gap, an interval, a
category) cannot be rendered from a magnitude. The codec writes the value only
for a non-scalar head, so a scalar surface grows by its labels alone. The
snapshot format moved to version 3; a version-2 snapshot decodes to nothing
and is rebuilt through the census path, never read without its labels. The
assert-time build (1124) and the read-time rebuild still go through one heads
selection, and a test holds them byte-for-byte equal in the new shape.

**The rows are the answer, not a hint.** For the chart's query — one metric,
one method, no subject, current heads — every step of the store's pipeline is
decided by what a row holds: the branch, the exact period overlap, the method,
L4 visibility (every row is a head; a head stamped after the read's instant
declines) and the listing order (census order, then the stable sort on
hierarchy and period start). So the browse's own rules run unchanged over the
rows — head-for-period, overlap membership, the later-`AsOf` tiebreak, the cap
checked before the gate — and a parity test over originals, revisions, a
slice-scoped run, `Absent` heads and an `Internal` column holds the row path's
`FactSeries` equal to the fact path's, field for field, with the same audit
rows. It declines, and the browse reads facts exactly as before, wherever the
fact path would enumerate, for a method-less query, and for an admitted row
with a `Local` instant (whose read-back from a blob depends on the reading
machine's zone). `IFactStore.Query` itself is unchanged and still returns, and
downloads, facts; the rows are an internal read of the blob store, and a store
without them — or a decorator over one — is read the fact way.

**The gate judges rows (`IHeldFactRowDisclosureGate`).** Beside 1125's held
form, a Facts-tier, in-process entry takes the rows the door read — each a
`HeldFactRow` of fact id, metric and classification — under the same vouching
rule. Its verdicts and audit rows are the id form's in every configuration,
because the gate's decision procedure reads of each fact exactly those three
things; the contract pack holds that for the base and taint configurations and
the disclosure suite for the viewer-aware one. **Under a taint configuration
the row form still decides**: the taint stage walks the derivation graph,
which the gate builds from the scope's whole listing in every form and reaches
each fact by id, so it never needed the points' facts. A chart under taint
therefore pays the graph load and no per-point read — less than after 1125, not
the same. Phase 1093's write-maintained taint index is what would retire the
graph load itself. The browse takes the row form from its held gate by
capability; a held gate that does not offer it is asked in its held form, over
facts, so a decorator is never bypassed.

Measured with the browse pack's counting storage (in-memory backend; one table
run of SKUs under twenty brands, two months, two columns; one brand charted):

| Scope | Points | Before: fact downloads | Before: time | After: fact downloads | After: snapshot downloads | After: time |
|---|---|---|---|---|---|---|
| 2,000 facts, warm | 50 | 50 | 6.3 ms | 0 | 0 | 3.0 ms |
| 20,000 facts, warm | 500 | 500 | 52.7 ms | 0 | 0 | 44.0 ms |
| 2,000 facts, first read after the upload | 50 | 50 | 5.3 ms | 0 | 0 | 4.7 ms |
| 20,000 facts, first read after the upload | 500 | 500 | 73.6 ms | 0 | 0 | 35.3 ms |

The snapshot count is 0 because the writing replica's parse cache holds the
snapshot the batch built (1124); a cold replica downloads it once. The
in-memory times are the handler's own work and understate the change against
object storage: at the 136–148 ms per GET and 16 downloads in flight measured
above, the 500-point chart was about 32 rounds (some 4.5 s) and the 2,250-point
chart of the 1122 measurement about 141 (some 20 s); each is now at most one.
Under a taint configuration, over the parity corpus (7 points, 16 facts in
scope): the fact path after 1125 read 23 facts (the graph's 16 plus the 7
points), the row path reads 16 — the graph load alone.

### A view reads once and refines locally: the view slice (Phase 1127)

The browse was built on the rule "every change of page, metric, direction or subject
prefix is a new server read". Each of those reads pays the store, the gate and a round
trip. Fact tables change when a run lands, not while someone explores, so the browse
now works the other way round.

`IFactBrowseApi.QuerySlice` reads one table's current heads over a period range, an
optional subject branch and a metric set. It returns them as one columnar table, a
`Fuaran.Core.Column` `Table` in its canonical codec, which any columnar consumer can
bind with no adapter. The client prepares the slice once (`DataFrame.prepare`). It then
answers every drill-down page, sort, metric change, branch filter and chart as a
`Fuaran.Compute.DataFrame` pipeline over it (`FactBrowseSlice.rowPage`, `.series`).
It reads again only when it leaves the slice or the slice's **run stamp** moves.

- **One read per metric, each one the series read.** Each metric's heads come from the
  same head query `QuerySeries` uses. On the blob store with the row-form gate (Phase
  1126) that is one surface download per metric and no fact. Elsewhere the slice falls
  back to fact reads, as the series read does.
- **Judged before it is encoded.** The gate decides every cell before encoding. A
  withheld cell keeps its subject, period, fact id and ranking position, the
  existence-level data the series and drill-down reads already serve. It carries no
  value and no rendering.
- **Ranks without values.** Each metric's comparable heads are ranked over the whole
  slice in the population read's own order (value, then fact id), in both directions,
  withheld heads included. A drill-down under any prefix inside the slice is that order
  restricted to the prefix. So the client can number a withheld row's place without
  holding its value, and its page matches `QueryRows` row for row.
- **The summary is stricter.** A local page's minimum, maximum and mean are withheld
  whenever any matched head is withheld. The server withholds them only when the
  ranking up to the page's end holds one, because it can fold values the slice never
  receives. The local summary can therefore be stricter than the server's, never looser.
- **The run stamp.** `FactTableListing.RunStamp` is the current run's watermark token.
  The slice records it, read before any head, so a run that commits mid-read leaves the
  slice looking older than it is and never newer. A refreshed listing that names
  another stamp drops the slice, and so does the per-run notification.
- **Bounded.** `FactBrowseApi.MaxSliceCells` (25,000 heads across metrics) is checked
  before the gate. A table over it is refused with the cap named, and the browse falls
  back to the server's page reads for that table.
- **What stays a server read.** `GetFact` returns a fact's supersession chain and
  `GetRunMovers` returns a run's movers with the facts they replaced. Neither is in a
  slice of current heads.

The packages are the published Apache-2.0 `Fuaran.Core.Column` and
`Fuaran.Compute.DataFrame`, restored from nuget.org. The ruling that admits them is
recorded in [the open-core boundary](open-core-boundary.md).

**Measured** (in-memory blob store, twenty brands, two months, two columns). The
session is: open a table, change metric twice, sort, filter to a brand, page three
times, open one chart.

| | 2,000 facts | 20,000 facts |
|---|---|---|
| Before: server reads | 9 | 9 |
| Before: blob downloads | 250 | 325 |
| Before: per interaction | 2.0–3.0 ms | 19.8–38.5 ms |
| After: server reads | 1 (the slice) | 1 (the slice) |
| After: blob downloads | 2 (one surface per metric), then 0 | 2, then 0 |
| After: slice read + decode + prepare | 65 ms | 743 ms |
| After: encoded slice | 452 KB | 4.55 MB |
| After: per interaction (local) | 0.5–6.6 ms | 7.6–43.9 ms |

These figures are in-process, so they price neither the network nor object storage.
On a deployment each "before" interaction is a round trip plus its share of the 325
downloads, at the 136–148 ms per GET measured above. That cost is what made each
interaction take seconds, and after the slice it is paid once per table. Locally, an
interaction over the 20,000-fact table costs about what the in-memory server read does,
so the saving is the trip, not the compute. At the cap (25,000 cells, one metric) the
slice encodes to 5.9 MB. It decodes and prepares in 279 ms on .NET and 946 ms under
Fable on Node, and a page over it takes 32 ms on .NET and 238 ms under Fable.

### A first upload leaves its metrics' surfaces warm (Phase 1124)

`AssertBatch` loads the whole scope to find lineage heads whenever the
point-read index is not consulted (below `FactIndexOptions.MinimumFacts`, or
crossing it in this batch) or is incomplete. On those paths the batch then
holds every fact of the scope — the ones it read and the ones it wrote — so
for each metric it touched whose snapshot does not exist, and when the scope
as it now stands reaches `FactSurfaceOptions.MinimumHeads`, it builds that
snapshot with writes and no fact reads. It lists the scope once after the
writes and builds through the read-time rebuild's own path (the same heads
selection, row order and census), so a snapshot built at assert is
byte-for-byte the one a read would build over the same log; a test asserts
exactly that, over a crossing batch that also supersedes earlier heads. A
fact another writer lands concurrently is left out of the census, so the next
read folds it as unseen. The build is best effort, like all surface
maintenance: a failure costs the later read its enumeration, never a
different answer, and never fails the batch.

**What still builds on first read:** a batch over a scope whose index is
complete (it reads only the touched lineages' heads and does not hold the
scope), a scalar `Assert`, and any snapshot flushed or lost afterwards.

Measured with the counting storage fake in `FactStoreTests` (one batch into
an empty scope, two metrics alternating by subject; the series read is one
metric over three months, the population read a top-10 ranking of the other):

| Scope | First series read: before | after | First population read: before | after | Second replica: series / population |
|---|---|---|---|---|---|
| 2,000 facts | 2,253 | 252 (its points) | 2,011 | 10 (its page) | 253 / 11 |
| 20,000 facts | 22,503 | 2,502 (its points) | 20,011 | 10 (its page) | 2,503 / 11 |

The browse pack's cold `QueryRows` after a 2,400-fact table run reads 20
downloads, the same as a warm page (2,421 before).

### What each read costs on the blob store (Phase 1122)

Above the thresholds (`FactIndexOptions.defaults` and
`FactSurfaceOptions.defaults`, 512 each), with the index complete and the
metric's surface built:

| Read | Shape | Downloads |
|---|---|---|
| Point | `Get`; `Query` naming a subject and a metric | the facts it returns |
| Lineage | `QuerySupersessionChain`; the head lookup inside `Assert` / `AssertBatch` | the lineage's facts (one, to confirm, for a head lookup) |
| Population | `QueryPopulation` | the top-k page it returns, plus the snapshot when the parse cache is cold |
| Series | `Query` naming a metric and no subject, current heads | the metric's heads in the range under the query's `SubjectBranch` (all of them when it names none), plus the snapshot when the parse cache is cold |
| Browse chart | `FactBrowseHandler.querySeries` (Phase 1126) | **one surface download** — the snapshot, only when the parse cache is cold — and no fact; under a taint configuration, plus the gate's graph load (below) |
| Full history | `Query` with `IncludeSuperseded`, or with a historical `AsOf` | every fact in the scope |

**What enumerates by definition**: a query naming neither a subject nor a
metric (`FactQuery.all`, and the full-history listing built on it) — its
answer is every fact, so no index makes it read fewer; a query naming a
subject and no metric; and the history reads above, because reconstructing
what was current at an earlier instant needs the superseded facts a heads-only
read model has dropped. **What enumerates once**: the first consulting read
after the index went incomplete (it rewrites the missing leaves), and the
first read of a metric whose surface does not exist (it builds it — no
longer after a first upload, which builds it at assert; Phase 1124). The
disclosure gate's own check under taint policies loads the scope separately
and is not covered by this table.

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

## The method a derived table's run declares (Phase 1040)

A declared fact table ([fact-tables.md](fact-tables.md)) writes every cell as a fact whose `Method`
is the **table's** lineage — `Computed(producingOperation, "v<schemaVersion>", tableId)` — so the
table's facts never merge with another producer's. When the table is *derived* (a run computes its
rows from uploaded inputs by some derivation), the run can also say **which** derivation, and at
which version, by declaring a method when it opens:

```fsharp skip=fragment
let options =
    FactTableRunOptions.none
    |> FactTableRunOptions.withInputs [ "sha256:" + fileHash ]
    |> FactTableRunOptions.withMethod (DeclaredMethod.create "trade.derive" "v1")

let! run = writer.OpenRun(scopeId, "trade-daily", options = options)
```

Every fact the run writes, absences included, then names that method in its evidence:
`DeclaredMethod.ofEvidence fact.Evidence` is `Some { MethodId = "trade.derive"; MethodVersion = "v1" }`,
and `DeclaredMethod.describe` renders it for a citation (`trade.derive v1`). A fact's evidence now
names the method as well as the inputs (`InputHash.named`) — what it was computed *from*, and *how*.

**The method is evidence, not identity.** It is stamped as one token, `method:<id>@<version>`, among
the fact's input hashes. That puts it in the fact's **content address** but not in its **lineage
key** (L3), which is exactly the pair of properties a re-derivation needs:

- a run by another method (or another version of one) writes *new* facts — never an idempotent skip
  that would leave the old method's name on an unchanged value;
- those facts stay in the table's one lineage, so they **supersede** what the earlier method wrote
  for the same subject and period, exactly as a same-method run does. Re-deriving a table never
  leaves two current facts behind.

That is the difference from D19's competing methods above: two *producers* computing one
`(subject, metric, period)` are two lineages; two *derivations of one table* are one lineage whose
facts say which derivation wrote them. A run that declares no method writes byte-for-byte what it
wrote before (GP 11), and its facts name none. The token is never in the `InputHash` form, so
`InputHash.named` never reads it as an input.

Both writers honour it: `DefaultFactTableWriter` over any `IFactStore`, and the delegate writer,
which keeps the method beside the run (`_delegate-tables/methods/<runId>.json`, only when declared)
and stamps every row it mints under that run. A method whose id or version is empty, contains
whitespace or a control character, or whose id contains `@`, is refused at open with
`FactTableRunOptionsRefused`. The laws are in `IFactTableWriterContract.provenanceTests`, bound to
every implementer. Migration: [`1040-fact-table-run-method.md`](../migrations/1040-fact-table-run-method.md).

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

### The gate judges facts the caller already holds (Phase 1125)

`IFactDisclosureGate.Check` takes ids and resolves each through the store, so
a door that has just read its facts pays for them twice: once to read them,
once more at the gate. The re-read has one virtue — the verdict is over what
the store holds, and a fact's `Disclosure` is not covered by its content
address, so a fact handed in cannot be verified from its id.

`IHeldFactDisclosureGate.CheckHeld` (Facts tier, beside the id form) trades
that virtue away for in-process callers only. It takes the resolved scope,
the principal, the surface, the ids to judge and **the facts the caller
read**. What the caller vouches for: **every held fact was read from this
scope's fact store, through that scope, during this request.** The verdicts
and audit rows are the id form's — the same taint, viewer, purpose and budget
facets, the same `FactDisclosureDenied` / `Declassified` / `Accessed` rows —
with each id resolved from the held facts instead of the store. An id with no
held fact is denied `unknown-fact`, exactly as the id form denies an id the
scope does not hold, so a door whose read found nothing hands the id with
nothing beside it. Under a taint configuration the derivation graph is
loaded as the id form loads it; the held form saves the per-id reads and
nothing else.

**In-process only.** A remoting, peer or webhook path never takes it: there
the facts would come from outside the store, and the id form is the only
sound one. That is also why it is not a member of `IFactDisclosureGate`,
which sits below the `Fact` type and has a dozen implementers.

**How a door reaches it.** `FactsCompose.withFactStore` registers
`IHeldFactDisclosureGate` as `FactDisclosureGate.held` over the composed
`IFactDisclosureGate` — resolved, not registered beside it, so the two cannot
disagree and `withTeamOutputVisibility`'s replacement gate is the one judged.
The platform gate is its own held form. Any other gate — a deployment's
decorator, a test double — gets its id form behind the held entry: it is
never bypassed, only left without the saving.

The fact browse's four reads ride it (`FactBrowseDeps.Gate` is the held form).
Measured with the browse pack's counting storage (in-memory backend; two
table runs over 200 SKUs, two months; each read warm):

| Read | Store reads | Before: downloads | After: downloads |
|---|---|---|---|
| `QueryRows`, a page of 10 | 10 | 20 | 10 |
| `QuerySeries`, 100 points | 100 | 200 | 100 (0 since Phase 1126: the chart reads and judges rows) |
| `GetRunMovers` | 32 | 52 | 32 |
| `GetFact`, with its chain | 4 | 6 | 4 |

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

There are two doors, and they are armed by different things:

- **The tool door.** `query_facts` / `query_metric_population` /
  `list_metric_coverage` — the model asks, and gets an answer. Reliable,
  but it fires only when the model thinks to reach for it. **Armed by**
  composing the fact store AND the AI tier: the store declares the tools,
  and only the AI companion's composition registers them.
- **The push door.** Each user turn is compiled into a
  `RetrievalRequest.FactClause` by the answer planner, resolved ahead of
  vector search, and merged into the prompt ahead of the similarity
  chunks under the verbatim-quoting contract. A question naming a
  registered metric and subject therefore arrives with its facts already
  in context — no tool round-trip, and nothing for the model to decide.
  **Armed by** three things together: the fact store, a RAG app (whose
  retrieval pipeline picks up the fact resolver and the clause planner),
  and a **question compiler** for the planner.

Composing the store on its own therefore arms neither door fully: with no
AI tier the tools are never registered, and with no compiler the planner
refuses every question, so the push door is dormant.

#### The question compiler (Phase 986)

The planner takes its compiler from the first of these that the
composition provides:

1. **An explicit choice** — `FactsCompose.withQuestionCompiler compiler`.
2. **A registered `IAIProvider`** — the structured-output compiler over it.
3. **The AI tier's `IAIProviderFactory`** — the registration every AI or
   RAG composition makes. The compiler resolves a provider per plan, as the
   planning principal in the plan's scope (a team member for a team scope,
   the user for a user scope, a session otherwise), so the deployment's
   fallback policy, a team's own provider profile, usage metering and quota
   all apply to the compile exactly as they do to the turn it serves. A
   factory that cannot resolve a provider refuses the question with its own
   reason; nothing is pushed.

With none of them, the planner refuses every question, and a startup
warning — validator `fact-question-compiler` — says so and names the
remedies. To keep the push door off deliberately, and silence the warning,
choose the no-op compiler:
`FactsCompose.withQuestionCompiler AnswerPlanner.noQuestionCompiler`.
`RAGServerApp.withFactClausePlanning false` instead stops a RAG app from
consulting the planner at all.

#### Composing the tier onto a RAG app in one call

```fsharp skip=fragment
RAGServerApp.create factory providerProfile embedder
|> RAGServerApp.withFacts FactsCompose.withFactTier
|> RAGServerApp.withConfig config
```

`FactsCompose.withFactTier` switches `ServerConfig.FactStore` on and
applies `withFactStore` and `withFactTableWriter`. `RAGServerApp.withFacts`
applies it to the app's base when the app is **composed**, so it may sit on
either side of `withConfig` — which replaces the whole `ServerConfig`,
fact-store knob included, and used to make the hand-written
`RAGServerApp.mapAI` route a silent no-op when it ran first. Because a RAG
app always composes the AI tier, this one call arms both doors: the tools
through the AI tier, the push door through the factory-backed compiler.
The `mapAI` route still works when the knob is already on; `withFacts` is
the order-independent form. The tier is passed in rather than referenced
because the fact companion is a separate package — a RAG app that composes
no facts takes no dependency on it.

The push door refuses rather than guesses: unregistered vocabulary
compiles to a typed refusal, never a nearest match, so a question that
resolves nothing produces no clause and retrieval is exactly what it
would have been. The compile can cost a provider call, so it is bounded
(default 3 s) and degrades to no clause on overrun or fault — a turn is
never blocked by planning. Turn it off with
`RAGServerApp.withFactClausePlanning false` when the store is composed
for its tool surface alone.

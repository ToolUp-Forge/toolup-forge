# Retrieval and fact-store performance under concurrent load

Phase 886, with the storage arms run and budgeted by Phase 929. What the retrieval pipeline and the
fact store cost when many callers use them at once, measured by the load harness in
`src/ToolUp.RAG.Benchmarks`, with what each figure was measured over, the machine's load when it
was taken, and which figures are **extrapolated rather than measured**. Before Phase 886 every scale
figure on record was taken one query at a time over an in-memory blob backend.

## Running it

The harness is a separate project; no deployment loads it (GP 13). One command runs the gate
configurations on a machine with no cloud account and no paid service:

```powershell
dotnet build src/ToolUp.RAG.Benchmarks -c Release
dotnet src/ToolUp.RAG.Benchmarks/bin/Release/net10.0/ToolUp.RAG.Benchmarks.dll load gate
```

The matrix is run by hand:

```powershell
# retrieval: stores x blob arm x corpus sizes
dotnet src/ToolUp.RAG.Benchmarks/bin/Release/net10.0/ToolUp.RAG.Benchmarks.dll load retrieval `
    --stores flat,hnsw,pgvector --blob memory --sizes 10000,100000,1000000 `
    --concurrency 8 --queries 200 --rounds 5 --recall 50 --out results.txt

# facts: the stated scale is the default (300,000 subjects x 3 metrics x 52 weeks)
dotnet src/ToolUp.RAG.Benchmarks/bin/Release/net10.0/ToolUp.RAG.Benchmarks.dll load facts `
    --blob memory --subjects 300000 --metrics 3 --weeks 52 --concurrency 4

# facts on the database-backed store instead of the blob store (Phase 929)
dotnet src/ToolUp.RAG.Benchmarks/bin/Release/net10.0/ToolUp.RAG.Benchmarks.dll load facts `
    --store postgres --subjects 25000 --metrics 3 --weeks 4 --concurrency 4

# one storage arm's gate configuration (Phase 929): azurite or postgres;
# Phase 962's postgres-history is the database arm's population read over 52 weeks
dotnet src/ToolUp.RAG.Benchmarks/bin/Release/net10.0/ToolUp.RAG.Benchmarks.dll load gate --arm postgres

# Phase 962: seed once and keep the table, then measure the same rows again
# (for example after building an index by hand) without re-seeding
dotnet src/ToolUp.RAG.Benchmarks/bin/Release/net10.0/ToolUp.RAG.Benchmarks.dll load facts `
    --store postgres --subjects 25000 --metrics 3 --weeks 52 --keep yes
dotnet src/ToolUp.RAG.Benchmarks/bin/Release/net10.0/ToolUp.RAG.Benchmarks.dll load facts `
    --store postgres --subjects 25000 --metrics 3 --weeks 52 --table <kept table> --scope <its scope>
```

The budget gate runs the gate configurations and decides them against the `load` block of
`perf-budgets.json`:

```powershell
pwsh ./dev-scripts/perf-budget-gate.ps1 -SkipServer -SkipClient   # the load half alone
pwsh ./dev-scripts/perf-budget-gate.ps1                            # all three halves, as CI runs it

# the load half plus its two storage arms, each decided against its own block (Phase 929)
pwsh ./dev-scripts/perf-budget-gate.ps1 -SkipServer -SkipClient -LoadArms azurite,postgres
```

The storage arms are **not** run by CI: the perf-budget job starts neither the emulator nor a
database, so they are off unless `-LoadArms` names them, and each is decided against a block of its
own (`loadAzurite`, `loadPostgres`, and since Phase 962 `loadPostgresHistory` for the `postgres-history`
arm) so the memory arm's ceilings never have to accommodate a network hop. The documented local invocation above is how they are held to their budgets.

### The arms

| Arm | What it is | Armed by |
|---|---|---|
| blob `memory` | a dictionary; a "read" is a lookup | always available |
| blob `disk` | the SDK's `LocalFileStorage` over a temp directory — real file I/O | always available |
| blob `azurite` | the Azure Blob companion against the local Azurite emulator | `TOOLUP_PARITY_AZURITE` |
| store `flat` | `InMemoryVectorStore` (exact scan) | always available |
| store `hnsw` | the HNSW companion | always available |
| store `pgvector` | the pgvector companion, HNSW index (m=16, ef_construction=64), built with plain `create`, a table of its own per run, dropped afterwards. Since Phase 939 `create` composes `PgvectorTuning.recommended`, so this arm now measures the tuned posture; the Phase 929 rows below predate that | `TOOLUP_PGVECTOR_CONNECTION_STRING` |
| fact store `blob` | `BlobFactStore`, the default `IFactStore`, over whichever blob arm is named | always available |
| fact store `postgres` | the database-backed fact store (`ToolUp.FactStores.Postgres`, Phase 888), a table of its own per run, settled with `VACUUM (ANALYZE)` after the seed and dropped afterwards unless `--keep yes` (Phase 962); its rows print `blob=none`, and a row more gives the rows the population read's summary touched, from its executed plan | `TOOLUP_PGVECTOR_CONNECTION_STRING` |

The emulator arms are free and local. Azurite comes up with the cloud-parity lane
(`docker compose -f compose.parity.yml up -d --wait`, then
`$env:TOOLUP_PARITY_AZURITE = "UseDevelopmentStorage=true"`); a local Postgres with the pgvector
extension (`docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=postgres pgvector/pgvector:pg17`)
arms `pgvector` and the `postgres` fact store — one local server and one variable serve both, and
the fact store needs no extension. **An arm that is named but not armed is an error, never a
fallback to memory** — a row labelled `azurite` that ran against a dictionary is the failure the
harness exists to prevent.

The `postgres` fact store is seeded through `AssertBatch`, not by writing rows in its format: on that
store a batch assert is an indexed write, linear in the seed, where on the blob store it would be
quadratic. A seed batch of 1,000 drafts touches more than 64 lineages, so it takes the scope's
exclusive lock and the batches run one at a time: about 4,000 facts a second on the machine below,
which is what bounds the sizes this arm can reach (Phase 962). The blob store is still seeded by writing its blobs directly, for the reason below.

### What every row says

Every printed row names its store, its blob arm, its corpus size, its concurrency and its
statistic. Latency is **total** latency — measured around `IRetrievalPipeline.Retrieve` by
`RagTelemetry.RetrievalMeter`, so embedding, dense search, BM25, fusion and merge are all inside it —
reported as p50 / p95 / p99 / max over every measured operation, plus the p95 of each round and the
minimum of those (the statistic the gate reads). Beside every clock is the **blob read count**,
because the count survives a change of backend and the clock does not (the Phase 702 lesson): on
object storage every read is a request.

Each round is a closed loop: N callers, each on its own thread-pool task, each taking the next
operation as soon as its last one returns. An operation that fails fails the run; nothing is ever
timed as a fast failure.

## The machine

Every number below was taken on one machine: Windows 11, Intel Core Ultra 9 386H, 16 logical
processors, 31.5 GB — Phase 886's on 2026-09-28, Phase 929's on 2026-09-29, with Azurite and
PostgreSQL 17 (the `pgvector/pgvector:pg17` image, default configuration) in Docker Desktop on the
same machine. **It was not a quiet machine on either day.** Six build-and-test sessions ran beside
it throughout (886: CPU load 90%+, between 2.5 and 13 GB free; 929: CPU load 88-100% for all but
two runs, between 7 and 16 GB free), so every clock here overstates the quiet cost, and run-to-run
spread is wide (the population read's gate p95 measured 87.15 / 13.40 / 32.54 ms across three 886
runs). Each Phase 929 row below names the load its run started under. The read counts are
unaffected: they are deterministic.

## Retrieval — measured

Local embedder (hashed, 512 dimensions), hybrid pipeline (dense + BM25), top-10, one scope.

| Store | Blob | Chunks | Callers | p50 ms | p95 ms | p99 ms | Throughput q/s | Blob reads / query | Recall@10 vs flat scan |
|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| flat | memory | 2,000 | 1 | 5.37 | 7.72 | 19.92 | 173 | 0 | — |
| flat | memory | 2,000 | 8 | 6.89 | 9.64 | 11.42 | 1,045 | 0 | — |
| flat | memory | 10,000 | 8 | 23.28 | 48.09 | 57.27 | 310 | 0 | 1.000 (50 queries) |
| flat | disk | 10,000 | 8 | 21.81 | 37.34 | 47.11 | 336 | 0 | — |
| flat | memory | 100,000 | 8 | 410.49 | 537.57 | 612.64 | 18.5 | 0 | 1.000 (20 queries) |
| hnsw | memory | 1,000 | 8 | 111.38 | 152.42 | 250.81 | 68 | 0 | 1.000 (20 queries) |
| hnsw | memory | 2,000 | 1 | 18.83 | 26.55 | 30.64 | 51 | 0 | — |
| hnsw | memory | 2,000 | 8 | 179.59 | 216.37 | 367.64 | 45 | 0 | — |
| hnsw | memory | 5,000 | 1 | 27.12 | 32.96 | 34.74 | 36 | 0 | 1.000 (10 queries) |

Phase 929 added the storage arms and the largest in-memory size this machine could hold. Each
row names the CPU load and free memory its run started under.

| Store | Blob | Chunks | Callers | p50 ms | p95 ms | p99 ms | Throughput q/s | Blob reads / query | Recall@10 vs flat scan | Load at start |
|---|---|---:|---:|---:|---:|---:|---:|---:|---|---|
| flat | memory | 10,000 | 8 | 30.71 | 55.76 | 81.79 | 236 | 0 | — | CPU 99%, 13.9 GB free |
| flat | azurite | 10,000 | 8 | 24.97 | 38.97 | 48.14 | 302 | 0 | — | CPU 88%, 13.4 GB free |
| flat | memory | 250,000 | 8 | 1,133.28 | 1,371.40 | 1,411.09 | 6.7 | 0 | — | CPU 94%, 7.0 GB free |
| pgvector | memory | 10,000 | 8 | 3.67 | 6.50 | 10.76 | 1,963 | 0 | 0.810 (50 queries) | CPU 93%, 13.2 GB free |
| pgvector | memory | 100,000 | 8 | 10.63 | 24.77 | 27.67 | 630 | 0 | 0.670 (20 queries) | CPU 94%, 15.2 GB free |

(The two 10,000-chunk flat rows are the second of three `load gate` runs of each arm; the three
azurite runs' round-minimum p95 was 40.03 / 28.35 / 37.81 ms against the memory arm's 41.50 /
37.42 / 40.49 ms. The 250,000-chunk run used 40 queries x 3 rounds; the pgvector 100,000 run 100 x 3.
The pgvector seed was one upsert per chunk: 64 s at 10,000 chunks, 1,002 s at 100,000. Since Phase 946 the harness writes each 512-chunk embedding batch through `IVectorStoreBatch` (`upsertBatch`, one round-trip): measured back to back on one loaded machine (CPU 100%, about 12 GB free), the 10,000-chunk seed fell from 113.9 s to 19.1 s, and the 100,000-chunk seed took 325.4 s. Both seeds include the local embedding and the in-process BM25 index.)

The two pgvector rows were measured before Phase 939, when `create` composed `PgvectorTuning.unchanged`:
pgvector's own untuned defaults (the server's default `ef_search`, no iterative scan, no exact fallback
on a short page). They are not the behaviour of today's `create`. Phase 939 made
`PgvectorTuning.recommended` the default (`ef_search` 100 per query, `relaxed_order` iterative scanning,
an exact re-run of a scope whose page comes back short, up to four scopes searched concurrently). The
companion README records what that posture measured: recall@10 of 0.964 to 1.000 across scopes of a
165,000-row table where the untuned default gave 0.05 to 0.52, and 1.000 over 50 queries on 5,000 rows
in Phase 939's own re-run. See [the migration note](../migrations/939-pgvector-tuned-default.md). The
harness's own 10,000 and 100,000-chunk cells have not been re-run under the tuned default, so this page
carries no latency or recall figure for them.

Four findings from Phase 929, each out of its scope to fix:

- **The azurite arm moves nothing on the retrieval path.** Its gate figure sits inside the memory
  arm's noise, because once the warm-up has loaded the scope's index the flat store and the BM25
  index answer from memory; the emulator is paid in the seed and the warm-up.
- **pgvector, at its untuned defaults, is the fastest store measured under concurrency, and its
  recall falls with size.** At
  10,000 chunks it answered at 3.67 ms p50 against the flat store's 30.71; at 100,000 at 24.77 ms p95
  against the flat store's 537.57 (886). But recall of its own search against the exact scan was
  0.810 at 10,000 chunks and 0.670 at 100,000 — the store's HNSW index at the companion's defaults
  (m=16, ef_construction=64, the server's default `ef_search`) trades a third of the true top 10 at
  100,000 chunks for that speed. Phase 939 has since made the tuned posture the default (above); this
  finding describes `PgvectorTuning.unchanged`, which a deployment now opts into.
- **pgvector's first rounds after a seed are an order of magnitude slower than its last.** In the
  three gate runs over 10,000 chunks, the p50 over all 1,000 queries was 51.24 / 49.84 / 3.59 ms
  while every run's fastest round had a p95 under 4.1 ms. A standalone run seconds apart measured
  3.67 ms p50. The gate reads the minimum over rounds, which is stable; a caller meeting a freshly
  seeded table meets the slow rounds.
- **The flat store's per-query cost is linear in the corpus, confirmed at a third point.** p95 at
  eight callers: 48 ms at 10,000 chunks (886), 538 ms at 100,000 (886), 1,371 ms at 250,000 —
  about 5.5 µs per chunk.

Phase 886's three findings, likewise out of scope:

- **The HNSW store's graph build is super-linear and far slower than its own header claims.** Wall
  time for a whole cell (seed, build, queries) was 20 s at 1,000 chunks, 51 s at 2,000 and 413 s at
  5,000; the seed itself took under a second, so nearly all of it is the one-time graph build the
  first search pays. A 10,000-chunk cell was stopped after more than twenty minutes without
  finishing its build. The store's header says a ~50k build is sub-second; at 512 dimensions on this
  machine it is not within three orders of magnitude of that.
- **The HNSW store serialises searches.** Its throughput does not rise with callers (51 q/s at one,
  45 q/s at eight, at 2,000 chunks) while its latency rises about eightfold; the flat store scales
  (173 → 1,045 q/s). Under concurrency, at every size measured here, the exact scan is the faster
  store.
- **Neither store reads a blob per query once warm.** On every arm, the flat store and the BM25
  index answer from memory after the scope's index is loaded, so the blob arm moves the cold load
  and the flushes, not the per-query path. The gate holds that at zero.

Recall of the HNSW store's own search (not the fused pipeline) against the exact flat scan was 1.000
at every size it could be built at. The flat store against itself is the probe that proves the ground
truth is computed correctly, and it reads 1.000 too.

## Retrieval — not measured, and why

| Cell | Status |
|---|---|
| flat, 500,000 chunks (Phase 14k's deferred p95) | **Attempted and stopped; extrapolated.** Phase 929 ran `load retrieval --stores flat --sizes 500000` (CPU 97%, 12.1 GB free at start). 172 s in, still seeding, the process's working set was 7.56 GB and the machine had 2.3 GB free; it was stopped before it could take the memory of the five sessions beside it. The 250,000-chunk run peaked at about 6.5 GB, so 500,000 needs about 13 GB — more than this machine had free on either day. On the slope measured at three sizes (about 5.5 µs per chunk), the p95 at eight callers is **~2.75 s**, the figure 886 extrapolated from two. The largest size measured is 250,000 chunks: **p95 1,371 ms**. |
| flat, 1,000,000 chunks | **Extrapolated** on the same slope: p95 near 5.5 s at eight callers, over ~26 GB of process memory at the 250,000-chunk run's density. Not run, for the reason above. |
| pgvector, 500,000 chunks | **Attempted and stopped (Phase 946).** With the batched seed the row was run (`load retrieval --stores pgvector --sizes 500000 --rounds 2 --recall 0`, CPU 100%, 12.5 GB free at start) under a watchdog that stops it at a 6 GB working set, to protect the other sessions on the machine. It was stopped 30.6 minutes in, still seeding, at 6.43 GB: the vectors live in the database, but the in-process BM25 index and the chunk texts the harness holds grow with the corpus. The row needs a machine with the memory to spare, or a database keyword index (`PostgresFullTextIndex`) in place of the in-process one. At pgvector's untuned defaults (before Phase 939) its p95 rose from 6.50 to 24.77 ms between 10,000 and 100,000 chunks, and its recall fell from 0.810 to 0.670. |
| hnsw, 10,000 and above | **Not measurable** on this machine within a working session: the graph build did not finish (see above). Phase 929 did not retry it: nothing in the store has changed since. |

## Facts — measured

`BlobFactStore` (the shipped `IFactStore`), memory blob arm, four callers. The store is seeded
directly in its own format — seeding through `Assert` is quadratic, because every assert
re-enumerates the scope — and the seed is proven readable through the store before anything is
timed. A point read asks for one subject's metric for the latest week
(`FactQuery.forSubjectMetric`); a population read ranks one metric's latest week, top 10; each assert
supersedes a latest-week fact of a distinct subject; a batch assert carries 50 such drafts.

| Facts in scope | Operation | p50 ms | p95 ms | Throughput ops/s | Blob reads / op |
|---:|---|---:|---:|---:|---:|
| 3,000 | point read (subject + metric) | 48.39 | 91.39 | 65 | 3,000 |
| 12,000 | point read (subject + metric) | 168.71 | 468.66 | 19 | 12,000 |
| 48,000 | point read (subject + metric) | 646.96 | 1,108.41 | 5.2 | 48,000 |
| 48,000 | point read by fact id | 0.08 | 1.63 | 9,150 | 1 |
| 3,000 | population read | 5.84 | 97.14 | 155 | 611 |
| 48,000 | population read | 69.22 | 909.01 | 17 | 12,011 |
| 3,000 | single assert | 63.89 | 99.83 | 51 | 3,009 |
| 12,000 | single assert | 194.47 | 280.99 | 20 | 12,049 |
| 48,000 | single assert | 786.07 | 1,158.41 | 4.7 | 48,009 |
| 48,000 | batch assert (50) | 836.80 | 1,008.42 | 4.3 | 48,428 |

(3,000 = 250 subjects, 12,000 = 1,000 subjects and 48,000 = 4,000 subjects, each x 3 metrics x
4 weeks. The 12,000 rows are from a gate run.)

The read counts say it plainly: **a subject-and-metric read and every assert read every fact in the
scope.** Only a read by content-addressed id is O(1). The population read is cheaper than a full
enumeration because the Phase 702 surface is consulted above its 512-head threshold, but its read
count still grows with the scope.

### Since Phase 890, and on the storage arms (Phase 929)

**The table above describes the store as 886 measured it, and it has since changed.** Phase 890 gave
`BlobFactStore` a point-read index, so a subject-and-metric read and an assert no longer read the
whole scope. Re-measured on 2026-09-29 by the gate configuration (1,000 subjects x 3 metrics x
4 weeks = 12,000 facts, four callers), the point read costs **480.96 blob reads** (was 12,000: the
first reads of a fresh scope build the index, amortised over the 100 measured reads) and an assert
**2** (was 12,049).

The gate figure is the p95 of one round, minimum over five, for three runs of each arm. The
database-backed store (`ToolUp.FactStores.Postgres`, Phase 888) reads no blob at all.

| Arm | Facts | Point read ms | Population read ms | Single assert ms | Batch assert (50) ms | Blob reads / point read | Load at start |
|---|---:|---|---|---|---|---:|---|
| blob store, memory | 12,000 | 55.35 / 55.86 / 51.70 | 17.93 / 23.90 / 17.61 | 82.24 / 68.16 / 73.35 | 99.48 / 104.85 / 94.13 | 480.96 | CPU 96-99%, 8.8-13.9 GB free |
| blob store, azurite | 3,000 | 1,719.36 / 1,613.64 / 1,672.12 | 741.11 / 833.83 / 992.47 | 1,952.48 / 1,936.76 / 1,793.30 | 5,772.83 / 2,450.52 / 3,092.75 | 240.92 | CPU 88-100%, 9.4-13.4 GB free |
| postgres | 12,000 | 2.56 / 2.77 / 2.42 | 12.94 / 10.13 / 8.59 | 6.57 / 7.36 / 6.27 | 19.84 / 19.86 / 19.07 | 0 | CPU 94-100%, 15.2-15.8 GB free |

(The azurite fact cell is 250 subjects, not 1,000: every blob read is an HTTP request to the
emulator, and a fresh scope's index build reads the whole scope, so the memory arm's cell would take
most of an hour per run. Its gate runs took 114-148 s each. On the emulator the cost of an assert is
its two prefix LISTS, not its two reads — a list over a 3,000-blob scope is a paged request of its
own.)

The database-backed store was also measured past the gate's size, seeded through `AssertBatch`:

| Facts | Subjects x metrics x weeks | Seed | Point read p50 / p95 ms | By id p50 / p95 ms | Population read p50 / p95 ms | Single assert p50 / p95 ms | Batch assert (50) p50 / p95 ms | Load at start |
|---:|---|---:|---|---|---|---|---|---|
| 300,000 | 25,000 x 3 x 4 | 50.9 s | 1.54 / 2.54 | 1.02 / 1.48 | 317.47 / 395.24 | 6.95 / 15.33 | 19.70 / 39.81 | CPU 3%, 15.1 GB free |
| 1,200,000 | 300,000 x 1 x 4 | 98.4 s | 1.23 / 2.00 | 0.85 / 1.10 | 2,801.01 / 3,460.91 | 8.39 / 15.90 | 20.87 / 30.70 | CPU 51%, 14.4 GB free |

**The point read, the read by id and both asserts do not grow with the scope** — 2.54 ms p95 at
300,000 facts and 2.00 at 1,200,000, against 3.70 at 12,000. **The population read does**: at
300,000 subjects it ranks one metric's latest week in 2.8 s at the median, about nine times the
25,000-subject figure for twelve times the subjects. That is the one operation on this store whose
cost is the population rather than the answer. Phase 940 made it cheaper, and it is still not
flat. See the next section.

### The population read on the database arm (Phase 940)

**Verdict: about three times faster at every size, and still linear in the population.** The
statistics are functions of the whole filtered population, so no per-query read can touch fewer
rows than the population. A flat read needs an aggregate maintained on every write, and that is
not built. The reasons are below.

Measured on 2026-09-30 by `load facts --store postgres --metrics 1 --weeks 4 --concurrency 4`
(20 operations x 5 rounds). The database was one local PostgreSQL 17 (`pgvector/pgvector:pg17`,
default configuration), on the same machine as the harness, with other work running. Each
population read ranks one metric's latest week, top 10, so the population is exactly the subject
count.

| Subjects (facts) | Phase 929 p50 / p95 ms | Before p50 / p95 ms | After p50 / p95 ms | Load at start (before / after) |
|---:|---|---|---|---|
| 25,000 (100,000) | 317.47 / 395.24 (25,000 x 3 x 4) | 250.52 / 391.20 | 81.59 / 245.75 | CPU 88% / 100%, 13.9 / 16.2 GB free |
| 100,000 (400,000) | — | 737.01 / 1,351.16 | 229.43 / 303.83 | CPU 96% / 22%, 11.4 / 16.9 GB free |
| 300,000 (1,200,000) | 2,801.01 / 3,460.91 | 1,875.44 / 2,232.92 | 659.28 / 858.41 | CPU 96% / 10%, 12.2 / 15.9 GB free |

"Before" is the tree at the start of Phase 940, re-measured beside "after" so the two share a
machine and a day. Phase 929's 25,000-subject cell had three metrics, so its table was three times
larger. From 25,000 to 300,000 subjects the p50 grew 7.5 times before and 8.1 times after.

**Where the time went, before.** Measured once at 300,000 subjects against a table of the same
shape, seeded in SQL, with one caller and a warm cache:

| Stage | ms |
|---|---:|
| The member read in the database (`EXPLAIN ANALYZE`: a sequential scan of 1,200,000 rows, 300,000 kept, 133,337 buffers) | 343 |
| That read plus the transfer and the materialisation into 300,000 members | 433-468 |
| The in-memory pipeline: statistics (172-217) plus a full sort for the ranking (441-477) | 684-696 |

So about two thirds of the time was spent in memory, most of it sorting 300,000 members to keep
10. The rest was the scan, and the scan is the part that cannot shrink. With four callers, one
read and pipeline took 3.8 s of wall time and the read alone took 1.2 s.

**The choice: statistics and top-k in SQL, not a subject-paged read.** A subject-paged read moves
the same rows in smaller pieces and still runs the same pipeline, so it bounds memory and not time.
The read now issues four statements in one repeatable-read snapshot:

1. a summary row;
2. the method mix;
3. the top k, `ORDER BY magnitude, fact_id COLLATE "C" LIMIT k`;
4. the payloads of those k facts.

A fifth statement runs only when needed: an exact `count(DISTINCT path)`, when the summary's
count of distinct path hashes is below the fact count.

Each statistic keeps the shared pipeline's arithmetic:

- `numeric` comparison is exact decimal comparison.
- The sum is exact in `numeric`. The mean is `PopulationStats`' own `total / decimal count`,
  computed in .NET.
- Freshness is the `Freshness.deriveAt` comparison, restated as `as_of >= t - window`.
- The top k use the ranking's tiebreak, and `PopulationRanking.rankMembers` re-ranks them in .NET.

Warm, at 300,000 subjects, the summary took 159-171 ms, and the method mix and the top k took about
90 ms each.

**Three cases stay in memory**, because SQL cannot compute them with identical decimal arithmetic.
In these cases the old member read runs unchanged:

- **A canonical-method selection when two methods are present.** The selection runs before the
  threshold and needs every member of a contested group. A probe inside the same snapshot finds
  them. A single-method population skips the selection, which is the shared function's own first
  branch.
- **A sum whose left-to-right `decimal` fold would round.** This applies when the sum of absolute
  values at the largest scale exceeds `decimal`'s mantissa. Example: twenty copies of
  `1.0000000000000000000000000001` fold to `20.000000000000000000000000001`, but the exact sum is
  `...002`, so the two means differ. The live test pack holds this case.
- **A freshness window where `asOf + window` could leave `DateTime`'s range.** In this case the
  shared derivation throws.

One detail is identical only up to decimal equality. Equal extremes at two scales, such as `12.0`
and `12.00`, are equal decimals. Which one the in-memory fold keeps depends on its enumeration
order, and neither store pins that order.

**Why it is not flat.** Every statistic (the subject count, the extremes, the mean, the histogram
and the method mix) is defined over the population the query selects. The query chooses the
`AsOf` instant, the period, the subject depth or prefix, and the threshold, so the population can
only be counted by reading it. The only flat design is an aggregate maintained on every write,
per (scope, metric, period), and it was not built, for three reasons:

- It cannot answer an `AsOf` replay, a threshold or a subject prefix. Those still need this read.
- A minimum or maximum cannot be maintained under supersession without a re-read.
- Every assert to one metric would update one hot row, which serialises writers the store
  deliberately keeps parallel.

That is a durable schema decision, and it is recorded as an open question rather than taken here.
Phase 962 measured both options at the stated depth; see the next section.

### A covering index against a maintained aggregate (Phase 962)

**Verdict: the covering index, keyed by the period's END.** It makes the population read cost what
the asked period's population costs, however much history each subject carries. At 100,000
subjects x 3 metrics x 52 weeks (15,600,000 facts) the read's median went from 16.3 s to 124 ms.
The write-maintained aggregate was measured too and not built: it answers no shipped caller's
question alone, and it serialises concurrent writers on one row.

Phase 940 measured one metric over 4 weeks, so its latest-week population was a quarter of the
table. At the stated scale (3 metrics x 52 weeks) one metric's latest week is 1/156 of the rows,
and the read did not use that. Its only index was `pop_idx (scope, metric, is_head)`, so every
statement scanned the table.

Measured on 2026-10-01 by `load facts --store postgres --metrics 3 --weeks 52 --concurrency 4`
(20 operations x 5 rounds). The database was one local PostgreSQL 17 (`pgvector/pgvector:pg17`,
default configuration) in Docker with a hard 6 GB memory limit, on the machine the gate section
names, with other sessions' builds beside it (CPU 11-93%, 5-13 GB free). Each seed was settled with
`VACUUM (ANALYZE)`, the state autovacuum reaches on a live table. "Before" is the Phase 940 schema.
"After" is the same table with the covering index built and `pop_idx` dropped, measured with
`--table` / `--scope` over the kept seed. Population read p50 / p95 in ms:

| Subjects (facts) | Table + indexes | Before | After | Speed-up (p50) | Rows the summary touched, after |
|---:|---:|---|---|---:|---:|
| 10,000 (1,560,000) | 2.4 GB | 577.12 / 776.76 | 19.70 / 22.93 | 29x | 10,000 |
| 25,000 (3,900,000) | 6.1 GB | 1,286.42 / 1,959.17 | 32.98 / 39.00 | 39x | 25,000 |
| 50,000 (7,800,000) | 12 GB | 9,533.79 / 10,536.70 | 77.06 / 83.67 | 124x | 50,000 |
| 100,000 (15,600,000) | 24 GB | 16,268.31 / 16,721.22 | 124.40 / 141.21 | 131x | 100,000 |
| 300,000 x 3 x **4** (3,600,000) | — | — | 386.65 / 520.37 | — | 300,000 |

**Before, the read grew with the TABLE; after, with the asked population.** Before, every summary
and top-k statement was a parallel sequential scan of the whole table: 174,552 buffers at 10,000
subjects, 867,876 at 50,000 and 1,732,289 at 100,000 (one caller, `EXPLAIN (ANALYZE, BUFFERS)`).
Past 50,000 subjects the table no longer fits the container's memory, so the scan reads from disk
and the curve bends upward: 9.5 s at 50,000 subjects against 1.3 s at 25,000. After, each statement
is an `Index Only Scan` of the asked week's heads with no heap fetch: 331, 800, 1,603 and 3,193
buffers at the four sizes, and exactly one row touched per subject. One caller's summary took 8, 20,
46 and 83 ms.

The last row isolates history depth. Its table has 300,000 subjects but only 4 weeks, so the
latest-week population is 300,000 in a table a quarter the size of the 100,000-subject cell. It was
seeded fresh on the final schema.

**The index the shard proposed, keyed by period START, does not help.** The period overlap is
`period_from < @to AND @from < period_to`. For the latest week the first conjunct holds for every
week, so an index led by `period_from_ticks` turns the predicate into a range over the metric's
whole history. Measured at 25,000 subjects: 40,668 buffers per statement against 432,326 for the
table scan, and 280 ms against 267 ms for one caller (the slice does not fit the database's buffer
cache any better than the table). In the harness the p50 was 1,318.90 ms against 1,286.42 ms, no
better than no index. Keyed by period END, the latest week is the narrow range `period_to > @from`.
The cost of that choice is symmetric: a question about an EARLY period ranges over every later
period. Every shipped caller asks either the latest period or no period at all (no period ranges
over the metric's heads with either key).

**What the index costs a write.** The index is about 247 bytes a row (385 MB at 1,560,000 rows,
3,855 MB at 15,600,000). It built in 3.9 s, 5.7 s, 21 s and 52 s at the four sizes. Each assert pays
one more index insert, and the measurements cannot separate that from noise:

| Subjects | Single assert p50 / p95, before → after | Batch assert (50) p50 / p95, before → after |
|---:|---|---|
| 25,000 | 48.90 / 54.76 → 49.69 / 53.00 | 76.27 / 94.45 → 92.33 / 103.65 |
| 50,000 | 51.90 / 59.97 → 51.18 / 56.80 | 123.09 / 154.59 → 119.44 / 156.00 |
| 100,000 | 51.68 / 56.78 → 51.55 / 53.83 | 124.92 / 144.43 → 112.49 / 130.00 |

A single assert is about 48 ms on this database whatever the indexes, because its commit flush
dominates it. The bulk figure is the seed: 10,000 subjects seeded in 307.8 s on the old schema and
332.9 s on the new one, 8% slower. `pop_idx` is redundant once the covering index exists. Every
statement that read it now plans on the new index, so the migration drops it.

**The aggregate, prototyped and measured.** A table of per-(scope, metric, hierarchy, period)
counts and sums of the current heads, maintained by statement-level triggers (one grouped upsert
per touched group per statement, the cheapest shape the design has). It holds no minimum or
maximum, which cannot be decremented under supersession without a re-read.

- **Its read:** one primary-key row, 0.05 ms and 5 buffers, at every size: flat, as 940 predicted.
- **Its write, one writer:** free. At 10,000 subjects with one caller, the single assert was
  48.67 ms against 48.54 ms without it, and the batch 61.19 against 62.76.
- **Its write, four writers on one metric's latest week:** the row is a lock held to each commit,
  so the writers queue. The single assert went from 51.18 / 56.80 to 148.84 / 324.06 at 50,000
  subjects, and from 48.90 / 54.76 to 153.76 / 291.19 at 25,000. The batch went from
  119.44 / 156.00 to 218.67 / 429.90. One (metric, period) then takes about one write per commit
  time (about 20 a second here), however many writers there are.
- **What it answers:** 940's list stands. It cannot answer an `AsOf` replay, a threshold, a subject
  prefix or depth, the extremes, or the freshness histogram (freshness is derived at the read's
  instant). It holds no members, so it cannot rank them either. No shipped caller can be answered by
  it alone:
  - `AnswerPlanner` and the `query_metric_population` tool rank the top k under thresholds,
    prefixes, depths and periods, and the tool also replays `AsOf`.
  - The fact browser pages a ranking (top k up to 1,000) at a depth and prefix.
  - The coverage tool and narrative are the nearest fit (no period, threshold or `AsOf`). But they
    read the method mix across competing methods and the freshness histogram, and they probe the
    top 10 for the disclosure gate.

  Every caller would therefore still run the per-query read, and the aggregate would add a
  hot-row write cost to buy nothing.

**Why the index, in numbers.** At 100,000 subjects the index takes the read from 16,268 ms to
124 ms at the median, costs about 8% of seed throughput, and leaves the per-assert cost
unchanged. The aggregate would take a stats-only read from 124 ms to 0.05 ms, but no shipped
caller makes a stats-only read. It would also triple the median cost of concurrent asserts to one
metric's latest period, and cap them at the commit rate. **The read is still linear in the
population** (about 1.2 µs per subject at the median with four callers, from 10,000 to 100,000), as
940 established. A flat read across subject counts is not on offer for any question the shipped
callers ask. What the index makes flat is the cost across **history**: the read is the same
whether each subject carries 4 weeks or 52.

**Two defects found on the way, both in the `AsOf` branch of the visible-set statement.** The branch
finds the predecessors of successors written after `t`. As an `IN` semi-join it was planned on the
table's statistics, and statistics taken before a table's supersessions turned it into a walk:

- a nested loop re-scanning every successor in the scope for each superseded row (91 ms of a 94 ms
  summary at 12,000 facts, and a 3.4 s p95 population read at four callers);
- or a walk of every successor in the scope;
- or a walk of every superseded row of the metric.

A fresh `ANALYZE` hid all three, which is why no settled benchmark had shown them. The branch is now
driven by construction. The successor ids are read once from the transaction-time range, and each
is fetched by primary key in a lateral sub-select the planner may not flatten. A read "as of now"
costs one empty index range: 3 buffers and 0.05 ms at 3,900,000 facts. Over a reused table with
stale statistics, the same 12,000-fact population read's p95 went from 3,478 ms to 117 ms. A live
test pins it: it seeds 400 subjects x 52 weeks, takes the statistics, revises 100 heads, and holds
the read to the asked week's 400 rows and an index-only scan. Against the old branch the same test
touched 500 rows and failed.

**Largest size reached, and why it stopped there.** The largest is 100,000 subjects x 3 x 52 =
15,600,000 facts, a 24 GB table. Memory did not stop it. A watchdog stopped the harness at a 6 GB
working set (it peaked at 0.41 GB), and the database container had a hard 6 GB limit it stayed
within. The limit was seed time. The batch seed takes the scope's exclusive lock (a batch of 1,000
touches more than 64 lineages), so it runs at about 4,000 facts a second, and the 100,000-subject
seed took 67 minutes. The stated 300,000 x 3 x 52 = 46,800,000 facts would take over three hours
and about 72 GB. On the measured slope the index's median there is about 360 ms. The 300,000 x 3 x 4
row measures that population directly: 386.65 ms at the median and 520.37 at p95, where Phase 940's
same population read 659.28 / 858.41 over a table a third the size. Before the change, every statement would scan a 72 GB
table from disk; on the measured curve that is about 50 s a read (extrapolated, not run).

The large cell is budgeted. `perf-budgets.json`'s `loadPostgresHistory` block gates the population
read over 2,500 subjects x 3 x 52 (390,000 facts), both as a clock and as the rows the summary
touches; see the gate section.

No budget was added to `perf-budgets.json`. The phase's condition for one was a flat result, and
the gate's 12,000-fact database cell already carries a population-read ceiling (`loadPostgres`).

## Facts — extrapolated to the stated scale

The stated scale is 300,000 subjects x several metrics x a year of weekly refreshes: at three
metrics and 52 weeks, **46,800,000 facts**, one blob each. It was not seeded — at the measured
per-fact cost it is not seedable in a working session on this machine, and not holdable in memory
beside the sessions it was shared with. What the measured slope says (point read p50 ~13.5 µs per
fact in scope at four callers, memory arm):

| At 46.8 M facts | Extrapolated |
|---|---|
| subject-and-metric point read | ~46.8 M blob reads and ~10 minutes per read on the memory arm |
| single assert | the same enumeration, per assert |
| point read by fact id | 1 blob read, unchanged |
| at 300,000 facts (one metric, one week — Phase 702's cardinality) | ~4 s per point read, 300,000 reads |

On object storage each of those reads is a request, so the memory-arm clock is the optimistic bound.
The count is the figure to trust.

These rows are 886's, over the store before Phase 890's point-read index; the point read and the
assert no longer enumerate (see above). The whole-scope paths that remain — the index build of a
fresh scope, the population surface, the full-history walk — still grow with the scope.

On the database-backed store the stated scale was not seeded either: 46.8 M rows through
`AssertBatch` at the measured 6,000-12,000 facts a second is one to two hours of seeding and tens of
gigabytes of table. What its measured rows say about that scale: the point read, the read by id and
the asserts were flat between 12,000 and 1,200,000 facts, so nothing measured suggests they move; the
population read grew about 9x for 12x the subjects, so at 300,000 subjects its ~2.8 s median is the
figure to plan with, whatever the history behind each subject. **Superseded by Phase 962:** without
an index the read did grow with the history (16.3 s at 100,000 subjects x 3 x 52). With the covering
index it does not: 386.65 ms at the median for a 300,000-subject population, measured, and about
360 ms on the measured slope at 52 weeks.

## The gate

`perf-budgets.json`'s `load` block budgets the gate configurations (flat store at 10,000 chunks with
eight callers; the fact store at 12,000 facts with four), with baselines from three runs on the
machine above and clock ceilings at about fourteen times their baselines — inside the twenty-times
rule `ToolUp.Platform.Build.Tests` enforces. The two read counts are deterministic, so their ceilings
are tight: retrieval blob reads per query at 0, fact blob reads per point read at 1.5x.

It was shown to fail by planting two regressions and running the load half of the gate: a 600 ms
sleep inside the retrieval path took `retrievalP95Ms` to 631 ms against a 450 ms ceiling, and a
second download of every blob in the fact store's enumeration took `factBlobReadsPerPointRead` to
24,000 against 18,000 — **while the point-read clock stayed inside its ceiling** (160 ms against
2,500), which is the case for gating the count. With both removed the gate was green again.

**The storage arms (Phase 929)** are budgeted in two further blocks of the same file, each from
three runs on the machine above and held to the same laws: `loadAzurite` (the `load` block's
retrieval cell with the blob arm on the emulator; the fact cell at 3,000 facts on the emulator) and
`loadPostgres` (the retrieval cell on the pgvector store; the `load` block's fact cell on the
database-backed store). Their clock ceilings sit about fourteen times above their baselines, except
the postgres population read's, which sits at 19.8x because its first run on a freshly started
container read 16x the baseline with its cache cold. The postgres arm's two blob-read counts are
ceilinged at 0: that arm reads no blob, and one that starts to has changed shape. The same phase
re-baselined the `load` block's four fact figures that Phase 890 had moved, so their ceilings again
sit about fourteen times above what the path costs rather than fifty.

**The database arm's large cell (Phase 962)** is budgeted in a fourth block, `loadPostgresHistory`:
the `postgres-history` arm runs the fact store alone over 2,500 subjects x 3 metrics x 52 weeks
(390,000 facts), so one metric's latest week is 1/156 of the table. Its population read is gated
twice: as a clock (baseline 6.95 ms, ceiling 97, about 14x) and as a COUNT, the table rows the read's
summary touches, taken from its executed plan (`PostgresFactStore.ExplainPopulation`; baseline 2,500,
the population; ceiling 1.5x). On the schema before Phase 962 the same cell read 134-147 ms. That
clears the clock ceiling by only about 1.4x, so the count is the decisive guard: the old table scan
touches 390,000 rows, 156x the population, and an index keyed by period start would touch 130,000.
Both asserts are budgeted there too, because they pay the index. Shown to fail by deciding a
measurement whose row count was planted at 390,000: the clock stayed inside its ceiling and the count
breached. Decided green by `pwsh ./dev-scripts/perf-budget-gate.ps1 -SkipServer -SkipClient -LoadArms
postgres-history` on 2026-10-01 (CPU 8-12%, 12-13 GB free), and the `postgres` arm stayed green
beside it.

All three of the earlier blocks were decided green by
`pwsh ./dev-scripts/perf-budget-gate.ps1 -SkipServer -SkipClient -LoadArms azurite,postgres` on
2026-09-29 (CPU 91%, 12.7 GB free at start).

No assertion in the ordinary test suite reads the wall clock; timing lives in the budget gate only.

## The metrics on the retrieval path

`RagTelemetry.RetrievalMeter` wraps any `IRetrievalPipeline`. It records every call's total latency
into `IRagTelemetry` under the `Total` pseudo-stage — so it appears in `/health/rag`'s
`RetrievalStageP50Ms` / `RetrievalStageP95Ms` beside the per-stage entries — and keeps an in-flight
gauge (`InFlight`, `PeakInFlight`, `Completed`). It rides the existing stage channel rather than a
new `IRagTelemetry` member because that interface has implementations outside this repository. The
harness composes it around every pipeline it measures; `RAGServerApp` does not compose it by
default yet.

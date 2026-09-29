# Retrieval and fact-store performance under concurrent load

Phase 886. What the retrieval pipeline and the fact store cost when many callers use them at once,
measured by the load harness in `src/ToolUp.RAG.Benchmarks`, with what each figure was measured
over and which figures are **extrapolated rather than measured**. Before this phase every scale
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
```

The budget gate runs the gate configurations and decides them against the `load` block of
`perf-budgets.json`:

```powershell
pwsh ./dev-scripts/perf-budget-gate.ps1 -SkipServer -SkipClient   # the load half alone
pwsh ./dev-scripts/perf-budget-gate.ps1                            # all three halves, as CI runs it
```

### The arms

| Arm | What it is | Armed by |
|---|---|---|
| blob `memory` | a dictionary; a "read" is a lookup | always available |
| blob `disk` | the SDK's `LocalFileStorage` over a temp directory — real file I/O | always available |
| blob `azurite` | the Azure Blob companion against the local Azurite emulator | `TOOLUP_PARITY_AZURITE` |
| store `flat` | `InMemoryVectorStore` (exact scan) | always available |
| store `hnsw` | the HNSW companion | always available |
| store `pgvector` | the pgvector companion, HNSW index (m=16, ef_construction=64) | `TOOLUP_PGVECTOR_CONNECTION_STRING` |

The emulator arms are free and local. Azurite comes up with the cloud-parity lane
(`docker compose -f compose.parity.yml up -d --wait`, then
`$env:TOOLUP_PARITY_AZURITE = "UseDevelopmentStorage=true"`); a local Postgres with the pgvector
extension (`docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=postgres pgvector/pgvector:pg17`)
arms `pgvector`. **An arm that is named but not armed is an error, never a fallback to memory** — a
row labelled `azurite` that ran against a dictionary is the failure the harness exists to prevent.
The fact store has no database arm yet: the shipped `IFactStore` is `BlobFactStore`, so its "real
backend" arm is object storage.

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

Every number below was taken on 2026-09-28 on one machine: Windows 11, Intel Core Ultra 9 386H,
16 logical processors, 31.5 GB. **It was not a quiet machine.** Six build-and-test sessions ran
beside it throughout (CPU load 90%+, between 2.5 and 13 GB free), so every clock here overstates the
quiet cost, and run-to-run spread is wide (the population read's gate p95 measured 87.15 / 13.40 /
32.54 ms across three runs). The read counts are unaffected: they are deterministic.

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

Three findings, each out of this phase's scope to fix:

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
| flat, 1,000,000 chunks | **Extrapolated.** The flat scan is linear: 10k → 100k raised p50 from 23 to 410 ms and cut throughput from 310 to 18.5 q/s. A further 10x puts p50 near 4 s and throughput near 2 q/s at eight callers, over ~2 GB of vectors held in memory. Not run: the machine could not spare the memory beside the sessions it was shared with. |
| flat, 500,000 chunks (Phase 14k's deferred p95) | **Extrapolated** on the same slope: p95 near 2.7 s at eight callers. `load retrieval --sizes 500000` measures it. |
| hnsw, 10,000 and above | **Not measurable** on this machine within a working session: the graph build did not finish (see above). |
| pgvector, any size | **Not measured.** The harness arms it; no local Postgres was running. |
| any store over `azurite` | **Not measured.** The harness arms it; the Docker daemon was not running. |

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

No assertion in the ordinary test suite reads the wall clock; timing lives in the budget gate only.

## The metrics on the retrieval path

`RagTelemetry.RetrievalMeter` wraps any `IRetrievalPipeline`. It records every call's total latency
into `IRagTelemetry` under the `Total` pseudo-stage — so it appears in `/health/rag`'s
`RetrievalStageP50Ms` / `RetrievalStageP95Ms` beside the per-stage entries — and keeps an in-flight
gauge (`InFlight`, `PeakInFlight`, `Completed`). It rides the existing stage channel rather than a
new `IRagTelemetry` member because that interface has implementations outside this repository. The
harness composes it around every pipeline it measures; `RAGServerApp` does not compose it by
default yet.

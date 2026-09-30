# ToolUp.VectorStores.Pgvector

PostgreSQL + [pgvector](https://github.com/pgvector/pgvector) `IVectorStore` for the `ToolUp.RAG` companion — the **external rung** of the retrieval scale story.

| Store | Ceiling | Durability | Replicas |
|---|---|---|---|
| `InMemoryVectorStore` (default) | ~50k chunks | blob snapshot | single process |
| `ToolUp.VectorStores.Hnsw` | ~1M chunks | blob snapshot | single process |
| **`ToolUp.VectorStores.Pgvector`** | database-bound | the database | **many** |

Corpus size is only half the reason to reach for this companion. The in-process stores each hold a private index and persist it asynchronously, so two replicas of one deployment do not see each other's ingests until a flush-and-reload cycle. Here the index *is* the database: every replica reads and writes the same rows, so retrieval is consistent across replicas with no per-process index state to reconcile.

Licensed under Apache-2.0. Npgsql is the only vendor dependency, and it stays inside this package (GP 1).

## Requirements

- PostgreSQL 13+ with the `vector` extension available on the server.
- An embedding dimensionality known at compose time — the column is `vector(N)` and `N` is a property of the embedding model, not something the companion can guess.

## Composition

```fsharp skip=fragment
open ToolUp.RAG.VectorStores.Pgvector

let options = { PgvectorOptions.forDimensions 1536 with Table = "toolup_rag_chunks" }
let store = PgvectorVectorStore.create connectionString options (Some logger)
```

The connection string comes from the deployment (`ISecretStore` / configuration) — the companion never reads environment variables or config files itself.

Register the readiness probe alongside it:

```fsharp skip=fragment
Health.create store
```

## Fail-loud posture

Every failure a misconfiguration can produce is raised as a `PgvectorStoreException` **at `create` time**, never at the first query inside a live request:

- unreachable database / bad credentials,
- missing `vector` extension (with the exact `CREATE EXTENSION vector;` an operator must run),
- missing table under `SchemaMode = VerifyOnly`,
- an option out of bounds (table name that is not a plain SQL identifier, dimensionality outside `[1, 16000]`).

`TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION` keeps the meaning it has for the in-tree stores: with it set, a row whose `metadata` column is not a decodable JSON object aborts the read rather than degrading to empty metadata.

An `Upsert` whose vector length does not match the column dimension is refused with a message naming both — the column dimension is fixed at migration time, so the honest options are to re-embed the corpus or compose a separate store per embedding model.

## Scope isolation (GP 4)

Scope is a first-class `scope` column and part of the composite primary key `(scope, chunk_id)`. **Every** statement the companion issues except the scope *enumeration* carries a `scope = @scope` predicate, and multi-scope search runs one scope-parameterised query per requested scope rather than a single `scope = ANY(...)`. There is no query shape that can read across scopes. `Sql.scopeBoundStatements` enumerates the set; the test pack asserts the predicate on every member, so a statement added later without it fails the build gate rather than shipping a leak.

## Schema

`SchemaMode = AutoMigrate` (the default) issues this idempotently at `create`. For a deployment whose application role has no DDL grant, provision it out of band and compose with `SchemaMode = VerifyOnly`:

```sql
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS toolup_rag_chunks (
    scope      text        NOT NULL,
    chunk_id   text        NOT NULL,
    content    text        NOT NULL,
    metadata   jsonb       NOT NULL DEFAULT '{}'::jsonb,
    embedding  vector(1536) NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT toolup_rag_chunks_pkey PRIMARY KEY (scope, chunk_id)
);

CREATE INDEX IF NOT EXISTS toolup_rag_chunks_scope_live_idx
    ON toolup_rag_chunks (scope) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS toolup_rag_chunks_scope_deleted_idx
    ON toolup_rag_chunks (scope, deleted_at);
```

The `deleted_at` column is the tombstone (Phase 14h soft-delete contract): `DeleteChunk` stamps it, `Search` and the default `ListChunks` filter on it, `RestoreChunk` clears it, and `Vacuum` deletes rows whose stamp predates the retention threshold. `ListChunks includeDeleted = true` projects it back onto chunk metadata as `_deletedAt`, so the contract-visible shape matches the in-tree stores exactly.

## ANN index

The default is `NoAnnIndex` — exact cosine scan, perfect recall, and fast enough below a few hundred thousand rows (GP 11: the default is the conservative behaviour). Opt in when the corpus warrants it:

```fsharp skip=fragment
let options = {
    PgvectorOptions.forDimensions 1536 with
        AnnIndex = HnswAnnIndex(16, 64)
}
```

`IvfFlatAnnIndex lists` is the alternative; it must be built *after* the table holds representative data, so provision it out of band rather than at first `create` on an empty table.

## Production posture — what `create` composes, and `PgvectorTuning`

One approximate index serves every scope in the table, and the `scope = @scope` filter is applied while the index is walked. A **small scope in a large shared table** is therefore the exposed case: the approximate scan can exhaust its candidate list before it has found `topK` rows from that scope, and return fewer than it could. A deployment that separates teams into many small scopes has many such scopes.

Since Phase 939, `create` and `createWithDataSource` compose `PgvectorTuning.recommended`, the posture measured below to return full, correct pages. Configuring an index is all a production table needs:

```fsharp skip=fragment
let options = {
    PgvectorOptions.forDimensions 1536 with
        AnnIndex = HnswAnnIndex(16, 64)
}

let store = PgvectorVectorStore.create connectionString options (Some logger)
```

`createTuned` / `createTunedWithDataSource` take a tuning explicitly. `PgvectorTuning.unchanged` is pgvector's own defaults with nothing added, the posture `create` used before Phase 939; pass it to keep that behaviour (see [the migration note](../../../docs/migrations/939-pgvector-tuned-default.md)). Under `NoAnnIndex`, the `forDimensions` default, every search is an exact scan whichever tuning is composed, so the only difference `create` makes there is that a multi-scope search runs up to four scopes concurrently.

With an approximate index configured, the search statement orders by distance **alone**, and the `(score, scope, chunkId)` total order is restored by re-sorting the returned page. Measured, the planner served the older two-key statement from the same index, through an Incremental Sort presorted on the distance, with identical recall, so the `IndexOrderedSearch` switch that chose between them saved only that sort step, and Phase 939 retired it. Under `NoAnnIndex` the statement keeps the total order in SQL.

### Recommended settings (`PgvectorTuning.recommended`)

| Setting | Recommended (what `create` uses) | `unchanged` | What it does |
|---|---|---|---|
| `AnnIndex` (options) | `HnswAnnIndex(16, 64)` | `NoAnnIndex` | Approximate index; exact scan without one. Set on the options, not the tuning: `create` does not configure one for you. |
| `SearchWidth` | `Some 100` | `None` | `hnsw.ef_search` (or `ivfflat.probes`) for each query, set with `set_config(…, true)` inside that query's own transaction — it ends at commit, so a pooled connection never carries one caller's width to the next. The same 100 serves both families; see [IVFFlat width](#ivfflat-width) for why. |
| `IterativeScan` | `true` | `false` | `relaxed_order` iterative scanning: pgvector keeps walking the index until the scope filter has yielded `topK` rows. Needs pgvector **0.8.0+**; the version is read at `create`, the setting is never sent to an older server, and the health probe reports it as requested-but-not-applied. |
| `ExactFallbackOnShortPage` | `true` | `false` | With an approximate index configured, a scope whose page comes back shorter than `topK` is re-run with `Sql.searchExact`, which materialises the scope's live rows before ranking them, so **no** approximate index can serve it. A short page means the scope holds fewer live chunks than `topK`, or the filter starved the approximate scan; the re-run makes a **full top-k a guarantee** rather than a tuning outcome. The two-key statement cannot play this part: it is index-served too (measured). |
| `MaxSearchConcurrency` | `4` | `1` | A multi-scope `Search` runs its per-scope queries concurrently under this bound — each holds one pooled connection while it runs. The answer is identical; only latency changes. |
| `ExactScanWarningRows` | `500_000` | `500_000` | The preflight validator warns when a table above this estimated row count has no approximate index. |

### Scope-respecting indexing — the choice and its ceiling

**One shared index, a filtered scan with a raised width, iterative scanning, and the exact short-page fallback** — decided by measurement (below), no longer provisional. It needs no per-scope DDL, keeps one schema for every deployment (including a `VerifyOnly` one whose role has no DDL grant), and its correctness — a full top-k whenever the scope holds that many live chunks — does not depend on any width being large enough.

The two alternatives were measured on the same corpus and are faster per query at pgvector's default width, but neither is adopted:

- **A partial HNSW index per scope** (`… WHERE scope = 'x'`): perfect recall at the default width and 0.2–0.7 ms per query. But it puts an index build on the path of every scope's creation, and a partial index is usable only when the planner sees the scope VALUE: under a generic plan (a client that prepares its statements, e.g. Npgsql auto-prepare) the planner falls back to the `(scope)` index and an exact sort, so the index silently stops being used.
- **Partitioning by scope** (one partition, with its own HNSW index, per scope): recall 0.99–1.00 at the default width and 0.2–1.4 ms per query, and runtime pruning keeps it correct under generic plans. But every new scope is a `CREATE TABLE … PARTITION OF`, the primary key and every index are per partition, and planning cost grows with the partition count: a generic plan over 214 partitions took 22 ms to plan against 0.4 ms to execute.

Its ceiling is cost, not correctness. The shared posture costs 1.8–2.6 ms per query on the scopes it routes to the index, where a per-scope index costs under 1 ms, and a starved page pays a second, exact query over its own rows (0.7 ms for a 2,000-chunk scope, 12 ms for 20,000, 69 ms for 100,000). With iterative scanning on, no page came back short in the measurement. Partitioning is the escalation for a deployment with few, large, long-lived scopes, where the per-scope DDL is affordable and sub-millisecond latency matters more than one schema.

### What has been measured

All figures below come from a **local container** (Docker Desktop on a Windows laptop), PostgreSQL 17.11 with pgvector 0.8.6 — the image `compose.parity.yml`'s `pgvector` service pins — on 2026-09-30. They describe that machine; a production server will differ in absolute latency, not in which plan it picks or what comes back short.

**Index choice** — [`bench/index-choice.sql`](bench/index-choice.sql), which builds the corpus and prints every figure here. 165,000 rows of 128-dimension vectors clustered around 200 shared topics, so every scope spans every topic; scopes of 100,000, 20,000, 10,000, 5,000, ten of 2,000 and two hundred of 50 rows; `hnsw(m = 16, ef_construction = 64)`; 50 queries, top-10, recall against the exact ranking; latency is server-side execution of one statement.

| Scope (share of table) | Planner's choice | Default width (`ef_search` 40): recall@10 · short pages | Recommended (`ef_search` 100, `relaxed_order`): recall@10 · short pages · mean ms |
|---|---|---|---|
| 100,000 (61 %) | HNSW index | 0.964 · 0/50 | 0.978 · 0/50 · 1.9 |
| 20,000 (12 %) | HNSW index | 0.520 · **50/50** | 0.972 · 0/50 · 1.8 |
| 10,000 (6 %) | HNSW index | 0.222 · **50/50** | 0.964 · 0/50 · 2.0 |
| 5,000 (3 %) | HNSW index | 0.090 · **50/50** | 0.972 · 0/50 · 2.6 |
| 2,000 (1.2 %) | HNSW index at the default width; `(scope)` index + exact sort under the recommended settings | 0.046 · **50/50** | 1.000 · 0/50 · 0.5 |
| 50 (0.03 %) | `(scope)` index + exact sort | 1.000 · 0/50 | 1.000 · 0/50 · 0.05 |

- **The exposed band is not the smallest scopes.** The planner answers a 50-row scope from the `(scope)` index with an exact sort. The starved scopes are the ones large enough for the planner to route them to the approximate index and too small to fill its candidate list — here 1–12 % of the table, every query short.
- **A raised width alone is not enough.** `ef_search = 400` without iterative scanning still returned 14 of 50 pages short for the 3 % scope (recall 0.900); iterative scanning closed every one at a quarter of that width.
- **The fallback's exact statement**, for comparison, costs 0.07 ms (50 rows), 0.7 ms (2,000), 2.9 ms (5,000), 12 ms (20,000) and 69 ms (100,000).

**The `ORDER BY` question — both `EXPLAIN` plans.** Phase 892 read the planner and concluded that the two-key `ORDER BY embedding <=> $q, chunk_id` could not be served from the approximate index, so search would be an exact scan. The plans refute it. For a scope the planner routes to the index, the two-key statement is

```text
Limit
  ->  Incremental Sort
        Sort Key: ((embedding <=> '[…]'::vector)), chunk_id
        Presorted Key: ((embedding <=> '[…]'::vector))
        ->  Index Scan using bench_embedding_hnsw_idx on bench
              Order By: (embedding <=> '[…]'::vector)
              Filter: ((deleted_at IS NULL) AND (scope = 'team:s5k'::text))
```

and the distance-only statement is the same index scan without the Incremental Sort, with identical recall at every width measured. So `search` is approximate once an index exists, and the store's short-page fallback, which used to re-run it, now runs `Sql.searchExact` instead: the scope's rows are materialised first and ranked after, and its plan never contains the approximate index (`CTE scoped -> Index Scan using …_scope_live_idx`, then `Sort`). The live arm asserts all three plans.

### IVFFlat width

`SearchWidth = Some 100` was measured for HNSW (`ef_search`). Under `IvfFlatAnnIndex` it sets `ivfflat.probes` to 100, which Phase 939 measured separately (same container and date as above): 100,000 rows of 32-dimension vectors clustered around 100 shared topics, scopes of 1,000, 5,000 and 12,000 rows spread across every topic, 30 queries per scope, top-10, the store's distance-only statement, one statement's round-trip.

| `lists`, index built | pgvector default (`probes` 1): recall@10 · short pages | `probes` 10 (√lists) + iterative: recall@10 | `probes` 100 + iterative: recall@10 · mean ms |
|---|---|---|---|
| 1,000, after the load | 0.08–0.24 · 8–30 of 30 | 0.92–0.97 | 1.000 · 5.3–6.8 |
| 100, after the load | 0.89–0.98 · 0–9 of 30 | 0.997–1.000 | 1.000 · 4.7–10.8 |
| 100, on the empty table (as `AutoMigrate` builds it) | 0.42–0.53 · 0–10 of 30 | 0.90–0.997 | 1.000 · 6.2–9.7 |

The default stays at 100 for both families. At 1,000 lists, 100 probes is a tenth of the lists and the plan still uses the index, at 1.000 recall where the √lists rule of thumb gives 0.92–0.97. Where `lists` is 100 or fewer, 100 probes reaches every list, and the planner stopped using the index: it answered from the `(scope)` index with an exact sort. That is exact, and costs what the fallback's exact statement costs (above), growing with the scope. Iterative scanning alone does not fix recall: at 1 probe it filled every page, but recall stayed at 0.28–0.86 for 1,000 lists. A deployment with many lists and a latency budget tighter than recall can lower the width with `{ PgvectorTuning.recommended with SearchWidth = Some 10 }`.

**The live arm** (`TOOLUP_PGVECTOR_CONNECTION_STRING` pointed at the `compose.parity.yml` service; 76 of 76 cases green) prints:

- **Reproduction** — 5,270 rows, 16 dimensions, `hnsw(16, 64)`, default width, no iterative scan, no fallback: a 20-chunk scope and a 250-chunk scope both returned a full top-10, each answered from the `(scope)` index with an exact sort. A table this small does not starve; the index-choice corpus above is where starvation shows.
- **Fallback** — the same shape with the planner steered to the index (`enable_sort = off` on the connection) and the fallback on: the page before the fallback held 4 of 10 rows, and the store returned the exact top-10 in order. With the fallback re-running the two-key statement, as it did before Phase 928, the case fails.
- **Recall and latency** — 5,000 × 32 dimensions, recommended settings: recall@10 = 1.000 over 50 queries; one `Search` call, client round-trip included (settings statement, search and commit on one connection), mean 3.8 ms, p95 4.8 ms.
- **Posture read** — `pgvector 0.8.6; … index: hnsw (m=16, ef_construction=64) [present]; search width: hnsw.ef_search = 100 (per query); iterative scan: relaxed_order (per query); …`.

**Phase 939 re-run** — the same image, served on a spare port, on 2026-09-30; 79 of 79 cases green:

- **`create`, red first** — `create` over `hnsw(16, 64)`, with the same 250-chunk scope in 5,250 rows and the same `enable_sort = off` steering as the fallback case. The page from pgvector's default posture held 4 of 10 rows. Before this phase, `create` returned exactly those 4. It now returns the exact top-10, in order.
- **Defaults** — `create` and `createWithDataSource` both report `PgvectorTuning.recommended`, a recorded extension version (0.8.6, so iterative scanning is applied) and `hnsw.ef_search = 100 (per query)`. Under `NoAnnIndex`, `create` sends no per-query settings.
- **Recall and latency** — the recommended case above measured recall@10 = 1.000, mean 5.8 ms, p95 6.8 ms on this run.

### Batched writes

The store also implements `IVectorStoreBatch`: `IVectorStore.upsertBatch store scope chunks` writes a whole batch as **one statement** whatever its size — rows as parallel arrays, every vector in one binary `real[]` parameter (no extra type-handler package). A chunk id repeated inside a batch keeps its LAST value, as sequential upserts would, and a vector of the wrong dimension refuses the whole batch before anything is written.

### Health and preflight

```fsharp skip=fragment
app
|> RAGServerApp.withHealthCheck (Health.create store)
|> RAGServerApp.withConfigValidator (Health.validator store)
```

The readiness probe reports `Degraded` — with a full posture line naming the extension version, the configured index and whether it exists on the table, the search width in force, and the iterative-scan state — when the store is running around something an operator should fix: iterative scanning requested but unsupported, a configured index absent from the table, or an extension upgraded since `create`. `Health.report store` returns the same line on demand. The validator warns (never aborts) when the table is above `ExactScanWarningRows` with no approximate index.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

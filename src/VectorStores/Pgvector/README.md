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

## Production posture — `createTuned` and `PgvectorTuning`

One approximate index serves every scope in the table, and the `scope = @scope` filter is applied while the index is walked. A **small scope in a large shared table** is therefore the exposed case: the approximate scan can exhaust its candidate list before it has found `topK` rows from that scope, and return fewer than it could. A deployment that separates teams into many small scopes has many such scopes.

`create` keeps the original behaviour exactly. For a production table, compose the tuned entry point instead:

```fsharp skip=fragment
let options = {
    PgvectorOptions.forDimensions 1536 with
        AnnIndex = HnswAnnIndex(16, 64)
}

let store = PgvectorVectorStore.createTuned connectionString options PgvectorTuning.recommended (Some logger)
```

### Recommended settings (`PgvectorTuning.recommended`)

| Setting | Recommended | `unchanged` (what `create` uses) | What it does |
|---|---|---|---|
| `AnnIndex` (options) | `HnswAnnIndex(16, 64)` | `NoAnnIndex` | Approximate index; exact scan without one. |
| `SearchWidth` | `Some 100` | `None` | `hnsw.ef_search` (or `ivfflat.probes`) for each query, set with `set_config(…, true)` inside that query's own transaction — it ends at commit, so a pooled connection never carries one caller's width to the next. |
| `IterativeScan` | `true` | `false` | `relaxed_order` iterative scanning: pgvector keeps walking the index until the scope filter has yielded `topK` rows. Needs pgvector **0.8.0+**; the version is read at `create`, the setting is never sent to an older server, and the health probe reports it as requested-but-not-applied. |
| `IndexOrderedSearch` | `true` | `false` | The search statement orders by distance **alone** — the only `ORDER BY` an ordering-operator index can serve — and the `(score, scope, chunkId)` total order is restored by re-sorting the returned page. The original statement's secondary `chunk_id` sort key cannot be served from the index. |
| `ExactFallbackOnShortPage` | `true` | `false` | A scope whose index-ordered page comes back shorter than `topK` is re-run with the exact statement. A short page means the scope holds fewer live chunks than `topK`, or the filter starved the approximate scan; the exact re-run is cheap in exactly that small-scope case (it reads the scope through its own `(scope)` index), and it makes a **full top-k a guarantee** rather than a tuning outcome. |
| `MaxSearchConcurrency` | `4` | `1` | A multi-scope `Search` runs its per-scope queries concurrently under this bound — each holds one pooled connection while it runs. The answer is identical; only latency changes. |
| `ExactScanWarningRows` | `500_000` | `500_000` | The preflight validator warns when a table above this estimated row count has no approximate index. |

### Scope-respecting indexing — the choice and its ceiling

The shipped choice — **provisional until measured** (see below) — is **one shared index, a filtered scan with a raised width, iterative scanning, and the exact short-page fallback**. It needs no per-scope DDL, keeps one schema for every deployment, and its correctness (a full top-k whenever the scope holds that many live chunks) does not depend on any width being large enough. Its ceiling is cost, not correctness: a scope that is starved pays a second, exact query over its own rows, so the fallback is cheap for small scopes and would be expensive for a large scope that is *also* starved — which the raised width and iterative scanning exist to make rare.

A partial index per scope and table partitioning by scope were considered and not adopted: both put per-scope DDL on the hot path of scope creation, and neither has yet been shown by measurement to beat the shipped posture. They remain the escalation if the fallback rate is measured to be high.

### What has been measured

Nothing on a live database yet for this posture: the build that introduced it had no PostgreSQL to measure against. The live test arm (`TOOLUP_PGVECTOR_CONNECTION_STRING`) carries the cases that produce the record, each printing its figure:

- **Reproduction** — a 20-chunk scope beside a 5,000-chunk scope, `hnsw(16, 64)`, default width, no iterative scan, no fallback: how many of a top-10 come back, with the query plan.
- **Full top-k** — the same table under the recommended tuning returns a full top-5 / 10 / 20 (asserted).
- **Index use** — `EXPLAIN` shows the HNSW index serving the distance-only statement and **not** the two-key one (asserted: the exact fallback depends on the second half).
- **Recall** — recall@10 against an exact in-memory ranking, 5,000 × 32-dim, recommended settings (asserted ≥ 0.9, printed).

Record the printed figures here when the arm is first run against a representative database.

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

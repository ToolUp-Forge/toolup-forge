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

```fsharp
open ToolUp.RAG.VectorStores.Pgvector

let options = { PgvectorOptions.forDimensions 1536 with Table = "toolup_rag_chunks" }
let store = PgvectorVectorStore.create connectionString options (Some logger)
```

The connection string comes from the deployment (`ISecretStore` / configuration) — the companion never reads environment variables or config files itself.

Register the readiness probe alongside it:

```fsharp
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

```fsharp
let options = {
    PgvectorOptions.forDimensions 1536 with
        AnnIndex = HnswAnnIndex(16, 64)
}
```

`IvfFlatAnnIndex lists` is the alternative; it must be built *after* the table holds representative data, so provision it out of band rather than at first `create` on an empty table.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

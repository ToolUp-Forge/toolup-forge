# Vector-store companions

The Platform's `IVectorStore` interface is the persistence layer under `ToolUp.RAG` — it stores embedded text chunks, searches them by cosine similarity, and owns the soft-delete / vacuum lifecycle. A deployment substitutes one implementation for another with a single composition line; nothing above the interface changes.

For the interface itself, the chunking layer and the retrieval pipeline, see [`rag/concepts.md`](../rag/concepts.md). For authoring a new one, see [`rag/extending.md`](../rag/extending.md).

## What's shipped — the scale story

| Store | Corpus ceiling | Durability | Replicas | Recall |
|---|---|---|---|---|
| `InMemoryVectorStore` (default, in `ToolUp.RAG.Server`) | ~50k chunks | blob snapshot | single process | exact |
| `ToolUp.VectorStores.Hnsw` | ~1M chunks / scope | blob snapshot | single process | approximate (~0.95+) |
| `ToolUp.VectorStores.Pgvector` | database-bound | the database | **many** | exact by default, approximate on opt-in |

The first two rungs are about corpus size. The third is about something else, and it is the reason to reach for it well before a million chunks: **both in-process stores hold their index in process memory** and persist it asynchronously. Two replicas of one deployment therefore each own a private index, and a chunk ingested on replica A is invisible to replica B until a flush-and-reload cycle. Several startup validators guard exactly this — they refuse a multi-replica configuration composed with a single-process store.

With Pgvector the index *is* the database. Every replica reads and writes the same rows, so retrieval is consistent across replicas with no per-process index state to reconcile, and a rolling restart loses nothing.

## Index locality — what each store declares (Phase 963)

The replica warning (`rag-in-process-index-replicas`) asks the composed store where its index lives, through the optional `IVectorStoreLocality` interface beside `IVectorStore`. It never looks at the store's type name.

| Store | Declares | Why |
|---|---|---|
| `InMemoryVectorStore` | `InProcess` | the index is the process's in-memory map, flushed to blob storage but searched from memory |
| `ToolUp.VectorStores.Hnsw` | `InProcess` | the graph persists to blob storage, but each replica builds and searches its own copy |
| `ToolUp.VectorStores.Pgvector` | `Shared` | every replica searches the one table |

Locality is a separate axis from `CompanionCapability.Readiness`. Readiness is a package-level posture joined across a composition. Locality is a fact about one composed instance. HNSW shows the difference: its data survives a restart, yet two replicas still answer from two graphs.

**A custom store** implements `IVectorStoreLocality` beside `IVectorStore` and returns `Some VectorIndexLocality.InProcess` or `Some VectorIndexLocality.Shared`. A store that declares nothing is read as not in-process, so the warning stays silent for it, as it did for any unrecognised store before (GP 11). An in-memory store that wants the warning has to declare it.

**A decorator** (telemetry, caching, a disclosure gate) does not move the index, so it forwards the declaration of the store it wraps:

```fsharp skip=fragment
type TracingVectorStore(inner: IVectorStore) =
    interface IVectorStoreLocality with
        member _.IndexLocality = VectorIndexLocality.declared inner

    interface IVectorStore with
        // ... forward each member to inner
```

The member returns an option because F# implements interfaces statically. A decorator answers the probe whatever it wraps, and `None` passes an undeclared inner store through as undeclared. A decorator that does not implement the interface reads as undeclared, whatever it wraps. The warning names the composed type, which is the decorator.

The keyword-index half of the same warning still recognises `InMemoryBM25Index` by a type test, so a decorator around that index is not seen as in-process.

## Picking a store

### `InMemoryVectorStore` — the default

Use when: a single instance, a corpus in the tens of thousands of chunks, and no operational appetite for another moving part. Nothing to compose — it is what `composeWithRAG` wires when you do not substitute.

Don't use when: search latency has become noticeable (a flat scan is 1M+ comparisons per query at 50k chunks), or the deployment runs more than one replica.

### `ToolUp.VectorStores.Hnsw`

Use when: one instance with a large corpus. An approximate-nearest-neighbour graph, orders of magnitude faster than the flat scan, at a small recall cost. Per-scope graph, so scope isolation is structural.

Don't use when: the deployment scales horizontally, or the corpus must survive without a warm-restart snapshot.

### `ToolUp.VectorStores.Pgvector`

Use when: more than one replica serves retrieval; the vectors must be durable in the same transactional store as the rest of the deployment's data; the corpus outgrows what fits comfortably in a process's working set; or an operator needs to inspect and manage the corpus with ordinary SQL.

Don't use when: the deployment has no PostgreSQL and adding one buys nothing — the in-process stores are genuinely simpler, and a deployment that does not compose this pays nothing for its existence (GP 13).

Requires PostgreSQL 13+ with the [pgvector](https://github.com/pgvector/pgvector) extension available on the server. Both are OSS, so this is not a paid-by-default dependency (GP 2); Npgsql is the only vendor package and it stays inside the companion (GP 1).

Setup:

```fsharp skip=fragment
open ToolUp.RAG.VectorStores.Pgvector

// `Dimensions` has no default — it is a property of the composed
// embedding model, and the column is `vector(N)`.
let options = PgvectorOptions.forDimensions 1536

let store = PgvectorVectorStore.create connectionString options (Some logger)
```

The connection string arrives from the deployment (`ISecretStore` / configuration) — like every companion, this one never reads environment variables or config files itself.

Register the readiness probe alongside it, so a replica that cannot reach the corpus is taken out of rotation rather than left to answer ungrounded:

```fsharp skip=fragment
Health.create store
```

## Fail-loud at `create`, not at first query

`PgvectorVectorStore.create` probes connectivity, the `vector` extension, and the schema before it returns a store. Every failure a misconfiguration can produce is raised there, as a `PgvectorStoreException` naming the operator action:

| Failure | What the message says |
|---|---|
| database unreachable / bad credentials | the store is not composed; check connection string, network, credentials |
| `vector` extension absent | run `CREATE EXTENSION vector;` as a superuser, then restart |
| table absent under `SchemaMode = VerifyOnly` | provision the DDL, or compose with `AutoMigrate` |
| table name that is not a plain SQL identifier | the name is interpolated into every statement, so only a plain identifier is accepted |
| dimensionality outside `[1, 16000]` | pgvector's own ceiling |

This matters more for an external store than an in-process one: a store that discovers its database is unreachable on the first retrieval of a live request has already accepted traffic it cannot serve.

`TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION` keeps the meaning it has for the in-tree stores. With it set, a row whose `metadata` column is not a decodable JSON object aborts the read rather than degrading to empty metadata — the compliance posture where stopping beats answering from a corpus you cannot fully read.

An `Upsert` whose vector length does not match the column dimension is refused with a message naming both. The column dimension is fixed at migration time, so the honest remedies are to re-embed the corpus with the composed provider or to compose a separate store per embedding model — silently truncating or padding would produce a ranked, meaningless score.

## Scope isolation is structural (GP 4)

Scope is a first-class `scope` column and part of the composite primary key `(scope, chunk_id)`. Every statement the companion issues except the scope *enumeration* carries a `scope = @scope` predicate, and a multi-scope search runs one scope-parameterised query per requested scope rather than a single `scope = ANY(...)` — which would move the guarantee inside an array parameter.

`Sql.scopeBoundStatements` enumerates that set, and the test pack asserts the predicate on every member. A statement added later without it fails the build gate rather than shipping a leak. This is the SQL twin of the HNSW store's per-scope graph: cross-scope leakage is impossible by construction, not by remembering to filter.

## Schema

`SchemaMode = AutoMigrate` (the default) issues the migration idempotently at `create`. Where the application role has no DDL grant, provision it out of band and compose with `SchemaMode = VerifyOnly`:

```sql
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS toolup_rag_chunks (
    scope      text         NOT NULL,
    chunk_id   text         NOT NULL,
    content    text         NOT NULL,
    metadata   jsonb        NOT NULL DEFAULT '{}'::jsonb,
    embedding  vector(1536) NOT NULL,
    deleted_at timestamptz  NULL,
    CONSTRAINT toolup_rag_chunks_pkey PRIMARY KEY (scope, chunk_id)
);

CREATE INDEX IF NOT EXISTS toolup_rag_chunks_scope_live_idx
    ON toolup_rag_chunks (scope) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS toolup_rag_chunks_scope_deleted_idx
    ON toolup_rag_chunks (scope, deleted_at);
```

`deleted_at` is the tombstone behind the soft-delete contract: `DeleteChunk` stamps it, `Search` and the default `ListChunks` filter on it, `RestoreChunk` clears it, and `Vacuum` deletes rows whose stamp predates the retention threshold. `ListChunks includeDeleted = true` projects it back onto chunk metadata as `_deletedAt`, so the contract-visible shape is identical to the in-tree stores'.

`metadata` is deliberately a flat `jsonb` object rather than an opaque blob, so an operator can query the corpus with ordinary SQL (`metadata ->> '_origin' = 'Document'`).

## ANN index

The default is exact cosine scan — perfect recall, and fast enough below a few hundred thousand rows. An approximate index is a deliberate opt-in (GP 11: the default is the conservative behaviour):

```fsharp skip=fragment
let options = {
    PgvectorOptions.forDimensions 1536 with
        AnnIndex = HnswAnnIndex(16, 64)
}
```

`IvfFlatAnnIndex lists` is the alternative. It must be built *after* the table holds representative data, so provision it out of band rather than at first `create` against an empty table.

## Production posture — `PgvectorTuning.recommended`, the default since Phase 939

`create` and `createWithDataSource` compose `PgvectorTuning.recommended`. A table that holds many scopes needs only an approximate index configured on the options:

```fsharp skip=fragment
open ToolUp.RAG.VectorStores.Pgvector

let options = {
    PgvectorOptions.forDimensions 1536 with
        AnnIndex = HnswAnnIndex(16, 64)
}

// The store owns the data source it builds from the connection string.
let store = PgvectorVectorStore.create connectionString options (Some logger)

// Or share a data source the deployment already owns; the store never disposes it.
let shared = PgvectorVectorStore.createWithDataSource dataSource options (Some logger)
```

Both run the fail-loud probe and migration, and read the installed extension version once, so iterative scanning is only ever sent to a server that supports it (pgvector 0.8.0 and later). The returned store also implements `IVectorStoreBatch`, so `IVectorStore.upsertBatch` writes a whole batch in one statement. Under `NoAnnIndex`, the `forDimensions` default, every search is exact whatever the tuning, and the one change the default makes is that a multi-scope search runs up to four scopes concurrently.

`createTuned` / `createTunedWithDataSource` take the tuning as one more argument, validated against the options before any I/O. Pass `PgvectorTuning.unchanged` (pgvector's own defaults, the posture `create` used before Phase 939) to keep the old behaviour; [`docs/migrations/939-pgvector-tuned-default.md`](../migrations/939-pgvector-tuned-default.md) says what changed and why.

Why it matters: one approximate index serves every scope in the table, and the scope filter is applied as the index is walked. Measured on a local container (pgvector 0.8.6, PostgreSQL 17), with pgvector's default search width, a scope holding 1–12 % of a 165,000-row table was routed to the index by the planner and came back short on **every** query: recall@10 fell to 0.05–0.52. `PgvectorTuning.recommended` held recall@10 at 0.96 or above with no short pages for every scope size measured, at under 3 ms per query server-side. It sets:

- a per-query search width (`hnsw.ef_search = 100`, or `ivfflat.probes = 100` under IVFFlat, measured separately), applied transaction-locally so a pooled connection never carries it to the next caller;
- `relaxed_order` iterative scanning, where the extension supports it;
- an exact re-run of any scope whose page comes back short of `topK`, through a statement no approximate index can serve — which makes a full top-k a guarantee rather than a tuning outcome;
- bounded concurrency (4) for a multi-scope search.

With an approximate index configured, the search orders by distance alone and the page is re-sorted into the total order; there is no longer a setting for it. To adjust the posture, start from `PgvectorTuning.recommended`, override single fields with `{ PgvectorTuning.recommended with … }`, and compose it through `createTuned`. The companion [README](../../src/VectorStores/Pgvector/README.md#production-posture--what-create-composes-and-pgvectortuning) carries the full settings table, the index-choice measurement behind the shared-index posture, and every recorded figure.

## Testing

The companion's test pack has two arms:

- a **structural arm** that always runs — it reads the scope-isolation guarantee off the generated SQL, exercises the `create`-time option guards, and round-trips the vector / metadata codecs, all without a database;
- a **live arm** gated on `TOOLUP_PGVECTOR_CONNECTION_STRING`, which runs the full `IVectorStore` contract, the scope-isolation cases, the shared deterministic-ordering contract, and the two-replica consistency case. It reports **Pending** when the variable is unset, so a fresh checkout is green without a database.

The live arm also carries the posture cases: the starvation reproduction, the `EXPLAIN` assertions that the fallback's exact statement is never served by the approximate index, the fallback repairing a starved page exactly, recall and latency at the recommended settings, and the batched write.

Its declared home is the `pgvector` service in `compose.parity.yml` (pinned to the image the recorded figures came from):

```powershell
docker compose -f compose.parity.yml up -d --wait pgvector
$env:TOOLUP_PGVECTOR_CONNECTION_STRING = "Host=localhost;Port=5433;Username=postgres;Database=postgres"
dotnet build src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter-test-list "PgvectorVectorStore"
```

Any other PostgreSQL with pgvector 0.8.0 or later works the same way.

Each live case provisions its own table and drops it afterwards, so the arm is re-runnable and two concurrent runs against one database do not interfere.

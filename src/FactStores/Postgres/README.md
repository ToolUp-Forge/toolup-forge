# ToolUp.FactStores.Postgres

A PostgreSQL-backed `IFactStore` for `ToolUp.Facts`. Facts are rows, and a
point read, a lineage-head lookup, a population read and a batch write are
each an indexed query — so the cost of a question scales with its answer, not
with the scope's history. Server-only; the Npgsql dependency stays in this
package (GP 1).

`BlobFactStore` remains the default. Reach for this companion when a scope
holds tens of thousands of facts, or when several replicas share the fact
base: the blob store reads the whole scope for a population read, an assert
and every whole-store walk, on every replica. With more than one replica
configured, the blob store's startup guard warns above 50,000 facts in one
scope and refuses above 300,000, naming this package.

## Requirements

- PostgreSQL 13 or later (any build; no extension is needed).
- A role with DDL rights on the target schema for `AutoMigrate`, or a table
  provisioned from the DDL below for `VerifyOnly`.

## Composition

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withStorage blob
|> FactsCompose.withFactStore
|> PostgresFactStoreCompose.withPostgresFactStore connectionString PostgresFactStoreOptions.defaults
|> ServerApp.run
```

`withPostgresFactStore` replaces the composed `IFactStore` through
`FactsCompose.withFactStoreImplementation`, so every registration the fact
tier builds over the store — the evidence source, the disclosure gate, the
resolver, the provenance graph, reactive recomputation, the fact tools —
follows it. Place it straight after `withFactStore`. The store is built on
first use with the composed `IEventStore`, the metric registry when one is
registered, and `DateTime.UtcNow` as the transaction-time clock.

To build one directly (a test, a tool, a shared pool):

```fsharp skip=fragment
let store =
    PostgresFactStore.createWithDataSource dataSource PostgresFactStoreOptions.defaults events (Some registry) (fun () -> DateTime.UtcNow)
```

`create` takes a connection string and owns the pooled `NpgsqlDataSource`
it builds; `createWithDataSource` uses a pool the caller owns. Both validate
the options and probe (or migrate) the schema before returning, and raise
`PostgresFactStoreException` naming the operator action on failure — never a
deferred failure on the first read of a live request.

| Option | Default | Meaning |
|---|---|---|
| `Table` | `toolup_facts` | Plain SQL identifier, at most 48 characters |
| `SchemaMode` | `AutoMigrate` | `AutoMigrate` issues the idempotent DDL; `VerifyOnly` refuses a missing table |
| `CommandTimeoutSeconds` | `0` | `0` inherits the data source's timeout |
| `MaxWriteAttempts` | `5` | Attempts for a write that loses a race to a concurrent writer |

## Schema

```sql
CREATE TABLE IF NOT EXISTS toolup_facts (
    scope text NOT NULL,
    fact_id text NOT NULL,
    hierarchy text NOT NULL,
    path text[] NOT NULL,
    metric text NOT NULL,
    period_from_ticks bigint NOT NULL,
    period_to_ticks bigint NOT NULL,
    method_identity text NOT NULL,
    lineage_hash text NOT NULL,
    as_of_ticks bigint NOT NULL,
    as_of timestamptz NOT NULL,
    supersedes text NULL,
    is_head boolean NOT NULL,
    magnitude numeric NULL,
    payload text NOT NULL,
    PRIMARY KEY (scope, fact_id)
);
CREATE INDEX IF NOT EXISTS toolup_facts_point_idx ON toolup_facts (scope, hierarchy, path, metric, period_from_ticks);
CREATE INDEX IF NOT EXISTS toolup_facts_lineage_idx ON toolup_facts (scope, lineage_hash);
CREATE UNIQUE INDEX IF NOT EXISTS toolup_facts_head_idx ON toolup_facts (scope, lineage_hash) WHERE is_head;
CREATE INDEX IF NOT EXISTS toolup_facts_pop_idx ON toolup_facts (scope, metric, is_head);
CREATE INDEX IF NOT EXISTS toolup_facts_succ_idx ON toolup_facts (scope, supersedes) WHERE supersedes IS NOT NULL;
CREATE INDEX IF NOT EXISTS toolup_facts_txtime_idx ON toolup_facts (scope, as_of_ticks);
```

`payload` is the fact exactly as `BlobFactStore` serialises it; every other
column is a projection of that payload, written in the same row. Valid and
transaction time are `DateTime` ticks, so the one-tick successor rule (`AsOf`
strictly increasing within a lineage) survives the round trip; `as_of` is a
readable copy. `magnitude` is the value's rankable magnitude
(`PopulationValue.comparable`), `NULL` for a shape that has none.

**Scope isolation (GP 4).** `scope` leads every key and is a predicate of
every statement; `Sql.scopeBoundStatements` enumerates the statement shapes
and the test pack asserts each one binds it.

## Semantics

- **Idempotent by constraint.** The content address is the primary key
  within a scope; a replayed assert writes nothing and audits nothing.
- **Supersession in one transaction.** The old head's `is_head` is cleared
  and the successor inserted together. The partial unique index keeps one
  current head per lineage; writers of one lineage serialise on advisory
  locks (a batch touching more than 64 lineages takes the scope's lock
  instead), and a writer that still loses a race rolls back and re-derives.
  Chains stay linear across replicas.
- **Batches are all-or-nothing.** A malformed batch is refused before any
  write, naming offenders by position (the `BlobFactStore` message); a
  storage failure rolls the whole batch back.
- **AsOf reads (law L4)** come from the same table: the current heads written
  by `t`, plus the predecessors of successors written after `t` (driven from
  the transaction-time index). No separate read model, so a head dated ahead
  of the reading clock is simply not yet visible.
- **Population reads** push down the subject set, the metric, the period,
  visibility, a single named method and — when no canonical selection can
  apply — the value threshold. Canonical selection, the ranking and every
  statistic run in memory through the shared `PopulationQueryTypes`
  functions, so the answer equals the blob store's by construction. Only the
  ranked top-k are read in full.
- **Audit (GP 6)** is the blob store's: `FactAsserted` (+ `FactSuperseded`)
  per fact on the scalar path, one `FactBatchAsserted` row per batch, under
  the reserved `_facts` source module, written after the commit.
- **Listing order.** A point read is ordered by (hierarchy, metric, period
  start) as the contract says; ties are broken by content address.

## Operating it

`ExplainQuery(scope, query)` returns the executed plan
(`EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`) of a point read's visible-heads
statement — which index answered, and how many rows it touched. At 300,000
subjects in one scope a subject-and-metric point read touched 5 rows, with
no sequential scan.

Migrating an existing blob store's facts into the table is not provided yet.

## Testing

The contract packs, the differential against `BlobFactStore`, the audit
shape, racing writers, the schema modes and the 300,000-subject read run
against a live database when `TOOLUP_TEST_POSTGRES` holds a connection
string:

```powershell
docker run -d --name toolup-pg -e POSTGRES_PASSWORD=<password> -p 5432:5432 postgres:17
$env:TOOLUP_TEST_POSTGRES = "Host=localhost;Port=5432;Username=postgres;Password=<password>;Database=postgres"
dotnet run --project src/ToolUp.Platform.Tests -- --filter-test-list "Phase 888"
```

Unset, the live arm reports one Pending case; the options, statement-shape,
scale-guard and composition tests run regardless.

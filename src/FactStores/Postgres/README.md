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
CREATE INDEX IF NOT EXISTS toolup_facts_population_idx ON toolup_facts (scope, metric, hierarchy, period_to_ticks)
    INCLUDE (period_from_ticks, as_of_ticks, magnitude, method_identity, path, fact_id) WHERE is_head;
DROP INDEX IF EXISTS toolup_facts_pop_idx;
CREATE INDEX IF NOT EXISTS toolup_facts_succ_idx ON toolup_facts (scope, supersedes) WHERE supersedes IS NOT NULL;
CREATE INDEX IF NOT EXISTS toolup_facts_txtime_idx ON toolup_facts (scope, as_of_ticks);
```

**The population index (Phase 962).** `toolup_facts_population_idx` covers the
population read: the current heads of one metric, keyed by the period's END so
the latest period is a narrow range, and carrying every column the summary, the
method mix and the top k read, so the read is an index-only scan of the asked
period's heads. It replaces Phase 888's `toolup_facts_pop_idx`
`(scope, metric, is_head)`, which `AutoMigrate` drops once the new index exists.

- **`VerifyOnly` checks only that the table exists**, not its indexes. A
  `VerifyOnly` deployment adds the population index out of band, then drops
  `toolup_facts_pop_idx`. Without it every population read scans the table.
  `ExplainPopulation(scope, query)` returns the executed plan of a population
  read's summary: an `Index Only Scan` on the population index is the check.
- **On a populated table, build it `CONCURRENTLY` first.** `AutoMigrate`'s plain
  `CREATE INDEX` holds a lock that blocks writes while it builds: 6 s for
  3,900,000 rows on the machine `docs/rag/performance.md` names, so about
  75 s at 46,800,000. `CREATE INDEX CONCURRENTLY` does not block writes. Run it
  under the same name before upgrading, and the migration's `IF NOT EXISTS`
  then finds it in place:

  ```sql
  CREATE INDEX CONCURRENTLY IF NOT EXISTS toolup_facts_population_idx ON toolup_facts (scope, metric, hierarchy, period_to_ticks)
      INCLUDE (period_from_ticks, as_of_ticks, magnitude, method_identity, path, fact_id) WHERE is_head;
  DROP INDEX CONCURRENTLY IF EXISTS toolup_facts_pop_idx;
  ```

  A `CONCURRENTLY` build that fails leaves an INVALID index of that name, and
  `IF NOT EXISTS` then skips it. Drop it and build again; `pg_index.indisvalid`
  says which state it is in.

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
  the transaction-time index by the statement's shape, not by the planner's
  statistics, since Phase 962). No separate read model, so a head dated ahead
  of the reading clock is simply not yet visible.
- **Population reads** push down the subject set, the metric, the period,
  visibility, a single named method and — when no canonical selection can
  apply — the value threshold. Since Phase 940 the statistics and the top k
  are computed in the database too, in one repeatable-read snapshot, so a
  summary row, the method mix and k members cross the wire instead of every
  member. Each statistic is computed with the shared pipeline's arithmetic:
  counts, the period bounds, the minimum and maximum (`numeric` comparison is
  exact decimal comparison), the freshness histogram (`as_of >= t - window`),
  the method mix (ordered ordinally in .NET) and the top k (by value, ties by
  content address, re-ranked in .NET by `PopulationRanking.rankMembers`). The
  sum is exact in `numeric`, and the mean is the same `total / count` division,
  taken in .NET. Three cases still read every member and run the shared
  `PopulationQueryTypes` functions in memory, because the database cannot
  compute them with identical decimal arithmetic:
  - a canonical-method selection with two methods present to choose between
    (the in-snapshot probe finds them);
  - a sum whose left-to-right `decimal` fold would round, where the sum of
    absolute values at the largest scale overflows `decimal`'s mantissa;
  - a freshness window reaching past `DateTime`'s range, where the shared
    derivation throws.

  Only the ranked top-k are read in full.
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
no sequential scan. `ExplainPopulation(scope, query)` does the same for a
population read's summary statement (Phase 962): on a correctly indexed table
it is an `Index Only Scan` on `toolup_facts_population_idx` touching one row per
subject of the asked period.

**The population read is linear in the population, not flat (Phase 940,
measured).** Measured on one local PostgreSQL 17 with four concurrent callers,
the same table and statement shapes the load harness runs. Each population is
`N x 1 metric x 4 weeks`, and the read ranks the latest week (top 10). Each
cell is the p50 / p95 in ms:

| Subjects | Before (member read) | After (aggregates in the database) |
|---:|---|---|
| 25,000 | 250.52 / 391.20 | 81.59 / 245.75 |
| 100,000 | 737.01 / 1,351.16 | 229.43 / 303.83 |
| 300,000 | 1,875.44 / 2,232.92 | 659.28 / 858.41 |

The read is about three times faster at every size, and it still grows with
the population. What remains is the database's own scan of the matching rows.
Every statistic is defined over the whole filtered population, at an `AsOf`
instant, a period, a subject prefix and a threshold the caller chooses. No
per-query read can therefore touch fewer rows than the population. A flat read
needs an aggregate maintained on every write, and this companion does not build
one. `docs/rag/performance.md` has the breakdown and the reasoning.

**History no longer costs the read anything (Phase 962, measured).** Phase 940's
cell had 4 weeks of history. At 3 metrics x 52 weeks, one metric's latest week is
1/156 of the table, and before the population index every statement scanned the
whole table. Population read p50 / p95 in ms, four concurrent callers:

| Subjects (facts) | Before | After |
|---:|---|---|
| 25,000 (3,900,000) | 1,286.42 / 1,959.17 | 32.98 / 39.00 |
| 100,000 (15,600,000) | 16,268.31 / 16,721.22 | 124.40 / 141.21 |

A 300,000-subject population read 386.65 / 520.37. The read still grows with the
population, at about 1.2 µs per subject. A write-maintained aggregate was
prototyped and measured. Its read was flat (0.05 ms), but no shipped caller's
question can be answered by it alone, and four concurrent writers to one metric's
period queued on its row, tripling their median assert. So it was not built. The
index costs about 247 bytes a row and about 8% of bulk-seed throughput, with no
measurable change per assert. The performance document has the full tables.

## Migrating from `BlobFactStore` (Phase 941)

`FactStoreMigration.migrate` moves a deployment's facts from its
`BlobFactStore` into the table without losing history, and proves the copy.

**Why not re-assert the facts.** `IFactStore.Assert` stamps a new transaction
time. Every `AsOf` read depends on each fact's original transaction time and
its supersession link (law L4), so a copy through `Assert` answers "what did
we know at `t`" differently from the source. The migration writes each row
raw instead, through the same row projection the store's own writes use:
original content address, transaction time, supersession link and head flag.

**What it does, per scope.**

1. Reads every fact blob (`BlobFactStore.ExportScope`). A blob that does not
   read, parse, or sit under its own content address refuses the scope and is
   named, unless `AllowUnreadableSource` says otherwise (the blob store's own
   reads skip such a blob, so accepting it preserves every read).
2. Checks the source is a fact base the table can hold: content addresses
   unique, every chain linear with strictly increasing transaction time, one
   current head per lineage. A violation refuses the scope, naming each fact.
3. Writes the facts in (transaction time, content address) order, a page per
   transaction (`PageSize`, default 1,000). Each page is staged with a binary
   `COPY` and inserted with `ON CONFLICT (scope, fact_id) DO NOTHING`, under
   the scope's exclusive write lock, and records its progress in
   `<table>_migration` in the same transaction.
4. Verifies source against target: every row's presence, payload,
   transaction time, supersession column, lineage hash and head flag; every
   lineage's chain as the target's columns walk it; and both stores' `AsOf`
   reads (current heads and history) at `AsOfSamples` of the source's
   transaction times, one tick before each, and the end of time. Each
   difference names its fact id.
5. Writes one `FactStoreMigrated` audit record to `IEventStore` under the
   reserved `_facts` source: source, target, counts and verification result.
   The per-fact `FactAsserted` history already lives in `IEventStore`, so no
   per-fact event is re-emitted.

**Resumable.** An interrupted run leaves its committed pages and their
progress row; the next run continues after the last committed page. A page
replayed from the start writes nothing, because the content address is the
primary key. A scope an earlier run verified over the same source facts (a
digest of their content addresses) is skipped.

**Where to run it.** It is a compose-time / operator entry point, not a
`toolup` CLI command: the CLI is a dependency-free host (pure BCL +
`FSharp.Core`), and the migration needs Npgsql and the fact tier, which live
in this package. Run it from a small console or script that references this
package and can build the deployment's blob backend:

```fsharp skip=fragment
let events: IEventStore = (* the deployment's event store *)
let source = BlobFactStore(blobStorage, events, registry, (fun () -> DateTime.UtcNow))
use target = PostgresFactStore.create connectionString PostgresFactStoreOptions.defaults events registry (fun () -> DateTime.UtcNow)
let! scopes = scopeEnumerator.ListScopes()
let! report = FactStoreMigration.migrate source target events FactStoreMigrationOptions.defaults scopes
printfn "%s" (FactStoreMigration.render report)
exit (FactStoreMigration.exitCode report)   // 0 only when every scope verified
```

Build both stores with the same metric registry (or both without one): the
verification compares their `AsOf` reads, and the registry decides the
canonical-method selection those reads apply. `FactStoreMigration.verify`
runs the same check without writing anything.

**The procedure.**

1. **Stop writers.** Every replica that asserts facts, including scheduled
   recomputation. The verification is a snapshot comparison, so a fact
   written to the blob store mid-run is simply not migrated.
2. **Migrate.** Run `migrate` over every scope. Re-run it until the exit code
   is `0`; a re-run continues where the last one stopped and skips verified
   scopes.
3. **Verify.** `migrate` verifies each scope it copies. Read the rendered
   report: a failed scope lists each difference by fact id. Run `verify` again
   immediately before the switch if any time has passed.
4. **Switch the composition.** Add `PostgresFactStoreCompose.withPostgresFactStore`
   straight after `FactsCompose.withFactStore`, deploy, and restart writers.
5. **Roll back** by removing that line and redeploying. The migration never
   writes to or deletes from the blob store, so the blob store is exactly as
   it was when writers stopped. Facts asserted after the switch are in the
   table only; before rolling back past them, decide whether to lose them or
   re-assert them against the blob store. To retry a migration from nothing,
   delete the scope's rows and its progress row:
   `DELETE FROM toolup_facts WHERE scope = '<scope>'` and
   `DELETE FROM toolup_facts_migration WHERE scope = '<scope>'`.

Memory: a scope is read whole, as the blob store's own population read and
assert already do; the rows are written a page at a time.

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

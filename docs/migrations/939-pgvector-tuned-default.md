# The pgvector store's `create` takes the tuned posture

**Ships in:** ToolUp.VectorStores.Pgvector (`PgvectorVectorStore.create`, `createWithDataSource`,
`PgvectorTuning`). Breaking, on the 0.24.1 draft: a record field is removed and `create` behaves
differently.

## What changes

`create` and `createWithDataSource` now compose `PgvectorTuning.recommended`, where they used to
compose `PgvectorTuning.unchanged`. They also read the installed `vector` extension version at
`create`, as `createTuned` always has, so iterative scanning is sent only to pgvector 0.8.0 or later.

What that does to a store depends on its options:

- **With an approximate index configured** (`AnnIndex = HnswAnnIndex …` or `IvfFlatAnnIndex …`), each
  search sets a width of 100 for its own transaction (`hnsw.ef_search` or `ivfflat.probes`), scans
  iteratively (`relaxed_order`), and re-runs a scope exactly when its page comes back short of `topK`.
  A multi-scope search runs up to four scopes concurrently.
- **Under `NoAnnIndex`**, the `PgvectorOptions.forDimensions` default, every search is already an
  exact scan. No per-query setting applies and the answers do not change. The one difference is that a
  multi-scope search runs up to four scopes concurrently, holding up to four pooled connections while
  it runs. That, and one extra `SELECT extversion` at `create`.

`PgvectorTuning.IndexOrderedSearch` is removed. With an approximate index configured, the search
statement always orders by distance alone and the store re-sorts the page into its total order. Under
`NoAnnIndex` the statement keeps the total order in SQL. Phase 928 measured, on pgvector 0.8.6 and
PostgreSQL 17, that the planner served the two-key statement from the same index through an
Incremental Sort, with identical recall. So the switch changed only that sort step, and nothing
depended on it: the short-page fallback runs `Sql.searchExact` whatever the statement shape.

`PgvectorTuning.unchanged` is still available. It means pgvector's own defaults with nothing added:
database-default width, no iterative scan, no fallback, sequential multi-scope search. It differs from
the pre-939 posture only in the statement shape above.

The I/O-free constructor (`new PgvectorVectorStore(dataSource, options, ownsDataSource, ?logger)`)
still composes `unchanged`. It reads nothing from the database, so it cannot know whether iterative
scanning is supported.

## Why this departs from GP 11

GP 11 keeps a deployment that composes nothing new on its old behaviour. This change does not,
deliberately. Measured on a 165,000-row table with an HNSW index at pgvector's defaults, scopes holding
about 1 to 12 percent of the table came back short on every query, with recall@10 of 0.05 to 0.52. The
old default returned short pages and wrong answers, and nothing reported it. Keeping it as the default
would preserve a defect, not a contract. The recommended posture returned no short pages and recall@10
of 0.964 to 1.000 on the same table. The companion README records both measurements, and the IVFFlat
one behind using the same width of 100 for that family.

## Who must change

A deployment that constructs a tuning in full (`{ SearchWidth = …; IterativeScan = …; … }`) drops the
field:

```diff
  let tuning = {
      SearchWidth = Some 200
      IterativeScan = true
-     IndexOrderedSearch = true
      ExactFallbackOnShortPage = true
      MaxSearchConcurrency = 8
      ExactScanWarningRows = 1_000_000L
  }
```

`{ PgvectorTuning.recommended with IndexOrderedSearch = … }` loses the `IndexOrderedSearch = …` line.
Code that only calls `create` or `createWithDataSource` compiles unchanged.

## Keeping the old posture

```diff
- let store = PgvectorVectorStore.create connectionString options (Some logger)
+ let store =
+     PgvectorVectorStore.createTuned connectionString options PgvectorTuning.unchanged (Some logger)
```

`createTunedWithDataSource dataSource options PgvectorTuning.unchanged logger` is the equivalent for
`createWithDataSource`. To keep only sequential multi-scope search, use
`{ PgvectorTuning.recommended with MaxSearchConcurrency = 1 }`.

## Verify

`Health.report store` names the posture in force. With an index configured it reads
`search width: hnsw.ef_search = 100 (per query); iterative scan: relaxed_order (per query); ordering:
index-ordered, page re-sorted; exact fallback on a short page: on; multi-scope concurrency: 4`. The
Platform pack's `PgvectorVectorStore` list runs the live cases when `TOOLUP_PGVECTOR_CONNECTION_STRING`
points at a server.

## Rollback

Compose `createTuned … PgvectorTuning.unchanged`, as above.

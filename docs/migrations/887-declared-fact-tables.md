# Phase 887 — declared fact tables and the writer seam

**What changes for you: nothing, unless you want it to.** A composition that declares no fact table
composes exactly as before. It registers no table registry and no preflight validator, and the
composition rule manifest and composition baselines are unchanged. The new surface is opt-in.

**What breaks, and for whom.** `ServerModule` gains a field (`FactTables`), and `ServerApp` gains
two (`RegisteredFactTables`, `FactTableBindings`). Code that builds either record through its
constructors (`ServerModule.create`, `ServerApp.empty`, `{ x with … }`) is unaffected. A
full-literal construction of either record, which is rare and unsupported, needs the new fields:

```diff
 {
     Name = "sales"
     …
     NotificationCategories = []
+    FactTables = []
 }
```

## Adopting it (a population producer)

Replace per-subject assertion with a declared table and one run per refresh:

```diff
 ServerModule.create "sales"
 |> ServerModule.declareMetrics [ revenue ]
 |> ServerModule.declareSubjects [ products ]
+|> ServerModule.declareFactTables [ skuSales ]
```

```diff
-let! _ = facts.AssertBatch(scopeId, drafts)          // one fact per subject per metric
+let! run = writer.OpenRun(scopeId, "sku-sales")
+let! _ = writer.WriteRows(scopeId, run.RunId, rows)  // in batches
+let! commit = writer.Commit(scopeId, run.RunId)      // one run, one audit record
```

And compose the writer, which binds every declared table:

```diff
 |> FactsCompose.withFactStore
+|> FactsCompose.withFactTableWriter
```

A module that asserts a handful of facts changes nothing. Declaration fields, preflight rules and
the default writer's behaviour: [`docs/platform/fact-tables.md`](../platform/fact-tables.md).

## Verification

- Startup logs `fact-tables: N table(s) declared [...]`, and the `fact-table-declarations` preflight
  passes. A `Required` table with no binding refuses to start, naming the table.
- After a commit, `writer.Status(scopeId, tableId)` reports `Fresh` and the commit's watermark, and
  `IFactStore.QueryPopulation` over a column's metric ranks the committed rows.

## Rollback

Remove `declareFactTables` and `withFactTableWriter`. Facts a committed table already wrote stay in
the fact store as ordinary facts under the table's own method, and nothing else reads them as a
table.

# Declared fact tables

A module that computes a quantity for a whole population — every product, every store, every
account — can declare the result as a **fact table**: one row per subject at one level of a
registered subject hierarchy, one column per registered metric. The table is part of the module's
output contract, exactly as its metrics and subject hierarchies are, and it is refreshed as **one
run with one commit** rather than as one assertion per subject per metric.

A module knows the table's *logical* identity (its declared id). The composition binds its
*physical* destination. The module writes through `IFactTableWriter`, and no connection string,
physical table name or storage type ever crosses that seam.

## Declaring a table

```fsharp skip=fragment
open ToolUp.Platform.Grounding

let skuSales: FactTableDefinition = {
    Id = "sku-sales"
    SchemaVersion = 1
    Hierarchy = "products"          // a registered SubjectDefinition id
    Level = "sku"                   // one of that hierarchy's Levels
    Columns = [
        FactTableDefinition.column "revenue" FactTableValueShape.Scalar
        { FactTableDefinition.column "segment" FactTableValueShape.Categorical with
            Disclosure = Some FactTableDisclosure.Internal }
    ]
    PeriodGrain = FactTablePeriodGrain.Month
    ProducingOperation = "sales-rollup"
    RefreshCadence = TimeSpan.FromDays 1.0
    HistoryMode = FactTableHistoryMode.Replace
    Disclosure = FactTableDisclosure.Surfaceable
    Requirement = FactTableRequirement.Required
}

ServerModule.create "sales"
|> ServerModule.declareMetrics [ revenue; segment ]
|> ServerModule.declareSubjects [ products ]
|> ServerModule.declareFactTables [ skuSales ]
```

`declareFactTables` composes left to right beside `declareMetrics` and `declareSubjects`. A module
that declares no table is unchanged: no registry, no preflight validator and no writer is composed.

| Field | Meaning |
|---|---|
| `Id` / `SchemaVersion` | The stable logical identity, and the declaration's version (recorded on every run). |
| `Hierarchy` / `Level` | Where the rows sit. A row's subject path has exactly as many members as the level's depth. |
| `Columns` | Registered metric ids, each with the `FactValue` shape its cells take. `Absent` is admitted in any column. |
| `PeriodGrain` | Declared metadata ("this table is monthly"); the commit checks each period is half-open and never re-buckets. |
| `ProducingOperation` | Becomes the method of every fact the table writes, so the table is its own lineage. |
| `RefreshCadence` | Drives staleness, and how long a run may stay open. |
| `HistoryMode` | `AppendByRun` — every run is a complete, attributable snapshot. `Replace` — a cell is rewritten only when it changed. |
| `Disclosure` | The default for every column; a column may declare its own. |
| `Requirement` | `Required` tables must be bound and must commit; `Optional` ones may not. |

## Binding it, and what the composition checks

The composition binds tables to a store. The facts companion's default writer binds every declared
table:

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withConfig { ServerConfig.defaults with FactStore = EnabledFactStore }
|> ServerApp.addModules [ salesModule ]
|> FactsCompose.withFactStore
|> FactsCompose.withFactTableWriter   // binds every table to the default writer
```

A composition that declares a table gets a structural preflight (`FactTablePreflight`, validator
`fact-table-declarations`), which runs even under `SkipPreflight`:

| Rule | Severity | Fires when |
|---|---|---|
| `fact-table-duplicate-id` | error | two modules declare one table id |
| `fact-table-malformed` | error | empty id or producing operation, no columns, a metric in two columns, non-positive cadence |
| `fact-table-unregistered-metric` | error | a column names a metric no module registers |
| `fact-table-unknown-subject-level` | error | the hierarchy is not registered, or the level is not one of its levels |
| `fact-table-unbound-required` | error | a `Required` table is bound to no store; the app refuses to start, naming the table |
| `fact-table-metric-two-homes` | warning | two tables carry one metric at one level, or a table carries a metric the composition also recomputes fact by fact (`Eager` / `OnQuery` recompute policy) |

These rules are a separate family from `CompositionValidator.rules` and are exported as their own
`ruleManifest`. That keeps the published composition rule manifest, and every composition that
declares no table, exactly as they were.

**One home per metric and level.** A metric lives in a fact table or in individual assertions, not
both, or the competing-methods rules become confusing. The two-homes warning checks what the
composition can see: nothing declares ad-hoc `IFactStore.Assert` calls, so a handler asserting the
same metric at the same level by hand is not visible at compose time.

## Writing a run

```fsharp skip=fragment
let writer = services.GetRequiredService<IFactTableWriter>()

let! run = writer.OpenRun(scopeId, "sku-sales")            // Result<FactTableRunRecord, _>
for batch in rows |> List.chunkBySize 5_000 do
    let! _ = writer.WriteRows(scopeId, run.RunId, batch)    // staged; nothing visible yet
    ()
let! commit = writer.Commit(scopeId, run.RunId)             // Result<FactTableCommit, _>
```

The commit is the one act a refresh is:

- It **validates every row** against the declaration: the subject path sits at the declared level
  and has no empty member, no subject-and-period key appears twice, the period is half-open, every
  declared column has a value of its declared shape, and no undeclared column appears.
- On **any** failure it rejects the whole run and writes nothing. The refusal,
  `FactTableRowsRejected`, names each bad row by position and subject.
- On success it swaps the table's current run, mints the **watermark** (table, a sequence strictly
  increasing per table and scope, the commit time, a content digest), computes the **change
  summary** against the previous run, and writes **one audit record** for the run.

The change summary counts rows as new, changed, unchanged or removed, and lists the scalar cells
that moved most. A run can also be abandoned. `Runs` and `Status` report the history, the
watermark, freshness against the cadence, and the latest run's outcome:

| Outcome | When |
|---|---|
| `Succeeded` | committed |
| `InProgress` | open and within the cadence |
| `Failed` | a `Required` table's run that was rejected, abandoned, or left open past the cadence |
| `Discarded` | the same, for an `Optional` table |

A table whose last commit is older than its cadence reads as `Stale`: a missed refresh shows up
through the cadence, not as silence.

## The default writer

`DefaultFactTableWriter` writes each cell as an ordinary fact through the composed `IFactStore`, so
every point and population read (and the AI tools over them) answers over a committed table with
no extra wiring:

- **Method:** `Computed(producingOperation, "v<schemaVersion>", tableId)`. **Evidence:** the run's
  watermark. **Disclosure:** the column's.
- **`Replace`:** the content address carries the value, so an unchanged cell is an idempotent skip
  and re-running an unchanged population writes nothing. **`AppendByRun`:** the watermark is part of
  the address too, so every run writes every cell and names itself.
- **Removed rows:** a row the new run no longer carries is superseded by an `Absent` fact per cell,
  so the current population is exactly the latest run.
- **Audit:** the trail holds one `FactTableRunCommitted` (or `…Rejected` / `…Abandoned`) record per
  run under the `_facts` source. The facts themselves go through one `AssertBatch`, whose single
  summarised receipt row the run record cites by digest. Nothing is audited per row.
- **Atomicity:** validation is all-or-nothing and runs before the first write. The table's current
  run (head, snapshot and summary) moves in one write after the batch succeeds. A storage failure
  part-way through the batch leaves the head where it was and the run open, and re-committing is
  exact because facts are content-addressed. A deployment that wants one physical transaction
  composes a table store behind the same seam.
- **Size:** correct at any size, and efficient at small. The cost is linear in the run plus the
  previous run's snapshot.

## Portability (GP 12)

`IFactTableWriter` is identity-by-value (string ids, record values), async throughout, and reports
every failure as a `FactTableWriteError` case. It keeps no state between calls: staged rows and run
records live in the backing store. Ordering holds only within one (scope, table). Times are at
second precision. The contract pack `IFactTableWriterContract.tests` holds any implementation to
the same bar.

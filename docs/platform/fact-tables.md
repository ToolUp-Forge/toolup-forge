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

A composition that declares a table, or binds one by name, gets a structural preflight
(`FactTablePreflight`, validator `fact-table-declarations`), which runs even under `SkipPreflight`:

| Rule | Severity | Fires when |
|---|---|---|
| `fact-table-duplicate-id` | error | two modules declare one table id |
| `fact-table-malformed` | error | empty id or producing operation, no columns, a metric in two columns, non-positive cadence |
| `fact-table-unregistered-metric` | error | a column names a metric no module registers |
| `fact-table-unknown-subject-level` | error | the hierarchy is not registered, or the level is not one of its levels |
| `fact-table-unbound-required` | error | a `Required` table is bound to no store; the app refuses to start, naming the table |
| `fact-table-metric-two-homes` | warning | two tables carry one metric at one level, or a table carries a metric the composition also recomputes fact by fact (`Eager` / `OnQuery` recompute policy) |
| `fact-table-binding-undeclared` | error | a by-name binding (`ServerApp.bindFactTables`, or `FactsCompose.withDelegateFacts`) names a table no module declares |

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

Where a run's facts come from is part of the contract too (Phase 938). `OpenRun` takes an optional
`FactTableRunProvenance`. With no argument, or with `ComputedRun`, the run writes the table's own
lineage. With `ImportedRun`, each origin's rows are written `Imported`; team publication opens its
runs this way. Every writer honours the provenance, and every decorator passes it through untouched.
`IFactTableWriterContract.provenanceTests` and `IFactTableWriterContract.decoratorTests` check both.
See [the migration note](../migrations/938-run-provenance-in-the-fact-table-writer.md).

## Run inputs, period slices, and one open run per table (Phase 994)

`OpenRun` also takes `FactTableRunOptions`, so a producer that publishes a daily delivery writes
one run for that day:

- **Inputs.** The content hashes of the files the run was computed from, as `sha256:<hex>` (any
  `<algorithm>:<lowercase hex>`). Every fact the run commits names them in `Evidence.InputHashes`,
  so a number reaches its upload in one hop. `query_facts` returns them as `inputs`, and the browse
  surface's fact detail lists them. Under `Replace`, a cell re-derived from a new input is
  re-asserted naming it, even when its value did not move.
- **Period slice.** The half-open period the run replaces. Rows outside it are rejected with the
  run, the commit withdraws only committed rows inside it, and every other row stands without being
  rewritten. The change summary counts the slice only, and the watermark digests the whole table
  after the commit. A committed row the slice would split refuses the run.
- **One open run.** While a run of a table is open in a scope and within the table's refresh
  cadence, a second `OpenRun` is refused with `FactTableRunInProgress`, naming the open run.

Every writer and decorator honours the options or refuses them with `FactTableRunOptionsRefused`.
The default writer honours them over any fact store. The delegate writer refuses them, because it
mints a table's rows under its current run. `IFactTableWriterContract.runOptionsTests` and
`exclusivityTests` check them. See [the migration note](../migrations/994-fact-run-inputs-and-periods.md).

**Decision — a concurrent run is refused, not queued.** A queue would have the writer hold a
producer's call until another run ends. That is state between calls, and a wait no portability rule
lets an implementation promise. A refusal is data the producer can act on: it names the open run, so
the producer can retry, or abandon that run if its producer died. A run left open past the table's
cadence no longer holds the table, so a crashed producer cannot hold it for good. If that stale run
later commits, the base-sequence check refuses whichever of the two commits second. The claim is a
blob written before the run's record. On a store with conditional writes (`IConditionalBlobStorage`)
it is atomic, so concurrent opens admit exactly one run. Elsewhere it holds within the store's
read-then-write precision, and the commit's sequence check is the backstop. A claim is never
released. The run's terminal record ends it, so ending a run needs no second write that could race a
new claim.

## Delegate facts — a population held as a pointer to a table

The default writer turns every cell into a fact. For a large population that is one stored fact,
one blob and one index entry per subject per metric, most of which no answer will ever quote. A
table can instead be held as **delegate facts**: the table is kept as rows, and the fact tier holds
one record per column that points at it.

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withConfig { ServerConfig.defaults with FactStore = EnabledFactStore }
|> ServerApp.addModules [ salesModule ]
|> FactsCompose.withFactStore
|> FactsCompose.withFactTableWriter          // every other table, as facts
|> FactsCompose.withDelegateFacts [ "sku-sales" ]
```

`withDelegateFacts` binds each named table to the `delegate-table` destination and decorates the
composed `IFactStore` and `IFactTableWriter`. The module still writes through `IFactTableWriter`
exactly as above. The fact tools, the answer planner, the disclosure gate and the coverage
narrative resolve the same `IFactStore` they always did. A composition that names no table is
unchanged.

**The record.** Each (table, column) pair becomes a `DelegateFact`: the metric, the table id, a
source reference, the declared query spec, the table's current watermark, the method, the
disclosure class and the history fidelity. A commit asserts the record again, and the new one
supersedes the last, so the record's supersession chain is the table's run history.

**Only what is quoted becomes a fact.** A read of a delegated metric is pushed down to the table,
and the table answers only these questions:

| Read | Pushed down as | Mints |
|---|---|---|
| point read (`Query`) of a subject at the table's level | `DelegateTableRead.Point` | that subject's rows |
| population read (`QueryPopulation`) at the table's level, or at no level | `DelegateTableRead.Ranked` | the ranking, at most `PopulationQuery.MaxTopK` rows |
| coherence totals (see below) | `DelegateTableRead.ChildTotals` | nothing |

Every returned row becomes an ordinary fact. Its evidence names the run by watermark, and the
watermark is one of its input hashes. For an append-by-run table this is exactly the fact the
default writer would have written, with the same id. A repeated read of an unchanged table is an
idempotent re-assertion. The summary statistics describe the whole matched population, and a
subject that nobody asked about has no fact. A read that names no subject ("every fact for this
metric") is not a point read. It reaches the underlying store, which holds what was quoted.

**Reads by an imported method (Phase 964).** A run opened with imported provenance mints the rows
under each origin's root member as `Imported <certificate>`, and the table records each certificate
it imported under. A read naming that method is delegated like one naming the delegate's own
method: a point read mints the subject's rows and answers from its imported lineage, and a
population read with `OneMethod (Imported c)` ranks only the rows under the root member of the
run's origin for `c`, narrowing any path prefix the caller gave to it. A certificate the table never
recorded is not delegated, and a run with no origin for a recorded certificate answers empty. One
read is still refused, by name, on the population error channel: a run that imports one certificate
under several root members, because a single ranked read answers from one subtree and merging
rankings would also have to merge their statistics.

**No free-form query.** The query spec is a column mapping declared at composition: the subject is
the row's path at the table's level, the period is the row's period, and the value is the column's
cell. A caller supplies only what the tool parameters already carry (metric, subject, period,
ordering, count), and those arrive as a typed `DelegateTableRead`.

**History.**

- An **append-by-run** table keeps every run, so its history is `Exact`. An `AsOf` read is answered
  from the run that was current at that time. A value from a run that is no longer current is not
  asserted, because asserting it now would make it the current head of its lineage. It is recorded
  beside the log as a reconstruction instead, which `Get` and the disclosure gate resolve like any
  fact.
- A **replace** table keeps only its latest run, so its history is `Approximate`. An `AsOf` read is
  **refused**. The population read returns the refusal on its error channel. The point read has no
  error channel, so it returns one `Absent` fact whose reason is the refusal. Neither read
  approximates.

**Refresh.** A commit advances the delegate records. It then finds the facts quoted from earlier
runs, using the delegated form of the reactive-recomputation invalidation walk, and re-reads each
one from the new run. A changed row's
fact is superseded by its new value, and a removed row's fact is superseded by `Absent`. The read
path enforces the same rule: before it answers, it mints the current row and retires a removed
one. So a fact from a run that is no longer current is never served as a current head, even if a
refresh fails.

**Whole-store walks.**

- **Coherence checking** removes delegate-owned facts from the fact list it would otherwise compare.
  A quoted sample would read as a partial load. For each delegated additive metric it asks the table
  instead: per-parent totals when the table holds the children, and point reads of the named
  parents when the table holds the parents.
- **Invalidation** narrows its read to the delegated metric and method (the facts that were quoted)
  and compares each one's run against the table's current watermark.

Neither walk mints a fact or reads the population into the fact tier.

**Disclosure.** A minted fact is born with the column's class, or the table's class where the
column declares none, so the gate judges it like any other fact.

**Coverage narrative.** A delegated metric's narrative is built from the declaration and the last
run's reach: row count, distinct subjects, comparable cells and period range. The table records all
of these at commit, so the narrative reads no row and mints nothing. It therefore cites no fact.
Its posture comes from the class: a surfaceable column is described, and an internal or restricted
column is reported as restricted.

**Writer.** The delegate writer has the default writer's run lifecycle, validation, watermark,
change summary and single audit record. It keeps one row image per committed run, beside the
scope's facts under `_delegate-tables/`, and it writes no fact (`FactsWritten = 0`). A table bound
anywhere else goes to the writer that was already composed.

# Phase 994 — a fact-table run names its inputs, can replace one period, and is exclusive per table

**What changes.** `IFactTableWriter.OpenRun` takes a fourth optional argument,
`?options: FactTableRunOptions`, with two fields:

- `Inputs` — the content hashes of the files or objects the run was computed from, in the
  `<algorithm>:<lowercase hex>` form (`InputHash.isContentHash`, e.g. `sha256:9f04…`). Every fact the
  run commits, absences included, carries them in `Evidence.InputHashes`. The `query_facts` tool now
  returns them as `inputs` (`InputHash.named`), and the browse surface's fact detail lists them.
- `Slice` — the half-open period the run replaces. The run's rows must lie inside it, and the commit
  withdraws only committed rows inside it. Rows outside it stand and are not rewritten, so under
  `AppendByRun` a daily run writes one day's facts, not the table's history. A committed row that
  the slice would split refuses the run. `None` replaces the whole table, as before.

A table now takes **one open run per scope**. `OpenRun` refuses a second run with
`FactTableRunInProgress(tableId, openRunId)` while another run of the table is open and within the
table's refresh cadence. A run that has ended, or is past the cadence, no longer holds the table.
Two new `FactTableWriteError` cases: `FactTableRunInProgress` and `FactTableRunOptionsRefused`
(options a writer cannot honour, refused at open, or a slice that splits a committed row, refused
at commit).

**Who is affected.**

| You… | Change |
|---|---|
| call `OpenRun(scopeId, tableId[, provenance])` | Nothing compiles differently. A second concurrent open of one table is now refused: end the first run, or retry after it ends. |
| match `FactTableWriteError` exhaustively | **Breaking.** Add the two cases, or use `FactTableWriteError.describe`. |
| implement `IFactTableWriter` | **Breaking.** Take `?options`, honour it or refuse it with `FactTableRunOptionsRefused`, and keep one open run per table and scope. |
| decorate an `IFactTableWriter` | **Breaking.** Forward `options` untouched, and keep an omitted one omitted. |

```diff
-member _.OpenRun(scopeId, tableId, ?provenance) =
-    inner.OpenRun(scopeId, tableId, ?provenance = provenance)
+member _.OpenRun(scopeId, tableId, ?provenance, ?options) =
+    inner.OpenRun(scopeId, tableId, ?provenance = provenance, ?options = options)
```

A daily producer:

```fsharp skip=fragment
let options =
    FactTableRunOptions.none
    |> FactTableRunOptions.withInputs [ "sha256:" + fileHash ]
    |> FactTableRunOptions.forSlice { From = day; To = day.AddDays 1.0; Label = Some(day.ToString "yyyy-MM-dd") }

match! writer.OpenRun(scopeId, "trade-daily", options = options) with
| Error(FactTableRunInProgress(_, openRunId)) -> … // another upload is publishing this table
| Error e -> …
| Ok run -> … // WriteRows that day's rows only, then Commit
```

**Writers.** `DefaultFactTableWriter` honours both options over any `IFactStore`, the blob store and
`PostgresFactStore` alike. The delegate writer keeps one exclusive run per table but refuses run
options: it holds one row image per run and mints every row under the current run, so it could not
attribute an earlier slice's rows to the run that wrote them. Bind a table that needs inputs or
slices to the default writer.

**Stored data.** A run's options are kept with its staged rows, at
`_fact-tables/rows/<runId>/options.json`, only when it declares any, and are reclaimed with the rows.
Each table keeps its open-run claim at `_fact-tables/open/<tableId>.json` (`_delegate-tables/open/…`
for the delegate writer). The default writer now deletes a table's superseded snapshot once the head
moves, so a table keeps one snapshot instead of one per commit. Facts already written are untouched.

**Verify.** Bind `IFactTableWriterContract.exclusivityTests` to every writer, `runOptionsTests` to a
writer that takes options (`runOptionsRefusedTests` to one that refuses them), and the new case of
`decoratorTests` to a decorator. `IFactTableWriterContract.defaultWriterOver` binds the default
writer over your own `IFactStore`.

**Rollback.** Revert the package. Facts written under it keep their input hashes. Claim and options
blobs are ignored by the older writer, and a sliced run's carried rows stay as they were committed.

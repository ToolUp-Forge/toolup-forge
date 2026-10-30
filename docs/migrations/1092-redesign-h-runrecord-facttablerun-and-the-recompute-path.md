# A fact-table run carries its options on its record, and its lifecycle is one state machine

**Ships in:** ToolUp.Platform.Core and ToolUp.Facts.Server 0.38.0.

**Affected:**

- Code that builds a `FactTableRunRecord` as a full record literal (a test double of `IFactTableWriter`, usually).
  That is a source break (FS0764: the record gained `Options` and `RecordVersion`). Build the record with
  `FactTableRun.opened` instead.
- A consumer that decorates `IFactTableWriter` to remember which options a run was opened with (a request-scoped
  capture of `OpenRun`'s `options`, read back at `Commit`). It can drop the capture: the run's record carries them.
- A producer that wanted run inputs on a table bound to the delegated store. The delegated writer used to refuse
  `Inputs`; it now takes them. A period slice is still refused there.

Nothing else changes shape. `IFactTableWriter`'s members are unchanged, and a run opened with no options behaves as
before.

## What changes

1. **The record.** `FactTableRunRecord` gains `Options : RunOptions` (what `OpenRun` was given: `Inputs`, `Slice`,
   `Method`) and `RecordVersion : int` (`FactTableRunRecord.CurrentVersion` for a record written now). `RunOptions`,
   `RunSlice` and `RunMethod` are new in `ToolUp.Platform.Core` and require qualified access, so an unqualified
   `TemporalExtent` or `DeclaredMethod` literal keeps inferring the fact tier's records. `FactTableRunOptions` stays the
   builder a producer opens a run with; `FactTableRunOptions.toRun` and `FactTableRunOptions.ofRun` move between the
   two.
2. **Reading it.** `OpenRun`, `Runs` and `Status` (its `LatestRun`) return the options for every writer, the
   default and the delegated one, and the decorators pass them through untouched. The default writer's commit reads
   the run's inputs, slice and method from the record, and the delegated writer mints every row of a run under the
   inputs and method its record names.
3. **Old records.** A record written before 0.38.0 has neither member. It reads with `Options = RunOptions.empty` and
   `RecordVersion = 0` (`FactTableRunRecord.ofStored`, applied wherever a writer reads a record). A run opened by an
   earlier release and committed by this one still finds its options: the default writer reads them from the file it
   kept beside the staged rows, and the delegated writer reads the method it kept beside the record. This release
   keeps writing both files, so an earlier release's replica finds them too.
4. **The lifecycle.** `FactTableRun` gains `opened`, `tryOpen`, `stage`, `commit`, `reject`, `abandon`, `hasEnded` and
   `statusName`. Every transition but `opened` takes an `OpenFactTableRun`, which only `tryOpen` hands out and only for
   an `Open` record, so a transition out of a committed, rejected or abandoned run cannot be written. Both writers
   execute these transitions. `FactTableWriteError.ofTransition` maps a refused one to `FactTableRunClosed`, the error
   the writers already returned.
5. **The delegated store and disclosure.** `DelegatedFactStore` implements `IDisclosureTaintIndexSource` by reading its
   inner store's index (or listing the inner store when that keeps none), so a disclosure check composed over the
   delegate reads the index instead of listing the scope.

## Migrate

A test double that answered `OpenRun` with a literal:

```diff
-            return Ok {
-                TableId = tableId
-                RunId = "recorded"
-                SchemaVersion = 1
-                OpenedAt = t0
-                BaseSequence = 0L
-                StagedRows = 0
-                Status = FactTableRunStatus.Open
-            }
+            return Ok(FactTableRun.opened tableId "recorded" 1 t0 0L RunOptions.empty)
```

A writer decorator that captured the options at `OpenRun` to read them at `Commit`:

```diff
-    member _.OpenRun(scopeId, tableId, ?provenance, ?options) = async {
-        captured[(scopeId, tableId)] <- defaultArg options FactTableRunOptions.none
-        return! inner.OpenRun(scopeId, tableId, ?provenance = provenance, ?options = options)
-    }
+    member _.OpenRun(scopeId, tableId, ?provenance, ?options) =
+        inner.OpenRun(scopeId, tableId, ?provenance = provenance, ?options = options)
     ...
     member _.Commit(scopeId, runId) = async {
         let! committed = inner.Commit(scopeId, runId)
-        let options = captured[...]
+        // the run's record carries what it was opened with
+        let! runs = inner.Runs(scopeId, tableId)
+        let options = runs |> Result.map (List.find (fun r -> r.RunId = runId) >> _.Options)
         ...
```

## Verify

- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "run record carries its options"`
  passes for the default writer, the delegated writer and the notifying decorator.
- `--filter-test-list "Phase 1092"` passes: the state machine's laws and the delegated writer's run options.

## Rollback

Pin 0.37.0. A record written by 0.38.0 carries two members 0.37.0 ignores, and the records stay usable. 0.38.0 still
writes the files 0.37.0 reads a run's options from (the options beside a default-writer run's staged rows, a
delegated run's method beside its record), so either release can commit a run the other opened, in a rolling upgrade
or a rollback. The one exception: a delegated run opened by 0.38.0 with inputs, whose rows a 0.37.0 replica mints
without them. Drain such runs before rolling back.

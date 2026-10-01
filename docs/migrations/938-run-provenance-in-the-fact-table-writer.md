# Phase 938 — run provenance is part of the `IFactTableWriter` contract

**What changes.** `IFactTableWriter.OpenRun` takes an optional third argument,
`?provenance: FactTableRunProvenance`, which says where the run's facts come from. `ComputedRun`
(or no argument) means the table's own lineage, exactly as before. `ImportedRun origins` means each
origin's rows are written `Imported`, naming the origin's certificate, with evidence naming the origin
and a disclosure no wider than the origin published. `FactTableRunProvenance`, `FactTableImportOrigin`,
`FactTableImportedCell` and `FactTableRunProvenance.rewrite` are now public.

Before this phase the provenance was staged beside an open run as a side channel that only the
default writer read, so team publication refused a target table bound to any other writer
(`FactTableNotBoundHere`). That refusal is gone. A consolidation goes through the composed writer
whatever store the target is bound to, and the delegate writer (Phase 889) writes it too.

**Who is affected.**

| You… | Change |
|---|---|
| call `OpenRun(scopeId, tableId)` | Nothing. The call compiles and behaves as before. |
| implement `IFactTableWriter` | **Breaking.** Implement the three-argument member and honour the provenance (below). |
| decorate an `IFactTableWriter` | **Breaking.** Forward the provenance untouched, and keep an omitted one omitted. |

**Implementing it.** Keep the provenance with the run in your backing store, like its staged rows
(portability rule 4: another instance may commit the run). Apply `FactTableRunProvenance.rewrite` to
every draft the run mints, absences included. Drop the provenance when the run is abandoned or
rejected. A `ComputedRun` should store nothing, so a deployment that never imports is unchanged (GP 11).

```diff
-member _.OpenRun(scopeId, tableId) = async {
+member _.OpenRun(scopeId, tableId, ?provenance) = async {
+    let provenance = defaultArg provenance ComputedRun
     …keep `provenance` with the run; at commit, map each draft through
+    FactTableRunProvenance.rewrite provenance
```

A decorator forwards it:

```diff
-member _.OpenRun(scopeId, tableId) = inner.OpenRun(scopeId, tableId)
+member _.OpenRun(scopeId, tableId, ?provenance) = inner.OpenRun(scopeId, tableId, ?provenance = provenance)
```

**Stored data.** The default writer keeps an imported run's provenance at the blob name the staging
used, `_fact-tables/provenance/<runId>.json`. A run opened and staged before the upgrade therefore
still commits `Imported`. The delegate writer keeps a committed imported run's provenance under
`_delegate-tables/provenance/`, because it mints that run's rows whenever they are read. It also
records the certificates each table imported under in `_delegate-tables/lineages/`, so a refresh
retires a quoted imported fact just as it retires a computed one. A computed run writes none of
these files.

**Verify.** Bind the packs against your implementation: `IFactTableWriterContract.provenanceTests`
for a writer, and `IFactTableWriterContract.decoratorTests` for a decorator. The first checks through
point reads that a computed run writes exactly what an unmarked run writes, and that an imported
run's rows are `Imported`, floored and withdrawn per origin. The second checks that the provenance
reaches the inner writer untouched.

**Rollback.** Revert the package. Runs committed under this version keep the facts they wrote. A
delegate-bound table then mints its imported rows `Computed` again, and publication refuses a
target that is not bound to the default writer again.

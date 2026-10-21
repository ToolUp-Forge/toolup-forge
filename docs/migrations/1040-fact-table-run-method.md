# Phase 1040 — a fact-table run declares the method that produced it

**What changes.** `FactTableRunOptions` gains a third field, `Method: DeclaredMethod option`, set
with `FactTableRunOptions.withMethod`. `DeclaredMethod` (`ToolUp.Facts.Core`) is a method's id and
version — `{ MethodId = "trade.derive"; MethodVersion = "v1" }`, or `DeclaredMethod.create "trade.derive" "v1"`.
A run that declares one stamps every fact it writes, absences included, with the token
`method:<id>@<version>` among its `Evidence.InputHashes`; read it back with
`DeclaredMethod.ofEvidence`, render it with `DeclaredMethod.describe`.

The fact's `Method` is unchanged — still the table's own lineage,
`Computed(producingOperation, "v<schemaVersion>", tableId)`. The declared method is in the fact's
content address but not its lineage, so a run by another method supersedes the earlier method's
facts for the same subject and period instead of competing with them. A run that declares no method
writes exactly what it wrote before. Details: [facts.md](../platform/facts.md#the-method-a-derived-tables-run-declares-phase-1040).

**Who is affected.**

| You… | Change |
|---|---|
| build options with `FactTableRunOptions.none \|> withInputs … \|> forSlice …` | Nothing. |
| construct `FactTableRunOptions` as a full record literal | **Breaking (FS0764).** Add `Method = None`, or switch to the builders. |
| ride a derivation's identity as an input hash (a made-up `sha256:` of `"derive v1"`) | Declare it instead — see the diff below. The input list should name real inputs only. |
| read a method out of `Evidence.InputHashes` yourself | Use `DeclaredMethod.ofEvidence`. |
| implement `IFactTableWriter` | Honour `Method` (stamp it with `FactTableRunOptions.rewrite`, which now appends the method token) or refuse it with `FactTableRunOptionsRefused`. |
| decorate an `IFactTableWriter` | Nothing — keep passing `options` through untouched. |

```diff
 let options =
     FactTableRunOptions.none
-    |> FactTableRunOptions.withInputs [ fileHash; deriveIdentityHash ]
+    |> FactTableRunOptions.withInputs [ fileHash ]
+    |> FactTableRunOptions.withMethod (DeclaredMethod.create "trade.derive" "v1")
     |> FactTableRunOptions.forSlice slice
```

Expect one supersession per cell on the first run after the switch: the facts' content addresses
change (the made-up input is gone, the method token is in), and they supersede the previous run's
facts in the same lineage. Nothing competes and nothing needs cleaning up.

**Writers.** `DefaultFactTableWriter` honours the method over any `IFactStore`. The delegate writer
honours it too — it keeps the method at `_delegate-tables/methods/<runId>.json` (only when
declared; reclaimed with the run if the run never commits) and stamps every row it mints under that
run. It still refuses inputs and slices (Phase 994).

**Verify.** `IFactTableWriterContract.provenanceTests` now holds the method laws — declared method
recorded on every fact, absent method unchanged, cross-method supersession, a malformed method
refused — and `decoratorTests` passes a method through. Bind both to your writer as before; a fact
written by a method-declaring run answers `DeclaredMethod.ofEvidence fact.Evidence = Some method`.

**Rollback.** Revert the package and drop the `withMethod` call. Facts already written keep their
method token; an older reader sees it as one more entry in `InputHashes` (it is never in the
`InputHash` content-hash form, so `InputHash.named` ignores it). A run's kept options blob carrying a
`Method` field is read by the older writer as if the field were absent.

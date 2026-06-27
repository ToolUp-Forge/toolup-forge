# Phase 219 — Derived-column transforms in CSV mapping (consumer adoption)

**Composes onto [Phase 218](218-mapping-per-row-validation-dry-run-preview.md) / the
[Mapping-aware Data Manager](mapping-data-manager.md) (Phase 172).** Additive and opt-in to the
already-optional mapping Data Manager; a deployment that doesn't compose it — and any saved mapping
with no derived columns — is byte-for-byte unchanged (GP 13).

**What changes.** The column-mapping engine previously mapped one schema field 1:1 to one CSV column
(with per-cell `CellTransform` cleaning). It now also supports **derived / computed columns** — a
schema field produced from zero, one, or several source columns via a small, Fable-safe transform
expression (concat, split-and-take, format/template, constant, substring). This lets non-trivial CSVs
(a single "Full Name" → first/last, two columns → one composite key, a literal constant column) map
cleanly without a manual pre-edit pass. Derived mappings persist inside the `Conversion` exactly like
the existing `Remediation` transforms, so a re-import of the same fingerprint re-derives automatically.

Flow (mapping-review step): pick target → review/override the 1:1 mapping → **Add a derived column**
(pick a target field + expression kind + source columns) → **Confirm & validate** (the dry-run runs
over the *derived* shape too). A derived column satisfies a required field; cycles / unbound source
references are caught at confirm time as errors-as-data (GP 12.3), never a throw.

**New surface (all additive):**
- `ColumnMappingTypes` (Core, Fable-safe) — `ColumnExpr` DU (`SourceColumn` / `Constant` /
  `Concat` / `SplitTake` / `Format` / `Substring`), `DerivedColumn` (`{ Field; Expr }`),
  `DerivedColumnError`.
- `Conversion` gains a `Derived: DerivedColumn list` field. **Wire-/persistence-back-compat:** a
  pre-219 saved recipe has no `Derived` field, which the record deserialiser fills with `null`
  (F# `[]` is a real object, not null). The default `IConversionStore` normalises that `null` → `[]`
  on read, and `ColumnMapping.rewriteCsvWithDerived` coerces defensively, so an old recipe re-imports
  unchanged.
- `ColumnMapping` (Core, pure / Fable-safe) — `evalColumnExpr`, `validateDerivedColumns`,
  `columnExprRefs`, `describeColumnExpr` / `describeDerivedColumn`, and
  `rewriteCsvWithDerived schema mapping transforms derived rawCsv`. The Phase-172 `rewriteCsv` is
  retained unchanged and delegates with no derived columns — byte-for-byte-identical output.
- `MappingDataManagerUI` — a minimal derived-column builder on the mapping-review step (the persisted
  `ColumnExpr` supports nesting; the builder offers the flat common cases).

## Diff to apply

**No consumer action required.** Every change is additive and defaults to the prior behaviour:

- A deployment on `NoColumnMapping` (the default) is unaffected — no derived-column surface, zero cost.
- A deployment already on `EnabledColumnMapping` gets the derived-column builder for free. Existing
  saved recipes carry no derived columns, so they rewrite exactly as before.
- A consumer that ships its **own** `IConversionStore` should normalise a deserialised `Conversion`'s
  `Derived` field from `null` → `[]` the same way the default store does (or rely on
  `rewriteCsvWithDerived`'s own coercion) so a pre-219 blob re-imports without an NRE.

## Verification steps

- `dotnet build ToolUp.Forge.sln` — clean (additive `ColumnExpr` / `DerivedColumn` types + the
  `Conversion.Derived` field + the new Core functions compile against every existing composition).
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` —
  `DerivedColumnEval` passes: each `ColumnExpr` kind evaluates correctly, composes with
  `CellTransform`, round-trips through persistence, validates unbound / cross-derived references, and
  a mapping with no derived columns produces byte-for-byte-identical output to Phase 172.
- Public-API gate: the additive Core surface is reflected in
  `api-baselines/ToolUp.Platform.Core.approved.txt` (the `Conversion` constructor gains the `Derived`
  parameter; the new types / functions are added).
- Fable gate: `cd samples/MinimalClient && dotnet fable -o output` — the derived-column builder
  transpiles (pure Core evaluator, no server-only API leaks into the client path).
- End-to-end: enable `EnabledColumnMapping`; upload a CSV with a "Full Name" column; map a "First"
  field via a `SplitTake` on space, index 0; confirm — the imported object carries the split value,
  and re-uploading the same structure re-derives automatically.

## Rollback

Revert the commit. The `Derived` field defaults to the empty list and the new types/functions are
unreferenced by any pre-219 recipe, so removal is safe. A recipe saved *with* derived columns under
this version would lose them on rollback (the field is dropped), but no data-shape migration is
required — the rewrite is a read-only projection.

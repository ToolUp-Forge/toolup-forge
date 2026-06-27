# Phase 218 — CSV-mapping per-row validation / dry-run preview (consumer adoption)

**Composes onto [Mapping-aware Data Manager](mapping-data-manager.md) (Phase 172).** Additive and
opt-in to the already-optional mapping Data Manager; a deployment that doesn't compose it is
byte-for-byte unchanged (GP 13).

**What changes.** The mapping wizard's **Confirm** step no longer commits directly. After the user
confirms the field mapping it runs a **dry-run validation** of the *mapped* CSV against the target
type's schema and shows a per-row / per-cell error report **before** anything is written —
"N of M rows would fail, here's why and where". Previously a bad row was only discovered when
`DataType.Process` threw on the first failure during commit; now the failures come back as data
(GP 12.3) with no write and no `DataType.Process` call.

Flow: pick target → review/override mapping → **Confirm & validate** → **validation preview**
(pass/fail counts + sampled failing cells grouped by column) → **Import** (or **Back to mapping** to
fix). The report reuses the data-quality review's severity vocabulary — a policy-blocked commit reads
red (must act), a warn-only failure reads amber (proceed at your discretion), a clean report reads
green.

**New surface (all additive):**
- `ColumnMappingTypes` (Core, Fable-safe) — `DryRunReport`, `DryRunCellIssue`, `DryRunRowIssue`,
  `DryRunValidationRequest`.
- `IConversionApi.ValidateConversion: DryRunValidationRequest -> Async<Result<DryRunReport, string>>`
  (`[<AllowAnonymous>]`, scope-isolated like the rest of the contract).
- `IMappingDryRunValidator` (server seam) + the default BCL-only `MappingDryRunValidator.create`
  (coarse type + required-cell checks over the platform's coarse `DataTypeSchema`). Registered via
  `TryAddSingleton` under `ColumnMapping = EnabledColumnMapping`, so a deployment can compose a richer
  validator (e.g. one backed by `ToolUp.Tabular`'s constraint/pattern engine) ahead of it and win —
  keeping that companion's vendor dependency out of `ToolUp.Platform.*` (GP 1).
- `ServerConfig.MappingDryRun: MappingDryRunPolicy` — `WarnOnValidationFailure` (default; failures
  surfaced but commit allowed — preserves the prior unconditional-commit behaviour, GP 11) or
  `BlockOnValidationFailure` (commit refused while any row would fail). Env:
  `TOOLUP_MAPPING_DRYRUN_BLOCK=enabled`.

## Diff to apply

**No consumer action required.** Every change is additive and defaults to the prior behaviour:

- A deployment on `NoColumnMapping` (the default) is unaffected — no validator registered, no route,
  zero cost.
- A deployment already on `EnabledColumnMapping` gets the validation preview for free with
  `WarnOnValidationFailure` semantics: the report is shown, commit is never blocked, so the wizard
  behaves as before plus a heads-up.
- To make validation a hard gate, opt in:

```fsharp
let config =
    { ServerConfig.defaults with
        ColumnMapping = EnabledColumnMapping
        MappingDryRun = BlockOnValidationFailure }
```

A consumer that ships its **own** `IConversionStore` needs no change (the validator is a separate
seam). A consumer wanting richer-than-coarse validation registers its own `IMappingDryRunValidator`
singleton before/over the default.

## Verification steps

- `dotnet build ToolUp.Forge.sln` — clean (additive `ServerConfig` field + `IConversionApi` method +
  two new server files compile against every existing composition).
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` —
  `MappingDryRunValidation` passes: a clean mapped CSV reports zero failures; a type/constraint
  violation pinpoints the exact failing cells (row/column/expected/actual) without committing;
  `DataType.Process` is never invoked during the dry-run; warn-vs-block policy decision.
- Fable gate: `cd samples/MinimalClient && dotnet fable -o output` — the wizard's validation step
  transpiles (no server-only API leaks into the client path).
- End-to-end: enable `EnabledColumnMapping`; upload a CSV with a bad cell (text where a number is
  expected, or an empty required column); confirm the mapping; the validation preview pinpoints the
  failing rows. Under `WarnOnValidationFailure` you can still import; under `BlockOnValidationFailure`
  the Import button is disabled until the mapping/source is fixed.

## Rollback

Revert the commit. `MappingDryRun` defaults to warn-only and the new types/seam are unreferenced by
any existing consumer, so removal is safe. No persisted artefact and no data-shape migration — the
dry-run is a read-only inspection.

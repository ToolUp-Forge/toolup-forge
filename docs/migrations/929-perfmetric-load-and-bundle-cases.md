# Phases 886, 909 and 929 — `PerfMetric` gained nine cases, and the load budget two storage arms

**What changes for you: nothing, unless you `match` on `ToolUp.Platform.PerfMetric` yourself.** The
perf-budget gate's metric type lives in `ToolUp.Platform.Build`, and two phases added cases to it.
A closed union gaining a case breaks every exhaustive `match` over it (FS0025 under
warnings-as-errors), which is the only way this reaches a consumer. Phase 929 added nothing to the
union; its additions are new functions and targets beside the existing ones, listed at the end.

## The nine cases

| Phase | Case | Unit | Budget block |
|---|---|---|---|
| 886 | `RetrievalP95Ms` | ms | `load` |
| 886 | `RetrievalBlobReadsPerQuery` | reads | `load` |
| 886 | `FactPointReadMs` | ms | `load` |
| 886 | `FactPopulationReadMs` | ms | `load` |
| 886 | `FactAssertMs` | ms | `load` |
| 886 | `FactBatchAssertMs` | ms | `load` |
| 886 | `FactBlobReadsPerPointRead` | reads | `load` |
| 909 | `ClientMinimalBundleKiB` | KiB | `client` |
| 909 | `ClientShellBundleKiB` | KiB | `client` |

Each phase appended its cases after the existing ones, so no earlier case's integer `Tag` moved.

## If you match on `PerfMetric`

Add the cases you handle, or a wildcard for the ones you do not:

```fsharp skip=fragment
// before — exhaustive over the five cases 192 and 849 shipped
let unitOf metric =
    match metric with
    | ColdStartMs | HotPathMs | ClientBootMs -> "ms"
    | ClientDecodePerResponseUs | ClientViewPerDispatchUs -> "us"

// after — prefer the SDK's own accessor, which is kept exhaustive for you
let unitOf metric = PerfMetric.unit metric
```

`PerfMetric.key`, `PerfMetric.unit`, `PerfMetric.describe` and the block lists (`PerfMetric.server`,
`PerfMetric.client`, `PerfMetric.load`, `PerfMetric.all`) cover every case, so code that goes through
them needs no edit.

## If you read the gate's report text

The headroom ratio on an `[ok]` line is now printed to up to three decimals (`1.025x`), not one
(`1.0x`), so a ceiling set just above its baseline is distinguishable from one already reached. A
ratio with no fractional digits beyond the first reads as before (`14.0x`). Only a parser of the
report's prose would notice.

## Phase 929's additions (additive)

- `PerfBudgetGate.LoadAzuriteBlockProperty` / `LoadPostgresBlockProperty` — the `loadAzurite` and
  `loadPostgres` blocks of `perf-budgets.json`.
- `PerfBudgetGate.parseLoadAzuriteBudget` / `parseLoadPostgresBudget` and
  `PerfBudgetGate.verifyLoadAzurite` / `verifyLoadPostgres` — the same shape as the `load` block's
  pair, restricted to the same metrics.
- The FAKE targets `VerifyLoadAzuritePerfBudget` and `VerifyLoadPostgresPerfBudget`, registered by
  `PerfBudgetGate.registerTarget` beside the existing three.

A budget file with no `loadAzurite` / `loadPostgres` block is unaffected: the new targets are only
run by `dev-scripts/perf-budget-gate.ps1 -LoadArms …`, and the existing targets read nothing new.

## Verification

```powershell
dotnet run --project src/ToolUp.Platform.Build.Tests   # the PerfBudget list, including "load arms"
pwsh ./dev-scripts/perf-budget-gate.ps1 -SkipServer -SkipClient -LoadArms azurite,postgres
```

The second needs the local emulator and a local PostgreSQL; `docs/rag/performance.md` says how to
start both.

## Rollback

Pin `ToolUp.Platform.Build` to the release before the one carrying this note. The budget file's
`loadAzurite` / `loadPostgres` blocks are ignored by an older decider.

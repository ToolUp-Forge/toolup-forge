# 1168 — AG Grid and AG Charts load on demand; the first load has a budget

**Breaking for one narrow case, 0.38.0.** A production build of the SDK shell now writes several files.
AG Grid and AG Charts each load the first time a grid or chart mounts, so a first load fetches about 39% of
the shell's JavaScript. No composition root has to change to get this. The break is `PerfMetric` gaining a
case, which only reaches you if you `match` on it.

## What changes

| Before | After |
|---|---|
| `shellBundleKiB`: one file, 3,748 KiB | the same sum, 3,740 KiB across five files |
| no first-load figure | `shellFirstLoadKiB`: 1,475 KiB (357 KiB gzipped), against 1,000 KiB gzipped before |
| `AgGrid.grid` / `AgChart.chart` render on the first pass | they render a placeholder (`role="status"`, `aria-busy`) until the chunk arrives |
| the shell wraps the tree in `AgGridProvider` | each grid takes `ClientConfig.GridModules` through `AgGridProvider`, from its chunk |
| `AgGridModuleConfig.community.Modules` held the Community module | it is empty, and an empty array means Community |

A chunk that fails to arrive throws into the nearest error boundary. In the SDK shell that is `ModuleBoundary`,
whose **Reload module** button remounts the module and fetches the chunk again. A program that mounts grids
outside the shell needs an error boundary of its own, or a failed chunk unmounts its React root.

## What keeps AG Grid in your first load

A build gets the split only if nothing it loads at the start reaches AG Grid statically. Three things do:

| You use | Effect | To get the split |
|---|---|---|
| `Theme.themeBalham` as it stands | `ag-grid-community` stays in your entry | `AgGrid.baseTheme BaseTheme.themeBalham` (`themeQuartz` and `themeBalham` have names) |
| `Theme.withParams` / `Theme.withPart`, or `Theme.colorSchemeDark` | the same | nothing yet: a customised theme is built from AG Grid's own object |
| `AgGridEnterprise.gridModuleConfig` in the composition root | `ag-grid-enterprise` stays in your entry | nothing yet: the licence and modules are set at boot |

Each one keeps working as it did. The cost is only a larger first load.

## If you match on `PerfMetric`

`ToolUp.Platform.Build` gains `ClientShellFirstLoadKiB`, appended last, so no earlier case's `Tag` moves. Add it, or a
wildcard, to an exhaustive `match`.

## Verification

```powershell
pwsh ./dev-scripts/perf-budget-gate.ps1 -SkipServer -SkipLoad
```

It prints `shellFirstLoadKiB` beside the other client sizes, and it stops with a named finding if `AgGridRuntime` or
`AgChartRuntime` is no longer a chunk loaded on demand.

## Rollback

Revert the phase's commits. Nothing persisted depends on it.

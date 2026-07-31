# ToolUp.Platform Technical Guide — 08. UI Components & Front-End

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 7. Module Communication, Indexing & Portability](07-module-communication-and-portability.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 9. Module Conventions, Data Flow & Build →](09-module-conventions-data-flow-and-build.md)

---

## AG Grid Enterprise Companion

AG Grid Enterprise initialisation lives in `src/AgGridEnterprise/`, separate from ToolUp.Platform. This separation serves two purposes:

1. **Licensing boundary.** `ag-grid-enterprise` has a commercial EULA. The SDK works with Community edition without shipping Enterprise code. The licensing obligation is visible in the companion's `package.json`.

2. **Bundle isolation.** `ag-grid-enterprise` registers modules with a global `ModuleRegistry` on import — bundlers cannot tree-shake it. If it were imported anywhere in ToolUp.Platform, every deployment would include the full Enterprise bundle.

### How it works

All `ag-grid-enterprise` and `ag-charts-enterprise` imports and module registration calls live at **module top level** in `AgGridEnterprise.fs`:

```fsharp
// Module top-level — runs immediately when AgGridEnterprise.fs is first evaluated,
// which happens before Client.fs because AgGridEnterprise.Client.props is imported first.
let private moduleRegistry: obj = import "ModuleRegistry" "ag-grid-enterprise"
let private allEnterpriseModules: obj = import "AllEnterpriseModule" "ag-grid-enterprise"
let private integratedChartsModule: obj = import "IntegratedChartsModule" "ag-grid-enterprise"
let private licenseManager: obj = import "LicenseManager" "ag-grid-enterprise"
let private agChartsEnterpriseModule: obj = import "AgChartsEnterpriseModule" "ag-charts-enterprise"

let private integratedChartsWithCharts: obj =
    emitJsExpr (integratedChartsModule, agChartsEnterpriseModule) "$0.with($1)"

do moduleRegistry?registerModules ([| allEnterpriseModules; integratedChartsWithCharts |])

/// Set the license key. Call once before the Elmish program mounts.
let register (licenseKey: string) =
    if not registered then
        licenseManager?setLicenseKey licenseKey
        registered <- true
```

**Why module top-level matters:** AG Charts Enterprise animations depend on hooks installed during `AgChartsEnterpriseModule.setup()` (called internally by `.with()`). These hooks must be in place before the first `AgCharts` React component is created. If the registration calls were deferred inside `register()`, there would be a race between the first chart render (triggered by React mounting) and `register()` being called — even though `register()` is called before `Client.run`, by the time React renders the first chart, animations would not be properly initialized. Module-level evaluation is guaranteed to complete before any React rendering begins.

### Integration

- `AgGridEnterprise.Client.props` injects the `.fs` file into the client project's compile order, before `Client.fs`
- The consuming app's client entry point (`Client.fs`) calls `AgGridEnterprise.register licenseKey` before `Client.run` to set the license key
- Module rendering code (`AgGrid.grid [...]`, `AgChart.chart [...]`) is unchanged — the Fable bindings in `AgGrid.fs`/`AgChart.fs` import from `ag-grid-react`/`ag-charts-react` (Community packages) and work identically with both editions

### Community-only deployment

Remove the `.props` import from the consuming app's client `.fsproj` and the `AgGridEnterprise.register` call from `Client.fs`. The app builds and runs with Community-level grid features. `AgChart.fs` falls back to `AgChartsCommunityModule.setup()` via `ensureChartsModulesRegistered()`.

### Rules

- Do not move Enterprise imports into ToolUp.Platform or SDK.Client.fs
- Do not add top-level imports of `ag-grid-enterprise` or `ag-charts-enterprise` in any file outside `src/AgGridEnterprise/`
- Do not move the module-level registration calls inside a function — this breaks AG Charts Enterprise animations
- Module CSS imports must use `ag-grid-community/styles/`, not `ag-grid-enterprise/styles/`

## AG Charts: Axes Format and Animation

### Axes format: direction-keyed object

`AgChart.axes` in `AgChart.fs` produces a JS object keyed by direction (`"x"` for time/category, `"y"` for number), not an array and not a position-keyed object. This matches AG Charts v13+'s `getPrimaryAxisKeys` fallback logic, which looks for direction keys `"x"` and `"y"` when resolving which axis to bind to which series dimension.

```fsharp skip=fragment
static member inline axes(v: obj seq) =
    "axes"
    ==> (v
         |> Seq.map (fun axis ->
             let pos: string = axis?position
             let key =
                 if isNull (box pos) then
                     let axisType: string = axis?``type``
                     match axisType with
                     | "time" | "category" -> "x"
                     | _ -> "y"
                 else
                     pos  // explicit position used as key for secondary axes
             key ==> axis)
         |> Seq.toList
         |> createObj)
```

**Do not change this to `Seq.toArray v`** — AG Charts v13 rejects an array for `axes` (it checks `isObject4`). **Do not use position strings as primary keys** — `"bottom"` and `"left"` are not recognised by `getPrimaryAxisKeys` and produce invisible axes. When `Axis.position` is set explicitly (e.g. for secondary axes), the position string is used as the key so the secondary-axis loop (`"position" in axisOptions`) can find it.

### Animation: the memoizedChart wrapper

AG Charts v13.2.1 introduced a regression that makes animations appear instant when rendered from an Elmish application. Understanding the mechanism is important to avoid accidentally breaking it again.

**The regression:** `applyOptions()` always ends with `this.update(type, { newAnimationBatch: true })`. If `newAnimationBatch: true` AND `animationManager.isActive() = true` (an animation is running), `_performUpdateSkipAnimations` is set to `true` and the running animation is immediately aborted.

**Why Elmish triggers this:** `ag-charts-react`'s `useEffect([options])` calls `chart.update(options)` whenever the `options` prop reference changes. Fable's `createObj` always returns a new JS object, so every Elmish re-render (which reconstructs the Feliz tree) produces a new `options` reference. Elmish re-renders happen frequently — on every state update, including those triggered by the chart's own data load completing. The sequence:

1. Chart renders for the first time → `chart.update(options)` → animation starts (via `queueMicrotask`)
2. Elmish re-renders (e.g. loading state finishes) → new `options` reference → `useEffect` fires → `chart.update(options)` again
3. Because `queueMicrotask` runs before the next paint, the animation is already active (`isActive() = true`) when step 2 fires
4. `newAnimationBatch: true` + `isActive() = true` → animation skipped

**The fix — `MemoizedChart`:**

`MemoizedChart` is a `[<ReactComponent>]` defined in `AgChart.fs` that wraps the real `AgCharts` component:

```fsharp
[<ReactComponent>]
let MemoizedChart (options: obj) =
    let prevJsonRef = React.useRef ""
    let stableRef = React.useRef options
    let json = JS.JSON.stringify options

    if json <> prevJsonRef.current then
        prevJsonRef.current <- json
        stableRef.current <- options

    Interop.reactApi.createElement (agChart, createObj [ "options" ==> stableRef.current ])
```

On each render it JSON-serializes the incoming options and compares against the previous serialization. Only when the JSON differs (i.e. chart data has semantically changed) does it update `stableRef.current`. The actual `AgCharts` component therefore receives a stable reference between Elmish re-renders, preventing `useEffect([options])` from firing spuriously and killing animations.

`AgChart.chart` delegates to this wrapper:

```fsharp skip=fragment
static member inline chart props =
    ensureChartsModulesRegistered ()
    MemoizedChart (createObj !!props)
```

**Critical: `MemoizedChart` must not be `private`.** `AgChart.chart` is a `static member inline` on an `[<Erase>]` type. Fable inlines the method body at every call site, so the compiled JS at the call site contains a direct import of `MemoizedChart` from `AgChart.js`. If `MemoizedChart` is declared `private`, Fable does not export it, producing a runtime `SyntaxError: does not provide an export named 'MemoizedChart'`. This applies generally: **any module-level value referenced from an `inline` method on an `[<Erase>]` type must be exported (non-private)**.

**Do not add a changing `prop.key` to the chart container.** A `prop.key` that changes when the selected fact/series changes forces React to unmount and remount the entire chart component, destroying the `AgCharts` instance. This eliminates all transition animations — the chart always draws from scratch. The `MemoizedChart` wrapper handles correct `chart.update()` delivery without unmounting.

## AG Grid / AG Charts binding reference (Phase 12e)

The Fable bindings target **ag-grid 35.3.0** / **ag-charts 13.3.0** — the versions pinned in the samples' `package.json`. Binding-version releases of the UI surface align to those pins (bump the pin, bump the binding version).

### Where each feature's bindings live

| Surface | File | Highlights |
|---|---|---|
| Community grid | `src/ToolUp.Platform.Client/Client/UI/AgGrid.fs` | events (cell/row/column/display), filter API on `IGridApi`, Theming-API `Theme` builder, `CsvExportParams`, `LocaleText`, selection completion, `ColumnDef` completion |
| Community charts | `src/ToolUp.Platform.Client/Client/UI/AgChart.fs` | `Series` (+ histogram), `PieSeries`, `BubbleSeries`, `AgChart` tooltip/crosshair/sync/padding/legend, `Axis` completion, `LegendOptions`, `ChartThemeBuilder` |
| Enterprise grid | `src/AgGridEnterprise/AgGridEnterpriseTypes.fs` | Set/Multi Filter, Excel export (+`ExcelStyle`), Master/Detail, Status Bar, Sidebar, charts integration, range selection, SSRM, custom agg funcs |
| Enterprise charts | `src/AgGridEnterprise/AgChartEnterpriseTypes.fs` | Sankey, Sunburst, Treemap (`HierarchyNode`), Candlestick, Ohlc, Heatmap, Waterfall, Box-plot, Range series, `SparklineOptions` + `MemoizedSparkline` |

### Rules carried forward from the Community bindings

- **Type-erasure / inline-export rule.** `MemoizedChart` and `MemoizedSparkline` — and any module value referenced by an `inline` member on an `[<Erase>]` type (`ChartPalette`, the `Theme.*` imports) — must be non-`private`, or a consumer gets a runtime "does not provide an export" `SyntaxError`.
- **JSON-memo rule.** Both `MemoizedChart` (charts) and `MemoizedSparkline` (sparklines) memoise their React props by `JSON.stringify` equality so Elmish re-renders don't kill animations.
- **Direction-keyed `axes`**, no `prop.key` on chart wrappers, `data` through the builders — as above.
- **Enterprise imports isolation.** Enterprise series types are erased and emit no JS imports; the sole `ag-charts-enterprise` / `ag-grid-enterprise` imports live module-top-level in `AgGridEnterprise.fs`.

### Source of truth + cookbooks

- `.d.ts` under `node_modules/ag-{grid,charts}-{community,enterprise}/dist/types/src/` and `node_modules/ag-charts-types/`.
- Authoring cookbooks (canonical, single-Read for an AI agent): [`../../ToolUp.Platform.Client/Client/UI/COOKBOOK.md`](../../ToolUp.Platform.Client/Client/UI/COOKBOOK.md) (Community) and [`../../AgGridEnterprise/COOKBOOK.md`](../../AgGridEnterprise/COOKBOOK.md) (Enterprise).
- In 13.3.0, heatmap / waterfall / box-plot / range-bar / range-area are Enterprise-only (bound in the companion, not `AgChart.fs`). Long-tail (Advanced Filter custom UI, Viewport Row Model, nightingale / radial / radar, Annotations) stay `obj` escape hatches.

## Module-level error boundaries (Phase 12c)

Every module's view tree is wrapped in `Components.ModuleBoundary` at the per-module view-dispatch site in `SDK.Client.fs view`. The boundary contains runtime exceptions to the affected module's sidebar entry — other modules keep their state and remain usable. The Reload button surfaced in the error UI dispatches `ResetModule moduleId`, which re-runs the module's `Init` against the current `ClientModuleContext`.

The boundary is the SDK's only class component. `Fable.React.Types.Component<'P,'S>` exposes `componentDidCatch` natively, so no `react-error-boundary` npm dependency is needed; the wrapper is ~100 lines of F# in `Components/ModuleBoundary.fs`.

### What the boundary catches

- **Sync F# exceptions during the view-function call** — `pageView state dispatch` / `v state dispatch`. Caught because the call lives inside `ModuleViewHost`'s render, which is a child component of the boundary.
- **React render-time exceptions** anywhere in the produced tree (Feliz `prop.children` thunks, downstream JS-component renders, anything that throws during reconciliation).

### What the boundary does NOT catch (out of scope for Phase 12c)

- **Errors in module `Update` functions.** These throw inside the synchronous Elmish update tick and crash the whole MVU loop. A separate phase should wrap the boxed `Update` invocation at the `ModuleMsg` handler in `SDK.Client.fs:559-580`.
- **Errors in shell chrome** — sidebar render, `pageHeader`, `AppShell` skeleton, side-panel, `GlobalOverlays`, `ToastCentre`. Every component above the per-module boundary is unprotected.
- **In-flight cmd cancellation.** Stale `Cmd<obj>` from the dead module state will still fire after Reset and route through `ModuleMsg`. The new state's `Update` may not handle them; this is acceptable for an emergency reset and avoids inventing an AbortController-style scope-id pattern.

### `ClientConfig.OnError` telemetry contract

```fsharp
type ModuleErrorReport = {
    ModuleId: string
    Error: exn
    ComponentStack: string
}
```

`OnError: (ModuleErrorReport -> unit) option` on `ClientConfig`. Default `None`. When set, the deployment owns logging entirely — the boundary does not also `console.error` (no double-log). When unset, a single `console.error` fires for dev diagnostics. Synchronous; deployments wanting async forwarding (e.g. forwarding to a server-side activity sink) wrap the call themselves.

`ComponentStack` is best-effort — empty string when React did not provide one (rare, but possible if the throw happens in a context React's reconciler can't introspect). Telemetry consumers should treat the string as informational, not load-bearing.

### Reset semantics — `Model.ResetCounters` and the React `key`

`Model.ResetCounters: Map<string, int>` tracks Reload clicks per module Id. The boundary's React `key` composes the active team Id and the per-module reset counter (`$"{teamId}#{counter}"`). Either changing forces React to unmount the old boundary instance and mount a fresh one with `Error = None`:

- **Reload click** — `ResetModule` increments the counter; the boundary remounts; the error UI clears.
- **Team switch** — `TeamSwitched` wipes `ResetCounters` and the team-Id slot in the key changes regardless; the boundary remounts even if the user was looking at the error UI for the previous team.

Without the team-Id slot, switching teams while the boundary held `Error = Some` would leave the error UI stuck for the new team's data.

### Reset-time `Init` crash — F# try/with at the handler

If `Init` itself throws during `ResetModule` (rare but possible — a misconfigured module, a bad config-store value, etc.), the unprotected handler would crash the shell's update tick. The handler wraps `Init` in F# try/with:

- On success: re-store the state, route the cmd via `Cmd.map ModuleMsg`.
- On failure: surface via `OnError` (or fallback `console.error`); leave `ModuleStates` without the key. The next `ModuleSelected` for this module retries from scratch.

The cold-boot path at `SDK.Client.fs:459-460` (`reinitActiveAfterPrefetch` and seed init) does NOT have an analogous try/with — flagged as a Phase 12c follow-up if real cold-boot Init crashes are observed.

### i18n placeholder

Phase 12a (locale resolution) is not yet shipped. The boundary's error UI strings are hardcoded English with `// PHASE-12A-I18N: replace with tr "modules.error.{title,detail,reload}"` markers. Once Phase 12a ships, replace the literals at `Components/ModuleBoundary.fs:104,109,113` with `tr` calls — the migration is mechanical.


---

> [← Prev: 7. Module Communication, Indexing & Portability](07-module-communication-and-portability.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 9. Module Conventions, Data Flow & Build →](09-module-conventions-data-flow-and-build.md)

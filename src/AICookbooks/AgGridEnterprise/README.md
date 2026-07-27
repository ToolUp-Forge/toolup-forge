# ToolUp.AICookbooks.AgGridEnterprise

Enterprise AG Grid + AG Chart prompt-builder companion for `ToolUp.AI` (Phase 12e).

## What it does

The Enterprise twin of `ToolUp.AICookbooks.AgChart`. Exposes a `SystemPromptBuilder` a deployment composes into `composeWithAI`'s prompt list, so the in-app AI assistant authors Enterprise charts and grids in F# against the typed bindings — Sankey / Sunburst / Treemap / Candlestick / Ohlc / Heatmap / Waterfall / Box-plot / Range series and Sparkline, plus Set Filter, Multi Filter, Master-Detail, Excel export, Status Bar, Sidebar, SSRM and custom aggregations.

Extraction reuses `AgChartAICookbook`'s section parse and token bound (`## Critical constraints` + `## The shortest possible chart`, ~600 tokens, truncated on a line boundary), keyed to the Enterprise heading and the Enterprise `COOKBOOK.md`.

## What it does NOT do

Nothing auto-injects this, and it does not imply an AG Grid Enterprise licence. Enterprise components are opt-in companions (GP 2) — compose this only in a deployment that already licenses and initialises AG Grid / AG Charts Enterprise.

## Enabling

```fsharp
open ToolUp.AI

let enterpriseGuidance = AgGridEnterpriseAICookbook.systemPromptBuilder (Some logger)

AIServerApp.empty
|> AIServerApp.withSystemPromptBuilder enterpriseGuidance
|> AIServerApp.run
```

## Cookbook resolution

`systemPromptBuilder` probes for `COOKBOOK.md` most-specific first: the `TOOLUP_COOKBOOK_PATH` override (a file, or a directory holding `COOKBOOK.md`), the assembly-relative copy this package ships (`content\COOKBOOK.md`), then a dev repo-relative path back to the Enterprise source cookbook. `buildFromFile` takes an explicit path.

## Degradation

A read failure or an empty extraction produces a single startup `Warn` and a no-op builder returning an empty string — the guidance is lost, the deployment still starts.

## See also

- `src/AgGridEnterprise/COOKBOOK.md` — the Enterprise authoring reference this reads.
- `ToolUp.AICookbooks.AgChart` — the Community companion, whose parse and token bound this reuses.

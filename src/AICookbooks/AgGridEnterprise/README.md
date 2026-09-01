# ToolUp.AICookbooks.AgGridEnterprise

Enterprise AG Grid + AG Chart prompt-builder companion for `ToolUp.AI` (Phase 12e).

## What it does

The Enterprise twin of `ToolUp.AICookbooks.AgChart`. Exposes a `SystemPromptBuilder` a deployment composes into `composeWithAI`'s prompt list, so the in-app AI assistant authors Enterprise charts and grids in F# against the typed bindings — Sankey / Sunburst / Treemap / Candlestick / Ohlc / Heatmap / Waterfall / Box-plot / Range series and Sparkline, plus Set Filter, Multi Filter, Master-Detail, Excel export, Status Bar, Sidebar, SSRM and custom aggregations.

Extraction reuses `AgChartAICookbook`'s section parse and token bound (`## Critical constraints` + `## The shortest possible chart`, ~600 tokens, truncated on a line boundary), keyed to the Enterprise heading and the Enterprise `COOKBOOK.md`.

## What it does NOT do

Nothing auto-injects this, and it does not imply an AG Grid Enterprise licence. Enterprise components are opt-in companions (GP 2) — compose this only in a deployment that already licenses and initialises AG Grid / AG Charts Enterprise.

## Enabling

```fsharp skip=fragment
open ToolUp.AI

let enterpriseGuidance = AgGridEnterpriseAICookbook.systemPromptBuilder (Some logger)

AIServerApp.create aiProviderFactory providerProfile
|> AIServerApp.withAIConfig {
    Branding = branding
    SystemPrompt = Some enterpriseGuidance
    MaxHistoryMessages = None
    AISurfaceDerivation = TrustClient
}
|> AIServerApp.run
```

## Cookbook resolution

`systemPromptBuilder` probes for `COOKBOOK.Enterprise.md` most-specific first: the `TOOLUP_ENTERPRISE_COOKBOOK_PATH` override (a file, or a directory holding `COOKBOOK.Enterprise.md`), the assembly-relative copy this package ships (`content\COOKBOOK.Enterprise.md`), then a dev repo-relative path back to the Enterprise source cookbook. `buildFromFile` takes an explicit path.

The copied name is companion-specific by design — a deployment composing both builders holds both files in one output directory, and a shared name would let one overwrite the other, silently feeding Enterprise guidance to the Community builder. It is exposed as `AgGridEnterpriseAICookbook.CookbookFileName`.

## Degradation

A read failure or an empty extraction produces a single startup `Warn` and a no-op builder returning an empty string — the guidance is lost, the deployment still starts.

## See also

- `src/Feliz.AgGrid.Enterprise/COOKBOOK.md` — the Enterprise authoring reference this reads.
- `ToolUp.AICookbooks.AgChart` — the Community companion, whose parse and token bound this reuses.

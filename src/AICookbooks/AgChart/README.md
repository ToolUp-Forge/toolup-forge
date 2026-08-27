# ToolUp.AICookbooks.AgChart

Community AG Chart + AG Grid prompt-builder companion for `ToolUp.AI` (Phase 12e).

## What it does

Exposes a `SystemPromptBuilder` a deployment composes into `composeWithAI`'s prompt list, so the in-app AI assistant authors charts and grids in F# against the typed `AgChart` / `AgGrid` bindings rather than guessing at the JS API.

At construction it reads the Community `COOKBOOK.md`, extracts its `## Critical constraints` and `## The shortest possible chart` sections via a header-keyed parse, and prepends them under an authoring heading. The extraction is bounded to ~600 tokens (truncated on a line boundary) so the per-conversation cost is predictable.

The read and extraction happen **once** at construction; the returned builder serves the cached guidance on every turn.

## What it does NOT do

Nothing auto-injects this. `ToolUp.AI` carries no dependency on the UI bindings — the deployment's composition root is the only place that sees both, so wiring it is an explicit opt-in (GP 13: deployments that don't compose it pay nothing).

## Enabling

```fsharp skip=fragment
open ToolUp.AI

let chartGuidance = AgChartAICookbook.systemPromptBuilder (Some logger)

AIServerApp.create aiProviderFactory providerProfile
|> AIServerApp.withAIConfig {
    Branding = branding
    SystemPrompt = Some chartGuidance
    MaxHistoryMessages = None
    AISurfaceDerivation = TrustClient
}
|> AIServerApp.run
```

## Cookbook resolution

`systemPromptBuilder` probes for `COOKBOOK.Community.md` on these paths, most-specific first:

1. `TOOLUP_COOKBOOK_PATH` — a file path, or a directory holding `COOKBOOK.Community.md`.
2. Assembly-relative — the copy this package ships beside the assembly (`content\COOKBOOK.Community.md`, copied to the build output).
3. A dev repo-relative path from the build output back to the source cookbook in `ToolUp.Platform.Client`.

`buildFromFile` takes an explicit path when a deployment wants to supply its own cookbook.

The copied name is deliberately not plain `COOKBOOK.md`: the Enterprise companion copies its own cookbook into the same output directory, and a shared name lets one silently overwrite the other. The repo's *source* file keeps the plain name — only the copied and packed artefact is companion-specific. It is exposed as `AgChartAICookbook.CookbookFileName`.

## Degradation

A read failure, an unrecognised cookbook, or an empty extraction produces a single startup `Warn` on the supplied `ILogger` and a no-op builder returning an empty string. A deployment that cannot ship its `COOKBOOK.md` loses the chart-authoring guidance; it does not fail to start.

## See also

- `src/ToolUp.Platform.Client/Client/UI/COOKBOOK.md` — the Community authoring reference this reads.
- `ToolUp.AICookbooks.AgGridEnterprise` — the Enterprise twin (Sankey / Sunburst / Treemap / OHLC / Sparkline, Set Filter / Master-Detail / Excel export).

# Render tools draw a chart or table from gated facts

**Ships in:** ToolUp.Facts.Presentation.Server 0.37.0 (a new package), ToolUp.AI.Server and ToolUp.Facts.Server 0.37.0.
It rides the untagged 0.37.0 draft, whose class is already breaking. For a deployment it is opt-in and additive. It is
breaking only for code that builds an `AIServerApp` as a full record literal instead of calling `AIServerApp.create` or
`AIServerApp.createFrom`.

**Affected:** a deployment with the fact store and the AI companion that wants the assistant to show a chart or a table
of its fact tables in the conversation. A deployment that does not compose the render tools registers no new tool and
takes no new dependency.

## What changes

1. **Two tools.** `render_chart` draws a trend, a breakdown or a comparison of one metric. `render_table` draws subjects
   by one or more metrics for one day. The model writes a presentation request (Phase 1129), never a row. The server
   reads the series it names under the disclosure gate at the `FactToolResult` surface, the same door every fact tool's
   result passes, and draws the block. It answers with a text summary the model reads and the block the assistant
   message carries (Phase 1131). The summary carries counts, the canonical period, the title and the withholding
   policies, and never a value. A malformed request, an unknown table, metric or level, and a kind the other tool draws
   are refused by name. A caller the policy denies every point gets a refusal, not an empty chart.
2. **Their own package.** `ToolUp.Facts.Presentation.Server` holds them, so the Fuaran-UI dependency comes only with them.
   `ToolUp.Facts.Server` stays free of it.
3. **`AIServerApp.AIAnsweringTools`.** The record-form sibling of `ServerApp.AITools`, which holds string executors only.
   `composeAI` registers it through `AIToolRegistry.createAnsweringTool`, under the same tool-name collision check, tool
   policy and effect envelope. Append to it with `AIServerApp.withAIAnsweringTools`.
4. **`FactBrowseHandler.factBrowseDeps`.** The fact browse's per-request dependencies, public, so an in-process reader
   takes them as a value instead of resolving the container itself.

## What to do

Reference `ToolUp.Facts.Presentation.Server` and add one compose line to the AI composition, after the fact store:

```fsharp skip=fragment
AIServerApp.createFrom factory providerProfile app
|> PresentationCompose.withRenderTools
|> composeAI
```

The tools take ids they cannot list, so name the fact tables in the assistant's standing context, for example as a
static layer of the system prompt:

```fsharp skip=fragment
SystemPrompt = Some(SystemPromptBuilder.compose [
    SystemPromptBuilder.fromStatic "Fact tables you may chart: daily-trade (metric revenue; hierarchy sites, level site)."
    SystemPromptBuilder.activeModuleContext
])
```

A literal `AIServerApp` gains `AIAnsweringTools = []`. Nothing else moves.

## Verification

`dotnet build` names every literal that needs the field (FS0764). In the Platform pack, `Phase 1132 — render tools draw a
chart or table from gated facts` checks the refusals, the summary's wording, the part, the door, the composition and a
scripted turn end to end. `Phase 1132 the tool-result door for a block`, in the disclosure egress pack, checks that a
withheld point is a count and never a value, and that a denied caller gets a refusal.

## Rollback

Drop the compose line and the package reference. A conversation saved with a block keeps it, and the panel still
rebuilds it from its request under the reader's gate.

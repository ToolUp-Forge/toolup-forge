# `evaluate_pipeline` composed: the answering form and the pipeline block

**Ships in:** ToolUp.Facts.Presentation, ToolUp.Facts.Presentation.Server and ToolUp.AI.Client 0.38.0. The new
composition is opt-in; one change to `ToolUp.AI.Client` breaks an exhaustive match.

**Affected:** a deployment that wants the assistant to compute over its facts adds one compose line. Code that matches
`BlockRender.BlockOutcome` exhaustively adds one case. Nobody else has to change anything.

## What changes

1. **`PresentationCompose.withPipelineTool`** (in `ToolUp.Facts.Presentation.Server`) registers `evaluate_pipeline` in
   the answering form, beside `render_chart` and `render_table`, and mounts `IPipelineBlockApi` at
   `/api/_facts/pipeline-block/*`. It is idempotent and does nothing under `NoFactStore`. The executor
   (`PipelineTool.answerWith`) runs unchanged, judged at `FactToolResult`.
2. **What the turn carries.** The model reads `PipelineTool.summary`: the shape, the columns and the withheld report,
   never a value. The assistant message carries one block part of kind `"pipeline"` (`PipelineBlock.Kind`). Its
   provenance holds the request in the tool's own argument shape, with the canonical pipeline, `pipeline_hash` and
   `run_stamp`, the fact ids the lineage read and the withheld report. Its node is the grid's frame: the title and the
   column names, with no row. No cell value is persisted.
3. **The re-run.** `IPipelineBlockApi.Evaluate` takes the part's request and runs the same executor for the signed-in
   reader, judged at `FactBrowse`. `PipelineAnsweringTool.rerunWith` is the same call over explicit dependencies.
4. **The panel.** `BlockRender.Block` draws a pipeline block through `BlockRender.resolvePipeline`. It sends the part's
   request, draws the reader's result as a grid (`PipelineBlock.redraw`) with withheld columns named, and shows a note
   (`data-block-recomputed`) for each way the reader's result differs from the recorded one: a run committed since,
   more or less withheld, different facts read. The outcome is the new case `BlockOutcome.Computed(block, differences)`.
   The re-run defaults to the server's endpoint (`BlockRender.remotePipelineResolver`), so a client boot registers
   nothing; `BlockRender.registerPipelineResolver` installs another, which is what a test does.
5. **`ToolUp.Facts.Presentation`** gains `PipelineBlockRequest`, `PipelineBlockResult`, `PipelineBlockColumn`,
   `IPipelineBlockApi` and the `PipelineBlock` module. All of it is Fable-clean, so the panel and the server draw the
   same grid.

## What to do

To compose the tool, add one line beside the render tools:

```fsharp skip=fragment
AIServerApp.createFrom factory providerProfile app
|> PresentationCompose.withRenderTools
|> PresentationCompose.withPipelineTool
|> composeAI
```

The tool takes table, metric and level ids it cannot list, so name the fact tables in the assistant's standing context,
as for the render tools:

```fsharp skip=fragment
SystemPromptBuilder.fromStatic "Fact tables you may compute over: daily-trade (metric revenue; hierarchy sites, level site)."
```

If your code matches `BlockRender.BlockOutcome` with no wildcard, add the case:

```fsharp skip=fragment
| BlockRender.BlockOutcome.Computed(block, differences) -> ...
```

## Verification

In the Platform pack, `Phase 1145 — evaluate_pipeline computes over a gated fact slice` / `the answering form and the
pipeline block` checks the summary and the part, that no cell value is persisted, both doors, the reader's grid
against the reference evaluator, and a reader whose gate differs. `Phase 1132 — render tools …` / `the composition`
checks `withPipelineTool`, and `end to end` runs a scripted turn through the composed tool and re-runs its block. In the
Fable harness, `Conversation pipeline block (Phase 1156)` draws a block whose re-run differs from the recorded one.

## Rollback

Remove the `withPipelineTool` line. Pipeline blocks already in a conversation then render a refusal from the unmounted
endpoint instead of a grid; nothing else changes.

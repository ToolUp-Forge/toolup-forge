# `evaluate_pipeline`: compute over a gated fact slice

**Ships in:** ToolUp.Facts.Core and ToolUp.Facts.Server 0.38.0. It is additive, and nothing is composed anew.

**Affected:** nobody has to change anything. This note says what arrived, and the one line a deployment will add when
the tool is composed.

## What changes

1. **The executor.** `PipelineTool.answerWith` (in `ToolUp.Facts.Server`) takes the fact browse's dependencies, the
   caller's resolved scope and principal, and the tool's arguments: a fact table, a period range, an optional subject
   prefix and metric set, and a `Fuaran.Compute` pipeline written as data. It reads the slice through the browse's own
   slice read, judged at `FactToolResult`, and refuses an empty slice or one whose every cell is withheld. It decodes
   and walks the pipeline, refusing by name a malformed step, an unknown column, an output it cannot name, another
   source, a parameter or the clock. It evaluates the pipeline with the reference evaluator, and refuses a result over
   the slice's cell cap. Any output column whose lineage reads a metric with a withheld cell is withheld, with its count
   by policy. `PipelineTool.summary` is the text the model reads: the shape, the columns and the withheld report,
   never a value. `PipelineTool.definition` is the tool's declaration.
2. **The types.** `PipelineToolRequest`, `PipelineToolResult`, `PipelineWithheldColumn`, `PipelineWithheldCount` and
   `PipelineRefusal` (with `PipelineRefusal.describe`) in `ToolUp.Facts.Core`. The pipeline and the result table travel
   as their canonical codec strings, so the tier takes no columnar dependency.
3. **`FactBrowseHandler.atSurface`.** Re-asks a held gate at another egress surface, keeping its row form. It was
   private to the render tools, and both readers now share it.

## What to do

Nothing. No composition registers `evaluate_pipeline` yet, because its result is meant to reach the user as a block on
the assistant turn: the record form a tool answers belongs to the AI composition, and the conversation panel does not
yet re-run a pipeline block. A deployment's tool list is unchanged.

When it is composed, the tool takes table, metric and level ids it cannot list. As with the render tools, the deployment
names its fact tables in the assistant's standing context. The line it adds is the same one:

```fsharp skip=fragment
SystemPromptBuilder.fromStatic "Fact tables you may compute over: daily-trade (metric revenue; hierarchy sites, level site)."
```

## Verification

In the Platform pack, `Phase 1145 — evaluate_pipeline computes over a gated fact slice` checks the refusals, the
reference evaluator's answer, the withheld rule, the door and the summary's wording. In the disclosure egress pack,
`Phase 1145 the tool-result door for a pipeline result` checks that a withheld value is a count and never a value, and
that a denied caller gets a refusal.

## Rollback

Nothing is composed, so there is nothing to roll back.

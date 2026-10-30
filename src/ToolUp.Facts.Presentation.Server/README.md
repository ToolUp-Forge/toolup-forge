# ToolUp.Facts.Presentation.Server

The `render_chart`, `render_table` and `evaluate_pipeline` AI tools in the answering form. The model writes a *presentation request* (the
`ToolUp.Facts.Presentation` vocabulary: a kind, a fact table, its metric(s), the subjects and a
period) and never a row. The server reads the series the request names through the fact browse's
series read, under the disclosure gate at the tool-result door. It draws the block with the
presentation builders and answers with a text summary the model reads and the block the assistant
message carries.

## What it gives you

- **`PresentationCompose.withRenderTools`** composes both tools onto the AI composition. It is
  idempotent, and it does nothing under `NoFactStore`.
- **`RenderTools.answerWith`** is the executor over explicit dependencies: the fact browse's
  dependencies, the caller's resolved scope and principal. **`RenderTools.answer`** is the
  `HttpContext` adapter the composition registers.
- **Refused by name.** A malformed request, an unknown table, metric or subject level, and a kind
  the other tool draws are each refused with a sentence naming what to repair.
- **The model reads counts, never values.** The summary names the kind, the metric, the table, the
  canonical period, the title, how many points were read and how many were withheld under which
  policy. A withheld point is a count there and is never drawn. A caller the policy denies every
  point gets a refusal, not an empty chart.

- **`PresentationCompose.withPipelineTool`** composes `evaluate_pipeline` in the same answering
  form and mounts `IPipelineBlockApi`. The model reads the executor's summary, and the message
  carries a pipeline block that persists the request and never a row. A reader's panel sends that
  request to the endpoint, which evaluates it again for the reader under the disclosure gate at the
  browse door.
- **`PresentationCompose.withDerivedAssertions`** lets `evaluate_pipeline` assert one numeric
  column of its result as facts of a registered derived metric (the `assert` argument), each naming
  the facts it was computed from, so a figure the assistant quotes verifies and a narrative may cite
  it. Without it the argument is absent and refused.

```fsharp skip=fragment
open ToolUp.AI.AICompose
open ToolUp.Facts.Presentation

AIServerApp.createFrom factory providerProfile app
|> PresentationCompose.withRenderTools
|> PresentationCompose.withPipelineTool
|> PresentationCompose.withDerivedAssertions // optional
|> composeAI
```

## Dependencies

`ToolUp.Facts.Presentation`, `ToolUp.Facts.Server` and `ToolUp.AI.Server`. The published
Apache-2.0 `Fuaran.UI` packages arrive through `ToolUp.Facts.Presentation`, so a deployment that
does not reference this package carries none of them.

Apache-2.0.

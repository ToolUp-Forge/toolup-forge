# A pipeline result becomes a `Computed` fact: derived assertions

**Ships in:** ToolUp.Facts.Core, ToolUp.Facts.Server and ToolUp.Facts.Presentation.Server 0.38.0. The new composition
is opt-in; one change to `ToolUp.Facts.Core` breaks an exhaustive match.

**Affected:** a deployment that wants the assistant's computed figures to be quotable and citable adds one compose line
and registers the derived metrics it may assert. Code that matches `PipelineRefusal` exhaustively adds the new cases.
Nobody else has to change anything.

## What changes

1. **`PresentationCompose.withDerivedAssertions`** (in `ToolUp.Facts.Presentation.Server`) composes `evaluate_pipeline`
   (through `withPipelineTool` when it is not composed yet) with the `assert` argument in its declaration
   (`PipelineTool.definitionWithAssertions`) and the write it may make declared beside its read
   (`WriteState "_facts"`). It does nothing under `NoFactStore` and is idempotent.
2. **What `assert` does** (`PipelineAssertion.answerWith`). The evaluation runs exactly as before. Then one numeric
   column of the result is asserted, one fact per result row, through one `IFactStore.AssertBatch`. Each fact has the
   row's subject (the result must keep the slice's `subject` or `subject_path` as a key), the slice's period, the
   method `Computed("fuaran.compute.pipeline", <Fuaran.Compute.DataFrame version>, <pipeline hash>)`, and evidence
   naming every input fact as `fact:<id>` (`FactInput`). The model reads the facts' ids and renderings, so a figure it
   quotes verifies under the numeric answer gate, and the grounding gate admits a citation of it.
3. **Disclosure.** The taint walk reads the facts a derived fact names as its computed lineage: its label is the join
   of its inputs' labels. A `TaintPropagating` restricted input denies it at every door until a declassification
   routine registered for `fuaran.compute.pipeline` clears it. A deployment with no taint-propagating policy sees no
   change.
4. **Without the compose line**, `PipelineTool.answerWith` refuses a request carrying `assert` with the new case
   `PipelineRefusal.AssertionNotOffered`.
5. **`ToolUp.Facts.Core`** gains `PipelineAssertionRequest`, `PipelineAssertedFact`, `PipelineAssertionResult`, the
   `FactInput` module, `DisclosureTaintNode.namedInputs` / `inferredInputs`, and fourteen `PipelineRefusal` cases (the
   assertion's refusals). **`ToolUp.Facts.Server`** gains the `PipelineAssertion` module, `PipelineTool.assertionOf`,
   `PipelineTool.definitionWithAssertions` and `PipelineTool.AssertArgument`.

## What to do

To let the assistant assert, add one line beside the pipeline tool:

```fsharp skip=fragment
AIServerApp.createFrom factory providerProfile app
|> PresentationCompose.withPipelineTool
|> PresentationCompose.withDerivedAssertions
|> composeAI
```

and register each derived metric the assistant may assert, naming the pipeline operation as its producer:

```fsharp skip=fragment
ServerModule.create "Trade"
|> ServerModule.declareMetrics [ {
    Id = "site_total"; Name = "Site total"; Unit = "GBP"; Dimensionality = "currency"
    Direction = HigherIsBetter; DisplayFormat = "N2"; Staleness = UntilSuperseded
    ProducingOperation = Some PipelineAssertion.OperationId
    CanonicalMethod = None; RecomputePolicy = None; RollUp = Some(Additive 0.01m); Context = None } ]
```

A metric that is not registered, or not registered as produced by `PipelineAssertion.OperationId`, is refused by name,
so the assistant never asserts a source metric.

If you match `PipelineRefusal` exhaustively, add the new cases, or fall through to `PipelineRefusal.describe`.

## Verifying

Ask the assistant for a per-subject total with `assert` naming a registered derived metric and the table's level. The
tool's text carries one `factId` and `rendering` per subject; the figure the assistant quotes verifies under `Strict`;
the fact is in the store with a `Computed` method naming the pipeline hash. Re-run after correcting one input fact: that
subject's derived fact supersedes, the others are unchanged.

## Rolling back

Remove the `withDerivedAssertions` line. Facts already asserted stay in the store, as every fact does; their lineage
keeps its correction history.

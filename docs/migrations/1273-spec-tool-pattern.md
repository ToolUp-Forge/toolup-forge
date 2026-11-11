# A tool-result pattern for "spec in, Facts out"

**Ships in:** ToolUp.Facts.Core and ToolUp.Facts.Server 0.42.0. Additive: no existing call changes, and
`evaluate_pipeline` answers exactly as before (its refusal text, summary, block part and asserted facts).

**Affected:** a deployment that wants its own AI tool over the deployment's facts in the shape `evaluate_pipeline`
has: the model writes a closed spec, the server checks it before reading anything, evaluates it over a gated slice,
shows the result without the model retyping it, and asserts rows of it as facts.

## What changes

1. **`ISpecRefusal`** (ToolUp.Facts.Core): `Name`, `Remedy` and `Admissible`. A spec tool's refusal type implements
   it; the door frames every refusal from it. `PipelineRefusal` implements it (`PipelineRefusal.name` and
   `PipelineRefusal.admissible` are added beside `describe`).
2. **`SpecDoorRefusal`, `SpecResult`, `SpecFactRow`** (ToolUp.Facts.Core): the door's own refusals, what an evaluation
   answers as the door reads it, and a row declared as a fact. `PipelineWithheldCount` and `PipelineWithheldColumn`
   now live beside them (same namespace and names; nothing to change) and `PipelineWithheldCount.Phrase` renders a
   withheld report.
3. **`SpecTool<'Spec, 'Refusal, 'Result>`** (ToolUp.Facts.Server): the record a deployment fills in (`Decode`,
   `Validate`, `Evaluate`, `Hash`, `Project`, `Summary`, `OfDoor`, the definition, the operation id and version), and
   the `SpecTool` module, the door: `answer` / `answerText` over explicit dependencies, `executor` for a request,
   `refusalText`, and the pieces a tool composes (`sliceOf`, `sliceParameters`, `textOf`, `parse`, `readable`,
   `withinCap`, `derivedMetric`, `checkRow`, `draftOf`, `assertRows`).
4. **`FactsCompose.withSpecTool tool`**: declares the tool and the door's executor on `ServerApp.AITools`, beside the
   fact tools. With no fact store it changes nothing; a second tool of the same name is refused.
5. **`PipelineTool.specToolUnder profile`** is `evaluate_pipeline` as a `SpecTool`, and `PipelineTool.OperationId` /
   `ComputeVersion` are where `PipelineAssertion`'s same-named values now read from.

## A worked registration

A tool totalling one metric per subject, asserting the totals as a derived metric:

```fsharp skip=fragment
type TotalsSpec = { Metric: string; AssertAs: string option }

type TotalsRefusal =
    | Door of SpecDoorRefusal
    | MetricNotRequested of metric: string * requested: string list

    interface ISpecRefusal with
        member this.Name =
            match this with
            | Door d -> (d :> ISpecRefusal).Name
            | MetricNotRequested _ -> "MetricNotRequested"
        member this.Remedy =
            match this with
            | Door d -> (d :> ISpecRefusal).Remedy
            | MetricNotRequested(m, _) -> sprintf "metric '%s' is not among the slice's metrics; total one of them" m
        member this.Admissible =
            match this with
            | MetricNotRequested(_, requested) -> requested
            | Door d -> (d :> ISpecRefusal).Admissible

let totals: SpecTool<TotalsSpec, TotalsRefusal, MyTotals> = {
    Definition = { myDefinition with Parameters = SpecTool.sliceParameters @ [ totalsParameter ] }
    OperationId = "acme.totals"                       // the ProducingOperation of the derived metric
    Version = "1"
    Decode = decodeTotals                             // SpecTool.sliceOf root, then the "totals" argument
    Validate = fun _scope _principal request ->       // every refusal, together
        if List.contains request.Spec.Metric request.Slice.Metrics then Ok request.Spec
        else Error [ MetricNotRequested(request.Spec.Metric, request.Slice.Metrics) ]
    Evaluate = evaluateTotals                         // over SpecRead.Slice, the gated slice
    Hash = fun spec -> sprintf "totals:%s" spec.Metric
    Project = fun t -> { Table = t.Table; Rows = t.Rows; Columns = t.Columns; Withheld = []; Facts = t.Rows' }
    Summary = fun t -> sprintf "Totalled %d subject(s)." t.Rows
    OfDoor = Door
}

let app = app |> FactsCompose.withFactStore |> FactsCompose.withSpecTool totals
```

Register the derived metric with `ProducingOperation = Some "acme.totals"`; the door refuses a row of any other
metric. A result whose `Facts` is empty is shown and asserts nothing.

## Not included

- **A block part for a deployment's spec tool.** `withSpecTool` registers a string-returning tool. The pipeline's
  block (`PipelineAnsweringTool`, Phase 1156) persists the pipeline request and is drawn by re-running it under the
  reader's gate, so it is particular to that spec; a spec-generic block needs a generic block request and re-run.
- **The base card** (Phase 1272) as an asserted fact's evidence: that phase has not shipped.

## Verification

- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 1273"`:
  a second, test-only spec through every door.
- The `evaluate_pipeline` packs (`Phase 1145`, `Phase 1146`, `Pipeline profile`, `Phase 525`) are unchanged and green.

## Rollback

Nothing to roll back in a consumer: no existing call moved. Drop the `withSpecTool` call to remove a tool.

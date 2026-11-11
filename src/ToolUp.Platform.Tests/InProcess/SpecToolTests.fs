// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.SpecToolTests

// ─── The spec-tool door ──────────────────────────────────────────────
//
// `SpecTool` is the door every "spec in, Facts out" tool goes through:
// decode, validate before any read, the gated slice read, the cap, the
// assertion door and the refusal framing. `evaluate_pipeline` is its first
// instance (its own pack pins it unchanged); this pack drives a SECOND,
// test-only spec through every door:
//
//   1. **Refused by name, together.** The validator's refusals come back in
//      one answer, each with its remedy, and the framing is pinned; a door
//      refusal (malformed arguments, no facts, nothing disclosable) reads in
//      the door's words.
//   2. **A block, not a fact.** A result that declares no row is answered as
//      its summary and asserts nothing.
//   3. **Facts out.** Declared rows are asserted with `Computed(operation,
//      version, spec hash)`, each naming the facts it was computed from, and
//      a metric another operation produces is refused.
//   4. **The cap and the budget bind.** A result over the cell cap is
//      refused; the answer is held to the definition's result budget.
//   5. **Registration.** `FactsCompose.withSpecTool` declares the tool beside
//      the fact tools and refuses a second tool of the same name.

open System
open Expecto
open Fuaran.Core
open Fuaran.Compute
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.Facts
open ToolUp.Platform.Tests.InProcess.FactRenderToolTests.RenderFixtures
open ToolUp.Platform.Infra
open ToolUp.Platform.Compose

// ─── A second spec: per-subject totals ──────────────────────────────

/// The test spec: the total of one metric per subject, optionally asserted.
type TotalsSpec = {
    Metric: string
    Scale: int
    AssertAs: string option
}

/// The test spec's own refusals.
type TotalsRefusal =
    | Door of SpecDoorRefusal
    | MalformedTotals of reason: string
    | ScaleOutOfRange of scale: int
    | MetricNotRequested of metric: string * requested: string list
    | Evaluation of reason: string

    interface ISpecRefusal with
        member this.Name =
            match this with
            | Door d -> (d :> ISpecRefusal).Name
            | MalformedTotals _ -> "MalformedTotals"
            | ScaleOutOfRange _ -> "ScaleOutOfRange"
            | MetricNotRequested _ -> "MetricNotRequested"
            | Evaluation _ -> "Evaluation"

        member this.Remedy =
            match this with
            | Door d -> (d :> ISpecRefusal).Remedy
            | MalformedTotals reason -> sprintf "the 'totals' argument is malformed: %s" reason
            | ScaleOutOfRange scale -> sprintf "scale %d is outside 1..10; write a scale between them" scale
            | MetricNotRequested(metric, _) ->
                sprintf "metric '%s' is not among the slice's metrics; total one of them" metric
            | Evaluation reason -> sprintf "the totals failed over the slice: %s" reason

        member this.Admissible =
            match this with
            | MetricNotRequested(_, requested) -> requested
            | Door d -> (d :> ISpecRefusal).Admissible
            | _ -> []

[<Literal>]
let private Operation = "test.totals"

let private perSubject (metric: string) : Transform list = [
    GroupBy(
        [ FactBrowseSlice.SubjectColumn ],
        [
            {
                Name = "total"
                Fn = Sum
                Of = metric
            }
        ]
    )
    Transform.sortBy [ FactBrowseSlice.SubjectColumn, Asc ]
]

let private decodeTotals (root: Text.Json.JsonElement) : Result<SpecRequest<TotalsSpec>, TotalsRefusal> =
    match SpecTool.sliceOf root with
    | Error door -> Error(Door door)
    | Ok slice ->
        match root.TryGetProperty "totals" with
        | true, v when v.ValueKind = Text.Json.JsonValueKind.Object ->
            match SpecTool.textOf "metric" v, v.TryGetProperty "scale" with
            | Ok metric, (true, scale) when scale.ValueKind = Text.Json.JsonValueKind.Number ->
                let assertAs =
                    match SpecTool.textOf "assert_as" v with
                    | Ok m -> Some m
                    | Error _ -> None

                Ok {
                    Slice = slice
                    Spec = {
                        Metric = metric
                        Scale = scale.GetInt32()
                        AssertAs = assertAs
                    }
                }
            | _ -> Error(MalformedTotals "it names 'metric' and a numeric 'scale'")
        | _ -> Error(MalformedTotals "'totals' is required: an object")

/// Both static checks, returned together.
let private validateTotals
    (_: ResolvedScope)
    (_: string)
    (request: SpecRequest<TotalsSpec>)
    : Result<TotalsSpec, TotalsRefusal list> =
    let spec = request.Spec

    match
        [
            if spec.Scale < 1 || spec.Scale > 10 then
                ScaleOutOfRange spec.Scale
            if not (List.contains spec.Metric request.Slice.Metrics) then
                MetricNotRequested(spec.Metric, request.Slice.Metrics)
        ]
    with
    | [] -> Ok spec
    | refusals -> Error refusals

/// The evaluation's result: the totals, and the rows to assert.
type private Totals = {
    Result: PipelineToolResult
    Rows: SpecFactRow list
}

let private evaluateTotals (read: SpecRead) (request: SpecRequest<TotalsSpec>) : Async<Result<Totals, TotalsRefusal>> = async {
    let pipeline = perSubject request.Spec.Metric

    let pipelineRequest: PipelineToolRequest = {
        TableId = request.Slice.TableId
        PeriodFrom = request.Slice.PeriodFrom
        PeriodTo = request.Slice.PeriodTo
        PathPrefix = request.Slice.SubjectPrefix
        Metrics = request.Slice.Metrics
        Pipeline = DataFrameCodec.encodePipeline pipeline
    }

    match PipelineTool.evaluate pipelineRequest pipeline read.Slice read.ReadAt with
    | Error refusal -> return Error(Evaluation(PipelineRefusal.describe refusal))
    | Ok result ->
        match request.Spec.AssertAs with
        | None -> return Ok { Result = result; Rows = [] }
        | Some metric ->
            // The rows are read as this tool's own derived metric and then
            // declared as the one asked for, so the door, not this
            // evaluator, is what judges the metric.
            match
                PipelineAssertion.rowsOf Operation read.Registry read.Slice result {
                    Column = "total"
                    AsMetric = "spec_total"
                    Level = "site"
                }
            with
            | Ok rows ->
                return
                    Ok {
                        Result = result
                        Rows =
                            rows
                            |> List.map (fun r -> {
                                r with
                                    Metric = metric
                                    Value = r.Value * decimal request.Spec.Scale
                            })
                    }
            | Error refusal -> return Error(Evaluation(PipelineRefusal.describe refusal))
}

let private definition: AIToolDefinition = {
    PipelineTool.definition with
        Name = "total_metric"
        Description =
            "Total one metric per subject over a slice. A result that names no subject is shown as a block and asserts no fact."
        Parameters =
            SpecTool.sliceParameters
            @ [
                ToolSchema.parameter
                    "totals"
                    "The metric to total, a scale and optionally the derived metric to assert as."
                    true
                    (ToolSchema.objectOf [])
            ]
}

let private totalsTool: SpecTool<TotalsSpec, TotalsRefusal, Totals> = {
    Definition = definition
    OperationId = Operation
    Version = "1"
    Decode = decodeTotals
    Validate = validateTotals
    Evaluate = evaluateTotals
    Hash = fun spec -> sprintf "totals:%s:%d" spec.Metric spec.Scale
    Project =
        fun t -> {
            Table = t.Result.Table
            Rows = t.Result.Rows
            Columns = t.Result.Columns
            Withheld = t.Result.Withheld
            Facts = t.Rows
        }
    Summary = fun t -> sprintf "Totalled %d subject(s)." t.Result.Rows
    OfDoor = Door
}

// ─── Fixtures ────────────────────────────────────────────────────────

let private registration (id: string) (producing: string option) : MetricRegistration = {
    Module = "trade"
    Definition = {
        Id = id
        Name = id
        Unit = "GBP"
        Dimensionality = "currency"
        Direction = HigherIsBetter
        DisplayFormat = "N2"
        Staleness = UntilSuperseded
        ProducingOperation = producing
        CanonicalMethod = None
        RecomputePolicy = None
        RollUp = Some(Additive 0.01m)
        Context = None
    }
}

let private totalsRegistry: IMetricRegistry =
    MetricRegistry.build [
        registration "revenue" None
        registration "margin" None
        registration "spec_total" (Some Operation)
        registration "site_total" (Some PipelineAssertion.OperationId)
    ] [
        {
            SubjectRegistration.Module = "trade"
            Definition = {
                Id = "sites"
                Name = "Sites"
                Levels = [ "region"; "site" ]
                Calendar = None
            }
        }
    ]

let private totalsWorld () : World =
    let w = world ()

    {
        w with
            Deps = {
                w.Deps with
                    Registry = Some totalsRegistry
            }
    }

let private argsWith (table: string) (metric: string) (days: int) (totals: string) : string =
    sprintf
        """{"table":"%s","period_from":"%s","period_to":"%s","metrics":["%s"],"totals":%s}"""
        table
        (day1.ToString "yyyy-MM-dd")
        ((day1.AddDays(float days)).ToString "yyyy-MM-dd")
        metric
        totals

let private revenueTotals (totals: string) = argsWith trade.Id "revenue" 3 totals

let private answerOf (tool: SpecTool<TotalsSpec, TotalsRefusal, Totals>) (w: World) (args: string) =
    SpecTool.answer tool w.Deps w.Scope "u" args |> Async.RunSynchronously

let private textOf (tool: SpecTool<TotalsSpec, TotalsRefusal, Totals>) (w: World) (args: string) =
    SpecTool.answerText tool w.Deps w.Scope "u" args |> Async.RunSynchronously

let private refusalsOf (w: World) (args: string) : TotalsRefusal list =
    match answerOf totalsTool w args with
    | Error refusals -> refusals
    | Ok _ -> failtest "expected a refusal"

// ─── 1. Refused by name, together ───────────────────────────────────

let private refusalTests =
    testList "refused by name, together" [
        test "the validator's refusals are returned together, each with its remedy" {
            let w = totalsWorld ()
            let args = revenueTotals """{"metric":"margin","scale":0}"""

            Expect.equal
                (refusalsOf w args)
                [ ScaleOutOfRange 0; MetricNotRequested("margin", [ "revenue" ]) ]
                "both static refusals, in one answer"

            Expect.equal
                (textOf totalsTool w args)
                "total_metric refused the request for 2 reasons: (1) scale 0 is outside 1..10; write a scale between them; (2) metric 'margin' is not among the slice's metrics; total one of them."
                "the framing the model reads is pinned"
        }

        test "a refusal names what would have been admitted" {
            let refusal = MetricNotRequested("margin", [ "revenue" ]) :> ISpecRefusal
            Expect.equal refusal.Name "MetricNotRequested" "the name"
            Expect.equal refusal.Admissible [ "revenue" ] "the admissible set"
        }

        test "one refusal reads as one sentence" {
            Expect.equal
                (SpecTool.refusalText "total_metric" [ ScaleOutOfRange 11 ])
                "total_metric refused the request: scale 11 is outside 1..10; write a scale between them."
                "one sentence"
        }

        test "the validator runs before any read" {
            // An unknown table would be refused by the slice read; the static
            // refusal answers first.
            let w = totalsWorld ()
            let args = argsWith "no-such-table" "revenue" 3 """{"metric":"revenue","scale":0}"""
            Expect.equal (refusalsOf w args) [ ScaleOutOfRange 0 ] "refused before the read"
        }

        test "malformed arguments are the door's refusal" {
            let w = totalsWorld ()

            Expect.equal
                (refusalsOf w "not json")
                [ Door(SpecDoorRefusal.MalformedArguments "the arguments are not valid JSON") ]
                "not JSON"

            Expect.equal
                (textOf totalsTool w """{"totals":{}}""")
                "total_metric refused the request: 'table' is required: a non-empty string."
                "the slice arguments are read by the door"
        }

        test "a slice with no fact is refused" {
            let w = totalsWorld ()

            let args =
                sprintf
                    """{"table":"%s","period_from":"2030-01-01","period_to":"2030-01-02","metrics":["revenue"],"totals":{"metric":"revenue","scale":1}}"""
                    trade.Id

            Expect.equal (refusalsOf w args) [ Door(SpecDoorRefusal.NoFacts trade.Id) ] "no fact"
        }

        test "a slice every cell of which is withheld is refused, never an empty table" {
            let w = totalsWorld ()
            let args = argsWith margins.Id "margin" 3 """{"metric":"margin","scale":1}"""

            match refusalsOf w args with
            | [ Door(SpecDoorRefusal.NothingDisclosable(6,
                                                        [ {
                                                              PolicyRef = "trade-figures"
                                                              Count = 6
                                                          } ])) ] -> ()
            | other -> failtestf "expected nothing disclosable, got %A" other
        }

        test "evaluate_pipeline's refusal is a spec refusal reading as before" {
            let refusal = PipelineRefusal.UnknownColumn(2, "x", [ "a"; "b" ]) :> ISpecRefusal

            Expect.equal refusal.Name "UnknownColumn" "the case"

            Expect.equal
                refusal.Remedy
                (PipelineRefusal.describe (PipelineRefusal.UnknownColumn(2, "x", [ "a"; "b" ])))
                "the sentence"

            Expect.equal refusal.Admissible [ "a"; "b" ] "the columns"

            Expect.equal
                (PipelineTool.refusalText (PipelineRefusal.NoFacts "t"))
                "evaluate_pipeline refused the request: no fact of table 't' answers the slice; widen the period range or the subject prefix."
                "a door case reads in the pipeline's words"
        }
    ]

// ─── 2. A block, not a fact ──────────────────────────────────────────

let private blockTests =
    testList "a result that names no fact is a block" [
        test "no declared row: the summary, and nothing asserted" {
            let w = totalsWorld ()

            match answerOf totalsTool w (revenueTotals """{"metric":"revenue","scale":1}""") with
            | Ok(totals, None) ->
                Expect.equal totals.Result.Rows 2 "one row per site"
                Expect.equal totals.Result.Columns [ FactBrowseSlice.SubjectColumn; "total" ] "the columns"
            | Ok(_, Some _) -> failtest "a result naming no row asserted a fact"
            | Error refusals -> failtestf "expected a result, got %A" refusals

            Expect.equal
                (textOf totalsTool w (revenueTotals """{"metric":"revenue","scale":1}"""))
                "Totalled 2 subject(s)."
                "the model reads the summary alone"
        }
    ]

// ─── 3. Facts out ────────────────────────────────────────────────────

let private factTests =
    testList "declared rows become facts" [
        test "rows are asserted with the tool's method, naming their inputs" {
            let w = totalsWorld ()

            let args =
                revenueTotals """{"metric":"revenue","scale":2,"assert_as":"spec_total"}"""

            match answerOf totalsTool w args with
            | Ok(_, Some asserted) ->
                Expect.equal (List.length asserted.Facts) 2 "one fact per site"
                Expect.equal asserted.AssertedCount 2 "both new"

                for f in asserted.Facts do
                    Expect.equal f.Metric "spec_total" "the declared metric"
                    Expect.equal f.InputCount 3 "three days of the site's revenue"
                    Expect.isSome f.Rendering "the caller may be shown it"

                    match
                        w.Deps.Store.Get(w.Scope, f.FactId)
                        |> FactStoreResult.orRaiseAsync
                        |> Async.RunSynchronously
                    with
                    | Some fact ->
                        Expect.equal
                            fact.Method
                            (Computed(Operation, "1", "totals:revenue:2"))
                            "the spec hash is the method"
                    | None -> failtest "the asserted fact is not stored"
            | Ok(_, None) -> failtest "expected an assertion"
            | Error refusals -> failtestf "expected an assertion, got %A" refusals
        }

        test "the asserted answer names each fact for the verifier" {
            let w = totalsWorld ()

            let text =
                textOf totalsTool w (revenueTotals """{"metric":"revenue","scale":1,"assert_as":"spec_total"}""")

            Expect.stringContains text "\"factId\"" "fact ids"
            Expect.stringContains text SpecTool.AssertedNote "the note"
        }

        test "a metric another operation produces is refused" {
            let w = totalsWorld ()

            let args =
                revenueTotals """{"metric":"revenue","scale":1,"assert_as":"site_total"}"""

            Expect.equal
                (refusalsOf w args)
                [
                    Door(SpecDoorRefusal.NotADerivedMetric("site_total", Some PipelineAssertion.OperationId, Operation))
                ]
                "not this tool's metric"

            Expect.equal
                (textOf totalsTool w args)
                "total_metric refused the request: metric 'site_total' is registered as produced by 'fuaran.compute.pipeline', not by the operation 'test.totals', so the assistant does not assert it."
                "pinned"
        }
    ]

// ─── 4. The cap and the budget bind ─────────────────────────────────

let private boundTests =
    testList "the cap and the budget bind" [
        test "a result over the cell cap is refused" {
            let w = totalsWorld ()

            let overCap = {
                totalsTool with
                    Project =
                        fun t -> {
                            totalsTool.Project t with
                                Rows = FactBrowseApi.MaxSliceCells
                        }
            }

            match answerOf overCap w (revenueTotals """{"metric":"revenue","scale":1}""") with
            | Error [ Door(SpecDoorRefusal.ResultOverCap(cells, cap)) ] ->
                Expect.equal cells (FactBrowseApi.MaxSliceCells * 2) "rows times columns"
                Expect.equal cap FactBrowseApi.MaxSliceCells "the slice's cap"
            | other -> failtestf "expected the cap, got %A" other
        }

        test "the answer is held to the definition's result budget" {
            let w = totalsWorld ()

            let tight = {
                totalsTool with
                    Definition = {
                        totalsTool.Definition with
                            ResultBudget = ResultBudgetChars 5
                    }
            }

            let text = textOf tight w (revenueTotals """{"metric":"revenue","scale":1}""")

            let budgeted, elided =
                AIToolRegistry.ToolResultBudget.apply tight.Definition.Name tight.Definition.ResultBudget text

            Expect.equal elided (Some text.Length) "the whole answer is over the budget"
            Expect.isFalse (budgeted.Contains "Totalled") "the elided answer carries none of it"
        }
    ]

// ─── 5. Registration ─────────────────────────────────────────────────

let private factsApp () : ServerApp =
    {
        ServerApp.empty with
            Config = {
                ServerConfig.defaults with
                    Stores = {
                        ServerConfig.defaults.Stores with
                            FactStore = FeatureMode.Enabled
                    }
            }
    }
    |> FactsCompose.withFactStore

let private registrationTests =
    testList "registration" [
        test "the tool is declared beside the fact tools" {
            let app = factsApp () |> FactsCompose.withSpecTool totalsTool
            let names = app.AITools |> List.map (fun (d, _) -> d.Name)
            Expect.contains names "total_metric" "declared"
            Expect.contains names "query_facts" "beside the fact tools"
        }

        test "a second tool of the same name is refused" {
            Expect.throws
                (fun () ->
                    factsApp ()
                    |> FactsCompose.withSpecTool totalsTool
                    |> FactsCompose.withSpecTool totalsTool
                    |> ignore)
                "one tool per name"
        }

        test "no fact store: nothing is declared" {
            let app = ServerApp.empty |> FactsCompose.withSpecTool totalsTool
            Expect.isEmpty app.AITools "unchanged"
        }
    ]

let tests =
    testList "Phase 1273 — the spec-tool door: spec in, Facts out" [
        refusalTests
        blockTests
        factTests
        boundTests
        registrationTests
    ]
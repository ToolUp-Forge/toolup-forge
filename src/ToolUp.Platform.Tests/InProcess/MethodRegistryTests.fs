// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.MethodRegistryTests

// ─── The method registry and the method profile ──────────────────────
//
// A deployment registers method cards; each becomes one spec tool, an entry
// in the method profile's fragment and a page `describe_method` answers.
// What this pack pins:
//
//   1. **The profile is the tool list.** A registered card is a callable tool
//      and an entry in the fragment; an unregistered one is neither. A card
//      built in code, with an evaluator its component supplies, registers
//      like any other, and `FactsCompose.withMethods` declares its tools.
//   2. **Registration refuses by name**, every refusal together, and
//      registers nothing.
//   3. **A call is refused by the card's name with its remedy**: a missing,
//      mistyped or unadmitted argument, a precondition checked before any
//      read, one the evaluation refuses under, and a name the card does not
//      declare.
//   4. **Facts out under the card's identity**: an asserted row's method is
//      `Computed(card id, card version, parameter hash)`.
//   5. **`describe_method`** answers the examples the fragment omits.
//   6. **`evaluate_pipeline` is the first card**, with no observable change.
//   7. **Measurement**: the fragment's size per card, and the choose, call
//      and read accuracies over scripted analysis turns.

open System
open System.Text.Json
open Expecto
open Fuaran.Core
open Fuaran.Compute
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra
open ToolUp.Platform.Compose
open ToolUp.Facts
open ToolUp.Platform.Tests.InProcess.FactRenderToolTests.RenderFixtures
open ToolUp.Platform.Tests.InProcess.PipelineToolTests.PipelineFixtures

// ─── A card built in code: the mean of one metric per subject ───────

[<Literal>]
let private MeanId = "test.subject_mean"

[<Literal>]
let private MeanHash = "sha256:subject-mean-kernel-1"

let private metricInSlice: MethodCondition = {
    Name = "MetricInSlice"
    Statement = "the metric averaged is one of the slice's metrics"
    Remedy = "add the metric to 'metrics', or average one of them"
}

let private enoughRows: MethodCondition = {
    Name = "EnoughRows"
    Statement = "every subject has at least 'min_rows' rows in the slice"
    Remedy = "widen the period range or lower 'min_rows'"
}

let private noSubjects: MethodCondition = {
    Name = "NoSubjects"
    Statement = "the slice names at least one subject"
    Remedy = "widen the subject prefix"
}

let private parameter (name: string) (meaning: string) (schema: string) (required: bool) : MethodParameter = {
    Name = name
    Meaning = meaning
    Schema = schema
    Required = required
    Default = None
    Range = None
    Role = None
}

let private meanCard: MethodCard = {
    Identity = {
        Id = MeanId
        Version = "1"
        DefinitionHash = MeanHash
        Package = "ToolUp.Platform.Tests"
        Determinism = MethodDeterminism.Pinned
    }
    Meaning = {
        Summary = "Average one metric per subject over a slice, and assert each subject's mean as a fact."
        Answers = "What is each site's typical daily value?"
        Assumes = [ "the days are comparable" ]
    }
    Applicability = {
        Preconditions = [ metricInSlice; enoughRows ]
        DoesNotApply = [ "the metric is a stock, not a flow" ]
        Alternatives = [
            {
                Method = "evaluate_pipeline"
                PreferWhen = "the question is a total, not a mean"
            }
        ]
        Choice = MethodChoice.Mechanical
    }
    Signature = {
        ToolName = "subject_mean"
        Parameters = [
            parameter "metric" "The metric to average." """{"type":"string"}""" true
            {
                parameter "min_rows" "The fewest rows a subject may have." """{"type":"integer"}""" false with
                    Default = Some "2"
                    Range = Some "1 to 10"
            }
            {
                parameter
                    "scale"
                    "The unit the mean is stated in."
                    """{"type":"string","enum":["unit","thousands"]}"""
                    false with
                    Default = Some "\"unit\""
            }
        ]
        Refusals = [ noSubjects ]
    }
    Outputs = {
        Tables = [
            {
                Name = "means"
                Meaning = "One row per subject."
                Columns = [
                    {
                        Name = "mean"
                        Meaning = "The subject's mean."
                    }
                ]
            }
        ]
        Diagnostics = [
            {
                Name = "rows"
                Meaning = "The rows each mean reads."
                Healthy = "at least 7 reads as stable; fewer as indicative"
            }
        ]
        Warnings = []
        Facts = [ "subject_mean" ]
    }
    Interpretation = {
        Rules = [ "a mean over fewer than seven days is indicative" ]
        Say = [ "the mean of <metric> per subject over the period" ]
        NeverSay = [ "a trend: a mean has none" ]
    }
    Presentation = {
        Blocks = [ "table" ]
        BaseCard = None
    }
    Examples = [
        {
            Title = "Mean daily revenue per site"
            Call =
                """{"table":"daily-trade","period_from":"2026-10-01","period_to":"2026-10-04","metrics":["revenue"],"metric":"revenue"}"""
            Result = "Two rows: north>s1 and north>s2."
            Explanation = "EXAMPLE-ONLY-TEXT: each row is a site's mean over three days."
        }
    ]
}

let private textArg (call: MethodCall) (name: string) : string =
    call.Arguments |> Map.find name |> _.GetString()

let private meanEvaluator: MethodEvaluator = {
    DefinitionHash = MeanHash
    Checks =
        Map.ofList [
            metricInSlice.Name, (fun call -> List.contains (textArg call "metric") call.Slice.Metrics)
        ]
    Evaluate =
        fun read call -> async {
            let metric = textArg call "metric"
            let minRows = (Map.find "min_rows" call.Arguments).GetInt32()

            let pipeline = [
                GroupBy(
                    [ FactBrowseSlice.SubjectColumn ],
                    [
                        {
                            Name = "mean"
                            Fn = Mean
                            Of = metric
                        }
                        {
                            Name = "rows"
                            Fn = Count
                            Of = FactBrowseSlice.RowColumn
                        }
                    ]
                )
                Transform.sortBy [ FactBrowseSlice.SubjectColumn, Asc ]
            ]

            let request: PipelineToolRequest = {
                TableId = call.Slice.TableId
                PeriodFrom = call.Slice.PeriodFrom
                PeriodTo = call.Slice.PeriodTo
                PathPrefix = call.Slice.SubjectPrefix
                Metrics = call.Slice.Metrics
                Pipeline = DataFrameCodec.encodePipeline pipeline
            }

            match PipelineTool.evaluate request pipeline read.Slice read.ReadAt with
            | Error _ -> return Error "NoSubjects"
            | Ok result when result.Rows = 0 -> return Error "NoSubjects"
            | Ok result ->
                let tooFew =
                    match ColumnCodec.decode result.Table with
                    | Ok(Embedded table) ->
                        cells "rows" table
                        |> List.exists (fun c ->
                            match c with
                            | Int n -> n < minRows
                            | _ -> true)
                    | _ -> true

                if tooFew then
                    return Error "EnoughRows"
                else
                    match
                        PipelineAssertion.rowsOf MeanId read.Registry read.Slice result {
                            Column = "mean"
                            AsMetric = "subject_mean"
                            Level = "site"
                        }
                    with
                    | Error _ -> return Error "Undocumented"
                    | Ok rows ->
                        return
                            Ok {
                                Result = {
                                    Table = result.Table
                                    Rows = result.Rows
                                    Columns = result.Columns
                                    Withheld = result.Withheld
                                    Facts = rows
                                }
                                Summary = sprintf "Averaged %d subject(s)." result.Rows
                            }
        }
}

let private meanBinding () = MethodTool.bind meanCard meanEvaluator

let private registered (bindings: MethodBinding list) : MethodProfile =
    match MethodRegistry.register bindings with
    | Ok profile -> profile
    | Error refusals -> failtestf "registration refused: %A" refusals

let private refusalsOf (bindings: MethodBinding list) : MethodRegistrationRefusal list =
    match MethodRegistry.register bindings with
    | Ok _ -> failtest "expected the registration to be refused"
    | Error refusals -> refusals

// ─── Fixtures ────────────────────────────────────────────────────────

let private meanRegistry: IMetricRegistry =
    let registration (id: string) (producing: string option) : MetricRegistration = {
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

    MetricRegistry.build [ registration "revenue" None; registration "subject_mean" (Some MeanId) ] [
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

let private meanWorld () : World =
    let w = world ()

    {
        w with
            Deps = {
                w.Deps with
                    Registry = Some meanRegistry
            }
    }

/// The arguments: daily-trade revenue over the three seeded days, then
/// `own` (the method's arguments, a JSON object body without braces).
let private meanArgs (own: string) : string =
    sprintf
        """{"table":"%s","period_from":"%s","period_to":"%s","metrics":["revenue"]%s}"""
        trade.Id
        (day1.ToString "yyyy-MM-dd")
        ((day1.AddDays 3.0).ToString "yyyy-MM-dd")
        (if own = "" then "" else "," + own)

let private toolOf (profile: MethodProfile) (name: string) : MethodToolBuild =
    profile.Tools
    |> List.tryFind (fun t -> t.Definition.Name = name)
    |> Option.defaultWith (fun () -> failtestf "no tool '%s'" name)

let private answerText (w: World) (profile: MethodProfile) (name: string) (args: string) : string =
    (toolOf profile name).Answer w.Deps w.Scope "u" args |> Async.RunSynchronously

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

// ─── 1. The profile is the tool list ────────────────────────────────

let private otherCard: MethodCard = {
    meanCard with
        Identity = {
            meanCard.Identity with
                Id = "test.unregistered"
        }
        Signature = {
            meanCard.Signature with
                ToolName = "unregistered_method"
        }
}

let private profileTests =
    testList "the profile is the tool list" [
        test "a card built in code is a callable tool and an entry in the fragment; one not registered is neither" {
            let profile = registered [ meanBinding () ]

            Expect.equal
                (profile.Tools |> List.map _.Definition.Name)
                [ "subject_mean"; MethodProfile.DescribeName ]
                "one tool per card, then describe_method"

            Expect.stringContains profile.Fragment "### `subject_mean` — method `test.subject_mean` v1" "its entry"

            Expect.stringContains
                profile.Fragment
                "- Applies when: the metric averaged is one of the slice's metrics"
                "when it applies"

            Expect.stringContains profile.Fragment "- Never say: a trend: a mean has none" "how to read it"
            Expect.isFalse (profile.Fragment.Contains "unregistered") "an unregistered card is not in the fragment"

            Expect.isFalse
                (profile.Tools
                 |> List.exists (fun t -> t.Definition.Name = otherCard.Signature.ToolName))
                "nor callable"

            let w = meanWorld ()

            Expect.stringStarts
                (answerText w profile MethodProfile.DescribeName """{"method":"test.unregistered"}""")
                "describe_method refused the request: no method 'test.unregistered' is registered"
                "nor describable"
        }

        test "the generated declaration: the meaning, the slice arguments, then the card's own" {
            let d = (toolOf (registered [ meanBinding () ]) "subject_mean").Definition
            Expect.equal d.Description meanCard.Meaning.Summary "the meaning paragraph"

            Expect.equal
                (d.Parameters |> List.map _.Name)
                [
                    "table"
                    "period_from"
                    "period_to"
                    "path_prefix"
                    "metrics"
                    "metric"
                    "min_rows"
                    "scale"
                ]
                "slice, then the card's"

            let minRows = d.Parameters |> List.find (fun p -> p.Name = "min_rows")

            Expect.equal
                minRows.Description
                "The fewest rows a subject may have. Admissible: 1 to 10. Default: 2."
                "range and default"

            Expect.equal minRows.Default (Some "2") "the default"

            Expect.stringContains
                (d.Parameters |> List.find (fun p -> p.Name = "scale")).Type
                "\"enum\":[\"unit\",\"thousands\"]"
                "the closed set"

            Expect.equal
                d.Effects
                (ToolEffectDeclaration.declare [ ReadFacts; ComputeFacts; WriteState FactEvents.SourceModule ])
                "it asserts, so it declares the write"
        }

        test "no card: an empty fragment and no tool" {
            let profile = registered []
            Expect.equal profile.Fragment "" "nothing to say"
            Expect.isEmpty profile.Tools "nothing to call"

            Expect.equal
                (factsApp () |> FactsCompose.withMethods profile).AITools.Length
                (factsApp ()).AITools.Length
                "the composition is unchanged"
        }

        test "withMethods declares every tool beside the fact tools, and refuses a name already declared" {
            let profile = registered [ meanBinding () ]

            let names =
                (factsApp () |> FactsCompose.withMethods profile).AITools
                |> List.map (fun (d, _) -> d.Name)

            Expect.contains names "subject_mean" "the card's tool"
            Expect.contains names MethodProfile.DescribeName "describe_method"
            Expect.contains names "query_facts" "beside the fact tools"

            Expect.throws
                (fun () ->
                    factsApp ()
                    |> FactsCompose.withMethods profile
                    |> FactsCompose.withMethods profile
                    |> ignore)
                "a tool name declared twice"

            Expect.isEmpty (ServerApp.empty |> FactsCompose.withMethods profile).AITools "no fact store: nothing"
        }
    ]

// ─── 2. Registration refuses by name ────────────────────────────────

let private withSignature (f: MethodSignature -> MethodSignature) (card: MethodCard) = {
    card with
        Signature = f card.Signature
}

let private registrationTests =
    testList "registration refuses by name" [
        test "a schema that does not decode, a default that does not fit, a reserved parameter" {
            let card =
                meanCard
                |> withSignature (fun s -> {
                    s with
                        Parameters = [
                            parameter "metric" "m" """{"type":"strng"}""" true
                            {
                                parameter "min_rows" "n" """{"type":"integer"}""" false with
                                    Default = Some "\"two\""
                            }
                            parameter "table" "t" """{"type":"string"}""" false
                        ]
                })

            Expect.equal
                (refusalsOf [ MethodTool.bind card meanEvaluator ])
                [
                    MethodRegistrationRefusal.ParameterNameReserved(MeanId, "table")
                    MethodRegistrationRefusal.SchemaDoesNotDecode(
                        MeanId,
                        "metric",
                        "metric has type 'strng', which a tool schema does not carry"
                    )
                    MethodRegistrationRefusal.DefaultDoesNotFit(
                        MeanId,
                        "min_rows",
                        "method 'test.subject_mean' reads the argument 'min_rows' as an integer; write it so"
                    )
                ]
                "every refusal, together"
        }

        test "a definition hash its evaluator does not implement is unbound" {
            Expect.equal
                (refusalsOf [
                    MethodTool.bind meanCard {
                        meanEvaluator with
                            DefinitionHash = "sha256:other"
                    }
                ])
                [
                    MethodRegistrationRefusal.DefinitionHashUnbound(MeanId, MeanHash, "sha256:other")
                ]
                "unbound"
        }

        test "a repeated id and a repeated tool name, describe_method's among them" {
            let describeNamed =
                {
                    otherCard with
                        Identity = {
                            otherCard.Identity with
                                Id = "test.third"
                        }
                }
                |> withSignature (fun s -> {
                    s with
                        ToolName = MethodProfile.DescribeName
                })

            Expect.equal
                (refusalsOf [ meanBinding (); meanBinding (); MethodTool.bind describeNamed meanEvaluator ])
                [
                    MethodRegistrationRefusal.DuplicateId MeanId
                    MethodRegistrationRefusal.DuplicateToolName("test.third", MethodProfile.DescribeName)
                    MethodRegistrationRefusal.DuplicateToolName(MeanId, "subject_mean")
                ]
                "each by name"
        }

        test "conditions: a reserved name, a repeated one, a two-line remedy, a check for none" {
            let card = {
                meanCard with
                    Applicability = {
                        meanCard.Applicability with
                            Preconditions = [
                                { metricInSlice with Name = "NoFacts" }
                                { enoughRows with Remedy = "one\ntwo" }
                            ]
                    }
                    Signature = {
                        meanCard.Signature with
                            Refusals = [ enoughRows ]
                    }
            }

            Expect.equal
                (refusalsOf [ MethodTool.bind card meanEvaluator ])
                [
                    MethodRegistrationRefusal.ConditionNameInvalid(MeanId, "NoFacts")
                    MethodRegistrationRefusal.DuplicateCondition(MeanId, "EnoughRows")
                    MethodRegistrationRefusal.ConditionTextInvalid(MeanId, "EnoughRows")
                    MethodRegistrationRefusal.CheckUndeclared(MeanId, "MetricInSlice")
                ]
                "each by name"
        }

        test "a card over a spec tool must carry the tool's operation and version" {
            let card = {
                PipelineCard.cardOf PipelineTool.definition with
                    Identity = {
                        (PipelineCard.cardOf PipelineTool.definition).Identity with
                            Version = "0.0.1"
                    }
            }

            Expect.equal
                (refusalsOf [
                    MethodTool.ofSpecTool card PipelineCard.DefinitionHash None (PipelineTool.specToolUnder None)
                ])
                [
                    MethodRegistrationRefusal.IdentityUnbound(
                        PipelineTool.OperationId,
                        "0.0.1",
                        PipelineTool.OperationId,
                        PipelineTool.ComputeVersion
                    )
                ]
                "unbound"
        }

        test "every refusal reads as one sentence" {
            for refusal in
                [
                    MethodRegistrationRefusal.NoId
                    MethodRegistrationRefusal.NoMeaning MeanId
                    MethodRegistrationRefusal.ToolNameInvalid(MeanId, "Subject Mean")
                    MethodRegistrationRefusal.DuplicateParameter(MeanId, "metric")
                ] do
                let text = MethodRegistrationRefusal.describe refusal
                Expect.isFalse (String.IsNullOrWhiteSpace text || text.Contains '\n') $"%A{refusal}"
        }
    ]

// ─── 3. A call is refused by the card's name ────────────────────────

let private callTests =
    testList "a call is refused by the card's name with its remedy" [
        test "argument refusals come back together, before any read" {
            let w = meanWorld ()
            let profile = registered [ meanBinding () ]

            Expect.equal
                (answerText w profile "subject_mean" (meanArgs "\"min_rows\":\"two\",\"scale\":\"millions\""))
                "subject_mean refused the request for 3 reasons: (1) method 'test.subject_mean' requires the argument 'metric'; add it; (2) method 'test.subject_mean' reads the argument 'min_rows' as an integer; write it so; (3) method 'test.subject_mean' admits the argument 'scale' as one of 'unit', 'thousands'; write one of them."
                "missing, mistyped, not admitted"
        }

        test "a precondition checked before the read refuses by its name" {
            let w = meanWorld ()
            let profile = registered [ meanBinding () ]

            Expect.equal
                (answerText w profile "subject_mean" (meanArgs "\"metric\":\"margin\""))
                "subject_mean refused the request: method 'test.subject_mean' requires that the metric averaged is one of the slice's metrics; add the metric to 'metrics', or average one of them."
                "the card's condition and remedy"

            Expect.equal
                ((MethodRefusal.Unmet(MeanId, metricInSlice) :> ISpecRefusal).Name)
                "MetricInSlice"
                "named by the card"
        }

        test "a precondition on the data is the evaluation's, under the card's name" {
            let w = meanWorld ()
            let profile = registered [ meanBinding () ]

            Expect.stringContains
                (answerText w profile "subject_mean" (meanArgs "\"metric\":\"revenue\",\"min_rows\":5"))
                "requires that every subject has at least 'min_rows' rows in the slice; widen the period range or lower 'min_rows'"
                "three days each, five asked for"
        }

        test "a name the card does not declare is refused as undeclared" {
            let w = meanWorld ()

            let profile =
                registered [
                    MethodTool.bind meanCard {
                        meanEvaluator with
                            Evaluate = fun _ _ -> async.Return(Error "Mystery")
                    }
                ]

            Expect.stringContains
                (answerText w profile "subject_mean" (meanArgs "\"metric\":\"revenue\""))
                "refused the call as 'Mystery', which its card does not declare"
                "undeclared"
        }
    ]

// ─── 4. Facts out under the card's identity ─────────────────────────

let private factTests =
    testList "facts out under the card's identity" [
        test "an asserted row's method is the card's id, version and parameter hash" {
            let w = meanWorld ()
            let profile = registered [ meanBinding () ]
            let text = answerText w profile "subject_mean" (meanArgs "\"metric\":\"revenue\"")

            use document = JsonDocument.Parse text
            let root = document.RootElement
            let facts = root.GetProperty("asserted").GetProperty("facts")
            Expect.equal (facts.GetArrayLength()) 2 "one fact per site"

            let arguments =
                Map.ofList [
                    "metric", JsonDocument.Parse("\"revenue\"").RootElement
                    "min_rows", JsonDocument.Parse("2").RootElement
                    "scale", JsonDocument.Parse("\"unit\"").RootElement
                ]

            let expected = Computed(MeanId, "1", MethodTool.parameterHash MeanHash arguments)

            for f in facts.EnumerateArray() do
                let stored =
                    w.Deps.Store.Get(w.Scope, f.GetProperty("factId").GetString())
                    |> FactStoreResult.orRaiseAsync
                    |> Async.RunSynchronously

                match stored with
                | Some fact -> Expect.equal fact.Method expected "the card's identity, the defaults hashed in"
                | None -> failtest "the asserted fact is stored"

            let summary =
                JsonDocument.Parse(root.GetProperty("summary").GetString()).RootElement

            Expect.equal (summary.GetProperty("method").GetProperty("id").GetString()) MeanId "the method named"

            Expect.equal
                (summary.GetProperty("diagnostics").[0].GetProperty("healthy").GetString())
                "at least 7 reads as stable; fewer as indicative"
                "the diagnostics' healthy ranges"

            Expect.equal
                (summary.GetProperty("interpretation").GetProperty("neverSay").[0].GetString())
                "a trend: a mean has none"
                "the reading rules"
        }

        test "the parameter hash is over the definition and the canonical arguments" {
            let a = Map.ofList [ "x", JsonDocument.Parse("""{"b":1,"a":[1, 2]}""").RootElement ]

            let b =
                Map.ofList [ "x", JsonDocument.Parse("""{ "a":[1,2], "b":1 }""").RootElement ]

            Expect.equal
                (MethodTool.parameterHash "h" a)
                (MethodTool.parameterHash "h" b)
                "member order and spacing do not count"

            Expect.notEqual (MethodTool.parameterHash "h" a) (MethodTool.parameterHash "h2" a) "the definition does"
        }
    ]

// ─── 5. describe_method ─────────────────────────────────────────────

let private describeTests =
    testList "describe_method" [
        test "it answers the examples the fragment omits" {
            let w = meanWorld ()
            let profile = registered [ meanBinding () ]

            let page =
                answerText w profile MethodProfile.DescribeName """{"method":"test.subject_mean"}"""

            Expect.stringContains page "EXAMPLE-ONLY-TEXT" "the worked example"
            Expect.stringContains page "at least 7 reads as stable" "the healthy range"
            Expect.isFalse (profile.Fragment.Contains "EXAMPLE-ONLY-TEXT") "never in the prompt"

            Expect.stringStarts
                (answerText w profile MethodProfile.DescribeName "{}")
                "describe_method refused the request: name the method as 'method', one of 'test.subject_mean'."
                "a call naming nothing"
        }
    ]

// ─── 6. evaluate_pipeline is the first card ─────────────────────────

let private pipelineTests =
    testList "evaluate_pipeline as a card" [
        test "the generated declaration is the shipped one, in both forms" {
            let plain = registered [ PipelineCard.binding None false ]
            Expect.equal (toolOf plain PipelineTool.Name).Definition PipelineTool.definition "plain"
            let asserting = registered [ PipelineCard.binding None true ]

            Expect.equal
                (toolOf asserting PipelineTool.Name).Definition
                PipelineTool.definitionWithAssertions
                "asserting"
        }

        test "it answers exactly as the shipped tool does" {
            let profile = registered [ PipelineCard.binding None false ]
            let tool = PipelineTool.specToolUnder None

            for pipeline in [ perSubject; ranked; [ Filter(Binary(Gt, Col "nope", Lit(Decimal "1"))) ] ] do
                let w = world ()
                let args = revenueArgs pipeline

                let expected =
                    SpecTool.answerText tool w.Deps w.Scope "u" args |> Async.RunSynchronously

                let w2 = world ()
                Expect.equal (answerText w2 profile PipelineTool.Name args) expected "the same text"
        }

        test "under a profile, the profile's fragment is the card's grammar in the method profile" {
            let pipeline =
                match PipelineProfile.register PipelineProfile.defaultProfile with
                | Ok r -> r
                | Error refusals -> failtestf "%A" refusals

            let profile = registered [ PipelineCard.binding (Some pipeline) false ]
            Expect.stringContains profile.Fragment pipeline.Fragment "folded into the entry"

            Expect.isTrue
                ((registered [ PipelineCard.binding None false ]).Fragment.Contains "pipeline profile"
                 |> not)
                "none without one"
        }
    ]

// ─── 7. Measurement ─────────────────────────────────────────────────

/// Scripted analysis turns: a question, the tool the script calls and its
/// arguments. A scripted turn does not read the prompt, so these grade the
/// registry's own answers to a fixed script — what an applicable choice, a
/// valid first call and a read the card prescribes look like to it — and say
/// nothing about a model, whose benchmark is the per-method evaluation's.
let private turns: (string * string * string) list = [
    "typical daily revenue per site", "subject_mean", meanArgs "\"metric\":\"revenue\""
    "mean revenue in thousands", "subject_mean", meanArgs "\"metric\":\"revenue\",\"scale\":\"thousands\""
    "mean margin per site", "subject_mean", meanArgs "\"metric\":\"margin\""
    "a stable weekly mean", "subject_mean", meanArgs "\"metric\":\"revenue\",\"min_rows\":7"
    "total revenue per site", PipelineTool.Name, revenueArgs perSubject
    "the best site", PipelineTool.Name, revenueArgs ranked
    "clusters of sites", "cluster_sites", meanArgs ""
    "how to read a mean", MethodProfile.DescribeName, """{"method":"test.subject_mean"}"""
]

let private measurementTests =
    testList "measurement" [
        test "measurement: fragment size per card, and choose, call and read accuracy over the scripted turns" {
            let profile = registered [ meanBinding (); PipelineCard.binding None false ]

            let sizes = MethodProfile.entryTokens profile
            Expect.equal (sizes |> List.map fst) [ MeanId; PipelineTool.OperationId ] "one entry per card"

            Expect.isTrue
                (sizes |> List.forall (fun (_, t) -> t > 0 && t < 400))
                $"%A{sizes}, %d{profile.FragmentTokens} in all"

            let w = meanWorld ()
            let callable = profile.Tools |> List.map _.Definition.Name |> Set.ofList

            let graded =
                turns
                |> List.map (fun (question, tool, args) ->
                    let chosen = callable.Contains tool

                    let answer = if chosen then answerText w profile tool args else ""

                    let valid = chosen && not (answer.Contains "refused the request")

                    let read =
                        valid
                        && (tool = PipelineTool.Name
                            || answer.Contains "interpretation"
                            || answer.Contains "examples")

                    question, chosen, valid, read)

            let count f = graded |> List.filter f |> List.length
            let choose = count (fun (_, c, _, _) -> c)
            let call = count (fun (_, _, v, _) -> v)
            let read = count (fun (_, _, _, r) -> r)

            Expect.equal (choose, call, read) (7, 5, 5) "choose 7/8, call 5/8, read 5/8 on the script"

            Expect.equal
                (graded
                 |> List.filter (fun (_, c, v, _) -> c && not v)
                 |> List.map (fun (q, _, _, _) -> q))
                [ "mean margin per site"; "a stable weekly mean" ]
                "refused by name: a metric outside the slice, too few rows"
        }
    ]

// ─── The refusal contract's samples ─────────────────────────────────

/// One `MethodRefusal` per case, for the `ISpecRefusal` contract pack.
let methodRefusals: ISpecRefusal list = [
    MethodRefusal.Door(SpecDoorRefusal.NoFacts "daily-trade")
    MethodRefusal.MissingArgument(MeanId, "metric")
    MethodRefusal.ArgumentMistyped(MeanId, "min_rows", "an integer")
    MethodRefusal.ArgumentNotAdmitted(MeanId, "scale", [ "unit"; "thousands" ])
    MethodRefusal.Unmet(MeanId, metricInSlice)
    MethodRefusal.Undeclared(MeanId, "Mystery")
]

let tests =
    testList "Method registry" [
        profileTests
        registrationTests
        callTests
        factTests
        describeTests
        pipelineTests
        measurementTests
    ]
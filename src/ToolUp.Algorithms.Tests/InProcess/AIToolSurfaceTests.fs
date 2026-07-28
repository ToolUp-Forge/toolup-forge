// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.Tests.InProcess.AIToolSurfaceTests

open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Algorithms
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations
open ToolUp.Algorithms.Tests.InProcess.ReferenceProvider

// ─── Phase 11.E.2 — the AI tool surface ─────────────────────────────
//
// The tool surface is the only untyped edge in the companion, so it is
// tested directly rather than only through a composed server.

let private registry = AlgorithmProviderRegistry [ provider ]

let private describeInfo =
    registry.Algorithms |> List.find (fun a -> a.Id = Ids.Describe)

let private smoothInfo =
    registry.Algorithms |> List.find (fun a -> a.Id = Ids.Smooth)

let private regressionInfo =
    registry.Algorithms |> List.find (fun a -> a.Id = Ids.Regression)

let definitionTests =
    testList "tool definitions" [

        test "one tool per algorithm, plus the enumeration tool" {
            let tools = AlgorithmAITools.toolsFor registry.Algorithms

            Expect.hasLength tools 5 "four algorithms plus _algorithms.list"

            Expect.exists
                tools
                (fun (d, _) -> d.Name = AlgorithmAITools.ListToolName)
                "the enumeration tool must always be present — a model needs to discover before it can call"
        }

        test "tool names are namespaced under the module" {
            for info in registry.Algorithms do
                let definition = AlgorithmAITools.definitionFor info

                Expect.equal
                    definition.Name
                    ("_algorithms." + info.Id)
                    "tool names are the algorithm id under the module prefix"

                Expect.equal
                    definition.SourceModule
                    AlgorithmAITools.SourceModule
                    "SourceModule must be the tool module"
        }

        test "no tool name collides" {
            let names =
                AlgorithmAITools.toolsFor registry.Algorithms |> List.map (fun (d, _) -> d.Name)

            Expect.equal
                (List.distinct names |> List.length)
                names.Length
                "a duplicate tool name fails the agent loop at startup"
        }

        test "advertised parameters are the algorithm's own declarations" {
            // Load-bearing: the parser reads exactly the names
            // AlgorithmParameters.forKind publishes, so if the tool
            // schema were authored separately the two could drift.
            let definition = AlgorithmAITools.definitionFor describeInfo

            let advertised = definition.Parameters |> List.map _.Name
            let declared = describeInfo.Parameters |> List.map _.Name

            Expect.equal
                advertised
                declared
                "the tool schema is a projection of the declaration, not a second authoring"
        }

        test "the description carries the precision contract and the provider stamp" {
            let definition = AlgorithmAITools.definitionFor describeInfo

            Expect.stringContains definition.Description describeInfo.PrecisionContract "precision must reach the model"
            Expect.stringContains definition.Description "reference" "the serving provider must be named"
        }

        test "every tool is server-resident and offered on both surfaces" {
            for (definition, _) in AlgorithmAITools.toolsFor registry.Algorithms do
                Expect.equal definition.Location ServerResident "algorithms execute server-side"
                Expect.equal definition.Surface Both "an analytical tool is useful from either AI surface"
                Expect.isNone definition.EmitsActions "algorithm tools return JSON only — they publish no client action"
        }

        test "the quantile-definition parameter warns the model about the spreadsheet mismatch" {
            let definition = AlgorithmAITools.definitionFor describeInfo

            let param =
                definition.Parameters |> List.find (fun p -> p.Name = "quantileDefinition")

            Expect.stringContains
                param.Description
                "Excel"
                "the model must be told why the default matters — this is the measured divergence the catalog exists to close"

            Expect.equal param.Required false "the convention is optional; the default is the safe one"
        }
    ]

let parserTests =
    testList "argument parsing" [

        test "parses a descriptive request with defaults" {
            match AlgorithmAITools.parseInvocation Ids.Describe DescriptiveStatistics """{"values":[1,2,3]}""" with
            | Ok(SummariseDescriptive r) ->
                Expect.equal r.Values [| 1.0; 2.0; 3.0 |] "values parsed"
                Expect.equal r.Quantiles DescriptiveRequest.defaultQuantiles "quantiles default to the quartiles"
                Expect.equal r.Convention ExcelCompatible "the convention defaults to the spreadsheet-compatible one"
            | other -> failtestf "expected a descriptive invocation, got %A" other
        }

        test "parses an explicit quantile convention" {
            match
                AlgorithmAITools.parseInvocation
                    Ids.Describe
                    DescriptiveStatistics
                    """{"values":[1,2],"quantileDefinition":"medianUnbiased"}"""
            with
            | Ok(SummariseDescriptive r) -> Expect.equal r.Convention MedianUnbiased "the named convention is honoured"
            | other -> failtestf "expected a descriptive invocation, got %A" other
        }

        test "an unknown convention is a readable InvalidArguments, not a silent default" {
            match
                AlgorithmAITools.parseInvocation
                    Ids.Describe
                    DescriptiveStatistics
                    """{"values":[1],"quantileDefinition":"r9"}"""
            with
            | Error(AlgorithmError.InvalidArguments(_, d)) ->
                Expect.stringContains
                    d
                    "excelCompatible"
                    "the error must list the admissible values so the model can correct"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }

        test "parses a regression request with numeric and categorical columns" {
            let json =
                """{"response":[1,2,3,4],
                    "numeric":[{"name":"spend","values":[1,2,3,4]}],
                    "categorical":[{"name":"region","values":["N","N","S","S"]}]}"""

            match AlgorithmAITools.parseInvocation Ids.Regression Regression json with
            | Ok(FitRegression r) ->
                Expect.hasLength r.Numeric 1 "one numeric column"
                Expect.hasLength r.Categorical 1 "one categorical column"
                Expect.equal r.Categorical.Head.Values [| "N"; "N"; "S"; "S" |] "categorical labels stay raw strings"
                Expect.isTrue r.Intercept "the intercept defaults to true"
            | other -> failtestf "expected a regression invocation, got %A" other
        }

        test "a missing required argument is named" {
            match AlgorithmAITools.parseInvocation Ids.Describe DescriptiveStatistics "{}" with
            | Error(AlgorithmError.InvalidArguments(_, d)) ->
                Expect.stringContains d "values" "the missing field is named"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }

        test "a non-numeric entry in a number array is rejected" {
            match AlgorithmAITools.parseInvocation Ids.Describe DescriptiveStatistics """{"values":[1,"two"]}""" with
            | Error(AlgorithmError.InvalidArguments(_, d)) ->
                Expect.stringContains d "non-numeric" "the problem is named"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }

        test "malformed JSON is data, not an exception" {
            match AlgorithmAITools.parseInvocation Ids.Describe DescriptiveStatistics "{not json" with
            | Error(AlgorithmError.InvalidArguments(_, d)) ->
                Expect.stringContains d "valid JSON" "the problem is named"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }

        test "parses a smoothing request, requiring the kind" {
            match
                AlgorithmAITools.parseInvocation
                    Ids.Smooth
                    TimeSeriesSmoothing
                    """{"values":[1,2,3],"kind":"centredMean","window":3}"""
            with
            | Ok(SmoothSeries r) ->
                Expect.equal r.Kind CentredMean "kind parsed"
                Expect.equal r.Window 3 "window parsed"
                Expect.equal r.WarmUp UndefinedWarmUp "the warm-up defaults to undefined"
            | other -> failtestf "expected a smoothing invocation, got %A" other
        }

        test "smoothing without a kind is refused — the alignment choice cannot be skipped" {
            match AlgorithmAITools.parseInvocation Ids.Smooth TimeSeriesSmoothing """{"values":[1,2,3]}""" with
            | Error(AlgorithmError.InvalidArguments(_, d)) ->
                Expect.stringContains d "kind" "the model must state trailing vs centred explicitly"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }

        test "parses a distribution request, requiring the family" {
            match
                AlgorithmAITools.parseInvocation
                    Ids.DistributionFit
                    DistributionFit
                    """{"values":[1,2],"family":"normal"}"""
            with
            | Ok(FitDistribution r) ->
                Expect.equal r.Family NormalFamily "family parsed"
                Expect.isNone r.Method "an unstated method lets the provider choose — and report"
            | other -> failtestf "expected a distribution invocation, got %A" other
        }

        test "an unknown family lists the admissible ones" {
            match
                AlgorithmAITools.parseInvocation
                    Ids.DistributionFit
                    DistributionFit
                    """{"values":[1],"family":"gaussian"}"""
            with
            | Error(AlgorithmError.InvalidArguments(_, d)) ->
                Expect.stringContains d "negativeBinomial" "the error enumerates the families"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }
    ]

let projectionTests =
    testList "outcome projection" [

        let dispatcher = AlgorithmDispatcher(registry) :> IAlgorithmDispatcher

        let projectJson invocation id =
            match dispatcher.Execute(id, invocation) |> Async.RunSynchronously with
            | Ok outcome ->
                let json = JsonSerializer.Serialize(AlgorithmAITools.projectOutcome outcome)
                JsonDocument.Parse json
            | Error e -> failtest (AlgorithmError.describe e)

        test "a descriptive projection carries the quantile definition as a plain string" {
            use doc = projectJson sampleDescriptive Ids.Describe
            let root = doc.RootElement

            Expect.equal
                (root.GetProperty("quantileDefinition").GetString())
                "excelCompatible"
                "the echoed convention must survive projection as a readable string, not a tagged union"

            Expect.equal (root.GetProperty("count").GetInt32()) 8 "count projected"
        }

        test "a smoothing projection carries the alignment and nulls the warm-up" {
            use doc = projectJson sampleSmoothing Ids.Smooth
            let root = doc.RootElement

            Expect.equal (root.GetProperty("alignment").GetString()) "centred" "alignment must reach the model"

            let values = root.GetProperty("values").EnumerateArray() |> Seq.toArray

            Expect.equal values.Length 6 "the smoothed series is the same length as the input"

            Expect.equal
                values[0].ValueKind
                JsonValueKind.Null
                "an undefined warm-up period must project as null, never as a partial-window number"
        }

        test "a regression projection labels its terms and names the reference level" {
            use doc = projectJson sampleRegression Ids.Regression
            let root = doc.RootElement

            let terms =
                root.GetProperty("coefficients").EnumerateArray()
                |> Seq.map (fun c -> c.GetProperty("term").GetString())
                |> Seq.toList

            Expect.contains terms "spend" "a numeric coefficient is labelled with its column name"

            let references =
                root.GetProperty("referenceLevels").EnumerateArray()
                |> Seq.map (fun r -> r.GetProperty("factor").GetString(), r.GetProperty("level").GetString())
                |> Seq.toList

            Expect.contains
                references
                ("region", "North")
                "a contrast coefficient is uninterpretable without its reference level"
        }

        test "a distribution projection names the estimator that ran" {
            use doc = projectJson sampleDistribution Ids.DistributionFit
            let root = doc.RootElement

            Expect.equal
                (root.GetProperty("method").GetString())
                "maximumLikelihood"
                "the estimator must be reported even when the request did not name one"

            Expect.isTrue (root.TryGetProperty("aic") |> fst) "AIC is a first-class field, not a caller computation"
        }

        test "an error projects to a readable { error, message } pair" {
            let json =
                JsonSerializer.Serialize(AlgorithmAITools.projectError (AlgorithmError.NotFound "nope"))

            use doc = JsonDocument.Parse json
            Expect.equal (doc.RootElement.GetProperty("error").GetString()) "notFound" "the machine tag is stable"

            Expect.stringContains
                (doc.RootElement.GetProperty("message").GetString())
                "nope"
                "the human message names the offending id so the model can correct"
        }

        test "a catalog projection names the tool that invokes it" {
            let json = JsonSerializer.Serialize(AlgorithmAITools.projectAlgorithm smoothInfo)
            use doc = JsonDocument.Parse json

            Expect.equal
                (doc.RootElement.GetProperty("tool").GetString())
                "_algorithms.timeseries.smooth"
                "the enumeration result must tell the model which tool to call next"

            Expect.equal
                (doc.RootElement.GetProperty("provider").GetString())
                "reference"
                "the provider stamp is surfaced"
        }

        test "no projected field carries an F# tagged-union shape" {
            // A projection that let a DU through would hand the model
            // {"Case":...,"Fields":[...]} to interpret.
            for (id, invocation) in sampleInvocations do
                match dispatcher.Execute(id, invocation) |> Async.RunSynchronously with
                | Ok outcome ->
                    let json = JsonSerializer.Serialize(AlgorithmAITools.projectOutcome outcome)

                    Expect.isFalse
                        (json.Contains "\"Case\"")
                        (sprintf "'%s' leaked a tagged union into its tool result" id)
                | Error e -> failtest (AlgorithmError.describe e)
        }
    ]

let contributorTests =
    testList "/dev/inspect contributor" [

        test "reports the catalog under an Algorithms panel, grouped by provider" {
            let catalog = AlgorithmCatalog(registry) :> IAlgorithmCatalog
            let contributor = AlgorithmCatalogContributor(catalog) :> IDevDiagnosticsContributor
            let panel, payload = contributor.Contribute() |> Async.RunSynchronously

            Expect.equal panel "Algorithms" "the panel name is the operator's entry point"

            let json = JsonSerializer.Serialize payload
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            Expect.equal (root.GetProperty("count").GetInt32()) 4 "every registered algorithm is counted"

            Expect.stringContains json "reference" "the serving provider is named"
            Expect.stringContains json regressionInfo.Id "every registered algorithm is listed by id"

            // Asserted on an ASCII-only prefix: this is raw
            // `JsonSerializer.Serialize`, which escapes non-ASCII, and
            // the reference contract text carries an em dash. The
            // platform's own dev-diagnostics writer uses the relaxed
            // encoder; the property under test is that the contract
            // reaches the panel at all.
            Expect.stringContains
                json
                "Deterministic for a given request."
                "the precision contract must be surfaced — it is the only place two implementations' numerical differences are stated"
        }

        test "an empty catalog contributes an empty panel rather than failing" {
            let catalog = AlgorithmCatalog(AlgorithmProviderRegistry []) :> IAlgorithmCatalog
            let contributor = AlgorithmCatalogContributor(catalog) :> IDevDiagnosticsContributor
            let _, payload = contributor.Contribute() |> Async.RunSynchronously

            use doc = JsonDocument.Parse(JsonSerializer.Serialize payload)
            Expect.equal (doc.RootElement.GetProperty("count").GetInt32()) 0 "no providers, no algorithms, no failure"
        }
    ]
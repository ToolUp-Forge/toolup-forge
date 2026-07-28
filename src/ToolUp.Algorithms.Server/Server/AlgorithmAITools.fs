// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.AlgorithmAITools

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations

// ─── Phase 11.E.2 — the AI tool surface ─────────────────────────────
//
// Every catalog entry becomes one `AIToolDefinition`, plus one
// discovery tool (`_algorithms.list`) so a model can enumerate what a
// deployment offers before committing to a call.
//
// The tool surface is the ONLY place in this companion where arguments
// are stringly typed. JSON arrives, is parsed into one of the four
// typed request records at this edge, and everything downstream is
// type-checked. A parse failure is a typed `InvalidArguments` returned
// as a JSON error payload the model can read and correct from — never
// an exception into the agent loop.
//
// **Serialisation** uses the canonical STJ converter set
// (`FableConverters`), and results are projected onto anonymous records
// of PRIMITIVES before serialising. Emitting the F# DUs directly would
// give the model tagged-union JSON to interpret; projecting gives it
// the flat, self-describing shape the tool description advertises. The
// echoed-convention fields (`quantileDefinition`, `alignment`,
// `method`, `referenceLevels`) survive the projection deliberately —
// they are the point.

let private jsonOptions = FableConverters.create ()

/// Serialise a value with the platform's canonical converter set.
let private serialise (value: obj) : string =
    JsonSerializer.Serialize(value, jsonOptions)

/// The `SourceModule` every algorithm tool declares, and the
/// `ServerModule.Name` the compose step registers them under.
[<Literal>]
let SourceModule = "_algorithms"

/// The catalog-enumeration tool's name.
[<Literal>]
let ListToolName = "_algorithms.list"

/// Tool name for one catalogued algorithm — `_algorithms.{id}`.
let toolNameFor (algorithmId: AlgorithmId) : string = SourceModule + "." + algorithmId

// ═══ JSON argument parsing ══════════════════════════════════════════

let private invalid (id: AlgorithmId) (detail: string) : Result<'T, AlgorithmError> =
    Error(AlgorithmError.InvalidArguments(id, detail))

let private tryProp (root: JsonElement) (name: string) : JsonElement option =
    match root.TryGetProperty name with
    | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
    | _ -> None

let private numberArray (id: AlgorithmId) (name: string) (el: JsonElement) : Result<float[], AlgorithmError> =
    if el.ValueKind <> JsonValueKind.Array then
        invalid id (sprintf "'%s' must be an array of numbers" name)
    else
        let items = el.EnumerateArray() |> Seq.toArray

        match items |> Array.tryFind (fun i -> i.ValueKind <> JsonValueKind.Number) with
        | Some bad -> invalid id (sprintf "'%s' contains a non-numeric entry (%s)" name (bad.ToString()))
        | None -> Ok(items |> Array.map _.GetDouble())

let private stringArray (id: AlgorithmId) (name: string) (el: JsonElement) : Result<string[], AlgorithmError> =
    if el.ValueKind <> JsonValueKind.Array then
        invalid id (sprintf "'%s' must be an array of strings" name)
    else
        let items = el.EnumerateArray() |> Seq.toArray

        match items |> Array.tryFind (fun i -> i.ValueKind <> JsonValueKind.String) with
        | Some bad -> invalid id (sprintf "'%s' contains a non-string entry (%s)" name (bad.ToString()))
        | None -> Ok(items |> Array.map _.GetString())

let private requiredNumberArray (id: AlgorithmId) (root: JsonElement) (name: string) : Result<float[], AlgorithmError> =
    match tryProp root name with
    | None -> invalid id (sprintf "'%s' is required" name)
    | Some el -> numberArray id name el

/// Parse the `[{ name, values }]` predictor-column shape shared by the
/// numeric and categorical regression arguments.
let private predictorColumns
    (id: AlgorithmId)
    (root: JsonElement)
    (name: string)
    (readValues: JsonElement -> Result<'V, AlgorithmError>)
    : Result<(string * 'V) list, AlgorithmError> =
    match tryProp root name with
    | None -> Ok []
    | Some el when el.ValueKind <> JsonValueKind.Array ->
        invalid id (sprintf "'%s' must be an array of { name, values } objects" name)
    | Some el ->
        el.EnumerateArray()
        |> Seq.toList
        |> List.fold
            (fun acc item ->
                match acc with
                | Error _ -> acc
                | Ok columns ->
                    match tryProp item "name", tryProp item "values" with
                    | Some n, Some v when n.ValueKind = JsonValueKind.String ->
                        readValues v |> Result.map (fun values -> columns @ [ n.GetString(), values ])
                    | _ -> invalid id (sprintf "each '%s' entry needs a string 'name' and a 'values' array" name))
            (Ok [])

let private parseRegression (id: AlgorithmId) (root: JsonElement) : Result<AlgorithmInvocation, AlgorithmError> =
    requiredNumberArray id root "response"
    |> Result.bind (fun response ->
        predictorColumns id root "numeric" (numberArray id "numeric.values")
        |> Result.bind (fun numeric ->
            predictorColumns id root "categorical" (stringArray id "categorical.values")
            |> Result.map (fun categorical ->
                let intercept =
                    match tryProp root "intercept" with
                    | Some el when el.ValueKind = JsonValueKind.False -> false
                    | _ -> true

                FitRegression {
                    Response = response
                    Numeric = numeric |> List.map (fun (n, v) -> { Name = n; Values = v })
                    Categorical = categorical |> List.map (fun (n, v) -> { Name = n; Values = v })
                    Intercept = intercept
                })))

let private parseDescriptive (id: AlgorithmId) (root: JsonElement) : Result<AlgorithmInvocation, AlgorithmError> =
    requiredNumberArray id root "values"
    |> Result.bind (fun values ->
        let quantiles =
            match tryProp root "quantiles" with
            | None -> Ok DescriptiveRequest.defaultQuantiles
            | Some el -> numberArray id "quantiles" el

        quantiles
        |> Result.bind (fun qs ->
            let convention =
                match tryProp root "quantileDefinition" with
                | None -> Ok ExcelCompatible
                | Some el when el.ValueKind = JsonValueKind.String ->
                    match QuantileConvention.parse (el.GetString()) with
                    | Some c -> Ok c
                    | None ->
                        invalid
                            id
                            (sprintf
                                "unknown quantileDefinition '%s' — expected \"excelCompatible\" or \"medianUnbiased\""
                                (el.GetString()))
                | Some _ -> invalid id "'quantileDefinition' must be a string"

            convention
            |> Result.map (fun c ->
                SummariseDescriptive {
                    Values = values
                    Quantiles = qs
                    Convention = c
                })))

let private parseDistribution (id: AlgorithmId) (root: JsonElement) : Result<AlgorithmInvocation, AlgorithmError> =
    requiredNumberArray id root "values"
    |> Result.bind (fun values ->
        let family =
            match tryProp root "family" with
            | Some el when el.ValueKind = JsonValueKind.String ->
                match DistributionFamily.parse (el.GetString()) with
                | Some f -> Ok f
                | None ->
                    invalid
                        id
                        (sprintf
                            "unknown family '%s' — expected one of: %s"
                            (el.GetString())
                            (DistributionFamily.all |> List.map DistributionFamily.name |> String.concat ", "))
            | Some _ -> invalid id "'family' must be a string"
            | None -> invalid id "'family' is required"

        family
        |> Result.bind (fun f ->
            let method' =
                match tryProp root "method" with
                | None -> Ok None
                | Some el when el.ValueKind = JsonValueKind.String ->
                    match EstimationMethod.parse (el.GetString()) with
                    | Some m -> Ok(Some m)
                    | None ->
                        invalid
                            id
                            (sprintf
                                "unknown method '%s' — expected \"maximumLikelihood\" or \"methodOfMoments\""
                                (el.GetString()))
                | Some _ -> invalid id "'method' must be a string"

            method'
            |> Result.map (fun m ->
                FitDistribution {
                    Values = values
                    Family = f
                    Method = m
                })))

let private parseSmoothing (id: AlgorithmId) (root: JsonElement) : Result<AlgorithmInvocation, AlgorithmError> =
    requiredNumberArray id root "values"
    |> Result.bind (fun values ->
        let kind =
            match tryProp root "kind" with
            | Some el when el.ValueKind = JsonValueKind.String ->
                match SmoothingKind.parse (el.GetString()) with
                | Some k -> Ok k
                | None ->
                    invalid
                        id
                        (sprintf
                            "unknown kind '%s' — expected \"trailingMean\", \"centredMean\" or \"exponentiallyWeighted\""
                            (el.GetString()))
            | Some _ -> invalid id "'kind' must be a string"
            | None -> invalid id "'kind' is required"

        kind
        |> Result.bind (fun k ->
            let warmUp =
                match tryProp root "warmUp" with
                | None -> Ok UndefinedWarmUp
                | Some el when el.ValueKind = JsonValueKind.String ->
                    match WarmUpPolicy.parse (el.GetString()) with
                    | Some w -> Ok w
                    | None ->
                        invalid
                            id
                            (sprintf
                                "unknown warmUp '%s' — expected \"undefined\" or \"partialWindow\""
                                (el.GetString()))
                | Some _ -> invalid id "'warmUp' must be a string"

            warmUp
            |> Result.bind (fun w ->
                let window =
                    match tryProp root "window" with
                    | Some el when el.ValueKind = JsonValueKind.Number -> Ok(el.GetInt32())
                    | Some _ -> invalid id "'window' must be a number"
                    | None -> Ok 3

                let alpha =
                    match tryProp root "alpha" with
                    | Some el when el.ValueKind = JsonValueKind.Number -> Ok(Some(el.GetDouble()))
                    | Some _ -> invalid id "'alpha' must be a number"
                    | None -> Ok None

                match window, alpha with
                | Error e, _
                | _, Error e -> Error e
                | Ok win, Ok a ->
                    Ok(
                        SmoothSeries {
                            Values = values
                            Kind = k
                            Window = win
                            Alpha = a
                            WarmUp = w
                        }
                    ))))

/// Parse an AI tool's raw argument JSON into a typed invocation for
/// `kind`. Exposed for the contract pack — the parser is the only
/// untyped edge in the companion, so it is tested directly rather than
/// only through the executor.
let parseInvocation
    (algorithmId: AlgorithmId)
    (kind: AlgorithmKind)
    (argsJson: string)
    : Result<AlgorithmInvocation, AlgorithmError> =
    let parsed =
        try
            let text =
                if String.IsNullOrWhiteSpace argsJson then
                    "{}"
                else
                    argsJson

            use doc = JsonDocument.Parse text
            Ok(doc.RootElement.Clone())
        with ex ->
            invalid algorithmId (sprintf "arguments are not valid JSON: %s" ex.Message)

    parsed
    |> Result.bind (fun root ->
        if root.ValueKind <> JsonValueKind.Object then
            invalid algorithmId "arguments must be a JSON object"
        else
            match kind with
            | Regression -> parseRegression algorithmId root
            | DescriptiveStatistics -> parseDescriptive algorithmId root
            | DistributionFit -> parseDistribution algorithmId root
            | TimeSeriesSmoothing -> parseSmoothing algorithmId root)

// ═══ Outcome projection ═════════════════════════════════════════════

/// Project a typed outcome onto the flat primitive shape the tool
/// description advertises. Anonymous records only — no F# DU reaches
/// the model.
let projectOutcome (outcome: AlgorithmOutcome) : obj =
    match outcome with
    | RegressionOutcome r ->
        box {|
            coefficients =
                r.Coefficients
                |> List.map (fun c -> {|
                    term = c.Term
                    estimate = c.Estimate
                |})
            intercept = r.Intercept
            rSquared = r.RSquared
            adjustedRSquared = r.AdjustedRSquared
            residualStandardError = r.ResidualStandardError
            observations = r.Observations
            referenceLevels =
                r.ReferenceLevels
                |> List.map (fun l -> {| factor = l.Factor; level = l.Level |})
        |}
    | DescriptiveOutcome d ->
        box {|
            count = d.Count
            mean = d.Mean
            median = d.Median
            standardDeviation = d.StandardDeviation
            variance = d.Variance
            minimum = d.Minimum
            maximum = d.Maximum
            skewness = d.Skewness
            kurtosis = d.Kurtosis
            quantiles =
                d.Quantiles
                |> List.map (fun q -> {|
                    probability = q.Probability
                    value = q.Value
                |})
            quantileDefinition = QuantileConvention.name d.Convention
        |}
    | DistributionOutcome f ->
        box {|
            family = DistributionFamily.name f.Family
            method = EstimationMethod.name f.Method
            parameters = f.Parameters |> List.map (fun p -> {| name = p.Name; value = p.Value |})
            logLikelihood = f.LogLikelihood
            aic = f.Aic
            bic = f.Bic
            observations = f.Observations
        |}
    | SmoothingOutcome s ->
        box {|
            values = s.Values
            kind = SmoothingKind.name s.Kind
            window = s.Window
            alignment = SmoothingAlignment.name s.Alignment
            warmUp = WarmUpPolicy.name s.WarmUp
        |}

/// Project a typed error onto the `{ error, message }` shape the model
/// reads and corrects from.
let projectError (error: AlgorithmError) : obj =
    box {|
        error = AlgorithmError.tag error
        message = AlgorithmError.describe error
    |}

/// Project a catalog entry onto the flat shape `_algorithms.list`
/// returns.
let projectAlgorithm (info: AlgorithmInfo) : obj =
    box {|
        id = info.Id
        displayName = info.DisplayName
        kind = AlgorithmKind.name info.Kind
        description = info.Description
        tool = toolNameFor info.Id
        returns = info.ReturnsDescription
        precision = info.PrecisionContract
        provider = info.ProviderId
        providerVersion = info.ProviderVersion
    |}

// ═══ Tool definitions + executors ═══════════════════════════════════

/// The `AIToolDefinition` for one catalogued algorithm. Parameters are
/// the algorithm's own declarations projected onto the AI tool schema,
/// so the advertised arguments and the parser cannot disagree (both
/// derive from `AlgorithmParameters.forKind`).
let definitionFor (info: AlgorithmInfo) : AIToolDefinition = {
    Name = toolNameFor info.Id
    Description =
        sprintf
            "%s — %s Returns %s Precision: %s (provider '%s' %s)."
            info.DisplayName
            info.Description
            info.ReturnsDescription
            info.PrecisionContract
            info.ProviderId
            info.ProviderVersion
    Parameters =
        info.Parameters
        |> List.map (fun p -> {
            Name = p.Name
            Type = AlgorithmParameterType.jsonTypeName p.Type
            Description = p.Description
            Required = p.Required
            Default = p.Default
        })
    SourceModule = SourceModule
    EmitsActions = None
    Location = ServerResident
    Surface = Both
}

/// The catalog-enumeration tool definition.
let listDefinition: AIToolDefinition = {
    Name = ListToolName
    Description =
        "Enumerate the analytical algorithms this deployment offers — regression, descriptive statistics, distribution fitting, time-series smoothing. Each entry names the tool to call for it, the shape it returns, and its precision contract. Call this first when the user asks for an analysis and you do not already know which algorithm ids are available; the per-algorithm tools carry the full parameter schemas."
    Parameters = [
        {
            Name = "kind"
            Type = "string"
            Description =
                sprintf
                    "Optional filter — one of: %s. Omit to list every algorithm."
                    (AlgorithmKind.all |> List.map AlgorithmKind.name |> String.concat ", ")
            Required = false
            Default = Some "null"
        }
    ]
    SourceModule = SourceModule
    EmitsActions = None
    Location = ServerResident
    Surface = Both
}

let private resolveCatalog (ctx: HttpContext) : IAlgorithmCatalog option =
    match ctx.RequestServices.GetService(typeof<IAlgorithmCatalog>) with
    | :? IAlgorithmCatalog as catalog -> Some catalog
    | _ -> None

let private resolveDispatcher (ctx: HttpContext) : IAlgorithmDispatcher option =
    match ctx.RequestServices.GetService(typeof<IAlgorithmDispatcher>) with
    | :? IAlgorithmDispatcher as dispatcher -> Some dispatcher
    | _ -> None

/// Executor for `_algorithms.list`.
let executeList (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    match resolveCatalog ctx with
    | None ->
        return
            serialise {|
                algorithms = ([]: obj list)
                note = "no algorithm catalog registered in this deployment"
            |}
    | Some catalog ->
        let kindFilter =
            try
                let text =
                    if String.IsNullOrWhiteSpace argsJson then
                        "{}"
                    else
                        argsJson

                use doc = JsonDocument.Parse text

                match doc.RootElement.TryGetProperty "kind" with
                | true, v when v.ValueKind = JsonValueKind.String -> AlgorithmKind.parse (v.GetString())
                | _ -> None
            with _ ->
                None

        let! algorithms =
            match kindFilter with
            | Some k -> catalog.ListByKind k
            | None -> catalog.ListAlgorithms()

        return
            serialise {|
                algorithms = algorithms |> List.map projectAlgorithm
            |}
}

/// Executor for one catalogued algorithm. Parses the arguments into a
/// typed invocation, dispatches, and projects the outcome — every
/// failure a readable JSON error rather than an exception.
let executeAlgorithm (info: AlgorithmInfo) (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    match resolveDispatcher ctx with
    | None ->
        return
            serialise (
                projectError (
                    AlgorithmError.ExecutionFailed(info.Id, "no algorithm dispatcher registered in this deployment")
                )
            )
    | Some dispatcher ->
        match parseInvocation info.Id info.Kind argsJson with
        | Error e -> return serialise (projectError e)
        | Ok invocation ->
            let! result = dispatcher.Execute(info.Id, invocation)

            match result with
            | Error e -> return serialise (projectError e)
            | Ok outcome -> return serialise (projectOutcome outcome)
}

/// Every algorithm AI tool for a catalog snapshot: the enumeration tool
/// plus one per algorithm. Registered onto a `ServerModule` by
/// `AlgorithmsCompose`.
let toolsFor (algorithms: AlgorithmInfo list) : (AIToolDefinition * (HttpContext -> string -> Async<string>)) list =
    (listDefinition, executeList)
    :: (algorithms |> List.map (fun info -> definitionFor info, executeAlgorithm info))
module ToolUp.Forms.Aggregations

open System
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.AggregationTypes

// ─── Phase 21b — Per-field aggregation helpers ──────────────────────
//
// Pure helpers — no DI, no IO. `IFormApi.GetAggregations` resolves
// the schema + submissions + issued tokens, then calls these
// functions to roll up the per-question stats.

let private extractNumeric =
    function
    | NumberValue n -> Some n
    | _ -> None

let private extractChoice =
    function
    | ChoiceValue s -> [ s ]
    | MultiChoiceValue xs -> xs
    | _ -> []

let private extractBool =
    function
    | BoolValue b -> Some b
    | _ -> None

let private extractText =
    function
    | TextValue s when not (String.IsNullOrWhiteSpace s) -> Some s
    | _ -> None

let private extractDate =
    function
    | DateValue d -> Some(DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
    | DateTimeValue dt -> Some dt
    | _ -> None

let private median (xs: float list) : float option =
    match xs with
    | [] -> None
    | _ ->
        let sorted = List.sort xs
        let n = List.length sorted

        if n % 2 = 1 then
            Some sorted[n / 2]
        else
            Some((sorted[n / 2 - 1] + sorted[n / 2]) / 2.0)

let private populationStdDev (xs: float list) : float option =
    match xs with
    | [] -> None
    | _ ->
        let n = float (List.length xs)
        let mean = List.average xs

        let variance =
            xs |> List.sumBy (fun x -> (x - mean) * (x - mean)) |> (fun s -> s / n)

        Some(sqrt variance)

let private aggregateNumeric (values: float list) : NumericAggregation = {
    Count = List.length values
    Mean =
        match values with
        | [] -> None
        | _ -> Some(List.average values)
    Median = median values
    StdDev = populationStdDev values
    Min =
        match values with
        | [] -> None
        | _ -> Some(List.min values)
    Max =
        match values with
        | [] -> None
        | _ -> Some(List.max values)
}

let private aggregateChoice (options: string list) (votes: string list) : ChoiceAggregation =
    let initial = options |> List.map (fun o -> o, 0) |> Map.ofList

    let counts =
        votes
        |> List.fold
            (fun acc v ->
                let prev = Map.tryFind v acc |> Option.defaultValue 0
                Map.add v (prev + 1) acc)
            initial

    {
        Counts = counts
        TotalVotes = List.length votes
    }

let private aggregateBool (values: bool list) : BoolAggregation = {
    TrueCount = values |> List.filter id |> List.length
    FalseCount = values |> List.filter not |> List.length
}

[<Literal>]
let private TextSampleLimit = 10

[<Literal>]
let private TextSampleCharCap = 200

let private aggregateText (values: string list) : TextAggregation =
    let truncate (s: string) =
        if s.Length <= TextSampleCharCap then
            s
        else
            s.Substring(0, TextSampleCharCap) + "…"

    {
        ResponseCount = List.length values
        Sample = values |> List.truncate TextSampleLimit |> List.map truncate
    }

let private aggregateDate (values: DateTimeOffset list) : DateAggregation = {
    Count = List.length values
    Min =
        match values with
        | [] -> None
        | _ -> Some(List.min values)
    Max =
        match values with
        | [] -> None
        | _ -> Some(List.max values)
}

/// Aggregate one field across all committed submissions. Returns
/// `OpaqueAggregation` for kinds without a meaningful summary
/// (file uploads, nested submissions, entity refs).
let aggregateField (field: FieldSchema) (submissions: Submission list) : FieldAggregation =
    let extractedValues =
        submissions |> List.choose (fun s -> Map.tryFind field.Key s.Values)

    match field.Kind with
    | NumberField _ ->
        extractedValues
        |> List.choose extractNumeric
        |> aggregateNumeric
        |> NumericFieldAggregation
    | ChoiceField options ->
        extractedValues
        |> List.collect extractChoice
        |> aggregateChoice options
        |> ChoiceFieldAggregation
    | MultiChoiceField options ->
        extractedValues
        |> List.collect extractChoice
        |> aggregateChoice options
        |> ChoiceFieldAggregation
    | BoolField ->
        extractedValues
        |> List.choose extractBool
        |> aggregateBool
        |> BoolFieldAggregation
    | TextField _ ->
        extractedValues
        |> List.choose extractText
        |> aggregateText
        |> TextFieldAggregation
    | DateField
    | DateTimeField ->
        extractedValues
        |> List.choose extractDate
        |> aggregateDate
        |> DateFieldAggregation
    | FileField _
    | EntityRefField _
    | NestedFormField _ -> OpaqueAggregation(extractedValues |> List.length)
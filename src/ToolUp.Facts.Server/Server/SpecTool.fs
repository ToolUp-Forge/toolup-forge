// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Globalization
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra
open ToolUp.Platform.Scope

// ─── Spec in, Facts out: the door ────────────────────────────────────
//
// A spec tool is an AI tool whose argument is a closed, hashable spec the
// model writes over one gated slice of one fact table. A deployment supplies
// what is particular to its spec:
//
//   - `Decode`: the tool's JSON arguments to the slice and the spec, refusing
//     a malformed one by name (`sliceOf` reads the standard slice arguments);
//   - `Validate`: the static checks run before anything reads a fact, whose
//     refusals are returned TOGETHER so the model repairs in one call;
//   - `Evaluate`: the evaluation over the gated slice (`SpecRead`), answering
//     the tool's own result;
//   - `Hash`: the spec's canonical hash, which the asserted facts' method
//     names, and `Project`: the result as the door reads it (`SpecResult`).
//
// The door is written once, here, for every spec tool:
//
//   1. the arguments are parsed, decoded and validated before any read;
//   2. the slice is read through the fact browse's view-slice read under the
//      caller's scope and principal, with the gate asked at the TOOL-RESULT
//      door; a slice with no fact, or with every cell withheld, is refused —
//      a principal the policy denies gets a refusal, not an empty table;
//   3. the evaluator runs over the gated slice (and any other table it reads,
//      through the same gated read: `SpecRead.Read`);
//   4. the result is bounded by the slice's cell cap;
//   5. the rows the result declares as facts (`SpecResult.Facts`) are minted
//      with `Computed(OperationId, Version, Hash spec)`, each naming the facts
//      it was computed from, asserted as one audited batch, read back and
//      judged at the tool-result door. A result that declares no row is a
//      block only and is never a fact.
//
// Every refusal is the tool's own type, implementing `ISpecRefusal`, and the
// model reads it framed one way (`refusalText`). The result budget is the
// definition's, applied by the AI tool registry like every tool's.

/// What a spec tool's arguments name: the slice and the spec over it.
type SpecRequest<'Spec> = {
    /// The slice of one fact table the spec is evaluated over.
    Slice: FactSliceQuery
    /// The spec.
    Spec: 'Spec
}

/// What an evaluator reads: the slice the gate admitted, and the gated read of
/// any other table the spec reads.
type SpecRead = {
    /// The request's slice, judged at the tool-result door, with at least one
    /// cell the caller may be shown.
    Slice: FactSlice
    /// When the slice was read.
    ReadAt: DateTime
    /// The metric registry, when one is composed.
    Registry: IMetricRegistry option
    /// Another fact table read through the same gated read, over the request's
    /// period range and subject prefix: the table and its metrics (empty: all
    /// of them).
    Read: string -> string list -> Async<Result<FactSlice, SpecDoorRefusal>>
}

/// What the assertion door stored for a result.
type SpecAssertion = {
    /// One fact per declared row, in row order.
    Facts: PipelineAssertedFact list
    /// Rows newly stored as the first fact of their lineage.
    AssertedCount: int
    /// Rows newly stored that superseded a predecessor.
    SupersedingCount: int
    /// Rows whose fact was already stored.
    IdempotentCount: int
    /// The batch receipt's digest over its full ordered disposition.
    ReceiptDigest: string
}

/// A "spec in, Facts out" tool: what is particular to one spec. The door
/// (`SpecTool.answer`) is the rest.
type SpecTool<'Spec, 'Refusal, 'Result when 'Refusal :> ISpecRefusal> = {
    /// The tool's declaration.
    Definition: AIToolDefinition
    /// The `Computed` operation id an asserted fact carries: the identity a
    /// derived metric's `ProducingOperation` names.
    OperationId: string
    /// The version of the evaluator the spec runs under.
    Version: string
    /// The arguments as the slice and the spec.
    Decode: JsonElement -> Result<SpecRequest<'Spec>, 'Refusal>
    /// The static checks run before any fact is read, for the scope and the
    /// principal: the spec to evaluate, or every refusal found.
    Validate: ResolvedScope -> string -> SpecRequest<'Spec> -> Result<'Spec, 'Refusal list>
    /// The evaluation over the gated slice.
    Evaluate: SpecRead -> SpecRequest<'Spec> -> Async<Result<'Result, 'Refusal>>
    /// The spec's canonical hash.
    Hash: 'Spec -> string
    /// The result as the door reads it.
    Project: 'Result -> SpecResult
    /// The text the model reads for a result: never a cell value.
    Summary: 'Result -> string
    /// A door refusal as the tool's own refusal.
    OfDoor: SpecDoorRefusal -> 'Refusal
}

/// The spec-tool door.
[<RequireQualifiedAccess>]
module SpecTool =

    // ── The slice arguments ───────────────────────────────────────

    let private malformed (reason: string) =
        Error(SpecDoorRefusal.MalformedArguments reason)

    let private instantOf (name: string) (text: string) : Result<DateTime, SpecDoorRefusal> =
        let styles = DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal

        match DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, styles) with
        | true, day -> Ok day
        | false, _ ->
            match DateTime.TryParse(text, CultureInfo.InvariantCulture, styles) with
            | true, instant -> Ok instant
            | false, _ -> malformed (sprintf "'%s' is not a date (yyyy-MM-dd) or an ISO-8601 instant: '%s'" name text)

    let private textsOf (name: string) (root: JsonElement) : Result<string list, SpecDoorRefusal> =
        match root.TryGetProperty name with
        | false, _ -> Ok []
        | true, v when v.ValueKind = JsonValueKind.Null -> Ok []
        | true, v when v.ValueKind = JsonValueKind.Array ->
            let items = v.EnumerateArray() |> List.ofSeq

            if items |> List.forall (fun i -> i.ValueKind = JsonValueKind.String) then
                Ok(items |> List.map _.GetString())
            else
                malformed (sprintf "'%s' must be an array of strings" name)
        | true, _ -> malformed (sprintf "'%s' must be an array of strings" name)

    /// Member `name` of `root` as a non-empty string, or the refusal naming it.
    let textOf (name: string) (root: JsonElement) : Result<string, SpecDoorRefusal> =
        match root.TryGetProperty name with
        | true, v when
            v.ValueKind = JsonValueKind.String
            && not (String.IsNullOrWhiteSpace(v.GetString()))
            ->
            Ok(v.GetString())
        | _ -> malformed (sprintf "'%s' is required: a non-empty string" name)

    /// The standard slice arguments of `root` — `table`, `period_from`,
    /// `period_to`, `path_prefix` and `metrics` — refused at the first that is
    /// malformed, in that order.
    let sliceOf (root: JsonElement) : Result<FactSliceQuery, SpecDoorRefusal> =
        match
            textOf "table" root,
            textOf "period_from" root |> Result.bind (instantOf "period_from"),
            textOf "period_to" root |> Result.bind (instantOf "period_to"),
            textsOf "path_prefix" root,
            textsOf "metrics" root
        with
        | Ok table, Ok from, Ok until, Ok prefix, Ok metrics ->
            Ok {
                TableId = table
                PeriodFrom = from
                PeriodTo = until
                SubjectPrefix = prefix
                Metrics = metrics
            }
        | Error e, _, _, _, _
        | _, Error e, _, _, _
        | _, _, Error e, _, _
        | _, _, _, Error e, _
        | _, _, _, _, Error e -> Error e

    /// The declarations of the standard slice arguments `sliceOf` reads.
    let sliceParameters: ToolParameterSchema list = [
        ToolSchema.parameter "table" "The fact table to read." true ToolSchema.text
        ToolSchema.parameter
            "period_from"
            "The first instant of the slice, yyyy-MM-dd or an ISO-8601 UTC instant (inclusive): a fact is read when its period overlaps the range."
            true
            ToolSchema.text
        ToolSchema.parameter
            "period_to"
            "The end of the slice, yyyy-MM-dd or an ISO-8601 UTC instant (exclusive)."
            true
            ToolSchema.text
        ToolSchema.parameter
            "path_prefix"
            "Member ids from the hierarchy root narrowing the subjects. Omit for every subject of the table."
            false
            (ToolSchema.arrayOf ToolSchema.text)
        ToolSchema.parameter
            "metrics"
            "The metric columns of the table to read. Omit for all of them."
            false
            (ToolSchema.arrayOf ToolSchema.text)
    ]

    /// The arguments as a JSON object, or the refusal naming why not.
    let parse (argsJson: string) : Result<JsonElement, SpecDoorRefusal> =
        let parsed =
            try
                Ok((JsonDocument.Parse argsJson).RootElement.Clone())
            with _ ->
                malformed "the arguments are not valid JSON"

        match parsed with
        | Ok root when root.ValueKind <> JsonValueKind.Object -> malformed "the arguments must be a JSON object"
        | other -> other

    // ── The bounds ────────────────────────────────────────────────

    /// The slices refused before evaluation: a table with no fact in range,
    /// or one every cell of which is withheld from the caller.
    let readable (table: string) (slice: FactSlice) : Result<FactSlice, SpecDoorRefusal> =
        if slice.CellCount = 0 then
            Error(SpecDoorRefusal.NoFacts table)
        elif slice.WithheldCount >= slice.CellCount then
            Error(
                SpecDoorRefusal.NothingDisclosable(
                    slice.CellCount,
                    slice.Withheld
                    |> List.map (fun w ->
                        ({
                            PolicyRef = w.PolicyRef
                            Count = w.Count
                        }
                        : PipelineWithheldCount))
                )
            )
        else
            Ok slice

    /// A result of `cells` cells, refused when it is over the slice's cell cap.
    let withinCap (cells: int) : Result<unit, SpecDoorRefusal> =
        if cells > FactBrowseApi.MaxSliceCells then
            Error(SpecDoorRefusal.ResultOverCap(cells, FactBrowseApi.MaxSliceCells))
        else
            Ok()

    // ── Evaluation ────────────────────────────────────────────────

    /// The evaluation of the arguments for `principal` in `scope`, with the
    /// gated slice it was computed over and the request it decoded: parsed,
    /// decoded and validated before any read, then read, evaluated and bounded.
    let evaluateSliced
        (tool: SpecTool<'Spec, 'Refusal, 'Result>)
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<Result<FactSlice * SpecRequest<'Spec> * 'Result, 'Refusal list>> =
        async {
            match
                parse argsJson
                |> Result.mapError tool.OfDoor
                |> Result.bind tool.Decode
                |> Result.mapError List.singleton
                |> Result.bind (fun request ->
                    tool.Validate scope principal request
                    |> Result.map (fun spec -> { request with Spec = spec }))
            with
            | Error refusals -> return Error refusals
            | Ok request ->
                let gated = {
                    deps with
                        Gate = FactBrowseHandler.atSurface FactToolResult deps.Gate
                }

                let readAt = deps.Clock()

                let read (table: string) (metrics: string list) = async {
                    match!
                        FactBrowseHandler.querySlice gated scope principal {
                            request.Slice with
                                TableId = table
                                Metrics = metrics
                        }
                    with
                    | Error reason -> return Error(SpecDoorRefusal.SliceRefused reason)
                    | Ok slice -> return Ok slice
                }

                let! primary = read request.Slice.TableId request.Slice.Metrics

                match primary |> Result.bind (readable request.Slice.TableId) with
                | Error refusal -> return Error [ tool.OfDoor refusal ]
                | Ok slice ->
                    let specRead = {
                        Slice = slice
                        ReadAt = readAt
                        Registry = deps.Registry
                        Read = read
                    }

                    match! tool.Evaluate specRead request with
                    | Error refusal -> return Error [ refusal ]
                    | Ok result ->
                        let projected = tool.Project result

                        match withinCap (projected.Rows * List.length projected.Columns) with
                        | Error refusal -> return Error [ tool.OfDoor refusal ]
                        | Ok() -> return Ok(slice, request, result)
        }

    // ── Facts out ─────────────────────────────────────────────────

    /// The most facts one asserted value may name as its inputs.
    let MaxInputs: int = PopulationQuery.MaxTopK

    /// The registered metric `metric`, refused unless it is registered as
    /// produced by `operationId`.
    let derivedMetric
        (registry: IMetricRegistry option)
        (operationId: string)
        (metric: string)
        : Result<MetricDefinition, SpecDoorRefusal> =
        match registry |> Option.bind (fun r -> r.TryGetMetric metric) with
        | None -> Error(SpecDoorRefusal.UnregisteredMetric metric)
        | Some definition when definition.ProducingOperation = Some operationId -> Ok definition
        | Some definition ->
            Error(SpecDoorRefusal.NotADerivedMetric(metric, definition.ProducingOperation, operationId))

    /// A row refused when its inputs cannot be named or are too many for one
    /// fact.
    let checkRow (row: SpecFactRow) : Result<unit, SpecDoorRefusal> =
        let named = SubjectRef.toString row.Subject

        if List.isEmpty row.Inputs then
            Error(SpecDoorRefusal.InputsUnnamed named)
        elif List.length row.Inputs > MaxInputs then
            Error(SpecDoorRefusal.InputsOverCap(named, List.length row.Inputs, MaxInputs))
        else
            Ok()

    /// The draft a row is asserted as, under `method`, with evidence naming
    /// every input fact and `trigger` as its trigger ref.
    let draftOf (method: MethodRef) (trigger: string option) (row: SpecFactRow) : FactDraft = {
        Subject = row.Subject
        Metric = MetricRef row.Metric
        Value = Scalar row.Value
        Period = row.Period
        Method = method
        Evidence = {
            ResultRef = None
            InputHashes = row.Inputs |> List.map FactInput.ofFactId
            TriggerRef = trigger
        }
        Confidence = None
        Disclosure = Surfaceable
    }

    /// The content address a draft is stored under.
    let factIdOf (d: FactDraft) : string =
        Fact.compute d.Subject d.Metric d.Period d.Method (Fact.effectiveInputHashes d.Method d.Evidence d.Value)

    let private firstError (checks: Result<unit, SpecDoorRefusal> list) : Result<unit, SpecDoorRefusal> =
        checks
        |> List.tryPick (function
            | Error e -> Some e
            | Ok() -> None)
        |> function
            | Some e -> Error e
            | None -> Ok()

    /// The rows asserted for `principal` in `scope` as facts of the spec
    /// `spec`: each metric checked as derived by the tool's operation and each
    /// row's inputs named, then one audited batch, read back and judged at the
    /// tool-result door. The facts' trigger ref names the tool and the
    /// principal.
    let assertRows
        (tool: SpecTool<'Spec, 'Refusal, 'Result>)
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (spec: 'Spec)
        (rows: SpecFactRow list)
        : Async<Result<SpecAssertion, 'Refusal>> =
        async {
            let metrics = rows |> List.map _.Metric |> List.distinct

            let checkedMetrics =
                metrics
                |> List.map (fun m -> derivedMetric deps.Registry tool.OperationId m |> Result.map (fun d -> m, d))

            match firstError ((checkedMetrics |> List.map (Result.map ignore)) @ (rows |> List.map checkRow)) with
            | Error refusal -> return Error(tool.OfDoor refusal)
            | Ok() ->
                let formats =
                    checkedMetrics
                    |> List.choose Result.toOption
                    |> List.map (fun (m, d) -> m, d.DisplayFormat)
                    |> Map.ofList

                let method = Computed(tool.OperationId, tool.Version, tool.Hash spec)
                let trigger = Some(sprintf "%s:%s" tool.Definition.Name principal)
                let drafts = rows |> List.map (draftOf method trigger)

                match! deps.Store.AssertBatch(scope, drafts) with
                | Error reason ->
                    return Error(tool.OfDoor(SpecDoorRefusal.AssertionFailed(FactStoreError.describe reason)))
                | Ok receipt ->
                    let ids = drafts |> List.map factIdOf

                    let! stored =
                        ids
                        |> List.map (fun id -> deps.Store.Get(scope, id) |> FactStoreResult.orRaiseAsync)
                        |> Async.Sequential

                    let held = stored |> Array.choose id |> List.ofArray

                    let! verdicts =
                        (FactBrowseHandler.atSurface FactToolResult deps.Gate)
                            .CheckHeld(scope, principal, FactToolResult, ids, held)

                    let facts =
                        List.zip3 ids drafts rows
                        |> List.map (fun (factId, d, row) ->
                            let withheld =
                                match verdicts.TryFind factId with
                                | Some FactDisclosable -> None
                                | Some(FactNotDisclosable policy) -> Some policy
                                | None -> Some "unknown-fact"

                            {
                                FactId = factId
                                Subject = SubjectRef.toString d.Subject
                                Metric = row.Metric
                                Rendering =
                                    match withheld with
                                    | None ->
                                        Some(
                                            FactRendering.render
                                                (formats.TryFind row.Metric |> Option.defaultValue "")
                                                d.Value
                                        )
                                    | Some _ -> None
                                WithheldPolicy = withheld
                                InputCount = List.length d.Evidence.InputHashes
                            }
                            : PipelineAssertedFact)

                    return
                        Ok {
                            Facts = facts
                            AssertedCount = receipt.AssertedCount
                            SupersedingCount = receipt.SupersedingCount
                            IdempotentCount = receipt.IdempotentCount
                            ReceiptDigest = receipt.Digest
                        }
        }

    /// The door: the evaluation, and, when its result declares rows as facts,
    /// their assertion. A result that declares none answers no assertion.
    let answer
        (tool: SpecTool<'Spec, 'Refusal, 'Result>)
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<Result<'Result * SpecAssertion option, 'Refusal list>> =
        async {
            match! evaluateSliced tool deps scope principal argsJson with
            | Error refusals -> return Error refusals
            | Ok(_, request, result) ->
                match (tool.Project result).Facts with
                | [] -> return Ok(result, None)
                | rows ->
                    match! assertRows tool deps scope principal request.Spec rows with
                    | Error refusal -> return Error [ refusal ]
                    | Ok assertion -> return Ok(result, Some assertion)
        }

    // ── The answer ────────────────────────────────────────────────

    /// The text a refusal answers with: one refusal as one sentence, several
    /// numbered, each with its remedy, so the model repairs in one call.
    let refusalText (toolName: string) (refusals: 'Refusal list) : string =
        match refusals |> List.map (fun r -> (r :> ISpecRefusal).Remedy) with
        | [ one ] -> sprintf "%s refused the request: %s." toolName one
        | many ->
            sprintf
                "%s refused the request for %d reasons: %s."
                toolName
                (List.length many)
                (many |> List.mapi (fun i r -> sprintf "(%d) %s" (i + 1) r) |> String.concat "; ")

    /// The asserted facts as the model reads them: each with its id and, where
    /// the model may be shown it, its rendering, which is what the answer
    /// verifier reads.
    let factsJson (facts: PipelineAssertedFact list) : obj list =
        facts
        |> List.map (fun f ->
            match f.Rendering with
            | Some rendering ->
                box {|
                    factId = f.FactId
                    subject = f.Subject
                    metric = f.Metric
                    rendering = rendering
                    inputs = f.InputCount
                |}
            | None ->
                box {|
                    factId = f.FactId
                    subject = f.Subject
                    metric = f.Metric
                    withheld = f.WithheldPolicy |> Option.defaultValue ""
                    inputs = f.InputCount
                |})

    /// The note an asserted answer carries.
    [<Literal>]
    let AssertedNote =
        "Each asserted figure is a fact: quote it by its rendering, which verifies. State no other figure from the result."

    /// The text the model reads for an answer: the summary, and the asserted
    /// facts when there are any.
    let text (tool: SpecTool<'Spec, 'Refusal, 'Result>) (result: 'Result, assertion: SpecAssertion option) : string =
        match assertion with
        | None -> tool.Summary result
        | Some asserted ->
            FactTool.serialize {|
                summary = tool.Summary result
                asserted = {|
                    facts = factsJson asserted.Facts
                    newFacts = asserted.AssertedCount
                    superseding = asserted.SupersedingCount
                    unchanged = asserted.IdempotentCount
                    receiptDigest = asserted.ReceiptDigest
                |}
                note = AssertedNote
            |}

    /// The door over explicit dependencies, answered as the text the model
    /// reads.
    let answerText
        (tool: SpecTool<'Spec, 'Refusal, 'Result>)
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<string> =
        async {
            match! answer tool deps scope principal argsJson with
            | Error refusals -> return refusalText tool.Definition.Name refusals
            | Ok answered -> return text tool answered
        }

    /// The registered executor: the fact browse's dependencies for this
    /// request, the scope the platform's scope resolution minted for it, and
    /// the request's user as the principal.
    let executor (tool: SpecTool<'Spec, 'Refusal, 'Result>) : HttpContext -> string -> Async<string> =
        fun ctx argsJson ->
            match FactBrowseHandler.factBrowseDeps ctx with
            | Some deps ->
                answerText
                    tool
                    deps
                    (StorageScopeResolver.ScopeResolution.forRequest ctx)
                    (FactTool.userIdOf ctx)
                    argsJson
            | None ->
                async.Return(
                    sprintf
                        "%s refused the request: the fact store is not composed in this deployment, so there are no facts to compute over."
                        tool.Definition.Name
                )
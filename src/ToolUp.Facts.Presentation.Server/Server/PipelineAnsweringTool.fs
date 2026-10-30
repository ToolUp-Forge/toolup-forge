// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts.Presentation

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.AI

// ─── evaluate_pipeline in the answering form ────────────────────────
//
// The fact tier's `PipelineTool` evaluates the model's pipeline over a gated
// slice and returns a record; this module is where that record reaches the
// conversation. The executor runs unchanged, with the gate asked at the
// TOOL-RESULT door, and its record becomes:
//
//   - the text the model reads: `PipelineTool.summary`, the shape, the column
//     names and the withheld report, never a value;
//   - one block part the assistant message carries: the slice spec, the
//     canonical pipeline, its hash, the run stamp and the withheld report as
//     the part's provenance, and a node naming the result's columns with no
//     row. No cell value is persisted.
//
// A reader's panel draws the part by evaluating the persisted request again
// under the reader's own gate, at `FactBrowse` — the door every other fact a
// reader is shown passes — through `IPipelineBlockApi`, mounted beside the
// tool by `PresentationCompose.withPipelineTool`. The re-run is the same
// executor over the same slice read: only the principal and the door differ.

/// `evaluate_pipeline` composed in the answering form, and the re-run a
/// pipeline block part is drawn from.
[<RequireQualifiedAccess>]
module PipelineAnsweringTool =

    let private refusal (refused: PipelineRefusal) : ToolHelpers.ToolAnswer = {
        Text = PipelineTool.refusalText refused
        Parts = []
    }

    /// The request a part persists for an evaluation: the request as the
    /// executor read it, with its canonical pipeline, the pipeline's hash and
    /// the run stamp.
    let requestOf (result: PipelineToolResult) : PipelineBlockRequest = {
        TableId = result.Request.TableId
        PeriodFrom = result.Request.PeriodFrom
        PeriodTo = result.Request.PeriodTo
        PathPrefix = result.Request.PathPrefix
        Metrics = result.Request.Metrics
        Pipeline = result.Request.Pipeline
        PipelineHash = result.PipelineHash
        RunStamp = result.RunStamp
    }

    /// An evaluation as the block's wire result.
    let resultOf (result: PipelineToolResult) : PipelineBlockResult = {
        Table = result.Table
        Rows = result.Rows
        Columns = result.Columns
        Withheld =
            result.Withheld
            |> List.map (fun c -> {
                Column = c.Column
                Metrics = c.Metrics
                Withheld =
                    c.Withheld
                    |> List.map (fun w ->
                        ({
                            PolicyRef = w.PolicyRef
                            Count = w.Count
                        }
                        : BlockWithheld))
            })
        PipelineHash = result.PipelineHash
        RunStamp = result.RunStamp
        FactIds = result.FactIds
        FactIdCount = result.FactIdCount
    }

    /// An evaluation answered as `text` and the block part.
    let private answered (text: string) (result: PipelineToolResult) : ToolHelpers.ToolAnswer =
        match PipelineBlock.frame<unit> (requestOf result) (resultOf result) with
        | Error defect -> refusal (PipelineRefusal.EvaluationFailed(PresentationDefect.describe defect))
        | Ok block -> {
            Text = text
            Parts = [ BlockPart(Fuaran.UI.Generated.encodeNode block.Node, block.Provenance) ]
          }

    /// The executor over explicit dependencies: the slice read under `scope`
    /// for `principal`, with the gate asked at `FactToolResult`, answered as
    /// the summary and the block part. Arguments carrying `assert` are refused.
    let answerWith
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<ToolHelpers.ToolAnswer> =
        async {
            match! PipelineTool.answerWith deps scope principal argsJson with
            | Error refused -> return refusal refused
            | Ok result -> return answered (PipelineTool.summary result) result
        }

    /// The executor composed with derived assertions: the answer `answerWith`
    /// gives, and, when the arguments carry `assert`, the asserted facts in the
    /// text the model reads (`PipelineAssertion.text`), where the answer
    /// verifier finds them. The block part is the evaluation's, unchanged.
    let answerAssertingWith
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<ToolHelpers.ToolAnswer> =
        async {
            match! PipelineAssertion.answerWith deps scope principal argsJson with
            | Error refused -> return refusal refused
            | Ok(result, Some asserted) -> return answered (PipelineAssertion.text asserted) result
            | Ok(result, None) -> return answered (PipelineTool.summary result) result
        }

    /// A persisted request evaluated again for `principal` under `scope`, with
    /// the gate asked at `FactBrowse`: the result a reader's panel draws.
    /// Refused in the executor's own words.
    let rerunWith
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (requestJson: string)
        : Async<Result<PipelineBlockResult, string>> =
        async {
            // The executor asks its gate at the tool-result door; a gate fixed
            // at `FactBrowse` is asked there whichever door the read names.
            let atBrowse = {
                deps with
                    Gate = FactBrowseHandler.atSurface FactBrowse deps.Gate
            }

            match! PipelineTool.answerWith atBrowse scope principal requestJson with
            | Ok result -> return Ok(resultOf result)
            | Error refused -> return Error(PipelineRefusal.describe refused)
        }

    let private userIdOf (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

    let private notComposed =
        "the fact store is not composed in this deployment, so there are no facts to compute over"

    /// The registered executor: the fact browse's dependencies for this
    /// request, the scope the platform's scope resolution minted for it, and
    /// the request's user as the principal.
    let answer (ctx: HttpContext) (argsJson: string) : Async<ToolHelpers.ToolAnswer> =
        match FactBrowseHandler.factBrowseDeps ctx with
        | Some deps -> answerWith deps (StorageScopeResolver.ScopeResolution.forRequest ctx) (userIdOf ctx) argsJson
        | None ->
            async.Return {
                Text = sprintf "%s refused the request: %s." PipelineTool.Name notComposed
                Parts = []
            }

    /// The tool, as `AIServerApp.withAIAnsweringTools` takes it.
    let tool: AIToolDefinition * (HttpContext -> string -> Async<ToolHelpers.ToolAnswer>) =
        PipelineTool.definition, answer

    /// The registered executor with derived assertions.
    let answerAsserting (ctx: HttpContext) (argsJson: string) : Async<ToolHelpers.ToolAnswer> =
        match FactBrowseHandler.factBrowseDeps ctx with
        | Some deps ->
            answerAssertingWith deps (StorageScopeResolver.ScopeResolution.forRequest ctx) (userIdOf ctx) argsJson
        | None ->
            async.Return {
                Text = sprintf "%s refused the request: %s." PipelineTool.Name notComposed
                Parts = []
            }

    /// The tool with derived assertions: the `assert` argument in its
    /// declaration, and the executor that asserts.
    let assertingTool: AIToolDefinition * (HttpContext -> string -> Async<ToolHelpers.ToolAnswer>) =
        PipelineTool.definitionWithAssertions, answerAsserting

    /// The per-request re-run API, for the request's user.
    let api (ctx: HttpContext) : IPipelineBlockApi = {
        Evaluate =
            fun requestJson ->
                match FactBrowseHandler.factBrowseDeps ctx with
                | Some deps ->
                    rerunWith deps (StorageScopeResolver.ScopeResolution.forRequest ctx) (userIdOf ctx) requestJson
                | None -> async.Return(Error notComposed)
    }

    /// The mounted route handler: `IPipelineBlockApi` at
    /// `/api/_facts/pipeline-block/*`.
    let handler: Giraffe.Core.HttpHandler =
        Api.make (api, routeBuilder = PipelineBlock.routeBuilder)
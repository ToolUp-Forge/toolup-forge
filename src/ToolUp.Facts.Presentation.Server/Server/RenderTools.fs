// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts.Presentation

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.AI

// ─── The render tools (Phase 1132) ──────────────────────────────────
//
// `render_chart` and `render_table`: the model writes a presentation request
// (Phase 1129) and the server answers with a block whose every number is a
// fact the disclosure gate admitted. The model writes the request, never a
// row, so it cannot put a figure on the page.
//
// Each executor:
//
//   1. decodes the request through the presentation codec, refusing a
//      malformed one by name, and refuses a kind the other tool draws;
//   2. checks the table, each metric and the subject level against the
//      deployment's declarations, refusing an unknown one by name;
//   3. reads one series per metric through the fact browse's series read,
//      with the gate asked at the TOOL-RESULT door (`FactToolResult`): the
//      result reaches the model and the conversation, so it is judged where
//      every other fact tool's result is judged, never at the browse door;
//   4. draws the block with the presentation builders, which validate the
//      node before it leaves;
//   5. answers the record form (Phase 1131): a text summary the model reads,
//      and the block part the assistant message carries.
//
// **What the model reads.** Counts, the request's canonical period, the
// title and the policies that withheld points — never a value, and never a
// number the tool computed beyond a count. The values are in the block,
// which the user sees and the model does not. A withheld point is a count
// in the summary; in the node it is named, with its reason, and never drawn.
//
// **A principal the policy denies gets a refusal, not an empty chart.** When
// no point of the read carries a published number — every point withheld,
// none found, or every read refused — the tool attaches nothing and says why.
//
// **Dependencies are values.** The executor's core takes the fact browse's
// dependencies, the caller's resolved scope and principal; the `HttpContext`
// adapter takes the dependencies from the fact browse's own per-request
// resolution (`FactBrowseHandler.factBrowseDeps`) and reads no container
// itself.

/// Which render tool.
[<RequireQualifiedAccess>]
type RenderTool =
    /// `render_chart`: a trend, a breakdown or a comparison.
    | Chart
    /// `render_table`: subjects by metrics for one delivery.
    | Table

/// The `render_chart` and `render_table` AI tools: definitions and executors
/// over the fact browse's series read. Composed with
/// `PresentationCompose.withRenderTools`.
[<RequireQualifiedAccess>]
module RenderTools =

    /// The chart tool's name.
    [<Literal>]
    let ChartToolName = "render_chart"

    /// The table tool's name.
    [<Literal>]
    let TableToolName = "render_table"

    /// A tool's name.
    let name (tool: RenderTool) : string =
        match tool with
        | RenderTool.Chart -> ChartToolName
        | RenderTool.Table -> TableToolName

    /// The kinds a tool draws.
    let kinds (tool: RenderTool) : PresentationKind list =
        match tool with
        | RenderTool.Chart -> [
            PresentationKind.Trend
            PresentationKind.Breakdown
            PresentationKind.Comparison
          ]
        | RenderTool.Table -> [ PresentationKind.Table ]

    // ── Definitions ───────────────────────────────────────────────

    let private periodSchema =
        ToolSchema.objectOf [
            ToolSchema.field
                "day"
                "The last day covered, yyyy-MM-dd. Give day (with rangeDays), or from and to."
                false
                ToolSchema.text
            ToolSchema.field "rangeDays" "The days ending on day, inclusive. Defaults to 1." false ToolSchema.integer
            ToolSchema.field "from" "The first day covered, yyyy-MM-dd (inclusive)." false ToolSchema.text
            ToolSchema.field "to" "The day after the last day covered, yyyy-MM-dd (exclusive)." false ToolSchema.text
        ]

    let private annotationSchema =
        ToolSchema.objectOf [
            ToolSchema.field "on" "The day marked, yyyy-MM-dd." true ToolSchema.text
            ToolSchema.field "label" "What the mark says. Text, never a number to plot." true ToolSchema.text
        ]

    let private parameters (tool: RenderTool) : ToolParameterSchema list = [
        ToolSchema.parameter
            "kind"
            (match tool with
             | RenderTool.Chart ->
                 "trend: one line per subject over the days of the period. breakdown: one bar per subject for the last day. comparison: the last day against the delivery before it, per subject."
             | RenderTool.Table -> "Always table: subjects by metrics for the last day of the period.")
            true
            (ToolSchema.enumOf (kinds tool |> List.map PresentationRequest.kindName))
        ToolSchema.parameter "tableId" "The fact table to read." true ToolSchema.text
        ToolSchema.parameter
            "metrics"
            (match tool with
             | RenderTool.Chart -> "Exactly one metric column of the table."
             | RenderTool.Table -> "The metric columns of the table, one table column each.")
            true
            (ToolSchema.arrayOf ToolSchema.text)
        ToolSchema.parameter
            "subjectPrefix"
            "Member ids from the hierarchy root narrowing the subjects. Omit for every subject of the table."
            false
            (ToolSchema.arrayOf ToolSchema.text)
        ToolSchema.parameter
            "subjectLevel"
            "The hierarchy level the subjects sit at. Omit for the table's declared level."
            false
            ToolSchema.text
        ToolSchema.parameter "period" "The whole UTC days covered." true periodSchema
        match tool with
        | RenderTool.Chart ->
            ToolSchema.parameter
                "compareTo"
                "A comparison only: what the last day is drawn against."
                false
                (ToolSchema.enumOf [ "previousDelivery" ])
        | RenderTool.Table -> ()
        ToolSchema.parameter
            "title"
            "The block's title. Omit to derive one from the table and metric."
            false
            ToolSchema.text
        ToolSchema.parameter
            "annotations"
            "Days to mark, each with a label."
            false
            (ToolSchema.arrayOf annotationSchema)
    ]

    let private description (tool: RenderTool) : string =
        let what =
            match tool with
            | RenderTool.Chart ->
                "Show the user a chart of the deployment's verified facts: a trend, a breakdown or a comparison of one metric of one fact table."
            | RenderTool.Table ->
                "Show the user a table of the deployment's verified facts: subjects by one or more metrics of one fact table, for one day."

        what
        + " You write the request; the server reads the facts it names under the disclosure gate and draws them, so every number the user sees is a verified fact. The table, metric and subject-level ids come from the deployment's standing context (the fact tables its modules declared); use ids given in the conversation or system context, since this tool cannot list them. The result tells you what was drawn, how many points were read and how many were withheld; it carries none of the block's values, so do not state any. A malformed request, or an unknown table, metric or level, is refused by name: repair it from the message."

    /// A tool's declaration: server-resident (a part is the server's judgement
    /// of a read the server made), on both surfaces, declaring `ReadFacts`
    /// only, under the fact store's reserved source.
    let definition (tool: RenderTool) : AIToolDefinition = {
        Name = name tool
        Description = description tool
        Parameters = parameters tool
        SourceModule = FactEvents.SourceModule
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
        Effects = ToolEffectDeclaration.readFacts
    }

    // ── The answer ────────────────────────────────────────────────

    let private refusal (tool: RenderTool) (reason: string) : ToolHelpers.ToolAnswer = {
        Text = sprintf "%s refused the request: %s." (name tool) reason
        Parts = []
    }

    let private quoted (ids: string list) : string =
        match ids with
        | [] -> "none"
        | ids -> ids |> List.map (sprintf "'%s'") |> String.concat ", "

    /// The request checked against the deployment's declarations: the table,
    /// each metric, and the subject level when the table's hierarchy is
    /// registered.
    let private checkDeclared
        (deps: FactBrowseHandler.FactBrowseDeps)
        (request: PresentationRequest)
        : Result<unit, string> =
        match deps.Tables.TryGetTable request.TableId with
        | None ->
            Error(
                sprintf
                    "no fact table '%s' is declared in this deployment (declared: %s)"
                    request.TableId
                    (quoted (deps.Tables.Tables |> List.map _.Id))
            )
        | Some table ->
            let metrics = FactTableDefinition.metrics table

            match request.Metrics |> List.tryFind (fun m -> not (List.contains m metrics)) with
            | Some metric ->
                Error(sprintf "fact table '%s' has no metric '%s' (its metrics: %s)" table.Id metric (quoted metrics))
            | None ->
                let subject =
                    deps.Registry |> Option.bind (fun r -> r.TryGetSubject table.Hierarchy)

                match request.SubjectLevel, subject with
                | Some level, Some subject when (FactTableDefinition.levelDepth subject level).IsNone ->
                    Error(
                        sprintf
                            "hierarchy '%s' has no level '%s' (its levels: %s)"
                            table.Hierarchy
                            level
                            (quoted subject.Levels)
                    )
                | _ -> Ok()

    /// The withheld counts, by policy, as one phrase.
    let private withheldPhrase (withheld: BlockWithheld list) : string =
        match withheld with
        | [] -> "none withheld"
        | [ w ] -> sprintf "%d withheld under %s" w.Count w.PolicyRef
        | many ->
            sprintf
                "%d withheld (%s)"
                (many |> List.sumBy _.Count)
                (many
                 |> List.map (fun w -> sprintf "%d under %s" w.Count w.PolicyRef)
                 |> String.concat ", ")

    let private refusalSentences (answers: SeriesAnswer list) : string =
        answers
        |> List.choose (function
            | SeriesAnswer.Refused(metric, reason) -> Some(sprintf " The read refused %s: %s" metric reason)
            | SeriesAnswer.Answered _ -> None)
        |> String.concat ""

    /// The text the model reads for a drawn block. Counts, the canonical
    /// period, the title and the withholding policies only: never a value.
    let summary (request: PresentationRequest) (answers: SeriesAnswer list) (provenance: BlockProvenance) : string =
        let read =
            answers
            |> List.sumBy (function
                | SeriesAnswer.Answered s -> List.length s.Points
                | SeriesAnswer.Refused _ -> 0)

        sprintf
            "Drew a %s of %s from fact table '%s' over %s, titled \"%s\": %d point(s) read, %s.%s The user sees the block; this result carries none of its values, so state none."
            (PresentationRequest.kindName request.Kind)
            (String.concat ", " request.Metrics)
            request.TableId
            provenance.Period
            (PresentationRequest.title request)
            read
            (withheldPhrase provenance.Withheld)
            (refusalSentences answers)

    /// Why nothing is drawable, or `None` when at least one point of the read
    /// carries a published number.
    let private undrawable (request: PresentationRequest) (answers: SeriesAnswer list) : string option =
        let served =
            answers
            |> List.choose (function
                | SeriesAnswer.Answered s -> Some s
                | SeriesAnswer.Refused _ -> None)

        let points = served |> List.collect _.Points

        let published = points |> List.filter (fun p -> not p.Withheld && p.Value.IsSome)

        let refused = refusalSentences answers

        if not (List.isEmpty published) then
            None
        elif List.isEmpty served then
            Some(sprintf "the fact series read refused every metric.%s" refused)
        elif List.isEmpty points then
            Some(
                sprintf
                    "no fact in table '%s' answers it over %s%s"
                    request.TableId
                    (PresentationRequest.periodText request)
                    (if refused = "" then "" else "." + refused)
            )
        else
            let provenance = Provenance.ofAnswers request answers

            Some(
                sprintf
                    "no point it reads carries a number you may be shown: %d point(s) read, %s%s"
                    (List.length points)
                    (withheldPhrase provenance.Withheld)
                    (if refused = "" then "" else "." + refused)
            )

    /// The executor over explicit dependencies — the testable core `answer`
    /// adapts `HttpContext` onto. `deps` are the fact browse's; the read runs
    /// under `scope` for `principal`, with the gate asked at `FactToolResult`.
    let answerWith
        (tool: RenderTool)
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<ToolHelpers.ToolAnswer> =
        async {
            match PresentationRequest.ofJson argsJson with
            | Error e -> return refusal tool (PresentationRequestError.describe e)
            | Ok request when not (List.contains request.Kind (kinds tool)) ->
                let other =
                    match tool with
                    | RenderTool.Chart -> TableToolName
                    | RenderTool.Table -> ChartToolName

                return
                    refusal
                        tool
                        (sprintf
                            "it draws %s, and the request's kind is '%s'; use %s"
                            (kinds tool |> List.map PresentationRequest.kindName |> quoted)
                            (PresentationRequest.kindName request.Kind)
                            other)
            | Ok request ->
                match checkDeclared deps request with
                | Error reason -> return refusal tool reason
                | Ok() ->
                    let gated = {
                        deps with
                            Gate = FactBrowseHandler.atSurface FactToolResult deps.Gate
                    }

                    let! answers =
                        PresentationRequest.toQueries FactBrowseApi.MaxSeriesPoints request
                        |> List.map (fun query -> async {
                            match! FactBrowseHandler.querySeries gated scope principal query with
                            | Ok series -> return SeriesAnswer.Answered series
                            | Error reason -> return SeriesAnswer.Refused(query.Metric, reason)
                        })
                        |> Async.Sequential

                    let answers = List.ofArray answers

                    match undrawable request answers with
                    | Some reason -> return refusal tool reason
                    | None ->
                        match Presentation.build<unit> request answers with
                        | Error defect -> return refusal tool (PresentationDefect.describe defect)
                        | Ok block ->
                            return {
                                Text = summary request answers block.Provenance
                                Parts = [ BlockPart(Fuaran.UI.Generated.encodeNode block.Node, block.Provenance) ]
                            }
        }

    let private userIdOf (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

    /// The registered executor: the fact browse's dependencies for this
    /// request, the scope the platform's scope resolution minted for it, and
    /// the request's user as the principal.
    let answer (tool: RenderTool) (ctx: HttpContext) (argsJson: string) : Async<ToolHelpers.ToolAnswer> =
        match FactBrowseHandler.factBrowseDeps ctx with
        | Some deps ->
            answerWith tool deps (StorageScopeResolver.ScopeResolution.forRequest ctx) (userIdOf ctx) argsJson
        | None ->
            async.Return(
                refusal tool "the fact store is not composed in this deployment, so there are no facts to draw"
            )

    /// Both tools, as `AIServerApp.withAIAnsweringTools` takes them.
    let tools: (AIToolDefinition * (HttpContext -> string -> Async<ToolHelpers.ToolAnswer>)) list = [
        definition RenderTool.Chart, answer RenderTool.Chart
        definition RenderTool.Table, answer RenderTool.Table
    ]
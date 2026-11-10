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
open ToolUp.Platform.Infra
open ToolUp.Platform.Scope
open ToolUp.Platform.Jobs.SmokeTests

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
//   3. reads the series the request asks for (one per metric, and an offset
//      comparison's comparison day) through the fact browse's series read,
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

    /// The comparison baselines a schema offers: `previousDelivery`, then one
    /// `daysBefore:<days>` per offered offset, in the order given.
    let compareToValues (offsets: int list) : string list =
        "previousDelivery"
        :: (offsets
            |> List.map (PresentationCompareTo.DaysBefore >> PresentationCompareTo.text))

    /// The offsets as a deployment may offer them: each from 1 to
    /// `PresentationCompareTo.MaxDaysBefore`, none twice. Raises
    /// `ArgumentException` naming the first offset it refuses, so a bad
    /// offering fails at composition, never in a model's turn.
    let checkOffsets (offsets: int list) : int list =
        match
            offsets
            |> List.tryFind (fun d -> d < 1 || d > PresentationCompareTo.MaxDaysBefore)
        with
        | Some d ->
            invalidArg
                "offsets"
                (sprintf
                    "a comparison offset of %d days is offered; each must be from 1 to %d"
                    d
                    PresentationCompareTo.MaxDaysBefore)
        | None ->
            match offsets |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1) with
            | Some(d, _) -> invalidArg "offsets" (sprintf "the comparison offset of %d days is offered twice" d)
            | None -> offsets

    let private parameters (offsets: int list) (tool: RenderTool) : ToolParameterSchema list = [
        ToolSchema.parameter
            "kind"
            (match tool, offsets with
             | RenderTool.Chart, [] ->
                 "trend: one line per subject over the days of the period. breakdown: one bar per subject for the last day. comparison: the last day against the delivery before it, per subject."
             | RenderTool.Chart, _ ->
                 "trend: one line per subject over the days of the period. breakdown: one bar per subject for the last day. comparison: the last day against the delivery before it, or against the delivery a given number of days before, per subject."
             | RenderTool.Table, _ -> "Always table: subjects by metrics for the last day of the period.")
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
                (match offsets with
                 | [] -> "A comparison only: what the last day is drawn against."
                 | _ ->
                     "A comparison only: what the last day is drawn against. previousDelivery: the latest earlier day with facts. daysBefore:<days>: the delivery that many days earlier (daysBefore:7 is the same day last week, daysBefore:364 the same weekday last year).")
                false
                (ToolSchema.enumOf (compareToValues offsets))
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
    ///
    /// `offsets` are the comparison day offsets the deployment offers
    /// (`PresentationCompose.withComparisonOffsets`): `render_chart`'s schema
    /// lists each as a `daysBefore:<days>` baseline. With none it is the schema
    /// a deployment that offers none has always had, byte for byte. The
    /// offsets shape what the model is told only: the request vocabulary
    /// accepts any offset in bounds. Raises `ArgumentException` on an offset
    /// out of bounds or offered twice.
    let definitionOffering (offsets: int list) (tool: RenderTool) : AIToolDefinition = {
        Name = name tool
        Description = description tool
        Parameters = parameters (checkOffsets offsets) tool
        SourceModule = FactEvents.SourceModule
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
        Effects = ToolEffectDeclaration.readFacts
    }

    /// A tool's declaration for a deployment that offers no comparison
    /// offsets: `definitionOffering []`.
    let definition (tool: RenderTool) : AIToolDefinition = definitionOffering [] tool

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
        match AccessContext.callerIdOf ctx.Items with
        | Some id -> id
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

    /// Both tools, as `AIOptions.withAIAnsweringTools` takes them, their
    /// schemas offering the comparison day `offsets` (`definitionOffering`).
    let toolsOffering
        (offsets: int list)
        : (AIToolDefinition * (HttpContext -> string -> Async<ToolHelpers.ToolAnswer>)) list =
        [
            definitionOffering offsets RenderTool.Chart, answer RenderTool.Chart
            definitionOffering offsets RenderTool.Table, answer RenderTool.Table
        ]

    /// Both tools, offering no comparison offsets: `toolsOffering []`.
    let tools: (AIToolDefinition * (HttpContext -> string -> Async<ToolHelpers.ToolAnswer>)) list =
        toolsOffering []

    // ── The language profile's grammar ────────────────────────────
    //
    // The AI tier's `LanguageProfile` references no Fuaran-UI assembly; this
    // tier supplies the vocabulary it is registered against and runs the
    // language's decoder and pre-emit validator over a block it admitted.

    /// The vocabulary the pinned `Fuaran.UI` package publishes, generated from
    /// the IDL its encoder and decoder are: the kinds and their wire fields,
    /// the node properties and the kind categories. Register a profile
    /// against it: `LanguageProfile.register RenderTools.languageSchema`.
    let languageSchema: LanguageSchema = {
        Kinds = Fuaran.UI.Generated.kindFieldNames
        NodeProperties = Fuaran.UI.Generated.envelopeFieldNames
        Categories =
            Fuaran.UI.Generated.kindCategories
            |> Map.toList
            |> List.collect (fun (category, kinds) -> kinds |> Set.toList |> List.map (fun kind -> kind, category))
            |> Map.ofList
    }

    /// Validate a model-composed block (its wire JSON) under `profile`. A
    /// data-bearing node names one of `requests`, the presentation requests
    /// the caller holds, so its rows come from a gated read and never inline.
    /// The profile's own check runs first; a block it admits is decoded under
    /// a policy admitting exactly the profile's kinds and must pass the
    /// pre-emit validator. `Ok` carries the decoded node.
    let validateComposedBlock
        (profile: RegisteredLanguageProfile)
        (requests: Map<string, PresentationRequest>)
        (blockJson: string)
        : Result<Fuaran.UI.Types.Node<unit>, ComposedBlockRefusal list> =
        match LanguageProfile.check profile (requests |> Map.keys |> Set.ofSeq) blockJson with
        | Error refusals -> Error refusals
        | Ok() ->
            let policy =
                Fuaran.UI.KindPolicy.DecodePolicy.admitting profile.Profile.Name (profile.Profile.Kinds |> Map.keys)

            match Fuaran.UI.Ops.JsonDecode.decodeNodeWithPolicy policy blockJson with
            | Error e -> Error [ ComposedBlockRefusal.Decode(Fuaran.UI.Ops.JsonDecode.DecodeError.render e) ]
            | Ok tree ->
                let node =
                    Fuaran.UI.Ops.Types.WireTree.reify tree
                    |> Fuaran.UI.NodeMap.mapMsg (fun _ -> ())

                let errors =
                    match Fuaran.UI.PreEmitValidate.validateForTransport node with
                    | Ok() -> []
                    | Error defects ->
                        defects
                        |> List.choose (fun d ->
                            match Fuaran.UI.PreEmitValidate.describe d with
                            | code, Fuaran.Core.Severity.Error, message -> Some(sprintf "%s: %s" code message)
                            | _ -> None)

                if List.isEmpty errors then
                    Ok node
                else
                    Error [ ComposedBlockRefusal.Grammar errors ]
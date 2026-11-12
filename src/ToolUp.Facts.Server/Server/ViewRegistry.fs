// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text.Json
open Microsoft.Extensions.DependencyInjection
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open ToolUp.Platform
open ToolUp.Platform.Compose
open ToolUp.Platform.Grounding

// ─── Views over a module's dataset model (Phase 1348) ────────────────
//
// A view (`ServerModule.declareViews`) is a named, versioned `Fuaran.Compute`
// pipeline over a module's modelled tables. At registration every view's
// pipeline is decoded step by step (`PipelineTool.decode`), its parameters
// are held to the ones it declares, and it is checked by `Fuaran.DB`'s
// pipeline check against its module's model: a column the model does not
// name, a table it does not hold or a join along a relation it does not
// declare refuses it by name. The walk's output must be a closed column set,
// and exactly the columns the view declares.
//
// A view is evaluated as `evaluate_pipeline` evaluates a pipeline: its
// parameters bound, over the gated slice of its table and of every table it
// joins, through `PipelineTool.evaluateJoined`, so the lineage and the
// withheld rule are that tool's. A pipeline must pass both the model check
// and the gate: the model check decides shape, the gate every cell.
//
// Each view is a method card (`ViewRegistry.bindings`): its description is
// the card's meaning, its parameters the signature, its columns the outputs
// and its evaluator the evaluation above. One tool per view, registered
// through `MethodRegistry.register` beside every other card; there is no
// separate tool for running a view.

/// A view, checked against its module's model.
type RegisteredView = {
    /// The declaring module.
    Module: string
    /// The view as declared.
    Definition: FactViewDefinition
    /// The pipeline, decoded.
    Pipeline: Transform list
    /// The pipeline's canonical hash.
    PipelineHash: string
    /// The hash of the model it was checked against.
    ModelHash: string
    /// The fact tables it joins, in step order.
    Joins: string list
}

/// Why a view was refused at registration. Nothing was registered.
[<RequireQualifiedAccess>]
type ViewRefusal =
    /// The dataset models the view would be checked against were refused.
    | Model of DatasetModelRefusal
    /// The pipeline is not `Fuaran.Compute` wire JSON.
    | Undecodable of view: string * reason: string
    /// The pipeline names a parameter the view does not declare, or the view
    /// declares one its pipeline never names.
    | ParameterMismatch of view: string * undeclared: string list * unused: string list
    /// `Fuaran.DB`'s pipeline check refused the pipeline against the model.
    | ModelCheck of view: string * code: string * reason: string
    /// The walk could not name the result's columns.
    | OpenResult of view: string * reason: string
    /// The result's columns are not the ones the view declares.
    | ColumnsDisagree of view: string * declared: string list * walked: string list

/// Companion helpers for `ViewRefusal`.
[<RequireQualifiedAccess>]
module ViewRefusal =
    /// One sentence naming what to repair.
    let describe (refusal: ViewRefusal) : string =
        let names (xs: string list) =
            if xs.IsEmpty then
                "none"
            else
                xs |> List.map (sprintf "'%s'") |> String.concat ", "

        match refusal with
        | ViewRefusal.Model m -> DatasetModelRefusal.describe m
        | ViewRefusal.Undecodable(v, reason) -> sprintf "view '%s': its pipeline does not decode: %s" v reason
        | ViewRefusal.ParameterMismatch(v, undeclared, unused) ->
            sprintf
                "view '%s': its pipeline names parameters it does not declare (%s) or declares parameters its pipeline never names (%s)"
                v
                (names undeclared)
                (names unused)
        | ViewRefusal.ModelCheck(v, code, reason) ->
            sprintf "view '%s' is refused by its dataset model (%s): %s" v code reason
        | ViewRefusal.OpenResult(v, reason) ->
            sprintf "view '%s': the result's columns cannot be named before it runs: %s" v reason
        | ViewRefusal.ColumnsDisagree(v, declared, walked) ->
            sprintf "view '%s' declares the columns %s and its pipeline answers %s" v (names declared) (names walked)

/// The result of a view: the evaluation, and the identity it was computed
/// under.
type ViewResult = {
    /// The view's id.
    View: string
    /// The view's version.
    Version: string
    /// The pipeline's canonical hash.
    PipelineHash: string
    /// The hash of the model the view was checked against.
    ModelHash: string
    /// The evaluation, as `evaluate_pipeline` answers one.
    Result: PipelineToolResult
}

/// Registering, evaluating and binding views.
[<RequireQualifiedAccess>]
module ViewRegistry =

    /// The fact tables a pipeline joins, in step order.
    let joinedTables (pipeline: Transform list) : string list =
        pipeline
        |> List.choose (function
            | Join(Ref table, _, _) -> Some table
            | _ -> None)
        |> List.distinct

    let private refusalCode (r: Fuaran.DB.PipelineRefusal) : string =
        match r with
        | Fuaran.DB.PipelineRefusal.UnknownColumn _ -> "UnknownColumn"
        | Fuaran.DB.PipelineRefusal.UnknownTable _ -> "UnknownTable"
        | Fuaran.DB.PipelineRefusal.UnrelatedJoin _ -> "UnrelatedJoin"
        | Fuaran.DB.PipelineRefusal.OpenWalk _ -> "OpenWalk"
        | Fuaran.DB.PipelineRefusal.EmbeddedOperand _ -> "EmbeddedOperand"
        | Fuaran.DB.PipelineRefusal.UnboundSortKey _ -> "UnboundSortKey"
        | Fuaran.DB.PipelineRefusal.UnitMismatch _ -> "UnitMismatch"

    /// Check one view against the model it is declared over.
    let checkView (model: RegisteredDatasetModel) (view: FactViewDefinition) : Result<RegisteredView, ViewRefusal> =
        match PipelineTool.decode view.Pipeline with
        | Error refusal -> Error(ViewRefusal.Undecodable(view.Id, PipelineTool.refusalText refusal))
        | Ok pipeline ->
            let named = Transform.paramsOf pipeline
            let declared = view.Parameters |> List.map _.Name
            let undeclared = named |> List.filter (fun p -> not (List.contains p declared))
            let unused = declared |> List.filter (fun p -> not (List.contains p named))

            if not undeclared.IsEmpty || not unused.IsEmpty then
                Error(ViewRefusal.ParameterMismatch(view.Id, undeclared, unused))
            else
                match PipelineCheck.check model.Model (Ref view.Table) pipeline with
                | Error r -> Error(ViewRefusal.ModelCheck(view.Id, refusalCode r, PipelineCheck.describe r))
                | Ok(SchemaKnowledge.AtLeast(_, reason)) -> Error(ViewRefusal.OpenResult(view.Id, reason))
                | Ok(SchemaKnowledge.Closed columns) ->
                    let walked = columns |> List.map _.Name
                    let wanted = view.Columns |> List.map _.Name

                    if walked <> wanted then
                        Error(ViewRefusal.ColumnsDisagree(view.Id, wanted, walked))
                    else
                        Ok {
                            Module = model.Module
                            Definition = view
                            Pipeline = pipeline
                            PipelineHash = IncrementalCodec.pipelineHash pipeline
                            ModelHash = model.Hash
                            Joins = joinedTables pipeline
                        }

    /// Check every view the composition declares against its module's model
    /// (`DatasetModelRegistry.check` first). `Ok` holds every view; `Error`
    /// every refusal found.
    let check
        (models: DatasetModelRegistry)
        (views: FactViewRegistration list)
        : Result<RegisteredView list, ViewRefusal list> =
        let checkedViews =
            views
            |> List.map (fun r ->
                match models.Models |> List.tryFind (fun m -> m.Module = r.Module) with
                | Some model -> checkView model r.View
                | None ->
                    Error(
                        ViewRefusal.ModelCheck(
                            r.View.Id,
                            FactTablePreflight.ModelTableUndeclaredRule,
                            sprintf "module '%s' declares no dataset model" r.Module
                        )
                    ))

        match
            checkedViews
            |> List.collect (function
                | Error e -> [ e ]
                | Ok _ -> [])
        with
        | [] -> Ok(checkedViews |> List.choose Result.toOption)
        | refusals -> Error refusals

    /// The models and the views of a composed app, checked together: the
    /// dataset models (`DatasetModelRegistry.register`), then every view.
    let register (app: ServerApp) : Result<DatasetModelRegistry * RegisteredView list, ViewRefusal list> =
        match DatasetModelRegistry.register app with
        | Error refusals -> Error(refusals |> List.map ViewRefusal.Model)
        | Ok models -> check models app.RegisteredViews |> Result.map (fun views -> models, views)

    /// `register`, refusing the composition, naming every refusal, when one
    /// is found: the startup failure a view outside its model is.
    let registerOrRefuse (app: ServerApp) : DatasetModelRegistry * RegisteredView list =
        match register app with
        | Ok registered -> registered
        | Error refusals ->
            ServiceComposition.refuse (
                CompositionRefusal.InvalidDeclaration(
                    "ViewRegistry.register",
                    refusals |> List.map ViewRefusal.describe |> String.concat "; "
                )
            )

    /// A typed argument as the cell its parameter's type takes, or why not.
    /// An integer is admitted where a decimal is declared.
    let argument (parameter: FactViewParameter) (value: FactViewArgument) : Result<Cell, string> =
        match parameter.Type, value with
        | "string", FactViewArgument.Text s -> Ok(Str s)
        | "integer", FactViewArgument.Integer n -> Ok(Int n)
        | "decimal", FactViewArgument.Decimal d -> Ok(Decimal(d.ToString(Globalization.CultureInfo.InvariantCulture)))
        | "decimal", FactViewArgument.Integer n -> Ok(Decimal(n.ToString(Globalization.CultureInfo.InvariantCulture)))
        | "boolean", FactViewArgument.Boolean b -> Ok(Bool b)
        | _ -> Error(sprintf "parameter '%s' takes a %s" parameter.Name parameter.Type)

    /// A call's argument as the cell its parameter's type takes, or why not.
    let argumentCell (parameter: FactViewParameter) (value: JsonElement) : Result<Cell, string> =
        let typed =
            match value.ValueKind with
            | JsonValueKind.String -> Some(FactViewArgument.Text(value.GetString()))
            | JsonValueKind.Number ->
                match value.TryGetInt32() with
                | true, n -> Some(FactViewArgument.Integer n)
                | _ ->
                    match value.TryGetDecimal() with
                    | true, d -> Some(FactViewArgument.Decimal d)
                    | _ -> None
            | JsonValueKind.True -> Some(FactViewArgument.Boolean true)
            | JsonValueKind.False -> Some(FactViewArgument.Boolean false)
            | _ -> None

        match typed with
        | Some a -> argument parameter a
        | None -> Error(sprintf "parameter '%s' takes a %s" parameter.Name parameter.Type)

    /// Evaluate `view` with `arguments` bound, over the gated slice of its
    /// table and the gated slices of the tables it joins (`joined`, by table
    /// id), as `evaluate_pipeline` evaluates a pipeline: the lineage, the
    /// withheld rule, the cap and the record are that tool's. Pure.
    let evaluate
        (view: RegisteredView)
        (arguments: Map<string, Cell>)
        (request: PipelineToolRequest)
        (slice: FactSlice)
        (joined: (string * FactSlice) list)
        (readAt: DateTime)
        : Result<ViewResult, ToolUp.Facts.PipelineRefusal> =
        let bound = Transform.substitute arguments view.Pipeline

        PipelineTool.evaluateJoined request bound slice joined readAt
        |> Result.map (fun result -> {
            View = view.Definition.Id
            Version = view.Definition.Version
            PipelineHash = view.PipelineHash
            ModelHash = view.ModelHash
            Result = result
        })

    // ── Each view a method card ───────────────────────────────────

    /// The precondition a call meets when it reads the view's own table.
    [<Literal>]
    let ViewTableCondition = "ViewTable"

    /// The refusal a call's argument earns when it is not of its type.
    [<Literal>]
    let ArgumentCondition = "ViewArgument"

    /// The refusal an evaluation earns when the slice read or the evaluation
    /// refuses.
    [<Literal>]
    let EvaluationCondition = "ViewEvaluation"

    let private schemaOf (parameter: FactViewParameter) : string =
        let ty =
            match parameter.Type with
            | "integer" -> "integer"
            | "decimal" -> "number"
            | "boolean" -> "boolean"
            | _ -> "string"

        sprintf "{\"type\":\"%s\",\"description\":%s}" ty (JsonSerializer.Serialize parameter.Description)

    /// The method card a view becomes: its id and version the card's
    /// identity, its description the meaning, its parameters the signature
    /// and its columns the outputs.
    let card (view: RegisteredView) : MethodCard =
        let v = view.Definition

        {
            Identity = {
                Id = "view." + v.Id
                Version = v.Version
                DefinitionHash = view.PipelineHash
                Package = "ToolUp.Facts.Server"
                Determinism = MethodDeterminism.Pinned
            }
            Meaning = {
                Summary = v.Description
                Answers = v.Description
                Assumes = [ sprintf "The slice reads fact table '%s', the view's own table." v.Table ]
            }
            Applicability = {
                Preconditions = [
                    {
                        Name = ViewTableCondition
                        Statement = sprintf "The call's table is '%s'." v.Table
                        Remedy = sprintf "Pass table '%s'." v.Table
                    }
                ]
                DoesNotApply = []
                Alternatives = []
                Choice = MethodChoice.Mechanical
            }
            Signature = {
                ToolName = "view_" + v.Id
                Parameters =
                    v.Parameters
                    |> List.map (fun p -> {
                        Name = p.Name
                        Meaning = p.Description
                        Schema = schemaOf p
                        Required = p.Required
                        Default = None
                        Range = None
                        Role = None
                    })
                Refusals = [
                    {
                        Name = ArgumentCondition
                        Statement = "Every argument is of its parameter's type."
                        Remedy = "Write each argument as its parameter's type."
                    }
                    {
                        Name = EvaluationCondition
                        Statement = "The view's tables can be read and the view evaluated over them."
                        Remedy =
                            "Narrow the period range or the subject prefix, or read the refusal the slice read gives."
                    }
                ]
            }
            Outputs = {
                Tables = [
                    {
                        Name = v.Id
                        Meaning = v.Description
                        Columns =
                            v.Columns
                            |> List.map (fun c -> {
                                Name = c.Name
                                Meaning = c.Description
                            })
                    }
                ]
                Diagnostics = []
                Warnings = []
                Facts = []
            }
            Interpretation = {
                Rules = [ "A withheld column's cells are empty: its lineage reads a withheld cell." ]
                Say = [ "Present the result; name the view and its version." ]
                NeverSay = [ "Never retype a figure from the result into prose." ]
            }
            Presentation = {
                Blocks = [ "table" ]
                BaseCard = None
            }
            Examples = []
        }

    /// The text the model reads for a view's result: the view's identity and
    /// the evaluation's shape, never a value.
    let summary (result: ViewResult) : string =
        sprintf
            "View '%s' version %s (pipeline %s, model %s). %s"
            result.View
            result.Version
            result.PipelineHash
            result.ModelHash
            (PipelineTool.summary result.Result)

    /// The evaluator a view's card is bound to.
    let evaluator (view: RegisteredView) : MethodEvaluator = {
        DefinitionHash = view.PipelineHash
        Checks = Map.ofList [ ViewTableCondition, fun call -> call.Slice.TableId = view.Definition.Table ]
        Evaluate =
            fun read call -> async {
                let arguments =
                    view.Definition.Parameters
                    |> List.choose (fun p -> call.Arguments.TryFind p.Name |> Option.map (fun v -> p, v))
                    |> List.map (fun (p, v) -> argumentCell p v |> Result.map (fun c -> p.Name, c))

                if arguments |> List.exists Result.isError then
                    return Error ArgumentCondition
                else
                    let rec readJoined acc tables = async {
                        match tables with
                        | [] -> return Ok(List.rev acc)
                        | table :: rest ->
                            match! read.Read table [] with
                            | Error _ -> return Error EvaluationCondition
                            | Ok slice -> return! readJoined ((table, slice) :: acc) rest
                    }

                    match! readJoined [] view.Joins with
                    | Error name -> return Error name
                    | Ok joined ->
                        let request = {
                            TableId = call.Slice.TableId
                            PeriodFrom = call.Slice.PeriodFrom
                            PeriodTo = call.Slice.PeriodTo
                            PathPrefix = call.Slice.SubjectPrefix
                            Metrics = call.Slice.Metrics
                            Pipeline = view.Definition.Pipeline
                        }

                        let bound = arguments |> List.choose Result.toOption |> Map.ofList

                        match evaluate view bound request read.Slice joined read.ReadAt with
                        | Error _ -> return Error EvaluationCondition
                        | Ok result ->
                            return
                                Ok {
                                    Result = {
                                        Table = result.Result.Table
                                        Rows = result.Result.Rows
                                        Columns = result.Result.Columns
                                        Withheld = result.Result.Withheld
                                        Facts = []
                                    }
                                    Summary = summary result
                                }
            }
    }

    /// One method binding per view, for the deployment's one
    /// `MethodRegistry.register` call beside its other cards.
    let bindings (views: RegisteredView list) : MethodBinding list =
        views |> List.map (fun v -> MethodTool.bind (card v) (evaluator v))

    // ── A series over a view ──────────────────────────────────────

    /// The result columns a view answers for a series to draw one of its
    /// columns: each row's subject, its path and its period.
    let seriesColumns: string list = [
        FactBrowseSlice.SubjectColumn
        FactBrowseSlice.PathColumn
        FactBrowseSlice.FromColumn
        FactBrowseSlice.ToColumn
    ]

    let private quoted (xs: string list) =
        if xs.IsEmpty then
            "none"
        else
            xs |> List.map (sprintf "'%s'") |> String.concat ", "

    /// The arguments a series names, bound to the view's parameters: a name
    /// the view does not declare, a required parameter left unnamed or an
    /// argument of the wrong type is refused by name.
    let seriesArguments
        (view: RegisteredView)
        (given: Map<string, FactViewArgument>)
        : Result<Map<string, Cell>, string> =
        let v = view.Definition
        let declared = v.Parameters |> List.map _.Name

        match
            given
            |> Map.toList
            |> List.map fst
            |> List.filter (fun n -> not (List.contains n declared))
        with
        | unknown :: _ ->
            Error(sprintf "View '%s' declares no parameter '%s'; it declares %s." v.Id unknown (quoted declared))
        | [] ->
            match
                v.Parameters
                |> List.tryFind (fun p -> p.Required && not (given.ContainsKey p.Name))
            with
            | Some p -> Error(sprintf "View '%s' needs the argument '%s'." v.Id p.Name)
            | None ->
                v.Parameters
                |> List.choose (fun p -> given.TryFind p.Name |> Option.map (fun a -> p, a))
                |> List.fold
                    (fun bound (p, a) ->
                        bound
                        |> Result.bind (fun m ->
                            argument p a
                            |> Result.map (fun c -> Map.add p.Name c m)
                            |> Result.mapError (sprintf "View '%s': %s." v.Id)))
                    (Ok Map.empty)

    /// Why a view cannot be drawn as `request` asks, before anything is read.
    let private seriesShape (view: RegisteredView) (request: FactSeriesQuery) : Result<unit, string> =
        let v = view.Definition
        let columns = v.Columns |> List.map _.Name

        if request.TableId <> v.Table then
            Error(sprintf "View '%s' reads table '%s'; the series names table '%s'." v.Id v.Table request.TableId)
        elif request.SubjectLevel.IsSome then
            Error(sprintf "View '%s' decides its own rows, so a series over it takes no subject level." v.Id)
        elif request.PeriodFrom >= request.PeriodTo then
            Error(FactBrowseApi.periodRangeRefusal request.PeriodFrom request.PeriodTo)
        elif not (List.contains request.Metric columns) then
            Error(sprintf "View '%s' answers no column '%s'; it answers %s." v.Id request.Metric (quoted columns))
        else
            match seriesColumns |> List.filter (fun c -> not (List.contains c columns)) with
            | [] -> Ok()
            | missing ->
                Error(
                    sprintf
                        "View '%s' cannot be drawn as a series: it answers no %s column (a series needs %s)."
                        v.Id
                        (quoted missing)
                        (quoted seriesColumns)
                )

    /// The series drawn from one view evaluation: one point per result row,
    /// ordered by subject path then period. The drawn column withheld marks
    /// every point withheld, with no value and no rendering; a null cell is a
    /// point with no value. Pure.
    let seriesOf (request: FactSeriesQuery) (displayFormat: string) (result: ViewResult) : Result<FactSeries, string> =
        let cap = FactBrowseApi.clampSeriesPoints request.MaxPoints

        match ColumnCodec.decode result.Result.Table with
        | Error e -> Error("The view's result could not be decoded: " + ColumnCodec.errorString e)
        | Ok(Ref name) -> Error(sprintf "The view's result names a source '%s' rather than carrying its rows." name)
        | Ok(Embedded table) ->
            let cells =
                table.Columns
                |> List.map (fun c -> c.Name, Array.ofList (Column.toCells c))
                |> Map.ofList

            let rows = Table.rowCount table

            if rows > cap then
                Error(FactBrowseApi.seriesCapRefusal rows cap)
            else
                let at (column: string) (i: int) : Cell =
                    match cells.TryFind column with
                    | Some values -> values.[i]
                    | None -> Null

                let text (cell: Cell) =
                    match cell with
                    | Str s -> Some s
                    | _ -> None

                let instant (cell: Cell) =
                    match cell with
                    | Timestamp t -> FactBrowseSlice.parseTimestamp t
                    | _ -> DateTime.MinValue

                let withheldColumn =
                    result.Result.Withheld |> List.tryFind (fun c -> c.Column = request.Metric)

                let withheld = withheldColumn.IsSome

                let points =
                    List.init rows (fun i ->
                        let value =
                            if withheld then
                                None
                            else
                                match at request.Metric i with
                                | Decimal d -> Some(FactBrowseSlice.parseDecimal d)
                                | Int n -> Some(decimal n)
                                | Float f -> Some(decimal f)
                                | _ -> None

                        {
                            Subject = text (at FactBrowseSlice.SubjectColumn i) |> Option.defaultValue ""
                            SubjectPath =
                                match text (at FactBrowseSlice.PathColumn i) with
                                | None
                                | Some "" -> []
                                | Some path -> path.Split '>' |> List.ofArray
                            PeriodFrom = instant (at FactBrowseSlice.FromColumn i)
                            PeriodTo = instant (at FactBrowseSlice.ToColumn i)
                            PeriodLabel = text (at FactBrowseSlice.LabelColumn i)
                            Value = value
                            Rendering = value |> Option.map (FactBrowseSlice.renderDerived displayFormat)
                            FactId = ""
                            AsOf = result.Result.ReadAt
                            Freshness = ""
                            Withheld = withheld
                        })
                    |> List.sortBy (fun p -> p.SubjectPath, p.PeriodFrom, p.PeriodTo)

                Ok {
                    TableId = request.TableId
                    Metric = request.Metric
                    SubjectPrefix = request.SubjectPrefix
                    SubjectLevel = ""
                    PeriodFrom = request.PeriodFrom
                    PeriodTo = request.PeriodTo
                    DisplayFormat = displayFormat
                    Points = points
                    MaxPoints = cap
                    WithheldCount = if withheld then rows else 0
                    Withheld =
                        withheldColumn
                        |> Option.map (fun c ->
                            c.Withheld
                            |> List.map (fun w -> {
                                FactWithheldCount.PolicyRef = w.PolicyRef
                                Count = w.Count
                            }))
                        |> Option.defaultValue []
                    View =
                        Some {
                            ViewId = result.View
                            Version = result.Version
                            PipelineHash = result.PipelineHash
                            ModelHash = result.ModelHash
                        }
                }

    /// The series a view-sourced request asks for, over `views`: the view
    /// found by id, its arguments bound, its own table and every table it
    /// joins read through `readSlice` over the request's period range and
    /// subject prefix, evaluated as the view's own tool evaluates it
    /// (`evaluate`), and drawn by `seriesOf`. Refusals name what to repair.
    let series
        (views: RegisteredView list)
        (readSlice: FactSliceQuery -> Async<Result<FactSlice, string>>)
        (readAt: DateTime)
        (request: FactSeriesQuery)
        (source: FactSeriesViewSource)
        : Async<Result<FactSeries, string>> =
        async {
            match views |> List.tryFind (fun v -> v.Definition.Id = source.ViewId) with
            | None ->
                return
                    Error(
                        sprintf
                            "No view '%s' is registered; the registered views are %s."
                            source.ViewId
                            (quoted (views |> List.map _.Definition.Id))
                    )
            | Some view ->
                match
                    seriesShape view request
                    |> Result.bind (fun () -> seriesArguments view source.Arguments)
                with
                | Error reason -> return Error reason
                | Ok arguments ->
                    let sliceOf (table: string) : FactSliceQuery = {
                        TableId = table
                        PeriodFrom = request.PeriodFrom
                        PeriodTo = request.PeriodTo
                        SubjectPrefix = request.SubjectPrefix
                        Metrics = []
                    }

                    let rec readAll acc tables = async {
                        match tables with
                        | [] -> return Ok(List.rev acc)
                        | table :: rest ->
                            match! readSlice (sliceOf table) with
                            | Error reason -> return Error reason
                            | Ok slice -> return! readAll ((table, slice) :: acc) rest
                    }

                    match! readAll [] (view.Definition.Table :: view.Joins) with
                    | Error reason -> return Error reason
                    | Ok [] -> return Error(sprintf "View '%s': its table was not read." view.Definition.Id)
                    | Ok((_, slice) :: joined) ->
                        let pipelineRequest = {
                            TableId = view.Definition.Table
                            PeriodFrom = request.PeriodFrom
                            PeriodTo = request.PeriodTo
                            PathPrefix = request.SubjectPrefix
                            Metrics = []
                            Pipeline = view.Definition.Pipeline
                        }

                        let format =
                            slice.Metrics
                            |> List.tryFind (fun m -> m.Metric = request.Metric)
                            |> Option.map _.DisplayFormat
                            |> Option.defaultValue ""

                        match evaluate view arguments pipelineRequest slice joined readAt with
                        | Error refusal -> return Error(PipelineRefusal.describe refusal)
                        | Ok result -> return seriesOf request format result
        }

    /// The reader the fact browse resolves view-sourced series through, over
    /// the registered views.
    let seriesReader (views: RegisteredView list) : IFactViewSeriesReader =
        { new IFactViewSeriesReader with
            member _.ReadSeries(readSlice, readAt, request, source) =
                series views readSlice readAt request source
        }

    /// **Adds** the series reader over `views` (`registerOrRefuse`'s views), so
    /// `QuerySeries` reads a registered view for every caller, and a page's
    /// chart and an answer draw one number through one evaluation and one
    /// gate. With no fact store composed, or no view, it changes nothing; a
    /// second reader is refused.
    let withSeries (views: RegisteredView list) (app: ServerApp) : ServerApp =
        match app.Config.Stores.FactStore with
        | FeatureMode.Disabled -> app
        | FeatureMode.Enabled when views.IsEmpty -> app
        | FeatureMode.Enabled ->
            ServerApp.addContribution
                (Contribution.service (ComponentId.forCompanionSlot "ToolUp.Facts") (fun s ->
                    if s |> Seq.exists (fun d -> d.ServiceType = typeof<IFactViewSeriesReader>) then
                        ServiceComposition.refuse (
                            CompositionRefusal.InvalidDeclaration(
                                "ViewRegistry.withSeries",
                                "a view series reader is already registered; register every view in one ViewRegistry.withSeries call"
                            )
                        )
                    else
                        s.AddSingleton<IFactViewSeriesReader>(seriesReader views)))
                app
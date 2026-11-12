// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DatasetModelRegistryTests

// ─── A dataset model over a module's fact tables, and views over it ───
//
// What this pack pins:
//
//   1. **The declarations.** A modelled table the module does not declare, a
//      modelled table with no description, a measure publishing no registered
//      metric and a relation naming an unmodelled table are each refused by
//      name, at preflight and by the registry alike; a well-formed model lowers
//      to a Fuaran.DB model whose columns carry the tables' own descriptions.
//   2. **The views.** A view reading a column the model does not name, or
//      joining along a relation it does not declare, is refused by name; a
//      view is one method tool, listed in the method profile, whose answer
//      names the view, its version, the pipeline hash and the model hash.
//   3. **The gate stays the authority.** A view the model admits over a table
//      the gate withholds answers withheld cells, not values.
//   4. **Joins through the model.** The relation check admits a join along a
//      declared relation, refuses an undeclared one by name before anything is
//      read, and a withheld cell withholds the joined column.
//   5. **What the assistant reads.** `describe_dataset_model` answers from the
//      caller's projection: a table the gate withholds whole is not named, and
//      every table carries its own description, modelled or not.

open System
open Expecto
open Fuaran.Core
open Fuaran.Compute
open ToolUp.Platform
open ToolUp.Platform.Compose
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Scope.StorageScopeResolver
open ToolUp.Facts
open ToolUp.Platform.Tests.InProcess.FactRenderToolTests.RenderFixtures

// ─── Fixtures ───────────────────────────────────────────────────────

let private openMargins: FactTableDefinition = {
    margins with
        Id = "daily-margin-open"
        Columns = [ FactTableDefinition.column "margin" FactTableValueShape.Scalar ]
}

let private metric (id: string) : MetricDefinition = {
    Id = id
    Name = id
    Unit = "GBP"
    Dimensionality = "currency"
    Direction = HigherIsBetter
    DisplayFormat = ""
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = None
    Context = None
}

let private describe (table: FactTableDefinition) (column: string) : FactTableDescriptionRegistration = {
    Module = "trade"
    Description = {
        TableId = table.Id
        Description = sprintf "What %s holds." table.Id
        Columns = [ column, sprintf "The %s of a site on a day." column ]
    }
}

let private keys = [
    FactBrowseSlice.SubjectColumn, FactBrowseSlice.SubjectColumn
    FactBrowseSlice.FromColumn, FactBrowseSlice.FromColumn
]

let private model: DatasetModelDeclaration = {
    Tables = [ trade.Id; openMargins.Id; margins.Id ]
    Relations = [
        {
            Name = "trade-margin"
            Left = trade.Id
            Right = openMargins.Id
            On = keys
        }
        {
            Name = "trade-restricted-margin"
            Left = trade.Id
            Right = margins.Id
            On = keys
        }
    ]
    Measures = [
        {
            Name = "total revenue"
            Table = trade.Id
            Column = "revenue"
            Aggregate = "sum"
            Publishes = "total-revenue"
        }
    ]
}

/// The joined view: subject, day, revenue, margin and revenue less margin,
/// for revenue above `min`.
let private joinedPipeline (table: string) : Transform list = [
    Join(Ref table, keys, Left)
    Filter(Binary(Gt, Col "revenue", Param "min"))
    Project [
        FactBrowseSlice.SubjectColumn, FactBrowseSlice.SubjectColumn
        FactBrowseSlice.FromColumn, FactBrowseSlice.FromColumn
        "revenue", "revenue"
        "margin", "margin"
    ]
    Derive("net", Binary(Sub, Col "revenue", Col "margin"))
]

let private column (name: string) : FactViewColumn = {
    Name = name
    Description = sprintf "The view's %s." name
}

let private view (id: string) (table: string) (pipeline: Transform list) : FactViewDefinition = {
    Id = id
    Version = "1.0.0"
    Description = "Revenue less margin per site and day."
    Table = table
    Parameters = [
        {
            Name = "min"
            Type = "decimal"
            Description = "The least revenue a row keeps."
            Required = true
        }
    ]
    Columns = [ "subject"; "period_from"; "revenue"; "margin"; "net" ] |> List.map column
    Pipeline = DataFrameCodec.encodePipeline pipeline
}

let private composition
    (model: DatasetModelDeclaration)
    (descriptions: FactTableDescriptionRegistration list)
    (views: FactViewDefinition list)
    : FactTableComposition =
    {
        Tables = [
            for t in [ trade; margins; openMargins ] do
                {
                    FactTableRegistration.Module = "trade"
                    Definition = t
                }
        ]
        Metrics = [
            for id in [ "revenue"; "margin"; "total-revenue" ] do
                {
                    MetricRegistration.Module = "trade"
                    Definition = metric id
                }
        ]
        Subjects = []
        Bindings = []
        Descriptions = descriptions
        DatasetModels = [ { Module = "trade"; Model = model } ]
        Views =
            views
            |> List.map (fun v -> {
                FactViewRegistration.Module = "trade"
                View = v
            })
    }

let private described = [
    describe trade "revenue"
    describe margins "margin"
    describe openMargins "margin"
]

let private registryOf (c: FactTableComposition) : DatasetModelRegistry =
    match DatasetModelRegistry.check c with
    | Ok r -> r
    | Error refusals ->
        failtestf "the model was refused: %s" (refusals |> List.map DatasetModelRefusal.describe |> String.concat "; ")

let private codesOf (c: FactTableComposition) : string list =
    match DatasetModelRegistry.check c with
    | Ok _ -> failtest "the model registered, and should have been refused"
    | Error refusals -> refusals |> List.map DatasetModelRefusal.code

let private viewRefusals (c: FactTableComposition) : ViewRefusal list =
    match ViewRegistry.check (registryOf c) c.Views with
    | Ok _ -> failtest "the views registered, and should have been refused"
    | Error refusals -> refusals

let private viewsOf (c: FactTableComposition) : RegisteredView list =
    match ViewRegistry.check (registryOf c) c.Views with
    | Ok views -> views
    | Error refusals ->
        failtestf "the views were refused: %s" (refusals |> List.map ViewRefusal.describe |> String.concat "; ")

/// A world with the revenue table, the restricted margin table and the open
/// one, each written for three days and two sites.
let private joinWorld () : World =
    let storage =
        ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage()

    let events =
        ToolUp.Platform.Infra.InMemoryEventStore.InMemoryEventStore() :> IEventStore

    let ticks = ref 0

    let clock () =
        ticks.Value <- ticks.Value + 1
        day1.AddDays(10.0).AddSeconds(float ticks.Value)

    let tables =
        FactTableRegistry.build [
            for t in [ trade; margins; openMargins ] do
                {
                    FactTableRegistration.Module = "trade"
                    Definition = t
                }
        ] [ BindAllFactTables DefaultFactTableWriter.Destination ]

    let store =
        BlobFactStore.createWithRegistryAndClock storage events (Some registry) clock

    let writer =
        DefaultFactTableWriter.createWithClock store storage events tables (Some registry) clock

    let gate = OverlayGate(FactDisclosureGate(store, events))
    let scopeId = "team-" + Guid.NewGuid().ToString "N"

    let w = {
        Deps = {
            Tables = tables
            Writer = Some writer
            Store = store
            Gate = gate
            Registry = Some registry
            Clock = clock
        }
        Gate = gate
        Scope =
            ScopeResolution.ofStorageScope {
                ScopeId = scopeId
                Container = "container-" + scopeId
                Persist = true
            }
    }

    seed writer w.Scope trade "revenue"
    seed writer w.Scope margins "margin"
    seed writer w.Scope openMargins "margin"
    w

let private sliceArgs (table: string) (extra: string) : string =
    sprintf
        """{"table":"%s","period_from":"%s","period_to":"%s"%s}"""
        table
        (day1.ToString "yyyy-MM-dd")
        ((day1.AddDays 3.0).ToString "yyyy-MM-dd")
        extra

let private factIdOf (w: World) (table: string) (metric: string) (site: string) (offset: int) : string =
    match
        FactBrowseHandler.querySeries w.Deps w.Scope "u" {
            TableId = table
            Metric = metric
            SubjectPrefix = []
            SubjectLevel = None
            PeriodFrom = day1
            PeriodTo = day1.AddDays 3.0
            MaxPoints = FactBrowseApi.MaxSeriesPoints
        }
        |> Async.RunSynchronously
    with
    | Ok s ->
        s.Points
        |> List.find (fun p -> List.last p.SubjectPath = site && p.PeriodFrom = day1.AddDays(float offset))
        |> _.FactId
    | Error e -> failtestf "series: %A" e

let private permitting (policy: string) : DisclosurePolicyResolver =
    fun p _ -> if p = policy then Some true else None

let private tests1 =
    testList "Dataset model declarations" [

        test "a well-formed model lowers to Fuaran.DB, its columns described by the tables' own descriptions" {
            let registry = registryOf (composition model described [])
            let m = List.exactlyOne registry.Models

            Expect.stringStarts m.Hash "sha256:" "the model hash"
            Expect.equal (m.Hash.Length) 71 "64 hex digits"

            let revenue =
                Fuaran.DB.Model.tryColumn "daily-trade.revenue" m.Model
                |> Option.map (fun (_, c) -> c.Field.Description)

            Expect.equal revenue (Some(Some "The revenue of a site on a day.")) "the description is the table's own"

            Expect.equal
                (registry.TryModelOf trade.Id |> Option.map _.Module)
                (Some "trade")
                "the trade table is modelled"
        }

        test "a modelled table the module does not declare is refused by name" {
            let c =
                composition
                    {
                        model with
                            Tables = model.Tables @ [ "daily-cost" ]
                    }
                    described
                    []

            Expect.contains (codesOf c) FactTablePreflight.ModelTableUndeclaredRule "the rule"
        }

        test "a modelled table with no description is refused by name" {
            let c =
                composition model [ describe trade "revenue"; describe openMargins "margin" ] []

            Expect.equal (codesOf c) [ FactTablePreflight.ModelTableUndescribedRule ] "the one rule"
        }

        test "a measure naming no registered metric is refused by name" {
            let c =
                composition
                    {
                        model with
                            Measures = [
                                {
                                    model.Measures.Head with
                                        Publishes = "net-revenue"
                                }
                            ]
                    }
                    described
                    []

            Expect.equal (codesOf c) [ FactTablePreflight.MeasureMetricUndeclaredRule ] "the one rule"
        }

        test "a relation naming a table the model does not model is refused by name" {
            let c =
                composition
                    {
                        model with
                            Relations = [
                                {
                                    model.Relations.Head with
                                        Right = "daily-cost"
                                }
                            ]
                    }
                    described
                    []

            Expect.equal (codesOf c) [ FactTablePreflight.ModelTableUndeclaredRule ] "the one rule"
        }

        test "the composition preflight reports the same refusals as errors" {
            let c = composition model [ describe trade "revenue" ] []
            let defects = FactTablePreflight.defects c

            Expect.isTrue
                (defects
                 |> List.exists (fun d -> d.RuleCode = FactTablePreflight.ModelTableUndescribedRule))
                "reported at preflight"

            match FactTablePreflight.toValidationResult defects with
            | ToolUp.Platform.ConfigValidation.ValidationResult.Error message ->
                Expect.stringContains message "daily-margin" "names the table"
            | other -> failtestf "expected the preflight to refuse, got %A" other
        }
    ]

let private tests2 =
    testList "Views over a dataset model" [

        test "a view reading a column the model does not name is refused by name" {
            let pipeline = [ Project [ "cost", "cost" ] ]

            let c =
                composition model described [
                    {
                        view "costs" trade.Id pipeline with
                            Parameters = []
                            Columns = [ column "cost" ]
                    }
                ]

            match viewRefusals c with
            | [ ViewRefusal.ModelCheck("costs", "UnknownColumn", reason) ] ->
                Expect.stringContains reason "cost" "names the column"
            | other -> failtestf "expected the unknown-column refusal, got %A" other
        }

        test "a view joining along a relation the model does not declare is refused by name" {
            let pipeline = [
                Join(Ref openMargins.Id, [ FactBrowseSlice.PathColumn, FactBrowseSlice.PathColumn ], Left)
                Project [ "revenue", "revenue" ]
            ]

            let c =
                composition model described [
                    {
                        view "undeclared" trade.Id pipeline with
                            Parameters = []
                            Columns = [ column "revenue" ]
                    }
                ]

            match viewRefusals c with
            | [ ViewRefusal.ModelCheck("undeclared", "UnrelatedJoin", reason) ] ->
                Expect.stringContains reason "it declares: subject = subject" "names the relations that are declared"
            | other -> failtestf "expected the unrelated-join refusal, got %A" other
        }

        test "a view whose declared columns are not its result's is refused" {
            let c =
                composition model described [
                    {
                        view "net" trade.Id (joinedPipeline openMargins.Id) with
                            Columns = [ column "net" ]
                    }
                ]

            match viewRefusals c with
            | [ ViewRefusal.ColumnsDisagree("net", [ "net" ], _) ] -> ()
            | other -> failtestf "expected the columns refusal, got %A" other
        }

        test "a view is one method tool, listed in the profile, and its answer carries its identity" {
            let w = joinWorld ()

            let c =
                composition model described [ view "net_by_site" trade.Id (joinedPipeline openMargins.Id) ]

            let views = viewsOf c
            let registered = List.exactlyOne views

            let profile =
                match MethodRegistry.register (ViewRegistry.bindings views) with
                | Ok p -> p
                | Error refusals ->
                    failtestf
                        "the view's card was refused: %s"
                        (refusals |> List.map MethodRegistrationRefusal.describe |> String.concat "; ")

            Expect.stringContains profile.Fragment "view_net_by_site" "listed in the profile"

            let tool =
                profile.Tools |> List.find (fun t -> t.Definition.Name = "view_net_by_site")

            let text =
                tool.Answer w.Deps w.Scope "u" (sliceArgs trade.Id ""","min":0""")
                |> Async.RunSynchronously

            Expect.stringContains text "net_by_site" "the view"
            Expect.stringContains text "1.0.0" "its version"
            Expect.stringContains text registered.PipelineHash "the pipeline hash"
            Expect.stringContains text registered.ModelHash "the model hash"
        }

        test "a view the model admits over a table the gate withholds answers withheld cells, not values" {
            let w = joinWorld ()

            let c =
                composition model described [ view "restricted_net" trade.Id (joinedPipeline margins.Id) ]

            let registered = List.exactlyOne (viewsOf c)

            let request = {
                TableId = trade.Id
                PeriodFrom = day1
                PeriodTo = day1.AddDays 3.0
                PathPrefix = []
                Metrics = []
                Pipeline = registered.Definition.Pipeline
            }

            let read table =
                FactBrowseHandler.querySlice w.Deps w.Scope "u" {
                    TableId = table
                    PeriodFrom = day1
                    PeriodTo = day1.AddDays 3.0
                    SubjectPrefix = []
                    Metrics = []
                }
                |> Async.RunSynchronously
                |> function
                    | Ok s -> s
                    | Error e -> failtestf "slice: %A" e

            match
                ViewRegistry.evaluate
                    registered
                    (Map.ofList [ "min", Decimal "0" ])
                    request
                    (read trade.Id)
                    [ margins.Id, read margins.Id ]
                    DateTime.UtcNow
            with
            | Error refusal -> failtestf "expected a result, got %s" (PipelineRefusal.describe refusal)
            | Ok result ->
                Expect.equal result.View "restricted_net" "the view's identity"

                Expect.equal
                    (result.Result.Withheld |> List.map _.Column |> Set.ofList)
                    (set [ "margin"; "net" ])
                    "the restricted margin and what reads it are withheld"
        }
    ]

let private tests3 =
    testList "Joins through the dataset model" [

        let profileOver (registry: DatasetModelRegistry) =
            PipelineProfile.defaultProfile
            |> PipelineProfile.withSteps [ "join" ]
            |> PipelineProfile.withRelationCheck (DatasetModelRegistry.relationCheck (permitting "none") registry)
            |> PipelineProfile.register
            |> function
                | Ok p -> p
                | Error e -> failtestf "profile: %A" e

        let answer (w: World) profile (pipeline: Transform list) =
            PipelineTool.answerUnder (Some profile) w.Deps w.Scope "u" (pipelineArgs trade.Id "revenue" 3 pipeline)
            |> Async.RunSynchronously

        let along = [
            Join(Ref openMargins.Id, keys, Left)
            Project [
                "subject", "subject"
                "period_from", "period_from"
                "revenue", "revenue"
                "margin", "margin"
            ]
        ]

        test "a join along a declared relation is admitted" {
            let w = joinWorld ()
            let profile = profileOver (registryOf (composition model described []))

            match answer w profile along with
            | Ok result -> Expect.equal result.Rows 6 "one row per subject and day"
            | Error refusal -> failtestf "expected a result, got %s" (PipelineRefusal.describe refusal)
        }

        test "a join along keys no relation declares is refused by name before anything is read" {
            let w = joinWorld ()
            let profile = profileOver (registryOf (composition model described []))

            let off = [
                Join(Ref openMargins.Id, [ FactBrowseSlice.PathColumn, FactBrowseSlice.PathColumn ], Left)
            ]

            match answer w profile off with
            | Error(PipelineRefusal.JoinRefused(1, reason)) ->
                Expect.stringContains reason "it declares: subject = subject" "names a relation"
            | other -> failtestf "expected the join refusal, got %A" other

            Expect.isEmpty w.Gate.Surfaces "nothing was read"
        }

        test "a withheld cell withholds the joined column" {
            let w = joinWorld ()
            let profile = profileOver (registryOf (composition model described []))
            w.Gate.Deny [ factIdOf w openMargins.Id "margin" "s2" 1 ]

            match answer w profile along with
            | Ok result -> Expect.equal (result.Withheld |> List.map _.Column) [ "margin" ] "the joined column"
            | Error refusal -> failtestf "expected a result, got %s" (PipelineRefusal.describe refusal)
        }
    ]

let private tests4 =
    testList "describe_dataset_model" [

        let tablesOf (text: string) =
            use doc = System.Text.Json.JsonDocument.Parse text

            [
                for m in doc.RootElement.GetProperty("models").EnumerateArray() do
                    for t in m.GetProperty("tables").EnumerateArray() do
                        t.GetProperty("name").GetString(),
                        (match t.TryGetProperty "description" with
                         | true, d -> Some(d.GetString())
                         | _ -> None)
            ]

        test "a table the gate withholds whole is not named; every table carries its own description" {
            let registry = registryOf (composition model described [])

            let text =
                DatasetModelTools.answer registry DisclosurePolicyResolver.denyUnknown """{"part":"tables"}"""

            let tables = tablesOf text

            Expect.equal (tables |> List.map fst) [ trade.Id; openMargins.Id ] "the restricted table is not named"

            Expect.equal
                (tables |> List.map snd)
                [ Some "What daily-trade holds."; Some "What daily-margin-open holds." ]
                "each table's own description"
        }

        test "a resolver permitting the policy shows the restricted table" {
            let registry = registryOf (composition model described [])

            let text =
                DatasetModelTools.answer registry (permitting "trade-figures") """{"part":"tables"}"""

            Expect.contains (tablesOf text |> List.map fst) margins.Id "shown"
        }

        test "an unmodelled table is described as before, beside a modelled one" {
            let partial = {
                model with
                    Tables = [ trade.Id; openMargins.Id ]
                    Relations = [ model.Relations.Head ]
            }

            let registry = registryOf (composition partial described [])

            Expect.equal
                (registry.TryDescription margins.Id |> Option.map _.Description)
                (Some "What daily-margin holds.")
                "the unmodelled table's description"

            Expect.equal
                (registry.TryDescription trade.Id |> Option.map _.Description)
                (Some "What daily-trade holds.")
                "the modelled table's description"

            Expect.isNone (registry.TryModelOf margins.Id) "not modelled"
        }

        test "measures name the metric they publish" {
            let registry = registryOf (composition model described [])

            let text =
                DatasetModelTools.answer registry DisclosurePolicyResolver.denyUnknown """{"part":"measures"}"""

            Expect.stringContains text "\"publishes\":\"total-revenue\"" "the published metric"
        }
    ]

let tests = testList "Dataset model and views" [ tests1; tests2; tests3; tests4 ]
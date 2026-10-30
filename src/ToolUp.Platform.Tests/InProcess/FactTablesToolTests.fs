// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FactTablesToolTests

// ─── `list_fact_tables` ──────────────────────────────────────────────
//
// The table listing the assistant reads before it plans a fact question:
//
//   1. Every declared table is listed, ordered by id, with its description,
//      the hierarchy's path from root to the table's level with each level's
//      description, its metric columns with the registry's declarations and
//      the column descriptions, and its grain, cadence and standing.
//   2. One table by id; an unknown id refused by name.
//   3. Disclosure: the sample is judged at `FactToolResult`; a table whose
//      sampled facts are all withheld is its id and policy only; a partly
//      withheld table is described, naming the policy. No value appears.
//   3b. A level with at most the declared cap of members lists their names;
//      above the cap, or off, none; a withheld member is counted, never named.
//   4. Composition: one knob with the other fact tools, and the narrative's
//      tool set is the same list.
//   5. A scripted turn asked for a metric at a level no table holds answers
//      that it is not held, with no query call.

open System
open System.Text.Json
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.FactRenderToolTests.RenderFixtures

/// The render tools' fixture with its tables, metrics and hierarchy
/// described.
module TableFixtures =

    let private metric (id: string) (context: string option) : MetricDefinition = {
        Id = id
        Name = sprintf "%s (net)" id
        Unit = "GBP"
        Dimensionality = "currency"
        Direction = HigherIsBetter
        DisplayFormat = "#,##0.00"
        Staleness = UntilSuperseded
        ProducingOperation = None
        CanonicalMethod = None
        RecomputePolicy = None
        RollUp = None
        Context = context
    }

    /// The revenue metric's declared context.
    [<Literal>]
    let RevenueContext = "Net invoiced sales: what the tills recorded, less refunds."

    /// The trade table's description.
    [<Literal>]
    let TradeDescription =
        "Trade per site per day, rolled up nightly from the till feed."

    /// The revenue column's description.
    [<Literal>]
    let RevenueColumn = "Net invoiced sales for the site on the day."

    /// The metric registry, with the sites hierarchy described (its levels
    /// listed out of declaration order on purpose).
    let registry: IMetricRegistry =
        MetricRegistry.buildDescribed
            [
                {
                    MetricRegistration.Module = "trade"
                    Definition = metric "revenue" (Some RevenueContext)
                }
                {
                    MetricRegistration.Module = "trade"
                    Definition = metric "margin" None
                }
            ] [
                {
                    SubjectRegistration.Module = "trade"
                    Definition = {
                        Id = "sites"
                        Name = "Sites"
                        Levels = [ "region"; "site" ]
                        Calendar = None
                    }
                }
            ] [
                {
                    SubjectDescriptionRegistration.Module = "trade"
                    Description = {
                        SubjectId = "sites"
                        Description = "Where a sale is booked: each site sits in one region."
                        Levels = [
                            "site", "A single trading outlet."
                            "region", "A group of neighbouring sites."
                        ]
                    }
                }
            ]

    /// The declared tables, both described.
    let tables: IFactTableRegistry =
        FactTableRegistry.buildDescribed
            [
                {
                    FactTableRegistration.Module = "trade"
                    Definition = trade
                }
                {
                    FactTableRegistration.Module = "trade"
                    Definition = margins
                }
            ] [ BindAllFactTables DefaultFactTableWriter.Destination ] [
                {
                    FactTableDescriptionRegistration.Module = "trade"
                    Description = {
                        TableId = trade.Id
                        Description = TradeDescription
                        Columns = [ "revenue", RevenueColumn ]
                    }
                }
                {
                    FactTableDescriptionRegistration.Module = "trade"
                    Description = {
                        TableId = margins.Id
                        Description = "Margin per site per day."
                        Columns = []
                    }
                }
            ]

    /// The render fixture's world, read through the described registries.
    let world () : World =
        let w = FactRenderToolTests.RenderFixtures.world ()

        {
            w with
                Deps = {
                    w.Deps with
                        Tables = tables
                        Registry = Some registry
                }
        }

    /// Every revenue figure the fixture seeds, as the store renders it and as
    /// a raw decimal: none may appear in a result.
    let figures: string list = [
        for day in 0..2 do
            for site in [ "s1"; "s2" ] do
                let v = revenueOn day site
                string v
                v.ToString("#,##0.00", Globalization.CultureInfo.InvariantCulture)
    ]

    /// The tool's raw answer in `w` for principal `u`.
    let answer (w: World) (args: string) : string =
        FactTablesTool.executeWith w.Deps w.Scope "u" args |> Async.RunSynchronously

    /// The tool's answer as JSON.
    let answerJson (w: World) (args: string) : JsonElement =
        (JsonDocument.Parse(answer w args)).RootElement.Clone()

    /// The projection in `w` for principal `u`.
    let project (w: World) (table: string option) : FactTableView list =
        match FactTablesTool.project w.Deps w.Scope "u" table |> Async.RunSynchronously with
        | Ok views -> views
        | Error e -> failtestf "expected a projection, got %s" e

    /// The table row for `id` in an answer.
    let tableRow (answer: JsonElement) (id: string) : JsonElement =
        answer.GetProperty("tables").EnumerateArray()
        |> Seq.tryFind (fun t -> t.GetProperty("id").GetString() = id)
        |> Option.defaultWith (fun () -> failtestf "no row for table '%s'" id)

    /// The property names of a JSON object, in order.
    let propertyNames (el: JsonElement) : string list =
        el.EnumerateObject() |> Seq.map _.Name |> List.ofSeq

open TableFixtures

let private shownOf (view: FactTableView) : FactTableShown =
    match view with
    | FactTableView.Shown s -> s
    | FactTableView.Withheld w -> failtestf "expected '%s' to be described, it was withheld: %s" w.TableId w.Reason

let private withheldOf (view: FactTableView) : FactTableWithheld =
    match view with
    | FactTableView.Withheld w -> w
    | FactTableView.Shown s -> failtestf "expected '%s' to be withheld, it was described" s.TableId

let private str (el: JsonElement) (name: string) = el.GetProperty(name).GetString()

// ─── 1. The listing ──────────────────────────────────────────────────

let private listingTests =
    testList "every declared table, with its descriptions" [
        test "every declared table is listed, ordered by id" {
            let w = world ()

            let ids =
                project w None
                |> List.map (function
                    | FactTableView.Shown s -> s.TableId
                    | FactTableView.Withheld v -> v.TableId)

            Expect.equal ids [ "daily-margin"; "daily-trade" ] "both declared tables, by id"
        }

        test "a described table carries its description, its level path and its columns' declarations" {
            let w = world ()

            let s =
                project w None
                |> List.find (function
                    | FactTableView.Shown s -> s.TableId = trade.Id
                    | _ -> false)
                |> shownOf

            Expect.equal s.Description (Some TradeDescription) "the table's description"
            Expect.equal s.Hierarchy "sites" "its hierarchy"
            Expect.equal s.HierarchyName (Some "Sites") "the hierarchy's name"

            Expect.equal
                s.HierarchyDescription
                (Some "Where a sale is booked: each site sits in one region.")
                "the hierarchy's description"

            Expect.equal s.Level "site" "its level"

            Expect.equal
                s.Path
                [
                    {
                        Level = "region"
                        Description = Some "A group of neighbouring sites."
                        Members = None
                    }
                    {
                        Level = "site"
                        Description = Some "A single trading outlet."
                        Members = None
                    }
                ]
                "root to the table's level, in declaration order, each described"

            Expect.equal
                s.Columns
                [
                    {
                        Metric = "revenue"
                        Name = Some "revenue (net)"
                        Unit = Some "GBP"
                        DisplayFormat = Some "#,##0.00"
                        Context = Some RevenueContext
                        Description = Some RevenueColumn
                        Shape = "Scalar"
                        RestrictedUnderPolicies = []
                    }
                ]
                "the column: the registry's declarations and the table's description of it"

            Expect.equal s.PeriodGrain "Day" "the grain"
            Expect.equal s.RefreshCadenceSeconds 86400L "the cadence"
            Expect.equal s.HistoryMode "Replace" "the history mode"
            Expect.equal s.RowCount (Some 6) "the committed rows"
            Expect.isSome s.LastRun "the last committed run"
            Expect.isSome s.LastRefreshedAt "when it committed"
            Expect.equal s.LatestRunOutcome (Some "succeeded") "the latest run's outcome"
            Expect.isEmpty s.RestrictedUnderPolicies "nothing sampled was withheld"
        }

        test "the result carries the same projection as JSON" {
            let w = world ()
            let answer = answerJson w "{}"
            Expect.equal (answer.GetProperty("tableCount").GetInt32()) 2 "two tables"

            let row = tableRow answer trade.Id
            Expect.isFalse (row.GetProperty("withheld").GetBoolean()) "described"
            Expect.equal (str row "description") TradeDescription "its description"
            Expect.equal (str row "level") "site" "its level"

            let path =
                row.GetProperty("path").EnumerateArray()
                |> Seq.map (fun l -> str l "level", str l "description")
                |> List.ofSeq

            Expect.equal
                path
                [
                    "region", "A group of neighbouring sites."
                    "site", "A single trading outlet."
                ]
                "the path, with each level's description"

            let column = row.GetProperty("columns").EnumerateArray() |> Seq.exactlyOne
            Expect.equal (str column "metric") "revenue" "the column's metric id"
            Expect.equal (str column "context") RevenueContext "the metric's context"
            Expect.equal (str column "description") RevenueColumn "the column's description"
            Expect.equal (row.GetProperty("rowCount").GetInt32()) 6 "the row count"
            Expect.equal (str row "freshness") "fresh" "the freshness"
        }

        test "a table with no committed row is described with its declarations, never refreshed" {
            let w = world ()

            let elsewhere = {
                w with
                    Scope =
                        ScopeResolution.ofStorageScope {
                            ScopeId = "team-empty-" + Guid.NewGuid().ToString "N"
                            Container = "container-empty"
                            Persist = true
                        }
            }

            for view in project elsewhere None do
                let s = shownOf view
                Expect.equal s.Freshness "never-refreshed" (sprintf "'%s' has never run here" s.TableId)
                Expect.isNone s.RowCount "no committed rows"
                Expect.isNone s.LastRun "no run"
        }

        test "undescribed registries leave the description fields absent and the path the level alone" {
            let w = FactRenderToolTests.RenderFixtures.world ()

            let s =
                {
                    w with
                        Deps = { w.Deps with Registry = None }
                }
                |> fun w -> project w (Some trade.Id)
                |> List.exactlyOne
                |> shownOf

            Expect.isNone s.Description "no table description"
            Expect.isNone s.HierarchyName "no registered hierarchy"

            Expect.equal
                s.Path
                [
                    {
                        Level = "site"
                        Description = None
                        Members = None
                    }
                ]
                "the level alone"

            Expect.equal
                (s.Columns |> List.map (fun c -> c.Metric, c.Name, c.Context))
                [ "revenue", None, None ]
                "the column, its declarations unknown"
        }
    ]

// ─── 2. By id ────────────────────────────────────────────────────────

let private byIdTests =
    testList "one table by id" [
        test "one table by id" {
            let w = world ()
            let views = project w (Some trade.Id)
            Expect.equal (views |> List.map (shownOf >> _.TableId)) [ trade.Id ] "that table alone"

            let answer = answerJson w """{"table":"daily-trade"}"""
            Expect.equal (answer.GetProperty("tableCount").GetInt32()) 1 "one row"
        }

        test "an unknown id is refused by name" {
            let w = world ()
            let answer = answerJson w """{"table":"weekly-trade"}"""

            Expect.equal
                (str answer "error")
                "No fact table 'weekly-trade' is declared in this deployment. Call `list_fact_tables` with no arguments to list every declared table."
                "the refusal names the id and the remedy"
        }

        test "a malformed argument is refused" {
            let w = world ()
            let answer = answerJson w """{"table":3}"""
            Expect.equal (str answer "error") "Argument 'table' must be a string." "the argument's shape"
        }

        test "no arguments, null and a blank id all list every table" {
            let w = world ()

            for args in [ ""; "null"; "{}"; """{"table":"  "}""" ] do
                Expect.equal ((answerJson w args).GetProperty("tableCount").GetInt32()) 2 (sprintf "%A lists both" args)
        }
    ]

// ─── 3. Disclosure ───────────────────────────────────────────────────

let private disclosureTests =
    testList "disclosure" [
        test "the sample is judged at the tool-result door" {
            let w = world ()
            project w None |> ignore
            Expect.isNonEmpty w.Gate.Surfaces "the gate was asked"
            Expect.all w.Gate.Surfaces (fun s -> s = FactToolResult) "at FactToolResult, never FactBrowse"
        }

        test "a table whose sampled facts are all withheld is its id and policy only" {
            let w = world ()

            let withheld = project w (Some margins.Id) |> List.exactlyOne |> withheldOf

            Expect.equal
                withheld
                {
                    TableId = margins.Id
                    RestrictedUnderPolicies = [ "trade-figures" ]
                    Reason = FactTablesTool.RestrictedReason
                }
                "existence and policy"
        }

        test "a partly withheld table is described, naming the policy on the table and the column" {
            let w = world ()
            w.Gate.Deny [ revenueFactId w "s2" 1 ]

            let s = project w (Some trade.Id) |> List.exactlyOne |> shownOf
            Expect.equal s.RestrictedUnderPolicies [ "trade-figures" ] "the table names the policy"

            Expect.equal
                (s.Columns |> List.map _.RestrictedUnderPolicies)
                [ [ "trade-figures" ] ]
                "and so does the column"
        }

        test "denying every fact of a table withholds it" {
            let w = world ()

            w.Gate.Deny [
                for site in [ "s1"; "s2" ] do
                    for offset in 0..2 do
                        revenueFactId w site offset
            ]

            let withheld = project w (Some trade.Id) |> List.exactlyOne |> withheldOf
            Expect.equal withheld.RestrictedUnderPolicies [ "trade-figures" ] "the policy"
        }

        test "no value appears in any result" {
            let w = world ()
            w.Gate.Deny [ revenueFactId w "s2" 0 ]

            for args in [ "{}"; """{"table":"daily-trade"}"""; """{"table":"daily-margin"}""" ] do
                let text = answer w args

                Expect.isFalse (text.Contains DistinctiveDigits) (sprintf "%s: the distinctive value" args)

                for figure in figures do
                    Expect.isFalse (text.Contains figure) (sprintf "%s: '%s' is in no result" args figure)
        }
    ]

// ─── 3b. Members of a small level ────────────────────────────────────

let private levelMembers (s: FactTableShown) : FactLevelMembers option = (List.last s.Path).Members

let private projectCapped (cap: int) (w: World) (table: string) : FactTableShown =
    match
        FactTablesTool.projectWith cap w.Deps w.Scope "u" (Some table)
        |> Async.RunSynchronously
    with
    | Ok views -> views |> List.exactlyOne |> shownOf
    | Error e -> failtestf "expected a projection, got %s" e

let private answerWith (options: FactTablesOptions) (w: World) (args: string) : JsonElement =
    (JsonDocument.Parse(
        FactTablesTool.executeWithOptions options w.Deps w.Scope "u" args
        |> Async.RunSynchronously
    ))
        .RootElement.Clone()

let private lastLevel (row: JsonElement) : JsonElement =
    row.GetProperty("path").EnumerateArray() |> Seq.last

let private strings (el: JsonElement) : string list =
    el.EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq

let private memberTests =
    testList "the members of a small level" [
        test "a two-site table lists both site names on its level, and no other level" {
            let w = world ()
            let s = projectCapped FactTablesOptions.DefaultMemberCap w trade.Id

            Expect.equal
                (levelMembers s)
                (Some(
                    FactLevelMembers.Listed {
                        Names = [ "s1"; "s2" ]
                        Unlisted = 0
                        RestrictedUnderPolicies = []
                    }
                ))
                "the site names, ordinal-sorted, none unlisted"

            Expect.isNone (List.head s.Path).Members "the region level above it names nothing"
        }

        test "the tool lists the names under the default cap" {
            let w = world ()
            let row = tableRow (answerJson w "{}") trade.Id
            let members = (lastLevel row).GetProperty("members")

            Expect.isTrue (members.GetProperty("listed").GetBoolean()) "listed"
            Expect.equal (strings (members.GetProperty "names")) [ "s1"; "s2" ] "the site names"
            Expect.equal (members.GetProperty("count").GetInt32()) 2 "two members"
            Expect.equal (members.GetProperty("unlisted").GetInt32()) 0 "none withheld"

            Expect.isFalse
                ((row.GetProperty("path").EnumerateArray() |> Seq.head).TryGetProperty("members")
                 |> fst)
                "the region level carries none"
        }

        test "the default cap is twenty, and the plain projection names no member" {
            Expect.equal FactTablesOptions.defaults.MemberCap 20 "twenty"
            Expect.equal FactTablesOptions.membersOff.MemberCap 0 "off"

            let w = world ()
            let s = project w (Some trade.Id) |> List.exactlyOne |> shownOf
            Expect.isNone (levelMembers s) "project asks for no members"
        }

        test "the cap is honoured: at the cap the names are listed, above it only the count" {
            let w = world ()

            Expect.isSome
                (match levelMembers (projectCapped 2 w trade.Id) with
                 | Some(FactLevelMembers.Listed l) -> Some l
                 | _ -> None)
                "two members under a cap of two are listed"

            match levelMembers (projectCapped 1 w trade.Id) with
            | Some(FactLevelMembers.CountOnly c) ->
                Expect.equal
                    c
                    {
                        Count = 2
                        Cap = 1
                        Reason = FactTablesTool.OverCapReason
                    }
                    "the count, the cap and why"
            | other -> failtestf "expected a count only above the cap, got %A" other

            let members =
                (lastLevel (tableRow (answerWith { MemberCap = 1 } w "{}") trade.Id)).GetProperty("members")

            Expect.isFalse (members.GetProperty("listed").GetBoolean()) "not listed"
            Expect.equal (propertyNames members |> List.sort) [ "cap"; "count"; "listed"; "reason" ] "no names property"
            Expect.isFalse ((string members).Contains "s1") "no member name crosses"
        }

        test "a cap of zero (or less) lists none" {
            let w = world ()

            for cap in [ 0; -3 ] do
                Expect.isNone (levelMembers (projectCapped cap w trade.Id)) (sprintf "cap %d: no members" cap)

                let row = tableRow (answerWith { MemberCap = cap } w "{}") trade.Id

                for level in row.GetProperty("path").EnumerateArray() do
                    Expect.isFalse
                        (level.TryGetProperty("members") |> fst)
                        (sprintf "cap %d: no level carries members" cap)
        }

        test "a table with no committed row names no member" {
            let w = world ()

            let elsewhere = {
                w with
                    Scope =
                        ScopeResolution.ofStorageScope {
                            ScopeId = "team-empty-" + Guid.NewGuid().ToString "N"
                            Container = "container-empty"
                            Persist = true
                        }
            }

            Expect.isNone (levelMembers (projectCapped 20 elsewhere trade.Id)) "nothing to name"
        }

        test "a partly visible level lists what is visible and counts the rest" {
            let w = world ()

            w.Gate.Deny [
                for offset in 0..2 do
                    revenueFactId w "s2" offset
            ]

            match levelMembers (projectCapped 20 w trade.Id) with
            | Some(FactLevelMembers.Listed l) ->
                Expect.equal l.Names [ "s1" ] "only the visible site is named"
                Expect.equal l.Unlisted 1 "the other is counted"
                Expect.equal l.RestrictedUnderPolicies [ "trade-figures" ] "and the policy named"
            | other -> failtestf "expected a listed level, got %A" other

            let text =
                answerWith FactTablesOptions.defaults w """{"table":"daily-trade"}""" |> string

            Expect.isFalse (text.Contains "\"s2\"") "the withheld site's name crosses nowhere"
        }

        test "the member read is judged at the tool-result door" {
            let w = world ()
            projectCapped 20 w trade.Id |> ignore
            Expect.all w.Gate.Surfaces (fun s -> s = FactToolResult) "at FactToolResult, never FactBrowse"
        }
    ]

// ─── 4. Composition ──────────────────────────────────────────────────

let private composedUnder (knob: FactStoreMode) : ServerApp * ServiceProvider * IBlobStorage =
    let app =
        {
            ServerApp.empty with
                Config = {
                    ServerConfig.defaults with
                        FactStore = knob
                }
        }
        |> FactsCompose.withFactStore
        |> FactsCompose.withFactTableWriter

    let storage = InMemoryBlobStorage() :> IBlobStorage
    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(storage) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    services.AddSingleton<IMetricRegistry>(registry) |> ignore
    services.AddSingleton<IFactTableRegistry>(tables) |> ignore

    match ServerApp.serviceConfig app with
    | Some cfg -> cfg services |> ignore
    | None -> ()

    app, services.BuildServiceProvider(), storage

let private contextFor (sp: IServiceProvider) (scope: ResolvedScope) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- sp
    ScopeResolution.carry ctx scope
    ctx.Items["ToolUp.UserId"] <- box "user-1"
    ctx :> HttpContext

let private newScope () : ResolvedScope =
    let scopeId = "team-" + Guid.NewGuid().ToString "N"

    ScopeResolution.ofStorageScope {
        ScopeId = scopeId
        Container = "container-" + scopeId
        Persist = true
    }

let private compositionTests =
    testList "composition" [
        test "the definition: server-resident, reads facts, one optional argument" {
            let d = FactTablesTool.definition
            Expect.equal d.Name "list_fact_tables" "its name"
            Expect.equal d.Location ServerResident "server-resident"
            Expect.equal d.Effects ToolEffectDeclaration.readFacts "ReadFacts only"
            Expect.equal (d.Parameters |> List.map (fun p -> p.Name, p.Required)) [ "table", false ] "its argument"
            Expect.stringContains d.Description "never a value" "it says it carries no value"
            Expect.stringContains d.Description "NOT HELD" "it says what an absent level means"
        }

        test "coverage points table-shaped questions at the table listing" {
            Expect.stringContains
                CoverageTool.definition.Description
                "call `list_fact_tables`, which reports by table where this tool reports by metric"
                "one sentence in the coverage tool's description"
        }

        test "the fact store's knob declares the table listing with the other fact tools" {
            let app, _, _ = composedUnder EnabledFactStore

            Expect.equal
                (app.AITools |> List.map (fun (d, _) -> d.Name))
                [
                    "query_facts"
                    "query_metric_population"
                    "list_metric_coverage"
                    "list_fact_tables"
                    "aggregate_metric"
                ]
                "the fact tools, in compose order"
        }

        test "the grounded narrative's tool set is the same list" {
            let app, _, _ = composedUnder EnabledFactStore

            Expect.equal
                (NarrativeGrounding.factTools |> List.map (fun (d, _) -> d.Name))
                (app.AITools |> List.map (fun (d, _) -> d.Name))
                "one list"
        }

        test "no fact store declares no table listing" {
            let app, _, _ = composedUnder NoFactStore
            Expect.isEmpty app.AITools "nothing declared"
        }

        test "without a fact store the executor refuses naming itself" {
            let ctx = DefaultHttpContext()
            ctx.RequestServices <- ServiceCollection().BuildServiceProvider()

            Expect.equal
                (FactTablesTool.execute ctx "{}" |> Async.RunSynchronously)
                """{"error":"The fact store is not composed in this deployment — `list_fact_tables` is unavailable."}"""
                "the shell's refusal, naming this tool"
        }

        test "composed, the executor lists the tables the request's scope holds" {
            let _, sp, _ = composedUnder EnabledFactStore
            let scope = newScope ()
            seed (sp.GetRequiredService<IFactTableWriter>()) scope trade "revenue"

            let answer =
                (JsonDocument.Parse(FactTablesTool.execute (contextFor sp scope) "{}" |> Async.RunSynchronously))
                    .RootElement

            let row = tableRow answer trade.Id
            Expect.isFalse (row.GetProperty("withheld").GetBoolean()) "described"
            Expect.equal (row.GetProperty("rowCount").GetInt32()) 6 "its committed rows, in this scope"
        }

        test "withFactTablesOptions hands the cap to the executor as a value" {
            let app, sp, _ = composedUnder EnabledFactStore
            let scope = newScope ()
            seed (sp.GetRequiredService<IFactTableWriter>()) scope trade "revenue"

            let run (a: ServerApp) =
                let exec =
                    a.AITools |> List.find (fun (d, _) -> d.Name = FactTablesTool.Name) |> snd

                exec (contextFor sp scope) "{}" |> Async.RunSynchronously

            Expect.stringContains
                (run app)
                "\"names\":[\"s1\",\"s2\"]"
                "composed plainly, the default cap lists the sites"

            let capped = FactsCompose.withFactTablesOptions { MemberCap = 1 } app
            Expect.stringContains (run capped) "\"listed\":false" "a cap of one counts only"

            let off = FactsCompose.withFactTablesOptions FactTablesOptions.membersOff app
            Expect.isFalse ((run off).Contains "\"members\"") "a cap of zero lists none"

            Expect.equal
                (off.AITools |> List.map (fun (d, _) -> d.Name))
                (app.AITools |> List.map (fun (d, _) -> d.Name))
                "the same tools, in the same order"
        }

        test "withFactTablesOptions is refused before withFactStore and inert without a store" {
            let enabled = {
                ServerApp.empty with
                    Config = {
                        ServerConfig.defaults with
                            FactStore = EnabledFactStore
                    }
            }

            Expect.throwsT<CompositionRefused>
                (fun () -> FactsCompose.withFactTablesOptions FactTablesOptions.defaults enabled |> ignore)
                "the tool is not declared yet"

            Expect.isEmpty
                (FactsCompose.withFactTablesOptions FactTablesOptions.defaults ServerApp.empty).AITools
                "no fact store, nothing declared"
        }
    ]

// ─── 5. A scripted turn ──────────────────────────────────────────────
//
// The model is asked for a metric at a level no table holds. The provider
// knows no table id and no level: it lists the tables, looks for one holding
// the metric at the asked level, and finding none answers from the listing
// that it is not held, naming where it is. It issues no query call.

/// A provider that calls `list_fact_tables`, then answers from that result
/// alone whether `metric` is held at `level`.
type private ListingProvider(metric: string, level: string) =
    let mutable turns = 0

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "test-tables"
            Model = "test-tables-model"
        }

        member _.SendMessage(messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            turns <- turns + 1

            match turns with
            | 1 ->
                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = Guid.NewGuid().ToString()
                                Name = "list_fact_tables"
                                Arguments = "{}"
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            | _ ->
                let listing =
                    messages
                    |> List.collect _.ToolResults
                    |> List.last
                    |> fun r -> (JsonDocument.Parse r.Content).RootElement

                let holding =
                    listing.GetProperty("tables").EnumerateArray()
                    |> Seq.filter (fun t -> not (t.GetProperty("withheld").GetBoolean()))
                    |> Seq.filter (fun t ->
                        t.GetProperty("columns").EnumerateArray()
                        |> Seq.exists (fun c -> c.GetProperty("metric").GetString() = metric))
                    |> Seq.map (fun t -> t.GetProperty("id").GetString(), t.GetProperty("level").GetString())
                    |> List.ofSeq

                let answer =
                    match holding |> List.filter (fun (_, l) -> l = level) with
                    | [] ->
                        let where =
                            holding
                            |> List.map (fun (id, l) -> sprintf "at %s in %s" l id)
                            |> String.concat ", "

                        sprintf "No table holds %s at the %s level; it is held %s." metric level where
                    | (id, _) :: _ -> sprintf "%s holds %s at the %s level." id metric level

                return
                    Ok {
                        Content = answer
                        ToolCalls = []
                        StopReason = "end_turn"
                        Usage = None
                    }
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

let private turnTests =
    testList "a scripted turn" [
        testCaseAsync "asked for a metric at a level no table holds, the turn says so without a query call"
        <| async {
            let app, sp, _ = composedUnder EnabledFactStore
            let scope = newScope ()
            seed (sp.GetRequiredService<IFactTableWriter>()) scope trade "revenue"

            let toolRegistry = AIToolRegistry()
            toolRegistry.RegisterAll(app.AITools |> List.map (fun (def, exec) -> createTool def exec))

            let events = ResizeArray<AIStreamEvent>()
            let onEvent (evt: AIStreamEvent) = lock events (fun () -> events.Add evt)

            let! finalMessages =
                AIAgentEngine.runAgentLoopWithInput
                    (ListingProvider("revenue", "region") :> IAIProvider)
                    toolRegistry
                    (ClientToolDispatch.ClientToolDispatchRegistry())
                    (contextFor sp scope)
                    (Guid.NewGuid())
                    (Guid.NewGuid())
                    AISurface.FullPage
                    None
                    None
                    CancellationToken.None
                    [ AIProviderMessage.text "user" "What was revenue by region last week?" ]
                    ToolUp.AI.ModelInput.empty
                    onEvent

            let toolCalls =
                lock events (fun () -> List.ofSeq events)
                |> List.choose (function
                    | ToolCallStarted(_, name, _) -> Some name
                    | _ -> None)

            Expect.equal toolCalls [ "list_fact_tables" ] "the listing, and no query call"

            let answer =
                finalMessages
                |> List.filter (fun m -> m.Role = "assistant" && m.Content <> "")
                |> List.last
                |> _.Content

            Expect.equal
                answer
                "No table holds revenue at the region level; it is held at site in daily-trade."
                "it is not held, and where it is"
        }
    ]

let tests =
    testList "list_fact_tables — the assistant sees what tables exist before it asks" [
        listingTests
        byIdTests
        disclosureTests
        memberTests
        compositionTests
        turnTests
    ]
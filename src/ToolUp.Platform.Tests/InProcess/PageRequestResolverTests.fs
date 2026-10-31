// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.PageRequestResolverTests

// The request resolvers a stored page renders through: a presentation request
// and a pipeline request answered under the reader's gate, a withheld point
// absent and counted and never zero, one canonical answer through the typed
// and the canonical-JSON entry points, a per-session cache dropped by the
// fact-table change signal, and composition only with a fact store.

open System
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Fuaran.Core
open Fuaran.Compute
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Facts
open ToolUp.Facts.Presentation
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.FactRenderToolTests.RenderFixtures

let private run (a: Async<'T>) : 'T = Async.RunSynchronously a

let private okOf (label: string) (r: Result<'T, PageRefusal>) : 'T =
    match r with
    | Ok v -> v
    | Error e -> failtestf "%s refused (%s): %s" label (PageRefusal.cause e) (PageRefusal.describe e)

/// A fixed gate fingerprint per principal: the viewer facts of a caller with
/// no team.
let private gateOf (w: World) (principal: string) () =
    async.Return(
        PageGateViewer.fingerprint {
            Principal = principal
            ScopeId = w.Scope.ScopeId
            ActiveTeamId = None
            IsPlatformAdmin = false
            TeamRole = None
            OutputVisibility = None
        }
    )

/// A resolver for `principal` in `w`, in its session of `cache`.
let private resolverIn (cache: PageResolutionCache) (w: World) (principal: string) : PageRequestResolver =
    let session = run (cache.Session(w.Scope, principal))
    PageRequestResolver.create session (gateOf w principal) w.Deps w.Scope principal

/// A resolver with a cache of its own and no signal.
let private freshResolver (w: World) (principal: string) =
    resolverIn (PageResolutionCache None) w principal

/// The revenue grouped by site over the three seeded days.
let private totalsPipeline: Transform list = [
    GroupBy(
        [ FactBrowseSlice.SubjectColumn ],
        [
            {
                Name = "total"
                Fn = Sum
                Of = "revenue"
            }
        ]
    )
]

let private pipelineRequest (tableId: string) : PipelineBlockRequest = {
    TableId = tableId
    PeriodFrom = day1
    PeriodTo = day1.AddDays 3.0
    PathPrefix = []
    Metrics = [ (if tableId = margins.Id then "margin" else "revenue") ]
    Pipeline = DataFrameCodec.encodePipeline totalsPipeline
    PipelineHash = ""
    RunStamp = None
}

let private tableOf (resolution: PageResolution) : Table =
    match PageResolution.table resolution with
    | Ok t -> t
    | Error e -> failtest e

/// Every number cell of a table, as floats; a null cell is skipped.
let private numbers (table: Table) : float list = [
    for column in table.Columns do
        for i in 0 .. Column.length column - 1 do
            match Column.cell i column with
            | Cell.Float f -> f
            | Cell.Int n -> float n
            | Cell.Decimal d -> float (Decimal.Parse(d, Globalization.CultureInfo.InvariantCulture))
            | _ -> ()
]

/// How many null cells a table holds.
let private nulls (table: Table) : int =
    table.Columns
    |> List.sumBy (fun column ->
        [ 0 .. Column.length column - 1 ]
        |> List.filter (fun i -> Column.cell i column = Cell.Null)
        |> List.length)

let private codecTests =
    testList "the canonical form" [
        test "a presentation request round-trips, and its canonical text sorts members and drops whitespace" {
            let request = PageRequest.Presentation(trendRequest, [ "b"; "a"; "b" ])

            let text =
                match PageRequest.toJson request with
                | Ok t -> t
                | Error e -> failtest (PageRequestError.describe e)

            Expect.stringStarts
                text
                """{"dependsOn":["a","b"],"kind":"presentation","payload":{"""
                "sorted, no whitespace"

            Expect.isFalse
                (text.Contains ": " || text.Contains ", " || text.Contains "\n")
                "no whitespace is written between tokens"

            match PageRequest.ofJson text with
            | Ok(PageRequest.Presentation(r, keys)) ->
                Expect.equal r trendRequest "the presentation request is the codec's"
                Expect.equal keys [ "a"; "b" ] "dependsOn is sorted and distinct"
            | other -> failtestf "expected the presentation request back, got %A" other

            let shuffled =
                """{"payload":{"metrics":["revenue"],"tableId":"daily-trade","kind":"trend","period":{"rangeDays":3,"day":"2026-10-03"},"title":"Revenue per day by site"},"kind":"presentation","dependsOn":["b","a"]}"""

            match PageRequest.ofJson shuffled |> Result.bind PageRequest.toJson with
            | Ok again -> Expect.equal again text "one request has one text, whatever order it was written in"
            | Error e -> failtest (PageRequestError.describe e)
        }

        test "a pipeline request round-trips with its pipeline canonical" {
            let request = PageRequest.Pipeline(pipelineRequest trade.Id, [])

            let text =
                match PageRequest.toJson request with
                | Ok t -> t
                | Error e -> failtest (PageRequestError.describe e)

            Expect.isFalse (text.Contains "dependsOn") "an empty dependsOn is omitted"

            match PageRequest.ofJson text with
            | Ok(PageRequest.Pipeline(r, [])) ->
                Expect.equal r.TableId trade.Id "the table"
                Expect.equal r.PeriodFrom day1 "the period start"
                Expect.equal r.PeriodTo (day1.AddDays 3.0) "the period end"
                Expect.equal (PageRequest.toJson (PageRequest.Pipeline(r, []))) (Ok text) "the text is a fixed point"
            | other -> failtestf "expected the pipeline request back, got %A" other
        }

        test "an unknown kind and a malformed payload are refused by name" {
            match PageRequest.ofJson """{"kind":"chart","payload":{}}""" with
            | Error(PageRequestError.UnknownKind "chart") -> ()
            | other -> failtestf "expected the unknown kind, got %A" other

            match PageRequest.ofJson """{"kind":"presentation","payload":{"kind":"trend"}}""" with
            | Error(PageRequestError.Presentation(PresentationRequestError.MissingField "tableId")) -> ()
            | other -> failtestf "expected the presentation codec's refusal, got %A" other

            match PageRequest.ofJson "not json" with
            | Error(PageRequestError.NotJson _) -> ()
            | other -> failtestf "expected not-JSON, got %A" other
        }
    ]

let private withheldTests =
    testList "a withheld point is absent and counted, never zero" [
        test "a presentation request under a gate that withholds one point" {
            let w = world ()
            w.Gate.Deny [ revenueFactId w "s2" 1 ]

            let resolution =
                (freshResolver w "u").ResolvePresentation trendRequest
                |> run
                |> okOf "the trend"

            let table = tableOf resolution
            Expect.equal resolution.Rows 2 "the day with the withheld point is not drawn"

            Expect.equal
                resolution.Withheld
                [
                    {
                        PolicyRef = "trade-figures"
                        Count = 1
                    }
                ]
                "the withheld point is counted by its policy"

            Expect.isFalse (numbers table |> List.contains 0.0) "no point is drawn as zero"

            Expect.isFalse
                (numbers table |> List.contains (float DistinctiveValue))
                "the withheld value is nowhere in the rows"

            Expect.isFalse
                ((PageResolution.toJson resolution).Contains DistinctiveDigits)
                "nor anywhere in the resolution's text"

            Expect.isTrue
                (resolution.Notes |> List.exists (fun n -> n.Contains "2026-10-02 is not drawn"))
                "the left-out day is named"
        }

        test "a table-kind request leaves out a subject with no cell it may show" {
            let w = world ()
            w.Gate.Deny [ revenueFactId w "s2" 2 ]

            let request = {
                trendRequest with
                    Kind = PresentationKind.Table
                    Title = None
            }

            let resolution =
                (freshResolver w "u").ResolvePresentation request |> run |> okOf "the table"

            let table = tableOf resolution
            Expect.equal resolution.Columns [ "subject"; "revenue" ] "a subject column and one per metric"
            Expect.equal resolution.Rows 1 "the subject whose one cell is withheld is left out"
            Expect.equal (nulls table) 0 "no row is drawn for it"
            Expect.isFalse (numbers table |> List.contains 0.0) "never zero"
            Expect.equal (PageResolution.withheldCount resolution) 1 "and counted"

            Expect.isTrue
                (resolution.Notes |> List.exists (fun n -> n.Contains "revenue is not shown"))
                "and the cell is named"
        }

        test "a pipeline request under a gate that withholds one point" {
            let w = world ()
            w.Gate.Deny [ revenueFactId w "s2" 1 ]

            let resolution =
                (freshResolver w "u").ResolvePipeline(pipelineRequest trade.Id)
                |> run
                |> okOf "the pipeline"

            let table = tableOf resolution
            Expect.equal resolution.Columns [ FactBrowseSlice.SubjectColumn; "total" ] "the result's columns"
            Expect.equal resolution.Rows 2 "one row per site"
            Expect.equal (nulls table) 2 "the withheld column's cells are null"
            Expect.isFalse (numbers table |> List.contains 0.0) "never zero"

            Expect.equal
                resolution.Withheld
                [
                    {
                        PolicyRef = "trade-figures"
                        Count = 1
                    }
                ]
                "the withheld cell is counted by its policy"

            Expect.isNonEmpty resolution.Notes "the withheld column is named"
        }
    ]

let private refusalTests =
    testList "a refusal is typed by cause" [
        test "an undeclared table" {
            let w = world ()

            match
                (freshResolver w "u").ResolvePresentation {
                    trendRequest with
                        TableId = "nowhere"
                }
                |> run
            with
            | Error(PageRefusal.UnknownTable("nowhere", declared)) ->
                Expect.containsAll declared [ trade.Id; margins.Id ] "the declared tables are named"
            | other -> failtestf "expected the unknown table, got %A" other

            match (freshResolver w "u").ResolvePipeline(pipelineRequest "nowhere") |> run with
            | Error(PageRefusal.UnknownTable("nowhere", _)) -> ()
            | other -> failtestf "expected the unknown table, got %A" other
        }

        test "everything withheld is a refusal naming the count, never an empty table" {
            let w = world ()

            let request = {
                trendRequest with
                    TableId = margins.Id
                    Metrics = [ "margin" ]
            }

            match (freshResolver w "u").ResolvePresentation request |> run with
            | Error(PageRefusal.Withheld(read, withheld)) ->
                Expect.equal read 6 "every point read"
                Expect.equal (withheld |> List.sumBy _.Count) 6 "every point withheld"
            | other -> failtestf "expected withheld, got %A" other

            match (freshResolver w "u").ResolvePipeline(pipelineRequest margins.Id) |> run with
            | Error(PageRefusal.Withheld(6, _)) -> ()
            | other -> failtestf "expected withheld, got %A" other
        }

        test "an unknown kind and a malformed payload through the canonical entry point" {
            let w = world ()
            let resolver = freshResolver w "u"

            match resolver.ByKind "chart" "{}" |> run with
            | Error(PageRefusal.UnknownKind "chart") -> ()
            | other -> failtestf "expected the unknown kind, got %A" other

            match resolver.ByKind PageRequest.PresentationKind "{" |> run with
            | Error(PageRefusal.MalformedRequest _) -> ()
            | other -> failtestf "expected a malformed request, got %A" other

            Expect.equal
                (PageRequestResolver.byKind resolver |> Map.keys |> List.ofSeq)
                [ PageRequest.PipelineKind; PageRequest.PresentationKind ]
                "the map a page view takes holds both kinds"
        }
    ]

/// The canonical payload of a page request: what a page tier carries under its
/// kind.
let private payloadOf (request: PageRequest) : string =
    match PageRequest.toJVal request with
    | Ok(JObj members) ->
        members
        |> List.find (fun (k, _) -> k = "payload")
        |> snd
        |> Canonical.write Rendering.CrossHost
    | other -> failtestf "no payload: %A" other

let private entryPointTests =
    testList "one resolution through both entry points" [
        test "a presentation request resolves to the same bytes typed and over canonical JSON" {
            let w = world ()
            w.Gate.Deny [ revenueFactId w "s2" 1 ]
            let client = freshResolver w "u"
            let report = freshResolver w "u"

            let typed =
                client.ResolvePresentation trendRequest
                |> run
                |> okOf "client"
                |> PageResolution.toJson

            let canonical =
                (PageRequestResolver.byKind report).[PageRequest.PresentationKind] (
                    payloadOf (PageRequest.Presentation(trendRequest, []))
                )
                |> run
                |> okOf "report"

            Expect.equal canonical typed "the same bytes"
        }

        test "a pipeline request resolves to the same bytes typed and over canonical JSON" {
            let w = world ()
            w.Gate.Deny [ revenueFactId w "s2" 1 ]
            let client = freshResolver w "u"
            let report = freshResolver w "u"

            let typed =
                client.ResolvePipeline(pipelineRequest trade.Id)
                |> run
                |> okOf "client"
                |> PageResolution.toJson

            let canonical =
                report.ByKind PageRequest.PipelineKind (payloadOf (PageRequest.Pipeline(pipelineRequest trade.Id, [])))
                |> run
                |> okOf "report"

            Expect.equal canonical typed "the same bytes"
        }
    ]

let private cacheTests =
    testList "the per-session cache" [
        test "a hit, a miss after the table changes, and no change for another table" {
            let w = world ()
            let signal = FactTableChangeSignal.create None (fun () -> true)
            let cache = PageResolutionCache(Some signal)
            let resolver = resolverIn cache w "u"

            let resolve () =
                resolver.ResolvePresentation trendRequest |> run |> okOf "the trend"

            Expect.equal (resolve ()).Rows 3 "nothing withheld at first"

            w.Gate.Deny [ revenueFactId w "s2" 1 ]
            Expect.equal (resolve ()).Rows 3 "a hit: the session answers what it kept"

            run (signal.Raise(w.Scope, [ margins.Id ]))
            Expect.equal (resolve ()).Rows 3 "another table's change keeps it"

            run (signal.Raise(w.Scope, [ trade.Id ]))
            let after = resolve ()
            Expect.equal after.Rows 2 "a miss after the table changed: the gate is asked again"
            Expect.equal (PageResolution.withheldCount after) 1 "and the withheld point is counted"
        }

        test "a committed run raises its table's change through the writer" {
            let w = world ()
            let signal = FactTableChangeSignal.create None (fun () -> true)
            let cache = PageResolutionCache(Some signal)
            let resolver = resolverIn cache w "u"

            let resolve () =
                resolver.ResolvePresentation trendRequest |> run |> okOf "the trend"

            Expect.equal (resolve ()).Rows 3 "nothing withheld at first"
            w.Gate.Deny [ revenueFactId w "s2" 1 ]
            seed (FactTableChangeSignal.raisingWriter signal w.Deps.Writer.Value) w.Scope trade "revenue"
            Expect.equal (resolve ()).Rows 2 "the commit dropped the kept resolution"
        }

        test "a data-object change drops every table of its scope, and only when armed" {
            let w = world ()
            let mutable armed = false
            let signal = FactTableChangeSignal.create None (fun () -> armed)
            let cache = PageResolutionCache(Some signal)
            let resolver = resolverIn cache w "u"

            let resolve () =
                resolver.ResolvePresentation trendRequest |> run |> okOf "the trend"

            let react = FactTableChangeSignal.reaction (fun () -> signal)

            Expect.equal (resolve ()).Rows 3 "nothing withheld at first"
            w.Gate.Deny [ revenueFactId w "s2" 1 ]
            run (react w.Scope [ "object-1" ])
            Expect.equal (resolve ()).Rows 3 "a dormant signal raises nothing"

            armed <- true
            run (react w.Scope [ "object-1" ])
            Expect.equal (resolve ()).Rows 2 "an armed one drops the scope's resolutions"
        }

        test "no resolution is shared between callers" {
            let w = world ()
            let cache = PageResolutionCache None
            let first = resolverIn cache w "u"

            Expect.equal (first.ResolvePresentation trendRequest |> run |> okOf "u").Rows 3 "u's resolution"
            w.Gate.Deny [ revenueFactId w "s2" 1 ]

            let second = resolverIn cache w "v"
            let theirs = second.ResolvePresentation trendRequest |> run |> okOf "v"
            Expect.equal theirs.Rows 2 "v is resolved under the gate as it stands, never from u's session"
            Expect.equal (PageResolution.withheldCount theirs) 1 "with the withheld point counted"
        }

        test "a gate fingerprint that moves misses" {
            let w = world ()
            let cache = PageResolutionCache None
            let session = run (cache.Session(w.Scope, "u"))
            let mutable role: string option = None

            let gate () =
                async.Return(
                    PageGateViewer.fingerprint {
                        Principal = "u"
                        ScopeId = w.Scope.ScopeId
                        ActiveTeamId = Some "t"
                        IsPlatformAdmin = false
                        TeamRole = role
                        OutputVisibility = None
                    }
                )

            let resolver = PageRequestResolver.create session gate w.Deps w.Scope "u"

            let resolve () =
                resolver.ResolvePresentation trendRequest |> run |> okOf "the trend"

            Expect.equal (resolve ()).Rows 3 "nothing withheld at first"
            w.Gate.Deny [ revenueFactId w "s2" 1 ]
            Expect.equal (resolve ()).Rows 3 "the same viewer hits"
            role <- Some "Viewer"
            Expect.equal (resolve ()).Rows 2 "a viewer whose role moved is resolved again"
        }
    ]

/// A request context over a composed app: the fact tier, its writer and,
/// when `pages`, the page request resolvers.
let private composedContext (pages: bool) : HttpContext * IServiceProvider =
    let app =
        {
            ServerApp.empty with
                Config = {
                    ServerConfig.defaults with
                        FactStore = EnabledFactStore
                }
        }
        |> FactsCompose.withFactStore
        |> FactsCompose.withFactTableWriter
        |> fun app ->
            if pages then
                PresentationCompose.withPageRequests app
            else
                app

    let services = ServiceCollection() :> IServiceCollection
    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    services.AddSingleton<IMetricRegistry>(registry) |> ignore
    services.AddSingleton<IFactTableRegistry>(tables) |> ignore

    match ServerApp.serviceConfig app with
    | Some configure -> configure services |> ignore
    | None -> failtest "the composed fact tier registers its services"

    let built = services.BuildServiceProvider() :> IServiceProvider
    let scopeId = "team-" + Guid.NewGuid().ToString "N"

    let scope: StorageScope = {
        ScopeId = scopeId
        Container = "container-" + scopeId
        Persist = true
    }

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- built
    ctx.Items["ToolUp.StorageScope"] <- box scope
    let resolved = ScopeResolution.remember ctx scope
    ctx.Items["ToolUp.UserId"] <- box "u"

    seed (built.GetRequiredService<IFactTableWriter>()) resolved trade "revenue"
    ctx :> HttpContext, built

let private compositionTests =
    testList "composition" [
        test "a deployment composing no fact store registers no resolver" {
            let app =
                {
                    ServerApp.empty with
                        Config = {
                            ServerConfig.defaults with
                                FactStore = NoFactStore
                        }
                }
                |> PresentationCompose.withPageRequests

            Expect.isNone (ServerApp.serviceConfig app) "nothing is registered"
            Expect.isEmpty app.Contributions "no contribution is added"
        }

        test "a deployment composing the fact store and page requests resolves a request for its caller" {
            let ctx, services = composedContext true

            Expect.isTrue (FactTableChangeSignal.demanded services) "the change signal is armed"

            let resolvers = services.GetRequiredService<PageRequestResolvers>()

            let resolver =
                match resolvers.ForRequest ctx |> run with
                | Ok r -> r
                | Error e -> failtest (PageRefusal.describe e)

            let resolution =
                resolver.ResolvePresentation trendRequest |> run |> okOf "the trend"

            Expect.equal resolution.Rows 3 "three days drawn"
            Expect.equal resolution.Columns [ "day"; "s1"; "s2" ] "a day column and one per site"
        }

        test "page requests not composed leaves the signal dormant and no resolver registered" {
            let _, services = composedContext false
            Expect.isFalse (FactTableChangeSignal.demanded services) "nothing demands the signal"

            Expect.isNull
                (services.GetService typeof<PageRequestResolvers>)
                "no resolver is registered without the call"
        }
    ]

let tests =
    testList "page request resolvers — the data a stored page renders, under the reader's gate" [
        codecTests
        withheldTests
        refusalTests
        entryPointTests
        cacheTests
        compositionTests
    ]
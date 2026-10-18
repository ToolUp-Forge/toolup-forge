// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.DeclaredDeliveryTests

open System
open System.IO
open System.Text
open System.Threading
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.Tracing
open ToolUp.DataSources.Common
open ToolUp.DataSources.Csv
open ToolUp.DataSources.Tests.Support

// ─── Phase 1015 — a declared source's data arrives as its principal ───
//
// A deployment declares a data source and how its data arrives — pulled on a
// cadence, or ingested as it lands at a watched location — and names the
// principal the delivery runs as. These cases run the whole path in process:
// the shipped team scope resolver over a real team store resolves the
// principal, the shipped in-process scheduler fires the delivery's job on its
// own cadence (its tick, not run-now), and the shipped ingestor commits the
// run. They prove a pull commits under the principal's scope, a watched drop
// is ingested once, an unresolvable principal or an unreadable location
// refuses startup, seeding is idempotent and retires what it no longer
// declares, and the delivery tier builds no resolved scope from a string.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private silentChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async { return () }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe _ = async { return () }
    }

let private csvBytes (rows: string list) =
    Encoding.UTF8.GetBytes(String.concat "\n" ("id,amount" :: rows) + "\n")

/// The data-ingestion substrate as a composition root wires it — the shipped
/// config store, object store, ingestor, ingestion job handler and in-process
/// scheduler — over one blob store, with the Csv connector reading
/// `connectorStorage` (the same store unless a case breaks it), and the
/// platform's declared-principal resolver over the shipped team resolver.
type private World(?connectorStorage: IBlobStorage) =
    let store = FakeBlobStorage.InMemoryBlobStorage()
    let blobs = store :> IBlobStorage
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

    let scheduler =
        JobScheduler.create
            (JobStore.create blobs events)
            events
            silentChannel
            ServerConfig.defaults
            silentLogger
            (NoOpActivitySink() :> IActivitySink)

    let configs = DataSourceConfigStore.create blobs
    let csv = CsvDataSource.create (defaultArg connectorStorage blobs)

    let ingestor =
        DataIngestor.create
            configs
            (TestFakes.InMemorySecretStore())
            (DataObjectStore.DataObjectStore(blobs) :> IDataObjectStore)
            events
            [ csv ]
            blobs
            silentLogger

    let notifications =
        NotificationChannel.InMemoryNotificationChannel(None) :> INotificationChannel

    let teams = TeamManagement.TeamStore(blobs, notifications)

    let cache =
        new Microsoft.Extensions.Caching.Memory.MemoryCache(Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())

    let scopeResolver =
        new TeamScopeResolver(teams, cache, notifications) :> IStorageScopeResolver

    let deps: DeclaredDeliveryDeps = {
        Configs = configs
        Scheduler = scheduler
        Ingestor = ingestor
        Connectors = [ csv ]
        Ledger = DataSourceConfigStore.declaredDeliveryLedger blobs
        WatchPoll = DeclaredDelivery.DefaultWatchPoll
    }

    do
        (scheduler :> IJobScheduler)
            .RegisterHandler(
                DataIngestionJobHandler.JobHandlerName,
                DataIngestionJobHandler.create ingestor silentLogger
            )

        (scheduler :> IJobScheduler)
            .RegisterHandler(DeclaredDelivery.WatchJobHandlerName, DeclaredDelivery.watchHandler deps silentLogger)

    member _.Store = store
    member _.Blobs = blobs
    member _.Events = events
    member _.Scheduler = scheduler
    member _.Configs = configs
    member _.Ingestor = ingestor
    member _.Csv = csv
    member _.Deps = deps
    member _.Resolver = DeclaredPrincipalResolver(scopeResolver.Resolve)

    /// A principal that owns a fresh team, active on it.
    member _.TeamPrincipal(name: string) : DeclaredPrincipal =
        let userId = "svc-" + Guid.NewGuid().ToString "N"
        let team = "team" + Guid.NewGuid().ToString "N"

        async {
            let! _ = teams.CreateTeam(team, team + "-name")
            let! _ = teams.AddMember(team, userId, TeamRole.Owner)
            let! _ = teams.SetActiveTeam(userId, team)
            return ()
        }
        |> Async.RunSynchronously

        {
            Name = name
            UserId = userId
            DisplayName = $"{name} service principal"
        }

    /// The scope `principal` resolves to, as the deployment resolves it.
    member this.ScopeOf(principal: DeclaredPrincipal) : ResolvedScope =
        match
            this.Resolver.Resolve(DeclaredPrincipal.user principal)
            |> Async.RunSynchronously
        with
        | Ok scope -> scope
        | Error e -> failtestf "the principal did not resolve: %A" e

let private csvSource (id: string) (scope: (string * string) list) (tables: string list option) : DataSourceConfig = {
    TestFakes.config id CsvDataSource.Kind scope with
        Tables = tables
}

let private seed (world: World) (declared: DeclaredDataSources) =
    DeclaredDelivery.seed (Some world.Resolver) world.Deps declared
    |> Async.RunSynchronously

let private seeded world declared =
    match seed world declared with
    | Ok report -> report
    | Error refusals -> failtestf "seeding was refused: %A" refusals

let private refusalsOf world declared =
    match seed world declared with
    | Ok report -> failtestf "seeding was not refused: %A" report
    | Error refusals -> refusals

/// The declared jobs for `sourceId` in scope that can still fire.
let private liveJobs (world: World) (scopeId: string) (sourceId: string) =
    (world.Scheduler :> IJobScheduler).ListJobs scopeId
    |> Async.RunSynchronously
    |> List.filter (fun job ->
        job.Status <> JobStatus.Cancelled
        && job.Tags.TryFind DeclaredDelivery.SourceTag = Some sourceId)

let private ingestionRuns (world: World) (scopeId: string) (sourceId: string) =
    world.Ingestor.GetRecentRuns(scopeId, sourceId, 50) |> Async.RunSynchronously

let private completedJobRuns (world: World) (scopeId: string) (jobId: JobId) =
    (world.Scheduler :> IJobScheduler).GetRecentRuns(scopeId, jobId, 50)
    |> Async.RunSynchronously
    |> List.filter _.CompletedAt.IsSome
    |> List.length

/// Fire the scheduler's own cadence at the job's next due time and wait for
/// that dispatch to complete. Bounded.
let private tick (world: World) (scopeId: string) (job: JobDefinition) =
    let current =
        (world.Scheduler :> IJobScheduler).Get(scopeId, job.JobId)
        |> Async.RunSynchronously
        |> Option.defaultWith (fun () -> failtest "the declared job is gone")

    let due =
        current.NextRunAt
        |> Option.defaultWith (fun () -> failtest "the declared job has no next run")

    let before = completedJobRuns world scopeId job.JobId
    world.Scheduler.RunTick(due.AddSeconds 1.0) |> Async.RunSynchronously
    let deadline = DateTime.UtcNow.AddSeconds 60.0

    while completedJobRuns world scopeId job.JobId <= before && DateTime.UtcNow < deadline do
        Thread.Sleep 25

    if completedJobRuns world scopeId job.JobId <= before then
        failtest "the declared job's dispatch never completed"

let private onlyJob world scopeId sourceId =
    match liveJobs world scopeId sourceId with
    | [ job ] -> job
    | jobs -> failtestf "expected one live declared job, got %A" jobs

/// Locate the repository root from the test binary, for the source scan.
let private repoRoot () =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then
            failtest "the repository root (ToolUp.Forge.sln) is not above the test binary"
        elif File.Exists(Path.Combine(dir.FullName, "ToolUp.Forge.sln")) then
            dir.FullName
        else
            up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let tests =
    testList "Phase 1015 — a declared source's data arrives under its declared principal" [

        test "a scheduled pull fires on its cadence and commits under the principal's scope" {
            let world = World()
            let analyst = world.TeamPrincipal "analyst"
            let scope = world.ScopeOf analyst
            world.Store.Put("exports", "sales.csv", csvBytes [ "1,10"; "2,20" ])

            let report =
                seeded world {
                    Principals = [ analyst ]
                    Sources = [
                        {
                            Source = csvSource "sales" [ "container", "exports" ] (Some [ "sales" ])
                            Delivery = DataSourceDelivery.ScheduledPull("0 6 * * *", "analyst")
                        }
                    ]
                }

            Expect.equal report.Seeded [ scope.ScopeId, "sales" ] "seeded at the principal's scope"

            Expect.isSome
                (world.Configs.Get(scope.ScopeId, "sales") |> Async.RunSynchronously)
                "the config is at the principal's scope"

            let job = onlyJob world scope.ScopeId "sales"
            Expect.equal job.Handler DataIngestionJobHandler.JobHandlerName "the platform's own ingestion job"
            Expect.equal job.Trigger (CronTrigger "0 6 * * *") "on the declared cadence"

            Expect.isTrue
                (job.Tags.ContainsKey CarriedJobScope.TokenTag)
                "the job carries the principal's resolved scope (the platform's carrier)"

            Expect.isEmpty (ingestionRuns world scope.ScopeId "sales") "nothing ran before the cadence"

            tick world scope.ScopeId job

            match ingestionRuns world scope.ScopeId "sales" with
            | [ run ] ->
                Expect.equal run.Status IngestionStatus.Succeeded "the pull succeeded"
                Expect.equal run.ScopeId scope.ScopeId "committed under the principal's scope"
            | runs -> failtestf "expected one ingestion run, got %A" runs

            let completed =
                world.Events.ReadByType(scope.ScopeId, "IngestionRunCompleted")
                |> Async.RunSynchronously

            Expect.hasLength completed 1 "IngestionRunCompleted fired in the principal's scope"
        }

        test "a watched drop is ingested once, and a new or rewritten object is ingested again" {
            let world = World()
            let loader = world.TeamPrincipal "loader"
            let scope = world.ScopeOf loader
            let container = scope.Storage.Value.Container

            seeded world {
                Principals = [ loader ]
                Sources = [
                    {
                        Source = csvSource "drops" [] None
                        Delivery = DataSourceDelivery.Watched("incoming", "loader")
                    }
                ]
            }
            |> ignore

            let config =
                world.Configs.Get(scope.ScopeId, "drops")
                |> Async.RunSynchronously
                |> Option.defaultWith (fun () -> failtest "the watched source was not seeded")

            Expect.equal
                (config.ConnectionScope.TryFind "container", config.ConnectionScope.TryFind "prefix")
                (Some container, Some "incoming/")
                "a watch reads its principal's scope container at its location"

            let job = onlyJob world scope.ScopeId "drops"
            Expect.equal job.Handler DeclaredDelivery.WatchJobHandlerName "the watch poll"
            Expect.isTrue (job.Tags.ContainsKey CarriedJobScope.TokenTag) "the poll carries the principal's scope"

            world.Store.Put(container, "incoming/a.csv", csvBytes [ "1,10" ])
            tick world scope.ScopeId job

            let tables () =
                ingestionRuns world scope.ScopeId "drops"
                |> List.map (fun run -> run.Table, run.Status)
                |> List.sort

            Expect.equal (tables ()) [ "a", IngestionStatus.Succeeded ] "the drop is ingested"

            tick world scope.ScopeId job
            Expect.equal (tables ()) [ "a", IngestionStatus.Succeeded ] "polling again ingests nothing new"

            world.Store.Put(container, "incoming/b.csv", csvBytes [ "2,20" ])
            world.Store.Put(container, "incoming/notes.json", Encoding.UTF8.GetBytes "{}")
            world.Store.Put(container, "elsewhere/c.csv", csvBytes [ "3,30" ])
            tick world scope.ScopeId job

            Expect.equal
                (tables ())
                [ "a", IngestionStatus.Succeeded; "b", IngestionStatus.Succeeded ]
                "only the new object at the location, and only the connector's own files"

            world.Store.Put(container, "incoming/a.csv", csvBytes [ "1,10"; "1,11" ])
            tick world scope.ScopeId job

            Expect.equal
                (ingestionRuns world scope.ScopeId "drops"
                 |> List.filter (fun r -> r.Table = "a")
                 |> List.length)
                2
                "an object rewritten in place is a new object"

            Expect.hasLength
                (world.Events.ReadByType(scope.ScopeId, "IngestionRunCompleted")
                 |> Async.RunSynchronously)
                3
                "each ingestion committed in the principal's scope"
        }

        test "an unresolvable principal refuses startup, naming what it lacks, and seeds nothing" {
            let world = World()

            let stranger: DeclaredPrincipal = {
                Name = "stranger"
                UserId = "no-team-" + Guid.NewGuid().ToString "N"
                DisplayName = "Stranger"
            }

            let declared = {
                Principals = [ stranger ]
                Sources = [
                    {
                        Source = csvSource "sales" [ "container", "exports" ] (Some [ "sales" ])
                        Delivery = DataSourceDelivery.ScheduledPull("0 6 * * *", "stranger")
                    }
                ]
            }

            Expect.equal
                (refusalsOf world declared)
                [ PrincipalUnresolved("sales", "stranger", "an active team") ]
                "the principal resolves to no team"

            // The same refusal through the composed startup service: the host
            // does not start.
            let services = ServiceCollection()
            services.AddSingleton<IDataSourceConfigStore>(world.Configs) |> ignore
            services.AddSingleton<IDataIngestor>(world.Ingestor) |> ignore
            services.AddSingleton<IJobScheduler>(world.Scheduler) |> ignore
            services.AddSingleton<IBlobStorage>(world.Blobs) |> ignore
            services.AddSingleton<IDataSource>(world.Csv) |> ignore
            services.AddSingleton<DeclaredPrincipalResolver>(world.Resolver) |> ignore
            let sp = services.BuildServiceProvider() :> IServiceProvider

            let service =
                DeclaredDelivery.withDeclaredDataSources declared DeclaredDelivery.DefaultWatchPoll sp

            let raised =
                try
                    service.StartAsync(CancellationToken.None).GetAwaiter().GetResult()
                    None
                with DeclaredDataSourcesRefused refusals ->
                    Some refusals

            Expect.equal
                raised
                (Some [ PrincipalUnresolved("sales", "stranger", "an active team") ])
                "the start is refused"

            Expect.equal (world.Deps.Ledger.ReadSeeded() |> Async.RunSynchronously) (Ok Set.empty) "nothing was seeded"

            Expect.stringContains
                (DeclaredDataSourcesRefused [ PrincipalUnresolved("sales", "stranger", "an active team") ]).Message
                "principal 'stranger' does not resolve"
                "the refusal names the source, the principal and the grant"
        }

        test "an unreadable watched location refuses startup" {
            let world0 = World()

            let unreadable =
                { new IBlobStorage with
                    member _.Upload(c, n, b) = world0.Blobs.Upload(c, n, b)
                    member _.Download(c, n) = world0.Blobs.Download(c, n)
                    member _.Delete(c, n) = world0.Blobs.Delete(c, n)
                    member _.List(_, _) = async { return raise (IOException "the container is offline") }
                    member _.Exists(c, n) = world0.Blobs.Exists(c, n)
                    member _.GetMetadata(c, n) = world0.Blobs.GetMetadata(c, n)
                    member _.DownloadRange(c, n, o, l) = world0.Blobs.DownloadRange(c, n, o, l)
                    member _.CanComposeFrom = false

                    member _.ComposeFrom(_, _, _) =
                        composeNotSupported "unreadable test storage"

                    member _.Erase(c, p, policy, dryRun) =
                        world0.Blobs.Erase(c, p, policy, dryRun)
                }

            let world = World(unreadable)
            let loader = world.TeamPrincipal "loader"

            match
                refusalsOf world {
                    Principals = [ loader ]
                    Sources = [
                        {
                            Source = csvSource "drops" [] None
                            Delivery = DataSourceDelivery.Watched("incoming", "loader")
                        }
                    ]
                }
            with
            | [ LocationUnreadable("drops", "incoming", reason) ] ->
                Expect.stringContains reason "offline" "the refusal carries the read failure"
            | refusals -> failtestf "expected the location refused, got %A" refusals

            Expect.isEmpty (liveJobs world (world.ScopeOf loader).ScopeId "drops") "no watch was scheduled"
        }

        test "every shape refusal is named before anything resolves" {
            let sql =
                { new IDataSource with
                    member _.Kind = "Sql"
                    member _.Connect _ = async { return Ok() }
                    member _.ListTables _ = async { return Ok [] }
                    member _.GetSchema(_, t) = async { return Ok { TableName = t; Columns = [] } }
                    member _.Query(_, _) = async { return Ok [||] }
                }

            let world = World()

            let deps = {
                world.Deps with
                    Connectors = [ world.Csv; sql ]
            }

            let p = world.TeamPrincipal "p"

            let source id kind scope tables delivery = {
                Source = {
                    csvSource id scope tables with
                        Kind = kind
                }
                Delivery = delivery
            }

            let refusals =
                match
                    DeclaredDelivery.seed (Some world.Resolver) deps {
                        Principals = [ p; p ]
                        Sources = [
                            source "bad id" "Csv" [] (Some [ "t" ]) (DataSourceDelivery.ScheduledPull("0 6 * * *", "p"))
                            source
                                "ghost"
                                "Csv"
                                []
                                (Some [ "t" ])
                                (DataSourceDelivery.ScheduledPull("0 6 * * *", "nobody"))
                            source "no-tables" "Csv" [] None (DataSourceDelivery.ScheduledPull("0 6 * * *", "p"))
                            source
                                "bad-cron"
                                "Csv"
                                []
                                (Some [ "t" ])
                                (DataSourceDelivery.ScheduledPull("whenever", "p"))
                            source
                                "unknown"
                                "Mongo"
                                []
                                (Some [ "t" ])
                                (DataSourceDelivery.ScheduledPull("0 6 * * *", "p"))
                            source "warehouse" "Sql" [] None (DataSourceDelivery.Watched("in", "p"))
                            source "located" "Csv" [ "container", "mine" ] None (DataSourceDelivery.Watched("in", "p"))
                            source "climbing" "Csv" [] None (DataSourceDelivery.Watched("in/../../x", "p"))
                            source "twice" "Csv" [] None DataSourceDelivery.Upload
                            source "twice" "Csv" [] None DataSourceDelivery.Upload
                        ]
                    }
                    |> Async.RunSynchronously
                with
                | Error refusals -> refusals
                | Ok report -> failtestf "seeding was not refused: %A" report

            let has (expected: DeclaredDeliveryRefusal -> bool) (what: string) =
                Expect.isTrue (refusals |> List.exists expected) $"refused: {what}"

            has
                (function
                | InvalidSourceId("bad id", _) -> true
                | _ -> false)
                "an id a storage path cannot hold"

            has ((=) (UndeclaredPrincipal("ghost", "nobody"))) "an undeclared principal"
            has ((=) (NoTablesToPull "no-tables")) "a pull with no tables"

            has
                (function
                | InvalidCadence("bad-cron", "whenever", _) -> true
                | _ -> false)
                "a cadence that is not cron"

            has ((=) (UnknownConnector("unknown", "Mongo"))) "a Kind no connector answers"
            has ((=) (NotWatchable("warehouse", "Sql"))) "a watch over a non-file connector"
            has ((=) (LocationInConfig("located", "container"))) "a watch whose config locates itself"

            has
                (function
                | InvalidLocation("climbing", _, _) -> true
                | _ -> false)
                "a location that climbs out of the scope container"

            has ((=) (DuplicateSourceId "twice")) "a duplicated source id"
            has ((=) (DuplicatePrincipal "p")) "a duplicated principal"

            Expect.equal (world.Deps.Ledger.ReadSeeded() |> Async.RunSynchronously) (Ok Set.empty) "nothing was seeded"
        }

        test "no declared-principal resolver refuses a delivery; a deployment declaring none starts" {
            let world = World()
            let p = world.TeamPrincipal "p"

            let declared = {
                Principals = [ p ]
                Sources = [
                    {
                        Source = csvSource "sales" [ "container", "exports" ] (Some [ "sales" ])
                        Delivery = DataSourceDelivery.ScheduledPull("0 6 * * *", "p")
                    }
                ]
            }

            Expect.equal
                (DeclaredDelivery.seed None world.Deps declared |> Async.RunSynchronously)
                (Error [ NoPrincipalResolver ])
                "nothing resolves a principal, so nothing runs"

            Expect.isOk
                (DeclaredDelivery.seed None world.Deps { Principals = []; Sources = [] }
                 |> Async.RunSynchronously)
                "a deployment declaring nothing starts"
        }

        test "re-seeding is idempotent, a changed cadence replaces the job, and a removed source is retired" {
            let world = World()
            let p = world.TeamPrincipal "p"
            let scope = world.ScopeOf p

            let declare cadence tables = {
                Principals = [ p ]
                Sources = [
                    {
                        Source = csvSource "sales" [ "container", "exports" ] (Some tables)
                        Delivery = DataSourceDelivery.ScheduledPull(cadence, "p")
                    }
                ]
            }

            seeded world (declare "0 6 * * *" [ "sales" ]) |> ignore
            let first = onlyJob world scope.ScopeId "sales"
            seeded world (declare "0 6 * * *" [ "sales" ]) |> ignore
            Expect.equal (onlyJob world scope.ScopeId "sales").JobId first.JobId "a re-deploy keeps the job"

            seeded world (declare "0 7 * * *" [ "sales" ]) |> ignore
            let moved = onlyJob world scope.ScopeId "sales"
            Expect.notEqual moved.JobId first.JobId "a new cadence is a new job"
            Expect.equal moved.Trigger (CronTrigger "0 7 * * *") "on the new cadence"

            seeded world (declare "0 7 * * *" [ "sales"; "returns" ]) |> ignore

            Expect.equal
                (liveJobs world scope.ScopeId "sales"
                 |> List.map (fun j -> j.Tags[DeclaredDelivery.JobKeyTag])
                 |> List.sort)
                [ "pull:returns"; "pull:sales" ]
                "one pull job per table"

            let report = seeded world { Principals = [ p ]; Sources = [] }
            Expect.equal report.Retired [ scope.ScopeId, "sales" ] "the removed source is retired"
            Expect.isEmpty (liveJobs world scope.ScopeId "sales") "its jobs are cancelled"

            Expect.isNone (world.Configs.Get(scope.ScopeId, "sales") |> Async.RunSynchronously) "its config is deleted"

            Expect.equal (world.Deps.Ledger.ReadSeeded() |> Async.RunSynchronously) (Ok Set.empty) "the ledger is empty"
        }

        test "a source re-declared as upload keeps its config and stops its delivery" {
            let world = World()
            let p = world.TeamPrincipal "p"
            let scope = world.ScopeOf p
            let source = csvSource "sales" [ "container", "exports" ] (Some [ "sales" ])

            seeded world {
                Principals = [ p ]
                Sources = [
                    {
                        Source = source
                        Delivery = DataSourceDelivery.ScheduledPull("0 6 * * *", "p")
                    }
                ]
            }
            |> ignore

            let report =
                seeded world {
                    Principals = []
                    Sources = [
                        {
                            Source = source
                            Delivery = DataSourceDelivery.Upload
                        }
                    ]
                }

            Expect.isEmpty report.Retired "an upload source is not retired"
            Expect.isEmpty (liveJobs world scope.ScopeId "sales") "its pull is cancelled"

            Expect.isSome
                (world.Configs.Get(scope.ScopeId, "sales") |> Async.RunSynchronously)
                "its config stays, for uploads"

            let retired = seeded world { Principals = []; Sources = [] }
            Expect.equal retired.Retired [ scope.ScopeId, "sales" ] "removing it altogether retires it"
        }

        test "the delivery tier builds no resolved scope: no mint spelling and no resolver construction in its source" {
            let spellings = [
                "ResolvedScope." + "ofCarried"
                "ResolvedScope." + "ofStorageScope"
                "ScopeResolution." + "ofStorageScope"
                "ScopeResolution." + "remember"
                "DeclaredPrincipalResolver" + "("
            ]

            let found (text: string) = spellings |> List.filter text.Contains

            Expect.equal
                (found ("x " + String.concat "\n" spellings) |> List.length)
                (List.length spellings)
                "every spelling is caught (go-red)"

            let path =
                Path.Combine(repoRoot (), "src", "DataSources", "Common", "DeclaredDelivery.fs")

            Expect.isTrue (File.Exists path) "the delivery source exists"
            Expect.isEmpty (found (File.ReadAllText path)) "the delivery tier builds no resolved scope"
        }
    ]
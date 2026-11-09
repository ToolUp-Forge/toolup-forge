// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DeliveryTriggerTests

// The fact tier's once-per-delivery narrative trigger and its publication
// principal binding, each through the real writer and the real scope reads:
//
//   1. A delivery fires a run once over its newest period, however many tables
//      it carries, and fires when that period's commit brought it or changed it.
//   2. A restatement of an earlier period, and a re-delivery that changed
//      nothing, fire nothing.
//   3. A run with no options keeps the per-commit trigger.
//   4. Every refusal on the path is logged at Warning with the run key and the
//      table, and none reaches the commit.
//   5. The unit is the platform's delivery where one is in progress, so a job
//      attempt fires under the scope it carried.
//   6. A publication's principal resolves from a typed binding, a principal
//      that cannot read the scope is a named refusal, and binding one composes the
//      narrative publication read for the disclosure reach finding.

open System
open System.Collections.Concurrent
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Infra.Metrics
open ToolUp.Platform.Config
open ToolUp.Platform.Scope
open ToolUp.Platform.Scope.StorageScopeResolver
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose

// ─── Fixtures ─────────────────────────────────────────────────────────

let private metric (id: string) : MetricDefinition = {
    Id = id
    Name = id
    Unit = "count"
    Dimensionality = "count"
    Direction = HigherIsBetter
    DisplayFormat = ""
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = None
    Context = None
}

let private registry: IMetricRegistry =
    MetricRegistry.build [
        {
            MetricRegistration.Module = "sales"
            Definition = metric "revenue"
        }
    ] [
        {
            SubjectRegistration.Module = "sales"
            Definition = {
                Id = "products"
                Name = "Products"
                Levels = [ "sku" ]
                Calendar = None
            }
        }
    ]

let private table (id: string) : FactTableDefinition = {
    Id = id
    SchemaVersion = 1
    Hierarchy = "products"
    Level = "sku"
    Columns = [ FactTableDefinition.column "revenue" FactTableValueShape.Scalar ]
    PeriodGrain = FactTablePeriodGrain.Day
    ProducingOperation = "sales-rollup"
    RefreshCadence = TimeSpan.FromDays 1.0
    HistoryMode = FactTableHistoryMode.AppendByRun
    Disclosure = FactTableDisclosure.Surfaceable
    Requirement = FactTableRequirement.Optional
}

[<Literal>]
let private Orders = "orders"

[<Literal>]
let private Lines = "lines"

[<Literal>]
let private Unwatched = "unwatched"

[<Literal>]
let private Daily = "daily-report"

let private tables: IFactTableRegistry =
    FactTableRegistry.build
        ([ Orders; Lines; Unwatched ]
         |> List.map (fun id -> {
             FactTableRegistration.Module = "sales"
             Definition = table id
         }))
        [ BindAllFactTables DefaultFactTableWriter.Destination ]

let private runsFor (tableId: string) =
    match tableId with
    | Orders
    | Lines -> [ Daily ]
    | _ -> []

let private day (n: int) : TemporalExtent = {
    From = DateTime(2026, 9, n, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 9, n + 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some(sprintf "2026-09-%02d" n)
}

let private row (n: int) (sku: string) (revenue: decimal) : FactTableRow = {
    Subject = [ sku ]
    Period = day n
    Values = Map.ofList [ "revenue", Scalar revenue ]
}

let private delivered (n: int) (input: char) =
    FactTableRunOptions.none
    |> FactTableRunOptions.withInputs [ "sha256:" + String(input, 64) ]
    |> FactTableRunOptions.forSlice (day n)

let private storage (scopeId: string) : StorageScope = {
    ScopeId = scopeId
    Container = "container-" + scopeId
    Persist = true
}

/// A logger that keeps its warnings.
type DeliveryTriggerLogger() =
    let warnings = ConcurrentQueue<string>()
    member _.Warnings = warnings |> List.ofSeq

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn message = warnings.Enqueue message
        member _.Error(_, _) = ()

/// What the scheduler is told to do with a job.
type SchedulerBehaviour =
    | Accepts
    | RefusesSchedule
    | RefusesTrigger
    | Throws

/// A scheduler that honours a job's idempotency key, as the platform's does,
/// and keeps the jobs it fired.
type DeliveryTriggerScheduler(behaviour: SchedulerBehaviour) =
    let keys = ConcurrentDictionary<string, JobId>()
    let jobs = ConcurrentDictionary<JobId, ResolvedScope * JobRegistration>()
    let fired = ConcurrentQueue<ResolvedScope * JobRegistration>()

    member _.Fired = fired |> List.ofSeq

    interface IJobScheduler with
        member _.RegisterHandler(_, _) = ()
        member _.RegisterHandlerAsync(_, _) = async { return Ok() }

        member _.Schedule(scope: ResolvedScope, registration: JobRegistration) = async {
            match behaviour with
            | RefusesSchedule -> return Error(ScheduleError.InvalidCron("* *", "refused for the test"))
            | Throws -> return raise (InvalidOperationException "the scheduler is down")
            | Accepts
            | RefusesTrigger ->
                let id = Guid.NewGuid()
                let key = registration.Idempotency |> Option.map _.Key |> Option.get

                if keys.TryAdd(key, id) then
                    jobs[id] <- (scope, registration)
                    return Ok(Scheduled id)
                else
                    return Ok(Coalesced keys[key])
        }

        member _.Schedule(_: JobRegistration) = async { return failwith "only the resolved overload is used" }
        member _.Cancel(_, _) = async { return () }
        member _.Disable(_, _) = async { return () }
        member _.Enable(_, _) = async { return () }
        member _.Get(_, _) = async { return None }
        member _.ListJobs _ = async { return [] }
        member _.GetRecentRuns(_, _, _) = async { return [] }

        member _.TriggerOnce(_, jobId, _) = async {
            match behaviour with
            | RefusesTrigger -> return Error(TriggerError.DispatchRefused "refused for the test")
            | _ ->
                fired.Enqueue jobs[jobId]
                return Ok()
        }

        member _.NotifyEventWritten(_, _, _) = async { return () }

/// A response whose completion the test runs.
type private CompletableResponse() =
    inherit HttpResponseFeature()
    let callbacks = ResizeArray<(obj -> Threading.Tasks.Task) * obj>()

    override _.OnCompleted(callback, state) =
        callbacks.Add(((fun s -> callback.Invoke s), state))

    member _.Complete() = async {
        for callback, state in callbacks do
            do! callback state |> Async.AwaitTask
    }

/// The real writer, decorated, over one scheduler and a settable request.
type DeliveryTriggerWorld(behaviour: SchedulerBehaviour option) =
    let storageBlobs = InMemoryBlobStorage() :> IBlobStorage
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let facts = BlobFactStore.create storageBlobs events
    let logger = DeliveryTriggerLogger()
    let scheduler = behaviour |> Option.map DeliveryTriggerScheduler
    let mutable current: HttpContext = null

    let accessor =
        { new IHttpContextAccessor with
            member _.HttpContext
                with get () = current
                and set _ = ()
        }

    let writer =
        DeliveryTrigger.decorate
            runsFor
            (fun () -> scheduler |> Option.map (fun s -> s :> IJobScheduler))
            (fun () -> Some accessor)
            logger
            (DefaultFactTableWriter.create facts storageBlobs events tables (Some registry))

    new() = DeliveryTriggerWorld(Some Accepts)

    member _.Writer = writer
    member _.Warnings = logger.Warnings
    member _.Fired = scheduler |> Option.map _.Fired |> Option.defaultValue []
    member _.Serve(ctx: HttpContext) = current <- ctx

    /// Publish `rows` to a table in one run, and answer the commit.
    member _.Publish(scope: ResolvedScope, tableId: string, options: FactTableRunOptions option, rows) = async {
        let! opened =
            match options with
            | Some o -> writer.OpenRun(scope, tableId, options = o)
            | None -> writer.OpenRun(scope, tableId)

        let run =
            match opened with
            | Ok run -> run
            | Error e -> failtestf "open: %s" (FactTableWriteError.describe e)

        match! writer.WriteRows(scope, run.RunId, rows) with
        | Ok _ -> ()
        | Error e -> failtestf "write: %s" (FactTableWriteError.describe e)

        match! writer.Commit(scope, run.RunId) with
        | Ok commit -> return commit
        | Error e -> return failtestf "commit: %s" (FactTableWriteError.describe e)
    }

/// One request: `body` runs under the request's resolved scope, then the
/// response completes.
let private onRequest (world: DeliveryTriggerWorld) (scopeId: string) (body: ResolvedScope -> Async<unit>) = async {
    let ctx = DefaultHttpContext()
    let response = CompletableResponse()
    ctx.Features.Set<IHttpResponseFeature>(response)
    let resolved = ScopeResolution.remember ctx (storage scopeId)
    world.Serve ctx
    do! body resolved
    do! response.Complete()
    world.Serve null
}

let private scopeId = "team-trading"

let private publishDay (world: DeliveryTriggerWorld) (n: int) (input: char) (revenue: decimal) =
    onRequest world scopeId (fun scope -> async {
        let! _ = world.Publish(scope, Orders, Some(delivered n input), [ row n "a1" revenue ])
        return ()
    })

let private invalidated (registration: JobRegistration) =
    (GroundedNarrativeTrigger.tryParsePayload registration.Payload).Value.InvalidatedInputs

let private periodOf (n: int) =
    let extent = day n
    sprintf "period:%s..%s" (extent.From.ToString "o") (extent.To.ToString "o")

// ─── The firing rule ──────────────────────────────────────────────────

let private firingTests =
    testList "the firing rule" [

        testCaseAsync "a new period fires once for two tables of one delivery, under the request's scope"
        <| async {
            let world = DeliveryTriggerWorld()

            do!
                onRequest world scopeId (fun scope -> async {
                    let! _ = world.Publish(scope, Orders, Some(delivered 1 'a'), [ row 1 "a1" 10m ])
                    let! _ = world.Publish(scope, Lines, Some(delivered 1 'a'), [ row 1 "a1" 3m ])
                    Expect.isEmpty world.Fired "nothing fires until the request completes"
                })

            match world.Fired with
            | [ scope, registration ] ->
                Expect.equal scope.ScopeId scopeId "the request's resolved scope"
                Expect.isFalse scope.IsAnonymous "resolved, not anonymous"
                Expect.contains (invalidated registration) (periodOf 1) "over the newest period"
            | fired -> failtestf "expected one firing, got %d" fired.Length
        }

        testCaseAsync "a table no run names fires nothing and is read no further"
        <| async {
            let world = DeliveryTriggerWorld()

            do!
                onRequest world scopeId (fun scope -> async {
                    let! _ = world.Publish(scope, Unwatched, Some(delivered 1 'a'), [ row 1 "a1" 10m ])
                    return ()
                })

            Expect.isEmpty world.Fired "not watched"
            Expect.isEmpty world.Warnings "and nothing to say"
        }

        testCaseAsync "a re-delivery with no change fires nothing"
        <| async {
            let world = DeliveryTriggerWorld()
            do! publishDay world 1 'a' 10m
            do! publishDay world 1 'b' 10m
            Expect.hasLength world.Fired 1 "only the delivery that brought the period"
            Expect.isEmpty world.Warnings "an unchanged re-delivery is not a refusal"
        }

        testCaseAsync "a re-delivery with one changed row fires once, so the report citing it regenerates"
        <| async {
            let world = DeliveryTriggerWorld()
            do! publishDay world 1 'a' 10m
            do! publishDay world 1 'b' 11m

            match world.Fired with
            | [ _; _, correction ] ->
                Expect.contains (invalidated correction) (periodOf 1) "over the corrected period"
                Expect.contains (invalidated correction) ("delivery:sha256:" + String('b', 64)) "keyed on its delivery"
            | fired -> failtestf "expected two firings, got %d" fired.Length
        }

        testCaseAsync "a correction that declares no inputs is told apart by its content, never coalesced away"
        <| async {
            let world = DeliveryTriggerWorld()
            let sliceOnly = FactTableRunOptions.none |> FactTableRunOptions.forSlice (day 1)

            for revenue in [ 10m; 11m ] do
                do!
                    onRequest world scopeId (fun scope -> async {
                        let! _ = world.Publish(scope, Orders, Some sliceOnly, [ row 1 "a1" revenue ])
                        return ()
                    })

            Expect.hasLength world.Fired 2 "the correction started its own job"
        }

        testCaseAsync
            "a restatement of an earlier period fires nothing, alone or beside a newer period that did not move"
        <| async {
            let world = DeliveryTriggerWorld()
            do! publishDay world 1 'a' 10m
            do! publishDay world 2 'b' 20m
            Expect.hasLength world.Fired 2 "two new periods"

            do! publishDay world 1 'c' 99m
            Expect.hasLength world.Fired 2 "the earlier period alone"

            do!
                onRequest world scopeId (fun scope -> async {
                    let! _ = world.Publish(scope, Orders, Some(delivered 1 'd'), [ row 1 "a1" 98m ])
                    let! _ = world.Publish(scope, Orders, Some(delivered 2 'd'), [ row 2 "a1" 20m ])
                    return ()
                })

            Expect.hasLength world.Fired 2 "and beside the newest period, unchanged"
        }

        testCaseAsync "two inputs in one request are two deliveries"
        <| async {
            let world = DeliveryTriggerWorld()

            do!
                onRequest world scopeId (fun scope -> async {
                    let! _ = world.Publish(scope, Orders, Some(delivered 1 'a'), [ row 1 "a1" 10m ])
                    let! _ = world.Publish(scope, Lines, Some(delivered 1 'b'), [ row 1 "a1" 3m ])
                    return ()
                })

            Expect.hasLength world.Fired 2 "one per delivery"
        }

        testCaseAsync "a run with no options keeps the per-commit trigger"
        <| async {
            let world = DeliveryTriggerWorld()

            do!
                onRequest world scopeId (fun scope -> async {
                    let! first = world.Publish(scope, Orders, None, [ row 1 "a1" 10m ])
                    Expect.hasLength world.Fired 1 "as it commits, not at completion"
                    let! second = world.Publish(scope, Orders, None, [ row 1 "a1" 11m ])
                    Expect.notEqual first.Watermark.Sequence second.Watermark.Sequence "two commits"
                    Expect.hasLength world.Fired 2 "each its own firing"
                })

            match world.Fired with
            | (_, first) :: _ ->
                Expect.equal (invalidated first) [ Orders + "@1" |> sprintf "fact-table:%s" ] "its commit"
            | [] -> failtest "nothing fired"
        }
    ]

// ─── Refusals ─────────────────────────────────────────────────────────

let private refusalTests =
    let named (world: DeliveryTriggerWorld) (reason: string) =
        Expect.exists
            world.Warnings
            (fun w -> w.Contains Daily && w.Contains Orders && w.Contains reason)
            (sprintf "a warning names the run, the table and '%s'; got %A" reason world.Warnings)

    testList "refusals" [

        testCaseAsync "an off-request commit fires nothing and logs why"
        <| async {
            let world = DeliveryTriggerWorld()
            let! commit = world.Publish(TestScope.resolved scopeId, Orders, Some(delivered 1 'a'), [ row 1 "a1" 10m ])
            Expect.equal commit.RowCount 1 "the commit succeeded"
            Expect.isEmpty world.Fired "nothing fired"
            named world "off the request path"
        }

        testCaseAsync "an off-request commit of a run with no options fires nothing and logs why"
        <| async {
            let world = DeliveryTriggerWorld()
            let! commit = world.Publish(TestScope.resolved scopeId, Orders, None, [ row 1 "a1" 10m ])
            Expect.equal commit.RowCount 1 "the commit succeeded"
            Expect.isEmpty world.Fired "nothing fired"
            named world "off the request path"
        }

        testCaseAsync "a request that resolved another scope fires nothing and logs why"
        <| async {
            let world = DeliveryTriggerWorld()

            do!
                onRequest world "team-other" (fun _ -> async {
                    let! _ =
                        world.Publish(TestScope.resolved scopeId, Orders, Some(delivered 1 'a'), [ row 1 "a1" 10m ])

                    return ()
                })

            Expect.isEmpty world.Fired "nothing fired"
            named world "team-other"
        }

        testCaseAsync "a scheduler refusal is logged and the commit succeeds"
        <| async {
            for behaviour, reason in
                [
                    RefusesSchedule, "refused the job"
                    RefusesTrigger, "trigger refused"
                    Throws, "the scheduler is down"
                ] do
                let world = DeliveryTriggerWorld(Some behaviour)

                do!
                    onRequest world scopeId (fun scope -> async {
                        let! commit = world.Publish(scope, Orders, Some(delivered 1 'a'), [ row 1 "a1" 10m ])
                        Expect.equal commit.RowCount 1 "the commit succeeded"
                    })

                Expect.isEmpty world.Fired "nothing fired"
                named world reason
        }

        testCaseAsync "no scheduler composed is logged and the commit succeeds"
        <| async {
            let world = DeliveryTriggerWorld(None)
            do! publishDay world 1 'a' 10m
            named world "no IJobScheduler"
        }
    ]

// ─── The unit of work ─────────────────────────────────────────────────

let private jobContext (scope: ResolvedScope) : JobContext = {
    JobId = Guid.NewGuid()
    ScopeId = scope.ScopeId
    AccessContext = AccessContext.unrestricted (AuthenticatedUser "worker")
    Attempt = 1
    Trigger = Trigger.Manual
    Scope = scope
    TriggerSource = ScheduledManually(UserId "worker")
    ScheduledAt = DateTime.UtcNow
    RunningAt = DateTime.UtcNow
    Payload = ""
    DeadLetterDestination = None
}

let private unitTests =
    let attempt (world: DeliveryTriggerWorld) (jobScope: ResolvedScope) (commitScope: ResolvedScope) =
        let handler =
            { new IJobHandler with
                member _.Execute _ = async {
                    let! _ = world.Publish(commitScope, Orders, Some(delivered 1 'a'), [ row 1 "a1" 10m ])
                    let! _ = world.Publish(commitScope, Lines, Some(delivered 1 'a'), [ row 1 "a1" 3m ])
                    Expect.isEmpty world.Fired "nothing fires until the attempt returns"
                    return Success
                }
            }

        (DeliveryCompleted.aroundJob "pull" handler).Execute(jobContext jobScope)

    testList "the unit of work" [

        testCaseAsync "a job attempt's delivery fires once it returns, under the scope the job carried"
        <| async {
            let world = DeliveryTriggerWorld()
            let scope = TestScope.resolved scopeId
            let! _ = attempt world scope scope

            match world.Fired with
            | [ fired, _ ] -> Expect.equal fired.ScopeId scopeId "the job's scope"
            | fired -> failtestf "expected one firing, got %d" fired.Length
        }

        testCaseAsync "a job attempt that carried no scope fires nothing and logs why"
        <| async {
            let world = DeliveryTriggerWorld()
            let! _ = attempt world ResolvedScope.anonymous (TestScope.resolved scopeId)
            Expect.isEmpty world.Fired "nothing fired"

            Expect.exists
                world.Warnings
                (fun w -> w.Contains Daily && w.Contains Orders && w.Contains "resolved no scope")
                "the log says why"
        }

        test "a seam's state is one per delivery and per key, and absent off a delivery" {
            let key = obj ()
            let logger = DeliveryTriggerLogger()
            let create () = obj (), (fun _ -> async.Return())

            Expect.isNone (DeliveryCompleted.tryDeliveryState key logger create) "no delivery in progress"

            let mutable completions = 0

            let handler =
                { new IJobHandler with
                    member _.Execute _ = async {
                        let counted () =
                            obj (),
                            (fun _ -> async {
                                completions <- completions + 1
                                return failwith "the work failed"
                            })

                        let first = DeliveryCompleted.tryDeliveryState key logger counted
                        let second = DeliveryCompleted.tryDeliveryState key logger counted
                        Expect.isSome first "state on the delivery in progress"
                        Expect.isTrue (obj.ReferenceEquals(first.Value, second.Value)) "the same state"
                        return Success
                    }
                }

            let result =
                (DeliveryCompleted.aroundJob "pull" handler).Execute(jobContext (TestScope.resolved scopeId))
                |> Async.RunSynchronously

            Expect.equal result Success "a failing completion never reaches the job"
            Expect.equal completions 1 "the work ran once"
            Expect.exists logger.Warnings (fun w -> w.Contains "the work failed") "and its failure is logged"
        }
    ]

// ─── Composition ──────────────────────────────────────────────────────

let private factApp () : ServerApp =
    {
        ServerApp.empty with
            Config = {
                ServerConfig.defaults with
                    Surfaces = [ SurfaceProfile.individual ]
                    Stores = {
                        ServerConfig.defaults.Stores with
                            FactStore = FeatureMode.Enabled
                    }
            }
    }
    |> FactsCompose.withFactStore

let private composed (app: ServerApp) (services: IServiceCollection) : IServiceCollection =
    match ServerApp.serviceConfig app with
    | Some configure -> configure services
    | None -> services

let private compositionTests =
    testList "composition" [

        testCaseAsync
            "composed after withFactTableWriter, the resolved writer fires, and the sweep still passes through"
        <| async {
            let scheduler = DeliveryTriggerScheduler Accepts
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let services =
                ServiceCollection()
                    .AddSingleton<IBlobStorage>(blobs)
                    .AddSingleton<IEventStore>(events)
                    .AddSingleton<IFactStore>(BlobFactStore.create blobs events)
                    .AddSingleton<ILogger>(DeliveryTriggerLogger())
                    .AddSingleton<IJobScheduler>(scheduler)
                    .AddSingleton<IFactTableRegistry>(tables)
                    .AddSingleton<IMetricRegistry>(registry)
                    .AddSingleton<IHttpContextAccessor>(HttpContextAccessor())

            let app =
                factApp ()
                |> FactsCompose.withFactTableWriter
                |> FactsCompose.withDeliveryTrigger runsFor

            use provider = (composed app services).BuildServiceProvider()
            let writer = provider.GetRequiredService<IFactTableWriter>()
            let ctx = DefaultHttpContext()
            let response = CompletableResponse()
            ctx.Features.Set<IHttpResponseFeature>(response)
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext <- ctx
            let scope = ScopeResolution.remember ctx (storage scopeId)

            let! opened = writer.OpenRun(scope, Orders, options = delivered 1 'a')

            let run =
                match opened with
                | Ok run -> run
                | Error e -> failtestf "open: %s" (FactTableWriteError.describe e)

            let! _ = writer.WriteRows(scope, run.RunId, [ row 1 "a1" 10m ])
            let! committed = writer.Commit(scope, run.RunId)
            Expect.isOk committed "committed"
            do! response.Complete()
            Expect.hasLength scheduler.Fired 1 "the composed writer fired"
            Expect.isTrue (writer :? IFactTableOrphanSweep) "the decorated writer still sweeps"
        }

        test "composed before withFactTableWriter, the composition is refused naming the order" {
            let app = factApp () |> FactsCompose.withDeliveryTrigger runsFor

            Expect.throwsC (fun () -> composed app (ServiceCollection()) |> ignore) (fun ex ->
                Expect.stringContains ex.Message "FactsCompose.withDeliveryTrigger" "refused, naming the builder")
        }

        test "with the fact store disabled the app is unchanged" {
            let app = ServerApp.empty |> FactsCompose.withDeliveryTrigger runsFor
            Expect.isEmpty app.Contributions "nothing composed"
        }
    ]

// ─── The publication principal ────────────────────────────────────────

let private publicationTests =
    let publishing = "team-publishing"
    let setting = "TEST_PUBLICATION_PRINCIPAL_1253"

    let resolvingTo (scope: string) =
        DeclaredPrincipalResolver(fun _ -> async.Return(Ok(storage scope)))

    let provider (app: ServerApp) (resolver: DeclaredPrincipalResolver option) =
        let logger = DeliveryTriggerLogger()

        let services =
            ServiceCollection().AddSingleton<ILogger>(logger) :> IServiceCollection

        resolver
        |> Option.iter (fun r -> services.AddSingleton<DeclaredPrincipalResolver>(r) |> ignore)

        (composed app services).BuildServiceProvider(), logger

    let withSetting (value: string option) (body: unit -> unit) =
        let prior = Environment.GetEnvironmentVariable setting

        try
            Environment.SetEnvironmentVariable(setting, Option.toObj value)
            body ()
        finally
            Environment.SetEnvironmentVariable(setting, prior)

    let bound =
        ServerApp.empty
        |> FactPublication.withPublicationPrincipals [ publishing, setting ]

    let refusedWith (reason: string) (resolver: DeclaredPrincipalResolver option) (scope: string) =
        let sp, logger = provider bound resolver

        use _ = sp
        let answered = FactPublication.declaredPrincipal scope sp |> Async.RunSynchronously
        Expect.isNone answered "refused"

        Expect.exists
            logger.Warnings
            (fun w -> w.Contains scope && w.Contains reason)
            (sprintf "the refusal names '%s'; got %A" reason logger.Warnings)

    let reach () =
        let config =
            DisclosureTaintConfig.ofLists [
                {
                    PolicyRef = "trade-figures"
                    Mode = Plain
                    PermitSurfaces = DisclosureTaintConfig.allSurfaces
                    ContributorScope = None
                }
            ] []

        fun (app: ServerApp) ->
            let services = ServiceCollection() :> IServiceCollection
            services.AddSingleton<DisclosureTaintConfig>(config) |> ignore

            composed app services
            |> Seq.choose (fun d ->
                match d.ImplementationInstance with
                | :? ConfigValidation.IConfigValidator as v when v.Name = "fact-disclosure-reach" -> Some v
                | _ -> None)
            |> Seq.exactlyOne
            |> fun v -> v.Validate() |> Async.RunSynchronously

    testList "the publication principal" [

        test "a bound principal resolves in the publishing scope, its setting read and trimmed" {
            withSetting (Some "  reader-1 ") (fun () ->
                let sp, logger = provider bound (Some(resolvingTo publishing))
                use _ = sp

                match FactPublication.declaredPrincipal publishing sp |> Async.RunSynchronously with
                | Some scope -> Expect.equal scope.ScopeId publishing "the publishing scope"
                | None -> failtestf "refused: %A" logger.Warnings)
        }

        test "the setting's value is the identity resolved" {
            let seen = ConcurrentQueue<string>()

            let resolver =
                DeclaredPrincipalResolver(fun request ->
                    request.User |> Option.iter (fun u -> seen.Enqueue u.UserId.Value)
                    async.Return(Ok(storage publishing)))

            withSetting (Some "  reader-1 ") (fun () ->
                let sp, _ = provider bound (Some resolver)
                use _ = sp

                FactPublication.declaredPrincipal publishing sp
                |> Async.RunSynchronously
                |> ignore)

            Expect.equal (List.ofSeq seen) [ "reader-1" ] "resolved as the bound user"
        }

        test "a principal resolving to another shard is a named refusal" {
            withSetting (Some "reader-1") (fun () ->
                refusedWith "resolves to scope 'team-other'" (Some(resolvingTo "team-other")) publishing)
        }

        test "a principal the resolver refuses is a named refusal" {
            let resolver = DeclaredPrincipalResolver(fun _ -> async.Return(Error NoActiveTeam))

            withSetting (Some "reader-1") (fun () -> refusedWith "lacks an active team" (Some resolver) publishing)
        }

        test "an unbound scope, an unset setting and an absent resolver are named refusals" {
            withSetting (Some "reader-1") (fun () ->
                refusedWith "no principal is bound" (Some(resolvingTo "elsewhere")) "elsewhere"
                refusedWith "no DeclaredPrincipalResolver" None publishing)

            withSetting None (fun () -> refusedWith setting (Some(resolvingTo publishing)) publishing)
        }

        test "a scope bound to two settings or a blank name refuses the composition" {
            for bindings in [ [ publishing, "A"; publishing, "B" ]; [ "", "A" ]; [ publishing, " " ] ] do
                Expect.throwsC
                    (fun () -> ServerApp.empty |> FactPublication.withPublicationPrincipals bindings |> ignore)
                    (fun ex ->
                        Expect.stringContains
                            ex.Message
                            "FactPublication.withPublicationPrincipals"
                            "refused, naming the builder")
        }

        test "a later call replaces an earlier binding" {
            let app =
                bound |> FactPublication.withPublicationPrincipals [ "team-later", setting ]

            withSetting (Some "reader-1") (fun () ->
                let sp, _ = provider app (Some(resolvingTo "team-later"))
                use _ = sp

                Expect.isSome
                    (FactPublication.declaredPrincipal "team-later" sp |> Async.RunSynchronously)
                    "the later binding"

                Expect.isNone
                    (FactPublication.declaredPrincipal publishing sp |> Async.RunSynchronously)
                    "and the earlier one is gone")
        }

        test "an all-Plain configuration with the publication read composed reports, without it says nothing" {
            let prior =
                Environment.GetEnvironmentVariable ConfigKeys.Names.acceptPlainDisclosure

            try
                Environment.SetEnvironmentVariable(ConfigKeys.Names.acceptPlainDisclosure, null)

                match reach () (factApp () |> FactPublication.withPublicationPrincipals [ publishing, setting ]) with
                | ConfigValidation.ValidationResult.Warning message ->
                    Expect.stringContains message DisclosureReachFinding.Code "the finding's code"
                    Expect.stringContains message FactPublication.NarrativePublicationRead "names the read"
                | other -> failtestf "expected the finding, got %A" other

                Expect.equal
                    (reach () (factApp ()))
                    ConfigValidation.ValidationResult.Ok
                    "no surface composed, nothing to say"
            finally
                Environment.SetEnvironmentVariable(ConfigKeys.Names.acceptPlainDisclosure, prior)
        }
    ]

[<Tests>]
let tests =
    testList "Phase 1253 the delivery trigger and the publication principal as combinators" [
        firingTests
        refusalTests
        unitTests
        compositionTests
        publicationTests
    ]
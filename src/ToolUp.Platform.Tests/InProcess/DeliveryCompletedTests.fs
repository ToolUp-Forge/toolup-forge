// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DeliveryCompletedTests

// ─── Phase 1042 — a module registers a delivery-completed callback ───
//
// A delivery is the ingestion one request, or one ingestion job attempt,
// performs. A hook registered on the compose surface is called ONCE per
// completed delivery, with every table's run, under the scope the platform
// resolved for it. The claims, each pinned through the real seams — the
// `ServerApp.withDeliveryCompleted` registration and pre-middleware, the
// ingestor and job handler `ComposeJobs.registerDataIngestion` builds:
//
//   1. A request that ingests three tables fires once, after its response,
//      with three runs in settling order, under the request's scope.
//   2. A table that fails is reported as `Failed` beside the ones that
//      committed — including a refusal before any run was recorded.
//   3. A scheduled pull (the platform's ingestion job, carried scope) fires
//      once its attempt returns; a job that carried no scope fires nothing.
//   4. A throwing callback, synchronous or asynchronous, leaves the response
//      untouched, is logged and audited, and does not stop the next one.
//   5. `TriggerRefresh` schedules under the request's resolved scope, so a
//      queued refresh's delivery reaches the hook end to end.
//   6. Registering no hook leaves the ingestor exactly as it was (GP 13),
//      and the new files mint no scope (Phase 797).
//   7. A completed delivery and a delivery trigger's firing are different
//      events: each happens where the other does not.

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Reflection
open System.Text
open System.Threading.Tasks
open Expecto
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Jobs.DeliveryCompleted
open ToolUp.Platform.Secrets
open ToolUp.Platform.Scope.StorageScopeResolver
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose

// ─── Fixtures ─────────────────────────────────────────────────────────

let private sourceId = "trading"
let private tables = [ "orders"; "lines"; "returns" ]

let private storage (scopeId: string) : StorageScope = {
    ScopeId = scopeId
    Container = "container-" + scopeId
    Persist = true
}

let private newScopeId () = "team-" + Guid.NewGuid().ToString("N")

/// A logger that keeps its warnings.
type private RecordingLogger() =
    let warnings = ConcurrentQueue<string>()
    member _.Warnings = warnings |> List.ofSeq

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn message = warnings.Enqueue message
        member _.Error(_, _) = ()

let private noSecrets =
    { new ISecretStore with
        member _.GetSecret(_, _) = async { return None }
        member _.SetSecret(_, _, _) = async { return Ok() }
        member _.DeleteSecret(_, _) = async { return Ok() }
        member _.ListKeys _ = async { return [] }
    }

/// A scheduler that holds the handlers registered with it and runs a job
/// on `TriggerOnce` the way the in-process one does about scope: a job
/// scheduled through the typed overload runs under that scope, one
/// scheduled through the string overload runs anonymous.
type private RecordingScheduler() =
    let handlers = ConcurrentDictionary<string, IJobHandler>()
    let jobs = ConcurrentDictionary<JobId, JobRegistration * ResolvedScope option>()
    let typed = ConcurrentQueue<ResolvedScope * JobRegistration>()
    let untyped = ConcurrentQueue<JobRegistration>()
    let results = ConcurrentQueue<JobResult>()

    member _.Typed = typed |> List.ofSeq
    member _.Untyped = untyped |> List.ofSeq
    member _.Results = results |> List.ofSeq

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers[name] <- handler

        member _.RegisterHandlerAsync(name, handler) = async {
            handlers[name] <- handler
            return Ok()
        }

        member _.Schedule(scope: ResolvedScope, registration: JobRegistration) = async {
            let id = Guid.NewGuid()
            typed.Enqueue(scope, registration)
            jobs[id] <- (registration, Some scope)
            return Ok(Scheduled id)
        }

        member _.Schedule(registration: JobRegistration) = async {
            let id = Guid.NewGuid()
            untyped.Enqueue registration
            jobs[id] <- (registration, None)
            return Ok(Scheduled id)
        }

        member _.Cancel(_, _) = async { return Ok() }
        member _.Disable(_, _) = async { return Ok() }
        member _.Enable(_, _) = async { return Ok() }
        member _.Get(_, _) = async { return None }
        member _.ListJobs _ = async { return [] }
        member _.GetRecentRuns(_, _, _) = async { return [] }

        member _.TriggerOnce(_, jobId, byUserId) = async {
            match jobs.TryGetValue jobId with
            | false, _ -> return Error(TriggerError.DispatchRefused "unknown job")
            | true, (registration, scope) ->
                let ctx: JobContext = {
                    JobId = jobId
                    ScopeId = registration.ScopeId
                    AccessContext = AccessContext.unrestricted (AuthenticatedUser byUserId.Value)
                    Attempt = 1
                    Trigger = registration.Trigger
                    Scope = scope |> Option.defaultValue ResolvedScope.anonymous
                    TriggerSource = ScheduledManually byUserId
                    ScheduledAt = DateTime.UtcNow
                    RunningAt = DateTime.UtcNow
                    Payload = registration.Payload
                    DeadLetterDestination = None
                }

                let! result = handlers[registration.Handler].Execute ctx
                results.Enqueue result
                return Ok()
        }

        member _.NotifyEventWritten(_, _, _) = async { return () }

/// What a hook saw.
type private Seen() =
    let deliveries = ConcurrentQueue<Delivery>()

    let first =
        TaskCompletionSource<Delivery>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.All = deliveries |> List.ofSeq

    member _.Callback: DeliveryCompletedCallback =
        fun _ delivery -> async {
            deliveries.Enqueue delivery
            first.TrySetResult delivery |> ignore
        }

    /// The first delivery, or a failure after five seconds.
    member _.First = async {
        let! winner =
            Task.WhenAny(first.Task, Task.Delay(TimeSpan.FromSeconds 5.0))
            |> Async.AwaitTask

        if obj.ReferenceEquals(winner, first.Task) then
            return first.Task.Result
        else
            return failtest "no delivery reached the hook within five seconds"
    }

    /// Let anything still in flight land before counting.
    member this.Settled = async {
        do! Async.Sleep 300
        return this.All
    }

/// The platform's data-ingestion composition over in-memory stores, with
/// the hooks `app` registered: the real `ServerApp.withDeliveryCompleted`
/// service registration followed by `ComposeJobs.registerDataIngestion`,
/// as `compose` orders them.
type private Rig = {
    Services: IServiceProvider
    Ingestor: IDataIngestor
    Scheduler: RecordingScheduler
    Events: IEventStore
    Logger: RecordingLogger
    Source: InMemoryDataSource.InMemoryDataSource
    Configs: IDataSourceConfigStore
}

let private configure (app: ServerApp) (scheduler: RecordingScheduler) (logger: RecordingLogger) =
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let source = InMemoryDataSource.create ()

    fun (services: IServiceCollection) ->
        ServerApp.serviceConfig app |> Option.iter (fun f -> f services |> ignore)
        services.AddSingleton<IDataSource>(source) |> ignore
        services.AddSingleton<IJobScheduler>(scheduler) |> ignore
        services.AddSingleton<IEventStore>(events) |> ignore

        ComposeJobs.registerDataIngestion
            services
            {
                ServerConfig.defaults with
                    DataIngestion = FeatureMode.Enabled
            }
            blobs
            noSecrets
            (DataObjectStore.DataObjectStore(blobs) :> IDataObjectStore)
            events
            logger

        events, source

let private seedScope (configs: IDataSourceConfigStore) (source: InMemoryDataSource.InMemoryDataSource) scopeId =
    configs.Save(
        scopeId,
        {
            Id = sourceId
            Name = "Trading"
            Kind = "InMemory"
            ConnectionScope = Map.empty
            CredentialKey = "unused"
            Tables = Some tables
            Tags = Map.empty
        }
    )
    |> Async.RunSynchronously

    for t in tables do
        source.SeedText(sourceId, t, "id,value\n1,10\n")

let private rig (app: ServerApp) : Rig =
    let scheduler = RecordingScheduler()
    let logger = RecordingLogger()
    let services = ServiceCollection()
    let events, source = configure app scheduler logger services
    let provider = services.BuildServiceProvider() :> IServiceProvider

    {
        Services = provider
        // Resolving the ingestor registers the platform's job handler.
        Ingestor = provider.GetRequiredService<IDataIngestor>()
        Scheduler = scheduler
        Events = events
        Logger = logger
        Source = source
        Configs = provider.GetRequiredService<IDataSourceConfigStore>()
    }

/// A host running the app's pre-middleware ahead of `handler`, as `compose`
/// mounts it, over the same data-ingestion composition. `handler` stands in
/// for the scope-resolution middleware (it records the request's scope) and
/// a module's delivery route.
let private hosted (app: ServerApp) (handler: Rig -> HttpHandler) (send: Net.Http.HttpClient -> Async<'T>) = async {
    let scheduler = RecordingScheduler()
    let logger = RecordingLogger()
    let composed = ref None

    use host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost
                    .UseTestServer()
                    .ConfigureServices(fun services ->
                        services.AddGiraffe() |> ignore
                        composed.Value <- Some(configure app scheduler logger services))
                    .Configure(fun (builder: IApplicationBuilder) ->
                        let mutable b = builder

                        for thunk in (ServerApp.composedExtensions app).PreMiddleware do
                            b <- thunk b

                        let sp = b.ApplicationServices
                        let events, source = composed.Value.Value

                        let r = {
                            Services = sp
                            Ingestor = sp.GetRequiredService<IDataIngestor>()
                            Scheduler = scheduler
                            Events = events
                            Logger = logger
                            Source = source
                            Configs = sp.GetRequiredService<IDataSourceConfigStore>()
                        }

                        b.UseGiraffe(handler r))
                |> ignore)
            .Build()

    do! host.StartAsync() |> Async.AwaitTask
    use client = host.GetTestClient()
    let! result = send client
    do! host.StopAsync() |> Async.AwaitTask
    return result, logger
}

/// A delivery route: record the request's scope (the scope-resolution
/// middleware's write), then ingest `ingest` — `(sourceId, table)` pairs —
/// in order, and answer 200 "delivered".
let private deliveryRoute (scopeId: string option) (ingest: (string * string) list) (r: Rig) : HttpHandler =
    route "/deliver"
    >=> fun next ctx -> task {
        match scopeId with
        | Some id ->
            ScopeResolution.remember ctx (storage id) |> ignore
            seedScope r.Configs r.Source id
        | None -> ()

        let scope = defaultArg scopeId "anonymous"

        for source, table in ingest do
            let! _ = r.Ingestor.RunIngestion(scope, source, table) |> Async.StartAsTask
            ()

        return! text "delivered" next ctx
    }

let private post (client: Net.Http.HttpClient) = async {
    use! resp = client.PostAsync("/deliver", new Net.Http.StringContent("")) |> Async.AwaitTask
    let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    return resp.StatusCode, body
}

let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

// ─── Tests ────────────────────────────────────────────────────────────

let private requestTests =
    testList "request deliveries" [

        testCaseAsync "a three-table delivery fires once, after the response, with three runs under the request's scope"
        <| async {
            let seen = Seen()
            let scopeId = newScopeId ()
            let app = ServerApp.empty |> ServerApp.withDeliveryCompleted ("seen", seen.Callback)

            let! (status, body), _ =
                hosted app (deliveryRoute (Some scopeId) (tables |> List.map (fun t -> sourceId, t))) post

            Expect.equal status HttpStatusCode.OK "the delivery answered"
            Expect.equal body "delivered" "with its own body"

            let! delivery = seen.First
            let! all = seen.Settled
            Expect.hasLength all 1 "the hook fired exactly once for the delivery"
            Expect.equal delivery.Scope.ScopeId scopeId "under the scope the request resolved"
            Expect.isFalse delivery.Scope.IsAnonymous "a resolved scope, not the anonymous one"
            Expect.equal (delivery.Runs |> List.map _.Table) tables "every table, in the order it settled"
            Expect.isTrue (Delivery.isComplete delivery) "all three committed"
            Expect.isTrue (delivery.Runs |> List.forall (fun r -> r.RunId.IsSome)) "each names its run"

            Expect.isTrue
                (delivery.Runs |> List.forall (fun r -> r.ResultObjectId.IsSome))
                "and where its payload landed"

            match delivery.Origin with
            | DeliveryOrigin.Request(m, p) ->
                Expect.equal m "POST" "the request's method"
                Expect.equal p "/deliver" "and its path"
            | other -> failtestf "expected a request origin, got %A" other
        }

        testCaseAsync "a failed table is reported beside the committed ones, a refused one without a run id"
        <| async {
            let seen = Seen()
            let app = ServerApp.empty |> ServerApp.withDeliveryCompleted ("seen", seen.Callback)

            let ingest = [ sourceId, "orders"; sourceId, "never-seeded"; "unconfigured", "orders" ]

            let! (status, _), _ = hosted app (deliveryRoute (Some(newScopeId ())) ingest) post
            Expect.equal status HttpStatusCode.OK "the delivery itself still answered"

            let! delivery = seen.First
            Expect.hasLength delivery.Runs 3 "three tables were delivered"
            Expect.isFalse (Delivery.isComplete delivery) "not complete"
            Expect.equal (Delivery.committed delivery |> List.map _.Table) [ "orders" ] "one committed"

            match Delivery.failed delivery with
            | [ unseeded; unconfigured ] ->
                Expect.equal unseeded.Table "never-seeded" "the table the source had no content for"
                Expect.isSome unseeded.RunId "its run was recorded"
                Expect.isNone unseeded.ResultObjectId "and landed nowhere"

                match unseeded.Outcome with
                | DeliveredOutcome.Failed(SchemaMismatch _) -> ()
                | other -> failtestf "expected the connector's refusal, got %A" other

                Expect.equal unconfigured.DataSourceId "unconfigured" "the unconfigured source"
                Expect.isNone unconfigured.RunId "refused before any run was recorded"

                match unconfigured.Outcome with
                | DeliveredOutcome.Failed(UnexpectedFailure _) -> ()
                | other -> failtestf "expected the setup refusal, got %A" other
            | other -> failtestf "expected two failed tables, got %A" other
        }

        testCaseAsync "a request that ingests nothing is no delivery, and an anonymous one calls no hook"
        <| async {
            let seen = Seen()
            let app = ServerApp.empty |> ServerApp.withDeliveryCompleted ("seen", seen.Callback)

            let! (status, _), _ = hosted app (deliveryRoute (Some(newScopeId ())) []) post
            Expect.equal status HttpStatusCode.OK "answered"

            let! (anonStatus, _), logger = hosted app (deliveryRoute None [ sourceId, "orders" ]) post
            Expect.equal anonStatus HttpStatusCode.OK "answered"

            let! all = seen.Settled
            Expect.isEmpty all "neither reached the hook"

            Expect.exists
                logger.Warnings
                (fun w -> w.Contains "resolved no scope")
                "the anonymous delivery says why it was not delivered"
        }

        testCaseAsync "a labelled ingestion (Phase 1041) is delivered under its label"
        <| async {
            let seen = Seen()
            let scopeId = newScopeId ()
            let app = ServerApp.empty |> ServerApp.withDeliveryCompleted ("seen", seen.Callback)

            let label =
                match IngestionLabel.tryCreate "daily-orders" with
                | Ok l -> l
                | Error e -> failtestf "the test label was refused: %A" e

            let labelledRoute (r: Rig) : HttpHandler =
                route "/deliver"
                >=> fun next ctx -> task {
                    ScopeResolution.remember ctx (storage scopeId) |> ignore
                    seedScope r.Configs r.Source scopeId
                    let! _ = r.Ingestor.RunIngestion(scopeId, sourceId, "orders", label) |> Async.StartAsTask
                    return! text "delivered" next ctx
                }

            let! (status, _), _ = hosted app labelledRoute post
            Expect.equal status HttpStatusCode.OK "answered"
            let! delivery = seen.First
            Expect.equal (delivery.Runs |> List.map _.Table) [ "daily-orders" ] "filed under the label"
            Expect.isTrue (Delivery.isComplete delivery) "and committed — the decorator did not refuse it"
        }

        testCaseAsync "two hooks each fire once — the middleware is mounted once however many register"
        <| async {
            let a = Seen()
            let b = Seen()

            let app =
                ServerApp.empty
                |> ServerApp.withDeliveryCompleted ("a", a.Callback)
                |> ServerApp.withDeliveryCompleted ("b", b.Callback)

            let! _ = hosted app (deliveryRoute (Some(newScopeId ())) [ sourceId, "orders" ]) post
            let! _ = a.First
            let! _ = b.First
            let! seenA = a.Settled
            let! seenB = b.Settled
            Expect.hasLength seenA 1 "the first hook fired once"
            Expect.hasLength seenB 1 "the second hook fired once"

            Expect.equal (List.head seenA).DeliveryId (List.head seenB).DeliveryId "both were handed the same delivery"
        }
    ]

let private failureTests =
    testList "a throwing callback" [

        testCaseAsync "never affects the response, is logged and audited, and does not stop the next hook"
        <| async {
            let after = Seen()
            let scopeId = newScopeId ()

            let app =
                ServerApp.empty
                |> ServerApp.withDeliveryCompleted ("throws-sync", fun _ _ -> failwith "sync boom")
                |> ServerApp.withDeliveryCompleted (
                    "throws-async",
                    fun _ _ -> async {
                        do! Async.Sleep 1
                        return failwith "async boom"
                    }
                )
                |> ServerApp.withDeliveryCompleted ("after", after.Callback)

            let events = ref None

            let route (r: Rig) =
                events.Value <- Some r.Events
                deliveryRoute (Some scopeId) [ sourceId, "orders" ] r

            let! (status, body), logger = hosted app route post
            Expect.equal status HttpStatusCode.OK "the response is the route's own"
            Expect.equal body "delivered" "untouched"

            let! delivery = after.First
            Expect.equal delivery.Scope.ScopeId scopeId "the hook after both failures still ran"

            let warnings = logger.Warnings
            Expect.exists warnings (fun w -> w.Contains "'throws-sync' callback failed") "the sync throw is logged"
            Expect.exists warnings (fun w -> w.Contains "'throws-async' callback failed") "the async throw is logged"

            let! audited = events.Value.Value.ReadBySource(scopeId, DeliverySourceModule)

            let failures =
                audited |> List.filter (fun e -> e.EventType = CallbackFailedEventType)

            Expect.hasLength failures 2 "each failure is audited in the delivery's scope"

            Expect.isTrue
                (failures |> List.exists (fun e -> e.Payload.Contains "throws-sync"))
                "naming the callback that failed"
        }
    ]

let private jobTests =
    testList "job deliveries" [

        testCaseAsync "a scheduled pull fires once its attempt returns, under the job's carried scope"
        <| async {
            let seen = Seen()

            let r =
                rig (ServerApp.empty |> ServerApp.withDeliveryCompleted ("seen", seen.Callback))

            let scopeId = newScopeId ()
            seedScope r.Configs r.Source scopeId
            let resolved = ScopeResolution.remember (DefaultHttpContext()) (storage scopeId)
            let scheduler = r.Scheduler :> IJobScheduler

            // The registration a declared daily pull schedules (Phase 1015):
            // the platform's own ingestion job, cron-triggered, typed scope.
            let registration: JobRegistration = {
                ScopeId = scopeId
                Handler = DataIngestionJobHandler.JobHandlerName
                Payload = DataIngestionJobHandler.payloadFor sourceId "orders"
                Trigger = Trigger.CronTrigger "0 6 * * *"
                Idempotency = None
                RetryPolicy = JobRetryPolicy.defaults
                ShardKey = Some sourceId
                Precision = JobPrecision.Minute
                CreatedBy = "declared-pull"
                Tags = Map.empty
            }

            let! jobId = async {
                match! scheduler.Schedule(resolved, registration) with
                | Ok(Scheduled id | Coalesced id) -> return id
                | Error e -> return failtestf "scheduling failed: %A" e
            }

            let! fired = scheduler.TriggerOnce(scopeId, jobId, UserId "cron")
            Expect.isOk fired "the tick ran the job"
            Expect.equal r.Scheduler.Results [ JobResult.Success ] "the job's result is the handler's own"

            let! delivery = seen.First
            let! all = seen.Settled
            Expect.hasLength all 1 "fired once"
            Expect.isTrue (obj.ReferenceEquals(delivery.Scope, resolved)) "under the scope the job carried"
            Expect.equal (delivery.Runs |> List.map _.Table) [ "orders" ] "with the pulled table"

            match delivery.Origin with
            | DeliveryOrigin.Job(handler, id) ->
                Expect.equal handler DataIngestionJobHandler.JobHandlerName "the platform's ingestion job"
                Expect.equal id jobId "that job"
            | other -> failtestf "expected a job origin, got %A" other
        }

        testCaseAsync "a job that carried no scope delivers to no hook, and says so"
        <| async {
            let seen = Seen()

            let r =
                rig (ServerApp.empty |> ServerApp.withDeliveryCompleted ("seen", seen.Callback))

            let scopeId = newScopeId ()
            seedScope r.Configs r.Source scopeId
            let scheduler = r.Scheduler :> IJobScheduler

            let registration: JobRegistration = {
                ScopeId = scopeId
                Handler = DataIngestionJobHandler.JobHandlerName
                Payload = DataIngestionJobHandler.payloadFor sourceId "orders"
                Trigger = Trigger.Manual
                Idempotency = None
                RetryPolicy = JobRetryPolicy.defaults
                ShardKey = None
                Precision = JobPrecision.Minute
                CreatedBy = "string-overload"
                Tags = Map.empty
            }

            let! jobId = async {
                match! scheduler.Schedule registration with
                | Ok(Scheduled id | Coalesced id) -> return id
                | Error e -> return failtestf "scheduling failed: %A" e
            }

            let! _ = scheduler.TriggerOnce(scopeId, jobId, UserId "someone")
            Expect.equal r.Scheduler.Results [ JobResult.Success ] "the ingestion itself still ran"
            let! all = seen.Settled
            Expect.isEmpty all "no scope was minted for it"
            Expect.exists r.Logger.Warnings (fun w -> w.Contains "resolved no scope") "and the log says why"
        }

        testCaseAsync "TriggerRefresh schedules under the request's scope, and its delivery reaches the hook"
        <| async {
            let seen = Seen()

            let r =
                rig (ServerApp.empty |> ServerApp.withDeliveryCompleted ("seen", seen.Callback))

            let userScope = "user-1042"
            seedScope r.Configs r.Source userScope

            let requestServices = ServiceCollection()
            requestServices.AddSingleton<IJobScheduler>(r.Scheduler) |> ignore

            requestServices.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser userScope))
            |> ignore

            let ctx = DefaultHttpContext()
            ctx.RequestServices <- requestServices.BuildServiceProvider()
            let resolved = ScopeResolution.remember ctx (storage userScope)

            let! queued = (DataIngestionApiHandler.dataIngestionApi ctx).TriggerRefresh(sourceId, "orders")
            Expect.isOk queued "queued and run"
            Expect.hasLength r.Scheduler.Typed 1 "through the typed overload"
            Expect.isEmpty r.Scheduler.Untyped "and not the string one"

            Expect.isTrue
                (obj.ReferenceEquals(fst r.Scheduler.Typed.Head, resolved))
                "under the scope the middleware recorded — the handler reads the same key"

            let! delivery = seen.First
            Expect.isTrue (obj.ReferenceEquals(delivery.Scope, resolved)) "the refresh delivered under it"
        }

        testCaseAsync "a request the middleware resolved nothing for keeps the string overload"
        <| async {
            let r = rig ServerApp.empty
            seedScope r.Configs r.Source "user-1042b"
            let requestServices = ServiceCollection()
            requestServices.AddSingleton<IJobScheduler>(r.Scheduler) |> ignore

            requestServices.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser "user-1042b"))
            |> ignore

            let ctx = DefaultHttpContext()
            ctx.RequestServices <- requestServices.BuildServiceProvider()
            let! queued = (DataIngestionApiHandler.dataIngestionApi ctx).TriggerRefresh(sourceId, "orders")
            Expect.isOk queued "queued"
            Expect.isEmpty r.Scheduler.Typed "nothing typed was invented"
            Expect.hasLength r.Scheduler.Untyped 1 "the string overload, as before"
        }
    ]

/// The two counts of one deployment: a hook on the delivery and a trigger on the
/// fact tables, over a route that ingests, commits, or does both.
let private reconciliationTests =
    let scopeId = "team-reconcile"

    let extent: ToolUp.Facts.TemporalExtent = {
        From = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
        To = DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc)
        Label = None
    }

    let options (input: char) =
        ToolUp.Facts.FactTableRunOptions.none
        |> ToolUp.Facts.FactTableRunOptions.withInputs [ "sha256:" + String(input, 64) ]
        |> ToolUp.Facts.FactTableRunOptions.forSlice extent

    let row: ToolUp.Facts.FactTableRow = {
        Subject = [ "a1" ]
        Period = extent
        Values = Map.ofList [ "revenue", ToolUp.Facts.Scalar 10m ]
    }

    /// One request: `ingest` the tables, then commit the fact table, as the
    /// module owning both would.
    let route
        (world: DeliveryTriggerTests.DeliveryTriggerWorld)
        (ingest: string list)
        (input: char)
        (r: Rig)
        : HttpHandler =
        Giraffe.Routing.route "/deliver"
        >=> fun next ctx -> task {
            let scope = ScopeResolution.remember ctx (storage scopeId)
            seedScope r.Configs r.Source scopeId
            world.Serve ctx

            for table in ingest do
                let! _ = r.Ingestor.RunIngestion(scopeId, sourceId, table) |> Async.StartAsTask
                ()

            let! _ =
                world.Publish(scope, "orders", Some(options input), [ row ])
                |> Async.StartAsTask

            return! text "delivered" next ctx
        }

    testList "the two counts" [

        testCaseAsync "a re-delivery that changed nothing is a completed delivery and no firing"
        <| async {
            let seen = Seen()
            let world = DeliveryTriggerTests.DeliveryTriggerWorld()
            let app = ServerApp.empty |> ServerApp.withDeliveryCompleted ("seen", seen.Callback)

            let! _ = hosted app (route world [ "orders" ] 'a') post
            let! _ = seen.First
            let! _ = seen.Settled
            Expect.hasLength world.Fired 1 "the first delivery fires both"

            let! _ = hosted app (route world [ "orders" ] 'b') post
            let! deliveries = seen.Settled
            Expect.hasLength deliveries 2 "the hook counts the second delivery"
            Expect.hasLength world.Fired 1 "and the trigger does not: nothing changed"
        }

        testCaseAsync "a request that commits and ingests nothing is a firing and no completed delivery"
        <| async {
            let seen = Seen()
            let world = DeliveryTriggerTests.DeliveryTriggerWorld()
            let app = ServerApp.empty |> ServerApp.withDeliveryCompleted ("seen", seen.Callback)

            let! _ = hosted app (route world [] 'a') post
            let! deliveries = seen.Settled
            Expect.isEmpty deliveries "the hook has no ingestion run to report"
            Expect.hasLength world.Fired 1 "the trigger fires, on the same completion signal"
        }
    ]

let private compositionTests =
    testList "composition" [

        test "registering no hook leaves the platform ingestor undecorated (GP 13)" {
            let r = rig ServerApp.empty
            Expect.equal (r.Ingestor.GetType().Name) "DataIngestor" "the ingestor itself, not a decorator"
            Expect.isEmpty (r.Services.GetServices<DeliveryCompletedHook>()) "and no hook is registered"
        }

        test "a module's hook is registered by addModule, and mounts the request seam once" {
            let seen = Seen()

            let m =
                ServerModule.create "Trading"
                |> ServerModule.withDeliveryCompleted ("module-hook", seen.Callback)

            Expect.hasLength m.DeliveryCompletedHooks 1 "the module carries its hook"
            Expect.isEmpty (ServerModule.create "Other").DeliveryCompletedHooks "a module declares none by default"

            let app = ServerApp.empty |> ServerApp.addModule m
            let services = ServiceCollection()
            ServerApp.serviceConfig app |> Option.iter (fun f -> f services |> ignore)
            let provider = services.BuildServiceProvider()

            Expect.equal
                (provider.GetServices<DeliveryCompletedHook>() |> Seq.map _.Name |> List.ofSeq)
                [ "module-hook" ]
                "the module's hook is registered with the app"

            Expect.hasLength
                (ServerApp.composedExtensions app).PreMiddleware
                ((ServerApp.composedExtensions ServerApp.empty).PreMiddleware.Length + 1)
                "with the request seam"

            // A builder that counts what is mounted on it.
            let inner = ApplicationBuilder(provider) :> IApplicationBuilder
            let mounted = ref 0

            let counting =
                { new IApplicationBuilder with
                    member _.ApplicationServices
                        with get () = inner.ApplicationServices
                        and set v = inner.ApplicationServices <- v

                    member _.ServerFeatures = inner.ServerFeatures
                    member _.Properties = inner.Properties
                    member _.New() = inner.New()
                    member _.Build() = inner.Build()

                    member this.Use middleware =
                        mounted.Value <- mounted.Value + 1
                        inner.Use middleware |> ignore
                        this
                }

            let apply () =
                (ServerApp.composedExtensions app).PreMiddleware
                |> List.fold (fun b thunk -> thunk b) counting
                |> ignore

            apply ()
            Expect.equal mounted.Value 1 "the seam mounts its middleware"
            apply ()
            Expect.equal mounted.Value 1 "and applying it again mounts nothing more"
        }

        test "the new delivery seam and the refresh handler mint no scope (Phase 797)" {
            let mints = [
                "ResolvedScope." + "ofCarried"
                "ResolvedScope." + "ofStorageScope"
                "ScopeResolution." + "ofStorageScope"
                "ScopeResolution." + "remember"
                "\"ToolUp." + "StorageScope\""
                ":? " + "StorageScope"
            ]

            for file in
                [
                    "ToolUp.Platform.Server/Server/DeliveryCompleted.fs"
                    "ToolUp.Platform.Server/Server/DataIngestionApiHandler.fs"
                ] do
                let path = Path.Combine(repoRoot (), "src", file)
                Expect.isTrue (File.Exists path) (sprintf "%s exists" file)
                let source = File.ReadAllText path

                for needle in mints do
                    Expect.isFalse (source.Contains needle) (sprintf "%s carries no '%s'" file needle)
        }
    ]

/// The platform ingestor over fresh in-memory stores, for the
/// `IDataIngestor` contract pack; `wrap` decorates it.
let private ingestorUnderTest (wrap: IDataIngestor -> IDataIngestor) () : IDataIngestorContract.IngestorUnderTest =
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let configs = DataSourceConfigStore.create blobs
    let source = InMemoryDataSource.create ()

    let ingestor =
        DataIngestor.create
            configs
            noSecrets
            (DataObjectStore.DataObjectStore(blobs) :> IDataObjectStore)
            (InMemoryEventStore.InMemoryEventStore() :> IEventStore)
            [ source :> IDataSource ]
            blobs
            (RecordingLogger())

    {
        Ingestor = wrap ingestor
        Seed =
            fun scopeId id tables ->
                configs.Save(
                    scopeId,
                    {
                        Id = id
                        Name = id
                        Kind = "InMemory"
                        ConnectionScope = Map.empty
                        CredentialKey = "unused"
                        Tables = Some tables
                        Tags = Map.empty
                    }
                )
                |> Async.RunSynchronously

                for t in tables do
                    source.SeedText(id, t, "id,value\n1,10\n")
    }

/// The seam's two production implementations, each bound to its pack.
let private conformanceTests =
    let recordingHook: DeliveryCompletedHook = {
        Name = "contract"
        Callback = fun _ _ -> async { return () }
    }

    let recorded =
        ingestorUnderTest (
            DeliveryCompleted.recording
                [ recordingHook ]
                (ServiceCollection().BuildServiceProvider())
                (RecordingLogger())
                None
        )

    testList "IDataIngestor conformance" [
        IDataIngestorContract.tests "DataIngestor" (ingestorUnderTest id)
        IDataIngestorContract.tests "DeliveryCompleted.recording" recorded
        ICallerLabelledIngestorContract.tests "DataIngestor" (ingestorUnderTest id)
        ICallerLabelledIngestorContract.tests "DeliveryCompleted.recording" recorded
    ]

[<Tests>]
let tests =
    testList "Phase 1042 — a module registers a delivery-completed callback" [
        requestTests
        failureTests
        jobTests
        reconciliationTests
        compositionTests
        conformanceTests
    ]
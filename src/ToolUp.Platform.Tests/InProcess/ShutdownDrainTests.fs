module ToolUp.Platform.Tests.InProcess.ShutdownDrainTests

// ─── Phase 869 — a graceful stop loses no acknowledged write and abandons no job ───
//
// Each case starts something, stops it the way a host does (a shutdown window
// carried by the `StopAsync` token), and asserts on what survived:
//
//  • the retrieval stores — `composeRAG` registers its in-process vector store
//    and keyword index as INSTANCES, which the container never disposes, so the
//    final flush their `Dispose` performs never ran: writes acknowledged inside
//    the flush window were gone after a restart;
//  • the ingestion service — a document in flight at stop is either finished
//    inside the window or handed back to the durable queue, never stranded under
//    a lease nobody will settle;
//  • the job scheduler — a dispatch in flight at stop observes cancellation
//    through its async context, and the stop waits for it within the window.

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tracing
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.RAG.IngestionTypes
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.RAGCompose

let private quietLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Poll `condition` every 20 ms for up to `timeoutMs`. The timeout is the
/// failure path, never the schedule: a green run returns as soon as it holds.
let private waitUntil (timeoutMs: int) (condition: unit -> bool) = async {
    let sw = Stopwatch.StartNew()

    while not (condition ()) && sw.ElapsedMilliseconds < int64 timeoutMs do
        do! Async.Sleep 20

    return condition ()
}

/// Stop `service` the way a host does: with a token that fires when the
/// shutdown window closes. Returns how long the stop took.
let private stopWithin (window: TimeSpan) (service: IHostedService) = async {
    use cts = new CancellationTokenSource(window)
    let sw = Stopwatch.StartNew()
    do! service.StopAsync cts.Token |> Async.AwaitTask
    return sw.Elapsed
}

// ─── Retrieval stores ────────────────────────────────────────────────

let private unitVec: float32 array =
    Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

let private constantEmbedder =
    { new IEmbeddingProvider with
        member _.GenerateEmbedding _ = async { return unitVec }

        member _.GenerateEmbeddings texts = async { return texts |> Seq.map (fun _ -> unitVec) |> Seq.toArray }

        member _.ProviderId = "stub"
        member _.ModelId = "constant"
        member _.Dimensions = 8
    }

let private stubFactory =
    { new ToolUp.AI.IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None

        member _.Resolve _ = async { return Error ToolUp.AI.NoProviderConfigured }

        member _.TryResolveByLabel(_, _) = async { return Error ToolUp.AI.NoProviderConfigured }

        member _.BuildPlatform(_, _, _) = None
    }

let private stubProfile =
    { new ToolUp.Platform.Providers.IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }
    }

let private ragApp (storage: IBlobStorage) =
    RAGServerApp.create stubFactory stubProfile constantEmbedder
    |> RAGServerApp.withStorage storage

/// A real generic host over exactly what `composeRAG` registers — so the stop
/// is the host's own: hosted services stopped in reverse order, then the
/// lifecycle hooks, inside `HostOptions.ShutdownTimeout`.
let private ragHost (app: RAGServerApp) : IHost =
    let composed = composeRAG app
    let builder = Host.CreateEmptyApplicationBuilder(HostApplicationBuilderSettings())

    match composed.Extensions.ServiceConfig with
    | Some f -> f builder.Services |> ignore
    | None -> ()

    builder.Services.Configure<HostOptions>(fun (o: HostOptions) -> o.ShutdownTimeout <- TimeSpan.FromSeconds 10.0)
    |> ignore

    builder.Build()

let private chunk: TextChunk = {
    Content = "acme quarterly revenue rose sharply"
    Metadata = Map.empty
}

let private hostedServiceTypeNames (app: RAGServerApp) =
    let composed = composeRAG app
    let services = ServiceCollection() :> IServiceCollection

    let services =
        match composed.Extensions.ServiceConfig with
        | Some f -> f services
        | None -> services

    use sp = services.BuildServiceProvider()
    sp.GetServices<IHostedService>() |> Seq.map _.GetType().Name |> List.ofSeq

let private externalVectorStore () =
    let inner =
        new InMemoryVectorStore(InMemoryBlobStorage() :> IBlobStorage, flushIntervalMs = 60000) :> IVectorStore

    { new IVectorStore with
        member _.Upsert s c v t = inner.Upsert s c v t
        member _.Search s q k = inner.Search s q k
        member _.ListChunks s d = inner.ListChunks s d
        member _.DeleteChunk s c = inner.DeleteChunk s c
        member _.RestoreChunk s c = inner.RestoreChunk s c
        member _.Vacuum s r = inner.Vacuum s r
        member _.DeleteByScope s = inner.DeleteByScope s
        member _.ListScopes() = inner.ListScopes()
        member _.Erase(s, u, p, d) = inner.Erase(s, u, p, d)
    }

let private retrievalStores =
    testList "the retrieval stores are flushed at a graceful stop" [
        testCaseAsync "a chunk upserted inside the flush window is present after a graceful stop and a restart"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            use host = ragHost (ragApp storage)
            do! host.StartAsync() |> Async.AwaitTask

            // Both writes land inside the stores' two-second flush window:
            // acknowledged, in memory, not yet persisted.
            do! host.Services.GetRequiredService<IVectorStore>().Upsert Deployment "c1" unitVec chunk
            do! host.Services.GetRequiredService<ISparseIndex>().Upsert Deployment "c1" chunk

            do! host.StopAsync() |> Async.AwaitTask

            // The restart: fresh stores over the same storage, which hydrate
            // from what the stopped process persisted.
            use vectors =
                new InMemoryVectorStore(storage, logger = quietLogger, flushIntervalMs = 60000)

            let! stored = (vectors :> IVectorStore).ListChunks Deployment false
            Expect.equal (stored |> List.map fst) [ "c1" ] "the vector store's acknowledged write survived the stop"

            use keywords =
                new InMemoryBM25Index(storage, logger = quietLogger, flushIntervalMs = 60000)

            let! hits = (keywords :> ISparseIndex).Search [ Deployment ] "revenue" 5

            Expect.equal
                (hits |> List.map _.ChunkId)
                [ "c1" ]
                "the keyword index's acknowledged write survived the stop"
        }

        test "a composition that constructs no in-process store registers no flush service (GP 13)" {
            let owned = hostedServiceTypeNames (ragApp (InMemoryBlobStorage() :> IBlobStorage))

            let supplied =
                hostedServiceTypeNames (
                    ragApp (InMemoryBlobStorage() :> IBlobStorage)
                    |> RAGServerApp.withVectorStore (externalVectorStore ())
                    |> RAGServerApp.withoutSparseIndex
                )

            Expect.contains
                owned
                "RetrievalStoreFlushService"
                "the default composition owns its stores, so it flushes them"

            Expect.isFalse
                (List.contains "RetrievalStoreFlushService" supplied)
                "a supplied store and no keyword index: nothing owned, nothing registered"
        }
    ]

// ─── Ingestion ───────────────────────────────────────────────────────

let private mkJob (docId: string) : DocumentIngestionJob = {
    DocumentId = docId
    DocumentName = docId
    Chunks = [ docId + ":chunk:0", { Content = "x"; Metadata = Map.empty } ]
    Scope = Deployment
    ScopeId = "scope-869"
    Container = "deployment"
    OriginatingUserId = None
    Attempt = None
}

/// A pipeline whose `Index` signals entry and then takes `hold` (a
/// cancellable sleep), recording the chunk only if the hold completes.
type private HoldingPipeline(hold: TimeSpan) =
    let entered = Collections.Concurrent.ConcurrentQueue<string>()
    let indexed = Collections.Concurrent.ConcurrentQueue<string>()

    member _.Entered = List.ofSeq entered
    member _.Indexed = List.ofSeq indexed

    interface IRetrievalPipeline with
        member _.Retrieve _ _ = async.Return []

        member _.Index chunkId _ _ = async {
            entered.Enqueue chunkId
            do! Async.Sleep hold
            indexed.Enqueue chunkId
        }

        member _.DeleteByScope _ = async.Return()

let private ingestionService (q: IngestionQueue) (pipeline: IRetrievalPipeline) =
    ToolUp.RAG.IngestionService.create
        q
        pipeline
        constantEmbedder
        (InMemoryEventStore.InMemoryEventStore() :> IEventStore)
        []
        1
        quietLogger
        (ToolUp.RAG.RagTelemetry.createNoOp ())
        (ToolUp.Platform.Usage.NoOpUsageLog())
        (ToolUp.Platform.Usage.NoOpTeamQuotaPolicy())
        IngestionRetryPolicy.defaults
        None
        (ToolUp.RAG.IngestionService.IngestionAlertState())
        (fun () -> None)
        ignore

/// Every job the store will hand out right now, claimed under a fresh lease.
let private claimAll (store: IIngestionQueueStore) = async {
    let claimed = ResizeArray<IngestionLease>()
    let mutable more = true

    while more do
        match! store.Claim(TimeSpan.FromMinutes 1.0) with
        | Some lease -> claimed.Add lease
        | None -> more <- false

    return List.ofSeq claimed
}

/// A store whose next `Claim`, once armed, takes the lease and then holds it
/// until released — the instant a claim lands as the stop arrives.
type private GatedClaimStore(inner: IIngestionQueueStore) =
    let mutable armed = false
    let claimed = TaskCompletionSource()
    let release = TaskCompletionSource()

    member _.Arm() = armed <- true
    member _.Claimed = claimed.Task
    member _.Release() = release.TrySetResult() |> ignore

    interface IIngestionQueueStore with
        member _.Name = inner.Name
        member _.Enqueue(job, capacity) = inner.Enqueue(job, capacity)

        member _.Claim leaseDuration = async {
            let! lease = inner.Claim leaseDuration

            if armed && lease.IsSome then
                armed <- false
                claimed.TrySetResult() |> ignore
                do! release.Task |> Async.AwaitTask

            return lease
        }

        member _.Complete leaseId = inner.Complete leaseId
        member _.Release leaseId = inner.Release leaseId
        member _.ReclaimExpired() = inner.ReclaimExpired()
        member _.Depth() = inner.Depth()

let private ingestion =
    testList "an ingestion job in flight at stop is finished or handed back" [
        testCaseAsync "a document claimed as the stop arrives is processed, not stranded under its lease"
        <| async {
            let inner = InMemoryIngestionQueueStore()
            let store = GatedClaimStore(inner)
            let q = IngestionQueue(10, DropWrite, store)
            let pipeline = HoldingPipeline(TimeSpan.Zero)
            use svc = ingestionService q pipeline
            do! (svc :> IHostedService).StartAsync CancellationToken.None |> Async.AwaitTask

            store.Arm()
            Expect.isTrue (q.Enqueue(mkJob "d1")) "accepted"
            let! claimed = waitUntil 5000 (fun () -> store.Claimed.IsCompleted)
            Expect.isTrue claimed "the drain loop's claim has taken the lease"

            // The stop signals the loop while the claim is still returning.
            let stopping = Async.StartAsTask(stopWithin (TimeSpan.FromSeconds 10.0) svc)

            do! Async.Sleep 200
            store.Release()
            let! _ = stopping |> Async.AwaitTask

            Expect.equal pipeline.Indexed [ "d1:chunk:0" ] "the claimed document was processed"
            let! depth = (inner :> IIngestionQueueStore).Depth()
            Expect.equal depth 0 "and acknowledged — no lease left to wait out its expiry"
        }

        testCaseAsync "a document in flight at stop is finished inside the window"
        <| async {
            let store = InMemoryIngestionQueueStore()
            let q = IngestionQueue(10, DropWrite, store)
            let pipeline = HoldingPipeline(TimeSpan.FromMilliseconds 400.0)
            use svc = ingestionService q pipeline
            do! (svc :> IHostedService).StartAsync CancellationToken.None |> Async.AwaitTask

            Expect.isTrue (q.Enqueue(mkJob "d1")) "accepted"
            let! inFlight = waitUntil 5000 (fun () -> not pipeline.Entered.IsEmpty)
            Expect.isTrue inFlight "the document is in flight"

            let! _ = stopWithin (TimeSpan.FromSeconds 10.0) svc

            Expect.equal pipeline.Indexed [ "d1:chunk:0" ] "the in-flight document was finished, not cut off"
            let! depth = (store :> IIngestionQueueStore).Depth()
            Expect.equal depth 0 "and acknowledged: nothing pending, nothing held under a lease"
        }

        testCaseAsync "a document still running when the window closes is back on the durable queue"
        <| async {
            let store = InMemoryIngestionQueueStore()
            let q = IngestionQueue(10, DropWrite, store)
            let pipeline = HoldingPipeline(TimeSpan.FromMinutes 10.0)
            use svc = ingestionService q pipeline
            do! (svc :> IHostedService).StartAsync CancellationToken.None |> Async.AwaitTask

            Expect.isTrue (q.Enqueue(mkJob "d1")) "accepted"
            let! inFlight = waitUntil 5000 (fun () -> not pipeline.Entered.IsEmpty)
            Expect.isTrue inFlight "the document is in flight"

            let! _ = stopWithin (TimeSpan.FromMilliseconds 300.0) svc

            // Claimable NOW — not after the ten-minute lease expires.
            let! next = claimAll store
            Expect.equal (next |> List.map _.Job.DocumentId) [ "d1" ] "the unfinished document is claimable again"
            Expect.equal (next |> List.map _.Attempt) [ 2 ] "as its second delivery"
        }

        testCaseAsync "a graceful stop completes inside the shutdown window under a full queue"
        <| async {
            let store = InMemoryIngestionQueueStore()
            let q = IngestionQueue(4, DropWrite, store)
            let pipeline = HoldingPipeline(TimeSpan.FromMinutes 10.0)
            use svc = ingestionService q pipeline
            do! (svc :> IHostedService).StartAsync CancellationToken.None |> Async.AwaitTask

            Expect.isTrue (q.Enqueue(mkJob "d1")) "accepted"
            let! inFlight = waitUntil 5000 (fun () -> not pipeline.Entered.IsEmpty)
            Expect.isTrue inFlight "one document is in flight"

            let accepted = [ for i in 2..8 -> q.Enqueue(mkJob (sprintf "d%d" i)) ]

            Expect.equal
                (accepted |> List.filter id |> List.length)
                3
                "the queue is full: three wait, the rest are refused"

            let window = TimeSpan.FromMilliseconds 500.0
            let! took = stopWithin window svc

            Expect.isLessThan
                took
                (window + TimeSpan.FromSeconds 1.5)
                "the stop returned within the window (plus the hand-back), not after the document finished"

            let! next = claimAll store

            Expect.equal
                (next |> List.map _.Job.DocumentId |> List.sort)
                [ "d1"; "d2"; "d3"; "d4" ]
                "every accepted document is claimable after the stop — the in-flight one included"
        }
    ]

// ─── Job scheduler ───────────────────────────────────────────────────

let private silentChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async { return () }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }
    }

let private scheduler () =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let jobStore = JobStore.create storage eventStore

    JobScheduler.create jobStore eventStore silentChannel ServerConfig.defaults quietLogger (NoOpActivitySink())

let private manualJob (handler: string) : JobRegistration = {
    ScopeId = "scope-869"
    Handler = handler
    Payload = ""
    Trigger = Manual
    Idempotency = None
    RetryPolicy = {
        JobRetryPolicy.defaults with
            MaxAttempts = 1
            InitialBackoff = TimeSpan.Zero
            MaxBackoff = TimeSpan.Zero
    }
    ShardKey = None
    Precision = Minute
    CreatedBy = "test"
    Tags = Map.empty
}

let private jobScheduler =
    testList "a dispatch in flight at stop observes cancellation" [
        testCaseAsync "the handler's async context is cancelled at stop, and the stop waits for it"
        <| async {
            let sched = scheduler ()
            let started = TaskCompletionSource()
            let observed = TaskCompletionSource()
            let finished = TaskCompletionSource()

            let handler =
                { new IJobHandler with
                    member _.Execute _ = async {
                        try
                            use! _ = Async.OnCancel(fun () -> observed.TrySetResult() |> ignore)
                            started.TrySetResult() |> ignore
                            do! Async.Sleep(TimeSpan.FromMinutes 10.0)
                            return Success
                        finally
                            finished.TrySetResult() |> ignore
                    }
                }

            let api = sched :> IJobScheduler
            api.RegisterHandler("hold-869", handler)

            let! scheduled = api.Schedule(manualJob "hold-869")

            let jobId =
                match scheduled with
                | Ok id -> id
                | Error e -> failtestf "schedule failed: %A" e

            match! api.TriggerOnce("scope-869", jobId, "test") with
            | Ok() -> ()
            | Error e -> failtestf "TriggerOnce failed: %s" e

            let! running = waitUntil 10000 (fun () -> started.Task.IsCompleted)
            Expect.isTrue running "the dispatch is in flight"

            let! took = stopWithin (TimeSpan.FromSeconds 10.0) sched

            Expect.isTrue observed.Task.IsCompleted "the handler observed cancellation through its async context"
            Expect.isTrue finished.Task.IsCompleted "and the stop waited for the dispatch to end"
            Expect.isLessThan took (TimeSpan.FromSeconds 5.0) "promptly, not at the end of the window"
        }
    ]

// ─── Background.start ────────────────────────────────────────────────

type private CapturingLogger() =
    let lines = Collections.Concurrent.ConcurrentQueue<string * string>()

    member _.Lines = List.ofSeq lines

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn m = lines.Enqueue("warn", m)

        member _.Error(m, ex) =
            lines.Enqueue("error", m + (ex |> Option.map (fun e -> " | " + e.Message) |> Option.defaultValue ""))

let private background =
    testList "Background.start" [
        testCaseAsync "a throw is caught and logged under the label, never left to take the process down"
        <| async {
            let logger = CapturingLogger()
            let drain = Background.DrainSet()

            Background.start drain logger "[Probe] event=work_failed" (async { failwith "boom" })

            let! drained = drain.WaitAsync(CancellationToken.None) |> Async.AwaitTask
            Expect.isTrue drained "the work ended"
            Expect.equal logger.Lines [ "error", "[Probe] event=work_failed | boom" ] "logged once, with the label"
        }

        testCaseAsync "the drain set waits for running work, and reports a window that closed first"
        <| async {
            let drain = Background.DrainSet()
            let release = TaskCompletionSource()
            Background.start drain quietLogger "[Probe]" (Async.AwaitTask release.Task)

            Expect.equal drain.Count 1 "the work is tracked"

            use window = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)
            let! closed = drain.WaitAsync window.Token |> Async.AwaitTask
            Expect.isFalse closed "the window closed before the work ended"

            release.SetResult()
            let! drained = drain.WaitAsync(CancellationToken.None) |> Async.AwaitTask
            Expect.isTrue drained "it ended once released"
            Expect.equal drain.Count 0 "nothing left running"
        }

        testCaseAsync "cancellation reaches the work through its async context, and nothing new starts after it"
        <| async {
            let logger = CapturingLogger()
            let drain = Background.DrainSet()
            let started = TaskCompletionSource()
            let observed = TaskCompletionSource()

            Background.start
                drain
                logger
                "[Probe]"
                (async {
                    use! _ = Async.OnCancel(fun () -> observed.TrySetResult() |> ignore)
                    started.TrySetResult() |> ignore
                    do! Async.Sleep(TimeSpan.FromMinutes 10.0)
                })

            let! running = waitUntil 10000 (fun () -> started.Task.IsCompleted)
            Expect.isTrue running "the work is running"
            drain.Cancel()
            let! drained = drain.WaitAsync(CancellationToken.None) |> Async.AwaitTask
            Expect.isTrue drained "the cancelled work ended"
            Expect.isTrue observed.Task.IsCompleted "it observed the cancellation"

            let mutable ran = false
            Background.start drain logger "[Late]" (async { ran <- true })
            do! Async.Sleep 100
            Expect.isFalse ran "work offered to a cancelled set is not started"
            Expect.exists logger.Lines (fun (_, m) -> m.StartsWith "[Late]" && m.Contains "not started") "and says so"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 869 — shutdown flushes and drains" [ retrievalStores; ingestion; jobScheduler; background ]
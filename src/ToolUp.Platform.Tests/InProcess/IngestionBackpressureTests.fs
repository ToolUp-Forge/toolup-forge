module ToolUp.Platform.Tests.InProcess.IngestionBackpressureTests

// ─── Phase 303 — ingestion-queue backpressure observability ──────────
//
// Covers the Phase 303 additions:
//   * `IngestionQueue` drop counters — `Dropped` (cumulative) + rolling
//     60s window, bumped by `RecordDrop`; `Policy` default (`DropWrite`);
//     `Block` policy + `EnqueueBlocking`.
//   * `RAGCompose.emitIngestionDrop` — the drop-observability triple: a
//     `KnowledgeIngestionDropped` audit under `_platform.knowledge`, a
//     Warning `SystemMessage` to the uploading user (when attributed), and
//     the queue counter bump. This is the mechanism behind the acceptance
//     criteria (a) one audit per dropped document + (b) one Warning
//     notification; the endpoint side (c) `/health/rag` reads the same
//     queue counters this test exercises.

open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.IngestionTypes
open System
open System.Threading
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IRetrievalTracer
open ToolUp.RAG.RetrievalPipeline
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private run = Async.RunSynchronously

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private mkJob (docId: string) : DocumentIngestionJob = {
    DocumentId = docId
    DocumentName = docId
    Chunks = [ docId + ":chunk:0", { Content = "x"; Metadata = Map.empty } ]
    Scope = Deployment
    ScopeId = "scope-1"
    Container = "deployment"
    OriginatingUserId = None
    Attempt = None
}

/// Thread-safe capturing IAuditLog double.
type private CapturingAuditLog() =
    let events = ResizeArray<string * AuditEvent>()
    let gate = obj ()

    member _.Events = lock gate (fun () -> events |> List.ofSeq)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { lock gate (fun () -> events.Add(scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// Thread-safe capturing INotificationChannel double.
type private CapturingNotifications() =
    let published = ResizeArray<string * Notification>()
    let gate = obj ()

    member _.Published = lock gate (fun () -> published |> List.ofSeq)

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { lock gate (fun () -> published.Add(scopeId, notification)) }

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()

let private queueCounters =
    testList "IngestionQueue drop counters + policy" [
        test "Enqueue returns false past capacity; RecordDrop bumps cumulative + rolling 60s" {
            let q = IngestionQueue(2)
            Expect.isTrue (q.Enqueue(mkJob "a")) "first accepted"
            Expect.isTrue (q.Enqueue(mkJob "b")) "second accepted"
            Expect.isFalse (q.Enqueue(mkJob "c")) "third rejected — at capacity"
            Expect.equal q.Dropped 0L "no drops recorded until RecordDrop is called"
            q.RecordDrop()
            Expect.equal q.Dropped 1L "cumulative incremented"
            Expect.equal q.DroppedLast60s 1 "rolling window sees the drop"
        }

        test "default overflow policy is DropWrite (back-compat)" {
            Expect.equal (IngestionQueue(2).Policy) DropWrite "default"
        }

        test "Block policy: EnqueueBlocking completes when space is available" {
            let q = IngestionQueue(2, Block)
            Expect.equal q.Policy Block "policy stored"
            run (q.EnqueueBlocking(mkJob "a"))
            Expect.equal q.Count 1 "depth incremented after blocking enqueue"
        }
    ]

let private dropEmission =
    testList "emitIngestionDrop observability triple" [
        test "writes one KnowledgeIngestionDropped audit under _platform.knowledge + bumps queue counter" {
            let q = IngestionQueue(1)
            let audit = CapturingAuditLog()

            run (
                ToolUp.RAG.RAGCompose.emitIngestionDrop
                    (Some(audit :> IAuditLog))
                    None
                    q
                    noopLogger
                    "team:t1"
                    "doc.pdf"
                    3
                    "ingestion queue full after bounded retry"
                    None
            )

            Expect.equal q.Dropped 1L "queue cumulative bumped"

            match audit.Events with
            | [ (scopeId, KnowledgeIngestionDropped p) ] ->
                Expect.equal scopeId KnowledgeSourceModule.value "recorded under _platform.knowledge"
                Expect.equal p.ScopeKey "team:t1" "scope key"
                Expect.equal p.DocId "doc.pdf" "doc id"
                Expect.equal p.ChunkCount 3 "chunk count"
                Expect.equal p.QueueCapacity 1 "capacity read from the queue"
            | other -> failtestf "expected one KnowledgeIngestionDropped, got %A" other
        }

        test "publishes a Warning SystemMessage to the uploading user when attributed" {
            let q = IngestionQueue(1)
            let notifs = CapturingNotifications()

            run (
                ToolUp.RAG.RAGCompose.emitIngestionDrop
                    None
                    (Some(notifs :> INotificationChannel))
                    q
                    noopLogger
                    "team:t1"
                    "doc.pdf"
                    2
                    "queue full"
                    (Some "user-9")
            )

            match notifs.Published with
            | [ (scopeId, Notification.SystemMessage(level, _)) ] ->
                Expect.equal scopeId "user-9" "published to the uploader's scope"
                Expect.equal level SystemMessageLevel.Warning "Warning level"
            | other -> failtestf "expected one Warning SystemMessage, got %A" other
        }

        test "no user attribution ⇒ no notification, but the drop is still counted" {
            let q = IngestionQueue(1)
            let notifs = CapturingNotifications()

            run (
                ToolUp.RAG.RAGCompose.emitIngestionDrop
                    None
                    (Some(notifs :> INotificationChannel))
                    q
                    noopLogger
                    "deployment"
                    "doc.pdf"
                    1
                    "queue full"
                    None
            )

            Expect.isEmpty notifs.Published "no publish without an originating user"
            Expect.equal q.Dropped 1L "drop still counted"
        }
    ]

// ─── Phase 894 — a chat turn's latency budget; a real queue capacity ─
//
// The two reproductions the phase opened with are kept here as permanent
// cases. Measured on the pre-894 tree (2026-09-29): with every embed
// delayed by 3 s, one hybrid retrieval took 3,021 ms — the stall tracked
// the provider exactly, with nothing bounding it; and with a worker pool
// of one and a queue of capacity two, all ten of ten enqueues were
// accepted while one document ran, because the drain loop dequeued
// eagerly and each document then waited for a slot OUTSIDE the queue's
// accounting. The cases below pin the corrected behaviour.

let private unitVec = Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

let private ctxU = AccessContext.unrestricted (AuthenticatedUser "u")

/// An embedder that sleeps `delay` on every call and counts the calls.
type private DelayedEmbedder(delay: TimeSpan) =
    let mutable calls = 0
    member _.Calls = calls

    interface IEmbeddingProvider with
        member _.GenerateEmbedding _ = async {
            Interlocked.Increment(&calls) |> ignore

            if delay > TimeSpan.Zero then
                do! Async.Sleep(int delay.TotalMilliseconds)

            return unitVec
        }

        member this.GenerateEmbeddings texts =
            batchedFallback (this :> IEmbeddingProvider).GenerateEmbedding texts

        member _.Dimensions = 8
        member _.ProviderId = "test"
        member _.ModelId = "delayed-v1"

/// Captures every trace the pipeline emits.
type private CapturingTracer() =
    let traces = ResizeArray<RetrievalTrace>()
    let gate = obj ()
    member _.Traces = lock gate (fun () -> traces |> List.ofSeq)

    interface IRetrievalTracer with
        member _.Trace trace _ = async { lock gate (fun () -> traces.Add trace) }
        member _.Miss _ _ = async.Return()

type private FixedResolver(facts: ResolvedFact list, delay: TimeSpan, fail: bool) =
    interface IFactResolver with
        member _.Resolve(_, _) = async {
            if delay > TimeSpan.Zero then
                do! Async.Sleep(int delay.TotalMilliseconds)

            if fail then
                failwith "fact store unavailable"

            return facts
        }

/// A disclosure gate that would disclose everything — after `delay`.
type private SlowGate(delay: TimeSpan) =
    let check (ids: string list) = async {
        do! Async.Sleep(int delay.TotalMilliseconds)
        return ids |> List.map (fun id -> id, FactDisclosable) |> Map.ofList
    }

    interface IFactDisclosureGate with
        member _.Check(_: string, _: string, _: FactEgressSurface, ids: string list) = check ids
        member _.Check(_: ResolvedScope, _: string, _: FactEgressSurface, ids: string list) = check ids

let private resolvedFact (id: string) : ResolvedFact = {
    FactId = id
    Rendering = "42"
    Freshness = FactFresh
    SupersededBy = None
    Metric = "revenue"
}

let private clause: FactClause = {
    SubjectHierarchy = "brand"
    SubjectPath = [ "acme" ]
    Metric = "revenue"
    PeriodFrom = None
    PeriodTo = None
    AsOf = None
}

let private chunk = {
    Content = "alpha bravo"
    Metadata = Map.empty
}

/// A vector store and a BM25 index over one in-memory blob store, each
/// holding chunk `c1` for `User "u"`.
let private seededIndexes () = async {
    let blob = InMemoryBlobStorage() :> IBlobStorage

    let store =
        new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(blob, logger = noopLogger, flushIntervalMs = 60000)

    let bm25 =
        new ToolUp.RAG.InMemoryBM25Index.InMemoryBM25Index(blob, logger = noopLogger, flushIntervalMs = 60000)

    do! (store :> IVectorStore).Upsert (User "u") "c1" unitVec chunk
    do! (bm25 :> ISparseIndex).Upsert (User "u") "c1" chunk
    return store, bm25
}

let private budgets (embedTimeout: TimeSpan) (factStage: TimeSpan) : RetrievalBudgets = {
    QueryEmbed = {
        QueryEmbedPolicy.defaults with
            Timeout = embedTimeout
    }
    FactStage = factStage
}

let private stagesOf (tracer: CapturingTracer) = tracer.Traces |> List.collect _.Stages

let private queryPath =
    testList "query path" [
        testCaseAsync
            "reproduction: a 10 s embedder — retrieval returns inside its budget with keyword results and a stage mark"
        <| async {
            let! store, bm25 = seededIndexes ()
            use _ = store
            use _ = bm25
            let tracer = CapturingTracer()

            let pipeline =
                RetrievalPipeline(
                    store,
                    DelayedEmbedder(TimeSpan.FromSeconds 10.0),
                    bm25,
                    tracer = tracer,
                    budgets = budgets (TimeSpan.FromMilliseconds 300.0) (TimeSpan.FromSeconds 3.0)
                )
                :> IRetrievalPipeline

            let sw = Diagnostics.Stopwatch.StartNew()
            let! results = pipeline.Retrieve (RetrievalRequest.create "alpha" [ User "u" ] 5 Interleaved) ctxU
            sw.Stop()

            Expect.isLessThan sw.ElapsedMilliseconds 5000L "the turn is bounded by its embed budget, not the provider"
            Expect.equal (results |> List.map _.ChunkId) [ "c1" ] "the keyword branch answers alone"

            Expect.contains (stagesOf tracer) "DenseDegraded:TimedOut" "the degradation is marked on the trace"

            Expect.isFalse (tracer.Traces |> List.exists _.DenseUsed) "the trace reports the dense branch as not used"
        }

        testCaseAsync "a composition that sets no budget keeps today's results for a healthy provider"
        <| async {
            let! store, bm25 = seededIndexes ()
            use _ = store
            use _ = bm25
            let request = RetrievalRequest.create "alpha" [ User "u" ] 5 Interleaved

            let unbounded =
                RetrievalPipeline(store, DelayedEmbedder TimeSpan.Zero, bm25) :> IRetrievalPipeline

            let bounded =
                RetrievalPipeline(store, DelayedEmbedder TimeSpan.Zero, bm25, budgets = RetrievalBudgets.defaults)
                :> IRetrievalPipeline

            let! before = unbounded.Retrieve request ctxU
            let! after = bounded.Retrieve request ctxU
            Expect.equal after before "the defaults change nothing for a healthy provider"
            Expect.isNonEmpty after "and there is something to compare"
        }

        test "RAGServerApp composes the default budgets and an off-path trace queue" {
            let app =
                ToolUp.RAG.RAGCompose.RAGServerApp.create
                    Unchecked.defaultof<_>
                    Unchecked.defaultof<_>
                    (DelayedEmbedder TimeSpan.Zero)

            Expect.equal app.RetrievalBudgets RetrievalBudgets.defaults "default budgets"
            Expect.equal app.RetrievalTraceQueueCapacity 1024 "default trace queue bound"

            let tuned =
                app
                |> ToolUp.RAG.RAGCompose.RAGServerApp.withQueryEmbedTimeout (TimeSpan.FromSeconds 1.0)
                |> ToolUp.RAG.RAGCompose.RAGServerApp.withQueryEmbedAttempts 2
                |> ToolUp.RAG.RAGCompose.RAGServerApp.withQueryEmbedConcurrency 16
                |> ToolUp.RAG.RAGCompose.RAGServerApp.withFactStageTimeout (TimeSpan.FromMilliseconds 750.0)
                |> ToolUp.RAG.RAGCompose.RAGServerApp.withRetrievalTraceQueueCapacity 0

            Expect.equal
                tuned.RetrievalBudgets
                {
                    QueryEmbed = {
                        MaxAttempts = 2
                        Timeout = TimeSpan.FromSeconds 1.0
                        MaxConcurrentCalls = Some 16
                    }
                    FactStage = TimeSpan.FromMilliseconds 750.0
                }
                "each builder sets its own field"

            Expect.equal tuned.RetrievalTraceQueueCapacity 0 "0 writes traces inline"
        }

        testCaseAsync "one hundred concurrent identical queries against a cold cache make one provider call"
        <| async {
            let inner = DelayedEmbedder(TimeSpan.FromMilliseconds 200.0)

            let cached =
                ToolUp.RAG.CachingEmbeddingProvider.create
                    inner
                    (new ToolUp.RAG.InMemoryEmbeddingCache.InMemoryEmbeddingCache())

            let! vectors = List.replicate 100 (cached.GenerateEmbedding "same query") |> Async.Parallel

            Expect.equal inner.Calls 1 "concurrent misses for one key share one provider call"
            Expect.equal vectors.Length 100 "every caller is answered"
            Expect.isTrue (vectors |> Array.forall (fun v -> v = unitVec)) "with the shared vector"
        }

        testCaseAsync "a coalesced provider failure reaches every waiter as the provider's own exception"
        <| async {
            let failing =
                { new IEmbeddingProvider with
                    member _.GenerateEmbedding _ = async {
                        do! Async.Sleep 100
                        return raise (TimeoutException "provider stalled")
                    }

                    member this.GenerateEmbeddings texts =
                        batchedFallback (this :> IEmbeddingProvider).GenerateEmbedding texts

                    member _.Dimensions = 8
                    member _.ProviderId = "test"
                    member _.ModelId = "failing-v1"
                }

            let cached =
                ToolUp.RAG.CachingEmbeddingProvider.create
                    failing
                    (new ToolUp.RAG.InMemoryEmbeddingCache.InMemoryEmbeddingCache())

            let! outcomes = List.replicate 5 (Async.Catch(cached.GenerateEmbedding "q")) |> Async.Parallel

            for outcome in outcomes do
                match outcome with
                | Choice2Of2(:? TimeoutException) -> ()
                | other -> failtestf "expected the provider's TimeoutException, got %A" other
        }

        testCaseAsync "the concurrency ceiling refuses as a value, and frees its slot when the call finishes"
        <| async {
            let gate =
                QueryEmbedGate(
                    {
                        QueryEmbedPolicy.defaults with
                            MaxConcurrentCalls = Some 1
                    }
                )

            let embedder = DelayedEmbedder(TimeSpan.FromMilliseconds 400.0)
            let! first = Async.StartChild(gate.Embed embedder "a")
            do! Async.Sleep 50
            let! second = gate.Embed embedder "b"

            Expect.equal
                second
                (QueryEmbedOutcome.Refused 1)
                "the second call is refused while the first holds the slot"

            match! first with
            | QueryEmbedOutcome.Embedded _ -> ()
            | other -> failtestf "the first call should embed, got %A" other

            match! gate.Embed embedder "c" with
            | QueryEmbedOutcome.Embedded _ -> ()
            | other -> failtestf "the slot is free again, got %A" other

            Expect.equal embedder.Calls 2 "the refused call reached no provider"
        }

        testCaseAsync "a disclosure check that times out discloses nothing"
        <| async {
            let! store, bm25 = seededIndexes ()
            use _ = store
            use _ = bm25
            let tracer = CapturingTracer()

            let pipeline =
                RetrievalPipeline(
                    store,
                    DelayedEmbedder TimeSpan.Zero,
                    tracer = tracer,
                    factResolver = FixedResolver([ resolvedFact "f1" ], TimeSpan.Zero, false),
                    disclosureGate = SlowGate(TimeSpan.FromSeconds 10.0),
                    budgets = budgets (TimeSpan.FromSeconds 5.0) (TimeSpan.FromMilliseconds 200.0)
                )
                :> IRetrievalPipeline

            let request = {
                RetrievalRequest.create "alpha" [ User "u" ] 5 Interleaved with
                    FactClause = Some clause
            }

            let sw = Diagnostics.Stopwatch.StartNew()
            let! results = pipeline.Retrieve request ctxU
            sw.Stop()

            Expect.isLessThan sw.ElapsedMilliseconds 5000L "the turn does not wait on the disclosure check"

            Expect.isFalse
                (results |> List.exists (fun m -> m.Metadata.ContainsKey ChunkMetadata.FactIdKey))
                "no fact is admitted whose disclosure check did not complete"

            Expect.equal (results |> List.map _.ChunkId) [ "c1" ] "the turn proceeds on its chunks"
            Expect.contains (stagesOf tracer) "FactsDegraded:TimedOut" "the degradation is marked"
        }

        testCaseAsync "a faulting fact store degrades the turn instead of failing it"
        <| async {
            let! store, bm25 = seededIndexes ()
            use _ = store
            use _ = bm25
            let tracer = CapturingTracer()

            let pipeline =
                RetrievalPipeline(
                    store,
                    DelayedEmbedder TimeSpan.Zero,
                    tracer = tracer,
                    factResolver = FixedResolver([], TimeSpan.Zero, true),
                    budgets = RetrievalBudgets.defaults
                )
                :> IRetrievalPipeline

            let request = {
                RetrievalRequest.create "alpha" [ User "u" ] 5 Interleaved with
                    FactClause = Some clause
            }

            let! results = pipeline.Retrieve request ctxU
            Expect.equal (results |> List.map _.ChunkId) [ "c1" ] "the chunks still answer"
            Expect.contains (stagesOf tracer) "FactsDegraded:Failed" "the fault is marked"
        }

        testCaseAsync "the trace write leaves the request path, and a lost trace is counted"
        <| async {
            let release = Tasks.TaskCompletionSource<unit>()
            let mutable written = 0

            let blocking =
                { new IRetrievalTracer with
                    member _.Trace _ _ = async {
                        do! release.Task |> Async.AwaitTask
                        Interlocked.Increment(&written) |> ignore
                    }

                    member _.Miss _ _ = async.Return()
                }

            use tracer = ToolUp.RAG.RetrievalTracers.createBackground blocking 1 noopLogger

            let trace: RetrievalTrace = {
                QueryHash = "h"
                QueryLength = 1
                RequestedScopes = []
                PermittedScopes = []
                TopK = 1
                AdaptiveK = false
                CandidatePoolSize = 0
                TopScore = 0.0
                DenseUsed = true
                SparseUsed = false
                RerankerName = None
                LatencyMs = 0L
                Stages = []
                ResultCount = 0
                StageTimings = []
                RewriteDecision = None
                RewrittenQueryHash = None
            }

            let sw = Diagnostics.Stopwatch.StartNew()

            for _ in 1..5 do
                do! (tracer :> IRetrievalTracer).Trace trace ctxU

            sw.Stop()

            Expect.isLessThan sw.ElapsedMilliseconds 1000L "emitting never waits on the writer"
            Expect.isGreaterThanOrEqual tracer.Lost 3L "a full queue drops and counts (one in the writer, one queued)"

            release.SetResult()
            Expect.isTrue (tracer.Flush(TimeSpan.FromSeconds 5.0)) "the queue drains once the writer is free"
            Expect.equal (int64 written + tracer.Lost) 5L "every emission is either written or counted lost"
        }
    ]

/// An in-memory store that also implements the Phase 892 batch surface,
/// counting batches.
type private BatchingVectorStore(blob: IBlobStorage) =
    inherit ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(blob, logger = noopLogger, flushIntervalMs = 60000)
    let mutable batches = 0
    member _.Batches = batches

    interface IVectorStoreBatch with
        member this.UpsertBatch scope chunks = async {
            Interlocked.Increment(&batches) |> ignore

            for id, v, c in chunks do
                do! (this :> IVectorStore).Upsert scope id v c
        }

let private noopEventStore () =
    ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> IEventStore

let private startService
    (q: IngestionQueue)
    (pipeline: IRetrievalPipeline)
    (observers: IIngestionStatusObserver list)
    (retry: IngestionRetryPolicy)
    =
    ToolUp.RAG.IngestionService.create
        q
        pipeline
        (DelayedEmbedder TimeSpan.Zero)
        (noopEventStore ())
        observers
        1
        noopLogger
        (ToolUp.RAG.RagTelemetry.createNoOp ())
        (ToolUp.Platform.Usage.NoOpUsageLog())
        (ToolUp.Platform.Usage.NoOpTeamQuotaPolicy())
        retry
        None
        (ToolUp.RAG.IngestionService.IngestionAlertState())
        (fun () -> None)
        ignore

/// Poll `condition` every 20 ms for up to `timeoutMs`.
let private waitUntil (timeoutMs: int) (condition: unit -> bool) = async {
    let sw = Diagnostics.Stopwatch.StartNew()

    while not (condition ()) && sw.ElapsedMilliseconds < int64 timeoutMs do
        do! Async.Sleep 20

    return condition ()
}

let private ingestionPath =
    testList "ingestion path" [
        testCaseAsync "reproduction: a pool of one and a queue of capacity two — the fourth enqueue is refused"
        <| async {
            let q = IngestionQueue(2)
            let release = Tasks.TaskCompletionSource<unit>()
            let mutable started = 0

            let pipeline =
                { new IRetrievalPipeline with
                    member _.Retrieve _ _ = async.Return []

                    member _.Index _ _ _ = async {
                        Interlocked.Increment(&started) |> ignore
                        do! release.Task |> Async.AwaitTask
                    }

                    member _.DeleteByScope _ = async.Return()
                }

            use svc = startService q pipeline [] IngestionRetryPolicy.defaults
            use cts = new CancellationTokenSource()

            do!
                (svc :> Microsoft.Extensions.Hosting.IHostedService).StartAsync cts.Token
                |> Async.AwaitTask

            try
                Expect.isTrue (q.Enqueue(mkJob "d1")) "the first document is accepted"
                let! running = waitUntil 5000 (fun () -> started = 1 && q.Count = 0)
                Expect.isTrue running "the one worker took the first document"

                let accepted = [ for i in 2..10 -> q.Enqueue(mkJob (sprintf "d%d" i)) ]

                Expect.equal
                    accepted
                    [ true; true; false; false; false; false; false; false; false ]
                    "two wait in the queue; the fourth enqueue and every one after it is refused"

                Expect.equal q.Count 2 "the queue's depth is what is waiting"
                Expect.equal started 1 "and only the running document holds the worker"
            finally
                release.TrySetResult() |> ignore
                cts.Cancel()

            do!
                (svc :> Microsoft.Extensions.Hosting.IHostedService).StopAsync CancellationToken.None
                |> Async.AwaitTask
        }

        testCaseAsync "an in-process retry releases its slot while it sleeps"
        <| async {
            let q = IngestionQueue(10)
            let indexed = Collections.Concurrent.ConcurrentQueue<string>()
            let mutable firstFailed = 0

            let pipeline =
                { new IRetrievalPipeline with
                    member _.Retrieve _ _ = async.Return []

                    member _.Index chunkId _ _ = async {
                        if chunkId.StartsWith "a:" && Interlocked.Exchange(&firstFailed, 1) = 0 then
                            raise (TimeoutException "transient")

                        indexed.Enqueue chunkId
                    }

                    member _.DeleteByScope _ = async.Return()
                }

            // Attempt 2 waits InitialBackoff × 2 = 2 s. No scheduler is
            // composed, so the in-process retry loop runs it.
            let retry: IngestionRetryPolicy = {
                MaxAttempts = 2
                InitialBackoff = TimeSpan.FromSeconds 1.0
                MaxBackoff = TimeSpan.FromSeconds 1.0
                JitterFactor = 0.0
            }

            use svc = startService q pipeline [] retry
            use cts = new CancellationTokenSource()

            do!
                (svc :> Microsoft.Extensions.Hosting.IHostedService).StartAsync cts.Token
                |> Async.AwaitTask

            try
                Expect.isTrue (q.Enqueue(mkJob "a")) "a accepted"
                let! failedOnce = waitUntil 5000 (fun () -> firstFailed = 1)
                Expect.isTrue failedOnce "a's first attempt failed and its retry is sleeping"
                Expect.isTrue (q.Enqueue(mkJob "b")) "b accepted"

                let! bDone = waitUntil 1500 (fun () -> Seq.contains "b:chunk:0" indexed)

                Expect.isTrue bDone "b was indexed while a's retry slept — the sleep held no worker slot"

                Expect.isFalse (Seq.contains "a:chunk:0" indexed) "a's retry had not yet run"

                let! aDone = waitUntil 5000 (fun () -> Seq.contains "a:chunk:0" indexed)
                Expect.isTrue aDone "a's retry re-acquired a slot and indexed"
            finally
                cts.Cancel()

            do!
                (svc :> Microsoft.Extensions.Hosting.IHostedService).StopAsync CancellationToken.None
                |> Async.AwaitTask
        }

        testCaseAsync "892.t6: a batch-capable store takes a document in one UpsertBatch round-trip"
        <| async {
            let blob = InMemoryBlobStorage() :> IBlobStorage
            use batchingStore = new BatchingVectorStore(blob)

            let pipeline =
                RetrievalPipeline(batchingStore, DelayedEmbedder TimeSpan.Zero) :> IRetrievalPipeline

            let mutable indexedEvents = 0

            let observer =
                { new IIngestionStatusObserver with
                    member _.OnChunkIndexed _ = async { Interlocked.Increment(&indexedEvents) |> ignore }
                    member _.OnChunkFailed(_, _) = async.Return()
                }

            let q = IngestionQueue(10)
            use svc = startService q pipeline [ observer ] IngestionRetryPolicy.defaults
            use cts = new CancellationTokenSource()

            do!
                (svc :> Microsoft.Extensions.Hosting.IHostedService).StartAsync cts.Token
                |> Async.AwaitTask

            try
                let doc = {
                    mkJob "doc" with
                        Chunks = [
                            for i in 0..2 ->
                                sprintf "doc:%d" i,
                                {
                                    Content = sprintf "c%d" i
                                    Metadata = Map.empty
                                }
                        ]
                }

                Expect.isTrue (q.Enqueue doc) "accepted"
                let! done' = waitUntil 5000 (fun () -> indexedEvents = 3)
                Expect.isTrue done' "every chunk is reported indexed"
                Expect.equal batchingStore.Batches 1 "one batched round-trip for the document"

                let! stored = (batchingStore :> IVectorStore).ListChunks Deployment false
                Expect.equal stored.Length 3 "every chunk of the document is stored"
            finally
                cts.Cancel()

            do!
                (svc :> Microsoft.Extensions.Hosting.IHostedService).StopAsync CancellationToken.None
                |> Async.AwaitTask
        }
    ]

let private phase894 =
    testList "Phase 894 — a chat turn's latency budget and a real ingestion capacity" [ queryPath; ingestionPath ]

// ─── Phase 945 — the follow-ups Phase 894 recorded ─────────────────
//
// (a) the query policy governs the embedder's retries, (b) a lost
// retrieval trace is visible on `/health/rag`, (c) the drain loop waits
// for work without holding a worker permit. Each was pinned red on the
// pre-change tree before the change that turns it green.

/// Answers each request from a script indexed by request number (0-based)
/// and counts the requests that reached the wire.
type private ScriptedHandler(respond: int -> System.Net.Http.HttpResponseMessage) =
    inherit System.Net.Http.HttpMessageHandler()
    let mutable requests = 0
    member _.Requests = Volatile.Read(&requests)

    override _.SendAsync(_request, _ct) =
        let n = Interlocked.Increment(&requests) - 1
        Tasks.Task.FromResult(respond n)

let private embeddingOk () =
    new System.Net.Http.HttpResponseMessage(
        Net.HttpStatusCode.OK,
        Content =
            new System.Net.Http.StringContent(
                "{\"data\":[{\"index\":0,\"embedding\":[0.5,0.5,0.5]}]}",
                Text.Encoding.UTF8,
                "application/json"
            )
    )

let private embeddingUnavailable () =
    new System.Net.Http.HttpResponseMessage(
        Net.HttpStatusCode.ServiceUnavailable,
        Content = new System.Net.Http.StringContent("upstream is down")
    )

/// Fails twice with a 503, then answers.
let private failsTwice (n: int) =
    if n < 2 then embeddingUnavailable () else embeddingOk ()

let private keyStore =
    { new ToolUp.Platform.Secrets.ISecretStore with
        member _.GetSecret(_, _) = async { return Some "sk-test" }
        member _.SetSecret(_, _, _) = async { return Error "read-only" }
        member _.DeleteSecret(_, _) = async { return Ok() }
        member _.ListKeys _ = async { return [] }
    }

/// An OpenAI embedder over `handler` with three attempts and `backoff`
/// before the second (doubling before the third), no jitter.
let private openAiOverWith (backoff: TimeSpan) (handler: ScriptedHandler) =
    let client =
        new System.Net.Http.HttpClient(handler, BaseAddress = Uri("https://api.openai.invalid"))

    OpenAIEmbeddingProvider.OpenAIEmbeddingOptions.defaults
    |> OpenAIEmbeddingProvider.withEmbedderModel "test-embedding-model" 3
    |> OpenAIEmbeddingProvider.withEmbedderRetryPolicy {
        MaxAttempts = 3
        InitialBackoff = backoff
        MaxBackoff = TimeSpan.FromSeconds 30.0
        JitterFactor = 0.0
    }
    |> OpenAIEmbeddingProvider.createWithClient client keyStore

/// The provider's own retry sequence is 400 ms then 800 ms of backoff —
/// 1.2 s, longer than the query budgets below.
let private openAiOver = openAiOverWith (TimeSpan.FromMilliseconds 400.0)

/// A provider that retries internally (three attempts by default) over a
/// transport failing its first two requests, and honours a per-call
/// override — the shape of an API-backed provider, without the network.
type private RetryingFake() =
    let mutable requests = 0
    member _.Requests = Volatile.Read(&requests)

    member private _.Call(attempts: int) = async {
        let mutable result = None
        let mutable attempt = 1

        while result.IsNone do
            let n = Interlocked.Increment(&requests) - 1

            if n >= 2 then
                result <- Some unitVec
            elif attempt >= attempts then
                raise (TimeoutException "transient")
            else
                attempt <- attempt + 1

        return result.Value
    }

    interface IEmbeddingProvider with
        member this.GenerateEmbedding _ = this.Call 3

        member this.GenerateEmbeddings texts =
            batchedFallback (this :> IEmbeddingProvider).GenerateEmbedding texts

        member _.Dimensions = 8
        member _.ProviderId = "test"
        member _.ModelId = "retrying-v1"

    interface IEmbeddingProviderCallOverride with
        member this.GenerateEmbeddingWith(callOverride, _) =
            this.Call(max 1 callOverride.Retry.MaxAttempts)

let private embedderRetries =
    testList "(a) the query policy governs the embedder's retries" [
        testCaseAsync "a query embed makes the attempts its policy names, decides inside its budget, and frees its slot"
        <| async {
            let handler = ScriptedHandler failsTwice

            // The composed shape: the provider behind the caching decorator.
            let embedder =
                ToolUp.RAG.CachingEmbeddingProvider.create
                    (openAiOver handler)
                    (new ToolUp.RAG.InMemoryEmbeddingCache.InMemoryEmbeddingCache())

            let gate =
                QueryEmbedGate {
                    MaxAttempts = 1
                    Timeout = TimeSpan.FromMilliseconds 300.0
                    MaxConcurrentCalls = Some 1
                }

            let! first = gate.Embed embedder "first query"

            Expect.equal
                (QueryEmbedOutcome.label first)
                "Failed"
                "the query policy's one attempt failed and said so, rather than the provider retrying until the budget ran out"

            let! second = gate.Embed embedder "second query"

            Expect.notEqual
                (QueryEmbedOutcome.label second)
                "Refused"
                "the first embed's slot was free once it answered — no provider retry was still holding it"

            // Long enough for the provider's own sequence (1.2 s) to have run.
            do! Async.Sleep 1600

            Expect.equal handler.Requests 2 "one request per query embed: the provider did not retry on the query path"
        }

        testCaseAsync "ingestion keeps the provider's own retry policy"
        <| async {
            let handler = ScriptedHandler failsTwice
            let! vector = (openAiOver handler).GenerateEmbedding "a chunk"
            Expect.equal vector.Length 3 "the third attempt answered"
            Expect.equal handler.Requests 3 "the provider retried twice, as configured"
        }

        IEmbeddingProviderCallOverrideContract.tests "OpenAIEmbeddingProvider" (fun () ->
            let handler = ScriptedHandler failsTwice

            {
                Provider = openAiOverWith (TimeSpan.FromMilliseconds 1.0) handler
                Requests = fun () -> handler.Requests
            })

        IEmbeddingProviderCallOverrideContract.tests "CachingEmbeddingProvider" (fun () ->
            let inner = RetryingFake()

            {
                Provider =
                    ToolUp.RAG.CachingEmbeddingProvider.create
                        inner
                        (new ToolUp.RAG.InMemoryEmbeddingCache.InMemoryEmbeddingCache())
                Requests = fun () -> inner.Requests
            })
    ]

let private stubAiFactory =
    { new ToolUp.AI.IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Error ToolUp.AI.NoProviderConfigured }
        member _.TryResolveByLabel(_, _) = async { return Error ToolUp.AI.NoProviderConfigured }
        member _.BuildPlatform(_, _, _) = None
    }

let private stubProviderProfile =
    { new ToolUp.Platform.Providers.IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }
    }

/// GET `/health/rag` against `services` and parse the body.
let private healthRag (services: IServiceProvider) = async {
    let ctx = Microsoft.AspNetCore.Http.DefaultHttpContext()
    ctx.RequestServices <- services
    let body = new IO.MemoryStream()
    ctx.Response.Body <- body

    let! _ =
        ToolUp.RAG.RagHealthHandler.healthHandler (fun c -> Tasks.Task.FromResult(Some c)) ctx
        |> Async.AwaitTask

    return Text.Json.Nodes.JsonNode.Parse(Text.Encoding.UTF8.GetString(body.ToArray()))
}

let private lostTraces =
    testList "(b) lost traces are visible" [
        testCaseAsync "a full trace queue's losses are reported on /health/rag"
        <| async {
            let release = Tasks.TaskCompletionSource<unit>()

            // A trace writer that never finishes until released, so a
            // queue of one fills at once.
            let blocking =
                { new IRetrievalTracer with
                    member _.Trace _ _ = async { do! release.Task |> Async.AwaitTask }
                    member _.Miss _ _ = async { do! release.Task |> Async.AwaitTask }
                }

            let app =
                ToolUp.RAG.RAGCompose.RAGServerApp.create
                    stubAiFactory
                    stubProviderProfile
                    (DelayedEmbedder TimeSpan.Zero)
                |> ToolUp.RAG.RAGCompose.RAGServerApp.withStorage (InMemoryBlobStorage() :> IBlobStorage)
                |> ToolUp.RAG.RAGCompose.RAGServerApp.withRetrievalTraceQueueCapacity 1

            let composed = ToolUp.RAG.RAGCompose.composeRAG app

            let services =
                Microsoft.Extensions.DependencyInjection.ServiceCollection()
                :> Microsoft.Extensions.DependencyInjection.IServiceCollection

            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IRetrievalTracer>(
                services,
                blocking
            )
            |> ignore

            let services =
                match composed.Extensions.ServiceConfig with
                | Some configure -> configure services
                | None -> services

            use sp =
                Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider
                    services

            try
                let pipeline = sp.GetService(typeof<IRetrievalPipeline>) :?> IRetrievalPipeline

                for i in 1..5 do
                    let! _ =
                        pipeline.Retrieve
                            (RetrievalRequest.create (sprintf "query %d" i) [ User "u" ] 5 Interleaved)
                            ctxU

                    ()

                let! health = healthRag sp
                let traces = health["RetrievalTraces"]

                Expect.isNotNull traces "/health/rag carries the retrieval-trace block"

                // An int64 rides as a JSON string under the Fable converters,
                // as `IngestionQueueDrops.Cumulative` does.
                Expect.isGreaterThanOrEqual
                    (int64 (traces["Lost"].ToString()))
                    3L
                    "five traces through a queue of one: at least three were lost, and the count says so"

                Expect.equal (traces["Capacity"].GetValue<int>()) 1 "the configured queue bound"
            finally
                release.TrySetResult() |> ignore
        }
    ]

let private queueWaits =
    testList "(c) the ingestion queue can wait for work" [
        for durable in [ false; true ] do
            let arm = if durable then "durable" else "in-memory"

            let newQueue () =
                if durable then
                    IngestionQueue(
                        10,
                        store = InMemoryIngestionQueueStore(),
                        claimPollInterval = TimeSpan.FromMilliseconds 20.0
                    )
                else
                    IngestionQueue(10)

            testCaseAsync (sprintf "%s: WaitForWork completes when a job arrives and takes nothing" arm)
            <| async {
                let q = newQueue ()
                let iq = q :> IIngestionQueue
                use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)

                let! empty = iq.TryDequeue()
                Expect.isNone empty "nothing to take from an empty queue, and no wait"

                let waiting = Async.StartAsTask(iq.WaitForWork cts.Token)
                do! Async.Sleep 150
                Expect.isFalse waiting.IsCompleted "an empty queue keeps the waiter waiting"

                Expect.isTrue (q.Enqueue(mkJob "a")) "accepted"
                let! woke = waiting |> Async.AwaitTask
                Expect.isTrue woke "the waiter wakes for the job"
                Expect.equal q.Count 1 "waiting took nothing: the job is still in the queue's accounting"

                let! taken = iq.TryDequeue()
                Expect.equal (taken |> Option.map _.Job.DocumentId) (Some "a") "the job is taken by TryDequeue"
                do! iq.Ack taken.Value.LeaseId
                Expect.equal q.Count 0 "and then it has left the queue"
            }

            testCaseAsync (sprintf "%s: WaitForWork answers false on cancellation" arm)
            <| async {
                let iq = newQueue () :> IIngestionQueue
                use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)
                let! woke = iq.WaitForWork cts.Token
                Expect.isFalse woke "cancelled with no work"
            }
    ]

let private phase945 =
    testList "Phase 945 — RAG operations follow-ups" [ embedderRetries; lostTraces; queueWaits ]

let tests =
    testList "Phase 303 — ingestion-queue backpressure observability" [
        queueCounters
        dropEmission
        phase894
        phase945
    ]
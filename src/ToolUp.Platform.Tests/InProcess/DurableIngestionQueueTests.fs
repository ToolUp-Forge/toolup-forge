module ToolUp.Platform.Tests.InProcess.DurableIngestionQueueTests

// ─── Phase 509 — durable ingestion queue: seam + store contract ──────
//
// The shipped ingestion queue is a process-local `Channels` channel, so
// a restart mid-ingestion loses every queued document and
// `RagIngestionInstanceValidator` refuses `ReplicaCount > 1`. Phase 509
// puts an `IIngestionQueue` seam over the queue and an
// `IIngestionQueueStore` under it, so a durable backing can be composed.
//
// Three arms:
//
//  • **Seam / back-compat (always on).** With no store composed the queue
//    behaves exactly as before — same capacity refusal, same depth gauge
//    — and reports `IsDurable = false`, which is what keeps the
//    multi-replica refusal firing (GP 11 / GP 13).
//
//  • **Store contract (always on).** The properties the acceptance rests
//    on, pinned against `InMemoryIngestionQueueStore` — the reference
//    implementation of the same contract the Redis companion implements:
//      - a claim is ATOMIC, so N drainers over ONE queue never process a
//        document twice (a real concurrency test, not a comment);
//      - an unacknowledged lease is reclaimed and REDELIVERED, so a
//        drainer that dies mid-document loses nothing;
//      - redelivery is attempt-capped, so a poison document is dropped
//        and counted rather than spinning a replica forever.
//
//  • **Live Redis (env-gated on `TOOLUP_REDIS_CONNECTION`).** The same
//    contract against a real Redis, where the atomicity is `LMOVE`
//    rather than a lock. Reported **Pending** when the variable is
//    unset, so a fresh checkout is green without a broker — the posture
//    every sibling Redis pack takes, and no Docker requirement.

open System
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.IngestionTypes

let private run = Async.RunSynchronously

[<Literal>]
let private LiveConnectionEnvVar = "TOOLUP_REDIS_CONNECTION"

let private liveConnectionString =
    match Environment.GetEnvironmentVariable LiveConnectionEnvVar with
    | null
    | "" -> None
    | s -> Some s

let private mkJob (docId: string) : DocumentIngestionJob = {
    DocumentId = docId
    DocumentName = docId + ".pdf"
    Chunks = [
        docId + ":chunk:0",
        {
            Content = "body of " + docId
            Metadata = Map.empty
        }
    ]
    Scope = Deployment
    ScopeId = "scope-1"
    Container = "deployment"
    OriginatingUserId = Some "user-7"
}

// ─── Seam / back-compat arm ──────────────────────────────────────────

let private seamTests =
    testList "IIngestionQueue seam — in-memory default unchanged" [
        test "no store composed ⇒ IsDurable = false and the channel backing is named" {
            let q = IngestionQueue(4)

            Expect.isFalse
                q.IsDurable
                "the default queue is NOT durable — this is what keeps the multi-replica refusal firing"

            Expect.equal q.BackingName "in-memory-channel" "backing named for health output"
        }

        test "capacity refusal is unchanged through the seam" {
            let q = IngestionQueue(2) :> IIngestionQueue
            Expect.isTrue (run (q.EnqueueAsync(mkJob "a"))) "first accepted"
            Expect.isTrue (run (q.EnqueueAsync(mkJob "b"))) "second accepted"

            Expect.isFalse
                (run (q.EnqueueAsync(mkJob "c")))
                "third rejected at capacity — the caller's backpressure contract"
        }

        test "Dequeue hands back the enqueued job and drops the depth gauge" {
            let concrete = IngestionQueue(4)
            let q = concrete :> IIngestionQueue
            Expect.isTrue (run (q.EnqueueAsync(mkJob "a"))) "enqueued"
            Expect.equal concrete.Count 1 "depth gauge sees the pending job"

            let lease =
                Expect.wantSome (run (q.Dequeue CancellationToken.None)) "a queued job must be dequeued"

            Expect.equal lease.Job.DocumentId "a" "the same job comes back"
            Expect.equal lease.Attempt 1 "first delivery"
            Expect.equal concrete.Count 0 "depth gauge drops at dequeue, as it always has"
        }

        test "Ack / Abandon / RecoverStranded are inert on the channel (nothing to redeliver from)" {
            let q = IngestionQueue(4) :> IIngestionQueue
            Expect.isTrue (run (q.EnqueueAsync(mkJob "a"))) "enqueued"

            let lease = Expect.wantSome (run (q.Dequeue CancellationToken.None)) "dequeued"

            run (q.Abandon(lease.LeaseId, "drainer died"))

            Expect.equal
                (run (q.RecoverStranded()))
                0
                "a process-local channel has no stranded state to recover — it died with the process, which is exactly the gap the durable arm closes"
        }
    ]

// ─── Store-contract arm ──────────────────────────────────────────────

/// The contract cases, parameterised over a store factory so the same
/// assertions run against `InMemoryIngestionQueueStore` and (env-gated)
/// the Redis companion. `label` names the arm in the report.
let private storeContract
    (label: string)
    (newStore: unit -> IIngestionQueueStore)
    (cleanup: IIngestionQueueStore -> unit)
    =
    testList label [
        test "enqueue then claim returns the job; complete removes it" {
            let store = newStore ()

            try
                Expect.isTrue (run (store.Enqueue(mkJob "a", 10))) "accepted"
                Expect.equal (run (store.Depth())) 1 "one job held"

                let lease =
                    Expect.wantSome (run (store.Claim(TimeSpan.FromMinutes 1.0))) "claimable"

                Expect.equal lease.Job.DocumentId "a" "the payload round-trips through the store"
                Expect.equal lease.Attempt 1 "first delivery"
                Expect.equal (run (store.Depth())) 1 "in-flight still counts against depth"

                run (store.Complete lease.LeaseId)
                Expect.equal (run (store.Depth())) 0 "completed job is gone"
                Expect.isNone (run (store.Claim(TimeSpan.FromMinutes 1.0))) "nothing left to claim"
            finally
                cleanup store
        }

        test "capacity is enforced over pending + in-flight" {
            let store = newStore ()

            try
                Expect.isTrue (run (store.Enqueue(mkJob "a", 2))) "first accepted"
                Expect.isTrue (run (store.Enqueue(mkJob "b", 2))) "second accepted"
                Expect.isFalse (run (store.Enqueue(mkJob "c", 2))) "third refused at capacity"

                // Claiming does not free a slot — the job is still the
                // store's responsibility until it is acknowledged.
                store.Claim(TimeSpan.FromMinutes 1.0) |> run |> ignore
                Expect.isFalse (run (store.Enqueue(mkJob "d", 2))) "an in-flight job still occupies its slot"
            finally
                cleanup store
        }

        test "RESTART: a claim that is never acknowledged is reclaimed and redelivered — no document lost" {
            let store = newStore ()

            try
                for docId in [ "a"; "b"; "c" ] do
                    Expect.isTrue (run (store.Enqueue(mkJob docId, 10))) "accepted"

                // A drainer claims one document and then "dies" — no
                // Complete, no Release, exactly what a killed process
                // leaves behind.
                let orphan =
                    Expect.wantSome (run (store.Claim(TimeSpan.FromMilliseconds 50.0))) "claimable"

                Thread.Sleep 400

                let reclaimed = run (store.ReclaimExpired())
                Expect.equal reclaimed 1 "the stranded lease is reclaimed"

                // Everything that was ever enqueued is still drainable,
                // including the document the dead drainer was holding.
                let drained =
                    [
                        for _ in 1..3 do
                            match run (store.Claim(TimeSpan.FromMinutes 1.0)) with
                            | Some lease ->
                                run (store.Complete lease.LeaseId)
                                yield lease.Job.DocumentId
                            | None -> ()
                    ]
                    |> List.sort

                Expect.equal drained [ "a"; "b"; "c" ] "every document survives the restart, the orphaned one included"

                Expect.equal (run (store.Depth())) 0 "queue fully drained"

                // The redelivered document carries a higher attempt
                // number — the store counts deliveries, which is what
                // bounds a poison message.
                Expect.equal orphan.Attempt 1 "the lost delivery was the first"
            finally
                cleanup store
        }

        test "TWO REPLICAS, ONE QUEUE: concurrent drainers never process a document twice" {
            let store = newStore ()

            try
                let total = 120

                for i in 1..total do
                    Expect.isTrue (run (store.Enqueue(mkJob (sprintf "doc-%03d" i), total * 2))) "accepted"

                let gate = obj ()
                let processed = ResizeArray<string>()

                // Four drainers across two "replicas" (two IIngestionQueue
                // instances over ONE store), all racing for the same jobs.
                // If `Claim` were not atomic — a peek followed by a
                // remove, say — two drainers would see the same head and
                // the distinct-count assertion below would fail.
                let replicaA =
                    IngestionQueue(total * 2, DropWrite, store, TimeSpan.FromMinutes 1.0, TimeSpan.FromMilliseconds 5.0)
                    :> IIngestionQueue

                let replicaB =
                    IngestionQueue(total * 2, DropWrite, store, TimeSpan.FromMinutes 1.0, TimeSpan.FromMilliseconds 5.0)
                    :> IIngestionQueue

                use cts = new CancellationTokenSource()
                cts.CancelAfter(TimeSpan.FromSeconds 30.0)

                let drainer (queue: IIngestionQueue) = async {
                    let mutable go = true

                    while go && not cts.IsCancellationRequested do
                        match! queue.Dequeue cts.Token with
                        | Some lease ->
                            // Hold the job briefly so the drainers
                            // genuinely overlap rather than serialising.
                            do! Async.Sleep 1

                            let count =
                                lock gate (fun () ->
                                    processed.Add lease.Job.DocumentId
                                    processed.Count)

                            do! queue.Ack lease.LeaseId

                            if count >= total then
                                cts.Cancel()
                        | None -> go <- false
                }

                [| drainer replicaA; drainer replicaB; drainer replicaA; drainer replicaB |]
                |> Async.Parallel
                |> Async.RunSynchronously
                |> ignore

                let drained = lock gate (fun () -> List.ofSeq processed)

                Expect.equal drained.Length total "every document was drained exactly once in total"

                Expect.equal
                    (drained |> List.distinct |> List.length)
                    total
                    "NO document was processed twice — the atomic claim is what makes multi-replica draining safe"

                Expect.equal (run (store.Depth())) 0 "the shared queue is empty"
            finally
                cleanup store
        }

        test "redelivery is attempt-capped: a poison document is dropped rather than spun forever" {
            let store = newStore ()

            try
                Expect.isTrue (run (store.Enqueue(mkJob "poison", 10))) "accepted"

                // Deliver-and-abandon until the store gives up. The cap is
                // small (2 in-memory / 3 Redis) but the loop is bounded
                // well above either, so this asserts the cap EXISTS rather
                // than assuming its value.
                let mutable deliveries = 0
                let mutable draining = true

                while draining && deliveries < 20 do
                    match run (store.Claim(TimeSpan.FromMinutes 1.0)) with
                    | Some lease ->
                        deliveries <- deliveries + 1
                        run (store.Release lease.LeaseId)
                    | None -> draining <- false

                Expect.isLessThan
                    deliveries
                    20
                    "the store stopped redelivering — an uncapped queue would have spun to the loop bound"

                Expect.isGreaterThan deliveries 1 "it was redelivered at least once before being given up on"
                Expect.equal (run (store.Depth())) 0 "the poison document is gone, not stuck in the queue"
            finally
                cleanup store
        }
    ]

// ─── The validator lift (509.D) ──────────────────────────────────────

let private cfg (replicaCount: int) (escapeHatch: bool) : ServerConfig = {
    ServerConfig.defaults with
        ReplicaCount = replicaCount
        AcceptInProcessIngestionInMultiInstance = escapeHatch
}

let private validate (config: ServerConfig) (durableQueue: bool) : ValidationResult =
    let v =
        ToolUp.RAG.RagConfigValidator.RagIngestionInstanceValidator(config, durableQueue) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

let private validatorTests =
    testList "Phase 509.D — the multi-replica refusal is lifted by a durable queue, not removed" [
        test "in-memory queue + ReplicaCount = 1 → Ok" {
            Expect.equal (validate (cfg 1 false) false) Ok "single instance was always fine"
        }

        test "in-memory queue + ReplicaCount = 2 → Error (the refusal still fires)" {
            match validate (cfg 2 false) false with
            | Error msg ->
                Expect.stringContains msg "ReplicaCount = 2" "names the replica count"
                Expect.stringContains msg "process-local channel" "names why"

                Expect.stringContains
                    msg
                    "withDurableIngestionQueue"
                    "points at the fix this phase shipped, not just the escape hatch"

                Expect.stringContains msg "AcceptInProcessIngestionInMultiInstance" "still documents the escape hatch"
            | other -> failtestf "expected Error, got %A" other
        }

        test "DURABLE queue + ReplicaCount = 2 → Ok (the premise of the refusal no longer holds)" {
            Expect.equal
                (validate (cfg 2 false) true)
                Ok
                "a durable queue outlives the process and claims atomically, so multi-replica ingestion is safe"
        }

        test "escape hatch still works for a deployment that accepts best-effort ingestion" {
            Expect.equal (validate (cfg 2 true) false) Ok "explicit opt-in unchanged"
        }
    ]

// ─── Composition: a durable store makes the composed queue durable ───

let private compositionTests =
    testList "composed queue reports its backing" [
        test "a store composed ⇒ IsDurable = true and the backing is named" {
            let store = InMemoryIngestionQueueStore() :> IIngestionQueueStore
            let q = IngestionQueue(10, DropWrite, store)
            Expect.isTrue q.IsDurable "the composed queue is durable"
            Expect.equal q.BackingName "in-memory" "reports the store's own name"
        }

        test "an enqueue through the queue lands in the store, and vice versa" {
            let store = InMemoryIngestionQueueStore() :> IIngestionQueueStore

            let q =
                IngestionQueue(10, DropWrite, store, TimeSpan.FromMinutes 1.0, TimeSpan.FromMilliseconds 5.0)
                :> IIngestionQueue

            Expect.isTrue (run (q.EnqueueAsync(mkJob "a"))) "enqueued through the queue"

            Expect.equal
                (run (store.Depth()))
                1
                "the store holds it — the queue is not buffering behind the store's back"

            // A job put straight into the store is visible to the queue —
            // this is the shape a SIBLING replica's enqueue takes.
            Expect.isTrue (run (store.Enqueue(mkJob "b", 10))) "sibling replica enqueued"

            let drained =
                [
                    for _ in 1..2 do
                        match run (q.Dequeue CancellationToken.None) with
                        | Some lease ->
                            run (q.Ack lease.LeaseId)
                            yield lease.Job.DocumentId
                        | None -> ()
                ]
                |> List.sort

            Expect.equal drained [ "a"; "b" ] "this replica drains its own AND the sibling's document"
        }

        test "RecoverStranded on the composed queue reclaims an expired lease" {
            let store = InMemoryIngestionQueueStore() :> IIngestionQueueStore

            let q =
                IngestionQueue(10, DropWrite, store, TimeSpan.FromMilliseconds 50.0, TimeSpan.FromMilliseconds 5.0)
                :> IIngestionQueue

            Expect.isTrue (run (q.EnqueueAsync(mkJob "a"))) "enqueued"

            // Dequeue and never Ack — the process "dies" here.
            run (q.Dequeue CancellationToken.None) |> ignore
            Thread.Sleep 400

            Expect.equal (run (q.RecoverStranded())) 1 "the stranded lease is reclaimed by the restarted replica"

            let lease =
                Expect.wantSome (run (q.Dequeue CancellationToken.None)) "and the document is drainable again"

            Expect.equal lease.Job.DocumentId "a" "no document lost across the restart"
            Expect.equal lease.Attempt 2 "the redelivery is counted"
        }
    ]

// ─── Live Redis arm ──────────────────────────────────────────────────

let private liveRedisTests (connectionString: string) =
    // A GUID-suffixed prefix per store so concurrent runs against one
    // shared Redis cannot interfere, and a `MaxDeliveryAttempts` low
    // enough that the poison-document case terminates quickly.
    let newStore () =
        let options = {
            ToolUp.RAG.IngestionQueues.Redis.RedisIngestionQueueStore.RedisIngestionQueueOptions.defaults with
                KeyPrefix = sprintf "toolup:test:ingestion:%s" (Guid.NewGuid().ToString "N")
                MaxDeliveryAttempts = 3
        }

        ToolUp.RAG.IngestionQueues.Redis.RedisIngestionQueueStore.createWith connectionString options None

    let cleanup (store: IIngestionQueueStore) =
        // Drain whatever is left so the test prefix does not linger.
        let mutable draining = true

        while draining do
            match run (store.Claim(TimeSpan.FromSeconds 1.0)) with
            | Some lease -> run (store.Complete lease.LeaseId)
            | None -> draining <- false

        match box store with
        | :? IDisposable as d -> d.Dispose()
        | _ -> ()

    storeContract "live Redis store" newStore cleanup

[<Tests>]
let tests =
    testList "Phase 509 — durable ingestion queue" [
        seamTests
        compositionTests
        storeContract
            "in-memory reference store"
            (fun () -> InMemoryIngestionQueueStore(2) :> IIngestionQueueStore)
            ignore
        validatorTests

        match liveConnectionString with
        | Some connectionString -> liveRedisTests connectionString
        | None ->
            // A single Pending case so the report lists the live arm as
            // skipped rather than silently omitting it.
            testList "live Redis store" [ ptestCase $"skipped — {LiveConnectionEnvVar} not set" <| fun _ -> () ]
    ]
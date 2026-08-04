module ToolUp.RAG.IngestionTypes

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Ingestion job record ─────────────────────────────────────────

/// Per-chunk job descriptor passed to `IIngestionStatusObserver` callbacks.
/// Documents are queued as `DocumentIngestionJob`s (one per upload), but
/// observers receive an `IngestionJob` per chunk so the existing progress
/// UX (which is per-chunk) is unchanged.
type IngestionJob = {
    DocumentId: string
    DocumentName: string
    ChunkId: string
    Chunk: TextChunk
    Scope: VectorScope
    /// Scope ID used for event emission (matches the container's team/user ID).
    ScopeId: string
    /// Storage container the source document lives in (e.g. `team-{id}` or
    /// `user-{id}`). Carried so `IIngestionStatusObserver` implementations
    /// can locate the document's persisted index without re-deriving the
    /// container from convention.
    Container: string
    /// User id of the principal that originated this enqueue, as
    /// resolved from the request's `AccessContext`. Observers that
    /// publish per-user notifications cross-check this against the
    /// persisted `UploadedBy` field before publishing, so a stale
    /// or spoofed index entry cannot redirect notifications to the
    /// wrong user. `None` when the enqueue came from a non-user
    /// path (e.g. the SDK's post-save vectorisation hook); observers
    /// treat `None` as "no re-auth, fall back to historical
    /// publish-to-UploadedBy" for backwards compatibility.
    OriginatingUserId: string option
}

/// Per-document job carrying every chunk produced by a single upload. The
/// background service issues one batched embedding call covering all
/// `Chunks`, then indexes each chunk individually against the (now-hot)
/// embedding cache. A 100-chunk document goes from ~100 sequential HTTP
/// embedding calls to one batched call plus 100 cache hits.
type DocumentIngestionJob = {
    DocumentId: string
    DocumentName: string
    /// Chunks in document order. The pair carries the synthesised chunk ID
    /// (e.g. `"{fileName}:chunk:{i}"`) alongside the chunk payload so the
    /// queue producer doesn't have to re-derive ID conventions downstream.
    Chunks: (string * TextChunk) list
    Scope: VectorScope
    ScopeId: string
    Container: string
    /// User id of the originating principal. See `IngestionJob.OriginatingUserId`
    /// for the re-auth contract. `None` for non-user enqueue paths.
    OriginatingUserId: string option
}

// ─── Ingestion queue ──────────────────────────────────────────────

/// Phase 303 — behaviour when a `DocumentIngestionJob` is offered to a
/// full ingestion queue. Expressed as data (portability rule 3 — no
/// callbacks) so a future distributed ingestion companion can map the
/// same choice onto its native backpressure mechanism.
type IngestionOverflowPolicy =
    /// Default, back-compat behaviour. `Enqueue` returns `false` when the
    /// queue is full; the post-save hook retries with a short bounded
    /// backoff and, if still full, drops the document — emitting a
    /// `KnowledgeIngestionDropped` audit, a Warning `SystemMessage` to the
    /// uploader, and the queue `Dropped` counters. Favours upload
    /// responsiveness over never-drop.
    | DropWrite
    /// Never drop: the post-save hook awaits queue space
    /// (`EnqueueBlocking`) instead of retrying-then-dropping. The upload
    /// HTTP response has already returned (the hook is fire-and-forget), so
    /// blocking delays *indexing* under sustained load but never loses a
    /// document. Suited to bulk backfills where completeness beats latency.
    | Block
    /// Compliance-grade fail-loud. Behaves like `DropWrite` (drop + audit +
    /// notify) but additionally raises `ConfigPreflightFailedException` once
    /// sustained saturation is observed in the rolling 60s window — a loud,
    /// operator-visible signal (logged by the post-save-hook error path)
    /// that the deployment is losing documents, for deployments that prefer
    /// a screaming error over a quiet drop.
    | Refuse

// ─── Phase 509 — durable ingestion-queue seam ─────────────────────

/// One document job handed to a drainer under a lease. The lease is the
/// unit of at-least-once delivery: the drainer `Ack`s it when the
/// document's chunks have been reported, or `Abandon`s it so the job is
/// redelivered. Identity is by value (portability rule 1) — `LeaseId` is
/// a `string`, never a live handle, so a lease taken on one replica is
/// nameable on any other.
type IngestionLease = {
    /// Opaque, store-issued identity for this delivery. Only meaningful
    /// to the issuing store.
    LeaseId: string
    Job: DocumentIngestionJob
    /// 1-based delivery attempt. `1` is the first delivery; `2+` means a
    /// prior delivery was abandoned or its lease expired unacknowledged.
    Attempt: int
}

/// Durable backing store for the ingestion queue — the primitive a
/// companion implements (Redis today; any store with an atomic
/// pop-and-claim can implement it).
///
/// This is deliberately the *narrow* half of the seam: `IIngestionQueue`
/// carries the policy (capacity, overflow, telemetry counters) and this
/// carries only the durable operations. Satisfies the six portability
/// rules — identity by value, `Async` at every boundary, retry expressed
/// as data (the store's own delivery-attempt cap), no state carried in
/// the caller between calls, no cross-shard ordering promise beyond
/// per-store FIFO, and no sub-second precision claim.
type IIngestionQueueStore =
    /// Stable name for logs / health output (e.g. `"redis"`).
    abstract Name: string

    /// Append a document job. Returns `false` when the store already
    /// holds `capacity` jobs (pending + in-flight), so the caller's
    /// backpressure contract is identical to the in-memory channel's.
    abstract Enqueue: job: DocumentIngestionJob * capacity: int -> Async<bool>

    /// Atomically remove the head job and hold it under a lease for
    /// `leaseDuration`. `None` when the store is empty. **Atomicity is
    /// the load-bearing property** — it is what lets N replicas drain one
    /// queue without double-processing.
    abstract Claim: leaseDuration: TimeSpan -> Async<IngestionLease option>

    /// The job was processed. Removes it permanently.
    abstract Complete: leaseId: string -> Async<unit>

    /// The job was not processed. Returns it for redelivery, unless it
    /// has already used its delivery-attempt budget — in which case the
    /// store drops it and counts the drop (a job that crashes its
    /// drainer must not redeliver forever).
    abstract Release: leaseId: string -> Async<unit>

    /// Return every lease that expired without an `Ack` (the replica
    /// holding it died) for redelivery. Returns how many were reclaimed.
    /// This is the restart-recovery primitive: a crash mid-ingestion
    /// leaves an expired lease, and the next reclaim re-queues it.
    abstract ReclaimExpired: unit -> Async<int>

    /// Pending + in-flight jobs. A gauge, not a boundary.
    abstract Depth: unit -> Async<int>

/// The queue seam `IngestionBackgroundService` drains. The shipped
/// in-memory `IngestionQueue` is the default implementation; composing an
/// `IIngestionQueueStore` swaps the same type onto durable storage
/// (GP 11 — a deployment that composes nothing keeps the prior
/// behaviour byte for byte).
type IIngestionQueue =
    /// `true` when the queue is backed by a store that outlives this
    /// process. Read by `RagIngestionInstanceValidator` — the
    /// multi-replica refusal is lifted only for a durable queue.
    abstract IsDurable: bool
    abstract Capacity: int
    abstract Policy: IngestionOverflowPolicy

    /// Try to enqueue. `false` ⇒ at capacity (the caller surfaces the
    /// rejection; see `IngestionOverflowPolicy`).
    abstract EnqueueAsync: job: DocumentIngestionJob -> Async<bool>

    /// Await queue space, then enqueue. Never returns `false`.
    abstract EnqueueBlockingAsync: job: DocumentIngestionJob -> Async<unit>

    /// Await the next job. `None` only when `ct` is cancelled.
    abstract Dequeue: ct: CancellationToken -> Async<IngestionLease option>

    /// Report the lease's job as handled — it is removed permanently.
    abstract Ack: leaseId: string -> Async<unit>

    /// Report the lease's job as NOT handled. On a durable queue the job
    /// is redelivered (subject to the store's attempt cap); on the
    /// in-memory default there is nothing to redeliver from, so this
    /// records the reason and returns.
    abstract Abandon: leaseId: string * reason: string -> Async<unit>

    /// Reclaim leases stranded by a dead drainer. Returns how many were
    /// re-queued. Always `0` on the in-memory default (a process-local
    /// channel has no stranded state to recover — it died with the
    /// process).
    abstract RecoverStranded: unit -> Async<int>

/// Process-local `IIngestionQueueStore`, shared by every queue instance
/// constructed over it.
///
/// **Not durable across a process restart** — it is a dictionary. What it
/// *is*, is a faithful implementation of the store contract: atomic
/// claim, lease expiry, attempt-capped redelivery. That makes it (a) the
/// reference the contract is pinned against, and (b) the substrate a
/// single process can use to run several drainers over one queue. A
/// deployment that needs the queue to survive the process composes a
/// durable companion (`ToolUp.IngestionQueues.Redis`) instead.
type InMemoryIngestionQueueStore(?maxDeliveryAttempts: int, ?name: string) =
    let maxAttempts = max 1 (defaultArg maxDeliveryAttempts 3)
    let storeName = defaultArg name "in-memory"
    let gate = obj ()
    let pending = System.Collections.Generic.Queue<DocumentIngestionJob * int>()

    let inFlight =
        System.Collections.Generic.Dictionary<string, DocumentIngestionJob * int * DateTimeOffset>()

    let mutable dropped = 0

    /// Requeue or drop, per the attempt budget. Caller holds `gate`.
    let returnOrDrop (job: DocumentIngestionJob) (attempts: int) =
        if attempts >= maxAttempts then
            dropped <- dropped + 1
        else
            pending.Enqueue(job, attempts)

    /// Jobs dropped after exhausting their delivery-attempt budget.
    member _.Dropped = dropped

    /// Configured delivery-attempt cap.
    member _.MaxDeliveryAttempts = maxAttempts

    interface IIngestionQueueStore with
        member _.Name = storeName

        member _.Enqueue(job, capacity) = async {
            return
                lock gate (fun () ->
                    if pending.Count + inFlight.Count >= capacity then
                        false
                    else
                        pending.Enqueue(job, 0)
                        true)
        }

        member _.Claim(leaseDuration) = async {
            return
                lock gate (fun () ->
                    if pending.Count = 0 then
                        None
                    else
                        let job, attempts = pending.Dequeue()
                        let leaseId = Guid.NewGuid().ToString("N")
                        let attempt = attempts + 1
                        inFlight[leaseId] <- (job, attempt, DateTimeOffset.UtcNow + leaseDuration)

                        Some {
                            LeaseId = leaseId
                            Job = job
                            Attempt = attempt
                        })
        }

        member _.Complete(leaseId) = async { lock gate (fun () -> inFlight.Remove leaseId |> ignore) }

        member _.Release(leaseId) = async {
            lock gate (fun () ->
                match inFlight.TryGetValue leaseId with
                | true, (job, attempts, _) ->
                    inFlight.Remove leaseId |> ignore
                    returnOrDrop job attempts
                | _ -> ())
        }

        member _.ReclaimExpired() = async {
            return
                lock gate (fun () ->
                    let now = DateTimeOffset.UtcNow

                    let expired =
                        inFlight
                        |> Seq.filter (fun kv ->
                            let _, _, expiry = kv.Value
                            expiry <= now)
                        |> Seq.map _.Key
                        |> Seq.toList

                    for leaseId in expired do
                        let job, attempts, _ = inFlight[leaseId]
                        inFlight.Remove leaseId |> ignore
                        returnOrDrop job attempts

                    expired.Length)
        }

        member _.Depth() = async { return lock gate (fun () -> pending.Count + inFlight.Count) }

/// Thread-safe queue used to hand off whole documents from upload handlers
/// to the background ingestion service. Backed by a `System.Threading.Channels`
/// bounded channel so a 10k-document spike is rejected at the door rather
/// than silently buffering and quietly tripping the embedding provider's
/// rate limiter. Default capacity 5,000 pending document jobs — tune via
/// `RAGServerApp.withIngestionQueueCapacity` for deployments with bursty
/// uploads or stricter memory budgets.
///
/// Per-document granularity (rather than per-chunk) lets the background
/// service issue one batched embedding call per upload and amortise
/// round-trip latency over N inputs.
///
/// `Enqueue` returns `false` when the queue is full so callers can surface
/// the rejection — KB upload handlers mark the affected document `Failed`
/// rather than enqueue silently. A live-depth counter (`Count`) drives
/// telemetry under `/health/rag` so admins can see when backpressure
/// is active before users notice.
///
/// Phase 509 — an optional `store` swaps the process-local channel for a
/// durable backing (`IIngestionQueueStore`) WITHOUT changing this type's
/// surface, so every existing call site (`Enqueue` / `Count` / `Capacity`
/// / the drop counters) keeps working and a deployment that composes no
/// store is byte-for-byte unchanged (GP 11 / GP 13). The durable arm
/// additionally supports lease-based at-least-once delivery — see
/// `IIngestionQueue`.
type IngestionQueue
    (
        ?capacity: int,
        ?overflowPolicy: IngestionOverflowPolicy,
        ?store: IIngestionQueueStore,
        ?leaseDuration: TimeSpan,
        ?claimPollInterval: TimeSpan
    ) =
    let cap = defaultArg capacity 5000
    let policy = defaultArg overflowPolicy DropWrite

    // Phase 509 — how long a claimed job may go unacknowledged before
    // another drainer may reclaim it. Comfortably longer than a normal
    // document (batched embed + N index calls); short enough that a
    // crashed replica's work is picked up in a bounded window.
    let leaseDuration = defaultArg leaseDuration (TimeSpan.FromMinutes 10.0)

    // Poll interval for the durable claim loop. `Claim` is a
    // pull primitive (the store contract stays implementable by any
    // backend); a short poll keeps latency close to the channel's.
    let claimPollInterval =
        defaultArg claimPollInterval (TimeSpan.FromMilliseconds 100.0)

    // Phase 303 — drop observability. `RecordDrop` is called by the
    // enqueue-site (the post-save hook) each time a document is permanently
    // dropped after the bounded retry, so the counters reflect *final*
    // drops, not transient full-then-cleared enqueues. `droppedCumulative`
    // is the process-lifetime total; `dropWindow` is the rolling 60s window
    // surfaced on `/health/rag` so an operator sees active saturation
    // without trawling the audit trail.
    let mutable droppedCumulative = 0L
    let dropWindow = ConcurrentQueue<DateTimeOffset>()
    let dropWindowSpan = TimeSpan.FromSeconds 60.0

    let evictDropWindow () =
        let cutoff = DateTimeOffset.UtcNow - dropWindowSpan
        let mutable head = DateTimeOffset.MinValue

        while dropWindow.TryPeek(&head) && head < cutoff do
            dropWindow.TryDequeue(&head) |> ignore

    // `Wait` (not `DropWrite`): under `DropWrite`, `TryWrite` ALWAYS
    // returns `true` while silently discarding the incoming job when
    // full — which defeats the entire `Enqueue : bool` contract below
    // (callers mark the doc `Failed`, telemetry records the rejection,
    // `/health/rag` surfaces backpressure). Under `Wait`, the
    // *non-blocking* `TryWrite` correctly returns `false` when the
    // channel is full (the "wait" only applies to `WriteAsync`, which
    // this producer never calls), so a full queue is rejected loudly
    // and visibly instead of losing documents in silence.
    let channel =
        Channel.CreateBounded<DocumentIngestionJob>(
            BoundedChannelOptions(cap, SingleReader = true, FullMode = BoundedChannelFullMode.Wait)
        )

    let mutable depth = 0

    // Phase 509 — the enqueue paths, let-bound so the class members and
    // the `IIngestionQueue` implementation below are the SAME code rather
    // than two members that could drift.
    let enqueueAsync (job: DocumentIngestionJob) : Async<bool> = async {
        match store with
        | Some s ->
            let! accepted = s.Enqueue(job, cap)

            if accepted then
                Interlocked.Increment(&depth) |> ignore

            return accepted
        | None ->
            if channel.Writer.TryWrite(job) then
                Interlocked.Increment(&depth) |> ignore
                return true
            else
                return false
    }

    let enqueueBlocking (job: DocumentIngestionJob) : Async<unit> = async {
        match store with
        | Some _ ->
            // Durable arm: the store has no blocking append, so poll
            // until it accepts. `Block` means never drop, so this loops
            // rather than giving up. Runs only on the fire-and-forget
            // post-save path, so the wait never delays a response.
            let mutable accepted = false

            while not accepted do
                let! ok = enqueueAsync job
                accepted <- ok

                if not accepted then
                    do! Async.Sleep claimPollInterval
        | None ->
            do! channel.Writer.WriteAsync(job).AsTask() |> Async.AwaitTask
            Interlocked.Increment(&depth) |> ignore
    }

    /// Snapshot of pending document jobs in the queue. Updated by `Enqueue`
    /// (incremented before write) and the reader path (decremented after
    /// `ReadAsync` returns) — see `IngestionBackgroundService` for the
    /// decrement site. Atomicity of read-then-print-then-act is *not*
    /// guaranteed; treat as a depth gauge rather than a boundary.
    member _.Count = depth

    /// Configured maximum capacity. Useful for telemetry (`Count / Capacity`
    /// gives saturation) and for emitting `Retry-After` hints to clients.
    member _.Capacity = cap

    /// Try to enqueue a document. Returns `true` on success, `false` when the
    /// queue is at capacity. Callers that need user-visible feedback should
    /// surface the failure (KB marks the doc `Failed`); fire-and-forget hooks
    /// log a warning and move on.
    member _.Enqueue(job: DocumentIngestionJob) : bool =
        match store with
        | Some _ ->
            // Durable arm. The synchronous shape is preserved for the
            // existing call sites (KB's upload handlers); the round-trip
            // is one store append. New call sites should prefer
            // `EnqueueAsync`.
            enqueueAsync job |> Async.RunSynchronously
        | None ->
            if channel.Writer.TryWrite(job) then
                Interlocked.Increment(&depth) |> ignore
                true
            else
                false

    /// Async `Enqueue`. Identical semantics; the only correct shape on
    /// the durable arm, where the append is I/O.
    member _.EnqueueAsync(job: DocumentIngestionJob) : Async<bool> = enqueueAsync job

    member _.Reader = channel.Reader

    /// `true` when a durable store backs this queue. Drives the
    /// multi-replica validator lift (Phase 509.D).
    member _.IsDurable = store.IsSome

    /// The durable store's name, or `"in-memory-channel"` for the
    /// default. Surfaced on `/health/rag`-shaped output.
    member _.BackingName =
        match store with
        | Some s -> s.Name
        | None -> "in-memory-channel"

    /// Configured overflow policy (Phase 303). Read by the post-save hook
    /// to decide between drop-with-observability (`DropWrite` / `Refuse`)
    /// and never-drop blocking (`Block`).
    member _.Policy = policy

    /// Await queue space and enqueue (Phase 303 — `Block` policy). Uses the
    /// channel's blocking `WriteAsync`, so a full queue suspends the caller
    /// until the drainer frees a slot rather than returning `false`. Only
    /// called on the fire-and-forget post-save path, so the wait never
    /// delays an upload's HTTP response.
    member _.EnqueueBlocking(job: DocumentIngestionJob) : Async<unit> = enqueueBlocking job

    /// Record one permanently-dropped document (Phase 303). Increments the
    /// cumulative total and stamps the rolling 60s window.
    member _.RecordDrop() =
        System.Threading.Interlocked.Increment(&droppedCumulative) |> ignore
        dropWindow.Enqueue(DateTimeOffset.UtcNow)

    /// Process-lifetime count of documents dropped on a full queue
    /// (Phase 303).
    member _.Dropped = System.Threading.Interlocked.Read(&droppedCumulative)

    /// Documents dropped within the trailing 60 seconds (Phase 303).
    /// Evicts stale window entries on read, so it is a live saturation
    /// gauge for `/health/rag`.
    member _.DroppedLast60s =
        evictDropWindow ()
        dropWindow.Count

    /// Called by `IngestionBackgroundService` after a successful `ReadAsync`
    /// to keep the depth counter consistent with the underlying channel.
    /// Not part of the public surface for outside callers — kept `internal`
    /// in spirit by being undocumented in the README.
    member _.RecordDequeue() =
        System.Threading.Interlocked.Decrement(&depth) |> ignore

    // ─── Phase 509 — the drainer-facing seam ──────────────────────
    //
    // The in-memory arm reproduces the historical loop exactly: read one
    // job off the channel, decrement the depth gauge, hand it to the
    // caller. It issues a lease id so the drainer's code path is
    // identical either way, but `Ack` / `Abandon` have nothing to act on
    // — a process-local channel cannot redeliver, which is precisely the
    // gap the durable arm closes and the reason
    // `RagIngestionInstanceValidator` refuses multi-replica without it.
    interface IIngestionQueue with
        member _.IsDurable = store.IsSome
        member _.Capacity = cap
        member _.Policy = policy
        member _.EnqueueAsync(job) = enqueueAsync job
        member _.EnqueueBlockingAsync(job) = enqueueBlocking job

        member _.Dequeue(ct: CancellationToken) = async {
            match store with
            | Some s ->
                let mutable claimed = None

                while claimed.IsNone && not ct.IsCancellationRequested do
                    let! lease = s.Claim leaseDuration

                    match lease with
                    | Some _ -> claimed <- lease
                    | None -> do! Async.Sleep claimPollInterval

                return claimed
            | None ->
                let! job = channel.Reader.ReadAsync(ct).AsTask() |> Async.AwaitTask
                System.Threading.Interlocked.Decrement(&depth) |> ignore

                return
                    Some {
                        LeaseId = Guid.NewGuid().ToString("N")
                        Job = job
                        Attempt = 1
                    }
        }

        member _.Ack(leaseId) = async {
            match store with
            | Some s ->
                do! s.Complete leaseId
                System.Threading.Interlocked.Decrement(&depth) |> ignore
            | None ->
                // The channel already removed the job at `Dequeue` and
                // the depth gauge was decremented there.
                ()
        }

        member _.Abandon(leaseId, _reason) = async {
            match store with
            | Some s ->
                do! s.Release leaseId
                // The job is back in the store's pending set, so the
                // depth gauge is unchanged.
                ()
            | None -> ()
        }

        member _.RecoverStranded() = async {
            match store with
            | Some s -> return! s.ReclaimExpired()
            | None -> return 0
        }

// ─── Ingestion status observer ────────────────────────────────────

/// Optional observer hook invoked by `IngestionBackgroundService` after each
/// chunk's pipeline call completes. Modules that ingested the document
/// (e.g. `KnowledgeBase`) implement this to surface live progress and to
/// persist terminal transitions back to their own status index.
///
/// Phase 9c portability: identity is by value (`IngestionJob.DocumentId` is
/// `string`), every method is `Async<unit>`, no callback shape leaks framework
/// semantics, and observers receive all relevant state via the job parameter
/// rather than assuming in-memory state across invocations.
type IIngestionStatusObserver =
    /// Fired after a chunk has been successfully indexed by `IRetrievalPipeline`.
    abstract OnChunkIndexed: IngestionJob -> Async<unit>
    /// Fired when a chunk's pipeline call threw. `error` is the exception message.
    abstract OnChunkFailed: IngestionJob * error: string -> Async<unit>

// ─── Retry / dead-letter policy (Phase 14t) ───────────────────────

/// Retry / dead-letter policy applied to transient embedder failures
/// during ingestion. Expressed as data (portability rule 3 — no
/// callbacks, no supervision objects) so it reads identically to
/// `WebhookRetryPolicy` / `JobRetryPolicy`; a future distributed
/// ingestion companion can map the same record onto its native retry
/// mechanism. One field is added over the webhook/job shape:
/// `JitterFactor`, which spreads the backoff so a provider-wide
/// rate-limit window doesn't cause every retrying chunk to re-hit the
/// provider in lockstep (thundering herd).
///
/// Backoff is exponential — `min(InitialBackoff * 2^(attempt-1), MaxBackoff)`
/// — with up to `+JitterFactor` proportional jitter layered on top.
/// `MaxAttempts` is inclusive: `5` means up to 5 index attempts (the
/// initial inline attempt plus 4 scheduled retries) before the chunk
/// is dead-lettered.
type IngestionRetryPolicy = {
    MaxAttempts: int
    InitialBackoff: TimeSpan
    MaxBackoff: TimeSpan
    /// Proportional jitter ∈ [0, 1]. `0.2` spreads each backoff by up
    /// to +20%. `0.0` disables jitter (deterministic backoff).
    JitterFactor: float
}

module IngestionRetryPolicy =
    /// SDK defaults: 5 attempts, 30s initial backoff, 30min cap, +20%
    /// jitter. Tuned for OpenAI-style embedding rate limits, which
    /// generally clear well inside the 30-minute window — matching the
    /// `WebhookRetryPolicy` defaults so the two backoff shapes feel
    /// identical to operators.
    let defaults = {
        MaxAttempts = 5
        InitialBackoff = TimeSpan.FromSeconds 30.0
        MaxBackoff = TimeSpan.FromMinutes 30.0
        JitterFactor = 0.2
    }

    /// Base exponential backoff for `attempt` (1-indexed), before
    /// jitter. Attempt 1 is the initial index and runs immediately
    /// (`TimeSpan.Zero`); attempt 2+ uses `min(InitialBackoff *
    /// 2^(attempt-1), MaxBackoff)`. Mirrors `WebhookRetryPolicy.delayFor`
    /// / `JobRetryPolicy.delayFor` so all three feel the same.
    let baseDelayFor (policy: IngestionRetryPolicy) (attempt: int) : TimeSpan =
        if attempt <= 1 then
            TimeSpan.Zero
        else
            let exponent = float (attempt - 1)
            let raw = policy.InitialBackoff.TotalMilliseconds * (2.0 ** exponent)
            let capped = min raw policy.MaxBackoff.TotalMilliseconds
            TimeSpan.FromMilliseconds capped

    /// The jitter component (never negative) to add on top of a base
    /// backoff already applied elsewhere (the scheduler applies the
    /// exponential base; the retry handler adds this). `sample` is a
    /// caller-supplied uniform draw ∈ [0, 1) — kept a parameter rather
    /// than calling `Random` internally so the function stays pure /
    /// Fable-safe / testable. Returns `base * JitterFactor * sample`,
    /// i.e. 0..(JitterFactor·base) of additive spread.
    let jitterComponentFor (policy: IngestionRetryPolicy) (attempt: int) (sample: float) : TimeSpan =
        let baseMs = (baseDelayFor policy attempt).TotalMilliseconds

        if baseMs <= 0.0 || policy.JitterFactor <= 0.0 then
            TimeSpan.Zero
        else
            let clampedSample = max 0.0 (min 1.0 sample)
            TimeSpan.FromMilliseconds(baseMs * policy.JitterFactor * clampedSample)

/// Classification of a per-chunk index failure, deciding whether the
/// chunk is retried or dead-lettered immediately (Phase 14t). Derived
/// from the embedder exception by `IngestionService.classifyIndexFailure`.
type EmbedFailureClass =
    /// Non-retryable — bad credentials (401 / 403) or a malformed
    /// request (other 4xx). Retrying cannot succeed, so the chunk is
    /// dead-lettered immediately and the Owner/Admin are alerted that
    /// the embedding provider is unusable.
    | Permanent of reason: string
    /// Retryable — rate limit (429), a 5xx, a timeout, or a network
    /// error. Retried with exponential backoff + jitter. `retryAfter`
    /// carries a provider `Retry-After` hint when one is recoverable
    /// (`None` ⇒ use the policy's computed backoff).
    | Transient of retryAfter: TimeSpan option

/// Event-type names for the ingestion retry / dead-letter audit family
/// (Phase 14t). Emitted under `SourceModule = "ToolUp.RAG"` alongside
/// the existing `KnowledgeChunkIndexed` / `KnowledgeChunkFailed` events.
module IngestionEventTypes =
    /// The embedding provider rejected the call in a non-retryable way
    /// (bad credentials / malformed request). Payload: `{ ScopeId;
    /// Reason }`. Paired with an Owner/Admin `SystemMessage(Error)`.
    [<Literal>]
    let EmbeddingProviderUnavailable = "KnowledgeEmbeddingProviderUnavailable"

    /// A chunk exhausted its retry budget (or failed permanently) and
    /// was dropped from indexing. Payload: `{ DocId; ChunkIndex;
    /// Reason; AttemptCount }`.
    [<Literal>]
    let IngestionDeadLettered = "KnowledgeIngestionDeadLettered"

/// Serialisable payload for a scheduled ingestion-retry job (Phase
/// 14t). Carries everything the `IngestionRetryJobHandler` needs to
/// re-attempt `IRetrievalPipeline.Index` for one chunk on any node,
/// with no in-memory state (portability rule 4). Persisted as the
/// `JobDefinition.Payload` JSON string, so its shape stays
/// `FableConverters`-serialisable (DUs / records / `Map` / option).
type IngestionRetryPayload = {
    DocumentId: string
    DocumentName: string
    ChunkId: string
    /// Zero-based position of the chunk within its document. Surfaced
    /// in the `KnowledgeIngestionDeadLettered` payload.
    ChunkIndex: int
    Chunk: TextChunk
    Scope: VectorScope
    ScopeId: string
    Container: string
    OriginatingUserId: string option
}
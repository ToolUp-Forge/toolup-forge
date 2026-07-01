module ToolUp.RAG.IngestionTypes

open System
open System.Collections.Concurrent
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
type IngestionQueue(?capacity: int, ?overflowPolicy: IngestionOverflowPolicy) =
    let cap = defaultArg capacity 5000
    let policy = defaultArg overflowPolicy DropWrite

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
        if channel.Writer.TryWrite(job) then
            System.Threading.Interlocked.Increment(&depth) |> ignore
            true
        else
            false

    member _.Reader = channel.Reader

    /// Configured overflow policy (Phase 303). Read by the post-save hook
    /// to decide between drop-with-observability (`DropWrite` / `Refuse`)
    /// and never-drop blocking (`Block`).
    member _.Policy = policy

    /// Await queue space and enqueue (Phase 303 — `Block` policy). Uses the
    /// channel's blocking `WriteAsync`, so a full queue suspends the caller
    /// until the drainer frees a slot rather than returning `false`. Only
    /// called on the fire-and-forget post-save path, so the wait never
    /// delays an upload's HTTP response.
    member _.EnqueueBlocking(job: DocumentIngestionJob) : Async<unit> = async {
        do! channel.Writer.WriteAsync(job).AsTask() |> Async.AwaitTask
        System.Threading.Interlocked.Increment(&depth) |> ignore
    }

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
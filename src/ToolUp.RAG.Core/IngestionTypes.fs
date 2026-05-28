module ToolUp.RAG.IngestionTypes

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
type IngestionQueue(?capacity: int) =
    let cap = defaultArg capacity 5000

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
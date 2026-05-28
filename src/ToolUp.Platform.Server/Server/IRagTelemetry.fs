module ToolUp.Platform.IRagTelemetry

/// Rolling-window aggregate exposed by `IRagTelemetry.Snapshot`. Powers the
/// `/health/rag` endpoint and any admin dashboard that wants a quick read on
/// ingestion / retrieval health without scanning the event store. Each block
/// is a 1-minute rolling tally — entries older than the window have already
/// been evicted by the implementation.
///
/// **Privacy contract.** None of these fields can be used to reconstruct user
/// content. Latencies, batch sizes, and counts are aggregate; query miss
/// counts are global to the window, not per-team or per-query.
type RagTelemetrySnapshot = {
    // ─── Embedding provider ─────────────────────────────────────────
    /// Number of `IEmbeddingProvider.GenerateEmbeddings` calls observed in
    /// the window. Includes batched calls (one entry per batch, regardless
    /// of batch size).
    EmbeddingCallCount: int
    /// Sum of `Inputs.Length` across every batched embedding call. Divide
    /// by `EmbeddingCallCount` for average batch size; pair with the latency
    /// blob to spot batches that are too small to amortise round-trip cost.
    EmbeddingTextCount: int
    /// Average wall-clock latency of an embedding call in ms. `0.0` when no
    /// calls were observed in the window.
    EmbeddingAvgLatencyMs: float
    /// p95 latency of an embedding call in ms. `0` when no calls.
    EmbeddingP95LatencyMs: int64

    // ─── Ingestion queue ────────────────────────────────────────────
    /// Most recent observed depth of the ingestion queue. Updated on every
    /// `Enqueue` (success or rejection). 0 ≤ depth ≤ Capacity.
    QueueDepth: int
    /// Configured capacity of the ingestion queue. Useful as a denominator
    /// for client dashboards: `depth / capacity` is the saturation ratio.
    QueueCapacity: int
    /// Number of `Enqueue` calls that returned `false` because the queue
    /// was at capacity. Non-zero in this snapshot indicates active
    /// backpressure — KB upload handlers will be marking docs `Failed`.
    QueueRejections: int

    // ─── Vector-store flush ─────────────────────────────────────────
    /// Number of flush passes (`InMemoryVectorStore.flushAll` or any
    /// alternate store's equivalent) observed in the window.
    FlushCount: int
    /// Total dirty chunks persisted across all flushes in the window.
    /// Pair with `FlushCount` for a per-flush mean.
    FlushDirtyChunks: int
    /// Average wall-clock latency of a flush in ms. `0.0` when no flushes.
    FlushAvgLatencyMs: float

    // ─── Retrieval ──────────────────────────────────────────────────
    /// Number of retrieval calls observed via `RecordRetrieval`. Aligns
    /// with `IRetrievalTracer` emission sites; `Hits + Misses + Empties`
    /// equals `RetrievalCount` in the steady state.
    RetrievalCount: int
    /// Calls where the result list was non-empty AND `TopScore` exceeded
    /// the configured `MinScore` threshold (or > 0 when no threshold is
    /// set). Treat as the success signal.
    RetrievalHits: int
    /// Calls where the result list was non-empty but every match scored
    /// at or below the configured `MinScore` threshold. These are the
    /// queries whose retrievals will be surfaced to the model as a miss
    /// signal under WS3.5.
    RetrievalLowScoreMisses: int
    /// Calls where the result list was empty. Distinguishes "the corpus
    /// is too small" (lots of empties) from "the corpus is too noisy"
    /// (lots of low-score misses).
    RetrievalEmpties: int
    /// Average top-result score across hits in the window. `0.0` when
    /// `RetrievalHits = 0`. Useful as a quality gauge — a falling average
    /// across deployments is the early signal that retrieval framing,
    /// chunking, or the embedding model needs attention.
    RetrievalAvgTopScore: float

    // ─── Observer dispatch ─────────────────────────────────────────
    /// Number of `IIngestionStatusObserver` callbacks that threw in the
    /// window. The ingestion service catches observer exceptions to
    /// keep one bad observer from blocking other observers (and the
    /// ingestion job itself) — but a non-zero count here is the audit
    /// signal that some observers' downstream side-effects (KB status
    /// persistence, notification publish, audit-log write) silently did
    /// not run for some chunks. Investigate via the `[IngestionBackgroundService]
    /// event=observer_threw` log lines; once persistent, the affected
    /// observer probably needs its own retry / dead-letter path.
    ObserverFailureCount: int
}

/// Companion-extensible telemetry sink. Default implementation in
/// `ToolUp.RAG.RagTelemetry` keeps a 1-minute rolling-aggregate in process
/// memory; deployments wanting Prometheus / OpenTelemetry export register a
/// concrete `IRagTelemetry` ahead of `composeWithRAG` and the default is
/// replaced. The four `Record*` members are sync because they are
/// fire-and-forget hot-path emissions (analogous to `ILogger.Info`); only
/// `Snapshot` is `Async<_>` because backed implementations may need an RTT.
///
/// **Phase 9c portability.**
/// - **Identity by value** — every parameter is a primitive; no live handles.
/// - **Async at boundaries** — `Snapshot` is `Async<_>`. Hot-path recorders
///   are sync by design (per Phase 9c rule 2's escape clause for
///   fire-and-forget telemetry, mirroring `ILogger`).
/// - **Stateless handlers** — no callback shape; implementations own state.
/// - **No cross-shard ordering claim** — counts are window-scoped aggregates.
type IRagTelemetry =
    /// Record one `IEmbeddingProvider.GenerateEmbeddings` invocation. `texts`
    /// is the batch size (number of inputs) and `latencyMs` is the
    /// wall-clock time spent in the underlying provider call.
    abstract RecordEmbedding: texts: int * latencyMs: int64 -> unit

    /// Record an `IngestionQueue.Enqueue` outcome. `depth` is the post-call
    /// queue depth and `accepted` is the bool the channel returned. Used for
    /// rolling rejection counts; saturation telemetry comes from `depth`.
    abstract RecordEnqueue: depth: int * capacity: int * accepted: bool -> unit

    /// Record one vector-store flush pass. `dirtyChunks` is the count of
    /// chunks persisted and `latencyMs` is the wall-clock time spent in
    /// the flush.
    abstract RecordFlush: dirtyChunks: int * latencyMs: int64 -> unit

    /// Record one retrieval outcome. `topScore` is the highest score in the
    /// returned candidate list (`0.0` when empty), `resultCount` is the
    /// final `Matches.Length`, and `minScoreThreshold` is the deployment's
    /// configured score gate (or `0.0` when none). Implementations classify
    /// the outcome (hit / low-score miss / empty) using these inputs.
    abstract RecordRetrieval: topScore: float * resultCount: int * minScoreThreshold: float -> unit

    /// Record one `IIngestionStatusObserver` callback failure. The
    /// `IngestionBackgroundService` catches observer exceptions so a single
    /// broken observer doesn't block siblings or the ingestion job; this
    /// recorder makes the swallowed failures visible in aggregate. `observerName`
    /// is a free-form label the recorder may use to bucket by
    /// observer impl; the default rolling implementation aggregates a
    /// single counter regardless of label.
    abstract RecordObserverFailure: observerName: string -> unit

    /// Snapshot the rolling-window aggregates. Intended for `/health/rag`
    /// (every few seconds); not a hot-path call.
    abstract Snapshot: unit -> Async<RagTelemetrySnapshot>
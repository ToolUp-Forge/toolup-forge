module ToolUp.RAG.RagTelemetry

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.IRagTelemetry

// ─── No-op telemetry ──────────────────────────────────────────────

/// `IRagTelemetry` that discards every emission and answers `Snapshot`
/// with an all-zero record. Wired by `composeWithRAG` when a deployment
/// has explicitly opted out (or registered nothing) — keeps the DI
/// resolution unconditional inside the pipeline.
type NoOpRagTelemetry() =
    let zeroSnapshot = {
        EmbeddingCallCount = 0
        EmbeddingTextCount = 0
        EmbeddingAvgLatencyMs = 0.0
        EmbeddingP95LatencyMs = 0L
        QueueDepth = 0
        QueueCapacity = 0
        QueueRejections = 0
        FlushCount = 0
        FlushDirtyChunks = 0
        FlushAvgLatencyMs = 0.0
        IndexLoadErrors = 0
        RetrievalCount = 0
        RetrievalHits = 0
        RetrievalLowScoreMisses = 0
        RetrievalEmpties = 0
        RetrievalAvgTopScore = 0.0
        RetrievalStageP50Ms = []
        RetrievalStageP95Ms = []
        ObserverFailureCount = 0
    }

    interface IRagTelemetry with
        member _.RecordEmbedding(_, _) = ()
        member _.RecordEnqueue(_, _, _) = ()
        member _.RecordFlush(_, _) = ()
        member _.RecordIndexLoadError(_) = ()
        member _.RecordRetrievalStages(_) = ()
        member _.RecordRetrieval(_, _, _) = ()
        member _.RecordObserverFailure(_) = ()
        member _.Snapshot() = async.Return zeroSnapshot

// ─── Rolling-window telemetry (default for Phase 14m) ─────────────

/// One observation in the rolling window. Stored verbatim in the
/// per-event queues so the snapshot can compute averages, p95, and
/// per-event counts directly without lossy histograms. Memory cost is
/// bounded by the window size (60 s by default) × event rate; for typical
/// SDK loads (tens of embeddings/sec, single-digit retrievals/sec) the
/// queues stay under ~10k entries each.
type private TimedSample = {
    Timestamp: DateTimeOffset
    LatencyMs: int64
    /// Secondary value (batch size for embeddings, dirty-chunk count for
    /// flushes). Zero for retrieval samples (which use `TopScore` instead).
    Magnitude: int
    /// Top-result score for retrieval samples. Zero for non-retrieval.
    Score: float
}

type private RetrievalOutcome =
    | Hit
    | LowScoreMiss
    | Empty

type private RetrievalSample = {
    Timestamp: DateTimeOffset
    Outcome: RetrievalOutcome
    TopScore: float
}

/// In-memory `IRagTelemetry` keeping a 1-minute rolling window of every
/// recorded event. Hot-path recorders (`RecordEmbedding`,
/// `RecordEnqueue`, `RecordFlush`, `RecordRetrieval`) are O(1) — they
/// `Enqueue` onto a `ConcurrentQueue` and update a small set of
/// `Interlocked` counters. `Snapshot` does the eviction pass and the
/// heavier aggregation work; called from the `/health/rag` endpoint a
/// handful of times per second at most.
///
/// The window size is configurable so deployments wanting longer lookback
/// (e.g. for slow-burst dashboards) can extend it without forking. Default
/// is 60 s — short enough to surface a live spike, long enough to absorb
/// per-second jitter.
type RollingRagTelemetry(?windowSeconds: int) =
    let window = defaultArg windowSeconds 60 |> max 1 |> float |> TimeSpan.FromSeconds

    let embeddingSamples = ConcurrentQueue<TimedSample>()
    let flushSamples = ConcurrentQueue<TimedSample>()
    let retrievalSamples = ConcurrentQueue<RetrievalSample>()
    // Per-call stage-timing breakdowns (Phase 122). Each entry is one
    // retrieval call's `(stageName, elapsedMs)` list; the snapshot
    // flattens, groups by stage, and computes P50/P95 per stage.
    let stageSamples = ConcurrentQueue<DateTimeOffset * (string * float) list>()

    // Queue depth + capacity are point-in-time gauges, not rolling
    // aggregates — the snapshot returns the most recent observation.
    // `Interlocked.Exchange` is enough for atomic last-writer-wins.
    let mutable lastDepth = 0
    let mutable lastCapacity = 0
    // Rejections are window-aggregated so they're queue-tracked too.
    let queueRejections = ConcurrentQueue<DateTimeOffset>()
    // Observer-failure timestamps. The observer label is dropped here
    // (the snapshot exposes one aggregate count); the per-observer
    // breakdown lives in the existing `event=observer_threw` log line.
    let observerFailures = ConcurrentQueue<DateTimeOffset>()
    // Phase 14v — corrupt-index scope-load-failure timestamps. The
    // `scopeKey` label is dropped here (the snapshot exposes one
    // aggregate count, keeping the privacy contract scope-free); the
    // per-scope detail lives in the `KnowledgeIndexLoadFailed` audit trail.
    let indexLoadErrors = ConcurrentQueue<DateTimeOffset>()

    let now () = DateTimeOffset.UtcNow

    let evict (q: ConcurrentQueue<'T>) (cutoff: DateTimeOffset) (ts: 'T -> DateTimeOffset) =
        let mutable head = Unchecked.defaultof<'T>

        while q.TryPeek(&head) && ts head < cutoff do
            q.TryDequeue(&head) |> ignore

    /// Average of `int64` latencies expressed as `float`. Returns 0.0 on
    /// an empty input so callers don't need a guard.
    let avgInt64 (xs: int64 array) =
        if xs.Length = 0 then
            0.0
        else
            (xs |> Array.sumBy float) / (float xs.Length)

    /// p95 from a (possibly small) sample. Sorts in place and indexes at
    /// `ceil(0.95 * n) - 1`. For the SDK's expected sample sizes (tens to
    /// hundreds per minute) the sort cost is negligible.
    let p95 (xs: int64 array) =
        if xs.Length = 0 then
            0L
        else
            let sorted = Array.sort xs
            let idx = max 0 (int (ceil (0.95 * float sorted.Length)) - 1)
            sorted[min idx (sorted.Length - 1)]

    /// Percentile over an already-sorted float sample — same nearest-rank
    /// convention as `p95` above, generalised so the per-stage breakdown
    /// can reuse one sort per stage for both P50 and P95.
    let percentile (p: float) (sorted: float array) =
        if sorted.Length = 0 then
            0.0
        else
            let idx = max 0 (int (ceil (p * float sorted.Length)) - 1)
            sorted[min idx (sorted.Length - 1)]

    interface IRagTelemetry with
        member _.RecordEmbedding(texts, latencyMs) =
            embeddingSamples.Enqueue {
                Timestamp = now ()
                LatencyMs = latencyMs
                Magnitude = texts
                Score = 0.0
            }

        member _.RecordEnqueue(depth, capacity, accepted) =
            System.Threading.Interlocked.Exchange(&lastDepth, depth) |> ignore
            System.Threading.Interlocked.Exchange(&lastCapacity, capacity) |> ignore

            if not accepted then
                queueRejections.Enqueue(now ())

        member _.RecordFlush(dirtyChunks, latencyMs) =
            flushSamples.Enqueue {
                Timestamp = now ()
                LatencyMs = latencyMs
                Magnitude = dirtyChunks
                Score = 0.0
            }

        member _.RecordRetrievalStages(stageTimings) =
            // Empty breakdowns (permitted-scopes-empty early return) carry
            // no per-stage signal — recording them would only grow the queue.
            if not (List.isEmpty stageTimings) then
                stageSamples.Enqueue(now (), stageTimings)

        member _.RecordRetrieval(topScore, resultCount, minScoreThreshold) =
            let outcome =
                if resultCount = 0 then Empty
                elif topScore > minScoreThreshold then Hit
                else LowScoreMiss

            retrievalSamples.Enqueue {
                Timestamp = now ()
                Outcome = outcome
                TopScore = topScore
            }

        member _.RecordObserverFailure(_) = observerFailures.Enqueue(now ())

        member _.RecordIndexLoadError(_) = indexLoadErrors.Enqueue(now ())

        member _.Snapshot() = async {
            let cutoff = now () - window

            evict embeddingSamples cutoff _.Timestamp
            evict flushSamples cutoff _.Timestamp
            evict retrievalSamples cutoff _.Timestamp
            evict stageSamples cutoff fst
            evict queueRejections cutoff id
            evict observerFailures cutoff id
            evict indexLoadErrors cutoff id

            // Snapshot the queues into arrays once so we don't double-iterate.
            let embeddings = embeddingSamples.ToArray()
            let flushes = flushSamples.ToArray()
            let retrievals = retrievalSamples.ToArray()
            let rejections = queueRejections.ToArray()

            let embeddingLatencies = embeddings |> Array.map _.LatencyMs

            let flushLatencies = flushes |> Array.map _.LatencyMs

            let hits = retrievals |> Array.filter (fun r -> r.Outcome = Hit)

            let lowScoreMisses = retrievals |> Array.filter (fun r -> r.Outcome = LowScoreMiss)

            let empties = retrievals |> Array.filter (fun r -> r.Outcome = Empty)

            let avgTopScore =
                if hits.Length = 0 then
                    0.0
                else
                    (hits |> Array.sumBy _.TopScore) / float hits.Length

            let observerFailureSnapshot = observerFailures.ToArray()

            // Flatten every call's breakdown, group by stage name, sort each
            // stage's samples once, and read both percentiles off the sorted
            // array. Sorted by stage name so the serialised snapshot is
            // deterministic run-to-run.
            let stageGroups =
                stageSamples.ToArray()
                |> Array.collect (snd >> List.toArray)
                |> Array.groupBy fst
                |> Array.map (fun (stage, xs) -> stage, (xs |> Array.map snd |> Array.sort))
                |> Array.sortBy fst

            return {
                EmbeddingCallCount = embeddings.Length
                EmbeddingTextCount = embeddings |> Array.sumBy _.Magnitude
                EmbeddingAvgLatencyMs = avgInt64 embeddingLatencies
                EmbeddingP95LatencyMs = p95 embeddingLatencies
                QueueDepth = lastDepth
                QueueCapacity = lastCapacity
                QueueRejections = rejections.Length
                FlushCount = flushes.Length
                FlushDirtyChunks = flushes |> Array.sumBy _.Magnitude
                FlushAvgLatencyMs = avgInt64 flushLatencies
                IndexLoadErrors = indexLoadErrors.ToArray().Length
                RetrievalCount = retrievals.Length
                RetrievalHits = hits.Length
                RetrievalLowScoreMisses = lowScoreMisses.Length
                RetrievalEmpties = empties.Length
                RetrievalAvgTopScore = avgTopScore
                RetrievalStageP50Ms = [ for stage, sorted in stageGroups -> stage, percentile 0.5 sorted ]
                RetrievalStageP95Ms = [ for stage, sorted in stageGroups -> stage, percentile 0.95 sorted ]
                ObserverFailureCount = observerFailureSnapshot.Length
            }
        }

let createNoOp () : IRagTelemetry = NoOpRagTelemetry() :> _

let createRolling (windowSeconds: int) : IRagTelemetry =
    RollingRagTelemetry(windowSeconds = windowSeconds) :> _

let createDefault () : IRagTelemetry = RollingRagTelemetry() :> _
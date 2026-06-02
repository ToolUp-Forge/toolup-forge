module ToolUp.RAG.IngestionService

open System
open System.Threading
open Microsoft.Extensions.Hosting
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IRagTelemetry
open ToolUp.Platform.Usage
open ToolUp.RAG.IngestionTypes

// ─── Background service ───────────────────────────────────────────

/// Dequeues `DocumentIngestionJob` entries and indexes every chunk. Each
/// document goes through a two-step flow:
///
/// 1. **Pre-warm the embedding cache** with a single batched call
///    (`embedder.GenerateEmbeddings`). For OpenAI this is one HTTP POST
///    carrying the whole document's chunk text instead of N sequential
///    POSTs — a 100-chunk document collapses from ~15 s to ~150 ms.
/// 2. **Index each chunk** through `IRetrievalPipeline.Index`. The
///    pipeline calls back into the *cached* embedder so each per-chunk
///    embed is a cache hit (the wrapper is a `CachingEmbeddingProvider`,
///    keyed by provider+model+dim+sha256(text)). This keeps `Index`'s
///    contract single-chunk while still amortising the network cost.
///
/// Per-chunk observer events fire after each `Index` call so the existing
/// progress UX is unchanged. `KnowledgeChunkIndexed` / `KnowledgeChunkFailed`
/// audit events are emitted per chunk for the same reason.
///
/// Concurrency is capped at `MaxConcurrency` parallel documents so the
/// embedding provider is not flooded. The default rose from 4 (per-chunk
/// world) to 8 (per-document world) because each "slot" now amortises
/// itself across a whole document. When Phase 9b (`IJobScheduler`) lands,
/// this service can be replaced at the composition root — the interface
/// contract and event emission are unchanged.
type IngestionBackgroundService
    (
        queue: IngestionQueue,
        pipeline: IRetrievalPipeline,
        embedder: IEmbeddingProvider,
        eventStore: IEventStore,
        observers: IIngestionStatusObserver list,
        maxConcurrency: int,
        logger: ILogger,
        telemetry: IRagTelemetry,
        usageLog: IUsageLog,
        quota: ITeamQuotaPolicy
    ) =
    inherit BackgroundService()

    let sem = new SemaphoreSlim(maxConcurrency, maxConcurrency)

    let jsonOptions = FableConverters.create ()

    let toJson o =
        JsonSerializer.Serialize(o, jsonOptions)

    let emitIndexed (job: IngestionJob) = async {
        let payload =
            toJson {|
                DocumentId = job.DocumentId
                ChunkId = job.ChunkId
                Scope = job.Scope
            |}

        let evt = Events.create job.ScopeId "ToolUp.RAG" "KnowledgeChunkIndexed" payload
        do! eventStore.Write(evt)
    }

    let emitFailed (job: IngestionJob) (error: string) = async {
        let payload =
            toJson {|
                DocumentId = job.DocumentId
                ChunkId = job.ChunkId
                Error = error
            |}

        let evt = Events.create job.ScopeId "ToolUp.RAG" "KnowledgeChunkFailed" payload
        do! eventStore.Write(evt)
    }

    let notifyObservers (callback: string) (job: IngestionJob) (notify: IIngestionStatusObserver -> Async<unit>) = async {
        for obs in observers do
            try
                do! notify obs
            with ex ->
                // Telemetry counter so the aggregate failure rate is
                // visible at `/health/rag` (`ObserverFailureCount`) —
                // the per-incident detail lives in the Error log line.
                // Observer label is the .NET type name; for an
                // object-expression observer it'll be the synthesised
                // closure name, which is good enough for bucketing in
                // a future per-label breakdown.
                telemetry.RecordObserverFailure(obs.GetType().Name)

                logger.Error(
                    $"[IngestionBackgroundService] event=observer_threw callback={callback} doc={job.DocumentId} chunk={job.ChunkId} provider={embedder.ProviderId}/{embedder.ModelId}: continuing with remaining observers",
                    Some ex
                )
    }

    /// Build the per-chunk `IngestionJob` projection used by observer
    /// callbacks and audit events. The DocumentIngestionJob carries all the
    /// fields verbatim except per-chunk identity.
    let chunkJob (doc: DocumentIngestionJob) (chunkId: string) (chunk: TextChunk) : IngestionJob = {
        DocumentId = doc.DocumentId
        DocumentName = doc.DocumentName
        ChunkId = chunkId
        Chunk = chunk
        Scope = doc.Scope
        ScopeId = doc.ScopeId
        Container = doc.Container
        OriginatingUserId = doc.OriginatingUserId
    }

    let processJob (doc: DocumentIngestionJob) = async {
        do! sem.WaitAsync() |> Async.AwaitTask

        try
            let chunkTexts = doc.Chunks |> List.map (fun (_, c) -> c.Content) |> List.toArray

            // Phase 9 compute-quota: embedding API calls are billable
            // and were previously unattributed AND ungated. Pre-flight
            // the per-scope budget BEFORE the call. On breach, mark every
            // chunk `Failed` (observable — consistent with the ingestion
            // back-pressure contract; NEVER a silent drop) and skip the
            // document; no embedding spend is incurred.
            let! quotaGate = quota.CheckTokenBudget(doc.ScopeId, ResourceKinds.apiRequests, decimal chunkTexts.Length)

            match quotaGate with
            | Error qb ->
                let msg =
                    sprintf
                        "Embedding quota exceeded for scope '%s' (%s limit %M): document not indexed. Usage resets per the configured per-day / per-month window."
                        qb.ScopeId
                        qb.Kind
                        qb.Limit

                logger.Error(
                    $"[IngestionBackgroundService] event=embedding_quota_breached doc={doc.DocumentId} scope={doc.ScopeId}: {msg}",
                    None
                )

                for (chunkId, chunk) in doc.Chunks do
                    let job = chunkJob doc chunkId chunk
                    do! emitFailed job msg
                    do! notifyObservers "OnChunkFailed" job (fun o -> o.OnChunkFailed(job, msg))
            | Ok() ->
                // Pre-warm the embedding cache with one batched call. Failures
                // here propagate to per-chunk failure events because the
                // subsequent `pipeline.Index` calls will rerun the same
                // embedding step and surface the same error per chunk —
                // observers see the failure on the unit they care about.
                try
                    let sw = System.Diagnostics.Stopwatch.StartNew()
                    let! _ = embedder.GenerateEmbeddings chunkTexts
                    sw.Stop()
                    telemetry.RecordEmbedding(chunkTexts.Length, sw.ElapsedMilliseconds)

                    // Best-effort per-scope usage attribution (billing
                    // visibility). A `Record` failure must NEVER fail
                    // ingestion (mirrors the AI `MeteringProvider`).
                    try
                        do!
                            usageLog.Record {
                                RecordId = Guid.NewGuid()
                                ScopeId = doc.ScopeId
                                ResourceKind = ResourceKinds.apiRequests
                                Quantity = decimal chunkTexts.Length
                                Unit = "embeddings"
                                Origin = None
                                Metadata =
                                    Map.ofList [
                                        "provider", embedder.ProviderId
                                        "model", embedder.ModelId
                                        "documentId", doc.DocumentId
                                    ]
                                Timestamp = DateTime.UtcNow
                            }
                    with _ ->
                        ()
                with ex ->
                    // NOT a graceful fallback — `pipeline.Index` below calls
                    // the SAME embedder per chunk, so a down / misconfigured
                    // provider means every chunk of this document is about to
                    // fail identically (N KnowledgeChunkFailed events). Say
                    // that plainly instead of the old misleading "falling
                    // back to per-chunk embed" text, which sent operators
                    // chasing a per-chunk bug during a provider outage.
                    logger.Error(
                        $"[IngestionBackgroundService] event=batched_embedding_failed doc={doc.DocumentId} docName={doc.DocumentName} chunks={doc.Chunks.Length} provider={embedder.ProviderId}/{embedder.ModelId}: the batched embedding call failed; the per-chunk Index path uses the same provider, so every chunk of this document will now fail the same way. This is almost always a provider-level problem (missing/invalid API key, rate limit, or network), not a per-chunk data issue — fix the embedding provider and re-ingest the document.",
                        Some ex
                    )

                for (chunkId, chunk) in doc.Chunks do
                    let job = chunkJob doc chunkId chunk

                    try
                        do! pipeline.Index chunkId chunk doc.Scope
                        do! emitIndexed job
                        do! notifyObservers "OnChunkIndexed" job (fun o -> o.OnChunkIndexed job)
                    with ex ->
                        do! emitFailed job ex.Message
                        do! notifyObservers "OnChunkFailed" job (fun o -> o.OnChunkFailed(job, ex.Message))
        finally
            sem.Release() |> ignore
    }

    override _.ExecuteAsync(stoppingToken: CancellationToken) = task {
        while not stoppingToken.IsCancellationRequested do
            try
                let! job = queue.Reader.ReadAsync(stoppingToken).AsTask()
                queue.RecordDequeue()
                // Fire each job without awaiting — concurrency is controlled
                // by the semaphore inside processJob.
                Async.Start(processJob job, stoppingToken)
            with
            | :? OperationCanceledException -> ()
            | ex ->
                logger.Error(
                    $"[IngestionBackgroundService] event=dequeue_loop_error provider={embedder.ProviderId}/{embedder.ModelId}: unexpected error",
                    Some ex
                )
    }

    interface IDisposable with
        member _.Dispose() = sem.Dispose()

// ─── Convenience constructor (default concurrency) ────────────────

/// `maxConcurrency` defaults to 8 because each slot now processes a full
/// document via batched embedding, not a single chunk — twice the legacy
/// per-chunk default of 4 keeps inflight work modest per provider key.
let create
    queue
    pipeline
    embedder
    eventStore
    (observers: IIngestionStatusObserver list)
    (maxConcurrency: int)
    (logger: ILogger)
    (telemetry: IRagTelemetry)
    (usageLog: IUsageLog)
    (quota: ITeamQuotaPolicy)
    =
    new IngestionBackgroundService(
        queue,
        pipeline,
        embedder,
        eventStore,
        observers,
        maxConcurrency = maxConcurrency,
        logger = logger,
        telemetry = telemetry,
        usageLog = usageLog,
        quota = quota
    )
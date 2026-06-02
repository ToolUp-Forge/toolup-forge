module ToolUp.RAG.ReembeddingService

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open Microsoft.Extensions.Hosting
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Rescan queue ─────────────────────────────────────────────────

/// Unbounded channel of scopes awaiting a re-embed scan. The composing
/// app pushes Platform/Deployment scopes on startup; team scopes can be
/// pushed on demand (e.g. after a model swap admin action).
///
/// **Per-process in-flight dedup.** Enqueueing a scope that's already
/// being scanned (or queued to scan) no-ops — the previous comment claimed
/// idempotency on the basis of "duplicate scopes just re-scan and find no
/// work the second time", but in practice a second enqueue during a
/// long-running first scan double-pays the embedding API for every chunk
/// the first scan is still working through. The in-flight set is cleared
/// after `scanScope` returns. **Multi-instance is not covered** — distributed
/// re-embedding coordination needs an `IJobScheduler`-based lease and is
/// deferred to a follow-up roadmap phase; multi-instance deployments
/// today should expect duplicate work across replicas.
type ReembeddingQueue() =
    let channel =
        Channel.CreateUnbounded<VectorScope>(UnboundedChannelOptions(SingleReader = true, SingleWriter = false))

    let inFlight = ConcurrentDictionary<VectorScope, byte>()

    member _.Reader = channel.Reader

    /// Enqueue a scope for re-embedding scan. No-op when the scope is
    /// already in flight in this process. Returns `true` if the scope
    /// was newly enqueued, `false` if it was already in flight (or the
    /// channel write failed, which is unreachable on an unbounded channel).
    member _.Enqueue(scope: VectorScope) : bool =
        if inFlight.TryAdd(scope, 0uy) then
            if channel.Writer.TryWrite(scope) then
                true
            else
                // Channel write failed (unreachable on unbounded): roll
                // back the in-flight claim so a retry isn't deadlocked.
                inFlight.TryRemove(scope) |> ignore
                false
        else
            false

    /// Called by `ReembeddingBackgroundService` after a scope's scan
    /// completes (or fails). Releases the in-flight claim so subsequent
    /// model swaps / admin re-enqueues can re-trigger the scan.
    member _.MarkScanComplete(scope: VectorScope) = inFlight.TryRemove(scope) |> ignore

// ─── Background service ───────────────────────────────────────────

/// Drains `ReembeddingQueue`, enumerates each scope's chunks (via
/// `IVectorStore.ListChunks includeDeleted = false`), detects entries
/// whose `_embedProvider`/`_embedModel`/`_embedDim` metadata does not
/// match the current `IEmbeddingProvider`, and re-runs `pipeline.Index`
/// on them. Re-indexing reuses the same `chunkId` so the upsert replaces
/// the prior vector (and overwrites the version stamps).
///
/// Concurrency capped at `MaxConcurrency` parallel re-embeds per scope,
/// matching `IngestionBackgroundService`'s pattern. Honours `CancellationToken`
/// so a host shutdown cleanly interrupts a long scan.
type ReembeddingBackgroundService
    (
        queue: ReembeddingQueue,
        store: IVectorStore,
        pipeline: IRetrievalPipeline,
        embedder: IEmbeddingProvider,
        eventStore: IEventStore,
        maxConcurrency: int,
        logger: ILogger
    ) =
    inherit BackgroundService()

    let sem = new SemaphoreSlim(maxConcurrency, maxConcurrency)

    let jsonOptions = FableConverters.create ()

    let toJson o =
        JsonSerializer.Serialize(o, jsonOptions)

    let scopeId (scope: VectorScope) =
        match scope with
        | Platform -> "_platform"
        | Deployment -> "_deployment"
        | Team teamId -> teamId

    let needsReembed (chunk: TextChunk) =
        let provider = chunk.Metadata |> Map.tryFind EmbeddingVersion.MetadataProviderKey

        let model = chunk.Metadata |> Map.tryFind EmbeddingVersion.MetadataModelKey

        let dims = chunk.Metadata |> Map.tryFind EmbeddingVersion.MetadataDimensionsKey

        match provider, model, dims with
        | None, _, _
        | _, None, _
        | _, _, None ->
            // Pre-versioning chunk — re-embed to stamp current version.
            true
        | Some p, Some m, Some d ->
            p <> embedder.ProviderId
            || m <> embedder.ModelId
            || d <> string embedder.Dimensions

    let emitReembedded (scope: VectorScope) (chunkId: string) = async {
        let payload = toJson {| Scope = scope; ChunkId = chunkId |}

        let evt =
            Events.create (scopeId scope) "ToolUp.RAG" "KnowledgeChunkReembedded" payload

        do! eventStore.Write(evt)
    }

    let emitReembedFailed (scope: VectorScope) (chunkId: string) (error: string) = async {
        let payload =
            toJson {|
                Scope = scope
                ChunkId = chunkId
                Error = error
            |}

        let evt =
            Events.create (scopeId scope) "ToolUp.RAG" "KnowledgeChunkReembedFailed" payload

        do! eventStore.Write(evt)
    }

    let processOne (scope: VectorScope) (chunkId: string) (chunk: TextChunk) = async {
        do! sem.WaitAsync() |> Async.AwaitTask

        try
            try
                do! pipeline.Index chunkId chunk scope
                do! emitReembedded scope chunkId
            with ex ->
                logger.Error(
                    sprintf
                        "[ReembeddingService] Failed to re-embed %s in %A — leaving prior vector in place"
                        chunkId
                        scope,
                    Some ex
                )

                do! emitReembedFailed scope chunkId ex.Message
        finally
            sem.Release() |> ignore
    }

    let scanScope (scope: VectorScope) (ct: CancellationToken) = async {
        try
            let! chunks = store.ListChunks scope false
            let stale = chunks |> List.filter (fun (_, c) -> needsReembed c)

            if not stale.IsEmpty then
                logger.Info(
                    sprintf "[ReembeddingService] %d stale chunk(s) detected in %A — re-embedding" stale.Length scope
                )

            for chunkId, chunk in stale do
                if not ct.IsCancellationRequested then
                    Async.Start(processOne scope chunkId chunk, ct)
        finally
            // Release the in-flight claim so a future model swap / admin
            // re-enqueue can re-trigger this scope. Stale chunks dispatched
            // above run concurrently via `Async.Start` — releasing here
            // permits a fresh scan to start before the prior `processOne`
            // batch finishes, which is fine: every chunk's `pipeline.Index`
            // is idempotent by `chunkId`, so the worst case is one wasted
            // re-embed on a chunk that both scans pick up.
            queue.MarkScanComplete scope
    }

    override _.ExecuteAsync(stoppingToken: CancellationToken) = task {
        // Initial drain: scan whatever scopes the store currently knows
        // about. For lazy-loading stores (e.g. `InMemoryVectorStore`) cold
        // scopes aren't enumerated until they're touched — those get
        // pushed onto the queue by the composing app or by a future
        // scope-load notification.
        try
            let! initialScopes = store.ListScopes() |> Async.StartAsTask
            let scopeList = initialScopes |> List.ofSeq

            for s in scopeList do
                queue.Enqueue s |> ignore

            // Pending-work visibility. Operators previously had no signal
            // for how many scopes the re-embed pass would cover — nor that
            // a lazy-loading store (InMemoryVectorStore) only enumerates
            // scopes touched this process, so cold team corpora keep
            // serving stale-dimension vectors after a model swap until
            // something queries them. Make both explicit.
            logger.Info(
                $"[ReembeddingService] Initial scan enqueued {List.length scopeList} scope(s) reported by the vector store."
            )

            if not (List.isEmpty scopeList) then
                logger.Warn
                    "[ReembeddingService] If the vector store is lazy-loading (e.g. InMemoryVectorStore), this list covers only scopes loaded so far — cold team scopes are NOT re-embedded until first accessed. After an embedding model/provider swap, touch each team's corpus (or restart with an eager store) to force re-embed; until then those scopes serve stale-dimension vectors (which now score as non-matches rather than corrupt results)."

            // Surface the scan into the event/audit fabric, not just the
            // log, so re-embed coverage (and the lazy-store cold-scope
            // caveat) is discoverable from HealthMonitor / audit tooling
            // rather than relying on an operator catching a Warn line.
            let scanPayload =
                toJson {|
                    ScopesEnqueued = List.length scopeList
                    Provider = embedder.ProviderId
                    Model = embedder.ModelId
                    ColdScopesCaveat =
                        "Lazy-loading vector stores enumerate only scopes touched this process; cold team scopes are not re-embedded until first accessed."
                |}

            do! eventStore.Write(Events.create "_platform" "ToolUp.RAG" "ReembeddingScanStarted" scanPayload)
        with ex ->
            logger.Error("[ReembeddingService] Initial scope drain failed", Some ex)

        while not stoppingToken.IsCancellationRequested do
            try
                let! scope = queue.Reader.ReadAsync(stoppingToken).AsTask()
                do! scanScope scope stoppingToken
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.Error("[ReembeddingService] Unexpected error in dequeue loop", Some ex)
    }

    interface IDisposable with
        member _.Dispose() = sem.Dispose()

// ─── Convenience constructor ──────────────────────────────────────

let create
    (queue: ReembeddingQueue)
    (store: IVectorStore)
    (pipeline: IRetrievalPipeline)
    (embedder: IEmbeddingProvider)
    (eventStore: IEventStore)
    (logger: ILogger)
    =
    new ReembeddingBackgroundService(queue, store, pipeline, embedder, eventStore, maxConcurrency = 2, logger = logger)
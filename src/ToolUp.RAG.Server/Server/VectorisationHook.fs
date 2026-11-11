module ToolUp.RAG.VectorisationHook

open ToolUp.Platform
open ToolUp.Platform.Infra.IRagTelemetry
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.VectorisationTypes
open ToolUp.Platform.Files.FileManagement
open ToolUp.Platform.FileProcessor
open ProcessedDataTypes
open DataManagementTypes
open ToolUp.RAG.RagAuditEvents
open ToolUp.RAG.IngestionTypes
open ToolUp.Platform.Infra
open ToolUp.Platform.Jobs
open ToolUp.Platform.Audit

// ─── Post-save vectorisation hook ────────────────────────────────

/// What the post-save vectorisation hook reads. Built where every value
/// exists — inside RAG's service registration, from the `Substrate` the
/// contribution receives and the runtime the container builds — so nothing
/// is captured before it can be resolved.
type internal VectorisationDeps = {
    /// The vectorisation handlers the deployment's modules contribute.
    Handlers: VectorisationHandler list
    Queue: IngestionQueue
    Telemetry: IRagTelemetry
    /// Whether a handler's document summary is produced and indexed.
    EnableSummaries: bool
    MaxChunkBytes: int option
    MaxDocumentBytes: int option
    EventStore: IEventStore
    /// The per-file ingestion status the Data Manager badge reads.
    StatusStore: IIngestionStatusStore
    /// The Data Manager's attempts over `StatusStore`.
    Attempts: IIngestionAttemptStore
    OverflowPolicy: IngestionOverflowPolicy
    AuditLog: IAuditLog
    Notifications: INotificationChannel
    Logger: ILogger
}

/// Build the file-save hook that enqueues processed data for vectorisation.
/// Looks up the `VectorisationHandler` for the file's data type, generates
/// chunks, and enqueues a single `DocumentIngestionJob` carrying all of
/// them. The background service then issues one batched embedding call per
/// document — see `IngestionBackgroundService` for the cache-warming flow.
///
/// When the queue is at capacity, `Enqueue` returns `false`. Because the
/// drainer (`IngestionBackgroundService`) may simply be behind, we retry
/// with a short bounded backoff (`enqueueRetryDelaysMs`) before giving up
/// — most transient saturation clears within that window. The post-save
/// hook is fire-and-forget (runs after `AddFile` already returned HTTP
/// 200), so the backoff sleeps never delay the user's upload response.
/// Only after the retries are exhausted is the document dropped: we log at
/// `Error` AND write a `DocumentVectorisationDropped` event so the
/// otherwise-silent loss ("data manager file lands but is not indexed") is
/// queryable alongside the `DocumentVectorisationSkipped` / `DocumentRejected`
/// events. KB's own enqueue sites (`UploadDocument`, `AddNote`,
/// `UpdateNote`, `IngestNarrative`) additionally mark the document `Failed`
/// so the user sees the rejection in the KB UI; the Data-Manager client
/// badge for the same is a roadmap follow-on.
/// Bounded backoff delays (ms) for re-attempting a full ingestion queue.
/// One entry per retry after the initial attempt — five total tries over
/// ~1.85s. Runs on the fire-and-forget post-save path, so this does not
/// add latency to the upload response.
let private enqueueRetryDelaysMs: int list = [ 100; 250; 500; 1000 ]

/// Emit the drop-observability triple for one document the
/// ingestion queue could not accept: record it on the queue's `Dropped`
/// counters, write a `KnowledgeIngestionDropped` audit under
/// `_platform.knowledge` (deployment-wide, like the corrupt-index trail),
/// and publish a Warning `SystemMessage` to the uploading user (when the
/// enqueue carried a user attribution) so the client can surface "this
/// document could not be indexed — try again in a moment". Both the audit
/// log and the notification channel are optional / best-effort — a
/// channel-less / audit-less deployment still bumps the queue counter and
/// leaves the caller's `Error` log line (GP 13). Mirrors the
/// `InMemoryVectorStore` corrupt-index emission shape. `scopeKey` is the
/// vector-store scope key (`platform` / `deployment` / `team:{id}`),
/// matching `KnowledgeIndexLoadFailedPayload.ScopeKey`.
let emitIngestionDrop
    (auditLog: IAuditLog)
    (notifications: INotificationChannel option)
    (queue: IngestionQueue)
    (logger: ILogger)
    (scopeKey: string)
    (docId: string)
    (chunkCount: int)
    (reason: string)
    (originatingUserId: UserId option)
    : Async<unit> =
    async {
        queue.RecordDrop()

        try
            do!
                auditLog.Record(
                    AuditScope.shard KnowledgeSourceModule.value,
                    AuditSubject.system "IngestionQueue",
                    RagAuditEvent.event (
                        KnowledgeIngestionDropped {
                            ScopeKey = scopeKey
                            DocId = docId
                            ChunkCount = chunkCount
                            QueueCapacity = queue.Capacity
                            Reason = reason
                        }
                    )
                )
                |> AuditRow.orRefuse
        with ex ->
            logger.Warn(sprintf "[RAGCompose] KnowledgeIngestionDropped audit write failed for %s: %s" docId ex.Message)

        match notifications, originatingUserId with
        | Some ch, Some uid ->
            try
                let text =
                    sprintf
                        "\"%s\" could not be indexed for search right now — the ingestion queue is saturated. The file is saved; re-upload it in a moment to index it for retrieval."
                        docId

                do! ch.Publish(uid.Value, Notification.SystemMessage(SystemMessageLevel.Warning, text))
            with ex ->
                logger.Warn(sprintf "[RAGCompose] ingestion-drop notification failed for %s: %s" docId ex.Message)
        | _ -> ()
    }

let internal makeVectorisationHook
    (deps: VectorisationDeps)
    : ProcessedData * ProcessedFileEntry * StorageScope * string -> Async<unit> =


    let jsonOptions = ToolUp.Remoting.Json.SystemTextJson.PlatformJson.options

    // Write the per-file ingestion status alongside the
    // `DocumentVectorisation{Skipped,Rejected,Dropped}` events these arms
    // already emit, so the Data Manager badge surfaces *why* a file
    // isn't searchable. Best-effort (mirrors `writeEvent`): a status-store
    // outage degrades to a missing badge, never an escaping exception on
    // the fire-and-forget post-save path.
    let setStatus (scopeId: string) (documentId: string) (status: FileIngestionStatus) = async {
        try
            do! deps.StatusStore.Set(scopeId, documentId, status)
        with ex ->
            deps.Logger.Warn(sprintf "[RAGCompose] ingestion-status write failed for %s: %s" documentId ex.Message)
    }

    // Begin the file's attempt BEFORE the enqueue, so the first chunk
    // callback finds it (and its total) persisted. Best-effort like the
    // status writes: an outage leaves the badge where it was.
    let beginAttempt (scopeId: string) (documentId: string) (enqueuedChunks: int) = async {
        let attempt: IngestionAttempt = {
            AttemptId = System.Guid.NewGuid().ToString("N")
            EnqueuedChunks = enqueuedChunks
        }

        try
            do! deps.Attempts.Begin(scopeId, documentId, attempt.AttemptId, enqueuedChunks)
        with ex ->
            deps.Logger.Warn(sprintf "[RAGCompose] ingestion attempt write failed for %s: %s" documentId ex.Message)

        return attempt
    }

    let writeEvent (scopeId: string) (eventName: string) (payload: obj) = async {
        try
            let json = System.Text.Json.JsonSerializer.Serialize(payload, jsonOptions)
            let evt = Events.create scopeId "ToolUp.RAG" eventName json
            do! EventStore.writeOrRaise deps.EventStore evt
        with ex ->
            deps.Logger.Warn(sprintf "[RAGCompose] event-write failed for %s: %s" eventName ex.Message)
    }

    let utf8ByteLen (s: string) =
        if isNull s then
            0
        else
            System.Text.Encoding.UTF8.GetByteCount s

    fun (processedData, entry, scope, createdBy) -> async {
        match deps.Handlers |> List.tryFind (fun h -> h.DataTypeId = entry.DataType) with
        | None ->
            // Wave 1 Gap #3 — surface the silent drop. Previously this
            // path was `()`: the file was saved, KB marked the doc
            // uploaded, but no chunks ever reached retrieval and the
            // user got no signal. Warn + audit event so KB can render
            // an "unsupported format" badge and ops dashboards can
            // alert on the rate.
            deps.Logger.Warn(
                sprintf
                    "[RAGCompose] No VectorisationHandler registered for DataType '%s' (file %s). The file is saved but will NOT be indexed for retrieval."
                    entry.DataType
                    entry.FileName
            )

            do!
                writeEvent scope.ScopeId "DocumentVectorisationSkipped" {|
                    DocumentId = entry.FileName
                    FileName = entry.FileName
                    DataType = entry.DataType
                    Reason = "no VectorisationHandler registered for this DataType"
                    Container = scope.Container
                |}

            do!
                setStatus
                    scope.Container
                    entry.FileName
                    (FileIngestionStatus.Failed(sprintf "no handler for type %s" entry.DataType))
        | Some handler ->
            let chunks = handler.Vectorise processedData

            if not chunks.IsEmpty then
                // The vector scope is the one the storage container names
                // (GP 4): a per-user container's chunks stay in that
                // caller's `User` scope, never `Deployment`.
                let vectorScope = VectorScope.ofContainer scope

                // Keyed by the file name in the chunk-id grammar.
                let basePairs =
                    chunks
                    |> List.mapi (fun i chunk ->
                        ChunkId.render {
                            DocumentId = entry.FileName
                            Ordinal = i
                        },
                        chunk)

                // Optional document-level summary (WS4.1). When the
                // handler exposes a `Summarise` and the deployment hasn't
                // disabled it, call it here and append the result with
                // `_isSummary = "true"` so retrieval can boost it. The
                // call lives in the post-save path (not inside
                // `IngestionBackgroundService`) because it's per-document
                // by nature — repeating it for each chunk would defeat
                // the purpose.
                let! summaryPair =
                    match handler.Summarise, deps.EnableSummaries with
                    | Some f, true -> async {
                        try
                            let! summary = f processedData

                            match summary with
                            | None -> return None
                            | Some chunk ->
                                let stamped = {
                                    chunk with
                                        Metadata = chunk.Metadata |> Map.add ChunkMetadata.IsSummaryKey "true"
                                }

                                let id = $"{entry.FileName}:summary"
                                return Some(id, stamped)
                        with ex ->
                            deps.Logger.Warn
                                $"[RAGCompose] Document summary failed for {entry.FileName}: {ex.Message} — indexing without summary chunk."

                            return None
                      }
                    | _ -> async.Return None

                let chunkPairs =
                    match summaryPair with
                    | Some pair -> basePairs @ [ pair ]
                    | None -> basePairs

                // Wave 2B Gap #6 — optional per-chunk / per-document size
                // bound. Default is `None` (no limit, historical behaviour).
                // When set, refuse the document at the post-save hook and
                // emit `RAG.DocumentRejected` so KB / dashboards can see
                // the rejection rate. Cheaper to compute byte counts
                // once here than to discover an OOM mid-pipeline.
                let chunkSizes = chunkPairs |> List.map (fun (_, c) -> utf8ByteLen c.Content)

                let totalBytes = chunkSizes |> List.sum

                let oversizeChunk =
                    match deps.MaxChunkBytes with
                    | Some limit -> chunkSizes |> List.exists (fun n -> n > limit)
                    | None -> false

                let oversizeDoc =
                    match deps.MaxDocumentBytes with
                    | Some limit -> totalBytes > limit
                    | None -> false

                if oversizeChunk || oversizeDoc then
                    let reason =
                        if oversizeChunk && oversizeDoc then
                            "chunk and document size limits exceeded"
                        elif oversizeChunk then
                            sprintf
                                "one or more chunks exceed MaxChunkBytes (%d) — largest = %d bytes"
                                (Option.defaultValue 0 deps.MaxChunkBytes)
                                (List.max chunkSizes)
                        else
                            sprintf
                                "document total %d bytes exceeds MaxDocumentBytes (%d)"
                                totalBytes
                                (Option.defaultValue 0 deps.MaxDocumentBytes)

                    deps.Logger.Error(
                        sprintf
                            "[RAGCompose] DROPPED vectorisation for %s — %s. The file is saved but is not indexed; raise the limit via RagOptions.withMaxChunkBytes / withMaxDocumentBytes, or split the document upstream."
                            entry.FileName
                            reason,
                        None
                    )

                    do!
                        writeEvent scope.ScopeId "DocumentRejected" {|
                            DocumentId = entry.FileName
                            FileName = entry.FileName
                            Container = scope.Container
                            ChunkCount = List.length chunkPairs
                            TotalBytes = totalBytes
                            MaxChunkBytes = deps.MaxChunkBytes
                            MaxDocumentBytes = deps.MaxDocumentBytes
                            Reason = reason
                        |}

                    do! setStatus scope.Container entry.FileName (FileIngestionStatus.Failed reason)
                else

                    let chunkCount = List.length chunkPairs
                    let! attempt = beginAttempt scope.Container entry.FileName chunkCount

                    let job: DocumentIngestionJob = {
                        DocumentId = entry.FileName
                        DocumentName = entry.FileName
                        Chunks = chunkPairs
                        Scope = vectorScope
                        ScopeId = scope.ScopeId
                        Container = scope.Container
                        // The post-save hook fires from session-file storage
                        // with no per-request `AccessContext` in scope, but
                        // `AddFile` threads the uploading user's id through the
                        // hook tuple as `createdBy`, so the ingestion job is
                        // attributed to the actual user rather than left
                        // anonymous. An empty/whitespace id (e.g. the "system"
                        // hydration path) falls back to `None`, preserving the
                        // historical publish-to-UploadedBy behaviour for
                        // observers (Wave 2B Gap #7).
                        OriginatingUserId =
                            if System.String.IsNullOrWhiteSpace createdBy then
                                None
                            else
                                Some(UserId createdBy)
                        Attempt = Some attempt
                    }

                    // Bounded retry: a full deps.Queue is often just the drainer
                    // running behind, so a short backoff usually clears space
                    // rather than dropping the document. Fire-and-forget path,
                    // so the sleeps don't delay the upload response.
                    // `EnqueueAsync`, not `Enqueue`: on a durable
                    // deps.Queue the append is I/O, and this path is already
                    // inside an async workflow, so there is no reason to
                    // block a thread on it.
                    let rec tryEnqueue (delays: int list) = async {
                        let! accepted = deps.Queue.EnqueueAsync job

                        if accepted then
                            return true
                        else
                            match delays with
                            | [] -> return false
                            | d :: rest ->
                                do! Async.Sleep d
                                return! tryEnqueue rest
                    }

                    let scopeKey = VectorScope.key vectorScope

                    match deps.OverflowPolicy with
                    | Block ->
                        // Never drop. Await deps.Queue space instead of
                        // retrying-then-dropping; the upload response has already
                        // returned, so the wait delays indexing under sustained
                        // load but never loses a document.
                        do! deps.Queue.EnqueueBlocking job
                        deps.Telemetry.RecordEnqueue(deps.Queue.Count, deps.Queue.Capacity, true)
                    | DropWrite
                    | Refuse ->
                        let! accepted = tryEnqueue enqueueRetryDelaysMs
                        deps.Telemetry.RecordEnqueue(deps.Queue.Count, deps.Queue.Capacity, accepted)

                        if not accepted then
                            // Retries exhausted — permanent data loss for this
                            // document's searchability. Log at Error so it trips
                            // error-rate alerting, AND write an event so the drop
                            // is queryable per-document in the RAG event trail
                            // (symmetric with DocumentVectorisationSkipped /
                            // DocumentRejected), not just in the deps.Telemetry snapshot.
                            let attempts = List.length enqueueRetryDelaysMs + 1
                            let reason = "ingestion deps.Queue full after bounded retry"

                            deps.Logger.Error(
                                $"[RAGCompose] Ingestion deps.Queue full ({deps.Queue.Count}/{deps.Queue.Capacity}) after {attempts} attempts — DROPPED vectorisation for {entry.FileName}. The file is saved but is permanently unsearchable until re-uploaded. Raise IngestionQueueCapacity (RagOptions.withIngestionQueueCapacity) or IngestionConcurrency if drops recur.",
                                None
                            )

                            do!
                                writeEvent scope.ScopeId "DocumentVectorisationDropped" {|
                                    DocumentId = entry.FileName
                                    FileName = entry.FileName
                                    Container = scope.Container
                                    ChunkCount = chunkCount
                                    QueueDepth = deps.Queue.Count
                                    QueueCapacity = deps.Queue.Capacity
                                    Attempts = attempts
                                    Reason = reason
                                |}

                            // Deployment-wide `KnowledgeIngestionDropped`
                            // audit + Warning `SystemMessage` to the uploader +
                            // deps.Queue `Dropped` counters (rolling 60s + cumulative,
                            // surfaced on /health/rag).
                            do!
                                emitIngestionDrop
                                    deps.AuditLog
                                    (Some deps.Notifications)
                                    deps.Queue
                                    deps.Logger
                                    scopeKey
                                    entry.FileName
                                    chunkCount
                                    reason
                                    job.OriginatingUserId

                            do! setStatus scope.Container entry.FileName (FileIngestionStatus.Failed reason)
                            do! deps.Attempts.Forget(scope.Container, entry.FileName)
    }
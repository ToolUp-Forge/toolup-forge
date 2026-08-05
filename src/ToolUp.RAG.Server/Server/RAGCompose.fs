module ToolUp.RAG.RAGCompose

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Providers
open ToolUp.Platform.RemotingHelpers
open ToolUp.Platform.TransientFault
open ToolUp.Platform.Server
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.Usage
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IEmbeddingCache
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IReranker
open ToolUp.Platform.IQueryRewriter
open ToolUp.Platform.IOcrProvider
open ToolUp.Platform.ITableExtractor
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IRetrievalTracer
open ToolUp.Platform.IRagTelemetry
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.VectorisationTypes
open ToolUp.Platform.FileManagement
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.AI
open ProcessedDataTypes
open DataManagementTypes
open ToolUp.AI
open ToolUp.AI.AICompose
open ToolUp.AI.AIToolRegistry
open ToolUp.AI.SystemPromptBuilder
open ToolUp.RAG.IngestionTypes
open ToolUp.RAG.SparseAnalysis
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.RAG.InMemoryEmbeddingCache
open ToolUp.RAG.CachingEmbeddingProvider
open ToolUp.RAG.RetrievalPipeline
open ToolUp.RAG.IngestionService
open ToolUp.RAG.RAGPromptBuilder

// ─── RAG-awareness preamble ──────────────────────────────────────

/// Grounding stance for the AI assistant. Controls how strictly the model
/// is required to ground its answers in the team's knowledge base.
type GroundingMode =
    /// Model uses KB context plus its own training data freely. Default —
    /// matches the prior behaviour and works for most agency / analysis
    /// use cases where general knowledge usefully complements team data.
    | Permissive
    /// KB is authoritative for the team's data; the model prefers KB
    /// content over training data on conflict, but still answers from
    /// general knowledge when the KB has nothing relevant. Equivalent to
    /// the WS1 default framing (`defaultRetrievalFraming`).
    | Preferred
    /// Model refuses to answer unless retrieval found at least one
    /// match above the configured `MinScore` threshold. Useful for
    /// regulated-brand / compliance-sensitive deployments where the
    /// assistant must never speculate. Implemented as a system-prompt
    /// directive plus a server-side guard that short-circuits the
    /// response on a retrieval miss.
    | StrictlyGrounded

/// Default framing text injected into the system prompt ahead of the retrieved
/// context block (used by `Preferred` mode). Tells the model:
///   1. The platform has a knowledge base of team-uploaded content.
///   2. The system has *already* searched it for the current message — the
///      results are below, no tool call needed.
///   3. KB content is authoritative for the team's data; prefer it over
///      training data when the two conflict.
///   4. An empty retrieval block means "search ran and found nothing" — the
///      model must say so explicitly rather than inventing facts.
///
/// Pairs with `RAGPromptBuilder.withRetrieval`'s explicit miss signal so the
/// model never has to guess whether the KB has been consulted.
let defaultRetrievalFraming =
    "You have access to a knowledge base containing this team's uploaded documents, saved notes, and prior analyses. \
     Before every reply, the system has already searched the knowledge base for content relevant to the user's message and injected the top results below under the heading \"Relevant context from your team's knowledge base\". \
     Each retrieved item is prefixed with a citation marker — [¹], [²], [³], … When you draw on a knowledge-base item, append its marker to the relevant sentence so the user can verify the source. \
     Treat this content as authoritative for the team's data and prefer it over your training data when the two conflict. \
     If the relevant-context section is missing or empty, the search returned nothing — say so explicitly rather than inventing facts.\
     \n\nStrict citation contract (Phase 6q):\
     \n  • Use ONLY the documented superscript markers [¹], [²], [³], … — never (1), [1], Source 1, ^1, or a bare digit. The renderer only recognises the documented shape; other forms render as dangling text that points to nothing.\
     \n  • Cite only when a sentence draws on a retrieved item. Do not append a marker to a sentence that comes from your own training data or general knowledge.\
     \n  • NEVER invent a citation number that does not appear in the provided context block. If you would write \"(4)\" but only items [¹], [²], [³] were retrieved, you are inventing — say \"I cannot verify this against your knowledge base\" instead.\
     \n  • If you want to acknowledge a claim you cannot ground, say so plainly (\"the documents don't speak directly to this\") rather than inserting a marker.\
     \n\nExamples:\
     \n  GOOD: \"Q3 revenue grew 14% [¹] driven by strong UK retail performance [²].\"\
     \n  BAD: \"Q3 revenue grew 14% (1).\" — uses parenthesised digit; the renderer cannot resolve it to a source.\
     \n  BAD: \"Per Source 2, retail led growth.\" — uses the word \"Source\" + digit; same problem.\
     \n  BAD: \"Recovery began in 2023 [⁴].\" — when only three items were retrieved, [⁴] is a phantom that points to nothing."

/// Strict-grounding directive appended to the framing preamble when
/// `GroundingMode = StrictlyGrounded`. Pairs with the server-side guard
/// in `RAGPromptBuilder` that short-circuits on a retrieval miss.
let strictlyGroundedDirective =
    "Strict grounding mode is active: if the relevant-context section is missing or empty, reply \"I don't have information on that in your knowledge base.\" Do not invent or speculate. Do not draw on your general training data to fill the gap."

/// Resolve the framing text for a `GroundingMode` + custom framing override.
/// `Permissive` returns the empty string (no preamble at all — model uses
/// KB and general knowledge freely). `Preferred` returns the supplied
/// framing (default = `defaultRetrievalFraming`). `StrictlyGrounded`
/// appends the strict directive to the supplied framing.
let resolveFraming (mode: GroundingMode) (framing: string) : string =
    match mode with
    | Permissive -> ""
    | Preferred -> framing
    | StrictlyGrounded ->
        if System.String.IsNullOrWhiteSpace framing then
            strictlyGroundedDirective
        else
            framing + "\n\n" + strictlyGroundedDirective

/// Phase 14r — companion sentence appended to the retrieval framing when
/// the deployment has live-interface tools loaded. Teaches the model that
/// a knowledge-base miss is not a dead end: "what is on the user's screen
/// right now" is answered by the interface inspection tool, not the KB. It
/// names the capability by *purpose* (interface inspection), never by tool
/// ID, so it stays correct regardless of which module registered the tool.
/// The knowledge base stays authoritative for saved documents / analyses;
/// only live on-screen state is relinquished to the inspection path.
let uiToolFramingCompanion =
    "This deployment also exposes live interface tools that can read the state of the module the user is currently viewing. \
     For a question about what is on the user's screen right now — the filters currently applied, the current selection, the values in a form, or any other live page state — the interface inspection tool is the canonical source, not the knowledge base. \
     Call it before answering rather than replying that you don't know or guessing from earlier in the conversation. \
     The knowledge base stays authoritative for saved documents, notes, and prior analyses; only live on-screen state defers to the inspection tool. \
     So an empty knowledge-base result is not a dead end when the user is asking about their current screen — inspect the live interface instead of saying the search found nothing."

/// Phase 14r — resolve the framing text, then append the live-interface
/// companion (`uiToolFramingCompanion`) when the deployment has such tools.
/// The companion is added only under `Preferred` — the default grounding
/// stance. `Permissive` emits no framing at all (the model is already free
/// to call any tool), and `StrictlyGrounded` deliberately refuses on a
/// retrieval miss, so redirecting to a tool would contradict its contract;
/// both ignore `toolFraming` and fall through to plain `resolveFraming`.
let resolveFramingWithTools
    (mode: GroundingMode)
    (framing: string)
    (toolFraming: RAGPromptBuilder.ToolFraming)
    : string =
    let baseFraming = resolveFraming mode framing

    match mode with
    | Preferred when toolFraming.HasLiveUiTools ->
        if System.String.IsNullOrWhiteSpace baseFraming then
            uiToolFramingCompanion
        else
            baseFraming + "\n\n" + uiToolFramingCompanion
    | _ -> baseFraming

// ─── Post-save vectorisation hook ────────────────────────────────

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

/// Phase 303 — sustained-saturation threshold for
/// `IngestionOverflowPolicy.Refuse`. Once this many documents have been
/// dropped inside the queue's rolling 60s window, `Refuse` raises
/// `ConfigPreflightFailedException` from the drop path. A handful of drops
/// in a minute already means the queue stayed full through the ~1.85s
/// bounded retry repeatedly — genuine sustained pressure, not a one-off
/// spike.
[<Literal>]
let private refuseSaturationThreshold = 5

/// Phase 303 — emit the drop-observability triple for one document the
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
    (auditLog: IAuditLog option)
    (notifications: INotificationChannel option)
    (queue: IngestionQueue)
    (logger: ILogger)
    (scopeKey: string)
    (docId: string)
    (chunkCount: int)
    (reason: string)
    (originatingUserId: string option)
    : Async<unit> =
    async {
        queue.RecordDrop()

        match auditLog with
        | Some a ->
            try
                do!
                    a.Record(
                        KnowledgeSourceModule.value,
                        KnowledgeIngestionDropped {
                            ScopeKey = scopeKey
                            DocId = docId
                            ChunkCount = chunkCount
                            QueueCapacity = queue.Capacity
                            Reason = reason
                        }
                    )
            with ex ->
                logger.Warn(
                    sprintf "[RAGCompose] KnowledgeIngestionDropped audit write failed for %s: %s" docId ex.Message
                )
        | None -> ()

        match notifications, originatingUserId with
        | Some ch, Some uid ->
            try
                let text =
                    sprintf
                        "\"%s\" could not be indexed for search right now — the ingestion queue is saturated. The file is saved; re-upload it in a moment to index it for retrieval."
                        docId

                do! ch.Publish(uid, Notification.SystemMessage(SystemMessageLevel.Warning, text))
            with ex ->
                logger.Warn(sprintf "[RAGCompose] ingestion-drop notification failed for %s: %s" docId ex.Message)
        | _ -> ()
    }

let private makeVectorisationHook
    (handlers: VectorisationHandler list)
    (queue: IngestionQueue)
    (telemetry: IRagTelemetry)
    (enableSummaries: bool)
    (maxChunkBytes: int option)
    (maxDocumentBytes: int option)
    (eventStoreRef: IEventStore option ref)
    (ingestionStatusStoreRef: IIngestionStatusStore option ref)
    (overflowPolicy: IngestionOverflowPolicy)
    (auditLogRef: IAuditLog option ref)
    (notificationChannelRef: INotificationChannel option ref)
    (logger: ILogger)
    : ProcessedData * ProcessedFileEntry * StorageScope * string -> Async<unit> =

    let jsonOptions = ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

    // Phase 173 — write the per-file ingestion status alongside the
    // `DocumentVectorisation{Skipped,Rejected,Dropped}` events these arms
    // already emit, so the Data Manager badge surfaces *why* a file
    // isn't searchable. Best-effort (mirrors `writeEvent`): a status-store
    // outage degrades to a missing badge, never an escaping exception on
    // the fire-and-forget post-save path. No store composed ⇒ no-op.
    let setStatus (scopeId: string) (documentId: string) (status: FileIngestionStatus) = async {
        match ingestionStatusStoreRef.Value with
        | None -> ()
        | Some s ->
            try
                do! s.Set(scopeId, documentId, status)
            with ex ->
                logger.Warn(sprintf "[RAGCompose] ingestion-status write failed for %s: %s" documentId ex.Message)
    }

    let setPending (scopeId: string) (documentId: string) (totalChunks: int) = async {
        match ingestionStatusStoreRef.Value with
        | None -> ()
        | Some s ->
            try
                do! s.SetPending(scopeId, documentId, totalChunks)
            with ex ->
                logger.Warn(
                    sprintf "[RAGCompose] ingestion-status pending write failed for %s: %s" documentId ex.Message
                )
    }

    let writeEvent (scopeId: string) (eventName: string) (payload: obj) = async {
        match eventStoreRef.Value with
        | None -> ()
        | Some es ->
            try
                let json = System.Text.Json.JsonSerializer.Serialize(payload, jsonOptions)
                let evt = Events.create scopeId "ToolUp.RAG" eventName json
                do! es.Write evt
            with ex ->
                logger.Warn(sprintf "[RAGCompose] event-write failed for %s: %s" eventName ex.Message)
    }

    let utf8ByteLen (s: string) =
        if isNull s then
            0
        else
            System.Text.Encoding.UTF8.GetByteCount s

    fun (processedData, entry, scope, createdBy) -> async {
        match handlers |> List.tryFind (fun h -> h.DataTypeId = entry.DataType) with
        | None ->
            // Wave 1 Gap #3 — surface the silent drop. Previously this
            // path was `()`: the file was saved, KB marked the doc
            // uploaded, but no chunks ever reached retrieval and the
            // user got no signal. Warn + audit event so KB can render
            // an "unsupported format" badge and ops dashboards can
            // alert on the rate.
            logger.Warn(
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
                let vectorScope =
                    if scope.Container.StartsWith "team-" then
                        Team(scope.ScopeId)
                    else
                        Deployment

                let basePairs =
                    chunks |> List.mapi (fun i chunk -> $"{entry.FileName}:chunk:{i}", chunk)

                // Optional document-level summary (WS4.1). When the
                // handler exposes a `Summarise` and the deployment hasn't
                // disabled it, call it here and append the result with
                // `_isSummary = "true"` so retrieval can boost it. The
                // call lives in the post-save path (not inside
                // `IngestionBackgroundService`) because it's per-document
                // by nature — repeating it for each chunk would defeat
                // the purpose.
                let! summaryPair =
                    match handler.Summarise, enableSummaries with
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
                            logger.Warn
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
                    match maxChunkBytes with
                    | Some limit -> chunkSizes |> List.exists (fun n -> n > limit)
                    | None -> false

                let oversizeDoc =
                    match maxDocumentBytes with
                    | Some limit -> totalBytes > limit
                    | None -> false

                if oversizeChunk || oversizeDoc then
                    let reason =
                        if oversizeChunk && oversizeDoc then
                            "chunk and document size limits exceeded"
                        elif oversizeChunk then
                            sprintf
                                "one or more chunks exceed MaxChunkBytes (%d) — largest = %d bytes"
                                (Option.defaultValue 0 maxChunkBytes)
                                (List.max chunkSizes)
                        else
                            sprintf
                                "document total %d bytes exceeds MaxDocumentBytes (%d)"
                                totalBytes
                                (Option.defaultValue 0 maxDocumentBytes)

                    logger.Error(
                        sprintf
                            "[RAGCompose] DROPPED vectorisation for %s — %s. The file is saved but is not indexed; raise the limit via RAGServerApp.withMaxChunkBytes / withMaxDocumentBytes, or split the document upstream."
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
                            MaxChunkBytes = maxChunkBytes
                            MaxDocumentBytes = maxDocumentBytes
                            Reason = reason
                        |}

                    do! setStatus scope.Container entry.FileName (FileIngestionStatus.Failed reason)
                else

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
                                Some createdBy
                    }

                    // Bounded retry: a full queue is often just the drainer
                    // running behind, so a short backoff usually clears space
                    // rather than dropping the document. Fire-and-forget path,
                    // so the sleeps don't delay the upload response.
                    // Phase 509 — `EnqueueAsync`, not `Enqueue`: on a durable
                    // queue the append is I/O, and this path is already
                    // inside an async workflow, so there is no reason to
                    // block a thread on it.
                    let rec tryEnqueue (delays: int list) = async {
                        let! accepted = queue.EnqueueAsync job

                        if accepted then
                            return true
                        else
                            match delays with
                            | [] -> return false
                            | d :: rest ->
                                do! Async.Sleep d
                                return! tryEnqueue rest
                    }

                    let scopeKey =
                        match vectorScope with
                        | Platform -> "platform"
                        | Deployment -> "deployment"
                        | Team teamId -> $"team:{teamId}"
                        // Qualified: `ParticipantType.User` is also in scope
                        // here, so an unqualified `User` binds to the wrong DU.
                        | VectorScope.User userId -> $"user:{userId}"

                    let chunkCount = List.length chunkPairs

                    match overflowPolicy with
                    | Block ->
                        // Phase 303 — never drop. Await queue space instead of
                        // retrying-then-dropping; the upload response has already
                        // returned, so the wait delays indexing under sustained
                        // load but never loses a document.
                        do! queue.EnqueueBlocking job
                        telemetry.RecordEnqueue(queue.Count, queue.Capacity, true)
                        do! setPending scope.Container entry.FileName chunkCount
                    | DropWrite
                    | Refuse ->
                        let! accepted = tryEnqueue enqueueRetryDelaysMs
                        telemetry.RecordEnqueue(queue.Count, queue.Capacity, accepted)

                        if accepted then
                            // Mark the file `Pending` with its chunk total so the
                            // ingestion observer can flip it to `Indexed` once the
                            // last chunk lands (Phase 173).
                            do! setPending scope.Container entry.FileName chunkCount
                        else
                            // Retries exhausted — permanent data loss for this
                            // document's searchability. Log at Error so it trips
                            // error-rate alerting, AND write an event so the drop
                            // is queryable per-document in the RAG event trail
                            // (symmetric with DocumentVectorisationSkipped /
                            // DocumentRejected), not just in the telemetry snapshot.
                            let attempts = List.length enqueueRetryDelaysMs + 1
                            let reason = "ingestion queue full after bounded retry"

                            logger.Error(
                                $"[RAGCompose] Ingestion queue full ({queue.Count}/{queue.Capacity}) after {attempts} attempts — DROPPED vectorisation for {entry.FileName}. The file is saved but is permanently unsearchable until re-uploaded. Raise IngestionQueueCapacity (RAGServerApp.withIngestionQueueCapacity) or IngestionConcurrency if drops recur.",
                                None
                            )

                            do!
                                writeEvent scope.ScopeId "DocumentVectorisationDropped" {|
                                    DocumentId = entry.FileName
                                    FileName = entry.FileName
                                    Container = scope.Container
                                    ChunkCount = chunkCount
                                    QueueDepth = queue.Count
                                    QueueCapacity = queue.Capacity
                                    Attempts = attempts
                                    Reason = reason
                                |}

                            // Phase 303 — deployment-wide `KnowledgeIngestionDropped`
                            // audit + Warning `SystemMessage` to the uploader +
                            // queue `Dropped` counters (rolling 60s + cumulative,
                            // surfaced on /health/rag).
                            do!
                                emitIngestionDrop
                                    auditLogRef.Value
                                    notificationChannelRef.Value
                                    queue
                                    logger
                                    scopeKey
                                    entry.FileName
                                    chunkCount
                                    reason
                                    job.OriginatingUserId

                            do! setStatus scope.Container entry.FileName (FileIngestionStatus.Failed reason)

                            // Phase 303 — compliance-grade fail-loud. Under
                            // `Refuse`, sustained saturation (≥ threshold drops in
                            // the rolling 60s window) raises
                            // `ConfigPreflightFailedException`. The post-save hook
                            // is fire-and-forget and wraps hook exceptions in an
                            // `Error` log (never crashing the already-sent upload
                            // response), so this surfaces as a loud, named error in
                            // the operator log alongside the audit trail rather
                            // than a silent drop.
                            if overflowPolicy = Refuse && queue.DroppedLast60s >= refuseSaturationThreshold then
                                raise (
                                    ConfigValidatorAggregator.ConfigPreflightFailedException(
                                        sprintf
                                            "RAG ingestion queue is sustainedly saturated: %d documents dropped in the last 60s (capacity %d). IngestionOverflowPolicy = Refuse treats this as fail-loud — raise IngestionQueueCapacity / IngestionConcurrency, add a distributed ingestion path, or switch to DropWrite / Block. Most recent drop: '%s'."
                                            queue.DroppedLast60s
                                            queue.Capacity
                                            entry.FileName
                                    )
                                )
    }

// ─── Null blob storage for when no storage is configured ─────────

let private makeNullBlobStorage () : IBlobStorage =
    { new IBlobStorage with
        member _.Upload(_, _, _) = async { return Ok "" }
        member _.Download(_, _) = async { return Error "no blob storage configured" }
        member _.DownloadRange(_, _, _, _) = async { return Error "no blob storage configured" }
        member _.Delete(_, _) = async { return Ok() }
        member _.List(_, _) = async { return [] }
        member _.Exists(_, _) = async { return false }
        member _.GetMetadata(_, _) = async { return Error "no blob storage configured" }

        member _.Erase(_, _, _, _) = async {
            return
                Result.Ok {
                    HandlerName = "blobs"
                    RecordsAffected = 0
                    Note = None
                }
        }
    }


// ─── RAGServerApp — record-based wrapper around `AIServerApp` + RAG ──
//
// `RAGServerApp` is the top layer of the record-based compose stack. It
// wraps an `AIServerApp` (which itself wraps `ServerApp`) and adds a
// single RAG-specific extension point: the embedding provider. The
// vectorisation handlers flow through from each module's `ServerModule`
// via the base `ServerApp`.
//
// Composition root pattern:
//
//     RAGServerApp.empty
//     |> RAGServerApp.withAI (
//         AIServerApp.empty
//         |> AIServerApp.withBase (
//             ServerApp.empty
//             |> ServerApp.withConfig config
//             |> ServerApp.withAuth (Some auth)
//             |> ServerApp.withStorage blobStorage
//             |> ServerApp.addModules [ ... ])
//         |> AIServerApp.withAIFactory factory
//         |> AIServerApp.withProviderProfile providerProfile
//         |> AIServerApp.withAITools allTools)
//     |> RAGServerApp.withEmbeddingProvider embedder
//     |> RAGServerApp.run

/// Record form of `composeWithRAG` arguments. Flat superset of
/// `AIServerApp` (which is itself a flat superset of `ServerApp`):
/// every `with*` helper from the inner layers is mirrored here as a
/// delegating helper, so the user writes a single fluent pipeline. The
/// required RAG dependency (`EmbeddingProvider`) plus the required AI
/// dependencies (`AIProviderFactory`, `ProviderProfile`) are
/// constructor parameters on `RAGServerApp.create`. Vectorisation
/// handlers come
/// from each module's `ServerModule.VectorisationHandlers` via the
/// base `ServerApp`.
type RAGServerApp = {
    AI: AIServerApp
    EmbeddingProvider: IEmbeddingProvider
    /// Observers fired by `IngestionBackgroundService` after each chunk
    /// completes (success or failure). Modules that need to surface live
    /// ingestion status — `KnowledgeBase` is the canonical example —
    /// register an observer here.
    IngestionObservers: IIngestionStatusObserver list
    /// Optional cross-encoder reranker. When `Some`, the retrieval pipeline
    /// inflates its candidate pool and reorders the top candidates by joint
    /// (query, document) relevance before MMR / topK truncation. Companion
    /// packages under `src/Rerankers/<Name>/` provide the implementation.
    Reranker: IReranker option
    /// Enable MMR diversity reranking after retrieval (and rerank, when
    /// present). Off by default — MMR helps duplicate-heavy corpora and
    /// hurts fact-extraction queries; opt in per deployment.
    EnableMmr: bool
    /// MMR `λ` parameter ∈ [0, 1]. Higher = more relevance-weighted, lower
    /// = more diversity-weighted. Ignored when `EnableMmr = false`.
    MmrLambda: float
    /// Optional substitute `IVectorStore`. When `None`, RAG uses the default
    /// `InMemoryVectorStore` (suitable up to ~50k chunks per scope). Companion
    /// packages under `src/VectorStores/<Name>/` (e.g. `Hnsw/` for HNSW.Net)
    /// supply alternatives — wire one in via `withVectorStore` for larger
    /// corpora or specific latency / scale targets.
    VectorStore: IVectorStore option
    /// Phase 501 — optional language-aware analyzer for the sparse (BM25)
    /// index. When `None`, the index uses `SparseAnalysis.identity` — the
    /// pre-501 tokenisation — so an existing deployment is byte-for-byte
    /// unchanged (GP 11). Companion packages under `src/SparseIndices/<Name>/`
    /// supply alternatives: `ToolUp.SparseIndices.Snowball` (stemming +
    /// stop words for European languages), `ToolUp.SparseIndices.Cjk`
    /// (n-gram segmentation for non-space-delimited scripts). Wire one in via
    /// `withSparseAnalyzer`. Composing one over an existing corpus re-analyses
    /// the persisted snapshot at startup rather than leaving the postings in
    /// the old vocabulary.
    SparseAnalyzer: ISparseAnalyzer option
    /// Framing preamble injected into the system prompt ahead of the retrieved
    /// context block. Tells the model that retrieval has already run for the
    /// current turn, that KB content is authoritative for the team's data, and
    /// that an empty result block means the search ran and found nothing.
    /// Defaults to `defaultRetrievalFraming`. Set to `""` to opt out entirely
    /// (e.g. for deployments that compose their own framing layer upstream).
    RetrievalFraming: string
    /// Maximum number of documents the `IngestionBackgroundService` processes
    /// in parallel. Each "slot" issues one batched embedding call covering a
    /// whole document, then indexes its chunks against the cache — so the
    /// effective embedding concurrency is `IngestionConcurrency` HTTP calls,
    /// not chunks. Default 8 (was 4 in the per-chunk world). Tune lower if
    /// the embedding provider rate-limits aggressively, higher for big
    /// corpora behind a key with generous TPM budget.
    IngestionConcurrency: int
    /// Maximum number of pending document jobs in the ingestion queue. When
    /// full, `IngestionQueue.Enqueue` returns `false`; KB upload handlers
    /// surface the rejection by marking the document `Failed`, and the
    /// post-save hook for non-KB modules logs a warning. Default 5,000 —
    /// raise for deployments with bursty bulk uploads, lower for tight
    /// memory budgets.
    IngestionQueueCapacity: int
    /// Phase 14t — retry / dead-letter policy for transient embedder
    /// failures during ingestion (429 rate limits, 5xx, timeouts,
    /// network faults). A transient per-chunk `Index` failure is retried
    /// with exponential backoff + jitter (via `IJobScheduler` when one is
    /// composed, so the pending retry survives process restart; an
    /// in-process loop otherwise) and dead-lettered after `MaxAttempts`;
    /// a permanent failure (401/403 bad credentials, other 4xx) is
    /// dead-lettered immediately with an Owner/Admin alert. Defaults to
    /// `IngestionRetryPolicy.defaults` (5 attempts, 30s→30min backoff,
    /// +20% jitter). Tune via `RAGServerApp.withIngestionRetryPolicy`.
    IngestionRetryPolicy: IngestionRetryPolicy
    /// Optional substitute `IRagTelemetry`. When `None`, RAG installs a
    /// 60-second rolling-window in-memory implementation backing the
    /// `/health/rag` endpoint. Pass `Some` to wire up Prometheus / OTel /
    /// Datadog export — companion packages under `src/RagTelemetry/<Name>/`
    /// (when shipped) provide implementations.
    Telemetry: IRagTelemetry option
    /// Retrieval shape knobs surfaced to `RAGPromptBuilder.withRetrieval`:
    /// `TopK`, `MinScore`, `Merge`, and `SnippetCharLimit`. Defaults reproduce
    /// the prior hard-coded behaviour (top-5, no score gate, interleaved,
    /// 240-char preview); operators tune via `withRetrievalDefaults` /
    /// targeted setters without recompiling.
    RetrievalDefaults: RetrievalDefaults
    /// Feature flag enabling conversation-history indexing. Off by default
    /// (deferred — see WS3.4 in the RAG uplift plan). When `true`, completed
    /// AI conversations are vectorised and committed under
    /// `ChunkOrigin.Conversation` so retrieval can surface "what we
    /// discussed last week". Reading the flag is enough to pre-allocate
    /// the data-model surface; the actual ingestion handler lands in a
    /// follow-up phase.
    IndexConversations: bool
    /// Grounding stance — `Permissive` (default, model uses KB + training
    /// data freely), `Preferred` (KB authoritative on conflict), or
    /// `StrictlyGrounded` (refuse to answer without a retrieval hit).
    /// Tune via `withGroundingMode`.
    GroundingMode: GroundingMode
    /// Master switch for document-level summary chunks (WS4.1). When
    /// `true` (default), `VectorisationHandler.Summarise` is called for
    /// every document and the resulting chunk is indexed under
    /// `_isSummary = "true"`. When `false`, the summary call is skipped
    /// even if a handler defines one — useful for cost-sensitive
    /// deployments. Has no effect if no handler defines `Summarise`.
    EnableDocumentSummaries: bool
    /// Phase 6q — post-stream citation handling. `Strict` (default)
    /// normalises drift variants (`(1)`, `[1]`, `Source 1`, bare
    /// `¹`, `^1`) onto the canonical `[¹]` marker when the digit
    /// binds to a real retrieved source; replaces phantoms (digit >
    /// `sources.Length`) with the `[unverified]` inline tag.
    /// `LenientNormalise` normalises valid digits but strips
    /// phantoms (leaves no marker). `Off` reproduces pre-Phase-6q
    /// behaviour byte-for-byte. Tune via `withCitationPolicy`.
    CitationPolicy: ToolUp.RAG.CitationNormaliser.RagCitationPolicy
    /// Captures `original → clamped` notes whenever a retrieval-defaults
    /// setter clamps an out-of-range value (e.g. `withTopK 0` → `1`,
    /// `withMinScore (Some 1.5)` → `Some 0.99`). The
    /// `RetrievalDefaultsValidator` surfaces these as a single `Warning`
    /// at startup so an operator who typoed a config knob doesn't
    /// silently lose the intended behaviour. Empty in the common case.
    RetrievalDefaultsClampLog: string list
    /// Optional hard cap on the UTF-8 byte size of any single chunk's
    /// `TextChunk.Content` reaching the ingestion queue. A pathological
    /// upload (e.g. an XLSX cell carrying a 100 MB string) that survives
    /// chunking can pin memory across every queued job — the queue is
    /// bounded by item count, not bytes. `None` (default) preserves the
    /// historical no-limit behaviour for backwards compatibility; setting
    /// `Some n` rejects oversize chunks at the post-save hook and emits
    /// `RAG.DocumentRejected` so KB can surface the rejection. Tune via
    /// `RAGServerApp.withMaxChunkBytes`.
    MaxChunkBytes: int option
    /// Optional hard cap on the sum of UTF-8 byte sizes across every chunk
    /// in a single `DocumentIngestionJob`. Same rationale as `MaxChunkBytes`
    /// but bounds the per-document footprint rather than per-chunk. `None`
    /// (default) preserves no-limit behaviour. Tune via
    /// `RAGServerApp.withMaxDocumentBytes`.
    MaxDocumentBytes: int option
    /// Phase 14y — hard cap on the character length of a retrieval query
    /// reaching `IRetrievalPipeline.Retrieve`. Above the cap the pipeline
    /// refuses with a `KnowledgeQueryTooLargeException` and emits a
    /// `KnowledgeQueryRejected` audit — a query this long is almost always a
    /// programming bug (an entire document pasted into the query slot), and
    /// embedding it wastes provider spend / can trip the provider's own token
    /// cap with an opaque error. Defaults to `Some 16384` (~4k tokens); tune
    /// via `RAGServerApp.withMaxQueryChars`. `None` disables the guard (not
    /// reachable through the public builder — the default is always on).
    MaxQueryChars: int option
    /// Phase 14w — tombstone retention window. `IVectorStore.DeleteChunk`
    /// soft-deletes (stamps `_deletedAt`); the scheduled vacuum
    /// hard-removes tombstones older than `now - TombstoneRetention`.
    /// Default 7 days — long enough for an accidental delete to be
    /// restored, short enough that soft-deleted content doesn't pin
    /// memory indefinitely. Tune via `RAGServerApp.withTombstoneRetention`.
    TombstoneRetention: System.TimeSpan
    /// Phase 14w — cron expression for the tombstone auto-vacuum sweep.
    /// `None` (default) = no scheduled vacuum: soft-deleted chunks are only
    /// reclaimed when an operator calls `IVectorStore.Vacuum` manually, so
    /// a long-running replica's memory grows without bound. `Some cron`
    /// (typically via `withVacuumSchedule` = daily 03:00 UTC) registers a
    /// `RAGVacuumJobHandler` on the `IJobScheduler` that sweeps every scope
    /// on the schedule. REQUIRES `ServerConfig.JobScheduler =
    /// InProcessJobScheduler` (or a distributed scheduler companion) — the
    /// `VacuumScheduleValidator` warns when the pair is misconfigured.
    VacuumSchedule: string option
    /// Phase 303 — behaviour when the ingestion queue is full. `DropWrite`
    /// (default) retries with a short bounded backoff then drops the
    /// document, emitting a `KnowledgeIngestionDropped` audit + a Warning
    /// `SystemMessage` to the uploader + the queue `Dropped` counters.
    /// `Block` never drops — the post-save hook awaits queue space (delays
    /// indexing under load, never loses a document). `Refuse` drops like
    /// `DropWrite` but additionally raises `ConfigPreflightFailedException`
    /// on sustained saturation for compliance-grade fail-loud. Tune via
    /// `RAGServerApp.withIngestionQueueOverflowPolicy`.
    OverflowPolicy: IngestionOverflowPolicy
    /// Phase 63.A — substitute the entire `IRetrievalPipeline`. `None`
    /// (default) builds the standard dense/hybrid `RetrievalPipeline` over
    /// the composed `IVectorStore` / `ISparseIndex` / `IEmbeddingProvider`.
    /// `Some p` registers `p` as the `IRetrievalPipeline` verbatim and skips
    /// the default pipeline construction — the seam a build-time-precomputed
    /// static-corpus pipeline (or any custom retrieval implementation) slots
    /// into. When set AND no `VectorisationHandler` is registered, the
    /// ingestion + reembedding background services are also suppressed (no
    /// producers ⇒ no consumers needed) so a static-doc deployment carries
    /// no live-ingestion overhead (GP 13). Query embedding still resolves the
    /// registered `IEmbeddingProvider`; only chunk embeddings move to build
    /// time. Set via `RAGServerApp.withRetrievalPipeline`.
    RetrievalPipelineOverride: IRetrievalPipeline option
    /// Phase 509 — durable backing for the ingestion queue. `None`
    /// (default) keeps the process-local `System.Threading.Channels`
    /// queue: correct for one replica and one process lifetime, and the
    /// reason `RagIngestionInstanceValidator` refuses `ReplicaCount > 1`.
    /// `Some store` moves the queue into that store, so documents survive
    /// a restart (an unacknowledged lease is redelivered) and N replicas
    /// drain one queue — which lifts the multi-replica refusal. The
    /// shipped durable backing is the `ToolUp.IngestionQueues.Redis`
    /// companion. Set via `RAGServerApp.withDurableIngestionQueue`.
    IngestionQueueStore: IIngestionQueueStore option
    /// Phase 509 — storage containers swept at startup for documents
    /// left `Pending` by a process that died mid-ingestion. Empty
    /// (default) ⇒ no sweep, so an existing deployment is unchanged
    /// (GP 11). The sweep matters most on the IN-MEMORY queue, where a
    /// restart genuinely loses the job and the per-file status would
    /// otherwise read `Pending` forever with nothing ever to clear it;
    /// on a durable queue the job is redelivered instead, so the sweep is
    /// belt-and-braces. Containers are enumerated by the consumer (the
    /// SDK has no scope-enumeration seam) — the same shape KB's
    /// `recoverStuckDocumentsAtStartup` uses. Set via
    /// `RAGServerApp.withIngestionRecoverySweep`.
    IngestionRecoveryScopes: string list
    /// Phase 633 — substitute the `IEmbeddingCache` the composition wraps
    /// the supplied `IEmbeddingProvider` in. `None` (default) constructs
    /// the process-local `InMemoryEmbeddingCache`, so an existing
    /// deployment is byte-for-byte unchanged (GP 11).
    ///
    /// A process-local cache is correct but per-replica: `EmbeddingCacheKey`
    /// carries no tenant component and each replica keeps its own entries,
    /// so the same text hits on replica A and misses on replica B — which
    /// is what `TeamModeSharedEmbeddingCacheValidator` warns about under
    /// `ReplicaCount > 1`. Composing a cross-replica cache (the shipped
    /// backing is the `ToolUp.EmbeddingCaches.Redis` companion) removes the
    /// divergence **and lifts that warning**, because the premise it rests
    /// on no longer holds. Set via `RAGServerApp.withEmbeddingCache`.
    EmbeddingCache: IEmbeddingCache option
}

/// Phase 633 — does this app's composed `IEmbeddingCache` span replicas?
///
/// This is the exact predicate `composeWithRAG` feeds into
/// `TeamModeSharedEmbeddingCacheValidator`, exposed so the lift can be
/// asserted (by a deployment's own preflight, or by a test) without
/// standing up a whole composition. Note what it deliberately does NOT
/// say: composing `InMemoryEmbeddingCache` explicitly returns `false`,
/// because the concern is the per-replica divergence, not whether a hook
/// was called — an operator who wires the process-local cache by hand has
/// changed nothing about what the validator is warning at.
let hasCrossReplicaEmbeddingCache (app: RAGServerApp) : bool =
    match app.EmbeddingCache with
    | Some composed -> not (composed :? InMemoryEmbeddingCache)
    | None -> false

// ─── composeRAG ───────────────────────────────────────────────────
//
// Phase 1h seam (RAG half). `composeRAG : RAGServerApp -> ServerApp`
// composes *over* `composeAI`: it builds the RAG-aware system-prompt
// layers (framing preamble + retrieval builder + strict-grounding
// guard), folds them into the AI assistant config, runs `composeAI` to
// lift every AI contribution (agent-loop handlers, tool registry, AI
// DI registrations, dev endpoints, tool-name + platform-provider
// validation) onto the inner `ServerApp`, then layers the RAG-specific
// contributions (vector store / sparse index / pipeline / ingestion +
// reembedding hosted services, citation normaliser, the `/health/rag`
// + `/dev/rag-citation` routes, the RAG config validators) on top.
//
// This replaces the former positional `composeWithRAG`, which inlined a
// verbatim copy of every AI DI registration and drifted from
// `AICompose.fs` whenever AI added infrastructure (Phase 6g.A / 6h both
// shipped AI-only and silently broke RAG deployments). Composing over
// `composeAI` makes that drift structurally impossible — RAG inherits
// every AI registration for free, including ones the hand-copy had
// fallen behind on (e.g. the Phase 171 `IActiveAiProbe`).
//
// `AIServerApp.run` is `composeAI >> ServerApp.run`; `RAGServerApp.run`
// is now likewise `composeRAG >> ServerApp.run`, and the additive
// `withRAG` extension calls `composeRAG` from inside a `ServerApp`-
// shaped pipeline so RAG contributions stack with Forms / AI / future
// companions on one composition root.

/// Apply every RAG-specific contribution onto the inner `ServerApp`
/// (composing over `composeAI` for the AI half), returning the composed
/// result without driving it. `RAGServerApp.run` calls this then
/// `ServerApp.run`; the additive `withRAG` extension calls it from
/// inside a `ServerApp`-shaped pipeline so RAG stacks with Forms / AI
/// contributions onto one composition root (Phase 1h goal).
///
/// **Advanced.** Consumers should use `RAGServerApp.run` unless they are
/// stacking multiple companion supersets — in which case use the
/// `withRAG` additive extension. Hidden from IntelliSense via
/// `[<EditorBrowsable>]`.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
let composeRAG (app: RAGServerApp) : ServerApp =
    let ai = app.AI
    let b = ai.Base
    let config = b.Config

    // Phase 14r — derive the tool-aware framing summary once, from the
    // deployment's aggregated module tool list (tools land here via
    // `withAITools`). Phase 538 — the derivation reads each tool's typed
    // `IsLiveInterface` declaration (or its `ClientResident` location),
    // never its name. Drives both the framing companion below and the
    // retrieval builder's empty-result message. A deployment with no
    // live-interface tools yields `HasLiveUiTools = false` ⇒ historical
    // framing, no regression.
    let toolFraming = RAGPromptBuilder.ToolFraming.fromTools (b.AITools |> List.map fst)

    // Phase 63.A — retrieval-pipeline override seam. When a consumer supplies
    // a pipeline via `withRetrievalPipeline` (e.g. a build-time-precomputed
    // static-corpus pipeline), it is registered verbatim and the default
    // `RetrievalPipeline` construction is skipped. `suppressIngestion`
    // additionally drops the ingestion + reembedding background services when
    // the override is set AND no module contributes a `VectorisationHandler`
    // (no producers ⇒ no consumers): a static-doc deployment then carries no
    // live-ingestion overhead (GP 13). Query embedding still resolves the
    // registered `IEmbeddingProvider`; only chunk embeddings move to build time.
    let suppressIngestion =
        app.RetrievalPipelineOverride.IsSome && b.VectorisationHandlers.IsEmpty

    // Phase 509 — one queue type, two backings. No store composed ⇒ the
    // historical process-local channel, byte for byte (GP 11 / GP 13);
    // `Some store` ⇒ the same surface over durable storage, with
    // lease-based at-least-once delivery and cross-replica draining.
    let queue =
        match app.IngestionQueueStore with
        | Some store -> IngestionQueue(app.IngestionQueueCapacity, app.OverflowPolicy, store)
        | None -> IngestionQueue(app.IngestionQueueCapacity, app.OverflowPolicy)

    // Default to a 60-second rolling-window telemetry sink so `/health/rag`
    // is meaningful out-of-the-box. Deployments wanting Prometheus / OTel
    // export pass their own `IRagTelemetry` via `withTelemetry`.
    let telemetry =
        app.Telemetry
        |> Option.defaultWith (fun () -> ToolUp.RAG.RagTelemetry.createDefault ())

    // Resolve a logger once — used for the null-blob warning, IngestionService,
    // the vector store flush loop, and the post-save vectorisation hook. Falls
    // back to `ConsoleLogger` so callers who don't pass one still see warnings.
    let ragLogger =
        b.Logger
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    // EventStore reference — filled in the RAG service config callback below.
    // The post-save hook captures it lazily so handler-skip and oversize-doc
    // rejections can write `RAG.*` events. Deferred-ref pattern (same as
    // `pipelineRef` / `tracerRef`) because the hook is built before the
    // service config runs.
    let eventStoreRef: IEventStore option ref = ref None

    // Phase 173 — the per-file ingestion-status store. Built in the
    // service-config callback below (where the `IDataObjectStore` probe
    // is available) and captured here by deferred ref, same pattern as
    // `eventStoreRef`. The post-save hook writes `Pending` / `Failed`
    // through it; the ingestion observer writes `Indexed`; `FileManagement`
    // reads it to badge the file list. `None` until the callback runs ⇒
    // the hook's status writes are no-ops if a file somehow ingests first.
    let ingestionStatusStoreRef: IIngestionStatusStore option ref = ref None

    // Phase 303 — audit log + notification channel references for the
    // ingestion-drop observability triple. Both are resolved from the
    // probe in the service-config callback below (same instances the
    // vector store's corrupt-index path uses), so — like `eventStoreRef` —
    // they're captured here by deferred ref because the post-save hook is
    // built before that callback runs. `None` until then ⇒ a drop before
    // the callback degrades to the queue counter + `Error` log only.
    let auditLogRef: IAuditLog option ref = ref None
    let notificationChannelRef: INotificationChannel option ref = ref None

    // Register the post-save vectorisation hook so `SessionFileStore.AddFile`
    // enqueues chunks after every successful file upload.
    if not b.VectorisationHandlers.IsEmpty then
        configurePostSaveHooks [
            makeVectorisationHook
                b.VectorisationHandlers
                queue
                telemetry
                app.EnableDocumentSummaries
                app.MaxChunkBytes
                app.MaxDocumentBytes
                eventStoreRef
                ingestionStatusStoreRef
                app.OverflowPolicy
                auditLogRef
                notificationChannelRef
                ragLogger
        ]

    // Pipeline + tracer references — filled in the RAG service config callback
    // below, before any request arrives. The prompt builder reads both lazily
    // at request time so the miss-diagnostic path resolves the same tracer the
    // pipeline writes its `KnowledgeRetrieved` events through.
    let pipelineRef: IRetrievalPipeline option ref = ref None
    let tracerRef: IRetrievalTracer option ref = ref None

    let ragBuilder: SystemPromptBuilder =
        fun ctx -> async {
            match pipelineRef.Value with
            | Some pipeline ->
                let! block =
                    (withRetrievalToolAware app.RetrievalDefaults (Some telemetry) tracerRef.Value toolFraming pipeline)
                        ctx

                // Server-side strict-grounding guard. Under `StrictlyGrounded`,
                // an empty retrieved set means "knowledge base had nothing
                // relevant" — refuse the turn WITHOUT a provider call rather
                // than relying solely on the prompt directive (which a model
                // can ignore or be jailbroken past). Pairs with
                // `strictlyGroundedDirective`.
                if app.GroundingMode = StrictlyGrounded && List.isEmpty ctx.RetrievedSources.Value then
                    ctx.ShortCircuit.Value <- Some "I don't have information on that in your knowledge base."

                return block
            | None -> return ""
        }

    // Framing preamble: explains to the model what the KB is, that retrieval
    // has already run for the current turn, and how to behave when nothing was
    // found. The `GroundingMode` further shapes the directive. Phase 14r — the
    // tool-aware resolver appends the live-interface companion under
    // `Preferred` when the deployment has such tools, so live-screen questions
    // defer to the inspection tool rather than a KB-miss refusal.
    let resolvedFraming =
        resolveFramingWithTools app.GroundingMode app.RetrievalFraming toolFraming

    let framingBuilder: SystemPromptBuilder option =
        if System.String.IsNullOrWhiteSpace resolvedFraming then
            None
        else
            Some(SystemPromptBuilder.fromStatic resolvedFraming)

    // Extend the AI config to include the framing preamble (if any) and the
    // retrieval builder. Order matters: the model sees framing *before* the
    // retrieved chunks so it knows what they are when it reads them.
    let resolvedAiConfig =
        let ragLayers = [ yield! framingBuilder |> Option.toList; yield ragBuilder ]

        match ai.AIConfig with
        | None ->
            Some {
                Branding = {
                    Name = "AI Assistant"
                    Icon = ""
                    ShowSidePanel = true
                }
                SystemPrompt = Some(SystemPromptBuilder.compose ragLayers)
                MaxHistoryMessages = None
                AISurfaceDerivation = TrustClient
            }
        | Some cfg ->
            let composedBuilder =
                match cfg.SystemPrompt with
                | Some existing -> SystemPromptBuilder.compose (existing :: ragLayers)
                | None -> SystemPromptBuilder.compose ragLayers

            Some {
                cfg with
                    SystemPrompt = Some composedBuilder
            }

    // Lift every AI contribution onto the inner `ServerApp` via `composeAI`,
    // injecting the RAG-augmented assistant config so the agent loop sees the
    // framing preamble + retrieval block. `composeAI` owns all AI DI
    // registrations, the agent-loop handlers, the tool registry, the AI dev
    // endpoints, and the tool-name + platform-provider validation — RAG no
    // longer duplicates any of them.
    let aiComposed: ServerApp = composeAI { ai with AIConfig = resolvedAiConfig }

    // Phase 6q follow-up C — /dev/rag-citation rolling-window stats. Master
    // gate is `ServerConfig.EnableDevEndpoints`; the per-endpoint
    // `EnableCitationDevEndpoint` override can only suppress, never force-on.
    let citationEndpointEnabled =
        match config.EnableCitationDevEndpoint with
        | Some explicit -> explicit && config.EnableDevEndpoints
        | None -> config.EnableDevEndpoints

    // Startup visibility: one line naming the dev endpoints this composition
    // actually exposes (AI dev endpoints are registered by `composeAI`; the
    // citation endpoint is RAG-owned). The default-off shape stays log-silent.
    do
        let active = [
            if config.EnableDevEndpoints then
                yield "/dev/ai-fastpath"
                yield "/dev/ai-latency"
            if citationEndpointEnabled then
                yield "/dev/rag-citation"
        ]

        if not active.IsEmpty then
            ragLogger.Info(sprintf "[RAGCompose] Dev endpoints registered: %s" (String.concat ", " active))

    // Phase 6q follow-up — ICitationNormaliser seam. Always register the
    // rolling-window counter store (the dev endpoint resolves it
    // unconditionally); register the normaliser implementation only when the
    // policy isn't Off so the AI handler can resolve `null` and skip the pass
    // on Off deployments (byte-for-byte pre-Phase-6q behaviour).
    let citationCounters: ICitationCounters =
        ToolUp.RAG.CitationNormaliserImpl.RollingCitationCounters() :> _

    let citationNormaliserOpt: ICitationNormaliser option =
        if app.CitationPolicy = CitationNormaliser.Off then
            None
        else
            Some(ToolUp.RAG.CitationNormaliserImpl.create app.CitationPolicy citationCounters)

    // RAG-owned routes layered on top of the AI handlers `composeAI` mounted.
    let ragHandlers = [
        ToolUp.RAG.RagHealthHandler.route
        if citationEndpointEnabled then
            yield! ToolUp.RAG.RAGCitationDevEndpoint.routes
    ]

    let ragServiceConfig (s: IServiceCollection) =
        // Citation seam — RAG-specific (composeWithAI callers never register
        // these; the AI handler resolves `null` and skips the pass).
        let s = s.AddSingleton<ICitationCounters>(citationCounters)

        let s =
            match citationNormaliserOpt with
            | Some normaliser -> s.AddSingleton<ICitationNormaliser>(normaliser)
            | None -> s

        // RAG services
        let blobStorageForRag =
            match b.Storage with
            | Some bs -> bs
            | None ->
                ragLogger.Warn
                    "[RAGCompose] No IBlobStorage supplied — vector index persistence is disabled. Ingested chunks will be lost across process restart. Pass `Some storage` (RAGServerApp.withStorage) for a durable deployment."

                makeNullBlobStorage ()

        let sparseIndex: ISparseIndex =
            match app.SparseAnalyzer with
            | None -> new InMemoryBM25Index(blobStorageForRag, logger = ragLogger) :> ISparseIndex
            | Some analyzer ->
                new InMemoryBM25Index(blobStorageForRag, logger = ragLogger, analyzer = analyzer) :> ISparseIndex

        // Wrap the supplied embedder so repeated query / chunk text hits a
        // cache rather than the underlying provider. Cache key includes
        // provider + model + dimensions so a model swap automatically
        // invalidates entries.
        //
        // Phase 633 — `withEmbeddingCache` substitutes the cache. Unset
        // (the default) constructs exactly the `InMemoryEmbeddingCache`
        // this site hard-constructed before, so an existing deployment is
        // byte-for-byte unchanged (GP 11).
        let embeddingCache: IEmbeddingCache =
            match app.EmbeddingCache with
            | Some composed -> composed
            | None -> new InMemoryEmbeddingCache() :> IEmbeddingCache

        let cachedEmbedder: IEmbeddingProvider =
            CachingEmbeddingProvider.create app.EmbeddingProvider embeddingCache

        let pipelineOptions: RetrievalPipelineOptions = {
            Reranker = app.Reranker
            EnableMmr = app.EnableMmr
            MmrLambda = app.MmrLambda
            ActiveModuleBoost = RetrievalPipelineOptions.defaults.ActiveModuleBoost
            SummaryBoost = RetrievalPipelineOptions.defaults.SummaryBoost
            FactNarrativeJoinBoost = RetrievalPipelineOptions.defaults.FactNarrativeJoinBoost
            QueryRewriteTimeoutMs = RetrievalPipelineOptions.defaults.QueryRewriteTimeoutMs
        }

        // Build the probe provider ONCE and resolve every pre-pipeline
        // registration from it. One probe = one consistent view (calling
        // `BuildServiceProvider` per lookup leaked duplicate singletons and
        // could resolve a different instance than the running app uses).
        let probe = s.BuildServiceProvider()

        // Resolve event store first so the retrieval tracer can write
        // `KnowledgeRetrieved` audit events alongside ingestion / RBAC /
        // file-op audit events.
        let eventStore =
            match probe.GetService(typeof<IEventStore>) with
            | :? IEventStore as es -> es
            | _ -> ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> IEventStore

        // Hand the resolved event store to the post-save hook so handler-skip
        // and oversize-doc rejections emit `RAG.*` audit events.
        eventStoreRef.Value <- Some eventStore

        // Phase 173 — per-file ingestion-status store + Data Manager
        // observer. Durable over `IDataObjectStore` when present (status
        // survives a restart), in-memory otherwise (ephemeral / test
        // scopes). ONE instance is shared three ways: the post-save hook
        // (writes `Pending` / `Failed`) and the observer (writes `Indexed`)
        // capture it directly; `FileManagement` resolves the same singleton
        // (registered below) to badge the file list. Only built here, when
        // RAG is composed — a no-RAG deployment never registers it, so the
        // file list shows no status column (GP 13).
        let ingestionStatusStore: IIngestionStatusStore =
            match probe.GetService(typeof<IDataObjectStore>) with
            | :? IDataObjectStore as dos -> IngestionStatusStore.create dos (Some ragLogger)
            | _ -> IngestionStatusStore.createInMemory ()

        ingestionStatusStoreRef.Value <- Some ingestionStatusStore

        // Live-badge notifications are published only when a channel is
        // composed; `None` ⇒ the badge still refreshes on the next
        // `ListFiles` (GP 13).
        let ingestionNotificationChannel =
            match probe.GetService(typeof<INotificationChannel>) with
            | :? INotificationChannel as ch -> Some ch
            | _ -> None

        // Phase 303 — hand the resolved notification channel to the
        // post-save hook so a queue-drop can warn the uploading user.
        notificationChannelRef.Value <- ingestionNotificationChannel

        let dataManagerIngestionObserver =
            ToolUp.RAG.DataManagerIngestionObserver.create ingestionStatusStore ingestionNotificationChannel ragLogger

        // Phase 14v — build the default vector store here (after the probe)
        // so it can resolve the audit log + notification channel and make
        // corrupt-index loads loud (audit event + telemetry counter +
        // SystemMessage) instead of starting the scope silently empty. Both
        // collaborators are optional — a `NoAuditLog` / channel-less
        // deployment still gets the telemetry counter + Warn log line.
        let ragAuditLog =
            match probe.GetService(typeof<IAuditLog>) with
            | :? IAuditLog as a -> Some a
            | _ -> None

        // Phase 303 — hand the resolved audit log to the post-save hook so
        // a queue-drop writes a `KnowledgeIngestionDropped` row under
        // `_platform.knowledge` (same instance the corrupt-index path uses).
        auditLogRef.Value <- ragAuditLog

        let vectorStore: IVectorStore =
            app.VectorStore
            |> Option.defaultWith (fun () ->
                new InMemoryVectorStore(
                    blobStorageForRag,
                    logger = ragLogger,
                    telemetry = telemetry,
                    ?auditLog = ragAuditLog,
                    ?notifications = ingestionNotificationChannel
                )
                :> IVectorStore)

        // Pick the registered tracer if any; default to the event-store tracer
        // so retrieval traces are persisted out-of-the-box.
        let retrievalTracer: IRetrievalTracer =
            match probe.GetService(typeof<IRetrievalTracer>) with
            | :? IRetrievalTracer as t -> t
            | _ -> ToolUp.RAG.RetrievalTracers.createEventStore eventStore ragLogger

        let pipeline: IRetrievalPipeline =
            // Phase 63.A — an override (e.g. the static-corpus pipeline) is
            // registered verbatim; the default dense/hybrid `RetrievalPipeline`
            // is not constructed. The override still receives the query through
            // `IRetrievalPipeline.Retrieve` and embeds it via the registered
            // `IEmbeddingProvider`; it just doesn't consume the vector store /
            // sparse index / reranker this branch would build.
            match app.RetrievalPipelineOverride with
            | Some overridePipeline -> overridePipeline
            | None ->
                // Phase 4b commit 5 + runtime-toggle follow-up — wire a snapshot
                // thunk that reads the live `IPlatformRuntimeConfigStore` value
                // when registered, otherwise falls back to the static
                // `ServerConfig.PlatformKnowledgeBase` value.
                let runtimeStoreOpt =
                    match probe.GetService(typeof<IPlatformRuntimeConfigStore>) with
                    | :? IPlatformRuntimeConfigStore as rc -> Some rc
                    | _ -> None

                let snapshot () =
                    match runtimeStoreOpt with
                    | Some rc -> rc.Snapshot()
                    | None -> config.PlatformKnowledgeBase

                // Phase 558 — fact-first wiring. When the deployment composed
                // the fact tier (`FactsCompose.withFactStore` registers the
                // `IFactResolver` + `IFactDisclosureGate` alongside the store,
                // before this compose runs), the pipeline picks both up here so
                // the Phase 522 fact stage + the Phase 525 retrieval egress
                // door fire with zero extra config. Absent registrations ⇒ the
                // constructor args are omitted and the pipeline is
                // byte-identical to a fact-less deployment (GP 11 / GP 13).
                // The pair is structural: the fact compose never registers one
                // without the other, so a resolver here always arrives gated.
                let factResolverOpt =
                    match probe.GetService(typeof<IFactResolver>) with
                    | :? IFactResolver as r -> Some r
                    | _ -> None

                let disclosureGateOpt =
                    match probe.GetService(typeof<IFactDisclosureGate>) with
                    | :? IFactDisclosureGate as g -> Some g
                    | _ -> None

                // Phase 506 — conversation-aware query rewrite, resolved the
                // same way the fact tier is: a deployment registers an
                // `IQueryRewriter` in DI (the shipped provider-backed one is
                // `ProviderQueryRewriter.create`) before `withRAG` runs, and
                // the pipeline picks it up. No registration ⇒ the argument is
                // omitted and retrieval is byte-identical (GP 11 / GP 13),
                // which is why this is a probe rather than a `RAGServerApp`
                // field: the opt-in already has a home, and the compose
                // surface does not grow a knob for it.
                let queryRewriterOpt =
                    match probe.GetService(typeof<IQueryRewriter>) with
                    | :? IQueryRewriter as r -> Some r
                    | _ -> None

                RetrievalPipeline(
                    vectorStore,
                    cachedEmbedder,
                    sparseIndex,
                    pipelineOptions,
                    retrievalTracer,
                    platformKnowledgeBaseSnapshot = snapshot,
                    // Phase 122 — same instance the `/health/rag` endpoint resolves,
                    // so per-stage P50/P95 surface in the snapshot.
                    telemetry = telemetry,
                    // Phase 14y — query-size cap + audit sink for the hard refusal.
                    ?maxQueryChars = app.MaxQueryChars,
                    eventStore = eventStore,
                    // Phase 558 — the fact stage + its retrieval egress door,
                    // present exactly when the fact tier is composed.
                    ?factResolver = factResolverOpt,
                    ?disclosureGate = disclosureGateOpt,
                    // Phase 506 — present exactly when a deployment registered
                    // an IQueryRewriter; absent otherwise.
                    ?queryRewriter = queryRewriterOpt
                )
                :> IRetrievalPipeline

        // Fill the deferred pipeline + tracer references so the prompt builder
        // works at request time. Both share the same tracer instance so traces
        // and miss diagnostics land in the same event-store stream.
        pipelineRef.Value <- Some pipeline
        tracerRef.Value <- Some retrievalTracer

        // Resolve usage-metering + quota substrate from the single probe (G10)
        // so embedding spend is attributed per scope and pre-flight-gated like
        // AI provider calls. Defensive NoOp fallback keeps ingestion working if
        // either is somehow absent.
        let ingestionUsageLog =
            match probe.GetService(typeof<IUsageLog>) with
            | :? IUsageLog as u -> u
            | _ -> NoOpUsageLog() :> IUsageLog

        let ingestionQuota =
            match probe.GetService(typeof<ITeamQuotaPolicy>) with
            | :? ITeamQuotaPolicy as q -> q
            | _ -> NoOpTeamQuotaPolicy() :> ITeamQuotaPolicy

        // Append the Data Manager ingestion-status observer (Phase 173) to
        // whatever observers the app registered (e.g. KB's). Append-only —
        // KB's observer is untouched; each fires independently and scopes
        // itself by the documents it tracks.
        let ingestionObservers = app.IngestionObservers @ [ dataManagerIngestionObserver ]

        // Phase 14t — retry / dead-letter substrate. One shared
        // `IngestionAlertState` spans the first-attempt path (the
        // background service) and the scheduled retry handler, so
        // provider-unavailable dedup + the dead-letter-rate threshold
        // count both. `ingestionNotificationChannel` (resolved above)
        // carries the Owner/Admin alerts; `None` ⇒ audit-only.
        let ingestionAlertState = IngestionAlertState()

        let retryHandlerDeps: IngestionRetryDeps = {
            Pipeline = pipeline
            EventStore = eventStore
            Observers = ingestionObservers
            NotificationChannel = ingestionNotificationChannel
            Telemetry = telemetry
            Logger = ragLogger
            Policy = app.IngestionRetryPolicy
            AlertState = ingestionAlertState
        }

        // The background service registers this handler on the resolved
        // scheduler at startup (the handler is compiled after
        // IngestionService, so the registration is injected here rather
        // than referenced from inside the service).
        let registerRetryHandler (scheduler: IJobScheduler) =
            scheduler.RegisterHandler(RetryHandlerName, ToolUp.RAG.IngestionRetryJobHandler.create retryHandlerDeps)

        // Background re-embedding: when a deployment swaps the embedding model,
        // chunks indexed under the old `EmbeddingVersion` are detected and
        // re-indexed via `IRetrievalPipeline.Index`.
        let reembedQueue = ToolUp.RAG.ReembeddingService.ReembeddingQueue()

        let reembedSvc =
            ToolUp.RAG.ReembeddingService.create reembedQueue vectorStore pipeline cachedEmbedder eventStore ragLogger

        // Document-understanding defaults: register no-ops so the KB extractor
        // can resolve them from DI unconditionally. Companion packages override
        // by registering their concrete provider *before* `composeRAG` runs.
        let ocrProvider: IOcrProvider =
            match probe.GetService(typeof<IOcrProvider>) with
            | :? IOcrProvider as p -> p
            | _ -> ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()

        let tableExtractor: ITableExtractor =
            match probe.GetService(typeof<ITableExtractor>) with
            | :? ITableExtractor as t -> t
            | _ -> ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()

        // Phase 115 — the unified index-lifecycle seam over every index tier
        // this composition fuses, so KB deletion paths fan out across the
        // vector store AND the sparse index.
        let indexLifecycle: ToolUp.Platform.IIndexLifecycle.IIndexLifecycle =
            ToolUp.Platform.IIndexLifecycle.DefaultIndexLifecycle(
                vectorStore,
                Some sparseIndex,
                Some embeddingCache,
                ragLogger
            )

        let s =
            s
                .AddSingleton<IVectorStore>(vectorStore)
                .AddSingleton<ISparseIndex>(sparseIndex)
                .AddSingleton<ToolUp.Platform.IIndexLifecycle.IIndexLifecycle>(indexLifecycle)
                .AddSingleton<IEmbeddingProvider>(cachedEmbedder)
                .AddSingleton<IEmbeddingCache>(embeddingCache)
                .AddSingleton<IRetrievalPipeline>(pipeline)
                .AddSingleton<IngestionQueue>(queue)
                .AddSingleton<ToolUp.RAG.ReembeddingService.ReembeddingQueue>(reembedQueue)
                .AddSingleton<IOcrProvider>(ocrProvider)
                .AddSingleton<ITableExtractor>(tableExtractor)
                .AddSingleton<IRagTelemetry>(telemetry)
                // Phase 173 — register the SAME store instance the hook +
                // observer write through, so `FileManagement.ListFiles`
                // resolves it and joins status onto the file-list read.
                .AddSingleton<IIngestionStatusStore>(ingestionStatusStore)

        // Phase 63.A — the ingestion + reembedding hosted services are the
        // load-bearing live-ingestion overhead. Register them only when NOT
        // suppressed. `suppressIngestion` is set when a retrieval-pipeline
        // override is composed AND no module contributes a
        // `VectorisationHandler` — i.e. a static-corpus / read-only-retrieval
        // deployment with no producers, so these consumers have nothing to do
        // (GP 13). A deployment that keeps the default pipeline, or overrides
        // the pipeline but still registers handlers, keeps both services.
        let s =
            if suppressIngestion then
                ragLogger.Info(
                    "[RAGCompose] Retrieval pipeline overridden with no VectorisationHandler registered — ingestion + reembedding background services suppressed (static-corpus / read-only retrieval mode)."
                )

                s
            else
                s
                    // Phase 14t — factory registration so the ingestion service
                    // captures the REAL host provider and can lazily resolve the
                    // (downstream-built) `IJobScheduler` for durable retries.
                    .AddSingleton<IHostedService>(fun (sp: System.IServiceProvider) ->
                        let getJobScheduler () =
                            match sp.GetService(typeof<IJobScheduler>) with
                            | :? IJobScheduler as js -> Some js
                            | _ -> None

                        create
                            queue
                            pipeline
                            cachedEmbedder
                            eventStore
                            ingestionObservers
                            app.IngestionConcurrency
                            ragLogger
                            telemetry
                            ingestionUsageLog
                            ingestionQuota
                            app.IngestionRetryPolicy
                            ingestionNotificationChannel
                            ingestionAlertState
                            getJobScheduler
                            registerRetryHandler
                        :> IHostedService)
                    .AddSingleton<IHostedService>(reembedSvc)

        // Phase 509.C — startup recovery sweep for the per-file ingestion
        // status. A restart mid-ingestion leaves the DURABLE status entry
        // at `Pending` while the in-memory job it referred to is gone, so
        // the Data Manager badge reads "still ingesting" forever with
        // nothing left to clear it. This flips those entries to `Failed`
        // with a restart-interrupted reason, making the loss visible.
        // Only registered when the consumer named containers to sweep —
        // an unconfigured deployment carries no hosted service (GP 13).
        let s =
            if app.IngestionRecoveryScopes.IsEmpty then
                s
            else
                let scopes = app.IngestionRecoveryScopes
                let statusStore = ingestionStatusStore

                let reason =
                    "Ingestion was interrupted by a process restart before the document finished indexing. Re-upload the file to re-index it."

                s.AddSingleton<IHostedService>(
                    { new IHostedService with
                        member _.StartAsync(_ct) =
                            async {
                                let mutable total = 0

                                for scope in scopes do
                                    try
                                        let! entries = statusStore.List scope

                                        let stuck =
                                            entries
                                            |> List.filter (fun (_, status) -> status = FileIngestionStatus.Pending)

                                        for (documentId, _) in stuck do
                                            do! statusStore.Set(scope, documentId, FileIngestionStatus.Failed reason)
                                            total <- total + 1
                                    with ex ->
                                        ragLogger.Error(
                                            sprintf
                                                "[RAGCompose] event=ingestion_recovery_scan_failed container=%s: skipping this container"
                                                scope,
                                            Some ex
                                        )

                                if total > 0 then
                                    ragLogger.Warn(
                                        sprintf
                                            "[RAGCompose] event=ingestion_recovery_swept count=%d containers=%d: document(s) left Pending by a prior process were marked Failed. Affected uploaders see a Failed badge and can re-upload."
                                            total
                                            (List.length scopes)
                                    )
                            }
                            |> Async.StartAsTask
                            :> System.Threading.Tasks.Task

                        member _.StopAsync(_ct) =
                            System.Threading.Tasks.Task.CompletedTask
                    }
                )

        let s =
            match app.Reranker with
            | Some r -> s.AddSingleton<IReranker>(r)
            | None -> s

        // Phase 14w — tombstone auto-vacuum. When a vacuum schedule is
        // configured, register the vacuum handler + schedule a cron job on
        // the resolved `IJobScheduler`. Deferred to an `IHostedService`
        // `StartAsync` (mirrors the DSR handler-registration pattern in
        // `ComposeJobs.registerDataSubjectRequestJobs`) because the
        // scheduler singleton is built downstream in `ComposeJobs` — it is
        // only resolvable from the built provider, not from this callback.
        // The handler captures the local `vectorStore` / `eventStore`
        // singletons directly (same instances registered above). Scheduling
        // is idempotent (stable key + one-year TTL) so a restart re-attaches
        // to the existing job definition rather than duplicating it.
        let s =
            match app.VacuumSchedule with
            | None -> s
            | Some cron ->
                let vacuumDeps: ToolUp.RAG.RAGVacuumJobHandler.RAGVacuumDeps = {
                    VectorStore = vectorStore
                    EventStore = eventStore
                    Retention = app.TombstoneRetention
                    Logger = ragLogger
                }

                let vacuumHandler = ToolUp.RAG.RAGVacuumJobHandler.create vacuumDeps

                s.AddSingleton<IHostedService>(fun (sp: System.IServiceProvider) ->
                    { new IHostedService with
                        member _.StartAsync(_ct) =
                            match sp.GetService(typeof<IJobScheduler>) with
                            | :? IJobScheduler as scheduler ->
                                scheduler.RegisterHandler(
                                    ToolUp.RAG.RAGVacuumJobHandler.VacuumHandlerName,
                                    vacuumHandler
                                )

                                let registration: JobRegistration = {
                                    ScopeId = "_platform"
                                    Handler = ToolUp.RAG.RAGVacuumJobHandler.VacuumHandlerName
                                    Payload = ""
                                    Trigger = CronTrigger cron
                                    Idempotency =
                                        Some {
                                            Key = "rag-tombstone-vacuum-_platform"
                                            TtlSeconds = 60 * 60 * 24 * 365
                                        }
                                    RetryPolicy = JobRetryPolicy.defaults
                                    ShardKey = None
                                    Precision = JobPrecision.Minute
                                    CreatedBy = "_platform"
                                    Tags = Map.ofList [ "source", "rag-compose"; "purpose", "tombstone-vacuum" ]
                                }

                                async {
                                    let! result = scheduler.Schedule registration

                                    match result with
                                    | Ok _ ->
                                        ragLogger.Info(
                                            sprintf
                                                "[RAGCompose] Tombstone auto-vacuum scheduled (cron '%s', retention %A)."
                                                cron
                                                app.TombstoneRetention
                                        )
                                    | Error err ->
                                        ragLogger.Warn(
                                            sprintf "[RAGCompose] Failed to schedule tombstone auto-vacuum: %A" err
                                        )
                                }
                                |> Async.StartAsTask
                                :> System.Threading.Tasks.Task
                            | _ ->
                                ragLogger.Warn(
                                    "[RAGCompose] withVacuumSchedule is set but JobScheduler = NoJobScheduler — the tombstone auto-vacuum will not run and soft-deleted chunks will accumulate. Set ServerConfig.JobScheduler = InProcessJobScheduler for steady-state memory."
                                )

                                System.Threading.Tasks.Task.CompletedTask

                        member _.StopAsync(_ct) =
                            System.Threading.Tasks.Task.CompletedTask
                    })

        // Phase 54d — RAG vector-store offboard purge hook, gated on the
        // same `TenantLifecycle = EnabledTenantLifecycle` switch the core
        // `ComposeTenantLifecycle` gates on. Additive `AddSingleton` —
        // the aggregator resolves the full `seq<ITenantLifecycle>`, so this
        // stacks with the core first-party hooks. Self-`Skipped`s when no
        // `IVectorStore` is composed (here it always is), so the gate is
        // the only condition.
        let s =
            match config.TenantLifecycle with
            | EnabledTenantLifecycle ->
                s.AddSingleton<ITenantLifecycle>(fun (sp: System.IServiceProvider) -> RagVectorStoreLifecycle.create sp)
            | NoTenantLifecycle -> s

        // Phase 9m.B — two `/dev/inspect` panels. Both are
        // unconditional: they answer questions ("does this deployment
        // keep its index?", "which of my data types are actually
        // indexed?") whose ANSWER may be fine, so gating them on the
        // matching validator having fired would hide exactly the healthy
        // case an operator wants to confirm. Neither does I/O — the
        // payload is closed over at compose time (GP 13: two records).
        s
            .AddSingleton<IDevDiagnosticsContributor>(
                ToolUp.RAG.RagConfigValidator.RagDurabilityContributor(
                    b.Storage.IsSome,
                    app.VectorStore.IsSome,
                    config.AcceptEphemeralRagIndex
                )
                :> IDevDiagnosticsContributor
            )
            .AddSingleton<IDevDiagnosticsContributor>(
                ToolUp.RAG.RagConfigValidator.VectorisationHandlerContributor(
                    b.DataTypes |> List.map _.Id,
                    b.VectorisationHandlers |> List.map _.DataTypeId
                )
                :> IDevDiagnosticsContributor
            )

    // RAG config validators (mirrors the set the former `composeWithRAG` /
    // `RAGServerApp.run` built). Constructed against the module-merged config
    // so team-mode / replica / persistence checks see the deployment shape.
    let finalConfig = {
        b.Config with
            ModuleNames =
                if b.Config.ModuleNames.IsEmpty then
                    b.ModuleNames
                else
                    b.Config.ModuleNames
            ModuleConfigs = b.Config.ModuleConfigs @ b.ModuleConfigs
    }

    let ragValidators: ConfigValidation.IConfigValidator list = [
        // Phase 4b commit 3 — IDF-leak warning when LocalEmbeddingProvider is
        // active in Team / MultiTeam mode.
        ToolUp.RAG.RagConfigValidator.TeamModeLocalEmbedderValidator(finalConfig, app.EmbeddingProvider)
        // Refuse (persistent) / warn (ephemeral) when RAG has no durable backing.
        ToolUp.RAG.RagConfigValidator.RagPersistenceValidator(finalConfig, b.Storage.IsSome, app.VectorStore.IsSome)
        // Phase 9j follow-up — refuse the in-process ingestion queue under
        // ReplicaCount > 1 (no leasing/redelivery ⇒ silent corpus loss).
        // Phase 509 — the refusal is lifted when a DURABLE queue is
        // composed: the premise it rests on (process-local, no
        // redelivery) no longer holds.
        ToolUp.RAG.RagConfigValidator.RagIngestionInstanceValidator(finalConfig, queue.IsDurable)
        // Wave 2A Gap #1 — warn when default InMemoryEmbeddingCache is active
        // under multi-instance Team mode (cache key has no tenant component).
        // Phase 633 — the warning is lifted when a CROSS-REPLICA cache is
        // composed: the premise it rests on (each replica keeps its own
        // entries) no longer holds.
        ToolUp.RAG.RagConfigValidator.TeamModeSharedEmbeddingCacheValidator(
            finalConfig,
            hasCrossReplicaEmbeddingCache app
        )
        // Wave 2A Gap #9 — surface the clamp log so silent clamping of
        // operator-supplied values is visible at startup.
        ToolUp.RAG.RagConfigValidator.RetrievalDefaultsValidator app.RetrievalDefaultsClampLog
        // 2026-06-12 audit (RAG Gap 8) — warn when the citation-dev-
        // endpoint override is configured as the retired force-on shape.
        ToolUp.RAG.RagConfigValidator.CitationDevEndpointValidator finalConfig
        // Phase 14w — surface the steady-state-memory contract: warn when a
        // vacuum schedule is set without a scheduler, or when a persistent
        // deployment has no vacuum schedule at all (tombstones accumulate).
        ToolUp.RAG.RagConfigValidator.VacuumScheduleValidator(finalConfig, app.VacuumSchedule.IsSome)
        // Phase 14y — warn when an authenticated RAG deployment runs with no
        // rate limiter: retrieval embeds every query, so an unbounded request
        // loop burns embedding spend even behind a proxy (per-query cost, not
        // per-connection). Mode ≠ Anonymous AND RateLimit = RateLimitConfig.none.
        ToolUp.RAG.RagConfigValidator.RAGRateLimitConfiguredValidator finalConfig
        // Phase 9m.B (2026-05-06 audit, Gap 4) — warn when the dev-only,
        // process-stateful LocalEmbeddingProvider serves a non-team
        // production shape. The team half is TeamModeLocalEmbedderValidator
        // above; the two partition the deployment space so one problem is
        // never reported twice.
        ToolUp.RAG.RagConfigValidator.LocalEmbeddingProviderInProductionModeValidator(
            finalConfig,
            app.EmbeddingProvider
        )
        // Phase 9m.B (Gap 6) — warn when data types are registered but NO
        // module contributes a VectorisationHandler: nothing is ever
        // indexed and there is no error anywhere in that path. Skipped
        // when a retrieval-pipeline override owns the corpus (the same
        // condition `suppressIngestion` uses above).
        ToolUp.RAG.RagConfigValidator.RAGHandlersRegisteredValidator(
            b.DataTypes |> List.map _.Id,
            b.VectorisationHandlers |> List.map _.DataTypeId,
            app.RetrievalPipelineOverride.IsSome
        )
        // Phase 9m.B (Gap 7) — explicit bounds on the tuning knobs. The
        // `with*` setters clamp lower bounds only, so nothing today
        // rejects `withTopK 200` or `withIngestionQueueCapacity 10`.
        ToolUp.RAG.RagConfigValidator.RAGConfigBoundsValidator {
            TopK = app.RetrievalDefaults.TopK
            MinScore = app.RetrievalDefaults.MinScore
            MmrLambda = app.MmrLambda
            MmrEnabled = app.EnableMmr
            SnippetCharLimit = app.RetrievalDefaults.SnippetCharLimit
            IngestionConcurrency = app.IngestionConcurrency
            IngestionQueueCapacity = app.IngestionQueueCapacity
        }
    ]

    // Merge RAG handlers + service config + the "RAG" notification-consumer
    // declaration onto whatever `composeAI` already accumulated. `composeAI`
    // declared "AI"; appending "RAG" reproduces the former combined
    // `[ "AI"; "RAG" ]` declaration.
    let baseExt = aiComposed.Extensions

    let mergedExt: ComposeExtensions = {
        baseExt with
            Handlers = baseExt.Handlers @ ragHandlers
            ServiceConfig =
                match baseExt.ServiceConfig with
                | None -> Some ragServiceConfig
                | Some baseFn -> Some(fun s -> ragServiceConfig (baseFn s))
            NotificationConsumers = baseExt.NotificationConsumers @ [ "RAG" ]
    }

    let withValidators =
        ragValidators
        |> List.fold (fun a v -> ServerApp.withConfigValidator v a) aiComposed

    {
        withValidators with
            Extensions = mergedExt
    }

module RAGServerApp =
    /// Construct a `RAGServerApp` with the three required dependencies:
    /// AI provider factory, the canonical platform `IProviderProfile`
    /// BYOK store (Phase 43.A — replaces the removed
    /// `IUserAIConfigStore` shim), and the embedding provider. All
    /// other fields default to the empty / `None` values; chain
    /// `with*` helpers (delegating to AI/Server or RAG-specific) to
    /// configure further.
    let create
        (factory: IAIProviderFactory)
        (providerProfile: IProviderProfile)
        (embedder: IEmbeddingProvider)
        : RAGServerApp =
        {
            AI = AIServerApp.create factory providerProfile
            EmbeddingProvider = embedder
            EmbeddingCache = None
            IngestionObservers = []
            Reranker = None
            EnableMmr = false
            MmrLambda = 0.5
            VectorStore = None
            SparseAnalyzer = None
            RetrievalFraming = defaultRetrievalFraming
            IngestionConcurrency = 8
            IngestionQueueCapacity = 5000
            IngestionRetryPolicy = IngestionRetryPolicy.defaults
            Telemetry = None
            RetrievalDefaults = RetrievalDefaults.defaults
            IndexConversations = false
            GroundingMode = Preferred
            EnableDocumentSummaries = true
            CitationPolicy = ToolUp.RAG.CitationNormaliser.Strict
            RetrievalDefaultsClampLog = []
            MaxChunkBytes = None
            MaxDocumentBytes = None
            MaxQueryChars = Some 16384
            TombstoneRetention = System.TimeSpan.FromDays 7.0
            VacuumSchedule = None
            OverflowPolicy = DropWrite
            RetrievalPipelineOverride = None
            IngestionQueueStore = None
            IngestionRecoveryScopes = []
        }

    /// Phase 1h composition seam — lift an existing `ServerApp` into a
    /// `RAGServerApp` so the additive `withRAG` extension can stack RAG
    /// (and AI) contributions onto whatever the input `ServerApp`
    /// already carries. The input `ServerApp` becomes the AI layer's
    /// `Base` (via `AIServerApp.createFrom`); all RAG-specific fields
    /// initialise to the same defaults as `create`. Required deps
    /// (`factory`, `providerProfile`, `embedder`) are constructor
    /// parameters, matching `create`.
    let createFrom
        (factory: IAIProviderFactory)
        (providerProfile: IProviderProfile)
        (embedder: IEmbeddingProvider)
        (baseApp: ServerApp)
        : RAGServerApp =
        {
            AI = AIServerApp.createFrom factory providerProfile baseApp
            EmbeddingProvider = embedder
            EmbeddingCache = None
            IngestionObservers = []
            Reranker = None
            EnableMmr = false
            MmrLambda = 0.5
            VectorStore = None
            SparseAnalyzer = None
            RetrievalFraming = defaultRetrievalFraming
            IngestionConcurrency = 8
            IngestionQueueCapacity = 5000
            IngestionRetryPolicy = IngestionRetryPolicy.defaults
            Telemetry = None
            RetrievalDefaults = RetrievalDefaults.defaults
            IndexConversations = false
            GroundingMode = Preferred
            EnableDocumentSummaries = true
            CitationPolicy = ToolUp.RAG.CitationNormaliser.Strict
            RetrievalDefaultsClampLog = []
            MaxChunkBytes = None
            MaxDocumentBytes = None
            MaxQueryChars = Some 16384
            TombstoneRetention = System.TimeSpan.FromDays 7.0
            VacuumSchedule = None
            OverflowPolicy = DropWrite
            RetrievalPipelineOverride = None
            IngestionQueueStore = None
            IngestionRecoveryScopes = []
        }

    /// Internal helper: prepend a clamp note if `original ≠ clamped`.
    /// Used by `withTopK` / `withMinScore` / `withSnippetCharLimit` /
    /// `withMmrLambda` / `withRetrievalDefaults` to record operator
    /// intent that the setter had to bend back into range.
    let private noteClamp
        (field: string)
        (original: string)
        (clamped: string)
        (changed: bool)
        (app: RAGServerApp)
        : RAGServerApp =
        if not changed then
            app
        else
            {
                app with
                    RetrievalDefaultsClampLog =
                        sprintf "%s = %s clamped to %s" field original clamped
                        :: app.RetrievalDefaultsClampLog
            }

    /// Replace the embedding provider after construction. Rare — the
    /// constructor parameter is the canonical path.
    let withEmbeddingProvider (provider: IEmbeddingProvider) (app: RAGServerApp) : RAGServerApp = {
        app with
            EmbeddingProvider = provider
    }

    // ─── Delegating helpers (mirror `AIServerApp` and `ServerApp`) ──────

    let withConfig (c: ServerConfig) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withConfig c app.AI
    }

    let withAuth (a: IAuthProvider) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withAuth a app.AI
    }

    let withLogger (l: ILogger) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withLogger l app.AI
    }

    let withStorage (s: IBlobStorage) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withStorage s app.AI
    }

    let withNotifications (n: INotificationChannel) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withNotifications n app.AI
    }

    /// Register an `IUserDirectory` for invite-form typeahead + email-
    /// address resolution. Delegates through `AIServerApp` to
    /// `ServerApp.withUserDirectory`.
    let withUserDirectory (directory: IUserDirectory) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withUserDirectory directory app.AI
    }

    /// Register an additional `ICspContributor` (a webfont / CDN / embed
    /// origin the first-party CSP defaults don't cover). Delegates
    /// through `AIServerApp` to `ServerApp.withCspContributor`.
    let withCspContributor (contributor: ICspContributor) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withCspContributor contributor app.AI
    }

    /// Phase 6f — register an out-of-band transactional notification
    /// sink. Delegates to `AIServerApp.withTransactionalSink` (which
    /// in turn delegates to `ServerApp.withTransactionalSink`). See
    /// `ServerApp.withTransactionalSink` for the full contract.
    let withHealthCheck (check: HealthChecks.IHealthCheck) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withHealthCheck check app.AI
    }

    /// Phase 9m — register a companion-contributed startup config
    /// validator. Delegates to `AIServerApp.withConfigValidator`.
    let withConfigValidator (validator: ConfigValidation.IConfigValidator) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withConfigValidator validator app.AI
    }

    /// Phase 22 — opt into AES-GCM envelope encryption for the
    /// registered IBlobStorage. Delegates to
    /// `AIServerApp.withEncryptedBlobStorage` (which in turn delegates
    /// to `ServerApp.withEncryptedBlobStorage`). See that helper's
    /// docstring for the resolver-choice contract.
    let withEncryptedBlobStorage (resolver: IBlobEncryptionKeyResolver) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withEncryptedBlobStorage resolver app.AI
    }

    /// Phase 9e — register a companion-contributed metrics sink
    /// alongside the in-process Prometheus default. Delegates to
    /// `AIServerApp.withMetricsSink` (which delegates to
    /// `ServerApp.withMetricsSink`).
    let withMetricsSink (sink: Metrics.IMetricsSink) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withMetricsSink sink app.AI
    }

    /// Phase 9v — declare an outbound rate-limit window for one
    /// upstream provider. Delegates to
    /// `AIServerApp.withRateLimitDescriptor` (which delegates to
    /// `ServerApp.withRateLimitDescriptor`).
    let withRateLimitDescriptor (descriptor: RateLimitDescriptor) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withRateLimitDescriptor descriptor app.AI
    }

    /// Phase 19 — register an entity type with the typed entity store.
    /// Delegates to `AIServerApp.withEntity` (which delegates to
    /// `ServerApp.withEntity`).
    let withEntity<'T> (registration: EntityTypes.EntityRegistration<'T>) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withEntity registration app.AI
    }

    /// Phase 9b.B — declare a composition-root-owned background job.
    /// Delegates through `AIServerApp.withJobHandler` to
    /// `ServerApp.withJobHandler` so the underlying `ServerApp.ScheduledJobs`
    /// list is what `composeWithRAG` threads through to the inner
    /// `compose`.
    let withJobHandler
        (handlerName: string, handler: IJobHandler, trigger: Trigger)
        (app: RAGServerApp)
        : RAGServerApp =
        {
            app with
                AI = AIServerApp.withJobHandler (handlerName, handler, trigger) app.AI
        }

    /// Phase 9b.B — declare a composition-root-owned background job
    /// with full control over every `JobRegistration` knob. Delegates
    /// to `AIServerApp.withScheduledJob`.
    let withScheduledJob (declaration: ScheduledJobDeclaration) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withScheduledJob declaration app.AI
    }

    /// Phase 9b.A — opt into back-fill of `OnEvent` jobs on detected
    /// scheduler tick drift. Delegates to `AIServerApp.withBackfillMissedTicks`.
    let withBackfillMissedTicks (enabled: bool) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withBackfillMissedTicks enabled app.AI
    }

    /// Phase 598 — opt into the event-trigger catch-up watermark.
    /// Delegates to `AIServerApp.withEventTriggerCatchUp`.
    let withEventTriggerCatchUp (enabled: bool) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withEventTriggerCatchUp enabled app.AI
    }

    /// Phase 9t — audit-write failure policy. Delegates to
    /// `AIServerApp.withAuditFailurePolicy`.
    let withAuditFailurePolicy (policy: AuditFailurePolicy) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withAuditFailurePolicy policy app.AI
    }

    /// Phase 599 — opt into the entity-write outbox. Delegates to
    /// `AIServerApp.withEntityOutbox`.
    let withEntityOutbox (enabled: bool) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withEntityOutbox enabled app.AI
    }

    let withTransactionalSink (sink: INotificationSink) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withTransactionalSink sink app.AI
    }

    let withExtensions (e: ComposeExtensions) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withExtensions e app.AI
    }

    /// Apply an `AIServerApp -> AIServerApp` transform to the inner AI
    /// layer. Escape hatch for companion compositions that operate on
    /// the `AIServerApp` surface but have no dedicated `RAGServerApp`
    /// delegate — the canonical case is registering a companion's AI
    /// tools (e.g. `ToolUp.KnowledgeBase.Server.AICompose.register`).
    /// Prefer a named `with*` helper when one exists; reach for `mapAI`
    /// only when the transform genuinely lives on a companion, so RAG
    /// never grows a reverse dependency on that companion's package.
    let mapAI (f: AIServerApp -> AIServerApp) (app: RAGServerApp) : RAGServerApp = { app with AI = f app.AI }

    let withPreMiddleware (f: IApplicationBuilder -> IApplicationBuilder) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withPreMiddleware f app.AI
    }

    let withPostMiddleware (f: IApplicationBuilder -> IApplicationBuilder) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withPostMiddleware f app.AI
    }

    let addModule (m: ServerModule) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.addModule m app.AI
    }

    let addModules (modules: ServerModule list) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.addModules modules app.AI
    }

    let withAIConfig (config: AIAssistantServerConfig) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withAIConfig config app.AI
    }

    let withModuleAIContexts (contexts: ModuleAIContext list) (app: RAGServerApp) : RAGServerApp = {
        app with
            AI = AIServerApp.withModuleAIContexts contexts app.AI
    }

    let withIngestionObserver (observer: IIngestionStatusObserver) (app: RAGServerApp) : RAGServerApp = {
        app with
            IngestionObservers = app.IngestionObservers @ [ observer ]
    }

    let withIngestionObservers (observers: IIngestionStatusObserver list) (app: RAGServerApp) : RAGServerApp = {
        app with
            IngestionObservers = app.IngestionObservers @ observers
    }

    /// Register a cross-encoder reranker. Companion packages under
    /// `src/Rerankers/<Name>/` (e.g. `Rerankers/Local/` for the CPU-bound
    /// ONNX reranker) provide implementations. Without one, retrieval
    /// returns the RRF-fused (or cosine-only) candidate list directly.
    let withReranker (reranker: IReranker) (app: RAGServerApp) : RAGServerApp = { app with Reranker = Some reranker }

    /// Enable MMR diversity reranking after retrieval (and rerank, when
    /// present). Off by default. Use `withMmrLambda` to tune the
    /// relevance/diversity balance.
    let withMmr (app: RAGServerApp) : RAGServerApp = { app with EnableMmr = true }

    /// Set the MMR `λ` ∈ [0, 1]. Higher = more relevance-weighted, lower =
    /// more diversity-weighted. 0.5 is the literature default. Implies
    /// `withMmr` if not already enabled.
    let withMmrLambda (lambda: float) (app: RAGServerApp) : RAGServerApp =
        // Contract is λ ∈ [0, 1]; clamp rather than let an
        // out-of-range value silently skew (or NaN) the MMR
        // re-ranking. Matches the clamp-in-setter convention used
        // by withIngestionConcurrency / withTopK.
        let clamped = max 0.0 (min 1.0 lambda)

        {
            app with
                EnableMmr = true
                MmrLambda = clamped
        }
        |> noteClamp "MmrLambda" (string lambda) (string clamped) (clamped <> lambda)

    /// Substitute a non-default `IVectorStore`. Companion packages under
    /// `src/VectorStores/<Name>/` (e.g. `Hnsw/` for HNSW.Net) provide
    /// implementations. Without one, RAG uses the in-memory flat-scan
    /// `InMemoryVectorStore` — fine up to ~50k chunks per scope.
    let withVectorStore (store: IVectorStore) (app: RAGServerApp) : RAGServerApp = { app with VectorStore = Some store }

    /// Phase 633 — substitute the `IEmbeddingCache` the composition wraps
    /// the supplied `IEmbeddingProvider` in, so a model's embedding is paid
    /// for once per FLEET rather than once per process.
    ///
    /// Without this the cache is the process-local `InMemoryEmbeddingCache`:
    /// correct, bounded, and entirely per-replica. `EmbeddingCacheKey`
    /// carries no tenant component and each replica keeps its own entries,
    /// so the same text hits on replica A and misses on replica B —
    /// different latency, different metering attribution, different
    /// short-window telemetry. That is what
    /// `TeamModeSharedEmbeddingCacheValidator` warns about under
    /// `ReplicaCount > 1`, and composing a cross-replica cache **lifts that
    /// warning** rather than merely silencing it.
    ///
    /// The shipped cross-replica backing is the
    /// `ToolUp.EmbeddingCaches.Redis` companion
    /// (`RedisEmbeddingCache.create connectionString logger`); any store
    /// every replica can read and write can implement `IEmbeddingCache`.
    /// Composing `InMemoryEmbeddingCache` explicitly is accepted and
    /// behaves exactly as the default — including the warning, because it
    /// is still process-local.
    let withEmbeddingCache (cache: IEmbeddingCache) (app: RAGServerApp) : RAGServerApp = {
        app with
            EmbeddingCache = Some cache
    }

    /// Phase 501 — compose a language-aware analyzer for the sparse (BM25)
    /// index. Companion packages under `src/SparseIndices/<Name>/` provide
    /// implementations: `ToolUp.SparseIndices.Snowball` for stemming and
    /// stop-word removal in European languages,
    /// `ToolUp.SparseIndices.Cjk` for n-gram segmentation of
    /// non-space-delimited scripts. Without one, the index keeps the shipped
    /// tokenisation (Unicode word runs, lower-cased) and behaves exactly as
    /// before (GP 11).
    ///
    /// The analyzer is applied at index time AND query time — it cannot be
    /// applied to only one side, because the index owns a single analyzer and
    /// its term paths accept nothing but that analyzer's output. Composing one
    /// over a corpus indexed by a different analyzer re-analyses the persisted
    /// snapshot on load (logged once per scope), so retrieval is never served
    /// from a stale vocabulary.
    ///
    /// Ignored when `withRetrievalPipeline` replaces the whole pipeline — that
    /// pipeline owns its own sparse index, if it has one.
    let withSparseAnalyzer (analyzer: ISparseAnalyzer) (app: RAGServerApp) : RAGServerApp = {
        app with
            SparseAnalyzer = Some analyzer
    }

    /// Phase 63.A — substitute the entire retrieval pipeline. `p` is
    /// registered as the `IRetrievalPipeline` verbatim; the default
    /// `RetrievalPipeline` (and, when no `VectorisationHandler` is present,
    /// the ingestion + reembedding background services) are skipped. Slots a
    /// build-time-precomputed static-corpus pipeline — or any custom
    /// `IRetrievalPipeline` — into the standard `RAGServerApp` fluent shape so
    /// static-doc-Q&A consumers and live-KB consumers share one composition
    /// root. Query embedding still uses the registered `IEmbeddingProvider`;
    /// only chunk embeddings are precomputed. `IEmbeddingProvider` remains a
    /// required `create` argument for now (relaxing it is a deferred follow-up).
    let withRetrievalPipeline (pipeline: IRetrievalPipeline) (app: RAGServerApp) : RAGServerApp = {
        app with
            RetrievalPipelineOverride = Some pipeline
    }

    /// Cap the number of documents the `IngestionBackgroundService` processes
    /// in parallel. Each slot does one batched embedding call covering a whole
    /// document, so the effective upstream concurrency equals this number of
    /// HTTP calls (not chunks). Default 8.
    let withIngestionConcurrency (concurrency: int) (app: RAGServerApp) : RAGServerApp = {
        app with
            IngestionConcurrency = max 1 concurrency
    }

    /// Cap the number of pending document jobs in the ingestion queue. When
    /// full, `IngestionQueue.Enqueue` returns `false` and KB upload handlers
    /// surface the rejection by marking the document `Failed`. Default 5,000.
    /// Lower for tight memory budgets, higher for deployments with bursty
    /// bulk uploads.
    let withIngestionQueueCapacity (capacity: int) (app: RAGServerApp) : RAGServerApp = {
        app with
            IngestionQueueCapacity = max 1 capacity
    }

    /// Phase 303 — choose what happens when the ingestion queue is full.
    /// `DropWrite` (default) retries with a short bounded backoff then drops
    /// the document, emitting a `KnowledgeIngestionDropped` audit under
    /// `_platform.knowledge`, a Warning `SystemMessage` to the uploader, and
    /// the queue `Dropped` counters surfaced on `/health/rag`. `Block` never
    /// drops — the post-save hook awaits queue space (the upload response has
    /// already returned, so this delays indexing under sustained load but
    /// never loses a document; suited to bulk backfills). `Refuse` drops like
    /// `DropWrite` but additionally raises `ConfigPreflightFailedException`
    /// once sustained saturation is observed in the rolling 60s window — a
    /// loud, operator-visible signal for compliance-grade deploys that prefer
    /// a screaming error over a quiet drop.
    let withIngestionQueueOverflowPolicy (policy: IngestionOverflowPolicy) (app: RAGServerApp) : RAGServerApp = {
        app with
            OverflowPolicy = policy
    }

    /// Phase 509 — back the ingestion queue with a durable store, so
    /// queued documents survive a process restart and N replicas drain
    /// ONE queue.
    ///
    /// Without this the queue is a process-local `Channels` channel: a
    /// restart mid-ingestion loses every queued document (the per-file
    /// status survives in a non-terminal state, the job does not), and
    /// `RagIngestionInstanceValidator` refuses `ReplicaCount > 1` because
    /// only the replica that handled an upload can drain it. Composing a
    /// store removes both — **and lifts that refusal**, because the
    /// premise it rests on no longer holds.
    ///
    /// The shipped backing is `ToolUp.IngestionQueues.Redis`; any store
    /// with an atomic pop-and-claim can implement `IIngestionQueueStore`.
    /// Delivery is at-least-once (a drainer that dies leaves its lease to
    /// expire, and the job is redelivered), which ingestion already
    /// tolerates — re-indexing a chunk overwrites the same vector-store
    /// id.
    let withDurableIngestionQueue (store: IIngestionQueueStore) (app: RAGServerApp) : RAGServerApp = {
        app with
            IngestionQueueStore = Some store
    }

    /// Phase 509 — sweep these storage containers at startup for
    /// documents left `Pending` by a process that died mid-ingestion,
    /// marking each `Failed` with a restart-interrupted reason.
    ///
    /// This closes the Data-Manager half of the no-recovery gap: the
    /// per-file `IIngestionStatusStore` entry is durable, but on the
    /// in-memory queue the JOB is not — so without a sweep the file's
    /// badge reads `Pending` forever and nothing will ever clear it.
    /// Marking `Failed` makes the loss visible with zero risk; the
    /// operator (or user) re-uploads to re-index. This is deliberately
    /// the same shape, and the same trade-off, as KB's
    /// `recoverStuckDocumentsAtStartup` — auto re-enqueue would need the
    /// per-handler extraction wiring lifted to a shared shape.
    ///
    /// Containers are enumerated by the consumer (typically
    /// `ITeamStore.ListAll` plus the well-known `_platform` /
    /// `_deployment` containers) because the SDK has no scope-enumeration
    /// seam. Empty (default) ⇒ no sweep and no hosted service (GP 11 /
    /// GP 13).
    let withIngestionRecoverySweep (containers: string list) (app: RAGServerApp) : RAGServerApp = {
        app with
            IngestionRecoveryScopes = containers
    }

    /// Phase 14t — set the retry / dead-letter policy for transient
    /// embedder failures during ingestion. Transient per-chunk failures
    /// (429 / 5xx / timeout / network) retry with exponential backoff +
    /// jitter and dead-letter after `MaxAttempts`; permanent failures
    /// (401/403 / other 4xx) dead-letter immediately with an Owner/Admin
    /// alert. Default `IngestionRetryPolicy.defaults` (5 attempts,
    /// 30s→30min backoff, +20% jitter). `MaxAttempts` is clamped to ≥ 1.
    let withIngestionRetryPolicy (policy: IngestionRetryPolicy) (app: RAGServerApp) : RAGServerApp = {
        app with
            IngestionRetryPolicy = {
                policy with
                    MaxAttempts = max 1 policy.MaxAttempts
            }
    }

    /// Substitute the rolling-window in-memory telemetry default with a
    /// concrete `IRagTelemetry` (e.g. a Prometheus / OTel exporter). The
    /// `/health/rag` endpoint resolves whatever is registered, so the
    /// override transparently feeds both the snapshot endpoint and any
    /// downstream metrics backend.
    let withTelemetry (telemetry: IRagTelemetry) (app: RAGServerApp) : RAGServerApp = {
        app with
            Telemetry = Some telemetry
    }

    /// Replace the entire `RetrievalDefaults` record. Use targeted setters
    /// (`withTopK`, `withMinScore`, `withMergeStrategy`,
    /// `withSnippetCharLimit`) when only one knob needs to change.
    let withRetrievalDefaults (defaults: RetrievalDefaults) (app: RAGServerApp) : RAGServerApp =
        // Clamp the whole-record override to the same bounds the
        // targeted setters enforce — otherwise a fat-fingered
        // TopK=0 / MinScore=1.0 here silently disables retrieval.
        let clamped = RetrievalDefaults.clamp defaults

        { app with RetrievalDefaults = clamped }
        |> noteClamp
            "RetrievalDefaults"
            (sprintf
                "{ TopK=%d; MinScore=%A; SnippetCharLimit=%d }"
                defaults.TopK
                defaults.MinScore
                defaults.SnippetCharLimit)
            (sprintf
                "{ TopK=%d; MinScore=%A; SnippetCharLimit=%d }"
                clamped.TopK
                clamped.MinScore
                clamped.SnippetCharLimit)
            (clamped <> defaults)

    /// Cap the number of matches included in each retrieval block. Higher
    /// gives the model more grounding context; too high crowds the prompt
    /// and dilutes per-source attention. Default 5.
    let withTopK (topK: int) (app: RAGServerApp) : RAGServerApp =
        let clamped = max 1 topK

        {
            app with
                RetrievalDefaults = {
                    app.RetrievalDefaults with
                        TopK = clamped
                }
        }
        |> noteClamp "TopK" (string topK) (string clamped) (clamped <> topK)

    /// Drop matches scoring at or below `threshold`. `None` disables the
    /// gate (default — every match returned by the pipeline is surfaced).
    /// Use a small positive value (e.g. `0.4`) to refuse weak matches in
    /// regulated-brand or low-tolerance deployments.
    let withMinScore (threshold: float option) (app: RAGServerApp) : RAGServerApp =
        // Cosine-similarity gate. Values ≥ 1.0 filter out
        // EVERY match (the assistant goes silent with no
        // diagnostic); negatives are a no-op gate. Clamp to a
        // sane [0.0, 0.99] so a fat-fingered threshold can't
        // silently disable retrieval entirely.
        let clamped = threshold |> Option.map (fun t -> max 0.0 (min 0.99 t))

        {
            app with
                RetrievalDefaults = {
                    app.RetrievalDefaults with
                        MinScore = clamped
                }
        }
        |> noteClamp "MinScore" (sprintf "%A" threshold) (sprintf "%A" clamped) (clamped <> threshold)

    /// Choose how multi-scope results combine. `Interleaved` re-ranks by
    /// score regardless of scope (default); `Separate` keeps per-scope
    /// grouping for callers presenting platform vs team knowledge
    /// separately.
    let withMergeStrategy (strategy: MergeStrategy) (app: RAGServerApp) : RAGServerApp = {
        app with
            RetrievalDefaults = {
                app.RetrievalDefaults with
                    Merge = strategy
            }
    }

    /// Set the maximum character length of each `RetrievedSource.Snippet`
    /// preview surfaced in the AI client's Sources panel. Trimmed with an
    /// ellipsis when content exceeds the budget. Default 240.
    let withSnippetCharLimit (limit: int) (app: RAGServerApp) : RAGServerApp =
        let clamped = max 16 limit

        {
            app with
                RetrievalDefaults = {
                    app.RetrievalDefaults with
                        SnippetCharLimit = clamped
                }
        }
        |> noteClamp "SnippetCharLimit" (string limit) (string clamped) (clamped <> limit)

    /// Replace the per-deployment origin allow-list. Default excludes
    /// `AIContext` (already injected verbatim every turn elsewhere). Pass
    /// `None` to clear the gate entirely; pass `Some` with a custom set
    /// to restrict retrieval to specific origins (e.g.
    /// `Set.ofList [ Document; Note ]` excludes narratives + AI context +
    /// conversations).
    let withOriginFilter (filter: Set<ChunkOrigin> option) (app: RAGServerApp) : RAGServerApp = {
        app with
            RetrievalDefaults = {
                app.RetrievalDefaults with
                    OriginFilter = filter
            }
    }

    /// Phase 502.D — set the deployment-level metadata-equality filter every
    /// prompt-path retrieval is scoped by. AND-combined, strict equality, and
    /// a chunk MISSING the key does not pass — the same semantics
    /// `RetrievalRequest.Filters` carries on both shipped pipelines.
    ///
    /// This is the operator's bound, not a default a caller can override: a
    /// per-request filter (`AIMessageRequest.RetrievalFilters`) merges on top
    /// and wins nothing on a key set here. A deployment scoped to
    /// `Map.ofList [ "tag", "policy" ]` therefore never surfaces a
    /// non-policy chunk to the model, whatever any client asks for.
    ///
    /// `None` (the default) leaves retrieval unfiltered and byte-identical
    /// to a filter-unaware deployment (GP 11).
    let withRetrievalFilters (filters: Map<string, string> option) (app: RAGServerApp) : RAGServerApp = {
        app with
            RetrievalDefaults = {
                app.RetrievalDefaults with
                    Filters = filters
            }
    }

    /// Enable conversation-history indexing (WS3.4 — scaffolded only,
    /// implementation lands in a follow-up phase). When `true`, completed
    /// AI conversations will be vectorised under
    /// `ChunkOrigin.Conversation`. Off by default; flipping the flag
    /// today is a no-op until the ingestion handler is wired.
    let withIndexConversations (enabled: bool) (app: RAGServerApp) : RAGServerApp = {
        app with
            IndexConversations = enabled
    }

    /// Set the grounding stance — `Permissive` (model uses KB + training
    /// data freely), `Preferred` (default — KB authoritative on conflict),
    /// or `StrictlyGrounded` (refuse to answer without a retrieval hit).
    /// `StrictlyGrounded` works best paired with `withMinScore (Some x)`
    /// so the model only refuses on genuinely-thin retrievals.
    let withGroundingMode (mode: GroundingMode) (app: RAGServerApp) : RAGServerApp = { app with GroundingMode = mode }

    /// Toggle document-level summary chunks. Default `true` — when a
    /// `VectorisationHandler` defines `Summarise`, the SDK calls it after
    /// `Vectorise` and indexes the resulting chunk under
    /// `_isSummary = "true"`. Pass `false` to skip the summary call
    /// entirely (cost control for AI-summary deployments where the extra
    /// per-document HTTP isn't worth the retrieval-quality gain).
    let withDocumentSummaries (enabled: bool) (app: RAGServerApp) : RAGServerApp = {
        app with
            EnableDocumentSummaries = enabled
    }

    /// Override the framing preamble injected into the system prompt ahead
    /// of retrieved chunks. The default (`defaultRetrievalFraming`) is
    /// authoritative-tone — KB content beats training data on conflict —
    /// and is what most deployments want. Pass `""` to opt out entirely
    /// (composes nothing) or any non-empty string to substitute custom
    /// framing.
    let withRetrievalFraming (framing: string) (app: RAGServerApp) : RAGServerApp = {
        app with
            RetrievalFraming = framing
    }

    /// Phase 6q — set the post-stream citation-normaliser policy.
    /// Default is `Strict` — drift variants (`(1)`, `[1]`, `Source 1`,
    /// bare `¹`, `^1`) are normalised onto `[¹]` when they bind to a
    /// real retrieved source, and phantoms (digit > retrieved count)
    /// are replaced with the `[unverified]` inline tag.
    /// `LenientNormalise` normalises valid digits but strips phantoms
    /// silently (no tag). `Off` disables the post-stream pass
    /// entirely — the model's emitted text reaches the conversation
    /// store byte-for-byte (pre-Phase-6q behaviour).
    let withCitationPolicy
        (policy: ToolUp.RAG.CitationNormaliser.RagCitationPolicy)
        (app: RAGServerApp)
        : RAGServerApp =
        { app with CitationPolicy = policy }

    /// Cap the UTF-8 byte size of any single chunk's `TextChunk.Content`
    /// reaching the ingestion queue. Default `None` = no limit (historical
    /// behaviour). A typical value is `Some (10 * 1024 * 1024)` (10 MB) —
    /// large enough that ordinary chunked PDFs / spreadsheets pass through
    /// unchanged, but small enough that a pathological spreadsheet cell
    /// can't pin memory across thousands of queued jobs.
    let withMaxChunkBytes (maxBytes: int) (app: RAGServerApp) : RAGServerApp = {
        app with
            MaxChunkBytes = Some(max 1 maxBytes)
    }

    /// Cap the sum of UTF-8 byte sizes across every chunk in a single
    /// `DocumentIngestionJob`. Default `None` = no limit. Typical value
    /// `Some (100 * 1024 * 1024)` (100 MB).
    let withMaxDocumentBytes (maxBytes: int) (app: RAGServerApp) : RAGServerApp = {
        app with
            MaxDocumentBytes = Some(max 1 maxBytes)
    }

    /// Phase 14y — cap the character length of a retrieval query reaching
    /// the pipeline. Above the cap, `IRetrievalPipeline.Retrieve` refuses
    /// with a `KnowledgeQueryTooLargeException` and emits a
    /// `KnowledgeQueryRejected` audit. Default `Some 16384` (~4k tokens) —
    /// generous for any genuine natural-language question, tight enough that
    /// an accidental whole-document paste is refused before it burns embedding
    /// spend. Clamped to a floor of 1.
    let withMaxQueryChars (maxChars: int) (app: RAGServerApp) : RAGServerApp = {
        app with
            MaxQueryChars = Some(max 1 maxChars)
    }

    /// Phase 14w — set the tombstone retention window. Soft-deleted chunks
    /// (`_deletedAt` stamped by `IVectorStore.DeleteChunk`) become
    /// vacuum-eligible once older than `retention`. Default 7 days.
    /// Clamped to a floor of one minute so a fat-fingered `TimeSpan.Zero`
    /// can't turn every soft-delete into an immediate hard-delete on the
    /// next sweep (which would defeat `RestoreChunk`).
    let withTombstoneRetention (retention: System.TimeSpan) (app: RAGServerApp) : RAGServerApp =
        let clamped =
            if retention < System.TimeSpan.FromMinutes 1.0 then
                System.TimeSpan.FromMinutes 1.0
            else
                retention

        {
            app with
                TombstoneRetention = clamped
        }

    /// Phase 14w — enable the tombstone auto-vacuum on the default daily
    /// 03:00 UTC schedule. Registers a `RAGVacuumJobHandler` on the
    /// `IJobScheduler` that sweeps every scope, hard-removing tombstones
    /// older than `TombstoneRetention`. REQUIRES `ServerConfig.JobScheduler
    /// = InProcessJobScheduler` (or a distributed scheduler companion) —
    /// without a scheduler the sweep never fires and the
    /// `VacuumScheduleValidator` warns at startup. Use
    /// `withVacuumScheduleCron` for a non-default cadence.
    let withVacuumSchedule (app: RAGServerApp) : RAGServerApp = {
        app with
            VacuumSchedule = Some RAGVacuumJobHandler.DefaultVacuumCron
    }

    /// Phase 14w — enable the tombstone auto-vacuum on a custom cron
    /// (`Minute Hour DayOfMonth Month DayOfWeek`; the in-process scheduler
    /// supports `*`, integers, comma lists, and `*/N`). An invalid
    /// expression is rejected at schedule time with
    /// `ScheduleError.InvalidCron` and logged at `Warn` — the deployment
    /// still boots.
    let withVacuumScheduleCron (cron: string) (app: RAGServerApp) : RAGServerApp = {
        app with
            VacuumSchedule = Some cron
    }

    /// Drive the final composition. Returns the process exit code. All
    /// required dependencies (`AIProviderFactory`, `ProviderProfile`,
    /// `EmbeddingProvider`) are guaranteed at compile time by
    /// `RAGServerApp.create`. Phase 1h — implementation is now
    /// `composeRAG >> ServerApp.run`; the RAG config validators and the
    /// AI/RAG DI + handler contributions all live in `composeRAG`, which
    /// composes over `composeAI`. Consumers needing to stack RAG with
    /// Forms / AI companions on one composition root use the additive
    /// `withRAG` extension instead.
    let run (app: RAGServerApp) : int = composeRAG app |> ServerApp.run

// ─── Additive companion-set extension `withRAG` (Phase 1h) ──────────
//
// Stack RAG contributions (and the AI assistant they compose over) onto
// an existing `ServerApp` pipeline alongside Forms / future companions,
// without forcing the deployment to commit to `RAGServerApp.run` as the
// terminal call.

/// Phase 1h — stack the RAG pipeline (and the AI assistant it composes
/// over) onto an existing `ServerApp` pipeline. Consumes the required
/// RAG dependency (`embedder`) plus the required AI dependencies
/// (`factory` + `providerProfile`) and a `configure` function that
/// builds RAG-specific state (retrieval knobs, grounding mode,
/// ingestion observers, citation policy, AI assistant config) on a fresh
/// `RAGServerApp` whose AI layer's `Base` is the input `ServerApp`.
///
/// The configurator should call only RAG/AI-specific helpers
/// (`RAGServerApp.withGroundingMode` / `withTopK` / `withAIConfig` / …);
/// the delegating helpers (`withConfig` / `withAuth` / …) exist on
/// `RAGServerApp` for backcompat but calling them inside the
/// configurator overwrites the base `ServerApp`'s existing
/// configuration. Set base configuration on the outer pipeline before
/// calling `withRAG`.
///
/// Calling `withRAG` twice on the same pipeline re-composes AI + RAG
/// (re-appends `AILatencyMetrics.registrations`, re-registers the DI
/// services, re-mounts the routes); the existing duplicate-detection
/// paths surface the misuse — most loudly at metric-sink construction.
///
/// Example — Forms + RAG in one composition root:
///
///     ServerApp.empty
///     |> ServerApp.withConfig config
///     |> ServerApp.withStorage storage
///     |> FormsCompose.withForms (fun f ->
///         f |> FormsServerApp.withFormSchema mySchema)
///     |> RAGCompose.withRAG factory providerProfile embedder (fun rag ->
///         rag
///         |> RAGServerApp.withAIConfig assistant
///         |> RAGServerApp.withGroundingMode StrictlyGrounded)
///     |> ServerApp.run
let withRAG
    (factory: IAIProviderFactory)
    (providerProfile: IProviderProfile)
    (embedder: IEmbeddingProvider)
    (configure: RAGServerApp -> RAGServerApp)
    (app: ServerApp)
    : ServerApp =
    RAGServerApp.createFrom factory providerProfile embedder app
    |> configure
    |> composeRAG
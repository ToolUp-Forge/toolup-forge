module ToolUp.RAG.RAGCompose

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Providers
open ToolUp.Platform.Compose.RemotingHelpers
open ToolUp.Platform.Infra.TransientFault
open ToolUp.Platform.Compose.Server
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra.BlobEncryption
open ToolUp.Platform.Usage
open ToolUp.Platform.Infra.IEmbeddingProvider
open ToolUp.Platform.Infra.IEmbeddingCache
open ToolUp.Platform.Infra.IVectorStore
open ToolUp.Platform.Infra.ISparseIndex
open ToolUp.Platform.Infra.IReranker
open ToolUp.Platform.Infra.IQueryRewriter
open ToolUp.Platform.Infra.IOcrProvider
open ToolUp.Platform.Infra.ITableExtractor
open ToolUp.Platform.Infra.IRetrievalPipeline
open ToolUp.Platform.Infra.IRetrievalTracer
open ToolUp.Platform.Infra.IRagTelemetry
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.VectorisationTypes
open ToolUp.Platform.Files.FileManagement
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
open ToolUp.Platform.Infra
open ToolUp.Platform.Infra.Usage
open ToolUp.Platform.Config
open ToolUp.Platform.Jobs
open ToolUp.Platform.Audit
open ToolUp.Platform.Compose

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

/// Companion sentence appended to the retrieval framing when
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

/// Resolve the framing text, then append the live-interface
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

/// The deployment's live-interface posture, derived from the
/// same tool list and the same predicate the framing uses
/// (`RAGPromptBuilder.ToolFraming.fromTools`, Phase 538: a tool declaring
/// `IsLiveInterface`, or a `ClientResident` one — never a name match), so
/// the report cannot disagree with what the prompt actually does. The
/// framing-eligible stance is `Preferred`, the one `resolveFramingWithTools`
/// appends the companion under. Surfaced on `/dev/inspect` and on the
/// deployment-readiness report; informational only — it gates nothing.
let liveInterfaceSummary
    (tools: AIToolDefinition list)
    (mode: GroundingMode)
    : DeploymentReadiness.LiveInterfaceSummary =
    let liveInterfaceTools =
        tools
        |> List.filter (fun def -> (RAGPromptBuilder.ToolFraming.fromTools [ def ]).HasLiveUiTools)
        |> List.map _.Name

    let framingEligible =
        match mode with
        | Preferred -> true
        | Permissive
        | StrictlyGrounded -> false

    DeploymentReadiness.LiveInterfaceSummary.derive liveInterfaceTools (string mode) framingEligible

/// The `/dev/inspect` panel for the live-interface posture.
/// Closed over at compose time (no I/O, GP 13), unconditional for the same
/// reason as the RAG durability panel: "UI-awareness: off" is often the
/// fine answer, and hiding it would hide exactly what an operator wants to
/// confirm.
type LiveInterfaceContributor(summary: DeploymentReadiness.LiveInterfaceSummary) =
    interface IDevDiagnosticsContributor with
        member _.Contribute() = async {
            let payload = {|
                uiAwareness = (if summary.HasLiveInterfaceTools then "on" else "off")
                hasLiveInterfaceTools = summary.HasLiveInterfaceTools
                liveInterfaceTools = summary.LiveInterfaceTools
                groundingMode = summary.GroundingMode
                toolAwareFramingActive = summary.ToolAwareFramingActive
                note = summary.Note |> Option.toObj
                summary = DeploymentReadiness.LiveInterfaceSummary.render summary
            |}

            return "Live interface", box payload
        }

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

/// Sustained-saturation threshold for
/// `IngestionOverflowPolicy.Refuse`. Once this many documents have been
/// dropped inside the queue's rolling 60s window, `Refuse` raises
/// `ConfigPreflightFailedException` from the drop path. A handful of drops
/// in a minute already means the queue stayed full through the ~1.85s
/// bounded retry repeatedly — genuine sustained pressure, not a one-off
/// spike.
[<Literal>]
let private refuseSaturationThreshold = 5

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
    (auditLog: IAuditLog option)
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

                do! ch.Publish(uid.Value, Notification.SystemMessage(SystemMessageLevel.Warning, text))
            with ex ->
                logger.Warn(sprintf "[RAGCompose] ingestion-drop notification failed for %s: %s" docId ex.Message)
        | _ -> ()
    }

let internal makeVectorisationHook
    (handlers: VectorisationHandler list)
    (queue: IngestionQueue)
    (telemetry: IRagTelemetry)
    (enableSummaries: bool)
    (maxChunkBytes: int option)
    (maxDocumentBytes: int option)
    (eventStore: IEventStore)
    (ingestionStatusStore: IIngestionStatusStore)
    (overflowPolicy: IngestionOverflowPolicy)
    (auditLog: IAuditLog)
    (notifications: INotificationChannel)
    (logger: ILogger)
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
            do! ingestionStatusStore.Set(scopeId, documentId, status)
        with ex ->
            logger.Warn(sprintf "[RAGCompose] ingestion-status write failed for %s: %s" documentId ex.Message)
    }

    let setPending (scopeId: string) (documentId: string) (totalChunks: int) = async {
        try
            do! ingestionStatusStore.SetPending(scopeId, documentId, totalChunks)
        with ex ->
            logger.Warn(sprintf "[RAGCompose] ingestion-status pending write failed for %s: %s" documentId ex.Message)
    }

    let writeEvent (scopeId: string) (eventName: string) (payload: obj) = async {
        try
            let json = System.Text.Json.JsonSerializer.Serialize(payload, jsonOptions)
            let evt = Events.create scopeId "ToolUp.RAG" eventName json
            do! eventStore.Write evt
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
                                Some(UserId createdBy)
                        // The Data Manager status store tracks a file, not
                        // an attempt.
                        Attempt = None
                    }

                    // Bounded retry: a full queue is often just the drainer
                    // running behind, so a short backoff usually clears space
                    // rather than dropping the document. Fire-and-forget path,
                    // so the sleeps don't delay the upload response.
                    // `EnqueueAsync`, not `Enqueue`: on a durable
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

                    let scopeKey = VectorScope.key vectorScope

                    let chunkCount = List.length chunkPairs

                    match overflowPolicy with
                    | Block ->
                        // Never drop. Await queue space instead of
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
                            // last chunk lands.
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
                                $"[RAGCompose] Ingestion queue full ({queue.Count}/{queue.Capacity}) after {attempts} attempts — DROPPED vectorisation for {entry.FileName}. The file is saved but is permanently unsearchable until re-uploaded. Raise IngestionQueueCapacity (RagOptions.withIngestionQueueCapacity) or IngestionConcurrency if drops recur.",
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

                            // Deployment-wide `KnowledgeIngestionDropped`
                            // audit + Warning `SystemMessage` to the uploader +
                            // queue `Dropped` counters (rolling 60s + cumulative,
                            // surfaced on /health/rag).
                            do!
                                emitIngestionDrop
                                    (Some auditLog)
                                    (Some notifications)
                                    queue
                                    logger
                                    scopeKey
                                    entry.FileName
                                    chunkCount
                                    reason
                                    job.OriginatingUserId

                            do! setStatus scope.Container entry.FileName (FileIngestionStatus.Failed reason)

                            // Compliance-grade fail-loud. Under
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
        // No bounded multi-part commit primitive here; callers assemble through memory.
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "the RAG null blob store"

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


// ─── The RAG companion: `RagOptions` + `withRAG` ─────────────────────
//
// RAG is a companion composed onto a `ServerApp`, not a record that wraps
// one. `RagOptions` holds what only the RAG tier knows (embedding
// provider, retrieval knobs, ingestion settings, …) and the options of the
// AI tier RAG composes over (`AI: AIOptions`). `withRAG options` registers
// the tier with `ServerApp.withCompanion`, so it is applied once, when the
// deployment resolves, to the deployment as every builder left it. The
// vectorisation handlers flow from each module's `ServerModule` via that
// deployment.
//
// Composition root pattern:
//
//     ServerApp.empty
//     |> ServerApp.withConfig config
//     |> ServerApp.withStorage blobStorage
//     |> ServerApp.addModules modules
//     |> RAGCompose.withRAG (RagOptions.create factory providerProfile embedder |> RagOptions.withTopK 8)
//     |> ServerApp.run

/// Which keyword (sparse) index a composition runs hybrid
/// retrieval over. The default, `InProcessSparseIndex`, constructs exactly
/// the index every composition constructed before this type existed.
[<RequireQualifiedAccess>]
type SparseIndexComposition =
    /// The in-process `InMemoryBM25Index`, blob-persisted, with the composed
    /// `SparseAnalyzer` (the identity analyzer when none). Per-process: on
    /// more than one replica each keeps its own postings.
    | InProcessSparseIndex
    /// A supplied `ISparseIndex`, registered verbatim (`withSparseIndex`).
    /// It tokenises however it was built to, so composing it together with
    /// `withSparseAnalyzer` is refused at composition — use
    /// `AnalyzedSparseIndex` to hand the analyzer to the index.
    | SuppliedSparseIndex of index: ISparseIndex
    /// An index built at composition for the composed analyzer (the identity
    /// analyzer when none) — `withAnalyzedSparseIndex`. The seam an index
    /// that tokenises in its own engine uses to map, or refuse, the analyzer.
    | AnalyzedSparseIndex of build: (ISparseAnalyzer -> ISparseIndex)
    /// No keyword index: dense retrieval only (`withoutSparseIndex`). Fusion
    /// over one list is that list, so the pipeline takes its dense-only path.
    | NoSparseIndex

/// What the RAG companion composes: the required RAG input
/// (`EmbeddingProvider`), the options of the AI tier it composes over
/// (`AI`, whose required inputs are the other two parameters of
/// `RagOptions.create`), and the RAG tier's own settings. Vectorisation
/// handlers come from each module's `ServerModule.VectorisationHandlers`
/// via the deployment.
type RagOptions = {
    /// The AI tier RAG composes over. RAG folds its retrieval layers into
    /// the assistant config and composes the tier itself, so a deployment
    /// composing RAG does not also compose `AICompose.withAI`. Set with
    /// `RagOptions.withAIOptions` (or `withAIConfig` /
    /// `withModuleAIContexts`).
    AI: AIOptions
    EmbeddingProvider: IEmbeddingProvider
    /// Observers fired by `IngestionBackgroundService` after each chunk
    /// completes (success or failure). Modules that need to surface live
    /// ingestion status — `KnowledgeBase` is the canonical example —
    /// register an observer here.
    IngestionObservers: IIngestionStatusObserver list
    /// Optional cross-encoder reranker. When `Some`, the retrieval pipeline
    /// inflates its candidate pool and reorders it by joint (query, document)
    /// relevance before the boosts, MMR and topK truncation. Every returned
    /// match is reranked: a pool larger than the reranker's `MaxBatchSize`
    /// is capped at it, and the trace says so. Companion
    /// packages under `src/Rerankers/<Name>/` provide the implementation.
    Reranker: IReranker option
    /// Enable MMR diversity reranking after retrieval (and rerank and the
    /// boosts, when present). MMR is the last step that orders the result:
    /// the `Interleaved` merge does not re-sort after it. Off by
    /// default — MMR helps duplicate-heavy corpora and hurts
    /// fact-extraction queries; opt in per deployment.
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
    /// Optional language-aware analyzer for the sparse (BM25)
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
    /// Retry / dead-letter policy for transient embedder
    /// failures during ingestion (429 rate limits, 5xx, timeouts,
    /// network faults). A transient per-chunk `Index` failure is retried
    /// with exponential backoff + jitter (via `IJobScheduler` when one is
    /// composed, so the pending retry survives process restart; an
    /// in-process loop otherwise) and dead-lettered after `MaxAttempts`;
    /// a permanent failure (401/403 bad credentials, other 4xx) is
    /// dead-lettered immediately with an Owner/Admin alert. Defaults to
    /// `IngestionRetryPolicy.defaults` (5 attempts, 30s→30min backoff,
    /// +20% jitter). Tune via `RagOptions.withIngestionRetryPolicy`.
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
    /// Whether the AI request pipeline compiles each user turn
    /// into a `RetrievalRequest.FactClause` before retrieval, and how long
    /// it may spend doing so. Defaults to on with a 3-second budget; inert
    /// unless the deployment composed the fact tier, which registers the
    /// `IFactClausePlanner` this reads (GP 13). Turn it off with
    /// `withFactClausePlanning false` when the fact tier is composed for
    /// its tool surface alone and a second model call per chat turn is not
    /// wanted. Tune via `withFactClausePlanning` / `withFactClausePlanOptions`.
    FactClausePlan: FactClausePlanOptions
    /// Master switch for document-level summary chunks (WS4.1). When
    /// `true` (default), `VectorisationHandler.Summarise` is called for
    /// every document and the resulting chunk is indexed under
    /// `_isSummary = "true"`. When `false`, the summary call is skipped
    /// even if a handler defines one — useful for cost-sensitive
    /// deployments. Has no effect if no handler defines `Summarise`.
    EnableDocumentSummaries: bool
    /// Post-stream citation handling. `Strict` (default)
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
    /// `RagOptions.withMaxChunkBytes`.
    MaxChunkBytes: int option
    /// Optional hard cap on the sum of UTF-8 byte sizes across every chunk
    /// in a single `DocumentIngestionJob`. Same rationale as `MaxChunkBytes`
    /// but bounds the per-document footprint rather than per-chunk. `None`
    /// (default) preserves no-limit behaviour. Tune via
    /// `RagOptions.withMaxDocumentBytes`.
    MaxDocumentBytes: int option
    /// Hard cap on the character length of a retrieval query
    /// reaching `IRetrievalPipeline.Retrieve`. Above the cap the pipeline
    /// refuses with a `KnowledgeQueryTooLargeException` and emits a
    /// `KnowledgeQueryRejected` audit — a query this long is almost always a
    /// programming bug (an entire document pasted into the query slot), and
    /// embedding it wastes provider spend / can trip the provider's own token
    /// cap with an opaque error. Defaults to `Some 16384` (~4k tokens); tune
    /// via `RagOptions.withMaxQueryChars`. `None` disables the guard (not
    /// reachable through the public builder — the default is always on).
    MaxQueryChars: int option
    /// Tombstone retention window. `IVectorStore.DeleteChunk`
    /// soft-deletes (stamps `_deletedAt`); the scheduled vacuum
    /// hard-removes tombstones older than `now - TombstoneRetention`.
    /// Default 7 days — long enough for an accidental delete to be
    /// restored, short enough that soft-deleted content doesn't pin
    /// memory indefinitely. Tune via `RagOptions.withTombstoneRetention`.
    TombstoneRetention: System.TimeSpan
    /// Cron expression for the tombstone auto-vacuum sweep.
    /// `None` (default) = no scheduled vacuum: soft-deleted chunks are only
    /// reclaimed when an operator calls `IVectorStore.Vacuum` manually, so
    /// a long-running replica's memory grows without bound. `Some cron`
    /// (typically via `withVacuumSchedule` = daily 03:00 UTC) registers a
    /// `RAGVacuumJobHandler` on the `IJobScheduler` that sweeps every scope
    /// on the schedule. REQUIRES `ServerConfig.JobScheduler =
    /// InProcessJobScheduler` (or a distributed scheduler companion) — the
    /// `VacuumScheduleValidator` warns when the pair is misconfigured.
    VacuumSchedule: string option
    /// Behaviour when the ingestion queue is full. `DropWrite`
    /// (default) retries with a short bounded backoff then drops the
    /// document, emitting a `KnowledgeIngestionDropped` audit + a Warning
    /// `SystemMessage` to the uploader + the queue `Dropped` counters.
    /// `Block` never drops — the post-save hook awaits queue space (delays
    /// indexing under load, never loses a document). `Refuse` drops like
    /// `DropWrite` but additionally raises `ConfigPreflightFailedException`
    /// on sustained saturation for compliance-grade fail-loud. Tune via
    /// `RagOptions.withIngestionQueueOverflowPolicy`.
    OverflowPolicy: IngestionOverflowPolicy
    /// Substitute the entire `IRetrievalPipeline`. `None`
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
    /// time. Set via `RagOptions.withRetrievalPipeline`.
    RetrievalPipelineOverride: IRetrievalPipeline option
    /// Durable backing for the ingestion queue. `None`
    /// (default) keeps the process-local `System.Threading.Channels`
    /// queue: correct for one replica and one process lifetime, and the
    /// reason `RagIngestionInstanceValidator` refuses `ReplicaCount > 1`.
    /// `Some store` moves the queue into that store, so documents survive
    /// a restart (an unacknowledged lease is redelivered) and N replicas
    /// drain one queue — which lifts the multi-replica refusal. The
    /// shipped durable backing is the `ToolUp.IngestionQueues.Redis`
    /// companion. Set via `RagOptions.withDurableIngestionQueue`.
    IngestionQueueStore: IIngestionQueueStore option
    /// Storage containers swept at startup for documents
    /// left `Pending` by a process that died mid-ingestion. Empty
    /// (default) ⇒ no sweep, so an existing deployment is unchanged
    /// (GP 11). The sweep matters most on the IN-MEMORY queue, where a
    /// restart genuinely loses the job and the per-file status would
    /// otherwise read `Pending` forever with nothing ever to clear it;
    /// on a durable queue the job is redelivered instead, so the sweep is
    /// belt-and-braces. Set via `RagOptions.withIngestionRecoverySweep`.
    ///
    /// No longer the ONLY way to name the containers. A
    /// deployment that composes `withScopeEnumerator` has them
    /// enumerated instead, and the two compose (the union is swept), so
    /// migrating from a hand-written list to an enumerator never loses
    /// coverage in the gap.
    IngestionRecoveryScopes: string list
    /// The composed scope-enumeration seam, answering "what
    /// storage scopes does this deployment hold?" for the restart
    /// recovery sweep. `None` (default) ⇒ scopes come only from
    /// `IngestionRecoveryScopes`, exactly as before (GP 11 / GP 13). Set
    /// via `RagOptions.withScopeEnumerator`; the SDK default is
    /// `ScopeEnumeration.fromTeamStore`.
    ScopeEnumerator: IScopeEnumerator option
    /// Substitute the `IEmbeddingCache` the composition wraps
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
    /// on no longer holds. Set via `RagOptions.withEmbeddingCache`.
    EmbeddingCache: IEmbeddingCache option
    /// The per-turn retrieval budgets: the query-time embed
    /// policy (attempts, wall-clock budget, concurrency ceiling) and the
    /// fact-stage budget. Default `RetrievalBudgets.defaults` (one attempt,
    /// 5 s, no ceiling; 3 s for facts): a healthy provider's results are
    /// unchanged, a stalled one costs the turn its dense branch or its
    /// pushed facts — marked on the trace — never the turn. Set via
    /// `withRetrievalBudgets` / `withQueryEmbedTimeout` /
    /// `withQueryEmbedAttempts` / `withQueryEmbedConcurrency` /
    /// `withFactStageTimeout`.
    RetrievalBudgets: RetrievalBudgets
    /// Bound on retrieval-trace emissions queued for the
    /// background writer that takes the trace write off the request path.
    /// Default 1,024. A full queue drops the trace and counts it
    /// (`BackgroundRetrievalTracer.Lost`). `0` writes traces inline on the
    /// request path, the pre-894 behaviour. Set via
    /// `withRetrievalTraceQueueCapacity`.
    RetrievalTraceQueueCapacity: int
    /// The keyword index hybrid retrieval runs over. Default
    /// `InProcessSparseIndex`, the in-process BM25 index exactly as every
    /// earlier composition built it. Set via `withSparseIndex` (a supplied
    /// index, e.g. the `ToolUp.SparseIndices.Postgres` companion, which is
    /// the same on every replica), `withAnalyzedSparseIndex` (an index built
    /// for the composed analyzer), or `withoutSparseIndex` (dense only).
    SparseIndex: SparseIndexComposition
    /// The fact tier, applied to the deployment when the RAG tier is
    /// COMPOSED rather than when it is set, so it sees the final
    /// `ServerConfig` whichever side of `withConfig` the call sits. `None`
    /// (the default) composes no fact tier here. Set via `withFacts`.
    FactTier: (ServerApp -> ServerApp) option
}

/// Does this app's composed `IEmbeddingCache` span replicas?
///
/// This is the exact predicate `composeWithRAG` feeds into
/// `TeamModeSharedEmbeddingCacheValidator`, exposed so the lift can be
/// asserted (by a deployment's own preflight, or by a test) without
/// standing up a whole composition. Note what it deliberately does NOT
/// say: composing `InMemoryEmbeddingCache` explicitly returns `false`,
/// because the concern is the per-replica divergence, not whether a hook
/// was called — an operator who wires the process-local cache by hand has
/// changed nothing about what the validator is warning at.
///
/// Read from the cache's own `IEmbeddingCacheLocality` declaration, never
/// its type, so a decorator that forwards the declaration keeps the
/// warning. An undeclared cache reads as cross-replica, as an unrecognised
/// cache did before (GP 11).
let hasCrossReplicaEmbeddingCache (options: RagOptions) : bool =
    match options.EmbeddingCache with
    | Some composed -> EmbeddingCacheLocality.isCrossReplica (box composed)
    | None -> false

/// Does this app's composed `IEmbeddingProvider` key its
/// state per `VectorScope`?
///
/// The predicate `composeWithRAG` feeds into
/// `TeamModeLocalEmbedderValidator`, exposed for the same reason as
/// `hasCrossReplicaEmbeddingCache` above: a deployment's own preflight
/// (or a test) can assert the lift without standing up a whole
/// composition.
///
/// Measured on the provider the deployment actually composed, not on the
/// wrapped one the pipeline holds — the question is what the operator
/// wired, and the caching decorator's answer is derived from it anyway.
/// A stateless embedder returns `false` and can never return anything
/// else: scope-keying is meaningless for a pure function of (model,
/// text), which is exactly why the capability is a probe rather than a
/// member on `IEmbeddingProvider`.
let hasScopeKeyedEmbeddingProvider (options: RagOptions) : bool =
    ScopedEmbedding.isScopeKeyed options.EmbeddingProvider

/// Is `store` an in-process vector store, whose index lives in
/// one process's memory (and so differs between replicas)? Keyed on the
/// composed instance, like `hasCrossReplicaEmbeddingCache`: what lifts the
/// replica warning is the store that removes its premise, not a builder
/// having been called.
///
/// Read from the store's own `IVectorStoreLocality`
/// declaration, never its type name, so a decorator that forwards the
/// declaration keeps the warning and a type that merely shares a
/// companion's name does not trip it. An undeclared store reads as not
/// in-process (GP 11), as an unrecognised store did before.
let isInProcessVectorStore (store: IVectorStore) : bool = VectorIndexLocality.isInProcess store

/// Is `index` an in-process keyword index, whose postings live
/// in one process's memory?
///
/// Read from the index's own `ISparseIndexLocality`
/// declaration, never a type test, so a decorator that forwards the
/// declaration keeps the warning. An undeclared index reads as not
/// in-process (GP 11).
let isInProcessSparseIndex (index: ISparseIndex) : bool = SparseIndexLocality.isInProcess index

/// The score space the composition implies: every keyword-index
/// composition but `withoutSparseIndex` fuses, and a reranker rescores.
/// What a reader falls back to for a pipeline that declares no space of
/// its own (`ScoredPool.declaring`).
let private composedSpace (options: RagOptions) : RetrievalScoreSpace =
    let keywordIndex =
        match options.SparseIndex with
        | SparseIndexComposition.NoSparseIndex -> false
        | SparseIndexComposition.InProcessSparseIndex
        | SparseIndexComposition.SuppliedSparseIndex _
        | SparseIndexComposition.AnalyzedSparseIndex _ -> true

    RetrievalScoreSpace.ofComposition keywordIndex options.Reranker.IsSome

/// The preflight question's space: the composed pipeline's, or `None` when
/// a supplied pipeline owns retrieval (`withRetrievalPipeline`) and its
/// space is not the SDK's to see before it runs.
let private composedScoreSpace (options: RagOptions) : RetrievalScoreSpace option =
    match options.RetrievalPipelineOverride with
    | Some _ -> None
    | None -> Some(composedSpace options)

/// Warns at startup when a configured `MinScore` cannot be met,
/// or cannot be confirmed reachable, in the composed pipeline's score space
/// (`RetrievalScoreSpace.thresholdFinding`). Silent without a threshold and
/// in the two spaces the SDK can bound, so a deployment that tunes nothing
/// is unchanged.
let private minScoreSpaceValidator
    (space: RetrievalScoreSpace option)
    (minScore: float option)
    : ConfigValidation.IConfigValidator =
    { new ConfigValidation.IConfigValidator with
        member _.Name = "rag-min-score-space"
        member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

        member _.Validate() = async {
            return
                match RetrievalScoreSpace.thresholdFinding space minScore with
                | Some message -> ConfigValidation.ValidationResult.Warning message
                | None -> ConfigValidation.ValidationResult.Ok
        }
    }

// ─── Phase 869 — flush the composition's own stores at a graceful stop ──
//
// `composeRAG` registers its vector store and keyword index as INSTANCES (so
// resolution order is what it always was), and the container never disposes
// an instance it was handed. The final flush each in-process store performs
// in `Dispose` therefore never ran: writes acknowledged inside their persist
// debounce (two seconds, or a hundred chunks) were lost on every graceful
// shutdown. This service ends their lifetime at stop instead.
//
// It flushes from `StoppedAsync`, not `StopAsync`. Hosted services stop in
// reverse registration order, and the job scheduler — registered by the base
// composition BEFORE this extension's services — stops AFTER every RAG
// service: while it drains, an ingestion retry or a vacuum job can still
// write through the store. `StoppedAsync` runs once every hosted service's
// `StopAsync` has returned, and ignores the window's token on purpose: the
// window may already be spent, and acknowledged writes are flushed anyway.
// `Dispose` (the provider disposes this factory-registered service) is the
// fallback for a host that never calls the lifecycle hooks; the flush runs
// once either way.
//
// Only the stores this composition CONSTRUCTED are flushed. A store supplied
// through `withVectorStore` / `withSparseIndex` is the caller's, who may
// dispose it themselves — disposing it here as well would make their own
// `Dispose` the second one.
//
// Placed `OnceAtStart`: it runs no loop, only a stop hook, and the stores
// hold THIS process's acknowledged writes, so every profile that opened them
// — a serverless host included, whose provider disposal is the hook's
// fallback — must flush them rather than let a peer cover for it.
type internal RetrievalStoreFlushService(owned: (string * System.IDisposable) list, logger: ILogger) =
    let mutable flushed = 0

    let flushOnce () =
        if System.Threading.Interlocked.Exchange(&flushed, 1) = 0 then
            for name, store in owned do
                try
                    store.Dispose()
                with ex ->
                    logger.Error(sprintf "[RAGCompose] event=store_flush_at_stop_failed store=%s" name, Some ex)

    interface IHostedService with
        member _.StartAsync _ =
            System.Threading.Tasks.Task.CompletedTask

        member _.StopAsync _ =
            System.Threading.Tasks.Task.CompletedTask

    interface IHostedLifecycleService with
        member _.StartingAsync _ =
            System.Threading.Tasks.Task.CompletedTask

        member _.StartedAsync _ =
            System.Threading.Tasks.Task.CompletedTask

        member _.StoppingAsync _ =
            System.Threading.Tasks.Task.CompletedTask

        member _.StoppedAsync _ =
            System.Threading.Tasks.Task.Run(fun () -> flushOnce ())

    interface System.IDisposable with
        member _.Dispose() = flushOnce ()

// ─── What RAG builds from the running app's services ─────────────
//
// The retrieval pipeline, the per-file ingestion-status store and the
// ingestion's collaborators read services the running app composed: the
// data-object store, a registered retrieval tracer, the fact tier, a query
// rewriter, the runtime knowledge-base toggle, the usage log and the quota
// policy. The container builds them ONCE, the first time anything resolves
// a service projected from them, so each sees exactly the instances the app
// runs with and no second container is built to find them.

/// RAG's run-time objects, built once by the container.
[<NoEquality; NoComparison>]
type internal RagRuntime = {
    /// The per-file ingestion status: written by the post-save hook and the
    /// Data Manager observer, read by the file list.
    IngestionStatusStore: IIngestionStatusStore
    /// The tracer the pipeline writes through (the off-path queue when composed).
    Tracer: IRetrievalTracer
    /// The off-path trace queue, when traces are not written inline.
    BackgroundTracer: ToolUp.RAG.RetrievalTracers.BackgroundRetrievalTracer option
    Pipeline: IRetrievalPipeline
    /// The fact-clause feeder, present exactly when the fact tier is composed.
    ClausePlanner: IFactClausePlanner option
    IngestionObservers: IIngestionStatusObserver list
    UsageLog: IUsageLog
    Quota: ITeamQuotaPolicy
    RetryDeps: IngestionRetryDeps
}

[<RequireQualifiedAccess>]
module internal RagRuntime =

    /// The runtime the container built, building it on first use.
    let resolve (sp: System.IServiceProvider) : RagRuntime = sp.GetRequiredService<RagRuntime>()

// ─── composeRAG ───────────────────────────────────────────────────
//
// `composeRAG options` is the RAG companion's `apply`. It composes *over*
// `composeAI`: it builds the RAG-aware system-prompt layers (framing
// preamble + retrieval builder + strict-grounding guard), folds them into
// the AI assistant config, runs `composeAI` to lift every AI contribution
// (agent-loop handlers, tool registry, AI DI registrations, dev
// endpoints, tool-name + platform-provider validation) onto the
// deployment, then layers the RAG-specific contributions (vector store /
// sparse index / pipeline / ingestion + reembedding hosted services,
// citation normaliser, the `/health/rag` + `/dev/rag-citation` routes,
// the RAG config validators) on top. Composing over `composeAI` keeps
// every AI registration in one place: RAG inherits each one rather than
// copying it.

/// The RAG companion's identity: the slot `withRAG` composes under, and the
/// owner of its DI registrations.
let private ragOwner: ComponentId = ComponentId.forCompanionSlot "ToolUp.RAG"

/// Apply every RAG-specific contribution onto `app` (composing over
/// `composeAI` for the AI half), returning the composed result without
/// driving it.
///
/// **Advanced.** Consumers compose RAG with `withRAG`. Hidden from
/// IntelliSense via `[<EditorBrowsable>]`.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
let composeRAG (options: RagOptions) (app: ServerApp) : ServerApp =
    // The fact tier goes onto the deployment FIRST, against the config it
    // was finally given, so the services RAG reads from it (the fact
    // resolver, the disclosure gate, the clause planner) are registered.
    let b =
        match options.FactTier with
        | None -> app
        | Some factTier -> factTier app

    let config = ServerApp.effectiveConfig b

    // Derive the tool-aware framing summary once, from the
    // deployment's aggregated module tool list (tools land here via
    // `withAITools`). Phase 538 — the derivation reads each tool's typed
    // `IsLiveInterface` declaration (or its `ClientResident` location),
    // never its name. Drives both the framing companion below and the
    // retrieval builder's empty-result message. A deployment with no
    // live-interface tools yields `HasLiveUiTools = false` ⇒ historical
    // framing, no regression.
    let toolFraming = RAGPromptBuilder.ToolFraming.fromTools (b.AITools |> List.map fst)

    // The same derivation, surfaced: which live-interface tools
    // are loaded and whether the framing above is consequently active.
    let liveInterface =
        liveInterfaceSummary (b.AITools |> List.map fst) options.GroundingMode

    // Retrieval-pipeline override seam. When a consumer supplies
    // a pipeline via `withRetrievalPipeline` (e.g. a build-time-precomputed
    // static-corpus pipeline), it is registered verbatim and the default
    // `RetrievalPipeline` construction is skipped. `suppressIngestion`
    // additionally drops the ingestion + reembedding background services when
    // the override is set AND no module contributes a `VectorisationHandler`
    // (no producers ⇒ no consumers): a static-doc deployment then carries no
    // live-ingestion overhead (GP 13). Query embedding still resolves the
    // registered `IEmbeddingProvider`; only chunk embeddings move to build time.
    let suppressIngestion =
        options.RetrievalPipelineOverride.IsSome && b.VectorisationHandlers.IsEmpty

    // One queue type, two backings. No store composed ⇒ the
    // historical process-local channel, byte for byte (GP 11 / GP 13);
    // `Some store` ⇒ the same surface over durable storage, with
    // lease-based at-least-once delivery and cross-replica draining.
    let queue =
        match options.IngestionQueueStore with
        | Some store -> IngestionQueue(options.IngestionQueueCapacity, options.OverflowPolicy, store)
        | None -> IngestionQueue(options.IngestionQueueCapacity, options.OverflowPolicy)

    // A supplied or analyzer-built keyword index is resolved
    // HERE, at composition, rather than inside the service registration: an
    // index that cannot serve the composed analyzer refuses now, naming it,
    // and the replica validator below sees the instance the deployment
    // actually composed. The in-process default is still constructed with
    // the RAG storage, below, exactly as before.
    let composedSparseIndex: ISparseIndex option =
        match options.SparseIndex, options.SparseAnalyzer with
        | SparseIndexComposition.SuppliedSparseIndex _, Some analyzer ->
            invalidOp (
                sprintf
                    "[RAGCompose] withSparseIndex and withSparseAnalyzer are both composed, but a supplied ISparseIndex tokenises however it was built to — the analyzer '%s' would be silently ignored. Compose the index with RagOptions.withAnalyzedSparseIndex so it is built for the analyzer (or refuses it), or drop withSparseAnalyzer."
                    analyzer.Id
            )
        | SparseIndexComposition.SuppliedSparseIndex index, None -> Some index
        | SparseIndexComposition.AnalyzedSparseIndex build, analyzer ->
            Some(build (analyzer |> Option.defaultValue ToolUp.RAG.SparseAnalysis.identity))
        | SparseIndexComposition.InProcessSparseIndex, _
        | SparseIndexComposition.NoSparseIndex, _ -> None

    // Default to a 60-second rolling-window telemetry sink so `/health/rag`
    // is meaningful out-of-the-box. Deployments wanting Prometheus / OTel
    // export pass their own `IRagTelemetry` via `withTelemetry`.
    let telemetry =
        options.Telemetry
        |> Option.defaultWith (fun () -> ToolUp.RAG.RagTelemetry.createDefault ())

    // Resolve a logger once — used for the null-blob warning, IngestionService,
    // the vector store flush loop, and the post-save vectorisation hook. Falls
    // back to `ConsoleLogger` so callers who don't pass one still see warnings.
    let ragLogger =
        b.Logger
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    // The retrieval layer of the system prompt. The pipeline, its tracer and
    // the fact-clause feeder are the ones the container built for the running
    // app (`RagRuntime`), read from the request's services; a context that
    // carries no RAG runtime contributes nothing.
    let ragBuilder: SystemPromptBuilder =
        fun ctx -> async {
            let runtime =
                if isNull ctx.Services then
                    None
                else
                    match ctx.Services.GetService(typeof<RagRuntime>) with
                    | :? RagRuntime as runtime -> Some runtime
                    | _ -> None

            match runtime with
            | Some runtime ->
                let! block =
                    (withRetrievalPlanned
                        options.RetrievalDefaults
                        (Some telemetry)
                        (Some runtime.Tracer)
                        toolFraming
                        runtime.ClausePlanner
                        options.FactClausePlan
                        (ScoredPool.declaring (composedSpace options) runtime.Pipeline))
                        ctx

                // Server-side strict-grounding guard. Under `StrictlyGrounded`,
                // an empty retrieved set means "knowledge base had nothing
                // relevant" — refuse the turn WITHOUT a provider call rather
                // than relying solely on the prompt directive (which a model
                // can ignore or be jailbroken past). Pairs with
                // `strictlyGroundedDirective`.
                if
                    options.GroundingMode = StrictlyGrounded
                    && List.isEmpty ctx.RetrievedSources.Value
                then
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
        resolveFramingWithTools options.GroundingMode options.RetrievalFraming toolFraming

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

        match options.AI.AIConfig with
        | None ->
            Some {
                Branding = {
                    Name = "AI Assistant"
                    Icon = ""
                    ShowSidePanel = true
                }
                SystemPrompt = Some(SystemPromptBuilder.compose ragLayers)
                Limits = AgentLoopLimits.defaults
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
    let aiComposed: ServerApp =
        composeAI
            {
                options.AI with
                    AIConfig = resolvedAiConfig
            }
            b

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
                yield "/dev/ai-allowlist"
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
        if options.CitationPolicy = CitationNormaliser.Off then
            None
        else
            Some(ToolUp.RAG.CitationNormaliserImpl.create options.CitationPolicy citationCounters)

    // RAG-owned routes layered on top of the AI handlers `composeAI` mounted.
    let ragHandlers = [
        ToolUp.RAG.RagHealthHandler.route
        if citationEndpointEnabled then
            yield! ToolUp.RAG.RAGCitationDevEndpoint.routes
    ]

    // RAG's registrations. A staged contribution: the event store, audit log
    // and notification channel are the values compose built (`Substrate`),
    // so nothing here builds a container to find them. What RAG needs from
    // services that are not substrate values is built by the container
    // itself, once, in `RagRuntime`, and the services that depend on it are
    // projected from it by factory.
    let ragStaged (substrate: Substrate) (s: IServiceCollection) =
        let eventStore = substrate.EventStore
        let auditLog = substrate.AuditLog
        let notifications = substrate.Notifications

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
                    "[RAGCompose] No IBlobStorage supplied — vector index persistence is disabled. Ingested chunks will be lost across process restart. Pass `Some storage` (RagOptions.withStorage) for a durable deployment."

                makeNullBlobStorage ()

        // The keyword index is a composition choice. The default constructs
        // the in-process index; a supplied or analyzer-built index was
        // resolved at composition (`composedSparseIndex`, above); `None` is
        // dense-only.
        let sparseIndex: ISparseIndex option =
            match options.SparseIndex with
            | SparseIndexComposition.InProcessSparseIndex ->
                match options.SparseAnalyzer with
                | None -> Some(new InMemoryBM25Index(blobStorageForRag, logger = ragLogger) :> ISparseIndex)
                | Some analyzer ->
                    Some(
                        new InMemoryBM25Index(blobStorageForRag, logger = ragLogger, analyzer = analyzer)
                        :> ISparseIndex
                    )
            | SparseIndexComposition.SuppliedSparseIndex _
            | SparseIndexComposition.AnalyzedSparseIndex _ -> composedSparseIndex
            | SparseIndexComposition.NoSparseIndex -> None

        // Wrap the supplied embedder so repeated query / chunk text hits a
        // cache rather than the underlying provider. Cache key includes
        // provider + model + dimensions so a model swap automatically
        // invalidates entries. `withEmbeddingCache` substitutes the cache;
        // unset constructs the `InMemoryEmbeddingCache` (GP 11).
        let embeddingCache: IEmbeddingCache =
            match options.EmbeddingCache with
            | Some composed -> composed
            | None -> new InMemoryEmbeddingCache() :> IEmbeddingCache

        let cachedEmbedder: IEmbeddingProvider =
            CachingEmbeddingProvider.create options.EmbeddingProvider embeddingCache

        let pipelineOptions: RetrievalPipelineOptions = {
            Reranker = options.Reranker
            EnableMmr = options.EnableMmr
            MmrLambda = options.MmrLambda
            ActiveModuleBoost = RetrievalPipelineOptions.defaults.ActiveModuleBoost
            SummaryBoost = RetrievalPipelineOptions.defaults.SummaryBoost
            FactNarrativeJoinBoost = RetrievalPipelineOptions.defaults.FactNarrativeJoinBoost
            QueryRewriteTimeoutMs = RetrievalPipelineOptions.defaults.QueryRewriteTimeoutMs
        }

        // The default vector store makes corrupt-index loads loud (audit
        // event + telemetry counter + SystemMessage) instead of starting the
        // scope silently empty, through the audit log and notification
        // channel compose built. It is an INSTANCE registration, so the
        // container never disposes it: `RetrievalStoreFlushService` ends its
        // lifetime at stop.
        let vectorStore: IVectorStore =
            options.VectorStore
            |> Option.defaultWith (fun () ->
                new InMemoryVectorStore(
                    blobStorageForRag,
                    logger = ragLogger,
                    telemetry = telemetry,
                    auditLog = auditLog,
                    notifications = notifications
                )
                :> IVectorStore)

        // One shared alert state spans the first-attempt path (the
        // background service) and the scheduled retry handler, so
        // provider-unavailable dedup and the dead-letter-rate threshold
        // count both.
        let ingestionAlertState = IngestionAlertState()

        // Built once by the container (`RagRuntime`), from the services the
        // running app composed. A service registered later in the
        // composition is seen here too: the container resolves the final
        // registration, as it does for every other consumer.
        let buildRuntime (sp: System.IServiceProvider) : RagRuntime =
            // The per-file ingestion-status store: durable over
            // `IDataObjectStore` when composed (status survives a restart),
            // in-memory otherwise. ONE instance is shared three ways: the
            // post-save hook (writes `Pending` / `Failed`), the observer
            // (writes `Indexed`) and `FileManagement` (badges the file list).
            let ingestionStatusStore: IIngestionStatusStore =
                match ServiceComposition.tryService<IDataObjectStore> sp with
                | Some dos -> IngestionStatusStore.create dos (Some ragLogger)
                | None -> IngestionStatusStore.createInMemory ()

            let dataManagerIngestionObserver =
                ToolUp.RAG.DataManagerIngestionObserver.create ingestionStatusStore (Some notifications) ragLogger

            // The registered tracer if any; default to the event-store tracer
            // so retrieval traces are persisted out-of-the-box.
            let composedTracer =
                match ServiceComposition.tryService<IRetrievalTracer> sp with
                | Some t -> t
                | None -> ToolUp.RAG.RetrievalTracers.createEventStore eventStore ragLogger

            // The trace write leaves the request path: the pipeline enqueues,
            // a background loop writes, a full queue drops and counts. `0`
            // keeps the inline write.
            let backgroundTracer =
                if options.RetrievalTraceQueueCapacity > 0 then
                    Some(
                        ToolUp.RAG.RetrievalTracers.createBackground
                            composedTracer
                            options.RetrievalTraceQueueCapacity
                            ragLogger
                    )
                else
                    None

            let retrievalTracer: IRetrievalTracer =
                match backgroundTracer with
                | Some background -> background :> IRetrievalTracer
                | None -> composedTracer

            let pipeline: IRetrievalPipeline =
                // An override (e.g. the static-corpus pipeline) is used
                // verbatim; the default dense/hybrid `RetrievalPipeline` is
                // not constructed.
                match options.RetrievalPipelineOverride with
                | Some overridePipeline -> overridePipeline
                | None ->
                    // The live `IPlatformRuntimeConfigStore` value when
                    // registered, otherwise the static
                    // `ServerConfig.PlatformKnowledgeBase` value.
                    let runtimeStoreOpt = ServiceComposition.tryService<IPlatformRuntimeConfigStore> sp

                    let snapshot () =
                        match runtimeStoreOpt with
                        | Some rc -> rc.Snapshot()
                        | None -> config.PlatformKnowledgeBase

                    RetrievalPipeline(
                        vectorStore,
                        cachedEmbedder,
                        ?sparseIndex = sparseIndex,
                        options = pipelineOptions,
                        tracer = retrievalTracer,
                        platformKnowledgeBaseSnapshot = snapshot,
                        // The same instance the `/health/rag` endpoint
                        // resolves, so per-stage P50/P95 surface there.
                        telemetry = telemetry,
                        ?maxQueryChars = options.MaxQueryChars,
                        eventStore = eventStore,
                        // The fact stage and its retrieval egress door,
                        // present exactly when the fact tier is composed
                        // (it registers the resolver and the gate as a
                        // pair); absent ⇒ a fact-less pipeline (GP 11 / 13).
                        ?factResolver = ServiceComposition.tryService<IFactResolver> sp,
                        ?disclosureGate = ServiceComposition.tryService<IFactDisclosureGate> sp,
                        // Conversation-aware query rewrite, present exactly
                        // when the deployment registered an `IQueryRewriter`.
                        ?queryRewriter = ServiceComposition.tryService<IQueryRewriter> sp,
                        budgets = options.RetrievalBudgets
                    )
                    :> IRetrievalPipeline

            // The Data Manager observer is appended to the app's own
            // observers (e.g. KB's); each fires independently.
            let ingestionObservers =
                options.IngestionObservers @ [ dataManagerIngestionObserver ]

            {
                IngestionStatusStore = ingestionStatusStore
                Tracer = retrievalTracer
                BackgroundTracer = backgroundTracer
                Pipeline = pipeline
                ClausePlanner = ServiceComposition.tryService<IFactClausePlanner> sp
                IngestionObservers = ingestionObservers
                // Embedding spend is attributed per scope and pre-flight
                // gated like AI provider calls; NoOp keeps ingestion working
                // when either is absent.
                UsageLog =
                    ServiceComposition.tryService<IUsageLog> sp
                    |> Option.defaultWith (fun () -> NoOpUsageLog() :> IUsageLog)
                Quota =
                    ServiceComposition.tryService<ITeamQuotaPolicy> sp
                    |> Option.defaultWith (fun () -> NoOpTeamQuotaPolicy() :> ITeamQuotaPolicy)
                RetryDeps = {
                    Pipeline = pipeline
                    EventStore = eventStore
                    Observers = ingestionObservers
                    NotificationChannel = Some notifications
                    Telemetry = telemetry
                    Logger = ragLogger
                    Policy = options.IngestionRetryPolicy
                    AlertState = ingestionAlertState
                }
            }

        // Background re-embedding: when a deployment swaps the embedding
        // model, chunks indexed under the old `EmbeddingVersion` are
        // re-indexed via `IRetrievalPipeline.Index`.
        let reembedQueue = ToolUp.RAG.ReembeddingService.ReembeddingQueue()

        // The unified index-lifecycle seam over every index tier this
        // composition fuses, so KB deletion paths fan out across the vector
        // store AND the sparse index.
        let indexLifecycle: ToolUp.Platform.Infra.IIndexLifecycle.IIndexLifecycle =
            ToolUp.Platform.Infra.IIndexLifecycle.DefaultIndexLifecycle(
                vectorStore,
                sparseIndex,
                Some embeddingCache,
                ragLogger
            )

        let s =
            s.AddSingleton<RagRuntime>(System.Func<System.IServiceProvider, RagRuntime>(buildRuntime))

        let s = s.AddSingleton<IVectorStore>(vectorStore)

        // Registered exactly when a keyword index is composed. Dense-only
        // registers none, so a consumer resolving `ISparseIndex` optionally
        // (the entity store's full-text leg) sees it absent.
        let s =
            match sparseIndex with
            | Some index -> s.AddSingleton<ISparseIndex>(index)
            | None -> s

        // The stores registered above are flushed at a graceful stop by
        // `RetrievalStoreFlushService`, which owns exactly the ones this
        // composition constructed. Nothing constructed, nothing registered
        // (GP 13).
        let s =
            let owned = [
                match options.VectorStore, box vectorStore with
                | None, (:? System.IDisposable as store) -> "vector-store", store
                | _ -> ()

                match options.SparseIndex, sparseIndex |> Option.map box with
                | SparseIndexComposition.InProcessSparseIndex, Some(:? System.IDisposable as index) ->
                    "sparse-index", index
                | _ -> ()
            ]

            match owned with
            | [] -> s
            | _ ->
                // No loop, only a stop hook flushing the stores THIS process
                // opened: a silo that skipped it would lose its own
                // acknowledged writes, so every profile runs it.
                Hosting.register
                    s
                    (BackgroundUnit.create "rag-retrieval-store-flush" OnceAtStart (fun _ ->
                        new RetrievalStoreFlushService(owned, ragLogger) :> IHostedService))

        // The off-path trace queue, registered so `/health/rag` can report
        // its lost-trace count. Absent when traces are written inline
        // (capacity 0), and then the endpoint omits the block.
        let s =
            if options.RetrievalTraceQueueCapacity > 0 then
                s.AddSingleton<ToolUp.RAG.RetrievalTracers.BackgroundRetrievalTracer>(
                    System.Func<System.IServiceProvider, ToolUp.RAG.RetrievalTracers.BackgroundRetrievalTracer>
                        (fun sp -> (RagRuntime.resolve sp).BackgroundTracer |> Option.get)
                )
            else
                s

        let s =
            s
                .AddSingleton<ToolUp.Platform.Infra.IIndexLifecycle.IIndexLifecycle>(indexLifecycle)
                .AddSingleton<IEmbeddingProvider>(cachedEmbedder)
                .AddSingleton<IEmbeddingCache>(embeddingCache)

        // A supplied pipeline stays an instance registration (its owner
        // keeps owning it); the default is the one the container built.
        let s =
            match options.RetrievalPipelineOverride with
            | Some overridePipeline -> s.AddSingleton<IRetrievalPipeline>(overridePipeline)
            | None ->
                s.AddSingleton<IRetrievalPipeline>(
                    System.Func<System.IServiceProvider, IRetrievalPipeline>(fun sp -> (RagRuntime.resolve sp).Pipeline)
                )

        let s =
            s
                .AddSingleton<IngestionQueue>(queue)
                .AddSingleton<ToolUp.RAG.ReembeddingService.ReembeddingQueue>(reembedQueue)

        // Document-understanding defaults: no-ops, so the KB extractor can
        // resolve them unconditionally. A companion's own provider,
        // registered before RAG, is kept; one registered after wins as the
        // later registration.
        Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<IOcrProvider>(
            s,
            ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
        )

        Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<ITableExtractor>(
            s,
            ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()
        )

        let s =
            s
                .AddSingleton<IRagTelemetry>(telemetry)
                // The SAME store instance the hook and observer write
                // through, so `FileManagementApi.ListFiles` joins status onto
                // the file-list read.
                .AddSingleton<IIngestionStatusStore>(
                    System.Func<System.IServiceProvider, IIngestionStatusStore>(fun sp ->
                        (RagRuntime.resolve sp).IngestionStatusStore)
                )

        // The post-save vectorisation hook: `SessionFileStore.AddFile`
        // enqueues chunks after every successful upload. Registered only
        // when a module contributes a `VectorisationHandler`.
        let s =
            if b.VectorisationHandlers.IsEmpty then
                s
            else
                s.AddSingleton<PostSaveHook>(
                    System.Func<System.IServiceProvider, PostSaveHook>(fun sp ->
                        PostSaveHook(
                            makeVectorisationHook
                                b.VectorisationHandlers
                                queue
                                telemetry
                                options.EnableDocumentSummaries
                                options.MaxChunkBytes
                                options.MaxDocumentBytes
                                eventStore
                                (RagRuntime.resolve sp).IngestionStatusStore
                                options.OverflowPolicy
                                auditLog
                                notifications
                                ragLogger
                        ))
                )

        // The ingestion + reembedding hosted services are the load-bearing
        // live-ingestion overhead. `suppressIngestion` is set when a
        // retrieval-pipeline override is composed AND no module contributes
        // a `VectorisationHandler`: a static-corpus deployment with no
        // producers, so these consumers have nothing to do (GP 13).
        let s =
            if suppressIngestion then
                ragLogger.Info(
                    "[RAGCompose] Retrieval pipeline overridden with no VectorisationHandler registered — ingestion + reembedding background services suppressed (static-corpus / read-only retrieval mode)."
                )

                s
            else
                // Ingestion is shared work when the queue is durable (a
                // composed `IIngestionQueueStore`: every replica drains ONE
                // queue, so a worker silo drains it for a web silo). Over the
                // in-process channel only the process that accepted the
                // upload can drain it, so it is process-local there: gating
                // it off a WebOnly silo would strand every job that silo's
                // HTTP tier enqueued.
                let ingestionPlacement =
                    match options.IngestionQueueStore with
                    | Some _ -> SharedWork
                    | None -> ProcessLocal

                // Built from the host provider, so it lazily resolves the
                // (downstream-built) `IJobScheduler` for durable retries.
                let ingestion =
                    BackgroundUnit.create "rag-ingestion" ingestionPlacement (fun (sp: System.IServiceProvider) ->
                        let runtime = RagRuntime.resolve sp

                        let getJobScheduler () =
                            match sp.GetService(typeof<IJobScheduler>) with
                            | :? IJobScheduler as js -> Some js
                            | _ -> None

                        // The service registers the retry handler on the
                        // resolved scheduler at startup (the handler is
                        // compiled after IngestionService, so the
                        // registration is injected here).
                        let registerRetryHandler (scheduler: IJobScheduler) =
                            scheduler.RegisterHandler(
                                RetryHandlerName,
                                ToolUp.RAG.IngestionRetryJobHandler.create runtime.RetryDeps
                            )

                        create
                            queue
                            runtime.Pipeline
                            cachedEmbedder
                            eventStore
                            runtime.IngestionObservers
                            options.IngestionConcurrency
                            ragLogger
                            telemetry
                            runtime.UsageLog
                            runtime.Quota
                            options.IngestionRetryPolicy
                            (Some notifications)
                            ingestionAlertState
                            getJobScheduler
                            registerRetryHandler
                        :> IHostedService)

                // Re-embedding is shared work: it scans the SHARED vector
                // store for chunks embedded under another model, and nothing
                // outside the service enqueues to its queue, so one silo
                // re-embeds for every silo (N replicas would pay the
                // embedding API N times for the same corpus).
                let reembedding =
                    BackgroundUnit.create "rag-reembedding" SharedWork (fun (sp: System.IServiceProvider) ->
                        ToolUp.RAG.ReembeddingService.create
                            reembedQueue
                            vectorStore
                            (RagRuntime.resolve sp).Pipeline
                            cachedEmbedder
                            eventStore
                            ragLogger
                        :> IHostedService)

                Hosting.register (Hosting.register s ingestion) reembedding

        // Startup recovery sweep for the per-file ingestion status. A restart
        // mid-ingestion leaves the DURABLE status entry at `Pending` while
        // the in-memory job it referred to is gone; the sweep flips those
        // entries to `Failed` with a restart-interrupted reason. The sweep
        // body is `ToolUp.Platform.IngestionRecoverySweep`, shared with KB;
        // this block owns WHETHER a sweep runs and which surfaces and scopes
        // it gets.
        //
        // Registered when the consumer named containers to sweep OR composed
        // a scope enumerator — an unconfigured deployment carries no hosted
        // service and no surface registration (GP 11 / GP 13). Surfaces are
        // resolved from the BUILT provider at StartAsync, so a companion that
        // registers its own surface is picked up with no ordering
        // requirement, and the scope enumeration is a live read.
        let s =
            if options.IngestionRecoveryScopes.IsEmpty && options.ScopeEnumerator.IsNone then
                s
            else
                let explicitScopes = options.IngestionRecoveryScopes
                let enumerator = options.ScopeEnumerator

                let s =
                    s.AddSingleton<IIngestionRecoverySurface>(
                        System.Func<System.IServiceProvider, IIngestionRecoverySurface>(fun sp ->
                            IngestionRecoverySweep.ofIngestionStatusStore (RagRuntime.resolve sp).IngestionStatusStore)
                    )

                // Once at start: a one-shot sweep settling the ingestion
                // jobs a restart lost.
                Hosting.register
                    s
                    (BackgroundUnit.create "rag-ingestion-recovery-sweep" OnceAtStart (fun sp ->
                        IngestionRecoverySweep.hostedService
                            ragLogger
                            (fun () -> sp.GetServices<IIngestionRecoverySurface>() |> List.ofSeq)
                            (fun () -> IngestionRecoverySweep.resolveScopes explicitScopes enumerator ragLogger)))

        let s =
            match options.Reranker with
            | Some r -> s.AddSingleton<IReranker>(r)
            | None -> s

        // Tombstone auto-vacuum. When a vacuum schedule is configured,
        // register the vacuum handler and schedule a cron job on the
        // `IJobScheduler`, from a hosted service's `StartAsync`, because the
        // scheduler is only resolvable from the built provider. Scheduling is
        // idempotent (stable key + one-year TTL), so a restart re-attaches to
        // the existing job definition rather than duplicating it.
        let s =
            match options.VacuumSchedule with
            | None -> s
            | Some cron ->
                let vacuumDeps: ToolUp.RAG.RAGVacuumJobHandler.RAGVacuumDeps = {
                    VectorStore = vectorStore
                    EventStore = eventStore
                    Retention = options.TombstoneRetention
                    Logger = ragLogger
                }

                let vacuumHandler = ToolUp.RAG.RAGVacuumJobHandler.create vacuumDeps

                // Once at start: it hands the vacuum job to the scheduler,
                // whose own unit decides where it runs.
                Hosting.register
                    s
                    (BackgroundUnit.create "rag-tombstone-vacuum" OnceAtStart (fun (sp: System.IServiceProvider) ->
                        { new IHostedService with
                            member _.StartAsync(_ct) =
                                match sp.GetService(typeof<IJobScheduler>) with
                                | :? IJobScheduler as scheduler ->
                                    scheduler.RegisterHandler(
                                        ToolUp.RAG.RAGVacuumJobHandler.VacuumHandlerName,
                                        vacuumHandler
                                    )

                                    let registration: JobRegistration = {
                                        ScopeId = ScopeKey.PlatformContainer
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
                                        CreatedBy = AuditSubject.PlatformSentinelUserId
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
                                                    options.TombstoneRetention
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
                        }))

        // RAG vector-store offboard purge hook, gated on the same
        // `TenantLifecycle = FeatureMode.Enabled` switch the core
        // `ComposeTenantLifecycle` gates on. Additive — the aggregator
        // resolves the full `seq<ITenantLifecycle>`, so this stacks with the
        // core first-party hooks.
        let s =
            match config.TenantLifecycle with
            | FeatureMode.Enabled ->
                s.AddSingleton<ITenantLifecycle>(fun (sp: System.IServiceProvider) -> RagVectorStoreLifecycle.create sp)
            | FeatureMode.Disabled -> s

        // Two `/dev/inspect` panels. Both are unconditional: they answer
        // questions ("does this deployment keep its index?", "which of my
        // data types are actually indexed?") whose ANSWER may be fine, so
        // gating them on the matching validator having fired would hide
        // exactly the healthy case an operator wants to confirm. Neither
        // does I/O — the payload is closed over at compose time.
        s
            .AddSingleton<IDevDiagnosticsContributor>(
                ToolUp.RAG.RagConfigValidator.RagDurabilityContributor(
                    b.Storage.IsSome,
                    options.VectorStore.IsSome,
                    config.RiskAcknowledgements.AcceptEphemeralRagIndex
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
            // The live-interface posture: a `/dev/inspect` panel, plus the
            // typed value the deployment-readiness gatherer reads (DI-only,
            // so the composition manifest and its boot seal are unchanged).
            .AddSingleton<DeploymentReadiness.LiveInterfaceSummary>(liveInterface)
            .AddSingleton<IDevDiagnosticsContributor>(
                LiveInterfaceContributor(liveInterface) :> IDevDiagnosticsContributor
            )

    // RAG config validators, constructed against the effective config (the
    // module names and config schemas merged in) so team-mode / replica /
    // persistence checks see the deployment shape.

    let ragValidators: ConfigValidation.IConfigValidator list = [
        // Phase 4b commit 3 — IDF-leak warning when LocalEmbeddingProvider is
        // active in Team / MultiTeam mode.
        // The warning is lifted when the composed provider is
        // SCOPE-KEYED: the premise it rests on (one IDF dictionary shared
        // by every team in the process) no longer holds. Still fires for
        // the unscoped `create ()` / `createPersistent`, which leak exactly
        // as before.
        ToolUp.RAG.RagConfigValidator.TeamModeLocalEmbedderValidator(
            config,
            options.EmbeddingProvider,
            hasScopeKeyedEmbeddingProvider options
        )
        // Refuse (persistent) / warn (ephemeral) when RAG has no durable backing.
        ToolUp.RAG.RagConfigValidator.RagPersistenceValidator(config, b.Storage.IsSome, options.VectorStore.IsSome)
        // Phase 9j follow-up — refuse the in-process ingestion queue under
        // ReplicaCount > 1 (no leasing/redelivery ⇒ silent corpus loss).
        // The refusal is lifted when a DURABLE queue is
        // composed: the premise it rests on (process-local, no
        // redelivery) no longer holds.
        ToolUp.RAG.RagConfigValidator.RagIngestionInstanceValidator(config, queue.IsDurable)
        // Wave 2A Gap #1 — warn when default InMemoryEmbeddingCache is active
        // under multi-instance Team mode (cache key has no tenant component).
        // The warning is lifted when a CROSS-REPLICA cache is
        // composed: the premise it rests on (each replica keeps its own
        // entries) no longer holds.
        ToolUp.RAG.RagConfigValidator.TeamModeSharedEmbeddingCacheValidator(
            config,
            hasCrossReplicaEmbeddingCache options
        )
        // Wave 2A Gap #9 — surface the clamp log so silent clamping of
        // operator-supplied values is visible at startup.
        ToolUp.RAG.RagConfigValidator.RetrievalDefaultsValidator options.RetrievalDefaultsClampLog
        // 2026-06-12 audit (RAG Gap 8) — warn when the citation-dev-
        // endpoint override is configured as the retired force-on shape.
        ToolUp.RAG.RagConfigValidator.CitationDevEndpointValidator config
        // Surface the steady-state-memory contract: warn when a
        // vacuum schedule is set without a scheduler, or when a persistent
        // deployment has no vacuum schedule at all (tombstones accumulate).
        ToolUp.RAG.RagConfigValidator.VacuumScheduleValidator(config, options.VacuumSchedule.IsSome)
        // Warn when an authenticated RAG deployment runs with no
        // rate limiter: retrieval embeds every query, so an unbounded request
        // loop burns embedding spend even behind a proxy (per-query cost, not
        // per-connection). Mode ≠ Anonymous AND RateLimit = RateLimitConfig.none.
        ToolUp.RAG.RagConfigValidator.RAGRateLimitConfiguredValidator config
        // Warn when the dev-only,
        // process-stateful LocalEmbeddingProvider serves a non-team
        // production shape. The team half is TeamModeLocalEmbedderValidator
        // above; the two partition the deployment space so one problem is
        // never reported twice.
        ToolUp.RAG.RagConfigValidator.LocalEmbeddingProviderInProductionModeValidator(config, options.EmbeddingProvider)
        // Warn when data types are registered but NO
        // module contributes a VectorisationHandler: nothing is ever
        // indexed and there is no error anywhere in that path. Skipped
        // when a retrieval-pipeline override owns the corpus (the same
        // condition `suppressIngestion` uses above).
        ToolUp.RAG.RagConfigValidator.RAGHandlersRegisteredValidator(
            b.DataTypes |> List.map _.Id,
            b.VectorisationHandlers |> List.map _.DataTypeId,
            options.RetrievalPipelineOverride.IsSome
        )
        // Explicit bounds on the tuning knobs. The
        // `with*` setters clamp lower bounds only, so nothing today
        // rejects `withTopK 200` or `withIngestionQueueCapacity 10`.
        ToolUp.RAG.RagConfigValidator.RAGConfigBoundsValidator {
            TopK = options.RetrievalDefaults.TopK
            MinScore = options.RetrievalDefaults.MinScore
            MmrLambda = options.MmrLambda
            MmrEnabled = options.EnableMmr
            SnippetCharLimit = options.RetrievalDefaults.SnippetCharLimit
            IngestionConcurrency = options.IngestionConcurrency
            IngestionQueueCapacity = options.IngestionQueueCapacity
        }
        // Warn when the configured MinScore cannot be met, or
        // cannot be confirmed reachable, in the composed score space.
        minScoreSpaceValidator (composedScoreSpace options) options.RetrievalDefaults.MinScore
        // Warn when more than one replica runs over an
        // in-process vector store or keyword index: each replica keeps its
        // own, so retrieval differs by which replica served the turn.
        // Skipped when a retrieval-pipeline override owns the corpus.
        ToolUp.RAG.RagConfigValidator.InProcessIndexReplicaValidator(
            config,
            (if options.RetrievalPipelineOverride.IsSome then
                 None
             else
                 match options.VectorStore with
                 | None -> Some "InMemoryVectorStore"
                 | Some store when isInProcessVectorStore store -> Some(store.GetType().Name)
                 | Some _ -> None),
            (if options.RetrievalPipelineOverride.IsSome then
                 None
             else
                 match options.SparseIndex with
                 | SparseIndexComposition.InProcessSparseIndex -> Some "InMemoryBM25Index"
                 | _ ->
                     match composedSparseIndex with
                     | Some index when isInProcessSparseIndex index -> Some "InMemoryBM25Index"
                     | _ -> None)
        )
    ]

    // Merge RAG handlers + the "RAG" notification-consumer
    // declaration onto whatever `composeAI` already accumulated. `composeAI`
    // declared "AI"; appending "RAG" reproduces the former combined
    // `[ "AI"; "RAG" ]` declaration.
    let baseExt = aiComposed.Extensions

    let mergedExt: ComposeExtensions = {
        baseExt with
            Handlers = baseExt.Handlers @ ragHandlers
            NotificationConsumers = baseExt.NotificationConsumers @ [ "RAG" ]
    }

    let withValidators =
        ragValidators
        |> List.fold (fun a v -> ServerApp.withConfigValidator v a) aiComposed

    {
        withValidators with
            Extensions = mergedExt
    }
    // RAG's registrations are a contribution under its own id.
    |> ServerApp.addContribution (Contribution.staged ragOwner ragStaged)

module RagOptions =
    /// The RAG companion's options with the three required inputs: the AI
    /// provider factory and the canonical platform `IProviderProfile` BYOK
    /// store (the AI tier's, `AIOptions.create`), and the embedding
    /// provider. Every other field takes its default; chain the
    /// `RagOptions.with*` setters to configure further.
    let create
        (factory: IAIProviderFactory)
        (providerProfile: IProviderProfile)
        (embedder: IEmbeddingProvider)
        : RagOptions =
        {
            AI = AIOptions.create factory providerProfile
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
            FactClausePlan = FactClausePlanOptions.defaults
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
            ScopeEnumerator = None
            RetrievalBudgets = RetrievalBudgets.defaults
            RetrievalTraceQueueCapacity = 1024
            SparseIndex = SparseIndexComposition.InProcessSparseIndex
            FactTier = None
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
        (options: RagOptions)
        : RagOptions =
        if not changed then
            options
        else
            {
                options with
                    RetrievalDefaultsClampLog =
                        sprintf "%s = %s clamped to %s" field original clamped
                        :: options.RetrievalDefaultsClampLog
            }

    /// Replace the embedding provider after construction. Rare — the
    /// constructor parameter is the canonical path.
    let withEmbeddingProvider (provider: IEmbeddingProvider) (options: RagOptions) : RagOptions = {
        options with
            EmbeddingProvider = provider
    }

    /// **Sets** the fact tier this RAG composition composes:
    ///
    /// ```fsharp skip=fragment
    /// options |> RagOptions.withFacts FactsCompose.withFactTier
    /// ```
    ///
    /// `factTier` is applied to the deployment when the RAG tier is
    /// composed, so the `withRAG` call may sit before or after `withConfig`
    /// (which replaces the whole `ServerConfig`, the fact-store knob
    /// included). Composed that way the fact tier arms both doors: the
    /// retrieval pipeline picks up the fact resolver and the clause
    /// planner — whose question compiler resolves through this app's own
    /// `IAIProviderFactory` unless the tier chose one with
    /// `FactsCompose.withQuestionCompiler` — so a question that names a
    /// fact pushes it into the prompt
    /// ahead of the retrieved passages, and the fact tools reach the model.
    ///
    /// The tier is a parameter rather than a reference because the fact
    /// companion is a separate package: a RAG deployment that composes no
    /// facts takes no dependency on it. Calling `withFacts` again replaces
    /// the tier.
    let withFacts (factTier: ServerApp -> ServerApp) (options: RagOptions) : RagOptions = {
        options with
            FactTier = Some factTier
    }

    /// **Sets** the options of the AI tier RAG composes over, replacing the
    /// ones `create` built.
    let withAIOptions (ai: AIOptions) (options: RagOptions) : RagOptions = { options with AI = ai }

    /// **Sets** the AI tier's assistant config (`AIOptions.withAIConfig`).
    let withAIConfig (config: AIAssistantServerConfig) (options: RagOptions) : RagOptions = {
        options with
            AI = AIOptions.withAIConfig config options.AI
    }

    /// **Sets** the AI tier's per-module contexts
    /// (`AIOptions.withModuleAIContexts`).
    let withModuleAIContexts (contexts: ModuleAIContext list) (options: RagOptions) : RagOptions = {
        options with
            AI = AIOptions.withModuleAIContexts contexts options.AI
    }

    let withIngestionObserver (observer: IIngestionStatusObserver) (options: RagOptions) : RagOptions = {
        options with
            IngestionObservers = options.IngestionObservers @ [ observer ]
    }

    let withIngestionObservers (observers: IIngestionStatusObserver list) (options: RagOptions) : RagOptions = {
        options with
            IngestionObservers = options.IngestionObservers @ observers
    }

    /// Register a cross-encoder reranker. Companion packages under
    /// `src/Rerankers/<Name>/` (e.g. `Rerankers/Local/` for the CPU-bound
    /// ONNX reranker) provide implementations. Without one, retrieval
    /// returns the RRF-fused (or cosine-only) candidate list directly. With
    /// one, the reranker scores every match retrieval returns — a pool
    /// larger than its `MaxBatchSize` is capped at it — and the boosts then
    /// nudge on its scale.
    let withReranker (reranker: IReranker) (options: RagOptions) : RagOptions = {
        options with
            Reranker = Some reranker
    }

    /// Enable MMR diversity reranking after retrieval (and rerank and the
    /// boosts, when present). MMR's order is the order the caller receives
    /// . Off by default. Use `withMmrLambda` to tune the
    /// relevance/diversity balance.
    let withMmr (options: RagOptions) : RagOptions = { options with EnableMmr = true }

    /// Set the MMR `λ` ∈ [0, 1]. Higher = more relevance-weighted, lower =
    /// more diversity-weighted. 0.5 is the literature default. Implies
    /// `withMmr` if not already enabled.
    let withMmrLambda (lambda: float) (options: RagOptions) : RagOptions =
        // Contract is λ ∈ [0, 1]; clamp rather than let an
        // out-of-range value silently skew (or NaN) the MMR
        // re-ranking. Matches the clamp-in-setter convention used
        // by withIngestionConcurrency / withTopK.
        let clamped = max 0.0 (min 1.0 lambda)

        {
            options with
                EnableMmr = true
                MmrLambda = clamped
        }
        |> noteClamp "MmrLambda" (string lambda) (string clamped) (clamped <> lambda)

    /// Substitute a non-default `IVectorStore`. Companion packages under
    /// `src/VectorStores/<Name>/` (e.g. `Hnsw/` for HNSW.Net) provide
    /// implementations. Without one, RAG uses the in-memory flat-scan
    /// `InMemoryVectorStore` — fine up to ~50k chunks per scope.
    ///
    /// A supplied store stays the caller's: the composition
    /// flushes and disposes at stop only the store it constructed, so dispose
    /// a supplied one yourself once the host has stopped.
    let withVectorStore (store: IVectorStore) (options: RagOptions) : RagOptions = {
        options with
            VectorStore = Some store
    }

    /// Substitute the `IEmbeddingCache` the composition wraps
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
    let withEmbeddingCache (cache: IEmbeddingCache) (options: RagOptions) : RagOptions = {
        options with
            EmbeddingCache = Some cache
    }

    /// Compose a language-aware analyzer for the sparse (BM25)
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
    let withSparseAnalyzer (analyzer: ISparseAnalyzer) (options: RagOptions) : RagOptions = {
        options with
            SparseAnalyzer = Some analyzer
    }

    /// Substitute the entire retrieval pipeline. `p` is
    /// registered as the `IRetrievalPipeline` verbatim; the default
    /// `RetrievalPipeline` (and, when no `VectorisationHandler` is present,
    /// the ingestion + reembedding background services) are skipped. Slots a
    /// build-time-precomputed static-corpus pipeline — or any custom
    /// `IRetrievalPipeline` — into the standard `RagOptions` fluent shape so
    /// static-doc-Q&A consumers and live-KB consumers share one composition
    /// root. Query embedding still uses the registered `IEmbeddingProvider`;
    /// only chunk embeddings are precomputed. `IEmbeddingProvider` remains a
    /// required `create` argument for now (relaxing it is a deferred follow-up).
    let withRetrievalPipeline (pipeline: IRetrievalPipeline) (options: RagOptions) : RagOptions = {
        options with
            RetrievalPipelineOverride = Some pipeline
    }

    /// Cap the number of documents the `IngestionBackgroundService` processes
    /// in parallel. Each slot does one batched embedding call covering a whole
    /// document, so the effective upstream concurrency equals this number of
    /// HTTP calls (not chunks). Default 8.
    let withIngestionConcurrency (concurrency: int) (options: RagOptions) : RagOptions =
        let clamped = max 1 concurrency

        {
            options with
                IngestionConcurrency = clamped
        }
        |> noteClamp "IngestionConcurrency" (string concurrency) (string clamped) (clamped <> concurrency)

    /// Cap the number of pending document jobs in the ingestion queue. When
    /// full, `IngestionQueue.Enqueue` returns `false` and KB upload handlers
    /// surface the rejection by marking the document `Failed`. Default 5,000.
    /// Lower for tight memory budgets, higher for deployments with bursty
    /// bulk uploads.
    let withIngestionQueueCapacity (capacity: int) (options: RagOptions) : RagOptions = {
        options with
            IngestionQueueCapacity = max 1 capacity
    }

    /// Choose what happens when the ingestion queue is full.
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
    let withIngestionQueueOverflowPolicy (policy: IngestionOverflowPolicy) (options: RagOptions) : RagOptions = {
        options with
            OverflowPolicy = policy
    }

    /// Back the ingestion queue with a durable store, so
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
    let withDurableIngestionQueue (store: IIngestionQueueStore) (options: RagOptions) : RagOptions = {
        options with
            IngestionQueueStore = Some store
    }

    /// Sweep these storage containers at startup for
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
    /// Containers are named by the consumer. Empty (default) ⇒ no sweep
    /// and no hosted service (GP 11 / GP 13).
    ///
    /// **Phase 723 — prefer `withScopeEnumerator` for anything but a
    /// fixed scope set.** A hand-written list is the shape that made the
    /// sweep least likely to be wired exactly where it mattered most: a
    /// deployment large enough to need restart recovery is the one whose
    /// container list is longest, changes as teams are created, and goes
    /// stale silently. The two compose — the union of the explicit list
    /// and the enumeration is swept — so a deployment can migrate
    /// without a gap and then drop the list.
    let withIngestionRecoverySweep (containers: string list) (options: RagOptions) : RagOptions = {
        options with
            IngestionRecoveryScopes = containers
    }

    /// Compose the scope-enumeration seam, so the restart
    /// recovery sweep discovers the containers to visit instead of being
    /// handed a list.
    ///
    /// The SDK default is `ScopeEnumeration.fromTeamStore teamStore`:
    /// every team's `team-{id}` container (read through the EXISTING
    /// `ITeamStore.ListTeams` — no interface member was added, so no
    /// external `ITeamStore` implementation breaks) plus the well-known
    /// `_platform` / `_deployment` containers. A deployment whose scopes
    /// come from elsewhere composes its own `IScopeEnumerator`; one whose
    /// scopes are genuinely fixed can pass `ScopeEnumeration.ofScopes`.
    ///
    /// Composing this ENABLES the sweep on its own — a deployment that
    /// names no containers and composes an enumerator sweeps the
    /// enumerated ones, which is the entire point of the seam. A
    /// deployment that composes neither is unchanged (GP 13).
    ///
    /// Note what the default cannot see: personal (`user-{id}`)
    /// containers are not enumerable from `ITeamStore`. A personal-mode
    /// deployment composes `ScopeEnumeration.ofScopes` or its own
    /// enumerator; see the `ScopeEnumeration.fromTeamStoreWith` remarks.
    let withScopeEnumerator (enumerator: IScopeEnumerator) (options: RagOptions) : RagOptions = {
        options with
            ScopeEnumerator = Some enumerator
    }

    /// Set the retry / dead-letter policy for transient
    /// embedder failures during ingestion. Transient per-chunk failures
    /// (429 / 5xx / timeout / network) retry with exponential backoff +
    /// jitter and dead-letter after `MaxAttempts`; permanent failures
    /// (401/403 / other 4xx) dead-letter immediately with an Owner/Admin
    /// alert. Default `IngestionRetryPolicy.defaults` (5 attempts,
    /// 30s→30min backoff, +20% jitter). `MaxAttempts` is clamped to ≥ 1.
    let withIngestionRetryPolicy (policy: IngestionRetryPolicy) (options: RagOptions) : RagOptions = {
        options with
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
    let withTelemetry (telemetry: IRagTelemetry) (options: RagOptions) : RagOptions = {
        options with
            Telemetry = Some telemetry
    }

    /// Replace the entire `RetrievalDefaults` record. Use targeted setters
    /// (`withTopK`, `withMinScore`, `withMergeStrategy`,
    /// `withSnippetCharLimit`) when only one knob needs to change.
    let withRetrievalDefaults (defaults: RetrievalDefaults) (options: RagOptions) : RagOptions =
        // Clamp the whole-record override to the same bounds the
        // targeted setters enforce — otherwise a fat-fingered
        // TopK=0 / MinScore=1.0 here silently disables retrieval.
        let clamped = RetrievalDefaults.clamp defaults

        {
            options with
                RetrievalDefaults = clamped
        }
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
    let withTopK (topK: int) (options: RagOptions) : RagOptions =
        let clamped = max 1 topK

        {
            options with
                RetrievalDefaults = {
                    options.RetrievalDefaults with
                        TopK = clamped
                }
        }
        |> noteClamp "TopK" (string topK) (string clamped) (clamped <> topK)

    /// Drop matches scoring at or below `threshold`. `None` disables the
    /// gate (default — every match returned by the pipeline is surfaced).
    /// Use a small positive value (e.g. `0.4`) to refuse weak matches in
    /// regulated-brand or low-tolerance deployments.
    ///
    /// The threshold applies in the composed pipeline's score
    /// space (`RetrievalScoreSpace`). The default composition builds a
    /// keyword index, so scores are FUSED and normalised onto `[0, 1]`
    /// within each query's candidate pool: the best match scores `1.0` and
    /// the threshold trims the weak tail relative to it. Dense-only
    /// (`withoutSparseIndex`) scores are cosine similarity, an absolute
    /// floor. With a reranker composed the threshold reads the reranker's
    /// own, uncalibrated scale, and a startup validator says so.
    let withMinScore (threshold: float option) (options: RagOptions) : RagOptions =
        // Both SDK-known spaces (cosine, normalised fused) top out at 1.0
        // and the gate keeps only scores strictly above the threshold, so
        // values ≥ 1.0 filter out EVERY unboosted match (the assistant
        // goes silent with no diagnostic); negatives are a no-op gate.
        // Clamp to a sane [0.0, 0.99] so a fat-fingered threshold can't
        // silently disable retrieval entirely.
        let clamped = threshold |> Option.map (fun t -> max 0.0 (min 0.99 t))

        {
            options with
                RetrievalDefaults = {
                    options.RetrievalDefaults with
                        MinScore = clamped
                }
        }
        |> noteClamp "MinScore" (sprintf "%A" threshold) (sprintf "%A" clamped) (clamped <> threshold)

    /// Choose how multi-scope results combine. `Interleaved` re-ranks by
    /// score regardless of scope (default); `Separate` keeps per-scope
    /// grouping for callers presenting platform vs team knowledge
    /// separately. With MMR enabled the `Interleaved` re-sort is skipped,
    /// because it would undo the diversity order MMR produced.
    let withMergeStrategy (strategy: MergeStrategy) (options: RagOptions) : RagOptions = {
        options with
            RetrievalDefaults = {
                options.RetrievalDefaults with
                    Merge = strategy
            }
    }

    /// Set the maximum character length of each `RetrievedSource.Snippet`
    /// preview surfaced in the AI client's Sources panel. Trimmed with an
    /// ellipsis when content exceeds the budget. Default 240.
    let withSnippetCharLimit (limit: int) (options: RagOptions) : RagOptions =
        let clamped = max 16 limit

        {
            options with
                RetrievalDefaults = {
                    options.RetrievalDefaults with
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
    let withOriginFilter (filter: Set<ChunkOrigin> option) (options: RagOptions) : RagOptions = {
        options with
            RetrievalDefaults = {
                options.RetrievalDefaults with
                    OriginFilter = filter
            }
    }

    /// Set the deployment-level metadata-equality filter every
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
    let withRetrievalFilters (filters: Map<string, string> option) (options: RagOptions) : RagOptions = {
        options with
            RetrievalDefaults = {
                options.RetrievalDefaults with
                    Filters = filters
            }
    }

    /// Enable conversation-history indexing (WS3.4 — scaffolded only,
    /// implementation lands in a follow-up phase). When `true`, completed
    /// AI conversations will be vectorised under
    /// `ChunkOrigin.Conversation`. Off by default; flipping the flag
    /// today is a no-op until the ingestion handler is wired.
    let withIndexConversations (enabled: bool) (options: RagOptions) : RagOptions = {
        options with
            IndexConversations = enabled
    }

    /// Set the grounding stance — `Permissive` (model uses KB + training
    /// data freely), `Preferred` (default — KB authoritative on conflict),
    /// or `StrictlyGrounded` (refuse to answer without a retrieval hit).
    /// `StrictlyGrounded` works best paired with `withMinScore (Some x)`
    /// so the model only refuses on genuinely-thin retrievals.
    let withGroundingMode (mode: GroundingMode) (options: RagOptions) : RagOptions = {
        options with
            GroundingMode = mode
    }

    /// Turn the fact-clause feeder on or off, keeping the
    /// current budget. On (the default) and with the fact tier composed,
    /// each user turn is compiled into a `FactClause` so a question naming
    /// registered vocabulary gets its facts pushed into the prompt ahead
    /// of the chunks, without a tool round-trip. Off restores the pre-708
    /// behaviour exactly: the push path stays dormant and facts reach the
    /// model only through `query_facts`.
    let withFactClausePlanning (enabled: bool) (options: RagOptions) : RagOptions = {
        options with
            FactClausePlan = {
                options.FactClausePlan with
                    Enabled = enabled
            }
    }

    /// Replace the whole fact-clause-planning options record
    /// (enabled flag + compile budget). Clamped into `[1 ms, 30 s]`: a
    /// non-positive budget is not a bound `Async.StartChild` will accept,
    /// and a budget past half a minute is not bounding a chat turn.
    let withFactClausePlanOptions (planOptions: FactClausePlanOptions) (options: RagOptions) : RagOptions = {
        options with
            FactClausePlan = FactClausePlanOptions.clamp planOptions
    }

    /// Toggle document-level summary chunks. Default `true` — when a
    /// `VectorisationHandler` defines `Summarise`, the SDK calls it after
    /// `Vectorise` and indexes the resulting chunk under
    /// `_isSummary = "true"`. Pass `false` to skip the summary call
    /// entirely (cost control for AI-summary deployments where the extra
    /// per-document HTTP isn't worth the retrieval-quality gain).
    let withDocumentSummaries (enabled: bool) (options: RagOptions) : RagOptions = {
        options with
            EnableDocumentSummaries = enabled
    }

    /// Override the framing preamble injected into the system prompt ahead
    /// of retrieved chunks. The default (`defaultRetrievalFraming`) is
    /// authoritative-tone — KB content beats training data on conflict —
    /// and is what most deployments want. Pass `""` to opt out entirely
    /// (composes nothing) or any non-empty string to substitute custom
    /// framing.
    let withRetrievalFraming (framing: string) (options: RagOptions) : RagOptions = {
        options with
            RetrievalFraming = framing
    }

    /// Set the post-stream citation-normaliser policy.
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
        (options: RagOptions)
        : RagOptions =
        { options with CitationPolicy = policy }

    /// Cap the UTF-8 byte size of any single chunk's `TextChunk.Content`
    /// reaching the ingestion queue. Default `None` = no limit (historical
    /// behaviour). A typical value is `Some (10 * 1024 * 1024)` (10 MB) —
    /// large enough that ordinary chunked PDFs / spreadsheets pass through
    /// unchanged, but small enough that a pathological spreadsheet cell
    /// can't pin memory across thousands of queued jobs.
    let withMaxChunkBytes (maxBytes: int) (options: RagOptions) : RagOptions = {
        options with
            MaxChunkBytes = Some(max 1 maxBytes)
    }

    /// Cap the sum of UTF-8 byte sizes across every chunk in a single
    /// `DocumentIngestionJob`. Default `None` = no limit. Typical value
    /// `Some (100 * 1024 * 1024)` (100 MB).
    let withMaxDocumentBytes (maxBytes: int) (options: RagOptions) : RagOptions = {
        options with
            MaxDocumentBytes = Some(max 1 maxBytes)
    }

    /// Cap the character length of a retrieval query reaching
    /// the pipeline. Above the cap, `IRetrievalPipeline.Retrieve` refuses
    /// with a `KnowledgeQueryTooLargeException` and emits a
    /// `KnowledgeQueryRejected` audit. Default `Some 16384` (~4k tokens) —
    /// generous for any genuine natural-language question, tight enough that
    /// an accidental whole-document paste is refused before it burns embedding
    /// spend. Clamped to a floor of 1.
    let withMaxQueryChars (maxChars: int) (options: RagOptions) : RagOptions = {
        options with
            MaxQueryChars = Some(max 1 maxChars)
    }

    /// Set the tombstone retention window. Soft-deleted chunks
    /// (`_deletedAt` stamped by `IVectorStore.DeleteChunk`) become
    /// vacuum-eligible once older than `retention`. Default 7 days.
    /// Clamped to a floor of one minute so a fat-fingered `TimeSpan.Zero`
    /// can't turn every soft-delete into an immediate hard-delete on the
    /// next sweep (which would defeat `RestoreChunk`).
    let withTombstoneRetention (retention: System.TimeSpan) (options: RagOptions) : RagOptions =
        let clamped =
            if retention < System.TimeSpan.FromMinutes 1.0 then
                System.TimeSpan.FromMinutes 1.0
            else
                retention

        {
            options with
                TombstoneRetention = clamped
        }

    /// Enable the tombstone auto-vacuum on the default daily
    /// 03:00 UTC schedule. Registers a `RAGVacuumJobHandler` on the
    /// `IJobScheduler` that sweeps every scope, hard-removing tombstones
    /// older than `TombstoneRetention`. REQUIRES `ServerConfig.JobScheduler
    /// = InProcessJobScheduler` (or a distributed scheduler companion) —
    /// without a scheduler the sweep never fires and the
    /// `VacuumScheduleValidator` warns at startup. Use
    /// `withVacuumScheduleCron` for a non-default cadence.
    let withVacuumSchedule (options: RagOptions) : RagOptions = {
        options with
            VacuumSchedule = Some RAGVacuumJobHandler.DefaultVacuumCron
    }

    /// Enable the tombstone auto-vacuum on a custom cron
    /// (`Minute Hour DayOfMonth Month DayOfWeek`; the in-process scheduler
    /// supports `*`, integers, comma lists, and `*/N`). An invalid
    /// expression is rejected at schedule time with
    /// `ScheduleError.InvalidCron` and logged at `Warn` — the deployment
    /// still boots.
    let withVacuumScheduleCron (cron: string) (options: RagOptions) : RagOptions = {
        options with
            VacuumSchedule = Some cron
    }

    // ─── Phase 894 — per-turn retrieval budgets ─────────────────────

    /// Replace the whole per-turn budget record. Default
    /// `RetrievalBudgets.defaults`.
    let withRetrievalBudgets (budgets: RetrievalBudgets) (options: RagOptions) : RagOptions = {
        options with
            RetrievalBudgets = budgets
    }

    /// Wall-clock budget for the query-time embed. On overrun
    /// the dense branch returns nothing and the sparse branch, when
    /// composed, answers alone (stage mark `DenseDegraded:TimedOut`).
    /// Default 5 s. Non-positive disables the budget.
    let withQueryEmbedTimeout (timeout: System.TimeSpan) (options: RagOptions) : RagOptions = {
        options with
            RetrievalBudgets = {
                options.RetrievalBudgets with
                    QueryEmbed = {
                        options.RetrievalBudgets.QueryEmbed with
                            Timeout = timeout
                    }
            }
    }

    /// Attempts the query-time embed makes inside its budget.
    /// Default 1 (no retry inside a chat turn); clamped to at least 1.
    let withQueryEmbedAttempts (attempts: int) (options: RagOptions) : RagOptions = {
        options with
            RetrievalBudgets = {
                options.RetrievalBudgets with
                    QueryEmbed = {
                        options.RetrievalBudgets.QueryEmbed with
                            MaxAttempts = max 1 attempts
                    }
            }
    }

    /// Ceiling on query-time embeds in flight at once. A call
    /// that finds it reached is refused at once, as a value — the dense
    /// branch degrades (stage mark `DenseDegraded:Refused`) rather than
    /// queueing. Default: no ceiling. Clamped to at least 1.
    let withQueryEmbedConcurrency (maxConcurrentCalls: int) (options: RagOptions) : RagOptions = {
        options with
            RetrievalBudgets = {
                options.RetrievalBudgets with
                    QueryEmbed = {
                        options.RetrievalBudgets.QueryEmbed with
                            MaxConcurrentCalls = Some(max 1 maxConcurrentCalls)
                    }
            }
    }

    /// Budget for fact resolution plus the disclosure check.
    /// On overrun or fault the turn proceeds without pushed facts (stage
    /// mark `FactsDegraded:TimedOut` / `FactsDegraded:Failed`); a fact whose
    /// disclosure check did not complete is never admitted. Default 3 s.
    let withFactStageTimeout (timeout: System.TimeSpan) (options: RagOptions) : RagOptions = {
        options with
            RetrievalBudgets = {
                options.RetrievalBudgets with
                    FactStage = timeout
            }
    }

    /// Bound on retrieval traces queued for the background
    /// writer. Default 1,024; a full queue drops and counts the trace.
    /// `0` writes traces inline on the request path (pre-894).
    let withRetrievalTraceQueueCapacity (capacity: int) (options: RagOptions) : RagOptions = {
        options with
            RetrievalTraceQueueCapacity = max 0 capacity
    }

    /// Substitute the keyword (sparse) index hybrid retrieval
    /// runs over, in the shape of `withVectorStore`. Companion packages under
    /// `src/SparseIndices/<Name>/` provide implementations — the
    /// `ToolUp.SparseIndices.Postgres` companion holds the index in the
    /// database, so the keyword leg is the same on every replica. Without
    /// one, RAG constructs the in-process `InMemoryBM25Index` exactly as
    /// before (GP 11).
    ///
    /// The supplied index tokenises however it was built to, so composing it
    /// together with `withSparseAnalyzer` is refused at composition; use
    /// `withAnalyzedSparseIndex` to have the index built for the analyzer.
    /// Ignored when `withRetrievalPipeline` replaces the whole pipeline.
    ///
    /// As with `withVectorStore`, a supplied index stays the
    /// caller's to dispose; the composition flushes only what it constructed.
    let withSparseIndex (index: ISparseIndex) (options: RagOptions) : RagOptions = {
        options with
            SparseIndex = SparseIndexComposition.SuppliedSparseIndex index
    }

    /// Substitute the keyword index with one BUILT FOR the
    /// composed analyzer: `build` receives the analyzer `withSparseAnalyzer`
    /// composed (the identity analyzer when none) at composition. The seam an
    /// index that tokenises inside its own engine uses to map the analyzer
    /// to its own configuration — or to refuse it, naming it, before the
    /// deployment starts (e.g. `PostgresFullTextIndex.factory`).
    let withAnalyzedSparseIndex (build: ISparseAnalyzer -> ISparseIndex) (options: RagOptions) : RagOptions = {
        options with
            SparseIndex = SparseIndexComposition.AnalyzedSparseIndex build
    }

    /// Compose NO keyword index: dense retrieval only. Fusion
    /// over one list is that list, so the pipeline takes its dense-only path
    /// (the one it takes with no reranker and no keyword leg), and no
    /// `ISparseIndex` is registered — the entity store's optional full-text
    /// leg sees it absent. Index lifecycle and erasure fan out over the
    /// dense store alone.
    let withoutSparseIndex (options: RagOptions) : RagOptions = {
        options with
            SparseIndex = SparseIndexComposition.NoSparseIndex
    }

// ─── The RAG companion on a `ServerApp` ────────────────────────────

/// **Sets** the RAG companion: the retrieval tier `options` describes, and
/// the AI tier it composes over, are composed once, when the deployment
/// resolves, onto the deployment as every builder left it — so a
/// `ServerApp` builder applied after this call still reaches both tiers. A
/// second call replaces the first.
///
/// RAG composes the AI tier itself (it folds its retrieval layers into the
/// assistant config), so this also claims the AI companion's slot with a
/// tier that adds nothing: an earlier `AICompose.withAI` is superseded by
/// `options.AI`, and a later one is refused when the deployment resolves
/// (`composeAI` refuses a deployment the AI tier was already composed
/// onto), so the AI tier is never composed twice.
///
///     ServerApp.empty
///     |> ServerApp.withConfig config
///     |> ServerApp.withStorage storage
///     |> RAGCompose.withRAG (
///         RagOptions.create factory providerProfile embedder
///         |> RagOptions.withAIConfig assistant
///         |> RagOptions.withGroundingMode StrictlyGrounded
///     )
///     |> ServerApp.run
let withRAG (options: RagOptions) (app: ServerApp) : ServerApp =
    app
    |> ServerApp.withCompanion aiCompanion id
    |> ServerApp.withCompanion ragOwner (composeRAG options)
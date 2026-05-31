module KnowledgeBase.ServerApiDeps

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IOcrProvider
open ToolUp.Platform.ITableExtractor
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.TeamManagement
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerJsonHelpers
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerInventory

// ─── Per-request dependency record ───────────────────────────────
//
// `knowledgeApi` resolves ~17 DI values + 4 local closures per
// request. Bundling them into a single record lets the per-method
// API handlers (`Server/Api/Documents.fs` etc.) take one parameter
// rather than 17, while keeping the original closure pattern
// (`deps.PublishInventory ()` reads naturally inside handler bodies).

/// Per-request dependencies passed to every API handler. Constructed
/// once by `KnowledgeApiDeps.resolve` at the top of `knowledgeApi`
/// and threaded into each handler invocation.
type KnowledgeApiDeps = {
    Storage: IBlobStorage
    Queue: IngestionQueue
    OcrProvider: IOcrProvider
    TableExtractor: ITableExtractor
    Notifications: INotificationChannel
    Logger: ILogger
    Scope: StorageScope
    UserId: string
    VectorScope: VectorScope
    VectorStore: IVectorStore option
    EventStore: IEventStore option
    /// Optional `INarrativeStore` for cross-store reset coherence
    /// (Phase 4b commit 2). Resolved from DI per request; `None` when
    /// the SDK has not registered an `INarrativeStore` (test harnesses
    /// bypassing the standard compose). Used by `resetIndex` to wipe
    /// persisted narrative entries for the caller's scope alongside
    /// KB blobs and vector chunks — without this the user resets their
    /// KB and `list_narratives` still returns prior entries.
    NarrativeStore: INarrativeStore option
    AccessContext: AccessContext
    /// Records ingestion-queue enqueue outcomes against the
    /// per-deployment `IRagTelemetry` snapshot. No-op when no queue
    /// or no telemetry sink is registered.
    RecordEnqueue: bool -> unit
    /// Publishes a fresh `InventorySummary` to the user's notification
    /// scope. Called after every inventory mutation.
    PublishInventory: unit -> Async<unit>
    /// Marks a document `Failed` after `IngestionQueue.Enqueue` returned
    /// false (queue at capacity). Updates `statusCache`, persists the
    /// failed status into the document index, and publishes an
    /// `IngestionStatusUpdate` notification.
    MarkIngestionFailed: string -> string -> string -> Async<unit>
    /// Owner / Admin gate for AI-context writes. Returns `Ok ()` for
    /// non-team modes and for users who can write team config; returns
    /// `Error <reason>` otherwise.
    EnsureContextWriteAllowed: unit -> Async<Result<unit, string>>
}

module KnowledgeApiDeps =

    /// Resolve all DI values + build the four local closures for the
    /// current request. Mirrors the original `knowledgeApi` prelude
    /// exactly — no behaviour change.
    let resolve (ctx: HttpContext) : KnowledgeApiDeps =
        let storage = ctx.RequestServices.GetService(typeof<IBlobStorage>) :?> IBlobStorage

        let queue =
            ctx.RequestServices.GetService(typeof<IngestionQueue>) :?> IngestionQueue

        // Telemetry sink resolved per-request so KB enqueue paths feed the
        // same `/health/rag` snapshot as the post-save vectorisation hook.
        // Falls back to a no-op when `composeWithRAG` hasn't registered one
        // (deployments using KB without RAG, or test harnesses).
        let ragTelemetry =
            match ctx.RequestServices.GetService(typeof<ToolUp.Platform.IRagTelemetry.IRagTelemetry>) with
            | :? ToolUp.Platform.IRagTelemetry.IRagTelemetry as t -> t
            | _ -> ToolUp.RAG.RagTelemetry.createNoOp ()

        let recordEnqueue (accepted: bool) =
            if not (isNull (box queue)) then
                ragTelemetry.RecordEnqueue(queue.Count, queue.Capacity, accepted)

        // Document-understanding providers. `composeWithRAG` registers no-op
        // defaults; companion-equipped deployments register a real OCR /
        // table extractor before composeWithRAG runs and the no-ops are
        // skipped. Either way `GetService` returns a non-null implementation.
        let ocrProvider =
            match ctx.RequestServices.GetService(typeof<IOcrProvider>) with
            | :? IOcrProvider as p -> p
            | _ -> ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()

        let tableExtractor =
            match ctx.RequestServices.GetService(typeof<ITableExtractor>) with
            | :? ITableExtractor as t -> t
            | _ -> ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()

        let notifications =
            ctx.RequestServices.GetService(typeof<INotificationChannel>) :?> INotificationChannel

        let logger =
            match ctx.RequestServices.GetService(typeof<ILogger>) with
            | :? ILogger as l -> l
            | _ ->
                { new ILogger with
                    member _.Debug _ = ()
                    member _.Info _ = ()
                    member _.Warn _ = ()
                    member _.Error(_, _) = ()
                }

        let scope =
            match ctx.Items.TryGetValue "ToolUp.StorageScope" with
            | true, (:? StorageScope as s) -> s
            | _ ->
                let userId =
                    match ctx.Items.TryGetValue "ToolUp.UserId" with
                    | true, (:? string as id) -> id
                    | _ -> "anonymous"

                {
                    ScopeId = userId
                    Container = sprintf "user-%s" userId
                    Persist = true
                }

        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        let vectorScope =
            if scope.Container.StartsWith "team-" then
                Team scope.ScopeId
            else
                Deployment

        // Vector store and event store are resolved lazily — only the Notes
        // and AI-context paths need them, and tests bypassing `composeWithRAG`
        // may not have them registered. `box _ <> null` guards each use.
        let vectorStore =
            match ctx.RequestServices.GetService(typeof<IVectorStore>) with
            | :? IVectorStore as v -> Some v
            | _ -> None

        let eventStore =
            match ctx.RequestServices.GetService(typeof<IEventStore>) with
            | :? IEventStore as e -> Some e
            | _ -> None

        let narrativeStore =
            match ctx.RequestServices.GetService(typeof<INarrativeStore>) with
            | :? INarrativeStore as n -> Some n
            | _ -> None

        let accessContext =
            match ctx.RequestServices.GetService(typeof<AccessContext>) with
            | :? AccessContext as ac -> ac
            | _ -> AccessContext.unrestricted (AnonymousSession userId)

        let publishInventory () =
            publishInventoryUpdate storage notifications logger userId scope.Container

        let markIngestionFailed (docId: string) (fileName: string) (reason: string) = async {
            let status = IngestionStatus.Failed reason
            statusCache.AddOrUpdate(docId, status, fun _ _ -> status) |> ignore

            let! existing = loadIndex storage scope.Container

            let updated =
                existing
                |> List.map (fun d -> if d.Id = docId then { d with Status = status } else d)

            do! saveIndex storage scope.Container updated

            if not (isNull (box notifications)) then
                try
                    let payload: IngestionStatusUpdate = {
                        DocumentId = docId
                        FileName = fileName
                        Outcome = "Failed"
                        ChunkCount = 0
                        ErrorReason = reason
                        UploadedBy = userId
                    }

                    let payloadJson = toJson payload
                    let notification = CustomNotification(IngestionStatusNotificationKey, payloadJson)
                    do! notifications.Publish(userId, notification)
                with ex ->
                    logger.Error(
                        sprintf "[KnowledgeBase] Failed to publish IngestionStatus notification for %s" docId,
                        Some ex
                    )
        }

        let ensureContextWriteAllowed () : Async<Result<unit, string>> = async {
            match accessContext.Subject with
            | TeamMember(userId, teamId) ->
                match ctx.RequestServices.GetService(typeof<ITeamStore>) with
                | :? ITeamStore as ts ->
                    let! role = ts.GetMemberRole(teamId, userId)

                    match role with
                    | Some r when TeamRoles.canWriteTeamConfig r -> return Ok()
                    | Some r ->
                        return
                            Error
                                $"Only team owners and admins can edit standing AI context. Your role: {TeamRoles.displayName r}."
                    | None -> return Error "You are not a member of this team."
                | _ -> return Error "Team management is not available in this deployment."
            | _ -> return Ok()
        }

        {
            Storage = storage
            Queue = queue
            OcrProvider = ocrProvider
            TableExtractor = tableExtractor
            Notifications = notifications
            Logger = logger
            Scope = scope
            UserId = userId
            VectorScope = vectorScope
            VectorStore = vectorStore
            EventStore = eventStore
            NarrativeStore = narrativeStore
            AccessContext = accessContext
            RecordEnqueue = recordEnqueue
            PublishInventory = publishInventory
            MarkIngestionFailed = markIngestionFailed
            EnsureContextWriteAllowed = ensureContextWriteAllowed
        }
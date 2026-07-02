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
    /// Phase 115 — unified deletion/erasure seam over every retrieval
    /// index the deployment composed (vector store + sparse index +
    /// embedding cache). Deletion paths route through this instead of
    /// looping `VectorStore.DeleteChunk` so a deleted document cannot
    /// keep surfacing through the hybrid sparse leg. `None` only when
    /// no vector store is wired at all (test harnesses bypassing
    /// `composeWithRAG`).
    IndexLifecycle: ToolUp.Platform.IIndexLifecycle.IIndexLifecycle option
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
    /// Per-`KnowledgeSource` original-document resolution (Phase 104).
    /// Resolved from DI when a deployment registered a custom resolver
    /// via `withOriginalSourceResolver`; falls back to the default
    /// (UploadedFile → raw blob, Note → note.md, narrative → `None`).
    OriginalResolver: KnowledgeBase.ServerOriginalSourceResolver.IOriginalSourceResolver
    /// Optional `IAuditLog` for the Phase 107 original-document access
    /// audit (`KnowledgeOriginalRetrieved` + denial events). `None`
    /// when the audit-log substrate isn't registered (test bypass,
    /// `NoAuditLog` deployment) — emission is best-effort and silent
    /// when the substrate is absent, same posture as the Platform KB
    /// admin surface.
    AuditLog: IAuditLog option
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
    /// `Error <reason>` otherwise. Reused by the destructive document /
    /// index handlers (`deleteDocument` / `resetIndex`) so a raw API call
    /// can't wipe a team's KB without owner/admin rights — repairing the
    /// asymmetry where only `SetAIContext` was gated.
    EnsureContextWriteAllowed: unit -> Async<Result<unit, string>>
    /// `true` when the request carried a middleware-resolved
    /// `ToolUp.StorageScope`; `false` when `resolve` synthesised a
    /// fallback scope. The destructive handlers fail closed when this is
    /// `false` and `UserId` is the literal `"anonymous"` (the shared
    /// `user-anonymous` collapse that happens when ScopeResolutionMiddleware
    /// isn't wired for the path) rather than mutate a container shared
    /// across every unscoped caller. Benign per-user fallbacks (a real
    /// user id, no `StorageScope`) keep `false` here but stay tenant-
    /// isolated, so they pass the guard.
    ScopeResolvedFromRequest: bool
    /// Phase 119 — compose-time upload policy (size cap, type allowlist,
    /// unsupported-type handling). Resolved from DI when a deployment
    /// composed `withUploadPolicy`; otherwise `KnowledgeUploadPolicy.permissive`
    /// (no caps; pre-119 behaviour modulo always-on filename sanitisation
    /// and the `UnsupportedFormat` status fix). Enforced by `uploadDocument`.
    UploadPolicy: KnowledgeUploadPolicy
    /// Phase 14x — compose-time content-hash dedup policy. Resolved from
    /// DI when a deployment composed `withDocumentDedup`; otherwise
    /// `KnowledgeDedupPolicy.enabled` (uploads dedup by default; the
    /// opt-out restores pre-14x behaviour byte-for-byte, GP 11).
    /// Consulted by `uploadDocument` before anything is persisted.
    DedupPolicy: KnowledgeDedupPolicy
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

        // Scope resolution. A middleware-resolved `ToolUp.StorageScope`
        // is the authoritative tenant boundary (GP 4). When it's absent
        // `resolve` synthesises a fallback — per-user when `ToolUp.UserId`
        // is present, or the shared `user-anonymous` container when neither
        // item is set. The shared collapse means ScopeResolutionMiddleware
        // isn't wired for this path and every unscoped caller lands in one
        // KB scope: surfaced loudly here, and the destructive handlers fail
        // closed on it (see `ScopeResolvedFromRequest`).
        let resolvedScope =
            match ctx.Items.TryGetValue "ToolUp.StorageScope" with
            | true, (:? StorageScope as s) -> Some s
            | _ -> None

        let scopeResolvedFromRequest = Option.isSome resolvedScope

        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        let scope =
            match resolvedScope with
            | Some s -> s
            | None ->
                if userId = "anonymous" then
                    logger.Error(
                        "[KnowledgeBase] SECURITY: request carried no resolved 'ToolUp.StorageScope' and no 'ToolUp.UserId' — collapsing onto the shared 'user-anonymous' container. ScopeResolutionMiddleware is not wired ahead of this KnowledgeApi route; every unscoped caller would share one KB scope, so destructive operations are refused. Wire the scope-resolution middleware to close this.",
                        None
                    )
                else
                    logger.Warn(
                        sprintf
                            "[KnowledgeBase] No resolved 'ToolUp.StorageScope' for this request; falling back to the per-user container 'user-%s'. Per-user isolation is preserved, but ScopeResolutionMiddleware appears unwired for this path — verify the middleware order."
                            userId
                    )

                {
                    ScopeId = userId
                    Container = sprintf "user-%s" userId
                    Persist = true
                }

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

        // Phase 115 — the composed lifecycle seam wins (it fans out
        // across vector store + sparse index + embedding cache).
        // Deployments with a vector store but no registered seam
        // (KB-without-RAG composition, older composition roots) get a
        // vector-only wrapper so the handlers have exactly one deletion
        // path — never the raw `vs.DeleteChunk` loop.
        let indexLifecycle =
            match ctx.RequestServices.GetService(typeof<ToolUp.Platform.IIndexLifecycle.IIndexLifecycle>) with
            | :? ToolUp.Platform.IIndexLifecycle.IIndexLifecycle as il -> Some il
            | _ ->
                vectorStore
                |> Option.map (fun vs ->
                    ToolUp.Platform.IIndexLifecycle.DefaultIndexLifecycle(vs, None, None, logger)
                    :> ToolUp.Platform.IIndexLifecycle.IIndexLifecycle)

        let eventStore =
            match ctx.RequestServices.GetService(typeof<IEventStore>) with
            | :? IEventStore as e -> Some e
            | _ -> None

        let narrativeStore =
            match ctx.RequestServices.GetService(typeof<INarrativeStore>) with
            | :? INarrativeStore as n -> Some n
            | _ -> None

        // Original-source resolver (Phase 104). Same probe-with-default
        // shape as the OCR / table-extractor providers above: a custom
        // resolver registered before compose wins; otherwise the
        // per-`KnowledgeSource` default applies.
        let originalResolver =
            match
                ctx.RequestServices.GetService(
                    typeof<KnowledgeBase.ServerOriginalSourceResolver.IOriginalSourceResolver>
                )
            with
            | :? KnowledgeBase.ServerOriginalSourceResolver.IOriginalSourceResolver as r -> r
            | _ -> KnowledgeBase.ServerOriginalSourceResolver.createDefault ()

        let auditLog =
            match ctx.RequestServices.GetService(typeof<IAuditLog>) with
            | :? IAuditLog as a -> Some a
            | _ -> None

        // Phase 119 — upload policy registered by `withUploadPolicy`;
        // the permissive default when absent (no caps, pre-119 behaviour).
        let uploadPolicy =
            match ctx.RequestServices.GetService(typeof<KnowledgeUploadPolicy>) with
            | :? KnowledgeUploadPolicy as p -> p
            | _ -> KnowledgeUploadPolicy.permissive

        // Phase 14x — dedup policy registered by `withDocumentDedup`;
        // dedup enabled when absent.
        let dedupPolicy =
            match ctx.RequestServices.GetService(typeof<KnowledgeDedupPolicy>) with
            | :? KnowledgeDedupPolicy as p -> p
            | _ -> KnowledgeDedupPolicy.enabled

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
            IndexLifecycle = indexLifecycle
            EventStore = eventStore
            NarrativeStore = narrativeStore
            AccessContext = accessContext
            OriginalResolver = originalResolver
            AuditLog = auditLog
            RecordEnqueue = recordEnqueue
            PublishInventory = publishInventory
            MarkIngestionFailed = markIngestionFailed
            EnsureContextWriteAllowed = ensureContextWriteAllowed
            ScopeResolvedFromRequest = scopeResolvedFromRequest
            UploadPolicy = uploadPolicy
            DedupPolicy = dedupPolicy
        }

    /// Fail-closed guard for destructive KB operations. Returns `Error`
    /// when scope resolution collapsed onto the shared `user-anonymous`
    /// container (ScopeResolutionMiddleware unwired — neither
    /// `ToolUp.StorageScope` nor `ToolUp.UserId` present on the request),
    /// so a raw API call cannot wipe a container shared across every
    /// unscoped caller. Benign per-user fallbacks (a real user id, no
    /// `StorageScope`) pass — they stay tenant-isolated. `resolve`
    /// already logged the collapse loudly; this is the operation-level
    /// refusal.
    let guardResolvedScope (deps: KnowledgeApiDeps) : Result<unit, string> =
        if not deps.ScopeResolvedFromRequest && deps.UserId = "anonymous" then
            Error
                "Storage scope is unresolved for this request (ScopeResolutionMiddleware not wired); refusing the destructive operation to avoid mutating the shared anonymous container."
        else
            Ok()
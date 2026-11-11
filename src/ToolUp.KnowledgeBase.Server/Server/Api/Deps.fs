module KnowledgeBase.ServerApiDeps

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra.IVectorStore
open ToolUp.Platform.Infra.IEmbeddingProvider
open ToolUp.Platform.Infra.IOcrProvider
open ToolUp.Platform.Infra.ITableExtractor
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Config.TeamManagement
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerJsonHelpers
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerInventory
open KnowledgeBase.ServerIngestionObserver
open ToolUp.Platform.Infra
open ToolUp.Platform.Scope
open ToolUp.Platform.Audit
open ToolUp.Platform.Compose

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
    /// The ingestion queue, `None` when no RAG tier is composed (the knowledge
    /// base then stores documents without indexing them).
    Queue: IngestionQueue option
    OcrProvider: IOcrProvider
    TableExtractor: ITableExtractor
    Notifications: INotificationChannel
    Logger: ILogger
    Scope: StorageScope
    UserId: UserId
    VectorScope: VectorScope
    VectorStore: IVectorStore option
    /// Unified deletion/erasure seam over every retrieval
    /// index the deployment composed (vector store + sparse index +
    /// embedding cache). Deletion paths route through this instead of
    /// looping `VectorStore.DeleteChunk` so a deleted document cannot
    /// keep surfacing through the hybrid sparse leg. `None` only when
    /// no vector store is wired at all (test harnesses bypassing
    /// `composeWithRAG`).
    IndexLifecycle: ToolUp.Platform.Infra.IIndexLifecycle.IIndexLifecycle option
    EventStore: IEventStore option
    /// The composed `IEmbeddingProvider`, carried solely so
    /// `resetIndex` can drop the scope's embedder state alongside its
    /// chunks. Read through `ScopedEmbedding.resetScope`, which is a
    /// no-op unless the provider is scope-keyed
    /// (`IScopedEmbeddingProviderFactory`), so a deployment on a
    /// stateless embedder — or on the unscoped local one, whose single
    /// global vocabulary must NOT be wiped by one tenant's reset — takes
    /// exactly the pre-14z path (GP 11).
    ///
    /// `None` when no embedder is registered (KB composed without RAG,
    /// test harnesses bypassing `composeWithRAG`).
    EmbeddingProvider: IEmbeddingProvider option
    /// Optional `INarrativeStore` for cross-store reset coherence
    /// . Resolved from DI per request; `None` when
    /// the SDK has not registered an `INarrativeStore` (test harnesses
    /// bypassing the standard compose). Used by `resetIndex` to wipe
    /// persisted narrative entries for the caller's scope alongside
    /// KB blobs and vector chunks — without this the user resets their
    /// KB and `list_narratives` still returns prior entries.
    NarrativeStore: INarrativeStore option
    AccessContext: AccessContext
    /// Per-`KnowledgeSource` original-document resolution.
    /// Resolved from DI when a deployment registered a custom resolver
    /// via `withOriginalSourceResolver`; falls back to the default
    /// (UploadedFile → raw blob, Note → note.md, narrative → `None`).
    OriginalResolver: KnowledgeBase.ServerOriginalSourceResolver.IOriginalSourceResolver
    /// Optional `IAuditLog` for the Phase 107 original-document access
    /// audit (`KnowledgeOriginalRetrieved` + denial events). `None`
    /// when the audit-log substrate isn't registered (test bypass,
    /// `FeatureMode.Disabled` deployment) — emission is best-effort and silent
    /// when the substrate is absent, same posture as the Platform KB
    /// admin surface.
    AuditLog: IAuditLog
    /// Records ingestion-queue enqueue outcomes against the
    /// per-deployment `IRagTelemetry` snapshot. No-op when no queue
    /// or no telemetry sink is registered.
    RecordEnqueue: bool -> unit
    /// Publishes a fresh `InventorySummary` to the user's notification
    /// scope. Called after every inventory mutation.
    PublishInventory: unit -> Async<unit>
    /// Owner / Admin gate for AI-context writes. Returns `Ok ()` for
    /// non-team modes and for users who can write team config; returns
    /// `Error <reason>` otherwise. Reused by the destructive document /
    /// index handlers (`deleteDocument` / `resetIndex`) so a raw API call
    /// can't wipe a team's KB without owner/admin rights — repairing the
    /// asymmetry where only `SetAIContext` was gated.
    EnsureContextWriteAllowed: unit -> Async<Result<unit, string>>
    /// `true` when the request carried a middleware-resolved
    /// the principal's `StorageScope`; `false` when `resolve` synthesised a
    /// fallback scope. The destructive handlers fail closed when this is
    /// `false` and `UserId` is the literal `"anonymous"` (the shared
    /// `user-anonymous` collapse that happens when ScopeResolutionMiddleware
    /// isn't wired for the path) rather than mutate a container shared
    /// across every unscoped caller. Benign per-user fallbacks (a real
    /// user id, no `StorageScope`) keep `false` here but stay tenant-
    /// isolated, so they pass the guard.
    ScopeResolvedFromRequest: bool
    /// Every compose-time policy the deployment set through the
    /// `KnowledgeBase.Server.with*` builders, as one record;
    /// `KnowledgePolicies.defaults` when it set none.
    Policies: KnowledgePolicies
    /// The versioned object store KB originals are retained in, present
    /// **only** when `Policies.ObjectRetention` is on. A store is registered
    /// for every composed deployment, so `None` means the deployment did not
    /// opt in, and every original-bytes path then takes the
    /// `knowledge/{docId}/{fileName}` convention branch with no store call at
    /// all (GP 11 / GP 13).
    DataObjectStore: IDataObjectStore option
    /// The composed content scanner, registered by `withContentScanning`.
    /// `None` when the deployment composed none: the upload path skips the
    /// scan branch entirely, so no digest is computed and no audit row is
    /// written (GP 11 / GP 13). `Policies.Scan` says what an unavailable
    /// scanner means.
    ContentScanner: IContentScanner option
    /// The fact-disclosure egress gate, registered in DI by
    /// the fact companion's compose whenever the fact store is enabled.
    /// `ingestNarrative` refuses to commit a narrative whose Metric spans
    /// reference facts the gate denies at the `FactNarrativePublication`
    /// surface. `None` (no fact store composed) ⇒ the commit path is
    /// byte-identical (GP 13). The gate judges in the resolved scope
    /// paired with it — the request's, or the one a server-side producer
    /// committed under — never one derived from a string.
    DisclosureGate: (IFactDisclosureGate * ResolvedScope) option
    /// The transport behind an allowlisted URL fetch, registered alongside
    /// the allowlist by `withUrlIngestion` (or replaced by
    /// `withUrlContentFetcher`). `None` when URL ingestion was never
    /// composed: the gate refuses before a transport is consulted (GP 13).
    UrlFetcher: KnowledgeBase.ServerBulkImport.IUrlContentFetcher option
}

module KnowledgeApiDeps =

    /// Resolve all DI values + build the four local closures, given the
    /// service provider and the caller's already-extracted identity.
    ///
    /// The request-free core `resolve` now delegates to. Every
    /// dependency this record holds comes from DI or from the two identity
    /// values below, so the only thing an `HttpContext` was ever supplying
    /// was those three: the provider, the resolved `StorageScope`, and the
    /// user id. Naming that split is what lets a server-side producer with
    /// no request (`INarrativeIngestor`) reach the SAME ingestion path
    /// rather than a parallel one — the alternative, a second deps
    /// construction beside this one, is how two paths that must agree stop
    /// agreeing.
    ///
    /// `resolvedScope` is `Some` when the caller genuinely resolved a scope
    /// (middleware, or a programmatic caller handing one over) and `None`
    /// when it did not — the distinction the destructive handlers fail
    /// closed on (`ScopeResolvedFromRequest`). The fallback synthesis and
    /// its loud logging are unchanged.
    let resolveFrom
        (services: IServiceProvider)
        (factScope: ResolvedScope)
        (resolvedScope: StorageScope option)
        (userId: string)
        : KnowledgeApiDeps =
        let storage =
            ServiceComposition.tryService<IBlobStorage> services
            |> Option.defaultWith (fun () ->
                invalidOp
                    "KnowledgeApiDeps: no IBlobStorage is registered. The ServerApp composition registers one unconditionally; a host that bypasses it must register one.")

        let queue = ServiceComposition.tryService<IngestionQueue> services

        // Telemetry sink resolved per-request so KB enqueue paths feed the
        // same `/health/rag` snapshot as the post-save vectorisation hook.
        // A no-op when `composeWithRAG` hasn't registered one.
        let ragTelemetry =
            ServiceComposition.tryService<ToolUp.Platform.Infra.IRagTelemetry.IRagTelemetry> services
            |> Option.defaultWith ToolUp.RAG.RagTelemetry.createNoOp

        let recordEnqueue (accepted: bool) =
            queue
            |> Option.iter (fun q -> ragTelemetry.RecordEnqueue(q.Count, q.Capacity, accepted))

        // Document-understanding providers: `composeWithRAG` registers no-op
        // defaults and a companion's own provider wins.
        let ocrProvider =
            ServiceComposition.tryService<IOcrProvider> services
            |> Option.defaultWith ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider

        let tableExtractor =
            ServiceComposition.tryService<ITableExtractor> services
            |> Option.defaultWith ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor

        // The base composition registers a channel unconditionally (a no-op
        // one when notifications are off), so an absent one is a
        // composition fault, refused here rather than carried as a null.
        let notifications =
            ServiceComposition.tryService<INotificationChannel> services
            |> Option.defaultWith (fun () ->
                invalidOp
                    "KnowledgeApiDeps: no INotificationChannel is registered. The ServerApp composition registers one unconditionally (a no-op channel when notifications are off); a host that bypasses it must register one.")

        let logger =
            ServiceComposition.tryService<ILogger> services
            |> Option.defaultValue (
                { new ILogger with
                    member _.Debug _ = ()
                    member _.Info _ = ()
                    member _.Warn _ = ()
                    member _.Error(_, _) = ()
                }
            )

        // Scope resolution. A middleware-resolved the principal's `StorageScope`
        // is the authoritative tenant boundary (GP 4). When it's absent
        // `resolve` synthesises a fallback — per-user when the principal's caller id (`AccessContext.callerId`)
        // is present, or the shared `user-anonymous` container when neither
        // item is set. The shared collapse means ScopeResolutionMiddleware
        // isn't wired for this path and every unscoped caller lands in one
        // KB scope: surfaced loudly here, and the destructive handlers fail
        // closed on it (see `ScopeResolvedFromRequest`).
        let scopeResolvedFromRequest = Option.isSome resolvedScope

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
                    Container = ScopeKey.container (ScopeKey.User(UserId userId))
                    Persist = true
                }

        // Vector scope mirrors the blob/index container's tenant boundary
        // (GP 4). A `team-{id}` container maps to the team-shared vector
        // scope; a `user-{id}` container (Individual mode,
        // AuthenticatedEphemeral, anonymous session) maps to that user's
        // *own* vector scope. Routing per-user uploads to the shared
        // `Deployment` scope was GAP-1 — every non-team caller's chunks
        // collapsed into one namespace that retrieval served to all
        // authenticated users. `Deployment` is now reserved for genuinely
        // deployment-wide shared content only.
        let vectorScope = VectorScope.ofContainer scope

        let vectorStore = ServiceComposition.tryService<IVectorStore> services

        // The composed lifecycle seam wins (it fans out across vector store +
        // sparse index + embedding cache). A deployment with a vector store
        // but no registered seam gets a vector-only wrapper, so the handlers
        // have exactly one deletion path.
        let indexLifecycle =
            match ServiceComposition.tryService<ToolUp.Platform.Infra.IIndexLifecycle.IIndexLifecycle> services with
            | Some lifecycle -> Some lifecycle
            | None ->
                vectorStore
                |> Option.map (fun vs ->
                    ToolUp.Platform.Infra.IIndexLifecycle.DefaultIndexLifecycle(vs, None, None, logger)
                    :> ToolUp.Platform.Infra.IIndexLifecycle.IIndexLifecycle)

        let policies =
            ServiceComposition.tryService<KnowledgePolicies> services
            |> Option.defaultValue KnowledgePolicies.defaults

        // The POLICY is the gate, not the presence of a store: one is
        // registered for every composed deployment, so probing DI alone would
        // silently move every existing deployment's originals on upgrade.
        let dataObjectStore =
            if not policies.ObjectRetention then
                None
            else
                match ServiceComposition.tryService<IDataObjectStore> services with
                | Some store -> Some store
                | None ->
                    logger.Warn(
                        "[KnowledgeBase] withObjectStoreRetention is composed but no IDataObjectStore is registered; originals fall back to the knowledge/{docId}/{fileName} blob convention."
                    )

                    None

        let accessContext =
            ServiceComposition.tryService<AccessContext> services
            |> Option.defaultWith (fun () -> AccessContext.unresolved "Deps")

        let publishInventory () =
            publishInventoryUpdate storage notifications logger userId scope.Container

        let ensureContextWriteAllowed () : Async<Result<unit, string>> = async {
            match accessContext.Subject with
            | TeamMember(userId, teamId) ->
                match ServiceComposition.tryService<ITeamStore> services with
                | Some ts ->
                    let! role = ts.GetMemberRole(TeamId teamId, UserId userId)

                    match role with
                    | Some r when TeamRoles.canWriteTeamConfig r -> return Ok()
                    | Some r ->
                        return
                            Error
                                $"Only team owners and admins can edit standing AI context. Your role: {TeamRoles.displayName r}."
                    | None -> return Error "You are not a member of this team."
                | None -> return Error "Team management is not available in this deployment."
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
            UserId = UserId userId
            VectorScope = vectorScope
            VectorStore = vectorStore
            IndexLifecycle = indexLifecycle
            EventStore = ServiceComposition.tryService<IEventStore> services
            EmbeddingProvider = ServiceComposition.tryService<IEmbeddingProvider> services
            NarrativeStore = ServiceComposition.tryService<INarrativeStore> services
            AccessContext = accessContext
            OriginalResolver =
                ServiceComposition.tryService<KnowledgeBase.ServerOriginalSourceResolver.IOriginalSourceResolver>
                    services
                |> Option.defaultWith KnowledgeBase.ServerOriginalSourceResolver.createDefault
            AuditLog = ToolUp.Platform.Config.Background.resolveAuditLog services
            RecordEnqueue = recordEnqueue
            PublishInventory = publishInventory
            EnsureContextWriteAllowed = ensureContextWriteAllowed
            ScopeResolvedFromRequest = scopeResolvedFromRequest
            Policies = policies
            DataObjectStore = dataObjectStore
            ContentScanner = ServiceComposition.tryService<IContentScanner> services
            DisclosureGate =
                ServiceComposition.tryService<IFactDisclosureGate> services
                |> Option.map (fun gate -> gate, factScope)
            UrlFetcher = ServiceComposition.tryService<KnowledgeBase.ServerBulkImport.IUrlContentFetcher> services
        }

    /// Resolve all DI values + build the four local closures for the
    /// current request. Mirrors the original `knowledgeApi` prelude
    /// exactly — no behaviour change. Extracts the three request-borne
    /// values `resolveFrom` needs and does nothing else.
    let resolve (ctx: HttpContext) : KnowledgeApiDeps =
        let resolvedScope =
            match AccessContext.scopeOf ctx.Items with
            | Some s -> Some s
            | _ -> None

        let userId =
            match AccessContext.callerIdOf ctx.Items with
            | Some id -> id
            | _ -> "anonymous"

        resolveFrom ctx.RequestServices (StorageScopeResolver.ScopeResolution.forRequest ctx) resolvedScope userId

    /// Fail-closed guard for destructive KB operations. Returns `Error`
    /// when scope resolution collapsed onto the shared `user-anonymous`
    /// container (ScopeResolutionMiddleware unwired — neither
    /// the principal's `StorageScope` nor the principal's caller id (`AccessContext.callerId`) present on the request),
    /// so a raw API call cannot wipe a container shared across every
    /// unscoped caller. Benign per-user fallbacks (a real user id, no
    /// `StorageScope`) pass — they stay tenant-isolated. `resolve`
    /// already logged the collapse loudly; this is the operation-level
    /// refusal.
    let guardResolvedScope (deps: KnowledgeApiDeps) : Result<unit, string> =
        if not deps.ScopeResolvedFromRequest && deps.UserId = UserId "anonymous" then
            Error
                "Storage scope is unresolved for this request (ScopeResolutionMiddleware not wired); refusing the destructive operation to avoid mutating the shared anonymous container."
        else
            Ok()

/// Why the ingestion queue refused an attempt.
type EnqueueRefused =
    /// The queue was at capacity.
    | QueueFull of count: int * capacity: int

/// Companion functions for `EnqueueRefused`.
module EnqueueRefused =

    /// The sentence a caller and the document's `Failed` status read.
    let describe (refused: EnqueueRefused) : string =
        match refused with
        | QueueFull(count, capacity) ->
            sprintf "Knowledge-base ingestion queue is full (%d/%d). Try again in a few seconds." count capacity

/// The knowledge base's one way into ingestion, and its one way to settle a
/// document no chunk callback will settle.
module KnowledgeIngest =

    /// The attempt store over the request's storage, announcing through its
    /// notification channel.
    let attempts (deps: KnowledgeApiDeps) : KnowledgeAttemptStore =
        KnowledgeAttemptStore(deps.Storage, Some deps.Notifications, deps.Logger)

    /// Settle `docId` in the request's scope at `status` and announce it to
    /// the requesting user.
    let settle (deps: KnowledgeApiDeps) (docId: string) (status: IngestionStatus) : Async<unit> =
        (attempts deps).Settle(deps.Scope.Container, docId, deps.UserId.Value, status)

    /// Enqueue `chunkPairs` of `doc` as a new ingestion attempt: begin the
    /// attempt (seeding `Embedding(0, n)` and the document's chunk count from
    /// `doc.ChunkCount`), enqueue the job carrying it, record the enqueue, and
    /// on a refusal settle the document `Failed` with the refusal's sentence.
    /// `Ok None` when there is nothing to enqueue: no chunk, or no queue
    /// composed.
    let enqueue
        (deps: KnowledgeApiDeps)
        (doc: KnowledgeDocument)
        (chunkPairs: (string * TextChunk) list)
        : Async<Result<IngestionAttempt option, EnqueueRefused>> =
        async {
            match deps.Queue with
            | None -> return Ok None
            | Some _ when List.isEmpty chunkPairs -> return Ok None
            | Some queue ->
                let attempt: IngestionAttempt = {
                    AttemptId = Guid.NewGuid().ToString("N")
                    EnqueuedChunks = chunkPairs.Length
                }

                do!
                    (attempts deps)
                        .Begin(
                            deps.Scope.Container,
                            doc.Id,
                            attempt.AttemptId,
                            attempt.EnqueuedChunks,
                            Some doc.ChunkCount
                        )

                let job: DocumentIngestionJob = {
                    DocumentId = doc.Id
                    DocumentName = doc.FileName
                    Chunks = chunkPairs
                    Scope = deps.VectorScope
                    ScopeId = deps.Scope.ScopeId
                    Container = deps.Scope.Container
                    OriginatingUserId = Some deps.UserId
                    Attempt = Some attempt
                }

                let! accepted = queue.EnqueueAsync(job)
                deps.RecordEnqueue accepted

                if accepted then
                    return Ok(Some attempt)
                else
                    let refused = QueueFull(queue.Count, queue.Capacity)
                    do! settle deps doc.Id (Failed(EnqueueRefused.describe refused))
                    return Error refused
        }
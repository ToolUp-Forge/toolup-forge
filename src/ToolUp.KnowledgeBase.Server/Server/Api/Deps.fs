module KnowledgeBase.ServerApiDeps

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IEmbeddingProvider
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
    /// Phase 14z — the composed `IEmbeddingProvider`, carried solely so
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
    /// Phase 510 — compose-time upload versioning, registered by
    /// `withDocumentVersioning`; `KnowledgeVersioningPolicy.disabled`
    /// when absent, which is what every deployment that has not opted in
    /// gets. `uploadDocument` reads it before looking for a predecessor,
    /// so a deployment without it takes no extra index read at all
    /// (GP 11 / GP 13).
    VersioningPolicy: KnowledgeVersioningPolicy
    /// Phase 105 — the versioned object store KB originals are retained
    /// in, present **only** when the deployment composed
    /// `withObjectStoreRetention true`.
    ///
    /// `None` is not "no store is registered" — one always is, for every
    /// composed deployment (`ComposeRuntimeServices`). It means the
    /// deployment did not opt in, and every original-bytes path then
    /// takes the pre-105 `knowledge/{docId}/{fileName}` convention
    /// branch with no store call at all (GP 11 / GP 13). Resolving the
    /// singleton behind the policy rather than probing for it is what
    /// makes that guarantee structural: there is no path from an
    /// un-opted-in deployment to a store write.
    DataObjectStore: IDataObjectStore option
    /// Phase 512 — compose-time per-scope corpus quota, registered by
    /// `withKnowledgeQuota`; `KnowledgeQuotaPolicy.unlimited` when absent
    /// (no tally taken, no upload refusable — pre-512 behaviour, GP 11).
    /// Enforced by `uploadDocument` and projected by `getScopeUsage`.
    QuotaPolicy: KnowledgeQuotaPolicy
    /// Phase 512 — compose-time age-based retention, registered by
    /// `withKnowledgeRetention`; `KnowledgeRetentionPolicy.retainForever`
    /// when absent. Carried on `deps` for read paths that want to report
    /// the policy; the sweep itself runs off the request path in
    /// `Server/RetentionSweep.fs` on the composed `IJobScheduler`.
    RetentionPolicy: KnowledgeRetentionPolicy
    /// Phase 515 — the composed content scanner, registered by
    /// `withContentScanning`. `None` when the deployment composed none,
    /// which is a stronger guarantee than substituting
    /// `AllowAllContentScanner`: the upload path skips the scan branch
    /// entirely, so no digest is computed and no audit row is written —
    /// the pre-515 path byte-for-byte (GP 11 / GP 13).
    ContentScanner: IContentScanner option
    /// Phase 515 — what the upload boundary does with a
    /// `ScanUnavailable` verdict. `ContentScanPolicy.defaults`
    /// (fail-closed) when absent; unreachable on a deployment with no
    /// scanner, because a deployment with none never scans at all.
    ScanPolicy: ContentScanPolicy
    /// Phase 525.D — the fact-disclosure egress gate, registered in DI by
    /// the fact companion's compose whenever the fact store is enabled.
    /// `ingestNarrative` refuses to commit a narrative whose Metric spans
    /// reference facts the gate denies at the `FactNarrativePublication`
    /// surface. `None` (no fact store composed) ⇒ the commit path is
    /// byte-identical (GP 13).
    DisclosureGate: IFactDisclosureGate option
    /// Phase 511 — archive-expansion resource guards for `importBatch`,
    /// registered by `withArchiveImportPolicy`. `ArchiveImportPolicy.defaults`
    /// when absent — the one KB policy whose uncomposed default is NOT
    /// permissive, because an unguarded archive expander is not a
    /// defensible default for a new surface. Read only by `importBatch`;
    /// the single-file upload path never sees it (GP 11).
    ArchiveImportPolicy: ArchiveImportPolicy
    /// Phase 511 — the URL-ingestion host allowlist, registered by
    /// `withUrlIngestion`. `UrlIngestionPolicy.disabled` when absent,
    /// whose allowlist is empty, so no URL is fetchable at all: URL
    /// ingestion is inert unless a deployment names a host.
    UrlIngestionPolicy: UrlIngestionPolicy
    /// Phase 511 — the transport behind an allowlisted URL fetch,
    /// registered alongside the policy by `withUrlIngestion` (or replaced
    /// by `withUrlContentFetcher`). `None` when URL ingestion was never
    /// composed — which is redundant with the empty allowlist above, and
    /// deliberately so: the gate refuses before a transport is consulted,
    /// and no transport exists to consult (GP 13).
    UrlFetcher: KnowledgeBase.ServerBulkImport.IUrlContentFetcher option
}

module KnowledgeApiDeps =

    /// Resolve all DI values + build the four local closures, given the
    /// service provider and the caller's already-extracted identity.
    ///
    /// Phase 707 — the request-free core `resolve` now delegates to. Every
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
        (resolvedScope: StorageScope option)
        (userId: string)
        : KnowledgeApiDeps =
        let storage = services.GetService(typeof<IBlobStorage>) :?> IBlobStorage

        let queue = services.GetService(typeof<IngestionQueue>) :?> IngestionQueue

        // Telemetry sink resolved per-request so KB enqueue paths feed the
        // same `/health/rag` snapshot as the post-save vectorisation hook.
        // Falls back to a no-op when `composeWithRAG` hasn't registered one
        // (deployments using KB without RAG, or test harnesses).
        let ragTelemetry =
            match services.GetService(typeof<ToolUp.Platform.IRagTelemetry.IRagTelemetry>) with
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
            match services.GetService(typeof<IOcrProvider>) with
            | :? IOcrProvider as p -> p
            | _ -> ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()

        let tableExtractor =
            match services.GetService(typeof<ITableExtractor>) with
            | :? ITableExtractor as t -> t
            | _ -> ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()

        let notifications =
            services.GetService(typeof<INotificationChannel>) :?> INotificationChannel

        let logger =
            match services.GetService(typeof<ILogger>) with
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
                    Container = sprintf "user-%s" userId
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
        let vectorScope =
            if scope.Container.StartsWith "team-" then
                Team scope.ScopeId
            else
                User scope.ScopeId

        // Vector store and event store are resolved lazily — only the Notes
        // and AI-context paths need them, and tests bypassing `composeWithRAG`
        // may not have them registered. `box _ <> null` guards each use.
        let vectorStore =
            match services.GetService(typeof<IVectorStore>) with
            | :? IVectorStore as v -> Some v
            | _ -> None

        // Phase 115 — the composed lifecycle seam wins (it fans out
        // across vector store + sparse index + embedding cache).
        // Deployments with a vector store but no registered seam
        // (KB-without-RAG composition, older composition roots) get a
        // vector-only wrapper so the handlers have exactly one deletion
        // path — never the raw `vs.DeleteChunk` loop.
        let indexLifecycle =
            match services.GetService(typeof<ToolUp.Platform.IIndexLifecycle.IIndexLifecycle>) with
            | :? ToolUp.Platform.IIndexLifecycle.IIndexLifecycle as il -> Some il
            | _ ->
                vectorStore
                |> Option.map (fun vs ->
                    ToolUp.Platform.IIndexLifecycle.DefaultIndexLifecycle(vs, None, None, logger)
                    :> ToolUp.Platform.IIndexLifecycle.IIndexLifecycle)

        let eventStore =
            match services.GetService(typeof<IEventStore>) with
            | :? IEventStore as e -> Some e
            | _ -> None

        // Phase 14z — `composeWithRAG` registers the (cache-wrapped)
        // embedder as an `IEmbeddingProvider` singleton; the wrapper
        // forwards the scope-keyed capability, so probing the resolved
        // singleton is equivalent to probing what the operator composed.
        let embeddingProvider =
            match services.GetService(typeof<IEmbeddingProvider>) with
            | :? IEmbeddingProvider as e -> Some e
            | _ -> None

        let narrativeStore =
            match services.GetService(typeof<INarrativeStore>) with
            | :? INarrativeStore as n -> Some n
            | _ -> None

        // Original-source resolver (Phase 104). Same probe-with-default
        // shape as the OCR / table-extractor providers above: a custom
        // resolver registered before compose wins; otherwise the
        // per-`KnowledgeSource` default applies.
        let originalResolver =
            match services.GetService(typeof<KnowledgeBase.ServerOriginalSourceResolver.IOriginalSourceResolver>) with
            | :? KnowledgeBase.ServerOriginalSourceResolver.IOriginalSourceResolver as r -> r
            | _ -> KnowledgeBase.ServerOriginalSourceResolver.createDefault ()

        let auditLog =
            match services.GetService(typeof<IAuditLog>) with
            | :? IAuditLog as a -> Some a
            | _ -> None

        // Phase 119 — upload policy registered by `withUploadPolicy`;
        // the permissive default when absent (no caps, pre-119 behaviour).
        let uploadPolicy =
            match services.GetService(typeof<KnowledgeUploadPolicy>) with
            | :? KnowledgeUploadPolicy as p -> p
            | _ -> KnowledgeUploadPolicy.permissive

        // Phase 14x — dedup policy registered by `withDocumentDedup`;
        // dedup enabled when absent.
        let dedupPolicy =
            match services.GetService(typeof<KnowledgeDedupPolicy>) with
            | :? KnowledgeDedupPolicy as p -> p
            | _ -> KnowledgeDedupPolicy.enabled

        // Phase 510 — versioning policy registered by
        // `withDocumentVersioning`; OFF when absent (pre-510 behaviour).
        let versioningPolicy =
            match services.GetService(typeof<KnowledgeVersioningPolicy>) with
            | :? KnowledgeVersioningPolicy as p -> p
            | _ -> KnowledgeVersioningPolicy.disabled

        // Phase 105 — original retention in the versioned object store.
        // The POLICY is the gate, not the presence of a store: one is
        // registered for every composed deployment, so probing DI would
        // silently move every existing deployment's originals on
        // upgrade. Un-opted-in ⇒ `None` ⇒ no store call on any path.
        let objectRetentionPolicy =
            match services.GetService(typeof<KnowledgeObjectRetentionPolicy>) with
            | :? KnowledgeObjectRetentionPolicy as p -> p
            | _ -> KnowledgeObjectRetentionPolicy.disabled

        let dataObjectStore =
            if not objectRetentionPolicy.RetainOriginalsInObjectStore then
                None
            else
                match services.GetService(typeof<IDataObjectStore>) with
                | :? IDataObjectStore as s -> Some s
                | _ ->
                    logger.Warn(
                        "[KnowledgeBase] withObjectStoreRetention is composed but no IDataObjectStore is registered; originals fall back to the knowledge/{docId}/{fileName} blob convention."
                    )

                    None

        // Phase 512 — corpus quota + retention policies registered by
        // `withKnowledgeQuota` / `withKnowledgeRetention`; the unlimited /
        // retain-forever defaults when absent.
        let quotaPolicy =
            match services.GetService(typeof<KnowledgeQuotaPolicy>) with
            | :? KnowledgeQuotaPolicy as p -> p
            | _ -> KnowledgeQuotaPolicy.unlimited

        let retentionPolicy =
            match services.GetService(typeof<KnowledgeRetentionPolicy>) with
            | :? KnowledgeRetentionPolicy as p -> p
            | _ -> KnowledgeRetentionPolicy.retainForever

        // Phase 515 — the composed content scanner + its error policy.
        // `None` rather than a substituted no-op: a deployment that never
        // called `withContentScanning` has no scanner singleton at all,
        // and the upload path skips the whole branch (GP 13).
        let contentScanner =
            match services.GetService(typeof<IContentScanner>) with
            | :? IContentScanner as s -> Some s
            | _ -> None

        let scanPolicy =
            match services.GetService(typeof<ContentScanPolicy>) with
            | :? ContentScanPolicy as p -> p
            | _ -> ContentScanPolicy.defaults

        // Phase 525.D — the fact-disclosure egress gate, present exactly
        // when the fact companion's compose registered the fact store.
        let disclosureGate =
            match services.GetService(typeof<IFactDisclosureGate>) with
            | :? IFactDisclosureGate as g -> Some g
            | _ -> None

        // Phase 511 — bulk-import policies. The archive guards default to
        // `ArchiveImportPolicy.defaults` (real caps, not permissive — see
        // the type); URL ingestion defaults to an EMPTY allowlist, so an
        // uncomposed deployment cannot fetch anything and no transport is
        // resolved for it either.
        let archiveImportPolicy =
            match services.GetService(typeof<ArchiveImportPolicy>) with
            | :? ArchiveImportPolicy as p -> p
            | _ -> ArchiveImportPolicy.defaults

        let urlIngestionPolicy =
            match services.GetService(typeof<UrlIngestionPolicy>) with
            | :? UrlIngestionPolicy as p -> p
            | _ -> UrlIngestionPolicy.disabled

        let urlFetcher =
            match services.GetService(typeof<KnowledgeBase.ServerBulkImport.IUrlContentFetcher>) with
            | :? KnowledgeBase.ServerBulkImport.IUrlContentFetcher as f -> Some f
            | _ -> None

        let accessContext =
            match services.GetService(typeof<AccessContext>) with
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
                match services.GetService(typeof<ITeamStore>) with
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
            EmbeddingProvider = embeddingProvider
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
            VersioningPolicy = versioningPolicy
            DataObjectStore = dataObjectStore
            QuotaPolicy = quotaPolicy
            RetentionPolicy = retentionPolicy
            ContentScanner = contentScanner
            ScanPolicy = scanPolicy
            DisclosureGate = disclosureGate
            ArchiveImportPolicy = archiveImportPolicy
            UrlIngestionPolicy = urlIngestionPolicy
            UrlFetcher = urlFetcher
        }

    /// Resolve all DI values + build the four local closures for the
    /// current request. Mirrors the original `knowledgeApi` prelude
    /// exactly — no behaviour change. Extracts the three request-borne
    /// values `resolveFrom` needs and does nothing else.
    let resolve (ctx: HttpContext) : KnowledgeApiDeps =
        let resolvedScope =
            match ctx.Items.TryGetValue "ToolUp.StorageScope" with
            | true, (:? StorageScope as s) -> Some s
            | _ -> None

        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        resolveFrom ctx.RequestServices resolvedScope userId

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
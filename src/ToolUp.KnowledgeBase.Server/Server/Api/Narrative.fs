module KnowledgeBase.ServerApiNarrative

open System
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Narrative
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerExtractors
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps

/// Phase 525.D — the narrative-commit egress door. When the fact
/// companion's disclosure gate is composed, a document whose Metric spans
/// reference facts the gate denies at the `FactNarrativePublication`
/// surface is refused commit to the (surfaceable) knowledge base — the
/// chunks would otherwise carry the denied values into retrieval for the
/// whole scope. Returns `Some diagnostic` naming the offending refs and
/// their policies (never the values); `None` when no gate is composed,
/// the document cites no facts, or everything is disclosable (GP 13 /
/// plan D17 — a deployment without classified facts is byte-identical).
/// Each deny is audited by the gate itself (GP 6).
let private disclosureRefusalFor (deps: KnowledgeApiDeps) (document: NarrativeDocument) : Async<string option> = async {
    match deps.DisclosureGate with
    | None -> return None
    | Some gate ->
        match NarrativeFacts.factRefs document |> Set.toList with
        | [] -> return None
        | refs ->
            let! verdicts = gate.Check(deps.Scope.ScopeId, deps.UserId, FactNarrativePublication, refs)

            let offending =
                verdicts
                |> Map.toList
                |> List.choose (fun (factId, verdict) ->
                    match verdict with
                    | FactNotDisclosable policyRef -> Some(sprintf "%s (policy %s)" factId policyRef)
                    | FactDisclosable -> None)

            match offending with
            | [] -> return None
            | offending ->
                return
                    Some(
                        sprintf
                            "Narrative commit refused: %d referenced fact(s) are not disclosable — %s. Remove or replace the offending metric spans, or have the facts reclassified, then commit again."
                            offending.Length
                            (String.concat "; " offending)
                    )
}

let ingestNarrative
    (deps: KnowledgeApiDeps)
    (request: IngestNarrativeRequest)
    : Async<Result<KnowledgeDocument, IngestNarrativeError>> =
    async {
        // Phase 525.D — disclosure egress check before anything is
        // persisted; the refusal diagnostic rides the existing
        // `IngestFailed` wire case (additive — no client change).
        let! disclosureDiagnostic = disclosureRefusalFor deps request.Document

        match request.Document.Provenance, disclosureDiagnostic with
        | None, _ -> return Error MissingProvenance
        | _, Some diagnostic -> return Error(IngestFailed diagnostic)
        | Some prov, None ->
            let! existing = loadIndex deps.Storage deps.Scope.Container

            let duplicate =
                existing
                |> List.tryFind (fun d ->
                    match d.Source with
                    | FromNarrative n -> n.ModuleId = prov.ModuleId && n.SettingsKey = prov.SettingsKey
                    | UploadedFile
                    | Note _ -> false)

            match duplicate, request.Overwrite with
            | Some existingDoc, false -> return Error(DuplicateExists existingDoc)
            | _ ->
                // Use existing docId on overwrite so vector-store upserts replace by
                // chunkId; mint a new id otherwise.
                let docId =
                    match duplicate with
                    | Some d -> d.Id
                    | None -> Guid.NewGuid().ToString()

                let fileName =
                    let safeTitle =
                        request.Document.Title
                        |> String.map (fun c ->
                            if Char.IsLetterOrDigit c || c = '-' || c = ' ' then
                                c
                            else
                                '-')

                    sprintf "%s.narrative.md" safeTitle

                // One chunk per section so retrieval can cite the section
                // that contributed — matches the per-page chunking we use
                // for uploaded documents.
                let chunks =
                    request.Document.Sections
                    |> List.mapi (fun i section ->
                        let sectionDoc: NarrativeDocument = {
                            Title = request.Document.Title
                            Subtitle = request.Document.Subtitle
                            Sections = [ section ]
                            Provenance = request.Document.Provenance
                            Lang = request.Document.Lang
                            CanonicalUrl = request.Document.CanonicalUrl
                        }

                        let body = NarrativeMarkdown.render sectionDoc

                        let src: SourceReference = {
                            DocumentId = docId
                            DocumentName = fileName
                            FileType = "narrative"
                            Location = Section section.Heading
                            IndexedAt = DateTimeOffset.UtcNow
                        }

                        let header = sprintf "Section %d · %s" (i + 1) section.Heading
                        let chunk = makeChunk Narrative fileName header body src

                        // Phase 521.D — record the fact ids this section's
                        // metric spans reference in chunk metadata, so a
                        // retrieval of the narrative chunk can surface its
                        // underlying facts (transitive citation) and the
                        // fact→narrative join (Phase 522.E) can prefer this
                        // chunk when one of those facts is resolved. Absent
                        // any reference the chunk is stamped as before.
                        match NarrativeFacts.factRefsInSection section |> Set.toList with
                        | [] -> chunk
                        | refs -> {
                            chunk with
                                Metadata = chunk.Metadata.Add(ChunkMetadata.FactRefsKey, String.concat "," refs)
                          })

                let fullMarkdown = NarrativeMarkdown.render request.Document
                let sizeBytes = int64 (Encoding.UTF8.GetByteCount fullMarkdown)

                let doc: KnowledgeDocument = {
                    Id = docId
                    FileName = fileName
                    FileType = "narrative"
                    UploadedAt = DateTimeOffset.UtcNow
                    UploadedBy = deps.UserId
                    Status = Queued
                    SizeBytes = sizeBytes
                    ChunkCount = chunks.Length
                    Source =
                        FromNarrative {
                            ModuleId = prov.ModuleId
                            PageRoute = prov.PageRoute
                            SettingsKey = prov.SettingsKey
                            SettingsDisplay = prov.SettingsDisplay
                            GeneratedAt = prov.GeneratedAt
                        }
                    // Phase 14x — narratives dedup by provenance
                    // (`DuplicateExists` + `Overwrite`), not content hash.
                    ContentHash = None
                }

                // Vector-chunk cleanup on overwrite. When the regenerated
                // narrative has fewer sections than the prior version,
                // trailing chunks (indices chunks.Length..oldN-1) would
                // orphan in the vector store — the re-ingestion's
                // Upsert paths cover 0..N-1 but leave the tail untouched,
                // so RAG retrieval keeps surfacing stale section content
                // under the live document id. Delete the tail now while
                // we still know the old chunk count from the prior
                // KnowledgeDocument; falls through silently when no
                // vector store is wired (test harnesses) or when the
                // new section count meets or exceeds the old.
                //
                // 0.4.3 — per-chunk DeleteChunk failures are caught,
                // counted, and surfaced as a single summary log line.
                // Previously they were swallowed silently; a partial
                // failure left orphans in the store and RAG kept
                // surfacing them under the live document id with no
                // operator signal.
                //
                // Phase 115 — the tail delete routes through the unified
                // lifecycle seam so the sparse index sheds the stale
                // sections too (pre-115 the `vs.DeleteChunk` loop left
                // them retrievable through the hybrid sparse leg). The
                // seam carries the per-chunk failure isolation + survivor
                // reporting this block used to hand-roll.
                match duplicate, deps.IndexLifecycle with
                | Some existingDoc, Some lifecycle when chunks.Length < existingDoc.ChunkCount ->
                    let mutable orphanIds: string list = []

                    for i in chunks.Length .. existingDoc.ChunkCount - 1 do
                        let chunkId = sprintf "%s:chunk:%d" docId i
                        let! report = lifecycle.DeleteChunk deps.VectorScope chunkId

                        if not (ToolUp.Platform.IIndexLifecycle.IndexLifecycleReport.isClean report) then
                            orphanIds <- chunkId :: orphanIds

                            deps.Logger.Warn(
                                sprintf
                                    "[KnowledgeBase] Orphan-chunk delete failed for %s (docId=%s scope=%s): %s"
                                    chunkId
                                    docId
                                    deps.Scope.ScopeId
                                    (ToolUp.Platform.IIndexLifecycle.IndexLifecycleReport.summarise report)
                            )

                    if not (List.isEmpty orphanIds) then
                        deps.Logger.Warn(
                            sprintf
                                "[KnowledgeBase] %d orphan chunk(s) remain in the retrieval indexes for docId=%s after narrative overwrite; RAG may surface stale section content. Surviving chunk ids: %s"
                                orphanIds.Length
                                docId
                                (orphanIds |> List.rev |> String.concat "; ")
                        )
                | _ -> ()

                // Persist rendered markdown so the document can be re-read
                // even if the embedding/store is rebuilt.
                let rawBlobName = sprintf "knowledge/%s/%s" docId fileName
                let! _ = deps.Storage.Upload(deps.Scope.Container, rawBlobName, Encoding.UTF8.GetBytes fullMarkdown)

                // Phase 116 — atomic index RMW (see `upsertIndexEntry`).
                // Released before the enqueue block below (which can route
                // through `MarkIngestionFailed` → `updateIndexStatus`). The
                // provenance duplicate-check above still reads outside the
                // lock; concurrent same-provenance commits remain a
                // last-writer race (residual, addressed by the deferred
                // ETag CAS), but neither write is lost from the index.
                do! upsertIndexEntry deps.Storage deps.Scope.Container doc

                let mutable returnedDoc = doc

                if box deps.Queue <> null && not chunks.IsEmpty then
                    // Seed before enqueue so an early observer firing isn't
                    // overwritten. Same pattern as the upload path above.
                    let initialStatus = Embedding(0, chunks.Length)

                    statusCache.AddOrUpdate(
                        docId,
                        initialStatus,
                        fun _ existing ->
                            match existing with
                            | Queued -> initialStatus
                            | other -> other
                    )
                    |> ignore

                    let chunkPairs =
                        chunks |> List.mapi (fun i chunk -> sprintf "%s:chunk:%d" docId i, chunk)

                    let job: DocumentIngestionJob = {
                        DocumentId = docId
                        DocumentName = fileName
                        Chunks = chunkPairs
                        Scope = deps.VectorScope
                        ScopeId = deps.Scope.ScopeId
                        Container = deps.Scope.Container
                        OriginatingUserId = Some deps.UserId
                    }

                    let accepted = deps.Queue.Enqueue(job)
                    deps.RecordEnqueue accepted

                    if not accepted then
                        let reason =
                            sprintf
                                "Knowledge-base ingestion queue is full (%d/%d). Try again in a few seconds."
                                deps.Queue.Count
                                deps.Queue.Capacity

                        do! deps.MarkIngestionFailed docId fileName reason

                        returnedDoc <- {
                            doc with
                                Status = IngestionStatus.Failed reason
                        }
                elif chunks.IsEmpty then
                    statusCache.AddOrUpdate(docId, Complete 0, fun _ _ -> Complete 0) |> ignore

                // Nudge the KB client to reload its document list.
                // The notification scope is the user id (matches the
                // SSE subscription filter); the payload's `scopeId`
                // carries the storage scope for downstream consumers
                // that need it.
                do! deps.Notifications.Publish(deps.UserId, DataRefreshed("KnowledgeBase", deps.Scope.ScopeId))
                do! deps.PublishInventory()

                return Ok returnedDoc
    }

/// Wipe body for `resetIndex`. Authorization (owner/admin in Team /
/// MultiTeam) and the fail-closed unresolved-scope guard are applied by
/// the `resetIndex` wrapper below, so this private body assumes the
/// caller is authorised and the scope is the caller's own.
let private performReset (deps: KnowledgeApiDeps) : Async<Result<unit, string>> = async {
    // Wipe every blob under `knowledge/` (index + raw uploads +
    // rendered narratives) and drop the in-memory status/progress
    // entries for the docs that lived in this scope. The
    // container lock blocks concurrent observer writes during
    // the read-then-delete window so a chunk completing mid-reset
    // can't resurrect the index after we've cleared it.
    //
    // Vector chunks are also dropped so RAG stops surfacing the
    // wiped documents. Chunks for each doc are deleted by id
    // (`{docId}:chunk:{i}`) up to its `ChunkCount` — same pattern
    // `deleteDocument` uses. The narrative-overwrite path in
    // `ingestNarrative` does its own per-write trailing-chunk
    // cleanup (using the prior KnowledgeDocument's ChunkCount
    // captured before re-ingestion), so by the time we reach this
    // sweep there are no orphan tails left to chase.
    // Phase 115 — captured under the lock, emitted as a structured
    // `KnowledgeScopeErased` audit row after release (GP 6 + GP 9): the
    // scope's document count and any chunks the fan-out left retrievable.
    let mutable documentCount = 0
    let mutable orphanChunkCount = 0

    let lock = acquireContainerLock deps.Scope.Container
    do! lock.WaitAsync() |> Async.AwaitTask

    try
        let! priorDocs = async {
            try
                let! existing = loadIndex deps.Storage deps.Scope.Container
                return existing
            with _ ->
                return []
        }

        documentCount <- priorDocs.Length

        try
            let! blobs = deps.Storage.List(deps.Scope.Container, "knowledge/")

            for blobName in blobs do
                let! _ = deps.Storage.Delete(deps.Scope.Container, blobName)
                ()
        with ex ->
            deps.Logger.Warn(
                sprintf
                    "[KnowledgeBase] ResetIndex: blob list failed for %s; falling back to index-only delete (%s)"
                    deps.Scope.Container
                    ex.Message
            )

            let! _ = deps.Storage.Delete(deps.Scope.Container, indexBlobName)
            ()

        // Drop each prior doc's index chunks so retrieval doesn't keep
        // surfacing them. Falls through if no index seam is wired
        // (tests).
        //
        // Phase 115 — routes through the unified lifecycle seam so the
        // sparse index sheds the wiped documents too (pre-115 the
        // `vs.DeleteChunk` loop left them retrievable through the
        // hybrid sparse leg and at rest in `bm25.json`). Deliberately
        // per-document rather than `DeleteByScope`: `deps.VectorScope`
        // can be the shared `Deployment` scope, where a scope-level
        // delete would wipe other containers' chunks as collateral.
        //
        // 0.4.3 — per-chunk failures caught, counted, and surfaced
        // as a single summary log line per ResetIndex call. Without
        // this, a partial-failure reset left RAG quoting wiped docs
        // with no operator signal. The seam now carries the per-chunk
        // isolation; this block aggregates its survivor reports.
        match deps.IndexLifecycle with
        | Some lifecycle ->
            let mutable orphanIds: string list = []

            for doc in priorDocs do
                let! report = lifecycle.DeleteDocument deps.VectorScope doc.Id doc.ChunkCount

                if not (ToolUp.Platform.IIndexLifecycle.IndexLifecycleReport.isClean report) then
                    let survivors = report.SurvivingChunkIds |> List.map snd |> List.distinct

                    orphanIds <- survivors @ orphanIds

                    deps.Logger.Warn(
                        sprintf
                            "[KnowledgeBase] ResetIndex chunk delete completed partially for docId=%s (scope=%s): %s"
                            doc.Id
                            deps.Scope.ScopeId
                            (ToolUp.Platform.IIndexLifecycle.IndexLifecycleReport.summarise report)
                    )

            orphanChunkCount <- (orphanIds |> List.distinct |> List.length)

            if not (List.isEmpty orphanIds) then
                deps.Logger.Warn(
                    sprintf
                        "[KnowledgeBase] ResetIndex left %d orphan chunk(s) in the retrieval indexes for scope %s; RAG may continue surfacing wiped documents. Surviving chunk ids: %s"
                        orphanIds.Length
                        deps.Scope.ScopeId
                        (orphanIds |> List.rev |> String.concat "; ")
                )
        | None -> ()

        for doc in priorDocs do
            statusCache.TryRemove(doc.Id) |> ignore
            progressCache.TryRemove(doc.Id) |> ignore

        // Cross-store coherence: wipe persisted narrative-store entries
        // for this scope alongside KB blobs and vector chunks. Without
        // this the user resets their KB but `list_narratives` continues
        // to return prior entries (the persistent narrative store is a
        // separate persistence layer from KB's own index — Phase 11.C.2
        // closure work added per-scope persistence to INarrativeStore;
        // ResetIndex needs to wipe both views to stay coherent).
        // No-op when no INarrativeStore is registered (test harnesses
        // without the SDK's standard compose).
        match deps.NarrativeStore with
        | Some narrativeStore ->
            try
                let! deletedNarratives = narrativeStore.DeleteScope deps.Scope.ScopeId

                if deletedNarratives > 0 then
                    deps.Logger.Info(
                        sprintf
                            "[KnowledgeBase] ResetIndex: deleted %d persisted narrative entries for scope %s"
                            deletedNarratives
                            deps.Scope.ScopeId
                    )
            with ex ->
                deps.Logger.Warn(
                    sprintf
                        "[KnowledgeBase] ResetIndex: INarrativeStore.DeleteScope failed for %s — leaving narrative entries in place (%s)"
                        deps.Scope.ScopeId
                        ex.Message
                )
        | None -> ()
    finally
        lock.Release() |> ignore

    KnowledgeBase.ServerInventory.invalidateInventoryCache deps.Scope.Container

    // Phase 115 — structured erasure-outcome audit for the scope wipe.
    // Complements the dispatcher's generic `Custom:KnowledgeIndexReset`
    // action row: that records who reset; this records what the fan-out
    // across the retrieval indexes actually did, and whether it left
    // anything retrievable (GP 6 + GP 9). `IAuditLog.Record` is
    // contractually best-effort and swallows its own failures, so an
    // audit gap never fails the reset. No-op when no `IAuditLog` is
    // composed (test harness / `NoAuditLog` deployment).
    match deps.AuditLog with
    | Some auditLog ->
        do!
            auditLog.Record(
                deps.Scope.ScopeId,
                KnowledgeScopeErased {
                    UserId = deps.UserId
                    ScopeId = deps.Scope.ScopeId
                    DocumentCount = documentCount
                    OrphanChunkCount = orphanChunkCount
                }
            )
    | None -> ()

    do! deps.Notifications.Publish(deps.UserId, DataRefreshed("KnowledgeBase", deps.Scope.ScopeId))
    do! deps.PublishInventory()
    return Ok()
}

/// Wipe the caller's KB scope. Gated before any destructive work runs:
///
/// 1. Fail closed (item 2) when scope resolution collapsed onto the shared
///    `user-anonymous` container (ScopeResolutionMiddleware unwired) — a raw
///    API call must not wipe a container shared across every unscoped caller.
/// 2. Owner / Admin only (item 1) in Team / MultiTeam modes, reusing the
///    same gate `SetAIContext` uses (`Ok ()` in Anonymous / Ephemeral /
///    Individual where the caller only reaches their own scope) — repairing
///    the asymmetry where a destructive reset was ungated while a standing-
///    context write was owner/admin-only.
///
/// Both guards run before the container lock so a refused caller never
/// contends for it. The successful-reset audit row is emitted by the
/// dispatcher's `[<Audit "Custom:KnowledgeIndexReset">]` annotation.
let resetIndex (deps: KnowledgeApiDeps) : Async<Result<unit, string>> = async {
    match KnowledgeApiDeps.guardResolvedScope deps with
    | Error e -> return Error e
    | Ok() ->
        match! deps.EnsureContextWriteAllowed() with
        | Error msg -> return Error msg
        | Ok() -> return! performReset deps
}
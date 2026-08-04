module KnowledgeBase.ServerApiDocuments

open System
open System.IO
open System.Security.Cryptography
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerExtractors
open KnowledgeBase.ServerExtractionErrors
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerJsonHelpers
open KnowledgeBase.ServerBulkImport
open KnowledgeBase.ServerApiDeps

// ─── Content-hash dedup (Phase 14x) ───────────────────────────────

/// Lowercase SHA-256 hex of the raw uploaded bytes — the dedup
/// identity. Raw bytes, not extracted text: extraction runs off the
/// request path (the upload returns before OCR starts), so only a
/// pre-persist hash can honour the "re-upload returns the existing
/// docId" contract — and the re-upload case dedup targets is
/// byte-identity by construction. Byte-different files whose *text*
/// happens to match (e.g. a re-exported PDF) ingest as separate
/// documents.
let private contentHashOf (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexStringLower

/// Per-scope secondary index mapping content hash → docId as
/// `_platform/kb-content-hash/{hash}/{docId}.ref` inside the scope's
/// own container (Phase 9f `BlobIndex`). GP 4 is structural: the
/// container IS the tenant boundary, so a hash can never match across
/// scopes. Refs are existence-only; the canonical
/// `knowledge/index.json` stays authoritative — a ref whose document
/// is gone (deleted / `ResetIndex`) is skipped at lookup and
/// overwritten by the next upload of the same bytes (drift contract).
let private contentHashIndex (deps: KnowledgeApiDeps) : SecondaryIndex.BlobIndex<string, string> =
    SecondaryIndex.BlobIndex.create deps.Storage deps.Scope.Container "_platform/kb-content-hash" id id Some

/// O(1) scope-local duplicate lookup: one `List` under the hash's
/// index segment, then a canonical-index verification of the candidate
/// ids. A `Failed` document never dedups — re-uploading the same bytes
/// is the documented retry path for a failed ingestion. In-flight
/// (`Queued` / `ExtractingText` / `Embedding`) and terminal
/// (`Complete` / `UnsupportedFormat`) documents both dedup: a
/// double-submit during ingestion is exactly the race this absorbs.
let private findDuplicate (deps: KnowledgeApiDeps) (contentHash: string) : Async<KnowledgeDocument option> = async {
    let! refs = (contentHashIndex deps).Lookup contentHash

    match refs with
    | [] -> return None
    | refs ->
        let candidateIds = refs |> List.map fst |> Set.ofList
        let! index = loadIndex deps.Storage deps.Scope.Container

        return
            index
            |> List.tryFind (fun d ->
                candidateIds.Contains d.Id
                && d.ContentHash = Some contentHash
                && (match d.Status with
                    | Failed _ -> false
                    | _ -> true))
}

// ─── Original retention (Phase 105) ───────────────────────────────
//
// One place decides WHERE an original's bytes live, and three
// operations — save, read, delete — are expressed against it. Before
// this the convention path `knowledge/{docId}/{fileName}` was rebuilt
// by hand at five call sites across three files; the point of routing
// through `IDataObjectStore` is defeated if any one of them keeps
// guessing.
//
// **`deps.DataObjectStore = None` is the pre-105 path byte-for-byte**
// (GP 11 / GP 13): the convention blob, no store call, no metadata
// envelope. See `KnowledgeObjectRetentionPolicy` for why that is an
// explicit opt-in rather than a DI probe.

/// The conventional blob key for a document's live original. Still the
/// storage location when retention is not composed, and always the
/// fallback READ location — a document uploaded before the opt-in lives
/// here forever, which is why there is no backfill step.
let private conventionOriginalBlobName (docId: string) (fileName: string) =
    sprintf "knowledge/%s/%s" docId fileName

/// Persist a document's original bytes.
///
/// The store branch uses `objectId = docId` (see
/// `KnowledgeObjectRetentionPolicy` for why the identity is derived
/// rather than stored) and the **`Versioned`** policy — never
/// `StrictlyVersioned`, whose `Delete` returns `DeleteForbidden` and
/// would break the delete cascade below. The envelope carries what a
/// later reader needs to describe the original without consulting the
/// KB index, plus `createdBy`, which is what makes the object visible
/// to the Phase 9h `Erase` subject match.
///
/// A store failure falls back to the convention blob rather than
/// failing the upload: the document is already admitted by this point,
/// and an original that landed at the legacy path is fully retrievable
/// (the read helper below tries both) where a refused upload is not.
let private saveOriginal
    (deps: KnowledgeApiDeps)
    (docId: string)
    (safeName: string)
    (ext: string)
    (contentHash: string option)
    (bytes: byte[])
    : Async<unit> =
    async {
        match deps.DataObjectStore with
        | None ->
            let! _ = deps.Storage.Upload(deps.Scope.Container, conventionOriginalBlobName docId safeName, bytes)
            ()
        | Some store ->
            let metadata =
                Map [
                    "kb.fileName", safeName
                    "kb.fileType", ext
                    "kb.sourceKind", "UploadedFile"
                    "kb.uploadedBy", deps.UserId
                    match contentHash with
                    | Some h -> "kb.contentHash", h
                    | None -> ()
                ]

            let! saved =
                store.Save(
                    deps.Scope.ScopeId,
                    docId,
                    bytes,
                    KnowledgeObjectRetentionPolicy.ObjectDataType,
                    deps.UserId,
                    metadata,
                    Versioned
                )

            match saved with
            | Ok _ -> ()
            | Error err ->
                deps.Logger.Warn(
                    sprintf
                        "[KnowledgeBase] IDataObjectStore.Save failed for docId=%s (%A); falling back to the knowledge/{docId}/{fileName} blob convention for this original."
                        docId
                        err
                )

                let! _ = deps.Storage.Upload(deps.Scope.Container, conventionOriginalBlobName docId safeName, bytes)
                ()
    }

/// Read a document's live original bytes, store first.
///
/// **The fallback IS the migration.** A document uploaded before
/// `withObjectStoreRetention` has no object, so `Get` returns
/// `NotFound` and the convention blob answers — forever, with no
/// backfill pass and no one-way cutover for existing content. It also
/// covers the reverse direction: an upload whose store `Save` failed
/// above landed at the convention path and still reads back.
let private readOriginalBytes (deps: KnowledgeApiDeps) (docId: string) (fileName: string) : Async<byte[] option> = async {
    let fromConvention () = async {
        match! deps.Storage.Download(deps.Scope.Container, conventionOriginalBlobName docId fileName) with
        | Ok bytes -> return Some bytes
        | Error _ -> return None
    }

    match deps.DataObjectStore with
    | None -> return! fromConvention ()
    | Some store ->
        match! store.Get(deps.Scope.ScopeId, docId) with
        | Ok(_, bytes) -> return Some bytes
        | Error _ -> return! fromConvention ()
}

/// Remove a document's live original from wherever it lives.
///
/// BOTH locations are swept, unconditionally, and that is deliberate:
/// a scope that ran for a while without retention composed and was then
/// opted in holds documents at either location, and a delete that only
/// cleared the composed one would leave the other's bytes at rest after
/// the document was reported deleted. `Delete` is idempotent on both
/// sides, so sweeping the location that holds nothing costs a no-op.
let private deleteOriginal (deps: KnowledgeApiDeps) (docId: string) (fileName: string) : Async<unit> = async {
    match deps.DataObjectStore with
    | None -> ()
    | Some store ->
        match! store.Delete(deps.Scope.ScopeId, docId) with
        | Ok() -> ()
        | Error err ->
            deps.Logger.Warn(
                sprintf
                    "[KnowledgeBase] IDataObjectStore.Delete failed for docId=%s (%A); the index entry is still removed."
                    docId
                    err
            )

    let! _ = deps.Storage.Delete(deps.Scope.Container, conventionOriginalBlobName docId fileName)
    ()
}

/// `true` when the object store is composed AND actually holds an
/// object for this document. `ListVersions` reads metadata only — no
/// content transfer — so this is the cheap "which era is this document
/// from" question, which is not the same as "is retention composed":
/// a scope opted in yesterday still holds every document uploaded
/// before that at the convention path.
let private storeHoldsOriginal (deps: KnowledgeApiDeps) (docId: string) : Async<bool> = async {
    match deps.DataObjectStore with
    | None -> return false
    | Some store ->
        let! versions = store.ListVersions(deps.Scope.ScopeId, docId)
        return not (List.isEmpty versions)
}

/// Resolve a document's original, store first.
///
/// Only the `UploadedFile` branch is store-backed — a `Note`'s
/// canonical form is its markdown and a narrative has no binary
/// original at all, so both stay entirely with the Phase 104
/// `IOriginalSourceResolver`, which is also what keeps a deployment's
/// CUSTOM resolver authoritative for the kinds it was registered to
/// serve. That is why retention did not need `IOriginalSourceResolver`
/// widened: the interface takes `(storage, container, doc)` and has no
/// `scopeId`, so a store-aware branch could not have been expressed
/// there without a breaking change to a public seam with external
/// implementations.
let private resolveOriginal (deps: KnowledgeApiDeps) (doc: KnowledgeDocument) : Async<OriginalDocument option> = async {
    let fromResolver () =
        deps.OriginalResolver.Resolve(deps.Storage, deps.Scope.Container, doc)

    match deps.DataObjectStore, doc.Source with
    | Some store, UploadedFile ->
        match! store.Get(deps.Scope.ScopeId, doc.Id) with
        | Ok(_, bytes) ->
            return
                Some {
                    FileName = doc.FileName
                    ContentType = KnowledgeBase.ServerOriginalSourceResolver.contentTypeFor doc.FileType
                    SizeBytes = int64 bytes.Length
                    Content = bytes
                }
        // Not in the store ⇒ a pre-opt-in document. The resolver's
        // convention-path branch is the fallback, and it is the
        // migration.
        | Error _ -> return! fromResolver ()
    | _ -> return! fromResolver ()
}

// ─── Document tags (Phase 502.C) ──────────────────────────────────

/// Stamp a document's tags into every chunk as `_tag.{tag} = "true"`.
///
/// This is the whole of the retrieval-side mechanism: the Phase 502.A
/// filter is AND-combined strict equality over chunk metadata in which
/// a chunk missing the key does not pass, so a stamped chunk is
/// selectable by `Filters = Some (Map ["_tag.policy", "true"])` and an
/// unstamped one is excluded — with no change to the pipeline at all.
/// An empty tag set returns the chunks unchanged, so an untagged
/// document's chunk metadata is byte-identical to its pre-502.C shape
/// (GP 11).
let private stampTags
    (tags: string list)
    (pairs: (ToolUp.Platform.VectorKnowledgeTypes.TextChunk * SourceReference) list)
    : (ToolUp.Platform.VectorKnowledgeTypes.TextChunk * SourceReference) list =
    match KnowledgeTags.metadataPairs tags with
    | [] -> pairs
    | tagPairs ->
        pairs
        |> List.map (fun (chunk, src) ->
            let metadata =
                tagPairs
                |> List.fold (fun (m: Map<string, string>) (k, v) -> m.Add(k, v)) chunk.Metadata

            { chunk with Metadata = metadata }, src)

// ─── Chunk-level content diff (Phase 510) ─────────────────────────

/// Lowercase SHA-256 hex of one chunk's rendered text — the identity a
/// re-ingest compares against the previous version's manifest.
///
/// Hashing `Content` (not the whole `TextChunk`) is deliberate:
/// `Metadata` carries `_source.IndexedAt`, a fresh timestamp on every
/// extraction, so a metadata-inclusive hash would differ for every chunk
/// on every re-ingest and the diff would never find anything unchanged.
/// `Content` is what gets embedded, so it is also exactly the right
/// identity for "would this produce the same vector".
let private chunkContentHash (chunk: ToolUp.Platform.VectorKnowledgeTypes.TextChunk) : string =
    SHA256.HashData(System.Text.Encoding.UTF8.GetBytes chunk.Content)
    |> Convert.ToHexStringLower

/// Positions whose content changed between the previous version's
/// manifest and the new chunk set — i.e. the chunks that genuinely need
/// re-embedding and re-upserting.
///
/// **Comparison is position-aligned, and that is a real limit worth
/// stating rather than hiding.** Chunk ids are positional
/// (`{docId}:chunk:{i}`), so a chunk's identity in every retrieval index
/// IS its index; an edit that inserts a paragraph near the top shifts
/// every later position and the diff correctly reports them all as
/// changed, because at those ids the stored content really did change.
/// The cheap win this leaves on the table — recognising shifted-but-
/// identical text — is already collected one layer down: the embedding
/// cache is content-hash keyed, so a shifted chunk is a cache HIT and
/// costs an upsert, not an embedding call. What this diff removes is the
/// upsert itself for the far more common append/edit-in-place shape.
///
/// A shorter `previous` (or an absent manifest, `[]`) makes every new
/// position changed, which is exactly the pre-510 behaviour.
let private changedChunkIndices (previous: string list) (current: string list) : Set<int> =
    let previousByIndex = previous |> List.toArray

    current
    |> List.mapi (fun i hash ->
        if i < previousByIndex.Length && previousByIndex[i] = hash then
            None
        else
            Some i)
    |> List.choose id
    |> Set.ofList

/// Phase 510 — delete the chunk positions a SHORTER new version left
/// behind. `[newCount .. oldCount - 1]` are ids the re-ingest's upserts
/// never reach (they only cover `0 .. newCount - 1`), so without this
/// they survive in the vector store AND the sparse index under the live
/// document id and retrieval keeps serving the previous version's tail
/// as if it were current content. This is the orphan-tail leak Phase 510
/// names, and the reason versioning could not simply reuse the docId
/// without it.
///
/// Routed through `IIndexLifecycle` rather than `IVectorStore` so the
/// sparse leg sheds the tail too — the same seam, and the same reason,
/// as the Phase 115 narrative-overwrite cleanup this mirrors. Failures
/// are isolated per chunk and summarised in one Warn line; the caller is
/// never failed for a partial tail delete, because the new version is
/// already correct and retryable.
let private deleteOrphanTail
    (deps: KnowledgeApiDeps)
    (docId: string)
    (newChunkCount: int)
    (priorChunkCount: int)
    : Async<unit> =
    async {
        match deps.IndexLifecycle with
        | Some lifecycle when priorChunkCount > newChunkCount ->
            let mutable survivors: string list = []

            for i in newChunkCount .. priorChunkCount - 1 do
                let chunkId = sprintf "%s:chunk:%d" docId i
                let! report = lifecycle.DeleteChunk deps.VectorScope chunkId

                if not (ToolUp.Platform.IIndexLifecycle.IndexLifecycleReport.isClean report) then
                    survivors <- chunkId :: survivors

            if not (List.isEmpty survivors) then
                deps.Logger.Warn(
                    sprintf
                        "[KnowledgeBase] %d orphan chunk(s) remain in the retrieval indexes for docId=%s after a shorter re-upload superseded version %d; RAG may surface stale trailing content until they are re-deleted. Surviving chunk ids: %s"
                        survivors.Length
                        docId
                        priorChunkCount
                        (survivors |> List.rev |> String.concat "; ")
                )
        | _ -> ()
    }

/// Phase 510 — archive the outgoing version before its live blob is
/// overwritten: copy its original bytes aside, then record an immutable
/// `KnowledgeDocumentVersion` for it.
///
/// Bytes are copied BEFORE the record is written, so a crash between the
/// two leaves preserved bytes nobody references (recoverable, invisible)
/// rather than a version record pointing at bytes that were never
/// copied (a broken promise the API would then have to honour). Same
/// ordering argument as the Phase 14x hash-ref write.
///
/// Phase 105 — the outgoing bytes are read through `readOriginalBytes`,
/// so this keeps working when the live original lives in the object
/// store rather than at the convention path. It still WRITES the
/// archive copy to `knowledge/{docId}/versions/{n}/{fileName}`:
/// `KnowledgeDocumentVersion.OriginalBlobName` is a wire-visible blob
/// handle that consumers (and the delete cascade's prefix sweep) already
/// resolve against `IBlobStorage`, and an object-store version is not
/// addressable by blob name — so re-pointing it would be a second,
/// breaking change riding along in an additive phase. The cost is that a
/// deployment composing BOTH retention and versioning holds a superseded
/// version's bytes twice; retiring the copy needs a locator field on
/// `KnowledgeDocumentVersion` and is recorded as follow-up work in
/// `docs/migrations/105-kb-original-retention-idataobjectstore.md`.
let private archiveSupersededVersion (deps: KnowledgeApiDeps) (prior: KnowledgeDocument) : Async<unit> = async {
    let archivedBlobName =
        versionedOriginalBlobName prior.Id prior.Version prior.FileName

    match! readOriginalBytes deps prior.Id prior.FileName with
    | Some priorBytes ->
        let! _ = deps.Storage.Upload(deps.Scope.Container, archivedBlobName, priorBytes)
        ()
    | None ->
        // The prior original is already gone (an earlier partial delete,
        // a legacy document whose blob was pruned). Losing bytes we never
        // had is not a reason to refuse the new version — but the record
        // below must not then claim they are retrievable, so say so
        // loudly and carry on.
        deps.Logger.Warn(
            sprintf
                "[KnowledgeBase] Superseding %s v%d but its original could not be read from the object store or the convention blob; the version record is written without preserved bytes."
                prior.Id
                prior.Version
        )

    do!
        appendVersion deps.Storage deps.Scope.Container {
            DocumentId = prior.Id
            Version = prior.Version
            FileName = prior.FileName
            FileType = prior.FileType
            SizeBytes = prior.SizeBytes
            ChunkCount = prior.ChunkCount
            ContentHash = prior.ContentHash
            UploadedAt = prior.UploadedAt
            UploadedBy = prior.UploadedBy
            OriginalBlobName = archivedBlobName
            SupersededAt = Some DateTimeOffset.UtcNow
        }
}

/// Persist + ingest path for a non-duplicate upload — the pre-14x body
/// of `uploadDocument`, plus the `ContentHash` stamp and the hash-index
/// registration. `contentHash` is `None` when the deployment opted out
/// via `withDocumentDedup false`, keeping the opt-out path byte-for-byte
/// identical to pre-14x behaviour (GP 11): no hash stored, no ref written.
///
/// Phase 510 — `predecessor` is `Some prior` exactly when this upload is
/// superseding an existing document in its lineage (`withDocumentVersioning
/// true` and a same-named live document found). It is `None` on every
/// pre-510 path, and every `predecessor`-conditioned branch below is a
/// no-op in that case, so an uncomposed deployment runs the identical
/// code it ran before (GP 11).
let private persistAndIngest
    (deps: KnowledgeApiDeps)
    (bytes: byte[])
    (docId: string)
    (safeName: string)
    (ext: string)
    (contentHash: string option)
    (predecessor: KnowledgeDocument option)
    : Async<KnowledgeDocument> =
    async {
        // Phase 510 — archive the outgoing version FIRST. Everything
        // below overwrites the live blob and the live index entry, so
        // the prior version has to be preserved before either happens.
        match predecessor with
        | Some prior -> do! archiveSupersededVersion deps prior
        | None -> ()
        // Persist the raw blob synchronously so a refresh during extraction
        // still finds the source file. ChunkCount stays 0 until extraction
        // completes — the observer reads it from the index, so the chunk
        // total has to land before the first chunk callback fires (the
        // background async below stamps it before enqueue).
        let doc: KnowledgeDocument = {
            Id = docId
            FileName = safeName
            FileType = ext
            UploadedAt = DateTimeOffset.UtcNow
            UploadedBy = deps.UserId
            Status = Queued
            SizeBytes = int64 bytes.Length
            ChunkCount = 0
            Source = UploadedFile
            ContentHash = contentHash
            // Phase 510 — the lineage advances only when this upload is
            // superseding one; a fresh document is version 1.
            Version =
                match predecessor with
                | Some prior -> prior.Version + 1
                | None -> 1
            // Phase 502.C — a new version of a tagged document keeps its
            // tags. The lineage is the same document, so dropping them
            // here would silently un-scope every query that had been
            // narrowing to it, on nothing more than a re-upload.
            Tags =
                match predecessor with
                | Some prior -> KnowledgeTags.normalise prior.Tags
                | None -> []
        }

        // Phase 105 — through `IDataObjectStore` when the deployment
        // composed `withObjectStoreRetention`, else the pre-105
        // convention blob.
        do! saveOriginal deps docId safeName ext contentHash bytes

        // Phase 116 — atomic index RMW so a concurrent upload to the same
        // container can't clobber this entry (or vice versa). Released before
        // the background extraction below, which re-acquires the same
        // container lock via `updateIndexStatus`.
        do! upsertIndexEntry deps.Storage deps.Scope.Container doc

        // Phase 510 — a superseded version's hash ref no longer describes
        // anything live (the lineage now carries the NEW bytes' hash), so
        // drop it. Left behind it would be a ref that `findDuplicate`
        // resolves to a candidate id and then correctly rejects on the
        // hash-verification step — harmless, but it accumulates one dead
        // ref per version forever. `Remove` is idempotent.
        match predecessor with
        | Some prior ->
            match prior.ContentHash with
            | Some priorHash when Some priorHash <> contentHash -> do! (contentHashIndex deps).Remove priorHash prior.Id
            | _ -> ()
        | None -> ()

        // Phase 14x — register the content hash in the O(1) dedup index.
        // Written after the canonical index entry so a crash between the
        // two writes leaves a missing ref (dedup miss → harmless
        // re-ingest), never a ref with no canonical entry behind it.
        match contentHash with
        | Some hash -> do! (contentHashIndex deps).Add hash docId None
        | None -> ()

        // Seed initial cache state. The background extractor flips this to
        // ExtractingText as soon as it starts running.
        statusCache.AddOrUpdate(docId, Queued, fun _ _ -> Queued) |> ignore

        // Spawn extraction off the request path. UploadDocument returns
        // within the time it takes to persist the raw blob (~hundreds of
        // ms), not the time it takes to OCR a PDF (~10s). Status flow
        // mirrors the synchronous predecessor:
        //   Queued → ExtractingText → Embedding(0, n) → Complete(n) | Failed reason
        // Errors are caught and routed through `deps.MarkIngestionFailed`
        // so the user sees a Failed badge rather than a doc stuck in
        // ExtractingText. Async.Start swallows otherwise-unobserved
        // exceptions, so the try/with is non-negotiable.
        let extractAndEnqueue = async {
            try
                let extractingStatus = ExtractingText

                statusCache.AddOrUpdate(docId, extractingStatus, fun _ _ -> extractingStatus)
                |> ignore

                do! updateIndexStatus deps.Storage deps.Scope.Container docId extractingStatus

                let! extracted = extractChunks deps.OcrProvider deps.TableExtractor docId safeName bytes

                // Phase 103 — stamp the structured original-document ref
                // (+ Phase 106 neutral locator) into each chunk so retrieval
                // surfaces `RetrievedSource.OriginalRef` without rebuilding
                // the blob-name convention.
                // Phase 502.C — and the document's tags, so a tagged
                // lineage's new version is filterable the moment it is
                // indexed rather than only after a manual re-tag.
                let chunks =
                    stampOriginalRefs docId safeName ext (int64 bytes.Length) extracted
                    |> stampTags doc.Tags

                if box deps.Queue <> null && not chunks.IsEmpty then
                    // Stamp ChunkCount BEFORE enqueue: the observer reads
                    // it from the index to compute progress, and the first
                    // chunk callback can fire before this method returns.
                    do! updateIndexChunkCount deps.Storage deps.Scope.Container docId chunks.Length

                    let initialStatus = Embedding(0, chunks.Length)

                    // Seed the cache; the observer's `AddOrUpdate` won't
                    // overwrite a fresher value (e.g. one already advanced
                    // to Embedding(1, n) by a racing callback).
                    statusCache.AddOrUpdate(
                        docId,
                        initialStatus,
                        fun _ existing ->
                            match existing with
                            | Queued
                            | ExtractingText -> initialStatus
                            | other -> other
                    )
                    |> ignore

                    do! updateIndexStatus deps.Storage deps.Scope.Container docId initialStatus

                    // Phase 510 — incremental re-index. On a supersede,
                    // compare each new chunk's content hash against the
                    // previous version's manifest and enqueue only the
                    // positions that actually changed. On a first ingest
                    // there is no manifest, `changed` is every index, and
                    // the enqueued set is byte-for-byte what pre-510
                    // enqueued.
                    let newHashes = chunks |> List.map (fst >> chunkContentHash)

                    let! previousHashes =
                        match predecessor with
                        | Some prior -> loadChunkHashes deps.Storage deps.Scope.Container prior.Id
                        | None -> async.Return []

                    let changed = changedChunkIndices previousHashes newHashes

                    let chunkPairs =
                        chunks
                        |> List.mapi (fun i (chunk, _) -> i, (sprintf "%s:chunk:%d" docId i, chunk))
                        |> List.filter (fun (i, _) -> changed.Contains i)
                        |> List.map snd

                    // Phase 510 — the shorter-re-upload orphan tail. Run
                    // BEFORE the enqueue so the stale ids are gone from
                    // every index even if the queue then refuses the job:
                    // a refused ingestion leaves the document `Failed` and
                    // retryable, whereas a surviving tail would keep being
                    // served as live content with nothing to signal it.
                    match predecessor with
                    | Some prior -> do! deleteOrphanTail deps docId chunks.Length prior.ChunkCount
                    | None -> ()

                    let job: DocumentIngestionJob = {
                        DocumentId = docId
                        DocumentName = safeName
                        Chunks = chunkPairs
                        Scope = deps.VectorScope
                        ScopeId = deps.Scope.ScopeId
                        Container = deps.Scope.Container
                        OriginatingUserId = Some deps.UserId
                    }

                    // An unchanged re-upload can leave nothing to
                    // enqueue at all. That is a legitimate terminal
                    // outcome, not an empty job to push through the
                    // queue: the document is already fully indexed at
                    // this content.
                    let accepted =
                        if List.isEmpty chunkPairs then
                            true
                        else
                            deps.Queue.Enqueue(job)

                    if not (List.isEmpty chunkPairs) then
                        deps.RecordEnqueue accepted

                    // Written after the tail delete + enqueue decision so
                    // the manifest never claims content the indexes do
                    // not hold (see `saveChunkHashes`).
                    if accepted then
                        do! saveChunkHashes deps.Storage deps.Scope.Container docId newHashes

                    if accepted && List.isEmpty chunkPairs then
                        // Nothing changed — the observer will never fire,
                        // so settle the terminal status here rather than
                        // leaving the document stuck at `Embedding(0, n)`.
                        let terminal = Complete chunks.Length
                        statusCache.AddOrUpdate(docId, terminal, fun _ _ -> terminal) |> ignore
                        do! updateIndexStatus deps.Storage deps.Scope.Container docId terminal

                        deps.Logger.Info(
                            sprintf
                                "[KnowledgeBase] Re-upload of '%s' (docId=%s v%d) changed no chunk content; %d chunk(s) reused, nothing re-embedded."
                                safeName
                                docId
                                doc.Version
                                chunks.Length
                        )
                    elif accepted && chunkPairs.Length < chunks.Length then
                        deps.Logger.Info(
                            sprintf
                                "[KnowledgeBase] Incremental re-index of '%s' (docId=%s v%d): %d of %d chunk(s) changed and were re-embedded; %d reused."
                                safeName
                                docId
                                doc.Version
                                chunkPairs.Length
                                chunks.Length
                                (chunks.Length - chunkPairs.Length)
                        )

                    if not accepted then
                        let reason =
                            sprintf
                                "Knowledge-base ingestion queue is full (%d/%d). Try again in a few seconds."
                                deps.Queue.Count
                                deps.Queue.Capacity

                        do! deps.MarkIngestionFailed docId safeName reason
                elif chunks.IsEmpty then
                    // Phase 119 — distinguish "no extractor for this type"
                    // (stored but never searchable) from a recognised-but-empty
                    // file. The pre-119 code reported both as `Complete 0`, which
                    // read as a successful index of an empty document and hid the
                    // fact that an unsupported upload would never be retrievable.
                    //
                    // Phase 500 adds the third case the pair above could
                    // not express: a recognised file with no TEXT LAYER
                    // (a scanned PDF, an image) and no `IOcrProvider`
                    // composed. That is neither an unrecognised type nor
                    // a successful index of an empty document — it is a
                    // missing capability, and reporting it as `Complete
                    // 0` told the user their scan was searchable when it
                    // was not. Checked FIRST because a scanned PDF is a
                    // *supported* extension and would otherwise be
                    // absorbed by the `Complete 0` arm. The probe reads
                    // nothing at all when an OCR companion IS composed
                    // (GP 13).
                    let terminal =
                        match ocrUnavailableDetail deps.OcrProvider safeName bytes with
                        | Some detail -> OcrUnavailable detail
                        | None ->
                            if isSupportedExtension ext then
                                Complete 0
                            else
                                UnsupportedFormat(sprintf "no extractor for '.%s' — stored but not searchable" ext)

                    statusCache.AddOrUpdate(docId, terminal, fun _ _ -> terminal) |> ignore
                    do! updateIndexStatus deps.Storage deps.Scope.Container docId terminal

                do! deps.PublishInventory()
            with ex ->
                deps.Logger.Error(sprintf "[KnowledgeBase] Extraction failed for %s/%s" docId safeName, Some ex)

                let reason = classify safeName ex
                do! deps.MarkIngestionFailed docId safeName reason
                do! deps.PublishInventory()
        }

        Async.Start extractAndEnqueue

        do! deps.PublishInventory()
        return doc
    }

/// Phase 512 — the scope's corpus quota, evaluated against the live
/// index. Returns `Some reason` when admitting one more document of
/// `incomingBytes` would breach `deps.QuotaPolicy`.
///
/// Phase 510 — `addsDocument` is `false` when the upload supersedes an
/// existing document rather than adding one. The distinction is
/// load-bearing: `KnowledgeQuotaPolicy.exceeds` refuses at
/// `currentDocuments >= MaxDocuments`, so a scope sitting exactly ON its
/// document cap would otherwise be unable to upload a NEW VERSION of a
/// document it already holds — a corpus that cannot be corrected, only
/// deleted from. The byte cap still applies in full (superseded versions
/// keep their preserved bytes, so a lineage genuinely does grow), which
/// is the lever that actually bounds storage.
///
/// Two properties are load-bearing:
///   * **An unlimited policy reads nothing.** `isUnlimited` short-circuits
///     before `loadIndex`, so an uncomposed deployment pays not even the
///     index read the tally would need (GP 13). This is also why the
///     "no policy ⇒ nothing capped" guarantee is structural rather than
///     emergent — there is no path from here to a refusal.
///   * **The tally is scope-local.** `deps.Scope.Container` comes from
///     the server-side resolver, so one team's corpus can never consume
///     another's headroom (GP 4).
let private quotaRefusal (deps: KnowledgeApiDeps) (incomingBytes: int64) (addsDocument: bool) : Async<string option> = async {
    if KnowledgeQuotaPolicy.isUnlimited deps.QuotaPolicy then
        return None
    else
        let! index = loadIndex deps.Storage deps.Scope.Container
        let usage = KnowledgeScopeUsage.ofDocuments deps.QuotaPolicy index

        let effectiveCount =
            if addsDocument then
                usage.DocumentCount
            else
                max 0 (usage.DocumentCount - 1)

        return KnowledgeQuotaPolicy.exceeds effectiveCount usage.TotalBytes incomingBytes deps.QuotaPolicy
}

/// The single-item upload path. **Every admission the Knowledge Base
/// makes goes through this function** — the interactive upload, and
/// every item of a Phase 511 bulk import alike — so the Phase 119 policy
/// checks, the Phase 515 content scan, the Phase 14x dedup, the Phase
/// 512 corpus quota and the Phase 510 versioning decision are applied
/// once, in one order, with no second implementation that could drift
/// out of step with them.
///
/// Phase 511 — `announceToUser` is the ONLY thing the bulk caller
/// changes, and it changes nothing about admission: it suppresses the
/// per-file "already exists, using the existing copy" `SystemMessage`
/// that the interactive path shows on a dedup hit. A 500-document
/// migration that emitted 200 of those toasts would be indistinguishable
/// from an outage from the user's seat; the batch publishes one
/// completion summary instead (`BulkImportNotificationKey`). The
/// interactive path passes `true` and is byte-for-byte its pre-511 self
/// (GP 11). The dedup AUDIT row is emitted either way — a trail that
/// thinned out under bulk import would be worse than useless.
let uploadDocumentCore
    (announceToUser: bool)
    (deps: KnowledgeApiDeps)
    (bytes: byte[])
    (fileName: string)
    : Async<KnowledgeDocument> =
    async {
        let docId = Guid.NewGuid().ToString()

        // Phase 119 — server-controlled storage key. `Path.GetFileName` strips
        // any directory component the caller smuggled in (`../../index.json` →
        // `index.json`); the docId GUID prefix + a separator-free name then
        // means the blob key can never escape `knowledge/{docId}/` or reach the
        // container-root `knowledge/index.json`. `FileNameSanitiser.validate`
        // rejects whatever survives stripping (control chars, over-length).
        let safeName = Path.GetFileName fileName
        let ext = Path.GetExtension(safeName).ToLowerInvariant().TrimStart('.')
        let policy = deps.UploadPolicy

        // Pure pre-persist policy evaluation (GP 9 — a refusal stores nothing
        // and is loud). Filename sanitisation runs regardless of policy; the
        // size cap / allowlist / unsupported-Reject levers fire only when the
        // deployment composed them via `withUploadPolicy`.
        let rejectionReason: string option =
            match FileNameSanitiser.validate safeName with
            | Error reason -> Some(sprintf "unsafe filename — %s" reason)
            | Ok() ->
                match KnowledgeUploadPolicy.exceedsSizeCap (int64 bytes.Length) policy with
                | Some reason -> Some reason
                | None ->
                    if not (KnowledgeUploadPolicy.allowsExtension ext policy) then
                        Some(sprintf "file type '.%s' is not in this deployment's upload allowlist" ext)
                    elif not (isSupportedExtension ext) && policy.OnUnsupportedType = Reject then
                        Some(
                            sprintf
                                "file type '.%s' has no extractor and this deployment's upload policy rejects unsupported types"
                                ext
                        )
                    else
                        None

        // Nothing is persisted for a refusal; the returned document carries
        // the typed rejection so the client can surface the reason. Logged at
        // Warn. Shared by the Phase 119 policy refusals above and the Phase
        // 512 quota refusal below — a quota breach IS a pre-persist policy
        // refusal, so it needs no new `IngestionStatus` case.
        let refuse (reason: string) : KnowledgeDocument =
            deps.Logger.Warn(sprintf "[KnowledgeBase] Upload rejected (%s): %s" reason safeName)

            {
                Id = docId
                FileName = safeName
                FileType = ext
                UploadedAt = DateTimeOffset.UtcNow
                UploadedBy = deps.UserId
                Status = UploadRejected reason
                SizeBytes = int64 bytes.Length
                ChunkCount = 0
                Source = UploadedFile
                ContentHash = None
                Version = 1
                // Nothing was persisted for a refusal, so there is no
                // document to tag.
                Tags = []
            }

        // Phase 510 — the document this upload supersedes, if any. Identity
        // is (file name, scope, `UploadedFile` source):
        //   * the SCOPE is the container, which comes from the server-side
        //     resolver — so a lineage can never span tenants (GP 4), for the
        //     same structural reason the content-hash index cannot;
        //   * `UploadedFile` only, because notes and narratives already own
        //     their own in-place update paths (`UpdateNote`,
        //     `IngestNarrative` + `Overwrite`) with their own identity rules
        //     — folding them into filename identity would let a note and an
        //     upload collide on a shared title;
        //   * a `Failed` predecessor is skipped, matching dedup: re-uploading
        //     after a failed ingestion is a RETRY of version N, not version
        //     N+1 of a version that never indexed anything.
        //
        // Returns `None` unless the deployment composed
        // `withDocumentVersioning true`, and reads nothing at all in that
        // case (GP 13) — the pre-510 upload path takes no extra index read.
        let findPredecessor () : Async<KnowledgeDocument option> = async {
            if not deps.VersioningPolicy.VersionUploads then
                return None
            else
                let! index = loadIndex deps.Storage deps.Scope.Container

                return
                    index
                    |> List.tryFind (fun d ->
                        d.FileName = safeName
                        && (match d.Source with
                            | UploadedFile -> true
                            | FromNarrative _
                            | Note _ -> false)
                        && (match d.Status with
                            | Failed _
                            | UploadRejected _ -> false
                            | _ -> true))
        }

        // Phase 512 — the corpus quota is checked immediately before a
        // persist, never before dedup: a duplicate upload stores nothing new,
        // so refusing it for quota would reject a request that costs the
        // scope no additional storage at all.
        //
        // Phase 510 — the predecessor lookup runs INSIDE this, after dedup
        // has already declined to short-circuit, so the ordering Phase 512
        // established (dedup, then quota, then persist) is preserved with
        // versioning slotted between quota and persist.
        let persistUnlessOverQuota (contentHash: string option) : Async<KnowledgeDocument> = async {
            let! predecessor = findPredecessor ()

            match! quotaRefusal deps (int64 bytes.Length) (Option.isNone predecessor) with
            | Some reason -> return refuse reason
            | None ->
                let targetDocId =
                    match predecessor with
                    | Some prior -> prior.Id
                    | None -> docId

                return! persistAndIngest deps bytes targetDocId safeName ext contentHash predecessor
        }

        // Phase 515 — run the composed content scanner and audit its verdict.
        //
        // Placement is load-bearing and is the whole point of the phase:
        //   * AFTER the pure `rejectionReason` checks — there is nothing to
        //     learn from streaming an oversized or disallowed-extension
        //     payload to a scanner when the upload is already refused, and
        //     the scan is the one expensive step on this path;
        //   * BEFORE dedup, the corpus quota, versioning and persistence —
        //     so a rejected payload never reaches the blob container and
        //     never consumes a byte of the scope's quota. Scanning before
        //     dedup in particular is deliberate: a byte-identical re-upload
        //     must not be admitted on the strength of a scan performed when
        //     the signature database was older.
        //
        // Returns `None` immediately when no scanner is composed (GP 13 — no
        // digest computed, no audit row, no branch taken).
        let contentScanRefusal () : Async<string option> = async {
            match deps.ContentScanner with
            | None -> return None
            | Some scanner ->
                let! verdict, refusal = ContentScan.evaluate scanner deps.ScanPolicy bytes safeName

                match verdict with
                | ScanClean -> ()
                | _ ->
                    deps.Logger.Warn(
                        sprintf
                            "[KnowledgeBase] Content scan (%s) returned '%s' for '%s': %s"
                            scanner.Name
                            (ScanVerdict.label verdict)
                            safeName
                            (ScanVerdict.reason verdict |> Option.defaultValue "no reason given")
                    )

                match deps.AuditLog with
                | Some audit ->
                    // Best-effort by contract (`IAuditLog.Record` swallows its
                    // own failures), awaited on the request path (GP 7) — an
                    // audit gap never fails the upload. Emitted for EVERY
                    // verdict, clean included: a trail that records only
                    // rejections cannot distinguish a payload the scanner
                    // passed from one it was never shown.
                    do!
                        audit.Record(
                            deps.Scope.ScopeId,
                            ContentScanned {
                                UserId = deps.UserId
                                ScopeId = deps.Scope.ScopeId
                                ScannerName = scanner.Name
                                FileName = safeName
                                ContentHash = contentHashOf bytes
                                SizeBytes = int64 bytes.Length
                                Verdict = ScanVerdict.label verdict
                                Reason = ScanVerdict.reason verdict
                                Refused = Option.isSome refusal
                                OccurredAt = DateTimeOffset.UtcNow
                            }
                        )
                | None -> ()

                return refusal
        }

        let! scanRejection =
            match rejectionReason with
            | Some _ -> async { return None }
            | None -> contentScanRefusal ()

        match Option.orElse scanRejection rejectionReason with
        | Some reason -> return refuse reason
        | None ->

            if not deps.DedupPolicy.DedupUploads then
                // `withDocumentDedup false` — pre-14x path byte-for-byte
                // (GP 11): no hash computed, no lookup, no ref written.
                return! persistUnlessOverQuota None
            else
                // Phase 14x — scope-local content-hash dedup, checked before
                // anything is persisted. A duplicate upload stores nothing:
                // the existing document is returned verbatim (same docId),
                // ingestion is skipped, the decision is audited, and the
                // user sees an Info toast.
                let contentHash = contentHashOf bytes
                let! duplicate = findDuplicate deps contentHash

                match duplicate with
                | None -> return! persistUnlessOverQuota (Some contentHash)
                | Some existing ->
                    deps.Logger.Info(
                        sprintf
                            "[KnowledgeBase] Upload of '%s' deduplicated onto existing document %s (content hash %s)"
                            safeName
                            existing.Id
                            contentHash
                    )

                    match deps.AuditLog with
                    | Some audit ->
                        // Best-effort by contract (`IAuditLog.Record` swallows
                        // its own failures), awaited on the request path (GP 7)
                        // — an audit gap never fails the upload.
                        do!
                            audit.Record(
                                deps.Scope.ScopeId,
                                KnowledgeDocumentDeduplicated {
                                    UserId = deps.UserId
                                    ScopeId = deps.Scope.ScopeId
                                    ExistingDocumentId = existing.Id
                                    FileName = safeName
                                    ContentHash = contentHash
                                }
                            )
                    | None -> ()

                    // Phase 511 — suppressed under bulk import; the batch's
                    // one completion summary replaces N per-file toasts.
                    if announceToUser && not (isNull (box deps.Notifications)) then
                        try
                            do!
                                deps.Notifications.Publish(
                                    deps.UserId,
                                    SystemMessage(
                                        SystemMessageLevel.Info,
                                        sprintf
                                            "\"%s\" already exists in the knowledge base — using the existing copy."
                                            safeName
                                    )
                                )
                        with ex ->
                            deps.Logger.Error(
                                sprintf "[KnowledgeBase] Failed to publish dedup notification for %s" safeName,
                                Some ex
                            )

                    return existing
    }

/// The interactive single-file upload — `uploadDocumentCore` with user
/// announcements on. Kept as its own name so `KnowledgeApi.UploadDocument`
/// binds to exactly what it always bound to (GP 11).
let uploadDocument (deps: KnowledgeApiDeps) (bytes: byte[]) (fileName: string) : Async<KnowledgeDocument> =
    uploadDocumentCore true deps bytes fileName

// ─── Bulk / programmatic import (Phase 511) ───────────────────────

/// Turn one submitted source into zero or more items. Archives expand
/// under the composed resource guards; a URL is fetched only when the
/// deployment allowlisted its host; a plain `File` is already an item.
///
/// Nothing here persists. That separation is what makes the whole
/// surface safe to add: expansion decides only *what to hand to the
/// upload boundary*, and the upload boundary decides — unchanged, and
/// exactly once — whether to admit it.
let private expandSource (deps: KnowledgeApiDeps) (source: BulkImportSource) : Async<ExpansionOutcome list> = async {
    match source with
    | BulkImportSource.File(fileName, content) ->
        return [
            Expanded {
                Source = fileName
                FileName = fileName
                Content = content
            }
        ]
    | BulkImportSource.Archive(fileName, content) -> return expandArchive deps.ArchiveImportPolicy fileName content
    | BulkImportSource.Url url ->
        // The gate runs FIRST and independently of whether a transport
        // exists, so an uncomposed deployment's refusal reads identically
        // to an out-of-allowlist one and neither leaks whether URL
        // ingestion is wired at all.
        match classifyUrl deps.UrlIngestionPolicy url with
        | Error reason -> return [ Rejected(url, url, reason) ]
        | Ok _ ->
            match deps.UrlFetcher with
            | None ->
                return [
                    Rejected(
                        url,
                        url,
                        "URL ingestion allowlists this host but no URL transport is composed; register one with KnowledgeBase.Server.withUrlIngestion or withUrlContentFetcher."
                    )
                ]
            | Some fetcher ->
                match! fetchUrl fetcher deps.UrlIngestionPolicy url with
                | Ok item -> return [ Expanded item ]
                | Error reason -> return [ Rejected(url, url, reason) ]
}

/// Phase 511 — corpus-scale import. Expands every source, routes each
/// surviving item through the single-item upload path, and returns one
/// report plus one completion notification.
///
/// **Items are admitted SEQUENTIALLY, and that is a correctness
/// requirement rather than a simplification.** The corpus quota is a
/// read-then-persist decision (`quotaRefusal` tallies the live index,
/// then `persistAndIngest` writes), so N concurrent items would each
/// observe the same pre-batch headroom and a batch could overshoot its
/// own quota by N-1 documents. The same argument applies to the Phase
/// 510 predecessor lookup — two items for the same file name racing
/// would both find no predecessor and both mint version 1. Sequential
/// admission makes each item's gates see the effect of every item before
/// it, which is what "each passing the existing per-item checks" has to
/// mean for a batch to be meaningful. Extraction and embedding still run
/// off the request path exactly as they do for a single upload, so this
/// is not the slow part.
///
/// **One bad item never fails the batch** (511.A): a refusal — whether
/// classified during expansion, returned as an `UploadRejected` status by
/// the upload boundary, or thrown by storage — becomes one line in the
/// report and the walk continues.
let importBatch (deps: KnowledgeApiDeps) (request: BulkImportRequest) : Async<BulkImportReport> = async {
    let batchId = Guid.NewGuid().ToString()
    let startedAt = DateTimeOffset.UtcNow
    let collected = ResizeArray<BulkImportItemReport>()

    for source in request.Sources do
        let! outcomes = expandSource deps source

        for outcome in outcomes do
            match outcome with
            | Rejected(sourceLabel, fileName, reason) ->
                deps.Logger.Warn(sprintf "[KnowledgeBase] Bulk import %s refused '%s': %s" batchId sourceLabel reason)

                collected.Add {
                    Source = sourceLabel
                    FileName = fileName
                    Outcome = BulkItemOutcome.Refused reason
                }
            | Expanded item ->
                // `announceToUser = false` — the per-file dedup toast is
                // replaced by this batch's single completion summary.
                let! itemReport = async {
                    try
                        let! doc = uploadDocumentCore false deps item.Content item.FileName

                        return {
                            Source = item.Source
                            FileName = doc.FileName
                            Outcome = BulkItemOutcome.Admitted doc
                        }
                    with ex ->
                        // A storage failure on one item is that item's
                        // failure. Reported, not propagated — the
                        // alternative loses every outcome already
                        // computed, including the successful ones.
                        deps.Logger.Error(
                            sprintf "[KnowledgeBase] Bulk import %s failed on '%s'" batchId item.Source,
                            Some ex
                        )

                        return {
                            Source = item.Source
                            FileName = item.FileName
                            Outcome = BulkItemOutcome.Refused(sprintf "import failed: %s" ex.Message)
                        }
                }

                collected.Add itemReport

    let report =
        BulkImportReport.ofItems batchId startedAt DateTimeOffset.UtcNow (List.ofSeq collected)

    deps.Logger.Info(
        sprintf
            "[KnowledgeBase] Bulk import %s completed: %d item(s) from %d source(s) — %d imported, %d refused."
            batchId
            (List.length report.Items)
            (List.length request.Sources)
            report.Imported
            report.Refused
    )

    // Phase 511.D — ONE completion signal for the whole batch. Published
    // best-effort on the request path: a notification failure must not
    // turn a completed import into a failed call, and the report the
    // caller already holds is the authoritative record either way.
    if not (isNull (box deps.Notifications)) then
        try
            let summary: BulkImportSummary = {
                BatchId = batchId
                TotalItems = List.length report.Items
                Imported = report.Imported
                Refused = report.Refused
                CompletedAt = report.CompletedAt
                RequestedBy = deps.UserId
            }

            do! deps.Notifications.Publish(deps.UserId, CustomNotification(BulkImportNotificationKey, toJson summary))
        with ex ->
            deps.Logger.Error(sprintf "[KnowledgeBase] Failed to publish bulk-import summary for %s" batchId, Some ex)

    return report
}

let getDocuments (deps: KnowledgeApiDeps) : Async<KnowledgeDocument list> =
    loadIndex deps.Storage deps.Scope.Container

/// Phase 512 — the caller's scope usage against the composed quota.
/// Purely derived from the scope's own document index, so it cannot
/// drift from the corpus it describes and carries no cross-tenant
/// signal (GP 4). An uncomposed deployment gets honest counts with
/// `None` caps — which is how a UI distinguishes "unlimited" from
/// "full".
let getScopeUsage (deps: KnowledgeApiDeps) : Async<KnowledgeScopeUsage> = async {
    let! docs = loadIndex deps.Storage deps.Scope.Container
    return KnowledgeScopeUsage.ofDocuments deps.QuotaPolicy docs
}

/// Phase 510 — the version history of one document lineage, newest
/// first. The current version is SYNTHESISED from the live index entry
/// rather than stored twice: the index is already authoritative for what
/// is current, and a second copy of it in the sidecar could only ever
/// drift from it.
///
/// Scope isolation is structural (GP 4) — the container comes from the
/// server-side resolver, so an id in another tenant's scope is simply
/// not found and returns `[]`, identical to an id that never existed
/// (no existence oracle, same posture as `getOriginalDocument`'s
/// `NotInScope`).
let getDocumentVersions (deps: KnowledgeApiDeps) (docId: string) : Async<KnowledgeDocumentVersion list> = async {
    let! index = loadIndex deps.Storage deps.Scope.Container

    match index |> List.tryFind (fun d -> d.Id = docId) with
    | None -> return []
    | Some current ->
        let! superseded = loadVersions deps.Storage deps.Scope.Container docId

        let currentRecord: KnowledgeDocumentVersion = {
            DocumentId = current.Id
            Version = current.Version
            FileName = current.FileName
            FileType = current.FileType
            SizeBytes = current.SizeBytes
            ChunkCount = current.ChunkCount
            ContentHash = current.ContentHash
            UploadedAt = current.UploadedAt
            UploadedBy = current.UploadedBy
            OriginalBlobName = sprintf "knowledge/%s/%s" current.Id current.FileName
            SupersededAt = None
        }

        return
            currentRecord :: superseded
            |> List.distinctBy _.Version
            |> List.sortByDescending _.Version
}

let deleteDocument (deps: KnowledgeApiDeps) (docId: string) : Async<Result<unit, string>> = async {
    // Fail closed if scope resolution collapsed onto the shared
    // `user-anonymous` container (item 2), then gate on owner/admin in
    // Team / MultiTeam modes (item 1 — same gate `SetAIContext` uses;
    // `Ok ()` in Anonymous / Ephemeral / Individual where the caller only
    // reaches their own scope). Destructive document deletion must not be
    // available to a non-admin team member via a raw API call.
    match KnowledgeApiDeps.guardResolvedScope deps with
    | Error e -> return Error e
    | Ok() ->
        match! deps.EnsureContextWriteAllowed() with
        | Error msg -> return Error msg
        | Ok() ->
            let! existing = loadIndex deps.Storage deps.Scope.Container
            let prior = existing |> List.tryFind (fun d -> d.Id = docId)

            match prior with
            | Some doc ->
                // Phase 115 — index deletion runs FIRST, through the unified
                // lifecycle seam (vector store + sparse index fan-out), and the
                // `index.json` entry is removed only after it completes clean.
                // The pre-115 ordering removed the index entry first and then
                // ran an unguarded `vs.DeleteChunk` loop — one throw left
                // orphaned embeddings still retrievable with no index entry to
                // trace them. A partial failure now leaves the document listed
                // (retryable) with a survivor-summary log instead.
                let! lifecycleReport =
                    match deps.IndexLifecycle with
                    | Some lifecycle -> lifecycle.DeleteDocument deps.VectorScope docId doc.ChunkCount
                    | None -> async.Return ToolUp.Platform.IIndexLifecycle.IndexLifecycleReport.empty

                if not (ToolUp.Platform.IIndexLifecycle.IndexLifecycleReport.isClean lifecycleReport) then
                    let summary =
                        ToolUp.Platform.IIndexLifecycle.IndexLifecycleReport.summarise lifecycleReport

                    deps.Logger.Warn(
                        sprintf
                            "[KnowledgeBase] deleteDocument %s completed partially — %s. The document stays listed so the delete can be retried."
                            docId
                            summary
                    )

                    return
                        Error(
                            sprintf "Some of the document's index entries could not be deleted (%s). Try again." summary
                        )
                else
                    // Phase 105 — removes the object-store object AND the
                    // convention blob, so a scope holding documents from
                    // both eras is fully swept.
                    do! deleteOriginal deps docId doc.FileName

                    // Phase 510 — a deleted document takes its whole
                    // lineage with it: every preserved prior-version
                    // original, the version sidecar, and the chunk-hash
                    // manifest. Enumerated by prefix rather than
                    // reconstructed from the version records, so a
                    // half-written archive (bytes copied, record not yet
                    // appended) is still swept — the failure mode where a
                    // "deleted" document's earlier revisions survive at
                    // rest is exactly what a data-subject deletion cannot
                    // afford. `Delete` is idempotent, so a document with
                    // no history simply deletes nothing extra.
                    let! versionBlobs = deps.Storage.List(deps.Scope.Container, sprintf "knowledge/%s/versions/" docId)

                    for blob in versionBlobs do
                        let! _ = deps.Storage.Delete(deps.Scope.Container, blob)
                        ()

                    let! _ = deps.Storage.Delete(deps.Scope.Container, versionsBlobName docId)
                    let! _ = deps.Storage.Delete(deps.Scope.Container, chunkHashesBlobName docId)

                    let updated = existing |> List.filter (fun d -> d.Id <> docId)
                    do! saveIndex deps.Storage deps.Scope.Container updated

                    // Phase 14x — drop the content-hash dedup ref so a
                    // future upload of the same bytes re-ingests fresh
                    // instead of being pointed at the deleted docId
                    // (`Remove` is idempotent; legacy docs carry no hash).
                    match doc.ContentHash with
                    | Some hash -> do! (contentHashIndex deps).Remove hash docId
                    | None -> ()

                    statusCache.TryRemove(docId) |> ignore
                    // Invalidate the prompt-build inventory cache so the next AI
                    // turn sees the updated document count, not the stale 30-s
                    // cached string.
                    KnowledgeBase.ServerInventory.invalidateInventoryCache deps.Scope.Container
                    do! deps.PublishInventory()
                    return Ok()
            | None ->
                // Unknown id — preserve the pre-115 idempotent shape (the index
                // is already in the requested state).
                statusCache.TryRemove(docId) |> ignore
                KnowledgeBase.ServerInventory.invalidateInventoryCache deps.Scope.Container
                do! deps.PublishInventory()
                return Ok()
}

// ─── Document tagging (Phase 502.C) ──────────────────────────────

/// Re-derive a document's current chunk set from its retained source
/// material, with `tags` stamped in.
///
/// There is no metadata-update primitive on `IVectorStore` — the
/// surface is `Upsert` / `Search` / `ListChunks` / `DeleteChunk`, and
/// `ListChunks` returns chunks without their vectors — so "change the
/// metadata on the chunks that are already indexed" is not expressible
/// as an edit. Re-deriving and re-upserting under the SAME chunk ids
/// is, and it is not as expensive as it looks: the content is
/// unchanged, and the embedding cache is content-hash keyed, so each
/// chunk costs an upsert and a cache hit rather than an embedding call.
///
/// `None` when the document has no re-derivable source (its original is
/// gone, or the kind has none) — the caller turns that into a refusal
/// rather than a half-applied tag.
let private rederiveChunks
    (deps: KnowledgeApiDeps)
    (doc: KnowledgeDocument)
    (tags: string list)
    : Async<(string * ToolUp.Platform.VectorKnowledgeTypes.TextChunk) list option> =
    async {
        let! derived = async {
            match doc.Source with
            | UploadedFile ->
                match! readOriginalBytes deps doc.Id doc.FileName with
                | None -> return None
                | Some bytes ->
                    let ext = doc.FileType

                    let! extracted = extractChunks deps.OcrProvider deps.TableExtractor doc.Id doc.FileName bytes

                    return Some(stampOriginalRefs doc.Id doc.FileName ext (int64 bytes.Length) extracted)
            | Note note ->
                match! deps.Storage.Download(deps.Scope.Container, sprintf "knowledge/%s/note.md" doc.Id) with
                | Error _ -> return None
                | Ok bodyBytes ->
                    let body = System.Text.Encoding.UTF8.GetString bodyBytes
                    let paragraphs = KnowledgeBase.ServerNotes.chunkNoteBody body

                    return
                        Some(
                            paragraphs
                            |> List.mapi (fun i p ->
                                KnowledgeBase.ServerNotes.buildNoteChunk
                                    doc.Id
                                    doc.FileName
                                    note.Title
                                    paragraphs.Length
                                    i
                                    p)
                        )
            // Refused by the caller before it gets here; kept total.
            | FromNarrative _ -> return None
        }

        return
            derived
            |> Option.map (fun pairs ->
                pairs
                |> stampTags tags
                |> List.mapi (fun i (chunk, _) -> sprintf "%s:chunk:%d" doc.Id i, chunk))
    }

/// Phase 502.C — replace a document's tag set and re-stamp its chunks
/// so the tags are actually filterable.
///
/// **Setting a tag is a re-index, not a label**, and the whole design
/// follows from that. Tags reach retrieval as `_tag.{tag}` chunk
/// metadata, which is what `RetrievalRequest.Filters` matches; a tag
/// written only to the index record would narrow nothing, and a filter
/// that silently matches nothing is precisely the defect Phase 502.A
/// was filed to remove. So this either re-stamps the chunks or refuses
/// — it never half-applies.
///
/// Gated exactly like `deleteDocument` (fail-closed on an unresolved
/// scope, then owner/admin in Team / MultiTeam): a tag change rewrites
/// which documents other members' scoped queries can reach, so it is a
/// shared-state write, not a personal annotation.
///
/// Idempotent by construction: normalising first means "set the tags it
/// already has" — in any order, any casing — compares equal and
/// re-indexes nothing.
let setDocumentTags (deps: KnowledgeApiDeps) (req: SetDocumentTagsRequest) : Async<Result<KnowledgeDocument, string>> = async {
    match KnowledgeApiDeps.guardResolvedScope deps with
    | Error e -> return Error e
    | Ok() ->
        match! deps.EnsureContextWriteAllowed() with
        | Error msg -> return Error msg
        | Ok() ->
            let! index = loadIndex deps.Storage deps.Scope.Container

            match index |> List.tryFind (fun d -> d.Id = req.DocId) with
            | None -> return Error "Document not found in this scope."
            | Some doc ->
                match doc.Source with
                | FromNarrative _ ->
                    // Loud, not partial. A narrative's chunks belong to
                    // the module that committed them, so a tag set here
                    // could never reach a filter — and reporting success
                    // for that would recreate the silent no-op 502.A
                    // removed.
                    return
                        Error
                            "Tags are not supported on module-generated narrative documents: their chunks are produced by the owning module, so a tag set here could never be reached by a retrieval filter."
                | UploadedFile
                | Note _ ->
                    let normalised = KnowledgeTags.normalise req.Tags

                    if normalised = KnowledgeTags.normalise doc.Tags then
                        // Nothing changed — do not pay a re-index for a
                        // no-op write.
                        return Ok doc
                    else
                        let! rederived = rederiveChunks deps doc normalised

                        match rederived with
                        | None ->
                            return
                                Error
                                    "The document's original content is no longer available, so its chunks cannot be re-stamped with the new tags. Re-upload the document and tag it again."
                        | Some chunkPairs ->
                            let updated = { doc with Tags = normalised }
                            do! upsertIndexEntry deps.Storage deps.Scope.Container updated

                            // Re-upsert EVERY position. The Phase 510
                            // incremental diff is deliberately bypassed:
                            // it compares chunk CONTENT hashes, and a tag
                            // change alters no content at all, so the
                            // diff would correctly report nothing changed
                            // and the new metadata would never be
                            // written. Content-hash-keyed embeddings mean
                            // the cost is upserts, not embedding calls.
                            if box deps.Queue <> null && not (List.isEmpty chunkPairs) then
                                let initialStatus = Embedding(0, chunkPairs.Length)

                                statusCache.AddOrUpdate(doc.Id, initialStatus, fun _ _ -> initialStatus)
                                |> ignore

                                do! updateIndexStatus deps.Storage deps.Scope.Container doc.Id initialStatus

                                let job: DocumentIngestionJob = {
                                    DocumentId = doc.Id
                                    DocumentName = doc.FileName
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

                                    do! deps.MarkIngestionFailed doc.Id doc.FileName reason

                                    return Error reason
                                else
                                    deps.Logger.Info(
                                        sprintf
                                            "[KnowledgeBase] Tags on '%s' (docId=%s) set to [%s]; %d chunk(s) re-stamped."
                                            doc.FileName
                                            doc.Id
                                            (String.concat "; " normalised)
                                            chunkPairs.Length
                                    )

                                    do! deps.PublishInventory()
                                    return Ok updated
                            else
                                do! deps.PublishInventory()
                                return Ok updated
}

// ─── Original-document retrieval (Phase 102) ─────────────────────

/// `KnowledgeSource` case name for the Phase 107 audit payload.
let private sourceKindName (source: KnowledgeSource) =
    match source with
    | UploadedFile -> "UploadedFile"
    | FromNarrative _ -> "FromNarrative"
    | Note _ -> "Note"

/// Best-effort audit emission for the original-access trail
/// (Phase 107). Awaited on the request path (GP 7) — `IAuditLog.Record`
/// is contractually best-effort and swallows its own failures, so audit
/// gaps never fail the fetch.
let private recordOriginalAccessAudit (deps: KnowledgeApiDeps) (event: AuditEvent) : Async<unit> =
    match deps.AuditLog with
    | Some a -> a.Record(deps.Scope.ScopeId, event)
    | None -> async.Return()

/// Fetch the *original* ingested document for a `docId` (Phase 102).
/// The lookup runs against the caller's resolved scope's index only —
/// scope isolation is structural (GP 4): the container comes from the
/// server-side scope resolver, never from the caller, so a document in
/// another team's scope is simply not findable and returns the same
/// `NotInScope` as a nonexistent id (no existence oracle). Resolution
/// of the bytes is per-`KnowledgeSource` via the Phase 104 resolver;
/// `None` becomes the typed `NoOriginalAvailable` rather than a
/// 404-shaped guess. Successful fetches and refusals both emit the
/// Phase 107 audit events.
let getOriginalDocument (deps: KnowledgeApiDeps) (docId: string) : Async<Result<OriginalDocument, KnowledgeBaseError>> = async {
    let denied (reason: string) =
        recordOriginalAccessAudit
            deps
            (KnowledgeOriginalRetrievalDenied {
                UserId = deps.UserId
                DocumentId = docId
                ScopeId = deps.Scope.ScopeId
                Reason = reason
            })

    try
        let! index = loadIndex deps.Storage deps.Scope.Container

        match index |> List.tryFind (fun d -> d.Id = docId) with
        | None ->
            do! denied "NotInScope"
            return Error NotInScope
        | Some doc ->
            // Phase 105 — store-first for uploaded files when retention
            // is composed; the Phase 104 resolver otherwise, and for
            // every other source kind.
            let! resolved = resolveOriginal deps doc

            match resolved with
            | Some original ->
                do!
                    recordOriginalAccessAudit
                        deps
                        (KnowledgeOriginalRetrieved {
                            UserId = deps.UserId
                            DocumentId = doc.Id
                            ScopeId = deps.Scope.ScopeId
                            SourceKind = sourceKindName doc.Source
                            FileName = doc.FileName
                        })

                return Ok original
            | None ->
                do! denied "NoOriginalAvailable"
                return Error NoOriginalAvailable
    with ex ->
        deps.Logger.Error(sprintf "[KnowledgeBase] GetOriginalDocument failed for %s" docId, Some ex)
        return Error(OriginalRetrievalFailed ex.Message)
}

// ─── Delivery-mode-aware original retrieval (Phase 108) ──────────

/// Fetch the original for `docId` in whatever delivery mode the
/// deployment composed (Phase 108).
///
/// **Unset is not "similar to" the Phase 102 path — it IS the Phase 102
/// path.** With no preview seam registered this calls
/// `getOriginalDocument` and wraps its result, so there is no second
/// copy of the scope gate, the resolver branch, or the audit emission
/// that could drift from the original. That is the whole reason the
/// default arm is one line.
///
/// With a seam registered (`withSignedOriginalUrls`, or a custom
/// `withOriginalPreviewSeam`), the scope gate runs FIRST — the index is
/// loaded for the caller's resolved container and an id that is not in
/// it refuses with `NotInScope`, identical to an id that does not exist
/// (no existence oracle) — and only then is the seam entered, which is
/// where a URL may be minted. **A signed URL is a bearer token, so this
/// ordering is the access control** (GP 4): reverse it and a caller
/// could hold a working link to a document they were about to be
/// refused. Phase 108's ordering test pins it.
///
/// The Phase 107 audit trail is emitted on both arms, so switching
/// delivery mode does not silently thin the access log.
///
/// The seam arrives as an explicit parameter rather than on
/// `KnowledgeApiDeps`: it is the only handler that wants it, and
/// `KnowledgeApiDeps` is constructed by a dozen test harnesses that
/// would all have to be edited to add a field none of them use.
let getOriginalDelivery
    (seam: KnowledgeBase.ServerOriginalPreviewSeam.IOriginalPreviewSeam option)
    (deps: KnowledgeApiDeps)
    (docId: string)
    : Async<Result<PreviewContent, KnowledgeBaseError>> =
    async {
        match seam with
        | None ->
            let! inlineResult = getOriginalDocument deps docId
            return inlineResult |> Result.map PreviewContent.Inline
        | Some seam ->
            let denied (reason: string) =
                recordOriginalAccessAudit
                    deps
                    (KnowledgeOriginalRetrievalDenied {
                        UserId = deps.UserId
                        DocumentId = docId
                        ScopeId = deps.Scope.ScopeId
                        Reason = reason
                    })

            try
                let! index = loadIndex deps.Storage deps.Scope.Container

                match index |> List.tryFind (fun d -> d.Id = docId) with
                | None ->
                    // Scope gate. Nothing below this line runs for a caller
                    // who does not own the document — including the mint.
                    do! denied "NotInScope"
                    return Error NotInScope
                | Some doc ->
                    // Phase 105 — a document whose original lives in the
                    // object store has no single signable blob name: the
                    // bytes sit in the store's content-addressed pool
                    // under a hash, and the seam signs an `IBlobStorage`
                    // key. Left to run, `ResolveMetadata` would describe
                    // the convention path, `GetMetadata` would miss, and
                    // the caller would get `NoOriginalAvailable` for a
                    // document that is perfectly retrievable.
                    //
                    // So retention wins over signed delivery for exactly
                    // those documents, and serves the Phase 102 inline
                    // result — degraded in byte-efficiency, never in
                    // correctness, and never silently: the log names the
                    // interaction. Documents predating the opt-in still
                    // sign normally, which is why this is decided per
                    // document rather than per deployment. Reuniting the
                    // two needs an object-store signing surface and is
                    // recorded as follow-up work in
                    // `docs/migrations/105-kb-original-retention-idataobjectstore.md`.
                    let! heldInStore = storeHoldsOriginal deps doc.Id

                    if heldInStore then
                        deps.Logger.Info(
                            sprintf
                                "[KnowledgeBase] docId=%s is retained in IDataObjectStore, which has no signable blob name; serving the original inline instead of a signed URL."
                                doc.Id
                        )

                        // Delegates to the Phase 102 handler rather than
                        // re-resolving here, so the audit emission stays
                        // in exactly one place on this arm too.
                        let! inlineResult = getOriginalDocument deps docId
                        return inlineResult |> Result.map PreviewContent.Inline
                    else
                        let! previewed = seam.Preview(deps.Storage, deps.Scope.Container, doc, None)

                        match previewed with
                        | Ok target ->
                            do!
                                recordOriginalAccessAudit
                                    deps
                                    (KnowledgeOriginalRetrieved {
                                        UserId = deps.UserId
                                        DocumentId = doc.Id
                                        ScopeId = deps.Scope.ScopeId
                                        SourceKind = sourceKindName doc.Source
                                        FileName = doc.FileName
                                    })

                            return Ok target.Content
                        | Error NoOriginalAvailable ->
                            do! denied "NoOriginalAvailable"
                            return Error NoOriginalAvailable
                        | Error other -> return Error other
            with ex ->
                deps.Logger.Error(sprintf "[KnowledgeBase] GetOriginalDelivery failed for %s" docId, Some ex)
                return Error(OriginalRetrievalFailed ex.Message)
    }

let getStatus (deps: KnowledgeApiDeps) (docId: string) : Async<IngestionStatus> = async {
    match statusCache.TryGetValue(docId) with
    | true, status -> return status
    | _ ->
        // Cache miss — process restarted, or no observer touched this doc
        // yet. Fall back to the persisted index so the client can still see
        // the last known terminal status (Complete/Failed) survive a reboot.
        let! index = loadIndex deps.Storage deps.Scope.Container

        return
            index
            |> List.tryFind (fun d -> d.Id = docId)
            |> Option.map _.Status
            |> Option.defaultValue (Failed "Document not found")
}

let getSuggestedQuestions (deps: KnowledgeApiDeps) (activeModule: string option) : Async<string list> = async {
    // Sample document names from the team's KB and produce
    // canned questions that point at real content. Fall back
    // to a generic onboarding set when the KB is empty so
    // the zero-state isn't a blank input box. Server-side
    // AI-generated suggestions (per the WS4.4 plan) are a
    // follow-up — this v1 is heuristic-only and free.
    try
        let! docs = loadIndex deps.Storage deps.Scope.Container

        let documents, notes =
            docs
            |> List.partition (fun d ->
                match d.Source with
                | Note _ -> false
                | UploadedFile
                | FromNarrative _ -> true)

        let topDocs = documents |> List.sortByDescending _.UploadedAt |> List.truncate 3

        let questions =
            [
                for doc in topDocs do
                    sprintf "What's in \"%s\"?" doc.FileName

                if notes.Length >= 1 then
                    "Summarise the team's notes."

                if documents.Length >= 2 then
                    "What themes do these documents share?"

                match activeModule with
                | Some name when documents.Length > 0 -> sprintf "How does our %s data look?" name
                | _ -> ()
            ]
            |> List.distinct
            |> List.truncate 5

        if questions.IsEmpty && documents.IsEmpty && notes.IsEmpty then
            // Empty KB — return nothing so the client can
            // render its own empty-state hint instead of
            // questions with no grounding.
            return []
        else
            return questions
    with ex ->
        deps.Logger.Warn(sprintf "[KnowledgeBase] GetSuggestedQuestions failed: %s" ex.Message)

        return []
}
module KnowledgeBase.ServerIndexStorage

open System
open System.Collections.Concurrent
open System.Text
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open SharedTypes
open KnowledgeBase.ServerJsonHelpers

// ─── Document index persistence ───────────────────────────────────

let indexBlobName = "knowledge/index.json"

/// Phase 510 — normalise a document read off the wire. A pre-510
/// `index.json` record carries no `Version` property, and a missing
/// JSON *number* deserialises to `0`, not to anything we could detect
/// downstream — so the coercion has to happen once, here at the store,
/// rather than being remembered at each of the ~dozen read sites.
///
/// `0 -> 1` is not a fallback, it is the truth: an unversioned document
/// is version 1 of its own lineage. Doing it on load also means
/// `saveIndex` can never persist a `0` back — every write is of a value
/// that came through here or was constructed by current code.
let private normaliseVersion (doc: KnowledgeDocument) : KnowledgeDocument =
    if doc.Version < 1 then { doc with Version = 1 } else doc

let loadIndex (storage: IBlobStorage) (container: string) = async {
    match! storage.Download(container, indexBlobName) with
    | Ok bytes ->
        try
            return
                fromJson<KnowledgeDocument list> (Encoding.UTF8.GetString bytes)
                |> List.map normaliseVersion
        with _ ->
            return []
    | Error _ -> return []
}

let saveIndex (storage: IBlobStorage) (container: string) (docs: KnowledgeDocument list) = async {
    let bytes = (toJson docs: string) |> Encoding.UTF8.GetBytes
    let! _ = storage.Upload(container, indexBlobName, bytes)
    ()
}

// ─── Phase 510 — version + chunk-hash sidecars ────────────────────
//
// Both sidecars live UNDER the document's own `knowledge/{docId}/`
// prefix rather than in a container-level file. `resetIndex` wipes
// everything under `knowledge/` by prefix, so a scope reset sheds them
// for free; `deleteDocument` deletes named blobs rather than a prefix,
// so it sweeps `knowledge/{docId}/versions/` explicitly — see the
// deletion block there. Keeping them under the document's own prefix is
// what makes that sweep expressible as one `List` call instead of a
// walk over the version records (which would miss a half-written
// archive).

/// Immutable superseded-version records for one document lineage.
let versionsBlobName (docId: string) =
    sprintf "knowledge/%s/versions.json" docId

/// Index-ordered content hashes of the chunks the CURRENT version
/// indexed. Read at re-ingest to decide which chunk positions actually
/// changed; absent (first ingest, legacy document, versioning never
/// composed) means "everything is new", which is precisely the pre-510
/// behaviour.
let chunkHashesBlobName (docId: string) =
    sprintf "knowledge/%s/chunk-hashes.json" docId

/// Where a superseded version's original bytes are preserved. The live
/// version keeps the conventional `knowledge/{docId}/{fileName}` path so
/// every pre-510 reader (`IOriginalSourceResolver`, the preview seam)
/// keeps resolving the current original with no change at all.
let versionedOriginalBlobName (docId: string) (version: int) (fileName: string) =
    sprintf "knowledge/%s/versions/%d/%s" docId version fileName

/// Superseded versions of `docId`, oldest first. Absent / unreadable
/// sidecar reads as `[]` — the same lenient posture `loadIndex` takes,
/// because a document with no history is the overwhelmingly common case
/// and must not be an error.
let loadVersions (storage: IBlobStorage) (container: string) (docId: string) = async {
    match! storage.Download(container, versionsBlobName docId) with
    | Ok bytes ->
        try
            return fromJson<KnowledgeDocumentVersion list> (Encoding.UTF8.GetString bytes)
        with _ ->
            return []
    | Error _ -> return []
}

/// The current version's per-chunk content hashes, index-ordered.
/// `[]` when no manifest exists — "nothing is known to be unchanged".
let loadChunkHashes (storage: IBlobStorage) (container: string) (docId: string) = async {
    match! storage.Download(container, chunkHashesBlobName docId) with
    | Ok bytes ->
        try
            return fromJson<string list> (Encoding.UTF8.GetString bytes)
        with _ ->
            return []
    | Error _ -> return []
}

/// Replace the chunk-hash manifest for `docId`. Written AFTER the
/// re-ingest has been enqueued and the stale tail deleted, so a crash
/// between the two leaves a manifest describing LESS than what is
/// indexed — which costs a redundant re-embed next time (harmless,
/// absorbed by the embedding cache) rather than skipping a chunk that
/// was never actually written (silently missing content).
let saveChunkHashes (storage: IBlobStorage) (container: string) (docId: string) (hashes: string list) = async {
    let bytes = (toJson hashes: string) |> Encoding.UTF8.GetBytes
    let! _ = storage.Upload(container, chunkHashesBlobName docId, bytes)
    ()
}

// ─── In-memory status cache ───────────────────────────────────────

let statusCache = ConcurrentDictionary<string, IngestionStatus>()

// Per-document chunk-completion counter. Maintained by the ingestion
// observer (`makeIngestionStatusObserver`) so concurrent chunk callbacks
// can be aggregated into `Embedding(processed, total)` without re-reading
// the persisted index for every increment.
let progressCache = ConcurrentDictionary<string, int>()

// Per-container lock used to serialise read-modify-write of `index.json`
// from the observer. Index updates are rare (one per chunk completion)
// and IO-bound, so a simple semaphore-per-container avoids cross-team
// contention without introducing a global lock.
let private containerLocks = ConcurrentDictionary<string, SemaphoreSlim>()

let acquireContainerLock (container: string) =
    containerLocks.GetOrAdd(container, fun _ -> new SemaphoreSlim(1, 1))

/// Phase 510 — append one superseded-version record under the container
/// lock.
///
/// The lock matters for the same reason `upsertIndexEntry` takes it:
/// this is a read-modify-write of a shared blob, and two concurrent
/// re-uploads of the same document would otherwise each read the same
/// history, append their own record, and have the second write drop the
/// first — losing a version permanently, which is the one thing an
/// immutable-version store must never do.
///
/// **Do not call from inside another `acquireContainerLock` critical
/// section** — the semaphore is non-reentrant.
let appendVersion (storage: IBlobStorage) (container: string) (record: KnowledgeDocumentVersion) = async {
    let lock = acquireContainerLock container
    do! lock.WaitAsync() |> Async.AwaitTask

    try
        let! existing = async {
            match! storage.Download(container, versionsBlobName record.DocumentId) with
            | Ok bytes ->
                try
                    return fromJson<KnowledgeDocumentVersion list> (Encoding.UTF8.GetString bytes)
                with _ ->
                    return []
            | Error _ -> return []
        }

        // Idempotent on version number: a retried supersede must not
        // double-record the same version.
        let updated =
            (existing |> List.filter (fun v -> v.Version <> record.Version)) @ [ record ]
            |> List.sortBy _.Version

        let json = (toJson updated: string) |> Encoding.UTF8.GetBytes
        let! _ = storage.Upload(container, versionsBlobName record.DocumentId, json)
        ()
    finally
        lock.Release() |> ignore
}

/// Phase 116 — atomic add-or-replace of a single document in the index,
/// under the container lock. Loads, drops any existing entry with the
/// same `Id`, appends `doc`, and saves — all while holding the lock so
/// two concurrent additive writers (upload / addNote / updateNote /
/// narrative) cannot each load the same index, append their own document,
/// and have the second save clobber the first (one document silently
/// lost from the index while its blob + chunks persist orphaned).
///
/// This is the *interim single-instance* guard, matching
/// `updateIndexStatus` / `updateIndexChunkCount` above: it serialises
/// within one process but not across replicas. The cross-replica fix is
/// ETag-conditional-write CAS on the index blob (Phase 9c half-2 /
/// Phase 116 ETag-gated tasks), deferred.
///
/// **Do not call from inside another `acquireContainerLock` critical
/// section** — the semaphore is non-reentrant. Callers acquire it only
/// around the index RMW and release before any background / enqueue work
/// that itself routes through `updateIndexStatus` / `MarkIngestionFailed`.
let upsertIndexEntry (storage: IBlobStorage) (container: string) (doc: KnowledgeDocument) = async {
    let lock = acquireContainerLock container
    do! lock.WaitAsync() |> Async.AwaitTask

    try
        let! existing = loadIndex storage container

        let updated =
            existing |> List.filter (fun d -> d.Id <> doc.Id) |> List.append [ doc ]

        do! saveIndex storage container updated
    finally
        lock.Release() |> ignore
}

/// Update the persisted status of a single document in the index. Acquires
/// the container lock, loads, mutates the matching doc, and saves. No-op
/// if the document is not present (deleted between writes).
let updateIndexStatus (storage: IBlobStorage) (container: string) (docId: string) (newStatus: IngestionStatus) = async {
    let lock = acquireContainerLock container
    do! lock.WaitAsync() |> Async.AwaitTask

    try
        let! existing = loadIndex storage container

        match existing |> List.tryFind (fun d -> d.Id = docId) with
        | None -> ()
        | Some _ ->
            let updated =
                existing
                |> List.map (fun d -> if d.Id = docId then { d with Status = newStatus } else d)

            do! saveIndex storage container updated
    finally
        lock.Release() |> ignore
}

/// Stamp `ChunkCount` on the persisted document once extraction has finished
/// and the chunk total is known. Same locking semantics as
/// `updateIndexStatus`. Async extraction (`Documents.uploadDocument`)
/// initialises with `ChunkCount = 0` because the count isn't known until
/// after OCR / table parsing completes.
let updateIndexChunkCount (storage: IBlobStorage) (container: string) (docId: string) (chunkCount: int) = async {
    let lock = acquireContainerLock container
    do! lock.WaitAsync() |> Async.AwaitTask

    try
        let! existing = loadIndex storage container

        match existing |> List.tryFind (fun d -> d.Id = docId) with
        | None -> ()
        | Some _ ->
            let updated =
                existing
                |> List.map (fun d ->
                    if d.Id = docId then
                        { d with ChunkCount = chunkCount }
                    else
                        d)

            do! saveIndex storage container updated
    finally
        lock.Release() |> ignore
}
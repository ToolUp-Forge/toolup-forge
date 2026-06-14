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

let loadIndex (storage: IBlobStorage) (container: string) = async {
    match! storage.Download(container, indexBlobName) with
    | Ok bytes ->
        try
            return fromJson<KnowledgeDocument list> (Encoding.UTF8.GetString bytes)
        with _ ->
            return []
    | Error _ -> return []
}

let saveIndex (storage: IBlobStorage) (container: string) (docs: KnowledgeDocument list) = async {
    let bytes = (toJson docs: string) |> Encoding.UTF8.GetBytes
    let! _ = storage.Upload(container, indexBlobName, bytes)
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
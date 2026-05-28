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
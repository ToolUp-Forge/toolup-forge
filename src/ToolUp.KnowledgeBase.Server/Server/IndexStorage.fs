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

/// Where a `Note` document persists its markdown body — `addNote` /
/// `updateNote` write it here, `IOriginalSourceResolver` serves it from
/// here, and (Phase 504.C) `deleteDocument` and the retention sweep
/// remove it from here. NOT the convention original path
/// (`knowledge/{docId}/{FileName}`): a note's `FileName` is
/// `"{title}.md"`, which never coincides with this blob unless the
/// sanitised title happens to be `note`.
let noteBodyBlobName (docId: string) = sprintf "knowledge/%s/note.md" docId

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

/// Phase 502.C — the same treatment for `Tags`, and for a sharper
/// reason than `Version`'s.
///
/// A pre-502.C `index.json` record carries no `Tags` property, and a
/// missing JSON *list* deserialises to `null`. F# `[]` is NOT null, so
/// every `doc.Tags |> List.map …` on a legacy record would throw —
/// silently, on read, in a document list that had worked for months.
/// Coercing once here means no read site has to remember, and
/// `saveIndex` can never persist a null back: every write is of a value
/// that came through this function or was constructed by current code.
///
/// `null -> []` is not a fallback, it is the truth: a document written
/// before tags existed has no tags.
let private normaliseTags (doc: KnowledgeDocument) : KnowledgeDocument =
    if isNull (box doc.Tags) then
        { doc with Tags = [] }
    else
        doc

let loadIndex (storage: IBlobStorage) (container: string) = async {
    match! storage.Download(container, indexBlobName) with
    | Ok bytes ->
        try
            return
                fromJson<KnowledgeDocument list> (Encoding.UTF8.GetString bytes)
                |> List.map (normaliseVersion >> normaliseTags)
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

// ─── Phase 69c.tail D - change notification over the status cache ──
//
// Before this, ingestion progress was a poll and only a poll: the
// TERMINAL transition was published through `INotificationChannel` by the
// ingestion observer, and everything between `Queued` and `Complete` was
// visible only by asking `GetStatus` again - which the KB client does
// every 2 s for every non-terminal document. There was no seam a typed
// stream could observe, which is why Phase 69c recorded this task as
// waiting on one rather than moving the poll server-side under a
// streaming shape.
//
// This is that seam, and it is deliberately the smallest one that can
// exist: an in-process, per-document subscriber list, published to by the
// three write helpers below. It adds no state to the cache, no ordering
// promise beyond "the value the cache settled on", and nothing at all to
// a deployment with no subscribers - `publish` over an empty subscriber
// map is a dictionary emptiness check (GP 13).
//
// **Write through `setStatus` / `updateStatus` / `clearStatus`, not
// through `statusCache` directly.** The cache stays public because it
// always was, and a direct `AddOrUpdate` still works - it just publishes
// nothing, so a subscriber silently misses that transition. The helpers
// are the whole discipline; there is no other.
//
// In-process by construction, like the `IngestionBackgroundService` queue
// it reports on: a chunk indexed on another instance is that instance's
// event, and the terminal `INotificationChannel` publish (which a
// distributed channel companion fans out) remains the cross-instance
// signal. A subscriber on the wrong instance sees the seed value and then
// silence until the terminal arrives through the channel - which is the
// pre-existing distribution story, not one this adds.
[<RequireQualifiedAccess>]
module IngestionStatusFeed =

    /// Per-subscription: the document id being watched, and the handler.
    /// Keyed by an opaque token so two subscriptions to the same document
    /// are independent and either can be disposed without touching the
    /// other.
    let private subscribers =
        ConcurrentDictionary<Guid, string * (IngestionStatus -> unit)>()

    /// Notify every subscriber watching `docId`. A handler that throws is
    /// swallowed: a subscriber is an observer of the ingestion path, never
    /// a participant in it, so its failure must not fail the write that
    /// triggered it.
    let internal publish (docId: string) (status: IngestionStatus) : unit =
        if not subscribers.IsEmpty then
            for entry in subscribers do
                let watched, handler = entry.Value

                if watched = docId then
                    try
                        handler status
                    with _ ->
                        ()

    /// Watch one document's ingestion status. `handler` is called on every
    /// transition written through the helpers below, on the thread that
    /// wrote it. Dispose to stop; disposal is idempotent.
    let subscribe (docId: string) (handler: IngestionStatus -> unit) : IDisposable =
        let token = Guid.NewGuid()
        subscribers[token] <- (docId, handler)

        { new IDisposable with
            member _.Dispose() = subscribers.TryRemove token |> ignore
        }

    /// How many subscriptions are live. For tests and diagnostics - a
    /// stream that leaks its subscriber is invisible without it.
    let internal count () = subscribers.Count

/// Set `docId`'s ingestion status and publish it to any subscriber.
let setStatus (docId: string) (status: IngestionStatus) : unit =
    statusCache.AddOrUpdate(docId, status, (fun _ _ -> status)) |> ignore
    IngestionStatusFeed.publish docId status

/// Set `docId`'s ingestion status through an update function, publishing
/// the value the cache SETTLED on rather than the one offered — the
/// enqueue paths deliberately decline to overwrite a fresher value (one
/// already advanced by a racing chunk callback), and a subscriber must
/// see what is true, not what was proposed.
let updateStatus (docId: string) (addValue: IngestionStatus) (update: IngestionStatus -> IngestionStatus) : unit =
    let settled =
        statusCache.AddOrUpdate(docId, addValue, (fun _ existing -> update existing))

    IngestionStatusFeed.publish docId settled

/// Forget `docId`'s ingestion status — the document was deleted, reset or
/// swept. Publishes a terminal `Failed` so a subscriber watching an
/// in-flight ingestion is told it will not finish instead of waiting for
/// a transition that can no longer come. A document that had already
/// reached a terminal status has no subscriber left to hear it.
let clearStatus (docId: string) : unit =
    statusCache.TryRemove docId |> ignore
    IngestionStatusFeed.publish docId (IngestionStatus.Failed "the document was removed before ingestion finished")

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
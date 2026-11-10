module KnowledgeBase.ServerIndexStorage

open System
open System.Collections.Concurrent
open System.Text
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open SharedTypes
open KnowledgeBase.ServerJsonHelpers
open ToolUp.Platform.Infra
open ToolUp.Platform.Config

// ─── Document index persistence ───────────────────────────────────

let indexBlobName = "knowledge/index.json"

/// Where a `Note` document persists its markdown body — `addNote` /
/// `updateNote` write it here, `IOriginalSourceResolver` serves it from
/// here, and `deleteDocument` and the retention sweep
/// remove it from here. NOT the convention original path
/// (`knowledge/{docId}/{FileName}`): a note's `FileName` is
/// `"{title}.md"`, which never coincides with this blob unless the
/// sanitised title happens to be `note`.
let noteBodyBlobName (docId: string) = sprintf "knowledge/%s/note.md" docId

/// normalise a document read off the wire. A pre-510
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

/// the same treatment for `Tags`, and for a sharper
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

/// Raised by the guarded index writers when the read-modify-write could
/// not complete — the index was unreadable or undecodable, the write
/// failed, or the retry budget was exhausted. Nothing was written. Every
/// caller runs inside a `try … with` that logs or routes the failure (the
/// upload handler surfaces it to the client, the ingestion observer logs
/// it), which is the point: before Phase 864 each of these cases was a
/// silent success. `saveIndex` raises it too, for a refused
/// whole-index write.
exception KnowledgeIndexWriteFailed of message: string

/// a refused write raises `KnowledgeIndexWriteFailed` rather
/// than reading as saved.
let saveIndex (storage: IBlobStorage) (container: string) (docs: KnowledgeDocument list) = async {
    let bytes = (toJson docs: string) |> Encoding.UTF8.GetBytes

    match! storage.Upload(container, indexBlobName, bytes) with
    | Ok _ -> return ()
    | Error storageError -> return raise (KnowledgeIndexWriteFailed(sprintf "index write refused: %s" storageError))
}

// ─── Guarded index writes ─────────────────────────────────────────
//
// Every production write of `index.json` is a read-modify-write of one
// shared blob, and every one goes through `updateIndexEntries` below — the
// one place a new writer goes. The named writers (`upsertIndexEntry`,
// `updateIndexEntry`, `updateIndexStatus`, `removeIndexEntries`) are thin
// wrappers over it, and `appendVersion` runs
// the same helper over the per-document version history. Through
// `BlobMapStore`:
//
//   * an index that exists and cannot be read, or does not decode, is never
//     read as empty and overwritten with empty-plus-one — the write is
//     refused and `KnowledgeIndexWriteFailed` is raised (the undecodable
//     bytes are copied aside for recovery);
//   * on a backend implementing `IConditionalBlobStorage`, writers on
//     different replicas lose no update — a lost precondition re-reads and
//     replays the change.
//
// `saveIndex` above is not guarded: it is a whole-index overwrite (a
// refused one raises) kept for seeding a fixed index, and no production
// writer calls it — a load-map-save over it is exactly the lost update this
// section exists to prevent.

let internal indexLogger = ConsoleLogger.ConsoleLogger() :> ILogger

let private indexCodec: BlobCodec<KnowledgeDocument list> = {
    Encode = fun docs -> (toJson docs: string) |> Encoding.UTF8.GetBytes
    Decode =
        fun content ->
            try
                Ok(
                    fromJson<KnowledgeDocument list> (Encoding.UTF8.GetString content)
                    |> List.map (normaliseVersion >> normaliseTags)
                )
            with ex ->
                Error ex.Message
}

/// Why a scope's index could not be read. Absence is not one of them: a
/// scope that has never held a document has no index, and reads as empty.
type IndexReadError =
    /// The index exists and the storage could not read it.
    | Unreadable of reason: string
    /// The index was read and did not decode. Its bytes were copied aside
    /// (`knowledge/index.json.corrupt-<sha256 prefix>`) and the index itself
    /// left untouched.
    | Corrupt of reason: string

[<RequireQualifiedAccess>]
module IndexReadError =

    /// The sentence a refusal and the operator log carry.
    let describe (error: IndexReadError) : string =
        match error with
        | Unreadable reason -> sprintf "the knowledge index for this scope could not be read (%s)" reason
        | Corrupt reason -> sprintf "the knowledge index for this scope is corrupt (%s)" reason

/// Read the scope's document index. An absent index is `Ok []`; an index
/// that exists and cannot be read, or does not decode, is an `Error`, never
/// an empty list. Whatever decides from the index — quota, dedup, versioning,
/// deletion, retention — refuses on the `Error`; a listing renders it with
/// `loadListing`.
let loadIndex (storage: IBlobStorage) (container: string) : Async<Result<KnowledgeDocument list, IndexReadError>> = async {
    match! BlobSnapshot.read storage indexLogger container indexBlobName indexCodec with
    | SnapshotLoad.Loaded documents -> return Ok documents
    | SnapshotLoad.Absent -> return Ok []
    | SnapshotLoad.Unreadable reason -> return Error(Unreadable reason)
    | SnapshotLoad.Corrupt(_, reason) -> return Error(Corrupt reason)
}

/// The scope's documents for a surface that lists them: an unreadable index
/// renders as an empty list, and the failure is recorded at `Error` on
/// `logger` so an operator can see why the scope looks empty.
let loadListing (logger: ILogger) (storage: IBlobStorage) (container: string) : Async<KnowledgeDocument list> = async {
    match! loadIndex storage container with
    | Ok documents -> return documents
    | Error error ->
        logger.Error(
            sprintf "[KnowledgeBase] %s; listing the scope '%s' as empty." (IndexReadError.describe error) container,
            None
        )

        return []
}

// One `BlobMapStore` per storage instance, so its capability probe and its
// one-time fallback warning are paid once per backend, not once per write.
let private indexStores =
    System.Runtime.CompilerServices.ConditionalWeakTable<IBlobStorage, BlobMapStore<KnowledgeDocument list>>()

let private indexStoreFor (storage: IBlobStorage) =
    indexStores.GetValue(storage, fun s -> BlobMapStore<KnowledgeDocument list>(s, indexCodec, indexLogger))

let private raiseOnFailure (result: Result<'R, BlobMapStoreError>) : 'R =
    match result with
    | Ok value -> value
    | Error e -> raise (KnowledgeIndexWriteFailed(BlobMapStoreError.describe e))

/// the guarded read-modify-write of `index.json`, and the one
/// place an index writer goes. `change` is pure over the current documents
/// (an absent index is the empty list) and decides with `BlobUpdate`:
/// `Write(documents, result)` persists, `Keep result` writes nothing. It may
/// run more than once — on a lost precondition it is replayed over the
/// fresh read — so it must not have effects; act on the returned `result`
/// instead. A write that cannot complete raises `KnowledgeIndexWriteFailed`
/// and writes nothing.
///
/// This takes no lock. On a backend with conditional writes the write is
/// safe against every other writer without one; on a backend without them
/// the in-process `acquireContainerLock` is what serialises writers, so a
/// caller holds it around this call (the named writers below take it
/// themselves; the ingestion observer holds it across its own read of the
/// attempt marker). Never call a named writer from inside the lock — the
/// semaphore is non-reentrant.
let updateIndexEntries
    (storage: IBlobStorage)
    (container: string)
    (change: KnowledgeDocument list -> BlobUpdate<KnowledgeDocument list, 'R>)
    : Async<'R> =
    async {
        let! result =
            (indexStoreFor storage)
                .Update(container, indexBlobName, fun current -> change (Option.defaultValue [] current))

        return raiseOnFailure result
    }

/// Guarded write of `index.json` where `change` returns `None` for
/// "nothing to write" — the shape the named writers share.
let private updateIndex
    (storage: IBlobStorage)
    (container: string)
    (change: KnowledgeDocument list -> KnowledgeDocument list option)
    =
    updateIndexEntries storage container (fun existing ->
        match change existing with
        | Some updated -> BlobUpdate.Write(updated, ())
        | None -> BlobUpdate.Keep())

/// whether index writes on this storage run as conditional
/// writes, asked through the same `BlobMapStore` (and so the same cached
/// capability probe) the writers use. `false` means a writer on another
/// replica can lose an index update.
let indexWritesAreConditional (storage: IBlobStorage) (container: string) : Async<bool> =
    (indexStoreFor storage).SupportsConditionalWrites(container, indexBlobName)

// ─── version + chunk-hash sidecars ────────────────────
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

    // a refused write must not leave the PREVIOUS manifest
    // standing: it describes content the indexes no longer hold, so a later
    // version matching it would diff as "unchanged" and skip chunks that are
    // not there. An ABSENT manifest only costs a full re-embed, so a refused
    // write falls back to deleting it, and raises only if that fails too.
    match! storage.Upload(container, chunkHashesBlobName docId, bytes) with
    | Ok _ -> return ()
    | Error writeError ->
        match! storage.Delete(container, chunkHashesBlobName docId) with
        | Ok() ->
            indexLogger.Warn(
                sprintf
                    "[KnowledgeBase] chunk-hash manifest for %s could not be written (%s); cleared instead, so the next re-upload re-embeds every chunk"
                    docId
                    writeError
            )
        | Error deleteError ->
            return
                raise (
                    KnowledgeIndexWriteFailed(
                        sprintf
                            "chunk-hash manifest for %s could not be written (%s) nor cleared (%s)"
                            docId
                            writeError
                            deleteError
                    )
                )
}

// ─── Change notification over ingestion status ────────────────────
//
// An in-process, per-document subscriber list the knowledge base's attempt
// store publishes every transition to: the seam the ingestion-status stream
// observes. It holds no status — the attempt record and the index are the
// status — and a deployment with no subscriber pays a dictionary emptiness
// check per transition (GP 13). A chunk indexed on another instance is that
// instance's event; the terminal `INotificationChannel` publish (which a
// distributed channel companion fans out) remains the cross-instance signal.
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

/// Tell `docId`'s subscribers its ingestion will not finish — the document
/// was deleted, reset or swept. Publishes a terminal `Failed` so a
/// subscriber watching an in-flight ingestion is not left waiting for a
/// transition that can no longer come. A document that had already reached
/// a terminal status has no subscriber left to hear it.
let clearStatus (docId: string) : unit =
    IngestionStatusFeed.publish docId (IngestionStatus.Failed "the document was removed before ingestion finished")

// Per-container lock used to serialise read-modify-write of `index.json`
// from the observer. Index updates are rare (one per chunk completion)
// and IO-bound, so a simple semaphore-per-container avoids cross-team
// contention without introducing a global lock.
let private containerLocks = ConcurrentDictionary<string, SemaphoreSlim>()

let acquireContainerLock (container: string) =
    containerLocks.GetOrAdd(container, fun _ -> new SemaphoreSlim(1, 1))

let private versionCodec: BlobCodec<KnowledgeDocumentVersion list> = {
    Encode = fun versions -> (toJson versions: string) |> Encoding.UTF8.GetBytes
    Decode =
        fun content ->
            try
                match fromJson<KnowledgeDocumentVersion list> (Encoding.UTF8.GetString content) with
                | versions when isNull (box versions) -> Error "version history decoded to null"
                | versions -> Ok versions
            with ex ->
                Error ex.Message
}

let private versionStores =
    System.Runtime.CompilerServices.ConditionalWeakTable<IBlobStorage, BlobMapStore<KnowledgeDocumentVersion list>>()

let private versionStoreFor (storage: IBlobStorage) =
    versionStores.GetValue(storage, fun s -> BlobMapStore<KnowledgeDocumentVersion list>(s, versionCodec, indexLogger))

/// append one superseded-version record under the container
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
///
/// a guarded read-modify-write: an unreadable or undecodable
/// history raises `KnowledgeIndexWriteFailed` instead of being replaced by
/// a one-record list, and on a conditional backend a concurrent writer on
/// another replica cannot drop this record.
let appendVersion (storage: IBlobStorage) (container: string) (record: KnowledgeDocumentVersion) = async {
    let lock = acquireContainerLock container
    do! lock.WaitAsync() |> Async.AwaitTask

    try
        let! result =
            (versionStoreFor storage)
                .Update(
                    container,
                    versionsBlobName record.DocumentId,
                    fun current ->
                        // Idempotent on version number: a retried supersede
                        // must not double-record the same version.
                        let updated =
                            (Option.defaultValue [] current
                             |> List.filter (fun v -> v.Version <> record.Version))
                            @ [ record ]
                            |> List.sortBy _.Version

                        BlobUpdate.Write(updated, ())
                )

        raiseOnFailure result
    finally
        lock.Release() |> ignore
}

/// atomic add-or-replace of a single document in the index,
/// under the container lock. Loads, drops any existing entry with the
/// same `Id`, appends `doc`, and saves — all while holding the lock so
/// two concurrent additive writers (upload / addNote / updateNote /
/// narrative) cannot each load the same index, append their own document,
/// and have the second save clobber the first (one document silently
/// lost from the index while its blob + chunks persist orphaned).
///
/// The lock is the in-process serialisation. Since Phase 864 the write is
/// also a guarded read-modify-write (see "guarded index writes" above): on a
/// backend implementing `IConditionalBlobStorage` it holds across replicas,
/// and an unreadable or undecodable index raises `KnowledgeIndexWriteFailed`
/// rather than being overwritten with this one document.
///
/// **Do not call from inside another `acquireContainerLock` critical
/// section** — the semaphore is non-reentrant. Callers acquire it only
/// around the index RMW and release before any background / enqueue work
/// that itself writes the index (`updateIndexEntry`).
let upsertIndexEntry (storage: IBlobStorage) (container: string) (doc: KnowledgeDocument) = async {
    let lock = acquireContainerLock container
    do! lock.WaitAsync() |> Async.AwaitTask

    try
        do!
            updateIndex storage container (fun existing ->
                Some(existing |> List.filter (fun d -> d.Id <> doc.Id) |> List.append [ doc ]))
    finally
        lock.Release() |> ignore
}

/// Apply `change` to one document's index entry through the guarded index
/// writer, under the container lock, and return the entry as written —
/// `None` when the document is not in the index (deleted between writes),
/// which writes nothing.
let updateIndexEntry
    (storage: IBlobStorage)
    (container: string)
    (docId: string)
    (change: KnowledgeDocument -> KnowledgeDocument)
    : Async<KnowledgeDocument option> =
    async {
        let lock = acquireContainerLock container
        do! lock.WaitAsync() |> Async.AwaitTask

        try
            return!
                updateIndexEntries storage container (fun existing ->
                    match existing |> List.tryFind (fun d -> d.Id = docId) with
                    | None -> BlobUpdate.Keep None
                    | Some doc ->
                        let updated = change doc

                        BlobUpdate.Write(
                            existing |> List.map (fun d -> if d.Id = docId then updated else d),
                            Some updated
                        ))
        finally
            lock.Release() |> ignore
    }

/// Set one document's persisted status. No-op when the document is not in
/// the index.
let updateIndexStatus (storage: IBlobStorage) (container: string) (docId: string) (newStatus: IngestionStatus) =
    updateIndexEntry storage container docId (fun d -> { d with Status = newStatus })
    |> Async.Ignore

// ─── The document's ingestion attempt ─────────────────────────────
//
// A document's current attempt and its progress live beside it, under its
// own `knowledge/{docId}/` prefix like the version sidecars, so a scope
// reset sheds the record by prefix and `deleteDocument` / the retention
// sweep remove it by name. The index entry carries the attempt's status
// only when it changes class (seeded in flight at `Begin`, settled at the
// terminal); the progress between the two is the record's alone.

/// Where a document's current ingestion attempt is persisted.
let ingestionAttemptBlobName (docId: string) =
    sprintf "knowledge/%s/ingestion-attempt.json" docId

// One record store per storage instance, so its capability probe is paid
// once per backend and its per-record lock is shared by every caller.
let private attemptStores =
    System.Runtime.CompilerServices.ConditionalWeakTable<IBlobStorage, AttemptRecords<IngestionStatus>>()

/// The knowledge base's attempt records over `storage`.
let attemptRecords (storage: IBlobStorage) : AttemptRecords<IngestionStatus> =
    attemptStores.GetValue(storage, fun s -> AttemptRecords<IngestionStatus>(s, indexLogger))

/// A document's status: its attempt's while that attempt is in flight, else
/// the index entry's. `None` when the document is not in the index.
let persistedStatus (storage: IBlobStorage) (container: string) (docId: string) : Async<IngestionStatus option> = async {
    let! record = (attemptRecords storage).Read(container, ingestionAttemptBlobName docId)

    match record with
    | Some record when
        not (isNull (box record.Status))
        && not (IngestionStatus.isTerminal record.Status)
        ->
        return Some record.Status
    | _ ->
        let! index = loadListing indexLogger storage container
        return index |> List.tryFind (fun d -> d.Id = docId) |> Option.map _.Status
}

/// Remove documents from the index. Same locking semantics as
/// `updateIndexEntry`; the removal is computed over the index as it stands
/// at the write, so a document added concurrently (on this replica or, on a
/// conditional backend, any other) survives. Writes nothing when none of
/// `docIds` is listed. Used by the delete path and the retention sweep.
let removeIndexEntries (storage: IBlobStorage) (container: string) (docIds: string list) = async {
    let removing = Set.ofList docIds
    let lock = acquireContainerLock container
    do! lock.WaitAsync() |> Async.AwaitTask

    try
        do!
            updateIndex storage container (fun existing ->
                if existing |> List.exists (fun d -> removing.Contains d.Id) then
                    Some(existing |> List.filter (fun d -> not (removing.Contains d.Id)))
                else
                    None)
    finally
        lock.Release() |> ignore
}
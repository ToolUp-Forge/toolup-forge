module ToolUp.Platform.DataObjectStore

open System
open System.Text
open System.Security.Cryptography
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Storage layout ──────────────────────────────────────────────
//
// Container: scope-derived (`team-{teamId}` / `user-{userId}` /
// `session-{sessionId}`). Cross-scope leakage is structural — every
// read and write derives the container from `scopeId`, never from
// blob name parsing.
//
// Layout:
//   {container}/objects/{objectId}/v{N}.json      version metadata
//   {container}/objects/_content/{hash}.data      deduplicated content
//
// Why one blob per version:
// - Append-only is achieved by never reading-then-writing a single
//   blob; every save produces a fresh `v{N}.json` (and at most one
//   fresh `_content/{hash}.data`). No atomic-append primitive
//   required across blob backends.
//
// Why `_content/` lives inside `objects/`:
// - Eviction (Purge) lists a single prefix and deletes everything;
//   no second pass needed.
// - The reserved objectId `_content` is filtered out of `ListVersions`
//   and the future `IDataCatalog.ListObjects` (Phase 7a) — admins
//   never see it.
//
// Concurrency:
// - Concurrent saves to the same `(scopeId, objectId)` race on
//   "max version + 1". The loser's metadata is overwritten but the
//   loser's content is dedup'd into `_content/`, so no data is lost.
//   Single-writer-per-object is the common case; cross-shard
//   ordering is not promised (Phase 9c Rule 5).

let private objectsRoot = "objects/"

let private contentPrefix = "objects/_content/"

let private reservedContentObjectId = "_content"

/// Cap on concurrent metadata-blob downloads. `ListVersions` and
/// `ListObjects` fan out one read per (version | object); without a
/// cap, a frequently-versioned object or a populous scope can spawn
/// thousands of concurrent tasks and saturate the .NET thread pool,
/// stalling unrelated requests during the burst. 16 keeps blob-store
/// throughput high without monopolising the pool — tune downward if
/// the storage backend has tighter per-connection limits.
let private metadataReadParallelism = 16

let private objectPrefix (objectId: string) = $"objects/{objectId}/"

let private versionBlobName (objectId: string) (version: int) = $"objects/{objectId}/v{version}.json"

let private contentBlobName (hash: string) = $"objects/_content/{hash}.data"

// ─── Hashing ────────────────────────────────────────────────────
//
// SHA-256-hex helper — duplicated from `CachingEmbeddingProvider.fs:11–19`
// and `RetrievalTracers.fs:19–33`. A future cleanup pass should extract
// this to `Server/HashHelpers.fs`; see Phase 7 commit history for the
// rationale (the helper is .NET-only — `System.Security.Cryptography` is
// not Fable-compatible — so it belongs in a Server-side shared module
// rather than `Shared/`).

let private sha256Hex (content: byte[]) : string =
    use sha = SHA256.Create()
    let bytes = sha.ComputeHash(content)
    let sb = StringBuilder(bytes.Length * 2)

    for b in bytes do
        sb.AppendFormat("{0:x2}", b) |> ignore

    sb.ToString()

// ─── JSON serialisation ──────────────────────────────────────────
//
// Match `ConfigStore.fs:16–26` / `TeamManagement.fs` — STJ +
// `FableConverters`. The DataObject record carries a
// `Map<string, string>` and a `VersioningPolicy` DU; FableConverters
// round-trips both losslessly.

module private Json =
    let private options = FableConverters.create ()

    let serialize (value: DataObject) : byte[] =
        JsonSerializer.Serialize(value, options) |> Encoding.UTF8.GetBytes

    let tryDeserialize (bytes: byte[]) : DataObject option =
        try
            let json = Encoding.UTF8.GetString(bytes)
            Some(JsonSerializer.Deserialize<DataObject>(json, options))
        with _ ->
            None

// ─── Path parsing ────────────────────────────────────────────────
//
// `IBlobStorage.List` returns names relative to the container root,
// but `LocalFileStorage` uses the OS path separator (backslash on
// Windows). Normalise on input so parsing is portable across backends.

let private normalize (blobName: string) = blobName.Replace('\\', '/')

/// Parse `objects/{objectId}/v{N}.json` → `Some N`. Returns `None`
/// when the name doesn't match (e.g. a `_content/{hash}.data` entry
/// caught by a broader prefix list).
let private parseVersion (objectId: string) (blobName: string) : int option =
    let normalised = normalize blobName
    let prefix = objectPrefix objectId

    if normalised.StartsWith(prefix) && normalised.EndsWith(".json") then
        let middle = normalised.Substring(prefix.Length)

        if middle.StartsWith("v") then
            let numPart = middle.Substring(1, middle.Length - 1 - ".json".Length)

            match Int32.TryParse(numPart) with
            | true, n when n > 0 -> Some n
            | _ -> None
        else
            None
    else
        None

/// Extract the objectId from a normalised `objects/{id}/...` path.
/// Returns `None` for the `_content` pseudo-object so callers can
/// filter it out.
let private parseObjectId (blobName: string) : string option =
    let normalised = normalize blobName

    if normalised.StartsWith(objectsRoot) then
        let tail = normalised.Substring(objectsRoot.Length)
        let slash = tail.IndexOf('/')

        if slash < 0 then
            None
        else
            let objectId = tail.Substring(0, slash)

            if objectId = reservedContentObjectId then
                None
            else
                Some objectId
    else
        None

// ─── Read helpers ────────────────────────────────────────────────

/// Map an `IBlobStorage` `string` error into a typed `DataObjectError`.
let private liftStorage (result: Result<'a, string>) : Result<'a, DataObjectError> =
    match result with
    | Ok v -> Ok v
    | Error msg -> Error(StorageFailure msg)

/// List all `v{N}.json` metadata blob names for an object, paired
/// with the parsed version number, sorted ascending. Empty list when
/// the object does not exist.
let private listVersionBlobs
    (blobStorage: IBlobStorage)
    (container: string)
    (objectId: string)
    : Async<(int * string) list> =
    async {
        let! names = blobStorage.List(container, objectPrefix objectId)

        return
            names
            |> List.choose (fun name ->
                match parseVersion objectId name with
                | Some n -> Some(n, name)
                | None -> None)
            |> List.sortBy fst
    }

/// Download and deserialize a `DataObject` from a metadata blob.
/// Returns `None` on storage error or parse failure — callers map to
/// the appropriate `DataObjectError` themselves.
let private downloadMetadata
    (blobStorage: IBlobStorage)
    (container: string)
    (blobName: string)
    : Async<DataObject option> =
    async {
        let! result = blobStorage.Download(container, blobName)

        match result with
        | Ok bytes -> return Json.tryDeserialize bytes
        | Error _ -> return None
    }

// ─── Orphaned content (Phase 7c) ─────────────────────────────────
//
// A content blob is *orphaned* when no surviving `v{N}.json` metadata
// blob in the same container names its hash. Two ways that happens:
//
//   1. A `Delete` / `Evict` / `Erase` removed the last metadata blob
//      referencing it — the in-band case `collectOrphanedContent`
//      reclaims inline, on the same call, for anything older than its
//      own short grace window (Phase 634).
//   2. **A `Save` wrote its content blob and then died before writing
//      its metadata blob.** `Save` is content-first by design (the
//      metadata blob names a hash that must already exist), so a crash,
//      a pod kill, or a storage error between the two writes leaves a
//      content blob nothing will ever reference again. Nothing reclaims
//      it: only `Delete` ran orphan GC, and this object was never
//      created, so nothing is ever deleted. That residue is what the
//      Phase 7c scheduled sweep exists for.
//
// The listing is factored out here so the sweep and the in-band GC read
// orphanhood from ONE definition. A second, parallel definition is
// exactly how a sweep and a store drift into disagreeing about what is
// reclaimable — and this pool is content-addressable, so disagreement is
// not a cosmetic bug: reclaiming a live hash silently breaks every
// version that points at it.

/// Phase 7c — an unreferenced content blob in a scope's dedup pool,
/// with the store-reported facts the sweep needs to decide whether it is
/// past its grace window and to record what it reclaimed.
type OrphanedContentBlob = {
    /// Lowercase SHA-256 hex the blob is keyed by (parsed from its
    /// `_content/{hash}.data` name).
    ContentHash: string
    /// Container-relative blob name, `/`-normalised.
    BlobName: string
    /// Size in bytes, as `IBlobStorage.GetMetadata` reported it.
    SizeBytes: int64
    /// Last write time in UTC. The grace window is measured against
    /// this, so an in-flight `Save` whose metadata write is merely slow
    /// is never mistaken for a crash residue.
    LastModified: DateTime
}

/// Container-relative names of every `_content/{hash}.data` blob no
/// surviving metadata blob in `container` references. Shared by the
/// in-band GC (`collectOrphanedContent`) and the Phase 7c sweep.
let private orphanedContentNames (blobStorage: IBlobStorage) (container: string) : Async<string list> = async {
    let! allNames = blobStorage.List(container, objectsRoot)
    let normalised = allNames |> List.map normalize

    // Set of hashes still in use, derived from every surviving
    // metadata blob. A blob whose name doesn't parse as
    // v{N}.json under some objectId is skipped.
    let metadataNames =
        normalised
        |> List.filter (fun n -> not (n.StartsWith(contentPrefix)) && n.EndsWith(".json"))

    let! metadatas =
        metadataNames
        |> List.map (downloadMetadata blobStorage container)
        |> fun xs -> Async.Parallel(xs, metadataReadParallelism)

    let referenced =
        metadatas |> Array.choose id |> Array.map _.ContentHash |> Set.ofArray

    return
        normalised
        |> List.filter (fun n ->
            if n.StartsWith(contentPrefix) && n.EndsWith(".data") then
                let hash =
                    n.Substring(contentPrefix.Length, n.Length - contentPrefix.Length - ".data".Length)

                not (referenced.Contains hash)
            else
                false)
}

/// Phase 634 — the grace window the IN-BAND GC applies to candidates
/// the calling operation did NOT itself release (the pass that runs on
/// the tail of `Delete` / `Evict` / `Erase`). Such a blob, younger than
/// this, is left for a later pass rather than reclaimed inline, because
/// at that age it is indistinguishable from a concurrent `Save` that has
/// written its content and not yet its metadata.
///
/// **This is the same value as `DataObjectOrphanSweepPolicy.Minimum-
/// GracePeriod`** — the floor the scheduled sweep clamps *up* to,
/// refusing a caller who asks for zero. It is duplicated rather than
/// referenced only because that module compiles ~327 files later and
/// cannot be named from here; the duplication is pinned by a test that
/// asserts the in-band boundary lands exactly on the sweep's published
/// minimum, so a change to one that is not made to the other goes red.
///
/// **Deliberately not a config knob.** The sweep's tunable
/// `GracePeriod` (default 24h) is the wrong value for this pass and its
/// policy record is unreachable from here in any case: honouring 24h
/// in-band would mean a `Delete` reclaimed nothing for a day, so a
/// deployment that never composed the sweep — the default — would leak
/// every deleted object's content indefinitely, trading a rare
/// data-loss race for a certain storage leak. The floor is the whole
/// requirement here: it need only exceed the content-write→metadata-
/// write gap of a live `Save`, which is milliseconds.
let private inBandOrphanGracePeriod = TimeSpan.FromMinutes 5.0

/// Phase 7c — orphaned content blobs in `container`, each stamped with
/// its size and last-write time. `container` is the scope's own
/// container and nothing else is enumerated, so the reach of one call is
/// exactly one scope (GP 4) — `IBlobStorage` has no cross-container
/// enumeration and this deliberately does not invent one.
///
/// A blob that vanishes between the list and the stat (a concurrent
/// `Delete`'s in-band GC got there first) is dropped rather than
/// reported: it is already reclaimed, which is the outcome the caller
/// wanted.
let listOrphanedContent (blobStorage: IBlobStorage) (container: string) : Async<OrphanedContentBlob list> = async {
    let! names = orphanedContentNames blobStorage container

    let! stamped =
        names
        |> List.map (fun name -> async {
            let! meta = blobStorage.GetMetadata(container, name)

            let hash =
                name.Substring(contentPrefix.Length, name.Length - contentPrefix.Length - ".data".Length)

            match meta with
            | Ok m ->
                return
                    Some {
                        ContentHash = hash
                        BlobName = name
                        SizeBytes = m.Size
                        LastModified = m.LastModified
                    }
            | Error _ -> return None
        })
        |> fun xs -> Async.Parallel(xs, metadataReadParallelism)

    return stamped |> Array.choose id |> Array.toList
}

// ─── Store ───────────────────────────────────────────────────────
//
// Stateless implementation: every method derives its result from
// parameters + `IBlobStorage`. No in-memory cache, no shared mutable
// state. Phase 9c Rule 4 — an Orleans grain or Akka actor running
// this could deactivate / restart between any two operations and
// behave identically.

/// Resolve the storage container for a `scopeId`. Anonymous,
/// authenticated-ephemeral, individual, and team scopes all already
/// embed their kind in the scopeId itself (`team-{teamId}` etc.) so
/// the container name is the scopeId. The `IBlobStorage` contract
/// (line 24-34 docstring) requires container names match a known
/// prefix — non-conforming scopeIds will be flagged by the
/// implementation's audit path.
///
/// Public since Phase 7c so the orphan sweep resolves a scope's
/// container through the same mapping the store writes through, rather
/// than re-deriving it — a sweep pointed at a different container than
/// the store writes to would either reclaim nothing or reclaim
/// another scope's blobs, and both failures are silent.
let containerFor (scopeId: string) = scopeId

type DataObjectStore(blobStorage: IBlobStorage, ?logger: ILogger) =

    let log = logger

    let logWarn (msg: string) =
        match log with
        | Some l -> l.Warn msg
        | None -> ()

    /// Read v1's metadata (when present) to enforce sticky policy.
    /// Returns `None` if v1 doesn't exist (i.e., this is the first
    /// save) or the metadata blob is unreadable.
    let readV1Policy (container: string) (objectId: string) : Async<VersioningPolicy option> = async {
        let! v1 = downloadMetadata blobStorage container (versionBlobName objectId 1)

        return v1 |> Option.map _.Policy
    }

    /// Idempotent content upload. Skips the upload when the
    /// content blob already exists (dedup hit) so identical content
    /// across versions writes only the metadata.
    let uploadContentIfMissing
        (container: string)
        (hash: string)
        (content: byte[])
        : Async<Result<unit, DataObjectError>> =
        async {
            let blobName = contentBlobName hash
            let! exists = blobStorage.Exists(container, blobName)

            if exists then
                return Ok()
            else
                let! result = blobStorage.Upload(container, blobName, content)

                return result |> liftStorage |> Result.map (fun _ -> ())
        }

    /// Garbage-collect orphaned content blobs after a delete. Delegates
    /// the "which blobs are orphaned" question to `listOrphanedContent`
    /// (Phase 7c) so this in-band pass and the scheduled sweep share one
    /// definition of orphanhood, then deletes what it names — subject to
    /// the Phase 634 rule below.
    ///
    /// `releasedHashes` are the content hashes named by the metadata the
    /// CALLING operation just removed or rewrote. Anything else that
    /// looks orphaned is something this operation knows nothing about.
    ///
    /// **A candidate is reclaimed when it was released by this
    /// operation, OR it is older than `inBandOrphanGracePeriod`.**
    /// Phase 634; before it, every candidate was reclaimed
    /// unconditionally, on the argument that an orphan seen on the tail
    /// of the caller's own `Delete` is a consequence of the operation in
    /// progress rather than a suspected crash residue. That argument is
    /// sound — for the blobs this caller released. It says nothing about
    /// the blob a CONCURRENT `Save` in the same scope wrote a
    /// millisecond ago and has not yet named from its metadata. To an
    /// unconditional pass that blob is indistinguishable from a crash
    /// residue, and reclaiming it destroys LIVE content: the writer's
    /// `Save` returns `Ok`, and every subsequent read of the object
    /// fails with `StorageFailure`, permanently, with nothing having
    /// errored at write time. Strictly worse than the crash-orphan leak
    /// Phase 7c closed, which costs storage rather than data.
    ///
    /// **Why the released set and not the window alone.** Removing bytes
    /// the caller just dereferenced is a contract, not an optimisation:
    /// an erasure sweep's guarantee is that the subject's bytes are gone
    /// at rest, not merely unreferenced — Phase 105 pins exactly that on
    /// both `Delete` and `Erase`. Deferring those to a scheduled sweep a
    /// deployment may never have composed would trade a data-loss race
    /// for a GDPR regression. The window is therefore applied only to
    /// candidates this operation did NOT release — which is precisely
    /// the set that can contain a live in-flight `Save`.
    ///
    /// **Residual, out of scope and unfixable by any window:** a
    /// concurrent `Save` whose content is BYTE-IDENTICAL to something
    /// being deleted dedups onto the existing blob (`uploadContentIf-
    /// Missing` skips the upload), so its hash is in `releasedHashes`
    /// and its blob is old. Neither arm of this rule protects it, and
    /// neither does the scheduled sweep's window. Closing that needs
    /// reference counting or a write lease over the dedup pool, not an
    /// age heuristic.
    ///
    /// The stat this needs (`LastModified`) is why it now goes through
    /// `listOrphanedContent` rather than `orphanedContentNames`. One
    /// consequence, deliberate and in the safe direction: a candidate
    /// whose `GetMetadata` fails is dropped rather than deleted, exactly
    /// as it is for the scheduled sweep.
    let collectOrphanedContent (container: string) (releasedHashes: Set<string>) : Async<int> = async {
        let! candidates = listOrphanedContent blobStorage container
        let cutoff = DateTime.UtcNow - inBandOrphanGracePeriod

        let toDelete =
            candidates
            |> List.filter (fun o -> releasedHashes.Contains o.ContentHash || o.LastModified <= cutoff)
            |> List.map _.BlobName

        let! _ =
            toDelete
            |> List.map (fun name -> blobStorage.Delete(container, name))
            |> Async.Parallel

        return toDelete.Length
    }

    /// Content hashes named by a set of version-metadata blobs. Read
    /// BEFORE those blobs are deleted — it is what tells the Phase 634
    /// in-band GC which of its candidates the operation itself released.
    let releasedContentHashes (container: string) (blobNames: string list) : Async<Set<string>> = async {
        let! metas =
            blobNames
            |> List.map (downloadMetadata blobStorage container)
            |> fun xs -> Async.Parallel(xs, metadataReadParallelism)

        return metas |> Array.choose id |> Array.map _.ContentHash |> Set.ofArray
    }

    // Phase 448.D follow-on — bounded ranged reads over the content-
    // addressed dedup pool, delegating to the backing storage's native
    // range support (Phase 455 `DownloadRange`). Over the encryption
    // decorator the delegate refuses (whole-blob AES-GCM ciphertext has
    // no readable mid-blob range) and the error propagates — capability
    // consumers treat it as the signal to fall back to `GetContent`.
    interface IContentRangeReader with
        member _.GetContentSize(scopeId, contentHash) = async {
            let container = containerFor scopeId
            let! result = blobStorage.GetMetadata(container, contentBlobName contentHash)
            return result |> liftStorage |> Result.map _.Size
        }

        member _.ReadContentRange(scopeId, contentHash, offset, length) = async {
            let container = containerFor scopeId

            let! result = blobStorage.DownloadRange(container, contentBlobName contentHash, offset, length)

            return liftStorage result
        }

    interface IDataObjectStore with
        member _.Save(scopeId, objectId, content, dataType, createdBy, metadata, policy) = async {
            if objectId = reservedContentObjectId then
                return Error(StorageFailure $"ObjectId '{reservedContentObjectId}' is reserved")
            else
                let container = containerFor scopeId

                // Sticky-policy enforcement: read v1 if present, fail
                // on mismatch.
                let! existingPolicy = readV1Policy container objectId

                match existingPolicy with
                | Some recorded when recorded <> policy -> return Error(PolicyMismatch(recorded, policy))
                | _ ->
                    let hash = sha256Hex content
                    let! contentResult = uploadContentIfMissing container hash content

                    match contentResult with
                    | Error err -> return Error err
                    | Ok() ->
                        // Determine next version. Unversioned
                        // always overwrites v1; Versioned /
                        // StrictlyVersioned increment.
                        let! nextVersion = async {
                            match policy with
                            | Unversioned -> return 1
                            | Versioned
                            | StrictlyVersioned ->
                                let! versions = listVersionBlobs blobStorage container objectId

                                return
                                    match versions with
                                    | [] -> 1
                                    | _ -> (versions |> List.map fst |> List.max) + 1
                        }

                        let dataObject = {
                            ObjectId = objectId
                            Version = nextVersion
                            CreatedAt = DateTime.UtcNow
                            CreatedBy = createdBy
                            ScopeId = scopeId
                            DataType = dataType
                            ContentHash = hash
                            Policy = policy
                            Metadata = metadata
                        }

                        let bytes = Json.serialize dataObject

                        let! upload = blobStorage.Upload(container, versionBlobName objectId nextVersion, bytes)

                        return upload |> liftStorage |> Result.map (fun _ -> dataObject)
        }

        member _.Get(scopeId, objectId) = async {
            let container = containerFor scopeId
            let! versions = listVersionBlobs blobStorage container objectId

            match versions with
            | [] -> return Error NotFound
            | _ ->
                let latestVersion, latestBlob = versions |> List.last
                let! metadata = downloadMetadata blobStorage container latestBlob

                match metadata with
                | None ->
                    logWarn $"Failed to read latest metadata for {scopeId}/{objectId}@v{latestVersion}"
                    return Error NotFound
                | Some obj ->
                    let! contentResult = blobStorage.Download(container, contentBlobName obj.ContentHash)

                    match contentResult with
                    | Ok content -> return Ok(obj, content)
                    | Error msg -> return Error(StorageFailure msg)
        }

        member _.GetVersion(scopeId, objectId, version) = async {
            if version <= 0 then
                return Error(VersionNotFound version)
            else
                let container = containerFor scopeId
                let blobName = versionBlobName objectId version
                let! metadata = downloadMetadata blobStorage container blobName

                match metadata with
                | None ->
                    // Distinguish "object exists, version doesn't"
                    // from "object never existed". Both surface as
                    // a missing v{N}.json — disambiguate with a
                    // v1 probe.
                    let! v1Exists = blobStorage.Exists(container, versionBlobName objectId 1)

                    if v1Exists then
                        return Error(VersionNotFound version)
                    else
                        return Error NotFound
                | Some obj ->
                    let! contentResult = blobStorage.Download(container, contentBlobName obj.ContentHash)

                    match contentResult with
                    | Ok content -> return Ok(obj, content)
                    | Error msg -> return Error(StorageFailure msg)
        }

        member _.GetContent(scopeId, contentHash) = async {
            // Content-addressable read: the bytes live at a per-scope,
            // hash-keyed blob, so a caller holding the `ContentHash`
            // (every `ListObjects` entry does) needs neither the version
            // list nor the metadata blob that `Get` / `GetVersion` fetch.
            let container = containerFor scopeId
            let! result = blobStorage.Download(container, contentBlobName contentHash)
            return liftStorage result
        }

        member _.ListVersions(scopeId, objectId) = async {
            let container = containerFor scopeId
            let! versions = listVersionBlobs blobStorage container objectId

            let! metadatas =
                versions
                |> List.map (fun (_, name) -> downloadMetadata blobStorage container name)
                |> fun xs -> Async.Parallel(xs, metadataReadParallelism)

            return metadatas |> Array.choose id |> Array.sortBy _.Version |> Array.toList
        }

        member _.ListObjects(scopeId) = async {
            let container = containerFor scopeId
            let! allNames = blobStorage.List(container, objectsRoot)

            // Group v*.json blobs by objectId. The `_content` pseudo-
            // object is filtered out by `parseObjectId`.
            let metadataByObject =
                allNames
                |> List.map normalize
                |> List.choose (fun name ->
                    if not (name.EndsWith(".json")) then
                        None
                    else
                        match parseObjectId name with
                        | None -> None
                        | Some objectId ->
                            match parseVersion objectId name with
                            | Some version -> Some(objectId, version, name)
                            | None -> None)
                |> List.groupBy (fun (objectId, _, _) -> objectId)

            // For each object, pick the highest-version metadata
            // blob. Download all of them in parallel.
            let latestBlobs =
                metadataByObject
                |> List.map (fun (_, entries) -> entries |> List.maxBy (fun (_, v, _) -> v) |> (fun (_, _, n) -> n))

            let! metadatas =
                latestBlobs
                |> List.map (downloadMetadata blobStorage container)
                |> fun xs -> Async.Parallel(xs, metadataReadParallelism)

            return metadatas |> Array.choose id |> Array.toList
        }

        member this.Recover(scopeId, objectId, version, createdBy) = async {
            let store = this :> IDataObjectStore
            let! sourceResult = store.GetVersion(scopeId, objectId, version)

            match sourceResult with
            | Error err -> return Error err
            | Ok(sourceObj, _content) ->
                let container = containerFor scopeId
                let! versions = listVersionBlobs blobStorage container objectId

                let nextVersion =
                    match versions with
                    | [] -> 1
                    | _ -> (versions |> List.map fst |> List.max) + 1

                let recoveredMetadata =
                    sourceObj.Metadata |> Map.add "_recovered_from" $"v{version}"

                let newObject = {
                    sourceObj with
                        Version = nextVersion
                        CreatedAt = DateTime.UtcNow
                        CreatedBy = createdBy
                        Metadata = recoveredMetadata
                }

                let bytes = Json.serialize newObject
                let! upload = blobStorage.Upload(container, versionBlobName objectId nextVersion, bytes)

                return upload |> liftStorage |> Result.map (fun _ -> newObject)
        }

        member _.Delete(scopeId, objectId) = async {
            let container = containerFor scopeId
            let! v1 = downloadMetadata blobStorage container (versionBlobName objectId 1)

            match v1 with
            | None ->
                // Idempotent: no v1 means the object has already
                // been deleted (or never existed).
                return Ok()
            | Some v1Obj when v1Obj.Policy = StrictlyVersioned -> return Error DeleteForbidden
            | Some _ ->
                let! versions = listVersionBlobs blobStorage container objectId

                // Phase 634 — read what these versions reference BEFORE
                // removing them, so the in-band GC can tell the content
                // this delete released (reclaim now, Phase 105's
                // bytes-gone-at-rest contract) from an unrelated orphan
                // that might be a concurrent `Save` mid-flight.
                let! released = releasedContentHashes container (versions |> List.map snd)

                let! _ =
                    versions
                    |> List.map (fun (_, name) -> blobStorage.Delete(container, name))
                    |> Async.Parallel

                let! _orphaned = collectOrphanedContent container released
                return Ok()
        }

        member _.Evict(scopeId, objectId) = async {
            // Same blob-removal path as `Delete`, but with no
            // `StrictlyVersioned` guard — eviction is an explicit
            // lifecycle choice by a caller that owns the object's
            // retention (see the interface doc-comment). Idempotent:
            // no version blobs means nothing to evict.
            let container = containerFor scopeId
            let! versions = listVersionBlobs blobStorage container objectId

            match versions with
            | [] -> return Ok()
            | _ ->
                // Phase 634 — same released-set read as `Delete`.
                let! released = releasedContentHashes container (versions |> List.map snd)

                let! _ =
                    versions
                    |> List.map (fun (_, name) -> blobStorage.Delete(container, name))
                    |> Async.Parallel

                let! _orphaned = collectOrphanedContent container released
                return Ok()
        }

        member _.Purge(scopeId) = async {
            let container = containerFor scopeId
            let! names = blobStorage.List(container, objectsRoot)

            let! results =
                names
                |> List.map (fun name -> blobStorage.Delete(container, name))
                |> Async.Parallel

            let firstError =
                results
                |> Array.tryPick (fun r ->
                    match r with
                    | Error msg -> Some msg
                    | Ok _ -> None)

            match firstError with
            | None -> return Ok()
            | Some msg -> return Error(StorageFailure msg)
        }

        member _.Erase(scopeId, subjectUserId, policy, dryRun) = async {
            if Erasure.isBlankSubject subjectUserId then
                return
                    Result.Ok {
                        HandlerName = "data-objects"
                        RecordsAffected = 0
                        Note = Some "blank subject — no-op (would otherwise match every object)"
                    }
            else
                let container = containerFor scopeId
                let! allNames = blobStorage.List(container, objectsRoot)

                let objectIds =
                    allNames |> List.map normalize |> List.choose parseObjectId |> List.distinct

                // Load every version's metadata per object.
                let! perObject =
                    objectIds
                    |> List.map (fun oid -> async {
                        let! vbs = listVersionBlobs blobStorage container oid

                        let! metas =
                            vbs
                            |> List.map (fun (_, name) -> async {
                                let! m = downloadMetadata blobStorage container name
                                return name, m
                            })
                            |> Async.Parallel

                        return oid, List.ofArray metas
                    })
                    |> Async.Parallel

                let namesSubject (m: DataObject) =
                    m.CreatedBy = subjectUserId
                    || (m.Metadata |> Map.exists (fun _ v -> v.Contains subjectUserId))

                let matched =
                    perObject
                    |> Array.filter (fun (_, metas) ->
                        metas
                        |> List.exists (fun (_, mo) ->
                            match mo with
                            | Some m -> namesSubject m
                            | None -> false))
                    |> Array.toList

                if dryRun || List.isEmpty matched then
                    let verb =
                        match policy with
                        | ErasurePolicy.HardDelete -> "removed"
                        | ErasurePolicy.Tombstone -> "tombstoned"
                        | ErasurePolicy.RetainPerCompliance -> "redacted"

                    return
                        Result.Ok {
                            HandlerName = "data-objects"
                            RecordsAffected = matched.Length
                            Note = Some(sprintf "%d object(s) would be %s in scope %s" matched.Length verb scopeId)
                        }
                else
                    // Phase 634 — the hashes this erasure releases. Read
                    // from the metadata already in hand, so no extra
                    // round trip: these are reclaimed inline however
                    // fresh, which is Phase 105's "the subject's bytes
                    // are gone at rest, not merely dereferenced".
                    let releasedHashes =
                        matched
                        |> List.collect (fun (_, metas) ->
                            metas |> List.choose (fun (_, mo) -> mo |> Option.map _.ContentHash))
                        |> Set.ofList

                    match policy with
                    | ErasurePolicy.HardDelete ->
                        let versionBlobs = matched |> List.collect (fun (_, metas) -> metas |> List.map fst)

                        do!
                            versionBlobs
                            |> List.map (fun name -> blobStorage.Delete(container, name))
                            |> Async.Parallel
                            |> Async.Ignore

                        let! _ = collectOrphanedContent container releasedHashes

                        return
                            Result.Ok {
                                HandlerName = "data-objects"
                                RecordsAffected = matched.Length
                                Note = Some(sprintf "%d object(s) removed in scope %s" matched.Length scopeId)
                            }
                    | ErasurePolicy.Tombstone
                    | ErasurePolicy.RetainPerCompliance ->
                        let redactContent = policy = ErasurePolicy.Tombstone
                        let tombstoneBytes = Encoding.UTF8.GetBytes Erasure.TombstoneMarker
                        let tombstoneHash = sha256Hex tombstoneBytes

                        if redactContent then
                            do! uploadContentIfMissing container tombstoneHash tombstoneBytes |> Async.Ignore

                        let redact (m: DataObject) = {
                            m with
                                CreatedBy =
                                    (if m.CreatedBy = subjectUserId then
                                         Erasure.TombstoneMarker
                                     else
                                         m.CreatedBy)
                                Metadata =
                                    m.Metadata
                                    |> Map.map (fun _ v ->
                                        if v.Contains subjectUserId then
                                            Erasure.TombstoneMarker
                                        else
                                            v)
                                ContentHash = (if redactContent then tombstoneHash else m.ContentHash)
                        }

                        do!
                            matched
                            |> List.collect (fun (_, metas) ->
                                metas
                                |> List.choose (fun (name, mo) -> mo |> Option.map (fun m -> name, redact m)))
                            |> List.map (fun (name, m) -> blobStorage.Upload(container, name, Json.serialize m))
                            |> Async.Parallel
                            |> Async.Ignore

                        if redactContent then
                            // The redacted metadata now names the
                            // tombstone hash, so the originals are
                            // released — minus the tombstone itself,
                            // which is live and freshly written.
                            let! _ = collectOrphanedContent container (releasedHashes.Remove tombstoneHash)
                            ()

                        let verb = if redactContent then "tombstoned" else "redacted"

                        return
                            Result.Ok {
                                HandlerName = "data-objects"
                                RecordsAffected = matched.Length
                                Note = Some(sprintf "%d object(s) %s in scope %s" matched.Length verb scopeId)
                            }
        }
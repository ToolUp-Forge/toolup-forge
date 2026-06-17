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
let private containerFor (scopeId: string) = scopeId

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

    /// Garbage-collect orphaned content blobs after a delete. Lists
    /// every remaining `v*.json` in the container, collects the set
    /// of referenced `ContentHash` values, and deletes any
    /// `_content/{hash}.data` whose hash is not referenced.
    let collectOrphanedContent (container: string) : Async<int> = async {
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
            |> Async.Parallel

        let referenced =
            metadatas |> Array.choose id |> Array.map _.ContentHash |> Set.ofArray

        let toDelete =
            normalised
            |> List.filter (fun n ->
                if n.StartsWith(contentPrefix) && n.EndsWith(".data") then
                    let hash =
                        n.Substring(contentPrefix.Length, n.Length - contentPrefix.Length - ".data".Length)

                    not (referenced.Contains hash)
                else
                    false)

        let! _ =
            toDelete
            |> List.map (fun name -> blobStorage.Delete(container, name))
            |> Async.Parallel

        return toDelete.Length
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

                let! _ =
                    versions
                    |> List.map (fun (_, name) -> blobStorage.Delete(container, name))
                    |> Async.Parallel

                let! _orphaned = collectOrphanedContent container
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
                let! _ =
                    versions
                    |> List.map (fun (_, name) -> blobStorage.Delete(container, name))
                    |> Async.Parallel

                let! _orphaned = collectOrphanedContent container
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
                    match policy with
                    | ErasurePolicy.HardDelete ->
                        let versionBlobs = matched |> List.collect (fun (_, metas) -> metas |> List.map fst)

                        do!
                            versionBlobs
                            |> List.map (fun name -> blobStorage.Delete(container, name))
                            |> Async.Parallel
                            |> Async.Ignore

                        let! _ = collectOrphanedContent container

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
                            let! _ = collectOrphanedContent container
                            ()

                        let verb = if redactContent then "tombstoned" else "redacted"

                        return
                            Result.Ok {
                                HandlerName = "data-objects"
                                RecordsAffected = matched.Length
                                Note = Some(sprintf "%d object(s) %s in scope %s" matched.Length verb scopeId)
                            }
        }
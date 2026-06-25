module ToolUp.Platform.FileManagement

open System
open System.Collections.Concurrent
open Microsoft.AspNetCore.Http
open DataManagementTypes
open ProcessedDataTypes
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.BlobStorage
open ToolUp.Platform
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Usage

// ─── File Type Detection ──────────────────────────────────────────

/// Detect the data type of file contents by trying each registered DataType
/// detector in order. First match wins; returns "UnrecognisedData" if none
/// match or the content is too short. Async to permit future I/O-bound
/// detectors (e.g. classifier services).
let detectFileType (dataTypes: DataType list) (contents: string) = async {
    let lines =
        contents.Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.RemoveEmptyEntries)
        |> Seq.map _.Trim()
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> Seq.toArray

    if lines.Length < 2 then
        return "UnrecognisedData"
    else
        let rec tryNext =
            function
            | [] -> async { return "UnrecognisedData" }
            | (dt: DataType) :: rest -> async {
                let! matched = dt.Detect contents
                if matched then return dt.Id else return! tryNext rest
              }

        return! tryNext dataTypes
}

/// Per-team availability gate (Phase 245 tri-state). Drop the data types
/// whose owning module is `Unavailable` for the caller so neither
/// detection nor processing can map a file into them — the raw file is
/// still stored ("upload allowed, mapping blocked"). `blockedTypeIds` is
/// empty outside team scope, so this is the identity in the common case.
let private availableFor (blockedTypeIds: Set<string>) (dataTypes: DataType list) =
    if blockedTypeIds.IsEmpty then
        dataTypes
    else
        dataTypes |> List.filter (fun dt -> not (blockedTypeIds.Contains dt.Id))

// ─── File Processing ──────────────────────────────────────────────

let private emptyProcessedData = { TypeName = ""; Payload = "" }

/// Render a single registered DataType for the detection-failure message —
/// uses its schema's required columns when available, falls back to display
/// name only when no schema has been published.
let private renderRecognisedType (dt: DataType) : string =
    match dt.Info.Schema with
    | Some schema ->
        let required = schema.Columns |> List.filter _.Required |> List.map _.Name

        match required with
        | [] -> dt.Info.DisplayName
        | cols -> sprintf "%s (expects %s)" dt.Info.DisplayName (String.concat ", " cols)
    | None -> dt.Info.DisplayName

/// Build an informative diagnostic when none of the registered detectors
/// matched a file's contents. Lists the actual columns found alongside the
/// expected-columns from each registered type's schema so the user can spot
/// which type they meant or which column they're missing.
let private composeDetectionFailure (dataTypes: DataType list) (fileName: string) (contents: string) : string =
    let foundHeaders = CsvHeaders.parse contents |> Set.toList |> List.sort

    let foundClause =
        match foundHeaders with
        | [] -> "No header row detected (file may be empty or non-CSV)."
        | cols -> sprintf "Found columns: %s." (String.concat ", " cols)

    let recognisedClause =
        match dataTypes with
        | [] -> "No data types are registered in this deployment."
        | xs ->
            xs
            |> List.map renderRecognisedType
            |> String.concat "; "
            |> sprintf "Recognised types: %s."

    sprintf "Couldn't detect a data type for '%s'. %s %s" fileName foundClause recognisedClause

/// Take the first line of a (potentially multi-line / stack-ish) exception
/// message — keeps user-facing errors readable without leaking internal frame
/// information.
let private firstLine (msg: string) : string =
    if isNull msg then
        ""
    else
        msg.Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.None)
        |> Array.tryHead
        |> Option.defaultValue msg

let processFile (dataTypes: DataType list) (fileName: string) (dataTypeId: DataTypeId) (contents: string) = async {
    let now = DateTime.UtcNow

    let mkEntry (error: string) = {
        FileName = fileName
        DataType = dataTypeId
        ProcessedAt = now
        Info = None
        Error = Some error
    }

    match dataTypes |> List.tryFind (fun dt -> dt.Id = dataTypeId) with
    | Some dataType ->
        try
            let! result = dataType.Process(fileName, contents)
            return result
        with ex ->
            // Wrap the parser's verbatim message with file + type context so
            // the user knows which upload broke and which DataType was being
            // attempted. Strip multi-line stack-ish text down to first line.
            let msg =
                sprintf "Failed to process '%s' as %s: %s" fileName dataType.Info.DisplayName (firstLine ex.Message)

            return emptyProcessedData, mkEntry msg
    | None when dataTypeId = "UnrecognisedData" ->
        return emptyProcessedData, mkEntry (composeDetectionFailure dataTypes fileName contents)
    | None -> return emptyProcessedData, mkEntry (sprintf "Unsupported or unrecognised data type '%s'." dataTypeId)
}

// ─── Persisted ProcessedFileEntry sidecars ────────────────────────
//
// `ProcessedFileEntry` records persist as sidecar objects in the same
// `IDataObjectStore` container as their underlying file, keyed by a
// namespaced ObjectId (`_processed_entry__{fileName}`). Mirrors the
// `_entity__{Type}__{Id}` precedent in `EntityStore.fs` so a future
// "modules emit entries directly" caller drops in without going
// through `SessionFileStore`. `Unversioned` policy — overwrite on
// every reprocess; the user-facing artefact is the current summary,
// not its history.
//
// Layout: `{container}/objects/_processed_entry__{fileName}/v1.json`
//
// `Info: obj option` round-trips via `FableConverters` — the same
// converter every existing `ProcessedFileEntry` traversal of an SDK
// boundary uses today.
module ProcessedEntryStore =
    open System.Text.Json
    open ToolUp.Remoting.Json.SystemTextJson

    [<Literal>]
    let private Prefix = "_processed_entry__"

    /// `dataType` metadata recorded on the persisted blob. The leading
    /// underscore matches the `_entity:` / `_dataingestion__` SDK-
    /// internal-data-type idiom and keeps these sidecars distinguishable
    /// in `IDataCatalog` views.
    [<Literal>]
    let DataType = "_processed_entry"

    let private jsonOptions = FableConverters.create ()

    /// Build the `IDataObjectStore` ObjectId for a file's entry sidecar.
    let objectIdFor (fileName: string) : string = Prefix + fileName

    /// `Some fileName` when `objectId` is a sidecar (prefix matches);
    /// `None` for raw file objects. Used by `loadPersistedFiles` to
    /// partition `ListObjects` results into "files" and "entries".
    let tryParseFileName (objectId: string) : string option =
        if objectId.StartsWith Prefix then
            Some(objectId.Substring Prefix.Length)
        else
            None

    let save
        (store: IDataObjectStore)
        (container: string)
        (createdBy: string)
        (entry: ProcessedFileEntry)
        : Async<unit> =
        async {
            let json = JsonSerializer.Serialize(entry, jsonOptions)
            let bytes = Text.Encoding.UTF8.GetBytes json

            do!
                store.Save(container, objectIdFor entry.FileName, bytes, DataType, createdBy, Map.empty, Unversioned)
                |> Async.Ignore
        }

    /// Deserialise persisted sidecar bytes into a `ProcessedFileEntry`.
    /// `None` (with a Warn) when the JSON can't be read back — e.g. the
    /// module's `Info` record shape changed since persistence. Callers
    /// fall back to reprocessing.
    let private tryDeserialise
        (logger: ILogger option)
        (container: string)
        (fileName: string)
        (bytes: byte[])
        : ProcessedFileEntry option =
        try
            let json = Text.Encoding.UTF8.GetString bytes
            Some(JsonSerializer.Deserialize<ProcessedFileEntry>(json, jsonOptions))
        with ex ->
            logger
            |> Option.iter (fun l ->
                l.Warn(
                    sprintf
                        "Persisted ProcessedFileEntry for '%s' in '%s' could not be deserialised; will reprocess. %s"
                        fileName
                        container
                        ex.Message
                ))

            None

    /// Load the persisted entry from an already-listed sidecar
    /// `DataObject`. `loadPersistedFiles` holds the latest metadata —
    /// including `ContentHash` — for every sidecar from its `ListObjects`
    /// sweep, so a direct `GetContent` reads the bytes in a single blob
    /// download, skipping both the per-object version-list and the
    /// metadata round-trip that `Get` / `GetVersion` perform. Deserialise
    /// / Warn-and-reprocess fallback via `tryDeserialise`.
    let tryLoadFromMeta
        (store: IDataObjectStore)
        (container: string)
        (logger: ILogger option)
        (meta: DataObject)
        : Async<ProcessedFileEntry option> =
        async {
            match! store.GetContent(container, meta.ContentHash) with
            | Ok bytes ->
                let fileName = tryParseFileName meta.ObjectId |> Option.defaultValue meta.ObjectId

                return tryDeserialise logger container fileName bytes
            | Error _ -> return None
        }

    let delete (store: IDataObjectStore) (container: string) (fileName: string) : Async<unit> = async {
        do! store.Delete(container, objectIdFor fileName) |> Async.Ignore
    }

// ─── File Store ───────────────────────────────────────────────────

// One-time guard so the header-trust degradation warning below fires once
// per process rather than on every request that takes the fallback path.
let mutable private headerTrustWarned = 0

/// Read the user ID for the current request. Prefers the value populated
/// by `ScopeResolutionMiddleware` (stored in `HttpContext.Items`) to avoid
/// duplicating an async auth call on the synchronous Fable.Remoting handler
/// path. Falls back to the X-User-Id header if the middleware did not run —
/// a header-trust degradation that warns once (see below).
let getUserId (ctx: HttpContext) =
    match ctx.Items.TryGetValue "ToolUp.UserId" with
    | true, (:? string as id) -> id
    | _ ->
        match ctx.Request.Headers.TryGetValue "X-User-Id" with
        | true, values when values.Count > 0 ->
            // `ScopeResolutionMiddleware` did not populate `ToolUp.UserId`,
            // so identity is being taken from a *client-supplied* `X-User-Id`
            // request header. That is safe only behind a gateway that
            // authoritatively sets/strips the header; if the middleware was
            // simply never wired, this is a subject-spoofing surface (any
            // caller can claim any user's scope). Surface it once per process
            // so the operator notices the silent degradation — previously it
            // fell through with no signal at all.
            if System.Threading.Interlocked.Exchange(&headerTrustWarned, 1) = 0 then
                let msg =
                    "FileManagement.getUserId fell back to the client-supplied X-User-Id header: ScopeResolutionMiddleware did not populate HttpContext.Items[\"ToolUp.UserId\"], so request identity is being trusted from an untrusted header. Safe only behind a gateway that authoritatively sets X-User-Id; otherwise wire ScopeResolutionMiddleware so the subject is resolved server-side."

                match ctx.RequestServices.GetService(typeof<ILogger>) with
                | :? ILogger as l -> l.Warn msg
                | _ -> eprintfn "%s" msg

            values[0]
        | _ -> "anonymous"

type StoredFile = {
    FileName: string
    DataType: DataTypeId
    Contents: string
    UploadedAt: DateTime
    SizeBytes: int64
    RowCount: int
}

/// Compose-resolved runtime configuration consumed by every
/// `SessionFileStore` instance. Built once during `compose` and
/// registered as an `IServiceCollection` singleton; resolved per-request
/// by `fileManagementApi` / `getFileContents` and threaded into the
/// store on first access. Immutable — replaces the prior pattern of
/// four module-level mutables read at hook-fire / quota-check / usage-
/// emission time.
type FileManagementRuntime = {
    /// Hooks that fire fire-and-forget after every successful
    /// `SessionFileStore.AddFile`. Empty by default; `RAGCompose`
    /// registers a vectorisation hook when any `VectorisationHandler`
    /// is wired. The fourth tuple element is the uploading user's id
    /// (`AddFile`'s `createdBy`) — distinct from `StorageScope.ScopeId`
    /// (which is the team in team scopes) so a hook can attribute the
    /// work it spawns to the actual user.
    PostSaveHooks: (ProcessedData * ProcessedFileEntry * StorageScope * string -> Async<unit>) list
    /// Logger used to record exceptions thrown by post-save hooks.
    /// Hooks run on `Async.Start` after the HTTP response has been
    /// sent, so without this an unhandled exception is invisible to
    /// the operator. `None` matches the test-harness path.
    PostSaveHooksLogger: ILogger option
    /// Per-scope storage quota resolver. `None` accepts uploads of
    /// any size. The shape `scopeId -> Async<int64 option>` lets
    /// future per-team overrides plug in without changing the
    /// contract.
    QuotaResolver: (string -> Async<int64 option>) option
    /// Phase 9d — `IUsageLog` for `storage.bytes` emission. `None`
    /// (test-harness / `NoUsageMetering`) makes the emission sites
    /// no-op.
    UsageLog: IUsageLog option
    /// Hard per-file ceiling (bytes). Independent of the per-scope quota:
    /// a single file over this is rejected before it is *detected over its
    /// whole length and processed*. Note this check runs in `AddFile`, by
    /// which point `upload.contents` is already a fully-materialised string
    /// — the request body was read into memory and deserialised by the
    /// transport before the handler ran. The pre-materialisation guard is
    /// the transport-level `ServerConfig.MaxRequestBodyBytes` (stamped onto
    /// Kestrel's `Limits.MaxRequestBodySize` in `buildHostBuilder`); this
    /// ceiling is the application-level backstop that additionally bounds
    /// the detect/parse cost. `None` disables it; the default is generous
    /// (a real CSV is far smaller) and exists so an unbounded upload can't
    /// blow up the detect/process pass by default.
    MaxFileBytes: int64 option
}

module FileManagementRuntime =
    /// Empty runtime — used by tests / harnesses that bypass `compose`,
    /// and as the construction starting point for `compose` itself.
    let empty: FileManagementRuntime = {
        PostSaveHooks = []
        PostSaveHooksLogger = None
        QuotaResolver = None
        UsageLog = None
        // 64 MiB — orders of magnitude above any real tabular upload in this
        // app's domain, but a hard backstop against a pathological/abusive
        // multi-hundred-MB body. Override (or `None`) at compose time.
        MaxFileBytes = Some(64L * 1024L * 1024L)
    }

/// Pending post-save hooks registered by companions (notably
/// `RAGCompose`) BEFORE `compose` runs. `compose` drains this into the
/// `FileManagementRuntime` it builds, then the list is no longer
/// consulted. The single surviving module-level mutable in this file —
/// the others were replaced by the runtime record. See the documented
/// exceptions in `src/ToolUp.Platform/README.md`.
//
// (b) — registration list survives across tests that bypass `compose`;
// reset for Expecto via `CacheReset.invalidateAll`. See
// docs/platform/testing-conventions.md.
let mutable internal pendingPostSaveHooks
    : (ProcessedData * ProcessedFileEntry * StorageScope * string -> Async<unit>) list = []

/// Register post-save hooks that fire after every successful
/// `SessionFileStore.AddFile`. Companions call this from their compose
/// path BEFORE the SDK's `compose` runs. Replaces the existing list
/// (matching the prior contract — a single companion owns hook
/// registration today).
let configurePostSaveHooks (hooks: (ProcessedData * ProcessedFileEntry * StorageScope * string -> Async<unit>) list) =
    pendingPostSaveHooks <- hooks

/// Drain pending companion-registered hooks. Called by `compose`
/// when it builds the `FileManagementRuntime`. After draining,
/// `pendingPostSaveHooks` is reset to `[]` so a second compose run
/// (e.g. integration tests that bring up the host repeatedly) starts
/// from a clean slate.
let internal drainPendingPostSaveHooks () =
    let hooks = pendingPostSaveHooks
    pendingPostSaveHooks <- []
    hooks

/// Test-only: drop pending hook registrations so a subsequent test
/// starts from a clean slate. Registered via
/// `ToolUp.Platform.Tests.Support.CacheReset.invalidateAll`. Never
/// called from production code paths — the `compose` drain pattern
/// resets `pendingPostSaveHooks` naturally there.
let internal __internal_resetForTests () = pendingPostSaveHooks <- []

type SessionFileStore
    (
        dataTypes: DataType list,
        dataObjectStore: IDataObjectStore option,
        scope: StorageScope,
        runtime: FileManagementRuntime
    ) =
    // Filename-keyed dictionaries use `StringComparer.OrdinalIgnoreCase`
    // to match the dominant filesystem semantics (NTFS / APFS / exFAT all
    // compare case-insensitively) and to spare callers — including the AI
    // assistant — from the "user says MediaOptimisation3.csv, code gets
    // mediaoptimisation3.csv back from the model" mismatch. All three
    // dictionaries share the comparer so AddFile / GetFile / DeleteFile /
    // processed-data lookups stay consistent.
    let files =
        ConcurrentDictionary<string, StoredFile>(StringComparer.OrdinalIgnoreCase)

    let processedData =
        ConcurrentDictionary<string, ProcessedData>(StringComparer.OrdinalIgnoreCase)

    let processedEntries =
        ConcurrentDictionary<string, ProcessedFileEntry>(StringComparer.OrdinalIgnoreCase)

    let container = scope.Container
    // Persistence routes through `IDataObjectStore` (Phase 7). Files are
    // stored with `Unversioned` policy — the legacy overwrite-on-save
    // semantics — and are visible to Phase 7a's catalog at
    // `{container}/objects/{fileName}/v1.json`. Ephemeral scopes drop
    // the store reference up-front so request-path persistence is a
    // no-op without per-call branching.
    let dataObjectStore = if scope.Persist then dataObjectStore else None

    let persistFile (file: StoredFile) (createdBy: string) = async {
        match dataObjectStore with
        | Some store ->
            let bytes = Text.Encoding.UTF8.GetBytes(file.Contents)

            do!
                store.Save(container, file.FileName, bytes, file.DataType, createdBy, Map.empty, Unversioned)
                |> Async.Ignore
        | None -> ()
    }

    let deletePersistedFile (fileName: string) = async {
        match dataObjectStore with
        | Some store -> do! store.Delete(container, fileName) |> Async.Ignore
        | None -> ()
    }

    // Bounded fan-out for the per-file blob reads in `loadPersistedFiles`.
    // Each file body + each entry sidecar is an independent
    // `IDataObjectStore` round-trip; on a remote store (S3 / Azure) the
    // reads below are fanned out (the prior loops ran them in series).
    // Both passes read content directly by hash — `GetContent` against
    // the `ContentHash` the single `ListObjects` sweep already returned —
    // rather than `Get` / `GetVersion`, which each re-resolve the latest
    // version (a per-object version-list blob op) and re-download the
    // metadata blob before the content. So every object costs exactly one
    // content fetch here, not version-list + metadata + content. The
    // bound collapses N serial waits into a few waves without letting a
    // large scope exhaust the backing store's connection pool; it matches
    // the `16` degree `DataObjectStore` already uses for its own metadata
    // fan-out (`metadataReadParallelism`) — same connection-pool budget,
    // and `DataObjectStore` holds no shared mutable state so concurrent
    // reads are safe.
    let startupHydrationConcurrency = 16

    // Wall-clock budget for the one-time per-scope hydration below. It runs
    // on the FIRST request thread that resolves a scope, so an unreachable
    // or very slow backing store would otherwise block that request
    // indefinitely (the work is O(total bytes) with no natural bound). On
    // expiry the in-flight reads are cancelled and the scope serves whatever
    // it hydrated so far; the remainder surfaces on a later restart or via
    // Reprocess. 30s is far above a healthy hydration yet bounds the
    // pathological case.
    let hydrationTimeoutMs = 30_000

    // Runs at `SessionFileStore` construction. Stores are built lazily per
    // scope via `getStore`'s `GetOrAdd`, so this is NOT server-startup work
    // — it runs on the FIRST request that resolves a given scope (typically
    // the first data-bearing request after a user logs in, and again for
    // every scope after a process restart / deploy). The whole sweep is one
    // async workflow driven by a single time-boxed `Async.RunSynchronously`
    // so the budget + cancellation below cover every blob round-trip
    // uniformly; the fan-out + fast-path keep the one-time cost from scaling
    // O(files × parse) on the request thread, and the surrounding try/with
    // degrades-not-throws so a `ListObjects`/read failure leaves the scope
    // empty-but-serving rather than 500-ing that first request.
    let loadPersistedFiles () =
        match dataObjectStore with
        | Some store ->
            let hydrate = async {
                let! allObjects = store.ListObjects(container)

                // Partition the listing into raw files vs. processed-entry
                // sidecars. The latter are loaded in pass 2 by stripping
                // the prefix and matching back to a file.
                let fileObjects, entryObjects =
                    allObjects
                    |> List.partition (fun obj -> ProcessedEntryStore.tryParseFileName obj.ObjectId |> Option.isNone)

                // Pass 1: load raw file bytes into the in-memory `files`
                // dictionary, fanned out across files. Per-file read failure
                // is silently skipped (matches prior behaviour — a corrupt
                // blob shouldn't crash startup for the rest of the scope).
                let! loadedFiles =
                    fileObjects
                    |> List.map (fun obj -> async {
                        match! store.GetContent(container, obj.ContentHash) with
                        | Ok bytes ->
                            let contents = Text.Encoding.UTF8.GetString bytes
                            let! detectedType = detectFileType dataTypes contents
                            let rowCount = max 0 (contents.Split('\n').Length - 1)

                            return
                                Some {
                                    FileName = obj.ObjectId
                                    DataType = detectedType
                                    Contents = contents
                                    UploadedAt = obj.CreatedAt
                                    SizeBytes = int64 contents.Length
                                    RowCount = rowCount
                                }
                        | Error e ->
                            // A corrupt/unreadable blob shouldn't crash startup for the
                            // rest of the scope — but it MUST NOT vanish silently: the
                            // user would just see their uploaded file gone after a
                            // restart with no operator signal.
                            let msg =
                                sprintf
                                    "Hydration: persisted file '%s' in '%s' could not be read; skipping. %A"
                                    obj.ObjectId
                                    container
                                    e

                            match runtime.PostSaveHooksLogger with
                            | Some l -> l.Warn msg
                            | None -> eprintfn "%s" msg

                            return None
                    })
                    |> fun xs -> Async.Parallel(xs, startupHydrationConcurrency)

                loadedFiles
                |> Array.iter (Option.iter (fun file -> files.TryAdd(file.FileName, file) |> ignore))

                let registeredTypes = dataTypes |> List.map _.Id |> Set.ofList
                let logger = runtime.PostSaveHooksLogger

                // Index the sidecar metadata from the same `ListObjects`
                // sweep so pass 2 reads each entry by known version
                // (`tryLoadFromMeta`) rather than issuing a fresh `Get`.
                // Case-insensitive to match the `files` dictionary comparer.
                let sidecarByFile =
                    System.Collections.Generic.Dictionary<string, DataObject>(StringComparer.OrdinalIgnoreCase)

                for entryObj in entryObjects do
                    match ProcessedEntryStore.tryParseFileName entryObj.ObjectId with
                    | Some fileName -> sidecarByFile[fileName] <- entryObj
                    | None -> ()

                // Pass 2: hydrate processed entries, fanned out across files.
                // Prefer a persisted sidecar over re-running `DataType.Process`:
                //   - Sidecar present + DataType still registered → register
                //     the persisted summary verbatim. We deliberately do NOT
                //     re-run `processFile` to rebuild the heavy in-memory
                //     parsed payload here: nothing reads `processedData` on the
                //     request path (`GetProcessedData` returns the summary
                //     `processedEntries`, and api handlers re-parse raw contents
                //     on demand via `getFileContents`), so the prior eager
                //     re-derivation was O(files × parse) of startup work whose
                //     result was never consumed — the dominant cost of the
                //     first post-login request for a scope with many / large
                //     files. `processedData` is now populated only for files
                //     touched this session (`AddFile` / `ReprocessFile`), which
                //     is where the payload is actually used (post-save hooks).
                //   - Sidecar present but DataType unregistered (module
                //     unloaded since persistence) → keep the persisted `Info`
                //     but overlay an `Error` so the user clicks Reprocess.
                //   - Sidecar missing (legacy upload before this feature, or
                //     unreadable) → process once to build the entry, then
                //     persist the rebuilt entry so the next restart hits the
                //     fast path.
                let! _ =
                    files.Values
                    |> Seq.map (fun file -> async {
                        let! persisted =
                            match sidecarByFile.TryGetValue file.FileName with
                            | true, meta -> ProcessedEntryStore.tryLoadFromMeta store container logger meta
                            | false, _ -> async { return None }

                        match persisted with
                        | Some entry when Set.contains entry.DataType registeredTypes ->
                            processedEntries.TryAdd(file.FileName, entry) |> ignore
                        | Some entry ->
                            let staleError =
                                sprintf
                                    "DataType '%s' is no longer registered. Click Reprocess to rebuild."
                                    entry.DataType

                            processedEntries.TryAdd(file.FileName, { entry with Error = Some staleError })
                            |> ignore
                        | None ->
                            let! _, entry = processFile dataTypes file.FileName file.DataType file.Contents
                            processedEntries.TryAdd(file.FileName, entry) |> ignore

                            // Promote the legacy file into the fast path on the
                            // next restart by writing the freshly-rebuilt entry.
                            // `"system"` createdBy mirrors the convention used
                            // elsewhere for non-user-attributed startup work.
                            do! ProcessedEntryStore.save store container "system" entry
                    })
                    |> fun xs -> Async.Parallel(xs, startupHydrationConcurrency)

                // Orphan-sweep: an entry sidecar whose underlying file is
                // gone (e.g. earlier crash between file-delete and sidecar-
                // delete). Idempotent — `Delete` on a missing object
                // returns `Ok ()` per `IDataObjectStore` contract.
                for orphan in entryObjects do
                    match ProcessedEntryStore.tryParseFileName orphan.ObjectId with
                    | Some fileName when not (files.ContainsKey fileName) ->
                        do! store.Delete(container, orphan.ObjectId) |> Async.Ignore
                    | _ -> ()
            }

            // Time-box the whole sweep on the first-request thread, and
            // degrade-not-throw. The `files` / `processedEntries`
            // dictionaries are populated incrementally inside `hydrate`, so a
            // timeout or store failure still leaves the scope serving
            // whatever loaded before the abort rather than failing the
            // request outright.
            try
                Async.RunSynchronously(hydrate, timeout = hydrationTimeoutMs)
            with
            | :? TimeoutException ->
                let msg =
                    sprintf
                        "Hydration of scope '%s' exceeded %dms; serving the %d file(s) loaded so far. The remainder hydrates on a later restart or via Reprocess."
                        container
                        hydrationTimeoutMs
                        files.Count

                match runtime.PostSaveHooksLogger with
                | Some l -> l.Warn msg
                | None -> eprintfn "%s" msg
            | ex ->
                let msg =
                    sprintf
                        "Hydration of scope '%s' failed (%s); serving the %d file(s) loaded so far rather than failing the request."
                        container
                        ex.Message
                        files.Count

                match runtime.PostSaveHooksLogger with
                | Some l -> l.Warn msg
                | None -> eprintfn "%s" msg
        | None -> ()

    do loadPersistedFiles ()

    member _.AddFile(upload: DataFileUpload, createdBy: string, ?blockedTypeIds: Set<string>) = async {
        // Per-team availability gate — detection + the explicit-`dataType`
        // honour below run against the caller's available types only, so a
        // file that would map into an Unavailable module lands unrecognised
        // (stored, but not processed into it).
        let availableDataTypes =
            availableFor (defaultArg blockedTypeIds Set.empty) dataTypes
        // Gap audit pass-2 #8 — file-name sanitisation. The filename
        // flows into the blob ObjectId (`_processed_entry__{fileName}`)
        // and into LocalFileStorage's filesystem path. Without this
        // check, control characters / null bytes / path separators
        // could collide blob keys across tenants on Linux dev
        // (where null-byte-in-filename truncates), produce
        // unwriteable storage keys on Windows, or simply produce
        // surprising error messages from cloud-storage backends.
        // Defence-in-depth: the HTTP framework filters most
        // attack-relevant chars upstream, but we make the rule
        // explicit at the SDK boundary.
        match FileNameSanitiser.validate upload.filename with
        | Error reason -> return Error(sprintf "Invalid filename '%s': %s" upload.filename reason)
        | Ok() ->

            // Honour an explicit, registered target `dataType` (the
            // mapping-aware Data Manager rewrites a CSV to a schema's
            // canonical headers and names the target type directly), and
            // fall back to header-based detection otherwise. The default
            // path is unchanged: the built-in `FileManagerUI` uploads
            // with `dataType = "UnrecognisedData"`, which never matches a
            // registered type and so always detects.
            let! detectedType =
                if
                    upload.dataType <> "UnrecognisedData"
                    && availableDataTypes |> List.exists (fun dt -> dt.Id = upload.dataType)
                then
                    async { return upload.dataType }
                else
                    detectFileType availableDataTypes upload.contents

            let rowCount = max 0 (upload.contents.Split('\n').Length - 1)
            // Real UTF-8 byte count, not the UTF-16 char count — this value
            // feeds the per-file ceiling, the scope quota sum, and the emitted
            // `storage.bytes` usage record, so multibyte content (non-ASCII
            // markets/SKUs, £/€/¥) must not be under-counted as chars.
            let sizeBytes = int64 (Text.Encoding.UTF8.GetByteCount upload.contents)

            // Phase 9 quota enforcement. The resolver is optional — when
            // not configured (or it returns `None` for this scope) the
            // store accepts uploads regardless of size. When a limit is
            // present, sum the existing files' bytes (the same value
            // surfaced by `GetFiles`) plus the incoming upload and reject
            // before any persist or process work runs. No audit event:
            // a rejection isn't a state change and shouldn't pollute the
            // audit trail with operator-noise.
            let! quota =
                match runtime.QuotaResolver with
                | Some resolve -> resolve scope.ScopeId
                | None -> async { return None }

            let existingBytes = files.Values |> Seq.sumBy _.SizeBytes

            match quota with
            // Hard per-file ceiling (independent of the per-scope quota) —
            // reject a pathological body before it is detected over its whole
            // length and processed. `upload.contents` is already in memory by
            // here (the transport materialised + deserialised the body); the
            // pre-materialisation guard is Kestrel's MaxRequestBodySize set
            // from `ServerConfig.MaxRequestBodyBytes`. `None` disables it.
            | _ when runtime.MaxFileBytes |> Option.exists (fun maxBytes -> sizeBytes > maxBytes) ->
                return
                    Error
                        $"File '{upload.filename}' ({sizeBytes} bytes) exceeds the {runtime.MaxFileBytes.Value}-byte per-file limit."
            | Some limit when existingBytes + sizeBytes > limit ->
                return
                    Error
                        $"Storage quota exceeded: {existingBytes + sizeBytes} bytes would exceed the {limit}-byte limit for this scope."
            | _ ->
                let file = {
                    FileName = upload.filename
                    DataType = detectedType
                    Contents = upload.contents
                    UploadedAt = DateTime.UtcNow
                    SizeBytes = sizeBytes
                    RowCount = rowCount
                }

                files.AddOrUpdate(upload.filename, file, fun _ _ -> file) |> ignore
                do! persistFile file createdBy

                // Phase 9d — emit positive `storage.bytes` for this scope.
                // No-op when `runtime.UsageLog = None` (NoUsageMetering or
                // tests). Best-effort: a failed `Record` must not fail the
                // upload — `IUsageLog.Record` is fire-and-forget by
                // contract, and the default impl wraps its own producer
                // path in try/catch.
                match runtime.UsageLog with
                | Some usageLog ->
                    let record = {
                        RecordId = Guid.NewGuid()
                        ScopeId = scope.ScopeId
                        ResourceKind = ResourceKinds.storageBytes
                        Quantity = decimal sizeBytes
                        Unit = "bytes"
                        Origin = None
                        Metadata = Map.ofList [ "fileName", upload.filename; "userId", createdBy ]
                        Timestamp = DateTime.UtcNow
                    }

                    do! usageLog.Record record
                | None -> ()

                let! data, entry = processFile availableDataTypes upload.filename detectedType upload.contents

                // Retain the heavy parsed payload in memory ONLY when a
                // post-save hook might consume it. Nothing reads this dict on
                // the request path (api handlers re-parse via getFileContents),
                // so populating it for every upload otherwise just pins a large
                // payload per file for the scope's lifetime.
                if not runtime.PostSaveHooks.IsEmpty then
                    processedData.AddOrUpdate(upload.filename, data, fun _ _ -> data) |> ignore

                processedEntries.AddOrUpdate(upload.filename, entry, fun _ _ -> entry) |> ignore

                // Persist the entry sidecar so the summary survives server
                // restart and the next `loadPersistedFiles` skips re-running
                // `Process` for this file. `dataObjectStore = None` (an
                // ephemeral scope) drops the persistence call — the in-
                // memory dictionaries above remain authoritative for the
                // session's lifetime.
                match dataObjectStore with
                | Some store -> do! ProcessedEntryStore.save store container createdBy entry
                | None -> ()

                // Fire post-save hooks asynchronously. Hooks come from the
                // compose-built `FileManagementRuntime` — companions register
                // them via `configurePostSaveHooks` before `compose` runs and
                // `compose` drains them into the runtime.
                //
                // Each hook is wrapped in a try/with that routes thrown
                // exceptions through `runtime.PostSaveHooksLogger`. The HTTP
                // response has already been sent successfully by the time
                // these run, so an unlogged exception is invisible to the
                // operator — and silently dropped index updates (RAG
                // vectorisation, audit publishers) leave the user thinking
                // their file is indexed when it isn't.
                if not runtime.PostSaveHooks.IsEmpty && entry.Error.IsNone then
                    let safeHook hook = async {
                        try
                            do! hook (data, entry, scope, createdBy)
                        with ex ->
                            let msg =
                                $"Post-save hook failed for file '{upload.filename}' (scope='{scope.ScopeId}')"

                            match runtime.PostSaveHooksLogger with
                            | Some logger -> logger.Error(msg, Some ex)
                            // No configured logger MUST NOT mean a silently
                            // dropped index update — the user was already told
                            // the upload succeeded. Fall back to stderr so a
                            // failed RAG/audit hook is never invisible.
                            | None -> eprintfn "%s: %O" msg ex
                    }

                    runtime.PostSaveHooks
                    |> List.map safeHook
                    |> Async.Parallel
                    |> Async.Ignore
                    |> Async.Start

                return
                    Ok {
                        FileInfo = {
                            FileName = file.FileName
                            DataType = file.DataType
                            SizeBytes = file.SizeBytes
                            UploadedAt = file.UploadedAt
                            RowCount = file.RowCount
                        }
                        Processed = entry
                    }
    }

    member _.GetFiles() =
        files.Values
        |> Seq.map (fun file -> {
            FileName = file.FileName
            DataType = file.DataType
            SizeBytes = file.SizeBytes
            UploadedAt = file.UploadedAt
            RowCount = file.RowCount
        })
        |> Seq.toList

    member _.GetFile(fileName: string) =
        match files.TryGetValue(fileName) with
        | true, file ->
            Ok {
                filename = file.FileName
                contents = file.Contents
                dataType = file.DataType
            }
        | false, _ ->
            // Include the uploaded filenames in the error so callers
            // (especially AI tools) can self-correct without another
            // round trip. The list is bounded — if the session has
            // many files, show the first 10 and a count — so the
            // message stays usable for model context and small for
            // a human-readable UI error.
            let available = files.Keys |> Seq.sortBy id |> Seq.toList

            let hint =
                match available with
                | [] -> "No files have been uploaded in this session."
                | xs when xs.Length <= 10 -> "Available files: " + String.concat ", " xs
                | xs ->
                    let preview = xs |> List.truncate 10 |> String.concat ", "
                    $"Available files (first 10 of {xs.Length}): {preview}"

            Error $"File '{fileName}' not found in session. {hint}"

    member _.GetProcessedData() = processedEntries.Values |> Seq.toList

    member _.DeleteFile(fileName: string) = async {
        processedData.TryRemove(fileName) |> ignore
        processedEntries.TryRemove(fileName) |> ignore
        do! deletePersistedFile fileName

        // Cascade the delete to the entry sidecar. Idempotent — a
        // sidecar may not exist for legacy uploads that predate this
        // feature, and `IDataObjectStore.Delete` returns `Ok ()` for
        // missing objects.
        match dataObjectStore with
        | Some store -> do! ProcessedEntryStore.delete store container fileName
        | None -> ()

        match files.TryRemove(fileName) with
        | true, removed ->
            // Phase 9d — emit negative `storage.bytes` so the running
            // sum reflects the live footprint. Same NoUsageMetering
            // short-circuit as `AddFile`.
            match runtime.UsageLog with
            | Some usageLog ->
                let record = {
                    RecordId = Guid.NewGuid()
                    ScopeId = scope.ScopeId
                    ResourceKind = ResourceKinds.storageBytes
                    Quantity = -(decimal removed.SizeBytes)
                    Unit = "bytes"
                    Origin = None
                    Metadata = Map.ofList [ "fileName", fileName ]
                    Timestamp = DateTime.UtcNow
                }

                do! usageLog.Record record
            | None -> ()

            return Ok()
        | false, _ -> return Error $"File '{fileName}' not found"
    }

    /// Re-run `DataType.Process` on the file's persisted bytes, refresh
    /// the in-memory parsed payload + summary, and overwrite the entry
    /// sidecar. The user-triggered escape hatch when an entry surfaces
    /// with an error after a server restart (typically because the
    /// `DataType` was unloaded and is now back, or the detector logic
    /// has changed). Post-save hooks are NOT fired — the raw bytes are
    /// unchanged, so RAG / vectorisation indexing is not invalidated.
    member _.ReprocessFile(fileName: string, createdBy: string, ?blockedTypeIds: Set<string>) = async {
        // Re-mapping honours the same per-team availability gate as upload —
        // a file cannot be reprocessed into a module that is Unavailable to
        // the caller's team.
        let availableDataTypes =
            availableFor (defaultArg blockedTypeIds Set.empty) dataTypes

        match files.TryGetValue(fileName) with
        | false, _ -> return Error $"File '{fileName}' not found"
        | true, file ->
            let! detectedType = detectFileType availableDataTypes file.Contents
            let! data, entry = processFile availableDataTypes fileName detectedType file.Contents

            processedData.AddOrUpdate(fileName, data, fun _ _ -> data) |> ignore
            processedEntries.AddOrUpdate(fileName, entry, fun _ _ -> entry) |> ignore

            // Refresh the file's `DataType` field so subsequent
            // `GetFiles` / `ListFiles` reflect the current detection.
            // The bytes themselves are unchanged, so we don't re-emit
            // `FileUploaded` audit or `storage.bytes` usage.
            let updated = { file with DataType = detectedType }
            files.AddOrUpdate(fileName, updated, fun _ _ -> updated) |> ignore

            match dataObjectStore with
            | Some store -> do! ProcessedEntryStore.save store container createdBy entry
            | None -> ()

            return Ok entry
    }

    /// Owner / Admin escape hatch — wipe every uploaded file plus its
    /// `_processed_entry__` sidecar in this scope. Iterates the in-
    /// memory `files` snapshot (the same set the caller saw via
    /// `GetFiles`) so we don't accidentally touch other namespaces in
    /// the container (entity stores, ingestion configs, etc.). One
    /// summed negative `storage.bytes` usage record on success — one
    /// per-file would multiply log noise on a deliberate bulk
    /// operation. Returns the file count for audit + UX feedback.
    member _.ResetDataStore() = async {
        let snapshot = files.Values |> Seq.toList
        let totalBytes = snapshot |> List.sumBy _.SizeBytes
        let fileCount = snapshot.Length

        for file in snapshot do
            do! deletePersistedFile file.FileName

            match dataObjectStore with
            | Some store -> do! ProcessedEntryStore.delete store container file.FileName
            | None -> ()

        files.Clear()
        processedData.Clear()
        processedEntries.Clear()

        if fileCount > 0 then
            match runtime.UsageLog with
            | Some usageLog ->
                let record = {
                    RecordId = Guid.NewGuid()
                    ScopeId = scope.ScopeId
                    ResourceKind = ResourceKinds.storageBytes
                    Quantity = -(decimal totalBytes)
                    Unit = "bytes"
                    Origin = None
                    Metadata = Map.ofList [ "operation", "reset"; "fileCount", string fileCount ]
                    Timestamp = DateTime.UtcNow
                }

                do! usageLog.Record record
            | None -> ()

        return fileCount
    }

    member _.Clear() =
        files.Clear()
        processedData.Clear()
        processedEntries.Clear()

type private StoreEntry = {
    Store: SessionFileStore
    LastAccessed: DateTime ref
    Persist: bool
}

let private stores = ConcurrentDictionary<string, StoreEntry>()

/// Default TTL for ephemeral stores (non-persistent). Stores not accessed within
/// this window are evicted from the in-memory dictionary. Configurable at
/// startup via `ServerConfig.EphemeralStoreEvictionMinutes` /
/// `TOOLUP_STORE_EVICTION_MINUTES=N` (Phase 1f extension, gap #10).
//
// (a) — compose-time-write-once scalar; tests that need a non-default
// value call `configureEvictionMinutes`. No Expecto reset hazard. See
// docs/platform/testing-conventions.md.
let mutable storeEvictionMinutes = 60.0

/// Phase 1f extension (gap #10). Set the eviction TTL from compose-side
/// config. Called once at startup; runtime eviction picks up the new
/// value on the next 10-minute timer tick.
let configureEvictionMinutes (minutes: float) =
    if minutes > 0.0 then
        storeEvictionMinutes <- minutes

/// Remove ephemeral stores that haven't been accessed within the TTL.
let private evictExpiredStores () =
    let cutoff = DateTime.UtcNow.AddMinutes(-storeEvictionMinutes)

    for kvp in stores do
        if not kvp.Value.Persist && kvp.Value.LastAccessed.Value < cutoff then
            stores.TryRemove(kvp.Key) |> ignore

/// Background timer for periodic eviction (runs every 10 minutes)
let private evictionTimer =
    new System.Threading.Timer(
        (fun _ -> evictExpiredStores ()),
        null,
        System.TimeSpan.FromMinutes(10.0),
        System.TimeSpan.FromMinutes(10.0)
    )

let getStore
    (dataTypes: DataType list)
    (dataObjectStore: IDataObjectStore option)
    (runtime: FileManagementRuntime)
    (scope: StorageScope)
    =
    let entry =
        stores.GetOrAdd(
            scope.Container,
            fun _ -> {
                Store = SessionFileStore(dataTypes, dataObjectStore, scope, runtime)
                LastAccessed = ref DateTime.UtcNow
                Persist = scope.Persist
            }
        )

    entry.LastAccessed.Value <- DateTime.UtcNow
    entry.Store

let getStoreForUser
    (dataTypes: DataType list)
    (dataObjectStore: IDataObjectStore option)
    (runtime: FileManagementRuntime)
    (userId: string)
    =
    let scope = {
        ScopeId = userId
        Container = $"user-{userId}"
        Persist = true
    }

    getStore dataTypes dataObjectStore runtime scope

/// Resolve the compose-built `FileManagementRuntime` from DI, returning
/// `FileManagementRuntime.empty` when none is registered (test harness
/// path — matches the prior `let mutable = None` defaults exactly).
let private resolveRuntime (ctx: HttpContext) : FileManagementRuntime =
    match ctx.RequestServices.GetService(typeof<FileManagementRuntime>) with
    | :? FileManagementRuntime as r -> r
    | _ -> FileManagementRuntime.empty

/// Raised by `getFileContents` when the requested file is not present in
/// the request-scoped session store. The Fable.Remoting error handler
/// classifies this as a 4xx user-action error and logs at `Warn`, so the
/// user-facing message survives but the dev log isn't polluted with
/// `[ERR]` lines for every "user clicked Run before uploading" case.
exception FileNotFoundInSessionException of string

/// Resolve a file's string contents from the request-scoped session file
/// store. Reads pre-resolved `DataType list`, `IDataObjectStore`, and the
/// storage scope from DI / `HttpContext.Items`; falls back to a user-
/// scoped store when the scope middleware did not run (tests, harness).
/// Raises `FileNotFoundInSessionException` on not-found / read error —
/// intended for api-layer handlers where the exception propagates to the
/// remoting error handler.
let getFileContents (ctx: HttpContext) (fileName: string) : string =
    let dataTypes =
        ctx.RequestServices.GetService(typeof<DataType list>) :?> DataType list

    let dataObjectStore =
        match ctx.RequestServices.GetService(typeof<IDataObjectStore>) with
        | :? IDataObjectStore as s -> Some s
        | _ -> None

    let runtime = resolveRuntime ctx

    let store =
        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
        | true, (:? StorageScope as scope) -> getStore dataTypes dataObjectStore runtime scope
        | _ -> getStoreForUser dataTypes dataObjectStore runtime (getUserId ctx)

    match store.GetFile(fileName) with
    | Ok fileContent -> fileContent.contents
    | Error msg -> raise (FileNotFoundInSessionException msg)

// ─── API Handler ──────────────────────────────────────────────────

let fileManagementApi (ctx: HttpContext) : FileManagementApi =
    let dataTypes =
        ctx.RequestServices.GetService(typeof<DataType list>) :?> DataType list

    let dataObjectStore =
        match ctx.RequestServices.GetService(typeof<IDataObjectStore>) with
        | :? IDataObjectStore as s -> Some s
        | _ -> None

    let runtime = resolveRuntime ctx

    // Scope is pre-resolved asynchronously by `ScopeResolutionMiddleware` and
    // stored in `HttpContext.Items`. Fall back to a user-based scope (using the
    // Items-cached userId) if the middleware did not run — this covers tests
    // and avoids re-introducing `Async.RunSynchronously`.
    let scope =
        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
        | true, (:? StorageScope as s) -> Some s
        | _ -> None

    let store =
        match scope with
        | Some s -> getStore dataTypes dataObjectStore runtime s
        | None -> getStoreForUser dataTypes dataObjectStore runtime (getUserId ctx)

    let userId = getUserId ctx

    let scopeId =
        match scope with
        | Some s -> s.ScopeId
        | None -> userId

    // Audit log is best-effort: emission is fire-and-forget via `Async.Start`
    // and `EventStoreAuditLog.Record` swallows write failures itself, so a
    // missing or failing `IAuditLog` never blocks file operations.
    let auditLog =
        match ctx.RequestServices.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as a -> Some a
        | _ -> None

    let recordAudit (event: AuditEvent) =
        match auditLog with
        | Some a -> a.Record(scopeId, event) |> Async.Start
        | None -> ()

    // Owner / Admin write gate for `ResetDataStore` — same shape as
    // `DataIngestionApiHandler.ensureWriteAllowed` and
    // `ConfigHandler.ensureWriteAllowed`. Single-user modes
    // (`Anonymous`, `AuthenticatedEphemeral`, `Individual`) are
    // ungated: the caller IS the data owner, no team to gate
    // against. `Team` / `MultiTeam` mode requires an `ITeamStore`
    // resolution + role lookup; missing team / non-Owner-Admin
    // returns a clear error.
    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> Some ac
        | _ -> None

    // Per-team availability gate (Phase 245 tri-state). The set of data
    // type ids whose every producing module is `Unavailable` for the
    // caller — detection/processing skips these so an upload never maps
    // into an unavailable module. Resolved per request via `IDataCatalog`
    // (type → producing modules). Short-circuits to empty when there is no
    // exposure config (the common case) or outside team scope.
    let computeBlockedTypeIds () : Async<Set<string>> =
        match accessContext, ctx.RequestServices.GetService(typeof<IDataCatalog>) with
        | Some ac, (:? IDataCatalog as catalog) when not (Map.isEmpty ac.ModuleExposure) -> async {
            let! pairs =
                dataTypes
                |> List.map (fun dt -> async {
                    let! producers = catalog.GetProducers dt.Id

                    let blocked =
                        not (List.isEmpty producers)
                        && producers |> List.forall (fun m -> not (AccessContext.isModuleAvailable m ac))

                    return (dt.Id, blocked)
                })
                |> Async.Parallel

            return
                pairs
                |> Array.choose (fun (id, blocked) -> if blocked then Some id else None)
                |> Set.ofArray
          }
        | _ -> async { return Set.empty }

    let ensureCanResetDataStore () : Async<Result<unit, string>> = async {
        match accessContext with
        | None ->
            // Tests / harnesses bypassing scope middleware. Treat
            // as ungated — same posture as the existing handlers'
            // fallback paths.
            return Ok()
        | Some ac ->
            match ac.Subject with
            | TeamMember(userId, teamId) ->
                match ctx.RequestServices.GetService(typeof<ITeamStore>) with
                | :? ITeamStore as ts ->
                    let! role = ts.GetMemberRole(teamId, userId)

                    match role with
                    | Some r when TeamRoles.canWriteTeamConfig r -> return Ok()
                    | Some r ->
                        return
                            Error
                                $"Only team owners and admins can reset the data store. Your role: {TeamRoles.displayName r}."
                    | None -> return Error "You are not a member of this team."
                | _ -> return Error "Team management is not available in this deployment."
            | _ -> return Ok()
    }

    {
        UploadFile =
            fun request -> async {
                let! blockedTypeIds = computeBlockedTypeIds ()
                let! result = store.AddFile(request.File, userId, blockedTypeIds)

                match result with
                | Ok response ->
                    recordAudit (
                        FileUploaded {
                            UserId = userId
                            FileName = response.FileInfo.FileName
                            DataType = response.FileInfo.DataType
                            SizeBytes = response.FileInfo.SizeBytes
                        }
                    )
                | Error _ -> ()

                return result
            }
        ListFiles =
            fun () -> async {
                return {
                    Files = store.GetFiles()
                    Processed = store.GetProcessedData()
                }
            }
        GetFileContent = fun fileName -> async { return store.GetFile(fileName) }
        DeleteFile =
            fun fileName -> async {
                let! result = store.DeleteFile(fileName)

                match result with
                | Ok() -> recordAudit (FileDeleted { UserId = userId; FileName = fileName })
                | Error _ -> ()

                return result
            }
        ReprocessFile =
            fun fileName -> async {
                let! blockedTypeIds = computeBlockedTypeIds ()
                let! result = store.ReprocessFile(fileName, userId, blockedTypeIds)

                match result with
                | Ok entry ->
                    recordAudit (
                        FileReprocessed {
                            UserId = userId
                            FileName = entry.FileName
                            DataType = entry.DataType
                            HadError = entry.Error.IsSome
                        }
                    )
                | Error _ -> ()

                return result
            }
        ResetDataStore =
            fun () -> async {
                let! gate = ensureCanResetDataStore ()

                match gate with
                | Error msg -> return Error msg
                | Ok() ->
                    let! fileCount = store.ResetDataStore()

                    recordAudit (
                        DataStoreReset {
                            UserId = userId
                            FileCount = fileCount
                        }
                    )

                    return Ok fileCount
            }
    }
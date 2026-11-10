// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Infra

open System
open System.Collections.Concurrent
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra.IRagTelemetry

// ─── The scoped snapshot index ──────────────────────────────────────
//
// An in-process retrieval store (the vector store, the BM25 index, the HNSW
// store) holds each scope's corpus in memory and persists it as ONE blob per
// scope. Every such store has to answer the same questions, and answering any
// of them differently in one store is a defect:
//
//   * Is the scope loaded? Loadedness is explicit, never inferred from
//     whether the store holds anything for the scope: a wiped scope is
//     loaded and empty, and must not re-hydrate the wipe away.
//   * Absent, unreadable or corrupt? An absent blob is loaded-and-empty. A
//     blob that exists but cannot be read refuses every mutation, because the
//     store persists a scope by writing back everything it holds, and a
//     mutation on an empty map would replace the corpus at the next flush. A
//     blob that reads but does not decode follows the declared
//     `CorruptPolicy`, with its telemetry, audit row and system message.
//   * When does a load happen? On first access, single-flight, so a mutation
//     cannot land between a concurrent load's read and its replay. Never in
//     a constructor: `Start` loads the platform and deployment scopes for a
//     composition that wants a corrupt snapshot to fail the boot.
//   * When is a scope written back? A mutation marks it dirty; a background
//     loop flushes the dirty set on a timer, or sooner past a chunk count;
//     `Dispose` flushes once more.
//
// `ScopedSnapshotIndex` answers them once. A store is a persisted shape
// (`'Snapshot`), a live shape (`'Live`), the two conversions between them,
// and the members that read and mutate the live shape.

/// Why a scope's persisted snapshot could not become its live form.
[<RequireQualifiedAccess>]
type SnapshotFault =
    /// The blob exists, but the storage could not read it.
    | Unreadable
    /// The blob was read, its bytes did not decode, and the corruption policy
    /// refuses to start the scope empty.
    | Corrupt

[<RequireQualifiedAccess>]
module internal SnapshotFaultText =

    let describe (scopeKey: string) (blobLocation: string) (reason: string) (fault: SnapshotFault) =
        match fault with
        | SnapshotFault.Unreadable ->
            $"The persisted snapshot '{blobLocation}' for RAG scope '{scopeKey}' exists but could not be read ({reason}); the mutation was refused so the scope's corpus is not replaced by an empty one. Retry once the storage can read the blob."
        | SnapshotFault.Corrupt ->
            $"Refusing to start RAG scope '{scopeKey}' from the corrupt snapshot '{blobLocation}' ({reason}). TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION is set: replace or delete the blob, then retry (unset the variable to start a corrupt scope empty instead)."

/// Raised by a store over `ScopedSnapshotIndex` when a scope's persisted
/// snapshot cannot become its live form: the blob exists but could not be
/// read (`SnapshotFault.Unreadable`), or it did not decode and the corruption
/// policy refuses (`SnapshotFault.Corrupt`). Nothing is written; the scope
/// stays unloaded, and the next access retries the load. An ABSENT snapshot is
/// not this error: that scope is loaded-and-empty.
[<Sealed>]
type RagScopeSnapshotUnreadableException
    /// Refuse an access to `scopeKey`, whose snapshot at `blobLocation` could
    /// not become its live form for `reason`.
    (scopeKey: string, blobLocation: string, reason: string, fault: SnapshotFault) =
    inherit Exception(SnapshotFaultText.describe scopeKey blobLocation reason fault)

    /// Refuse a mutation of `scopeKey`, whose snapshot at `blobLocation` exists
    /// but could not be read for `reason` (the storage's own error text).
    new(scopeKey: string, blobLocation: string, reason: string) =
        RagScopeSnapshotUnreadableException(scopeKey, blobLocation, reason, SnapshotFault.Unreadable)

    /// The scope whose access was refused (`team:<id>`, `user:<id>`, …).
    member _.ScopeKey = scopeKey

    /// The blob name of the snapshot.
    member _.BlobLocation = blobLocation

    /// The storage's or the decoder's own reason.
    member _.Reason = reason

    /// Unreadable or corrupt.
    member _.Fault = fault

/// What a store does with a snapshot that reads but does not decode.
[<RequireQualifiedAccess>]
type CorruptPolicy =
    /// Start the scope empty, and say so through the observers (the default).
    | StartEmpty
    /// Refuse the scope with `RagScopeSnapshotUnreadableException`
    /// (`SnapshotFault.Corrupt`) until the blob is repaired.
    | Refuse

/// Reading the policy from its configuration key.
[<RequireQualifiedAccess>]
module CorruptPolicy =

    /// The policy a raw `TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION` value names
    /// (unset or unrecognised is `StartEmpty`).
    let ofSetting (raw: string option) : CorruptPolicy =
        let key = ConfigKeys.key ConfigKeys.Names.ragRefuseOnIndexCorruption

        if ConfigKeys.ConfigKey.readPermissiveBool key raw then
            CorruptPolicy.Refuse
        else
            CorruptPolicy.StartEmpty

    /// The policy the process environment names. A composition reads it once
    /// and passes it to every store it builds; a store constructed without a
    /// policy reads it here.
    let fromEnvironment () : CorruptPolicy =
        ofSetting (
            Environment.GetEnvironmentVariable ConfigKeys.Names.ragRefuseOnIndexCorruption
            |> Option.ofObj
        )

/// The corruption policy and the observers a corrupt load reports to: a
/// telemetry counter, a `KnowledgeIndexLoadFailed` audit row and an Error
/// `SystemMessage`, once per scope per process. Every observer is optional;
/// with none, the store still logs.
type CorruptionHandling = {
    Policy: CorruptPolicy
    Telemetry: IRagTelemetry option
    AuditLog: IAuditLog
    Notifications: INotificationChannel option
}

/// Constructors for `CorruptionHandling`.
[<RequireQualifiedAccess>]
module CorruptionHandling =

    /// `policy` with no observer beyond the store's log.
    let logOnly (policy: CorruptPolicy) : CorruptionHandling = {
        Policy = policy
        Telemetry = None
        AuditLog = NoOpAuditLog() :> IAuditLog
        Notifications = None
    }

/// How a scoped snapshot index persists and what it does with a corrupt
/// snapshot.
type SnapshotPolicy = {
    /// The flush loop's period.
    FlushIntervalMs: int
    /// Flush as soon as this many mutations have been marked since the last
    /// flush, without waiting for the period. `None` flushes on the period alone.
    FlushChunkThreshold: int option
    /// Where each flush pass is recorded (`IRagTelemetry.RecordFlush`).
    FlushTelemetry: IRagTelemetry option
    /// The corruption policy and its observers.
    OnCorrupt: CorruptionHandling
}

/// Constructors for `SnapshotPolicy`.
[<RequireQualifiedAccess>]
module SnapshotPolicy =

    /// A two-second flush period, no threshold, no flush telemetry, and
    /// `onCorrupt`.
    let create (onCorrupt: CorruptionHandling) : SnapshotPolicy = {
        FlushIntervalMs = 2000
        FlushChunkThreshold = None
        FlushTelemetry = None
        OnCorrupt = onCorrupt
    }

/// Where a scope's snapshot lives: `{Container}/{VectorScope.key scope}/{FileName}`
/// in `Container`. The layout is a wire contract; the stores' names never move.
type SnapshotLayout = { Container: string; FileName: string }

/// The blob names a `SnapshotLayout` yields and reads back.
[<RequireQualifiedAccess>]
module SnapshotLayout =

    /// A file under the `_rag` container, one directory per scope.
    let rag (fileName: string) : SnapshotLayout = {
        Container = "_rag"
        FileName = fileName
    }

    /// The blob holding `scope`'s snapshot.
    let blobName (layout: SnapshotLayout) (scope: VectorScope) : string =
        $"{layout.Container}/{VectorScope.key scope}/{layout.FileName}"

    /// The scope a blob name holds, when it is this layout's file for a scope
    /// key `VectorScope.key` writes; another store's file under the same
    /// prefix never matches.
    let tryScopeOfBlob (layout: SnapshotLayout) (name: string) : VectorScope option =
        let prefix = layout.Container + "/"
        let suffix = "/" + layout.FileName

        if
            name.StartsWith prefix
            && name.EndsWith suffix
            && name.Length > prefix.Length + suffix.Length
        then
            let key = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length)

            if key.Contains "/" then None else VectorScope.tryOfKey key
        else
            None

/// The outcome of reading one scope's snapshot blob.
[<RequireQualifiedAccess>]
type SnapshotLoad<'Snapshot> =
    /// No blob: the scope is empty.
    | Absent
    /// The blob decoded.
    | Loaded of snapshot: 'Snapshot
    /// The blob exists but the storage could not read it.
    | Unreadable of reason: string
    /// The blob was read but did not decode.
    | Corrupt of bytes: int * reason: string

/// The one read of a snapshot blob.
[<RequireQualifiedAccess>]
module BlobSnapshot =

    /// Read `blobName` in `container` through `codec`. A failed download is
    /// `Absent` only when the blob is not there; one that is there but could
    /// not be read is `Unreadable`. A decoder that throws is read as one that
    /// returned `Error`.
    let read
        (storage: IBlobStorage)
        (container: string)
        (blobName: string)
        (codec: BlobCodec<'Snapshot>)
        : Async<SnapshotLoad<'Snapshot>> =
        async {
            match! storage.Download(container, blobName) with
            | Ok bytes ->
                let decoded =
                    try
                        codec.Decode bytes
                    with ex ->
                        Error ex.Message

                match decoded with
                | Ok snapshot -> return SnapshotLoad.Loaded snapshot
                | Error reason -> return SnapshotLoad.Corrupt(bytes.Length, reason)
            | Error reason ->
                let! present = storage.Exists(container, blobName)

                if present then
                    return SnapshotLoad.Unreadable reason
                else
                    return SnapshotLoad.Absent
        }

/// One loaded scope: its live form and the lock its readers and writers share.
[<Sealed>]
type internal ScopeSlot<'Live>(scope: VectorScope, live: 'Live) =
    let gate = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion)

    member _.Scope = scope

    member _.Reading(read: 'Live -> 'R) : 'R =
        gate.EnterReadLock()

        try
            read live
        finally
            gate.ExitReadLock()

    member _.Writing(write: 'Live -> 'R) : 'R =
        gate.EnterWriteLock()

        try
            write live
        finally
            gate.ExitWriteLock()

/// A lazily hydrated, flushable, scope-keyed in-memory index persisted as one
/// blob per scope (see the file header).
///
/// * `layout` names each scope's blob; `codec` carries `'Snapshot` as its bytes.
/// * `empty` is a scope's live form when it has no snapshot.
/// * `materialise` turns a decoded snapshot into the live form, and says
///   whether the live form should be written back (it changed on the way in).
///   A `materialise` that throws is a corrupt snapshot.
/// * `snapshot` turns a scope's live form into what is persisted; `None` deletes
///   the blob.
///
/// A mutation goes through `Mutate`, which hydrates the scope first and marks
/// it dirty when told; a read through `Read`, after `EnsureLoaded` (which
/// refuses an unreadable scope) or `EnsureLoadedForSearch` (which degrades
/// it). Readers of one scope run together; a writer runs alone. The
/// constructor does no I/O.
type ScopedSnapshotIndex<'Snapshot, 'Live>
    (
        storage: IBlobStorage,
        layout: SnapshotLayout,
        codec: BlobCodec<'Snapshot>,
        empty: VectorScope -> 'Live,
        materialise: VectorScope -> 'Snapshot -> 'Live * bool,
        snapshot: VectorScope -> 'Live -> 'Snapshot option,
        policy: SnapshotPolicy,
        storeName: string,
        logger: ILogger
    ) =

    let slots = ConcurrentDictionary<string, ScopeSlot<'Live>>()

    // Scopes mutated since their last successful persist, keyed by scope key
    // so repeated marks collapse.
    let dirty = ConcurrentDictionary<string, VectorScope>()

    // Scopes whose corrupt load has been reported this process: the
    // observers fire once per scope, while a refusal is raised on every
    // attempt.
    let reported = ConcurrentDictionary<string, byte>()

    let loadGate = new SemaphoreSlim(1, 1)

    // Signals the flush loop that the threshold was crossed. `(0, 1)`
    // collapses a burst of signals into one pass.
    let flushSignal = new SemaphoreSlim(0, 1)
    let cts = new CancellationTokenSource()
    let mutable dirtyMarks = 0
    let mutable disposed = 0

    let blobName (scope: VectorScope) = SnapshotLayout.blobName layout scope

    let rethrow (ex: exn) : 'a =
        Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
        Unchecked.defaultof<'a>

    let markDirty (scope: VectorScope) =
        dirty[VectorScope.key scope] <- scope
        let marks = Interlocked.Increment(&dirtyMarks)

        match policy.FlushChunkThreshold with
        | Some threshold when marks >= threshold ->
            try
                flushSignal.Release() |> ignore
            with :? SemaphoreFullException ->
                ()
        | _ -> ()

    let persistScope (scope: VectorScope) = async {
        match slots.TryGetValue(VectorScope.key scope) with
        | false, _ -> ()
        | true, slot ->
            let! written = async {
                match slot.Reading(snapshot scope) with
                | Some rows ->
                    let! uploaded = storage.Upload(layout.Container, blobName scope, codec.Encode rows)
                    return uploaded |> Result.map ignore
                | None -> return! storage.Delete(layout.Container, blobName scope)
            }

            match written with
            | Ok() -> ()
            | Error e ->
                logger.Warn
                    $"[{storeName}] Failed to persist scope {VectorScope.key scope}: {e} — will retry on next flush."

                markDirty scope
    }

    let flushAll () = async {
        // Reset the counter before the drain, so marks arriving during the
        // persist re-arm the threshold for the next pass.
        let drained = Interlocked.Exchange(&dirtyMarks, 0)

        // Snapshot-and-clear: a mutation arriving during this flush re-marks
        // its scope for the next pass rather than blocking this one.
        let scopes = [
            for KeyValue(key, _) in dirty do
                match dirty.TryRemove key with
                | true, scope -> yield scope
                | _ -> ()
        ]

        if not scopes.IsEmpty then
            let watch = Diagnostics.Stopwatch.StartNew()

            for scope in scopes do
                try
                    do! persistScope scope
                with ex ->
                    logger.Error($"[{storeName}] Flush failed for {VectorScope.key scope}", Some ex)
                    markDirty scope

            watch.Stop()

            match policy.FlushTelemetry with
            | Some telemetry -> telemetry.RecordFlush(drained, watch.ElapsedMilliseconds)
            | None -> ()
    }

    /// Report a corrupt load to the observers (once per scope), then refuse
    /// or let the scope start empty, as the policy says.
    let handleCorruption (scope: VectorScope) (bytes: int) (reason: string) = async {
        let scopeKey = VectorScope.key scope
        let location = blobName scope
        let handling = policy.OnCorrupt
        let refusing = handling.Policy = CorruptPolicy.Refuse

        let outcome =
            if refusing then
                "refusing the scope until the blob is repaired"
            else
                "starting empty"

        if reported.TryAdd(scopeKey, 0uy) then
            match handling.Telemetry with
            | Some telemetry -> telemetry.RecordIndexLoadError scopeKey
            | None -> ()

            do!
                handling.AuditLog.Record(
                    AuditScope.shard KnowledgeSourceModule.value,
                    AuditSubject.system storeName,
                    KnowledgeIndexLoadFailed {
                        ScopeKey = scopeKey
                        Reason = reason
                        Bytes = bytes
                        BlobLocation = location
                    }
                )
                |> AuditRow.orRefuse

            match handling.Notifications with
            | Some channel ->
                let consequence =
                    if refusing then
                        "Retrieval and writes for this scope are refused until the blob is repaired."
                    else
                        "Retrieval for this scope is running empty until the blob is repaired."

                do!
                    channel.Publish(
                        NotificationKind.PlatformReservedScope,
                        Notification.SystemMessage(
                            SystemMessageLevel.Error,
                            $"RAG knowledge index for scope '{scopeKey}' failed to load (corrupt blob '{location}', {bytes} bytes): {reason}. {consequence}"
                        )
                    )
            | None -> ()

            logger.Warn
                $"[{storeName}] Corrupt index for {scopeKey}: {reason} — {outcome} (telemetry + audit + SystemMessage emitted)."
        else
            logger.Warn
                $"[{storeName}] Corrupt index for {scopeKey}: {reason} — {outcome} (already reported this process)."

        if refusing then
            raise (RagScopeSnapshotUnreadableException(scopeKey, location, reason, SnapshotFault.Corrupt))
    }

    /// Read the scope's snapshot into a slot. Marked loaded (a slot exists)
    /// only after a completed attempt; an unreadable or refused-corrupt
    /// snapshot leaves the scope unloaded, so the next access retries.
    let loadScope (scope: VectorScope) = async {
        let scopeKey = VectorScope.key scope
        let! load = BlobSnapshot.read storage layout.Container (blobName scope) codec

        let! (live: 'Live), stale = async {
            match load with
            | SnapshotLoad.Absent -> return empty scope, false
            | SnapshotLoad.Unreadable reason ->
                return raise (RagScopeSnapshotUnreadableException(scopeKey, blobName scope, reason))
            | SnapshotLoad.Corrupt(bytes, reason) ->
                do! handleCorruption scope bytes reason
                return empty scope, false
            | SnapshotLoad.Loaded rows ->
                let materialised =
                    try
                        Ok(materialise scope rows)
                    with ex ->
                        Error ex.Message

                match materialised with
                | Ok result -> return result
                | Error reason ->
                    do! handleCorruption scope 0 reason
                    return empty scope, false
        }

        slots[scopeKey] <- ScopeSlot<'Live>(scope, live)

        if stale then
            markDirty scope
    }

    let ensureLoaded (scope: VectorScope) = async {
        let scopeKey = VectorScope.key scope

        if not (slots.ContainsKey scopeKey) then
            do! loadGate.WaitAsync() |> Async.AwaitTask

            try
                if not (slots.ContainsKey scopeKey) then
                    do! loadScope scope
            finally
                loadGate.Release() |> ignore
    }

    let ensureLoadedForSearch (scope: VectorScope) = async {
        match! ensureLoaded scope |> Async.Catch with
        | Choice1Of2() -> ()
        | Choice2Of2(:? RagScopeSnapshotUnreadableException as refused) when refused.Fault = SnapshotFault.Unreadable ->
            logger.Warn
                $"[{storeName}] Snapshot '{refused.BlobLocation}' for {refused.ScopeKey} exists but could not be read ({refused.Reason}) — searching without it; mutations of the scope are refused until it reads."
        | Choice2Of2 ex -> return rethrow ex
    }

    let flushLoop = async {
        while not cts.IsCancellationRequested do
            try
                let! _ = flushSignal.WaitAsync(policy.FlushIntervalMs, cts.Token) |> Async.AwaitTask

                if not cts.IsCancellationRequested then
                    do! flushAll ()
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.Error($"[{storeName}] Flush loop error", Some ex)
    }

    do Async.Start(flushLoop, cts.Token)

    /// The snapshot layout this index persists under.
    member _.Layout = layout

    /// The blob holding `scope`'s snapshot.
    member _.BlobName(scope: VectorScope) = blobName scope

    /// Guarantee `scope`'s snapshot has been read into memory. Raises
    /// `RagScopeSnapshotUnreadableException` when the blob exists but cannot be
    /// read, or is corrupt under `CorruptPolicy.Refuse`; the scope stays
    /// unloaded and the next call retries.
    member _.EnsureLoaded(scope: VectorScope) : Async<unit> = ensureLoaded scope

    /// The retrieval path's load: an unreadable snapshot degrades the scope to
    /// no results for this call, with a warning, instead of failing the whole
    /// search; the scope stays unloaded, so every mutation still refuses. A
    /// corrupt snapshot under `CorruptPolicy.Refuse` still raises.
    member _.EnsureLoadedForSearch(scope: VectorScope) : Async<unit> = ensureLoadedForSearch scope

    /// Load the platform and deployment scopes, which every deployment reads.
    /// A composition calls it at start, so a corrupt snapshot under
    /// `CorruptPolicy.Refuse` fails the boot; without it they load on first
    /// access like any other scope.
    member _.Start() : Async<unit> = async {
        do! ensureLoadedForSearch Platform
        do! ensureLoadedForSearch Deployment
    }

    /// Hydrate `scope` (refusing as `EnsureLoaded` does), then run `update`
    /// over its live form alone. `update` returns its result and whether it
    /// changed the scope; a change marks the scope dirty for the next flush.
    member _.Mutate(scope: VectorScope, update: 'Live -> 'R * bool) : Async<'R> = async {
        do! ensureLoaded scope
        let slot = slots[VectorScope.key scope]
        let result, changed = slot.Writing update

        if changed then
            markDirty scope

        return result
    }

    /// Run `read` over `scope`'s live form, beside any other reader of it;
    /// `None` when the scope is not loaded. Never loads: call `EnsureLoaded`
    /// or `EnsureLoadedForSearch` first.
    member _.Read(scope: VectorScope, read: 'Live -> 'R) : 'R option =
        match slots.TryGetValue(VectorScope.key scope) with
        | true, slot -> Some(slot.Reading read)
        | _ -> None

    /// Every scope the index holds: a loaded scope for which `holds` is true
    /// (so a wipe not yet flushed is not listed), and every scope persisted
    /// under the layout that is not loaded yet (so a re-embed sweep after a
    /// restart sees the cold ones).
    member _.ListScopes(holds: 'Live -> bool) : Async<VectorScope list> = async {
        let! persisted = storage.List(layout.Container, layout.Container + "/")

        let warm = [
            for KeyValue(_, slot) in slots do
                if slot.Reading holds then
                    yield slot.Scope
        ]

        let cold =
            persisted
            |> List.choose (SnapshotLayout.tryScopeOfBlob layout)
            |> List.filter (fun scope -> not (slots.ContainsKey(VectorScope.key scope)))

        return warm @ cold |> List.distinct
    }

    /// Persist every dirty scope now.
    member _.Flush() : Async<unit> = flushAll ()

    /// Stop the flush loop and flush once more, so no acknowledged write is
    /// lost across shutdown. Idempotent.
    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                cts.Cancel()

                try
                    flushAll () |> Async.RunSynchronously
                with ex ->
                    logger.Error($"[{storeName}] Final flush on dispose failed", Some ex)

                cts.Dispose()
                flushSignal.Dispose()
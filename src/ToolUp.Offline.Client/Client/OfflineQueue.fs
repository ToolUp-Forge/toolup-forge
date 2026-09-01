// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Offline.Client.OfflineQueue

open System
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Offline

// ─── Phase 24 — the durable client-side mutation queue ───────────────
//
// IndexedDB-backed, because it is the only browser store that is both
// durable across a tab close and large enough for entity payloads
// (`localStorage` is ~5 MB and synchronous — a field worker's day of
// queued inspections would blow it and block the main thread doing so).
//
// **No IndexedDB binding package.** The surface needed here is small
// and fixed — open a database, run one transaction against one object
// store, read/put/delete by key — so it is bound directly with
// `[<Emit>]` rather than by adding a third-party dependency to the
// SDK's supply chain. `Directory.Packages.props` carries no IndexedDB
// binding today, and this file is the whole reason one would have been
// added.
//
// **Promises, not `Async.FromContinuations`.** IndexedDB is
// callback-shaped, so the natural F# wrapper is `FromContinuations` —
// and that path is recorded IN THIS REPO as silently no-opping under
// Fable 5 when driven through `Cmd.OfAsync` (see the note in
// `Platform.Client/Client/FileManagerUI.fs`). Every request below is
// therefore wrapped into a JS promise inside the `[<Emit>]` body and
// awaited with `Async.AwaitPromise`, which is the pattern
// `Platform.Client/Client/CsrfClient.fs` already proves.

// ─── Wire shape ──────────────────────────────────────────────────────

/// Timestamp round-trip through the store.
///
/// ISO-8601 round-trip format, and a total parse: a record written by a
/// different SDK build with an unreadable timestamp yields
/// `DateTimeOffset.MinValue` rather than throwing. A queue that cannot
/// be READ is a queue whose contents are lost, so no single bad record
/// may take the drain down with it.
module private Timestamps =
    let toWire (value: DateTimeOffset) : string = value.ToString "o"

    let ofWire (raw: string) : DateTimeOffset =
        if String.IsNullOrWhiteSpace raw then
            DateTimeOffset.MinValue
        else
            try
                DateTimeOffset.Parse raw
            with _ ->
                DateTimeOffset.MinValue

// ─── The seam ────────────────────────────────────────────────────────

/// The durable queue of pending offline writes.
///
/// **Six-rule portability audit** (GP 12 — the interface is written to
/// pass it so a worker-thread, OPFS or in-memory implementation is
/// possible without changing a caller):
///
///  1. *Identity by value* — `MutationId` is a `string`; nothing here
///     returns a live handle, cursor or transaction.
///  2. *Async at every boundary* — every member returns `Async<_>`.
///  3. *Retry as data* — the queue stores `Attempts` and nothing else
///     about retrying; the schedule is the caller's `RetryPolicy`
///     record. No `OnFailure` callback parameter appears anywhere.
///  4. *Stateless between calls* — every member takes the ids it needs;
///     the implementation holds no cross-call state beyond the opened
///     database handle, which is a resource, not semantics.
///  5. *No cross-shard ordering promise* — ordering is guaranteed only
///     within one client's queue, by `LocalRevision`. Nothing is
///     promised about ordering relative to another device's queue, and
///     the server applies each mutation independently.
///  6. *Precision at the lower bound* — the queue stores `EnqueuedAt`
///     to the precision the browser clock supplies (typically
///     millisecond, coarsened further by cross-origin-isolation
///     mitigations). It promises milliseconds and no more.
type IOfflineQueue =
    /// Add a mutation. Replaces any existing entry with the same
    /// `MutationId` — enqueue is idempotent so a retried local write
    /// cannot duplicate the queue entry.
    abstract Enqueue: QueuedMutation -> Async<unit>

    /// Every entry, in `LocalRevision` order, whatever its state.
    /// Drives the badge and the conflict resolver.
    abstract List: unit -> Async<QueueEntry list>

    /// The entries eligible for replay right now, in `LocalRevision`
    /// order: `Pending`, plus `Failed` entries whose backoff has
    /// elapsed. `now` and `policy` are parameters rather than ambient
    /// state (rule 4) so the caller's clock and schedule are the ones
    /// that decide.
    abstract Drain: policy: RetryPolicy * now: DateTimeOffset -> Async<QueuedMutation list>

    /// The server applied it. Removes the entry — a settled mutation
    /// has no reason to occupy the store.
    abstract MarkApplied: MutationId -> Async<unit>

    /// The server reported a conflict. Parks the entry as `Conflicted`
    /// and stores the server document alongside it so the resolver can
    /// render both sides without another round trip.
    abstract MarkConflicted: MutationId * serverEntity: byte[] -> Async<unit>

    /// The user chose a side. `KeepLocal` re-arms the entry as
    /// `Pending` rebased onto `rebaseVersion`; `KeepServer` removes it;
    /// `Defer` leaves it conflicted.
    abstract MarkConflict: MutationId * ConflictResolution * rebaseVersion: int -> Async<unit>

    /// Replay failed transiently. Increments the attempt count and
    /// parks the entry as `Failed`; the caller's `RetryPolicy` decides
    /// when `Drain` offers it again.
    abstract MarkFailed: MutationId * reason: string -> Async<unit>

    /// The server refused it permanently. Removes the entry — retrying
    /// a `Rejected` mutation loops forever.
    abstract Discard: MutationId -> Async<unit>

    /// Drop everything. The sign-out path: one user's queued writes
    /// must never replay under the next user's credentials.
    abstract Clear: unit -> Async<unit>

// ─── IndexedDB bindings ──────────────────────────────────────────────
//
// One object store, `mutations`, keyed by `id`, with a `revision`
// index so reads come back in enqueue order without a client-side
// sort of the whole set.

[<Emit("(typeof indexedDB !== 'undefined' && indexedDB !== null)")>]
let private indexedDbAvailable () : bool = jsNative

[<Emit("""new Promise(function (resolve, reject) {
    var req = indexedDB.open($0, 1);
    req.onupgradeneeded = function () {
        var db = req.result;
        if (!db.objectStoreNames.contains('mutations')) {
            var store = db.createObjectStore('mutations', { keyPath: 'id' });
            store.createIndex('revision', 'revision', { unique: false });
        }
    };
    req.onsuccess = function () { resolve(req.result); };
    req.onerror = function () { reject(req.error); };
})""")>]
let private openDb (databaseName: string) : JS.Promise<obj> = jsNative

[<Emit("""new Promise(function (resolve, reject) {
    var tx = $0.transaction('mutations', 'readwrite');
    var store = tx.objectStore('mutations');
    store.put($1);
    tx.oncomplete = function () { resolve(); };
    tx.onerror = function () { reject(tx.error); };
    tx.onabort = function () { reject(tx.error); };
})""")>]
let private dbPut (db: obj) (record: obj) : JS.Promise<unit> = jsNative

[<Emit("""new Promise(function (resolve, reject) {
    var tx = $0.transaction('mutations', 'readwrite');
    var store = tx.objectStore('mutations');
    store.delete($1);
    tx.oncomplete = function () { resolve(); };
    tx.onerror = function () { reject(tx.error); };
    tx.onabort = function () { reject(tx.error); };
})""")>]
let private dbDelete (db: obj) (key: string) : JS.Promise<unit> = jsNative

[<Emit("""new Promise(function (resolve, reject) {
    var tx = $0.transaction('mutations', 'readwrite');
    tx.objectStore('mutations').clear();
    tx.oncomplete = function () { resolve(); };
    tx.onerror = function () { reject(tx.error); };
})""")>]
let private dbClear (db: obj) : JS.Promise<unit> = jsNative

[<Emit("""new Promise(function (resolve, reject) {
    var tx = $0.transaction('mutations', 'readonly');
    var req = tx.objectStore('mutations').index('revision').getAll();
    req.onsuccess = function () { resolve(req.result || []); };
    req.onerror = function () { reject(req.error); };
})""")>]
let private dbGetAll (db: obj) : JS.Promise<obj array> = jsNative

[<Emit("""new Promise(function (resolve, reject) {
    var tx = $0.transaction('mutations', 'readonly');
    var req = tx.objectStore('mutations').get($1);
    req.onsuccess = function () { resolve(req.result || null); };
    req.onerror = function () { reject(req.error); };
})""")>]
let private dbGet (db: obj) (key: string) : JS.Promise<obj> = jsNative

// ─── Record <-> entry mapping ────────────────────────────────────────

let private toRecord (entry: QueueEntry) : obj =
    let m = entry.Mutation

    let record = createEmpty<obj>
    record?id <- m.Id
    record?enqueuedAt <- Timestamps.toWire m.EnqueuedAt
    record?scopeId <- m.ScopeId
    record?entityType <- m.EntityType
    record?entityId <- m.EntityId
    record?op <- MutationOp.name m.Operation
    record?payload <- m.Payload
    record?baseVersion <- m.BaseVersion
    record?revision <- m.LocalRevision
    record?state <- MutationState.name entry.State

    record?reason <-
        match entry.State with
        | Failed reason -> reason
        | _ -> ""

    record?attempts <- entry.Attempts

    record?serverEntity <-
        match entry.ServerEntity with
        | Some bytes -> box bytes
        | None -> null

    record

/// Read one stored record back. `None` for a record this build cannot
/// interpret (an unknown `op`, say) — see the totality note on
/// `Timestamps`: one unreadable row must not fail the whole read.
let private ofRecord (record: obj) : QueueEntry option =
    if isNull (box record) then
        None
    else
        match MutationOp.tryParse (string record?op) with
        | None -> None
        | Some op ->
            let payload: byte[] =
                match record?payload with
                | null -> Array.empty
                | value -> unbox value

            let serverEntity: byte[] option =
                match record?serverEntity with
                | null -> None
                | value -> Some(unbox value)

            let attempts: int = unbox record?attempts
            let reason = string record?reason

            let state =
                match string record?state with
                | "applied" -> AppliedState
                | "conflicted" -> Conflicted
                | "failed" -> Failed reason
                | _ -> Pending

            Some {
                Mutation = {
                    Id = string record?id
                    EnqueuedAt = Timestamps.ofWire (string record?enqueuedAt)
                    ScopeId = string record?scopeId
                    EntityType = string record?entityType
                    EntityId = string record?entityId
                    Operation = op
                    Payload = payload
                    BaseVersion = unbox record?baseVersion
                    LocalRevision = unbox record?revision
                }
                State = state
                Attempts = attempts
                ServerEntity = serverEntity
            }

// ─── IndexedDB implementation ────────────────────────────────────────

/// The browser queue. `databaseName` scopes the store — pass a
/// per-deployment name so two apps on one origin do not share a queue,
/// and include the signed-in user where an origin is shared between
/// accounts.
type IndexedDbOfflineQueue(databaseName: string) =
    let mutable handle: obj option = None

    let db () : Async<obj> = async {
        match handle with
        | Some d -> return d
        | None ->
            let! d = openDb databaseName |> Async.AwaitPromise
            handle <- Some d
            return d
    }

    /// Read-modify-write one entry. `None` from `update` leaves the
    /// entry untouched; a missing entry is a no-op rather than an
    /// error, because the coordinator may report on a mutation a
    /// concurrent `Clear` has already removed.
    let updateEntry (id: MutationId) (update: QueueEntry -> QueueEntry option) : Async<unit> = async {
        let! d = db ()
        let! raw = dbGet d id |> Async.AwaitPromise

        match ofRecord raw with
        | None -> return ()
        | Some entry ->
            match update entry with
            | None -> return ()
            | Some updated -> do! dbPut d (toRecord updated) |> Async.AwaitPromise
    }

    interface IOfflineQueue with
        member _.Enqueue(mutation: QueuedMutation) = async {
            let! d = db ()

            let entry = {
                Mutation = mutation
                State = Pending
                Attempts = 0
                ServerEntity = None
            }

            do! dbPut d (toRecord entry) |> Async.AwaitPromise
        }

        member _.List() = async {
            let! d = db ()
            let! records = dbGetAll d |> Async.AwaitPromise

            return
                records
                |> Array.toList
                |> List.choose ofRecord
                |> List.sortBy _.Mutation.LocalRevision
        }

        member this.Drain(policy: RetryPolicy, now: DateTimeOffset) = async {
            let! entries = (this :> IOfflineQueue).List()
            return DrainSelection.eligible policy now entries
        }

        member _.MarkApplied(id: MutationId) = async {
            let! d = db ()
            do! dbDelete d id |> Async.AwaitPromise
        }

        member _.MarkConflicted(id: MutationId, serverEntity: byte[]) =
            updateEntry id (fun entry ->
                Some {
                    entry with
                        State = Conflicted
                        ServerEntity = Some serverEntity
                })

        member this.MarkConflict(id: MutationId, resolution: ConflictResolution, rebaseVersion: int) =
            match resolution with
            | KeepServer -> (this :> IOfflineQueue).MarkApplied id
            | Defer -> async { return () }
            | KeepLocal ->
                updateEntry id (fun entry ->
                    Some {
                        entry with
                            State = Pending
                            Attempts = 0
                            ServerEntity = None
                            Mutation = {
                                entry.Mutation with
                                    // Rebase onto what the server now
                                    // holds, so the next replay passes
                                    // the handler's version guard
                                    // instead of conflicting again on
                                    // the same stale base — an
                                    // unrebased KeepLocal is an
                                    // infinite conflict loop.
                                    BaseVersion = rebaseVersion
                            }
                    })

        member _.MarkFailed(id: MutationId, reason: string) =
            updateEntry id (fun entry ->
                Some {
                    entry with
                        State = Failed reason
                        Attempts = entry.Attempts + 1
                })

        member _.Discard(id: MutationId) = async {
            let! d = db ()
            do! dbDelete d id |> Async.AwaitPromise
        }

        member _.Clear() = async {
            let! d = db ()
            do! dbClear d |> Async.AwaitPromise
        }

/// Volatile fallback. Used when `indexedDB` is unavailable — private
/// browsing on some engines, an embedded webview with storage disabled,
/// or a test harness.
///
/// **It is honestly named.** Nothing here survives a reload, so an
/// offline edit made under this queue is lost if the tab closes before
/// reconnect. That is strictly better than throwing at the point of the
/// user's edit, and strictly worse than durability — which is why
/// `create` warns to the console rather than substituting it silently.
type InMemoryOfflineQueue() =
    let mutable entries: Map<MutationId, QueueEntry> = Map.empty

    let update (id: MutationId) (f: QueueEntry -> QueueEntry) = async {
        match Map.tryFind id entries with
        | None -> return ()
        | Some entry ->
            entries <- Map.add id (f entry) entries
            return ()
    }

    interface IOfflineQueue with
        member _.Enqueue(mutation: QueuedMutation) = async {
            entries <-
                Map.add
                    mutation.Id
                    {
                        Mutation = mutation
                        State = Pending
                        Attempts = 0
                        ServerEntity = None
                    }
                    entries
        }

        member _.List() = async { return entries |> Map.toList |> List.map snd |> List.sortBy _.Mutation.LocalRevision }

        member this.Drain(policy: RetryPolicy, now: DateTimeOffset) = async {
            let! all = (this :> IOfflineQueue).List()
            return DrainSelection.eligible policy now all
        }

        member _.MarkApplied(id: MutationId) = async { entries <- Map.remove id entries }

        member _.MarkConflicted(id: MutationId, serverEntity: byte[]) =
            update id (fun entry -> {
                entry with
                    State = Conflicted
                    ServerEntity = Some serverEntity
            })

        member this.MarkConflict(id: MutationId, resolution: ConflictResolution, rebaseVersion: int) =
            match resolution with
            | KeepServer -> (this :> IOfflineQueue).MarkApplied id
            | Defer -> async { return () }
            | KeepLocal ->
                update id (fun entry -> {
                    entry with
                        State = Pending
                        Attempts = 0
                        ServerEntity = None
                        Mutation = {
                            entry.Mutation with
                                BaseVersion = rebaseVersion
                        }
                })

        member _.MarkFailed(id: MutationId, reason: string) =
            update id (fun entry -> {
                entry with
                    State = Failed reason
                    Attempts = entry.Attempts + 1
            })

        member _.Discard(id: MutationId) = async { entries <- Map.remove id entries }

        member _.Clear() = async { entries <- Map.empty }

/// Build the best queue this browser supports. Falls back to the
/// volatile queue with a console warning — never silently, because the
/// difference between the two is whether the user's work survives a
/// tab close.
let create (databaseName: string) : IOfflineQueue =
    if indexedDbAvailable () then
        IndexedDbOfflineQueue databaseName :> IOfflineQueue
    else
        Browser.Dom.console.warn (
            "[ToolUp.Offline] indexedDB is unavailable; queued mutations will NOT survive a page reload. "
            + "Offline edits made in this session are lost if the tab closes before reconnecting."
        )

        InMemoryOfflineQueue() :> IOfflineQueue
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `ScopedSnapshotIndex` — the one hydration, flush and corruption law under
/// the three in-process retrieval stores. The first half pins every member of
/// the base over a fake `IBlobStorage`; the second pins what the base means
/// for each store: a corrupt snapshot under `CorruptPolicy.Refuse` refuses
/// with the one exception and reports to the same observers in all three
/// (before the base, the BM25 and HNSW stores only logged), and no store
/// constructor touches its storage.
module ToolUp.Platform.Tests.InProcess.ScopedSnapshotIndexTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra.IVectorStore
open ToolUp.Platform.Infra.ISparseIndex
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Infra

// ─── Fakes ────────────────────────────────────────────────────────────

/// An `IBlobStorage` over an in-memory one that counts every operation and
/// can make a blob unreadable: while a name is poisoned, `Download` fails
/// while `Exists` still reports the blob.
type private ProbeStorage() =
    let inner = InMemoryBlobStorage() :> IBlobStorage
    let calls = ConcurrentQueue<string * string>()
    let poisoned = ConcurrentDictionary<string, byte>()

    member _.Calls = List.ofSeq calls

    member _.CallsOf(op: string) =
        calls |> Seq.filter (fun (o, _) -> o = op) |> Seq.length

    member _.Poison(blobName: string) = poisoned[blobName] <- 0uy
    member _.Heal(blobName: string) = poisoned.TryRemove blobName |> ignore

    member _.Put(blobName: string, bytes: byte[]) =
        inner.Upload("_rag", blobName, bytes) |> Async.RunSynchronously |> ignore

    member _.Get(blobName: string) =
        match inner.Download("_rag", blobName) |> Async.RunSynchronously with
        | Ok bytes -> Some(Text.Encoding.UTF8.GetString bytes)
        | Error _ -> None

    interface IBlobStorage with
        member _.CanComposeFrom = false
        member _.ComposeFrom(c, t, s) = inner.ComposeFrom(c, t, s)

        member _.Upload(container, blobName, content) =
            calls.Enqueue("Upload", blobName)
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) =
            calls.Enqueue("Download", blobName)

            if poisoned.ContainsKey blobName then
                async { return Error "simulated read fault" }
            else
                inner.Download(container, blobName)

        member _.Delete(container, blobName) =
            calls.Enqueue("Delete", blobName)
            inner.Delete(container, blobName)

        member _.List(container, prefix) =
            calls.Enqueue("List", prefix)
            inner.List(container, prefix)

        member _.Exists(container, blobName) =
            calls.Enqueue("Exists", blobName)
            inner.Exists(container, blobName)

        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

type private RecordingAuditLog() =
    let rows = ConcurrentQueue<AuditSubject * AuditEvent>()
    member _.Rows = List.ofSeq rows

    interface IAuditLog with
        member _.Record(_, actor, audit) = async {
            rows.Enqueue(actor, audit)
            return Ok()
        }

        member _.GetAuditTrail(_, _, _) = async { return [] }

type private RecordingChannel() =
    let published = ConcurrentQueue<string * Notification>()
    member _.Published = List.ofSeq published

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue(scopeId, notification) }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

/// The observers a corrupt load reports to, and what each saw.
type private Observers() =
    member val Telemetry = ToolUp.RAG.RagTelemetry.RollingRagTelemetry()
    member val Audit = RecordingAuditLog()
    member val Channel = RecordingChannel()

    member this.Handling(policy: CorruptPolicy) : CorruptionHandling = {
        Policy = policy
        Telemetry = Some(this.Telemetry :> ToolUp.Platform.Infra.IRagTelemetry.IRagTelemetry)
        AuditLog = this.Audit :> IAuditLog
        Notifications = Some(this.Channel :> INotificationChannel)
    }

    member this.LoadErrors =
        let telemetry = this.Telemetry :> ToolUp.Platform.Infra.IRagTelemetry.IRagTelemetry
        (telemetry.Snapshot() |> Async.RunSynchronously).IndexLoadErrors

    member this.LoadFailedRows =
        this.Audit.Rows
        |> List.choose (fun (_, audit) ->
            match audit with
            | KnowledgeIndexLoadFailed payload -> Some payload
            | _ -> None)

    member this.SystemErrors =
        this.Channel.Published
        |> List.filter (fun (_, n) ->
            match n with
            | Notification.SystemMessage(SystemMessageLevel.Error, _) -> true
            | _ -> false)

// ─── A small index over the base ──────────────────────────────────────

/// The live form of the test index: chunk id → text. Its snapshot is the
/// rows as a sorted list, so a written blob is deterministic.
type private Live = Dictionary<string, string>

let private layout = SnapshotLayout.rag "probe.json"

let private codec = BlobCodec.json<(string * string) list>

let private rowsOf (live: Live) =
    [ for KeyValue(k, v) in live -> k, v ] |> List.sort

let private newIndex
    (storage: IBlobStorage)
    (handling: CorruptionHandling)
    (configure: SnapshotPolicy -> SnapshotPolicy)
    (materialise: VectorScope -> (string * string) list -> Live * bool)
    (snapshot: VectorScope -> Live -> (string * string) list option)
    =
    new ScopedSnapshotIndex<(string * string) list, Live>(
        storage,
        layout,
        codec,
        (fun _ -> Live()),
        materialise,
        snapshot,
        configure (SnapshotPolicy.create handling),
        "ProbeIndex",
        SilentLogger()
    )

let private plainMaterialise (_: VectorScope) (rows: (string * string) list) =
    let live = Live()

    for (k, v) in rows do
        live[k] <- v

    live, false

let private plainSnapshot (_: VectorScope) (live: Live) = Some(rowsOf live)

let private plainIndex (storage: IBlobStorage) =
    newIndex
        storage
        (CorruptionHandling.logOnly CorruptPolicy.StartEmpty)
        (fun p -> { p with FlushIntervalMs = 60_000 })
        plainMaterialise
        plainSnapshot

let private freshTeam () = Team(Guid.NewGuid().ToString "N")

let private blobOf (scope: VectorScope) = SnapshotLayout.blobName layout scope

let private put (storage: ProbeStorage) (scope: VectorScope) (rows: (string * string) list) =
    storage.Put(blobOf scope, codec.Encode rows)

let private garbage = Text.Encoding.UTF8.GetBytes "{ this is not a snapshot"

let private refusal (outcome: Choice<'a, exn>) =
    match outcome with
    | Choice2Of2(:? RagScopeSnapshotUnreadableException as ex) -> Some ex
    | _ -> None

let private add (key: string) (value: string) (live: Live) =
    live[key] <- value
    (), true

// ─── The base, member by member ───────────────────────────────────────

let private baseTests =
    testList "the base" [
        testCase "the constructor does no I/O" (fun () ->
            let storage = ProbeStorage()
            use _ = plainIndex storage
            Expect.isEmpty storage.Calls "constructing an index must not touch its storage")

        testAsync "EnsureLoaded on an absent snapshot loads the scope empty, once" {
            let storage = ProbeStorage()
            use index = plainIndex storage
            let scope = freshTeam ()
            do! index.EnsureLoaded scope
            do! index.EnsureLoaded scope
            Expect.equal (index.Read(scope, _.Count)) (Some 0) "an absent snapshot is loaded-and-empty"
            Expect.equal (storage.CallsOf "Download") 1 "a loaded scope is not read again"
        }

        testAsync "EnsureLoaded is single-flight under concurrent callers" {
            let storage = ProbeStorage()
            let scope = freshTeam ()
            put storage scope [ "a", "1" ]
            use index = plainIndex storage

            do! [ for _ in 1..16 -> index.EnsureLoaded scope ] |> Async.Parallel |> Async.Ignore

            Expect.equal (storage.CallsOf "Download") 1 "concurrent first accesses load the scope once"
            Expect.equal (index.Read(scope, _.Count)) (Some 1) "the snapshot's rows are loaded"
        }

        testAsync "an unreadable snapshot refuses, stays unloaded and is retried" {
            let storage = ProbeStorage()
            let scope = freshTeam ()
            put storage scope [ "a", "1" ]
            storage.Poison(blobOf scope)
            use index = plainIndex storage

            let! first = index.EnsureLoaded scope |> Async.Catch

            match refusal first with
            | Some ex ->
                Expect.equal ex.Fault SnapshotFault.Unreadable "the fault names the unreadable blob"
                Expect.equal ex.BlobLocation (blobOf scope) "the refusal names the blob"
            | None -> failtestf "an unreadable snapshot must refuse, not %A" first

            Expect.isNone (index.Read(scope, _.Count)) "a refused scope is not loaded"
            storage.Heal(blobOf scope)
            do! index.EnsureLoaded scope
            Expect.equal (index.Read(scope, _.Count)) (Some 1) "the next access retries and loads"
        }

        testAsync "EnsureLoadedForSearch degrades an unreadable snapshot to nothing" {
            let storage = ProbeStorage()
            let scope = freshTeam ()
            put storage scope [ "a", "1" ]
            storage.Poison(blobOf scope)
            use index = plainIndex storage
            do! index.EnsureLoadedForSearch scope
            Expect.isNone (index.Read(scope, _.Count)) "the scope stays unloaded, so a mutation still refuses"
            let! mutated = index.Mutate(scope, add "b" "2") |> Async.Catch
            Expect.isSome (refusal mutated) "a mutation of an unreadable scope refuses"
        }

        testAsync "Mutate hydrates first, so a write after a restart keeps the persisted rows" {
            let storage = ProbeStorage()
            let scope = freshTeam ()
            put storage scope [ "a", "1" ]
            let index = plainIndex storage
            do! index.Mutate(scope, add "b" "2")
            (index :> IDisposable).Dispose()

            Expect.equal
                (storage.Get(blobOf scope))
                (Some """[["a","1"],["b","2"]]""")
                "the flushed blob holds the persisted row and the new one"
        }

        testAsync "a Mutate that changes nothing writes nothing" {
            let storage = ProbeStorage()
            let scope = freshTeam ()
            let index = plainIndex storage
            let! count = index.Mutate(scope, (fun live -> live.Count, false))
            Expect.equal count 0 "the mutation's result is returned"
            (index :> IDisposable).Dispose()
            Expect.equal (storage.CallsOf "Upload") 0 "an unchanged scope is not flushed"
        }

        testCase "Read never loads" (fun () ->
            let storage = ProbeStorage()
            use index = plainIndex storage
            Expect.isNone (index.Read(freshTeam (), _.Count)) "an unloaded scope reads as None"
            Expect.isEmpty storage.Calls "Read touches no storage")

        testAsync "a snapshot of None deletes the blob" {
            let storage = ProbeStorage()
            let scope = freshTeam ()
            put storage scope [ "a", "1" ]

            let index =
                newIndex
                    storage
                    (CorruptionHandling.logOnly CorruptPolicy.StartEmpty)
                    id
                    plainMaterialise
                    (fun _ live -> if live.Count = 0 then None else Some(rowsOf live))

            do!
                index.Mutate(
                    scope,
                    fun live ->
                        live.Clear()
                        (), true
                )

            do! index.Flush()
            (index :> IDisposable).Dispose()
            Expect.isNone (storage.Get(blobOf scope)) "an empty scope's blob is deleted"
            Expect.equal (storage.CallsOf "Delete") 1 "deleted once"
        }

        testAsync "a stale materialisation is written back at the next flush" {
            let storage = ProbeStorage()
            let scope = freshTeam ()
            put storage scope [ "a", "old" ]

            let index =
                newIndex
                    storage
                    (CorruptionHandling.logOnly CorruptPolicy.StartEmpty)
                    id
                    (fun _ rows ->
                        let live = Live()

                        for (k, _) in rows do
                            live[k] <- "upgraded"

                        live, true)
                    plainSnapshot

            do! index.EnsureLoaded scope
            do! index.Flush()
            (index :> IDisposable).Dispose()
            Expect.equal (storage.Get(blobOf scope)) (Some """[["a","upgraded"]]""") "the upgraded form is persisted"
        }

        testAsync "a materialisation that throws is a corrupt snapshot" {
            let storage = ProbeStorage()
            let scope = freshTeam ()
            put storage scope [ "a", "1" ]
            let observers = Observers()

            use index =
                newIndex
                    storage
                    (observers.Handling CorruptPolicy.StartEmpty)
                    id
                    (fun _ _ -> invalidOp "cannot materialise")
                    plainSnapshot

            do! index.EnsureLoaded scope
            Expect.equal (index.Read(scope, _.Count)) (Some 0) "the scope starts empty"
            Expect.equal observers.LoadErrors 1 "and the corruption is counted"
        }

        testAsync "ListScopes lists held warm scopes and cold persisted ones, and no other file" {
            let storage = ProbeStorage()
            let cold = freshTeam ()
            let warm = freshTeam ()
            let emptied = freshTeam ()
            put storage cold [ "a", "1" ]
            put storage emptied [ "a", "1" ]
            storage.Put($"_rag/{VectorScope.key (freshTeam ())}/other.json", garbage)
            use index = plainIndex storage
            do! index.Mutate(warm, add "a" "1")

            do!
                index.Mutate(
                    emptied,
                    fun live ->
                        live.Clear()
                        (), true
                )

            let! scopes = index.ListScopes(fun live -> live.Count > 0)

            Expect.equal
                (Set.ofList scopes)
                (Set.ofList [ cold; warm ])
                "a loaded scope answers from memory; a cold one from its blob"
        }

        testAsync "the chunk threshold flushes before the period" {
            let storage = ProbeStorage()
            let scope = freshTeam ()

            use index =
                newIndex
                    storage
                    (CorruptionHandling.logOnly CorruptPolicy.StartEmpty)
                    (fun p -> {
                        p with
                            FlushIntervalMs = 60_000
                            FlushChunkThreshold = Some 3
                    })
                    plainMaterialise
                    plainSnapshot

            for i in 1..3 do
                do! index.Mutate(scope, add (string i) "x")

            let deadline = DateTime.UtcNow.AddSeconds 10.0

            while storage.Get(blobOf scope).IsNone && DateTime.UtcNow < deadline do
                do! Async.Sleep 20

            Expect.isSome (storage.Get(blobOf scope)) "crossing the threshold flushed without waiting a minute"
        }

        testAsync "Start loads the platform and deployment scopes" {
            let storage = ProbeStorage()
            put storage Platform [ "p", "1" ]
            use index = plainIndex storage
            do! index.Start()
            Expect.equal (index.Read(Platform, _.Count)) (Some 1) "platform is loaded"
            Expect.equal (index.Read(Deployment, _.Count)) (Some 0) "deployment is loaded (absent, empty)"
        }

        testAsync "Dispose flushes, once" {
            let storage = ProbeStorage()
            let scope = freshTeam ()
            let index = plainIndex storage
            do! index.Mutate(scope, add "a" "1")
            (index :> IDisposable).Dispose()
            (index :> IDisposable).Dispose()
            Expect.equal (storage.CallsOf "Upload") 1 "the acknowledged write is flushed exactly once"
        }

        testAsync "a first write into a new scope races reads and listings safely" {
            // The stress shape of the HNSW torn-read guard, at the base: every
            // store's scope map is the base's now.
            let storage = ProbeStorage()
            use index = plainIndex storage

            for round in 1..20 do
                let scope = freshTeam ()

                let writers = [
                    for i in 1..8 -> async { do! index.Mutate(scope, add (string i) (string round)) }
                ]

                let readers = [
                    for _ in 1..8 ->
                        async {
                            do! index.EnsureLoadedForSearch scope
                            index.Read(scope, _.Count) |> ignore
                            let! _ = index.ListScopes(fun live -> live.Count > 0)
                            ()
                        }
                ]

                do! writers @ readers |> Async.Parallel |> Async.Ignore
                Expect.equal (index.Read(scope, _.Count)) (Some 8) "every concurrent write landed"
        }

        testAsync "a corrupt snapshot under StartEmpty starts empty and reports once" {
            let storage = ProbeStorage()
            let scope = freshTeam ()
            storage.Put(blobOf scope, garbage)
            let observers = Observers()

            use index =
                newIndex storage (observers.Handling CorruptPolicy.StartEmpty) id plainMaterialise plainSnapshot

            do! index.EnsureLoaded scope
            Expect.equal (index.Read(scope, _.Count)) (Some 0) "the scope starts empty"
            Expect.equal observers.LoadErrors 1 "one telemetry count"
            Expect.equal observers.LoadFailedRows.Length 1 "one audit row"
            Expect.equal observers.SystemErrors.Length 1 "one system message"
        }

        testAsync "a corrupt snapshot under Refuse refuses on every attempt and reports once" {
            let storage = ProbeStorage()
            let scope = freshTeam ()
            storage.Put(blobOf scope, garbage)
            let observers = Observers()

            use index =
                newIndex storage (observers.Handling CorruptPolicy.Refuse) id plainMaterialise plainSnapshot

            for _ in 1..2 do
                let! outcome = index.EnsureLoadedForSearch scope |> Async.Catch

                match refusal outcome with
                | Some ex ->
                    Expect.equal ex.Fault SnapshotFault.Corrupt "a search does not degrade a refused corrupt scope"
                | None -> failtestf "a corrupt snapshot under Refuse must refuse, not %A" outcome

            Expect.isNone (index.Read(scope, _.Count)) "the scope is never loaded"
            Expect.equal observers.LoadErrors 1 "reported once"
            Expect.equal observers.LoadFailedRows.Length 1 "audited once"
            Expect.equal observers.SystemErrors.Length 1 "announced once"
        }

        test "CorruptPolicy reads its configuration key" {
            Expect.equal (CorruptPolicy.ofSetting None) CorruptPolicy.StartEmpty "unset starts empty"
            Expect.equal (CorruptPolicy.ofSetting (Some "true")) CorruptPolicy.Refuse "true refuses"
            Expect.equal (CorruptPolicy.ofSetting (Some "false")) CorruptPolicy.StartEmpty "false starts empty"
        }

        test "SnapshotLayout reads back only its own file for a well-formed key" {
            let scope = Team "t1"
            Expect.equal (SnapshotLayout.blobName layout scope) "_rag/team:t1/probe.json" "the layout's blob name"

            Expect.equal
                (SnapshotLayout.tryScopeOfBlob layout "_rag/team:t1/probe.json")
                (Some scope)
                "its own file reads back"

            Expect.isNone (SnapshotLayout.tryScopeOfBlob layout "_rag/team:t1/index.json") "another store's file"
            Expect.isNone (SnapshotLayout.tryScopeOfBlob layout "_rag/team:t1/x/probe.json") "a nested name"
            Expect.isNone (SnapshotLayout.tryScopeOfBlob layout "_rag//probe.json") "an empty key"
        }
    ]

// ─── The three stores over it ─────────────────────────────────────────

type private StoreUnderTest = {
    Name: string
    FileName: string
    Build: IBlobStorage -> CorruptionHandling -> IDisposable
    Mutate: IDisposable -> VectorScope -> Async<unit>
    Search: IDisposable -> VectorScope -> Async<int>
}

let private vector: float32 array = [| 1.0f; 0.0f; 0.0f |]

let private chunk: TextChunk = {
    Content = "hello corrupt world"
    Metadata = Map.empty
}

let private stores: StoreUnderTest list = [
    {
        Name = "InMemoryVectorStore"
        FileName = "index.json"
        Build =
            fun storage handling ->
                new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(
                    storage,
                    SilentLogger(),
                    telemetry = handling.Telemetry.Value,
                    auditLog = handling.AuditLog,
                    notifications = handling.Notifications.Value,
                    corruptPolicy = handling.Policy
                )
                :> IDisposable
        Mutate = fun store scope -> (store :?> IVectorStore).Upsert scope "c1" vector chunk
        Search =
            fun store scope -> async {
                let! hits = (store :?> IVectorStore).Search [ scope ] vector 10
                return hits.Length
            }
    }
    {
        Name = "InMemoryBM25Index"
        FileName = "bm25.json"
        Build =
            fun storage handling ->
                new ToolUp.RAG.InMemoryBM25Index.InMemoryBM25Index(
                    storage,
                    SilentLogger(),
                    telemetry = handling.Telemetry.Value,
                    auditLog = handling.AuditLog,
                    notifications = handling.Notifications.Value,
                    corruptPolicy = handling.Policy
                )
                :> IDisposable
        Mutate = fun store scope -> (store :?> ISparseIndex).Upsert scope "c1" chunk
        Search =
            fun store scope -> async {
                let! hits = (store :?> ISparseIndex).Search [ scope ] "corrupt" 10
                return hits.Length
            }
    }
    {
        Name = "HnswVectorStore"
        FileName = "hnsw-index.json"
        Build =
            fun storage handling ->
                new ToolUp.RAG.VectorStores.Hnsw.HnswVectorStore.HnswVectorStore(
                    storage,
                    SilentLogger(),
                    telemetry = handling.Telemetry.Value,
                    auditLog = handling.AuditLog,
                    notifications = handling.Notifications.Value,
                    corruptPolicy = handling.Policy
                )
                :> IDisposable
        Mutate = fun store scope -> (store :?> IVectorStore).Upsert scope "c1" vector chunk
        Search =
            fun store scope -> async {
                let! hits = (store :?> IVectorStore).Search [ scope ] vector 10
                return hits.Length
            }
    }
]

let private storeTests =
    testSequenced
    <| testList "the three stores" [
        for store in stores do
            testCase $"{store.Name}: the constructor does no I/O" (fun () ->
                let storage = ProbeStorage()
                let observers = Observers()
                use _ = store.Build storage (observers.Handling CorruptPolicy.StartEmpty)
                Expect.isEmpty storage.Calls "constructing the store must not touch its storage")

            testAsync $"{store.Name}: a corrupt snapshot under Refuse refuses with the one exception and reports" {
                let storage = ProbeStorage()
                let scope = freshTeam ()
                storage.Put($"_rag/{VectorScope.key scope}/{store.FileName}", garbage)
                let observers = Observers()
                use built = store.Build storage (observers.Handling CorruptPolicy.Refuse)

                let! mutated = store.Mutate built scope |> Async.Catch

                match refusal mutated with
                | Some ex ->
                    Expect.equal ex.Fault SnapshotFault.Corrupt "the refusal is the corrupt fault"
                    Expect.equal ex.ScopeKey (VectorScope.key scope) "it names the scope"
                | None -> failtestf "a corrupt snapshot under Refuse must refuse a mutation, not %A" mutated

                let! searched = store.Search built scope |> Async.Catch
                Expect.isSome (refusal searched) "and a search"

                Expect.equal observers.LoadErrors 1 "the telemetry counter moved once"

                match observers.LoadFailedRows with
                | [ row ] ->
                    Expect.equal row.ScopeKey (VectorScope.key scope) "the audit row names the scope"
                    Expect.equal row.BlobLocation $"_rag/{VectorScope.key scope}/{store.FileName}" "and the blob"
                    Expect.equal row.Bytes garbage.Length "and its size"
                | rows -> failtestf "expected one KnowledgeIndexLoadFailed row, got %A" rows

                Expect.equal observers.SystemErrors.Length 1 "one Error system message"
                Expect.equal (storage.CallsOf "Upload") 0 "nothing was written over the corrupt blob"
            }

            testAsync $"{store.Name}: a corrupt snapshot under StartEmpty starts empty and records that it did" {
                let storage = ProbeStorage()
                let scope = freshTeam ()
                storage.Put($"_rag/{VectorScope.key scope}/{store.FileName}", garbage)
                let observers = Observers()
                use built = store.Build storage (observers.Handling CorruptPolicy.StartEmpty)
                let! hits = store.Search built scope
                Expect.equal hits 0 "the scope starts empty"
                Expect.equal observers.LoadErrors 1 "the telemetry counter moved"
                Expect.equal observers.LoadFailedRows.Length 1 "the audit row was written"
                Expect.equal observers.SystemErrors.Length 1 "the system message was published"
            }
    ]

let private searchTests =
    testList "InMemoryVectorStore search" [
        testAsync "a search reads one tenant's scope; another tenant's corpus never reaches it" {
            let storage = ProbeStorage()

            use store =
                new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(
                    storage,
                    SilentLogger(),
                    corruptPolicy = CorruptPolicy.StartEmpty
                )

            let ivs = store :> IVectorStore
            let tenantA = freshTeam ()
            let tenantB = freshTeam ()

            for i in 1..3 do
                do! ivs.Upsert tenantA $"a{i}" [| 1.0f; float32 i * 0.01f |] chunk

            // B's chunks sit exactly on the query axis, so a scan that read
            // them would rank them first.
            for i in 1..50 do
                do! ivs.Upsert tenantB $"b{i}" [| 1.0f; 0.0f |] chunk

            let! hits = ivs.Search [ tenantA ] [| 1.0f; 0.0f |] 10

            Expect.equal
                (hits |> List.map _.ChunkId |> Set.ofList)
                (Set.ofList [ "a1"; "a2"; "a3" ])
                "only tenant A's chunks answer"

            Expect.isTrue (hits |> List.forall (fun h -> h.Scope = tenantA)) "every hit is in the searched scope"
        }
    ]

let tests = testList "ScopedSnapshotIndex" [ baseTests; storeTests; searchTests ]
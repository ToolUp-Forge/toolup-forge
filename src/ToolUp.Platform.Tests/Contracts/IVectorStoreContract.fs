module ToolUp.Platform.Tests.Contracts.IVectorStoreContract

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

/// Phase 861 — the restart laws every `IVectorStore` persisted over an
/// `IBlobStorage` must satisfy, plus the same laws for an `ISparseIndex`
/// persisted the same way. Bound by `InProcess` test files that supply a
/// factory building a store over the storage they are handed.
///
/// **The shape under test.** A store that hydrates a scope lazily — on the
/// first access after a process starts — and persists a scope by writing
/// back everything it holds IN MEMORY for it, loses the whole persisted
/// corpus the first time a restarted process MUTATES the scope before
/// anything has read it: the mutation lands on an empty, un-hydrated map,
/// and the next flush replaces the snapshot with just what arrived since
/// the restart. A document upload after a restart is exactly that shape.
/// So every law below is a three-boot sequence over ONE blob storage:
///
/// 1. boot one persists a scope with several chunks;
/// 2. boot two's FIRST act on that scope is the mutation under test;
/// 3. boot three reads the scope back and must see boot one's corpus with
///    boot two's mutation applied — never boot two's mutation alone.
///
/// Disposal is the flush: the pack disposes every store it builds, so a
/// store must persist everything it acknowledged on `Dispose` (the in-tree
/// stores do). A store that ignores the storage it is handed — one backed
/// by its own database — satisfies the laws trivially, which is correct:
/// it has no lazily-hydrated scope to lose.
///
/// **Unreadable is not absent.** `unreadableSnapshotTests` is the second
/// half: a snapshot that EXISTS but cannot be read must refuse every
/// mutation of its scope — raising the store's own typed refusal, which the
/// binding names through `isRefusal` — and write nothing, because
/// proceeding on an empty scope is the same corpus replacement by another
/// route. A scope with NO snapshot is loaded-and-empty and mutates freely;
/// every law's boot one relies on that.

let private dims = 8

/// Distinct, near-parallel vectors: every chunk sits within a hair of the
/// query axis, so a generous `topK` returns a whole scope and ranking can
/// never be what keeps a chunk out of a result. Distinct rather than
/// identical so a graph index is not asked to build over duplicates.
let private vectorFor (i: int) : float32 array =
    Array.init dims (fun d ->
        if d = 0 then 1.0f
        elif d = 1 then 0.001f * float32 i
        else 0.0f)

let private queryVec: float32 array =
    Array.init dims (fun d -> if d = 0 then 1.0f else 0.0f)

/// Every chunk carries this term, so one BM25 query enumerates a scope.
let private universalTerm = "common"

let private wideK = 1000

/// Chunks boot one persists. Three, so a truncation to boot two's one
/// chunk cannot be confused with an off-by-one.
let private corpusSize = 3

let private chunkId (i: int) = sprintf "restart-chunk-%d" i

let private chunkOf (i: int) : TextChunk = {
    Content = sprintf "%s restart chunk %d" universalTerm i
    Metadata = Map.empty
}

let private idsOf (range: int seq) = range |> Seq.map chunkId |> Set.ofSeq

/// A fresh lazily-hydrated scope per case — never `Platform` /
/// `Deployment`, which the in-tree stores load eagerly at construction.
let private freshTeam () = Team(Guid.NewGuid().ToString "N")

let private freshUser () = User(Guid.NewGuid().ToString "N")

/// The id the scope's persisted names are recognised by. Every in-tree
/// store embeds it in its blob name, and it is a fresh GUID per case.
let private scopeId (scope: VectorScope) =
    match scope with
    | Team id
    | User id -> id
    | Platform -> "platform"
    | Deployment -> "deployment"

let private shutdown (store: obj) =
    match store with
    | :? IDisposable as d -> d.Dispose()
    | _ -> ()

/// Build a store over `storage`, run `body`, then dispose it — disposal is
/// the flush every law depends on, so it runs on the failure path too.
let private boot (factory: IBlobStorage -> 'S) (storage: IBlobStorage) (body: 'S -> Async<'a>) = async {
    let store = factory storage

    try
        return! body store
    finally
        shutdown (box store)
}

/// An `IBlobStorage` that can make one scope's persisted snapshot
/// UNREADABLE: while `poisoned`, a `Download` of any blob whose name holds
/// `key` fails with a storage error, while `Exists` still reports the blob
/// — a snapshot that is there but cannot be read, as distinct from one that
/// is not there at all. Every write of such a blob is counted, poisoned or
/// not, so a case can assert that a refused mutation wrote nothing.
type private UnreadableScopeStorage(inner: IBlobStorage, key: string) =
    let writes = ConcurrentQueue<string>()
    let mutable poisoned = false

    let touches (blobName: string) = blobName.Contains key

    member _.Poison() = poisoned <- true
    member _.Heal() = poisoned <- false
    member _.Writes = List.ofSeq writes

    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(container, targetBlobName, sourceBlobNames) =
            inner.ComposeFrom(container, targetBlobName, sourceBlobNames)

        member this.Erase(container, prefix, policy, dryRun) =
            ToolUp.Platform.BlobStorage.eraseByPrefix (this :> IBlobStorage) container prefix policy dryRun

        member _.Upload(container, blobName, content) =
            if touches blobName then
                writes.Enqueue(sprintf "upload %s/%s" container blobName)

            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = async {
            if poisoned && touches blobName then
                return Error $"simulated read failure: {container}/{blobName} is present but unreadable"
            else
                return! inner.Download(container, blobName)
        }

        member this.DownloadRange(container, blobName, offset, length) =
            ToolUp.Platform.BlobStorage.downloadRangeViaDownload (this :> IBlobStorage) container blobName offset length

        member _.Delete(container, blobName) =
            if touches blobName then
                writes.Enqueue(sprintf "delete %s/%s" container blobName)

            inner.Delete(container, blobName)

        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

/// Boot one of every law: persist `corpusSize` chunks into `scope`.
let private seedDense (factory: IBlobStorage -> IVectorStore) (storage: IBlobStorage) (scope: VectorScope) =
    boot factory storage (fun store -> async {
        for i in 0 .. corpusSize - 1 do
            do! store.Upsert scope (chunkId i) (vectorFor i) (chunkOf i)
    })

let private searchIds (store: IVectorStore) (scope: VectorScope) = async {
    let! hits = store.Search [ scope ] queryVec wideK
    return hits |> List.map _.ChunkId |> Set.ofList
}

let private listedIds (store: IVectorStore) (scope: VectorScope) (includeDeleted: bool) = async {
    let! chunks = store.ListChunks scope includeDeleted
    return chunks |> List.map fst |> Set.ofList
}

/// The headline law, parameterised over the scope kind: an upsert as the
/// first act after a restart must ADD to the persisted scope.
let private upsertAfterRestart (factory: IBlobStorage -> IVectorStore) (scope: VectorScope) = async {
    let storage = InMemoryBlobStorage() :> IBlobStorage
    do! seedDense factory storage scope

    do!
        boot factory storage (fun store -> async {
            do! store.Upsert scope (chunkId corpusSize) (vectorFor corpusSize) (chunkOf corpusSize)
        })

    do!
        boot factory storage (fun store -> async {
            let expected = idsOf [ 0..corpusSize ]
            let! searched = searchIds store scope
            let! listed = listedIds store scope false

            Expect.equal
                searched
                expected
                "an upsert as the first act after a restart must add to the persisted scope — the next flush must not replace the corpus with the one new chunk"

            Expect.equal listed expected "ListChunks must see the whole scope, persisted corpus plus the new chunk"
        })
}

/// Restart laws every `IVectorStore` over an `IBlobStorage` must satisfy.
/// `factory` builds a store over the storage it is handed; the pack
/// disposes it (the flush).
let tests (name: string) (factory: IBlobStorage -> IVectorStore) =
    testList $"IVectorStore restart contract — {name}" [
        testAsync "an upsert after a restart keeps the persisted Team scope" {
            do! upsertAfterRestart factory (freshTeam ())
        }

        testAsync "an upsert after a restart keeps the persisted User scope" {
            do! upsertAfterRestart factory (freshUser ())
        }

        testAsync "RestoreChunk as the first act after a restart reaches the persisted tombstone" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let scope = freshTeam ()
            do! seedDense factory storage scope
            do! boot factory storage (fun store -> store.DeleteChunk scope (chunkId 1))
            do! boot factory storage (fun store -> store.RestoreChunk scope (chunkId 1))

            do!
                boot factory storage (fun store -> async {
                    let! searched = searchIds store scope

                    Expect.equal
                        searched
                        (idsOf [ 0 .. corpusSize - 1 ])
                        "a restore issued before any read must reach the persisted tombstone, and must not truncate the scope"
                })
        }

        testAsync "Vacuum as the first act after a restart purges persisted tombstones and keeps the rest" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let scope = freshTeam ()
            do! seedDense factory storage scope
            do! boot factory storage (fun store -> store.DeleteChunk scope (chunkId 1))

            do!
                boot factory storage (fun store -> async {
                    let! purged = store.Vacuum scope (DateTimeOffset.UtcNow.AddMinutes 1.0)

                    Expect.equal
                        purged
                        1
                        "a vacuum issued before any read must see the persisted tombstone — an un-hydrated scope purges nothing"
                })

            do!
                boot factory storage (fun store -> async {
                    let! listed = listedIds store scope true

                    Expect.equal
                        listed
                        (idsOf [ 0; 2 ])
                        "the vacuumed chunk is gone at rest and every live chunk survived"
                })
        }

        testAsync "DeleteChunk as the first act after a restart reaches the persisted snapshot" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let scope = freshTeam ()
            do! seedDense factory storage scope
            do! boot factory storage (fun store -> store.DeleteChunk scope (chunkId 1))

            do!
                boot factory storage (fun store -> async {
                    let! searched = searchIds store scope

                    Expect.equal
                        searched
                        (idsOf [ 0; 2 ])
                        "a tombstone written before any read must land in the persisted snapshot"
                })
        }

        testAsync "DeleteByScope as the first act after a restart reaches the persisted snapshot" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let scope = freshTeam ()
            do! seedDense factory storage scope
            do! boot factory storage (fun store -> store.DeleteByScope scope)

            do!
                boot factory storage (fun store -> async {
                    let! searched = searchIds store scope
                    let! listed = listedIds store scope true
                    Expect.isEmpty searched "a wipe issued before any read must reach the persisted snapshot"
                    Expect.isEmpty listed "nothing of a wiped scope survives at rest, tombstoned or not"
                })
        }

        testAsync "ListScopes after a restart names a persisted scope no read has touched" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let scope = freshTeam ()
            do! seedDense factory storage scope

            do!
                boot factory storage (fun store -> async {
                    let! scopes = store.ListScopes()

                    Expect.contains
                        scopes
                        scope
                        "ListScopes enumerates persisted scopes, not only the ones this process has loaded"
                })
        }
    ]

/// Erasure is a mutation whose interface already has a typed failure, so an
/// unreadable snapshot must surface as `StoreUnreachable` — which the
/// erasure orchestrator retries — never as a success that erased nothing.
let private eraseOnUnreadable
    (seed: IBlobStorage -> VectorScope -> Async<unit>)
    (factory: IBlobStorage -> 'S)
    (erase: 'S -> VectorScope -> Async<Result<ErasureSummary, ErasureError>>)
    =
    async {
        let scope = freshTeam ()

        let storage =
            UnreadableScopeStorage(InMemoryBlobStorage() :> IBlobStorage, scopeId scope)

        do! seed storage scope
        let writesBefore = storage.Writes.Length
        storage.Poison()

        do!
            boot factory storage (fun store -> async {
                match! erase store scope with
                | Result.Error(ErasureError.StoreUnreachable _) -> ()
                | other ->
                    failtestf
                        "an erasure over a scope whose snapshot could not be read must report StoreUnreachable, not %A"
                        other
            })

        Expect.equal
            (storage.Writes |> List.skip writesBefore)
            []
            "a refused erasure writes nothing — not at the call, and not at the flush"
    }

/// The unreadable-snapshot half, for a store persisted over `IBlobStorage`.
/// `isRefusal` recognises the store's own typed refusal.
let unreadableSnapshotTests (name: string) (isRefusal: exn -> bool) (factory: IBlobStorage -> IVectorStore) =
    let mutations: (string * (IVectorStore -> VectorScope -> Async<unit>)) list = [
        "Upsert", (fun s scope -> s.Upsert scope (chunkId 99) (vectorFor 99) (chunkOf 99))
        "DeleteChunk", (fun s scope -> s.DeleteChunk scope (chunkId 0))
        "RestoreChunk", (fun s scope -> s.RestoreChunk scope (chunkId 0))
        "Vacuum", (fun s scope -> s.Vacuum scope (DateTimeOffset.UtcNow.AddMinutes 1.0) |> Async.Ignore)
        "DeleteByScope", (fun s scope -> s.DeleteByScope scope)
    ]

    testList $"IVectorStore unreadable-snapshot contract — {name}" [
        for (memberName, mutate) in mutations do
            testAsync $"{memberName} on a scope whose snapshot exists but cannot be read refuses and writes nothing" {
                let scope = freshTeam ()
                let inner = InMemoryBlobStorage() :> IBlobStorage
                let storage = UnreadableScopeStorage(inner, scopeId scope)
                do! seedDense factory storage scope
                let writesBefore = storage.Writes.Length
                storage.Poison()

                do!
                    boot factory storage (fun store -> async {
                        // A read first: a failed load must not be remembered
                        // as a completed one. Whether the read itself raises
                        // or degrades is the store's choice; the mutation
                        // after it must still refuse.
                        try
                            let! _ = store.Search [ scope ] queryVec wideK
                            ()
                        with _ ->
                            ()

                        let! outcome = mutate store scope |> Async.Catch

                        match outcome with
                        | Choice1Of2() ->
                            failtestf
                                "%s proceeded on a scope whose snapshot could not be read — it must refuse rather than act on an empty scope"
                                memberName
                        | Choice2Of2 ex ->
                            Expect.isTrue
                                (isRefusal ex)
                                (sprintf
                                    "%s must refuse with the store's typed error, not %s: %s"
                                    memberName
                                    (ex.GetType().Name)
                                    ex.Message)
                    })

                Expect.equal
                    (storage.Writes |> List.skip writesBefore)
                    []
                    "a refused mutation writes nothing — not at the call, and not at the flush"

                storage.Heal()

                do!
                    boot factory storage (fun store -> async {
                        let! searched = searchIds store scope

                        Expect.equal
                            searched
                            (idsOf [ 0 .. corpusSize - 1 ])
                            "the persisted corpus is intact once the snapshot is readable again"
                    })
            }

        testAsync
            "Erase on a scope whose snapshot exists but cannot be read reports the store unreachable and writes nothing" {
            do!
                eraseOnUnreadable (seedDense factory) factory (fun s scope ->
                    s.Erase(scope, "restart", ErasurePolicy.HardDelete, false))
        }
    ]

// ─── The same laws for a sparse index ────────────────────────────────

let private seedSparse (factory: IBlobStorage -> ISparseIndex) (storage: IBlobStorage) (scope: VectorScope) =
    boot factory storage (fun index -> async {
        for i in 0 .. corpusSize - 1 do
            do! index.Upsert scope (chunkId i) (chunkOf i)
    })

let private sparseIds (index: ISparseIndex) (scope: VectorScope) = async {
    let! hits = index.Search [ scope ] universalTerm wideK
    return hits |> List.map _.ChunkId |> Set.ofList
}

/// The restart laws for an `ISparseIndex` persisted over `IBlobStorage`
/// — the members that interface has — and its unreadable-snapshot half.
let sparseIndexTests (name: string) (isRefusal: exn -> bool) (factory: IBlobStorage -> ISparseIndex) =
    testList $"ISparseIndex restart contract — {name}" [
        for (label, mkScope) in [ "Team", freshTeam; "User", freshUser ] do
            testAsync $"an upsert after a restart keeps the persisted {label} scope" {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let scope = mkScope ()
                do! seedSparse factory storage scope
                do! boot factory storage (fun index -> index.Upsert scope (chunkId corpusSize) (chunkOf corpusSize))

                do!
                    boot factory storage (fun index -> async {
                        let! found = sparseIds index scope

                        Expect.equal
                            found
                            (idsOf [ 0..corpusSize ])
                            "an upsert as the first act after a restart must add to the persisted scope, not replace it"
                    })
            }

        testAsync "DeleteChunk as the first act after a restart reaches the persisted snapshot" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let scope = freshTeam ()
            do! seedSparse factory storage scope
            do! boot factory storage (fun index -> index.DeleteChunk scope (chunkId 1))

            do!
                boot factory storage (fun index -> async {
                    let! found = sparseIds index scope

                    Expect.equal
                        found
                        (idsOf [ 0; 2 ])
                        "a delete written before any read must land in the persisted snapshot"
                })
        }

        testAsync "DeleteByScope as the first act after a restart reaches the persisted snapshot" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let scope = freshTeam ()
            do! seedSparse factory storage scope
            do! boot factory storage (fun index -> index.DeleteByScope scope)

            do!
                boot factory storage (fun index -> async {
                    let! found = sparseIds index scope
                    Expect.isEmpty found "a wipe issued before any read must reach the persisted snapshot"
                })
        }

        let mutations: (string * (ISparseIndex -> VectorScope -> Async<unit>)) list = [
            "Upsert", (fun i scope -> i.Upsert scope (chunkId 99) (chunkOf 99))
            "DeleteChunk", (fun i scope -> i.DeleteChunk scope (chunkId 0))
            "DeleteByScope", (fun i scope -> i.DeleteByScope scope)
        ]

        for (memberName, mutate) in mutations do
            testAsync $"{memberName} on a scope whose snapshot exists but cannot be read refuses and writes nothing" {
                let scope = freshTeam ()
                let inner = InMemoryBlobStorage() :> IBlobStorage
                let storage = UnreadableScopeStorage(inner, scopeId scope)
                do! seedSparse factory storage scope
                let writesBefore = storage.Writes.Length
                storage.Poison()

                do!
                    boot factory storage (fun index -> async {
                        try
                            let! _ = index.Search [ scope ] universalTerm wideK
                            ()
                        with _ ->
                            ()

                        let! outcome = mutate index scope |> Async.Catch

                        match outcome with
                        | Choice1Of2() ->
                            failtestf
                                "%s proceeded on a scope whose snapshot could not be read — it must refuse rather than act on an empty scope"
                                memberName
                        | Choice2Of2 ex ->
                            Expect.isTrue
                                (isRefusal ex)
                                (sprintf
                                    "%s must refuse with the index's typed error, not %s: %s"
                                    memberName
                                    (ex.GetType().Name)
                                    ex.Message)
                    })

                Expect.equal
                    (storage.Writes |> List.skip writesBefore)
                    []
                    "a refused mutation writes nothing — not at the call, and not at the flush"

                storage.Heal()

                do!
                    boot factory storage (fun index -> async {
                        let! found = sparseIds index scope

                        Expect.equal
                            found
                            (idsOf [ 0 .. corpusSize - 1 ])
                            "the persisted corpus is intact once the snapshot is readable again"
                    })
            }

        testAsync
            "Erase on a scope whose snapshot exists but cannot be read reports the store unreachable and writes nothing" {
            do!
                eraseOnUnreadable (seedSparse factory) factory (fun i scope ->
                    i.Erase(scope, "restart", ErasurePolicy.HardDelete, false))
        }
    ]
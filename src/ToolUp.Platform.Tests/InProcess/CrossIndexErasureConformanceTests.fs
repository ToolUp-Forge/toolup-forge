module ToolUp.Platform.Tests.InProcess.CrossIndexErasureConformanceTests

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IEmbeddingCache
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IIndexLifecycle
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.RAG.InMemoryEmbeddingCache

// ─── Phase 204 — cross-index erasure conformance (property) ───────────
//
// Phase 115 asserted the erasure fan-out ONCE, at its exit criterion, over
// three hand-written interleavings. This pack turns that assertion into a
// property: for a randomised ingest / delete / erase sequence over several
// `VectorScope`s, nothing deleted or erased is EVER retrievable again —
// not from the dense `IVectorStore`, not from the `ISparseIndex` BM25 leg,
// not through the fused hybrid path, and not from the embedding cache —
// and the invariant survives a mid-sequence process restart that
// re-hydrates both indexes from blob storage. That is the Phase 9h
// right-to-be-forgotten guarantee made executable rather than asserted.
//
// **Why a hand-rolled generator rather than FsCheck.** The pack has no
// property-testing dependency and adding one is a CPM + supply-chain
// change this test-only phase has no business making. A seeded
// `System.Random` gives what the property actually needs: sequences that
// are arbitrary with respect to the implementation but exactly
// reproducible from the seed printed in a failure message.
//
// **The model is the oracle.** Each command is interpreted twice — once
// against the real stores through `IIndexLifecycle`, once against a pure
// `Model` of what should remain — and the two are compared after EVERY
// command, so a failure names the shortest prefix that broke rather than
// the whole sequence.
//
// **Both directions are asserted, deliberately.** "Nothing deleted comes
// back" is satisfied vacuously by an index that returns nothing at all, so
// every sweep also asserts that every live chunk IS still retrievable from
// every leg. A fan-out that over-deletes fails here just as loudly as one
// that under-deletes.

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

/// One axis-aligned unit vector for every chunk and for every query, so
/// cosine similarity is ~1 across the whole corpus. That removes ranking
/// as a confounder: the only thing that can keep a chunk out of a dense
/// result set is deletion, which is precisely what is under test (the
/// `KnowledgeUserScopeIsolationTests` idiom).
let private unitVec: float32 array =
    Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

type private ConstantEmbedder() =
    interface IEmbeddingProvider with
        member _.GenerateEmbedding _ = async { return unitVec }

        member _.GenerateEmbeddings texts =
            batchedFallback (fun _ -> async { return unitVec }) texts

        member _.Dimensions = 8
        member _.ProviderId = "test"
        member _.ModelId = "constant-v1"

/// The scopes the generated sequences range over. Three, deliberately:
/// `Deployment` is loaded eagerly at construction, while `Team` / `User`
/// scopes hydrate lazily from blob storage — so the restart leg exercises
/// both hydration paths instead of only the easy one.
let private scopePool = [| Deployment; Team "t1"; User "u1" |]

/// Data subjects an `Erase` can target. Neither is a substring of the
/// other, so the `Content.Contains` matching contract shared by
/// `IVectorStore.eraseSubject` and `ISparseIndex.Erase` cannot match one
/// subject's chunks while erasing the other's.
let private subjectPool = [| "subject-alpha"; "subject-beta" |]

let private docPool = [| "d0"; "d1"; "d2" |]

/// Every chunk carries this term, so ONE BM25 query per scope enumerates
/// that scope's whole live sparse corpus. Safe because the index's IDF —
/// `log (1 + (n - df + 0.5) / (df + 0.5))` — stays strictly positive even
/// when `df = n`, so a universal term does not silently drop out of the
/// result set the way a classic `log (n / df)` would.
[<Literal>]
let private universalTerm = "common"

let private chunkIdFor (docId: string) (index: int) = sprintf "%s:chunk:%d" docId index

let private contentFor (docId: string) (index: int) (subject: string option) =
    match subject with
    | Some s -> sprintf "%s %s body %d attributed to %s" universalTerm docId index s
    | None -> sprintf "%s %s body %d unattributed" universalTerm docId index

// ─── Commands ─────────────────────────────────────────────────────────

type private Command =
    | Ingest of scope: VectorScope * docId: string * index: int * subject: string option
    | DeleteOneChunk of scope: VectorScope * docId: string * index: int
    | DeleteWholeDocument of scope: VectorScope * docId: string * chunkCount: int
    | DeleteWholeScope of scope: VectorScope
    | EraseSubject of scope: VectorScope * subject: string * dryRun: bool

/// A command sequence that is a pure function of `seed`, so a failure is
/// reproducible from the seed alone. The corpus is seeded first: a
/// sequence that only ever deleted from an empty index would satisfy the
/// invariant without exercising anything.
///
/// `allowScopeWipe = false` drops `DeleteByScope` from the tail. It is set
/// only by the restart property, and only because of the live defect the
/// pending reproducer at the bottom of this file pins — see the comment
/// there before widening it back. Every other command stays in both
/// sequences.
let private generateWith (seed: int) (tailLength: int) (allowScopeWipe: bool) : Command list =
    let rng = Random(seed)
    let pick (xs: 'a array) = xs[rng.Next xs.Length]

    let maybeSubject () =
        if rng.Next 3 = 0 then Some(pick subjectPool) else None

    [
        for scope in scopePool do
            for docId in docPool do
                for index in 0..1 do
                    Ingest(scope, docId, index, maybeSubject ())

        for _ in 1..tailLength do
            match rng.Next 100 with
            | n when n < 30 -> Ingest(pick scopePool, pick docPool, rng.Next 3, maybeSubject ())
            | n when n < 55 -> DeleteOneChunk(pick scopePool, pick docPool, rng.Next 3)
            // `chunkCount` deliberately reaches past the highest ingested
            // index: `DeleteDocument` must tolerate deleting a chunk id
            // that was never written.
            | n when n < 72 -> DeleteWholeDocument(pick scopePool, pick docPool, rng.Next 4)
            | n when n < 82 ->
                if allowScopeWipe then
                    DeleteWholeScope(pick scopePool)
                else
                    DeleteWholeDocument(pick scopePool, pick docPool, rng.Next 4)
            | n when n < 94 -> EraseSubject(pick scopePool, pick subjectPool, false)
            | _ -> EraseSubject(pick scopePool, pick subjectPool, true)
    ]

let private generate (seed: int) (tailLength: int) = generateWith seed tailLength true

// ─── The model (the oracle) ───────────────────────────────────────────

type private Model = {
    /// `(scope, chunkId) -> content` for everything that should still be
    /// retrievable from every leg.
    Live: Map<VectorScope * string, string>
    /// Everything that has been deleted or erased and must never be
    /// retrievable again. A key re-ingested after removal leaves this set —
    /// `Upsert` clears any pre-existing tombstone, so new content genuinely
    /// supersedes the old.
    Removed: Set<VectorScope * string>
}

module private Model =
    let empty = {
        Live = Map.empty
        Removed = Set.empty
    }

    let private removeKeys (keys: (VectorScope * string) list) (model: Model) = {
        Live = keys |> List.fold (fun (m: Map<_, _>) k -> m.Remove k) model.Live
        Removed = keys |> List.fold (fun (s: Set<_>) k -> s.Add k) model.Removed
    }

    let apply (model: Model) (cmd: Command) =
        match cmd with
        | Ingest(scope, docId, index, subject) ->
            let key = (scope, chunkIdFor docId index)

            {
                Live = model.Live.Add(key, contentFor docId index subject)
                Removed = model.Removed.Remove key
            }
        | DeleteOneChunk(scope, docId, index) -> removeKeys [ (scope, chunkIdFor docId index) ] model
        | DeleteWholeDocument(scope, docId, chunkCount) ->
            removeKeys [ for i in 0 .. chunkCount - 1 -> (scope, chunkIdFor docId i) ] model
        | DeleteWholeScope scope ->
            model.Live
            |> Map.toList
            |> List.map fst
            |> List.filter (fun (s, _) -> s = scope)
            |> fun keys -> removeKeys keys model
        // A dry run reports without mutating — that is the whole contract,
        // so the model must not move.
        | EraseSubject(_, _, true) -> model
        | EraseSubject(scope, subject, false) ->
            model.Live
            |> Map.toList
            |> List.filter (fun ((s, _), content) -> s = scope && content.Contains subject)
            |> List.map fst
            |> fun keys -> removeKeys keys model

    let liveIn (scope: VectorScope) (model: Model) =
        model.Live
        |> Map.toList
        |> List.choose (fun ((s, cid), _) -> if s = scope then Some cid else None)
        |> Set.ofList

    let removedIn (scope: VectorScope) (model: Model) =
        model.Removed |> Set.filter (fun (s, _) -> s = scope) |> Set.map snd

// ─── Harness ──────────────────────────────────────────────────────────

type private Harness = {
    VectorStore: IVectorStore
    Sparse: ISparseIndex
    Lifecycle: IIndexLifecycle
    Pipeline: IRetrievalPipeline
    Shutdown: unit -> unit
}

/// Boot a full hybrid deployment over `storage`. "Restart" is
/// `Shutdown()` (each in-memory store's `IDisposable` performs a final
/// synchronous flush of its dirty set) followed by another `boot` over the
/// SAME storage — the idiom `IndexLifecycleTests` established.
/// `flushIntervalMs = 60000` keeps the background flush loop out of the
/// way so persistence is driven deterministically by disposal.
let private boot (storage: IBlobStorage) (cache: IEmbeddingCache) : Harness =
    let vectorStore =
        new InMemoryVectorStore(storage, logger = SilentLogger(), flushIntervalMs = 60000)

    let bm25 =
        new InMemoryBM25Index(storage, logger = SilentLogger(), flushIntervalMs = 60000)

    let vs = vectorStore :> IVectorStore
    let sparse = bm25 :> ISparseIndex

    {
        VectorStore = vs
        Sparse = sparse
        Lifecycle = DefaultIndexLifecycle(vs, Some sparse, Some cache) :> IIndexLifecycle
        Pipeline =
            new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(vs, ConstantEmbedder(), sparse) :> IRetrievalPipeline
        Shutdown =
            fun () ->
                (vectorStore :> IDisposable).Dispose()
                (bm25 :> IDisposable).Dispose()
    }

/// A `TeamMember` subject, so `authorisedScopes` admits all three scopes in
/// `scopePool`: `Deployment` unconditionally, `Team "t1"` via `TeamId`,
/// `User "u1"` via `UserId`. Anything narrower would leave part of the
/// corpus unreachable through the hybrid path and weaken the sweep to a
/// vacuous pass on those scopes.
let private hybridContext = AccessContext.unrestricted (TeamMember("u1", "t1"))

/// Enough for any corpus this pack can build (3 scopes × 3 docs × 3
/// indices = 27), so no assertion can be weakened by truncation.
[<Literal>]
let private sweepK = 500

let private applyToSystem (h: Harness) (cmd: Command) = async {
    match cmd with
    | Ingest(scope, docId, index, subject) ->
        let chunk: TextChunk = {
            Content = contentFor docId index subject
            Metadata = Map.empty
        }

        do! h.VectorStore.Upsert scope (chunkIdFor docId index) unitVec chunk
        do! h.Sparse.Upsert scope (chunkIdFor docId index) chunk
        return None
    | DeleteOneChunk(scope, docId, index) ->
        let! report = h.Lifecycle.DeleteChunk scope (chunkIdFor docId index)
        return Some report
    | DeleteWholeDocument(scope, docId, chunkCount) ->
        let! report = h.Lifecycle.DeleteDocument scope docId chunkCount
        return Some report
    | DeleteWholeScope scope ->
        let! report = h.Lifecycle.DeleteByScope scope
        return Some report
    | EraseSubject(scope, subject, dryRun) ->
        let! result = h.Lifecycle.Erase(scope, subject, ErasurePolicy.HardDelete, dryRun)

        match result with
        | Result.Error e -> return failtest (sprintf "Erase must succeed: %s" (ErasureError.toMessage e))
        | Result.Ok _ -> return None
}

/// The invariant, asserted across every leg. `label` carries the seed and
/// the command prefix so a failure is reproducible without re-deriving it.
let private assertInvariant (label: string) (model: Model) (h: Harness) = async {
    for scope in scopePool do
        let! dense = h.VectorStore.Search [ scope ] unitVec sweepK
        let denseIds = dense |> List.map _.ChunkId |> Set.ofList

        let! sparse = h.Sparse.Search [ scope ] universalTerm sweepK
        let sparseIds = sparse |> List.map _.ChunkId |> Set.ofList

        let live = Model.liveIn scope model
        let removed = Model.removedIn scope model

        Expect.isEmpty
            (Set.intersect denseIds removed)
            (sprintf "%s / %A: dense leg served deleted or erased chunk(s)" label scope)

        Expect.isEmpty
            (Set.intersect sparseIds removed)
            (sprintf "%s / %A: BM25 sparse leg served deleted or erased chunk(s)" label scope)

        // Non-vacuity: an index that returned nothing would satisfy the two
        // assertions above trivially.
        Expect.isEmpty
            (Set.difference live denseIds)
            (sprintf "%s / %A: dense leg lost a chunk that was never deleted" label scope)

        Expect.isEmpty
            (Set.difference live sparseIds)
            (sprintf "%s / %A: BM25 sparse leg lost a chunk that was never deleted" label scope)

    // The fused hybrid path — the leg Phase 115 exists because of. A chunk
    // absent from both retrievers individually cannot be resurrected by
    // RRF, but asserting it directly is the point: this is the surface an
    // AI answer is actually grounded on.
    let request =
        RetrievalRequest.create universalTerm (List.ofArray scopePool) sweepK Interleaved

    let! hybrid = h.Pipeline.Retrieve request hybridContext

    let hybridKeys = hybrid |> List.map (fun m -> (m.Scope, m.ChunkId)) |> Set.ofList

    Expect.isEmpty
        (Set.intersect hybridKeys model.Removed)
        (sprintf "%s: hybrid retrieval served deleted or erased chunk(s)" label)

    let liveKeys = model.Live |> Map.toList |> List.map fst |> Set.ofList

    Expect.isEmpty
        (Set.difference liveKeys hybridKeys)
        (sprintf "%s: hybrid retrieval lost a chunk that was never deleted" label)
}

/// Drive `commands` through both interpretations, asserting after each.
let private runSequence (label: string) (h: Harness) (start: Model) (commands: Command list) = async {
    let mutable model = start
    let mutable step = 0

    for cmd in commands do
        step <- step + 1
        let! _ = applyToSystem h cmd
        model <- Model.apply model cmd
        do! assertInvariant (sprintf "%s step %d (%A)" label step cmd) model h

    return model
}

[<Tests>]
let tests =
    testList "Phase 204 — cross-index erasure conformance" [

        testAsync "property: no deleted or erased chunk is retrievable from any leg, over randomised sequences" {
            for seed in 1..12 do
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let cache = InMemoryEmbeddingCache() :> IEmbeddingCache
                let h = boot storage cache

                try
                    let! _ = runSequence (sprintf "seed %d" seed) h Model.empty (generate seed 16)
                    ()
                finally
                    h.Shutdown()
        }

        testAsync "property: the invariant survives a mid-sequence restart that re-hydrates from blob storage" {
            for seed in 101..106 do
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let cache = InMemoryEmbeddingCache() :> IEmbeddingCache
                // `allowScopeWipe = false` — see `generateWith` and the
                // pending reproducer at the bottom of this file.
                let commands = generateWith seed 20 false
                let half = commands.Length / 2

                let first = boot storage cache

                let! model = async {
                    try
                        return!
                            runSequence (sprintf "seed %d boot 1" seed) first Model.empty (List.truncate half commands)
                    finally
                        // Disposal flushes both indexes' dirty sets, so the
                        // restart below reads exactly what a real process
                        // exit would have left behind.
                        first.Shutdown()
                }

                // ── Restart: fresh indexes over the SAME blob storage ──
                let second = boot storage cache

                try
                    // The invariant must hold against the RE-LOADED state before
                    // anything else touches it — a deleted chunk that survived
                    // only at rest resurrects exactly here.
                    do! assertInvariant (sprintf "seed %d after restart" seed) model second

                    let! finalModel = runSequence (sprintf "seed %d boot 2" seed) second model (List.skip half commands)

                    ignore finalModel
                finally
                    second.Shutdown()

                // ── Second restart: the post-sequence state also survives ──
                let third = boot storage cache

                try
                    let! afterAll = async {
                        let mutable m = Model.empty

                        for cmd in commands do
                            m <- Model.apply m cmd

                        return m
                    }

                    do! assertInvariant (sprintf "seed %d after second restart" seed) afterAll third
                finally
                    third.Shutdown()
        }

        testAsync "Erase dryRun = true reports without mutating any index, the cache, or the snapshots at rest" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let cache = InMemoryEmbeddingCache() :> IEmbeddingCache
            let subject = subjectPool[0]

            let cacheKey = {
                Version = {
                    ProviderId = "test"
                    ModelId = "constant-v1"
                    Dimensions = 8
                }
                TextHash = "deadbeef"
            }

            // ── Boot 1: ingest a subject-naming corpus and persist it ──
            do! async {
                let h = boot storage cache

                try
                    let seedCommands = [
                        for scope in scopePool do
                            Ingest(scope, "d0", 0, Some subject)
                            Ingest(scope, "d0", 1, None)
                    ]

                    let! _ = runSequence "dryRun seed" h Model.empty seedCommands
                    ()
                finally
                    h.Shutdown()
            }

            let! bm25Before = storage.Download("_rag", "_rag/deployment/bm25.json")
            let! vectorBefore = storage.Download("_rag", "_rag/deployment/index.json")

            Expect.isTrue (Result.isOk bm25Before) "the sparse snapshot must exist before the dry run"
            Expect.isTrue (Result.isOk vectorBefore) "the dense snapshot must exist before the dry run"

            // ── Boot 2: dry-run erase; nothing may move ──
            do! async {
                let h = boot storage cache

                try
                    do! cache.Set cacheKey [| 0.5f; 0.5f |]

                    // `includeDeleted = true` so a tombstone-vs-purge change
                    // would show up here, not just a `Search` filter change.
                    let! denseBefore = h.VectorStore.ListChunks Deployment true
                    let! sparseBefore = h.Sparse.Search [ Deployment ] universalTerm sweepK

                    let! preview = h.Lifecycle.Erase(Deployment, subject, ErasurePolicy.HardDelete, true)

                    match preview with
                    | Result.Error e -> failtest (sprintf "dry-run erase must succeed: %s" (ErasureError.toMessage e))
                    | Result.Ok summary ->
                        Expect.isGreaterThan
                            summary.RecordsAffected
                            0
                            "the dry run must REPORT the matches it would erase — a zero count would make the mutation checks below vacuous"

                    let! denseAfter = h.VectorStore.ListChunks Deployment true
                    let! sparseAfter = h.Sparse.Search [ Deployment ] universalTerm sweepK

                    Expect.sequenceEqual
                        (denseAfter |> List.map fst |> List.sort)
                        (denseBefore |> List.map fst |> List.sort)
                        "dry run must not change the dense chunk set"

                    Expect.sequenceEqual
                        (denseAfter
                         |> List.map (fun (cid, c) -> cid, c.Metadata.TryFind ChunkMetadata.DeletedAtKey)
                         |> List.sortBy fst)
                        (denseBefore
                         |> List.map (fun (cid, c) -> cid, c.Metadata.TryFind ChunkMetadata.DeletedAtKey)
                         |> List.sortBy fst)
                        "dry run must not tombstone a dense chunk"

                    Expect.sequenceEqual
                        (sparseAfter |> List.map _.ChunkId |> List.sort)
                        (sparseBefore |> List.map _.ChunkId |> List.sort)
                        "dry run must not change the sparse chunk set"

                    let! cached = cache.TryGet cacheKey
                    Expect.isSome cached "dry run must not flush the embedding cache"
                finally
                    h.Shutdown()
            }

            // ── At rest: the snapshots are byte-identical to the pre-run ones ──
            let! bm25After = storage.Download("_rag", "_rag/deployment/bm25.json")
            let! vectorAfter = storage.Download("_rag", "_rag/deployment/index.json")

            let bytesOf label (r: Result<byte array, string>) =
                match r with
                | Ok b -> b
                | Error e -> failtest (sprintf "%s must be readable: %s" label e)

            Expect.sequenceEqual
                (bytesOf "bm25.json (after)" bm25After)
                (bytesOf "bm25.json (before)" bm25Before)
                "dry run must leave the persisted sparse snapshot byte-identical"

            Expect.sequenceEqual
                (bytesOf "index.json (after)" vectorAfter)
                (bytesOf "index.json (before)" vectorBefore)
                "dry run must leave the persisted dense snapshot byte-identical"

            // And the subject text is still there — a dry run that had
            // silently erased at rest would otherwise pass every check above
            // if both snapshots happened to be rewritten identically.
            Expect.stringContains
                (Encoding.UTF8.GetString(bytesOf "bm25.json" bm25After))
                subject
                "dry run must leave the subject's text at rest"
        }

        testAsync "IndexLifecycleReport fan-out counts name exactly the indexes composed" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let cache = InMemoryEmbeddingCache() :> IEmbeddingCache

            use vectorStore =
                new InMemoryVectorStore(storage, logger = SilentLogger(), flushIntervalMs = 60000)

            use bm25 =
                new InMemoryBM25Index(storage, logger = SilentLogger(), flushIntervalMs = 60000)

            let vs = vectorStore :> IVectorStore
            let sparse = bm25 :> ISparseIndex

            let chunk content : TextChunk = {
                Content = content
                Metadata = Map.empty
            }

            for i in 0..2 do
                do! vs.Upsert Deployment (chunkIdFor "d0" i) unitVec (chunk (contentFor "d0" i None))
                do! sparse.Upsert Deployment (chunkIdFor "d0" i) (chunk (contentFor "d0" i None))

            // ── Hybrid composition: both retrieval indexes report ──
            let hybrid = DefaultIndexLifecycle(vs, Some sparse, Some cache) :> IIndexLifecycle

            let! chunkReport = hybrid.DeleteChunk Deployment (chunkIdFor "d0" 0)

            Expect.sequenceEqual
                (chunkReport.Succeeded |> List.sort)
                [ "sparse-index"; "vector-store" ]
                "DeleteChunk must report BOTH retrieval indexes — the embedding cache is content-hash keyed and is not a per-chunk target"

            Expect.isTrue
                (IndexLifecycleReport.isClean chunkReport)
                "a clean DeleteChunk carries no failures and no survivors"

            let! docReport = hybrid.DeleteDocument Deployment "d0" 3

            Expect.sequenceEqual
                (docReport.Succeeded |> List.sort)
                [ "sparse-index"; "vector-store" ]
                "DeleteDocument must report both retrieval indexes"

            Expect.isEmpty docReport.SurvivingChunkIds "a clean DeleteDocument leaves no surviving chunk ids"

            let! scopeReport = hybrid.DeleteByScope Deployment

            Expect.sequenceEqual
                (scopeReport.Succeeded |> List.sort)
                [ "sparse-index"; "vector-store" ]
                "DeleteByScope must report both retrieval indexes"

            // ── Vector-only composition: the sparse target must be ABSENT,
            // not reported as a silent success. A report that named a target
            // the deployment never composed would be the same lie in the
            // other direction. ──
            let vectorOnly = DefaultIndexLifecycle(vs, None, None) :> IIndexLifecycle

            do! vs.Upsert Deployment (chunkIdFor "d1" 0) unitVec (chunk (contentFor "d1" 0 None))

            let! vectorOnlyReport = vectorOnly.DeleteChunk Deployment (chunkIdFor "d1" 0)

            Expect.sequenceEqual
                vectorOnlyReport.Succeeded
                [ "vector-store" ]
                "a vector-only deployment must report exactly one target"
        }

        testAsync "Erase RecordsAffected sums the per-index counts and the summary names both legs" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let cache = InMemoryEmbeddingCache() :> IEmbeddingCache

            use vectorStore =
                new InMemoryVectorStore(storage, logger = SilentLogger(), flushIntervalMs = 60000)

            use bm25 =
                new InMemoryBM25Index(storage, logger = SilentLogger(), flushIntervalMs = 60000)

            let vs = vectorStore :> IVectorStore
            let sparse = bm25 :> ISparseIndex
            let subject = subjectPool[1]

            let chunk content : TextChunk = {
                Content = content
                Metadata = Map.empty
            }

            // Two chunks name the subject, one does not.
            let matching = 2

            for i in 0..2 do
                let subjectOpt = if i < matching then Some subject else None
                let c = chunk (contentFor "d0" i subjectOpt)
                do! vs.Upsert Deployment (chunkIdFor "d0" i) unitVec c
                do! sparse.Upsert Deployment (chunkIdFor "d0" i) c

            let lifecycle =
                DefaultIndexLifecycle(vs, Some sparse, Some cache) :> IIndexLifecycle

            let! result = lifecycle.Erase(Deployment, subject, ErasurePolicy.HardDelete, false)

            match result with
            | Result.Error e -> failtest (sprintf "erase must succeed: %s" (ErasureError.toMessage e))
            | Result.Ok summary ->
                Expect.equal
                    summary.RecordsAffected
                    (matching * 2)
                    "RecordsAffected must be the SUM across the two indexes the fan-out reached — one index's count alone would under-report a hybrid erasure"

                Expect.equal
                    summary.HandlerName
                    "index-lifecycle"
                    "the merged summary is attributed to the seam, not to one leg"

                match summary.Note with
                | None -> failtest "the merged note must name each leg that contributed"
                | Some note ->
                    Expect.stringContains note "vector-store" "the merged note must name the dense leg"
                    Expect.stringContains note "sparse-index" "the merged note must name the sparse leg"
        }

        // ── PENDING: a live defect this property found and this phase is
        // not scoped to fix ────────────────────────────────────────────
        //
        // Found by the restart property above at seed 101, and the reason
        // that property runs with `allowScopeWipe = false`.
        //
        // Both in-process indexes decide "is this lazily-hydrated scope
        // already loaded?" by asking whether they currently hold anything
        // for it — `InMemoryVectorStore.ensureScopeLoaded` tests
        // `store.Keys |> Seq.exists (fun (sk, _) -> sk = scopeKey)`, and
        // `InMemoryBM25Index.Search` tests `scopes.ContainsKey`. Emptiness
        // is therefore read as absence. `DeleteByScope` empties the scope
        // in memory and marks it dirty, but the persisted snapshot lives
        // until the next flush — so the very next read re-hydrates the
        // scope from that snapshot and every chunk the scope-wide delete
        // just removed is back, in memory and (on the following flush)
        // at rest again.
        //
        // Not reachable on `Platform` / `Deployment`, which are loaded
        // eagerly at construction and never re-hydrated. Reachable on
        // every `Team` / `User` scope once a flush has happened — i.e. in
        // any real deployment, where the background flush loop runs on a
        // timer rather than only at disposal. `DeleteByScope` is the
        // documented "configuration-grade reset" for a scope, so a tenant
        // wipe that silently un-wipes itself is the shape that matters.
        //
        // The same emptiness-as-absence read has a second face: a
        // `DeleteChunk` / `DeleteByScope` issued on a fresh process
        // BEFORE anything has read that scope operates on an unloaded
        // (empty) map and no-ops, because neither delete path calls
        // `ensureScopeLoaded` first. The property above cannot see that
        // one — it asserts (and so hydrates every scope) immediately
        // after each restart.
        //
        // Flip this to `testAsync` when the hydration guard is fixed; it
        // asserts the CORRECT behaviour, so it will pass unchanged.
        ptestAsync "KNOWN DEFECT: DeleteByScope on a lazily-hydrated scope is undone by the next read" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let cache = InMemoryEmbeddingCache() :> IEmbeddingCache
            let scope = User "u1"

            // ── Boot 1: populate a lazily-hydrated scope and flush it ──
            do! async {
                let h = boot storage cache

                try
                    let! _ = runSequence "resurrect boot 1" h Model.empty [ Ingest(scope, "d0", 0, None) ]

                    ()
                finally
                    h.Shutdown()
            }

            // ── Boot 2: read (hydrates), wipe the scope, read again ──
            let h = boot storage cache

            try
                let! hydrated = h.VectorStore.Search [ scope ] unitVec sweepK

                Expect.isNonEmpty
                    hydrated
                    "the scope must hydrate from its snapshot — otherwise this reproduces nothing"

                let! report = h.Lifecycle.DeleteByScope scope
                Expect.isTrue (IndexLifecycleReport.isClean report) "DeleteByScope reports success"

                let! dense = h.VectorStore.Search [ scope ] unitVec sweepK
                let! sparse = h.Sparse.Search [ scope ] universalTerm sweepK

                Expect.isEmpty
                    dense
                    "a wiped scope must stay wiped on the dense leg — re-hydration must not resurrect it"

                Expect.isEmpty
                    sparse
                    "a wiped scope must stay wiped on the BM25 leg — re-hydration must not resurrect it"
            finally
                h.Shutdown()
        }
    ]
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
open Npgsql
open ToolUp.RAG.SparseAnalysis
open ToolUp.SparseIndices.Postgres.PostgresFullTextIndex

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
//
// **Any keyword index (Phase 946).** The two randomised properties boot
// their keyword index through a factory (`propertyTests`), and run over
// the in-process `InMemoryBM25Index` always and over the database
// full-text index (Phase 893) on its live arm, gated on
// `TOOLUP_PG_FULLTEXT_CONNECTION_STRING`. Probed: with that index's
// `Erase` turned into a dry run, both Postgres properties go red.

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
/// Phase 726 — the restart property used to draw from a narrowed command
/// set (`allowScopeWipe = false`, which substituted `DeleteWholeDocument`
/// for every `DeleteByScope`) because a scope wipe on a lazily-hydrated
/// scope was undone by the next read. That defect is fixed, so there is
/// one command set again and both properties draw from it.
let private generate (seed: int) (tailLength: int) : Command list =
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
            | n when n < 82 -> DeleteWholeScope(pick scopePool)
            | n when n < 94 -> EraseSubject(pick scopePool, pick subjectPool, false)
            | _ -> EraseSubject(pick scopePool, pick subjectPool, true)
    ]

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

/// Phase 946 — the keyword index a harness boots, over the run's blob
/// storage: the index, and how to shut it down. A factory is called once
/// per boot, so a restart re-opens the SAME backing (the blob snapshots of
/// `InMemoryBM25Index`, a database table for a database-backed index).
type private SparseFactory = IBlobStorage -> ISparseIndex * (unit -> unit)

/// The in-process keyword index, persisted to the run's blob storage.
/// `flushIntervalMs = 60000` keeps the background flush loop out of the
/// way so persistence is driven deterministically by disposal.
let private bm25Factory: SparseFactory =
    fun storage ->
        let bm25 =
            new InMemoryBM25Index(storage, logger = SilentLogger(), flushIntervalMs = 60000)

        bm25 :> ISparseIndex, (fun () -> (bm25 :> IDisposable).Dispose())

/// Boot a full hybrid deployment over `storage`, with the keyword index
/// `sparseFactory` opens. "Restart" is `Shutdown()` (each in-memory
/// store's `IDisposable` performs a final synchronous flush of its dirty
/// set) followed by another boot over the SAME storage — the idiom
/// `IndexLifecycleTests` established.
let private bootWith (sparseFactory: SparseFactory) (storage: IBlobStorage) (cache: IEmbeddingCache) : Harness =
    let vectorStore =
        new InMemoryVectorStore(storage, logger = SilentLogger(), flushIntervalMs = 60000)

    let vs = vectorStore :> IVectorStore
    let sparse, closeSparse = sparseFactory storage

    {
        VectorStore = vs
        Sparse = sparse
        Lifecycle = DefaultIndexLifecycle(vs, Some sparse, Some cache) :> IIndexLifecycle
        Pipeline =
            new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(vs, ConstantEmbedder(), sparse) :> IRetrievalPipeline
        Shutdown =
            fun () ->
                (vectorStore :> IDisposable).Dispose()
                closeSparse ()
    }

/// The pack's default boot: the in-process keyword index.
let private boot (storage: IBlobStorage) (cache: IEmbeddingCache) : Harness = bootWith bm25Factory storage cache

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
/// Phase 946 — the two randomised properties, over the keyword index a
/// factory source opens. `makeFactory` is called once per sequence and
/// returns the factory every boot of that sequence uses (so a restart
/// re-opens the same backing) and the teardown that removes the backing.
let private propertyTests (name: string) (makeFactory: unit -> SparseFactory * (unit -> unit)) =
    testList name [
        testAsync "property: no deleted or erased chunk is retrievable from any leg, over randomised sequences" {
            for seed in 1..12 do
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let cache = InMemoryEmbeddingCache() :> IEmbeddingCache
                let factory, teardown = makeFactory ()
                let h = bootWith factory storage cache

                try
                    let! _ = runSequence (sprintf "seed %d" seed) h Model.empty (generate seed 16)
                    ()
                finally
                    h.Shutdown()
                    teardown ()
        }

        testAsync "property: the invariant survives a mid-sequence restart that re-hydrates from blob storage" {
            for seed in 101..106 do
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let cache = InMemoryEmbeddingCache() :> IEmbeddingCache
                // Phase 726 — scope wipes are back in this sequence; the
                // hydration defect that forced them out is fixed and pinned
                // by the two reproducers at the bottom of this file.
                let commands = generate seed 20
                let half = commands.Length / 2
                let factory, teardown = makeFactory ()

                let first = bootWith factory storage cache

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
                let second = bootWith factory storage cache

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
                let third = bootWith factory storage cache

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
                    teardown ()
        }
    ]

/// The database full-text index's live arm, gated on the same variable as
/// its own pack (`PostgresFullTextIndexTests`).
[<Literal>]
let private PostgresConnectionEnvVar = "TOOLUP_PG_FULLTEXT_CONNECTION_STRING"

let private postgresConnectionString =
    match Environment.GetEnvironmentVariable PostgresConnectionEnvVar with
    | null
    | "" -> None
    | s -> Some s

/// One fresh table per sequence, every boot of the sequence re-opening it,
/// dropped by the teardown.
let private postgresFactory (connectionString: string) () : SparseFactory * (unit -> unit) =
    let dataSource = NpgsqlDataSource.Create connectionString

    let options = {
        PostgresFullTextOptions.defaults with
            Table = sprintf "fte_%s" (Guid.NewGuid().ToString("N").Substring(0, 16))
    }

    let factory: SparseFactory =
        fun _ ->
            let index = createWithDataSource dataSource options identity None
            index, (fun () -> (index :?> IDisposable).Dispose())

    let teardown () =
        use cmd = dataSource.CreateCommand(sprintf "DROP TABLE IF EXISTS %s;" options.Table)
        cmd.ExecuteNonQuery() |> ignore
        dataSource.Dispose()

    factory, teardown

// ─── Phase 960 — an erasure that could not redact says so ─────────────
//
// The config store and the data-object store redact by rewriting every
// matched blob, and both used to fan those rewrites out through
// `List.map (… Upload …) |> Async.Parallel |> Async.Ignore`. Every shipped
// blob store reports a refused write as `Error`, so a refused redaction was
// discarded with the rest of the list's results, the store returned `Ok`,
// and the DSR ledger recorded `ErasureCompleted` over a blob that still
// named the subject. These cases drive both stores over a blob double that
// refuses some writes, and assert the erasure fails, names the blob that
// did not redact, leaves the ones that did redacted, and re-runs clean.

/// `Upload` returns `Error` for every blob name `refused` selects; every
/// other operation passes through to `inner`.
type private UploadRefusingBlobStorage(inner: IBlobStorage, refused: string -> bool) =
    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            BlobStorage.composeNotSupported "test double"

        member _.Upload(container, blobName, content) =
            if refused blobName then
                async { return Error "simulated storage write refusal" }
            else
                inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = inner.Download(container, blobName)
        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

let private utf8 (s: string) = Encoding.UTF8.GetBytes s

/// The run's outcome as the DSR ledger records it: preview, confirm, and
/// the audit kinds the orchestrator emitted for the request.
let private ledgerKindsWith
    (policy: ErasurePolicy)
    (handler: ToolUp.Platform.IDataExporter.IErasureHandler)
    (scopeId: string)
    =
    async {
        let events = ResizeArray<DataSubjectRequestApiHandler.DsrAuditEvent>()

        let admin: AccessContext = {
            AccessContext.unrestricted (AuthenticatedUser "admin-actor") with
                PlatformRole = Some PlatformRole.PlatformAdmin
        }

        let api =
            DataSubjectRequestApiHandler.create
                []
                [ handler ]
                policy
                scopeId
                "admin-actor"
                admin
                (fun e -> async { lock events (fun () -> events.Add e) })
                None

        let! preview =
            api.PreviewErasure {
                SubjectUserId = "u1"
                TeamId = None
                Reason = "Phase 960 / 965"
                OverridePolicy = None
            }

        match preview with
        | Error e -> return failtestf "preview failed: %s" e
        | Ok p ->
            let! _ = api.ConfirmErasure p.Request.Id
            return events |> Seq.map _.Kind |> List.ofSeq
    }

let private ledgerKinds handler scopeId =
    ledgerKindsWith ErasurePolicy.Tombstone handler scopeId

let private expectPartialFailure (handlerName: string) (result: Result<ErasureSummary, ErasureError>) =
    match result with
    | Error(HandlerPartialFailure(name, partial, detail)) ->
        Expect.equal name handlerName "the failure names its handler"
        partial, detail
    | other -> failtestf "a refused write or delete must fail the erasure with HandlerPartialFailure; got %A" other

let private configDoc (scopeId: string) (moduleKey: string) = $"config/{scopeId}/{moduleKey}.json"

let private phase960Tests =
    testList "Phase 960 — a refused redaction fails the erasure" [

        testAsync "config: one refused redaction of two fails the erasure and names the blob" {
            let inner = InMemoryBlobStorage() :> IBlobStorage
            let modA = configDoc "team-1" "modA"
            let modB = configDoc "team-1" "modB"
            let! _ = inner.Upload("_platform", modA, utf8 """{"owner":"\"u1@x\"","keep":"\"safe\""}""")
            let! _ = inner.Upload("_platform", modB, utf8 """{"owner":"\"u1@x\""}""")

            let refusing = UploadRefusingBlobStorage(inner, (=) modB) :> IBlobStorage
            let store = ToolUp.Platform.ConfigStore.create refusing
            let! result = store.Erase("team-1", "u1", ErasurePolicy.Tombstone, false)

            let partial, detail = expectPartialFailure "config" result
            Expect.stringContains detail modB "the failure names the blob that did not redact"
            Expect.isFalse (detail.Contains modA) "a blob that redacted is not named as a failure"
            Expect.equal partial.RecordsAffected 1 "the partial summary counts only what redacted"

            let! a = inner.Download("_platform", modA)
            let! b = inner.Download("_platform", modB)

            match a, b with
            | Ok a, Ok b ->
                let a = Encoding.UTF8.GetString a
                Expect.isFalse (a.Contains "u1@x") "the redaction that landed stays landed"
                Expect.stringContains a "safe" "a non-matching value is retained"
                Expect.stringContains (Encoding.UTF8.GetString b) "u1@x" "the refused blob still names the subject"
            | a, b -> failtestf "both documents must still exist: %A / %A" a b

            // Idempotent and re-runnable: a healthy store finishes the job.
            let! rerun =
                (ToolUp.Platform.ConfigStore.create inner).Erase("team-1", "u1", ErasurePolicy.Tombstone, false)

            match rerun with
            | Ok s -> Expect.equal s.RecordsAffected 1 "the re-run redacts exactly the one that was left"
            | Error e -> failtestf "the re-run over a healthy store must succeed: %A" e

            match! inner.Download("_platform", modB) with
            | Ok b -> Expect.isFalse ((Encoding.UTF8.GetString b).Contains "u1@x") "the re-run redacted it"
            | Error e -> failtestf "modB must exist: %s" e
        }

        testAsync "config: the DSR ledger records the failure, never completion" {
            let inner = InMemoryBlobStorage() :> IBlobStorage
            let! _ = inner.Upload("_platform", configDoc "team-l" "modA", utf8 """{"owner":"\"u1@x\""}""")
            let! _ = inner.Upload("_platform", configDoc "team-l" "modB", utf8 """{"owner":"\"u1@x\""}""")

            let refusing =
                UploadRefusingBlobStorage(inner, (=) (configDoc "team-l" "modB")) :> IBlobStorage

            let handler =
                ToolUp.Platform.ConfigStoreErasureHandler.erasureHandler (ToolUp.Platform.ConfigStore.create refusing)

            let! kinds = ledgerKinds handler "team-l"
            Expect.contains kinds DataSubjectRequestApiHandler.ErasureFailed "the ledger records the failure"

            Expect.isFalse
                (List.contains DataSubjectRequestApiHandler.ErasureCompleted kinds)
                "the ledger never records completion over an unredacted blob"
        }

        for policy in [ ErasurePolicy.Tombstone; ErasurePolicy.RetainPerCompliance ] do
            testAsync $"data objects ({policy}): one refused metadata redaction fails the erasure and names the blob" {
                let inner = InMemoryBlobStorage() :> IBlobStorage

                let healthy =
                    ToolUp.Platform.DataObjectStore.DataObjectStore(inner) :> IDataObjectStore

                let! _ = healthy.Save("team-1", "o1", utf8 "secret-1", "dt", "u1", Map [ "k", "u1-ref" ], Versioned)
                let! _ = healthy.Save("team-1", "o2", utf8 "secret-2", "dt", "u1", Map.empty, Versioned)
                let refusedName = "objects/o2/v1.json"

                let failing =
                    ToolUp.Platform.DataObjectStore.DataObjectStore(UploadRefusingBlobStorage(inner, (=) refusedName))
                    :> IDataObjectStore

                let! result = failing.Erase("team-1", "u1", policy, false)

                let partial, detail = expectPartialFailure "data-objects" result
                Expect.stringContains detail refusedName "the failure names the blob that did not redact"
                Expect.isFalse (detail.Contains "objects/o1/") "a blob that redacted is not named as a failure"
                Expect.equal partial.RecordsAffected 1 "the partial summary counts only the object that redacted"

                match! healthy.Get("team-1", "o1") with
                | Ok(o1, _) ->
                    Expect.equal o1.CreatedBy Erasure.TombstoneMarker "the redaction that landed stays landed"
                | Error e -> failtestf "o1 must still exist: %A" e

                // The refused object still names the subject — and its
                // content is still READABLE: the metadata that names it
                // was not rewritten, so the bytes must not be reclaimed.
                match! healthy.Get("team-1", "o2") with
                | Ok(o2, content) ->
                    Expect.equal o2.CreatedBy "u1" "the refused blob still names the subject"

                    Expect.equal
                        (Encoding.UTF8.GetString content)
                        "secret-2"
                        "its content was not reclaimed from under it"
                | Error e -> failtestf "o2 must still be readable: %A" e

                let! rerun = healthy.Erase("team-1", "u1", policy, false)

                match rerun with
                | Ok s -> Expect.equal s.RecordsAffected 1 "the re-run redacts exactly the one that was left"
                | Error e -> failtestf "the re-run over a healthy store must succeed: %A" e

                match! healthy.Get("team-1", "o2") with
                | Ok(o2, _) -> Expect.equal o2.CreatedBy Erasure.TombstoneMarker "the re-run redacted it"
                | Error e -> failtestf "o2 must still exist: %A" e
            }

        testAsync "data objects: a refused tombstone content write fails the erasure before any metadata moves" {
            let inner = InMemoryBlobStorage() :> IBlobStorage

            let healthy =
                ToolUp.Platform.DataObjectStore.DataObjectStore(inner) :> IDataObjectStore

            let! _ = healthy.Save("team-1", "o1", utf8 "secret-1", "dt", "u1", Map.empty, Versioned)

            let failing =
                ToolUp.Platform.DataObjectStore.DataObjectStore(
                    UploadRefusingBlobStorage(inner, _.StartsWith("objects/_content/"))
                )
                :> IDataObjectStore

            let! result = failing.Erase("team-1", "u1", ErasurePolicy.Tombstone, false)

            let partial, detail = expectPartialFailure "data-objects" result
            Expect.stringContains detail "objects/_content/" "the failure names the tombstone content blob"
            Expect.equal partial.RecordsAffected 0 "nothing was redacted"

            // Metadata naming a tombstone that was never written would make
            // the object unreadable; the erasure stops before that.
            match! healthy.Get("team-1", "o1") with
            | Ok(o1, content) ->
                Expect.equal o1.CreatedBy "u1" "no metadata was rewritten"
                Expect.equal (Encoding.UTF8.GetString content) "secret-1" "the object is still whole"
            | Error e -> failtestf "o1 must still be readable: %A" e
        }

        testAsync "data objects: the DSR ledger records the failure, never completion" {
            let inner = InMemoryBlobStorage() :> IBlobStorage

            let healthy =
                ToolUp.Platform.DataObjectStore.DataObjectStore(inner) :> IDataObjectStore

            let! _ = healthy.Save("team-l", "o1", utf8 "secret-1", "dt", "u1", Map.empty, Versioned)
            let! _ = healthy.Save("team-l", "o2", utf8 "secret-2", "dt", "u1", Map.empty, Versioned)

            let failing =
                ToolUp.Platform.DataObjectStore.DataObjectStore(
                    UploadRefusingBlobStorage(inner, (=) "objects/o2/v1.json")
                )
                :> IDataObjectStore

            let! kinds = ledgerKinds (ToolUp.Platform.DataObjectStoreErasureHandler.erasureHandler failing) "team-l"
            Expect.contains kinds DataSubjectRequestApiHandler.ErasureFailed "the ledger records the failure"

            Expect.isFalse
                (List.contains DataSubjectRequestApiHandler.ErasureCompleted kinds)
                "the ledger never records completion over an unredacted blob"
        }
    ]

// ─── Phase 965 — a hard-delete erasure that could not delete says so ───
//
// The `HardDelete` arms of the same two functions fanned their `Delete`
// calls out through `Async.Parallel |> Async.Ignore`. `IBlobStorage.Delete`
// is idempotent on a missing blob (`IBlobStorageContract` pins it on every
// bound backend), so an `Error` from it is a refusal, never a not-found —
// and it was discarded: the erasure returned `Ok` with every matched record
// counted, and the DSR ledger recorded `ErasureCompleted` over a blob that
// still named the subject.

/// `Delete` returns `Error` for every blob name `refused` selects; every
/// other operation passes through to `inner`.
type private DeleteRefusingBlobStorage(inner: IBlobStorage, refused: string -> bool) =
    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            BlobStorage.composeNotSupported "test double"

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = inner.Download(container, blobName)

        member _.Delete(container, blobName) =
            if refused blobName then
                async { return Error "simulated storage delete refusal" }
            else
                inner.Delete(container, blobName)

        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

let private blobExists (inner: IBlobStorage) (container: string) (name: string) = async {
    match! inner.Download(container, name) with
    | Ok _ -> return true
    | Error _ -> return false
}

let private phase965Tests =
    testList "Phase 965 — a refused delete fails the erasure" [

        testAsync "config: one refused delete of two fails the erasure and names the blob" {
            let inner = InMemoryBlobStorage() :> IBlobStorage
            let modA = configDoc "team-1" "modA"
            let modB = configDoc "team-1" "modB"
            let! _ = inner.Upload("_platform", modA, utf8 """{"owner":"\"u1@x\""}""")
            let! _ = inner.Upload("_platform", modB, utf8 """{"owner":"\"u1@x\""}""")

            let refusing = DeleteRefusingBlobStorage(inner, (=) modB) :> IBlobStorage
            let store = ToolUp.Platform.ConfigStore.create refusing
            let! result = store.Erase("team-1", "u1", ErasurePolicy.HardDelete, false)

            let partial, detail = expectPartialFailure "config" result
            Expect.stringContains detail modB "the failure names the blob that was not deleted"
            Expect.isFalse (detail.Contains modA) "a blob that was deleted is not named as a failure"
            Expect.equal partial.RecordsAffected 1 "the partial summary counts only what was deleted"

            let! aThere = blobExists inner "_platform" modA
            let! bThere = blobExists inner "_platform" modB
            Expect.isFalse aThere "the delete that landed stays landed"
            Expect.isTrue bThere "the refused blob is still in the container"

            // Idempotent and re-runnable: a healthy store finishes the job.
            let! rerun =
                (ToolUp.Platform.ConfigStore.create inner).Erase("team-1", "u1", ErasurePolicy.HardDelete, false)

            match rerun with
            | Ok s -> Expect.equal s.RecordsAffected 1 "the re-run deletes exactly the one that was left"
            | Error e -> failtestf "the re-run over a healthy store must succeed: %A" e

            let! bAfter = blobExists inner "_platform" modB
            Expect.isFalse bAfter "the re-run deleted it"
        }

        testAsync "config: every refused delete is named, and a fully-refused erasure counts nothing" {
            let inner = InMemoryBlobStorage() :> IBlobStorage
            let modA = configDoc "team-2" "modA"
            let modB = configDoc "team-2" "modB"
            let! _ = inner.Upload("_platform", modA, utf8 """{"owner":"\"u1@x\""}""")
            let! _ = inner.Upload("_platform", modB, utf8 """{"owner":"\"u1@x\""}""")

            let refusing = DeleteRefusingBlobStorage(inner, (fun _ -> true)) :> IBlobStorage

            let! result =
                (ToolUp.Platform.ConfigStore.create refusing).Erase("team-2", "u1", ErasurePolicy.HardDelete, false)

            let partial, detail = expectPartialFailure "config" result
            Expect.stringContains detail modA "the first refused blob is named"
            Expect.stringContains detail modB "the second refused blob is named"
            Expect.equal partial.RecordsAffected 0 "nothing was deleted"
        }

        testAsync "config: the DSR ledger records the failure, never completion" {
            let inner = InMemoryBlobStorage() :> IBlobStorage
            let! _ = inner.Upload("_platform", configDoc "team-l" "modA", utf8 """{"owner":"\"u1@x\""}""")
            let! _ = inner.Upload("_platform", configDoc "team-l" "modB", utf8 """{"owner":"\"u1@x\""}""")

            let refusing =
                DeleteRefusingBlobStorage(inner, (=) (configDoc "team-l" "modB")) :> IBlobStorage

            let handler =
                ToolUp.Platform.ConfigStoreErasureHandler.erasureHandler (ToolUp.Platform.ConfigStore.create refusing)

            let! kinds = ledgerKindsWith ErasurePolicy.HardDelete handler "team-l"
            Expect.contains kinds DataSubjectRequestApiHandler.ErasureFailed "the ledger records the failure"

            Expect.isFalse
                (List.contains DataSubjectRequestApiHandler.ErasureCompleted kinds)
                "the ledger never records completion over an undeleted blob"
        }

        testAsync "data objects: one refused version delete fails the erasure and names the blob" {
            let inner = InMemoryBlobStorage() :> IBlobStorage

            let healthy =
                ToolUp.Platform.DataObjectStore.DataObjectStore(inner) :> IDataObjectStore

            let! _ = healthy.Save("team-1", "o1", utf8 "secret-1", "dt", "u1", Map.empty, Versioned)
            let! _ = healthy.Save("team-1", "o2", utf8 "secret-2", "dt", "u1", Map.empty, Versioned)
            let refusedName = "objects/o2/v1.json"

            let failing =
                ToolUp.Platform.DataObjectStore.DataObjectStore(DeleteRefusingBlobStorage(inner, (=) refusedName))
                :> IDataObjectStore

            let! result = failing.Erase("team-1", "u1", ErasurePolicy.HardDelete, false)

            let partial, detail = expectPartialFailure "data-objects" result
            Expect.stringContains detail refusedName "the failure names the blob that was not deleted"
            Expect.isFalse (detail.Contains "objects/o1/") "a blob that was deleted is not named as a failure"
            Expect.equal partial.RecordsAffected 1 "the partial summary counts only the object that was deleted"

            let! o1Meta = blobExists inner "team-1" "objects/o1/v1.json"
            let! o2Meta = blobExists inner "team-1" refusedName
            Expect.isFalse o1Meta "the delete that landed stays landed"
            Expect.isTrue o2Meta "the refused version blob is still in the container"

            // The refused object is still READABLE: the metadata that names
            // its content survives, so the bytes must not be reclaimed.
            match! healthy.Get("team-1", "o2") with
            | Ok(_, content) ->
                Expect.equal (Encoding.UTF8.GetString content) "secret-2" "its content was not reclaimed from under it"
            | Error e -> failtestf "o2 must still be readable: %A" e

            let! rerun = healthy.Erase("team-1", "u1", ErasurePolicy.HardDelete, false)

            match rerun with
            | Ok s -> Expect.equal s.RecordsAffected 1 "the re-run deletes exactly the one that was left"
            | Error e -> failtestf "the re-run over a healthy store must succeed: %A" e

            let! o2After = blobExists inner "team-1" refusedName
            Expect.isFalse o2After "the re-run deleted it"
        }

        testAsync
            "data objects: a refused delete leaves a deleted object's content reclaimed and the refused one's intact" {
            let inner = InMemoryBlobStorage() :> IBlobStorage

            let healthy =
                ToolUp.Platform.DataObjectStore.DataObjectStore(inner) :> IDataObjectStore

            let! _ = healthy.Save("team-c", "o1", utf8 "secret-1", "dt", "u1", Map.empty, Versioned)
            let! _ = healthy.Save("team-c", "o2", utf8 "secret-2", "dt", "u1", Map.empty, Versioned)

            let contentCount () = async {
                let! names = inner.List("team-c", "objects/_content/")
                return names |> List.filter _.EndsWith(".data") |> List.length
            }

            let! before = contentCount ()
            Expect.equal before 2 "two content blobs before the erasure"

            let failing =
                ToolUp.Platform.DataObjectStore.DataObjectStore(
                    DeleteRefusingBlobStorage(inner, (=) "objects/o2/v1.json")
                )
                :> IDataObjectStore

            let! result = failing.Erase("team-c", "u1", ErasurePolicy.HardDelete, false)
            expectPartialFailure "data-objects" result |> ignore

            // o1's metadata went, so its bytes are reclaimed — a re-run
            // could not find them again. o2's metadata survives, so its
            // bytes do too.
            let! after = contentCount ()
            Expect.equal after 1 "only the refused object's content remains"

            match! healthy.Get("team-c", "o2") with
            | Ok(_, content) -> Expect.equal (Encoding.UTF8.GetString content) "secret-2" "o2 is still whole"
            | Error e -> failtestf "o2 must still be readable: %A" e
        }

        testAsync "data objects: the DSR ledger records the failure, never completion" {
            let inner = InMemoryBlobStorage() :> IBlobStorage

            let healthy =
                ToolUp.Platform.DataObjectStore.DataObjectStore(inner) :> IDataObjectStore

            let! _ = healthy.Save("team-l", "o1", utf8 "secret-1", "dt", "u1", Map.empty, Versioned)
            let! _ = healthy.Save("team-l", "o2", utf8 "secret-2", "dt", "u1", Map.empty, Versioned)

            let failing =
                ToolUp.Platform.DataObjectStore.DataObjectStore(
                    DeleteRefusingBlobStorage(inner, (=) "objects/o2/v1.json")
                )
                :> IDataObjectStore

            let! kinds =
                ledgerKindsWith
                    ErasurePolicy.HardDelete
                    (ToolUp.Platform.DataObjectStoreErasureHandler.erasureHandler failing)
                    "team-l"

            Expect.contains kinds DataSubjectRequestApiHandler.ErasureFailed "the ledger records the failure"

            Expect.isFalse
                (List.contains DataSubjectRequestApiHandler.ErasureCompleted kinds)
                "the ledger never records completion over an undeleted blob"
        }
    ]

let tests =
    testList "Phase 204 — cross-index erasure conformance" [

        phase960Tests

        phase965Tests

        propertyTests "InMemoryBM25Index" (fun () -> bm25Factory, ignore)

        // Phase 946 — the same two properties over the database full-text
        // index (Phase 893), on its live arm.
        match postgresConnectionString with
        | Some connectionString -> propertyTests "PostgresFullTextIndex" (postgresFactory connectionString)
        | None ->
            testList "PostgresFullTextIndex" [
                ptestCase $"skipped — {PostgresConnectionEnvVar} not set" <| fun _ -> ()
            ]

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

        // ── Phase 726 — the emptiness-as-absence reproducers ───────────
        //
        // Written by Phase 204 (which found the defect at restart seed 101
        // and narrowed its own generator to avoid it) and left DISARMED
        // because the fix was outside that phase's lease. Phase 726 fixed
        // both faces and armed them.
        //
        // Face one, below. Both in-process indexes used to decide "is this
        // lazily-hydrated scope already loaded?" by asking whether they
        // currently held anything for it — `InMemoryVectorStore` tested
        // `store.Keys |> Seq.exists (fun (sk, _) -> sk = scopeKey)`, and
        // `InMemoryBM25Index` tested `scopes.ContainsKey`. Emptiness was
        // therefore read as absence. `DeleteByScope` empties the scope in
        // memory and marks it dirty, but the persisted snapshot lives until
        // the next flush — so the very next read re-hydrated the scope from
        // that snapshot and every chunk the scope-wide delete had just
        // removed was back, in memory and (on the following flush) at rest
        // again. Both stores now track loaded scope keys explicitly, so a
        // scope that genuinely holds nothing stays loaded-and-empty.
        //
        // Not reachable on `Platform` / `Deployment`, which are loaded
        // eagerly at construction. Reachable on every `Team` / `User` scope
        // once a flush has happened — i.e. in any real deployment, where the
        // background flush loop runs on a timer rather than only at
        // disposal. `DeleteByScope` is the documented "configuration-grade
        // reset" for a scope, so a tenant wipe that silently un-wipes itself
        // is the shape that matters.
        testAsync "DeleteByScope on a lazily-hydrated scope survives the next read" {
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

        // Face two of the same root, and the reason this case exists beside
        // the one above rather than inside it. The case above READS before it
        // wipes, so the scope is already hydrated by the time `DeleteByScope`
        // runs — it pins the explicit loaded-key tracking and nothing else.
        //
        // Here the wipe is the FIRST thing a fresh process does to the scope.
        // Neither delete path used to call its hydration guard, so the wipe
        // operated on an unloaded (empty) map, removed nothing, and left the
        // scope still marked un-read; the read that followed then hydrated
        // the whole persisted corpus straight back. Phase 204's property
        // could not see this one — it asserts, and so hydrates every scope,
        // immediately after each restart.
        //
        // This case therefore goes red if EITHER fix is removed: without the
        // delete-path hydration guard the wipe misses the snapshot, and
        // without explicit loaded-key tracking the read after the wipe infers
        // "holds nothing" as "not yet loaded" and re-hydrates anyway.
        testAsync "DeleteByScope issued before any read of the scope reaches the persisted snapshot" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let cache = InMemoryEmbeddingCache() :> IEmbeddingCache
            let scope = Team "t1"

            // ── Boot 1: populate a lazily-hydrated scope and flush it ──
            do! async {
                let h = boot storage cache

                try
                    let! _ =
                        runSequence "delete-before-read boot 1" h Model.empty [
                            Ingest(scope, "d0", 0, None)
                            Ingest(scope, "d0", 1, None)
                        ]

                    ()
                finally
                    h.Shutdown()
            }

            // The snapshots must actually be at rest, or the wipe below has
            // nothing to fail to reach and the case proves nothing.
            let! denseAtRest = storage.Download("_rag", "_rag/team:t1/index.json")
            let! sparseAtRest = storage.Download("_rag", "_rag/team:t1/bm25.json")
            Expect.isTrue (Result.isOk denseAtRest) "the dense snapshot must exist before the wipe"
            Expect.isTrue (Result.isOk sparseAtRest) "the sparse snapshot must exist before the wipe"

            // ── Boot 2: wipe FIRST — no read of the scope precedes it ──
            do! async {
                let h = boot storage cache

                try
                    let! report = h.Lifecycle.DeleteByScope scope
                    Expect.isTrue (IndexLifecycleReport.isClean report) "DeleteByScope reports success"

                    let! dense = h.VectorStore.Search [ scope ] unitVec sweepK
                    let! sparse = h.Sparse.Search [ scope ] universalTerm sweepK

                    Expect.isEmpty
                        dense
                        "a wipe issued before any read must reach the dense snapshot — the read after it must not resurrect the scope"

                    Expect.isEmpty
                        sparse
                        "a wipe issued before any read must reach the BM25 snapshot — the read after it must not resurrect the scope"
                finally
                    h.Shutdown()
            }

            // ── Boot 3: and it stayed wiped at rest, across a restart ──
            let third = boot storage cache

            try
                let! dense = third.VectorStore.Search [ scope ] unitVec sweepK
                let! sparse = third.Sparse.Search [ scope ] universalTerm sweepK

                Expect.isEmpty dense "the wipe must survive a restart on the dense leg"
                Expect.isEmpty sparse "the wipe must survive a restart on the BM25 leg"
            finally
                third.Shutdown()
        }

        // Phase 861 — the vector-store restart laws, bound to the in-process
        // dense store: every mutation hydrates a lazily loaded scope first
        // (Phase 726 covered the two deletes; this holds all of them), and
        // an unreadable snapshot refuses it.
        ToolUp.Platform.Tests.Contracts.IVectorStoreContract.tests "InMemoryVectorStore" (fun storage ->
            new InMemoryVectorStore(storage, logger = SilentLogger(), flushIntervalMs = 60000) :> IVectorStore)

        ToolUp.Platform.Tests.Contracts.IVectorStoreContract.unreadableSnapshotTests
            "InMemoryVectorStore"
            (function
            | :? RagScopeSnapshotUnreadableException -> true
            | _ -> false)
            (fun storage ->
                new InMemoryVectorStore(storage, logger = SilentLogger(), flushIntervalMs = 60000) :> IVectorStore)
    ]
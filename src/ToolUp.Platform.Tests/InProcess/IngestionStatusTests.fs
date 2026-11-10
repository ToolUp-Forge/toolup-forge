module ToolUp.Platform.Tests.InProcess.IngestionStatusTests

// ─── Phase 173 — Data Manager ingestion-status surfacing ─────────
//
// Two packs:
//   1. `IIngestionStatusStore` contract — Set/Get/List/GetTotal,
//      Pending→Indexed overwrite (total preserved), scope isolation —
//      over BOTH the in-memory default and the `IDataObjectStore`-backed
//      default (proves the serialise/deserialise path round-trips).
//   2. The Data Manager's attempts through the one ingestion observer —
//      the success path flips `Pending → Indexed` only on the LAST chunk;
//      a chunk failure flips to `Failed reason`; a document the store
//      never began (e.g. a KB document) is left untouched.
//   3. The knowledge-base lattice (`IngestionStatus.step` / `isTerminal`)
//      as a table.

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra.DataObjectStore
open ToolUp.Platform.VectorKnowledgeTypes
open DataManagementTypes
open ToolUp.RAG.IngestionTypes
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose
open ToolUp.Platform.Testing

let private run = Async.RunSynchronously
let private logger = ConsoleLogger.ConsoleLogger() :> ILogger

// ── 1. store contract, parameterised over a store factory ──

let private storeContract (label: string) (factory: unit -> IIngestionStatusStore) =
    testList label [
        test "Set then Get round-trips" {
            let store = factory ()
            run (store.Set("team-a", "sales.csv", Indexed))
            Expect.equal (run (store.Get("team-a", "sales.csv"))) (Some Indexed) "status reads back"
        }

        test "SetPending records status + chunk total" {
            let store = factory ()
            run (store.SetPending("team-a", "big.csv", 7))
            Expect.equal (run (store.Get("team-a", "big.csv"))) (Some Pending) "Pending status"
            Expect.equal (run (store.GetTotal("team-a", "big.csv"))) 7 "chunk total recorded"
        }

        test "overwrite Pending → Indexed preserves the chunk total" {
            let store = factory ()
            run (store.SetPending("team-a", "doc.csv", 4))
            run (store.Set("team-a", "doc.csv", Indexed))
            Expect.equal (run (store.Get("team-a", "doc.csv"))) (Some Indexed) "terminal status wins"
            Expect.equal (run (store.GetTotal("team-a", "doc.csv"))) 4 "total survives the transition"
        }

        test "List returns every (documentId, status) in the scope" {
            let store = factory ()
            run (store.Set("team-a", "a.csv", Indexed))
            run (store.Set("team-a", "b.csv", Failed "boom"))
            let listed = run (store.List "team-a") |> List.sortBy fst
            Expect.equal listed [ "a.csv", Indexed; "b.csv", Failed "boom" ] "both files present"
        }

        test "scope isolation — scope A status is invisible to scope B" {
            let store = factory ()
            run (store.Set("team-a", "secret.csv", Indexed))
            Expect.equal (run (store.Get("team-b", "secret.csv"))) None "other scope cannot read it"
            Expect.isEmpty (run (store.List "team-b")) "other scope lists nothing"
        }

        test "missing entry reads as None / total 0" {
            let store = factory ()
            Expect.equal (run (store.Get("team-a", "nope.csv"))) None "no status"
            Expect.equal (run (store.GetTotal("team-a", "nope.csv"))) 0 "no total"
        }
    ]

/// The store outlives this factory and the contract has no teardown hook,
/// so the directory lives until the test process exits.
let private dataObjectBackedFactory () : IIngestionStatusStore =
    let tempDir = TempDir.forRun "ingstatus"
    let blob = LocalFileStorage.LocalFileStorage(tempDir.Path) :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    IngestionStatusStore.create dos (Some logger)

// ── 2. the Data Manager's attempts, through the one observer ──

let private chunkJob (container: string) (documentId: string) (chunkIndex: int) : IngestionJob = {
    DocumentId = documentId
    DocumentName = documentId
    ChunkId = sprintf "%s:chunk:%d" documentId chunkIndex
    Chunk = { Content = "x"; Metadata = Map.empty }
    Scope = Deployment
    ScopeId = "scope-1"
    Container = container
    OriginatingUserId = None
    Attempt =
        Some {
            AttemptId = "attempt-1"
            EnqueuedChunks = 0
        }
}

/// A status store, the Data Manager's attempts over it, and the observer.
let private dataManager () =
    let store = IngestionStatusStore.createInMemory ()

    let attempts =
        IngestionAttemptStore.dataManager
            store
            (ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage())
            logger

    store, attempts, ToolUp.RAG.IngestionObserver.create attempts None logger

let private observerTests =
    testList "Data Manager attempt transitions" [
        test "flips Pending → Indexed only on the last chunk" {
            let store, attempts, observer = dataManager ()
            run (attempts.Begin("team-a", "two.csv", "attempt-1", 2))

            run (observer.OnChunkIndexed(chunkJob "team-a" "two.csv" 0))
            Expect.equal (run (store.Get("team-a", "two.csv"))) (Some Pending) "still Pending after 1 of 2"

            run (observer.OnChunkIndexed(chunkJob "team-a" "two.csv" 1))
            Expect.equal (run (store.Get("team-a", "two.csv"))) (Some Indexed) "Indexed after the last chunk"
        }

        test "a chunk failure marks the document Failed" {
            let store, attempts, observer = dataManager ()
            run (attempts.Begin("team-a", "bad.csv", "attempt-1", 3))

            run (observer.OnChunkFailed(chunkJob "team-a" "bad.csv" 0, "embedding provider down"))

            Expect.equal
                (run (store.Get("team-a", "bad.csv")))
                (Some(Failed "embedding provider down"))
                "Failed carries the reason"
        }

        test "a document the store never began (e.g. a KB doc) is left untouched" {
            let store, _, observer = dataManager ()

            run (observer.OnChunkIndexed(chunkJob "team-a" "kb-doc.pdf" 0))
            Expect.equal (run (store.Get("team-a", "kb-doc.pdf"))) None "no entry created on success path"

            run (observer.OnChunkFailed(chunkJob "team-a" "kb-doc.pdf" 0, "boom"))
            Expect.equal (run (store.Get("team-a", "kb-doc.pdf"))) None "no entry created on failure path"
        }
    ]

let private progressTests =
    testList "Data Manager attempt progress" [
        test "a failure settles the attempt: a later success of the same attempt leaves it Failed" {
            let store, attempts, observer = dataManager ()
            run (attempts.Begin("team-a", "three.csv", "attempt-1", 3))

            run (observer.OnChunkIndexed(chunkJob "team-a" "three.csv" 0))
            run (observer.OnChunkIndexed(chunkJob "team-a" "three.csv" 1))
            run (observer.OnChunkFailed(chunkJob "team-a" "three.csv" 2, "dead-lettered"))
            run (observer.OnChunkIndexed(chunkJob "team-a" "three.csv" 2))

            Expect.equal
                (run (store.Get("team-a", "three.csv")))
                (Some(Failed "dead-lettered"))
                "Failed is terminal for the attempt, as it is for a knowledge-base document; a retry is a new attempt"
        }

        test "a chunk reported twice is counted once" {
            let store, attempts, observer = dataManager ()
            run (attempts.Begin("team-a", "two.csv", "attempt-1", 2))

            run (observer.OnChunkIndexed(chunkJob "team-a" "two.csv" 0))
            run (observer.OnChunkIndexed(chunkJob "team-a" "two.csv" 0))

            Expect.equal
                (run (store.Get("team-a", "two.csv")))
                (Some Pending)
                "the redelivered chunk did not complete the file"
        }

        test "a callback of a superseded attempt moves nothing" {
            let store, attempts, observer = dataManager ()
            run (attempts.Begin("team-a", "one.csv", "attempt-1", 1))
            run (attempts.Begin("team-a", "one.csv", "attempt-2", 1))

            run (observer.OnChunkIndexed(chunkJob "team-a" "one.csv" 0))

            Expect.equal (run (store.Get("team-a", "one.csv"))) (Some Pending) "attempt one's late success is dropped"
        }
    ]

// ── 3. the knowledge-base lattice, as a table ──

module private Lattice =
    open SharedTypes


    let latticeTests =
        let rows: (SharedTypes.IngestionStatus * SharedTypes.ChunkOutcome * SharedTypes.IngestionStatus option) list = [
            SharedTypes.IngestionStatus.Queued,
            SharedTypes.ChunkIndexed,
            Some(SharedTypes.IngestionStatus.Embedding(1, 3))
            SharedTypes.IngestionStatus.ExtractingText,
            SharedTypes.ChunkIndexed,
            Some(SharedTypes.IngestionStatus.Embedding(1, 3))
            SharedTypes.IngestionStatus.Embedding(1, 3),
            SharedTypes.ChunkIndexed,
            Some(SharedTypes.IngestionStatus.Embedding(2, 3))
            SharedTypes.IngestionStatus.Embedding(2, 3),
            SharedTypes.ChunkIndexed,
            Some(SharedTypes.IngestionStatus.Complete 3)
            SharedTypes.IngestionStatus.Embedding(1, 3),
            SharedTypes.ChunkFailed "x",
            Some(SharedTypes.IngestionStatus.Failed "x")
            SharedTypes.IngestionStatus.Queued,
            SharedTypes.ChunkFailed "x",
            Some(SharedTypes.IngestionStatus.Failed "x")
            SharedTypes.IngestionStatus.Complete 3, SharedTypes.ChunkIndexed, None
            SharedTypes.IngestionStatus.Complete 3, SharedTypes.ChunkFailed "x", None
            SharedTypes.IngestionStatus.Failed "x", SharedTypes.ChunkIndexed, None
            SharedTypes.IngestionStatus.UploadRejected "r", SharedTypes.ChunkIndexed, None
            SharedTypes.IngestionStatus.UnsupportedFormat "u", SharedTypes.ChunkIndexed, None
            SharedTypes.IngestionStatus.OcrUnavailable "o", SharedTypes.ChunkFailed "x", None
        ]

        testList "Knowledge-base ingestion lattice" [
            test "IngestionStatus.step over a three-chunk attempt" {
                for status, outcome, expected in rows do
                    Expect.equal (IngestionStatus.step 3 status outcome) expected (sprintf "%A after %A" status outcome)
            }

            test "IngestionStatus.isTerminal is exactly the statuses step never leaves" {
                for status in
                    [
                        SharedTypes.IngestionStatus.Queued
                        SharedTypes.IngestionStatus.ExtractingText
                        SharedTypes.IngestionStatus.Embedding(1, 3)
                        SharedTypes.IngestionStatus.Complete 3
                        SharedTypes.IngestionStatus.Failed "x"
                        SharedTypes.IngestionStatus.UploadRejected "r"
                        SharedTypes.IngestionStatus.UnsupportedFormat "u"
                        SharedTypes.IngestionStatus.OcrUnavailable "o"
                    ] do
                    let stuck =
                        IngestionStatus.step 3 status SharedTypes.ChunkIndexed = None
                        && IngestionStatus.step 3 status (SharedTypes.ChunkFailed "y") = None

                    Expect.equal (IngestionStatus.isTerminal status) stuck (sprintf "%A" status)
            }
        ]

let tests =
    testList "Phase 173 — ingestion-status surfacing" [
        storeContract "IIngestionStatusStore (in-memory)" IngestionStatusStore.createInMemory
        storeContract "IIngestionStatusStore (IDataObjectStore-backed)" dataObjectBackedFactory
        observerTests
        progressTests
        Lattice.latticeTests
    ]
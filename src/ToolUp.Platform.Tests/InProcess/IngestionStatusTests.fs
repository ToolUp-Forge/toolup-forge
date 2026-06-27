module ToolUp.Platform.Tests.InProcess.IngestionStatusTests

// ─── Phase 173 — Data Manager ingestion-status surfacing ─────────
//
// Two packs:
//   1. `IIngestionStatusStore` contract — Set/Get/List/GetTotal,
//      Pending→Indexed overwrite (total preserved), scope isolation —
//      over BOTH the in-memory default and the `IDataObjectStore`-backed
//      default (proves the serialise/deserialise path round-trips).
//   2. `DataManagerIngestionObserver` transitions — the success path
//      flips `Pending → Indexed` only on the LAST chunk; a chunk
//      failure flips to `Failed reason`; a document the store never
//      marked `Pending` (e.g. a KB document) is left untouched.

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.VectorKnowledgeTypes
open DataManagementTypes
open ToolUp.RAG.IngestionTypes

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

let private dataObjectBackedFactory () : IIngestionStatusStore =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-ingstatus-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    IngestionStatusStore.create dos (Some logger)

// ── 2. observer transitions ──

let private chunkJob (container: string) (documentId: string) (chunkIndex: int) : IngestionJob = {
    DocumentId = documentId
    DocumentName = documentId
    ChunkId = sprintf "%s:chunk:%d" documentId chunkIndex
    Chunk = { Content = "x"; Metadata = Map.empty }
    Scope = Deployment
    ScopeId = "scope-1"
    Container = container
    OriginatingUserId = None
}

let private observerTests =
    testList "DataManagerIngestionObserver transitions" [
        test "flips Pending → Indexed only on the last chunk" {
            let store = IngestionStatusStore.createInMemory ()
            let observer = ToolUp.RAG.DataManagerIngestionObserver.create store None logger
            run (store.SetPending("team-a", "two.csv", 2))

            run (observer.OnChunkIndexed(chunkJob "team-a" "two.csv" 0))
            Expect.equal (run (store.Get("team-a", "two.csv"))) (Some Pending) "still Pending after 1 of 2"

            run (observer.OnChunkIndexed(chunkJob "team-a" "two.csv" 1))
            Expect.equal (run (store.Get("team-a", "two.csv"))) (Some Indexed) "Indexed after the last chunk"
        }

        test "a chunk failure marks the document Failed" {
            let store = IngestionStatusStore.createInMemory ()
            let observer = ToolUp.RAG.DataManagerIngestionObserver.create store None logger
            run (store.SetPending("team-a", "bad.csv", 3))

            run (observer.OnChunkFailed(chunkJob "team-a" "bad.csv" 0, "embedding provider down"))

            Expect.equal
                (run (store.Get("team-a", "bad.csv")))
                (Some(Failed "embedding provider down"))
                "Failed carries the reason"
        }

        test "a document the store never marked Pending (e.g. a KB doc) is left untouched" {
            let store = IngestionStatusStore.createInMemory ()
            let observer = ToolUp.RAG.DataManagerIngestionObserver.create store None logger

            // No SetPending — this is not a Data Manager file.
            run (observer.OnChunkIndexed(chunkJob "team-a" "kb-doc.pdf" 0))
            Expect.equal (run (store.Get("team-a", "kb-doc.pdf"))) None "no entry created on success path"

            run (observer.OnChunkFailed(chunkJob "team-a" "kb-doc.pdf" 0, "boom"))
            Expect.equal (run (store.Get("team-a", "kb-doc.pdf"))) None "no entry created on failure path"
        }
    ]

let tests =
    testList "Phase 173 — ingestion-status surfacing" [
        storeContract "IIngestionStatusStore (in-memory)" IngestionStatusStore.createInMemory
        storeContract "IIngestionStatusStore (IDataObjectStore-backed)" dataObjectBackedFactory
        observerTests
    ]
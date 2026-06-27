module ToolUp.Platform.Tests.InProcess.IngestionRetryTests

// ─── Phase 220 — Data-Manager ingestion retry + status filtering ─────
//
// Covers the Phase 220 additions:
//   * retry-eligibility (`FileIngestionStatus.isRetryable`) — only a
//     `Failed` file is retryable; an in-flight `Pending` is not (the
//     handler's idempotency guard), and a no-status (no-RAG) file isn't
//     either, so a no-RAG deployment exposes no retry affordance.
//   * status-filter narrowing (`FileIngestionStatus.matchesFilter`).
//   * the store re-cycles Failed → Pending → Indexed, proving a retried
//     file can re-enter the pipeline and reach the indexed terminal.

open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open DataManagementTypes
open ToolUp.RAG.IngestionTypes

let private run = Async.RunSynchronously
let private logger = ConsoleLogger.ConsoleLogger() :> ILogger

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

let private retryEligibility =
    testList "retry eligibility (isRetryable)" [
        test "only a Failed file is retryable" {
            Expect.isTrue (FileIngestionStatus.isRetryable (Some(Failed "too large"))) "Failed ⇒ retryable"
            Expect.isFalse (FileIngestionStatus.isRetryable (Some Indexed)) "Indexed ⇒ no retry"
            Expect.isFalse (FileIngestionStatus.isRetryable (Some NotIngested)) "NotIngested ⇒ no retry"
        }

        test "an in-flight Pending file is NOT retryable (idempotency)" {
            Expect.isFalse (FileIngestionStatus.isRetryable (Some Pending)) "Pending ⇒ no double-enqueue"
        }

        test "a no-status (no-RAG) file is NOT retryable" {
            Expect.isFalse (FileIngestionStatus.isRetryable None) "no entry ⇒ no retry affordance"
        }
    ]

let private filtering =
    testList "status filter (matchesFilter)" [
        test "AllFiles passes everything, including no-status files" {
            for status in [ Some Indexed; Some Pending; Some(Failed "x"); Some NotIngested; None ] do
                Expect.isTrue (FileIngestionStatus.matchesFilter AllFiles status) "AllFiles is a no-op"
        }

        test "each single-status filter narrows to exactly its status" {
            Expect.isTrue (FileIngestionStatus.matchesFilter OnlyIndexed (Some Indexed)) "Indexed ✓"
            Expect.isFalse (FileIngestionStatus.matchesFilter OnlyIndexed (Some Pending)) "Indexed ✗ Pending"
            Expect.isTrue (FileIngestionStatus.matchesFilter OnlyPending (Some Pending)) "Pending ✓"
            Expect.isTrue (FileIngestionStatus.matchesFilter OnlyFailed (Some(Failed "x"))) "Failed ✓"
            Expect.isFalse (FileIngestionStatus.matchesFilter OnlyFailed (Some Indexed)) "Failed ✗ Indexed"
        }

        test "OnlyNotIndexed matches both NotIngested and a missing entry" {
            Expect.isTrue (FileIngestionStatus.matchesFilter OnlyNotIndexed (Some NotIngested)) "NotIngested ✓"
            Expect.isTrue (FileIngestionStatus.matchesFilter OnlyNotIndexed None) "missing entry reads as NotIndexed"
            Expect.isFalse (FileIngestionStatus.matchesFilter OnlyNotIndexed (Some Indexed)) "Indexed ✗"
        }
    ]

let private reCycle =
    testList "store re-cycles Failed → Pending → Indexed on retry" [
        test "a Failed file can be re-ingested to Indexed" {
            let store = IngestionStatusStore.createInMemory ()
            let observer = ToolUp.RAG.DataManagerIngestionObserver.create store None logger

            // First ingest fails on a chunk.
            run (store.SetPending("team-a", "f.csv", 2))
            run (observer.OnChunkFailed(chunkJob "team-a" "f.csv" 0, "boom"))
            Expect.equal (run (store.Get("team-a", "f.csv"))) (Some(Failed "boom")) "Failed after the first attempt"
            Expect.isTrue (FileIngestionStatus.isRetryable (run (store.Get("team-a", "f.csv")))) "now retryable"

            // Retry re-marks Pending (what the post-save hook does on re-fire),
            // then both chunks index ⇒ Indexed.
            run (store.SetPending("team-a", "f.csv", 2))
            Expect.equal (run (store.Get("team-a", "f.csv"))) (Some Pending) "back to Pending on retry"
            run (observer.OnChunkIndexed(chunkJob "team-a" "f.csv" 0))
            run (observer.OnChunkIndexed(chunkJob "team-a" "f.csv" 1))
            Expect.equal (run (store.Get("team-a", "f.csv"))) (Some Indexed) "Indexed after the retry completes"
        }
    ]

let tests =
    testList "Phase 220 — ingestion retry + status filter" [ retryEligibility; filtering; reCycle ]
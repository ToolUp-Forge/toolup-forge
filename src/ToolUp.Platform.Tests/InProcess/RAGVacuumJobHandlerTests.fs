module ToolUp.Platform.Tests.InProcess.RAGVacuumJobHandlerTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.RAGVacuumJobHandler
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 14w — tombstone auto-vacuum scheduler ─────────────────────
//
// Pins the acceptance criterion: a vacuum sweep over a scope drops the
// tombstone count to zero and emits a `KnowledgeVacuumCompleted` audit
// event carrying `(ScopeKey, ChunksRemoved, BytesReclaimed, Duration)`;
// a scope with no vacuum-eligible tombstones is left untouched and emits
// nothing. The handler ignores its `JobContext` (all state is captured
// in `RAGVacuumDeps` at compose time — portability rule 4), so the tests
// drive `Execute` with a null context.

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private chunk content : TextChunk = {
    Content = content
    Metadata = Map.empty
}

let tests =
    testList "Phase 14w — RAGVacuumJobHandler" [

        testCaseAsync "sweep purges tombstones past retention and emits KnowledgeVacuumCompleted"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            use vectorStore = new InMemoryVectorStore(storage, flushIntervalMs = 60000)
            let vs = vectorStore :> IVectorStore

            let eventStore =
                ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let scope = Team "t1"
            do! vs.Upsert scope "doc:chunk:0" [| 1.0f; 0.0f |] (chunk "alpha content")
            do! vs.Upsert scope "doc:chunk:1" [| 0.0f; 1.0f |] (chunk "beta content")

            // Tombstone one chunk; leave the other live.
            do! vs.DeleteChunk scope "doc:chunk:0"

            let deps: RAGVacuumDeps = {
                VectorStore = vs
                EventStore = eventStore
                // Zero retention → cutoff = now, so the just-written
                // tombstone (stamped moments ago) is immediately eligible.
                Retention = TimeSpan.Zero
                Logger = noopLogger
            }

            let handler = create deps
            let! result = handler.Execute(Unchecked.defaultof<JobContext>)
            Expect.equal result JobResult.Success "sweep completes cleanly"

            // Tombstone purged; the live chunk survives.
            let! remaining = vs.ListChunks scope true
            Expect.hasLength remaining 1 "only the tombstoned chunk is hard-removed"
            Expect.equal (fst remaining.[0]) "doc:chunk:1" "the live chunk is untouched"

            // Audit event lands in the team's scope with the reclaim outcome.
            let! events = eventStore.ReadByType("t1", "KnowledgeVacuumCompleted")
            Expect.hasLength events 1 "one KnowledgeVacuumCompleted per purged scope"
            Expect.stringContains events.[0].Payload "\"ChunksRemoved\":1" "records the purged count"
            Expect.stringContains events.[0].Payload "\"ScopeKey\":\"team:t1\"" "records the scope key"
            Expect.stringContains events.[0].Payload "BytesReclaimed" "carries a reclaimed-bytes figure"
        }

        testCaseAsync "sweep over a scope with no eligible tombstones purges nothing and stays silent"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            use vectorStore = new InMemoryVectorStore(storage, flushIntervalMs = 60000)
            let vs = vectorStore :> IVectorStore

            let eventStore =
                ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let scope = Team "t2"
            // A live chunk, never deleted — nothing to reclaim.
            do! vs.Upsert scope "doc:chunk:0" [| 1.0f; 0.0f |] (chunk "still here")

            let deps: RAGVacuumDeps = {
                VectorStore = vs
                EventStore = eventStore
                Retention = TimeSpan.Zero
                Logger = noopLogger
            }

            let handler = create deps
            let! result = handler.Execute(Unchecked.defaultof<JobContext>)
            Expect.equal result JobResult.Success "an empty sweep still succeeds"

            let! remaining = vs.ListChunks scope true
            Expect.hasLength remaining 1 "the live chunk is untouched"

            let! events = eventStore.ReadByType("t2", "KnowledgeVacuumCompleted")
            Expect.isEmpty events "no audit event when nothing was reclaimed"
        }
    ]
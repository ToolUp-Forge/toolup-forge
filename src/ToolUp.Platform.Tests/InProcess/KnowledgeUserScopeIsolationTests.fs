module ToolUp.Platform.Tests.InProcess.KnowledgeUserScopeIsolationTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.VectorKnowledgeTypes

// GAP-1 regression pack (Investigate-Gaps 2026-07-07): before the fix,
// every non-team caller (Individual mode, AuthenticatedEphemeral,
// anonymous session) had its KB chunks routed to the single shared
// `VectorScope.Deployment`, and retrieval admitted `Deployment` for every
// authenticated user — so user A's private uploads surfaced in user B's
// AI assistant. The fix gives each non-team caller its own `VectorScope.User`
// namespace and gates it in `RetrievalPipeline.authorisedScopes` on
// `AccessContext.UserId`. These tests pin that a `user-A` upload is NEVER
// retrievable by a `user-B` caller — the vector-layer twin of the
// per-container blob boundary (GP 4).

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

// A single axis-aligned unit vector. Both users' chunks are stored with it
// and the embedder always returns it, so semantic similarity is ~1 for
// every chunk in every scope. That deliberately removes similarity as a
// confounder: the ONLY thing that can keep user B's chunk out of user A's
// results is scope authorisation, which is exactly what GAP-1 broke.
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

let private chunk content : TextChunk = {
    Content = content
    Metadata = Map.empty
}

let private newStore () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-user-scope-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage

    let store =
        new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(
            storage,
            logger = SilentLogger(),
            flushIntervalMs = 60000
        )

    store :> IVectorStore, store :> IDisposable

let private newPipeline (store: IVectorStore) : IRetrievalPipeline =
    new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(store, ConstantEmbedder()) :> IRetrievalPipeline

let tests =
    testList "KnowledgeUserScopeIsolation" [

        testAsync "GAP-1: a user-A upload is not retrievable by a user-B caller (authorisation filter)" {
            let store, dispose = newStore ()

            try
                do! store.Upsert (User "alice") "alice-1" unitVec (chunk "alice private note")
                do! store.Upsert (User "bob") "bob-1" unitVec (chunk "bob private note")

                let pipeline = newPipeline store

                // Bob explicitly requests Alice's scope. authorisedScopes must
                // strip it — Bob's AccessContext.UserId is "bob", not "alice".
                let request =
                    RetrievalRequest.create "anything" [ User "alice"; User "bob" ] 10 Interleaved

                let bobCtx = AccessContext.unrestricted (AuthenticatedUser "bob")

                let! results = pipeline.Retrieve request bobCtx

                Expect.isFalse
                    (results |> List.exists (fun m -> m.ChunkId = "alice-1"))
                    "Alice's private chunk must never surface for a Bob caller"

                Expect.all
                    results
                    (fun m -> m.Scope = User "bob")
                    "every match Bob retrieves must come from Bob's own user scope"
            finally
                dispose.Dispose()
        }

        testAsync "a user retrieves ONLY their own chunks even when the other user's scope is in the request" {
            let store, dispose = newStore ()

            try
                do! store.Upsert (User "alice") "alice-1" unitVec (chunk "alice private note")
                do! store.Upsert (User "bob") "bob-1" unitVec (chunk "bob private note")

                let pipeline = newPipeline store

                let request =
                    RetrievalRequest.create "anything" [ User "alice"; User "bob" ] 10 Interleaved

                let aliceCtx = AccessContext.unrestricted (AuthenticatedUser "alice")

                let! results = pipeline.Retrieve request aliceCtx

                Expect.isNonEmpty results "Alice must be able to retrieve her own chunk (positive control)"

                Expect.all
                    results
                    (fun m -> m.Scope = User "alice")
                    "no result may originate from a scope other than Alice's own"

                Expect.isFalse
                    (results |> List.exists (fun m -> m.ChunkId = "bob-1"))
                    "Bob's chunk must not leak into Alice's retrieval"
            finally
                dispose.Dispose()
        }

        testAsync "requesting only another user's scope returns nothing (fail-closed)" {
            let store, dispose = newStore ()

            try
                do! store.Upsert (User "alice") "alice-1" unitVec (chunk "alice private note")

                let pipeline = newPipeline store

                let request = RetrievalRequest.create "anything" [ User "alice" ] 10 Interleaved
                let bobCtx = AccessContext.unrestricted (AuthenticatedUser "bob")

                let! results = pipeline.Retrieve request bobCtx

                Expect.isEmpty results "Bob requesting only Alice's user scope must retrieve nothing"
            finally
                dispose.Dispose()
        }

        testAsync "store-level: User scopes are isolated namespaces (like Team)" {
            let store, dispose = newStore ()

            try
                do! store.Upsert (User "alice") "alice-1" unitVec (chunk "alice")
                do! store.Upsert (User "bob") "bob-1" unitVec (chunk "bob")

                // Query Alice's scope with Bob's exact vector — per-scope
                // namespaces mean Alice's index never sees bob-1.
                let! results = store.Search [ User "alice" ] unitVec 10

                Expect.all results (fun m -> m.Scope = User "alice") "every match must come from Alice's user scope"

                Expect.isFalse
                    (results |> List.exists (fun m -> m.ChunkId = "bob-1"))
                    "Bob's chunk must not surface in an Alice-scope query"
            finally
                dispose.Dispose()
        }
    ]
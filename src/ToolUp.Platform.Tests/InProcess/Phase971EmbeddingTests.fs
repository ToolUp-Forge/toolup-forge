module ToolUp.Platform.Tests.InProcess.Phase971EmbeddingTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.DataObjectStoreTests

// ─── Phase 971 — Embedding: a refused scope-state delete is reported ──
//
// `ScopedLocalEmbeddingProviders.ResetScope` drops the scope's in-memory
// TF-IDF state and deletes its persisted snapshot. `IBlobStorage.Delete`
// answers `Ok` on a missing blob, so an `Error` is a refusal: the snapshot
// is still at rest and the next `For(scope)` rehydrates the vocabulary the
// reset was asked to wipe. A reset that returned normally over that claimed
// a wipe that did not happen.

let private stateBlob = "embeddings/team-a/_local-tfidf-state.json"

let private feed (family: LocalEmbeddingProvider.ScopedLocalEmbeddingProviders) (scope: VectorScope) =
    let provider = family.For scope

    for text in [ "northern division revenue grew"; "southern division costs fell" ] do
        provider.GenerateEmbedding text |> Async.RunSynchronously |> ignore

let tests =
    testList "Phase 971 - Embedding" [
        testAsync "ResetScope raises when the scope's persisted state delete is refused, and the state is kept" {
            let inner = InMemoryBlobStorage()

            let storage =
                DeleteRefusingBlobStorage(inner, fun n -> n.EndsWith "_local-tfidf-state.json") :> IBlobStorage

            let family = LocalEmbeddingProvider.createScopedPersistent storage
            feed family (VectorScope.Team "a")

            match! Async.Catch(family.ResetScope(VectorScope.Team "a")) with
            | Choice1Of2() -> failtest "a refused state delete must not read as a completed reset"
            | Choice2Of2 _ -> ()

            let! names = (inner :> IBlobStorage).List("_platform", "embeddings/")
            Expect.contains names stateBlob "the refused snapshot is still at rest, so a re-run can delete it"
        }

        testAsync "ResetScope over healthy storage completes and removes the persisted state" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let family = LocalEmbeddingProvider.createScopedPersistent storage
            feed family (VectorScope.Team "a")

            do! family.ResetScope(VectorScope.Team "a")

            let! names = storage.List("_platform", "embeddings/")
            Expect.isFalse (names |> List.contains stateBlob) "the reset scope's snapshot is gone"
        }
    ]
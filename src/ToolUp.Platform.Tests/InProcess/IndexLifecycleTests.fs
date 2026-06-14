module ToolUp.Platform.Tests.InProcess.IndexLifecycleTests

open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IEmbeddingCache
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IIndexLifecycle
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.RAG.InMemoryEmbeddingCache

// ─── Phase 115 — IIndexLifecycle deletion / erasure contract pack ──────
//
// Pins the end-to-end guarantee the unified index-lifecycle seam exists
// to make structural: a delete or data-subject erasure that reaches the
// dense vector store ALSO reaches the sparse (BM25) leg and the embedding
// cache, immediately and after a process restart, with the deleted text
// gone from the persisted `bm25.json` at rest. Before Phase 115 every KB
// deletion path looped `IVectorStore.DeleteChunk` only, so deleted /
// erased content kept surfacing through the hybrid sparse leg into AI
// answers and survived at rest in blob storage.
//
// "Restart simulation" = dispose the in-memory stores (their `IDisposable`
// performs a final synchronous flush of the dirty set) and construct fresh
// instances over the SAME `InMemoryBlobStorage`. `flushIntervalMs = 60000`
// keeps the background flush loop out of the way so persistence is driven
// deterministically by disposal — the same idiom the precedent
// `InMemoryBM25IndexTests` uses.

let private deploymentBm25 = "_rag/deployment/bm25.json"
let private deploymentVectorIndex = "_rag/deployment/index.json"

let private chunk content : TextChunk = {
    Content = content
    Metadata = Map.empty
}

[<Tests>]
let tests =
    testList "IIndexLifecycle deletion / erasure fan-out" [

        testAsync "DeleteDocument: deleted document misses both legs and is gone from bm25.json after restart" {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            // ── First boot: ingest, delete one document, verify both legs miss ──
            do
                use vectorStore = new InMemoryVectorStore(storage, flushIntervalMs = 60000)
                use bm25 = new InMemoryBM25Index(storage, flushIntervalMs = 60000)
                let vs = vectorStore :> IVectorStore
                let sparse = bm25 :> ISparseIndex
                let lifecycle = DefaultIndexLifecycle(vs, Some sparse, None) :> IIndexLifecycle

                // `doc-del` has two chunks (platform id convention
                // `{docId}:chunk:{i}`); `doc-keep` is the bystander that
                // must survive the delete.
                vs.Upsert Deployment "doc-del:chunk:0" [| 1.0f; 0.0f; 0.0f |] (chunk "alpha apple")
                |> Async.RunSynchronously

                vs.Upsert Deployment "doc-del:chunk:1" [| 0.0f; 1.0f; 0.0f |] (chunk "beta banana")
                |> Async.RunSynchronously

                vs.Upsert Deployment "doc-keep:chunk:0" [| 0.0f; 0.0f; 1.0f |] (chunk "gamma grape")
                |> Async.RunSynchronously

                sparse.Upsert Deployment "doc-del:chunk:0" (chunk "alpha apple")
                |> Async.RunSynchronously

                sparse.Upsert Deployment "doc-del:chunk:1" (chunk "beta banana")
                |> Async.RunSynchronously

                sparse.Upsert Deployment "doc-keep:chunk:0" (chunk "gamma grape")
                |> Async.RunSynchronously

                // Pre-delete: both legs retrieve the doomed chunk.
                let denseHit =
                    vs.Search [ Deployment ] [| 1.0f; 0.0f; 0.0f |] 10 |> Async.RunSynchronously

                Expect.contains
                    (denseHit |> List.map _.ChunkId)
                    "doc-del:chunk:0"
                    "dense leg must retrieve the chunk before deletion"

                let sparseHit = sparse.Search [ Deployment ] "apple" 10 |> Async.RunSynchronously

                Expect.contains
                    (sparseHit |> List.map _.ChunkId)
                    "doc-del:chunk:0"
                    "sparse leg must retrieve the chunk before deletion"

                // Delete the whole document through the lifecycle seam.
                let report =
                    lifecycle.DeleteDocument Deployment "doc-del" 2 |> Async.RunSynchronously

                Expect.isTrue
                    (IndexLifecycleReport.isClean report)
                    (sprintf "lifecycle delete must complete cleanly: %s" (IndexLifecycleReport.summarise report))

                // Immediate: both legs miss every deleted chunk; bystander survives.
                let denseMiss0 =
                    vs.Search [ Deployment ] [| 1.0f; 0.0f; 0.0f |] 10 |> Async.RunSynchronously

                Expect.isFalse
                    (denseMiss0 |> List.exists (fun m -> m.ChunkId = "doc-del:chunk:0"))
                    "dense leg must not return the deleted chunk (tombstone filter)"

                let denseMiss1 =
                    vs.Search [ Deployment ] [| 0.0f; 1.0f; 0.0f |] 10 |> Async.RunSynchronously

                Expect.isFalse
                    (denseMiss1 |> List.exists (fun m -> m.ChunkId = "doc-del:chunk:1"))
                    "dense leg must not return the second deleted chunk"

                let sparseMiss =
                    sparse.Search [ Deployment ] "apple banana" 10 |> Async.RunSynchronously

                Expect.isEmpty
                    (sparseMiss |> List.filter (fun m -> m.ChunkId.StartsWith "doc-del"))
                    "sparse leg must not return any deleted chunk"

                let keepHit = sparse.Search [ Deployment ] "grape" 10 |> Async.RunSynchronously

                Expect.contains
                    (keepHit |> List.map _.ChunkId)
                    "doc-keep:chunk:0"
                    "bystander document must remain retrievable"
            // disposal here → final flush persists vector tombstone + bm25 minus deleted text

            // ── At rest: deleted text gone from bm25.json; bystander survives ──
            let! atRest = storage.Download("_rag", deploymentBm25)

            match atRest with
            | Error e -> failtest (sprintf "bm25.json must persist after the first boot: %s" e)
            | Ok bytes ->
                let json = Encoding.UTF8.GetString bytes

                Expect.isFalse (json.Contains "apple") "deleted chunk text must not survive at rest in bm25.json"
                Expect.isFalse (json.Contains "banana") "deleted chunk text must not survive at rest in bm25.json"
                Expect.stringContains json "grape" "bystander text must survive at rest in bm25.json"

            // ── Restart: fresh stores over the same storage still miss the doc ──
            use vectorStore2 = new InMemoryVectorStore(storage, flushIntervalMs = 60000)
            use bm252 = new InMemoryBM25Index(storage, flushIntervalMs = 60000)
            let vs2 = vectorStore2 :> IVectorStore
            let sparse2 = bm252 :> ISparseIndex

            let! denseAfter = vs2.Search [ Deployment ] [| 1.0f; 0.0f; 0.0f |] 10

            Expect.isFalse
                (denseAfter |> List.exists (fun m -> m.ChunkId = "doc-del:chunk:0"))
                "dense leg must still miss the deleted chunk after restart (tombstone persisted)"

            let! sparseAfter = sparse2.Search [ Deployment ] "apple banana" 10

            Expect.isEmpty
                (sparseAfter |> List.filter (fun m -> m.ChunkId.StartsWith "doc-del"))
                "sparse leg must still miss the deleted chunk after restart"

            let! keepAfter = sparse2.Search [ Deployment ] "grape" 10

            Expect.contains (keepAfter |> List.map _.ChunkId) "doc-keep:chunk:0" "bystander must survive restart"
        }

        testAsync "DeleteByScope: both legs wiped and the stale bm25.json snapshot is deleted at rest" {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            // ── Boot 1: ingest and persist a real bm25.json snapshot ──
            do
                use vectorStore = new InMemoryVectorStore(storage, flushIntervalMs = 60000)
                use bm25 = new InMemoryBM25Index(storage, flushIntervalMs = 60000)
                let vs = vectorStore :> IVectorStore
                let sparse = bm25 :> ISparseIndex

                vs.Upsert Deployment "a:chunk:0" [| 1.0f; 0.0f |] (chunk "alpha apple")
                |> Async.RunSynchronously

                vs.Upsert Deployment "b:chunk:0" [| 0.0f; 1.0f |] (chunk "beta banana")
                |> Async.RunSynchronously

                sparse.Upsert Deployment "a:chunk:0" (chunk "alpha apple")
                |> Async.RunSynchronously

                sparse.Upsert Deployment "b:chunk:0" (chunk "beta banana")
                |> Async.RunSynchronously

            // The snapshot must genuinely exist before the delete — otherwise
            // the test would pass vacuously even with the pre-115 short-circuit
            // bug present (which only fails to DELETE an existing snapshot).
            let! preDelete = storage.Download("_rag", deploymentBm25)
            Expect.isTrue (preDelete |> Result.isOk) "bm25.json must exist at rest after the ingest boot"

            // ── Boot 2: load the snapshot, DeleteByScope, verify in-memory ──
            do
                use vectorStore = new InMemoryVectorStore(storage, flushIntervalMs = 60000)
                use bm25 = new InMemoryBM25Index(storage, flushIntervalMs = 60000)
                let vs = vectorStore :> IVectorStore
                let sparse = bm25 :> ISparseIndex
                let lifecycle = DefaultIndexLifecycle(vs, Some sparse, None) :> IIndexLifecycle

                let report = lifecycle.DeleteByScope Deployment |> Async.RunSynchronously
                Expect.isTrue (IndexLifecycleReport.isClean report) "DeleteByScope must complete cleanly"

                let dense = vs.Search [ Deployment ] [| 1.0f; 0.0f |] 10 |> Async.RunSynchronously
                Expect.isEmpty dense "dense leg must be empty immediately after DeleteByScope"

                let sparseR =
                    sparse.Search [ Deployment ] "apple banana" 10 |> Async.RunSynchronously

                Expect.isEmpty sparseR "sparse leg must be empty immediately after DeleteByScope"
            // disposal → bm25 persistScope sees the scope removed and DELETES the snapshot

            // ── At rest: the stale bm25.json is gone (no resurrecting corpus) ──
            let! bm25AtRest = storage.Download("_rag", deploymentBm25)

            Expect.isTrue
                (match bm25AtRest with
                 | Error _ -> true
                 | Ok _ -> false)
                "bm25.json must be deleted at rest after DeleteByScope — the deleted corpus must not resurrect on restart"

            // ── Restart: both legs stay empty ──
            use vectorStore2 = new InMemoryVectorStore(storage, flushIntervalMs = 60000)
            use bm252 = new InMemoryBM25Index(storage, flushIntervalMs = 60000)

            let! denseAfter = (vectorStore2 :> IVectorStore).Search [ Deployment ] [| 1.0f; 0.0f |] 10
            Expect.isEmpty denseAfter "dense leg must remain empty after restart"

            let! sparseAfter = (bm252 :> ISparseIndex).Search [ Deployment ] "apple banana" 10
            Expect.isEmpty sparseAfter "sparse leg must remain empty after restart"
        }

        testAsync "Erase: subject content removed from vector + sparse + cache and all persisted snapshots" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let subject = "user-42"
            let secretText = sprintf "secret note about %s please redact" subject

            // ── Boot 1: ingest a subject-naming chunk + a bystander; persist ──
            do
                use vectorStore = new InMemoryVectorStore(storage, flushIntervalMs = 60000)
                use bm25 = new InMemoryBM25Index(storage, flushIntervalMs = 60000)
                let vs = vectorStore :> IVectorStore
                let sparse = bm25 :> ISparseIndex

                vs.Upsert Deployment "c1:chunk:0" [| 1.0f; 0.0f |] (chunk secretText)
                |> Async.RunSynchronously

                vs.Upsert Deployment "c2:chunk:0" [| 0.0f; 1.0f |] (chunk "innocent unrelated text")
                |> Async.RunSynchronously

                sparse.Upsert Deployment "c1:chunk:0" (chunk secretText)
                |> Async.RunSynchronously

                sparse.Upsert Deployment "c2:chunk:0" (chunk "innocent unrelated text")
                |> Async.RunSynchronously

            // ── Boot 2: dry-run then real erase through the lifecycle ──
            do
                use vectorStore = new InMemoryVectorStore(storage, flushIntervalMs = 60000)
                use bm25 = new InMemoryBM25Index(storage, flushIntervalMs = 60000)
                let vs = vectorStore :> IVectorStore
                let sparse = bm25 :> ISparseIndex
                let cache = InMemoryEmbeddingCache() :> IEmbeddingCache

                let lifecycle =
                    DefaultIndexLifecycle(vs, Some sparse, Some cache) :> IIndexLifecycle

                // Seed the cache; the erase must flush it (hash-keyed → full flush).
                let key = {
                    Version = {
                        ProviderId = "test"
                        ModelId = "m"
                        Dimensions = 2
                    }
                    TextHash = "deadbeef"
                }

                cache.Set key [| 0.5f; 0.5f |] |> Async.RunSynchronously
                let cachedBefore = cache.TryGet key |> Async.RunSynchronously
                Expect.isSome cachedBefore "cache entry must be present before erase"

                // Dry-run: counts but mutates nothing — not the indexes, not the cache.
                let dryRun =
                    lifecycle.Erase(Deployment, subject, ErasurePolicy.HardDelete, true)
                    |> Async.RunSynchronously

                match dryRun with
                | Error e -> failtest (sprintf "dry-run erase must succeed: %s" (ErasureError.toMessage e))
                | Ok s -> Expect.isTrue (s.RecordsAffected > 0) "dry-run must report the matched count"

                let cachedAfterDry = cache.TryGet key |> Async.RunSynchronously
                Expect.isSome cachedAfterDry "dry-run must not flush the cache"

                let sparseStillThere =
                    sparse.Search [ Deployment ] "secret" 10 |> Async.RunSynchronously

                Expect.contains
                    (sparseStillThere |> List.map _.ChunkId)
                    "c1:chunk:0"
                    "dry-run must not delete the subject's chunk"

                // Real erase (HardDelete: physical purge on the vector store).
                let erased =
                    lifecycle.Erase(Deployment, subject, ErasurePolicy.HardDelete, false)
                    |> Async.RunSynchronously

                match erased with
                | Error e -> failtest (sprintf "erase must succeed: %s" (ErasureError.toMessage e))
                | Ok _ -> ()

                // Immediate: subject content gone from both legs; cache flushed; bystander survives.
                let sparseGone = sparse.Search [ Deployment ] "secret" 10 |> Async.RunSynchronously
                Expect.isEmpty sparseGone "sparse leg must no longer serve the subject's chunk"

                let sparseKeep =
                    sparse.Search [ Deployment ] "innocent" 10 |> Async.RunSynchronously

                Expect.contains (sparseKeep |> List.map _.ChunkId) "c2:chunk:0" "bystander chunk must survive erase"

                // includeDeleted = true: HardDelete must PHYSICALLY purge, not tombstone.
                let vectorChunks = vs.ListChunks Deployment true |> Async.RunSynchronously

                Expect.isFalse
                    (vectorChunks |> List.exists (fun (cid, _) -> cid = "c1:chunk:0"))
                    "HardDelete must physically purge the subject's vector chunk"

                Expect.isTrue
                    (vectorChunks |> List.exists (fun (cid, _) -> cid = "c2:chunk:0"))
                    "bystander vector chunk must survive"

                let cachedAfter = cache.TryGet key |> Async.RunSynchronously
                Expect.isNone cachedAfter "erase must flush the embedding cache"
            // disposal → post-erase snapshots flushed to blob storage

            // ── At rest: subject text gone from both persisted snapshots ──
            let! vIndex = storage.Download("_rag", deploymentVectorIndex)

            match vIndex with
            | Error e -> failtest (sprintf "vector index.json must persist after erase: %s" e)
            | Ok bytes ->
                Expect.isFalse
                    ((Encoding.UTF8.GetString bytes).Contains subject)
                    "vector index.json must not contain the subject at rest after erase"

            let! bm = storage.Download("_rag", deploymentBm25)

            match bm with
            | Error _ -> () // an empty / absent snapshot is also acceptable
            | Ok bytes ->
                Expect.isFalse
                    ((Encoding.UTF8.GetString bytes).Contains subject)
                    "bm25.json must not contain the subject at rest after erase"
        }
    ]
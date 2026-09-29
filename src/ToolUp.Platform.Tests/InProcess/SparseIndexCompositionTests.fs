module ToolUp.Platform.Tests.InProcess.SparseIndexCompositionTests

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.RAGCompose

// ─── Phase 893 — the keyword index is a composition choice ───────────
//
// `composeRAG` used to construct `InMemoryBM25Index` unconditionally, so a
// deployment that had moved its dense store to the database still kept its
// keyword index in one process. What is pinned here:
//
//  • unset is the pre-893 composition — the in-process index, resolved from
//    DI exactly as before (the byte-identical pin, GP 11);
//  • `withSparseIndex` registers the supplied index verbatim, and refuses
//    at composition when an analyzer is composed beside it (the analyzer
//    would be silently ignored);
//  • `withAnalyzedSparseIndex` builds the index for the COMPOSED analyzer —
//    the seam an index that tokenises in its own engine maps (or refuses)
//    the analyzer through;
//  • `withoutSparseIndex` registers no keyword index and retrieval is the
//    dense list, unfused;
//  • the replica validator fires for an in-process store at two replicas,
//    is silent at one, and is lifted by the store that removes its premise.

let private unitVec: float32 array =
    Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

let private constantEmbedder =
    { new IEmbeddingProvider with
        member _.GenerateEmbedding _ = async { return unitVec }

        member _.GenerateEmbeddings texts = async { return texts |> Seq.map (fun _ -> unitVec) |> Seq.toArray }

        member _.ProviderId = "stub"
        member _.ModelId = "constant"
        member _.Dimensions = 8
    }

let private stubFactory =
    { new ToolUp.AI.IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None

        member _.Resolve _ = async { return Error ToolUp.AI.NoProviderConfigured }

        member _.TryResolveByLabel(_, _) = async { return Error ToolUp.AI.NoProviderConfigured }

        member _.BuildPlatform(_, _, _) = None
    }

let private stubProfile =
    { new ToolUp.Platform.Providers.IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }
    }

/// A keyword index that is NOT the in-process one — structurally what the
/// database companion looks like to the composition.
let private externalIndex () =
    { new ISparseIndex with
        member _.Upsert _ _ _ = async { return () }
        member _.Search _ _ _ = async { return [] }
        member _.DeleteByScope _ = async { return () }
        member _.DeleteChunk _ _ = async { return () }

        member _.Erase(_, _, _, _) = async {
            return
                Ok {
                    HandlerName = "sparse-index"
                    RecordsAffected = 0
                    Note = None
                }
        }
    }

/// A vector store that is NOT one of the in-process ones.
let private externalVectorStore () =
    let inner =
        new InMemoryVectorStore(InMemoryBlobStorage() :> BlobStorage.IBlobStorage, flushIntervalMs = 60000)
        :> IVectorStore

    { new IVectorStore with
        member _.Upsert s c v t = inner.Upsert s c v t
        member _.Search s q k = inner.Search s q k
        member _.ListChunks s d = inner.ListChunks s d
        member _.DeleteChunk s c = inner.DeleteChunk s c
        member _.RestoreChunk s c = inner.RestoreChunk s c
        member _.Vacuum s r = inner.Vacuum s r
        member _.DeleteByScope s = inner.DeleteByScope s
        member _.ListScopes() = inner.ListScopes()
        member _.Erase(s, u, p, d) = inner.Erase(s, u, p, d)
    }

let private newApp () =
    RAGServerApp.create stubFactory stubProfile constantEmbedder
    |> RAGServerApp.withStorage (InMemoryBlobStorage() :> BlobStorage.IBlobStorage)

let private withReplicas (count: int) (app: RAGServerApp) : RAGServerApp = {
    app with
        AI.Base.Config.ReplicaCount = count
}

let private provider (app: RAGServerApp) : ServiceProvider =
    let composed = composeRAG app
    let services = ServiceCollection() :> IServiceCollection

    let services =
        match composed.Extensions.ServiceConfig with
        | Some f -> f services
        | None -> services

    services.BuildServiceProvider()

let private resolvedSparse (app: RAGServerApp) : ISparseIndex option =
    use sp = provider app

    match sp.GetService typeof<ISparseIndex> with
    | null -> None
    | o -> Some(o :?> ISparseIndex)

/// Run the replica validator exactly as `composeRAG` registered it.
let private replicaVerdict (app: RAGServerApp) : ConfigValidation.ValidationResult =
    let composed = composeRAG app

    let v =
        composed.ConfigValidators
        |> List.find (fun v -> v.Name = "rag-in-process-index-replicas")

    v.Validate() |> Async.RunSynchronously

let private warningText (result: ConfigValidation.ValidationResult) =
    match result with
    | ConfigValidation.ValidationResult.Warning message -> message
    | other -> failtestf "expected a Warning, got %A" other

let tests =
    testList "Phase 893 — the keyword index is a composition choice" [

        testList "withSparseIndex / withAnalyzedSparseIndex / withoutSparseIndex" [
            test "unset ⇒ the composition still builds InMemoryBM25Index (GP 11)" {
                let app = newApp ()

                match app.SparseIndex with
                | SparseIndexComposition.InProcessSparseIndex -> ()
                | other -> failtestf "the default must be in-process, got %A" other

                match resolvedSparse app with
                | Some index -> Expect.isTrue (index :? InMemoryBM25Index) "the pre-893 default, unchanged"
                | None -> failtest "the default composition must register an ISparseIndex"
            }

            test "withSparseIndex registers the supplied index verbatim" {
                let supplied = externalIndex ()
                let app = newApp () |> RAGServerApp.withSparseIndex supplied

                match resolvedSparse app with
                | Some index -> Expect.isTrue (obj.ReferenceEquals(index, supplied)) "the composed instance"
                | None -> failtest "the supplied index must be registered"
            }

            test "withSparseIndex beside withSparseAnalyzer is refused at composition, naming the analyzer" {
                let analyzer =
                    ToolUp.RAG.SparseAnalysis.create "custom-analyzer-7" ToolUp.RAG.SparseAnalysis.tokeniseWords

                let app =
                    newApp ()
                    |> RAGServerApp.withSparseAnalyzer analyzer
                    |> RAGServerApp.withSparseIndex (externalIndex ())

                let ex = Expect.throwsC (fun () -> composeRAG app |> ignore) id
                Expect.stringContains ex.Message "custom-analyzer-7" "the refusal names the analyzer"
                Expect.stringContains ex.Message "withAnalyzedSparseIndex" "and the builder that serves the case"
            }

            test "withAnalyzedSparseIndex builds the index for the composed analyzer, at composition" {
                let analyzer =
                    ToolUp.RAG.SparseAnalysis.create "custom-analyzer-9" ToolUp.RAG.SparseAnalysis.tokeniseWords

                let mutable seen: string list = []
                let supplied = externalIndex ()

                let app =
                    newApp ()
                    |> RAGServerApp.withSparseAnalyzer analyzer
                    |> RAGServerApp.withAnalyzedSparseIndex (fun a ->
                        seen <- seen @ [ a.Id ]
                        supplied)

                composeRAG app |> ignore
                Expect.equal seen [ "custom-analyzer-9" ] "built once, at composition, for the composed analyzer"

                match resolvedSparse app with
                | Some index -> Expect.isTrue (obj.ReferenceEquals(index, supplied)) "the built index is registered"
                | None -> failtest "the built index must be registered"
            }

            test "withAnalyzedSparseIndex with no analyzer composed receives the identity analyzer" {
                let mutable seen = ""

                let app =
                    newApp ()
                    |> RAGServerApp.withAnalyzedSparseIndex (fun a ->
                        seen <- a.Id
                        externalIndex ())

                composeRAG app |> ignore
                Expect.equal seen ToolUp.RAG.SparseAnalysis.IdentityAnalyzerId "the identity analyzer"
            }

            test "a refusal inside the builder surfaces at composition" {
                let app =
                    newApp ()
                    |> RAGServerApp.withAnalyzedSparseIndex (fun a ->
                        invalidOp (sprintf "no configuration for analyzer '%s'" a.Id))

                let ex = Expect.throwsC (fun () -> composeRAG app |> ignore) id
                Expect.stringContains ex.Message "identity" "the builder's refusal, naming the analyzer"
            }

            test "withoutSparseIndex registers no keyword index" {
                let app = newApp () |> RAGServerApp.withoutSparseIndex
                Expect.isNone (resolvedSparse app) "dense only: nothing registered"
            }

            testAsync "withoutSparseIndex retrieves the dense list, unfused" {
                let app = newApp () |> RAGServerApp.withoutSparseIndex
                use sp = provider app
                let store = sp.GetRequiredService<IVectorStore>()
                let pipeline = sp.GetRequiredService<IRetrievalPipeline>()
                let scope = Team "t1"

                for chunkId in [ "c3"; "c1"; "c2" ] do
                    do!
                        store.Upsert scope chunkId unitVec {
                            Content = "note " + chunkId
                            Metadata = Map.empty
                        }

                let! dense = store.Search [ scope ] unitVec 10

                let! retrieved =
                    pipeline.Retrieve
                        (RetrievalRequest.create "note" [ scope ] 10 Interleaved)
                        (AccessContext.unrestricted (TeamMember("u1", "t1")))

                Expect.equal
                    (retrieved |> List.map _.ChunkId)
                    (dense |> List.map _.ChunkId)
                    "fusion over one list is that list"

                Expect.equal
                    (retrieved |> List.map _.Score)
                    (dense |> List.map _.Score)
                    "with the dense scores, not rank-fusion scores"
            }
        ]

        testList "InProcessIndexReplicaValidator" [
            test "fires at two replicas over the in-process stores, naming both and their replacements" {
                let message = newApp () |> withReplicas 2 |> replicaVerdict |> warningText
                Expect.stringContains message "InMemoryVectorStore" "names the vector store"
                Expect.stringContains message "ToolUp.VectorStores.Pgvector" "and the companion replacing it"
                Expect.stringContains message "InMemoryBM25Index" "names the keyword index"
                Expect.stringContains message "ToolUp.SparseIndices.Postgres" "and the companion replacing it"
            }

            test "is silent at one replica" {
                Expect.equal
                    (newApp () |> withReplicas 1 |> replicaVerdict)
                    ConfigValidation.ValidationResult.Ok
                    "one replica has nothing to diverge"
            }

            test "is lifted when both composed stores span replicas" {
                let verdict =
                    newApp ()
                    |> RAGServerApp.withVectorStore (externalVectorStore ())
                    |> RAGServerApp.withSparseIndex (externalIndex ())
                    |> withReplicas 3
                    |> replicaVerdict

                Expect.equal verdict ConfigValidation.ValidationResult.Ok "the premise no longer holds"
            }

            test "names only the store that is still in-process" {
                let message =
                    newApp ()
                    |> RAGServerApp.withVectorStore (externalVectorStore ())
                    |> withReplicas 2
                    |> replicaVerdict
                    |> warningText

                Expect.isFalse (message.Contains "InMemoryVectorStore") "the external vector store is not named"
                Expect.stringContains message "InMemoryBM25Index" "the in-process keyword index still is"
            }

            test "a hand-composed in-process index still warns — the lift keys on the instance, not the builder" {
                let handComposed =
                    new InMemoryBM25Index(InMemoryBlobStorage() :> BlobStorage.IBlobStorage, flushIntervalMs = 60000)

                let message =
                    newApp ()
                    |> RAGServerApp.withVectorStore (externalVectorStore ())
                    |> RAGServerApp.withSparseIndex handComposed
                    |> withReplicas 2
                    |> replicaVerdict
                    |> warningText

                Expect.stringContains message "InMemoryBM25Index" "still per-process"
            }

            test "dense-only at two replicas warns about the vector store alone" {
                let message =
                    newApp ()
                    |> RAGServerApp.withoutSparseIndex
                    |> withReplicas 2
                    |> replicaVerdict
                    |> warningText

                Expect.stringContains message "InMemoryVectorStore" "the in-process vector store"
                Expect.isFalse (message.Contains "InMemoryBM25Index") "no keyword index is composed"
            }
        ]
    ]
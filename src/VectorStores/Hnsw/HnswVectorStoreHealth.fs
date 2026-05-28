module ToolUp.RAG.VectorStores.Hnsw.Health

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform
open ToolUp.Platform.IVectorStore

// ─── Phase 9k HNSW vector store health probe ─────────────────────────
//
// Calls `IVectorStore.ListScopes ()` against the registered store. The
// HNSW store keeps its index in-memory (persisted asynchronously to
// blob storage), so this is a cheap round-trip that proves the index
// is alive. Failure modes the probe catches: load-time exception
// (corrupted persistence file, missing blob storage at startup),
// in-memory state lost to host shutdown.
//
// Usage from a deployment that wires HNSW:
//   let vectorStore = HnswVectorStore.create blobStorage (Some logger)
//   ...
//   |> RAGServerApp.withHealthCheck (HnswVectorStoreHealth.create vectorStore)

type HnswVectorStoreHealthCheck(store: IVectorStore) =
    interface IHealthCheck with
        member _.Name = "vector_store:hnsw"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 1.0

        member _.Check() = async {
            try
                let! _ = store.ListScopes()
                return Healthy
            with ex ->
                return Unhealthy ex.Message
        }

let create (store: IVectorStore) : IHealthCheck =
    HnswVectorStoreHealthCheck(store) :> IHealthCheck
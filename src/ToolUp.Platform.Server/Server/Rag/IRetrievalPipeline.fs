module ToolUp.Platform.IRetrievalPipeline

open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

/// High-level facade for the RAG retrieval pipeline. Sits above `IVectorStore`
/// and `IEmbeddingProvider`, handling embedding generation, scope-access
/// validation, and result merging. This is the interface that `SystemPromptBuilder`
/// implementations and modules use — they never call `IVectorStore` directly.
///
/// The pipeline enforces team isolation: a request carrying a `Team` scope
/// is validated against `AccessContext.TeamId` before the query reaches the
/// vector store. A mismatch returns an empty result rather than an error, so
/// callers do not need to handle auth failures from retrieval inline.
type IRetrievalPipeline =
    /// Query the vector store(s) with a natural-language string. The pipeline
    /// embeds the query, validates scope access, searches `IVectorStore`, and
    /// merges results according to `request.Merge`. Returns an empty list when
    /// no relevant chunks are found or when the caller lacks access to all
    /// requested scopes.
    abstract Retrieve: request: RetrievalRequest -> context: AccessContext -> Async<VectorMatch list>

    /// Embed a chunk and store it at `scope`. Used by ingestion code
    /// (e.g. `IngestionBackgroundService`, module post-save hooks) to add
    /// content without constructing the embedding externally.
    abstract Index: chunkId: string -> chunk: TextChunk -> scope: VectorScope -> Async<unit>

    /// Remove all indexed content for the given scope. Called when a document
    /// is deleted from the KnowledgeBase or a team is removed.
    abstract DeleteByScope: scope: VectorScope -> Async<unit>
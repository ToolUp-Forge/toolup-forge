module ToolUp.Platform.IEmbeddingProvider

/// Identity tuple for an embedding model. Stamped onto every chunk's
/// metadata at upsert time (`_embedProvider` / `_embedModel` / `_embedDim`)
/// so the `ReembeddingService` can detect chunks that no longer match the
/// active provider — e.g. after a model swap from `text-embedding-3-small`
/// to `text-embedding-3-large` — and re-embed them in the background.
/// Also forms part of the `IEmbeddingCache` key so two providers that
/// happen to share a `Dimensions` value never collide in the cache.
type EmbeddingVersion = {
    ProviderId: string
    ModelId: string
    Dimensions: int
}

module EmbeddingVersion =
    /// `_embedProvider` chunk-metadata key.
    [<Literal>]
    let MetadataProviderKey = "_embedProvider"

    /// `_embedModel` chunk-metadata key.
    [<Literal>]
    let MetadataModelKey = "_embedModel"

    /// `_embedDim` chunk-metadata key.
    [<Literal>]
    let MetadataDimensionsKey = "_embedDim"

/// Converts text into a dense float vector suitable for semantic similarity
/// search. Implementations are companion packages under `src/EmbeddingProviders/`;
/// the interface carries no provider-specific types or dependencies.
///
/// Satisfies Phase 9c portability rule 2 (async at every boundary) and
/// rule 4 (stateless per call — no in-memory state assumed between invocations).
type IEmbeddingProvider =
    /// Generate a dense embedding for the given text. The returned array has
    /// length `Dimensions`. Callers must not assume any particular normalisation;
    /// `IVectorStore` and `IRetrievalPipeline` implementations are responsible
    /// for normalising before cosine similarity if required.
    abstract GenerateEmbedding: text: string -> Async<float32 array>
    /// Generate dense embeddings for a sequence of texts in one call. The
    /// returned array is the same length and order as the input sequence;
    /// each inner array has length `Dimensions`. Implementations with native
    /// batch APIs (OpenAI, Cohere, Voyage, etc.) should issue a single HTTP
    /// call to amortise round-trip latency over N inputs — a 100-chunk
    /// document goes from ~15s of sequential HTTP calls to ~150ms of one
    /// batched call. Implementations without a batch path can delegate to
    /// `IEmbeddingProvider.batchedFallback` which fans out to N parallel
    /// `GenerateEmbedding` calls. Empty input yields an empty array.
    abstract GenerateEmbeddings: texts: string seq -> Async<float32 array array>
    /// The dimensionality of vectors produced by this provider. Must be constant
    /// for the lifetime of the provider instance. `IVectorStore` implementations
    /// may use this at initialisation to size index structures correctly.
    abstract Dimensions: int
    /// Stable identifier for the provider family — `"local"`, `"openai"`,
    /// `"cohere"`, etc. Combined with `ModelId` and `Dimensions` to form
    /// the `EmbeddingVersion` stamp; must not change for the lifetime of the
    /// instance, and should not vary across processes for the same provider.
    abstract ProviderId: string
    /// Model identifier within the provider — `"text-embedding-3-small"`,
    /// `"local-tfidf-v1"`, etc. Distinct values produce distinct cache keys
    /// and trigger background re-embedding when changed.
    abstract ModelId: string

/// Helper for `IEmbeddingProvider` implementations that don't have a native
/// batch endpoint. Fans the input sequence out to parallel single-text calls
/// and assembles the results in input order. Pass the provider's own
/// `GenerateEmbedding` member as `single` from inside its `GenerateEmbeddings`
/// implementation — the fan-out is bounded by the underlying async scheduler.
let batchedFallback (single: string -> Async<float32 array>) (texts: string seq) = async {
    let arr = texts |> Seq.toArray

    if arr.Length = 0 then
        return Array.empty
    else
        let! results = arr |> Array.map single |> Async.Parallel
        return results
}
module ToolUp.RAG.CachingEmbeddingProvider

open System.Security.Cryptography
open System.Text
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IEmbeddingCache

/// SHA256 the input text and return a lowercase hex digest. Used as the
/// `TextHash` portion of `EmbeddingCacheKey` so the cache never retains
/// raw user-supplied text.
let private sha256Hex (text: string) =
    use sha = SHA256.Create()
    let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes text)
    let sb = StringBuilder(bytes.Length * 2)

    for b in bytes do
        sb.AppendFormat("{0:x2}", b) |> ignore

    sb.ToString()

/// Decorator that wraps an existing `IEmbeddingProvider` with an
/// `IEmbeddingCache` lookup. On `GenerateEmbedding`:
///
/// 1. Compute the cache key from the wrapped provider's identity
///    (`ProviderId` / `ModelId` / `Dimensions`) and a SHA256 of the text.
/// 2. Probe the cache. On hit, return the cached vector immediately.
/// 3. On miss, delegate to the underlying provider, store the result, and
///    return it.
///
/// The decorator preserves the wrapped provider's identity surface
/// (`ProviderId` / `ModelId` / `Dimensions`) verbatim — downstream code
/// (`RetrievalPipeline`, `ReembeddingService`) should be unable to tell the
/// difference between a cached and an uncached call site.
type CachingEmbeddingProvider(inner: IEmbeddingProvider, cache: IEmbeddingCache) =

    let version = {
        ProviderId = inner.ProviderId
        ModelId = inner.ModelId
        Dimensions = inner.Dimensions
    }

    interface IEmbeddingProvider with
        member _.Dimensions = inner.Dimensions
        member _.ProviderId = inner.ProviderId
        member _.ModelId = inner.ModelId

        member _.GenerateEmbedding(text: string) = async {
            let key = {
                Version = version
                TextHash = sha256Hex text
            }

            match! cache.TryGet key with
            | Some hit -> return hit
            | None ->
                let! embedding = inner.GenerateEmbedding text
                do! cache.Set key embedding
                return embedding
        }

        // Cache-aware batch path: probe every key, then issue one batched
        // call to the inner provider for the misses only — preserving the
        // round-trip-amortisation savings the inner provider's batch path
        // exists to deliver. Stitching back into input order ensures the
        // caller never has to know which entries were cache hits.
        member _.GenerateEmbeddings(texts: string seq) = async {
            let arr = texts |> Seq.toArray

            if arr.Length = 0 then
                return Array.empty
            else
                let keys =
                    arr
                    |> Array.map (fun text -> {
                        Version = version
                        TextHash = sha256Hex text
                    })

                // Sequential probes — IEmbeddingCache implementations are
                // typically backed by a process-local store, so the
                // overhead of Async.Parallel here would dominate the work.
                let results = Array.zeroCreate<float32 array> arr.Length
                let misses = ResizeArray<int>()

                for i in 0 .. arr.Length - 1 do
                    match! cache.TryGet keys[i] with
                    | Some hit -> results[i] <- hit
                    | None -> misses.Add i

                if misses.Count > 0 then
                    let missTexts = misses |> Seq.map (fun i -> arr[i]) |> Seq.toArray
                    let! freshResults = inner.GenerateEmbeddings missTexts

                    // The stitch below trusts the IEmbeddingProvider batch
                    // contract (output[i] embeds input[i], same length).
                    // Verify the length half explicitly: a short response
                    // would otherwise surface as an IndexOutOfRange in the
                    // loop below — far from the misbehaving provider — and
                    // a long one would silently mis-stitch nothing today
                    // but is equally a contract breach worth naming.
                    if freshResults.Length <> misses.Count then
                        failwith (
                            sprintf
                                "Embedding provider '%s/%s' returned %d embeddings for %d inputs from GenerateEmbeddings — refusing to stitch the batch into the cache. The provider has broken the positional batch contract (output[i] embeds input[i]); accepting it would cache wrong embeddings under the inputs' hashes."
                                inner.ProviderId
                                inner.ModelId
                                freshResults.Length
                                misses.Count
                        )

                    for j in 0 .. misses.Count - 1 do
                        let idx = misses[j]
                        let embedding = freshResults[j]
                        results[idx] <- embedding
                        do! cache.Set keys[idx] embedding

                return results
        }

/// Wrap `inner` with `cache`. If you want a no-cache deployment, pass the
/// inner provider directly to `composeWithRAG` without going through this
/// decorator — there is no "no-op cache" type, missing the cache means
/// missing the wrapper.
let create (inner: IEmbeddingProvider) (cache: IEmbeddingCache) : IEmbeddingProvider =
    CachingEmbeddingProvider(inner, cache) :> IEmbeddingProvider
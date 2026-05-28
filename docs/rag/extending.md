# Extending ToolUp.RAG

How to write a new embedding provider, vector store, retrieval tracer, OCR provider, table extractor, image embedder, or reranker.

## Writing a new `IEmbeddingProvider`

A new provider goes in `ToolUp.EmbeddingProviders.<VendorName>`. Implement the interface, expose a `create` function.

```fsharp
module MyVendor.EmbeddingProvider

open ToolUp.Platform

type MyVendorEmbeddingProvider(secretStore: ISecretStore, model: string) =
    let dimensions =
        match model with
        | "myvendor-small" -> 768
        | "myvendor-large" -> 1536
        | _ -> failwith $"Unknown model: {model}"

    interface IEmbeddingProvider with
        member _.GenerateEmbedding(text) = async {
            let! apiKey = secretStore.GetSecret("_platform", "MYVENDOR_API_KEY")
            // Translate to vendor wire format, POST, parse result
            return [| 0.0f |]  // ...
        }
        member _.ProviderId = "myvendor"
        member _.ModelId = model
        member _.Dimensions = dimensions

module MyVendorEmbeddingProvider =
    let create (secretStore: ISecretStore) (model: string) : IEmbeddingProvider =
        MyVendorEmbeddingProvider(secretStore, model) :> _
```

### Provider rules

- **Receive `ISecretStore` through the `create` function.** Never read env vars / config files directly.
- **`ProviderId` must be globally unique.** Used as a discriminator on `EmbeddingVersion` stamps; collisions break re-embedding logic.
- **`Dimensions` must be honest.** The vector store validates incoming vectors against the provider's declared dimensions; mismatches throw.
- **Async at every boundary.** No sync `GenerateEmbedding` — vendor API calls are I/O.
- **Stateless between calls.** Distributed-ready providers must be stateless (portability rule 4). `LocalEmbeddingProvider` is the documented exception (in-process IDF state); mark any new stateful provider as dev-only in its file header.

### Wire into a consumer

```fsharp
open MyVendor.EmbeddingProvider

let embedder = MyVendorEmbeddingProvider.create secretStore "myvendor-large"

RAGServerApp.create (aiProviderFactory, aiConfigStore, embedder)
|> ...
|> RAGServerApp.run
```

Author `IHealthCheck` and `IConfigValidator` probes too — both self-register via DI; the validator emits `Warning` / `Error` at startup if misconfigured.

## Writing a new `IVectorStore`

Vector store impls are larger — they handle storage, indexing, search, soft-delete, vacuum, scope isolation, persistence. Reference impls:
- `InMemoryVectorStore` (in `ToolUp.RAG.Server`) — coarse-locked dictionaries, pre-normalised vectors, debounced blob persistence. ~600 lines.
- `ToolUp.VectorStores.Hnsw` — HNSW index with blob-backed persistence. ~400 lines.

Key contract requirements (from `IVectorStoreContract` test pack):

### Soft-delete semantics

`DeleteChunk` writes a `_deletedAt` tombstone. The chunk persists physically but is filtered from `Search` results. `Vacuum scope retainTombstones` hard-removes tombstones older than the retention window. `DeleteByScope` is a config-grade reset — bypasses tombstone semantics entirely (e.g., for crypto-shred).

The retention window matters because operators may need to recover a soft-deleted chunk within the window. The audit log records the delete as `KnowledgeChunkDeleted`; recovery is "find the tombstone, mark it un-deleted, rebuild the index entry".

### Scope isolation

`Search` accepts a list of scopes. The impl returns results from all listed scopes union'd. The caller (`RetrievalPipeline`) is responsible for filtering the requested list against `AccessContext.TeamId` — the vector store does NOT enforce auth; it trusts the caller. This split keeps the vector store stateless and the auth model centralised.

### Pre-normalisation

For cosine similarity (the standard), pre-normalise vectors at `Index` time so search reduces to a dot product. Faster than per-query normalisation.

### Persistence

Some impls (in-memory, HNSW) persist their state to `IBlobStorage` for warm restart. Decide between:
- **Sync persistence** — flush on every `Index`. Simple; slow.
- **Debounced persistence** — flush after N seconds of idle. The shipped impl uses 5s. Need to handle `IDisposable` to flush on shutdown so no chunks are lost.
- **No persistence** — re-build from `IEventStore` history on restart. Heavy startup; lightest steady-state. Most distributed vector stores (Qdrant, Pinecone) handle persistence themselves.

### Conformance test

Bind your impl into the `IVectorStoreContract` test pack:

```fsharp
testList "MyVectorStore conformance" [
    yield! IVectorStoreContract.tests
        (fun () -> MyVectorStore.create()  :> IVectorStore)
]
```

Run in CI. Failing tests indicate semantic violations; passing means drop-in compatibility.

## Writing a new `IRetrievalTracer`

Trivial interface; wire to whatever observability sink you want:

```fsharp
type DatadogRetrievalTracer(httpClient: HttpClient, apiKey: string) =
    interface IRetrievalTracer with
        member _.Trace(trace, accessCtx) = async {
            let payload = {| (* trace fields *) |}
            do! httpClient.PostAsJsonAsync("https://api.datadoghq.com/api/v2/...", payload)
                |> Async.AwaitTask
                |> Async.Ignore
        }
        member _.Miss(scope, queryHash) = async {
            // record miss metric
        }
```

Register via `withRetrievalTracer`:

```fsharp
RAGServerApp.create (...)
|> ...
|> RAGServerApp.withRetrievalTracer (DatadogRetrievalTracer(httpClient, apiKey))
|> RAGServerApp.run
```

Trace failures must be swallowed — retrieval can't fail because the tracer failed. The default tracer wraps `Trace` in try/with; custom tracers should too.

## Writing a new `IOcrProvider`

For OCR companions integrating with cloud OCR APIs (Azure Document Intelligence, AWS Textract, Google Document AI):

```fsharp
type AzureDocIntelligenceOcrProvider(client: DocumentAnalysisClient) =
    interface IOcrProvider with
        member _.IsScanned(documentBytes) = async {
            // Heuristic — try native text extraction; if it returns near-zero text, it's scanned
            return isLikelyScanned documentBytes
        }
        member _.ExtractText(documentBytes) = async {
            // Use Azure DocIntelligence to extract per-page text
            let! result = client.AnalyzeDocumentAsync("prebuilt-read", documentBytes) |> Async.AwaitTask
            return
                result.Value.Pages
                |> Seq.map (fun page -> {
                    Page = page.PageNumber
                    Text = page.Lines |> Seq.map _.Content |> String.concat "\n"
                })
                |> List.ofSeq
        }
```

OCR is expensive — typical pricing is ~$1.50 per 1000 pages. Use sparingly; pair with `IsScanned` heuristic to avoid OCR-ing every document.

## Writing a new `ITableExtractor`

```fsharp
type CamelotTableExtractor(...) =
    interface ITableExtractor with
        member _.ExtractTables(documentBytes) = async {
            // Call out to a Python sidecar running Camelot/Tabula/etc.
            // Or use a cloud API.
            return extractedTables
        }
```

Output shape (`ExtractedTable`) is deliberately compatible with `Chunking.SheetData` so consumers pipe through `chunkSpreadsheet` without translation. Preserve column headers and row order.

## Writing a new `IImageEmbedder`

```fsharp
type ClipImageEmbedder(httpClient: HttpClient, apiKey: string) =
    let dimensions = 512
    interface IImageEmbedder with
        member _.EmbedImage(imageBytes) = async {
            // POST to CLIP API
            return [| (* 512 floats *) |]
        }
        member _.EmbedQuery(text) = async {
            // Text embedding in the same modality space as images
            return [| (* 512 floats *) |]
        }
        member _.Dimensions = dimensions
        member _.ProviderId = "clip-vit-b32"
        member _.ModelId = "ViT-B/32"
```

The "modality space" property is key — image vectors and query-text vectors must be in the same space for cross-modal retrieval. Most CLIP-style providers satisfy this; check before assuming.

Reserved metadata keys for image embeddings: `ImageEmbeddingMetadata.{ProviderKey, ModelKey, DimensionsKey}` (in `ToolUp.Platform.Core`). Reserved `DataTypeId`: `ImageRegionDataTypeId`. The future multimodal index plugs in here.

No default `IImageEmbedder` is registered — there's no honest no-op for image vectors. Wire one explicitly if you need image retrieval.

## Writing a new `IReranker`

Cross-encoder rerankers (BGE Reranker, Cohere Rerank, Mixedbread Reranker):

```fsharp
type CohereReranker(httpClient: HttpClient, apiKey: string) =
    interface IReranker with
        member _.Rerank(query, candidates, topK) = async {
            let payload = {|
                model = "rerank-english-v2.0"
                query = query
                documents = candidates |> List.map (fun m -> m.Chunk.Text)
                top_n = topK
            |}
            let! response = httpClient.PostAsJsonAsync("https://api.cohere.ai/v1/rerank", payload)
                            |> Async.AwaitTask
            // Parse response, reorder candidates by reranked score
            return rerankedCandidates
        }
```

Rerankers run after dense + sparse retrieval over the merged candidate pool. They typically improve recall@5 by 10-20 points but add latency (50-200ms per request) and cost. Wire only when retrieval quality justifies it; profile end-to-end latency impact.

Required when `MergeStrategy = DenseSparseRerank`; ignored otherwise.

## Writing a new `ITextSummariser`

Optional. Used by `Chunking.withContextualHeader` to prepend a one-sentence summary to each chunk so the model has document-level context.

```fsharp
type ClaudeTextSummariser(aiProvider: IAIProvider) =
    interface ITextSummariser with
        member _.Summarise(text) = async {
            let! response = aiProvider.SendMessage {
                SystemPrompt = "Summarise the following text in one sentence."
                Messages = [ { Role = User; Content = text } ]
                Tools = []
                MaxTokens = 100
                Temperature = 0.0
                Stream = false
            }
            return response.Messages |> List.last |> _.Content
        }
```

LLM-backed summarisation costs tokens; wire only when retrieval quality benefits. Profile retrieval-quality improvement vs cost before adopting.

## Companion conventions

For embedding-provider companions:

```
src/EmbeddingProviders/<VendorName>/
├── <VendorName>EmbeddingProvider.fs
├── <VendorName>EmbeddingProviderHealth.fs
├── <VendorName>EmbeddingProvider.fsproj
├── <VendorName>EmbeddingProvider.Server.props
└── README.md
```

For vector-store companions:

```
src/VectorStores/<Name>/
├── <Name>VectorStore.fs
├── <Name>VectorStoreHealth.fs
├── <Name>VectorStore.fsproj
├── <Name>VectorStore.Server.props
└── README.md
```

The `.Server.props` extension contract injects source into the consuming server project. For pure-DLL companions, omit the `.props` and ship as a regular library — `<PackageReference>` and the types are visible after restore.

## Testing

Bind your impl into the contract test pack:

```fsharp
[<Tests>]
let tests =
    testList "MyEmbeddingProvider conformance" [
        yield! IEmbeddingProviderContract.tests
            (fun () -> MyEmbeddingProvider.create secretStore "default-model")
    ]
```

For vector stores, similarly bind into `IVectorStoreContract`. For retrieval pipelines, `IRetrievalPipelineContract`.

For higher-level integration tests, use the SDK's `InMemoryVectorStore` + `LocalEmbeddingProvider` as the dev substrate; build test fixtures over them; verify your higher-level code works end-to-end.

For end-to-end retrieval-quality tests, the `ToolUp.RAG.Evaluation` package ships evaluation harnesses (BEIR-shaped Q&A pairs, MRR@K / Recall@K metrics). Run it against your impl in periodic offline benchmarks.

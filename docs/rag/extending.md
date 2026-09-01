# Extending ToolUp.RAG

How to write a new embedding provider, vector store, retrieval tracer, OCR provider, table extractor, image embedder, or reranker.

## Writing a new `IEmbeddingProvider`

A new provider goes in `ToolUp.EmbeddingProviders.<VendorName>`
(`src/EmbeddingProviders/<VendorName>/`), as one flat module. Implement the
interface, expose a `create` function.

Note the two opens. `IEmbeddingProvider` and `ISecretStore` each live in
their own module beneath the `ToolUp.Platform` namespace, so `open
ToolUp.Platform` alone does **not** bring either type into scope — the
shipped providers open both explicitly.

```fsharp
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.Secrets

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

        member this.GenerateEmbeddings(texts) =
            // No native batch endpoint — fan out to N parallel single calls.
            batchedFallback (this :> IEmbeddingProvider).GenerateEmbedding texts

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
- **Implement the batch member as well.** `GenerateEmbeddings` is part of the interface, not an optional extra: a vendor with a native batch endpoint issues one HTTP call for N inputs (a 100-chunk document goes from ~15 s of sequential calls to ~150 ms), and one without delegates to `batchedFallback`, which fans out to N parallel `GenerateEmbedding` calls. The returned array must match the input sequence in length and order.
- **Async at every boundary.** No sync `GenerateEmbedding` — vendor API calls are I/O.
- **Stateless between calls.** Distributed-ready providers must be stateless (portability rule 4). `LocalEmbeddingProvider` is the documented exception (in-process IDF state); mark any new stateful provider as dev-only in its file header.

### Wire into a consumer

`RAGServerApp.create` is curried, and its second argument is the
`IProviderProfile` that resolves which model a scope gets — not a config
store:

```fsharp
let embedder = MyVendorEmbeddingProvider.create secretStore "myvendor-large"

RAGServerApp.create aiProviderFactory providerProfile embedder
|> RAGServerApp.run
```

Author `IHealthCheck` and `IConfigValidator` probes too — both self-register via DI; the validator emits `Warning` / `Error` at startup if misconfigured.

## Writing a new `IVectorStore`

Vector store impls are larger — they handle storage, indexing, search, soft-delete, vacuum, scope isolation, persistence. Reference impls:
- `InMemoryVectorStore` (in `ToolUp.RAG.Server`) — coarse-locked dictionaries, pre-normalised vectors, debounced blob persistence. ~600 lines.
- `ToolUp.VectorStores.Hnsw` — HNSW index with blob-backed persistence. ~400 lines.

Key contract requirements, read off `IVectorStore` itself
([`src/ToolUp.Platform.Server/Server/Rag/IVectorStore.fs`](../../src/ToolUp.Platform.Server/Server/Rag/IVectorStore.fs)):

### Soft-delete semantics

`DeleteChunk scope chunkId` writes a `_deletedAt` tombstone. The chunk persists physically but is filtered from `Search` results. `Vacuum scope olderThan` hard-removes tombstones older than the supplied `DateTimeOffset` and returns how many it took. `DeleteByScope` is a config-grade reset — bypasses tombstone semantics entirely (e.g., for crypto-shred).

The retention window matters because operators may need to recover a soft-deleted chunk within it. The audit log records the delete as `KnowledgeChunkDeleted`; recovery is `RestoreChunk scope chunkId`, which clears the tombstone and rebuilds the index entry — implement it, or a vacuum window is the only thing standing between a mistaken delete and permanent loss.

### Scope isolation

`Search` accepts a list of scopes. The impl returns results from all listed scopes union'd. The caller (`RetrievalPipeline`) is responsible for filtering the requested list against `AccessContext.TeamId` — the vector store does NOT enforce auth; it trusts the caller. This split keeps the vector store stateless and the auth model centralised.

### Pre-normalisation

For cosine similarity (the standard), pre-normalise vectors at `Upsert` time so search reduces to a dot product. Faster than per-query normalisation.

### Persistence

Some impls (in-memory, HNSW) persist their state to `IBlobStorage` for warm restart. Decide between:
- **Sync persistence** — flush on every `Upsert`. Simple; slow.
- **Debounced persistence** — flush after N seconds of idle. The shipped impl uses 5s. Need to handle `IDisposable` to flush on shutdown so no chunks are lost.
- **No persistence** — re-build from `IEventStore` history on restart. Heavy startup; lightest steady-state. Most distributed vector stores (Qdrant, Pinecone) handle persistence themselves.

### Conformance test

**There is no shipped `IVectorStore` conformance pack.** `ToolUp.Platform.Tests`
ships contract packs for many SDK seams (`src/ToolUp.Platform.Tests/Contracts/`),
but the vector-store seam is not one of them — the requirements above are the
contract, and the suites that hold the shipped implementations to it are
per-implementation:

- [`InProcess/HnswVectorStoreTests.fs`](../../src/ToolUp.Platform.Tests/InProcess/HnswVectorStoreTests.fs) — scope isolation (a query against the wrong scope must return zero, and a multi-scope query only the scopes asked for), tombstone exclusion + `RestoreChunk`, and deterministic ordering of equal-score matches.
- [`InProcess/PgvectorVectorStoreTests.fs`](../../src/ToolUp.Platform.Tests/InProcess/PgvectorVectorStoreTests.fs) — the same properties asserted structurally, over the emitted SQL: every chunk-touching statement binds the scope column, and search filters tombstones.

Model your own Expecto suite on those and run it in CI. Scope isolation and
soft-delete are the two worth pinning first: a violation of either is silent
at compose time and expensive afterwards.

## Writing a new `IRetrievalTracer`

Two members, both curried over `(payload, ctx)`: `Trace` takes a
`RetrievalTrace`, and `Miss` takes a `RetrievalMiss` — a separate record, so
an admin UI can surface "queries that fall through" without scanning every
trace.

```fsharp
open ToolUp.Platform.IRetrievalTracer

type DatadogRetrievalTracer(httpClient: HttpClient, apiKey: string) =
    interface IRetrievalTracer with
        member _.Trace trace ctx = async {
            let payload = {|
                queryHash = trace.QueryHash
                latencyMs = trace.LatencyMs
                topScore = trace.TopScore
            |}

            do! httpClient.PostAsJsonAsync("https://api.datadoghq.com/api/v2/...", payload)
                |> Async.AwaitTask
                |> Async.Ignore
        }

        member _.Miss miss ctx = async {
            // record miss metric
            do! httpClient.PostAsJsonAsync(
                    "https://api.datadoghq.com/api/v2/...",
                    {| queryHash = miss.QueryHash
                       matchesAboveMinScore = miss.MatchesAboveMinScore |})
                |> Async.AwaitTask
                |> Async.Ignore
        }
```

Register it in DI **before** the RAG composition runs. `composeRAG` probes
for an already-registered `IRetrievalTracer` and falls back to the default
only when it finds none, so registration is the whole of the wiring — there
is no `RAGServerApp` pipeline step for it:

```fsharp
services.AddSingleton<IRetrievalTracer>(DatadogRetrievalTracer(httpClient, apiKey))
```

Trace failures must be swallowed — retrieval can't fail because the tracer failed. The default tracer wraps `Trace` in try/with; custom tracers should too.

## Writing a new `IOcrProvider`

For OCR companions integrating with cloud OCR APIs (Azure Document Intelligence, AWS Textract, Google Document AI):

Both probing methods are curried and take the MIME type alongside the bytes, and the provider declares a `Name` for diagnostics:

```fsharp skip=fragment
type AzureDocIntelligenceOcrProvider(client: DocumentAnalysisClient) =
    interface IOcrProvider with
        member _.Name = "azure-document-intelligence"

        member _.IsScanned documentBytes mimeType = async {
            // Heuristic — try native text extraction; near-zero text means scanned.
            return isLikelyScanned documentBytes mimeType
        }

        member _.ExtractText documentBytes mimeType = async {
            // Use Azure DocIntelligence to extract per-page text
            let! result = client.AnalyzeDocumentAsync("prebuilt-read", documentBytes) |> Async.AwaitTask
            return
                result.Value.Pages
                |> Seq.map (fun page -> {
                    PageNumber = page.PageNumber
                    Text = page.Lines |> Seq.map _.Content |> String.concat "\n"
                })
                |> List.ofSeq
        }
```

OCR is expensive — typical pricing is ~$1.50 per 1000 pages. Use sparingly; pair with `IsScanned` heuristic to avoid OCR-ing every document.

## Writing a new `ITableExtractor`

Same shape as `IOcrProvider`: `ExtractTables` is curried and takes the MIME
type alongside the bytes, and the extractor declares a `Name` for
diagnostics.

```fsharp
open ToolUp.Platform.ITableExtractor

type CamelotTableExtractor() =
    interface ITableExtractor with
        member _.Name = "camelot-lattice"

        member _.ExtractTables documentBytes mimeType = async {
            // Call out to a Python sidecar running Camelot/Tabula/etc.
            // Or use a cloud API.
            return extractedTables
        }
```

Output shape (`ExtractedTable`) is deliberately compatible with `Chunking.SheetData` so consumers pipe through `chunkSpreadsheet` without translation. Preserve column headers and row order.

## Writing a new `IImageEmbedder`

`EmbedImage` is curried and takes the image's MIME type; both methods return `float[]`, not `float32[]`.

```fsharp
open ToolUp.Platform.IImageEmbedder

type ClipImageEmbedder(httpClient: HttpClient, apiKey: string) =
    let dimensions = 512

    interface IImageEmbedder with
        member _.ProviderId = "clip-vit-b32"
        member _.ModelId = "ViT-B/32"
        member _.Dimensions = dimensions

        member _.EmbedImage imageBytes mimeType = async {
            // POST to CLIP API
            return [| (* 512 floats *) |]
        }

        member _.EmbedQuery query = async {
            // Text embedding in the same modality space as images
            return [| (* 512 floats *) |]
        }
```

The "modality space" property is key — image vectors and query-text vectors must be in the same space for cross-modal retrieval. Most CLIP-style providers satisfy this; check before assuming.

Reserved metadata keys for image embeddings: `ImageEmbeddingMetadata.{ProviderKey, ModelKey, DimensionsKey}` (in `ToolUp.Platform.Core`). Reserved `DataTypeId`: `ImageRegionDataTypeId`. The future multimodal index plugs in here.

No default `IImageEmbedder` is registered — there's no honest no-op for image vectors. Wire one explicitly if you need image retrieval.

## Writing a new `IReranker`

Cross-encoder rerankers (BGE Reranker, Cohere Rerank, Mixedbread Reranker):

`Rerank` is curried over query and candidates and takes no `topK` — it reorders the pool it is given, and the caller truncates. The batch ceiling is declared as `MaxBatchSize` so the pipeline can chunk the pool for you:

```fsharp
open ToolUp.Platform.IReranker

type CohereReranker(httpClient: HttpClient, apiKey: string) =
    interface IReranker with
        member _.Name = "cohere-rerank"
        member _.MaxBatchSize = 100

        member _.Rerank query candidates = async {
            let payload = {|
                model = "rerank-english-v2.0"
                query = query
                documents = candidates |> List.map _.Content
            |}

            let! response =
                httpClient.PostAsJsonAsync("https://api.cohere.ai/v1/rerank", payload)
                |> Async.AwaitTask
            // Parse response, reorder candidates by reranked score.
            return rerankedCandidates
        }
```

Rerankers run after dense + sparse retrieval over the merged candidate pool. They typically improve recall@5 by 10-20 points but add latency (50-200ms per request) and cost. Wire only when retrieval quality justifies it; profile end-to-end latency impact.

Required when `MergeStrategy = DenseSparseRerank`; ignored otherwise.

## Writing a new `ITextSummariser`

Optional. Used by `Chunking.withContextualHeader` to prepend a one-sentence summary to each chunk so the model has document-level context.

`Summarise` receives the document context AND the chunk, curried — the point is a chunk-level summary written with the whole document in view:

`IAIProvider.SendMessage` takes a five-part tuple — messages, tools, an
optional system prompt, an optional per-delta streaming callback, and a
`RetryPolicy` — and returns `Result<AIProviderResponse, AIProviderError>`.
There is no request record and no `MaxTokens` / `Temperature` knob on the
call: model parameters belong to the provider's own configuration.

```fsharp
open ToolUp.Platform.AI
open ToolUp.Platform.ITextSummariser

type ClaudeTextSummariser(aiProvider: IAIProvider) =
    interface ITextSummariser with
        member _.Name = "claude-summariser"

        member _.Summarise documentContext chunk = async {
            match!
                aiProvider.SendMessage(
                    [ AIProviderMessage.text "user" $"Document:\n{documentContext}\n\nChunk:\n{chunk}" ],
                    [],
                    Some "Summarise the chunk in one sentence, using the document context.",
                    None,
                    RetryPolicy.defaults
                )
            with
            | Ok response -> return response.Content
            | Error err -> return failwithf "summariser: %A" err
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

`ToolUp.Platform.Tests` ships reusable Expecto contract packs for many SDK
seams — see `src/ToolUp.Platform.Tests/Contracts/`, where each pack is a
`module ToolUp.Platform.Tests.Contracts.IXxxContract` exposing
`tests (name: string) (factory: unit -> IXxx)`. **None of the RAG seams on
this page has one.** There is no `IEmbeddingProviderContract`, no
`IVectorStoreContract` and no `IRetrievalPipelineContract`; those seams are
held to their contracts by per-implementation suites under
`src/ToolUp.Platform.Tests/InProcess/` (`HnswVectorStoreTests.fs`,
`PgvectorVectorStoreTests.fs`, `LocalEmbeddingHashingTests.fs`,
`LocalEmbeddingScopeTests.fs`, `EmbeddingProviderEnvTests.fs`). Model your
own suite on the one nearest your seam and run it in CI.

For higher-level integration tests, use the SDK's `InMemoryVectorStore` + `LocalEmbeddingProvider` as the dev substrate; build test fixtures over them; verify your higher-level code works end-to-end.

For end-to-end retrieval-quality tests, the `ToolUp.RAG.Evaluation` package ships evaluation harnesses (BEIR-shaped Q&A pairs, MRR@K / Recall@K metrics). Run it against your impl in periodic offline benchmarks.

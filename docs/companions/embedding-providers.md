# Embedding provider companions

The Platform's `IEmbeddingProvider` interface generates vector embeddings from text. Each provider companion implements `IEmbeddingProvider` against a specific vendor / library.

For full details on `IEmbeddingProvider`, the chunking layer, retrieval pipeline, and how embeddings flow through `ToolUp.RAG`, see [`rag/concepts.md`](../rag/concepts.md). For authoring guide, see [`rag/extending.md`](../rag/extending.md).

## What's shipped

| Companion | Dimensions | Cost | Use case |
|---|---|---|---|
| `ToolUp.EmbeddingProviders.Local` | 512 | Free (in-process) | Dev / CI / offline. TF-IDF; low quality. |
| `ToolUp.EmbeddingProviders.OpenAI` | 1536 | ~$0.02 per 1M tokens | Production. `text-embedding-3-small`. |

The interface is the same:

```fsharp
type IEmbeddingProvider =
    abstract GenerateEmbedding: text: string -> Async<float32[]>
    abstract ProviderId: string
    abstract ModelId: string
    abstract Dimensions: int
```

`EmbeddingVersion` is the `(ProviderId, ModelId, Dimensions)` triple. Stamped onto every indexed chunk's metadata so model swaps are detectable.

## Picking a provider

### `ToolUp.EmbeddingProviders.Local` (TF-IDF, in-process)

Use when:
- Local development; offline; no API access.
- CI tests where retrieval quality isn't the focus.
- Tiny corpora where TF-IDF's keyword-overlap matching is sufficient.

Don't use when:
- Production retrieval — TF-IDF degrades badly on synonyms, paraphrases, semantic relevance.
- Non-English corpora — TF-IDF is even worse without language-specific tokenisation.
- Cross-document semantic search — TF-IDF can't relate "revenue" to "income" unless both literal words appear.

Setup:

```fsharp skip=fragment
open ToolUp.EmbeddingProviders.Local

let embedder = LocalEmbeddingProvider.create() :> IEmbeddingProvider

RAGServerApp.create (aiProviderFactory, aiConfigStore, embedder)
|> ...
|> RAGServerApp.run
```

No API key needed. No network calls. Pure in-process.

Stateful caveat: `LocalEmbeddingProvider` retains mutable IDF state across calls — the inverse-document-frequency table grows as new chunks are indexed. This means the same input text produces a different vector at time T1 vs T2 if other chunks were indexed in between. Documented dev-only exception to the six portability rules (rule 4 — stateless handlers between invocations).

### `ToolUp.EmbeddingProviders.OpenAI` (production)

Use when:
- Production retrieval over heterogeneous text content.
- Multi-language corpora — `text-embedding-3-small` handles 100+ languages.
- You need to scale to 100K+ chunks without retrieval quality degrading.

Setup:

```fsharp skip=fragment
open ToolUp.EmbeddingProviders.OpenAI

let embedder = OpenAIEmbeddingProvider.create secretStore :> IEmbeddingProvider

RAGServerApp.create (aiProviderFactory, aiConfigStore, embedder)
|> ...
|> RAGServerApp.run
```

Store API key under `_platform` scope, key name `OPENAI_API_KEY`. The provider pulls per-call.

Cost: ~$0.02 per 1M tokens for `text-embedding-3-small`. A 1000-document KB with 50 chunks each at ~500 tokens per chunk = 25M tokens = ~$0.50 once. Re-embedding on model swap = same cost again.

Latency: ~50-200ms per embed call (OpenAI's API). Pair with `CachingEmbeddingProvider` (auto-wrapped by `composeWithRAG`) so repeated queries / re-embeds hit the cache.

## Caching layer

The SDK auto-wraps any registered `IEmbeddingProvider` with `CachingEmbeddingProvider` — LRU cache, keys are `(ProviderId, ModelId, Dimensions, SHA256(text))`:

```fsharp
type IEmbeddingCache =
    abstract TryGet: providerId: string -> modelId: string -> dimensions: int -> textHash: string -> float32[] option
    abstract Set: providerId: string -> modelId: string -> dimensions: int -> textHash: string -> float32[] -> unit
    abstract HitRate: float
```

Default `InMemoryEmbeddingCache` has capacity 10000. Cache keys are SHA256-hashed — raw text never lands in keys.

Cache hits matter most when:
- The same query text recurs across users (e.g., common chat questions).
- Re-embedding a document with the same chunk text (model swap → re-embed → same chunk → cache hit if not evicted).

Replace the in-memory cache with a Redis-backed companion:

```fsharp skip=fragment
RAGServerApp.create (...)
|> ...
|> RAGServerApp.withEmbeddingCache (RedisEmbeddingCache.create connectionString None)
|> ...
```

`RedisEmbeddingCache.create : string -> ILogger option -> IEmbeddingCache` connects with the shipped defaults and owns the multiplexer. Use `fromMultiplexer` to share a connection pool the deployment already owns (the same `IConnectionMultiplexer` backing `RedisNotificationChannel` / `RedisDistributedLock`), or `createWith` to override `RedisEmbeddingCacheOptions`.

The Redis cache survives process restarts and serves across multiple app instances. Useful at scale.

It also **lifts** the `team-mode-shared-embedding-cache` preflight warning. That validator fires when the process-local `InMemoryEmbeddingCache` is active in `Team` / `MultiTeam` mode with `ReplicaCount > 1`, because each replica then keeps its own entries for the same text. A cross-replica cache removes that divergence, so the warning stops applying rather than being suppressed — `ServerConfig.AcceptSharedEmbeddingCacheInTeamMode` stays `false`. Composing `InMemoryEmbeddingCache` through the hook by hand is accepted and behaves exactly as the default, warning included: what lifts the warning is the cache spanning replicas, not the hook being called.

## Version stamping + re-embedding

Every indexed chunk carries `EmbeddingVersion` metadata:
- `_embedProvider` — `ProviderId` (e.g. "openai")
- `_embedModel` — `ModelId` (e.g. "text-embedding-3-small")
- `_embedDim` — `Dimensions` (e.g. "1536")

When you swap providers (or models), enqueue the affected scopes for re-embedding:

```fsharp skip=fragment
let queue = serviceProvider.GetRequiredService<ReembeddingQueue>()
do! queue.Enqueue(Team teamId)
```

The `ReembeddingBackgroundService`:
1. Lists all chunks in the scope via `IVectorStore.ListChunks`.
2. Filters chunks whose `EmbeddingVersion` doesn't match the current provider's.
3. Re-embeds each via the new provider.
4. Replaces the old vector via `IVectorStore.Upsert`, which is idempotent on `(scope, chunkId)`.
5. Emits `KnowledgeChunkReembedded` event.

Mixing providers within one corpus is structurally allowed but degrades retrieval — different models produce vectors in different spaces; cosine similarity between them is meaningless. Always re-embed the full scope after a provider change.

## Common configuration

All providers receive `ISecretStore` through their `create` function:

```fsharp skip=fragment
// The provider package is a top-level module, and `create` already returns
// IEmbeddingProvider.
let embedder = OpenAIEmbeddingProvider.create secretStore
```

The provider reads the API key per call from `ISecretStore` under the `_platform` scope. Key names are provider-specific (`OPENAI_API_KEY`, `COHERE_API_KEY`, etc.). Rotation is transparent — write the new key to `ISecretStore`; the next call reads it.

Distributed-ready providers MUST be stateless between calls (portability rule 4). `LocalEmbeddingProvider` is the documented exception (in-process IDF state); mark any new stateful provider as dev-only in its file header.

## Writing a new provider

For a vendor not covered (Cohere, Voyage, BGE, in-house):

```fsharp skip=fragment
module MyVendor.EmbeddingProvider

let create (secretStore: ISecretStore) (model: string) : IEmbeddingProvider =
    MyVendorEmbeddingProvider(secretStore, model) :> _

type MyVendorEmbeddingProvider(secretStore: ISecretStore, model: string) =
    let dimensions =
        match model with
        | "myvendor-small" -> 768
        | "myvendor-large" -> 1536
        | _ -> failwith $"Unknown model: {model}"

    interface IEmbeddingProvider with
        member _.GenerateEmbedding(text) = async {
            let! apiKey = secretStore.GetSecret("_platform", "MYVENDOR_API_KEY")
            // Translate, POST, parse, return float32[]
            return [| 0.0f |]
        }
        member _.ProviderId = "myvendor"
        member _.ModelId = model
        member _.Dimensions = dimensions
```

Wire:

```fsharp skip=fragment
let embedder = MyVendor.EmbeddingProvider.create secretStore "myvendor-large"
RAGServerApp.create (aiProviderFactory, aiConfigStore, embedder)
|> ...
```

Author an `IHealthCheck` + `IConfigValidator` for self-registration.

See [`rag/extending.md`](../rag/extending.md) for the full guide.

## Hardening checklist for production

- Production embedding provider — `LocalEmbeddingProvider` is dev-only.
- API keys in `ISecretStore`, scoped to `_platform`.
- `CachingEmbeddingProvider` wrapping enabled (auto-applied by `composeWithRAG`).
- Distributed cache (Redis) for multi-instance deployments.
- Health probe + config validator self-register.
- Model-swap procedure documented for operators — re-embed after swap, audit `KnowledgeChunkReembedded` events.
- Cost monitoring — track embedding API spend in the OpenTelemetry / Prometheus metrics layer.

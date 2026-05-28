module ToolUp.Platform.IEmbeddingCache

open ToolUp.Platform.IEmbeddingProvider

/// Cache key for an embedding lookup. Combines the provider identity with
/// a content hash so two providers (or two models from the same provider)
/// never collide on the same text. The hash is the caller's choice — the
/// default `CachingEmbeddingProvider` decorator uses SHA256.
type EmbeddingCacheKey = {
    Version: EmbeddingVersion
    /// Hex-encoded hash of the source text. Hash, not raw text — keeps the
    /// cache O(hashLength) per entry regardless of input length and avoids
    /// retaining user-supplied content in memory longer than necessary.
    TextHash: string
}

/// In-memory cache for embedding results. The intent is to avoid recomputing
/// embeddings for identical chat messages, repeated KB queries, and idempotent
/// re-ingestion paths. Cache implementations are companion-or-default; the
/// interface carries no specific eviction policy beyond an obligation to be
/// bounded.
///
/// Satisfies Phase 9c portability rules:
/// 1. Identity by value — `EmbeddingCacheKey` is a value record
/// 2. Async at every boundary
/// 3. No callback / supervision hooks
/// 4. Stateless between calls — implementations may keep an in-process LRU
///    but `TryGet` / `Set` must not require any prior call to succeed
/// 5. No cross-key ordering promises — a `Set` followed by `TryGet` on the
///    same key may miss if eviction races are observable to a distributed
///    backend; in-process caches must not exhibit this
type IEmbeddingCache =
    /// Look up an embedding by key. `None` = cache miss; the caller must
    /// generate the embedding via `IEmbeddingProvider.GenerateEmbedding` and
    /// then call `Set`.
    abstract TryGet: key: EmbeddingCacheKey -> Async<float32 array option>
    /// Insert or replace an embedding for `key`. Implementations may evict
    /// other entries to honour their bound.
    abstract Set: key: EmbeddingCacheKey -> embedding: float32 array -> Async<unit>
    /// Approximate hit-rate over the lifetime of this instance, in `[0, 1]`.
    /// Useful for the steady-state replay test in Phase 14h's acceptance
    /// criteria. A fresh cache returns `0.0`. Distributed implementations
    /// may return a local-process-only figure rather than a global one.
    abstract HitRate: unit -> Async<float>

    /// Phase 9h — DSR support. Drop every cached entry. The cache keys
    /// on a content *hash* (never the raw text or any subject id), so a
    /// per-subject invalidation is impossible by construction; a full
    /// flush is the privacy-correct response and is always safe — the
    /// cache is a pure recomputation optimisation. The DSR flow calls
    /// this after erasing the source chunks so a stale embedding of
    /// erased content cannot be served from cache.
    abstract Clear: unit -> Async<unit>
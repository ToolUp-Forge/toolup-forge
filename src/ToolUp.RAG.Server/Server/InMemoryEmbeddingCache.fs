module ToolUp.RAG.InMemoryEmbeddingCache

open System.Collections.Generic
open ToolUp.Platform.IEmbeddingCache

/// In-memory LRU cache for embedding results. Bounded by `capacity` (default
/// 10,000 entries) — when full, the least-recently-used key is evicted before
/// inserting the new one. Suitable for a single-process deployment; for
/// multi-instance deployments swap in a Redis-backed `IEmbeddingCache`
/// companion that satisfies Phase 9c rule 4 (stateless between calls).
///
/// Hit-rate is tracked across the lifetime of the instance — useful for
/// the Phase 14h acceptance test (≥80% steady-state hit-rate on a replay
/// workload). A cold cache reports 0.0.
///
/// Thread-safe under the same `lockObj` for both reads (which mutate
/// recency) and writes. The expected hot-path call rate is one cache
/// lookup per chat message or per ingested chunk, so contention is low
/// enough that a coarse lock is preferable to a sharded structure.
type InMemoryEmbeddingCache(?capacity: int) =
    let cap = defaultArg capacity 10000
    let lockObj = obj ()

    // Doubly-linked list head/tail bookkeeping is provided by `LinkedList<_>`.
    // Each node holds the cache key + the embedding payload; the dictionary
    // maps key → node for O(1) lookups, while the list preserves recency
    // order with O(1) move-to-front and O(1) tail eviction.
    let order = LinkedList<EmbeddingCacheKey * float32 array>()

    let lookup =
        Dictionary<EmbeddingCacheKey, LinkedListNode<EmbeddingCacheKey * float32 array>>()

    let mutable hits = 0L
    let mutable misses = 0L

    let touch (node: LinkedListNode<EmbeddingCacheKey * float32 array>) =
        // Move to head: removed-and-re-added is the documented O(1) idiom
        // for `LinkedList<_>`; there is no `MoveToFirst` primitive.
        order.Remove(node)
        order.AddFirst(node) |> ignore

    interface IEmbeddingCache with

        member _.TryGet(key: EmbeddingCacheKey) = async {
            return
                lock lockObj (fun () ->
                    match lookup.TryGetValue(key) with
                    | true, node ->
                        touch node
                        hits <- hits + 1L
                        Some(snd node.Value)
                    | _ ->
                        misses <- misses + 1L
                        None)
        }

        member _.Set (key: EmbeddingCacheKey) (embedding: float32 array) = async {
            lock lockObj (fun () ->
                match lookup.TryGetValue(key) with
                | true, existing ->
                    // Replace the embedding in-place and bump recency.
                    existing.Value <- (key, embedding)
                    touch existing
                | _ ->
                    if lookup.Count >= cap then
                        // Evict the least-recently-used entry. Tail of the
                        // list is the LRU because head is most-recent.
                        let lru = order.Last

                        if lru <> null then
                            order.RemoveLast()
                            lookup.Remove(fst lru.Value) |> ignore

                    let node = LinkedListNode((key, embedding))
                    order.AddFirst(node)
                    lookup[key] <- node)
        }

        member _.HitRate() = async {
            return
                lock lockObj (fun () ->
                    let total = hits + misses

                    if total = 0L then 0.0 else float hits / float total)
        }

        member _.Clear() = async {
            lock lockObj (fun () ->
                order.Clear()
                lookup.Clear())
        }
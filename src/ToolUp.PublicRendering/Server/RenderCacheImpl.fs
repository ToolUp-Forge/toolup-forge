// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading.Tasks
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 84 — render-cache implementations ─────────────────────────
//
// Two shipped impls of `IRenderCache` (+ `IRenderCacheInvalidation`):
//
//   - `InMemoryRenderCache` — a `ConcurrentDictionary`-backed cache for
//     single-instance deployments. The default when a deployment calls
//     `withRenderCache` without supplying its own impl.
//   - `BlobRenderCache` — an `IBlobStorage`-backed cache for multi-
//     instance deployments where every replica must see the same cached
//     render (and a publish on one replica must invalidate the others).
//
// Both are stateless between calls beyond the shared store (rule 4):
// every operation carries its full `RenderKey`. `InMemoryRenderCache`'s
// dict is process-local (acceptable for the single-instance contract it
// declares); `BlobRenderCache` holds no in-memory continuity at all.

/// Freshness helper shared by both impls: decide whether a stored entry
/// is returnable for `now`. A fresh entry (`now < ExpiresAt`) is always
/// returnable; an expired entry is returnable only when it permits
/// stale-while-revalidate. An expired, non-SWR entry is *not* returnable
/// and the caller drops it.
module internal RenderCacheEntry =
    let isReturnable (now: DateTimeOffset) (entry: RenderedPage) : bool =
        now < entry.ExpiresAt || entry.StaleWhileRevalidate

    /// Apply a `Cache` policy to a to-be-stored page, deriving the
    /// authoritative `ExpiresAt` / `StaleWhileRevalidate`. `NoCache`
    /// yields `None` (nothing to store).
    let forPolicy (policy: CachePolicy) (page: RenderedPage) : RenderedPage option =
        match policy with
        | NoCache -> None
        | Cache(ttlSeconds, swr) ->
            Some {
                page with
                    ExpiresAt = page.RenderedAt.AddSeconds(float ttlSeconds)
                    StaleWhileRevalidate = swr
            }

/// `ConcurrentDictionary`-backed render cache. Single-instance only —
/// the dict is process-local, so a multi-replica deployment would cache
/// (and invalidate) per-replica. Marked accordingly; use `BlobRenderCache`
/// when replicas must share a cache.
type InMemoryRenderCache() =
    let entries = ConcurrentDictionary<RenderKey, RenderedPage>()

    interface IRenderCache with
        member _.TryGet(key: RenderKey) : Async<RenderedPage option> = async {
            match entries.TryGetValue key with
            | true, entry ->
                if RenderCacheEntry.isReturnable DateTimeOffset.UtcNow entry then
                    return Some entry
                else
                    // Hard-expired with no stale-while-revalidate — drop it
                    // and report a miss so the caller re-renders.
                    entries.TryRemove key |> ignore
                    return None
            | _ -> return None
        }

        member _.Set (key: RenderKey) (page: RenderedPage) (policy: CachePolicy) : Async<unit> = async {
            match RenderCacheEntry.forPolicy policy page with
            | Some stored -> entries[key] <- stored
            | None -> () // NoCache — never store
        }

        member _.Invalidate(key: RenderKey) : Async<unit> = async { entries.TryRemove key |> ignore }

    interface IRenderCacheInvalidation with
        member _.PurgeSlug(slug: string) : Async<unit> = async {
            // Snapshot the keys before mutating — `ConcurrentDictionary`'s
            // `Keys` is a moving target under concurrent writes.
            entries.Keys
            |> Seq.filter (fun k -> k.Slug = slug)
            |> Seq.toList
            |> List.iter (fun k -> entries.TryRemove k |> ignore)
        }

module InMemoryRenderCache =
    /// Construct the default single-instance render cache.
    let create () : IRenderCache = InMemoryRenderCache() :> IRenderCache

/// `IBlobStorage`-backed render cache for multi-instance deployments.
/// Every replica reads/writes the same blob container, so a render
/// stored by one replica is served by all, and a `PurgeSlug` on one
/// replica is visible to the rest.
///
/// **Blob layout.** One blob per cache entry, named
/// `{escapedSlug}/{escapedScope}/{escapedVersion}.json` inside `container`
/// (default `"rendercache"`). Slug-first naming makes `PurgeSlug` a
/// single prefix `List` over `{escapedSlug}/`, sweeping every scope and
/// version of one slug. Each segment is `Uri.EscapeDataString`-encoded so
/// a slug's own `/` separators don't collide with the layout separators.
///
/// **Scope isolation (GP 4).** Isolation is by key, not by container: a
/// request derives its `ScopeId` from its resolved `AccessContext`, so
/// the blob name it reads/writes is structurally bound to its own scope.
/// Team A's request computes `ScopeId = "team-A"` and can only ever
/// address `…/team-A/…` blobs — it cannot name team B's blob.
type BlobRenderCache(blobStorage: IBlobStorage, container: string) =

    static let jsonOptions = FableConverters.create ()

    let escape (s: string) = Uri.EscapeDataString s

    let blobName (key: RenderKey) : string =
        let version =
            if String.IsNullOrEmpty key.ContentVersion then
                "_"
            else
                key.ContentVersion

        $"{escape key.Slug}/{escape key.ScopeId}/{escape version}.json"

    let slugPrefix (slug: string) : string = $"{escape slug}/"

    interface IRenderCache with
        member _.TryGet(key: RenderKey) : Async<RenderedPage option> = async {
            let! result = blobStorage.Download(container, blobName key)

            match result with
            | Error _ -> return None // miss (not-found or backend error → treat as miss)
            | Ok bytes ->
                let entry =
                    JsonSerializer.Deserialize<RenderedPage>(ReadOnlySpan(bytes), jsonOptions)

                if RenderCacheEntry.isReturnable DateTimeOffset.UtcNow entry then
                    return Some entry
                else
                    // Hard-expired, no stale-while-revalidate — delete and
                    // report a miss. Best-effort delete; a failed delete
                    // just means the next request re-evaluates expiry.
                    let! _ = blobStorage.Delete(container, blobName key)
                    return None
        }

        member _.Set (key: RenderKey) (page: RenderedPage) (policy: CachePolicy) : Async<unit> = async {
            match RenderCacheEntry.forPolicy policy page with
            | None -> () // NoCache — never store
            | Some stored ->
                let bytes = JsonSerializer.SerializeToUtf8Bytes(stored, jsonOptions)
                let! _ = blobStorage.Upload(container, blobName key, bytes)
                return ()
        }

        member _.Invalidate(key: RenderKey) : Async<unit> = async {
            let! _ = blobStorage.Delete(container, blobName key)
            return ()
        }

    interface IRenderCacheInvalidation with
        member _.PurgeSlug(slug: string) : Async<unit> = async {
            let! names = blobStorage.List(container, slugPrefix slug)

            for name in names do
                let! _ = blobStorage.Delete(container, name)
                ()
        }

module BlobRenderCache =
    [<Literal>]
    let DefaultContainer = "rendercache"

    /// Construct a blob-backed render cache over the supplied
    /// `IBlobStorage`, using the default `"rendercache"` container.
    let create (blobStorage: IBlobStorage) : IRenderCache =
        BlobRenderCache(blobStorage, DefaultContainer) :> IRenderCache

    /// Construct a blob-backed render cache with an explicit container
    /// name (for deployments that partition blob containers per concern).
    let createIn (container: string) (blobStorage: IBlobStorage) : IRenderCache =
        BlobRenderCache(blobStorage, container) :> IRenderCache

// ─── Phase 472 — edge fan-out on render-cache invalidation ───────────
//
// Phase 84 gave a publish one purge: the origin's own render cache. With
// a CDN in front, that is now the *inner* half of the job — the edge is
// still handing out the previous render until its TTL expires, and the
// publish that was supposed to make the change live has not.
//
// `EdgeAwareRenderCacheInvalidation` decorates the composed
// `IRenderCacheInvalidation` so a publish purges both. The ORDER is
// load-bearing and is the reason this is a decorator rather than a
// second call at the publish site: the origin cache is purged first,
// then the edge. Purging the edge first would let an edge node re-fetch
// while the origin cache still held the stale render, re-populating the
// edge with exactly the bytes the purge was meant to remove.
//
// The edge half never blocks (GP 7) — see `EdgeCache.purgeDetached`. A
// publish succeeds whether or not the CDN's API is reachable; a failed
// purge is audited and the edge object expires on its own TTL.

/// Default origin-relative paths to purge for a published slug.
module RenderCacheEdgePaths =
    /// `"hello"` → `[ "/hello"; "/hello/" ]`.
    ///
    /// Both variants, because a CDN keys its cache on the request URI as
    /// received: `/hello` and `/hello/` are two objects at the edge even
    /// where the origin routes them to one page. Purging only the form
    /// the SDK happens to spell would leave the other serving stale
    /// bytes, which is the failure this seam exists to prevent — and it
    /// is invisible, because the person checking types one of the two.
    let forSlug (slug: string) : string list =
        let trimmed = slug.Trim().TrimStart('/')

        if String.IsNullOrEmpty trimmed then
            [ "/" ]
        else
            let path = "/" + trimmed.TrimEnd('/')
            [ path; path + "/" ]

/// The origin half of the purge when a deployment composes an edge
/// cache but no render cache — which is an ordinary CDN-fronted shape,
/// not a misconfiguration: the edge IS that deployment's cache. Purging
/// nothing is then the correct origin-side behaviour, and it lets the
/// edge fan-out ride the one publish hook rather than needing a second.
type NoopRenderCacheInvalidation() =

    static let done': Async<unit> = async.Return()

    interface IRenderCacheInvalidation with
        member _.PurgeSlug(_: string) : Async<unit> = done'

/// Purge the origin render cache, then fan the affected edge paths out
/// to the composed `IEdgeCache`. Composed by
/// `PublicRenderingServerApp.withEdgeCache`; absent it, the pre-472
/// invalidator is registered unwrapped and nothing about publishing
/// changes (GP 11).
type EdgeAwareRenderCacheInvalidation
    (
        inner: IRenderCacheInvalidation,
        edgeCache: IEdgeCache,
        /// Origin-relative paths a purged slug maps to. Supplied rather
        /// than hard-coded because a deployment's public URL for a slug
        /// is its own routing decision — a multi-site or prefixed
        /// deployment does not serve `"/" + slug`.
        pathsForSlug: string -> string list,
        logger: ILogger option
    ) =

    /// The decorated origin-cache invalidator, so a caller holding the
    /// decorator can still reach the inner surface.
    member _.Inner = inner

    /// The composed edge cache this decorator fans out to.
    member _.EdgeCache = edgeCache

    interface IRenderCacheInvalidation with
        member _.PurgeSlug(slug: string) : Async<unit> = async {
            // Origin first — see the header note on ordering.
            do! inner.PurgeSlug slug
            EdgeCache.purgePathsDetached logger (Some edgeCache) (pathsForSlug slug)
        }

module EdgeAwareRenderCacheInvalidation =
    /// Wrap an invalidator with edge fan-out over the default slug→path
    /// mapping (`RenderCacheEdgePaths.forSlug`).
    let create
        (logger: ILogger option)
        (edgeCache: IEdgeCache)
        (inner: IRenderCacheInvalidation)
        : IRenderCacheInvalidation =
        EdgeAwareRenderCacheInvalidation(inner, edgeCache, RenderCacheEdgePaths.forSlug, logger)
        :> IRenderCacheInvalidation

    /// Wrap an invalidator with edge fan-out over an explicit slug→path
    /// mapping, for a deployment whose public URLs are not `"/" + slug`.
    let createWith
        (pathsForSlug: string -> string list)
        (logger: ILogger option)
        (edgeCache: IEdgeCache)
        (inner: IRenderCacheInvalidation)
        : IRenderCacheInvalidation =
        EdgeAwareRenderCacheInvalidation(inner, edgeCache, pathsForSlug, logger) :> IRenderCacheInvalidation

// ─── Phase 199 — default request-coalescer (stampede protection) ─────
//
// `InProcessRenderCoalescer` is the default `IRenderCoalescer`: a
// process-local single-flight keyed by `RenderKey`. It collapses a
// cold-key traffic spike within one replica so the expensive
// produce-and-store step runs once per in-flight key instead of once per
// concurrent request. Like `InMemoryRenderCache`, it is single-instance —
// each replica coalesces its own stampede; a multi-replica deployment MAY
// supply a distributed coalescer for cross-replica single-flight (the seam
// allows it, the SDK does not mandate it — GP 12).

/// Process-local per-`RenderKey` single-flight. While a render for a key
/// is in flight, all concurrent callers for that key share the one
/// computation; once it completes the key is released so a later miss
/// starts a fresh round.
type InProcessRenderCoalescer() =

    // One in-flight entry per key. The value is a `Lazy` wrapping the
    // shared task so the produce thunk starts *exactly once* even under a
    // `GetOrAdd` factory race: `ConcurrentDictionary` may invoke the
    // factory more than once under contention, but it publishes and returns
    // a single `Lazy`, and only that published `Lazy` is ever forced —
    // the discarded ones never run their thunk. The result is boxed to
    // `obj` so one non-generic map serves every `Coalesce<'T>` call site.
    let inFlight = ConcurrentDictionary<RenderKey, Lazy<Task<obj>>>()

    interface IRenderCoalescer with
        member _.Coalesce (key: RenderKey) (produce: unit -> Async<'T>) : Async<'T> =
            let entry =
                inFlight.GetOrAdd(
                    key,
                    fun _ ->
                        lazy
                            (Async.StartAsTask(
                                async {
                                    let! result = produce ()
                                    return box result
                                }
                            ))
                )

            async {
                try
                    // Forcing `.Value` starts the shared task once; every
                    // concurrent awaiter of the same `entry` awaits it.
                    let! boxed = Async.AwaitTask entry.Value
                    return unbox<'T> boxed
                finally
                    // Release the key once the shared render is done (the
                    // single-flight is per cold-key *round*, not permanent —
                    // permanent memoisation is the render cache's job, not
                    // the coalescer's). Remove by (key, value) identity so a
                    // newer round's entry, if one already replaced ours, is
                    // left untouched.
                    inFlight.TryRemove(KeyValuePair(key, entry)) |> ignore
            }

module InProcessRenderCoalescer =
    /// Construct the default process-local render coalescer. One instance
    /// per composed render cache (registered as a DI singleton alongside
    /// it) so its in-flight map is shared across every request on the
    /// replica.
    let create () : IRenderCoalescer =
        InProcessRenderCoalescer() :> IRenderCoalescer
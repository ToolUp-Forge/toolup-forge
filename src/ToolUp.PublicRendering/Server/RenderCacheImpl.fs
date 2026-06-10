// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open System
open System.Collections.Concurrent
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
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
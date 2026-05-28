// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Teams

open System
open System.Text
open System.Threading
open Newtonsoft.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Email-keyed pre-invite blob ─────────────────────────────────────
//
// Persists a flat map of `email -> PendingInviteByEmail` under the
// `_platform/pending-invites.json` blob (path constant declared in
// `TeamInviteTypes.PendingInvitesBlobPath`). Two access patterns:
//
//   * Admin-write path. `IssuePendingInviteByEmail` reads the current
//     map, adds or replaces the entry for the supplied email,
//     persists the updated map atomically (full-blob overwrite).
//     Owner/Admin only; gated in the API handler.
//
//   * Middleware-read path. `ScopeResolutionMiddleware` calls
//     `tryConsumeForEmail` on every authenticated sign-in resolve.
//     The function returns the pending entry (if any) AND atomically
//     removes it from the persisted map — the caller is expected to
//     follow up with `ITeamStore.AddMember`. A 30-second in-memory
//     cache absorbs the read path; reads on a sign-in that doesn't
//     match are bounded by the cache TTL.
//
// **Concurrency.** Cross-process writes are serialised by a single
// `SemaphoreSlim` — sufficient for single-instance deployments
// (the canonical Phase 5f shape). Distributed deployments require
// ETag-based optimistic concurrency on `IBlobStorage.Upload` (Phase
// 9c follow-up); the eventual `BlobPendingInviteStore` will bind to
// `IPendingInviteStore` against the ETag substrate without touching
// callers — they resolve the interface from DI.
//
// **Phase 5h.** File renamed from `PendingInviteStore.fs` to
// `InMemoryPendingInviteStore.fs`; the `InMemoryPendingInviteStore`
// type at namespace level adopts `IPendingInviteStore` explicitly so
// `ServerApp.withPendingInviteStore` can swap in alternative impls
// (the future `BlobPendingInviteStore`, an optional `RedisPendingInviteCache`
// decorator). The pre-existing `module PendingInviteStore` is preserved
// as a backward-compat shim — call sites depending on
// `PendingInviteStore.upsert` / `.remove` / etc. compile unchanged
// until they migrate to the interface seam.

/// Backward-compat shim — module-functions surface preserved verbatim
/// from the pre-Phase-5h shape. New code should resolve
/// `IPendingInviteStore` from DI and call its methods; this surface
/// remains until every in-tree caller has migrated.
module PendingInviteStore =

    type private CacheEntry = {
        Map: Map<string, PendingInviteByEmail>
        LoadedAt: DateTime
    }

    let private cacheTtl = TimeSpan.FromSeconds 30.0
    let private writeLock = new SemaphoreSlim(1, 1)
    // (b) — process-lifetime cache; reset for Expecto via
    // `CacheReset.invalidateAll`. See docs/platform/testing-conventions.md.
    let mutable private cache: CacheEntry option = None

    /// Test-only: drop the in-memory cache so a subsequent test starts
    /// from a clean slate. Registered via
    /// `ToolUp.Platform.Tests.Support.CacheReset.invalidateAll`. Never
    /// called from production code paths.
    let internal __internal_resetForTests () = cache <- None

    let private platformContainer = "_platform"
    let private blobName = "pending-invites.json"

    let private encodeMap (map: Map<string, PendingInviteByEmail>) : byte[] =
        map |> JsonConvert.SerializeObject |> Encoding.UTF8.GetBytes

    let private decodeMap (bytes: byte[]) : Map<string, PendingInviteByEmail> =
        if isNull bytes || bytes.Length = 0 then
            Map.empty
        else
            let json = Encoding.UTF8.GetString bytes

            try
                JsonConvert.DeserializeObject<Map<string, PendingInviteByEmail>>(json)
                |> Option.ofObj
                |> Option.defaultValue Map.empty
            with _ ->
                Map.empty

    let private loadFromStore (storage: IBlobStorage) : Async<Map<string, PendingInviteByEmail>> = async {
        match! storage.Download(platformContainer, blobName) with
        | Ok bytes -> return decodeMap bytes
        | Error _ -> return Map.empty
    }

    let private cachedRead (storage: IBlobStorage) : Async<Map<string, PendingInviteByEmail>> = async {
        let now = DateTime.UtcNow

        match cache with
        | Some entry when now - entry.LoadedAt < cacheTtl -> return entry.Map
        | _ ->
            let! fresh = loadFromStore storage

            cache <- Some { Map = fresh; LoadedAt = now }

            return fresh
    }

    let private writeAndInvalidate (storage: IBlobStorage) (map: Map<string, PendingInviteByEmail>) : Async<unit> = async {
        let bytes = encodeMap map
        let! _ = storage.Upload(platformContainer, blobName, bytes)

        cache <-
            Some {
                Map = map
                LoadedAt = DateTime.UtcNow
            }
    }

    /// Drop every entry whose `ExpiresAt` is in the past. Returns the
    /// compacted map and the count of removed entries. Pure over the
    /// input — callers decide whether to persist the result.
    let private dropExpired
        (now: DateTime)
        (map: Map<string, PendingInviteByEmail>)
        : Map<string, PendingInviteByEmail> * int =
        let kept = map |> Map.filter (fun _ entry -> entry.ExpiresAt >= now)

        let removed = map.Count - kept.Count
        kept, removed

    /// Walk the blob, remove every entry whose `ExpiresAt` is in the past,
    /// persist the compacted map iff anything was removed. Returns the
    /// number of removed entries. Useful as a scheduled background sweep
    /// (via `IJobScheduler`); also invoked opportunistically from `upsert`
    /// to keep storage growth bounded without a separate scheduled job.
    let sweepExpired (storage: IBlobStorage) : Async<int> = async {
        do! writeLock.WaitAsync() |> Async.AwaitTask

        try
            let! current = loadFromStore storage
            let compacted, removed = dropExpired DateTime.UtcNow current

            if removed > 0 then
                do! writeAndInvalidate storage compacted

            return removed
        finally
            writeLock.Release() |> ignore
    }

    /// Add or replace the pending entry for `email`. The caller is
    /// responsible for the Owner/Admin gate on the supplied
    /// `pending.TeamId` before invoking this function.
    ///
    /// Opportunistically drops expired entries from the blob in the same
    /// write — keeps storage growth bounded without a separate scheduled
    /// sweep. The full-blob overwrite is the cheap path here (the blob is
    /// flat JSON of typically dozens of entries; the read-compact-write
    /// round-trip is dominated by the storage round-trip regardless).
    let upsert (storage: IBlobStorage) (email: string) (pending: PendingInviteByEmail) : Async<unit> = async {
        do! writeLock.WaitAsync() |> Async.AwaitTask

        try
            let! current = loadFromStore storage
            let compacted, _ = dropExpired DateTime.UtcNow current
            let updated = compacted |> Map.add (email.ToLowerInvariant()) pending
            do! writeAndInvalidate storage updated
        finally
            writeLock.Release() |> ignore
    }

    /// Remove the pending entry for `email` and report whether anything
    /// was removed. Internal — the public `remove` (preserved below)
    /// drops the bool to keep the legacy `unit`-returning shape; the
    /// `IPendingInviteStore.Remove` member reads the bool so it can
    /// distinguish `Ok ()` (entry removed) from `Error NotFound`
    /// (nothing to remove).
    let internal removeReturning (storage: IBlobStorage) (email: string) : Async<bool> = async {
        do! writeLock.WaitAsync() |> Async.AwaitTask

        try
            let! current = loadFromStore storage
            let key = email.ToLowerInvariant()

            if Map.containsKey key current then
                do! writeAndInvalidate storage (Map.remove key current)
                return true
            else
                return false
        finally
            writeLock.Release() |> ignore
    }

    /// Remove the pending entry for `email` if present. Idempotent.
    /// Used by the API surface when an admin wants to cancel a pending
    /// invite before it is consumed.
    let remove (storage: IBlobStorage) (email: string) : Async<unit> = async {
        let! _ = removeReturning storage email
        return ()
    }

    /// List every pending entry — used by the admin UI to render the
    /// Pending Invites tab when the caller wants visibility into
    /// email-keyed (non-link) invitations. Returned as `(email, entry)`
    /// pairs; the caller filters by team where appropriate.
    let listAll (storage: IBlobStorage) : Async<(string * PendingInviteByEmail) list> = async {
        let! current = cachedRead storage
        return current |> Map.toList
    }

    /// Atomically read-and-remove the entry for `email` if present and
    /// not expired. Returns the consumed entry so the caller can act on
    /// it (typically `ITeamStore.AddMember`). On stale (expired) entry,
    /// removes it from the blob and returns `None`.
    let tryConsumeForEmail (storage: IBlobStorage) (email: string) : Async<PendingInviteByEmail option> = async {
        do! writeLock.WaitAsync() |> Async.AwaitTask

        try
            let! current = loadFromStore storage
            let key = email.ToLowerInvariant()

            match Map.tryFind key current with
            | None -> return None
            | Some entry when entry.ExpiresAt < DateTime.UtcNow ->
                do! writeAndInvalidate storage (Map.remove key current)
                return None
            | Some entry ->
                do! writeAndInvalidate storage (Map.remove key current)
                return Some entry
        finally
            writeLock.Release() |> ignore
    }

/// Single-instance, in-memory-cached implementation of
/// `IPendingInviteStore`. Wraps the `PendingInviteStore` module's
/// blob+lock+cache impl; one instance per deployment (a second one
/// against the same `IBlobStorage` would share the module-level
/// semaphore and cache anyway, so multi-instantiation is a code smell
/// rather than a correctness hazard).
///
/// Maps the module-function impl's exception-throwing failure model
/// onto the interface's `Result<_, PendingInviteStoreError>` shape:
/// every method catches `exn` and surfaces `StorageFailed ex.Message`.
/// `Conflict` is never raised by this implementation — single-instance
/// `SemaphoreSlim` serialisation is conflict-free by construction.
/// `Remove` returns `Error NotFound` when no entry was present (the
/// module-function `remove` collapses this to `Ok ()`).
type InMemoryPendingInviteStore(storage: IBlobStorage) =
    interface IPendingInviteStore with
        member _.Upsert(email, pending) = async {
            try
                do! PendingInviteStore.upsert storage email pending
                return Ok()
            with ex ->
                return Error(PendingInviteStoreError.StorageFailed ex.Message)
        }

        member _.Remove(email) = async {
            try
                let! removed = PendingInviteStore.removeReturning storage email

                if removed then
                    return Ok()
                else
                    return Error PendingInviteStoreError.NotFound
            with ex ->
                return Error(PendingInviteStoreError.StorageFailed ex.Message)
        }

        member _.TryConsumeForEmail(email) = async {
            try
                let! result = PendingInviteStore.tryConsumeForEmail storage email
                return Ok result
            with ex ->
                return Error(PendingInviteStoreError.StorageFailed ex.Message)
        }

        member _.ListAll() = async {
            try
                let! entries = PendingInviteStore.listAll storage
                return Ok entries
            with ex ->
                return Error(PendingInviteStoreError.StorageFailed ex.Message)
        }

        member _.SweepExpired() = async {
            try
                let! removed = PendingInviteStore.sweepExpired storage
                return Ok removed
            with ex ->
                return Error(PendingInviteStoreError.StorageFailed ex.Message)
        }
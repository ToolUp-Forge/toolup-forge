// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Teams

open System
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Remoting.Json.SystemTextJson
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

/// Phase 116 — raised by the load path when the persisted
/// `pending-invites.json` blob is present but fails to decode. Carries
/// the path the corrupt bytes were quarantined (renamed) to plus the
/// decode reason. `InMemoryPendingInviteStore` catches it to emit a
/// structured `logger.Error` and surfaces it to callers as
/// `StorageFailed`. Existence (vs. an empty/missing blob) is the load
/// path's signal to fail closed rather than write back a map derived
/// from a failed decode (GP 9).
exception PendingInvitesBlobCorrupt of quarantinePath: string * reason: string

/// Backward-compat shim — module-functions surface preserved verbatim
/// from the pre-Phase-5h shape. New code should resolve
/// `IPendingInviteStore` from DI and call its methods; this surface
/// remains until every in-tree caller has migrated.
module PendingInviteStore =

    let private jsonOptions = FableConverters.create ()

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
        JsonSerializer.Serialize(map, jsonOptions) |> Encoding.UTF8.GetBytes

    /// Decode the persisted blob. An empty / missing blob is the
    /// legitimate zero-state (`Ok Map.empty`); a present-but-unparseable
    /// blob is `Error reason` so the load path can fail closed rather
    /// than silently treating corruption as zero invites (Phase 116 /
    /// GP 9). Previously this collapsed every failure to `Map.empty`,
    /// which — combined with the full-blob-overwrite write path — meant
    /// one corrupt blob irreversibly erased every pending invite on the
    /// next `upsert`.
    let private decodeMap (bytes: byte[]) : Result<Map<string, PendingInviteByEmail>, string> =
        if isNull bytes || bytes.Length = 0 then
            Ok Map.empty
        else
            let json = Encoding.UTF8.GetString bytes

            try
                // 0.4.4 — `Option.ofObj` requires `'T : null`, which F# Map
                // doesn't satisfy. Box then null-check; defensive against
                // a deserialiser that hands back null on malformed input.
                match
                    JsonSerializer.Deserialize<Map<string, PendingInviteByEmail>>(json, jsonOptions)
                    |> box
                with
                | null -> Error "deserialiser returned null for a non-empty pending-invites blob"
                | o -> Ok(o :?> Map<string, PendingInviteByEmail>)
            with ex ->
                Error(sprintf "pending-invites blob failed to deserialise: %s" ex.Message)

    /// Quarantine path for a corrupt blob — a timestamped sibling of the
    /// canonical blob name so an operator can find and recover it. The
    /// canonical name is then freed (renamed-aside) so the store
    /// self-heals to empty on the next read instead of erroring forever.
    let private quarantineBlobName () =
        sprintf "%s.corrupt-%s" blobName (DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"))

    let private loadFromStore (storage: IBlobStorage) : Async<Map<string, PendingInviteByEmail>> = async {
        match! storage.Download(platformContainer, blobName) with
        | Ok bytes ->
            match decodeMap bytes with
            | Ok map -> return map
            | Error reason ->
                // Fail closed (GP 9). Rename the corrupt blob aside so the
                // bytes survive for forensic recovery, then raise so the
                // triggering operation fails WITHOUT writing — a
                // full-blob-overwrite store that proceeded from `Map.empty`
                // here would persist empty-plus-one and erase every other
                // pending invite irreversibly.
                let target = quarantineBlobName ()
                let! _ = storage.Upload(platformContainer, target, bytes)
                let! _ = storage.Delete(platformContainer, blobName)
                return raise (PendingInvitesBlobCorrupt(target, reason))
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

    /// Split the blob into the entries to keep (`ExpiresAt` at or after
    /// `now`) and the expired entries to drop (`ExpiresAt` strictly before
    /// `now`). Pure over the input — callers decide whether to persist the
    /// kept map and whether to emit a `TeamInviteExpired` audit row per
    /// dropped entry (Phase 547). Supersedes the earlier count-only
    /// `dropExpired`: the dropped entries themselves are needed so the
    /// audit hook can name inviter / invitee / team on each one.
    let private partitionExpired
        (now: DateTime)
        (map: Map<string, PendingInviteByEmail>)
        : Map<string, PendingInviteByEmail> * (string * PendingInviteByEmail) list =
        let kept = map |> Map.filter (fun _ entry -> entry.ExpiresAt >= now)

        let expired =
            map |> Map.toList |> List.filter (fun (_, entry) -> entry.ExpiresAt < now)

        kept, expired

    /// No-op expiry hook — the default the backward-compat module surface
    /// (`sweepExpired` / `upsert` / `tryConsumeForEmail`) passes, so a
    /// caller resolving the module functions directly, or an
    /// `IPendingInviteStore` impl composed without an `IAuditLog`, behaves
    /// byte-for-byte as before Phase 547 (GP 11). `InMemoryPendingInviteStore`
    /// supplies a real hook only when an audit log was composed.
    let private noExpiryHook: (string * PendingInviteByEmail) list -> Async<unit> =
        fun _ -> async { return () }

    /// Walk the blob, remove every entry whose `ExpiresAt` is in the past,
    /// persist the compacted map iff anything was removed. Returns the
    /// number of removed entries. Useful as a scheduled background sweep
    /// (via `IJobScheduler`); also invoked opportunistically from `upsert`
    /// to keep storage growth bounded without a separate scheduled job.
    let internal sweepExpiredWith
        (onExpired: (string * PendingInviteByEmail) list -> Async<unit>)
        (storage: IBlobStorage)
        : Async<int> =
        async {
            let! expired = async {
                do! writeLock.WaitAsync() |> Async.AwaitTask

                try
                    let! current = loadFromStore storage
                    let compacted, expired = partitionExpired DateTime.UtcNow current

                    if not (List.isEmpty expired) then
                        do! writeAndInvalidate storage compacted

                    return expired
                finally
                    writeLock.Release() |> ignore
            }

            // Emit AFTER the lock is released — audit `Record` may itself do
            // storage IO (an `IEventStore` append), and holding the
            // pending-invite write-lock across it would serialise every
            // other store write behind audit emission.
            do! onExpired expired
            return List.length expired
        }

    let sweepExpired (storage: IBlobStorage) : Async<int> = sweepExpiredWith noExpiryHook storage

    /// Add or replace the pending entry for `email`. The caller is
    /// responsible for the Owner/Admin gate on the supplied
    /// `pending.TeamId` before invoking this function.
    ///
    /// Opportunistically drops expired entries from the blob in the same
    /// write — keeps storage growth bounded without a separate scheduled
    /// sweep. The full-blob overwrite is the cheap path here (the blob is
    /// flat JSON of typically dozens of entries; the read-compact-write
    /// round-trip is dominated by the storage round-trip regardless).
    let internal upsertWith
        (onExpired: (string * PendingInviteByEmail) list -> Async<unit>)
        (storage: IBlobStorage)
        (email: string)
        (pending: PendingInviteByEmail)
        : Async<unit> =
        async {
            let! expired = async {
                do! writeLock.WaitAsync() |> Async.AwaitTask

                try
                    let! current = loadFromStore storage
                    let compacted, expired = partitionExpired DateTime.UtcNow current
                    let updated = compacted |> Map.add (email.ToLowerInvariant()) pending
                    do! writeAndInvalidate storage updated
                    return expired
                finally
                    writeLock.Release() |> ignore
            }

            // A re-issue to an email whose prior entry had already expired
            // drops that stale entry here — emit its expiry alongside the
            // fresh `TeamInviteIssued` the caller records, so the trail is
            // complete rather than swallowing the lapse.
            do! onExpired expired
        }

    let upsert (storage: IBlobStorage) (email: string) (pending: PendingInviteByEmail) : Async<unit> =
        upsertWith noExpiryHook storage email pending

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
    let internal tryConsumeForEmailWith
        (onExpired: (string * PendingInviteByEmail) list -> Async<unit>)
        (storage: IBlobStorage)
        (email: string)
        : Async<PendingInviteByEmail option> =
        async {
            let! result, expired = async {
                do! writeLock.WaitAsync() |> Async.AwaitTask

                try
                    let! current = loadFromStore storage
                    let key = email.ToLowerInvariant()

                    match Map.tryFind key current with
                    | None -> return None, []
                    | Some entry when entry.ExpiresAt < DateTime.UtcNow ->
                        // The sign-in matched a pending entry that had
                        // already lapsed — drop it AND surface the expiry
                        // as an audit row. This is the worst silent path:
                        // the invitee lands in neither Members nor Pending
                        // Invites, so without the row nobody is told.
                        do! writeAndInvalidate storage (Map.remove key current)
                        return None, [ key, entry ]
                    | Some entry ->
                        do! writeAndInvalidate storage (Map.remove key current)
                        return Some entry, []
                finally
                    writeLock.Release() |> ignore
            }

            do! onExpired expired
            return result
        }

    let tryConsumeForEmail (storage: IBlobStorage) (email: string) : Async<PendingInviteByEmail option> =
        tryConsumeForEmailWith noExpiryHook storage email

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
///
/// Phase 116 — takes an `ILogger` so a quarantined-corrupt-blob event
/// (raised as `PendingInvitesBlobCorrupt` by the load path) is surfaced
/// at `Error` level rather than disappearing into a generic
/// `StorageFailed` string.
type InMemoryPendingInviteStore(storage: IBlobStorage, logger: ILogger, auditLog: IAuditLog option) =

    /// Phase 547 — per-expiry audit hook. Emits one `TeamInviteExpired`
    /// under the entry's `team-{TeamId}` scope for every entry a sweep
    /// drops. Best-effort: a throwing `IAuditLog.Record` (misconfigured
    /// sink, unreachable `IEventStore`) must never fail the sweep — the
    /// entry is already durably gone — so each emission is guarded and a
    /// failure degrades to a `Warn`. No audit log composed → no-op (GP 11
    /// / GP 13).
    let onExpired: (string * PendingInviteByEmail) list -> Async<unit> =
        match auditLog with
        | None -> fun _ -> async { return () }
        | Some log ->
            fun expired -> async {
                for email, entry in expired do
                    try
                        do!
                            log.Record(
                                $"team-{entry.TeamId}",
                                TeamInviteExpired {
                                    TeamId = entry.TeamId
                                    InviteeEmail = email
                                    InviterUserId = entry.InviterUserId
                                    Role = entry.Role
                                    IssuedAt = entry.IssuedAt
                                    ExpiredAt = entry.ExpiresAt
                                }
                            )
                    with ex ->
                        logger.Warn(
                            sprintf
                                "[PendingInviteStore] TeamInviteExpired audit emission failed for team %s: %s"
                                entry.TeamId
                                ex.Message
                        )
            }

    /// Map a load-path failure onto the interface's error shape. A
    /// `PendingInvitesBlobCorrupt` is logged at `Error` (operator must
    /// recover the quarantined blob); every other exception collapses to
    /// `StorageFailed ex.Message` as before.
    let toStorageError (op: string) (ex: exn) : PendingInviteStoreError =
        match ex with
        | PendingInvitesBlobCorrupt(path, reason) ->
            logger.Error(
                sprintf
                    "[PendingInviteStore] %s aborted: pending-invites blob was corrupt and has been quarantined to %s (%s). No invites were overwritten; the store self-heals to empty on the next read."
                    op
                    path
                    reason,
                None
            )

            PendingInviteStoreError.StorageFailed(sprintf "pending-invites blob was corrupt (quarantined to %s)" path)
        | _ -> PendingInviteStoreError.StorageFailed ex.Message

    /// Phase 5h backward-compatible 2-arg constructor — composes the store
    /// without an audit log, so the expiry sweep stays silent exactly as
    /// before Phase 547 (GP 11). `ComposeTeamRuntime` uses the 3-arg form
    /// to wire the resolved `IAuditLog` when one is present.
    new(storage: IBlobStorage, logger: ILogger) = InMemoryPendingInviteStore(storage, logger, None)

    interface IPendingInviteStore with
        member _.Upsert(email, pending) = async {
            try
                do! PendingInviteStore.upsertWith onExpired storage email pending
                return Ok()
            with ex ->
                return Error(toStorageError "Upsert" ex)
        }

        member _.Remove(email) = async {
            try
                let! removed = PendingInviteStore.removeReturning storage email

                if removed then
                    return Ok()
                else
                    return Error PendingInviteStoreError.NotFound
            with ex ->
                return Error(toStorageError "Remove" ex)
        }

        member _.TryConsumeForEmail(email) = async {
            try
                let! result = PendingInviteStore.tryConsumeForEmailWith onExpired storage email
                return Ok result
            with ex ->
                return Error(toStorageError "TryConsumeForEmail" ex)
        }

        member _.ListAll() = async {
            try
                let! entries = PendingInviteStore.listAll storage
                return Ok entries
            with ex ->
                return Error(toStorageError "ListAll" ex)
        }

        member _.SweepExpired() = async {
            try
                let! removed = PendingInviteStore.sweepExpiredWith onExpired storage
                return Ok removed
            with ex ->
                return Error(toStorageError "SweepExpired" ex)
        }
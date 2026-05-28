// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── IPendingInviteStore ─────────────────────────────────────────────
//
// SDK-level interface for the email-keyed pending-invitation substrate
// (Phase 5h). The default impl is `InMemoryPendingInviteStore` — a
// single-instance blob+lock+cache impl carried forward from Phase 3d.
// A future `BlobPendingInviteStore` (deferred until forge Phase 9c
// half-2 ships `IBlobStorage.UploadWithETag`) binds to this interface
// without touching call sites; an optional `RedisPendingInviteCache`
// decorator may also bind here.
//
// **Six portability rules** (all honoured):
//
//   1. Identity by value. Email + team id are strings; the
//      `PendingInviteByEmail` record carries only string-valued + DU
//      fields. No `IActorRef`, `IGrainReference`, or other live handles.
//   2. Async at every boundary. Every method returns `Async<Result<_>>`.
//   3. Retry / supervision as data. Failures returned as
//      `PendingInviteStoreError`; no `OnFailure` callbacks. Callers
//      decide whether to retry, surface to UI, or drop the operation.
//   4. Stateless handlers between invocations. Implementations may
//      hold a process-local read-cache for throughput (the `InMemory`
//      default does — 30-second TTL), but every method's correctness
//      derives from the persisted store, not from in-memory continuity
//      between calls. A distributed impl that wants per-node correctness
//      drops the cache entirely (the future `BlobPendingInviteStore`
//      makes that trade explicitly).
//   5. No cross-shard ordering promises. Each email-keyed entry is
//      independent.
//   6. Precision N/A — pending-invite validity is timestamp-bounded,
//      not tick-driven.

/// Failure cases an `IPendingInviteStore` implementation can return.
/// Implementations map their substrate-specific failures into one of
/// these three constructors so the caller's match is the same regardless
/// of which backend is configured.
///
/// `[<RequireQualifiedAccess>]` because the case names (`NotFound`,
/// `Conflict`, `StorageFailed`) collide with sibling DUs in the SDK
/// (`DataObjectError`, `ShareTokenError`, etc.). Callers say
/// `PendingInviteStoreError.NotFound` to disambiguate.
[<RequireQualifiedAccess>]
type PendingInviteStoreError =
    /// Optimistic-concurrency conflict — applies to ETag-aware
    /// implementations (the future `BlobPendingInviteStore`). The
    /// substrate observed a concurrent update between read and write;
    /// the caller's retry budget is exhausted. `InMemoryPendingInviteStore`
    /// serialises writes via a process-local semaphore and never raises
    /// this case.
    | Conflict
    /// Underlying storage call failed (blob storage 5xx, network error,
    /// serialisation defect). The caller decides whether to retry or
    /// surface a user-visible error. The string carries a short
    /// human-readable summary suitable for logging.
    | StorageFailed of string
    /// `Remove` invoked for an email that has no pending entry. Returned
    /// rather than `Ok` so callers distinguishing "we removed something"
    /// from "there was nothing to remove" (e.g. audit emission paths)
    /// can branch. Idempotent callers treat this as success.
    | NotFound

/// Email-keyed pending-invitation substrate. The store persists a flat
/// `email -> PendingInviteByEmail` map. Two principal access patterns:
///
/// * **Admin-write path.** `Upsert` adds or replaces an entry; `Remove`
///   deletes it; `ListAll` enumerates for admin UIs.
/// * **Sign-in-resolve path.** `TryConsumeForEmail` atomically reads
///   and removes the entry on every authenticated sign-in resolve.
///
/// `SweepExpired` is a periodic compaction — used both as a scheduled
/// background sweep (via `IJobScheduler`) and opportunistically from
/// `Upsert` to keep storage growth bounded.
type IPendingInviteStore =
    /// Add or replace the pending entry for `email`. Caller is
    /// responsible for the Owner/Admin gate on the `pending.TeamId`
    /// before invoking. Returns `Ok ()` on success; `StorageFailed` /
    /// `Conflict` on substrate failure.
    abstract Upsert: email: string * pending: PendingInviteByEmail -> Async<Result<unit, PendingInviteStoreError>>

    /// Remove the pending entry for `email`. Returns `Ok ()` when an
    /// entry was found and removed; `Error NotFound` when no entry was
    /// present (idempotent callers may collapse this to success).
    /// `Conflict` / `StorageFailed` on substrate failure.
    abstract Remove: email: string -> Async<Result<unit, PendingInviteStoreError>>

    /// Atomically read-and-remove the entry for `email` if present and
    /// not expired. Returns `Ok (Some entry)` on a live entry; `Ok None`
    /// when no entry matches or the entry was stale (expired entries
    /// are dropped from the store as a side-effect). `Conflict` /
    /// `StorageFailed` on substrate failure.
    abstract TryConsumeForEmail: email: string -> Async<Result<PendingInviteByEmail option, PendingInviteStoreError>>

    /// List every pending entry. Used by the admin UI to render the
    /// pending-invites tab. Returned as `(email, entry)` pairs; the
    /// caller filters by team where appropriate. `StorageFailed` on
    /// substrate failure.
    abstract ListAll: unit -> Async<Result<(string * PendingInviteByEmail) list, PendingInviteStoreError>>

    /// Drop every entry whose `ExpiresAt` is in the past. Returns the
    /// count of removed entries on success. Cheap when nothing is
    /// expired (a single read, no write). `Conflict` / `StorageFailed`
    /// on substrate failure.
    abstract SweepExpired: unit -> Async<Result<int, PendingInviteStoreError>>
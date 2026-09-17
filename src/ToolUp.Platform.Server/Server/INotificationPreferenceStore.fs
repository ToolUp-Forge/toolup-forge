// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 441 — INotificationPreferenceStore ────────────────────────
//
// Persistence seam for the notification-preference substrate: each
// user's `UserNotificationPreferences` record, the per-user queue of
// held-back sends the send-path filter writes and the digest job
// drains, and the per-user digest watermarks the job uses to decide
// when a bucket is due.
//
// **Team-scoped structurally (GP 4).** Every member takes `scopeId`
// first and an implementation derives every storage key from it, so a
// handler cannot read another scope's preferences by accident. The one
// cross-scope member, `ListScopesWithPending`, exists for the digest
// job — which runs under the reserved `_platform` scope and has to
// discover which scopes hold work — and mirrors
// `IJobStore.ListScopesWithJobs` in shape and justification.
//
// **The pending queue is append-only from the filter's side.** The
// filter only ever `Enqueue`s; the job `ListPending`s, sends, then
// `Remove`s by id. An implementation keeps those two writers
// contention-free by giving each queued item its own blob (the default
// `BlobNotificationPreferenceStore` does), so a filter write during a
// drain is never lost to a read-modify-write race. Watermarks are
// written by the job alone.
//
// **Portability (GP 12).** No handler state between calls; every
// member is safe to call from any process; ids are the caller's
// (`PendingNotification.Id`), never store-minted, so a re-run after a
// crash can `Remove` exactly what it already sent.

/// Persistence seam for per-user notification preferences, the held
/// sends awaiting digest or quiet-hours release, and digest watermarks.
type INotificationPreferenceStore =
    /// The user's stored record, or `UserNotificationPreferences.empty`
    /// when nothing has been saved. `Error` only on a storage failure —
    /// an absent record is the common case and is not an error.
    abstract GetPreferences: scopeId: string * userId: string -> Async<Result<UserNotificationPreferences, string>>

    /// Replace the user's record wholesale. The caller validates
    /// (`UserNotificationPreferences.validate`) before saving.
    abstract SavePreferences:
        scopeId: string * userId: string * preferences: UserNotificationPreferences -> Async<Result<unit, string>>

    /// Hold one send for `item.UserId`. Idempotent on `item.Id`: a
    /// second enqueue of the same id overwrites, never duplicates.
    abstract Enqueue: scopeId: string * item: PendingNotification -> Async<Result<unit, string>>

    /// Every held item for the user, oldest first. Empty when none.
    abstract ListPending: scopeId: string * userId: string -> Async<PendingNotification list>

    /// Drop held items by id after they have been sent. Idempotent —
    /// an id that is already gone is not an error.
    abstract Remove: scopeId: string * userId: string * ids: Guid list -> Async<Result<unit, string>>

    /// Users in the scope with at least one held item.
    abstract ListPendingUsers: scopeId: string -> Async<string list>

    /// Scopes with at least one held item — the digest job's work
    /// discovery. Cross-scope by design; callable only from the
    /// `_platform` scope (the job), never from a request handler.
    abstract ListScopesWithPending: unit -> Async<string list>

    /// UTC instant each digest frequency was last sent to the user,
    /// keyed by `DigestFrequency.toWireString`. Empty when never sent.
    abstract GetDigestWatermarks: scopeId: string * userId: string -> Async<Map<string, DateTime>>

    /// Record that a digest of `frequency` was sent to the user at
    /// `sentAt` (UTC). Replaces the prior watermark for that frequency.
    abstract SetDigestWatermark:
        scopeId: string * userId: string * frequency: DigestFrequency * sentAt: DateTime -> Async<Result<unit, string>>
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 9i — SDK-wide cross-instance lease primitive ──────────────
//
// `IDistributedLock` is the *infrastructure mutex*: the one seam every
// subsystem that needs single-leader / exclusive-writer semantics
// acquires through, so a distributed deployment composes cross-instance
// exclusion once instead of each subsystem re-deriving its own
// process-local `SemaphoreSlim`. Before this phase, three did exactly
// that — the job scheduler's per-`JobId` dispatch mutex, the webhook
// retry path, and the blob-backed Platform-Admin store's
// read-modify-write serialisation — and every one of them silently
// stopped excluding anything the moment a second replica appeared.
//
// **Deliberately distinct from its two neighbours:**
//   * `IEntityLockStore` (Phase 442) is an *advisory* soft-lock — a UI
//     awareness signal ("Bob is editing this"), never a correctness
//     barrier. It never blocks and returns the current holder on
//     conflict.
//   * `ILifecycleLock` (Phase 54h) is the *tenant-offboard* exclusion
//     lock, scoped to one caller and keyed by scope id, with an opaque
//     `IDisposable` lease and no fence token. It predates this seam and
//     stays as it is — a narrower contract with one consumer.
//   This one is the general-purpose primitive: a value-typed `Lease`
//   carrying a monotonic fence token, explicit `Renew`, and a TTL that
//   is data rather than a callback.
//
// **Fence tokens — why the lease is not just a boolean.** A lease can
// lapse while its holder is still running (a GC pause, a paused VM, a
// slow store round-trip), so "I hold the lock" is never safe to assume
// for the whole of a critical section. `FenceToken` strictly increases
// per `LockId` across acquisitions, so a downstream store CAN be made
// safe: it records the highest token it has seen for a resource and
// rejects any write carrying a lower one. The stale holder's late write
// is then refused by the store rather than silently interleaved. This is
// the standard fencing pattern (Kleppmann); the token is useless if the
// downstream write path ignores it, so a subsystem adopting the lock for
// correctness (not just contention reduction) should thread the token
// into its write.
//
// **Six-rule portability audit (Phase 9c, GP 12):**
//   1. Identity by value      — `lockId` is a string, `FenceToken` an
//                               int64, `Lease` an immutable record. No
//                               live handle (`IActorRef`,
//                               `IGrainReference`) crosses the seam, so
//                               a lease survives serialisation and can
//                               be handed to another process.
//   2. Async at every boundary — all three methods return `Async<_>`.
//   3. Retry/supervision as data — the TTL is a `TimeSpan` parameter and
//                               contention is a value (`None`); there is
//                               no `OnLost: exn -> unit` callback and no
//                               framework-owned watchdog.
//   4. Stateless between calls — every call carries its full state
//                               (`lockId` + `ttl`, or the `Lease`
//                               itself), so a store-backed impl
//                               round-trips and any replica observes any
//                               other's hold. Nothing is closure-captured
//                               between invocations.
//   5. No cross-shard ordering — fence tokens are monotonic *per
//                               `LockId`*. No ordering is promised
//                               between two different lock ids, and an
//                               impl is free to use a per-key counter.
//   6. Precision at lower bound — the TTL is the declared precision
//                               contract, honoured at the backing
//                               store's resolution (Redis `PX` →
//                               millisecond). Sub-millisecond expiry is
//                               not promised by any implementation.

/// Phase 9i — a held lease on a distributed lock. Immutable and
/// identity-by-value (GP 12 rule 1), so it can be logged, persisted, or
/// handed across a process boundary and still released by whoever holds
/// the record.
///
/// * `LockId` — the lock this lease holds. The same string the holder
///   passed to `TryAcquire`.
/// * `FenceToken` — strictly increasing per `LockId` across successful
///   acquisitions, and *stable across `Renew`* (renewing extends the same
///   hold, it does not start a new one). Thread it into downstream writes
///   to make a lapsed-lease write refusable; see the fencing note above.
/// * `AcquiredAt` — UTC instant the hold began. Unchanged by `Renew`, so
///   `ExpiresAt - AcquiredAt` is NOT the TTL after a renewal; use
///   `Lease.remaining`.
/// * `ExpiresAt` — UTC instant the hold lapses if not renewed or
///   released. **Authoritative**: an implementation returns the expiry it
///   actually recorded, which may be earlier than `now + ttl` if the
///   store clamps it.
type Lease = {
    LockId: string
    FenceToken: int64
    AcquiredAt: DateTime
    ExpiresAt: DateTime
}

/// Phase 9i — cross-instance lease primitive. Acquire is **fail-fast**:
/// `TryAcquire` returns `None` immediately when another holder has the
/// id, so the loser decides what to do (skip this tick, return "already
/// running", or poll — see `DistributedLock.acquireBlocking`) instead of
/// having a blocking wait imposed on it. Contention is a value, never a
/// callback (GP 12 rule 3).
///
/// **Every implementation must satisfy `IDistributedLockContract`:**
/// acquire succeeds on a free id; a second same-id acquire while held
/// returns `None`; a lease past its TTL is reclaimable by anyone (so a
/// crashed holder never deadlocks the id forever); fence tokens strictly
/// increase per id; and `Release` returns the id to the pool
/// immediately.
///
/// **`Release` and `Renew` are holder-checked and never throw on loss.**
/// Both compare the caller's `FenceToken` against the current holder's:
/// a lease that already lapsed and was re-acquired by someone else is
/// never released out from under the new holder, and never renewed back
/// into existence.
type IDistributedLock =
    /// Try to take `lockId` for `ttl`. `Some lease` ⇒ the caller holds it
    /// until `lease.ExpiresAt` (or until `Release`); `None` ⇒ another
    /// holder has it and the caller must NOT proceed. Never blocks
    /// waiting for a holder to finish.
    ///
    /// `ttl` MUST exceed the worst-case duration of the critical section,
    /// or the lease lapses mid-work and a second holder is admitted.
    /// Either budget generously or `Renew` on a heartbeat.
    abstract TryAcquire: lockId: string * ttl: TimeSpan -> Async<Lease option>

    /// Extend a held lease by the TTL it was acquired with, measured from
    /// now. Returns the extended lease (same `LockId`, same `FenceToken`,
    /// same `AcquiredAt`, later `ExpiresAt`).
    ///
    /// **A lease that could not be renewed comes back UNCHANGED** — the
    /// hold was already lost (lapsed, released, or taken by a higher
    /// fence token), and the implementation does not resurrect it. There
    /// is no error case to catch: the caller checks the *returned*
    /// lease with `Lease.isLive` (or compares `ExpiresAt`) and stops
    /// working if the renewal did not move. Signalling loss by return
    /// value rather than exception keeps the seam framework-neutral.
    abstract Renew: lease: Lease -> Async<Lease>

    /// Release a held lease, returning `lease.LockId` to the pool
    /// immediately. Idempotent and holder-checked: releasing twice, or
    /// releasing a lease whose hold has already lapsed and been taken by
    /// a later fence token, is a silent no-op rather than an error — so a
    /// `finally` block can always call it unconditionally.
    abstract Release: lease: Lease -> Async<unit>

/// Lease inspection helpers. Pure timestamp math against the lease
/// record — no store round-trip, so a holder can cheaply re-check its
/// own liveness inside a long critical section (GP 13: costs nothing).
module Lease =
    /// True while the lease has not lapsed at `now`. The holder's own
    /// liveness check; also how a `Renew` result is read (an unchanged
    /// expiry that has already passed means the hold was lost).
    let isLiveAt (now: DateTime) (lease: Lease) : bool = lease.ExpiresAt > now

    /// `isLiveAt DateTime.UtcNow`.
    let isLive (lease: Lease) : bool = isLiveAt DateTime.UtcNow lease

    /// How long the hold has left at `now`; `TimeSpan.Zero` once lapsed
    /// (never negative, so it is safe to use as a delay).
    let remainingAt (now: DateTime) (lease: Lease) : TimeSpan =
        let left = lease.ExpiresAt - now
        if left > TimeSpan.Zero then left else TimeSpan.Zero

    /// `remainingAt DateTime.UtcNow`.
    let remaining (lease: Lease) : TimeSpan = remainingAt DateTime.UtcNow lease

    /// The TTL the lease was originally acquired with — the window
    /// `Renew` extends by. Derived from the acquire instant, so it is
    /// stable across renewals (which move `ExpiresAt` but not
    /// `AcquiredAt`) only for the FIRST window; implementations carry the
    /// original TTL themselves and this is the fallback an impl-agnostic
    /// caller uses.
    let originalTtl (lease: Lease) : TimeSpan = lease.ExpiresAt - lease.AcquiredAt

/// Impl-agnostic helpers over `IDistributedLock`. Everything here is
/// written against the interface alone, so it works identically on the
/// in-process default and on any companion.
module DistributedLock =
    /// Poll interval `acquireBlocking` uses when no explicit one is
    /// given. Short enough that an in-process hand-off feels immediate,
    /// long enough that a store-backed impl is not hammered.
    let defaultPollInterval = TimeSpan.FromMilliseconds 25.0

    /// Wait until `lockId` is held, polling `TryAcquire` at
    /// `pollInterval`.
    ///
    /// **This is the migration shim for a `SemaphoreSlim.WaitAsync` call
    /// site**, not the preferred shape for new code. The primitive is
    /// fail-fast on purpose (a caller that cannot proceed usually wants
    /// to skip, not to queue); a poll loop reintroduces unbounded waiting
    /// and, on a store-backed impl, one round-trip per interval. Prefer
    /// `TryAcquire` + an explicit `None` branch wherever the caller can
    /// sensibly do something else.
    let acquireBlockingEvery
        (pollInterval: TimeSpan)
        (lck: IDistributedLock)
        (lockId: string)
        (ttl: TimeSpan)
        : Async<Lease> =
        async {
            let mutable held: Lease option = None

            while held.IsNone do
                match! lck.TryAcquire(lockId, ttl) with
                | Some lease -> held <- Some lease
                | None -> do! Async.Sleep pollInterval

            return held.Value
        }

    /// `acquireBlockingEvery defaultPollInterval`.
    let acquireBlocking (lck: IDistributedLock) (lockId: string) (ttl: TimeSpan) : Async<Lease> =
        acquireBlockingEvery defaultPollInterval lck lockId ttl

    /// Release from a **synchronous** context — a `finally` block, a
    /// `Dispose`, any place that cannot `do!`.
    ///
    /// Best-effort and non-throwing by construction: the in-process
    /// default completes inline (its `Release` has no awaits, so
    /// `StartImmediate` runs it to completion synchronously), while a
    /// store-backed impl finishes on a continuation. A failed release
    /// costs only the lease's remaining TTL, which the next acquire
    /// reclaims — the same trade `RedisLifecycleLock.Dispose` documents —
    /// so `onError` is for observability, never for control flow.
    let releaseDetached (onError: exn -> unit) (lck: IDistributedLock) (lease: Lease) : unit =
        Async.StartImmediate(
            async {
                try
                    do! lck.Release lease
                with ex ->
                    onError ex
            }
        )

    /// Run `body` while holding `lockId`, releasing on every exit path
    /// including exceptions. Waits for the lock (see `acquireBlocking`'s
    /// caveat) — use `TryAcquire` directly when the caller has a sensible
    /// `None` branch.
    ///
    /// The release is awaited rather than detached, and the original
    /// exception is re-raised with its stack intact via
    /// `ExceptionDispatchInfo` (a plain `raise` inside an async would
    /// reset it).
    let withLease (lck: IDistributedLock) (lockId: string) (ttl: TimeSpan) (body: Lease -> Async<'T>) : Async<'T> = async {
        let! lease = acquireBlocking lck lockId ttl
        let! outcome = Async.Catch(body lease)
        do! lck.Release lease

        match outcome with
        | Choice1Of2 value -> return value
        | Choice2Of2 ex ->
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
            return Unchecked.defaultof<'T> // unreachable — Throw() does not return
    }
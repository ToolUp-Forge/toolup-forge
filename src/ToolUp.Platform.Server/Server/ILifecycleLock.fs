// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Threading

// ─── Phase 54h — distributed offboard exclusion lock ─────────────────
//
// Phase 54's `runGuarded` serialises concurrent offboards of one tenant
// scope via a process-local `SemaphoreSlim` — correct for a single
// process, but two replicas behind a load balancer can each start an
// offboard of the same tenant and interleave (double crypto-shred,
// racing erasures). `ILifecycleLock` lifts that exclusion behind a
// portable seam so a distributed deployment composes a cross-replica lock
// (the Redis reference impl) without touching the aggregator; the audit
// trail stays the cross-replica record of truth regardless.
//
// **Six-rule portability audit (Phase 9c, GP 12):**
//   1. Identity by value      — `scopeId` is a string; the lease is an
//                               opaque `IDisposable`, never a live remote
//                               handle (`IGrainReference` / `IActorRef`).
//   2. Async at every boundary — `Acquire` returns `Async<_>`.
//   3. Retry/supervision as data — contention is a value (`None`), not a
//                                  callback; the caller decides what to do.
//   4. Stateless between calls — the distributed impl round-trips to its
//                                store every acquire, so any replica sees
//                                what any other wrote; no in-memory
//                                cross-call state.
//   5. No cross-shard ordering — exclusion is per `scopeId`; no ordering
//                                promised across scopes.
//   6. Precision at lower bound — the lease TTL is the declared precision;
//                                an impl honours it at its store's
//                                resolution (Redis `PX` → millisecond).

/// Cross-replica exclusion lock for a tenant lifecycle scope (Phase 54h).
///
/// `Acquire scopeId` returns `Some lease` once the caller holds `scopeId`
/// *exclusively* — the caller runs the offboard and disposes the lease to
/// release. `None` means another holder currently has the scope, so the
/// caller must NOT proceed (a concurrent replica is mid-offboard).
///
/// An implementation MAY enforce exclusion either way:
///   * the in-process default (`InProcessLifecycleLock`) *blocks* until the
///     scope frees and then returns `Some` — it never returns `None`, so a
///     single-instance deployment serialises same-scope offboards exactly
///     as Phase 54's `SemaphoreSlim` did;
///   * the distributed default (the Redis reference impl) *try-acquires*
///     and returns `None` immediately when another replica holds the
///     scope, so the loser gets a prompt "offboard already in progress"
///     rather than a web request blocked for the minutes a cross-store
///     erasure can take.
///
/// **Lease-with-expiry.** A lease auto-expires after the implementation's
/// lease TTL, so a holder that crashes mid-offboard (its process dies, a
/// replica is killed) never deadlocks the scope forever — the next acquire
/// after the TTL succeeds. Disposing the lease releases early. In-process
/// the TTL is moot (a crashed holder is a dead process, and its semaphores
/// die with it), so the default uses an unbounded lease; the distributed
/// impl maps the TTL to its store's native expiry.
type ILifecycleLock =
    /// Try to take exclusive hold of `scopeId`. `Some lease` ⇒ the caller
    /// now holds it (dispose to release). `None` ⇒ another holder has it;
    /// do not proceed. May block until the scope frees (serialising
    /// default) or return `None` promptly (distributed default) — see the
    /// type doc.
    abstract Acquire: scopeId: string -> Async<IDisposable option>

// ─── in-process default ──────────────────────────────────────────────

/// Phase 54h — process-local `ILifecycleLock`: the zero-dependency floor,
/// and the default `runGuarded` uses when no distributed lock is composed.
/// Wraps the same per-scope `SemaphoreSlim(1, 1)` Phase 54's `runGuarded`
/// held, so a single-instance deployment serialises concurrent same-scope
/// offboards byte-identically: `Acquire` *blocks* on the scope's semaphore
/// and then returns `Some lease` — it never returns `None`, so a guarded
/// run under it always proceeds (Phase 54 behaviour), one-at-a-time per
/// scope. Different scopes never contend.
///
/// Per-scope semaphores are keyed by scope id and never removed — the
/// count of distinct scopes a process offboards in its lifetime is bounded
/// and small, so the leak is immaterial and removal would race the acquire
/// path (the rationale the aggregator's inline dictionary carried before
/// this lifted it behind the interface).
type InProcessLifecycleLock() =
    let scopeLocks = ConcurrentDictionary<string, SemaphoreSlim>()

    let lockFor (scopeId: string) : SemaphoreSlim =
        scopeLocks.GetOrAdd(scopeId, (fun _ -> new SemaphoreSlim(1, 1)))

    interface ILifecycleLock with
        member _.Acquire(scopeId: string) : Async<IDisposable option> = async {
            let gate = lockFor scopeId
            do! gate.WaitAsync() |> Async.AwaitTask

            // Release-once lease — disposing returns the slot. Guarded so a
            // double-dispose (the aggregator's `finally` plus a defensive
            // caller) never over-releases the semaphore past its max count.
            let released = ref 0

            let lease =
                { new IDisposable with
                    member _.Dispose() =
                        if Interlocked.Exchange(released, 1) = 0 then
                            gate.Release() |> ignore
                }

            return Some lease
        }

module InProcessLifecycleLock =
    /// A fresh process-local lock with its own per-scope semaphore table.
    /// Share ONE instance across the call sites that must mutually
    /// exclude — two instances have disjoint tables and so don't exclude
    /// each other.
    let create () : ILifecycleLock =
        InProcessLifecycleLock() :> ILifecycleLock

    /// The shared default backing `runGuarded` — one per process, so every
    /// inline guarded offboard contends on the same per-scope semaphores
    /// (the process-wide exclusion Phase 54's module-level dictionary gave).
    let shared: ILifecycleLock = create ()
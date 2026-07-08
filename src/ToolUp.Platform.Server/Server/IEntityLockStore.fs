// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── IEntityLockStore — Phase 442 ────────────────────────────────────
//
// Advisory, TTL-leased soft-locks over entities, keyed by value
// (`EntityLockRef`). "Advisory" — a lock is a UI-level *awareness*
// signal ("Bob is editing this — read-only for you"), NOT a
// correctness barrier. It is deliberately distinct from Phase 9i's
// `IDistributedLock`, the *infrastructure* mutex that serialises
// critical sections across processes: that one blocks and guarantees
// mutual exclusion; this one never blocks, returns the current holder on
// conflict, and lets the client decide what "read-only" means. Distinct
// consumers, distinct guarantees.
//
// No background sweeper (GP 13): a lease's liveness is pure timestamp
// math (`EntityLock.isExpired`). An expired lease is simply not returned
// as a holder and is re-acquirable by anyone; the `Expired` event is
// emitted lazily the next time an `Acquire` / `GetHolder` observes the
// stale lease. Deployments that never touch a stale lock pay nothing.
//
// Scope isolation (GP 4): every lease lives under the caller's `scopeId`
// and lock events publish on that same scope, so a lock's holder is only
// ever revealed to that team. Portability (GP 12): identity by value,
// async at every boundary, no ordering promise across refs, stateless
// between calls.
//
// Composition: `ServerConfig.Presence = EnabledPresence` registers the
// in-memory default into DI (see `ComposeNotifications`); `NoPresence`
// (the default) registers nothing. The SDK mounts no lock HTTP surface —
// a deployment exposes its own module-owned API over the resolved store
// (the `useEntityLock` hook's `LockTransport` is consumer-supplied to
// match).

/// Scope-isolated advisory soft-lock store. `Acquire` grants a lease or
/// returns the current holder (never blocks); `Renew` extends the
/// caller's own lease (or re-acquires a lost one); `Release` frees the
/// caller's own lease; `GetHolder` reads the live holder, if any.
type IEntityLockStore =
    /// Try to take (or refresh the caller's own) advisory lease over
    /// `ref` for `ttl`. Returns `Acquired` with the new / refreshed
    /// lease when the slot is free, expired, or already the caller's;
    /// `HeldByOther` with the live lease when a *different* user holds
    /// it. Never blocks (GP 12).
    abstract Acquire: scopeId: string * ref: EntityLockRef * holder: string * ttl: TimeSpan -> Async<LockOutcome>
    /// Extend the caller's own live lease by `ttl` (the client's
    /// auto-renew path). Behaves like `Acquire` when the lease has
    /// lapsed or was never held — re-acquires a free / expired slot,
    /// returns `HeldByOther` if someone else took it in the meantime.
    abstract Renew: scopeId: string * ref: EntityLockRef * holder: string * ttl: TimeSpan -> Async<LockOutcome>
    /// Release the caller's own lease (announces `Released`). Idempotent
    /// — releasing an absent lease, or one held by a different user, is
    /// a no-op.
    abstract Release: scopeId: string * ref: EntityLockRef * holder: string -> Async<unit>
    /// The current live holder of `ref`, or `None` when free or expired.
    /// Observing an expired lease frees it and emits `Expired` lazily
    /// (no background service).
    abstract GetHolder: scopeId: string * ref: EntityLockRef -> Async<LockLease option>

/// Dev / single-instance `IEntityLockStore`. Holds leases in process
/// memory — correct for a single node, NOT shared across replicas. A
/// multi-instance deployment supplies a distributed implementation (over
/// a shared store / the distributed `INotificationChannel` companion)
/// with no change to consuming code; the in-memory store is flagged
/// single-instance to the Phase 9c distributed-companion family.
///
/// `now` is injectable for deterministic tests. Mutations serialise on
/// the per-scope map so an acquire-vs-acquire race resolves to exactly
/// one winner; events publish best-effort outside the critical section.
type InMemoryEntityLockStore(channel: INotificationChannel, ?now: unit -> DateTime) =
    let clock = defaultArg now (fun () -> DateTime.UtcNow)
    let jsonOptions = FableConverters.create ()

    // scopeId -> (ref -> lease). Per-scope shard; a holder read touches
    // only the requested scope's inner map (GP 4).
    let scopes =
        ConcurrentDictionary<string, ConcurrentDictionary<EntityLockRef, LockLease>>()

    let scopeMap (scopeId: string) =
        scopes.GetOrAdd(scopeId, fun _ -> ConcurrentDictionary<EntityLockRef, LockLease>())

    let publish (scopeId: string) (change: LockChange) (lease: LockLease) = async {
        try
            let event: LockEvent = { Change = change; Lease = lease }
            let payloadJson = JsonSerializer.Serialize(event, jsonOptions)
            do! channel.Publish(scopeId, CustomNotification(CollaborationTopics.Lock, payloadJson))
        with _ ->
            // Best-effort fan-out — a channel failure must not fail the
            // lease mutation the caller already observed.
            ()
    }

    // Shared by Acquire + Renew — the take/refresh/conflict decision.
    // Returns (outcome, expired-lease-to-announce, taken-lease-to-announce);
    // the mutation happens under the per-scope lock, the announcements
    // outside it.
    let acquireCore (scopeId: string) (ref: EntityLockRef) (holder: string) (ttl: TimeSpan) =
        let now = clock ()
        let m = scopeMap scopeId

        lock m (fun () ->
            let fresh () = {
                Ref = ref
                Holder = holder
                AcquiredAt = now
                ExpiresAt = now + ttl
            }

            match m.TryGetValue ref with
            | true, existing when EntityLock.isLive now existing ->
                if existing.Holder = holder then
                    // Refresh the caller's own live lease — no new Taken event.
                    let lease = {
                        existing with
                            AcquiredAt = now
                            ExpiresAt = now + ttl
                    }

                    m[ref] <- lease
                    LockOutcome.Acquired lease, None, None
                else
                    // Live lease held by someone else — conflict, never blocks.
                    LockOutcome.HeldByOther existing, None, None
            | true, existing ->
                // Stale lease occupying the slot — lazily expire it, then grant.
                let lease = fresh ()
                m[ref] <- lease
                LockOutcome.Acquired lease, Some existing, Some lease
            | false, _ ->
                let lease = fresh ()
                m[ref] <- lease
                LockOutcome.Acquired lease, None, Some lease)

    let acquireAndAnnounce scopeId ref holder ttl = async {
        let outcome, expired, taken = acquireCore scopeId ref holder ttl

        match expired with
        | Some e -> do! publish scopeId LockChange.Expired e
        | None -> ()

        match taken with
        | Some l -> do! publish scopeId LockChange.Taken l
        | None -> ()

        return outcome
    }

    interface IEntityLockStore with
        member _.Acquire(scopeId, ref, holder, ttl) =
            acquireAndAnnounce scopeId ref holder ttl

        // Renew is take-or-refresh with the same semantics — extend an
        // own live lease, re-acquire a free/expired slot, yield to a
        // different live holder.
        member _.Renew(scopeId, ref, holder, ttl) =
            acquireAndAnnounce scopeId ref holder ttl

        member _.Release(scopeId, ref, holder) = async {
            let released =
                match scopes.TryGetValue scopeId with
                | true, m ->
                    lock m (fun () ->
                        match m.TryGetValue ref with
                        | true, existing when existing.Holder = holder ->
                            m.TryRemove ref |> ignore
                            Some existing
                        | _ -> None) // absent, or held by another user — no-op
                | _ -> None

            match released with
            | Some lease -> do! publish scopeId LockChange.Released lease
            | None -> ()
        }

        member _.GetHolder(scopeId, ref) = async {
            let now = clock ()

            let holder, expired =
                match scopes.TryGetValue scopeId with
                | true, m ->
                    lock m (fun () ->
                        match m.TryGetValue ref with
                        | true, existing when EntityLock.isLive now existing -> Some existing, None
                        | true, existing ->
                            // Lazily reap the stale lease on observation.
                            m.TryRemove ref |> ignore
                            None, Some existing
                        | _ -> None, None)
                | _ -> None, None

            match expired with
            | Some e -> do! publish scopeId LockChange.Expired e
            | None -> ()

            return holder
        }
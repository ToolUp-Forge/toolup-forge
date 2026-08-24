// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading

// ─── Phase 9i — process-local IDistributedLock default ───────────────
//
// The zero-dependency floor, registered unconditionally by `compose` so
// every subsystem resolves *an* `IDistributedLock` whether or not the
// deployment composed a distributed companion. Single-silo deployments —
// which is every deployment until one deliberately scales out — get
// exactly the exclusion their per-subsystem `SemaphoreSlim`s gave, from
// one shared table.
//
// **It is NOT a distributed lock and never pretends to be.** Two replicas
// each hold their own table and so exclude nothing from each other; that
// is the whole reason `ServerConfig.ReplicaCount > 1` is refused at
// preflight for the in-process single-writer subsystems
// (`MultiInstanceAdminCoherenceValidator`). Composing a store-backed
// companion is what makes exclusion cross a process boundary.
//
// **Why `ConcurrentDictionary` and no gate object.** Acquire is a CAS
// loop: `TryAdd` for a free id, `TryUpdate` against the *exact* stale
// lease record for a lapsed one. Both are atomic per key, so two threads
// racing the same id cannot both win, and distinct ids never contend at
// all (a single `lock` would have serialised them). A lost CAS burns a
// fence token before retrying — tokens are required to be strictly
// increasing, not gapless, so that is within contract.
//
// **The TTL is honoured, unlike `InProcessLifecycleLock`'s unbounded
// lease.** In-process a crashed holder is a dead process taking its
// semaphores with it, so Phase 54h could reasonably skip expiry. Here the
// TTL is part of the contract every implementation is held to, and the
// `IDistributedLockContract` pack exercises expiry against this default:
// an impl that ignored the TTL would pass in-process and deadlock in
// Redis, which is precisely the class of divergence the contract packs
// exist to catch. A lapsed lease is reclaimable by anyone; no sweeper is
// needed because liveness is pure timestamp math evaluated on the next
// acquire (GP 13 — a deployment that never contends pays nothing).

/// Phase 9i — process-local `IDistributedLock` over a
/// `ConcurrentDictionary<string, Lease>`. Fence tokens come from one
/// process-wide `Interlocked` counter, which trivially satisfies the
/// per-`LockId` monotonicity the contract requires (and, harmlessly,
/// more: tokens are globally ordered here, which no caller may depend on
/// — GP 12 rule 5 promises nothing across ids).
///
/// Share ONE instance across the call sites that must mutually exclude —
/// two instances have disjoint tables and so do not exclude each other.
/// `InProcessDistributedLock.shared` is the process-wide instance
/// `compose` registers.
type InProcessDistributedLock() =

    /// The currently-held lease per lock id. Absence ⇒ free; a present
    /// entry whose `ExpiresAt` has passed ⇒ lapsed and reclaimable.
    /// Entries are removed on release and overwritten on reclaim, so the
    /// table's size tracks *live* locks, not lifetime lock ids.
    let held = ConcurrentDictionary<string, Lease>()

    let mutable fenceCounter = 0L

    let mint (lockId: string) (ttl: TimeSpan) (now: DateTime) : Lease = {
        LockId = lockId
        FenceToken = Interlocked.Increment &fenceCounter
        AcquiredAt = now
        ExpiresAt = now + ttl
    }

    /// CAS acquire. Returns `None` only when a LIVE holder has the id;
    /// retries (rather than refusing) when a concurrent writer moved the
    /// entry between our read and our compare-and-swap, so a lost race
    /// against a *releasing* holder still acquires.
    let rec tryTake (lockId: string) (ttl: TimeSpan) : Lease option =
        let now = DateTime.UtcNow

        match held.TryGetValue lockId with
        | true, existing when Lease.isLiveAt now existing ->
            // Held by someone whose lease has not lapsed — fail fast.
            None
        | true, stale ->
            // Lapsed lease. Reclaim it only if nobody else changed the
            // entry first (`TryUpdate` compares against `stale` by
            // structural equality — an F# record, so this is a value
            // compare over all four fields, including the fence token).
            let fresh = mint lockId ttl now

            if held.TryUpdate(lockId, fresh, stale) then
                Some fresh
            else
                tryTake lockId ttl
        | false, _ ->
            let fresh = mint lockId ttl now

            if held.TryAdd(lockId, fresh) then
                Some fresh
            else
                tryTake lockId ttl

    interface IDistributedLock with
        member _.TryAcquire(lockId: string, ttl: TimeSpan) : Async<Lease option> = async { return tryTake lockId ttl }

        member _.Renew(lease: Lease) : Async<Lease> = async {
            let now = DateTime.UtcNow

            match held.TryGetValue lease.LockId with
            // Still ours AND still live — extend by the original window.
            // A lapsed lease is deliberately NOT renewable: another
            // acquirer may already have observed it as free, so
            // resurrecting it would hand out the id twice.
            | true, current when current.FenceToken = lease.FenceToken && Lease.isLiveAt now current ->
                let renewed = {
                    current with
                        ExpiresAt = now + Lease.originalTtl current
                }

                // A concurrent release between the read and the swap
                // leaves the hold genuinely lost — return the caller's
                // lease unchanged, which is the contract's "not renewed"
                // signal.
                if held.TryUpdate(lease.LockId, renewed, current) then
                    return renewed
                else
                    return lease
            | _ ->
                // Released, lapsed, or superseded by a higher fence
                // token. Unchanged lease ⇒ the caller's `Lease.isLive`
                // check fails and it stops working.
                return lease
        }

        member _.Release(lease: Lease) : Async<unit> = async {
            match held.TryGetValue lease.LockId with
            | true, current when current.FenceToken = lease.FenceToken ->
                // Remove only this exact entry, so a lease that lapsed
                // and was re-acquired by a later token is never released
                // out from under the new holder.
                (held :> ICollection<KeyValuePair<string, Lease>>).Remove(KeyValuePair(lease.LockId, current))
                |> ignore
            | _ -> ()
        }

module InProcessDistributedLock =
    /// A fresh process-local lock with its own table. Two instances do
    /// NOT exclude each other — use this for tests that want an isolated
    /// lock, and `shared` for anything that must contend with the rest of
    /// the process.
    let create () : IDistributedLock =
        InProcessDistributedLock() :> IDistributedLock

    /// The process-wide default. `compose` registers this instance, and
    /// the subsystems constructed eagerly during compose (the job
    /// scheduler, the Platform-Admin store) default to it, so every
    /// in-process acquirer of a given lock id contends on one table.
    let shared: IDistributedLock = create ()

/// Phase 9i — selector for one distributed-lock companion, mirroring
/// `NotificationChannelResolver` (Phase 11.G). The consumer threads in
/// one entry per companion it has wired; `ToolUp.Platform.Server` stays
/// free of any dependency on the companion package (GP 1).
type DistributedLockResolver = {
    /// Matched case-insensitively against `TOOLUP_DISTRIBUTED_LOCK`.
    /// Common value: `"redis"`.
    Name: string

    /// The companion's builder, applied with the deployment's `ILogger`
    /// and the resolved connection string. Returns `None` when the
    /// companion cannot construct a working lock, so the selector falls
    /// back to the in-process default with a Warn rather than failing
    /// startup — a deployment that would rather fail closed checks the
    /// resolved instance itself.
    Resolve: ILogger -> string -> IDistributedLock option

    /// Env-var name carrying the connection string the resolver consumes.
    /// Looked up before `Resolve` is called; unset ⇒ Warn + in-process
    /// fallback.
    ConnectionEnvVar: string
}

/// `TOOLUP_DISTRIBUTED_LOCK` selection. Same shape as
/// `NotificationChannel.fromEnv` — deliberately, so a deployment wiring
/// both distributed substrates writes the same two lines twice rather
/// than learning two conventions.
module DistributedLockSelection =
    [<Literal>]
    let EnvVar = ConfigKeys.Names.distributedLock

    let private envVar (name: string) =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> None
        | v -> Some v

    /// Resolve the deployment's `IDistributedLock` from
    /// `TOOLUP_DISTRIBUTED_LOCK`.
    ///
    /// Recognised values:
    ///   * unset / `inprocess` / `in-process` (default) —
    ///     `InProcessDistributedLock.shared`. GP 11: a deployment that
    ///     sets nothing is byte-for-byte unchanged.
    ///   * any value matched by a `resolvers` entry — resolved against
    ///     that companion; falls back to in-process + Warn when its
    ///     connection env var is unset or the resolver returns `None`.
    ///   * anything else — in-process + Warn naming the recognised
    ///     values.
    ///
    /// Fallback is a Warn rather than a hard failure because the
    /// in-process lock is *correct* for a single instance; the
    /// multi-instance case is caught separately at preflight by
    /// `MultiInstanceAdminCoherenceValidator`, which is the right place
    /// for a fail-closed gate.
    let fromEnv (logger: ILogger) (resolvers: DistributedLockResolver list) : IDistributedLock =
        let inProcess () =
            logger.Info "Distributed lock: in-process (InProcessDistributedLock) — exclusion does not cross replicas"
            InProcessDistributedLock.shared

        match envVar EnvVar |> Option.map _.ToLowerInvariant() with
        | None
        | Some "inprocess"
        | Some "in-process" -> inProcess ()
        | Some other ->
            match
                resolvers
                |> List.tryFind (fun r -> r.Name.Equals(other, StringComparison.OrdinalIgnoreCase))
            with
            | Some resolver ->
                match envVar resolver.ConnectionEnvVar with
                | None ->
                    logger.Warn
                        $"{EnvVar}={resolver.Name} but {resolver.ConnectionEnvVar} is not set. Falling back to the in-process InProcessDistributedLock — lock exclusion does NOT cross replicas."

                    inProcess ()
                | Some connStr ->
                    match resolver.Resolve logger connStr with
                    | Some lck ->
                        logger.Info $"Distributed lock: {resolver.Name} ({connStr})"
                        lck
                    | None ->
                        logger.Warn
                            $"{EnvVar}={resolver.Name} but {resolver.ConnectionEnvVar}={connStr} did not yield a working lock. Falling back to in-process."

                        inProcess ()
            | None ->
                let recognisedNames =
                    "inprocess" :: (resolvers |> List.map _.Name) |> String.concat ", "

                logger.Warn
                    $"{EnvVar}={other} not recognised. Valid values: {recognisedNames}. Falling back to in-process InProcessDistributedLock."

                inProcess ()
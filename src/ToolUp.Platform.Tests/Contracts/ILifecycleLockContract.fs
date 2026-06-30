module ToolUp.Platform.Tests.Contracts.ILifecycleLockContract

open System
open System.Collections.Generic
open System.Threading
open Expecto
open ToolUp.Platform

// ─── Phase 54h — ILifecycleLock contract pack ────────────────────────
//
// The conformance bar for a cross-replica offboard exclusion lock:
//   * mutual exclusion within a scope — two leases for one scope are never
//     held at once;
//   * cross-scope parallelism — different scopes never falsely contend;
//   * lease-expiry recovery — a holder that crashes (never disposes its
//     lease) does not deadlock the scope forever; the TTL reclaims it.
//
// The `ILifecycleLock` contract permits two contention semantics, so the
// pack runs against both shipped shapes:
//   * `InProcessLifecycleLock` — *blocks* until the scope frees and always
//     returns `Some` (the single-instance serialising default; Phase 54
//     behaviour). Mutual exclusion shows as "the second acquire waits".
//   * a fail-fast double modelling the Redis reference impl — *try-acquires*
//     and returns `None` while another holder has the scope (the
//     distributed default; the loser sees "offboard already in progress").
//     Mutual exclusion shows as "the second acquire returns `None`". This
//     in-memory double mirrors Redis `SET NX PX` + compare-and-delete so
//     the `None` branch + lease-expiry are exercised without a live Redis;
//     `RedisLifecycleLock` validates against the same bar pointed at a real
//     server.

/// In-memory fail-fast `ILifecycleLock` modelling the Redis `SET NX PX` +
/// compare-and-delete reference impl: `Acquire` takes the scope only if
/// free (else `None`); a lease auto-expires `leaseTtl` after it is taken
/// (so a never-disposed lease reclaims like a crashed holder); dispose
/// frees early but only if the scope still holds OUR token (so it never
/// releases a later holder's lease).
type private FailFastLifecycleLock(leaseTtl: TimeSpan) =
    let gate = obj ()
    let held = Dictionary<string, Guid * DateTimeOffset>()

    /// True when nothing holds `scopeId`, or its lease has expired.
    let isFree (scopeId: string) =
        match held.TryGetValue scopeId with
        | true, (_, deadline) -> deadline <= DateTimeOffset.UtcNow
        | false, _ -> true

    interface ILifecycleLock with
        member _.Acquire(scopeId: string) : Async<IDisposable option> = async {
            return
                lock gate (fun () ->
                    if not (isFree scopeId) then
                        None
                    else
                        let token = Guid.NewGuid()
                        held[scopeId] <- (token, DateTimeOffset.UtcNow.Add leaseTtl)
                        let released = ref 0

                        let lease =
                            { new IDisposable with
                                member _.Dispose() =
                                    if Interlocked.Exchange(released, 1) = 0 then
                                        lock gate (fun () ->
                                            match held.TryGetValue scopeId with
                                            | true, (t, _) when t = token -> held.Remove scopeId |> ignore
                                            | _ -> ())
                            }

                        Some lease)
        }

/// Whether the impl-under-test refuses contention (`FailFast`, returns
/// `None`) or absorbs it by waiting (`Blocking`, the in-process default).
type private Contention =
    | Blocking
    | FailFast

/// The shared cases every `ILifecycleLock` must satisfy regardless of
/// contention semantic, plus the semantic-specific mutual-exclusion case.
let private contractFor (name: string) (contention: Contention) (make: unit -> ILifecycleLock) =
    testList name [

        testCaseAsync "different scopes acquire concurrently — no false contention"
        <| async {
            let lock = make ()
            let! a = lock.Acquire "scope-a"
            let! b = lock.Acquire "scope-b"
            Expect.isTrue (Option.isSome a) "scope-a acquired"
            Expect.isTrue (Option.isSome b) "scope-b acquired while scope-a is held — distinct scopes never contend"
            a |> Option.iter _.Dispose()
            b |> Option.iter _.Dispose()
        }

        testCaseAsync "a disposed lease frees the scope for re-acquire"
        <| async {
            let lock = make ()

            match! lock.Acquire "scope-x" with
            | None -> failtest "first acquire of a free scope must succeed"
            | Some lease ->
                lease.Dispose()
                let! again = lock.Acquire "scope-x"
                Expect.isTrue (Option.isSome again) "the scope is acquirable again once the first lease is disposed"
                again |> Option.iter _.Dispose()
        }

        match contention with
        | FailFast ->
            testCaseAsync "mutual exclusion — a second same-scope acquire returns None while held"
            <| async {
                let lock = make ()

                match! lock.Acquire "scope-x" with
                | None -> failtest "first acquire of a free scope must succeed"
                | Some leaseA ->
                    let! contender = lock.Acquire "scope-x"

                    Expect.isTrue
                        (Option.isNone contender)
                        "the loser sees None — offboard already in progress for the scope"

                    leaseA.Dispose()
                    let! afterRelease = lock.Acquire "scope-x"
                    Expect.isTrue (Option.isSome afterRelease) "the scope is acquirable again once the holder releases"
                    afterRelease |> Option.iter _.Dispose()
            }
        | Blocking ->
            testCaseAsync "mutual exclusion — a second same-scope acquire waits until the first releases"
            <| async {
                let lock = make ()

                match! lock.Acquire "scope-x" with
                | None -> failtest "the blocking default never refuses a free scope"
                | Some leaseA ->
                    // The blocking default never returns None; the second
                    // acquire parks until the first lease releases. Start it
                    // off-thread and confirm it has NOT produced a (would-be
                    // concurrent) lease while A is held.
                    let contender = lock.Acquire "scope-x" |> Async.StartAsTask
                    do! Async.Sleep 120

                    Expect.isFalse
                        contender.IsCompleted
                        "the second same-scope acquire blocks while the first lease is held"

                    leaseA.Dispose()
                    let! contenderLease = contender |> Async.AwaitTask

                    Expect.isTrue
                        (Option.isSome contenderLease)
                        "the second acquire proceeds once the first lease releases"

                    contenderLease |> Option.iter _.Dispose()
            }

        // ─── lease-expiry recovery (TTL-bearing impls) ───────────────
        //
        // Only the fail-fast double carries a finite lease TTL — the
        // in-process default's lease is unbounded by design (a crashed
        // in-process holder is a dead process, taking its semaphores with
        // it), so there is nothing to expire. The Redis reference impl maps
        // the TTL onto `PX`, so this is its crash-recovery conformance case.
        match contention with
        | FailFast ->
            testCaseAsync "lease-expiry recovery — a never-disposed lease past its TTL frees the scope"
            <| async {
                // Short TTL; acquire and DELIBERATELY never dispose (the
                // crashed-holder case). After the TTL lapses the scope must
                // be acquirable again.
                let lock = FailFastLifecycleLock(TimeSpan.FromMilliseconds 100.0) :> ILifecycleLock

                match! lock.Acquire "scope-crash" with
                | None -> failtest "first acquire of a free scope must succeed"
                | Some _leaked ->
                    // Hold the lease leaked (never disposed). Before expiry a
                    // contender is refused.
                    let! duringHold = lock.Acquire "scope-crash"
                    Expect.isTrue (Option.isNone duringHold) "the scope is held before the lease expires"

                    do! Async.Sleep 250

                    let! afterExpiry = lock.Acquire "scope-crash"

                    Expect.isTrue
                        (Option.isSome afterExpiry)
                        "the lapsed lease is reclaimed so the scope can be offboarded again"

                    afterExpiry |> Option.iter _.Dispose()
            }
        | Blocking ->
            // The in-process lease is unbounded by design — a crashed
            // in-process holder is a dead process — so there is nothing to
            // expire. No case to run.
            testList "lease-expiry recovery — n/a (the in-process lease is unbounded)" []
    ]

let tests =
    testList "ILifecycleLock — cross-replica offboard exclusion contract" [
        contractFor "in-process default (blocking / serialising)" Blocking InProcessLifecycleLock.create
        contractFor "fail-fast double (models Redis SET NX PX)" FailFast (fun () ->
            FailFastLifecycleLock(TimeSpan.FromSeconds 30.0) :> ILifecycleLock)
    ]
module ToolUp.Platform.Tests.Contracts.IDistributedLockContract

open System
open Expecto
open ToolUp.Platform

// ─── Phase 9i — IDistributedLock contract pack ───────────────────────
//
// The conformance bar for the SDK-wide cross-instance lease primitive.
// Five properties, each one a way a lock implementation has historically
// been wrong:
//
//   1. **acquire** — a free id is takeable. The trivial case, and the one
//      that catches a store-backed impl whose key namespacing or
//      connection is broken before any of the interesting cases run.
//   2. **re-acquire blocks** — a second acquire of a HELD id returns
//      `None`. This is the whole point of the seam; an impl that returns
//      `Some` here excludes nothing.
//   3. **TTL expiry releases** — a lease nobody ever released is
//      reclaimable once past its `ExpiresAt`. A crashed holder must not
//      deadlock the id forever, and an impl that treats the TTL as
//      decoration passes (2) while deadlocking in production.
//   4. **fence token monotonic** — every successful acquire of one id
//      carries a strictly greater `FenceToken` than the last. Tokens are
//      what let a downstream store refuse a lapsed holder's late write;
//      a repeated or decreasing token makes that refusal wrong in the
//      dangerous direction.
//   5. **release returns the id to the pool** — and does so immediately,
//      not at the TTL.
//
// Plus the guards around them that are easy to get wrong and silent when
// they are: distinct ids never falsely contend; release is idempotent and
// holder-checked (so a stale lease cannot release a live holder's hold);
// and `Renew` extends a live hold while refusing a lost one.
//
// **Timing.** Every case that depends on expiry uses a short TTL and
// waits comfortably past it (2.5× or more). No case asserts that
// something happens *within* a deadline, so the pack is not
// load-sensitive on a busy CI runner — the only direction a slow machine
// can push these is "waited longer than needed".
//
// `contractFor` is exported so any implementation can bind against the
// same bar — `tests` binds the in-process default, and
// `InProcess/RedisDistributedLockTests.fs` binds the Redis reference impl
// env-gated on `TOOLUP_REDIS_CONNECTION`.

/// Fresh lock ids per case. Contract packs run against shared backends
/// (the Redis binding points at whatever server CI has), so a fixed id
/// would make two cases — or two concurrent runs — contend with each
/// other and fail for reasons that have nothing to do with the contract.
let private freshId (label: string) =
    sprintf "contract-%s-%s" label (Guid.NewGuid().ToString "N")

let contractFor (name: string) (make: unit -> IDistributedLock) =
    testList name [

        testCaseAsync "acquire — a free id yields a lease describing the hold"
        <| async {
            let lck = make ()
            let lockId = freshId "acquire"
            let ttl = TimeSpan.FromSeconds 30.0

            match! lck.TryAcquire(lockId, ttl) with
            | None -> failtest "a free lock id must be acquirable"
            | Some lease ->
                Expect.equal lease.LockId lockId "the lease names the id it holds"
                Expect.isTrue (lease.FenceToken > 0L) "a fence token is issued"

                Expect.isTrue
                    (lease.ExpiresAt > lease.AcquiredAt)
                    "the lease expires after it was acquired — the TTL is honoured as data, not ignored"

                Expect.isTrue (Lease.isLive lease) "a freshly-acquired lease is live"
                do! lck.Release lease
        }

        testCaseAsync "re-acquire blocks — a second acquire while held returns None"
        <| async {
            let lck = make ()
            let lockId = freshId "reacquire"
            let ttl = TimeSpan.FromSeconds 30.0

            match! lck.TryAcquire(lockId, ttl) with
            | None -> failtest "first acquire of a free id must succeed"
            | Some holder ->
                let! contender = lck.TryAcquire(lockId, ttl)

                Expect.isTrue
                    (Option.isNone contender)
                    "the loser sees None — the id is already held, and the primitive is fail-fast, never blocking"

                do! lck.Release holder
        }

        testCaseAsync "release returns the id to the pool — immediately, not at the TTL"
        <| async {
            let lck = make ()
            let lockId = freshId "release"
            // A long TTL, so a re-acquire that succeeds proves the RELEASE
            // freed the id rather than the expiry.
            let ttl = TimeSpan.FromMinutes 10.0

            match! lck.TryAcquire(lockId, ttl) with
            | None -> failtest "first acquire of a free id must succeed"
            | Some holder ->
                do! lck.Release holder

                match! lck.TryAcquire(lockId, ttl) with
                | None -> failtest "the id must be acquirable again once released — well inside the original TTL"
                | Some next ->
                    Expect.isTrue
                        (next.FenceToken > holder.FenceToken)
                        "the re-acquire carries a strictly greater fence token"

                    do! lck.Release next
        }

        testCaseAsync "TTL expiry releases — a never-released lease past its TTL frees the id"
        <| async {
            let lck = make ()
            let lockId = freshId "expiry"
            let ttl = TimeSpan.FromMilliseconds 200.0

            match! lck.TryAcquire(lockId, ttl) with
            | None -> failtest "first acquire of a free id must succeed"
            | Some _leaked ->
                // DELIBERATELY never released — the crashed-holder case.
                let! duringHold = lck.TryAcquire(lockId, ttl)
                Expect.isTrue (Option.isNone duringHold) "the id is held while the lease is live"

                do! Async.Sleep 600

                match! lck.TryAcquire(lockId, TimeSpan.FromSeconds 30.0) with
                | None ->
                    failtest
                        "the lapsed lease must be reclaimable — a crashed holder cannot be allowed to deadlock the id forever"
                | Some reclaimed ->
                    Expect.isTrue (Lease.isLive reclaimed) "the reclaiming lease is live"
                    do! lck.Release reclaimed
        }

        testCaseAsync "fence token monotonic — strictly increases across acquisitions of one id"
        <| async {
            let lck = make ()
            let lockId = freshId "fence"
            let ttl = TimeSpan.FromSeconds 30.0

            let mutable previous = 0L

            for round in 1..4 do
                match! lck.TryAcquire(lockId, ttl) with
                | None -> failtest (sprintf "acquire round %d of a free id must succeed" round)
                | Some lease ->
                    Expect.isTrue
                        (lease.FenceToken > previous)
                        (sprintf
                            "round %d: fence token %d must be strictly greater than the previous %d — a repeated token makes a downstream fence check wrong in the dangerous direction"
                            round
                            lease.FenceToken
                            previous)

                    previous <- lease.FenceToken
                    do! lck.Release lease
        }

        testCaseAsync "distinct ids never falsely contend"
        <| async {
            let lck = make ()
            let idA = freshId "distinct-a"
            let idB = freshId "distinct-b"
            let ttl = TimeSpan.FromSeconds 30.0

            let! a = lck.TryAcquire(idA, ttl)
            let! b = lck.TryAcquire(idB, ttl)
            Expect.isTrue (Option.isSome a) "id A acquired"
            Expect.isTrue (Option.isSome b) "id B acquired while A is held — distinct ids are independent"

            match a, b with
            | Some la, Some lb ->
                do! lck.Release la
                do! lck.Release lb
            | _ -> ()
        }

        testCaseAsync "release is idempotent — releasing twice is a silent no-op"
        <| async {
            let lck = make ()
            let lockId = freshId "double-release"
            let ttl = TimeSpan.FromSeconds 30.0

            match! lck.TryAcquire(lockId, ttl) with
            | None -> failtest "first acquire of a free id must succeed"
            | Some lease ->
                do! lck.Release lease
                // A `finally` may release a lease a happy path already
                // released; this must not throw and must not disturb a
                // subsequent holder.
                do! lck.Release lease

                match! lck.TryAcquire(lockId, ttl) with
                | None -> failtest "the id is still acquirable after a double release"
                | Some next -> do! lck.Release next
        }

        testCaseAsync "release is holder-checked — a stale lease cannot free a live holder's hold"
        <| async {
            let lck = make ()
            let lockId = freshId "stale-release"
            let shortTtl = TimeSpan.FromMilliseconds 200.0

            match! lck.TryAcquire(lockId, shortTtl) with
            | None -> failtest "first acquire of a free id must succeed"
            | Some stale ->
                // Let the first lease lapse, then let someone else take
                // the id.
                do! Async.Sleep 600

                match! lck.TryAcquire(lockId, TimeSpan.FromSeconds 30.0) with
                | None -> failtest "the lapsed id must be re-acquirable"
                | Some live ->
                    // The stale holder — a process that woke from a long
                    // pause believing it still holds the lock — releases.
                    // It must NOT free the new holder's hold.
                    do! lck.Release stale

                    let! contender = lck.TryAcquire(lockId, TimeSpan.FromSeconds 30.0)

                    Expect.isTrue
                        (Option.isNone contender)
                        "the live holder still holds the id — a stale lease's release is compare-and-act, not a blind delete"

                    do! lck.Release live
        }

        testCaseAsync "renew extends a live hold and keeps the same fence token"
        <| async {
            let lck = make ()
            let lockId = freshId "renew"
            let ttl = TimeSpan.FromSeconds 2.0

            match! lck.TryAcquire(lockId, ttl) with
            | None -> failtest "first acquire of a free id must succeed"
            | Some lease ->
                let! renewed = lck.Renew lease

                Expect.equal
                    renewed.FenceToken
                    lease.FenceToken
                    "renewing extends the SAME hold — a new token would mean a new acquisition, invalidating the holder's own fenced writes"

                Expect.equal renewed.LockId lease.LockId "the renewed lease names the same id"

                Expect.isTrue (renewed.ExpiresAt >= lease.ExpiresAt) "the renewal does not move the expiry backwards"

                Expect.isTrue (Lease.isLive renewed) "the renewed lease is live"

                // Still exclusive after a renew.
                let! contender = lck.TryAcquire(lockId, ttl)
                Expect.isTrue (Option.isNone contender) "the id is still held after a renew"

                do! lck.Release renewed
        }

        testCaseAsync "renew refuses a lost hold — the lease comes back unchanged"
        <| async {
            let lck = make ()
            let lockId = freshId "renew-lost"
            let shortTtl = TimeSpan.FromMilliseconds 200.0

            match! lck.TryAcquire(lockId, shortTtl) with
            | None -> failtest "first acquire of a free id must succeed"
            | Some lease ->
                do! Async.Sleep 600

                // Someone else now holds the id.
                match! lck.TryAcquire(lockId, TimeSpan.FromSeconds 30.0) with
                | None -> failtest "the lapsed id must be re-acquirable"
                | Some live ->
                    let! notRenewed = lck.Renew lease

                    Expect.equal
                        notRenewed.ExpiresAt
                        lease.ExpiresAt
                        "a lost hold is NOT resurrected — the lease returns unchanged, which is the contract's not-renewed signal"

                    Expect.isFalse
                        (Lease.isLive notRenewed)
                        "so the caller's own liveness check fails and it stops working"

                    do! lck.Release live
        }
    ]

/// The in-process default binding — the pack that runs on every checkout
/// with no external dependency.
let tests =
    testList "IDistributedLock — cross-instance lease primitive contract" [
        // A FRESH lock per binding, not `InProcessDistributedLock.shared`:
        // the shared instance is the one `compose` registers and the job
        // scheduler / admin store default to, so binding the pack to it
        // would let a concurrently-running scheduler test contend with a
        // contract case. Ids are GUID-suffixed anyway; this is belt and
        // braces on the axis that would be silent.
        contractFor "InProcessDistributedLock (the always-registered default)" InProcessDistributedLock.create
    ]
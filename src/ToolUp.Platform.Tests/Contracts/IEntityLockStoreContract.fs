module ToolUp.Platform.Tests.Contracts.IEntityLockStoreContract

open System
open System.Collections.Concurrent
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 442 — IEntityLockStore conformance ────────────────────────
//
// Proves the advisory soft-lock contract against the single-instance
// in-memory default: acquire on a free ref grants + publishes `Taken`;
// an acquire-conflict returns the current holder and never blocks (GP
// 12); release frees + publishes `Released`; a lease expires by pure
// timestamp math with NO background sweeper (GP 13) and emits `Expired`
// lazily on the next observation; renew extends; locks are scope-isolated
// (GP 4). Any external implementation can run the same bar.

let private jsonOptions = FableConverters.create ()

type private RecordingChannel() =
    let published = ConcurrentQueue<string * Notification>()

    member _.Locks =
        published
        |> Seq.choose (fun (scope, n) ->
            match n with
            | CustomNotification(key, json) when key = CollaborationTopics.Lock ->
                Some(scope, JsonSerializer.Deserialize<LockEvent>(json, jsonOptions))
            | _ -> None)
        |> List.ofSeq

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue(scopeId, notification) }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }

type private Clock(start: DateTime) =
    let mutable now = start
    member _.Now() = now
    member _.Advance(ts: TimeSpan) = now <- now + ts

let private ref1: EntityLockRef = {
    EntityType = "Report"
    EntityId = "r-1"
}

let private ttl = TimeSpan.FromSeconds 60.0

let tests =
    testList "IEntityLockStore contract (Phase 442)" [
        testCaseAsync "acquire on a free ref grants a lease and publishes Taken"
        <| async {
            let ch = RecordingChannel()
            let s = InMemoryEntityLockStore(ch) :> IEntityLockStore
            let! outcome = s.Acquire("team-a", ref1, "user-1", ttl)

            match outcome with
            | LockOutcome.Acquired lease -> Expect.equal lease.Holder "user-1" "holder is the acquirer"
            | LockOutcome.HeldByOther l -> failtestf "expected Acquired, got HeldByOther %s" l.Holder

            let taken = ch.Locks |> List.filter (fun (_, e) -> e.Change = LockChange.Taken)
            Expect.equal (taken |> List.map fst) [ "team-a" ] "Taken published on the caller's scope"
        }

        testCaseAsync "acquire-conflict returns the current holder and never blocks (GP 12)"
        <| async {
            let ch = RecordingChannel()
            let s = InMemoryEntityLockStore(ch) :> IEntityLockStore
            do! s.Acquire("team-a", ref1, "user-1", ttl) |> Async.Ignore
            let! outcome = s.Acquire("team-a", ref1, "user-2", ttl)

            match outcome with
            | LockOutcome.HeldByOther lease -> Expect.equal lease.Holder "user-1" "surfaces the incumbent holder"
            | LockOutcome.Acquired _ -> failtest "expected HeldByOther — the lock is already live"

            let taken = ch.Locks |> List.filter (fun (_, e) -> e.Change = LockChange.Taken)
            Expect.equal (List.length taken) 1 "no second Taken event for the loser"
        }

        testCaseAsync "GetHolder returns the live holder, then None after release + a Released event"
        <| async {
            let ch = RecordingChannel()
            let s = InMemoryEntityLockStore(ch) :> IEntityLockStore
            do! s.Acquire("team-a", ref1, "user-1", ttl) |> Async.Ignore

            let! held = s.GetHolder("team-a", ref1)
            Expect.equal (held |> Option.map _.Holder) (Some "user-1") "holder surfaced"

            do! s.Release("team-a", ref1, "user-1")
            let! freed = s.GetHolder("team-a", ref1)
            Expect.isNone freed "freed after release"

            let released =
                ch.Locks |> List.filter (fun (_, e) -> e.Change = LockChange.Released)

            Expect.equal (released |> List.map (fun (_, e) -> e.Lease.Holder)) [ "user-1" ] "Released names the holder"
        }

        testCaseAsync "release by a non-holder is a no-op"
        <| async {
            let ch = RecordingChannel()
            let s = InMemoryEntityLockStore(ch) :> IEntityLockStore
            do! s.Acquire("team-a", ref1, "user-1", ttl) |> Async.Ignore
            do! s.Release("team-a", ref1, "user-2") // not the holder
            let! held = s.GetHolder("team-a", ref1)
            Expect.equal (held |> Option.map _.Holder) (Some "user-1") "still held by user-1"
        }

        testCaseAsync "lease expiry frees the lock with no sweeper and emits Expired lazily"
        <| async {
            let clock = Clock(DateTime(2026, 1, 1))
            let ch = RecordingChannel()
            let s = InMemoryEntityLockStore(ch, now = clock.Now) :> IEntityLockStore
            do! s.Acquire("team-a", ref1, "user-1", ttl) |> Async.Ignore

            clock.Advance(TimeSpan.FromSeconds 30.0)
            let! stillHeld = s.GetHolder("team-a", ref1)
            Expect.isSome stillHeld "live within TTL"

            clock.Advance(TimeSpan.FromSeconds 40.0) // 70s total — past the 60s TTL
            let! expired = s.GetHolder("team-a", ref1)
            Expect.isNone expired "expired purely by timestamp math"

            let expiredEvents =
                ch.Locks |> List.filter (fun (_, e) -> e.Change = LockChange.Expired)

            Expect.equal (List.length expiredEvents) 1 "Expired emitted lazily on observation"

            // a fresh acquire after expiry succeeds (re-acquire path)
            let! reacquired = s.Acquire("team-a", ref1, "user-2", ttl)

            match reacquired with
            | LockOutcome.Acquired lease -> Expect.equal lease.Holder "user-2" "re-acquired after expiry"
            | LockOutcome.HeldByOther _ -> failtest "expired lease must be re-acquirable"
        }

        testCaseAsync "renew extends the caller's own lease past its original expiry"
        <| async {
            let clock = Clock(DateTime(2026, 1, 1))

            let s =
                InMemoryEntityLockStore(RecordingChannel(), now = clock.Now) :> IEntityLockStore

            do! s.Acquire("team-a", ref1, "user-1", ttl) |> Async.Ignore

            clock.Advance(TimeSpan.FromSeconds 50.0)
            do! s.Renew("team-a", ref1, "user-1", ttl) |> Async.Ignore // extend to 110s

            clock.Advance(TimeSpan.FromSeconds 40.0) // 90s total — past the original 60s, within the renewed window
            let! held = s.GetHolder("team-a", ref1)
            Expect.equal (held |> Option.map _.Holder) (Some "user-1") "kept alive by renew"
        }

        testCaseAsync "renew by a different user while live yields to the holder"
        <| async {
            let s = InMemoryEntityLockStore(RecordingChannel()) :> IEntityLockStore
            do! s.Acquire("team-a", ref1, "user-1", ttl) |> Async.Ignore
            let! outcome = s.Renew("team-a", ref1, "user-2", ttl)

            match outcome with
            | LockOutcome.HeldByOther lease -> Expect.equal lease.Holder "user-1" "cannot steal a live lease via renew"
            | LockOutcome.Acquired _ -> failtest "expected HeldByOther"
        }

        testCaseAsync "locks are scope-isolated (GP 4)"
        <| async {
            let ch = RecordingChannel()
            let s = InMemoryEntityLockStore(ch) :> IEntityLockStore
            do! s.Acquire("team-a", ref1, "user-1", ttl) |> Async.Ignore

            let! crossScope = s.GetHolder("team-b", ref1)
            Expect.isNone crossScope "no cross-scope holder read"

            // team-b can hold the same ref independently
            let! outcome = s.Acquire("team-b", ref1, "user-9", ttl)

            match outcome with
            | LockOutcome.Acquired lease -> Expect.equal lease.Holder "user-9" "independent lease per scope"
            | LockOutcome.HeldByOther _ -> failtest "same ref must be independently lockable per scope"

            let takenScopes =
                ch.Locks
                |> List.filter (fun (_, e) -> e.Change = LockChange.Taken)
                |> List.map fst
                |> List.sort

            Expect.equal takenScopes [ "team-a"; "team-b" ] "each Taken published on its own scope, never cross-team"
        }
    ]
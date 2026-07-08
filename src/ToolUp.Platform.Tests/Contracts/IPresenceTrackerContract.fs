module ToolUp.Platform.Tests.Contracts.IPresenceTrackerContract

open System
open System.Collections.Concurrent
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 442 — IPresenceTracker conformance ────────────────────────
//
// Proves the tracker contract against the single-instance in-memory
// default: join/move/leave keep a scope-isolated roster with a location
// descriptor (GP 4); join/move/leave fan out on the reserved
// `_platform.presence` key, published on the caller's OWN scope (never
// the cross-team `_platform` scope); stale peers expire out of the
// roster by heartbeat-window math. Any external implementation can run
// the same bar.

let private jsonOptions = FableConverters.create ()

/// A channel that records every publish so the pack can assert the
/// `_platform.presence` fan-out shape without async subscription timing.
type private RecordingChannel() =
    let published = ConcurrentQueue<string * Notification>()

    member _.Presence =
        published
        |> Seq.choose (fun (scope, n) ->
            match n with
            | CustomNotification(key, json) when key = CollaborationTopics.Presence ->
                Some(scope, JsonSerializer.Deserialize<PresenceEvent>(json, jsonOptions))
            | _ -> None)
        |> List.ofSeq

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue(scopeId, notification) }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }

/// A mutable clock the test advances by hand.
type private Clock(start: DateTime) =
    let mutable now = start
    member _.Now() = now
    member _.Advance(ts: TimeSpan) = now <- now + ts

let private loc m = PresenceLocation.ofModule m

let tests =
    testList "IPresenceTracker contract (Phase 442)" [
        testCaseAsync "join then roster lists the peer with its location"
        <| async {
            let ch = RecordingChannel()
            let t = InMemoryPresenceTracker(ch) :> IPresenceTracker
            do! t.Join("team-a", "user-1", Some "Ada", loc "reports")
            let! roster = t.Roster "team-a"
            Expect.equal roster.Length 1 "one peer"
            Expect.equal roster[0].UserId "user-1" "userId"
            Expect.equal roster[0].DisplayName (Some "Ada") "display name"
            Expect.equal roster[0].Location.Module "reports" "location module"
        }

        testCaseAsync "roster is scope-isolated (GP 4)"
        <| async {
            let ch = RecordingChannel()
            let t = InMemoryPresenceTracker(ch) :> IPresenceTracker
            do! t.Join("team-a", "user-1", None, loc "reports")
            do! t.Join("team-b", "user-2", None, loc "reports")
            let! a = t.Roster "team-a"
            Expect.equal (a |> List.map _.UserId) [ "user-1" ] "only team-a peers"
        }

        testCaseAsync "join / move / leave publish on the caller's own scope, never _platform"
        <| async {
            let ch = RecordingChannel()
            let t = InMemoryPresenceTracker(ch) :> IPresenceTracker
            do! t.Join("team-a", "user-1", None, loc "reports")
            do! t.Move("team-a", "user-1", loc "settings")
            do! t.Leave("team-a", "user-1")

            let evs = ch.Presence
            Expect.equal (evs |> List.map (fst)) [ "team-a"; "team-a"; "team-a" ] "all on team-a scope, not _platform"

            Expect.equal
                (evs |> List.map (fun (_, e) -> e.Change))
                [ PresenceChange.Joined; PresenceChange.Moved; PresenceChange.Left ]
                "join → move → leave sequence"

            let moved = evs |> List.find (fun (_, e) -> e.Change = PresenceChange.Moved) |> snd
            Expect.equal moved.Peer.Location.Module "settings" "move carries the new location"
        }

        testCaseAsync "move updates the peer's location in the roster"
        <| async {
            let ch = RecordingChannel()
            let t = InMemoryPresenceTracker(ch) :> IPresenceTracker
            do! t.Join("team-a", "user-1", Some "Ada", loc "reports")
            do! t.Move("team-a", "user-1", loc "settings")
            let! roster = t.Roster "team-a"
            Expect.equal roster.Length 1 "still one peer"
            Expect.equal roster[0].Location.Module "settings" "moved location"
            Expect.equal roster[0].DisplayName (Some "Ada") "display name preserved across move"
        }

        testCaseAsync "a stale peer expires out of the roster (no sweeper)"
        <| async {
            let clock = Clock(DateTime(2026, 1, 1))

            let t =
                InMemoryPresenceTracker(RecordingChannel(), expiry = TimeSpan.FromSeconds 90.0, now = clock.Now)
                :> IPresenceTracker

            do! t.Join("team-a", "user-1", None, loc "reports")
            clock.Advance(TimeSpan.FromSeconds 120.0)
            let! roster = t.Roster "team-a"
            Expect.isEmpty roster "expired out after the heartbeat window"
        }

        testCaseAsync "heartbeat refreshes liveness; leave removes"
        <| async {
            let clock = Clock(DateTime(2026, 1, 1))

            let t =
                InMemoryPresenceTracker(RecordingChannel(), expiry = TimeSpan.FromSeconds 90.0, now = clock.Now)
                :> IPresenceTracker

            do! t.Join("team-a", "user-1", None, loc "reports")
            clock.Advance(TimeSpan.FromSeconds 60.0)
            do! t.Heartbeat("team-a", "user-1")
            clock.Advance(TimeSpan.FromSeconds 60.0) // 120s since join, only 60s since heartbeat
            let! stillHere = t.Roster "team-a"
            Expect.equal stillHere.Length 1 "kept alive by the heartbeat"

            do! t.Leave("team-a", "user-1")
            let! gone = t.Roster "team-a"
            Expect.isEmpty gone "removed by leave"
        }
    ]
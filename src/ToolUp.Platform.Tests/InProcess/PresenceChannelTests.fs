module ToolUp.Platform.Tests.InProcess.PresenceChannelTests

open System
open Expecto
open ToolUp.Platform

// ─── Phase 241 — presence substrate ──────────────────────────────────
//
// Proves: join/heartbeat populate a scope-isolated roster; a roster
// read returns only the calling scope's members (GP 4); stale entries
// transition Online → Away → expired-out via the heartbeat window
// (deterministic through an injected clock); leave removes.

/// A mutable clock the test advances by hand.
type private Clock(start: DateTime) =
    let mutable now = start
    member _.Now() = now
    member _.Advance(ts: TimeSpan) = now <- now + ts

let private channelWith (clock: Clock) : IPresenceChannel =
    InMemoryPresenceChannel(
        awayThreshold = TimeSpan.FromSeconds 30.0,
        expiry = TimeSpan.FromSeconds 90.0,
        now = clock.Now
    )

let tests =
    testList "PresenceChannel (Phase 241)" [
        test "join then roster lists the participant Online" {
            let ch = channelWith (Clock(DateTime(2026, 1, 1)))
            ch.Join("team-a", "user-1", Some "Ada") |> Async.RunSynchronously
            let roster = ch.Roster "team-a" |> Async.RunSynchronously
            Expect.equal roster.Length 1 "one member"
            Expect.equal roster[0].PrincipalId "user-1" "principal"
            Expect.equal roster[0].DisplayName (Some "Ada") "display name"
            Expect.equal roster[0].Status Online "online"
        }

        test "roster is scope-isolated (GP 4)" {
            let ch = channelWith (Clock(DateTime(2026, 1, 1)))
            ch.Join("team-a", "user-1", None) |> Async.RunSynchronously
            ch.Join("team-b", "user-2", None) |> Async.RunSynchronously
            let a = ch.Roster "team-a" |> Async.RunSynchronously
            Expect.equal (a |> List.map _.PrincipalId) [ "user-1" ] "only scope a members"
        }

        test "stale heartbeat transitions Online → Away → expired" {
            let clock = Clock(DateTime(2026, 1, 1))
            let ch = channelWith clock
            ch.Join("team-a", "user-1", None) |> Async.RunSynchronously

            clock.Advance(TimeSpan.FromSeconds 45.0) // past away threshold (30s), within expiry (90s)
            let away = ch.Roster "team-a" |> Async.RunSynchronously
            Expect.equal away[0].Status Away "away after 45s"

            clock.Advance(TimeSpan.FromSeconds 60.0) // 105s total — past expiry
            let gone = ch.Roster "team-a" |> Async.RunSynchronously
            Expect.isEmpty gone "expired out of roster"
        }

        test "heartbeat refreshes liveness" {
            let clock = Clock(DateTime(2026, 1, 1))
            let ch = channelWith clock
            ch.Join("team-a", "user-1", None) |> Async.RunSynchronously
            clock.Advance(TimeSpan.FromSeconds 45.0)
            ch.Heartbeat("team-a", "user-1") |> Async.RunSynchronously
            let roster = ch.Roster "team-a" |> Async.RunSynchronously
            Expect.equal roster[0].Status Online "online again after heartbeat"
        }

        test "leave removes the participant" {
            let ch = channelWith (Clock(DateTime(2026, 1, 1)))
            ch.Join("team-a", "user-1", None) |> Async.RunSynchronously
            ch.Leave("team-a", "user-1") |> Async.RunSynchronously
            let roster = ch.Roster "team-a" |> Async.RunSynchronously
            Expect.isEmpty roster "removed"
        }
    ]
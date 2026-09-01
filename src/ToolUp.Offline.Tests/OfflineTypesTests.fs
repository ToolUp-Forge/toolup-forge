// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Offline.Tests.OfflineTypesTests

open System
open Expecto
open ToolUp.Offline

// ─── Phase 24 — Core model tests ─────────────────────────────────────
//
// The pure half of the companion: the retry schedule, the status
// derivation, the queue-stats fold, and the wire-name round trips.
// Everything here runs off-browser with no network, so a fresh
// checkout is green.

let private mutation (revision: int) : QueuedMutation = {
    Id = sprintf "m-%d" revision
    EnqueuedAt = DateTimeOffset(2026, 8, 31, 9, 14, 0, TimeSpan.Zero)
    ScopeId = "team-1"
    EntityType = "Inspection"
    EntityId = sprintf "e-%d" revision
    Operation = SaveOp
    Payload = [| 1uy; 2uy; 3uy |]
    BaseVersion = 3
    LocalRevision = revision
}

let private entry (revision: int) (state: MutationState) (attempts: int) : QueueEntry = {
    Mutation = mutation revision
    State = state
    Attempts = attempts
    ServerEntity = None
}

let retryPolicyTests =
    testList "RetryPolicy" [
        test "first attempt waits the initial delay" {
            Expect.equal (RetryPolicy.delayFor RetryPolicy.defaults 1) 1000 "attempt 1 is the initial delay"
        }

        test "delay grows geometrically" {
            let d2 = RetryPolicy.delayFor RetryPolicy.defaults 2
            let d3 = RetryPolicy.delayFor RetryPolicy.defaults 3
            let d4 = RetryPolicy.delayFor RetryPolicy.defaults 4

            Expect.equal d2 2000 "attempt 2 doubles"
            Expect.equal d3 4000 "attempt 3 doubles again"
            Expect.equal d4 8000 "attempt 4 doubles again"
        }

        test "delay is clamped at MaxDelayMs" {
            // Attempt 20 under a doubling schedule is ~2^19 seconds —
            // far past the ceiling. The point is that it CLAMPS rather
            // than overflowing to a negative or absurd delay.
            let delay = RetryPolicy.delayFor RetryPolicy.defaults 20
            Expect.equal delay RetryPolicy.defaults.MaxDelayMs "clamped to the ceiling"
        }

        test "a pathological attempt count still yields the ceiling, not garbage" {
            // Guards the exponent cap. Without it `2.0 ** 999.0` is
            // infinity and `int infinity` is Int32.MinValue — a
            // NEGATIVE delay, which reads as "retry immediately,
            // forever".
            let delay = RetryPolicy.delayFor RetryPolicy.defaults 999
            Expect.equal delay RetryPolicy.defaults.MaxDelayMs "still the ceiling"
            Expect.isGreaterThan delay 0 "never negative"
        }

        test "attempt 0 or below is treated as the first attempt" {
            Expect.equal (RetryPolicy.delayFor RetryPolicy.defaults 0) 1000 "attempt 0 does not raise"
            Expect.equal (RetryPolicy.delayFor RetryPolicy.defaults -5) 1000 "a negative attempt does not raise"
        }

        test "a multiplier below 1 does not shrink the delay" {
            // A misconfigured multiplier must not produce a retry storm.
            let policy = {
                RetryPolicy.defaults with
                    Multiplier = 0.1
            }

            Expect.equal (RetryPolicy.delayFor policy 5) policy.InitialDelayMs "floored at the initial delay"
        }

        test "MaxAttempts = 0 never exhausts" {
            let policy = {
                RetryPolicy.defaults with
                    MaxAttempts = 0
            }

            Expect.isFalse (RetryPolicy.isExhausted policy 1000) "0 means retry forever"
        }

        test "exhaustion fires at MaxAttempts" {
            Expect.isFalse (RetryPolicy.isExhausted RetryPolicy.defaults 7) "7 of 8 is not exhausted"
            Expect.isTrue (RetryPolicy.isExhausted RetryPolicy.defaults 8) "8 of 8 is exhausted"
        }
    ]

let queueStatsTests =
    testList "QueueStats" [
        test "counts by state, ignoring settled entries" {
            let entries = [
                entry 1 Pending 0
                entry 2 Pending 0
                entry 3 Conflicted 0
                entry 4 (Failed "boom") 2
                entry 5 AppliedState 0
            ]

            let stats = QueueStats.ofEntries entries

            Expect.equal stats.Pending 2 "two pending"
            Expect.equal stats.Conflicted 1 "one conflicted"
            Expect.equal stats.Failed 1 "one failed"
        }

        test "an empty queue is settled" {
            Expect.isTrue (QueueStats.isSettled (QueueStats.ofEntries [])) "nothing outstanding"
        }

        test "an applied-but-unpruned entry is settled" {
            Expect.isTrue
                (QueueStats.isSettled (QueueStats.ofEntries [ entry 1 AppliedState 0 ]))
                "applied counts nowhere"
        }
    ]

let syncStatusTests =
    testList "SyncStatus.derive" [
        test "online and empty is Online" {
            Expect.equal (SyncStatus.derive true false QueueStats.empty) Online "nothing to say"
        }

        test "offline reports the pending count" {
            let stats = { QueueStats.empty with Pending = 3 }
            Expect.equal (SyncStatus.derive false false stats) (Offline 3) "offline with 3 queued"
        }

        test "offline wins over conflicts" {
            // Documented ordering: reconnecting is the action that
            // unblocks a disconnected client, so telling them to
            // resolve conflicts they cannot submit is noise.
            let stats = {
                QueueStats.empty with
                    Pending = 1
                    Conflicted = 2
            }

            Expect.equal (SyncStatus.derive false false stats) (Offline 1) "offline takes precedence"
        }

        test "draining reports pending plus failed" {
            let stats = {
                QueueStats.empty with
                    Pending = 2
                    Failed = 1
            }

            Expect.equal (SyncStatus.derive true true stats) (Syncing 3) "everything still to send"
        }

        test "online and drained with conflicts reports the conflicts" {
            let stats = { QueueStats.empty with Conflicted = 2 }
            Expect.equal (SyncStatus.derive true false stats) (ConflictsPending 2) "conflicts surface"
        }

        test "every status has a non-empty label" {
            // The label match must stay exhaustive; this asserts it is
            // also useful, i.e. no case renders as "".
            let statuses = [ Online; Offline 0; Offline 4; Syncing 2; ConflictsPending 1 ]

            for status in statuses do
                Expect.isNotEmpty (SyncStatus.label status) (sprintf "%A has a label" status)
        }
    ]

let wireNameTests =
    testList "wire names" [
        test "MutationOp round-trips" {
            for op in [ SaveOp; DeleteOp ] do
                Expect.equal (MutationOp.tryParse (MutationOp.name op)) (Some op) (sprintf "%A round-trips" op)
        }

        test "an unknown MutationOp token is None, not a guess" {
            Expect.isNone (MutationOp.tryParse "upsert") "unrecognised tokens are skipped, never coerced"
        }

        test "MutationState names are distinct" {
            let names =
                [ Pending; AppliedState; Conflicted; Failed "x" ] |> List.map MutationState.name

            Expect.equal (List.distinct names |> List.length) 4 "four distinct names"
        }

        test "the enqueue timestamp survives an ISO round trip" {
            // Pins the contract the IndexedDB queue relies on: the
            // browser stores `ToString "o"` and parses it back. Same
            // BCL semantics on both sides, so a break here is a break
            // there.
            let original = DateTimeOffset(2026, 8, 31, 9, 14, 27, 123, TimeSpan.FromHours 1.0)
            let parsed = DateTimeOffset.Parse(original.ToString "o")

            Expect.equal parsed original "round-trips exactly, offset included"
        }
    ]

[<Tests>]
let tests =
    testList "OfflineTypes" [ retryPolicyTests; queueStatsTests; syncStatusTests; wireNameTests ]
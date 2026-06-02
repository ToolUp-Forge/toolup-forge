// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FastPathSequencerTelemetryTests

open Expecto
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.AI.FastPathBeaconHandler
open ToolUp.AI.FastPathTelemetryHandler

// ─── Phase 6j.G — sequencer telemetry rollup ─────────────────────
//
// Covers the .NET-verifiable half of 6j.G's forge-side close-out:
//   * `computeSequencerRollup` correctness over synthetic outcome
//     lists (hits / fall-throughs / user-interrupts).
//   * Round-trip of the camelCase wire shape the fast-path-resolver
//     companion emits through Newtonsoft.Json (case-insensitive
//     property matching) into the PascalCase F# records the handler
//     and rollup consume.
//
// The HTTP handler wiring (StorageScope resolution, IEventStore I/O,
// Giraffe response codes) is structural glue around these two units
// — the existing `FastPathBeaconOwnershipTests` covers that same
// pattern for the conversation beacon; mirroring it here would
// duplicate scaffolding without exercising new behaviour.

let private jsonOptions = FableConverters.create ()

let private outcome (kind: string) (clauseCount: int) : SequenceOutcomeBeacon = {
    Outcome = kind
    Instruction = "set country to UK and set brand to X"
    ClauseCount = clauseCount
    ClausesCompleted = clauseCount
}

let private rollupTests =
    testList "computeSequencerRollup — Phase 6j.G sequencer key derivation" [
        testCase "empty input — zeros across the board"
        <| fun _ ->
            let r = computeSequencerRollup []
            Expect.equal r.SequencedHits 0 "no events ⇒ zero hits"
            Expect.equal r.SequencedFallThroughRate 0.0 "no events ⇒ zero fall-through (not NaN)"
            Expect.equal r.MeanClausesPerSequence 0.0 "no events ⇒ zero mean clauses (not NaN)"

        testCase "all-resolved outcomes count as hits, mean over ClauseCount"
        <| fun _ ->
            let outcomes = [ outcome "all-resolved" 2; outcome "all-resolved" 3; outcome "all-resolved" 5 ]

            let r = computeSequencerRollup outcomes
            Expect.equal r.SequencedHits 3 "three all-resolved outcomes ⇒ three hits"
            Expect.equal r.SequencedFallThroughRate 0.0 "no fall-throughs ⇒ rate is 0"

            Expect.floatClose
                Accuracy.high
                r.MeanClausesPerSequence
                (10.0 / 3.0)
                "mean clauses-per-sequence over [2; 3; 5] is 10/3"

        testCase "resolver-side misses count toward fall-through rate"
        <| fun _ ->
            // Two hits + four resolver-side misses (one of each
            // fall-through outcome) ⇒ 4/6 fall-through rate.
            let outcomes = [
                outcome "all-resolved" 2
                outcome "all-resolved" 2
                outcome "partial-fall-through" 2
                outcome "sequence-capped" 9
                outcome "handler-failed-mid-sequence" 3
                outcome "handler-timed-out-mid-sequence" 3
            ]

            let r = computeSequencerRollup outcomes
            Expect.equal r.SequencedHits 2 "two all-resolved outcomes ⇒ two hits"

            Expect.floatClose
                Accuracy.high
                r.SequencedFallThroughRate
                (4.0 / 6.0)
                "four resolver misses out of six attempts ⇒ 4/6"

            Expect.floatClose
                Accuracy.high
                r.MeanClausesPerSequence
                ((2.0 + 2.0 + 2.0 + 9.0 + 3.0 + 3.0) / 6.0)
                "mean over all outcomes regardless of bucket"

        testCase "user-interrupts sit in denominator but NOT in fall-through numerator"
        <| fun _ ->
            // This is the load-bearing distinction: a paused or taken-
            // over sequence is a user-intent interrupt, not a resolver
            // failure. Counting them in the fall-through numerator would
            // bias the operator's tuning signal — make pattern coverage
            // look worse than it is.
            let outcomes = [
                outcome "all-resolved" 2
                outcome "all-resolved" 2
                outcome "partial-fall-through" 2
                outcome "paused-mid-sequence" 4
                outcome "taken-over-mid-sequence" 4
            ]

            let r = computeSequencerRollup outcomes
            Expect.equal r.SequencedHits 2 "two all-resolved outcomes ⇒ two hits"

            Expect.floatClose
                Accuracy.high
                r.SequencedFallThroughRate
                (1.0 / 5.0)
                "only the partial-fall-through counts as a resolver miss; pause + take-over do not"

        testCase "unknown outcome string sits in denominator but not in either named bucket"
        <| fun _ ->
            // Forward-compat: a future outcome kind the rollup doesn't
            // know about still counts toward the total-attempts
            // denominator (so MeanClausesPerSequence stays accurate)
            // but is silently excluded from both the hits count and
            // the fall-through numerator until the rollup learns about
            // it. Surfaces as a smaller-than-expected pair of totals
            // for an operator who notices.
            let outcomes = [ outcome "all-resolved" 2; outcome "future-outcome-kind" 4 ]

            let r = computeSequencerRollup outcomes
            Expect.equal r.SequencedHits 1 "only the all-resolved counts as a hit"
            Expect.equal r.SequencedFallThroughRate 0.0 "unknown outcome is not a fall-through"

            Expect.floatClose
                Accuracy.high
                r.MeanClausesPerSequence
                ((2.0 + 4.0) / 2.0)
                "mean still spans every outcome event"
    ]

let private wireRoundTripTests =
    testList "SequencedClauseBeacon / SequenceOutcomeBeacon wire round-trip" [
        testCase "camelCase wire JSON deserialises into PascalCase F# fields"
        <| fun _ ->
            // The fast-path-resolver companion emits these beacons via
            // raw `sprintf` JSON with lowercase keys. Newtonsoft.Json
            // matches property names case-insensitively during
            // deserialisation by default, so the F# PascalCase fields
            // populate without any custom contract resolver.
            let clauseJson =
                """{"clauseIndex":1,"clauseText":"set brand to Dove","patternMatched":"set brand to {value}","actionKind":"set-field","totalClauses":2}"""

            let clause =
                JsonSerializer.Deserialize<SequencedClauseBeacon>(clauseJson, jsonOptions)

            Expect.equal clause.ClauseIndex 1 "clauseIndex → ClauseIndex"
            Expect.equal clause.ClauseText "set brand to Dove" "clauseText → ClauseText"
            Expect.equal clause.PatternMatched "set brand to {value}" "patternMatched → PatternMatched"
            Expect.equal clause.ActionKind "set-field" "actionKind → ActionKind"
            Expect.equal clause.TotalClauses 2 "totalClauses → TotalClauses"

            let outcomeJson =
                """{"outcome":"all-resolved","instruction":"set country to UK and set brand to Dove","clauseCount":2,"clausesCompleted":2}"""

            let outcome =
                JsonSerializer.Deserialize<SequenceOutcomeBeacon>(outcomeJson, jsonOptions)

            Expect.equal outcome.Outcome "all-resolved" "outcome → Outcome"
            Expect.equal outcome.Instruction "set country to UK and set brand to Dove" "instruction → Instruction"
            Expect.equal outcome.ClauseCount 2 "clauseCount → ClauseCount"
            Expect.equal outcome.ClausesCompleted 2 "clausesCompleted → ClausesCompleted"

        testCase "event-store round-trip — PascalCase serialise + case-insensitive deserialise"
        <| fun _ ->
            // The handler serialises via `toJson beacon` (PascalCase
            // output) into the event store; the telemetry decoder
            // reads it back via the same `Newtonsoft.Json` settings.
            // Verify the round-trip preserves every field so a written
            // event surfaces in the rollup correctly.
            let original: SequenceOutcomeBeacon = {
                Outcome = "sequence-capped"
                Instruction = "do nine different things separated by and"
                ClauseCount = 9
                ClausesCompleted = 0
            }

            let payloadJson = JsonSerializer.Serialize(original, jsonOptions)

            let decoded =
                JsonSerializer.Deserialize<SequenceOutcomeBeacon>(payloadJson, jsonOptions)

            Expect.equal decoded original "every field round-trips through the event-store JSON"
    ]

let tests =
    testList "FastPathSequencerTelemetry" [ rollupTests; wireRoundTripTests ]
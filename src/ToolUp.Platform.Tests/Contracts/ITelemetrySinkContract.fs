// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.ITelemetrySinkContract

open Expecto
open ToolUp.Platform

// ─── Phase 163 — ITelemetrySink conformance pack ────────────────────────
//
// The portable contract any telemetry sink must satisfy: a stable non-empty
// `Name`, and a `Track` that completes (best-effort, never throwing across
// the boundary) for both a bare event and an event carrying properties.
// Bound to the no-op default, an in-test recording sink, and the GA4
// companion (env-gated).

let private sampleEvent: TelemetryEvent = {
    Event = "report_exported"
    Properties = Map [ "format", "pdf"; "module", "sales" ]
}

let tests (name: string) (factory: unit -> ITelemetrySink) =
    testList $"{name} — ITelemetrySink contract" [
        test "Name is a stable non-empty identifier" {
            let sink = factory ()
            Expect.isNotEmpty sink.Name "sink Name must be non-empty"
        }

        testCaseAsync "Track of a property-bearing event completes (best-effort, no throw)"
        <| async {
            let sink = factory ()
            // Must not throw — the test failing on an exception is the assertion.
            do! sink.Track("scope-a", sampleEvent)
        }

        testCaseAsync "Track of a bare event (no properties) completes"
        <| async {
            let sink = factory ()

            do!
                sink.Track(
                    "scope-a",
                    {
                        Event = "page_view"
                        Properties = Map.empty
                    }
                )
        }
    ]
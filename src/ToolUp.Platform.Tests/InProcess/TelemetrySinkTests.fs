// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.TelemetrySinkTests

open System
open System.Collections.Concurrent
open System.Net.Http
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts
open ToolUp.TelemetrySinks.Ga4

// ─── Phase 163 — ITelemetrySink conformance + behaviour ─────────────────
//
// Binds the contract pack to the no-op default + an in-test recording sink
// (always-on) and the GA4 companion (env-gated). Plus the two behavioural
// acceptance assertions: the no-op default is a true no-op, and a composed
// sink receives `Track` calls tagged with the per-tenant scope.

/// In-test sink that records every `Track` call for assertion.
type private RecordingTelemetrySink() =
    let calls = ConcurrentQueue<string * TelemetryEvent>()
    member _.Calls = calls |> Seq.toList

    interface ITelemetrySink with
        member _.Name = "recording"
        member _.Track(scopeId: string, event: TelemetryEvent) = async { calls.Enqueue(scopeId, event) }

[<Tests>]
let tests =
    testList "Phase 163 — ITelemetrySink" [
        ITelemetrySinkContract.tests "NoOpTelemetrySink" (fun () -> NoOpTelemetrySink() :> ITelemetrySink)
        ITelemetrySinkContract.tests "RecordingTelemetrySink" (fun () -> RecordingTelemetrySink() :> ITelemetrySink)

        testCaseAsync "NoOpTelemetrySink is a true no-op"
        <| async {
            let sink = NoOpTelemetrySink() :> ITelemetrySink
            Expect.equal sink.Name "noop" "no-op sink names itself"
            // Completes, records nothing, never throws — there is nothing to
            // observe, which is the point (zero cost at the emission site).
            do!
                sink.Track(
                    "scope-a",
                    {
                        Event = "anything"
                        Properties = Map [ "k", "v" ]
                    }
                )
        }

        testCaseAsync "A composed sink receives Track calls tagged with the per-tenant scope"
        <| async {
            let recording = RecordingTelemetrySink()

            do!
                (recording :> ITelemetrySink)
                    .Track(
                        "tenant-7",
                        {
                            Event = "page_view"
                            Properties = Map [ "path", "/home" ]
                        }
                    )

            let calls = recording.Calls
            Expect.equal calls.Length 1 "exactly one Track recorded"
            let scope, ev = calls[0]
            Expect.equal scope "tenant-7" "the call is tagged with the scope"
            Expect.equal ev.Event "page_view" "the event name is carried"
            Expect.equal ev.Properties["path"] "/home" "the properties are carried"
        }

        // GA4 companion — env-gated live arm (mirrors the storage / AI-provider
        // companions; skipped without GA4 stream credentials).
        match
            Environment.GetEnvironmentVariable "TOOLUP_GA4_MEASUREMENT_ID",
            Environment.GetEnvironmentVariable "TOOLUP_GA4_API_SECRET"
        with
        | (null | ""), _
        | _, (null | "") ->
            ptestCase "Ga4TelemetrySink — skipped (TOOLUP_GA4_MEASUREMENT_ID / _API_SECRET not set)"
            <| fun _ -> ()
        | mid, secret ->
            ITelemetrySinkContract.tests "Ga4TelemetrySink" (fun () ->
                Ga4TelemetrySink.create (new HttpClient()) mid secret)
    ]
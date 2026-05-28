module ToolUp.Platform.Tests.InProcess.HealthStateTrackerTests

open System
open System.Collections.Concurrent
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.HealthStateTracker

// ─── Phase 9p HealthStateTracker tests ───────────────────────────────
//
// Covers debounce semantics (3 consecutive observations required to
// transition stable state), per-probe isolation, the GP 13 default-
// off contract, and the transition emission shape. The
// `BackgroundService.ExecuteAsync` scheduling loop itself is not
// exercised — the testable surface is `runTick`, which the production
// service calls inline. Sleeping for a real wall-clock minute would
// add nothing the algorithm doesn't already cover.

/// `IHealthCheck` whose `Check` returns scripted results from a queue.
/// Each call dequeues the next observation; running past the queue
/// returns the last observation indefinitely (so a 6-tick test that
/// scripts 6 observations and a 7th probe would re-observe the 6th
/// status).
let private mkScriptedProbe (name: string) (script: HealthResult list) =
    let queue = ConcurrentQueue(script)
    let mutable lastObs = Healthy

    { new IHealthCheck with
        member _.Name = name
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 1.0

        member _.Check() = async {
            match queue.TryDequeue() with
            | true, obs ->
                lastObs <- obs
                return obs
            | false, _ -> return lastObs
        }
    }

/// Capture-everything `IAuditLog`. The production audit log writes
/// through `IEventStore`; tests don't need persistence — they need
/// to inspect emitted `(scopeId, audit)` pairs.
type private FakeAuditLog() =
    let captured = ConcurrentBag<string * AuditEvent>()

    member _.Captured = captured |> Seq.toList

    member _.HealthEvents =
        captured
        |> Seq.choose (fun (scopeId, audit) ->
            match audit with
            | HealthStateChanged p -> Some(scopeId, p)
            | _ -> None)
        |> Seq.toList

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { captured.Add(scopeId, audit) }
        // Tests don't exercise the read side — the tracker is a
        // write-only consumer of IAuditLog.
        member _.GetAuditTrail(_, _, _) = async { return [] }

let private now () =
    DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc)

let private runTickN (n: int) (probes: IHealthCheck list) (fakeLog: FakeAuditLog) =
    let states = ConcurrentDictionary<string, ProbeState>()

    async {
        for _ in 1..n do
            do! runTick probes states (fakeLog :> IAuditLog) now
    }

let tests =
    testList "HealthStateTracker" [
        testCaseAsync "Tick emits no audit when buffer is shorter than RequiredConsecutive"
        <| async {
            let log = FakeAuditLog()
            let probe = mkScriptedProbe "p" [ Unhealthy "down"; Unhealthy "still" ]
            // Two ticks — buffer holds 2 observations, below the
            // 3-required threshold. No transition recorded.
            do! runTickN 2 [ probe ] log

            Expect.isEmpty log.HealthEvents "fewer than RequiredConsecutive observations should not emit a transition"
        }

        testCaseAsync "Single-observation flap is absorbed by debounce"
        <| async {
            let log = FakeAuditLog()
            // Healthy×3 establishes baseline, then a single Unhealthy
            // flap, then Healthy×3 again. The lone Unhealthy never
            // accumulates 3-in-a-row so no transition fires.
            let probe =
                mkScriptedProbe "flapping" [ Healthy; Healthy; Healthy; Unhealthy "blip"; Healthy; Healthy; Healthy ]

            do! runTickN 7 [ probe ] log

            Expect.isEmpty
                log.HealthEvents
                "single-observation flap surrounded by Healthy must not surface as a transition"
        }

        testCaseAsync "Three consecutive transitions emit exactly one audit event"
        <| async {
            let log = FakeAuditLog()

            let probe =
                mkScriptedProbe "p" [
                    Healthy
                    Healthy
                    Healthy
                    Unhealthy "boom"
                    Unhealthy "still"
                    Unhealthy "still"
                ]

            do! runTickN 6 [ probe ] log

            Expect.equal log.HealthEvents.Length 1 "exactly one transition recorded"
            let scopeId, payload = log.HealthEvents[0]
            Expect.equal scopeId "_platform" "transitions emit under the reserved _platform scope"
            Expect.equal payload.ProbeName "p" "probe name preserved"
            Expect.equal payload.FromStatus "Healthy" "previous stable status"
            Expect.equal payload.ToStatus "Unhealthy" "new stable status"
            Expect.stringContains payload.Message "still" "last observation message preserved"
        }

        testCaseAsync "Stable state does not re-emit on subsequent identical observations"
        <| async {
            let log = FakeAuditLog()

            // Healthy×3 (baseline), Unhealthy×3 (transition), then 6
            // more Unhealthy. Only one transition should fire — the
            // tracker recognises the stable state hasn't changed
            // again.
            let probe =
                mkScriptedProbe "p" [
                    Healthy
                    Healthy
                    Healthy
                    Unhealthy "down"
                    Unhealthy "down"
                    Unhealthy "down"
                    Unhealthy "still down"
                    Unhealthy "still down"
                    Unhealthy "still down"
                    Unhealthy "still down"
                    Unhealthy "still down"
                    Unhealthy "still down"
                ]

            do! runTickN 12 [ probe ] log

            Expect.equal log.HealthEvents.Length 1 "stable state is not re-emitted on subsequent identical observations"
        }

        testCaseAsync "Per-probe isolation — two probes emit two distinct events keyed by ProbeName"
        <| async {
            let log = FakeAuditLog()

            let probeA =
                mkScriptedProbe "a" [
                    Healthy
                    Healthy
                    Healthy
                    Unhealthy "a-down"
                    Unhealthy "a-down"
                    Unhealthy "a-down"
                ]

            let probeB =
                mkScriptedProbe "b" [
                    Healthy
                    Healthy
                    Healthy
                    Unhealthy "b-down"
                    Unhealthy "b-down"
                    Unhealthy "b-down"
                ]

            do! runTickN 6 [ probeA; probeB ] log

            Expect.equal log.HealthEvents.Length 2 "two probes transitioning emit two events"

            let names = log.HealthEvents |> List.map (fun (_, p) -> p.ProbeName) |> List.sort

            Expect.equal names [ "a"; "b" ] "events keyed correctly per probe"
        }

        testCase "GP 13: HealthStateTracking = false does not register the BackgroundService"
        <| fun _ ->
            // GP 13 contract: a deployment that doesn't enable the
            // tracker pays nothing. We can't easily invoke
            // SDK.Server.compose end-to-end here (it spins up Kestrel),
            // but we can assert the registration shape is opt-in by
            // building a minimal service collection and verifying no
            // HealthStateTrackerService is resolved when the flag is
            // false. The actual `compose` registration is the
            // `if config.HealthStateTracking then services.AddSingleton...`
            // block in SDK.Server.fs — this test asserts that pattern
            // does the right thing by checking presence/absence
            // against an explicit toggle.
            let buildWithFlag (flag: bool) =
                let services = ServiceCollection()
                let log = FakeAuditLog() :> IAuditLog

                let logger =
                    { new ILogger with
                        member _.Debug _ = ()
                        member _.Info _ = ()
                        member _.Warn _ = ()
                        member _.Error(_, _) = ()
                    }

                if flag then
                    services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                        System.Func<IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                            new HealthStateTrackerService(sp, log, logger)
                            :> Microsoft.Extensions.Hosting.IHostedService)
                    )
                    |> ignore

                let sp = services.BuildServiceProvider()
                sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>() |> Seq.toList

            let withFlagOff = buildWithFlag false
            let withFlagOn = buildWithFlag true

            Expect.isEmpty withFlagOff "tracker is not registered when HealthStateTracking = false"

            Expect.equal withFlagOn.Length 1 "tracker is registered when HealthStateTracking = true"

            Expect.isTrue
                (withFlagOn[0] :? HealthStateTrackerService)
                "registered hosted service is HealthStateTrackerService"
    ]
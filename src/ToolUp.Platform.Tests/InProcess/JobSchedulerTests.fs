module ToolUp.Platform.Tests.InProcess.JobSchedulerTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tracing
open ToolUp.Platform.Tests.Contracts

// ─── InProcessJobScheduler — IJobScheduler contract binding ──────
//
// Binds the `IJobScheduler` contract pack to the Phase 9b in-process
// scheduler over `BlobJobStore` rooted in a fresh temp directory per
// factory call. Cross-test isolation is structural — each factory
// call gets its own filesystem subtree, and two scope ids are GUID-
// suffixed.
//
// The contract pack only exercises the IJobScheduler interface
// methods (Schedule, Cancel, Disable, Enable, TriggerOnce, Get,
// ListJobs). The BackgroundService dispatch loop is NOT started —
// `BackgroundService.ExecuteAsync` is only invoked by ASP.NET Core
// hosting, and this binding never calls `StartAsync`. All Phase 9c
// portability rules are about the interface shape, not the dispatch
// runtime.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Stub `INotificationChannel` that swallows publishes — the
/// scheduler only publishes a `SystemMessage` on dead-letter, which
/// the contract pack does not exercise.
let private silentChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async { return () }

        member _.Subscribe(_, _) = async { return Guid.NewGuid() }

        member _.Unsubscribe(_) = async { return () }
    }

let private buildScheduler () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-jobsched-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(root) |> ignore
    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let jobStore = JobStore.create storage eventStore
    let config = ServerConfig.defaults

    JobScheduler.create jobStore eventStore silentChannel config silentLogger (NoOpActivitySink() :> IActivitySink)

// Phase 9b.A — telemetry interface tests. Drift detection itself lives
// in the BackgroundService loop and uses `DateTime.UtcNow` directly,
// so the missed-tick edge is exercised manually per the recipe in
// `technical-guide/06-jobs-ingestion-and-diagnostics.md`. These tests
// pin the read-side contract: a fresh scheduler reports zero, the
// snapshot's `GeneratedAt` advances per call, and the
// `IJobSchedulerTelemetry` upcast succeeds (so DI registrations against
// the interface resolve at runtime).
let private telemetryTests =
    testList "InProcessJobScheduler — IJobSchedulerTelemetry" [
        test "fresh scheduler reports zero missed ticks and no last-drift" {
            let scheduler = buildScheduler () :> IJobSchedulerTelemetry
            let snap = scheduler.Snapshot()

            Expect.equal snap.TickMissedCount60Min 0 "no missed ticks at start"
            Expect.isNone snap.LastDriftMs "no last drift recorded"
            Expect.isNone snap.LastTickMissedAt "no last miss recorded"
        }

        test "consecutive snapshots advance GeneratedAt" {
            let scheduler = buildScheduler () :> IJobSchedulerTelemetry
            let first = scheduler.Snapshot()
            System.Threading.Thread.Sleep 5
            let second = scheduler.Snapshot()

            Expect.isGreaterThanOrEqual second.GeneratedAt first.GeneratedAt "GeneratedAt monotonic across calls"
        }

        test "InProcessJobScheduler upcasts to IJobSchedulerTelemetry" {
            let scheduler = buildScheduler ()

            match (scheduler :> obj) with
            | :? IJobSchedulerTelemetry -> ()
            | _ -> failtest "scheduler does not implement IJobSchedulerTelemetry"
        }
    ]

let tests =
    let factory () =
        let scheduler = buildScheduler () :> IJobScheduler
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        scheduler, "team-a-" + suffix, "team-b-" + suffix

    testList "InProcessJobScheduler" [ IJobSchedulerContract.tests "InProcessJobScheduler" factory; telemetryTests ]
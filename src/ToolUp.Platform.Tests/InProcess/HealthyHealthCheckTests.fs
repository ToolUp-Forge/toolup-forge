module ToolUp.Platform.Tests.InProcess.HealthyHealthCheckTests

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Tests.Contracts

/// Fake `IHealthCheck` that always returns `Healthy`. Binds the shared
/// `IHealthCheckContract` pack to exercise the Healthy code path. Real
/// production probes (Redis, Claude, blob storage, etc.) bind the same
/// pack with their own factory in their respective companion test
/// projects when env-gated dependencies are available.
let tests =
    let factory () =
        { new IHealthCheck with
            member _.Name = "fake_healthy"
            member _.Kind = Readiness
            member _.Timeout = TimeSpan.FromSeconds 1.0
            member _.Check() = async { return Healthy }
        }

    IHealthCheckContract.tests "FakeHealthyCheck" factory Healthy
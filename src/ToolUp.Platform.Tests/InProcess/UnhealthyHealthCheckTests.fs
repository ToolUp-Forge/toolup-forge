module ToolUp.Platform.Tests.InProcess.UnhealthyHealthCheckTests

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Tests.Contracts

/// Fake `IHealthCheck` that always returns `Unhealthy`. Binds the
/// shared `IHealthCheckContract` pack to exercise the Unhealthy code
/// path — `/ready` should return 503 when any registered probe is
/// Unhealthy.
let tests =
    let factory () =
        { new IHealthCheck with
            member _.Name = "fake_unhealthy"
            member _.Kind = Readiness
            member _.Timeout = TimeSpan.FromSeconds 1.0

            member _.Check() = async { return Unhealthy "synthetic failure" }
        }

    IHealthCheckContract.tests "FakeUnhealthyCheck" factory (Unhealthy "synthetic failure")
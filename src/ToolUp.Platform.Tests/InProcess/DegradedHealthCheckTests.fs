module ToolUp.Platform.Tests.InProcess.DegradedHealthCheckTests

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Tests.Contracts

/// Fake `IHealthCheck` that always returns `Degraded`. Binds the shared
/// `IHealthCheckContract` pack to exercise the Degraded code path —
/// `/ready` should stay 200 when every probe is Healthy or Degraded
/// (only Unhealthy flips it to 503).
let tests =
    let factory () =
        { new IHealthCheck with
            member _.Name = "fake_degraded"
            member _.Kind = Readiness
            member _.Timeout = TimeSpan.FromSeconds 1.0

            member _.Check() = async { return Degraded "synthetic warning" }
        }

    IHealthCheckContract.tests "FakeDegradedCheck" factory (Degraded "synthetic warning")
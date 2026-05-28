module ToolUp.Platform.Tests.InProcess.ErrorConfigValidatorTests

open System
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Tests.Contracts

/// Fake `IConfigValidator` that always returns `Error`. Binds the
/// shared `IConfigValidatorContract` pack to exercise the Error code
/// path. Aggregator-level abort behaviour (throwing
/// `ConfigPreflightFailedException`) is exercised separately in
/// `ConfigValidatorAggregatorTests` — this binding only verifies the
/// per-validator contract.
let tests =
    let factory () =
        { new IConfigValidator with
            member _.Name = "fake_error_validator"
            member _.Timeout = TimeSpan.FromSeconds 1.0
            member _.Validate() = async { return Error "demo error — dependency unreachable" }
        }

    IConfigValidatorContract.tests "FakeErrorValidator" factory (Error "demo error — dependency unreachable")
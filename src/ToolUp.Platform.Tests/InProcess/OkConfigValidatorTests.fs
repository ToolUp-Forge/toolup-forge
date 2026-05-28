module ToolUp.Platform.Tests.InProcess.OkConfigValidatorTests

open System
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Tests.Contracts

/// Fake `IConfigValidator` that always returns `Ok`. Binds the shared
/// `IConfigValidatorContract` pack to exercise the Ok code path.
/// Real production validators (OIDC discovery, blob sentinel,
/// SMTP TCP-connect) bind the same pack with their own factory in
/// their respective companion test projects when env-gated
/// dependencies are available.
let tests =
    let factory () =
        { new IConfigValidator with
            member _.Name = "fake_ok_validator"
            member _.Timeout = TimeSpan.FromSeconds 1.0
            member _.Validate() = async { return Ok }
        }

    IConfigValidatorContract.tests "FakeOkValidator" factory Ok
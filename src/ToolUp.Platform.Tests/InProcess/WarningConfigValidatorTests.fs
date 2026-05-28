module ToolUp.Platform.Tests.InProcess.WarningConfigValidatorTests

open System
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Tests.Contracts

/// Fake `IConfigValidator` that always returns `Warning`. Binds the
/// shared `IConfigValidatorContract` pack to exercise the Warning code
/// path. Warning means the dependency is reachable but flagged (slow
/// handshake, deprecated config) — startup proceeds, the message is
/// logged at Warn level.
let tests =
    let factory () =
        { new IConfigValidator with
            member _.Name = "fake_warning_validator"
            member _.Timeout = TimeSpan.FromSeconds 1.0
            member _.Validate() = async { return Warning "demo warning — dependency reachable but flagged" }
        }

    IConfigValidatorContract.tests
        "FakeWarningValidator"
        factory
        (Warning "demo warning — dependency reachable but flagged")
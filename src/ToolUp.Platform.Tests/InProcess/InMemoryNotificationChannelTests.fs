module ToolUp.Platform.Tests.InProcess.InMemoryNotificationChannelTests

open ToolUp.Platform
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.Tests.Contracts

/// Binds the shared `INotificationChannelContract` pack to the default
/// `InMemoryNotificationChannel`. Runs unconditionally — in-memory has
/// no external dependencies. A second binding under a `TOOLUP_REDIS_*`
/// gate exercises the same pack against Redis (Phase 6e).
let tests =
    let factory () =
        InMemoryNotificationChannel(None) :> INotificationChannel

    INotificationChannelContract.tests "InMemoryNotificationChannel" factory
# ToolUp.Platform.NotificationChannels.Redis

Redis `INotificationChannel` for `ToolUp.Platform` — scope-isolated pub/sub over `StackExchange.Redis`. Replaces the default `InMemoryNotificationChannel` for multi-instance / multi-silo deployments where SSE subscribers and notification publishers may live on different process boundaries.

Activated via `TOOLUP_NOTIFICATION_CHANNEL=redis` + `TOOLUP_REDIS_CONNECTION=<conn-string>`. Per-scope topic isolation is structural (one topic per `ScopeId`) — not a post-hoc filter.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

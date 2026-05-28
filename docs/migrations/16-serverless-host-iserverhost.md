# Phase 16 — `IServerHost` host abstraction + `ServerlessHost` BackgroundService gating

**What changes.** `SDK.Server.compose` (and the matching `composeWithAI` / `composeWithRAG` wrappers) now return `IServerHost` instead of `int`. Both the long-running Kestrel default and serverless host adapters drive the same configured request pipeline through this seam:

- `ServerApp.run` / `AIServerApp.run` / `RAGServerApp.run` (and every per-companion `run` helper) chain `IServerHost.RunBlocking()` at the tail to preserve the prior `int` exit-code semantics. No application-side change needed for the Kestrel default.
- Serverless host adapters (`ToolUp.Hosts.AzureFunctions`, future Lambda / GCF) resolve the `IServerHost` from the composition root and drive the pipeline per cloud invocation via `IServerHost.Invoke(ctx)`.

`ServerConfig.ServerlessHost = ServerlessHost` now gates every `IHostedService` registration in `compose`. A deployment opting into the flag (typically alongside `JobScheduler = NoJobScheduler`, `Webhooks = NoWebhooks`, and `Notifications = NoNotificationsExplicit`) yields a serverless-clean composition the Functions / Lambda / GCF adapter can drive without trying to start Kestrel or run background work the platform won't keep alive.

**Scope.** SDK-internal refactor; no public surface that consumer code calls directly. The breaking-shape change is `compose`'s return type, but `compose` is `EditorBrowsable.Never` (the documented entrypoint is `ServerApp.run` and its companions, which all preserve their `int` shape).

## What gets gated under `ServerlessHost = ServerlessHost`

Every `IHostedService` registration now matches on `config.ServerlessHost` and skips when the deployment opts into the serverless shape. The DI singletons that other handlers may resolve (`IWebhookDispatcher`, `IJobScheduler`, `TransactionalDispatcher`, `AuditReplicator`, `UsageBatchFlusher`) still register so admin routes and sibling worker silos resolve them — only the BackgroundService run-loop is gated off.

| Subsystem | What's gated | What still registers |
|---|---|---|
| Webhook dispatcher (`ComposeJobs.registerWebhookSubsystem`) | `WebhookDispatcherService` IHostedService | `IWebhookRegistry`, `IWebhookDeliveryLog`, `IWebhookDispatcher` singletons |
| Usage batch flusher (`SDK.Server.compose`) | `UsageBatchFlusher` IHostedService | `IUsageLog` singleton |
| Job scheduler (`ComposeJobs.registerJobScheduler`) | `InProcessJobScheduler` IHostedService | `IJobStore`, `IJobScheduler` singletons |
| OAuth state-store cleanup (`ComposeJobs.registerDataIngestion`) | `OAuthStateCleanupService` IHostedService | `IOAuthStateStore` singleton |
| OAuth token refresher startup-Recover (`ComposeJobs.registerOAuthRefresher`) | Inline IHostedService that calls `refresher.Recover()` on startup | `IOAuthTokenRefresher` singleton |
| Transactional dispatcher (`ComposeNotifications.registerTransactionalDispatcher`) | `TransactionalDispatcher` IHostedService | `TransactionalDispatcher` + per-sink `INotificationSink` singletons |
| Audit replicator (`ComposeAudit.registerAuditReplicatorHosting`) | `AuditReplicator` IHostedService | `AuditReplicator` + per-sink `IAuditSink` singletons |
| Health-state tracker (`SDK.Server.compose`) | `HealthStateTrackerService` IHostedService | (no DI singletons; the tracker is purely run-loop) |

## Diff to apply

**Consumers running the default Kestrel host (`ServerlessHost = KestrelHost`):** no source-level change. `ServerApp.run` / `AIServerApp.run` / `RAGServerApp.run` (and every per-companion `run`) absorb the new IServerHost shape internally; the `[<EntryPoint>] let main args = ... |> ServerApp.run` shape compiles and runs unchanged.

**Consumers wiring an `IServerHost`-aware extension (advanced — e.g. an AspNetCore-integration package hosting compose's pipeline in a non-Kestrel context):** the `compose` low-level entrypoint now returns `IServerHost`; consume `host.RunBlocking()` / `host.Invoke ctx` directly instead of calling compose and assuming an `int` return.

```fsharp
// Before — direct compose call (rare; almost no consumer does this):
let exitCode =
    compose handlers dataTypes config (Some auth) extensions ...
// `compose` returned int

// After:
let host =
    compose handlers dataTypes config (Some auth) extensions ...
// `host : IServerHost`
let exitCode = host.RunBlocking()  // preserves Kestrel default behaviour
```

**Apps opting into serverless hosting** wire the host-adapter companion in addition to flipping `ServerlessHost = ServerlessHost`:

```fsharp
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        Mode = Anonymous
        ServerlessHost = ServerlessHost
        JobScheduler = NoJobScheduler
        Webhooks = NoWebhooks
        Notifications = NoNotificationsExplicit
        AuditLog = NoAuditLog          // skip the audit replicator entirely
        UsageMetering = NoUsageMetering // skip the flusher entirely
        HealthStateTracking = false     // skip the health tracker
}
|> ServerApp.addModule (myModule.register ())
|> ServerApp.run   // companion adapter intercepts before `RunBlocking()` is called
```

The host adapter companion (`ToolUp.Hosts.AzureFunctions`, etc.) is responsible for resolving the `IServerHost`, calling `Host.StartAsync()` once at cold-start (the no-op when every background service is gated off), and driving `IServerHost.Invoke ctx` per cloud invocation.

## Verification

1. `dotnet build ToolUp.Forge.sln` — clean.
2. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — 0 failures, 0 errored.
3. Boot a stock deployment (default `ServerlessHost = KestrelHost`) — startup log byte-equivalent to the pre-Phase-16 shape; `app.Run()` blocks until shutdown; `kill` returns exit code `0`.
4. Boot an opted-in deployment (`ServerlessHost = ServerlessHost`, `JobScheduler = NoJobScheduler`, `Webhooks = NoWebhooks`) — confirm the `IHostedService` count under `/dev/inspect`'s service-descriptor panel drops to zero (or to only the host-adapter's own services).

## Rollback

Revert the `ServerlessHost = ServerlessHost` flag in `ServerConfig`; the gating short-circuits restore today's behaviour byte-for-byte. The IServerHost seam is non-breaking at the consumer-facing surface (`ServerApp.run` still returns `int`); rolling back the seam itself requires reverting the commit.

## Out of scope (Phase 16 follow-ups)

- The `ToolUp.Hosts.AzureFunctions` companion adapter is shipping in a follow-up commit alongside this one.
- `ToolUp.Hosts.AwsLambda` shipped as a separate follow-up — `bridgeV2` (API Gateway HTTP API v2 + Lambda Function URLs), `bridgeV1` (API Gateway REST), and `bridgeAlb` (Application Load Balancer) translate each event payload shape through `IServerHost.Invoke`. The package is purely additive — no consumer adoption work; deployments opt in by adding the `PackageReference` and writing a one-handler Lambda function per event source.
- `ToolUp.Hosts.GoogleCloudFunctions` shipped as a separate follow-up — the Functions Framework for .NET already invokes `IHttpFunction.HandleAsync(HttpContext)`, so the bridge collapses to swapping `RequestServices` onto `IServerHost.App.Services` and calling `IServerHost.Invoke ctx`. Ships a default `ServerHostFunction : IHttpFunction` consumers subclass (or use directly) so the framework's single-`IHttpFunction`-in-assembly auto-detection finds the target with no extra wiring. The package is purely additive — no consumer adoption work; deployments opt in by adding the `PackageReference` and registering one `FunctionsStartup`.
- The `ProcessProfile = WebOnly | WorkerOnly | DispatcherOnly` per-profile gating is a separate follow-up.

## Consumers

The migration is **N-A** for any consumer running the default `ServerlessHost = KestrelHost` — no source-level change is needed; the `IServerHost` return-type change is absorbed by the `ServerApp.run` / `AIServerApp.run` / `RAGServerApp.run` wrappers.

A consumer adopting **serverless hosting** also adopts the matching host-adapter companion package and tunes the gated-off subsystems' modes (`JobScheduler = NoJobScheduler`, `Webhooks = NoWebhooks`, etc.) to match.

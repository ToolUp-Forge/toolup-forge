# Phase 16a — `ProcessProfile` gating (`WebOnly` / `WorkerOnly` / `DispatcherOnly`)

**What changes.** `ServerConfig.ProcessProfile` now drives `IHostedService` registration in `SDK.Server.compose` through a centralised gating matrix. Each role activates a documented subset of the SDK's background subsystems so a deployment can horizontally scale per role against a shared persistence tier — same binary, same image, different `TOOLUP_PROCESS_PROFILE` env var.

The matrix lives in `Server/Compose/ProcessProfileGate.fs` and combines two orthogonal `ServerConfig` switches:

- `ServerlessHost` (Phase 16) — `ServerlessHost` short-circuits every background subsystem (host adapter platforms don't keep the process alive long enough for any of them to run).
- `ProcessProfile` (Phase 16a) — `AllInOne` (default) runs everything in one process; `WebOnly` runs none; `WorkerOnly` runs everything; `DispatcherOnly` runs only the outbound dispatchers.

## Gating matrix

| Subsystem | AllInOne | WebOnly | WorkerOnly | DispatcherOnly | ServerlessHost (any profile) |
|---|---|---|---|---|---|
| Job scheduler | ✓ | ✗ | ✓ | ✗ | ✗ |
| Webhook dispatcher | ✓ | ✗ | ✓ | ✓ | ✗ |
| Transactional dispatcher | ✓ | ✗ | ✓ | ✓ | ✗ |
| Audit replicator | ✓ | ✗ | ✓ | ✗ | ✗ |
| Usage batch flusher | ✓ | ✗ | ✓ | ✗ | ✗ |
| Health-state tracker | ✓ | ✗ | ✓ | ✗ | ✗ |
| OAuth state-store cleanup | ✓ | ✗ | ✓ | ✗ | ✗ |
| OAuth refresher startup-Recover | ✓ | ✗ | ✓ | ✗ | ✗ |
| HTTP middleware pipeline | ✓ | ✓ | ✗ | ✓ | ✓ |

Every gate-site in `compose` (and the per-concern Compose helpers) calls `ProcessProfileGate.shouldRegisterBackgroundService` with one of the `BackgroundSubsystem` DU cases. Adding a new subsystem only edits the matrix in one place.

## Diff to apply

**Existing deployments running the default `ProcessProfile = AllInOne`:** no change. `AllInOne` runs every background subsystem (identical to today's behaviour).

**Deployments wanting horizontal-scale-per-role:** flip the role per silo via `ServerConfig.ProcessProfile` or the `TOOLUP_PROCESS_PROFILE` env var (when `ServerConfigOverrides.fromEnv` is wired):

```fsharp
// Web tier silo — serves /api/* only; sibling worker drains jobs:
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        Mode = Team
        ProcessProfile = WebOnly
        // Persistence + cross-silo channel — point both silos at the same:
        EventStore = PersistentBlobBacked Retention.defaults
        JobScheduler = InProcessJobScheduler   // substrate still registers
        Notifications = NotificationsAuto      // wire Redis channel separately
}

// Worker tier silo — runs every background subsystem; no HTTP:
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        Mode = Team
        ProcessProfile = WorkerOnly
        EventStore = PersistentBlobBacked Retention.defaults
        JobScheduler = InProcessJobScheduler
        Notifications = NotificationsAuto
}
```

Both silos run from the same binary; the only difference is `ProcessProfile`. The Web tier registers job-scheduling routes (so `JobApiHandler` resolves `IJobScheduler` and persists `JobDefinition`s), but the scheduler `BackgroundService` doesn't tick locally — the Worker silo's `BackgroundService` registration drains the persisted queue.

## Cross-silo coordination caveats

- **Single-leader concerns** need cross-process coordination so two `WorkerOnly` instances don't mis-fire. Phase 9i shipped `IDistributedLock`, and Phase 16a took it where it applies here: the webhook dispatcher's failure-state read-modify-write (`ConsecutiveFailures` + auto-disable), previously last-write-wins across concurrent retry ladders. **Two things this list said and should not have:** there is no *webhook retry timer* — the dispatcher drains a per-replica in-process queue and its retry is an `Async.Sleep` inside one event's ladder — and the OAuth refresher owns no timer either, running as a scheduled job on `IJobScheduler`. What remains is the scheduler's cron due-job tick (a lock problem, unleased) and webhook/transactional event ownership (a durable-queue problem, which no lease can solve). So `ReplicaCount = 1` on `WorkerOnly` still stands. Full account: [`technical-guide/13-deployment-shapes.md`](../../src/ToolUp.Platform/technical-guide/13-deployment-shapes.md#cross-silo-coordination-contract).
- **Notification channel** must be a distributed implementation (Redis pub/sub via `src/NotificationChannels/Redis`) — the default `InMemoryNotificationChannel` won't bridge events between silos.
- **Event store** must be `PersistentBlobBacked` for the Web tier to write events the Worker tier reads (the in-memory store is per-process).

## WorkerOnly construction shape

The `WorkerOnly` profile today builds the same `WebApplication.CreateBuilder()` chain as every other profile, but `configurePipeline` is bypassed when `ProcessProfileGate.shouldRegisterHttpPipeline config = false`. Kestrel binds to its configured port but no handlers respond; sibling silos should not route HTTP at a `WorkerOnly` deployment.

A future refactor will swap to `Host.CreateApplicationBuilder()` for the `WorkerOnly` shape so the silo binds no port at all (the `IServerHost` interface's `createWorkerHost` helper supports it; the gating just doesn't construct it yet). Tracked on the phase body's deferred follow-ups.

## Verification

1. `dotnet build ToolUp.Forge.sln` — clean.
2. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — 0 failures, 0 errored.
3. Boot a stock deployment (default `AllInOne`) — startup log shows every IHostedService registration as today.
4. Boot a `ProcessProfile = WebOnly` deployment — the `/dev/inspect` Composition-seam panel (when shipped) confirms 0 background services registered; `/api/*` still serves.
5. Boot a `ProcessProfile = WorkerOnly` deployment — confirm `[Phase 16a] ProcessProfile = WorkerOnly` log line appears at compose, and no `/api/*` request returns 200 (HTTP not mounted).

## Rollback

Reset `ServerConfig.ProcessProfile = AllInOne`; the gating short-circuits restore today's behaviour byte-for-byte.

## Out of scope (Phase 16a follow-ups)

- ~~`/dev/inspect` Composition-seam panel reports the active `ProcessProfile` and the per-subsystem registration outcome.~~ **Shipped — Phase 16a tail.** The dev-inspect report now carries a `ProcessProfile` section with per-subsystem registration outcomes (see [`DevDiagnosticsHandler.fs`](../../src/ToolUp.Platform.Server/Server/DevDiagnosticsHandler.fs) — the `ProcessProfileSummary` DTO + the dedicated HTML panel).
- ~~TECHNICAL_GUIDE deployment-shapes section.~~ **Shipped — Phase 16a tail.** Lives at [`technical-guide/13-deployment-shapes.md`](../../src/ToolUp.Platform/technical-guide/13-deployment-shapes.md) with the three pure-Kestrel shapes (`AllInOne` / `WebOnly`+`WorkerOnly` / `WebOnly`+`WorkerOnly`+`DispatcherOnly`), the substrate contract, and the cross-silo coordination contract.
- ~~`IDistributedLock` (Phase 9i) cross-silo single-leader coordination.~~ **Shipped for the webhook half — Phase 16a tail.** The dispatcher's failure-state transition is held under `toolup:webhook-failure-state:{scopeId}:{subscriptionId}`. Still deferred: the scheduler's cron-tick election, and the durable queue that would give webhook events a cross-silo owner. `ReplicaCount = 1` remains the only safe shape for `WorkerOnly` / `DispatcherOnly`.
- `WorkerOnly` → `Host.CreateApplicationBuilder()` (no Kestrel binding) refactor. The `IServerHost.createWorkerHost` helper supports it; the compose-side construction-branch refactor is the follow-up commit.

## Consumers

The migration is **N-A** for any consumer running the default `ProcessProfile = AllInOne` — no source-level change is needed; the gating matrix is opt-in by setting `ProcessProfile` to a non-default case.

A consumer adopting **horizontal-scale-per-role** also adopts a distributed `INotificationChannel` (Redis), a `PersistentBlobBacked` `IEventStore`, and pins `ReplicaCount = 1` on the `WorkerOnly` silo until cron-tick election and a durable outbound queue land. A deployment that overrides `IDistributedLock` with a store-backed companion (`RedisDistributedLock`) should note that the eagerly-composed subsystems — the job scheduler, `BlobBackedPlatformAdminStore`, and now the webhook dispatcher — still bind the in-process default: threading a companion into them needs the lock resolved before `compose` constructs them, which is the standing Phase 9c follow-on. `WebhookDispatcher.createWithLock` is the seam on the webhook side for a consumer composing its own dispatcher.

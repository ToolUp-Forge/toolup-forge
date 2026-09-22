# ToolUp.JobSchedulers.Quartz

Quartz.NET `IJobScheduler` for `ToolUp.Platform` — cron, event and manual background-job dispatch over a Quartz `IScheduler`, with the deployment's own `IJobStore` as the canonical record of job definitions and run history.

Activated via `ServerConfig.JobScheduler = QuartzJobScheduler quartzConfig`.

## Why Quartz.NET

This is the **second** implementation of `IJobScheduler` / `IJobStore`, and it exists to test a claim rather than to add a feature: the SDK's [six portability rules](../../../docs/platform/portability-rules.md) assert that these interfaces can be implemented by a scheduling framework nobody designed them around. One implementation cannot show that. A second one, binding the shipped contract packs **unmodified**, can.

Quartz.NET was chosen on licensing as much as on capability. It is **Apache-2.0, with no commercial tier** — no paid feature gate, no dual licence, nothing a default composition could be silently billed for (GP 2), and nothing that complicates an Apache-2.0 companion's own dependency graph (GP 1). The obvious alternative in this space is LGPL as a *managed* NuGet dependency, which the repo's licence policy admits only for a dynamically-linked *native* library, and it gates batching and durable storage behind a paid tier. The capability question was never the deciding one.

The whole vendor surface is the single `Quartz` package; the `Quartz.Extensions.*` packages exist to host a scheduler out of `Microsoft.Extensions.Hosting`, which this companion does itself through the SDK's own compose and `IHostedService` seams.

## What lives where

| Concern | Owner |
|---|---|
| `Schedule` validation, idempotency, status transitions | `QuartzJobScheduler` (this package), against the SDK's vocabulary |
| Cron parsing for `ScheduleError.InvalidCron` | forge's own `CronExpression.tryParse`, so the error means what it means everywhere |
| Job definitions, run history, idempotency index, awaiting-external index | the deployment's `IJobStore` — `BlobJobStore` by default |
| Trigger evaluation, thread pool, misfire handling, retry backoff | Quartz |

### `QuartzJobStore` delegates rather than replacing

The design left one decision deliberately open: implement `IJobStore` over Quartz's own job-detail and trigger state, or delegate to the shipped store where Quartz has no equivalent. **It delegates, and projects.**

Quartz stores *schedules* — a job detail, its triggers, and their fire state. `IJobStore` stores rather more: per-scope isolation (GP 4), an idempotency-key index, per-attempt run history, and the Phase 319 awaiting-external secondary index. Quartz has an equivalent for **none** of them, and rebuilding them over `JobDataMap` (a string/object bag with no query surface) would mean shipping a second, weaker persistence format inside a package whose entire point is to be a *better* one.

So `QuartzJobStore` is a decorator: every read delegates verbatim, and `Save` / `Update` additionally **project** the definition onto Quartz — a durable job detail per job, plus a cron trigger for an active cron job. A cancelled job's Quartz job is deleted; a disabled one keeps its detail (so `TriggerOnce` still works) and loses its trigger.

The projection is **best-effort and rebuildable, deliberately**: a Quartz-side failure warns and never fails the canonical write, because a projection that could veto a write would make the scheduler's availability a function of Quartz's. `QuartzJobStore.Reproject scopeId` re-derives a scope's whole Quartz view from the canonical store — the recovery lever for a failed projection, for a restart of the in-memory Quartz store, and for a store written through before this decorator was composed.

## Composing it

The SDK core carries no Quartz reference, so `ComposeJobs` **adopts** a scheduler rather than constructing one. Build the companion before `ServerApp.run` and register both instances:

```fsharp skip=fragment
let! scheduler =
    QuartzJobScheduler.create jobStore notificationChannel serverConfig QuartzConfig.defaults logger

services.AddSingleton<IJobScheduler>(scheduler) |> ignore
services.AddSingleton<IJobStore>(scheduler.JobStore) |> ignore
```

with `ServerConfig.JobScheduler = QuartzJobScheduler QuartzConfig.defaults`. Compose finds the instances, registers the scheduler's `IHostedService` behind the usual process-profile gate, and says so loudly if either instance is missing — a deployment that asked for Quartz and silently got nothing is the failure worth naming.

Add the two probes the same way every companion does:

```fsharp skip=fragment
|> ServerApp.withHealthCheck (QuartzJobSchedulerHealth.create scheduler.QuartzScheduler)
|> ServerApp.withConfigValidator (QuartzValidator.create scheduler.QuartzScheduler quartzConfig serverConfig)
```

### A persistent or clustered Quartz store is configuration, not code

`QuartzConfig` selects Quartz's in-memory store, which is what a single-process deployment wants. A deployment needing durability or clustering does **not** need a second companion — it passes Quartz's own builder configuration through `QuartzJobScheduler.createWith`:

```fsharp skip=fragment
QuartzJobScheduler.createWith
    (fun builder -> builder.UsePersistentStore(fun store -> store.UseClustering()) |> ignore)
    jobStore notificationChannel serverConfig quartzConfig logger
```

## How the vocabularies map

Three translations are the substance of this package, and each was worth writing down because each has an off-by-one waiting in it.

- **Cron.** Forge's `Trigger.CronTrigger` is a five-field crontab expression, which Quartz reads directly under `CronFormat.Unix` — so there is no hand-rolled rewrite to drift from the forge parser. The misfire instruction is `DoNothing`: a deployment that was down over a fire boundary resumes at the *next* boundary rather than replaying a backlog.

- **Retry.** `JobRetryPolicy` maps onto `Quartz.RetryPolicy.Exponential`, so the backoff arithmetic runs in the backend rather than being re-implemented here — portability rule 3 ("retry as data") honoured by Quartz's own machinery. Both ends are indexed differently and both offsets matter: forge's `MaxAttempts` counts *dispatches* while Quartz's counts *retries after the first failure* (so the map is `MaxAttempts - 1`), and forge's first retry is attempt 2, whose wait is already `InitialBackoff * 2` (so the map is `2 * InitialBackoff`, clamped to `MaxBackoff`). Passing the initial backoff straight through looks obviously right and halves every wait; the binding's mapping test caught it by comparing against `JobRetryPolicy.delayFor` attempt for attempt rather than against the record's fields.

- **Precision.** `Schedule` rejects `JobPrecision.Second` with `PrecisionUnsupported(Second, [Minute])`. That is a statement about the **trigger vocabulary, not about Quartz**: Quartz's own cron dialect carries a seconds field and would fire sub-minute happily, but forge's five-field crontab has no way to *express* a sub-minute schedule, so no caller can ask for one through this interface. Portability rule 6 says an implementation that cannot honour a precision rejects it at registration rather than silently delaying dispatch.

## Declared limits

- **External-compute hand-off is not reconciled here.** A handler returning `JobResult.HandedOff` has its `ExternalHandle` persisted — nothing is lost — but this companion runs no reconciliation pass, so the run stays `AwaitingExternal` until something drives it. The adapter says so in a warning naming the remedy rather than leaving it to be inferred from a run that never finishes. Deployments using external compute want `InProcessJobScheduler`.
- **The in-memory Quartz store is not clustered.** N replicas over it each fire every cron job independently. The companion's own `IConfigValidator` asks Quartz (`SchedulerMetadata.JobStoreClustered` / `JobStorePersistent`) and refuses a multi-replica deployment over a non-persistent store, naming both remedies — a persistent clustered store, or `ServerConfig.AcceptInProcessSchedulerInMultiInstance` if the jobs are idempotent and the duplication is accepted.
- **No `IJobSchedulerTelemetry`.** The tick-drift telemetry the in-process default reports is about *its own* tick loop; Quartz has no equivalent tick to miss. `/dev/inspect`'s job-scheduler panel is therefore absent under this mode.

## Conformance

`src/ToolUp.Platform.Tests/InProcess/QuartzJobSchedulerTests.fs` binds `IJobSchedulerContract` and `IJobStoreContract` unmodified, and needs no external service — Quartz's in-memory store runs in-process, so the second binding is a standing gate rather than an occasional one. Beyond the packs it pins what a contract pack structurally cannot see: that a `Schedule` really produced a Quartz job and trigger, that `Cancel` really removed it, that a manual fire really reaches the registered handler and leaves a run row behind, and that the retry map preserves the count and the backoff.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

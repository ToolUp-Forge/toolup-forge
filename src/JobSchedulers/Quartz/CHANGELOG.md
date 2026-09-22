# Changelog — ToolUp.JobSchedulers.Quartz

All notable changes to the `ToolUp.JobSchedulers.Quartz` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

- Initial release: the **second** implementation of `IJobScheduler` and `IJobStore`, over Quartz.NET 4.
  - `QuartzJobScheduler` — the scheduling policy (`Schedule` validation chain, idempotency, status
    transitions, `TriggerOnce`, `NotifyEventWritten`) expressed against the SDK's own vocabulary, with
    trigger evaluation and dispatch delegated to a Quartz `IScheduler`. Also an `IHostedService`, so the
    composing host starts and shuts the scheduler down.
  - `QuartzJobStore` — an `IJobStore` decorator: reads delegate verbatim to the deployment's canonical
    store, and `Save` / `Update` project the definition onto Quartz as a durable job detail plus, for an
    active cron job, a cron trigger. `Reproject` rebuilds a scope's whole Quartz view from the canonical
    store.
  - `QuartzJobSchedulerHealth` — an `IHealthCheck` reading the scheduler's own status, so a scheduler
    that quietly went to standby reports `Degraded` instead of looking healthy while firing nothing.
  - `QuartzJobSchedulerValidator` — an `IConfigValidator` that refuses a thread pool too small to run a
    job, and refuses a multi-replica deployment over a non-persistent (therefore non-clustered) Quartz
    job store, asking Quartz for the real answer rather than inferring it from the configured mode.
  - `QuartzMapping` — the documented translation between the two vocabularies: five-field crontab read
    directly under `CronFormat.Unix`, `JobRetryPolicy` onto `Quartz.RetryPolicy.Exponential`, and the
    per-scope job and trigger keys.
  - Both shipped job contract packs (`IJobSchedulerContract`, `IJobStoreContract`) are bound unmodified
    against this companion and run without any external service.
  - Declared limits, each with a runtime signal rather than silence: external-compute hand-off is
    persisted but not reconciled here; the default in-memory Quartz store is not clustered; there is no
    `IJobSchedulerTelemetry` (Quartz has no tick loop of its own to report drift for).

Licensed under Apache-2.0. Quartz.NET is Apache-2.0 with no commercial tier.

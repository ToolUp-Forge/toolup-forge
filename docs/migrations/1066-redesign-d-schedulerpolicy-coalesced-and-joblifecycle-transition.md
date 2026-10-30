# `Schedule` says whether it coalesced, and `TriggerOnce` says why it refused

**Ships in:** ToolUp.Platform.Server, ToolUp.JobSchedulers.Quartz and ToolUp.Facts.Server 0.38.0, the next minor
after the tagged 0.37.0. The change is breaking-class: two `IJobScheduler` members change return type.

**Affected:** code that calls `IJobScheduler.Schedule` and reads the job id, code that calls
`IJobScheduler.TriggerOnce` and reads its error, every `IJobScheduler` implementation or decorator, and code that
calls `FactPublicationJobs.schedulePublication` / `scheduleRefresh` or
`RecomputeJobHandler.scheduleRecompute` / `scheduleRecomputeUnder`. A deployment that composes the shipped
schedulers and calls neither member itself changes nothing: every job runs, retries, dead-letters and is
reconciled exactly as before, with the same events, run rows and `NextRunAt` policy.

## What changes

1. **`Schedule` answers `Result<ScheduleOutcome, ScheduleError>`.** `ScheduleOutcome` is `Scheduled of JobId` (a
   new job was persisted) or `Coalesced of JobId` (a live job with the registration's idempotency key already
   exists in the scope, and nothing was created). Before, both were `Ok jobId` and the caller could not tell them
   apart. `ScheduleOutcome.jobId` reads the id either way.
2. **`TriggerOnce` answers `Result<unit, TriggerError>`.** The cases are `JobNotFound(scopeId, jobId)`,
   `JobCancelled`, `JobDisabled`, `SchedulerStopping` (the scheduler is shutting down; before, the in-process
   scheduler answered `Ok ()` and started nothing), `QuotaExceeded` (the quota decorator's refusal) and
   `DispatchRefused` (the backend refused to start the run). `TriggerError.message` renders the sentence the old
   `string` carried, so a wire that still carries a string — the job admin API does — is unchanged.
3. **One scheduling policy, shared.** `SchedulerPolicy` (`validate`, `persistNew`, `scheduleValidated`,
   `setStatus`, `triggerable`, `eventTriggered`) is the policy half of both shipped schedulers. The Quartz
   companion no longer carries its own copy.
4. **One way a run ends.** `JobLifecycle.transition` turns a run's outcome into its row, events, notification and
   folded definition, and `JobLifecycle.apply` performs them in that order. The in-process scheduler, external
   hand-off resolution (by poll or by callback) and the Quartz fire path all end runs through it. The lifecycle
   event payload records (`JobCompletedPayload`, `JobFailedPayload`, `JobDeadLetteredPayload` and the three
   `JobExternal*Payload` records) move from `JobScheduler` to `JobLifecycle`; their JSON is unchanged.
5. **The grounded-narrative data-arrival trigger fires only a job it created.** A data change replayed inside
   the idempotency window coalesces onto the job the first delivery created, and the narrative runs once.

## What to do

- Run the codemod: `dotnet run --project Build.fsproj -- Codemod <your source dir>`. It rewrites a declared
  `Result<JobId, ScheduleError>` to `Result<ScheduleOutcome, ScheduleError>` (`1066-schedule-result-type`), and
  an implementer's re-typed refusal sentences to their cases (`1066-trigger-error-not-found`,
  `1066-trigger-error-cancelled`, `1066-trigger-error-disabled`). It reports every `Schedule` and `TriggerOnce`
  call and every implementer member for review (`1066-schedule-call`, `1066-trigger-once-call`,
  `1066-scheduler-implementer`).
- At a `Schedule` call site, `Ok _` still compiles. A site that reads the id matches both cases, and a site that
  fires what it schedules fires only a job it created:

  ```fsharp skip=fragment
  match! scheduler.Schedule registration with
  | Ok(Scheduled jobId) -> do! fire jobId          // created now: fire it
  | Ok(Coalesced _) -> ()                          // already pending: it was fired when it was created
  | Error e -> log e
  ```

  Where the old behaviour is wanted unchanged, `| Ok(Scheduled jobId | Coalesced jobId) ->` reads the id either way.
- At a `TriggerOnce` call site, match the `TriggerError` cases, or render the old sentence:

  ```fsharp skip=fragment
  match! scheduler.TriggerOnce(scopeId, jobId, userId) with
  | Ok() -> ()
  | Error TriggerError.SchedulerStopping -> ()     // shutting down: nothing was started
  | Error refusal -> log (TriggerError.message refusal)
  ```

- An `IJobScheduler` implementation returns `Ok(Scheduled id)` / `Ok(Coalesced id)` and a `TriggerError`. A
  decorator forwards both unchanged. A scheduler can build on `SchedulerPolicy` rather than re-typing the
  validation chain, the idempotency rule, the status transition and the trigger preconditions; it can end a run
  with `JobLifecycle.transition` and `JobLifecycle.apply`, giving its own `LifecycleEffects`.

## Verify

- `dotnet build` is clean: every `Schedule` / `TriggerOnce` site the compiler names is migrated. A site that only
  formats the result (`string`, `%A`, `%O`) still compiles, which is why the codemod reports every call site.
- A test that schedules the same idempotency key twice sees `Scheduled id` then `Coalesced id`.
- The `IJobSchedulerContract` pack, run against your scheduler, includes the coalesced, disabled, stopping and
  unknown-job cases.

## Rollback

Pin 0.37.0. Nothing persisted changed shape: job definitions, run rows and event payloads are byte-for-byte the
same, so a store written under 0.38.0 is read by 0.37.0 unchanged.

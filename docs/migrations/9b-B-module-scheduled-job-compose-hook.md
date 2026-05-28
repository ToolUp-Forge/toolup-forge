# Phase 9b.B — Module-level scheduled-job compose hook

**Forge commits:** `5f73aa4` (substrate + base hook + `compose` wiring) + `8593425` (superset mirrors on AI / Forms / RAG / Scheduling).

## What changes

`ServerModule` and `ServerApp` (and every superset — `AIServerApp`,
`FormsServerApp`, `RAGServerApp`, `SchedulingServerApp`) gain two new
builder methods for declaring recurring / event-driven background jobs
at compose time:

- **`withJobHandler (handlerName, handler, trigger)`** — tupled
  shorthand. Defaults to `JobPrecision.Minute`, empty payload, default
  retry policy, `_platform` scope, auto-built per-scope idempotency key
  (`module-{handlerName}-{scopeId}`, one-year TTL).
- **`withScheduledJob declaration`** — accepts a full
  `ScheduledJobDeclaration` for callers needing custom scopes,
  payload, retry policy, shard-key, precision, idempotency, or tags.

`ServerModule.JobHandlers` accumulates per-module declarations;
`ServerApp.ScheduledJobs` accumulates module-level declarations (fanned
in by `addModule`) plus composition-root-level declarations.
`ComposeJobs.registerScheduledJobDeclarations` (new) runs after the
`IJobScheduler` singleton is built and applies `RegisterHandler` + per-
scope `Schedule` per declaration.

When `ServerConfig.JobScheduler = NoJobScheduler`, non-empty
declarations log a single startup `Warn` naming every skipped handler
and skip registration — a module declaring jobs in an unscheduled
deployment is a config mismatch, not a crash.

## Diff to apply

This refactor is **additive**. Existing consumers continue unchanged —
the new builder methods are opt-in, the new fields on `ServerModule`
and `ServerApp` default to the empty list, and `compose`'s new
positional parameter is threaded through `ServerApp.run` /
`AIServerApp.run` / `FormsServerApp.run` / `RAGServerApp.run` /
`SchedulingServerApp.run` automatically.

A consumer that wants to adopt the new pattern (for example, replacing
a post-`Build` resolution of `IJobScheduler` with a compose-time
declaration):

### Before — post-`Build` resolution

```fsharp
// somewhere in the composition root, AFTER ServerApp.run / Build()
let scheduler = sp.GetService<IJobScheduler>() :?> IJobScheduler
scheduler.RegisterHandler("salesanalysis.daily-rollup", SalesAnalysisRollupHandler())

let registration: JobRegistration = {
    ScopeId = "team-acme"
    Handler = "salesanalysis.daily-rollup"
    Payload = ""
    Trigger = CronTrigger "0 6 * * *"
    Idempotency = Some { Key = "salesanalysis.daily-rollup.team-acme"; TtlSeconds = 86400 }
    RetryPolicy = JobRetryPolicy.defaults
    ShardKey = None
    Precision = Minute
    CreatedBy = "_system"
    Tags = Map.empty
}

scheduler.Schedule registration |> Async.RunSynchronously |> ignore
```

### After — module-level compose-time declaration

```fsharp
// In the module's Server.fs
let serverModule =
    ServerModule.create "SalesAnalysis"
    |> ServerModule.withGuardedApi salesAnalysisApi
    |> ServerModule.withJobHandler (
        "salesanalysis.daily-rollup",
        SalesAnalysisRollupHandler() :> IJobHandler,
        CronTrigger "0 6 * * *")
```

The composition root needs no further change — `addModule` accumulates
the declaration onto `ServerApp.ScheduledJobs`, and `ServerApp.run`
applies it after the scheduler is built.

For per-tenant fan-out or a custom retry / payload / shard-key, use the
full-control variant:

```fsharp
let declaration =
    ScheduledJobDeclaration.create
        "salesanalysis.tenant-scan"
        (SalesAnalysisRollupHandler() :> IJobHandler)
        (CronTrigger "*/15 * * * *")
    |> ScheduledJobDeclaration.withScopes [ "team-acme"; "team-beta" ]
    |> ScheduledJobDeclaration.withRetryPolicy {
        JobRetryPolicy.defaults with MaxAttempts = 5
    }

let serverModule =
    ServerModule.create "SalesAnalysis"
    |> ServerModule.withScheduledJob declaration
```

## Verification steps

1. `dotnet build ToolUp.Forge.sln` — additive change, build stays
   green.
2. `dotnet run --project src/ToolUp.Platform.Tests/...` — the new
   `ComposeJobs.registerScheduledJobDeclarations (Phase 9b.B)`
   suite (`InProcess/ScheduledJobDeclarationTests.fs`) pins:
   - Empty declarations under `NoJobScheduler` is a silent no-op.
   - Non-empty declarations under `NoJobScheduler` emit exactly one
     `Warn` naming every skipped handler.
   - Single declaration registers + schedules under default
     `_platform` scope.
   - Explicit `Scopes` fans out per scope with distinct idempotency
     keys.
   - Restart re-registration is idempotent (no duplicate
     `JobDefinition` persisted).
   - Explicit `Idempotency` override is preserved.
   - `Tags["source"] = "compose-time"` is auto-stamped.
   - Invalid cron logs `Warn` but does not abort the registration
     loop.
3. Existing `JobSchedulerTests` + `IJobSchedulerContract` continue to
   pass — Phase 9b.B does not alter the scheduler contract; it only
   adds a compose-time data-driven path on top.

## Rollback

The new builder methods, fields, and `ComposeJobs.registerScheduledJobDeclarations`
helper are additive. Revert by:

1. `git revert 8593425 5f73aa4`.
2. Verify consumers that adopted the new pattern revert to the manual
   `RegisterHandler` + `Schedule` shape.

Existing consumers that never adopted the pattern are unaffected.

# Phase 1283: the job API schedules only handlers a caller may name

**Who this is for:** a deployment that calls `JobApi.Schedule` from a client. **Breaking:** the wire no longer
schedules a handler the deployment has not declared.

## What changes

1. **The wire schedules only a declared handler.** `JobApi.Schedule` answers
   `Error (ScheduleError.HandlerNotRegistered name)` for a handler the deployment has not declared
   wire-schedulable. That is the answer an unknown name already got, so the wire cannot tell a platform
   handler from a name nobody registered. Nothing is declared by default. Before this change a team Owner or
   Admin, or any signed-in user outside team mode, could schedule `_platform.tenant.lifecycle` with another
   tenant's scope in the payload and run that tenant's offboard hooks, the encryption-key destroy among them.
   **`JobApi.TriggerOnce` is not gated by the declaration** (operator ruling 2026-11-14). It still fires any
   existing job in the caller's scope, so the background-jobs admin's re-run works as before. It cannot choose
   a handler or a payload, and the execute-time checks below refuse a job whose payload names another scope.
2. **No job API method acts under the platform container.** A caller whose resolved scope is `_platform` is
   served as one with no scope: it lists nothing and schedules nothing.
3. **A handler whose payload names a scope refuses one that is not its job's own** (`JobPayloadScope` in
   `ToolUp.Platform.Server.Jobs`). This covers the tenant lifecycle and grace-period offboard handlers, the
   provider OAuth refresh and status probe, the OAuth token refresh (whose platform-container job may still
   name a tenant), model fit and its batch items, the asset derivative job, the RAG ingestion retry (whose
   vector scope must also be the one its container projects to), and the report render job. The refusal is a
   `PermanentFailure` naming both scopes. Every platform path already schedules these handlers under the scope
   their payload names, so a job the platform enqueued runs exactly as before.

## Which wire-scheduled jobs keep scheduling

Nothing in this repository calls `JobApi.Schedule`, so no first-party job loses a wire route. The one
first-party client of the job API, the background-jobs admin, uses `ListJobs`, `GetJob`, `GetRecentRuns` and
`TriggerOnce`, and all four are unchanged. A deployment whose own client schedules its own handler declares
that handler once.

## Do this

```fsharp skip=fragment
// In the composition root, beside the scheduler:
app
|> ServerApp.addServiceConfig (JobApiHandler.declareWireSchedulable "sales.daily-rollup")
```

Declare only a handler whose payload can name nothing outside the job's own scope, because the caller writes
the payload. A handler that reads only `ctx.ScopeId` / `ctx.Scope` qualifies. A handler that reads a scope,
container or user from its payload must compare it first, with `JobPayloadScope.requireOwn` or
`JobPayloadScope.requireOwnStorage`.

A handler of your own that reads a target scope from its payload should make the same comparison at the top
of `Execute`:

```fsharp skip=fragment
match JobPayloadScope.requireOwn ctx payload.ScopeId with
| Error reason -> return PermanentFailure reason
| Ok() -> (* the work *)
```

## Verify

- `JobApi.Schedule` with an undeclared handler answers `HandlerNotRegistered`. With a declared one it answers
  `Ok jobId`, and the job is stored under the caller's scope.
- The `The job API schedules only handlers a caller may name` list in `ToolUp.Platform.Tests` passes.

## Rollback

Declare the handlers your clients schedule. No setting restores the undeclared route, and none
should: the undeclared route is the defect this phase closes.

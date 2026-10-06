# Report subscriptions are scoped by default, and a save re-stamps an existing job

**Ships in:** ToolUp.Reporting.Server (`ReportingCompose.withReportSubscriptions`,
`ReportSubscriptionApiHandler`), ToolUp.Platform.Server (`IJobScopeReissue`, `JobScopeReissue`,
`ScopeReissueError`, `CarriedJobScope.owns` / `reissue`, the in-process scheduler and the quota decorator),
ToolUp.JobSchedulers.Quartz. Breaking, on the 0.24.1 draft.

**Affected:** a deployment that composes report subscriptions through `withReportSubscriptions`. A deployment
that does not compose subscriptions is unaffected.

## What changes

**1. The composed API factory takes the request's resolved scope.** `withReportSubscriptions` used to return
`IJobHandler * (string -> string -> IReportSubscriptionApi)` built over `ReportSubscriptionApiHandler.create`,
which schedules every subscription anonymously, so a scheduled grounded report refused on every run. It now
returns `IJobHandler * (string -> ResolvedScope -> string -> IReportSubscriptionApi)` built over
`createUnder`: the subscription's job runs under the scope the platform resolved for the creating request,
when that scope names the shard the subscription is stored in (the Phase 990 rule).

**2. Updating a subscription re-issues its job's carried scope.** An `UpdateSubscription` through `createUnder`
re-stamps the existing job's scope token under the saving request's scope (`JobScopeReissue.reissue`). The job
keeps its id, schedule and run history. A subscription scheduled before Phase 990, or through `create`, moves
onto its team's scope by being saved. You do not need to delete and re-create it.

**3. A scheduler re-issue verb.** `IJobScopeReissue.ReissueScope(scope, scopeId, jobId)` is a capability
interface beside `IScopeCarrierBinding`, not a new `IJobScheduler` member. Implementations of `IJobScheduler`
outside this repository still compile. The in-process scheduler and the Quartz companion implement it, and
`QuotaGatedJobScheduler` forwards it. It refuses (`ScopeReissueError.ScopeDoesNotOwnJob`) the anonymous scope
and any scope resolved for a shard other than `scopeId`, and it checks this before it reads the job. Call it
through `JobScopeReissue.reissue`, which returns `Unsupported` for a scheduler that does not implement it.

## What to do

- **Pass the resolved scope to the factory:**

  ```diff
  -let api = subscriptionsFor principal scopeId
  +let api = subscriptionsFor principal (ScopeResolution.forRequest ctx) scopeId
  ```

- **To keep anonymous subscriptions** (producers that read nothing scoped), build the API with
  `ReportSubscriptionApiHandler.create apiDeps principal scopeId`. That is now the explicit opt-out.
  Passing `ResolvedScope.anonymous` to the composed factory has the same effect.
- **If you wrote your own scheduler or scheduler decorator,** implement `IJobScopeReissue` too (decorators
  forward it through `JobScopeReissue.reissue inner …`). Otherwise an update through `createUnder` of a job
  that carries no scope token refuses with `ReportSubscriptionApiHandler.SubscriptionScopeNotReissued`, and
  you have to delete and re-create that subscription.
- **To migrate existing subscriptions,** save each one again (an update with its current fields) from a
  request resolved to its team.

## Verify

`ReportSubscriptionScopeTests` (Phase 991 lists) cover the following: a subscription composed through
`withReportSubscriptions` alone runs under its creator's scope; a pre-990 job is re-stamped in place by a
save and then reads its team's Fact; a cross-shard or anonymous re-issue is refused on both schedulers.

## Rollback

Build the API with `ReportSubscriptionApiHandler.create` to schedule anonymously again. A token that a
re-issue wrote is an ordinary Phase 935 token. An older build redeems it the same way.

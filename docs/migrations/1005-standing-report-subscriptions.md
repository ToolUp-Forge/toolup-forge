# A deployment declares a standing report subscription that runs under a named principal

**Ships in:** ToolUp.Reporting.Core (`StandingReportSubscription`, `StandingSubscriptionPrincipal`),
ToolUp.Reporting.Server (`ReportingCompose.withStandingSubscriptions` / `seedStandingSubscriptions`,
`IStandingSubscriptionLedger`, `ReportSubscriptionApiHandler.seedStanding`, `SubscriptionRunAudit.RunAs`),
ToolUp.Platform.Server (`DeclaredPrincipalResolver`, registered by the scope-resolution composition). Rides the
0.26.0 draft.

**Affected:** a deployment that creates report subscriptions by hand after every fresh deployment, and code that
builds a `SubscriptionRunAudit` record literal (it gains `RunAs`). Nothing else changes for a deployment that
declares no standing subscription.

## What changes

1. **A subscription can be declared.** `StandingReportSubscription` names a producer, a template, parameter
   bindings, a cadence, recipients, a format and a **principal** — never a scope. At startup the deployment's own
   scope resolver resolves the principal as it would a request signed in as it, and the job is scheduled under
   that resolution. A principal that is undeclared or does not resolve, a scope that does not persist, a missing
   template or an invalid subscription refuses the start (`StandingSubscriptionsRefused`), naming the
   subscription and what it lacks.
2. **Seeding is idempotent.** The row is `standing.<key>` at the principal's scope. A re-deploy updates it; a
   removed declaration's row is retired (job cancelled, row deleted). Request-created rows are untouched.
3. **A changed cadence now takes effect.** Saving a subscription with a new schedule cancels the job on the old
   cron and schedules one on the new cron. Before this change the row stored the new schedule and the job kept
   firing on the old one.
4. **`SubscriptionRunAudit.RunAs`** carries the principal the run acted as (the row's `CreatedBy`).

## What to do

- **Declare the principal and the subscription, and register the startup service** after the subscription job
  handler:

  ```diff
   let handler, subscriptionsFor = ReportingCompose.withReportSubscriptions missing jobDeps apiDeps
  +let standingDeps: ReportingCompose.StandingSubscriptionDeps =
  +    { Api = apiDeps; Templates = templates; Ledger = ReportSubscriptionStore.standingLedger blobs }
  +services.AddSingleton<IHostedService>(
  +    Func<IServiceProvider, IHostedService>(ReportingCompose.withStandingSubscriptions standing standingDeps))
  ```

- **Make the principal resolvable.** In a team-mode deployment, add the principal's user id to the team as a
  member and set that team active for it. The principal then resolves to that team.
- **Seed the template** at that team before the standing subscriptions start.
- **To move a hand-created subscription to a declared one,** declare it with the same producer, parameters,
  cadence, recipients and format, deploy, check that the `standing.<key>` row ran, and then delete the
  hand-created one through the management API. The two are separate rows with separate jobs, so deleting one
  does not affect the other.
- **A `SubscriptionRunAudit` literal** adds `RunAs = subscription.CreatedBy`.

## Verify

The Phase 1005 list in `ReportSubscriptionScopeTests` covers these cases: the declared subscription fires on its
cadence under its principal's team and is audited as the principal; the grounded producer reads that team's Fact
only; a re-deploy, a change and a removal leave exactly one row and one live job, or none; each refusal fails the
start with its reason.

## Rollback

Remove the startup service registration. The seeded rows and their jobs then stay as they are, and they are
ordinary subscriptions that you can manage through the API. To remove them first, deploy once with an empty
`Subscriptions` list, which retires every seeded row.

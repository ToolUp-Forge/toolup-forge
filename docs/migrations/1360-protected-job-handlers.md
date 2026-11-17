# Phase 1360: the job API refuses to stop a protected handler's job

**Who this is for:** a deployment that registers its own job handler and schedules that handler's jobs under a
team or user scope from its own server code, and a client that calls `JobApi.Cancel`, `Disable` or `Enable`.
**Behaviour change, additive surface:** the three verbs refuse a protected handler's job for a caller who is not
a platform administrator.

## What changes

1. **A handler can be declared protected** (`JobApiHandler.declareProtected` in `ToolUp.Platform.Server.Jobs`),
   once, where it is registered. `JobApi.Cancel`, `Disable` and `Enable` read the job in the caller's own scope
   and, when its handler is protected and the caller is not a platform administrator, answer
   `Error JobApiHandler.ProtectedHandlerRefusal` without touching the job. Every other job is managed exactly as
   before. A job id from another scope is not found in the caller's scope and is answered as it was before this
   change, so the refusal reveals nothing about another tenant's jobs.
2. **The platform declares its own tenant-scoped handlers protected.** Before this change a team Owner or Admin
   could cancel or disable the offboarding grace-period poll for their own team, and so stop a scheduled
   offboard. The declared handlers: the tenant lifecycle run and the grace-period offboard poll, the
   data-ingestion run, the provider OAuth refresh and status probe, the data-subject export, the data-object
   orphan sweep, the AI conversation and knowledge-base retention sweeps, fact recomputation, the grounded
   narrative run and the fact coherence check, the narrative regeneration, the asset derivative job, the RAG
   ingestion retry and the report render job. Each is declared only when the composition that registers it is
   composed.
3. **A handler cannot be both wire-schedulable and protected.** A wire caller could otherwise register a job it
   may not stop. `declareWireSchedulable` and `declareProtected` on the same handler name throw at composition,
   whichever is called first.

`TriggerOnce` is unchanged: it fires an existing job in the caller's scope and stops nothing.

## Do this

If your server code schedules a handler of its own under a tenant's scope, and a tenant must not be able to stop
that work, declare the handler protected where you register it:

```fsharp skip=fragment
app
|> ServerApp.addServiceConfig (JobApiHandler.declareProtected "billing.usage-snapshot")
```

The companions declare their own tenant-scoped handlers, so a deployment that composes them declares nothing:
the declared-delivery watch job from `ToolUp.DataSources.Common` 0.40.1 (`DeclaredDelivery.withDeclaredDataSources`),
and the calendar renewal jobs from `ToolUp.Calendar.Google` and `ToolUp.Calendar.Microsoft` 0.39.4
(`withChannelRenewal`, `withSubscriptionRenewal`, and `MicrosoftGraphSubscriptions.compose`). Remove a
declaration of `DeclaredDelivery.WatchJobHandlerName` or a bridge's `RenewalHandlerName` you added for an earlier
companion version; it is harmless but redundant. A deployment that declared one of those handlers
wire-schedulable is refused at composition from those versions, as for any protected handler.

A Stripe deployment registers the billing reconcile handler (`Reconciliation.HandlerName`, `ToolUp.Stripe.Server`)
itself, because that companion does not reference the job layer; if it schedules the reconcile under a tenant
scope, it declares that handler protected where it registers it.

A client that shows the result of `Cancel`, `Disable` or `Enable` shows the `Error` text as it does any other
refusal. The background-jobs admin page offers none of the three verbs, so it needs no change.

## Verify

- A team Admin's `Cancel`, `Disable` or `Enable` on a protected handler's job in their own scope answers
  `Error JobApiHandler.ProtectedHandlerRefusal`, and a platform administrator's answers `Ok`.
- The `a protected handler's job is stopped by a platform administrator only` list in `ToolUp.Platform.Tests`
  passes.

## Rollback

None is offered. A deployment that needs a team to manage one of its own handlers' jobs does not declare that
handler protected; the platform's own declarations stay.

# Phase 1253: the delivery trigger and the publication principal are combinators

**What changes.** The fact tier now offers two pieces of runtime a consuming application may carry itself:
`FactsCompose.withDeliveryTrigger` and `FactPublication.withPublicationPrincipals` with
`FactPublication.declaredPrincipal`. Additive: nothing changes until you compose them.

**One behaviour differs from a typical hand-written copy.** A run now fires when the delivery's newest period was
brought OR changed. A copy that fires on a brand-new period only leaves a published narrative citing superseded
Facts after a corrected re-delivery. A restatement of an earlier period still fires nothing.

## The diff

In the application's composition, delete the emitted `IFactTableWriter` decorator (the type that holds commits on
`HttpContext.Items` and enqueues on `Response.OnCompleted`), its helper module for delivery keys and periods, the
registration surgery that finds the writer's descriptor and re-registers it, and the lookup that scans every
environment variable for the publication's principal.

Add two compose lines, after the fact tier:

```fsharp skip=fragment
app
|> FactsCompose.withFactTableWriter
|> FactsCompose.withDeliveryTrigger runsOnCommit
|> FactPublication.withPublicationPrincipals [ "team-publishing", "PUBLICATION_PRINCIPAL_BOARD" ]
```

`runsOnCommit : string -> string list` is the table-to-runs function the application already has. In the read:

```diff
-let! publishing = scanEnvironmentForPrincipal services audience
+let! publishing = FactPublication.declaredPrincipal audience.PublishingScope services
```

The setting is now named, one per publishing scope, so the deployment's environment needs no prefix convention.
`declaredPrincipal` takes the scope id rather than the audience, because the audience type lives in
`ToolUp.PublicRendering`, which the fact tier does not reference.

## Verify

- Deliver a new period for two watched tables in one request: one job on the scheduler.
- Deliver the same content again: no new job. Correct one row of the newest period: one new job.
- Restate an earlier period: no new job.
- Start with an all-Plain disclosure configuration: the `FACTS-DISCLOSURE-CANNOT-TAINT` finding names the narrative
  publication read. A principal that resolves to another scope logs a Warning naming the publication.

## Rollback

Remove the two compose lines and restore the application's copies. No data moves: job idempotency keys and fact
records are unaffected.

See [Grounded narratives](../platform/grounded-narratives.md#the-triggers) for which of the delivery trigger and
`ServerModule.withDeliveryCompleted` to use for what.

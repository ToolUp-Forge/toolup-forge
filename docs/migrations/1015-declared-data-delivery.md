# A data source declares how its data arrives — a scheduled pull or a watched location, run under a declared principal

**Ships in:** ToolUp.Platform.Server (`DataSourceDelivery`, `DeclaredDataSource`, `DeclaredDataSources`,
`IDeclaredDeliveryLedger`, `DataSourceConfigStore.declaredDeliveryLedger`, and the shared `DeclaredPrincipal`
record with its `DeclaredPrincipal.user` / `userOf` / `missingGrant` readings), ToolUp.DataSources.Common
(`DeclaredDelivery.seed` / `watchHandler` / `depsOf` / `startupService` / `withDeclaredDataSources`,
`DeclaredDeliveryDeps`, `DeclaredDeliveryServices`, `DeclaredSeedReport`, `DeclaredDeliveryRefusal`,
`DeclaredDataSourcesRefused`, `WatchedObject`, `Files.listObjects`, `IWatchableFileSource`), and the Csv, Excel and
Parquet connectors, which now implement `IWatchableFileSource`. Rides the 0.28.0 draft. Additive only.

**Affected:** a deployment that creates a data source and then pulls it by hand, or uploads into it, after every
fresh deployment. Nothing changes for a deployment that declares no source: `DataSourceConfig` is unchanged, and
a source a request saves still arrives by upload.

## What changes

1. **A source can be declared with its delivery.** `DeclaredDataSource` pairs a `DataSourceConfig` with a
   `DataSourceDelivery`: `Upload` (today's behaviour, the default), `ScheduledPull(cadence, principal)` or
   `Watched(location, principal)`. The principal is a `DeclaredPrincipal` — a name, a user id and a label —
   never a scope.
2. **A scheduled pull runs as the principal.** At startup the deployment's scope resolver resolves the
   principal, the config is seeded at that scope, and one ingestion job per table in `Tables` is scheduled on the
   cadence under that resolution. Each run commits through the ordinary ingestion path, so
   `IngestionRunCompleted` is written in the principal's scope.
3. **A watched location is ingested once per object.** `location` is a blob prefix in the principal's own scope
   container; a poll lists it through the connector and ingests each object whose identity (blob name,
   last-modified instant, size) has not been ingested. Polling again ingests nothing; a rewritten object is
   ingested again.
4. **Refused at startup.** A principal that is undeclared or does not resolve, a scope that does not persist, an
   unknown connector, a bad cadence, a pull with no tables, a watch over a non-file connector or over a config that
   locates itself, and an unreadable location each fail the start (`DeclaredDataSourcesRefused`), naming the
   source and what it lacks. Nothing is written when anything is refused.
5. **The reporting tier's standing subscriptions read the same principal helpers.** `ReportingCompose` now
   resolves its principals through `DeclaredPrincipal.userOf` and names a missing grant through
   `DeclaredPrincipal.missingGrant`. Its behaviour and its public surface are unchanged.

## What to do

- **Declare the principals and the sources, and register the startup service:**

  ```diff
  +let declared: DeclaredDataSources = {
  +    Principals = [ { Name = "loader"; UserId = "svc-loader"; DisplayName = "Nightly loader" } ]
  +    Sources = [
  +        { Source = salesWarehouse; Delivery = DataSourceDelivery.ScheduledPull("0 6 * * *", "loader") }
  +        { Source = dropFolder; Delivery = DataSourceDelivery.Watched("incoming", "loader") }
  +    ]
  +}
  +services
  +|> DeclaredDelivery.withDeclaredDataSources declared DeclaredDelivery.DefaultWatchPoll
  +|> ignore
  ```

  The deployment must compose data ingestion (`ServerConfig.DataIngestion = EnabledDataIngestion`) and the job
  scheduler; a declared delivery without either refuses the start.
- **Make the principal resolvable.** In a team-mode deployment, add the principal's user id to the team as a
  member and set that team active for it. Its deliveries then run in that team.
- **For a scheduled pull,** list the tables it pulls in the config's `Tables`.
- **For a watched location,** leave `container` and `prefix` out of the config's `ConnectionScope`: the watch
  reads the principal's scope container at `location`, and sets both on the seeded config.

## Verify

The Phase 1015 list in `ToolUp.DataSources.Tests` (`DeclaredDeliveryTests`) runs each case in process over the
shipped team resolver, scheduler and ingestor: a scheduled pull fires on its cadence and commits under the
principal's scope; a watched drop is ingested once, and a new or rewritten object again; an unresolvable principal
and an unreadable location refuse the start; every shape refusal is named; seeding is idempotent and retires a
removed source; and the delivery tier's source holds no scope-mint spelling.

## Rollback

Deploy once with an empty `Sources` list, which cancels every declared job and deletes every declared config;
then remove the startup service registration. Removing the registration first leaves the seeded configs and their
jobs in place, and a watch job then has no handler to run on.

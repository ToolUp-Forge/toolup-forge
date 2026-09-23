# Phase 9x — The self-hosted observability admin module

**Ships in:** ToolUp.Platform.Core (the readback record shapes), ToolUp.Platform.Server (the
`/api/observability/*` endpoints and the alert engine's status board) and ToolUp.Platform.Client
(the module, its opt-in mode, and the health monitor's new sidebar group).

## What changes

A read surface over three substrates that already shipped: the Phase 828 log store, the Phase 829
metrics history and the Phase 178 alert engine.

- **Endpoints** (Platform-Admin gated — `AccessContext.canModifyPlatformConfig`, like the health
  monitor and the Datadog readback):
  `GET /api/observability/sources`, `/logs`, `/metrics?metric=…`, `/alerts`. Each data route is
  mounted only when its source is on (`ServerConfig.LogStore`, `ServerConfig.MetricsHistory`, a
  non-empty `ServerConfig.AlertRules`); with all three off nothing is mounted (GP 13). A source that
  is on without its store composed answers 503.
- **Alert state.** `AlertRuleEngine.AlertRuleStatusBoard` (registered only when a rule is declared)
  now receives an `AlertRuleObservation` per rule after every engine tick. The evaluation itself is
  unchanged — `runTickObserved` wraps `runTick`.
- **The module.** `ClientConfig.Observability: ObservabilityModuleMode`, default
  `NoObservabilityModule`. `DefaultObservabilityModule` injects a Logs / Metrics / Alerts module that
  reads `/sources` first and shows only the tabs the server enabled.
- **The health monitor moves to the "Observability" sidebar group** (was "Platform Management"), beside
  the Datadog readback and the new module. The group joined the platform-admin allow-list in the same
  change, so the monitor keeps its administration area, its `NavRole` gate and its visibility to a
  team-less platform admin. The string lives once, as `SidebarVisibility.ObservabilitySidebarGroup`;
  `DatadogReadbackUI.ObservabilityGroup` now aliases it.

## Diff to apply

Nothing, to keep today's behaviour — except that the health monitor's rail entry now sits under an
"Observability" heading. A consumer asserting on its group name (a sidebar snapshot, a test fixture
restating `"Platform Management"`) updates that one value.

To adopt the module, turn on at least one source server-side and ask for it client-side:

```fsharp
// Server
{ ServerConfig.defaults with
    LogStore = SqliteLogStore(LogStoreConfig.create "/var/lib/app/logs.db")
    TimeSeriesStore = InMemoryTimeSeries
    MetricsEndpoint = EnabledMetricsEndpoint
    MetricsHistory = EnabledMetricsHistory MetricsHistoryConfig.defaults }

// Client
{ ClientConfig.defaults with Observability = DefaultObservabilityModule }
```

A full-literal `ClientConfig` / `MessageCatalog` construction gains one field each
(`Observability`); a translated catalog adds the `Observability` section — regenerate from
`docs/platform/message-catalog-skeleton.fs`.

## Verification

- `ObservabilityHandlersTests` (Platform pack): strip mode through the composed router, the 403 gate,
  each read against the in-memory defaults, the board's transition rule, and that an observed tick
  delivers exactly what `runTick` delivers. The same file carries the Phase 9w Datadog readback
  endpoint tests.
- `SidebarVisibilityContractTests`: the no-active-team matrix still shows `_sdk.HealthMonitor` to a
  team-less platform admin.

## Rollback

Leave `ClientConfig.Observability` at `NoObservabilityModule`. The endpoints follow the three source
switches, so turning those off unmounts them.

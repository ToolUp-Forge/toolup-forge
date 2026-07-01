# Phase 178 — Alert-rule / threshold engine

**Ships in:** ToolUp.Platform.Core (the `AlertRule` data types + the
`ServerConfig.AlertRules` field) and ToolUp.Platform.Server (the
`AlertRuleEngine` evaluator + the `ServerApp.withAlertRule` / `withAlertRules`
registration helpers).

## What changes

A new, additive, **opt-in** background subsystem that turns the emit-only
observability floor operator-facing: it evaluates declarative threshold rules
against the signals the SDK already emits (`IMetricsSink` accumulator, Phase 9e;
`IHealthCheck` state, Phase 9k) and *delivers* breaches through the existing
notification family (`INotificationChannel` for in-app / SSE; the transactional
dispatcher for out-of-band email / SMS / push).

Nothing is registered and no tick runs unless a deployment declares at least one
rule — an empty `ServerConfig.AlertRules` (the default) is byte-for-byte
unchanged (GP 13).

### New surface

- `ToolUp.Platform.AlertRule` (Core / Shared, Fable-safe) — the rule as pure
  data:

  ```fsharp
  type AlertSource =
      | Metric of name: string * tags: Map<string, string>
      | HealthProbe of probeName: string

  type ThresholdCondition =
      | GreaterThan of float
      | LessThan of float
      | Equals of float
      | ProbeUnhealthy
      | ProbeDegraded          // "at least degraded" — Degraded OR Unhealthy

  type AlertDelivery =
      | ViaChannel of scopeId: string
      | ViaSink of NotificationKind.SinkKind

  type AlertRule = {
      Name: string             // unique per deployment (keys the breach window)
      Source: AlertSource
      Condition: ThresholdCondition
      ForDuration: TimeSpan    // debounce window; breach must persist across it
      Severity: SystemMessageLevel
      DeliverVia: AlertDelivery list
  }
  ```

- `ServerConfig.AlertRules: AlertRule list` — defaults to `AlertRule.none`
  (empty). Code-authored only; there is no env-var path, so `ServerConfig.fromEnv`
  inherits the empty default.

- `ServerApp.withAlertRule` / `ServerApp.withAlertRules` — the fluent registration
  helpers (mirror `withTransactionalSink`).

- `PrometheusMetricsSink.TryRead(name, tags): float option` — the metric-read tap
  the engine consumes. `IMetricsSink` itself stays **write-only** (its hot-path
  rule-2 exemption forbids a read method on the emission interface); the read
  lives on the concrete default sink.

### Behaviour

- **Debounce + re-arm.** A rule fires exactly once when its condition has held
  for `ForDuration` (evaluated on a wall-clock-aligned `JobPrecision.Minute`
  tick), and re-arms only after the signal recovers — mirrors Prometheus `for:`.
  A transient sub-window blip never fires.
- **Delivery.** `ViaChannel scopeId` publishes a `SystemMessage` (at the rule's
  `Severity`) on that scope. `ViaSink sinkKind` publishes the matching
  transactional notification under the reserved `_platform` scope; the
  `DispatchingNotificationChannel` decorator routes it to the registered
  `INotificationSink`. `ViaSink` envelopes carry no recipients — recipient
  targeting for engine-driven alerts (e.g. resolving platform admins) is a
  documented follow-up; this phase ships the routing seam.
- **Hosting.** A non-empty rule set hosts the `AlertRuleEngineService`
  `IHostedService`, gated by the `ProcessProfile` matrix (`AllInOne` /
  `WorkerOnly` run it; `WebOnly` / `DispatcherOnly` / `ServerlessHost` skip). The
  per-rule breach window is in-memory — single-instance (same class as
  `HealthStateTracker` / `JobScheduler`; flagged for the distributed companion).

## Diff to apply

Nothing for existing consumers. This is additive and default-off — a deployment
that doesn't declare `AlertRules` is unchanged (GP 13), and the metrics /
health / notification surfaces the engine reads are untouched.

To adopt, register rules at compose time:

```fsharp
open ToolUp.Platform

let queueDepthAlert = {
    Name = "ingestion-backlog"
    Source = Metric("toolup.jobs.queue_depth", Map.empty)
    Condition = GreaterThan 500.0
    ForDuration = TimeSpan.FromMinutes 10.0
    Severity = SystemMessageLevel.Warning
    DeliverVia = [ ViaChannel "_platform"; ViaSink NotificationKind.SinkKind.Email ]
}

let redisDownAlert = {
    Name = "redis-unhealthy"
    Source = HealthProbe "redis"
    Condition = ProbeUnhealthy
    ForDuration = TimeSpan.Zero
    Severity = SystemMessageLevel.Error
    DeliverVia = [ ViaChannel "_platform" ]
}

app
|> ServerApp.withAlertRules [ queueDepthAlert; redisDownAlert ]
```

`ViaSink` targets need the matching `INotificationSink` wired
(`ServerApp.withTransactionalSink`) and metric rules need
`ServerConfig.MetricsEndpoint = EnabledMetricsEndpoint` (else `TryRead` returns
`None` and metric rules never fire; health-probe rules still work).

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `AlertRuleEngineTests` — debounce (sub-window no fire), fire-once + re-arm,
  absent-series hold, `ProbeUnhealthy` / `ProbeDegraded` semantics, `ViaChannel`
  → `SystemMessage` on the target scope, `ViaSink` → matching transactional
  kind, and the GP 13 empty-set-hosts-no-engine contract.
- Public-API baseline regenerated for `ToolUp.Platform.Core` (the new
  `AlertRule` types + the `ServerConfig.AlertRules` field / ctor change).

## Rollback

Remove the `withAlertRule(s)` call(s). With no rules declared the engine is not
hosted; the `AlertRule` types + `ServerConfig.AlertRules` field remain inert.

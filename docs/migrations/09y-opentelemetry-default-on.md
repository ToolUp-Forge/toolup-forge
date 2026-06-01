# Phase 9y — OpenTelemetry default-on (audit finding: promotion dropped)

**TL;DR.** Phase 9y proposed promoting `services.AddOpenTelemetry()` from companion-opt-in (`ToolUp.Platform.Metrics.OpenTelemetry`) to default-on in `ToolUp.Platform.Server`. The portability audit (Task #1 in the phase body) concluded **the promotion is not genuinely zero-cost** and would violate GP 1 (companion isolation) + GP 13 (advanced behaviour is opt-in; deployments that don't use it pay nothing). **The companion-only design holds.** No consumer-side change is required.

## What this migration is (and isn't)

This document is the **finding-of-record** for the Phase 9y portability audit. It is **not** a migration in the change-something-in-your-code sense. The phase body required the finding to be documented either way ("document the finding either way"); this is that document.

If you maintain a downstream consumer of `ToolUp.Platform.Server`:

- **Doing nothing is correct.** No package bumps, no `ServerConfig` changes, no `ServerApp` builder additions, no startup-script env vars. The companion remains the explicit-opt-in seam for OTLP / Honeycomb / Datadog APM / Jaeger / Application Insights / Cloud Monitor / CloudWatch / Cloud Operations export, exactly as before.
- **Existing OTel companion wire-ups continue to work unchanged.** `OtelMetricsSink.create` and `OtelActivitySink.create` still construct BCL `Meter` / `ActivitySource` instances named `"ToolUp"` that the OpenTelemetry SDK picks up via `MeterProviderBuilder.AddMeter("ToolUp")` + `TracerProviderBuilder.AddSource("ToolUp")` in your composition root.

## The audit

### What was proposed

The phase body proposed registering `services.AddOpenTelemetry()` by default in `Server/SDK.Server.fs`, with the `MeterProvider` / `TracerProvider` wired to the metric + activity sources the SDK emits:

```fsharp
// Proposed default in ComposeRuntimeServices.registerMetricsAndObservability
services
    .AddOpenTelemetry()
    .WithMetrics(fun b -> b.AddMeter("ToolUp") |> ignore)
    .WithTracing(fun b -> b.AddSource("ToolUp") |> ignore)
|> ignore
```

The companion `ToolUp.Platform.Metrics.OpenTelemetry` would then be refactored to *override* the default registration (binding an `OTLP` / `azure-monitor` / `cloudwatch` / `gcp-operations` / `honeycomb` / `datadog` / `jaeger` exporter selected by `TOOLUP_OTEL_EXPORTER`) rather than add the `AddOpenTelemetry` chain from scratch.

The acceptance gate (GP 13): a fresh `samples/HelloWorld/`-shape app must continue to start with zero env vars, zero cloud accounts, and zero exporters, with no additional cost over the pre-change baseline.

### Cost dimensions evaluated

#### 1. NuGet dependency surface

`services.AddOpenTelemetry()` lives in `OpenTelemetry.Extensions.Hosting`. Pulling that package into `ToolUp.Platform.Server` transitively brings:

- `OpenTelemetry` (SDK core — `MeterProviderSdk`, `TracerProviderSdk`, batch processors, default samplers).
- `OpenTelemetry.Api` (BCL abstraction extensions).
- The hosting integration that registers `MeterProvider` / `TracerProvider` as `IHostedService` and wires them through ASP.NET Core's shutdown sequence.

Combined on-disk footprint is in the several-megabyte range across the SDK and API DLLs. Every downstream consumer of `ToolUp.Platform.Server` pays this cost — including the minimum-viable-platform shape (`samples/HelloWorld/`) that has no exporter wired and never observes a metric.

This conflicts with GP 1 ("companion packages isolate vendor dependencies — the SDK core carries no third-party vendor SDK"). The OpenTelemetry SDK is precisely the kind of vendor SDK that GP 1 carves out into companion packages; moving it into the core re-litigates a settled design choice for the sake of one boilerplate-line reduction at the consumer site.

#### 2. Runtime cost — listener attachment changes the hot path

Even with no exporter wired, calling `WithMetrics(b => b.AddMeter("ToolUp"))` and `WithTracing(b => b.AddSource("ToolUp"))` attaches BCL listeners to the named `Meter` / `ActivitySource`. The attached-listener case is not the same shape as the no-listener case the BCL primitives optimise for:

**Metrics emission (`Counter<double>.Add`, `Histogram<double>.Record`, observable gauges).** With no listener subscribed, instrument-level `Enabled` is `false` and the BCL fast-path elides the work. With a listener subscribed (the SDK's internal `MeterListener` subscribes on `AddMeter`-matched instruments at provider build time), `Enabled` flips to `true`. Every emission then:

- Allocates tag-set bookkeeping.
- Invokes the `MeasurementCallback` chain.
- Feeds the SDK's aggregation pipeline.
- The aggregated state buffers in the default `MetricReader`'s in-memory store. Without an exporter the buffer is never flushed; without a `MeterProviderBuilder.SetMaxMetricStreams` ceiling the buffer is bounded only by tag cardinality.

The metrics fan-out chain inside the SDK (Phase 9e `PrometheusMetricsSink` + companion sinks) is unaffected. The cost is additive at the BCL layer.

**Tracing emission (`ActivitySource.StartActivity`).** With no listener subscribed, `StartActivity` returns `null` immediately — and the SDK's `IActivitySink.NoOpActivitySink` folds that into `None`, so every `Option.iter` disposal site elides. With a listener subscribed (the SDK's internal `ActivityListener` subscribes via `AddSource("ToolUp")`), `StartActivity`:

- Walks the sampler chain (`AlwaysOnSampler` by default — every span sampled in).
- Allocates an `Activity` object.
- Sets `Activity.Current` on the async-local cursor.
- Returns the live `Activity` to the caller, whose `Option.iter` disposal then actually disposes.

The five SDK-auto-instrumented seams from Phase 9l (`ScopeResolutionMiddleware`, `JobScheduler.dispatchOne`, `WebhookDispatcher.runDelivery`, `TransactionalDispatcher.runDelivery`, `InMemoryModuleQueryBus.Ask`) all switch from "near-zero `None` round-trip" to "live `Activity` allocation per call" — even when the consumer has no exporter wired and no intent to use distributed tracing.

This contradicts the GP 13 promise ("advanced behaviour is opt-in; deployments that don't use it pay nothing").

#### 3. Consumer mental-model cost

The current companion-opt-in shape is a single decision point: *do you want OpenTelemetry export?* If yes, add the companion's `<PackageReference>` and call `ServerApp.withMetricsSink` / `withActivitySink`. If no, do nothing.

The proposed default-on shape introduces an unconfigured default. New consumers reading the SDK source would discover an `AddOpenTelemetry` call wired to a never-configured `MeterProvider` and reasonably ask "why is OTel registered if I never asked for it?" The answer ("so the companion can override it later") is non-obvious. The companion-opt-in shape is the right granularity for the choice the consumer is actually making.

#### 4. `samples/HelloWorld/` cold-start budget

The acceptance gate in the phase body required the fresh `dotnet new platformsdk-solution` to "build + run with zero env vars set, zero cloud accounts, zero exporters configured" within prior cold-start budgets. Adding `OpenTelemetry.Extensions.Hosting` to `ToolUp.Platform.Server`'s dependency closure increases the consumer-build's DLL-load cost (file IO + JIT warmup of OTel SDK types the consumer never calls) by a measurable amount on every cold start. The pre-change cold start does not pay this cost; the post-change baseline would. The "prior cold-start budget" is therefore a moving target the change itself would shift.

### Conclusion

The proposed promotion is not zero-cost on any of the four cost dimensions evaluated. The phase task #1 explicit exit path applies:

> If the no-op cost is non-negligible, drop the promotion and keep companion-only — document the finding either way.

**Decision: drop the promotion. The `ToolUp.Platform.Metrics.OpenTelemetry` companion remains the opt-in seam.**

The existing companion design — BCL `Meter` / `ActivitySource` named `"ToolUp"` owned by the companion, OpenTelemetry SDK lifecycle owned by the consumer — preserves GP 1 (no vendor SDK in the core) and GP 13 (consumers that don't wire OTel pay zero). The companion's `README.md` already documents the four-line consumer-side wire-up.

## What stays unchanged

- `ToolUp.Platform.Server` dependency closure: unchanged.
- `OtelMetricsSink` / `OtelActivitySink` public surfaces: unchanged.
- `ServerApp.withMetricsSink` / `ServerApp.withActivitySink` wire-up shape: unchanged.
- `MetricsMiddleware` placement, `/metrics` endpoint, `PrometheusMetricsSink` fan-out: unchanged.
- Companion `README.md` four-line deployment recipe: unchanged.
- `MeterProviderBuilder.AddMeter("ToolUp")` + `TracerProviderBuilder.AddSource("ToolUp")` in the consumer's `Program.fs`: still the consumer-owned step.

## Verification

No code changes shipped under this phase, so the verification reduces to:

1. `dotnet build` against the SDK solution remains clean (no new dependency reference to verify).
2. `samples/HelloWorld/` cold-start time + steady-state allocation remain at their pre-Phase-9y baseline.
3. A consumer that previously wired the companion via `OtelMetricsSink.create` + `OtelActivitySink.create` + the four-line OTel SDK boilerplate in `Program.fs` continues to export to its configured collector with no source-code change.

## Rollback

The phase shipped no code change, so rollback is the empty diff. A future re-evaluation of this finding (see "Re-evaluation conditions" below) is the route forward if the trade-offs shift.

## Re-evaluation conditions

This finding holds while the following remain true:

1. The OpenTelemetry SDK continues to require a per-`Meter` / per-`ActivitySource` listener subscription at provider build time (i.e. attached-listener emission cost stays materially higher than no-listener emission cost on .NET).
2. `OpenTelemetry.Extensions.Hosting` continues to live in a separate NuGet from BCL, requiring an additional package reference for `services.AddOpenTelemetry()`.
3. The SDK core's "minimum viable platform" promise (a `samples/HelloWorld/` shape app starts with zero env vars, zero cloud accounts) remains a load-bearing design principle.

Should any of these change — e.g. the BCL absorbs `AddOpenTelemetry` as a hosting-platform primitive, or the OpenTelemetry SDK adds a documented "registered but zero-cost when no exporter" mode — re-open the audit. The companion-opt-in shape is then a small refactor away from the default-on shape, not a structural rewrite.

## Consumer adoption

**Every cell is N-A.** No consumer-side adoption work is required.

| Consumer | Status | Reason |
|---|---|---|
| Pinned consumers | ⛔ N-A | No source change, no NuGet bump, no env-var change. Existing companion wire-ups continue unchanged. |
| In-tree consumers (`samples/`, `templates/`) | ⛔ N-A | Same. The `samples/HelloWorld/` cold-start baseline is preserved by the audit conclusion. |

## See also

- [`src/Metrics/OpenTelemetry/README.md`](../../src/Metrics/OpenTelemetry/README.md) — companion deployment recipe (unchanged).
- [`src/ToolUp.Platform/technical-guide/05-audit-health-and-metrics.md`](../../src/ToolUp.Platform/technical-guide/05-audit-health-and-metrics.md) — OpenTelemetry companion section, updated to reference this audit finding for grep-discoverability.
- [`docs/migrations/16d-forwarded-headers-default-on.md`](16d-forwarded-headers-default-on.md) — sibling Phase 16d default-on flip, which *did* clear its portability audit. The contrast is instructive: 16d flipped an existing `ServerConfig` field's default, with no new NuGet dep and no runtime cost change in the off-path. 9y would have added both.

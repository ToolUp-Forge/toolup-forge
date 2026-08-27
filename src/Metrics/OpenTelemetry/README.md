# ToolUp.Metrics.OpenTelemetry

OpenTelemetry-compatible metrics sink for the ToolUp Platform SDK (Phase 9e).

## What it does

Implements `IMetricsSink` against the BCL `System.Diagnostics.Metrics.Meter` API. `Meter` is the OTel-native metric primitive on .NET 10 — the OpenTelemetry SDK consumes `Meter` instances and exports them to OTLP, Prometheus, Console, or any registered exporter.

Composing this companion alongside the SDK's in-process `PrometheusMetricsSink` gives every `Increment` / `Record` / `SetGauge` call two destinations: the in-process Prometheus registry (scraped at `/metrics`) and the BCL Meter (consumed by whatever OTel SDK the deployment configures).

## What it does NOT do

This companion does NOT take a NuGet dependency on the OpenTelemetry SDK. The interface point is BCL `Meter`; the deployment owns the SDK's lifecycle (sampling, resource attributes, batch reader cadence, OTLP endpoint configuration). The split keeps this companion lean — apps that don't need OTLP export pay only the cost of a few BCL `Counter<double>` / `Histogram<double>` instruments.

## Enabling

1. Add the project reference and the props injection in your server project:

   ```xml
   <Import Project="..\Metrics\OpenTelemetry\ToolUp.Metrics.OpenTelemetry.Server.props" />
   <ProjectReference Include="..\Metrics\OpenTelemetry\ToolUp.Metrics.OpenTelemetry.fsproj" />
   ```

2. In your composition root, register the sink alongside the in-process default:

   ```fsharp skip=fragment
   open ToolUp.Platform.Metrics
   open ToolUp.Platform.Metrics.OpenTelemetry

   let otelSink =
       OtelMetricsSink.create StandardMetrics.registrations resolvedLogger

   ServerApp.empty
   |> ServerApp.withConfig { config with MetricsEndpoint = EnabledMetricsEndpoint }
   |> ServerApp.withMetricsSink otelSink
   |> ServerApp.run
   ```

3. To export to OTLP (the standard OTel collector format), add the exporter NuGet to your server project's `paket.references`:

   ```
   OpenTelemetry
   OpenTelemetry.Exporter.OpenTelemetryProtocol
   ```

   And in your `Program.fs`:

   ```fsharp
   open OpenTelemetry
   open OpenTelemetry.Metrics

   let meterProvider =
       Sdk.CreateMeterProviderBuilder()
           .AddMeter("ToolUp")
           .AddOtlpExporter(fun opts ->
               // Defaults to OTEL_EXPORTER_OTLP_ENDPOINT env var
               // (http://localhost:4317 if unset). Override here to
               // pin the endpoint inline.
               opts.Endpoint <- System.Uri("http://your-collector:4317"))
           .Build()
   ```

## How it composes

When `ServerApp.withMetricsSink otelSink` is wired in alongside `MetricsEndpoint = EnabledMetricsEndpoint`, `compose` folds the in-process `PrometheusMetricsSink` and every registered companion sink into a `FanOutMetricsSink`. A single `Increment` call dispatches to all sinks. The Prometheus sink is always at the head of the list so `/metrics` keeps returning current values even if the OTel exporter is down.

## Cardinality

The per-metric series-count cap is enforced by the in-process `PrometheusMetricsSink` at the head of the fan-out chain. Emissions past the cap fold into `_overflow="true"` before reaching this companion. The OTel SDK's `MeterProviderBuilder.SetMaxMetricStreams` is the natural place for an additional ceiling on the export side.

## Six-rule portability audit

| Rule | Status | Notes |
|---|---|---|
| 1. Identity by value | ✓ | Metric `name : string`, tag keys/values strings. No live framework handle on the surface. |
| 2. Async exemption | ✓ | Sync interface matches `IMetricsSink`'s documented exemption. |
| 3. Retry as data | ✓ | Sinks are write-only fire-and-forget; OTel SDK exporter handles retry. |
| 4. Stateless boundary | ✓ | Each call carries `(name, value, tags)` in full. Instrument cache is impl detail. |
| 5. No cross-shard ordering | ✓ | Metric points commutative; no ordering claimed. |
| 6. Precision documented | ✓ | Histogram bucket boundaries are `MetricDefinition.Kind = Histogram bs : float list`. |

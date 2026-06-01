# ToolUp.Cloud.Gcp

GCP cloud umbrella for the ToolUp Platform SDK (Phase 16c). One `<PackageReference>` replaces the five per-cloud entries a GCP-targeting consumer would otherwise add manually.

## What it pulls

| Package | What it does |
|---|---|
| [`ToolUp.Storage.GoogleCloud`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Storage/GoogleCloudStorage) | `IBlobStorage` companion against Google Cloud Storage. |
| [`ToolUp.Secrets.GcpSecretManager`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Secrets/GcpSecretManager) | `ISecretStore` companion against GCP Secret Manager. |
| [`ToolUp.AuditSinks.GcsArchive`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/AuditSinks/GcsArchive) | `IAuditSink` writing gzipped JSONL archives to a Retention-Policy-locked GCS bucket. |
| [`ToolUp.Metrics.OpenTelemetry`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Metrics/OpenTelemetry) | `IMetricsSink` exposing the BCL `Meter` for OTel export. |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | Standard OTLP exporter — send to a Cloud Operations collector. |

## How to enable

1. Add one reference:

   ```xml
   <PackageReference Include="ToolUp.Cloud.Gcp" />
   ```

2. Wire the per-companion components in your composition root as you would each individually — the umbrella does NOT introduce a new abstraction layer. A consumer dropping the umbrella and adding the five inner packages by hand gets byte-identical behaviour.

   ```fsharp
   open ToolUp.Platform
   open ToolUp.Platform.BlobStorage
   open ToolUp.Platform.AuditSinks.GcsArchive
   open OpenTelemetry
   open OpenTelemetry.Metrics

   // Storage + secrets
   let blobStorage = GoogleCloudStorage.create config :> IBlobStorage
   let secretStore = GcpSecretManagerSecretStore.create config :> ISecretStore

   // Audit archive
   let auditSink =
       GcsArchive.create
           "gcs-prod-audit"
           { Container = "acme-audit-prod"; PathPrefix = Some "v1" }
           blobStorage

   // OTel metrics — switch exporter via TOOLUP_OTEL_EXPORTER convention
   let exporter = System.Environment.GetEnvironmentVariable "TOOLUP_OTEL_EXPORTER"

   let meterProvider =
       let builder =
           Sdk.CreateMeterProviderBuilder()
               .AddMeter("ToolUp")

       match exporter with
       | "gcp-operations"
       | "otlp"
       | _ ->
           // OTLP to Cloud Operations collector. Set
           // OTEL_EXPORTER_OTLP_ENDPOINT to the collector's address.
           builder.AddOtlpExporter().Build()

   ServerApp.empty
   |> ServerApp.withConfig config
   |> ServerApp.withStorage blobStorage
   |> ServerApp.withSecretStore secretStore
   |> ServerApp.withAuditSink auditSink
   |> ServerApp.withMetricsSink (OtelMetricsSink.create StandardMetrics.registrations logger)
   |> ServerApp.run
   ```

## Cloud Operations export path

GCP's recommended metrics path is OTLP-to-collector. Run the [OpenTelemetry Collector with Google Cloud Operations exporter](https://github.com/GoogleCloudPlatform/opentelemetry-operations-collector), point your app at it via `OTEL_EXPORTER_OTLP_ENDPOINT`, and the collector handles the export to Cloud Monitoring / Cloud Trace.

Typical deployment shapes:
- **Cloud Run**: OTel sidecar container; localhost OTLP endpoint. Workload Identity binds the collector's GCP credentials.
- **GKE**: Cloud Operations collector DaemonSet; service-DNS OTLP endpoint.
- **Compute Engine**: agent-style collector (`ops-agent` with OTLP receiver enabled) installed on the VM.

## Exporter selection — `TOOLUP_OTEL_EXPORTER` convention

The umbrella does NOT register OTel itself — that's the consumer's startup code. The `TOOLUP_OTEL_EXPORTER` env var is a recommended convention your `Program.fs` can read to switch exporters:

| Value | Behaviour |
|---|---|
| `gcp-operations` | OTLP exporter pointed at a Cloud Operations collector. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to the collector's address. |
| `otlp` | Same as `gcp-operations` from this umbrella's perspective — the universal OTLP exporter. The endpoint determines the destination. |
| unset / other | Consumer's choice. Skipping the exporter entirely is supported — the `OtelMetricsSink` emits to the BCL `Meter`, which becomes a no-op without a registered listener. |

## Overriding an individual companion's version

Central Package Management (CPM) honours per-consumer overrides:

```xml
<ItemGroup>
  <PackageVersion Include="ToolUp.Cloud.Gcp" Version="0.4.4" />
  <PackageVersion Include="ToolUp.Storage.GoogleCloud" Version="0.5.0-preview1" />
</ItemGroup>
```

## When NOT to use the umbrella

Drop the umbrella and reference the companions individually when:
- You use GCS but a non-GCP secret store (e.g. HashiCorp Vault).
- You want a smaller dependency footprint.
- You're consciously diverging the version of one companion ahead of others.

## See also

- [`ToolUp.Cloud.Azure`](../Azure/README.md) — sibling Azure umbrella.
- [`ToolUp.Cloud.Aws`](../Aws/README.md) — sibling AWS umbrella.
- [`docs/operations/cloud-umbrella-packages.md`](https://github.com/ToolUp-Forge/toolup-forge/blob/main/docs/operations/cloud-umbrella-packages.md) — cross-umbrella reference.

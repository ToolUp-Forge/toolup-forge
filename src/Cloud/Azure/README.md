# ToolUp.Cloud.Azure

Azure cloud umbrella for the ToolUp Platform SDK (Phase 16c). One `<PackageReference>` replaces the five per-cloud entries a Azure-targeting consumer would otherwise add manually.

## What it pulls

| Package | What it does |
|---|---|
| [`ToolUp.Storage.AzureBlob`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Storage/AzureBlobStorage) | `IBlobStorage` companion against Azure Blob Storage. |
| [`ToolUp.Secrets.AzureKeyVault`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Secrets/AzureKeyVault) | `ISecretStore` companion against Azure Key Vault. |
| [`ToolUp.AuditSinks.AzureBlobArchive`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/AuditSinks/AzureBlobArchive) | `IAuditSink` writing gzipped JSONL archives to a WORM-enabled Azure Blob container. |
| [`ToolUp.Metrics.OpenTelemetry`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Metrics/OpenTelemetry) | `IMetricsSink` exposing the BCL `Meter` for OTel export. |
| `Azure.Monitor.OpenTelemetry.AspNetCore` | First-party Azure Monitor OTel distro — direct exporter without a collector. |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | Standard OTLP exporter — send to Azure Monitor's OTLP endpoint or any ADOT-style collector. |

## How to enable

1. Add one reference:

   ```xml
   <PackageReference Include="ToolUp.Cloud.Azure" />
   ```

2. Wire the per-companion components in your composition root as you would each individually — the umbrella does NOT introduce a new abstraction layer. A consumer dropping the umbrella and adding the five inner packages by hand gets byte-identical behaviour.

   ```fsharp
   open ToolUp.Platform
   open ToolUp.Platform.BlobStorage
   open ToolUp.Platform.AuditSinks.AzureBlobArchive
   open OpenTelemetry
   open OpenTelemetry.Metrics

   // Storage + secrets
   let blobStorage = AzureBlobStorage.create config :> IBlobStorage
   let secretStore = AzureKeyVaultSecretStore.create config :> ISecretStore

   // Audit archive
   let auditSink =
       AzureBlobArchive.create
           "azure-prod-audit"
           { Container = "acme-audit-prod"; PathPrefix = Some "v1" }
           blobStorage

   // OTel metrics — switch exporter via TOOLUP_OTEL_EXPORTER convention
   let exporter = System.Environment.GetEnvironmentVariable "TOOLUP_OTEL_EXPORTER"

   let meterProvider =
       let builder =
           Sdk.CreateMeterProviderBuilder()
               .AddMeter("ToolUp")

       match exporter with
       | "azure-monitor" ->
           // First-party direct exporter — no collector required.
           builder.AddAzureMonitorMetricExporter().Build()
       | "otlp"
       | _ ->
           // Standard OTLP — point at your collector via OTEL_EXPORTER_OTLP_ENDPOINT.
           builder.AddOtlpExporter().Build()

   ServerApp.empty
   |> ServerApp.withConfig config
   |> ServerApp.withStorage blobStorage
   |> ServerApp.withSecretStore secretStore
   |> ServerApp.withAuditSink auditSink
   |> ServerApp.withMetricsSink (OtelMetricsSink.create StandardMetrics.registrations logger)
   |> ServerApp.run
   ```

## Exporter selection — `TOOLUP_OTEL_EXPORTER` convention

The umbrella does NOT register OTel itself — that's the consumer's startup code (per Phase 9y, OTel default-on was DROPPED as it would violate GP 13 zero-cost-when-not-used). The `TOOLUP_OTEL_EXPORTER` env var is a recommended convention your `Program.fs` can read to switch exporters:

| Value | Behaviour |
|---|---|
| `azure-monitor` | Direct Azure Monitor exporter via `AddAzureMonitorMetricExporter()`. No collector required; sends straight to Application Insights. |
| `otlp` | Standard OTLP exporter via `AddOtlpExporter()`. Configure `OTEL_EXPORTER_OTLP_ENDPOINT` to point at your collector (Azure Monitor's OTLP endpoint, Honeycomb, Grafana Cloud, etc.). |
| unset / other | Consumer's choice. Skipping the exporter entirely is supported — the `OtelMetricsSink` still emits to the BCL `Meter`, which becomes a no-op without a registered listener. |

## Overriding an individual companion's version

Central Package Management (CPM) honours per-consumer overrides. To pin one transitive companion (e.g. an early-access build of `ToolUp.Storage.AzureBlob`) without dropping the umbrella, declare it explicitly in the consumer's `Directory.Packages.props`:

```xml
<ItemGroup>
  <PackageVersion Include="ToolUp.Cloud.Azure" Version="0.4.4" />
  <!-- Override one transitive companion — later entries win in CPM. -->
  <PackageVersion Include="ToolUp.Storage.AzureBlob" Version="0.5.0-preview1" />
</ItemGroup>
```

The umbrella's nuspec declares `ToolUp.Storage.AzureBlob` as a dependency with the umbrella's pack-time version; the consumer's `PackageVersion` override resolves the dep to the requested version at restore time. No fork or wrapper needed.

## When NOT to use the umbrella

Drop the umbrella and reference the companions individually when:
- Your deployment uses Azure Blob Storage but a non-Azure secret store (e.g. HashiCorp Vault on-prem).
- You want a smaller dependency footprint and don't need OTel.
- You're consciously diverging the version of one companion ahead of others.

The umbrella adds nothing the individual companions don't — it's purely an ergonomic shortcut.

## See also

- [`ToolUp.Cloud.Aws`](../Aws/README.md) — sibling AWS umbrella.
- [`ToolUp.Cloud.Gcp`](../Gcp/README.md) — sibling GCP umbrella.
- [`docs/operations/cloud-umbrella-packages.md`](https://github.com/ToolUp-Forge/toolup-forge/blob/main/docs/operations/cloud-umbrella-packages.md) — cross-umbrella reference, build-time-vs-runtime config taxonomy.

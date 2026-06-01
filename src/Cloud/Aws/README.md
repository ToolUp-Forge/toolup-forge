# ToolUp.Cloud.Aws

AWS cloud umbrella for the ToolUp Platform SDK (Phase 16c). One `<PackageReference>` replaces the five per-cloud entries an AWS-targeting consumer would otherwise add manually.

## What it pulls

| Package | What it does |
|---|---|
| [`ToolUp.Storage.AwsS3`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Storage/AwsS3Storage) | `IBlobStorage` companion against AWS S3. |
| [`ToolUp.Secrets.AwsSecretsManager`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Secrets/AwsSecretsManager) | `ISecretStore` companion against AWS Secrets Manager. |
| [`ToolUp.AuditSinks.S3Archive`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/AuditSinks/S3Archive) | `IAuditSink` writing gzipped JSONL archives to an S3 Object Lock bucket. |
| [`ToolUp.Metrics.OpenTelemetry`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Metrics/OpenTelemetry) | `IMetricsSink` exposing the BCL `Meter` for OTel export. |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | Standard OTLP exporter — send to an ADOT (AWS Distro for OpenTelemetry) collector for CloudWatch export. |

## How to enable

1. Add one reference:

   ```xml
   <PackageReference Include="ToolUp.Cloud.Aws" />
   ```

2. Wire the per-companion components in your composition root as you would each individually — the umbrella does NOT introduce a new abstraction layer. A consumer dropping the umbrella and adding the five inner packages by hand gets byte-identical behaviour.

   ```fsharp
   open ToolUp.Platform
   open ToolUp.Platform.BlobStorage
   open ToolUp.Platform.AuditSinks.S3Archive
   open OpenTelemetry
   open OpenTelemetry.Metrics

   // Storage + secrets
   let blobStorage = AwsS3Storage.create config :> IBlobStorage
   let secretStore = AwsSecretsManagerSecretStore.create config :> ISecretStore

   // Audit archive
   let auditSink =
       S3Archive.create
           "aws-prod-audit"
           { Container = "acme-audit-prod"; PathPrefix = Some "v1" }
           blobStorage

   // OTel metrics — switch exporter via TOOLUP_OTEL_EXPORTER convention
   let exporter = System.Environment.GetEnvironmentVariable "TOOLUP_OTEL_EXPORTER"

   let meterProvider =
       let builder =
           Sdk.CreateMeterProviderBuilder()
               .AddMeter("ToolUp")

       match exporter with
       | "cloudwatch"
       | "otlp"
       | _ ->
           // OTLP to ADOT collector — set OTEL_EXPORTER_OTLP_ENDPOINT
           // to your collector's address. The collector handles
           // CloudWatch export.
           builder.AddOtlpExporter().Build()

   ServerApp.empty
   |> ServerApp.withConfig config
   |> ServerApp.withStorage blobStorage
   |> ServerApp.withSecretStore secretStore
   |> ServerApp.withAuditSink auditSink
   |> ServerApp.withMetricsSink (OtelMetricsSink.create StandardMetrics.registrations logger)
   |> ServerApp.run
   ```

## ADOT (AWS Distro for OpenTelemetry)

AWS's recommended metrics path is OTLP-to-collector, not direct .NET-to-CloudWatch export. Run an [ADOT collector](https://aws-otel.github.io/) sidecar / DaemonSet, point your app at it via `OTEL_EXPORTER_OTLP_ENDPOINT`, and the collector handles the export to CloudWatch Metrics / X-Ray / CloudWatch Logs.

Typical deployment shapes:
- **ECS Fargate / EC2**: ADOT collector sidecar in the same task; localhost OTLP endpoint.
- **EKS**: ADOT collector DaemonSet; service-DNS OTLP endpoint.
- **Lambda**: ADOT Lambda layer; auto-instrumented OTLP-to-CloudWatch path.

## Exporter selection — `TOOLUP_OTEL_EXPORTER` convention

The umbrella does NOT register OTel itself — that's the consumer's startup code. The `TOOLUP_OTEL_EXPORTER` env var is a recommended convention your `Program.fs` can read to switch exporters:

| Value | Behaviour |
|---|---|
| `cloudwatch` | OTLP exporter pointed at an ADOT collector that exports to CloudWatch. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to the collector's address. |
| `otlp` | Same as `cloudwatch` from this umbrella's perspective — the universal OTLP exporter. The endpoint determines the destination. |
| unset / other | Consumer's choice. Skipping the exporter entirely is supported — the `OtelMetricsSink` emits to the BCL `Meter`, which becomes a no-op without a registered listener. |

## Overriding an individual companion's version

Central Package Management (CPM) honours per-consumer overrides:

```xml
<ItemGroup>
  <PackageVersion Include="ToolUp.Cloud.Aws" Version="0.4.4" />
  <PackageVersion Include="ToolUp.Storage.AwsS3" Version="0.5.0-preview1" />
</ItemGroup>
```

## When NOT to use the umbrella

Drop the umbrella and reference the companions individually when:
- You use AWS S3 but a non-AWS secret store (e.g. HashiCorp Vault).
- You want a smaller dependency footprint.
- You're consciously diverging the version of one companion ahead of others.

## See also

- [`ToolUp.Cloud.Azure`](../Azure/README.md) — sibling Azure umbrella.
- [`ToolUp.Cloud.Gcp`](../Gcp/README.md) — sibling GCP umbrella.
- [`docs/operations/cloud-umbrella-packages.md`](https://github.com/ToolUp-Forge/toolup-forge/blob/main/docs/operations/cloud-umbrella-packages.md) — cross-umbrella reference.

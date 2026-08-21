# Cloud umbrella packages

Three meta-packages bundle the per-cloud companion set into a single `<PackageReference>`:

| Umbrella | What it pulls (transitive) |
|---|---|
| [`ToolUp.Cloud.Azure`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Cloud/Azure) | `ToolUp.Storage.AzureBlob`, `ToolUp.Secrets.AzureKeyVault`, `ToolUp.AuditSinks.AzureBlobArchive`, `ToolUp.Metrics.OpenTelemetry`, `Azure.Monitor.OpenTelemetry.AspNetCore`, `OpenTelemetry.Exporter.OpenTelemetryProtocol` |
| [`ToolUp.Cloud.Aws`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Cloud/Aws) | `ToolUp.Storage.AwsS3`, `ToolUp.Secrets.AwsSecretsManager`, `ToolUp.AuditSinks.S3Archive`, `ToolUp.Metrics.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol` |
| [`ToolUp.Cloud.Gcp`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Cloud/Gcp) | `ToolUp.Storage.GoogleCloud`, `ToolUp.Secrets.GcpSecretManager`, `ToolUp.AuditSinks.GcsArchive`, `ToolUp.Metrics.OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol` |

The umbrellas are **purely transitive deps + per-cloud-default exporter package wiring**. No `.fs` source ships in the package; no new abstraction layer is introduced over the per-cloud seams. A consumer dropping the umbrella and adding the inner packages by hand gets byte-identical behaviour.

## When to use which umbrella — vs. individual companions

| Scenario | Recommendation |
|---|---|
| Single-cloud deployment, every persistence + auth tier on that cloud | Use the umbrella. |
| Mixed cloud (e.g. Azure Blob + HashiCorp Vault on-prem) | Reference the individual companions. The umbrella's value is bundling; once you diverge from one of its components, the bundling stops paying for itself. |
| You want to pin one transitive companion ahead of the others (preview / hotfix) | Use the umbrella + a CPM override (see below). The override + bundling coexist. |
| You're targeting two clouds from one consumer (rare — usually a deployment-level concern, not consumer-code) | Reference the individual companions. The umbrellas don't compose in this direction; each pulls its cloud's exporter as a transitive dep. |
| OSS contributor evaluating the SDK / running `samples/HelloWorld` | Skip the umbrella — `LocalFileStorage` + `InMemorySecretStore` are zero-config and require no cloud. |

## Central Package Management override pattern

The umbrella declares its transitive dependencies with the umbrella's pack-time version (currently `0.4.4` — bumped in lockstep with `ToolUpSdkVersion`). A consumer using Central Package Management can override one transitive companion's version without forking the umbrella or wrapping it:

```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- The umbrella declares its transitive deps at 0.4.4 -->
    <PackageVersion Include="ToolUp.Cloud.Azure" Version="0.4.4" />

    <!-- Override one inner companion. Later entries win in CPM, so
         this resolves the transitive at 0.5.0-preview1 even though
         the umbrella's nuspec says 0.4.4. -->
    <PackageVersion Include="ToolUp.Storage.AzureBlob" Version="0.5.0-preview1" />
  </ItemGroup>
</Project>
```

```xml
<!-- Consumer's .fsproj -->
<ItemGroup>
  <PackageReference Include="ToolUp.Cloud.Azure" />
</ItemGroup>
```

NuGet's resolver respects the consumer's CPM declaration — the transitive dep `ToolUp.Storage.AzureBlob` resolves to `0.5.0-preview1` even though the umbrella's nuspec declares it at `0.4.4`. No fork, no wrapper, no umbrella rebuild.

The same pattern applies to the vendor exporter packages (`Azure.Monitor.OpenTelemetry.AspNetCore`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`) — override the version in `Directory.Packages.props` and the consumer's restore picks up the new version transitively through the umbrella.

## Why no `ICloudProvider` / cross-cloud abstraction layer

The umbrellas deliberately do **not** introduce a unified `ICloudProvider` / `ICloudFactory` interface over `IBlobStorage` / `ISecretStore` / `IAuditSink`. The design principle is **per-cloud parity, not per-cloud abstraction**.

A cross-cloud abstraction would be the wrong abstraction:
- The seams are deliberately per-cloud — `Azure.Storage.Blobs.BlobClient` is not interchangeable with `AWSSDK.S3.S3Client` at the API level. Pretending otherwise produces lowest-common-denominator interfaces that don't unlock either cloud's advanced features.
- The SDK already has `IBlobStorage` as the abstraction over blob storage; that's where the unification belongs (and already lives, per-companion).
- The umbrella's value-add is purely the **dependency-graph ergonomic** (one PackageReference, fewer lines in `.fsproj`), not a new abstraction layer to learn.

If you find yourself wanting "one client interface to all clouds," you're at the wrong altitude. The right altitude is:
- Application code → `IBlobStorage` / `ISecretStore` / `IAuditSink` (already cloud-agnostic).
- Composition root → per-cloud companion (`AzureBlobStorage.create` / `AwsS3Storage.create` / etc.).

The umbrella sits in the composition-root tier as a packaging convenience, not a new abstraction over the composition root.

## OpenTelemetry exporter selection — `TOOLUP_OTEL_EXPORTER` convention

The umbrellas ship the OpenTelemetry **exporter packages** as transitive deps, but they do **not** register OTel in the SDK's composition root. Per the [OTel default-on audit conclusion](../migrations/09y-opentelemetry-default-on.md), default-on was **DROPPED** as it would violate **GP 13** (zero-cost-when-not-used) — listener attachment flips emission hot paths from no-op to live.

Each consumer wires OTel in their `Program.fs` themselves. The `TOOLUP_OTEL_EXPORTER` env var is a recommended **convention** the consumer's startup code can read to switch between exporters at runtime:

```fsharp
open OpenTelemetry
open OpenTelemetry.Metrics
// Azure umbrella only — supplies `AddAzureMonitorMetricExporter`.
open Azure.Monitor.OpenTelemetry.Exporter

let exporter = System.Environment.GetEnvironmentVariable "TOOLUP_OTEL_EXPORTER"

let meterProvider =
    let builder =
        Sdk.CreateMeterProviderBuilder()
            .AddMeter("ToolUp")

    match exporter with
    | "azure-monitor" ->
        // First-party direct exporter (Azure umbrella only).
        builder.AddAzureMonitorMetricExporter().Build()
    | "cloudwatch"
    | "gcp-operations"
    | "otlp"
    | _ ->
        // Universal OTLP — set OTEL_EXPORTER_OTLP_ENDPOINT
        // to point at your collector.
        builder.AddOtlpExporter().Build()
```

Per-cloud notes:
- **Azure**: `Azure.Monitor.OpenTelemetry.AspNetCore` is the first-party direct exporter — Application Insights without a collector. Alternative: OTLP to Azure Monitor's OTLP endpoint via an OpenTelemetry Collector.
- **AWS**: No first-party direct CloudWatch exporter. AWS's recommended pattern is OTLP-to-collector (AWS Distro for OpenTelemetry — ADOT — handles the CloudWatch export on the collector side).
- **GCP**: No first-party direct Cloud Operations exporter for .NET. GCP's recommended pattern is OTLP-to-collector (the Google Cloud Operations OpenTelemetry Collector handles the Cloud Monitoring / Cloud Trace export).

## Build-time vs. runtime config taxonomy

The umbrellas ship dependencies, not configuration. Two categories of config knob coexist in container-era deployments:

- **Build-time** (baked into the JS bundle at Vite build): Entra tenant / client IDs, AG Grid Enterprise license key, OIDC issuer overrides. Surfaced via `BundleConstants` (Forge SDK) or per-consumer `[<Emit>]` accessors. Re-bake the image to change.
- **Runtime** (read at process start from `TOOLUP_*` env vars): storage credentials, secret-store endpoints, exporter selection (`TOOLUP_OTEL_EXPORTER`), `OTEL_EXPORTER_OTLP_ENDPOINT`. Change the env var, restart the container.

The umbrella's exporter packages live in the runtime tier — the consumer's `Program.fs` reads env vars and dispatches the exporter choice at startup.

See the [Docker hosting](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Hosts/Docker) chapter for the full build-time-vs-runtime taxonomy under containerised deploys.

## Adopting an umbrella

Step-by-step for a fresh consumer:

1. **Add the PackageReference**:

   ```xml
   <ItemGroup>
     <PackageReference Include="ToolUp.Cloud.Azure" />
   </ItemGroup>
   ```

   (Replace `.Azure` with `.Aws` or `.Gcp` per target cloud.)

2. **Add the SDK floor pin to CPM** (already in `Directory.Packages.props` if you use the `ToolUp.Sdk` meta-manifest):

   ```xml
   <PackageVersion Include="ToolUp.Cloud.Azure" Version="$(ToolUpSdkVersion)" />
   ```

3. **Wire the components in your composition root** as you would for the individual companions. See each umbrella's README for the canonical wiring snippet.

4. **(Optional) override a single transitive companion's version** if you need a hotfix / preview ahead of the umbrella's bump. Per the CPM Override Pattern above.

## Adopting an umbrella in an existing consumer (migration)

If you already reference the individual companions:

1. Verify your existing `<PackageReference>` set matches the umbrella's transitive list (see the table at the top).
2. Replace the matching set with one umbrella `<PackageReference>`.
3. Remove the now-redundant CPM `<PackageVersion>` entries (the umbrella's nuspec carries them transitively, so they're no longer needed for the *minimum* surface; keep them only if you want to pin a version different from the umbrella's).
4. `dotnet restore` — output should be identical to the pre-migration restore.

The migration is **opt-in and reversible**. The umbrella is purely an ergonomic packaging convenience — drop it any time without behavioural change.

## See also

- [`ToolUp.Cloud.Azure` README](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Cloud/Azure/README.md) — Azure-specific wiring + exporter notes.
- [`ToolUp.Cloud.Aws` README](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Cloud/Aws/README.md) — AWS / ADOT wiring + exporter notes.
- [`ToolUp.Cloud.Gcp` README](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/Cloud/Gcp/README.md) — GCP / Cloud Operations wiring + exporter notes.
- [`docs/companions/storage-providers.md`](../companions/storage-providers.md) — per-cloud blob-storage companion deep-dive.
- [`docs/migrations/16c-cloud-umbrella-packages.md`](../migrations/16c-cloud-umbrella-packages.md) — umbrella-packages migration guide (consumer adoption).
- [`docs/migrations/09y-opentelemetry-default-on.md`](../migrations/09y-opentelemetry-default-on.md) — OTel-default-on audit + DROPPED decision rationale.

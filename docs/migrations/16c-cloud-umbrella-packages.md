# Phase 16c — `ToolUp.Cloud.{Azure,Aws,Gcp}` umbrella packages + audit-sink parity

## What changes

Three new meta-packages ship at `src/Cloud/{Azure,Aws,Gcp}/`:

- **`ToolUp.Cloud.Azure`** — transitively pulls `ToolUp.Storage.AzureBlob`, `ToolUp.Secrets.AzureKeyVault`, `ToolUp.AuditSinks.AzureBlobArchive`, `ToolUp.Metrics.OpenTelemetry`, plus `Azure.Monitor.OpenTelemetry.AspNetCore` + `OpenTelemetry.Exporter.OpenTelemetryProtocol`.
- **`ToolUp.Cloud.Aws`** — transitively pulls `ToolUp.Storage.AwsS3`, `ToolUp.Secrets.AwsSecretsManager`, `ToolUp.AuditSinks.S3Archive`, `ToolUp.Metrics.OpenTelemetry`, plus `OpenTelemetry.Exporter.OpenTelemetryProtocol`.
- **`ToolUp.Cloud.Gcp`** — transitively pulls `ToolUp.Storage.GoogleCloud`, `ToolUp.Secrets.GcpSecretManager`, `ToolUp.AuditSinks.GcsArchive`, `ToolUp.Metrics.OpenTelemetry`, plus `OpenTelemetry.Exporter.OpenTelemetryProtocol`.

Two new audit-sink companions close the per-cloud parity gap with the existing `ToolUp.AuditSinks.S3Archive`:

- **`ToolUp.AuditSinks.AzureBlobArchive`** at `src/AuditSinks/AzureBlobArchive/`.
- **`ToolUp.AuditSinks.GcsArchive`** at `src/AuditSinks/GcsArchive/`.

Both new audit-sink companions are byte-identical to S3Archive in implementation — they write through the abstract `IBlobStorage` interface (`Upload` only); the cloud-specific WORM mechanism is configured at the container / bucket level by the operator (Blob Immutability Policy / GCS Bucket Lock). Cloud-idiomatic naming + cloud-idiomatic immutability documentation in the per-companion README; no vendor SDK in either companion's dependency graph.

The umbrella adoption is **opt-in and purely additive**. A consumer doing nothing keeps their existing per-cloud `<PackageReference>` set and gets no behavioural change. The migration below describes the optional ergonomic uplift to the umbrella for new or refactoring consumers.

**No abstraction layer introduced over the per-cloud companions** — consumers can drop the umbrella any time and add the inner packages by hand with byte-identical behaviour. See `docs/operations/cloud-umbrella-packages.md` for the "why no `ICloudProvider`" decision.

## Diff to apply

The diff is purely a `.fsproj` / CPM ergonomic refactor — no code change in the consumer's composition root.

### `Directory.Packages.props` (CPM)

Add the umbrella SDK-floor pin (typically already present if the consumer uses the `ToolUp.Sdk` meta-manifest, but explicit pin is required if not):

```xml
<ItemGroup>
+ <PackageVersion Include="ToolUp.Cloud.Azure" Version="$(ToolUpSdkVersion)" />
  <!-- Or .Aws / .Gcp per target cloud -->
</ItemGroup>
```

### Consumer's `Server.fsproj` (or wherever cloud-companion `PackageReference`s live)

Before — five individual companion references:

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.Storage.AzureBlob" />
  <PackageReference Include="ToolUp.Secrets.AzureKeyVault" />
  <PackageReference Include="ToolUp.AuditSinks.AzureBlobArchive" />
  <PackageReference Include="ToolUp.Metrics.OpenTelemetry" />
  <PackageReference Include="Azure.Monitor.OpenTelemetry.AspNetCore" />
  <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
</ItemGroup>
```

After — one umbrella reference:

```xml
<ItemGroup>
- <PackageReference Include="ToolUp.Storage.AzureBlob" />
- <PackageReference Include="ToolUp.Secrets.AzureKeyVault" />
- <PackageReference Include="ToolUp.AuditSinks.AzureBlobArchive" />
- <PackageReference Include="ToolUp.Metrics.OpenTelemetry" />
- <PackageReference Include="Azure.Monitor.OpenTelemetry.AspNetCore" />
- <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
+ <PackageReference Include="ToolUp.Cloud.Azure" />
</ItemGroup>
```

### Consumer's composition root

**No change required.** The umbrella ships transitive deps, not new wiring. The consumer continues to call `AzureBlobStorage.create`, `AzureKeyVaultSecretStore.create`, `AzureBlobArchive.create`, `OtelMetricsSink.create`, etc. exactly as they did with the individual packages.

If you want to read the new `TOOLUP_OTEL_EXPORTER` convention to switch between exporters:

```fsharp
let exporter = System.Environment.GetEnvironmentVariable "TOOLUP_OTEL_EXPORTER"

let meterProvider =
    let builder =
        Sdk.CreateMeterProviderBuilder()
            .AddMeter("ToolUp")

    match exporter with
    | "azure-monitor" -> builder.AddAzureMonitorMetricExporter().Build()
    | _ -> builder.AddOtlpExporter().Build()
```

This is an *optional* convention — the umbrella does not register OTel itself (per Phase 9y, OTel default-on was DROPPED). See [`docs/operations/cloud-umbrella-packages.md`](../operations/cloud-umbrella-packages.md) for the full envvar dispatch story.

### Adopting one of the new audit-sink companions (separate path; no umbrella required)

If you target Azure / GCP and want the cloud-idiomatic audit-sink companion without adopting the full umbrella:

```xml
<ItemGroup>
+ <PackageReference Include="ToolUp.AuditSinks.AzureBlobArchive" />
  <!-- Or .GcsArchive for GCP -->
</ItemGroup>
```

And in the composition root, replace `S3Archive.create` with `AzureBlobArchive.create` / `GcsArchive.create`:

```fsharp
- open ToolUp.Platform.AuditSinks.S3Archive
+ open ToolUp.Platform.AuditSinks.AzureBlobArchive

  let auditSink =
-     S3Archive.create
+     AzureBlobArchive.create
          "azure-prod-audit"
          { Container = "acme-audit-prod"; PathPrefix = Some "v1" }
          blobStorage
```

The settings record (`AzureBlobArchiveSettings` / `GcsArchiveSettings`) is the same shape as `S3ArchiveSettings` — `Container` + optional `PathPrefix`. No behavioural difference; this is a naming + documentation refresh.

## Verification steps

After applying the diff above:

1. **`dotnet restore`** — confirms the umbrella's transitive graph resolves cleanly against the configured feed. NU1902 advisories should not appear (the umbrella's exporter pins clear the known issues on `OpenTelemetry.Api 1.9.x` / 1.10.x / 1.12.x).
2. **`dotnet build`** — confirms no breakage in the consumer's composition root. Since the umbrella is pure transitive deps, this is a no-op compile compared to the pre-migration build.
3. **`dotnet pack` (if the consumer is itself a packable SDK companion)** — the consumer's nuspec should list the umbrella as a dependency at the umbrella's pin version, NOT the five inner packages individually.
4. **Startup log diff** — byte-for-byte identical to the pre-migration startup. The umbrella adds no SDK-side wiring; the same `ServerApp.with*` calls run in the same order.
5. **CPM override smoke test** — declare an override on one transitive companion (e.g. `<PackageVersion Include="ToolUp.Storage.AzureBlob" Version="0.5.0-preview1" />`) and confirm `dotnet restore` resolves the transitive at the override version, not the umbrella's pinned version. This validates the CPM override pattern documented in `docs/operations/cloud-umbrella-packages.md`.

## Rollback

Drop the umbrella `<PackageReference>` and restore the five inner package references:

```xml
- <PackageReference Include="ToolUp.Cloud.Azure" />
+ <PackageReference Include="ToolUp.Storage.AzureBlob" />
+ <PackageReference Include="ToolUp.Secrets.AzureKeyVault" />
+ <PackageReference Include="ToolUp.AuditSinks.AzureBlobArchive" />
+ <PackageReference Include="ToolUp.Metrics.OpenTelemetry" />
+ <PackageReference Include="Azure.Monitor.OpenTelemetry.AspNetCore" />
+ <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
```

`dotnet restore` after the rollback resolves the identical transitive graph. No code change, no data migration. The umbrella is a packaging convenience — its presence or absence has zero behavioural impact on the running consumer.

If you also adopted one of the new audit-sink companions (`AzureBlobArchive` / `GcsArchive`) and want to roll back to `S3Archive`, swap the `open` + `create` call back. The data written to-date stays in the cloud container; the new sink writes to a (possibly) different container per the configured settings — operator-level decision whether to copy or fork the historical archive.

## Risks

- **Vendor exporter version drift between umbrellas and the SDK floor.** The umbrellas pin `OpenTelemetry.Exporter.OpenTelemetryProtocol 1.15.3` + `Azure.Monitor.OpenTelemetry.AspNetCore 1.2.0`. Consumers using the `ToolUp.Sdk` meta-manifest for SDK pinning + the umbrella for cloud pinning will pick up new exporter versions whenever the umbrella bumps, independently of their `ToolUpSdkVersion` lift. Operators wanting tight reproducibility should override the exporter versions explicitly in `Directory.Packages.props` (per the CPM override pattern).
- **NU1902 advisories on the exporter line.** OpenTelemetry's .NET SDK had a sequence of moderate-severity vulnerabilities on the 1.9.x → 1.12.x line (DoS in OTLP exporter; advisory chain GHSA-g94r-2vxg-569j / GHSA-4625-4j76-fww9 / GHSA-8785-wc3w-h8q6). The umbrella's 1.15.3 pin clears these. Consumers who override the exporter version downward via CPM may re-introduce one or more of these advisories — `dotnet restore` will flag NU1902.
- **Azure Monitor exporter is Azure-only.** `Azure.Monitor.OpenTelemetry.AspNetCore` is shipped only in `ToolUp.Cloud.Azure`. The AWS / GCP umbrellas can't activate the Azure Monitor exporter even if `TOOLUP_OTEL_EXPORTER=azure-monitor` is set; the consumer's startup code falls through to the default branch. This is intentional — the umbrella's scope is per-cloud, not cross-cloud.
- **Umbrella + Hosts companion separation.** The umbrellas do NOT pull `ToolUp.Platform.Hosts.Docker` (or any `Hosts.*` companion). Host adapter choice is a deployment-shape decision orthogonal to cloud choice — Azure-targeting containers + Azure Functions + App Service Linux container mode all use the same `ToolUp.Cloud.Azure` umbrella. See [Phase 16b](./16b-docker-host-companion.md) for the container host companion.
- **No abstraction over per-cloud seams.** The umbrella is purely ergonomic packaging. Consumers wanting "one client interface to all clouds" are at the wrong altitude — that's what `IBlobStorage` / `ISecretStore` / `IAuditSink` (cloud-agnostic) already provide at the application code level. The "per-cloud parity, not per-cloud abstraction" choice is deliberate.
- **Per-umbrella audit sink is `IBlobStorage`-abstracted, not cloud-SDK-native.** The new `AzureBlobArchive` / `GcsArchive` companions write through the abstract `IBlobStorage`, not via direct calls into `Azure.Storage.Blobs.AppendBlobClient` / `Google.Cloud.Storage.V1.UploadObject`. This matches the S3Archive design (single `Upload` method on the abstraction). Consumers wanting append-blob semantics or vendor-specific features (e.g. server-side encryption keys, custom blob metadata) should implement their own `IAuditSink` directly against the vendor SDK — the umbrella is for the common case.

# Azure Blob Archive audit sink

Phase 16c `IAuditSink` companion. Mirrors every audit event the SDK emits to a deploying-org-controlled Azure Blob container (or any `IBlobStorage`-compatible destination) as gzipped JSONL files. Cloud-idiomatic counterpart to [`ToolUp.AuditSinks.S3Archive`](../S3Archive/README.md) for deployments targeting Azure.

## Why "Azure Blob Archive" specifically

Azure Blob Storage with a **Blob Immutability Policy** (time-based retention or legal hold) provides compliance-grade WORM (Write-Once-Read-Many) semantics — once written, blobs cannot be modified or deleted until their retention period expires. The sink itself does not configure the immutability policy; that's a container-level deployment concern. The sink writes; the container enforces.

This companion is functionally identical to `ToolUp.AuditSinks.S3Archive` — both write through the abstract `IBlobStorage` interface. The cloud-specific name + defaults + this README's immutability documentation give Azure-targeting deployments an idiomatic reference; the implementation is the same gzipped-JSONL-per-batch shape.

## How to enable

1. Add a `<ProjectReference>` (or a `<PackageReference>` if you're pulling the published nupkg) so MSBuild builds it alongside the consuming project:

   ```xml
   <PackageReference Include="ToolUp.AuditSinks.AzureBlobArchive" />
   ```

2. Construct the sink in the composition root and register it via `ServerApp.withAuditSink`:

   ```fsharp skip=fragment
   open ToolUp.Platform.AuditSinks.AzureBlobArchive

   let auditSink =
       AzureBlobArchive.create
           "azure-prod-audit"   // stable deployment-unique name
           { Container = "acme-audit-prod"; PathPrefix = Some "v1" }
           blobStorage          // the deployment's IBlobStorage instance (typically AzureBlobStorage)

   ServerApp.empty
   |> ServerApp.withConfig config
   |> ServerApp.withStorage blobStorage
   |> ServerApp.withAuditSink auditSink
   |> ServerApp.run
   ```

3. (Production only.) Configure the destination container with a Blob Immutability Policy:

   ```bash
   # Time-based retention — 7 years, locked (cannot be reduced).
   az storage container immutability-policy create \
     --account-name acmeauditprod \
     --container-name acme-audit-prod \
     --period 2555 \
     --allow-protected-append-writes true

   az storage container immutability-policy lock \
     --account-name acmeauditprod \
     --container-name acme-audit-prod
   ```

   Or in Bicep / ARM, set the container's `immutableStorageWithVersioning.enabled = true` and the appropriate `defaultEncryptionScope` / `immutabilityPolicy`. See Microsoft Learn for the full set of options (`legalHolds`, version-level policies, etc.).

## Archive layout

One blob per delivered batch:

```
{PathPrefix}/{yyyy-MM-dd}/{HH-mm-ss-fffffff}-{sinkName}-{batchUuid}.jsonl.gz
```

Example: `v1/2026-05-05/14-23-45-1234567-azure-prod-audit-9b2e7c5a4d3f6e8ba1c2d3e4f5a6b7c8.jsonl.gz`

- **Date bucket** — leading `yyyy-MM-dd` segment. Auditors run date-range queries against the prefix; Azure Blob's list-blobs by prefix is O(1) per matching shard.
- **High-resolution timestamp** — `HH-mm-ss-fffffff` (ticks of a second). Multiple batches per second are routine under load.
- **Sink name** — middle segment lets one container host archives from multiple sinks without collision.
- **Batch UUID** — guaranteed unique even if the timestamp resolution is exhausted.

## File contents

Gzipped JSONL: one `AuditEvent` per line, JSON-serialised via `FableJsonConverter` (the SDK's canonical converter). LF line separator. Each line is independently parseable — auditors can `az storage blob download ... | gunzip -c | jq -c '.'` or feed directly to Azure Data Explorer / Synapse's GZIP-aware JSON parsers.

```json
{"Case":"UserLoggedIn","Fields":[{"UserId":"u123","AuthProvider":"Header"}]}
{"Case":"FileUploaded","Fields":[{"UserId":"u123","TeamId":"t-acme","FileName":"sales.csv","FileSize":12345}]}
```

## Idempotency on retry

The dispatcher retries the entire batch on `Result.Error`. Each retry generates a NEW `batchUuid`, so a retried batch lands as a new blob — duplicates are idempotent at the *audit-trail* level (the same events appear twice with different timestamps), not at the *blob* level. Auditors querying by `event.Id` will see exact-match deduplication on the wire-format `Id` field; querying by blob count will overcount.

## Single-instance limitation

Same as S3Archive: the replicator's bounded channel and per-scope semaphores are in-process. Multiple silos running the same replicator + sink configuration will each consume the post-write hook and **double-deliver** every batch. Deployments running the SDK in multi-instance configurations should serialise the audit replicator to a single elected leader at the orchestrator level (e.g., Azure App Service with `Always On = true` on a single instance, or a Kubernetes Deployment with `replicas: 1` for the audit-emitting tier) until the `IDistributedLock`-backed distributed companion ships.

## Local development

Wire `LocalFileStorage` instead of `AzureBlobStorage` for dev:

```fsharp skip=fragment
let blobStorage = LocalFileStorage "/var/lib/myapp/storage" :> IBlobStorage

let auditSink =
    AzureBlobArchive.create
        "local-dev"
        { Container = "audit-archive"; PathPrefix = None }
        blobStorage
```

Archived batches land at `/var/lib/myapp/storage/audit-archive/2026-05-05/...jsonl.gz` — inspect them with `gunzip -c | jq` like a real container.

## See also

- [`ToolUp.AuditSinks.S3Archive`](../S3Archive/README.md) — sibling AWS-flavoured audit-sink companion (same implementation, S3 Object Lock WORM mechanism).
- [`ToolUp.AuditSinks.GcsArchive`](../GcsArchive/README.md) — sibling GCP-flavoured audit-sink companion (same implementation, GCS retention policy WORM mechanism).
- [`ToolUp.Cloud.Azure`](../../Cloud/Azure/README.md) — Azure umbrella package that transitively pulls this sink alongside the rest of the Azure companion set.

# GCS Archive audit sink

Phase 16c `IAuditSink` companion. Mirrors every audit event the SDK emits to a deploying-org-controlled Google Cloud Storage bucket (or any `IBlobStorage`-compatible destination) as gzipped JSONL files. Cloud-idiomatic counterpart to [`ToolUp.AuditSinks.S3Archive`](../S3Archive/README.md) for deployments targeting GCP.

## Why "GCS Archive" specifically

Google Cloud Storage with a **bucket-level Retention Policy** provides compliance-grade WORM (Write-Once-Read-Many) semantics — once written, objects cannot be modified or deleted until their retention period expires. Combine with **Bucket Lock** to make the policy permanent (irreversibly enforced). The sink itself does not configure the retention policy; that's a bucket-level deployment concern. The sink writes; the bucket enforces.

This companion is functionally identical to `ToolUp.AuditSinks.S3Archive` — both write through the abstract `IBlobStorage` interface. The cloud-specific name + defaults + this README's immutability documentation give GCP-targeting deployments an idiomatic reference; the implementation is the same gzipped-JSONL-per-batch shape.

## How to enable

1. Add a `<PackageReference>`:

   ```xml
   <PackageReference Include="ToolUp.AuditSinks.GcsArchive" />
   ```

2. Construct the sink in the composition root and register it via `ServerApp.withAuditSink`:

   ```fsharp
   open ToolUp.Platform.AuditSinks.GcsArchive

   let auditSink =
       GcsArchive.create
           "gcs-prod-audit"   // stable deployment-unique name
           { Container = "acme-audit-prod"; PathPrefix = Some "v1" }
           blobStorage        // the deployment's IBlobStorage instance (typically GoogleCloudStorage)

   ServerApp.empty
   |> ServerApp.withConfig config
   |> ServerApp.withStorage blobStorage
   |> ServerApp.withAuditSink auditSink
   |> ServerApp.run
   ```

3. (Production only.) Configure the destination bucket with a Retention Policy + Bucket Lock:

   ```bash
   # 7-year retention; convert to seconds (7 × 365.25 × 86400 ≈ 220924800).
   gcloud storage buckets update gs://acme-audit-prod \
     --retention-period=220924800

   # Lock the policy — this is IRREVERSIBLE.
   gcloud storage buckets update gs://acme-audit-prod --lock-retention-period
   ```

   Or via Terraform: `google_storage_bucket.retention_policy { retention_period = 220924800; is_locked = true }`.

   See [Google Cloud Storage: Retention policies and Bucket Lock](https://cloud.google.com/storage/docs/bucket-lock) for the canonical reference.

## Archive layout

One blob per delivered batch:

```
{PathPrefix}/{yyyy-MM-dd}/{HH-mm-ss-fffffff}-{sinkName}-{batchUuid}.jsonl.gz
```

Example: `v1/2026-05-05/14-23-45-1234567-gcs-prod-audit-9b2e7c5a4d3f6e8ba1c2d3e4f5a6b7c8.jsonl.gz`

- **Date bucket** — leading `yyyy-MM-dd` segment. Auditors run date-range queries against the prefix; GCS list-objects by prefix is O(1) per matching shard.
- **High-resolution timestamp** — `HH-mm-ss-fffffff` (ticks of a second). Multiple batches per second are routine under load.
- **Sink name** — middle segment lets one bucket host archives from multiple sinks without collision.
- **Batch UUID** — guaranteed unique even if the timestamp resolution is exhausted.

## File contents

Gzipped JSONL: one `AuditEvent` per line, JSON-serialised via `FableJsonConverter` (the SDK's canonical converter). LF line separator. Each line is independently parseable — auditors can `gcloud storage cp gs://... - | gunzip -c | jq -c '.'` or feed directly to BigQuery's GZIP-aware JSON parsers via an external table over GCS.

```json
{"Case":"UserLoggedIn","Fields":[{"UserId":"u123","AuthProvider":"Header"}]}
{"Case":"FileUploaded","Fields":[{"UserId":"u123","TeamId":"t-acme","FileName":"sales.csv","FileSize":12345}]}
```

## Idempotency on retry

The dispatcher retries the entire batch on `Result.Error`. Each retry generates a NEW `batchUuid`, so a retried batch lands as a new object — duplicates are idempotent at the *audit-trail* level (the same events appear twice with different timestamps), not at the *object* level. Auditors querying by `event.Id` will see exact-match deduplication on the wire-format `Id` field; querying by object count will overcount.

## Single-instance limitation

Same as S3Archive: the replicator's bounded channel and per-scope semaphores are in-process. Multiple silos running the same replicator + sink configuration will each consume the post-write hook and **double-deliver** every batch. Deployments running the SDK in multi-instance configurations should serialise the audit replicator to a single elected leader at the orchestrator level (e.g., a Cloud Run min-instances=1 service with concurrency control, or a GKE Deployment with `replicas: 1` for the audit-emitting tier) until the `IDistributedLock`-backed distributed companion ships.

## Local development

Wire `LocalFileStorage` instead of `GoogleCloudStorage` for dev:

```fsharp
let blobStorage = LocalFileStorage "/var/lib/myapp/storage" :> IBlobStorage

let auditSink =
    GcsArchive.create
        "local-dev"
        { Container = "audit-archive"; PathPrefix = None }
        blobStorage
```

Archived batches land at `/var/lib/myapp/storage/audit-archive/2026-05-05/...jsonl.gz` — inspect them with `gunzip -c | jq` like a real bucket.

## See also

- [`ToolUp.AuditSinks.S3Archive`](../S3Archive/README.md) — sibling AWS-flavoured audit-sink companion (same implementation, S3 Object Lock WORM mechanism).
- [`ToolUp.AuditSinks.AzureBlobArchive`](../AzureBlobArchive/README.md) — sibling Azure-flavoured audit-sink companion (same implementation, Blob Immutability Policy WORM mechanism).
- [`ToolUp.Cloud.Gcp`](../../Cloud/Gcp/README.md) — GCP umbrella package that transitively pulls this sink alongside the rest of the GCP companion set.

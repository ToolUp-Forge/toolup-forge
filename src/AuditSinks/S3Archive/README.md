# S3 Archive audit sink

Phase 9g `IAuditSink` companion. Mirrors every audit event the SDK emits to a deploying-org-controlled S3 bucket (or any `IBlobStorage`-compatible destination) as gzipped JSONL files. Designed for SOC 2 / HIPAA / GDPR Article 30 / SOX-style compliance archives where the auditor requires a sink the deploying organisation does not control with the same credentials as the application.

## Why "S3 Archive" specifically

S3 (or any cloud blob store) with **Object Lock** enabled at the bucket level provides compliance-grade WORM (Write-Once-Read-Many) semantics — once written, blobs cannot be modified or deleted until their retention period expires. The sink itself does not configure Object Lock; that's a bucket-level deployment concern. The sink writes; the bucket enforces.

This is the **no-paid-deps default** of the three reference companions Phase 9g ships. The sink writes through the abstract `IBlobStorage` interface (a single `Upload` call per batch); deployments wire the matching `IBlobStorage` companion (`AwsS3Storage`, `AzureBlobStorage`, `GoogleCloudStorage`, or `LocalFileStorage` for dev) without touching this companion. No vendor SDK lives in this companion's `paket.references`.

## How to enable

1. Reference this companion's `Server.props` in the consuming server project's `.fsproj`:

   ```xml
   <Import Project="..\AuditSinks\S3Archive\S3ArchiveAuditSink.Server.props" />
   ```

2. Add a `<ProjectReference>` to the companion's `.fsproj` so MSBuild builds it alongside the consuming project (see existing `src/NotificationChannels/Email/Smtp/` for the exact pattern).

3. Construct the sink in the composition root and register it via `ServerApp.withAuditSink`:

   ```fsharp
   open ToolUp.Platform.AuditSinks.S3Archive

   let auditSink =
       S3Archive.create
           "s3-prod-audit"   // stable deployment-unique name
           { Container = "acme-audit-prod"; PathPrefix = Some "v1" }
           blobStorage       // the deployment's IBlobStorage instance

   ServerApp.empty
   |> ServerApp.withConfig config
   |> ServerApp.withStorage blobStorage
   |> ServerApp.withAuditSink auditSink
   |> ServerApp.run
   ```

4. (Production only.) Configure the destination bucket / container with the appropriate immutability + retention policy:
   - **AWS S3**: `aws s3api put-object-lock-configuration --bucket acme-audit-prod --object-lock-configuration '{"ObjectLockEnabled":"Enabled","Rule":{"DefaultRetention":{"Mode":"COMPLIANCE","Years":7}}}'`
   - **Azure Blob**: enable immutable storage with a time-based retention policy via the portal or `az storage blob immutability-policy`.
   - **GCS**: set a bucket-level retention policy via `gsutil retention set`.

## Archive layout

One blob per delivered batch:

```
{PathPrefix}/{yyyy-MM-dd}/{HH-mm-ss-fffffff}-{sinkName}-{batchUuid}.jsonl.gz
```

Example: `v1/2026-05-05/14-23-45-1234567-s3-prod-audit-9b2e7c5a4d3f6e8ba1c2d3e4f5a6b7c8.jsonl.gz`

- **Date bucket** — leading `yyyy-MM-dd` segment. Auditors run date-range queries against the prefix; cloud SDKs list-objects by prefix in O(1) per matching shard rather than scanning the bucket.
- **High-resolution timestamp** — `HH-mm-ss-fffffff` (ticks of a second). Multiple batches per second are routine under load; the timestamp makes blob names lexicographically chronological for date-range scans.
- **Sink name** — middle segment lets one bucket host archives from multiple sinks without collision.
- **Batch UUID** — guaranteed unique even if the timestamp resolution is exhausted.

## File contents

Gzipped JSONL: one `AuditEvent` per line, JSON-serialised via `FableJsonConverter` (the SDK's canonical converter). LF line separator. Each line is independently parseable — auditors can `gunzip -c file.jsonl.gz | jq -c '.'` or feed directly to AWS Athena's GZIP-aware JSON parser.

```json
{"Case":"UserLoggedIn","Fields":[{"UserId":"u123","AuthProvider":"Header"}]}
{"Case":"FileUploaded","Fields":[{"UserId":"u123","TeamId":"t-acme","FileName":"sales.csv","FileSize":12345}]}
```

## Idempotency on retry

The dispatcher retries the entire batch on `Result.Error`. Each retry generates a NEW `batchUuid`, so a retried batch lands as a new blob — duplicates are idempotent at the *audit-trail* level (the same events appear twice with different timestamps), not at the *blob* level. Auditors querying by `event.Id` will see exact-match deduplication on the wire-format `Id` field; querying by blob count will overcount.

For deployments where strict at-most-once delivery to S3 matters more than at-least-once, configure the bucket with append-only semantics + lifecycle rules to dedupe by content hash. The sink itself doesn't dedupe — by design, it's a thin transport.

## Single-instance limitation

The replicator's bounded channel and per-scope semaphores are in-process. Multiple silos running the same replicator + sink configuration will each consume the post-write hook and **double-deliver** every batch. The cursor write is monotonic so the duplication window is bounded, but at-most-once is not guaranteed for distributed deployments.

The Phase 9c half 2 distributed companion (planned, gated on multi-node testing infrastructure) will resolve this via the `IDistributedLock` (Phase 9i) leader election — only one replicator instance writes to the sink at a time. Until then, deployments running the SDK in multi-instance configurations should serialise the audit replicator to a single elected leader at the orchestrator level (e.g., a Kubernetes Deployment with `replicas: 1` for the audit-emitting tier).

## Local development

Wire `LocalFileStorage` instead of `AwsS3Storage` for dev:

```fsharp
let blobStorage = LocalFileStorage "/var/lib/myapp/storage" :> IBlobStorage

let auditSink =
    S3Archive.create
        "local-dev"
        { Container = "audit-archive"; PathPrefix = None }
        blobStorage
```

Archived batches land at `/var/lib/myapp/storage/audit-archive/2026-05-05/...jsonl.gz` — inspect them with `gunzip -c | jq` like a real bucket.

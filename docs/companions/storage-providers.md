# Storage provider companions

The Platform's `IBlobStorage` interface abstracts persistent blob storage. The shipped default (`LocalFileStorage`) is disk-backed for dev. Production deployments swap in a cloud companion against the same interface.

This page is a cross-cutting overview of the shipped storage companions. For full details on the `IBlobStorage` contract, encryption-at-rest decorator, data-object versioning, and data catalog, see [`platform/storage.md`](../platform/storage.md).

## What's shipped

| Companion | Description |
|---|---|
| `LocalFileStorage` (built into `ToolUp.Platform.Server`) | Disk-backed; writes to `./data/` by default. Dev / single-instance only. |
| `ToolUp.Storage.AwsS3` | AWS S3 bucket. Versioning + SSE-S3 / SSE-KMS + Object Lock (WORM). |
| `ToolUp.Storage.AzureBlob` | Azure Blob Storage container. Versioning + service-side encryption + immutability policies. |
| `ToolUp.Storage.GoogleCloud` | GCS bucket. Object versioning + CMEK + retention policies. |

All implement the same `IBlobStorage` interface. The choice is operational (where do your blobs live? what's your cloud?), not architectural.

## Picking a backend

### `LocalFileStorage` (dev / CI / single-instance)

Use when:
- Local development.
- CI test runs.
- Single-instance deployments where data lives on a single machine's disk (rare for production).

Don't use when:
- Multi-instance deployments — `LocalFileStorage` is not multi-process safe; two app nodes pointing at the same `data/` directory race on writes.
- Production where data durability matters — disk is a single point of failure.

Setup:

```fsharp skip=fragment
// Default — no withStorage call needed
ServerApp.empty
|> ServerApp.withConfig config
|> ...
|> ServerApp.run

// Explicit
let storage = LocalFileStorage("./data") :> IBlobStorage
ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withStorage storage
|> ...
```

`LocalFileStorageEncryptionAtRestValidator` emits a `Warning` when local storage is configured without the encryption-at-rest decorator — flags that disk encryption is your responsibility (OS-level, not SDK-level).

### `ToolUp.Storage.AwsS3` (AWS)

Use when:
- Deployment runs on AWS, on a service that can authenticate to S3 (EC2 with instance role, ECS with task role, Lambda, etc.).
- Long-term archival to S3 Glacier / Glacier Deep Archive via lifecycle rules.
- Compliance-grade WORM via Object Lock (used by the audit-replication subsystem).

Setup:

```fsharp skip=fragment
open ToolUp.Storage.AwsS3

let storage =
    AwsS3Storage.create {
        BucketName = "my-app-data"
        Region = "eu-west-2"
        Prefix = Some "tenant-blobs/"   // optional path prefix in the bucket
    } :> IBlobStorage

ServerApp.empty
|> ...
|> ServerApp.withStorage storage
|> ...
```

Configuration via standard AWS SDK resolution: env vars (`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_REGION`), `~/.aws/credentials` profile, or IMDS on EC2 / ECS task roles.

`AwsS3EncryptionAtRestValidator` calls `GetBucketEncryption` at preflight; emits `Warning` if bucket encryption isn't enabled. `AwsS3StorageHealth` probe verifies HEAD on the bucket.

### `ToolUp.Storage.AzureBlob` (Azure Blob)

Use when:
- Deployment runs on Azure, authenticating via Managed Identity / Service Principal / Workload Identity.
- Azure-native deployment topology.

Setup:

`ToolUp.Storage.AzureBlobStorage` is the module itself, so `create` is called unqualified after the open, and it already returns `IBlobStorage`:

```fsharp skip=fragment
open ToolUp.Storage.AzureBlobStorage

let storage =
    create {
        AccountName = "myappdata"
        ContainerName = "tenant-blobs"
        // Authentication via DefaultAzureCredential (env vars, managed identity, etc.)
    }
```

`AzureBlobEncryptionAtRestValidator` calls `GetServiceProperties`; emits `Warning` if encryption isn't enabled.

### `ToolUp.Storage.GoogleCloud` (GCP)

Use when:
- Deployment runs on GCP, authenticating via application default credentials.
- GCP-native deployment topology.

Setup:

```fsharp skip=fragment
open ToolUp.Storage.GoogleCloudStorage

let storage =
    create {
        ProjectId = "my-gcp-project"
        BucketName = "my-app-data"
    }
```

`GcsEncryptionAtRestValidator` calls `GetBucket`; emits `Warning` if encryption isn't enabled. CMEK (customer-managed encryption keys) supported via bucket configuration.

## Container conventions

The SDK uses container names for tenant isolation:

| Container | Scope |
|---|---|
| `_platform` | SDK-owned state (team memberships, encryption keys, audit-sink cursors, etc.) |
| `team-{teamId}` | Per-team data (Team / MultiTeam mode) |
| `user-{userId}` | Per-user data (Individual mode) |
| `session-{guid}` | Per-session data (Anonymous / Ephemeral mode) |

Module code never writes to `_platform` directly. Other containers are accessed via the resolved `StorageScope` (per-request).

### Cloud provider mapping

- **S3**: containers map to prefixes within one bucket (`s3://my-bucket/team-acme/...`). Object keys are the full path including container.
- **Azure Blob**: containers map to Azure blob containers (1:1). Object names are the relative path.
- **GCS**: containers map to prefixes within one bucket (similar to S3).

All providers expose `Save` / `Load` / `Delete` / `List` / `Exists` over the unified `IBlobStorage` interface. Internally, each translates the SDK's `container` + `objectId` arguments to the underlying provider's storage path.

## Encryption-at-rest decorator

The `EncryptedBlobStorage` decorator wraps any `IBlobStorage` and applies AES-GCM envelope encryption transparently. Layer it on top of the cloud companion for application-tier crypto:

```fsharp skip=fragment
let resolver = PerScopeKeyResolver(secretStore, blobStorage) :> IBlobEncryptionKeyResolver

ServerApp.empty
|> ...
|> ServerApp.withStorage cloudStorage
|> ServerApp.withEncryptedBlobStorage resolver
|> ...
```

Use cases:
- Cloud provider's encryption is opaque KMS — you want application-level keys so the cloud provider can't decrypt at-rest data.
- Per-tenant key destruction (crypto-shred) for GDPR / contract termination.
- Bucket in a region where customer-managed keys are unavailable.

See [`platform/storage.md`](../platform/storage.md) for the full key-resolver model + admin endpoint for destroying keys.

## Writing a custom provider

For a backend not covered (Cloudflare R2, MinIO, S3-compatible object store, etc.):

```fsharp skip=fragment
type MinioStorage(client: AmazonS3Client, bucket: string) =
    interface IBlobStorage with
        member _.Save(container, objectId, contents) = async {
            let key = $"{container}/{objectId}"
            let request =
                PutObjectRequest(
                    BucketName = bucket,
                    Key = key,
                    InputStream = new MemoryStream(contents))
            let! _ = client.PutObjectAsync(request) |> Async.AwaitTask
            return ()
        }
        member _.Load(container, objectId) = async {
            let key = $"{container}/{objectId}"
            try
                let request = GetObjectRequest(BucketName = bucket, Key = key)
                use! response = client.GetObjectAsync(request) |> Async.AwaitTask
                use ms = new MemoryStream()
                do! response.ResponseStream.CopyToAsync(ms) |> Async.AwaitTask
                return Some (ms.ToArray())
            with
            | :? AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.NotFound ->
                return None
        }
        // ... other members
```

Wire:

```fsharp skip=fragment
ServerApp.empty
|> ...
|> ServerApp.withStorage (MinioStorage(client, bucket) :> IBlobStorage)
|> ...
```

Author an `IHealthCheck` probe + an `IConfigValidator` for preflight verification.

S3-compatible providers (MinIO, Cloudflare R2, Wasabi, Backblaze B2) reuse the AWS SDK — point the SDK at the alternate endpoint via the `ServiceURL` configuration. Often `ToolUp.Storage.AwsS3` works against them with minor config changes.

## Migration between backends

To migrate from one backend to another (e.g. LocalFileStorage → AwsS3):

1. Wire the new storage via `withStorage`.
2. Write a one-off migration script that walks the old storage's containers + objects and copies each to the new storage.
3. Verify counts + sample contents.
4. Cut over by deploying the new wiring.

The SDK doesn't ship a migration tool — backends are stable enough that migration is rare. For complex migrations (re-keying encryption envelopes, re-organising containers), write the script against `IBlobStorage` directly.

## Hardening checklist for production

- Cloud companion appropriate to the deployment (AwsS3 / Azure / GoogleCloud).
- Bucket encryption at rest enabled at the cloud level. The `*EncryptionAtRestValidator` probes confirm this.
- Versioning enabled at the bucket level — protects against accidental delete / overwrite. The SDK's `IDataObjectStore` versioning is application-tier; bucket-level versioning is cloud-tier.
- Object Lock (or Azure immutability / GCS retention policies) for compliance archives.
- IAM / RBAC scoped tightly — the app's role can read/write the bucket; nothing more.
- Cross-region replication (where supported) for disaster recovery.
- Lifecycle rules for cold-tier archival of old data (S3 Glacier, Azure cool tier, GCS Coldline).
- `EncryptedBlobStorage` decorator with `PerScopeKeyResolver` for crypto-shred capability.

For multi-tenant deployments where compliance requires per-tenant key destruction, the `EncryptedBlobStorage` + `PerScopeKeyResolver` combination is mandatory. Cloud-tier deletion alone is not crypto-shred (replicas and backups may persist).

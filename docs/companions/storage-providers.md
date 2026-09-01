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

> **The package id is not the module path.** The table names the **NuGet package** (`ToolUp.Storage.AwsS3`); the **F# module you `open`** is `ToolUp.Storage.AwsS3Storage` — likewise `ToolUp.Storage.AzureBlob` → `…AzureBlobStorage` and `ToolUp.Storage.GoogleCloud` → `…GoogleCloudStorage`. Take the `open` from the code block, never from the table.

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

```fsharp
// Default — no withStorage call needed
ServerApp.empty
|> ServerApp.withConfig config
// ... the deployment's other ServerApp.with* calls ...
|> ServerApp.run

// Explicit. `LocalFileStorage` is a top-level MODULE containing a type of
// the same name, so the constructor is reached through the module.
let storage = LocalFileStorage.LocalFileStorage("./data") :> IBlobStorage

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withStorage storage
// ... the deployment's other ServerApp.with* calls ...
|> ServerApp.run
```

`LocalFileStorageEncryptionAtRestValidator` emits a `Warning` when local storage is configured without the encryption-at-rest decorator — flags that disk encryption is your responsibility (OS-level, not SDK-level).

### `ToolUp.Storage.AwsS3` (AWS)

Use when:
- Deployment runs on AWS, on a service that can authenticate to S3 (EC2 with instance role, ECS with task role, Lambda, etc.).
- Long-term archival to S3 Glacier / Glacier Deep Archive via lifecycle rules.
- Compliance-grade WORM via Object Lock (used by the audit-replication subsystem).

Setup:

`ToolUp.Storage.AwsS3Storage` is the module itself, so `create` is called unqualified after the open, and it already returns `IBlobStorage`:

```fsharp
open ToolUp.Storage.AwsS3Storage

let storage =
    create {
        BucketName = "my-app-data"
        Region = "eu-west-2"
        // `None` uses the region's default AWS endpoint. `Some url` points
        // at an S3-compatible store (MinIO, Cloudflare R2, Backblaze B2)
        // and switches the client to path-style addressing.
        EndpointUrl = None
    }

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withStorage storage
// ... the deployment's other ServerApp.with* calls ...
|> ServerApp.run
```

There is no per-deployment key prefix knob: ToolUp's logical containers are *themselves* the S3 key prefix (`{container}/{blobName}`), so one bucket holds every scope. See [Cloud provider mapping](#cloud-provider-mapping) below.

Configuration via standard AWS SDK resolution: env vars (`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_REGION`), `~/.aws/credentials` profile, or IMDS on EC2 / ECS task roles.

`AwsS3EncryptionAtRestValidator` calls `GetBucketEncryption` at preflight; emits `Warning` if bucket encryption isn't enabled. `AwsS3StorageHealth` probe verifies HEAD on the bucket.

### `ToolUp.Storage.AzureBlob` (Azure Blob)

Use when:
- Deployment runs on Azure and holds a Storage connection string (account key, or a SAS).
- Azure-native deployment topology.

Setup:

`ToolUp.Storage.AzureBlobStorage` is the module itself, so `create` is called unqualified after the open, and it already returns `IBlobStorage`:

```fsharp
open ToolUp.Storage.AzureBlobStorage

let storage =
    create {
        AzureBlobStorageConfig.defaults with
            ConnectionString = "DefaultEndpointsProtocol=https;AccountName=myappdata;AccountKey=..."
            // ONE Azure container holds every ToolUp scope as a blob-name
            // prefix — Azure's 3-63-char lowercase container naming rules
            // cannot express `_platform`. Defaults to "toolup".
            RootContainer = "tenant-blobs"
    }
```

**This companion authenticates by connection string, not `DefaultAzureCredential`.** For out-of-band rotation (a regenerated account key or SAS) set `ConnectionStringProvider = Some f`: `f` is called per operation and the client is rebuilt only when the resolved string changes, so the closure typically wraps an `ISecretStore.GetSecret` read. `UseDevelopmentStorage=true` targets Azurite.

`AzureBlobEncryptionAtRestValidator` calls `GetServiceProperties`; emits `Warning` if encryption isn't enabled.

### `ToolUp.Storage.GoogleCloud` (GCP)

Use when:
- Deployment runs on GCP, authenticating via application default credentials.
- GCP-native deployment topology.

Setup:

`ToolUp.Storage.GoogleCloudStorage` follows the same shape — the module *is* the companion, and `create` returns `IBlobStorage`:

```fsharp
open ToolUp.Storage.GoogleCloudStorage

let storage =
    create {
        // `CredentialsJson = None` (the default) follows the Application
        // Default Credentials chain. There is no project id: GCS bucket
        // names are globally unique, so the bucket alone locates the data.
        GoogleCloudStorageConfig.defaults with
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

All providers expose `Upload` / `Download` / `Delete` / `List` / `Exists` / `GetMetadata` over the unified `IBlobStorage` interface (plus the optional `DownloadRange` / `ComposeFrom` / `Erase` legs). Internally, each translates the SDK's `container` + `blobName` arguments to the underlying provider's storage path. `Upload` returns `Async<Result<string, string>>` — the `Ok` payload is the backend-native locator it wrote (e.g. `s3://my-bucket/team-acme/report.json`), not `unit`; `Download` returns `Async<Result<byte[], string>>`, not an option.

Blob names are `/`-delimited on this interface **on every platform** — a backend returning an OS-native separator from `List` breaks callers that strip a container prefix to recover an id. The `IBlobStorage` contract pack pins this.

## Encryption-at-rest decorator

The `EncryptedBlobStorage` decorator wraps any `IBlobStorage` and applies AES-GCM envelope encryption transparently. Layer it on top of the cloud companion for application-tier crypto:

```fsharp
// `PerScopeKeyResolver` is a top-level module containing a type of the
// same name. The constructor takes the secret backend the key material
// lives in, an IMemoryCache for the resolved per-scope keys, and an
// `IAuditLog option` for the key-lifecycle trail — not an IBlobStorage.
let resolver =
    PerScopeKeyResolver.PerScopeKeyResolver(secretStore, memoryCache, Some auditLog)
    :> IBlobEncryptionKeyResolver

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withStorage cloudStorage
|> ServerApp.withEncryptedBlobStorage resolver
// ... the deployment's other ServerApp.with* calls ...
|> ServerApp.run
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
        member _.Upload(container, blobName, content) = async {
            let key = $"{container}/{blobName}"
            let request =
                PutObjectRequest(
                    BucketName = bucket,
                    Key = key,
                    InputStream = new MemoryStream(content))
            let! _ = client.PutObjectAsync(request) |> Async.AwaitTask
            return Ok $"s3://{bucket}/{key}"
        }
        member _.Download(container, blobName) = async {
            let key = $"{container}/{blobName}"
            try
                let request = GetObjectRequest(BucketName = bucket, Key = key)
                use! response = client.GetObjectAsync(request) |> Async.AwaitTask
                use ms = new MemoryStream()
                do! response.ResponseStream.CopyToAsync(ms) |> Async.AwaitTask
                return Ok(ms.ToArray())
            with
            // NOTE: the AWS SDK surfaces exceptions here wrapped in
            // AggregateException, so this `:? AmazonS3Exception` test never
            // fires as written — the shipped AwsS3Storage companion matches
            // through an `(|Unwrapped|)` active pattern that flattens first.
            | :? AmazonS3Exception as ex when ex.StatusCode = HttpStatusCode.NotFound ->
                return Error $"not found: {key}"
        }
        // ... Delete / List / Exists / GetMetadata, plus the optional
        //     DownloadRange / CanComposeFrom / ComposeFrom / Erase legs
```

Wire:

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withStorage (MinioStorage(client, bucket) :> IBlobStorage)
// ... the deployment's other ServerApp.with* calls ...
|> ServerApp.run
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

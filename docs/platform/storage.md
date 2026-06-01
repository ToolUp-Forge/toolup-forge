# Storage

The Platform abstracts persistent storage behind `IBlobStorage`. Default in-process: `LocalFileStorage` writes to disk under `data/`. Production: cloud companions (`ToolUp.Storage.AwsS3`, `ToolUp.Storage.AzureBlob`, `ToolUp.Storage.GoogleCloud`) implement the same interface against object storage.

## `IBlobStorage`

```fsharp
type IBlobStorage =
    abstract Save: container: string -> objectId: string -> contents: byte[] -> Async<unit>
    abstract Load: container: string -> objectId: string -> Async<byte[] option>
    abstract Delete: container: string -> objectId: string -> Async<unit>
    abstract List: container: string -> prefix: string option -> Async<string list>
    abstract Exists: container: string -> objectId: string -> Async<bool>
```

Containers are tenant-scoped (`team-{teamId}`, `user-{userId}`, `session-{guid}`, `claim-{scopeId}` for share-token-bound resources, `_platform`). The container + `Persist` flag for each request fall out of the resolved `Subject` and the matching `SurfaceProfile`'s `Persistence` knob — see [`surfaces.md`](surfaces.md#persistence-routing) for the full per-subject routing table. Object IDs are arbitrary strings — usually structured paths like `objects/{objectId}/v{N}.json`.

The `_platform` container is reserved for SDK-owned state (team memberships, role assignments, encryption keys, audit-sink cursors, config blobs, etc.). Module code never writes there directly.

## Wiring a storage backend

```fsharp
// Default — disk-backed (dev)
ServerApp.empty
|> ServerApp.withStorage (LocalFileStorage("./data") :> IBlobStorage)
|> ...

// Production — AWS S3
open ToolUp.Storage.AwsS3
ServerApp.empty
|> ServerApp.withStorage (AwsS3Storage.create { BucketName = "my-bucket"; Region = "eu-west-2" } :> IBlobStorage)
|> ...
```

The shipped cloud companions:
- **`ToolUp.Storage.AwsS3`** — wraps the AWS SDK. Versioning + SSE-S3 / SSE-KMS + bucket-level Object Lock (used by the audit-replication WORM path). Configuration via standard AWS resolution (env vars, profile, IMDS).
- **`ToolUp.Storage.AzureBlob`** — wraps `Azure.Storage.Blobs`. Versioning + server-side encryption + immutability policies. Configuration via `DefaultAzureCredential`.
- **`ToolUp.Storage.GoogleCloud`** — wraps the GCP SDK. Object versioning + customer-managed encryption keys + bucket-level retention policies. Configuration via application default credentials.

Each cloud companion ships a matching `IConfigValidator` (`AwsS3EncryptionAtRestValidator`, etc.) that probes the bucket / container at preflight and emits a `Warning` if encryption-at-rest is not enabled at the cloud level.

## Encryption at rest (application-level)

The `EncryptedBlobStorage` decorator wraps any `IBlobStorage` and applies AES-GCM envelope encryption transparently. Useful in three scenarios:
- The bucket-level encryption is owned by the cloud provider's KMS, and you want application-tier crypto separation as defence in depth.
- Per-tenant key destruction (crypto-shred) for GDPR / contract termination is a requirement.
- The bucket is in a region where customer-managed keys are unavailable.

Envelope format:

```
[Magic:4 "TOBL"][KeyIdLen:1][KeyId:N][Nonce:12][Tag:16][Ciphertext:M]
```

- AES-GCM (256-bit key, 12-byte nonce, 16-byte AEAD tag) via BCL `System.Security.Cryptography.AesGcm`.
- KeyId is per-resolver — typically `"platform"` for `SingleKeyResolver` or `"scope:{scopeId}"` for `PerScopeKeyResolver`.
- The envelope is opaque to the underlying `IBlobStorage` — encryption is fully transparent to consumers.

### Key resolvers

```fsharp
type IBlobEncryptionKeyResolver =
    abstract ResolveKey: keyId: string -> Async<byte[] option>
    abstract CreateKey: keyId: string -> actorUserId: string -> Async<byte[]>
    abstract DestroyKey: keyId: string -> actorUserId: string -> Async<unit>
```

Two shipped resolvers:

**`SingleKeyResolver`** — one platform-wide key. Simplest setup. Suitable when crypto-shred isn't a tenant-level requirement.

```fsharp
let resolver = SingleKeyResolver(secretStore) :> IBlobEncryptionKeyResolver
ServerApp.empty
|> ServerApp.withEncryptedBlobStorage resolver
|> ...
```

**`PerScopeKeyResolver`** — per-tenant. `IMemoryCache` with 5-min sliding TTL so reads are fast after warmup. `DestroyKey scopeId actorUserId` crypto-shreds the tenant's data (subsequent reads fail because the envelope's KeyId can't be resolved). Crypto-shred is instant — far faster than walking and deleting every encrypted object.

```fsharp
let resolver = PerScopeKeyResolver(secretStore, blobStorage) :> IBlobEncryptionKeyResolver
ServerApp.empty
|> ServerApp.withEncryptedBlobStorage resolver
|> ...
```

The Platform exposes an admin endpoint `POST /api/_platform/encryption/destroy-scope-key/{scopeId}` gated by `PlatformRole.PlatformAdmin` (or `TOOLUP_ADMIN_TOKEN` + `X-Admin-Token` header for emergency access). Constant-time token comparison.

Three audit events fire under `_platform.audit`:
- `EncryptionKeyCreated` — emitted when a key is first generated.
- `EncryptionKeyRotated` — reserved for the future rotation flow.
- `EncryptionKeyDestroyed` — emitted on crypto-shred. Carries actor userId, target scopeId, timestamp.

### Writing a custom key resolver

Custom resolvers (per-`(scopeId, userId)`, BYOK, KMS-backed) plug in against the same interface:

```fsharp
type AwsKmsKeyResolver(kmsClient: IAmazonKMS) =
    interface IBlobEncryptionKeyResolver with
        member _.ResolveKey(keyId) = async {
            let! result = kmsClient.DecryptAsync(...) |> Async.AwaitTask
            return Some result.Plaintext.ToArray()
        }
        member _.CreateKey(keyId, actorUserId) = async {
            // KMS GenerateDataKey
            ...
        }
        member _.DestroyKey(keyId, actorUserId) = async {
            // KMS DeleteKey (or schedule deletion)
            ...
        }
```

The KMS-backed resolver companions for AWS / Azure / GCP are deferred work — the substrate is portable, but the integration matter requires per-cloud testing.

## Data object versioning

`IDataObjectStore` is built on `IBlobStorage` and adds version semantics:

```fsharp
type VersioningPolicy =
    | Unversioned          // single-version; saves overwrite
    | Versioned            // append-only; new save creates new version
    | StrictlyVersioned    // append-only; conflict-on-policy-mismatch

type IDataObjectStore =
    abstract Save: scopeId: string -> objectId: string -> bytes: byte[]
                  -> policy: VersioningPolicy -> Async<DataObject>
    abstract Get: scopeId: string -> objectId: string -> Async<DataObject option>
    abstract GetVersion: scopeId: string -> objectId: string -> version: int -> Async<DataObject option>
    abstract ListVersions: scopeId: string -> objectId: string -> Async<DataObject list>
    abstract ListObjects: scopeId: string -> Async<string list>
    abstract Recover: scopeId: string -> objectId: string -> version: int -> recovererUserId: string -> Async<DataObject>
    abstract Delete: scopeId: string -> objectId: string -> Async<unit>
    abstract Purge: scopeId: string -> objectId: string -> Async<unit>
```

Default `DataObjectStore` writes to `{container}/objects/{objectId}/v{N}.json` and adds content-addressable dedup at `{container}/objects/_content/{hash}.data` (per-scope, not a global pool — preserves team isolation). Sticky policy: once an object is created with `Unversioned`, attempting `Save` with `Versioned` returns `PolicyMismatch`.

`Recover` preserves the original author on the source version; the new version records the recoverer plus `Metadata["_recovered_from"]`. Useful for "restore the version from before the bad edit".

`SessionFileStore` (the file-management API) persists exclusively through `IDataObjectStore` with `Unversioned` policy. Modules that want history use `Versioned` directly.

## Data catalog

`IDataCatalog` exposes the registered data types across modules:

```fsharp
type IDataCatalog =
    abstract ListTypes: unit -> Async<DataTypeInfo list>
    abstract GetSchema: typeId: string -> Async<DataTypeSchema option>
    abstract GetProducers: typeId: string -> Async<string list>
    abstract ListObjects: scopeId: string -> typeId: string -> Async<DataObject list>
```

The catalog snapshots `(moduleName, DataType)` registrations at compose time. Deduplicates by `Id` for `ListTypes`; preserves multi-producer mappings in `GetProducers`. Surfaced through `PlatformApi.GetDataCatalog` for admin-UI / AI-tool discovery.

## Configuration knobs

Storage-related `ServerConfig` fields:

- `Storage` — `IBlobStorage` instance. Default `LocalFileStorage("./data")`.
- `EncryptionKeyResolver` — optional `IBlobEncryptionKeyResolver`. When set, `ServerApp.run` wires `EncryptedBlobStorage` around the configured storage.
- `MaxRequestBodyBytes` — caps single-request uploads. Defaults to generous; tighten for production.
- `DefaultTeamStorageQuotaBytes` — optional per-team quota (applied to `TeamMember` subjects when `Surfaces` includes a `Team` profile). Enforced via `ITeamQuotaPolicy` + `IUsageLog`.

Environment variables (read by the reference deployment, not by the SDK directly):
- `TOOLUP_STORAGE_PROVIDER=local|aws-s3|azure|gcs` — selects the storage companion.
- `TOOLUP_STORAGE_AWS_BUCKET`, `TOOLUP_STORAGE_AZURE_CONTAINER`, `TOOLUP_STORAGE_GCS_BUCKET` — per-provider config.
- `TOOLUP_ADMIN_TOKEN` — emergency-access token for the encryption-key destroy endpoint.

## Health checks

Storage health probes implement `IHealthCheck` and self-register via DI:

- `LocalFileStorageHealth` — verifies the data directory exists and is writable.
- `AwsS3StorageHealth` — HEAD on the bucket; reports latency.
- `AzureBlobStorageHealth` — `GetServiceProperties` against the account.
- `GoogleCloudStorageHealth` — bucket metadata get.

Each is wired automatically by the companion's `Server.props` extension contract; deployments don't need to register them explicitly. `/health` and `/ready` include them in the response.

## Limits + caveats

- **`LocalFileStorage` is not multi-process safe.** Two processes pointing at the same `data/` directory will race on writes. Use only single-instance.
- **`LocalFileStorageEncryptionAtRestValidator`** emits a `Warning` when local storage is configured without the encryption-at-rest decorator. The disk itself isn't encrypted by the SDK; that's an OS-level concern.
- **Cloud companions are not transactional.** `Save` is at-least-once; consumers handle idempotency. `IDataObjectStore` adds content-addressable dedup which masks duplicates for read-after-write within the same scope, but cross-object multi-step operations need application-level coordination.
- **Object size**: cloud companions stream uploads / downloads. `LocalFileStorage` reads the full byte array into memory.

For larger objects or streaming use cases (video, large dataset exports), the right shape is direct multipart upload to the cloud storage with the SDK getting only the resulting object ID. That's a future companion-extension story; the current API operates on full byte arrays.

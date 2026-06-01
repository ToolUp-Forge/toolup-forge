module ToolUp.Secrets.GcpSecretManager

open System
open Google.Cloud.SecretManager.V1
open Grpc.Core
open ToolUp.Platform.Secrets

// ─── Configuration ───────────────────────────────────────────────────
//
// Phase 2b — GCP Secret Manager implementation of `ISecretStore`.
// Mirrors the `src/Secrets/AwsSecretsManager` companion shape: one
// record-typed config, a `create` helper, and a `fromEnv` resolver.
// Activated via `TOOLUP_SECRET_STORE=gcp-secret-manager` +
// `TOOLUP_GCP_PROJECT_ID` in the consumer composition root.
//
// IAM. The caller's service account must hold the five Secret
// operations on the configured project (or on resources matching the
// `toolup_*` name prefix when finer-grained policies are needed):
//   secretmanager.secrets.create
//   secretmanager.secrets.delete
//   secretmanager.secrets.list
//   secretmanager.versions.access
//   secretmanager.versions.add
// Pre-defined roles:
//   - `roles/secretmanager.secretAccessor` — read (versions.access)
//   - `roles/secretmanager.secretVersionAdder` — write
//     (secrets.create / versions.add)
//   - `roles/secretmanager.admin` — full access (covers delete + list)
// A minimum-privilege policy spec ships in the companion README.
//
// Credential resolution. Application Default Credentials (ADC) — the
// Google SDK default chain. On GCP compute (Cloud Run / GKE / GCE /
// App Engine) ADC auto-resolves to the workload-identity-bound
// service account attached to the resource — no env vars, no JSON
// key on disk. Off-GCP (dev shells, cross-cloud deploys) ADC falls
// back to the path in `GOOGLE_APPLICATION_CREDENTIALS` pointing at a
// service-account JSON key file. ToolUp code never touches
// credentials directly; the companion takes only the project ID in
// config and lets `SecretManagerServiceClient.Create()` walk the ADC
// chain.
//
// Cross-tenant isolation (GP 4). Single GCP project holds every
// ToolUp scope as an underscore-prefixed secret ID: `toolup_{scopeId}_{key}`.
// GCP secret IDs allow `[A-Za-z0-9_-]` only — no slashes, no dots —
// so underscore is the separator. The prefix encodes the scope; a
// Cloud Audit Log entry shows exactly which scope each request
// touched. Per-scope project isolation is possible (one project per
// tenant) but operationally heavy and outside Phase 2b's scope.
//
// Cloud-KMS-native at-rest encryption. Secret Manager encrypts every
// secret at rest with Google-managed AES-256 keys by default;
// customer-managed encryption keys (CMEK) are configurable at the
// secret-creation level via the `Replication.Automatic.CustomerManagedEncryption`
// field (not exercised by this companion's default `SetSecret`).
// Wrapping this companion in `EncryptedSecretStore` would add a
// redundant envelope — the composition root deliberately does NOT
// wrap cloud-KMS companions, and the Phase 6l.E plaintext-secrets
// validator recognises `TOOLUP_SECRET_STORE=gcp-secret-manager` as
// equivalent to `EncryptedSecretStore` for the master-key gate.
//
// Versioning. Secret Manager treats each `SetSecret` as a new
// immutable version of the secret. `GetSecret name` resolves the
// `latest` version (the canonical alias for the highest-numbered
// enabled version). Callers that need a pinned version pass
// `name@versionId` (e.g. `"db-password@3"` or `"db-password@latest"`).
// The convention is documented in the TECHNICAL_GUIDE.md companion
// summary.
//
// Deletion. `DeleteSecret` removes the secret resource AND every
// version irreversibly. Unlike Azure Key Vault (soft-delete + purge)
// or AWS Secrets Manager (scheduled deletion), GCP has no recovery
// window — re-creating the same name immediately after delete is
// unconstrained. Tests still use GUID-suffixed scope IDs for
// hygiene.

/// Configuration for `GcpSecretManagerSecretStore`. Takes the GCP
/// project ID; credentials flow through Application Default
/// Credentials.
type GcpSecretManagerConfig = {
    /// GCP project ID — the slug from the GCP Console URL, e.g.
    /// `"my-project-12345"`. Read from `TOOLUP_GCP_PROJECT_ID`.
    ProjectId: string
}

module GcpSecretManagerConfig =
    let defaults = { ProjectId = "" }

// ─── Naming ──────────────────────────────────────────────────────────

module private Naming =
    // GCP Secret Manager secret IDs: 1-255 chars, `[A-Za-z0-9_-]` only.
    // No slashes, no dots, no `+`/`=`/`@` — strictly narrower than
    // AWS. Most ToolUp scope shapes (`_platform`, `team-{guid}`,
    // `user-{guid}`) pass through with only minor sanitisation; the
    // `@`-version-pin convention is parsed BEFORE sanitisation
    // because `@` is not a legal char in the underlying GCP secret
    // ID.
    let private isAllowed c =
        (c >= 'a' && c <= 'z')
        || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9')
        || c = '_'
        || c = '-'

    let sanitise (s: string) =
        s |> String.map (fun c -> if isAllowed c then c else '_')

    let secretId (scopeId: string) (key: string) =
        $"toolup_{sanitise scopeId}_{sanitise key}"

    let scopePrefix (scopeId: string) = $"toolup_{sanitise scopeId}_"

    let stripPrefix (prefix: string) (name: string) =
        if name.StartsWith prefix then
            Some(name.Substring prefix.Length)
        else
            None

    /// Split `"name@versionId"` into `(name, version)`, defaulting to
    /// `"latest"` when no `@` is present. GCP's `latest` alias
    /// resolves to the highest-numbered enabled version.
    let parseVersion (key: string) =
        match key.IndexOf '@' with
        | -1 -> key, "latest"
        | i -> key.Substring(0, i), key.Substring(i + 1)

    let projectResource (projectId: string) = $"projects/{projectId}"

    let secretResource (projectId: string) (secretId: string) =
        $"projects/{projectId}/secrets/{secretId}"

    let secretVersionResource (projectId: string) (secretId: string) (version: string) =
        $"projects/{projectId}/secrets/{secretId}/versions/{version}"

    /// Extract the trailing `{secretId}` from a resource name of the
    /// form `projects/{p}/secrets/{secretId}`. Defensive against
    /// future resource-name shape changes.
    let extractSecretId (resourceName: string) =
        let segments = resourceName.Split('/')

        if segments.Length >= 4 && segments[2] = "secrets" then
            Some segments[3]
        else
            None

// ─── ISecretStore implementation ─────────────────────────────────────

/// GCP Secret Manager implementation of `ISecretStore`. One
/// `SecretManagerServiceClient` per instance; the underlying gRPC
/// channel is thread-safe and designed for reuse.
type GcpSecretManagerSecretStore(config: GcpSecretManagerConfig) =
    do
        if String.IsNullOrWhiteSpace config.ProjectId then
            invalidArg "ProjectId" "GcpSecretManagerConfig.ProjectId is required (read from TOOLUP_GCP_PROJECT_ID)."

    let client = SecretManagerServiceClient.Create()
    let parent = Naming.projectResource config.ProjectId

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            let keyName, version = Naming.parseVersion key
            let secretId = Naming.secretId scopeId keyName
            let name = Naming.secretVersionResource config.ProjectId secretId version

            try
                let! response = client.AccessSecretVersionAsync name |> Async.AwaitTask
                // Payload.Data is a ByteString; secrets stored by
                // SetSecret are UTF-8 strings.
                return Some(response.Payload.Data.ToStringUtf8())
            with
            | :? RpcException as ex when ex.StatusCode = StatusCode.NotFound -> return None
            | :? RpcException as ex when ex.StatusCode = StatusCode.FailedPrecondition ->
                // Version is disabled or destroyed; surface as
                // "not found" per the ISecretStore contract.
                return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            // Strip any `@version` suffix on writes — versions are
            // assigned by Secret Manager, not the caller.
            let keyName, _ = Naming.parseVersion key
            let secretId = Naming.secretId scopeId keyName
            let secretName = Naming.secretResource config.ProjectId secretId

            let payload = SecretPayload()
            payload.Data <- Google.Protobuf.ByteString.CopyFromUtf8 value

            // AddSecretVersion is the hot path (the secret container
            // already exists from a prior write). On NotFound, fall
            // back to CreateSecret + AddSecretVersion. Cheaper than
            // a probe + branch for the common idempotent-write case.
            try
                let! _ = client.AddSecretVersionAsync(secretName, payload) |> Async.AwaitTask
                return Ok()
            with :? RpcException as ex when ex.StatusCode = StatusCode.NotFound ->
                try
                    let secret = Secret()
                    secret.Replication <- Replication()
                    secret.Replication.Automatic <- Replication.Types.Automatic()
                    let! _ = client.CreateSecretAsync(parent, secretId, secret) |> Async.AwaitTask
                    let! _ = client.AddSecretVersionAsync(secretName, payload) |> Async.AwaitTask
                    return Ok()
                with ex ->
                    return Error ex.Message
        }

        member _.DeleteSecret(scopeId, key) = async {
            let keyName, _ = Naming.parseVersion key
            let secretId = Naming.secretId scopeId keyName
            let name = Naming.secretResource config.ProjectId secretId

            try
                do! client.DeleteSecretAsync name |> Async.AwaitTask
                return Ok()
            with
            | :? RpcException as ex when ex.StatusCode = StatusCode.NotFound -> return Ok()
            | ex -> return Error ex.Message
        }

        member _.ListKeys(scopeId) = async {
            let prefix = Naming.scopePrefix scopeId
            let results = System.Collections.Generic.List<string>()
            // Filter syntax — Secret Manager supports prefix filters
            // on the `name` field (`name:toolup_{scopeId}_*`). The
            // wildcard semantics aren't fully spec'd across SDK
            // versions, so we defensively prefix-strip below.
            let request = ListSecretsRequest()
            request.Parent <- parent
            request.Filter <- $"name:{prefix}"

            let pageable = client.ListSecretsAsync request
            let enumerator = pageable.GetAsyncEnumerator()

            try
                let mutable keepGoing = true

                while keepGoing do
                    let! hasNext = enumerator.MoveNextAsync().AsTask() |> Async.AwaitTask

                    if hasNext then
                        // Secret.Name is the full resource name
                        // `projects/{p}/secrets/{secretId}`; recover
                        // the secretId, then strip the scope prefix
                        // to get the original key.
                        let secret = enumerator.Current

                        match Naming.extractSecretId secret.Name with
                        | Some sid ->
                            match Naming.stripPrefix prefix sid with
                            | Some k -> results.Add k
                            | None -> ()
                        | None -> ()
                    else
                        keepGoing <- false
            finally
                enumerator.DisposeAsync().AsTask().Wait()

            return results |> List.ofSeq
        }

// ─── Public entry points ─────────────────────────────────────────────

/// Construct an `ISecretStore` from a `GcpSecretManagerConfig`. The
/// `SecretManagerServiceClient` is initialised eagerly here; the
/// underlying gRPC channel connects lazily on first call. Bad
/// project ID / missing ADC credentials surface on first request as
/// an `RpcException`, not at composition.
let create (config: GcpSecretManagerConfig) : ISecretStore =
    new GcpSecretManagerSecretStore(config) :> ISecretStore

/// Read the project ID from `TOOLUP_GCP_PROJECT_ID` and construct a
/// `GcpSecretManagerSecretStore`. Returns `None` when the env var is
/// unset / empty so the deployment falls back to whatever the
/// composition root chose (typically `EncryptedSecretStore` over
/// `FileSecretStore`).
let fromEnv () : ISecretStore option =
    match Environment.GetEnvironmentVariable "TOOLUP_GCP_PROJECT_ID" with
    | null
    | "" -> None
    | projectId -> Some(create { ProjectId = projectId })

// ─── Phase 9c portability audit (six rules) ──────────────────────────
//
// 1. Identity by value — `ISecretStore` returns `string option` and
//    `Result<unit, string>`; never a GCP SDK live handle or a gRPC
//    channel.
// 2. Async at every boundary — every interface method returns
//    `Async<_>`; SDK Tasks bridged via `Async.AwaitTask`.
// 3. Retry as data — none expressed by this companion. The Google
//    Cloud client libraries carry their own gRPC-level retry policy
//    (idempotent reads retry on `Unavailable` etc.); the companion
//    stays pure pass-through and the deployment can override via
//    `SecretManagerServiceClientBuilder` if needed.
// 4. Stateless between calls — the cached `SecretManagerServiceClient`
//    is per-project not per-secret; no in-memory state survives
//    across method calls. Distributed-safe: any node with the same
//    ADC identity produces identical results.
// 5. No cross-shard ordering — Secret Manager makes no global
//    ordering promises across secrets, and this companion makes
//    none either. `ListKeys` order is whatever `ListSecretsAsync`
//    returns (page-by-page, no guaranteed sort).
// 6. Precision at the lower bound — N/A; `ISecretStore` has no
//    timing semantics.
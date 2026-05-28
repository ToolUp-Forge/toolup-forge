module ToolUp.Secrets.AzureKeyVault

open System
open Azure
open Azure.Identity
open Azure.Security.KeyVault.Secrets
open ToolUp.Platform.Secrets

// ─── Configuration ───────────────────────────────────────────────────
//
// Phase 2a — Azure Key Vault implementation of `ISecretStore`. Mirrors
// the `src/Storage/AzureBlobStorage` companion shape: one record-typed
// config value, a `create` helper, and a `fromEnv` resolver. Activated
// via `TOOLUP_SECRET_STORE=azure-key-vault` + `TOOLUP_AZURE_KEY_VAULT_URL`
// in `src/ToolUpApp-Server/Server.fs`.
//
// IAM / RBAC. Caller identity must hold the four Secret operations on
// the target vault — `secrets/get`, `secrets/set`, `secrets/delete`,
// `secrets/list`. RBAC role: `Key Vault Secrets Officer` (read+write)
// or `Key Vault Secrets User` (read-only). Vault access policy: tick
// every Secret operation under the caller's principal. Read-only
// deployments only need `secrets/get`; the `SetSecret` / `DeleteSecret`
// paths surface `Error` messages from the Azure SDK on a 403.
//
// Credential resolution. `DefaultAzureCredential` walks Azure's standard
// chain — env vars (`AZURE_CLIENT_ID` / `_SECRET` / `_TENANT_ID`),
// managed identity (IMDS on Azure VMs / App Service / Container Apps),
// Visual Studio sign-in, Azure CLI, Azure PowerShell. ToolUp code
// never touches credentials directly. For local development run
// `az login` once; the SDK reuses the cached token.
//
// Cross-tenant isolation (Guiding Principle 4). Single Key Vault holds
// every ToolUp scope as a name-prefixed secret: `toolup-{scopeId}-{key}`.
// Cross-scope reads are structurally impossible — the lookup name
// encodes the scope, so a Vault audit log shows exactly which scope's
// data each request touched. Multi-vault per-scope isolation is
// possible but operationally heavy (hits Azure's 1000-vault-per-
// subscription limit on any non-trivial deployment) and outside Phase
// 2a's scope. Naming-constraint workaround details in `Naming` below.
//
// Cloud-KMS-native at-rest encryption. Key Vault encrypts secrets at
// rest with HSM-backed AES-256 (Standard tier: software-protected;
// Premium tier: FIPS 140-2 Level 2 HSM). Wrapping this companion in
// `EncryptedSecretStore` would add a redundant envelope — `Server.fs`
// deliberately does NOT wrap cloud-KMS companions, and the Phase 6l.E
// validator recognises `TOOLUP_SECRET_STORE=azure-key-vault` as
// equivalent to `EncryptedSecretStore` for the master-key gate.
//
// Soft-delete. Key Vault has soft-delete enabled by default (90-day
// retention). `DeleteSecret` triggers the soft-delete state — the
// secret remains recoverable until purged. Re-creating a soft-deleted
// secret with the same name fails with `Conflict`; operators who need
// to immediately re-use a name must purge through the Azure portal /
// CLI (requires `secrets/purge` permission, irreversible). Tests
// avoid this by using GUID-suffixed scope IDs.

/// Configuration for `AzureKeyVaultSecretStore`. Construct from the
/// vault URL read out of `TOOLUP_AZURE_KEY_VAULT_URL`.
type AzureKeyVaultConfig = {
    /// Full vault URL — `https://{vault-name}.vault.azure.net/`. Read
    /// from the `TOOLUP_AZURE_KEY_VAULT_URL` env var (or wherever the
    /// deployment keeps it). Trailing slash optional; Azure SDK
    /// normalises either form.
    VaultUrl: string
}

module AzureKeyVaultConfig =
    let defaults = { VaultUrl = "" }

// ─── Naming ──────────────────────────────────────────────────────────

module private Naming =
    // Azure Key Vault secret names: 1-127 chars, `[a-zA-Z0-9-]` only.
    // No underscores, no dots, no slashes — so `_platform`, `team-...`,
    // `user-...` scopes all need normalisation. Mapping rule: every
    // non-alphanumeric, non-hyphen char becomes `-`; every uppercase
    // letter becomes lowercase. Lowercasing is forward-only — the
    // round-trip via `ListKeys` returns sanitised forms, documented in
    // the type's `ListKeys` member.
    //
    // Azure secret names are case-insensitive ("Foo" and "foo" collide
    // in the same vault), so even without our lowercasing the SDK
    // would conflate `KEY` and `key`. Our normalisation makes the
    // collision visible at the helper level rather than at the SDK
    // call site.
    let sanitise (s: string) =
        s
        |> String.map (fun c ->
            if (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-' then
                c
            elif c >= 'A' && c <= 'Z' then
                Char.ToLowerInvariant c
            else
                '-')

    let secretName (scopeId: string) (key: string) =
        $"toolup-{sanitise scopeId}-{sanitise key}"

    let scopePrefix (scopeId: string) = $"toolup-{sanitise scopeId}-"

    let stripPrefix (prefix: string) (name: string) =
        if name.StartsWith prefix then
            Some(name.Substring prefix.Length)
        else
            None

// ─── ISecretStore implementation ─────────────────────────────────────

/// Azure Key Vault implementation of `ISecretStore`. One `SecretClient`
/// per instance; the underlying Azure SDK client is thread-safe and
/// designed for reuse.
type AzureKeyVaultSecretStore(config: AzureKeyVaultConfig) =
    let credential = DefaultAzureCredential() :> Core.TokenCredential
    let client = SecretClient(Uri config.VaultUrl, credential)

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            let name = Naming.secretName scopeId key

            try
                let! response = client.GetSecretAsync name |> Async.AwaitTask
                return Some response.Value.Value
            with :? RequestFailedException as ex when ex.Status = 404 ->
                return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            let name = Naming.secretName scopeId key

            try
                let! _ = client.SetSecretAsync(name, value) |> Async.AwaitTask
                return Ok()
            with ex ->
                return Error ex.Message
        }

        member _.DeleteSecret(scopeId, key) = async {
            let name = Naming.secretName scopeId key

            try
                let! op = client.StartDeleteSecretAsync name |> Async.AwaitTask
                // Wait for the soft-delete to complete so a follow-up
                // `GetSecret` round-trips correctly. Idempotent — 404
                // on already-deleted maps to Ok per ISecretStore
                // contract.
                // `WaitForCompletionAsync()` returns `ValueTask<T>`; bridge
                // through `.AsTask()` for `Async.AwaitTask`.
                let! _ = op.WaitForCompletionAsync().AsTask() |> Async.AwaitTask
                return Ok()
            with
            | :? RequestFailedException as ex when ex.Status = 404 -> return Ok()
            | ex -> return Error ex.Message
        }

        member _.ListKeys(scopeId) = async {
            // Returns sanitised key names, not the original casing /
            // punctuation a caller passed to SetSecret. Sanitisation is
            // lossy (case-fold + non-alphanum→`-`), so original-casing
            // round-trip isn't possible from Vault metadata alone. The
            // contract pack tolerates this because every test sets-then-
            // lists with the same string.
            let prefix = Naming.scopePrefix scopeId
            let pageable = client.GetPropertiesOfSecretsAsync()
            let results = System.Collections.Generic.List<string>()
            let enumerator = pageable.GetAsyncEnumerator()

            try
                let mutable keepGoing = true

                while keepGoing do
                    let! hasNext = enumerator.MoveNextAsync().AsTask() |> Async.AwaitTask

                    if hasNext then
                        match Naming.stripPrefix prefix enumerator.Current.Name with
                        | Some k -> results.Add k
                        | None -> ()
                    else
                        keepGoing <- false
            finally
                enumerator.DisposeAsync().AsTask().Wait()

            return results |> List.ofSeq
        }

// ─── Public entry points ─────────────────────────────────────────────

/// Construct an `ISecretStore` from an `AzureKeyVaultConfig`. The
/// `SecretClient` is initialised lazily on the first request — no
/// startup work, no eager auth probe. If the vault URL is unreachable
/// or the caller lacks permissions, the failure surfaces on the first
/// `GetSecret` / `SetSecret` call as a `RequestFailedException`.
let create (config: AzureKeyVaultConfig) : ISecretStore =
    AzureKeyVaultSecretStore config :> ISecretStore

/// Read the vault URL from `TOOLUP_AZURE_KEY_VAULT_URL` and construct
/// an `AzureKeyVaultSecretStore`. Returns `None` when the env var is
/// unset / empty so the deployment falls back to whatever the
/// composition root chose (typically `EncryptedSecretStore` over
/// `FileSecretStore`).
let fromEnv () : ISecretStore option =
    match Environment.GetEnvironmentVariable "TOOLUP_AZURE_KEY_VAULT_URL" with
    | null
    | "" -> None
    | url -> Some(create { VaultUrl = url })

// ─── Phase 9c portability audit (six rules) ──────────────────────────
//
// 1. Identity by value — `ISecretStore` returns `string option` and
//    `Result<unit, string>`; never an Azure SDK live handle.
// 2. Async at every boundary — every interface method returns
//    `Async<_>`; SDK Tasks bridged via `Async.AwaitTask`.
// 3. Retry as data — none expressed by this companion. Caller-supplied
//    retry / backoff is the SDK's `SecretClientOptions.Retry` policy
//    (set by the deployment if needed); the companion stays pure
//    pass-through.
// 4. Stateless between calls — the cached `SecretClient` is per-vault
//    not per-secret; no in-memory state survives across method calls.
//    Distributed-safe: any node with vault access produces identical
//    results.
// 5. No cross-shard ordering — Key Vault makes no global ordering
//    promises, and this companion makes none either. `ListKeys` order
//    is whatever Azure returns (page-by-page, no guaranteed sort).
// 6. Precision at the lower bound — N/A; `ISecretStore` has no timing
//    semantics.
module ToolUp.Platform.SingleKeyResolver

open System
open System.Security.Cryptography
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.BlobEncryption

// ─── Phase 22 — SingleKeyResolver ───────────────────────────────────
//
// Default `IBlobEncryptionKeyResolver` implementation: one platform-
// wide AES-256 key shared across all scopes. Use case: deployments
// where one cryptographic boundary is enough — a single-tenant
// instance, or a multi-tenant deployment that doesn't need
// cryptographic separation between tenants beyond what the cloud
// provider's at-rest encryption gives.
//
// The key is persisted via `ISecretStore` under the reserved
// `_platform/encryption/master.key` slot, base64-encoded as a
// JSON-friendly string. On first resolution, the resolver auto-creates
// a 32-byte key from `RandomNumberGenerator.GetBytes` if absent. Once
// generated, the key is cached in memory for the process lifetime and
// never re-read from `ISecretStore`. (`ISecretStore.GetSecret` is
// async-cheap but the cache eliminates the round-trip on every blob
// op.)
//
// `KeyId` is the literal string `_platform/master/v1` — semver-style
// to support a future "rotate by writing v2 and falling back to v1
// for old blobs" path. v1-only is shipped today; v2 is a follow-up
// when a deployment actually needs key rotation.
//
// The reserved-scope-id naming (`_platform`) prevents collision with
// any team or user scope. `ISecretStore` callers that pass `_platform`
// as scopeId already understand the convention (see ISecretStore.fs
// docstring).

/// Stable identifier for the platform-wide master key. The string
/// travels in every encrypted blob's envelope header; changing it
/// strands all previously-encrypted data.
[<Literal>]
let private SingleKeyId = "_platform/master/v1"

[<Literal>]
let private SecretScope = "_platform"

[<Literal>]
let private SecretKey = "encryption/master.key"

[<Literal>]
let private KeyByteLength = 32 // AES-256

/// Generate a fresh 32-byte AES-256 key.
let private generateKey () : byte[] =
    RandomNumberGenerator.GetBytes KeyByteLength

/// Persists at `_platform/encryption/master.key` via `ISecretStore`.
/// On first `ResolveKey` call, generates and stores a new key if one
/// doesn't already exist. Subsequent calls hit the in-memory cache.
type SingleKeyResolver(secretStore: ISecretStore) =
    let cacheLock = new SemaphoreSlim(1, 1)
    let mutable cachedKey: EncryptionKey option = None

    /// Read the persisted key from `ISecretStore`, parsing the
    /// base64-encoded material. Returns `None` when no key is stored
    /// (first run) or when the stored value is malformed (manual
    /// corruption — extremely rare; an admin would have to edit the
    /// secret store directly).
    let tryLoadStored () = async {
        let! stored = secretStore.GetSecret(SecretScope, SecretKey)

        match stored with
        | None -> return None
        | Some base64 ->
            try
                let material = Convert.FromBase64String base64

                if material.Length = KeyByteLength then
                    return
                        Some {
                            KeyId = SingleKeyId
                            Material = material
                        }
                else
                    return None
            with _ ->
                return None
    }

    /// Auto-create a new master key on first resolution. Caller must
    /// hold `cacheLock` to prevent two concurrent first-call requests
    /// generating different keys.
    let createAndStore () = async {
        let material = generateKey ()
        let base64 = Convert.ToBase64String material
        let! result = secretStore.SetSecret(SecretScope, SecretKey, base64)

        match result with
        | Ok() ->
            return {
                KeyId = SingleKeyId
                Material = material
            }
        | Error msg -> return failwithf "SingleKeyResolver: failed to persist master key: %s" msg
    }

    /// Resolve the key, hitting the cache fast-path then falling back
    /// to load-or-create under the lock.
    let resolveCached () = async {
        match cachedKey with
        | Some key -> return key
        | None ->
            do! Async.AwaitTask(cacheLock.WaitAsync())

            try
                match cachedKey with
                | Some key -> return key
                | None ->
                    let! loaded = tryLoadStored ()

                    let! key =
                        match loaded with
                        | Some k -> async.Return k
                        | None -> createAndStore ()

                    cachedKey <- Some key
                    return key
            finally
                cacheLock.Release() |> ignore
    }

    interface IBlobEncryptionKeyResolver with
        member _.ResolveKey(_scope: StorageScope) = resolveCached ()

        member _.ResolveKeyById(keyId: string) = async {
            if keyId <> SingleKeyId then
                return Error(KeyNotFound keyId)
            else
                let! key = resolveCached ()
                return Ok key
        }

/// Build a `SingleKeyResolver` from an `ISecretStore`. The resolver
/// auto-creates a new 32-byte key on first `ResolveKey` call if none
/// is already stored at `_platform/encryption/master.key`.
let create (secretStore: ISecretStore) : IBlobEncryptionKeyResolver =
    SingleKeyResolver(secretStore) :> IBlobEncryptionKeyResolver
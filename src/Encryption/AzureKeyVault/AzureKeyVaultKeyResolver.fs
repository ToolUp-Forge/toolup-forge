// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Encryption.AzureKeyVault

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open Azure
open Azure.Core
open Azure.Security.KeyVault.Keys.Cryptography
open ToolUp.Platform
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.BlobEncryption

// ─── Phase 22a — Azure Key Vault-backed IBlobEncryptionKeyResolver ─────
//
// Envelope encryption against an Azure Key Vault (or Managed HSM)
// key-encryption-key (KEK) that never leaves the vault. Unlike AWS KMS
// (whose `GenerateDataKey` mints the DEK server-side), Key Vault exposes
// wrap/unwrap only — so the resolver mints a random AES-256 data key
// (DEK) locally, wraps it with the KEK via `CryptographyClient.WrapKey`,
// and stamps the wrapped DEK into the `EncryptionKey.KeyId`. A later read
// unwraps it via `CryptographyClient.UnwrapKey`. The resolver holds no
// key state between calls (GP 12 rule 4) — only a cache of transport
// `CryptographyClient`s keyed by KEK id (the client carries no secret).
//
// Because a wrapped DEK does not self-describe which KEK wrapped it (the
// AWS KMS ciphertext blob does), the `KeyId` encodes BOTH the KEK
// identifier and the wrapped DEK:
//   azure-kv:v1:{base64url(kekId)}.{base64(wrappedDek)}
// so `ResolveKeyById` recovers the KEK to build the right unwrap client.
// A single-KEK deployment uses one id for every scope; a multi-tenant
// deployment routes per scope via `keyIdForScope`.

/// `EncryptionKey.KeyId` prefix for an Azure-Key-Vault-wrapped DEK.
[<AutoOpen>]
module internal Constants =
    [<Literal>]
    let KeyIdPrefix = "azure-kv:v1:"

    let private b64Url (bytes: byte[]) : string =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private b64UrlDecode (s: string) : byte[] =
        let padded =
            match s.Length % 4 with
            | 2 -> s + "=="
            | 3 -> s + "="
            | _ -> s

        Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'))

    /// `prefix + base64url(kekId) + "." + base64(wrappedDek)`. The two
    /// segments share no alphabet with '.', so the split is unambiguous.
    let encodeKeyId (kekId: string) (wrapped: byte[]) : string =
        KeyIdPrefix
        + b64Url (Text.Encoding.UTF8.GetBytes kekId)
        + "."
        + Convert.ToBase64String wrapped

    /// Recover `(kekId, wrappedDek)` from a stamped `KeyId`.
    let decodeKeyId (keyId: string) : Result<string * byte[], unit> =
        if not (keyId.StartsWith(KeyIdPrefix, StringComparison.Ordinal)) then
            Error()
        else
            let body = keyId.Substring KeyIdPrefix.Length

            match body.IndexOf '.' with
            | i when i > 0 ->
                try
                    let kekId = Text.Encoding.UTF8.GetString(b64UrlDecode (body.Substring(0, i)))
                    let wrapped = Convert.FromBase64String(body.Substring(i + 1))
                    Ok(kekId, wrapped)
                with _ ->
                    Error()
            | _ -> Error()

/// Azure Key Vault-backed key resolver. `cryptoClientForKey` builds (and
/// the resolver caches) a `CryptographyClient` for a KEK identifier URI;
/// `keyIdForScope` routes a scope to its KEK; `wrapAlgorithm` is the wrap
/// algorithm the KEK supports (RSA-OAEP-256 by default — the common Key
/// Vault KEK shape).
type AzureKeyVaultKeyResolver
    (
        cryptoClientForKey: string -> CryptographyClient,
        keyIdForScope: StorageScope -> string,
        wrapAlgorithm: KeyWrapAlgorithm
    ) =

    // Transport-client cache — the client holds no key material; caching
    // avoids rebuilding a client (and its auth pipeline) per blob upload.
    let clients = ConcurrentDictionary<string, CryptographyClient>()

    let clientFor (kekId: string) =
        clients.GetOrAdd(kekId, cryptoClientForKey)

    let mapFailure (keyId: string) (ex: exn) : KeyResolutionError =
        match ex with
        | :? RequestFailedException as rfe ->
            match rfe.Status with
            // 404 — the KEK is gone (deleted / never existed).
            | 404 -> KeyNotFound keyId
            // 403 — forbidden: a disabled / purged KEK surfaces here; the
            // wrapped DEK is permanently unrecoverable (crypto-shred).
            | 403 -> KeyDestroyed keyId
            | _ -> StorageFailure rfe.Message
        | _ -> StorageFailure ex.Message

    /// Convenience ctor — one KEK for every scope, RSA-OAEP-256.
    new(credential: TokenCredential, kekId: string) =
        AzureKeyVaultKeyResolver(
            (fun id -> CryptographyClient(Uri id, credential)),
            (fun _ -> kekId),
            KeyWrapAlgorithm.RsaOaep256
        )

    interface IBlobEncryptionKeyResolver with
        member _.ResolveKey(scope: StorageScope) : Async<EncryptionKey> = async {
            let kekId = keyIdForScope scope
            // Mint a fresh AES-256 data key locally; the KEK only ever
            // wraps it — the DEK plaintext never reaches the vault.
            let dek = RandomNumberGenerator.GetBytes 32
            let client = clientFor kekId
            let! result = client.WrapKeyAsync(wrapAlgorithm, dek) |> Async.AwaitTask

            return {
                KeyId = encodeKeyId kekId result.EncryptedKey
                Material = dek
            }
        }

        member _.ResolveKeyById(keyId: string) : Async<Result<EncryptionKey, KeyResolutionError>> = async {
            match decodeKeyId keyId with
            | Error() -> return Error(KeyNotFound keyId)
            | Ok(kekId, wrapped) ->
                try
                    let client = clientFor kekId
                    let! result = client.UnwrapKeyAsync(wrapAlgorithm, wrapped) |> Async.AwaitTask

                    return Ok { KeyId = keyId; Material = result.Key }
                with ex ->
                    return Error(mapFailure keyId ex)
        }

module AzureKeyVaultKeyResolver =
    /// Construct a Key-Vault-backed `IBlobEncryptionKeyResolver` against
    /// one KEK (RSA-OAEP-256). `kekId` is the full key identifier URI
    /// (e.g. `https://my-vault.vault.azure.net/keys/blob-kek/<version>`).
    /// Wire via `ServerApp.withEncryptedBlobStorage`.
    let create (credential: TokenCredential) (kekId: string) : IBlobEncryptionKeyResolver =
        AzureKeyVaultKeyResolver(credential, kekId) :> IBlobEncryptionKeyResolver

    /// Construct a resolver that routes each scope to its own KEK
    /// (per-scope key custody), RSA-OAEP-256.
    let createPerScope
        (credential: TokenCredential)
        (keyIdForScope: StorageScope -> string)
        : IBlobEncryptionKeyResolver =
        AzureKeyVaultKeyResolver(
            (fun id -> CryptographyClient(Uri id, credential)),
            keyIdForScope,
            KeyWrapAlgorithm.RsaOaep256
        )
        :> IBlobEncryptionKeyResolver

    /// Full-control ctor — supply the `CryptographyClient` factory, the
    /// scope→KEK router, and the wrap algorithm (e.g. `A256KW` for a
    /// Managed-HSM AES KEK).
    let createWith
        (cryptoClientForKey: string -> CryptographyClient)
        (keyIdForScope: StorageScope -> string)
        (wrapAlgorithm: KeyWrapAlgorithm)
        : IBlobEncryptionKeyResolver =
        AzureKeyVaultKeyResolver(cryptoClientForKey, keyIdForScope, wrapAlgorithm) :> IBlobEncryptionKeyResolver
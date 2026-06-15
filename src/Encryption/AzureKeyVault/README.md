# ToolUp.Encryption.AzureKeyVault

Azure Key Vault-backed `IBlobEncryptionKeyResolver` for ToolUp.Platform
(Phase 22a). Envelope encryption: the deployment's key-encryption-key
(KEK) never leaves the vault. Each blob is encrypted with its own AES-256
data key (DEK) minted locally; the DEK is wrapped by the KEK via
`CryptographyClient.WrapKey`, and the wrapped DEK (plus the KEK
identifier) is stamped into the blob envelope's `KeyId`. A later read
unwraps it via `CryptographyClient.UnwrapKey`. The resolver holds no key
state between calls (GP 12 rule 4) — only a transport-client cache.

Mirrors `src/Encryption/AwsKms/` packaging. Server-only companion.

## Quick start

```fsharp
open Azure.Identity
open ToolUp.Encryption.AzureKeyVault

let credential = DefaultAzureCredential()
let kekId = "https://my-vault.vault.azure.net/keys/blob-kek/<version>"
let resolver = AzureKeyVaultKeyResolver.create credential kekId
// wire via ServerApp.withEncryptedBlobStorage resolver
```

Per-scope KEKs (multi-tenant key custody):

```fsharp
let resolver =
    AzureKeyVaultKeyResolver.createPerScope credential (fun scope -> kekUriFor scope.ScopeId)
```

Managed-HSM AES KEK (AES-key-wrap instead of RSA-OAEP):

```fsharp
open Azure.Security.KeyVault.Keys.Cryptography

let resolver =
    AzureKeyVaultKeyResolver.createWith
        (fun id -> CryptographyClient(System.Uri id, credential))
        (fun _ -> kekId)
        KeyWrapAlgorithm.A256Kw
```

## Behaviour

- `ResolveKey scope` → mint a random AES-256 DEK locally → `WrapKey`
  (RSA-OAEP-256 by default); returns the plaintext DEK + a `KeyId` of
  `azure-kv:v1:{base64url(kekId)}.{base64(wrappedDEK)}`.
- `ResolveKeyById keyId` → recover the KEK id + wrapped DEK → `UnwrapKey`.
  A 404 surfaces as `KeyResolutionError.KeyNotFound`; a 403 (disabled /
  purged KEK) as `KeyDestroyed` (HTTP 410 Gone at the API boundary —
  crypto-shred); other failures as `StorageFailure`.

The DEK plaintext is generated in-process and only ever leaves wrapped —
the KEK private material stays in the vault throughout.

## Verification

The resolver requires a live Key Vault KEK (no offline fake — Key Vault
has no local emulator and `CryptographyClient` is not practically
mockable). Mirrors the env-gated live-arm convention of the AWS KMS
resolver / `AIProviders.Tests`. Verify against a real vault:

1. Create an RSA key in a Key Vault; grant the deployment identity
   `wrapKey` + `unwrapKey` permissions.
2. `ResolveKey` → encrypt a blob → `ResolveKeyById` round-trips the DEK.
3. Disable the key → `ResolveKeyById` returns `KeyDestroyed`.

## License

Apache-2.0.

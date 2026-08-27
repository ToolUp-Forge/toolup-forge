# ToolUp.Encryption.GoogleCloudKms

GCP Cloud KMS-backed `IBlobEncryptionKeyResolver` for ToolUp.Platform
(Phase 22a). Envelope encryption: the deployment's KMS symmetric
CryptoKey (KEK) never leaves GCP. Each blob is encrypted with its own
AES-256 data key (DEK) minted locally; the DEK is `Encrypt`'d under the
KEK and the ciphertext (plus the KEK resource name) is stamped into the
blob envelope's `KeyId`. A later read `Decrypt`s it. The resolver holds
no key state between calls (GP 12 rule 4).

Mirrors `src/Encryption/AwsKms/` packaging. Server-only companion.

## Quick start

```fsharp
open Google.Cloud.Kms.V1
open ToolUp.Encryption.GoogleCloudKms

let client = KeyManagementServiceClient.Create() // ADC from the env
let keyName = "projects/my-proj/locations/europe-west2/keyRings/blob/cryptoKeys/kek"
let resolver = GoogleCloudKmsKeyResolver.create client keyName
// wire via ServerApp.withEncryptedBlobStorage resolver
```

Per-scope KEKs (multi-tenant key custody):

```fsharp skip=fragment
let resolver =
    GoogleCloudKmsKeyResolver.createPerScope client (fun scope -> keyNameFor scope.ScopeId)
```

## Behaviour

- `ResolveKey scope` → mint a random AES-256 DEK locally → KMS `Encrypt`
  under the KEK; returns the plaintext DEK + a `KeyId` of
  `gcp-kms:v1:{base64url(keyName)}.{base64(ciphertext)}`.
- `ResolveKeyById keyId` → recover the KEK resource name + ciphertext →
  KMS `Decrypt`. `NOT_FOUND` surfaces as `KeyResolutionError.KeyNotFound`;
  `FAILED_PRECONDITION` / `PERMISSION_DENIED` (disabled / destroyed key
  version) as `KeyDestroyed` (HTTP 410 Gone at the API boundary —
  crypto-shred); other gRPC failures as `StorageFailure`.

The DEK plaintext is generated in-process and only ever leaves as the
Encrypt plaintext / Decrypt result — the KEK key material stays in KMS.

## Verification

The resolver requires a live KMS CryptoKey (no offline fake — KMS has no
local emulator and `KeyManagementServiceClient` is not practically
mockable). Mirrors the env-gated live-arm convention of the AWS KMS
resolver / `AIProviders.Tests`. Verify against a real key:

1. Create a symmetric `ENCRYPT_DECRYPT` CryptoKey; grant the deployment
   service account `cloudkms.cryptoKeyEncrypterDecrypter`.
2. `ResolveKey` → encrypt a blob → `ResolveKeyById` round-trips the DEK.
3. Disable the key version → `ResolveKeyById` returns `KeyDestroyed`.

## License

Apache-2.0.

# ToolUp.Encryption.AwsKms

AWS KMS-backed `IBlobEncryptionKeyResolver` for ToolUp.Platform
(Phase 22a). Envelope encryption: the deployment's KMS customer master
key (CMK) never leaves AWS. Each blob is encrypted with its own AES-256
data key (DEK) minted by `GenerateDataKey`; the CMK-wrapped DEK
ciphertext is stamped into the blob envelope's `KeyId`, and a later read
unwraps it via `Decrypt`. The resolver holds no key state between calls
(GP 12 rule 4).

Mirrors `src/Storage/<Provider>/` packaging. Server-only companion.

## Quick start

```fsharp
open Amazon.KeyManagementService
open ToolUp.Encryption.AwsKms

let kms = new AmazonKeyManagementServiceClient() // region + creds from the env
let resolver = AwsKmsKeyResolver.create kms "arn:aws:kms:eu-west-2:...:key/<cmk>"
// wire via ServerApp.withEncryptedBlobStorage resolver
```

Per-scope CMKs (multi-tenant key custody):

```fsharp
let resolver = AwsKmsKeyResolver.createPerScope kms (fun scope -> cmkArnFor scope.ScopeId)
```

## Behaviour

- `ResolveKey scope` → `GenerateDataKey(CMK, AES_256)`; returns the
  plaintext DEK + a `KeyId` of `aws-kms:v1:{base64(wrappedDEK)}`.
- `ResolveKeyById keyId` → unwrap + `Decrypt`. A disabled / pending-
  deletion / deleted CMK surfaces as `KeyResolutionError.KeyDestroyed`
  (HTTP 410 Gone at the API boundary — crypto-shred); an unknown key as
  `KeyNotFound`; transient failures as `StorageFailure`.

## Verification

The resolver requires a live KMS CMK (no offline fake — KMS has no
local emulator and `IAmazonKeyManagementService` is not practically
mockable). Verify against a real CMK:

1. Create a symmetric KMS key; grant the deployment role
   `kms:GenerateDataKey` + `kms:Decrypt`.
2. `ResolveKey` → encrypt a blob → `ResolveKeyById` round-trips the DEK.
3. Disable the CMK → `ResolveKeyById` returns `KeyDestroyed`.

## Follow-ups (Phase 22a)

- Azure Key Vault + GCP KMS mirror resolvers (`src/Encryption/{AzureKeyVault,GoogleCloudKms}/`).
- The Phase 40 KMS-signing flavour (`IArtefactSigner` backed by KMS
  asymmetric `Sign`, key never in process memory).

## License

Apache-2.0.

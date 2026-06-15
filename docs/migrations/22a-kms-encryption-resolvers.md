# Migration — Phase 22a: KMS-backed encryption resolvers

**Status:** new opt-in companion(s) under `src/Encryption/<Provider>/`.
No consumer is *required* to act — a deployment keeps its existing
`IBlobEncryptionKeyResolver` (default `SingleKeyResolver`) until it opts
into KMS. This doc covers the shipped AWS KMS arm.

## What changes

A new NuGet package `ToolUp.Encryption.AwsKms` provides an
`IBlobEncryptionKeyResolver` that fronts AWS KMS with envelope
encryption — the CMK never leaves AWS.

## Diff to apply (opt-in)

`*.fsproj`:

```xml
<PackageReference Include="ToolUp.Encryption.AwsKms" />
```

Composition root:

```fsharp
open Amazon.KeyManagementService
open ToolUp.Encryption.AwsKms

let kms = new AmazonKeyManagementServiceClient()
let resolver = AwsKmsKeyResolver.create kms "arn:aws:kms:...:key/<cmk>"
// ServerApp.withEncryptedBlobStorage resolver
```

## Verification

KMS has no offline emulator and `IAmazonKeyManagementService` is not
practically mockable, so verification is against a live CMK:

- Grant the deployment role `kms:GenerateDataKey` + `kms:Decrypt`.
- Upload a blob → download round-trips (the wrapped DEK in the envelope
  `KeyId` unwraps via `Decrypt`).
- Disable the CMK → reads return `KeyResolutionError.KeyDestroyed` (410
  Gone at the API boundary).

## Rollback

Swap the resolver back to `SingleKeyResolver` / `PerScopeKeyResolver`.
Blobs written under a KMS DEK stay readable only while the CMK is live.

## Azure Key Vault + GCP KMS arms (shipped — Wave 8 close-out)

Two more `IBlobEncryptionKeyResolver` companions ship alongside the AWS
arm, same envelope-encryption shape, same error contract:

`ToolUp.Encryption.AzureKeyVault` — the DEK is wrapped/unwrapped by a Key
Vault KEK via `CryptographyClient` (Key Vault exposes wrap/unwrap, not
KMS-side data-key minting, so the DEK is generated locally):

```fsharp
open Azure.Identity
open ToolUp.Encryption.AzureKeyVault

let resolver =
    AzureKeyVaultKeyResolver.create
        (DefaultAzureCredential())
        "https://my-vault.vault.azure.net/keys/blob-kek/<version>"
```

`ToolUp.Encryption.GoogleCloudKms` — the DEK is `Encrypt`/`Decrypt`'d
under a KMS symmetric CryptoKey:

```fsharp
open Google.Cloud.Kms.V1
open ToolUp.Encryption.GoogleCloudKms

let resolver =
    GoogleCloudKmsKeyResolver.create
        (KeyManagementServiceClient.Create())
        "projects/p/locations/l/keyRings/r/cryptoKeys/kek"
```

Both stamp the KEK identifier into the `KeyId` (Azure/GCP ciphertext
doesn't self-describe its KEK the way an AWS KMS blob does), map
404/NotFound → `KeyNotFound`, disabled/forbidden → `KeyDestroyed`, and
support `createPerScope` for per-tenant key custody. Live verification is
env-gated (no offline emulator), mirroring the AWS arm.

## Phase 40 KMS-signing flavour (shipped — Wave 8 close-out)

`ToolUp.ArtefactSigning.AwsKms` provides an `IArtefactSigner` backed by an
AWS KMS asymmetric **ECC_NIST_P256** key — the private key never enters
process memory (the signer hashes locally and calls KMS `Sign` over the
digest, then converts the DER signature to JWS-shaped raw r‖s via the new
public `JwsBuilder` surface). Output is byte-identical to the in-process
signer, so `DefaultArtefactVerifier` validates it. See
[`40-artefact-signing.md`](40-artefact-signing.md).

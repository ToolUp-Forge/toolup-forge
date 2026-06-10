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

## Deferred arms

Azure Key Vault + GCP KMS mirror resolvers and the Phase 40 KMS-signing
flavour are drop-ins against the same contracts — see the phase doc.

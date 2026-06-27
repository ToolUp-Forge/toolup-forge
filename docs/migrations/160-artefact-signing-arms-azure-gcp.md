# Phase 160 — Azure Key Vault + GCP KMS artefact-signing arms

**What changes.** Two new opt-in, server-only companion packages complete
the Phase 40 `IArtefactSigner` cloud-signing story for non-AWS clouds:

- `ToolUp.ArtefactSigning.AzureKeyVault` — `AzureKeyVaultArtefactSigner`
  (`src/ArtefactSigning/AzureKeyVault/`), signs an artefact's JWS digest
  with an Azure Key Vault asymmetric **EC-P256** key via
  `CryptographyClient.Sign(SignatureAlgorithm.ES256, …)`.
- `ToolUp.ArtefactSigning.GoogleCloudKms` — `GoogleCloudKmsArtefactSigner`
  (`src/ArtefactSigning/GoogleCloudKms/`), signs via Cloud KMS
  `AsymmetricSign` (`EC_SIGN_P256_SHA256`).

Both produce a detached JWS byte-identical to the in-process
`DefaultArtefactSigner` / AWS-KMS arm, so the shipped
`DefaultArtefactVerifier` validates a signature from any arm. The private
key never enters process memory (the cloud KEK signs the local SHA-256
digest). `VerifyKey` serves the public JWK/PEM from the cloud key for the
`/_platform/signing-key/{keyId}` archival-verification endpoint.

**Signature-format note (per-cloud).** Azure Key Vault returns the ECDSA
signature already in IEEE P1363 `r‖s` (JWS ES256) form, so the Azure arm
passes it straight to `JwsBuilder.assembleDetachedJws` — **no DER→P1363
conversion**. GCP KMS (like AWS KMS) returns an ASN.1 DER signature, so
the GCP arm runs `JwsBuilder.derEcdsaToP1363` first.

**Consumer action: none.** Purely additive opt-in companions (GP 1 / GP 2
/ GP 13). A deployment that doesn't compose a KMS-backed signer is
byte-for-byte unchanged. Adopt only to keep artefact-signing keys in
Azure Key Vault / GCP KMS rather than in-process / AWS.

## Adopt (only if you want cloud-resident signing keys)

```fsharp
// Azure Key Vault (EC-P256 / ES256 key, `sign` operation granted)
open Azure.Identity
open ToolUp.ArtefactSigning.AzureKeyVault

let signer =
    AzureKeyVaultArtefactSigner.create
        (DefaultAzureCredential())
        (System.Uri "https://my-vault.vault.azure.net/keys/artefact-signing")
```

```fsharp
// GCP Cloud KMS (ASYMMETRIC_SIGN / EC_SIGN_P256_SHA256 key version)
open Google.Cloud.Kms.V1
open ToolUp.ArtefactSigning.GoogleCloudKms

let signer =
    GoogleCloudKmsArtefactSigner.createFromName
        (KeyManagementServiceClient.Create())
        "projects/p/locations/l/keyRings/r/cryptoKeys/k/cryptoKeyVersions/1"
```

Wire the resulting `IArtefactSigner` exactly where the in-process /
AWS-KMS signer was wired — the contract (`Sign` / `VerifyKey` / `KeyId`)
is identical.

## Verification

Sign/verify round-trip is env-gated live (no offline KMS emulator), per
the AWS-KMS arm + the Phase 22a resolver convention. The JWS-shape path
(header → digest → P1363 / DER→P1363 → detached JWS → verify with
`DefaultArtefactVerifier`) is exercised offline for both arms in
`ToolUp.ArtefactSigning.Tests` via the `IArtefactSignerContract` fixtures
(a local EC-P256 key stands in for the cloud KEK, reproducing each arm's
exact signature-format step).

## Rollback

Remove the companion `PackageReference` and revert to the in-process
`DefaultArtefactSigner` (or the AWS-KMS arm). No persisted-state or wire
change — a signature already produced verifies regardless of which arm
made it.

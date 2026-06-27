# ToolUp.ArtefactSigning.AzureKeyVault

Azure Key Vault-backed `IArtefactSigner` for ToolUp.ArtefactSigning
(Phase 160 / 40 / 22a). Signs artefacts with an Azure Key Vault (or
Managed HSM) asymmetric **EC-P256** (`ES256`) key — the private key never
enters process memory. The signer hashes the JWS signing input locally
(SHA-256) and asks Key Vault to sign the digest, then assembles the
detached JWS. The result is byte-identical to the in-process
`DefaultArtefactSigner` output, so the shipped `DefaultArtefactVerifier`
(or any JWS verifier holding the public key) validates it.

GP 1 — the Azure SDK dependency is isolated to this companion; it never
reaches the `ToolUp.ArtefactSigning` substrate. Server-only companion.

## Quick start

```fsharp
open Azure.Identity
open ToolUp.ArtefactSigning.AzureKeyVault

let cred = DefaultAzureCredential() // managed identity / env creds
let keyId = System.Uri "https://my-vault.vault.azure.net/keys/artefact-signing"
let signer = AzureKeyVaultArtefactSigner.create cred keyId

let! result = signer.Sign artefactBytes        // -> Ok ArtefactSignature
let! pubKey = signer.VerifyKey ()              // PEM + JWK for the verification endpoint
```

The produced `ArtefactSignature` verifies with the in-process
`DefaultArtefactVerifier` (the public key is served from Key Vault via
`GetKey`; the JWS shape is identical).

## EC-P256 only — and no DER→P1363 conversion

This companion covers `ES256`, the JWS shape compliance auditors expect
for non-repudiation. The Phase 40 `EdDSA` flavour stays in-process
(`DefaultArtefactSigner`). The Key Vault key must be created as **EC**
with curve **P-256** and key operations including `sign`.

Unlike the AWS KMS and GCP KMS arms — which receive an ASN.1 DER ECDSA
signature and run `JwsBuilder.derEcdsaToP1363` — **Azure Key Vault returns
the signature already in IEEE P1363 `r‖s` form** (the JWS ES256 shape),
because Key Vault follows the JOSE/JWA conventions. The signature segment
is passed straight to `JwsBuilder.assembleDetachedJws`.

## Verification

The signer requires a live Key Vault EC key (no offline fake — Key Vault
has no local emulator and `CryptographyClient` is not practically
mockable). Mirrors the env-gated live-arm convention of the AWS KMS arm /
the Phase 22a Azure encryption resolver / `AIProviders.Tests`. Verify
against a real key:

1. Create an EC `P-256` key with `sign` + `verify` operations; grant the
   deployment identity `Key Vault Crypto User` (Sign + Get Key).
2. `Sign` an artefact → verify the returned `ArtefactSignature` with
   `DefaultArtefactVerifier` (resolving the public key from `VerifyKey`).
3. Tamper the artefact → verification fails (`VerificationError.Tampered`).

The detached-JWS assembly path (header → SHA-256 digest → P1363 signature
→ `assembleDetachedJws`) is exercised offline against the shipped
`DefaultArtefactVerifier` in `ToolUp.ArtefactSigning.Tests` (a local
EC-P256 key produces the same P1363-over-digest shape Key Vault returns).

## License

Apache-2.0.

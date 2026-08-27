# ToolUp.ArtefactSigning.GoogleCloudKms

GCP Cloud KMS-backed `IArtefactSigner` for ToolUp.ArtefactSigning
(Phase 160 / 40 / 22a). Signs artefacts with a GCP Cloud KMS asymmetric
EC key (`EC_SIGN_P256_SHA256`) — the private key never enters process
memory. The signer hashes the JWS signing input locally (SHA-256) and
asks KMS to sign the digest via `AsymmetricSign`, then converts the DER
signature KMS returns into the raw `r‖s` shape JWS ES256 requires. The
detached JWS is byte-identical to the in-process `DefaultArtefactSigner`
output, so the shipped `DefaultArtefactVerifier` (or any JWS verifier
holding the public key) validates it.

GP 1 — the Google SDK dependency is isolated to this companion; it never
reaches the `ToolUp.ArtefactSigning` substrate. Server-only companion.

## Quick start

```fsharp skip=fragment
open Google.Cloud.Kms.V1
open ToolUp.ArtefactSigning.GoogleCloudKms

let client = KeyManagementServiceClient.Create() // ADC from the env
let keyVersion =
    "projects/my-proj/locations/europe-west2/keyRings/signing/cryptoKeys/artefact/cryptoKeyVersions/1"
let signer = GoogleCloudKmsArtefactSigner.createFromName client keyVersion

let! result = signer.Sign artefactBytes        // -> Ok ArtefactSignature
let! pubKey = signer.VerifyKey ()              // PEM + JWK for the verification endpoint
```

The produced `ArtefactSignature` verifies with the in-process
`DefaultArtefactVerifier` (the public key is served from KMS via
`GetPublicKey`; the JWS shape is identical).

## EC-P256 only

This companion covers `ES256`, the JWS shape compliance auditors expect
for non-repudiation. The Phase 40 `EdDSA` flavour stays in-process
(`DefaultArtefactSigner`). The KMS key must be created with purpose
`ASYMMETRIC_SIGN` and algorithm `EC_SIGN_P256_SHA256`. `Sign` /
`VerifyKey` address a specific **key version** resource name (ending in
`/cryptoKeyVersions/<v>`).

Like AWS KMS — and unlike Azure Key Vault, which returns the signature
already in IEEE P1363 form — GCP `AsymmetricSign` returns an **ASN.1 DER**
ECDSA signature, so this arm runs `JwsBuilder.derEcdsaToP1363` before
assembling the detached JWS.

## Verification

The signer requires a live KMS asymmetric key version (no offline fake —
KMS has no local emulator and `KeyManagementServiceClient` is not
practically mockable). Mirrors the env-gated live-arm convention of the
AWS KMS arm / the Phase 22a GCP encryption resolver / `AIProviders.Tests`.
Verify against a real key:

1. Create an `ASYMMETRIC_SIGN` / `EC_SIGN_P256_SHA256` CryptoKey; grant
   the deployment service account `cloudkms.signerVerifier` +
   `cloudkms.publicKeyViewer`.
2. `Sign` an artefact → verify the returned `ArtefactSignature` with
   `DefaultArtefactVerifier` (resolving the public key from `VerifyKey`).
3. Tamper the artefact → verification fails (`VerificationError.Tampered`).

The DER→P1363 conversion + detached-JWS assembly path is exercised
offline against the shipped `DefaultArtefactVerifier` in
`ToolUp.ArtefactSigning.Tests` (a local EC-P256 key produces the same
DER-over-digest shape KMS returns).

## License

Apache-2.0.

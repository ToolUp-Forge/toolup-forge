# ToolUp.ArtefactSigning.AwsKms

AWS KMS-backed `IArtefactSigner` for ToolUp.ArtefactSigning (Phase 40 /
22a). Signs artefacts with an AWS KMS asymmetric **ECDSA P-256**
(`ECC_NIST_P256`) key — the private key never enters process memory. The
signer hashes the JWS signing input locally (SHA-256) and asks KMS to
sign the digest, then converts the DER signature KMS returns into the raw
`r‖s` shape JWS ES256 requires. The detached JWS is byte-identical to the
in-process `DefaultArtefactSigner` output, so the shipped
`DefaultArtefactVerifier` (or any JWS verifier holding the public key)
validates it.

GP 1 — the AWS SDK dependency is isolated to this companion; it never
reaches the `ToolUp.ArtefactSigning` substrate. Server-only companion.

## Quick start

```fsharp
open Amazon.KeyManagementService
open ToolUp.ArtefactSigning.AwsKms

let kms = new AmazonKeyManagementServiceClient() // region + creds from the env
let signer = AwsKmsArtefactSigner.create kms "arn:aws:kms:eu-west-2:...:key/<asymmetric-ecc-key>"

let! result = signer.Sign artefactBytes        // -> Ok ArtefactSignature
let! pubKey = signer.VerifyKey ()              // PEM + JWK for the verification endpoint
```

The produced `ArtefactSignature` verifies with the in-process
`DefaultArtefactVerifier` (the public key is served from KMS via
`GetPublicKey`; the JWS shape is identical).

## ECDSA P-256 only

AWS KMS asymmetric signing offers RSA + ECC (P-256/384/521) + SM2 — not
Ed25519. The Phase 40 `EdDSA` flavour stays in-process
(`DefaultArtefactSigner`); this companion covers `ES256`, the JWS shape
compliance auditors expect for non-repudiation. The KMS key must be
created with `KeySpec = ECC_NIST_P256` and `KeyUsage = SIGN_VERIFY`.

## Verification

The signer requires a live KMS asymmetric key (no offline fake — KMS has
no local emulator and `IAmazonKeyManagementService` is not practically
mockable). Mirrors the env-gated live-arm convention of the AWS KMS
encryption resolver / `AIProviders.Tests`. Verify against a real key:

1. Create an asymmetric `ECC_NIST_P256` / `SIGN_VERIFY` KMS key; grant the
   deployment role `kms:Sign` + `kms:GetPublicKey`.
2. `Sign` an artefact → verify the returned `ArtefactSignature` with
   `DefaultArtefactVerifier` (resolving the public key from `VerifyKey`).
3. Tamper the artefact → verification fails (`VerificationError.Tampered`).

The pure DER→P1363 conversion + JWS-assembly helpers
(`JwsBuilder.derEcdsaToP1363` / `assembleDetachedJws`) are unit-tested
offline in `ToolUp.ArtefactSigning.Tests`.

## License

Apache-2.0.

# Migration — Phase 40: `ToolUp.ArtefactSigning` companion

**Status:** new opt-in server-only companion. No consumer is *required*
to adopt — nothing changes for a deployment that doesn't sign artefacts
(GP 13, zero-cost when unused). This doc is the "how to switch it on"
guide for consumers that want tamper-evident artefacts.

## What changes

Nothing, until you compose a signer. A new NuGet package
`ToolUp.ArtefactSigning` provides `IArtefactSigner` / `IArtefactVerifier`
and an anonymous public verification-key endpoint.

## Diff to apply (opt-in)

`*.fsproj` — add the package reference:

```xml
<PackageReference Include="ToolUp.ArtefactSigning" />
```

Composition root — construct a signer/verifier against the SDK's
already-present `ISecretStore` + `IAuditLog`, and register the signer in
DI plus mount the key endpoint on the anonymous-route group:

```fsharp
open ToolUp.ArtefactSigning

let signer   = DefaultArtefactSigner.createSystem secrets audit "signing-v1" EcdsaP256
let verifier = DefaultArtefactVerifier.create secrets
// services.AddSingleton<IArtefactSigner>(signer) etc.
// routes: choose [ SigningKeyHandler.routes; ...existing... ]
```

Then sign wherever an artefact is produced:

```fsharp
match! signer.Sign auditPackBytes with
| Ok signature -> // attach signature.DetachedJws as a sidecar / envelope field
| Error e      -> // SigningError.describe e
```

## Verification steps

- `dotnet build` clean with the new `PackageReference`.
- `GET /_platform/signing-key/{keyId}` returns `{ keyId, alg, pem, jwk }`
  without auth.
- A signed artefact verifies via `IArtefactVerifier.Verify`; a mutated
  byte fails with `VerificationError.Tampered`.
- An `ArtefactSigned` audit row appears under `_platform.signing` carrying
  the key id + artefact SHA-256 (never the bytes).

## Rollback

Remove the `PackageReference`, the DI registration, and the
`SigningKeyHandler.routes` mount. No persisted state outside the signing
keys in `ISecretStore` (`_platform/signing/{keyId}`), which can be left in
place or deleted.

## KMS-backed signing (shipped — Wave 8 close-out)

For keys that must never enter process memory, swap the in-process signer
for the KMS-backed flavour `ToolUp.ArtefactSigning.AwsKms` — the
`IArtefactSigner` contract is identical, so the composition-root change is
one line:

```fsharp
open Amazon.KeyManagementService
open ToolUp.ArtefactSigning.AwsKms

let kms = new AmazonKeyManagementServiceClient()
let signer = AwsKmsArtefactSigner.create kms "arn:aws:kms:...:key/<ecc-nist-p256-key>"
// verifier unchanged: DefaultArtefactVerifier.create secrets (public key
// served from KMS GetPublicKey via signer.VerifyKey, or seeded to the
// signing-key store).
```

The signer hashes the JWS signing input locally (SHA-256) and calls KMS
`Sign` over the digest; the DER signature KMS returns is converted to the
JWS raw-r‖s shape by the new public **`JwsBuilder`** surface
(`JwsBuilder.derEcdsaToP1363` / `assembleDetachedJws` /
`protectedHeaderEncoded` / `signingInput`). The produced detached JWS is
byte-identical to the in-process signer's, so `DefaultArtefactVerifier`
validates it unchanged.

**ECDSA P-256 only** — AWS KMS asymmetric signing does not offer Ed25519;
the `EdDSA` flavour stays in-process. The KMS key must be
`ECC_NIST_P256` / `SIGN_VERIFY`.

`JwsBuilder` (in `ToolUp.ArtefactSigning`) is now public so any
HSM/KMS-fronted signer can assemble a verifier-compatible JWS. Its pure
helpers (DER→P1363, JWS assembly) are unit-tested offline in
`ToolUp.ArtefactSigning.Tests`; the live KMS arm is env-gated (no
emulator).

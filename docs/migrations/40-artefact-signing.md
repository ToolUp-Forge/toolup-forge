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

## KMS-backed signing

For keys that must never enter process memory, swap the in-process signer
for the KMS-backed signing flavour (Phase 22a) — the `IArtefactSigner`
contract is identical, so the composition-root change is one line.

# ToolUp.ArtefactSigning

Cryptographic artefact-signing substrate for ToolUp.Platform (Phase 40).
Produces tamper-evident **detached-JWS** signatures over arbitrary
deployment artefacts — audit packs, exported reports, model
documentation — using per-deployment **ECDSA P-256** or **Ed25519**
signing keys, and exposes a **public verification-key endpoint** so a
relying party can validate signatures independently.

Server-only companion. Off by default and zero-cost when unused (GP 13):
nothing runs until you construct a signer.

> **Not** the same as the Phase 30a `IArtifactSigner` (note spelling:
> "Artefact" here vs "Artifact" there). Phase 30a signs *module-
> distribution artefacts* against an `ArtifactManifest` for the
> marketplace publish/install trust path. This companion signs *arbitrary
> byte payloads* for compliance non-repudiation. Different namespace
> (`ToolUp.ArtefactSigning`), no type collision.

## Quick start

```fsharp
open ToolUp.ArtefactSigning

// Compose against the SDK's ISecretStore + IAuditLog (already present in
// any ServerApp deployment). Auto-provisions a key on first use.
let signer   = DefaultArtefactSigner.createSystem secrets audit "signing-v1" EcdsaP256
let verifier = DefaultArtefactVerifier.create secrets

// Sign arbitrary bytes — the artefact is never embedded in the signature.
match! signer.Sign auditPackBytes with
| Ok signature ->
    // signature.DetachedJws : "base64url(header)..base64url(sig)"
    do! verifier.Verify(auditPackBytes, signature)   // Ok () | Error _
| Error e -> eprintfn "%s" (SigningError.describe e)
```

### Public verification-key endpoint

Mount the anonymous route so verifying parties can fetch the public key
(serves rotated-out keys too, for archival verification):

```fsharp
let app = choose [ SigningKeyHandler.routes; ...existing routes... ]
// GET /_platform/signing-key/{keyId}  ->  { keyId, alg, algorithm, pem, jwk }
```

### Helpers

- `ArtefactSigning.signAndEmbed` — sign + produce a sidecar `.sig` file.
- `ArtefactSigning.signedJsonEnvelope` / `verifyJsonEnvelope` — wrap a
  JSON payload as `{ payload, signature }` and round-trip it.
- `ArtefactSigning.signedPdfMetadata` — sign PDF bytes + return the
  `(key, value)` metadata pair to embed via your PDF toolkit.

## Keys & rotation

Key material lives in `ISecretStore` under scope `_platform`, key
`signing/{keyId}`. The signer reads it **per call** (never caches), so
rotating through the store takes effect immediately. To rotate: construct
a signer with a new `keyId`; new signs use it, and old signatures keep
verifying because the verifier resolves the public key by the signature's
`keyId` (the rotated-out blob stays discoverable).

For keys that must never enter process memory, wire a KMS-backed signer
(Phase 22a signing flavour) — the `IArtefactSigner` contract is identical.

## Portability (GP 12)

`IArtefactSigner` / `IArtefactVerifier` satisfy the six portability rules:
identity by value (`KeyId : string`, `byte[]` artefacts), async at every
boundary, failures-as-data (`Result<_, SigningError>` /
`Result<_, VerificationError>`), stateless between calls, no cross-shard
ordering, no timing-precision boundary.

## License

Apache-2.0.

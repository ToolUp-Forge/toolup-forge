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

```fsharp skip=fragment
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

```fsharp skip=fragment
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

## Application signing seam

Everything above signs bytes, which is the right shape for a publish
pipeline. An **application** signing its own payloads needs three further
facts on each signature, and `IApplicationSigner` carries them:

```fsharp skip=fragment
let provider = ApplicationSigning.inProcess secrets audit "app-signing-v1" EcdsaP256 "system"
let! signer  = ApplicationSigning.createActivated "system" provider
ApplicationSigning.registerProvider services provider |> ignore   // DI, opt-in

let! envelope = signer.SignPayload("invoice.issued", payloadBytes)
let! result   = signer.VerifyPayload("invoice.issued", payloadBytes, envelope)
```

- **Purpose binding.** The signature covers a versioned, length-prefixed
  framing of `(purpose, level, payload)`, so a signature minted for one
  use cannot be replayed as another, and relabelling the envelope breaks
  it.
- **Attestation level.** `Attribution` (the key is reachable from the
  signing process) or `IsolatedSigner` (the key is held outside it and
  never enters its memory), plus a `Reserved` case for future levels. The
  level is bound into the signed bytes, so it cannot be upgraded after
  the fact. Choose the provider that matches your custody:
  `ApplicationSigning.inProcess` or `ApplicationSigning.keyManaged`.
- **Key lifecycle as data.** `ISigningKeyLedger` records activation,
  retirement and revocation as append-only attributable events.
  Retirement is rotation — earlier signatures keep verifying. Revocation
  is distrust and reaches backwards — every signature under a revoked key
  is refused, carrying the recorded reason. A key with no recorded
  history verifies on its bytes, so a deployment that records nothing
  behaves exactly as it did before (GP 11).

Nothing is composed by default. A deployment that never calls
`ApplicationSigning.*` is unchanged and pays nothing (GP 13).

### The provider set

Both entry points take substrate the deployment already composed, so the
provider set is the cross-product of what is already shipped rather than a
new family of packages:

| Entry point | Key custody | Level |
|---|---|---|
| `ApplicationSigning.inProcess` | any `ISecretStore` — a local/file-backed store in development, or one of the managed-store companions in production | `Attribution` |
| `ApplicationSigning.keyManaged` | one of the key-management-backed `IArtefactSigner` companions (`ToolUp.ArtefactSigning.{AwsKms,AzureKeyVault,GoogleCloudKms}`) | `IsolatedSigner` |

Hardening the store behind `inProcess` does not change its level: the
level records whether the private key can reach process memory, and a key
fetched from a hardened store to sign locally still can.

Every provider is certified against one executable conformance pack
(`ISigningProviderConformance` in `ToolUp.ArtefactSigning.Tests`), which
is itself probe-verified: deliberately broken providers are run through
the same pack and it must reject each at the specific case that models
its defect. Adopting the seam:
[`docs/migrations/655-application-signing-seam.md`](../../docs/migrations/655-application-signing-seam.md).

## Portability (GP 12)

`IArtefactSigner` / `IArtefactVerifier` satisfy the six portability rules:
identity by value (`KeyId : string`, `byte[]` artefacts), async at every
boundary, failures-as-data (`Result<_, SigningError>` /
`Result<_, VerificationError>`), stateless between calls, no cross-shard
ordering, no timing-precision boundary.

## License

Apache-2.0.

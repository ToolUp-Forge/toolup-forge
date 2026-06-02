# Phase 30a — Signed module artefact format + verification

## What changes

Adds a portable signed-artefact substrate so a hub can publish a module
package (manifest + payload bytes) and an edge instance can verify the
publisher signature against a trust set before installation. Pure
substrate — no consumer-side change is required for deployments that
neither publish artefacts nor install them at runtime; the new
interfaces are opt-in.

New types in `ToolUp.Platform.Core` (`Shared/Types/ArtifactTypes.fs`):

- `SdkVersionRange` — `{ MinInclusive; MaxExclusive }` SemVer pair.
- `ContentHash` — newtype wrapping a SHA-256 hex string.
- `PublisherKeyId` — newtype wrapping a publisher key id string.
- `ArtifactDependency` — `{ PackageId; VersionRange }`.
- `ArtifactManifest` — `{ ModuleId; Version; SdkVersionRange; CodeHash;
  SchemaHash; Dependencies; PublisherKeyId }`.
- `SignedArtifact` — `{ Manifest; Payload; Signature }`.
- `ArtifactValidation` — `[<RequireQualifiedAccess>] Ok | Error of reason: string`.
- `ArtifactsSourceModule.value = "_platform.artefacts"` — reserved
  `SourceModule` for the audit family.

New interfaces in `ToolUp.Platform.Server`:

- `IArtifactSigner.Sign: ArtifactManifest * byte[] -> Async<SignedArtifact>`
  + `PublisherKeyId: PublisherKeyId`.
- `IArtifactVerifier.Verify: SignedArtifact -> Async<ArtifactValidation>`.
- `IPublisherKeyStore.{AddTrustedKey, RemoveTrustedKey, TryGetPublicKey,
  ListTrustedKeyIds}` — async-at-boundary, identity-by-value, stateless.

Default implementations:

- `Ed25519ArtifactSigner` / `Ed25519ArtifactVerifier` — Ed25519 over
  `canonical(manifest) || payload` via `BouncyCastle.Cryptography`
  (MIT). .NET 10 BCL surfaces only post-quantum primitives (MLDsa,
  SLHDsa), so the implementation uses BouncyCastle; the package is
  declared in `Directory.Packages.props` at 2.6.2 and pulled into
  `ToolUp.Platform.Server` only.
- `BlobBackedPublisherKeyStore` — persists trusted public keys to
  `_platform/trusted-publishers/{keyId}.pub` via `IBlobStorage`.

Three new `AuditEvent` cases (`AuditTypes.fs`), with matching payloads:

- `ArtifactSigned of ArtifactSignedPayload { Actor; PublisherKeyId;
  ModuleId; ArtifactVersion }`.
- `ArtifactVerified of ArtifactVerifiedPayload { PublisherKeyId;
  ModuleId; ArtifactVersion }`.
- `ArtifactRejected of ArtifactRejectedPayload { PublisherKeyId: string
  option; ModuleId; ArtifactVersion; Reason }`.

Two contract test packs in `ToolUp.Platform.Tests`:

- `IArtifactSignerContract` — `PublisherKeyId` non-empty; sign returns
  manifest + payload verbatim; signature is 64 bytes; Ed25519
  determinism.
- `IArtifactVerifierContract` — round-trip Ok; mutated payload Error;
  mutated manifest Error; untrusted publisher → `Error "untrusted
  publisher"`; invalid signature length Error.

## Six-rule portability audit (GP 12)

| Interface | Rule 1 (identity) | Rule 2 (async) | Rule 3 (retry data) | Rule 4 (stateless) | Rule 5 (no cross-shard order) | Rule 6 (precision) |
|---|---|---|---|---|---|---|
| `IArtifactSigner` | `PublisherKeyId` string newtype; `ModuleId` / `Version` strings | `Sign` returns `Async<SignedArtifact>` | Sign throws on missing key material; callers wrap | Each call derives from arguments + held private key; no inter-call state | N/A — signing is pure | Ed25519 deterministic; no clock contract |
| `IArtifactVerifier` | `PublisherKeyId` / `ModuleId` strings | `Verify` returns `Async<ArtifactValidation>` | Refusals surface as `ArtifactValidation.Error reason` — never thrown | Re-resolves publisher key from `IPublisherKeyStore` on every call | N/A — verification is pure function of inputs + store state | Ed25519 deterministic |
| `IPublisherKeyStore` | `PublisherKeyId` string newtype; keys as `byte[]` | All members return `Async<_>` | Implementation failures throw; callers wrap | No in-memory cache contract — each call round-trips to the backing store | Independent key reads | No timing contract |

## Diff to apply

### Consumer-side (sample — operator that publishes artefacts)

```fsharp
// 1. Generate a publisher key pair (one-time, e.g. via a CLI tool).
let publisherId = PublisherKeyId "myorg-publisher-2026"
let privateKey: byte[] = (* 32-byte Ed25519 private key from secure storage *)
let publicKey = Ed25519ArtifactSigner.derivePublicKey privateKey

// 2. Construct the signer (hub side).
let signer: IArtifactSigner = Ed25519ArtifactSigner.create publisherId privateKey

// 3. Sign a module artefact (hub side).
let manifest = {
    ModuleId = "MyModule"
    Version = "1.0.0"
    SdkVersionRange = { MinInclusive = "0.5.0"; MaxExclusive = "0.6.0" }
    CodeHash = ContentHash "<sha256-hex-of-payload>"
    SchemaHash = ContentHash "<sha256-hex-of-schemas>"
    Dependencies = []
    PublisherKeyId = publisherId
}
let payload: byte[] = (* DLL / source bundle bytes *)
let! signedArtifact = signer.Sign(manifest, payload)
```

### Consumer-side (sample — edge instance that installs artefacts)

```fsharp
// 1. Compose the key store + verifier on the edge (edgeScopeId is
// typically "_platform").
let keyStore: IPublisherKeyStore =
    BlobBackedPublisherKeyStore.create blobStorage
let verifier: IArtifactVerifier =
    Ed25519ArtifactVerifier.create keyStore auditLog "_platform"

// 2. Seed the trust set (operator action; out-of-band).
do! keyStore.AddTrustedKey(PublisherKeyId "myorg-publisher-2026", publicKeyBytes)

// 3. Before installing an artefact, verify it.
match! verifier.Verify signedArtifact with
| ArtifactValidation.Ok -> // proceed with install
| ArtifactValidation.Error reason -> // refuse install; reason logged by verifier
```

## Verification steps

1. `dotnet build src/ToolUp.Platform.Core/ToolUp.Platform.Core.fsproj` —
   confirms the shared types compile.
2. `dotnet build src/ToolUp.Platform.Server/ToolUp.Platform.Server.fsproj`
   — confirms interfaces + Ed25519 + blob-backed key store + audit
   wiring compile.
3. `dotnet build ToolUp.Forge.sln` — confirms cross-companion fanout
   stays clean.
4. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj`
   — runs `Ed25519ArtifactSubstrateTests` covering both contract packs
   + the substrate-specific assertions (round-trip, blob-backed store
   round-trip + idempotent remove, audit emission, untrusted-publisher
   refusal verbatim).

## Rollback

The substrate is opt-in by construction. To roll back: revert the
forge commit; no consumer-side change required since no shipped
consumer references the new interfaces yet (status ⛔ N-A across the
matrix). Trusted-publisher blobs written to
`_platform/trusted-publishers/{keyId}.pub` are inert when no verifier
is wired and can be left in place or removed at the operator's
discretion.

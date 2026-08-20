# 655 — application signing seam

**What changes:** `ToolUp.ArtefactSigning` gains an application-facing signing seam beside the
existing byte-level one. `IArtefactSigner`, `IArtefactVerifier`, `ArtefactSignature`, the sidecar
helpers and the public-key endpoint are **unchanged**. Nothing is composed by default: a deployment
that does not call `ApplicationSigning.*` is byte-for-byte identical and pays nothing (GP 11, GP 13).

New surface, all additive:

| Type | Purpose |
|---|---|
| `IApplicationSigner` | sign/verify an application's own payloads |
| `SignedPayloadEnvelope` | a signature plus the purpose and attestation level bound into it |
| `AttestationLevel` | `Attribution` \| `IsolatedSigner` \| `Reserved of label` |
| `ISigningKeyLedger` + `SecretStoreSigningKeyLedger` | key lifecycle as append-only attributable events |
| `SigningProvider` + `ApplicationSigning` | provider descriptors and the composition entry points |

## Why a new envelope rather than fields on `ArtefactSignature`

Widening the existing record would retype its constructor — a break for every publish-pipeline call
site and for the public-API baseline. `SignedPayloadEnvelope` wraps an ordinary `ArtefactSignature`,
so anything that already consumes one still does.

## Attestation levels

The level states what a signature claims, and is **bound into the signed bytes** — editing it in the
envelope breaks the signature rather than upgrading the claim.

- **`Attribution`** — the payload is attributed to this deployment's key, and nothing more. The
  private key is reachable from the signing process. This is the honest level for a key held in the
  deployment's own secret store, however well that store is guarded.
- **`IsolatedSigner`** — the private key is held outside the signing process, which sends a digest
  out and receives a signature back. Compromising the application host does not yield the key. It
  does **not** claim the payload was produced by trusted code.
- **`Reserved of label`** — forward compatibility for a level that makes a claim about the executing
  environment itself. None is implemented. An unrecognised label round-trips rather than failing to
  parse; treat it as **unverified**.

## Adopting it

```fsharp
open ToolUp.ArtefactSigning

// Compose once, at the composition root.
let provider = ApplicationSigning.inProcess secrets audit "app-signing-v1" EcdsaP256 "system"
let! signer = ApplicationSigning.createActivated "system" provider   // records the activation
ApplicationSigning.registerProvider services provider |> ignore

// Anywhere the application signs its own payload.
let signer = sp.GetRequiredService<IApplicationSigner>()
let! envelope = signer.SignPayload("invoice.issued", payloadBytes)
let! result = signer.VerifyPayload("invoice.issued", payloadBytes, envelope)
```

`registerProvider` also registers the provider's `IArtefactSigner` / `IArtefactVerifier` /
`ISigningKeyLedger` with `TryAddSingleton`, so a signer the deployment already composed for its
publish pipeline is never displaced.

For a key held by an external key-management service, use `ApplicationSigning.keyManaged` with that
service's `IArtefactSigner` (`ToolUp.ArtefactSigning.{AwsKms,AzureKeyVault,GoogleCloudKms}`) — the
level is `IsolatedSigner`, and passing an in-process signer there would overstate the claim, which is
why it is a separate entry point rather than a parameter. `inProcess` takes whatever `ISecretStore`
the deployment already composed, so no new package is needed for either provider.

## Purpose binding

`SignPayload` signs a versioned, length-prefixed framing of `(purpose, level, payload)`. A signature
minted as `"invoice.issued"` therefore cannot be replayed as `"refund.approved"`, and relabelling the
envelope breaks it. Choose stable purpose strings — changing one invalidates existing signatures.

## Key lifecycle

Rotation and revocation are **recorded events**, not key-file swaps:

```fsharp
do! ApplicationSigning.retire   ledger "operator-1" "app-signing-v1"
do! ApplicationSigning.activate ledger "operator-1" "app-signing-v2"
do! ApplicationSigning.revoke   ledger "operator-1" "app-signing-v1" "key material disclosed"
```

- **Retirement is rotation, not distrust** — the outgoing key's material stays where it is, so
  signatures made under it keep verifying. Do not delete a retired key's secret.
- **Revocation reaches backwards** — every signature under a revoked key is refused, including ones
  made before the revocation, and the refusal carries the recorded reason.
- A key with **no** recorded history verifies on its bytes, exactly as it did before this surface
  existed. An empty ledger revokes nothing.

The default ledger persists into the deployment's own `ISecretStore` under `_platform`. Appends are
read-modify-write and serialised in-process only; a deployment that rotates from several hosts at
once should implement `ISigningKeyLedger` over a store with a compare-and-swap.

## Verification steps

1. `dotnet build ToolUp.Forge.sln`
2. `dotnet run --project src/ToolUp.ArtefactSigning.Tests/ToolUp.ArtefactSigning.Tests.fsproj`
3. Confirm the provider-conformance list reports a pass for each provider, and that the
   `provider-conformance probe` cases pass — those run deliberately broken providers through the same
   pack and assert it rejects each at the specific case that models its defect.

## Rollback

Remove the `ApplicationSigning.registerProvider` / `register` call. Nothing else references the seam;
the byte-level signing path, the publish pipeline and the public-key endpoint are untouched, and any
recorded ledger events are inert data.

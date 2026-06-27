# Phase 215 — module-binding revocation list + transparency log

**Forge commit:** _(this commit)_
**Composes onto:** [Phase 165](165-module-binding-verifier.md) (the verifier) +
[Phase 166](166-module-binding-manifest-deploy-time-stamp.md) (the stamp manifest) +
[Phase 40](40-artefact-signing.md) (`JwsBuilder` detached-JWS path).

## What changes

Sign + verify (165/166) proves a stamp was minted under a trusted anchor, but
two pieces were missing that make signing trustworthy *at scale*:

1. **A kill switch.** A compromised anchor key has no revocation path — every
   stamp it ever minted keeps verifying.
2. **An audit.** There is no append-only record of what the gate admitted.

Phase 215 adds two opt-in seams over the Phase 165
`DefaultModuleBindingVerifier`, **both off by default** so a deployment that
configures neither is byte-for-byte unchanged (GP 13):

- **`ToolUp.Platform.Core`** (tier-shared, Fable-safe — beside
  `IModuleBindingVerifier`):
  - `IBindingRevocationList` — `IsRevoked: anchorId * stampId -> bool`,
    consulted **after** a stamp verifies cryptographically and **before** the
    verifier returns `Allowed`.
  - `IBindingTransparencyLog` — `Record: BindingDecision -> Async<unit>`,
    an append-only sink the verifier records every admit/deny into.
  - `BindingDecision` (the recorded shape: `ModuleId` / `AnchorId` /
    `StampId` / `Admitted` / `Reason` / `TimestampUtc`).
  - No-op defaults `BindingRevocationList.none` / `BindingTransparencyLog.none`.
- **`ToolUp.Platform.Server`** — `ModuleBindingRevocation` (crypto-free, same
  posture as the Phase 166 manifest): the `module-revocations.json` format +
  `parse` / `load`, the `stampId` derivation (base64url SHA-256 over the
  stamp's canonical material), the `RevocationSet` → `IBindingRevocationList`
  adapter, and a file-backed `FileBindingTransparencyLog` (JSON Lines).
- **`ToolUp.ArtefactSigning`** —
  - `DefaultModuleBindingVerifier.createWith anchors revocation log` — the
    verifier additionally consults the revocation list + records to the log.
  - `SignedRevocationList.verifyAndParse` / `loadSigned` — verifies a detached
    JWS over the revocation JSON (the Phase 40 primitives) against a public
    verify key **before** parsing, failing closed on a bad signature. An
    unsigned list an attacker can overwrite would silently un-revoke a
    compromised key, so the signed loader is the recommended path.

### The decision (load-bearing rule)

`Verify`, after the Phase 165 crypto check:

| Crypto | Revocation list | Outcome |
|---|---|---|
| Rejected | (any) | **Rejected** (unchanged) |
| Allowed | none configured | **Allowed** — zero-cost path, byte-identical to pre-215 (GP 13) |
| Allowed | anchor / stamp **not** revoked | **Allowed** |
| Allowed | anchor / stamp **revoked** | **Rejected** — a revoked stamp denies regardless of a valid signature |

When neither seam is configured the verifier takes a single branch and does
**no** revocation check, **no** record, and **no** `stampId` computation —
exactly the pre-215 gate.

## Diff to apply

This refactor is **additive and opt-in**. Existing consumers need **no
change** — `DefaultModuleBindingVerifier.create` is unchanged and byte-for-byte
the pre-215 verifier. A deployment opts in via `createWith`:

```fsharp
open ToolUp.Platform
open ToolUp.ArtefactSigning

// 1) Load the deployment's signed revocation list (fail-closed).
let revocation =
    match SignedRevocationList.loadSigned revSignerPublicKey EcdsaP256
              "module-revocations.json" "module-revocations.json.jws" with
    | Ok list -> list
    | Error e -> failwithf "revocation list did not verify: %s" e   // fail closed

// 2) Append-only transparency log (file flavour; or a blob-backed sink).
let log = FileBindingTransparencyLog("/var/log/toolup/module-bindings.jsonl")

// 3) Build the verifier with both seams (pass *.none to opt into one only).
let verifier =
    DefaultModuleBindingVerifier.createWith anchors revocation log

let app =
    ServerApp.empty
    |> ServerApp.withModuleBindingVerifier verifier
    |> ServerApp.addModule (myModule |> ServerModule.withBindingStamp stamp)
```

`module-revocations.json`:

```json
{
  "version": 1,
  "revokedAnchors": [ "release-2025" ],
  "revokedStamps":  [ { "anchorId": "release-2026", "stampId": "<base64url-sha256>" } ]
}
```

A revocation author computes `stampId` with `ModuleBindingRevocation.stampId`
(or reads it off a transparency-log line). Revoke a whole anchor to kill every
stamp under a compromised key; revoke a single `stampId` to retire one stamp.

## Verification steps

1. `dotnet build ToolUp.Forge.sln` — additive change, build stays green.
2. `dotnet run --project src/ToolUp.ArtefactSigning.Tests/ToolUp.ArtefactSigning.Tests.fsproj`
   — the `Phase 215 — module-binding revocation + transparency` suite pins:
   valid-but-revoked (by anchor and by stampId) denies; empty/unrelated list
   admits unchanged; transparency log records both an admit and a deny; the
   no-op defaults behave identically to the bare verifier (GP 13);
   `addModule` drops a revoked module; `stampId` is deterministic +
   stamp-specific; the JSON parser reads both arrays and fails closed on a
   newer major; the signed loader accepts a correct signature and fails closed
   on a tampered body / algorithm mismatch.
3. `cd samples/MinimalClient && dotnet fable -o output --noCache` — the Core
   contract addition is BCL-pure (`Async` / `DateTimeOffset`), so the Fable
   client tier compiles unchanged.

## Rollback

Additive throughout. Revert the single forge commit; consumers that never
called `createWith` (i.e. everyone on `create`) are unaffected.

# Phase 165 — `IModuleBindingVerifier`: opt-in module-binding gate

**Forge commit:** `463f346`

## What changes

`ServerApp.addModule` gains a second, opt-in gate beside the existing
`ModuleFilter.matches` name filter. A deployment may now refuse to load
modules that are not *bound* to it by a trust anchor — without changing the
behaviour of any deployment that doesn't opt in.

Three additive surfaces:

- **`ToolUp.Platform.Core`** — a tier-shared contract (pure types, no crypto,
  Fable-safe): the `ModuleBindingStamp` DU (`JwsStamp` / `MacStamp`), the
  `BindingOutcome` DU (`Allowed` / `Rejected of reason`), and the
  `IModuleBindingVerifier` interface
  (`Verify: moduleId * stamp option -> BindingOutcome`).
- **`ServerApp` / `ServerModule`** — `ServerApp.ModuleBindingVerifier:
  IModuleBindingVerifier option` (default `None`) set via
  `ServerApp.withModuleBindingVerifier`; `ServerModule.BindingStamp:
  ModuleBindingStamp option` (default `None`) set via
  `ServerModule.withBindingStamp`.
- **`ToolUp.ArtefactSigning`** — `DefaultModuleBindingVerifier` over a
  value-typed set of `ModuleBindingAnchor`s (`AsymmetricAnchor` carrying an
  ECDSA P-256 / Ed25519 public verify key; `SymmetricAnchor` carrying an
  HMAC-SHA256 MAC key). It reuses the Phase 40 detached-JWS verify
  primitives for the asymmetric path and a constant-time HMAC compare for
  the symmetric path — **no new crypto**.

### The gate (load-bearing rule)

`addModule`'s decision, after the name filter:

| Verifier configured? | Stamp present? | Outcome |
|---|---|---|
| No | No | **Allowed** — single cheap branch, byte-identical to pre-165 (GP 13) |
| No | Yes | **Rejected** — fail closed (a stamped module is self-protecting) |
| Yes | No | verifier decides (default verifier: **Allowed**) |
| Yes | Yes | verifier decides — **Allowed** iff some anchor verifies, else **Rejected** |

The canonical bytes a stamp is verified against are the UTF-8 **module id of
the module being gated** — recomputed in `addModule`, never read from a
self-asserted field — so a stamp minted for module A cannot be replayed onto
module B. A rejected module is dropped silently today (Phase 169 will emit a
module-load startup event at the drop point when it ships).

## Diff to apply

This refactor is **additive and opt-in**. Existing consumers need **no
change** — the new `ServerApp` / `ServerModule` fields default to `None`, and
`addModule` with neither a verifier nor a stamp is byte-for-byte the pre-165
path.

A deployment that wants to opt in composes a verifier over its trust anchors
and stamps the modules it controls:

```fsharp
open ToolUp.Platform
open ToolUp.ArtefactSigning

// The deployment's trust anchors (the keyring / custody is a consumer
// concern — forge only verifies presented stamps against these).
let anchors =
    [ AsymmetricAnchor("release-2026", EcdsaP256, releasePublicKeySpkiDer)
      SymmetricAnchor("ci-hmac", ciMacKeyBytes) ]

let app =
    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withModuleBindingVerifier (DefaultModuleBindingVerifier.create anchors)
    |> ServerApp.addModule (
        myModule |> ServerModule.withBindingStamp stamp)   // stamp from a deploy-time stamper
```

`addModule` then drops `myModule` unless `stamp` verifies under one of the
anchors. Unstamped modules continue to load (this verifier admits them); a
deployment that wants *every* module stamped layers that policy above the
verifier.

## Verification steps

1. `dotnet build ToolUp.Forge.sln` — additive change, build stays green.
2. `dotnet run --project src/ToolUp.ArtefactSigning.Tests/ToolUp.ArtefactSigning.Tests.fsproj`
   — the `Phase 165 — module-binding gate` suite pins: unbound module loads
   (GP 13); verifier-with-no-anchors + unstamped loads; correctly
   asymmetric- and symmetric-stamped modules load; ≥1 symmetric + ≥1
   asymmetric anchor verify in one deployment; replayed (cross-module),
   foreign-key, and tampered-MAC stamps are rejected; absent-anchor +
   present-stamp fails closed; present-stamp + no-verifier fails closed.
3. `cd samples/MinimalClient && dotnet fable -o output --noCache` — the Core
   contract is BCL-pure, so the Fable client tier compiles unchanged.

## Rollback

Additive throughout. Revert the single forge commit; consumers that never
called `withModuleBindingVerifier` / `withBindingStamp` are unaffected.

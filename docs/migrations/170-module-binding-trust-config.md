# Migration — Phase 170: Module-binding trust-anchor config surface (`fromEnv`)

Gives the Phase 165 module-binding gate a **runtime** way to declare its trust anchors + the
unbound-allowed policy bit, so one container image is configured for binding by environment alone
(matching the Wave 22 runtime-config theme) rather than baking anchors at compile time. Symmetric key
material resolves through `ISecretStore`, never plaintext config.

## What changes

- **`ServerConfig.ModuleBindingTrust : ModuleBindingTrustConfig`** (new field). Default =
  `{ Anchors = []; AllowUnbound = true }` — binding **off**, byte-for-byte the pre-binding pipeline
  (GP 13). Additive; every existing `ServerConfig` literal that copy-updates `defaults` inherits it.
- **`ModuleBindingAnchorRef`** (Core, BCL-pure) — a *description* of a trust anchor:
  `SymmetricAnchorRef (keyId, secretScope, secretKey)` (MAC key referenced indirectly, resolved via
  `ISecretStore`) or `AsymmetricAnchorRef (keyId, algorithm, publicKeyBase64)` (public verify key
  inline).
- **`fromEnv`** populates the block from:
  - `TOOLUP_MODULE_BINDING_ALLOW_UNBOUND` — `1`/`true` ⇒ admit unstamped modules (default `true`).
  - `TOOLUP_MODULE_BINDING_ANCHORS` — a `;`-separated list of
    `mac:<keyId>:<secretScope>:<secretKey>` or `asym:<keyId>:<alg>:<base64pubkey>`. A malformed entry
    is warned and skipped.
- **`ModuleBindingTrustResolver.resolve` + `ModuleBindingTrustValidator`** (in
  `ToolUp.ArtefactSigning`) — the resolver turns the config into an `IModuleBindingVerifier`
  (resolving symmetric secrets via `ISecretStore`); the validator (`IConfigValidator`) runs the same
  resolution at preflight and **fails startup closed** if a configured symmetric anchor's secret does
  not resolve — a named gap, never a silent disable.

## How to adopt

A deployment opts in by setting the env vars and wiring the resolved verifier + validator at compose
time:

```fsharp
open ToolUp.ArtefactSigning

// secretStore is the deployment's resolved ISecretStore.
let verifier =
    ModuleBindingTrustResolver.resolve config.ModuleBindingTrust secretStore
    |> Async.RunSynchronously
    |> function
       | Ok v -> v
       | Error e -> failwithf "module-binding trust anchors did not resolve: %s" e

app
|> ServerApp.withModuleBindingVerifier verifier
|> ServerApp.withConfigValidator (ModuleBindingTrustValidator(config.ModuleBindingTrust, secretStore))
```

Environment:

```bash
# Symmetric anchor whose 32-byte HMAC key lives in the secret store under _platform/mac-key-1
export TOOLUP_MODULE_BINDING_ANCHORS="mac:anchor-1:_platform:mac-key-1"
# Require every module to be bound:
export TOOLUP_MODULE_BINDING_ALLOW_UNBOUND=false
```

Stamp the modules with the matching anchor via `toolup stamp` (Phase 166), then ship the
`module-bindings.json` alongside the binary.

## Verification

- With no `TOOLUP_MODULE_BINDING_*` set, `ServerConfig.fromEnv` yields
  `ModuleBindingTrust = { Anchors = []; AllowUnbound = true }` — behaviour is byte-for-byte as before
  (GP 13).
- A configured symmetric anchor whose secret does not resolve refuses startup with a named
  `module-binding trust anchors did not resolve: …` error (the validator returns `Error`).
- A module stamped under anchor X verifies only where X is a configured anchor; under a different
  anchor (or none) it fails closed.

## Rollback

Purely additive. Unset the `TOOLUP_MODULE_BINDING_*` env vars (or drop the `withModuleBindingVerifier`
/ `withConfigValidator` wiring) and the deployment returns to the unbound, byte-for-byte-prior path.

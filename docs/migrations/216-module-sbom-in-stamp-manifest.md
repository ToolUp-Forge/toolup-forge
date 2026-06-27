# Phase 216 — module SBOM inside the stamp manifest

**Forge commit:** _(this commit)_
**Composes onto:** [Phase 166](166-module-binding-manifest-deploy-time-stamp.md) (the stamp manifest) +
[Phase 165](165-module-binding-verifier.md) (the verifier) +
[Phase 40](40-artefact-signing.md) (`JwsBuilder` detached-JWS path).

## What changes

Sign + verify (165/166) binds *that* a module is stamped; it says nothing about
*what's inside it*. Phase 216 embeds an optional, signed **SBOM** (software
bill-of-materials — assemblies, package references, per-component content hashes)
in the same detachable stamp-manifest entry. The SBOM carries its own binding
stamp — the SAME shape (`JwsStamp` / `MacStamp`) minted under the SAME anchor as
the module's stamp — over the SBOM's canonical bytes, so it is tamper-evident and
verified as a unit with the stamp.

**Purely additive (GP 11 / GP 13):** a manifest entry with no `sbom` section
verifies byte-for-byte as a Phase-166 stamp-only entry. A deployment that never
stamps an SBOM is unchanged, and never touches the new `IModuleSbomVerifier`
surface.

### Surfaces added

- **`ToolUp.Platform.Core`** (tier-shared, Fable-safe — beside
  `IModuleBindingVerifier`):
  - `ModuleSbomComponent` (`Name` / `Version` / `Sha256`), `ModuleSbom`
    (`Components`), `ModuleSbomStamp` (`Sbom` + `Signature: ModuleBindingStamp`).
  - `IModuleSbomVerifier` — `VerifySbom: moduleId * ModuleSbomStamp -> BindingOutcome`.
    Separate from `IModuleBindingVerifier` so the Phase 165 interface is unchanged.
- **`ToolUp.Platform.Server`** — `ModuleBindingManifest` gains crypto-free
  parsing: `parseSboms` / `loadSboms` / `loadSbomsFromDir` return the
  `moduleId → ModuleSbomStamp` map (only entries that carry an `sbom` section).
  An `sbom` present with no `sbomSig` is an `Error` (fail-closed, never silently
  dropped).
- **`ToolUp.ArtefactSigning`** —
  - `ModuleSbomSigning.canonicalBytes moduleId sbom` — the bytes the SBOM
    signature covers (order-independent, control-character-separated, bound to
    the module id). The single source of truth the verifier and any stamper sign
    against.
  - `DefaultModuleBindingVerifier` implements `IModuleSbomVerifier`;
    `DefaultModuleBindingVerifier.verifySbom anchors moduleId sbom` is the module
    helper. Reuses the exact module-stamp verify primitives (Phase 40
    `Jws.verify` / `HMACSHA256`).
- **`toolup stamp` CLI** — `--sbom-file <path>` (repeatable; the file's content
  SHA-256 becomes a component) and `--sbom-package <name@version>` (repeatable).
  The SBOM is signed under the same key as the stamp and merged into the entry;
  re-stamping regenerates it. The CLI's pure-BCL canonicalisation is pinned to
  `ModuleSbomSigning.canonicalBytes` by a round-trip test.

### The manifest entry shape

A stamped-with-SBOM entry (the CLI writes this; the reader parses it):

```json
{
  "version": 1,
  "bindings": {
    "Sales": {
      "kind": "jws",
      "detachedJws": "<stamp over the module id>",
      "sbom": {
        "components": [
          { "name": "Sales.dll", "version": "", "sha256": "<base64url-sha256>" },
          { "name": "ToolUp.Platform.Server", "version": "0.9.4", "sha256": "" }
        ]
      },
      "sbomSig": { "kind": "jws", "detachedJws": "<sig over the SBOM canonical bytes>" }
    }
  }
}
```

A stamp-only entry (`kind` + signature, no `sbom`/`sbomSig`) is exactly the
Phase-166 shape and parses identically.

## Diff to apply

This refactor is **additive and opt-in**. Existing consumers need **no change** —
`DefaultModuleBindingVerifier.create` / `createWith` are unchanged, and a
manifest with no `sbom` section verifies as before. To opt in:

```powershell
# Stamp a module AND record + sign an SBOM of its assemblies + packages.
toolup stamp --manifest module-bindings.json `
             --module Sales `
             --key-id k1 --mac-key-file mac.key `
             --sbom-file bin/Sales.dll `
             --sbom-package ToolUp.Platform.Server@0.9.4
```

```fsharp
open ToolUp.Platform
open ToolUp.ArtefactSigning

// Verify the SBOM read back from the manifest, alongside the module stamp.
match ModuleBindingManifest.loadSboms "module-bindings.json" with
| Ok sboms ->
    sboms
    |> Map.iter (fun moduleId sbom ->
        match DefaultModuleBindingVerifier.verifySbom anchors moduleId sbom with
        | Allowed -> ()
        | Rejected r -> failwithf "SBOM for %s did not verify: %s" moduleId r)
| Error e -> failwithf "manifest SBOM section did not parse: %s" e
```

## Verification steps

1. `dotnet build ToolUp.Forge.sln` — additive change, build stays green.
2. `dotnet run --project src/ToolUp.ArtefactSigning.Tests/ToolUp.ArtefactSigning.Tests.fsproj`
   — `Phase 216 — module SBOM in stamp manifest` pins: a signed SBOM (JWS + MAC)
   admits; a tampered/added component fails closed; an SBOM is bound to its
   module id (no cross-module replay); a wrong anchor fails; `parseSboms` reads a
   signed SBOM that then verifies, and surfaces an in-transit edit as a deny; a
   stamp-only manifest yields no SBOM (GP 13); an `sbom` with no `sbomSig` is an
   `Error`.
3. `dotnet run --project src/ToolUp.Cli.Tests/ToolUp.Cli.Tests.fsproj` — the
   `stamp` round-trip pins the CLI's pure-BCL SBOM minting to the server verifier
   (mint here → verify there), and that re-stamping regenerates the SBOM.
4. `cd samples/MinimalClient && dotnet fable -o output --noCache` — the Core
   contract addition is BCL-pure (records + an interface, no crypto), so the
   Fable client tier compiles unchanged.

## Rollback

Additive throughout. Revert the single forge commit; consumers that never stamped
an SBOM (everyone on `create` / `createWith` with stamp-only manifests) are
unaffected.

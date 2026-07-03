# Phase 285 — `IComponentRegistryContract` conformance pack (consumer migration)

**What changes.** A reusable GP-12 contract pack — `ComponentRegistryContract` in
`ToolUp.Platform.Tests` — asserts the Phase 279 component-identity laws against any component
registry, so an alternative or future registry validates against the same id-stability bar. Mirrors
the shipped contract packs (`IJobSchedulerContract`, `IShareTokenStoreContract`,
`IClientHostCapabilitiesContract`). The laws:

1. Declared ids are stable across a display-name **rename**.
2. Ids are stable across registration **re-order** (non-positional).
3. Default (no explicit id) derivation is **deterministic** and non-positional.
4. A **duplicate** explicit id is rejected at compose.
5. The Phase 280 manifest enumeration is **complete + id-keyed** (the manifest's module ids are
   exactly the registry's resolved ids).

The pack is parameterised over a `ComponentRegistryWitness` (operations over module declarations),
bound to the in-tree default `ServerApp` / `ServerModule` registry as its first conformance witness,
and wired into `Build.fsproj -- VerifyAll` so an identity-law regression fails CI. A self-test
(`selfTests`) binds deliberately unstable / positional / duplicate-tolerating witnesses and proves
each law fails against them — the pack has teeth.

**Scope.** Test/build infrastructure only. No runtime surface, no public API, byte-for-byte absent
from any consumer build (GP 11 / GP 13). **No consumer action.**

## Binding an alternative registry

```fsharp
let myWitness : ComponentRegistryContract.ComponentRegistryWitness = {
    ResolveModuleIds  = fun decls -> …   // (displayName, explicitId option) list -> ComponentId list
    ManifestModuleIds = fun decls -> …   // the Phase 280 manifest's module ids
    EnsureUnique      = …                 // the registry's compose-time duplicate-id enforcement
}
let myTests = ComponentRegistryContract.laws "my registry" myWitness
```

## Verification

- `Contracts/ComponentRegistryContract.fs`: `tests` (the in-tree default registry) runs green under
  `VerifyAll`; `selfTests` proves a rename-unstable / positional / duplicate-tolerating registry
  fails the corresponding law with a readable assertion.

## Rollback

Revert the Phase 285 forge commit — no runtime code path changes; nothing else references the pack.

## SDK-ADOPTION

⛔ N-A — test/build infra, no consumer-facing surface.

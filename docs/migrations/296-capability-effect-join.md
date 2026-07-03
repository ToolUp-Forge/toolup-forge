# Migration 296 — `CompanionCapability` effect-join surface

**Status:** additive — **pure-value operators over [Phase 282](282-companion-capability.md); no consumer action; a deployment that never reads it is byte-for-byte unchanged (GP 11) and pays nothing (GP 13).**

## What changes

[Phase 282](282-companion-capability.md) made a companion's effect / determinism / distributed-readiness
posture a typed value. This phase exposes it in a form an external **effect signature joins
componentwise**, and computes the **composed app's effect class** as the join of its companions'
capabilities — the static "is this composition pure / what can it touch?" property an authoring tool
needs to reason about a composition before it runs.

- **Axis joins** (in `ToolUp.Platform.Core`, `Shared/CompanionCapability.fs`): `EffectClass.join`
  (`Pure ⊔ Effecting = Effecting`), `DeterminismSource.join` (factor-set union — `clock ⊔ random =
  {clock; random}`), `Readiness.join` (`DistributedReady ⊔ DevOnly = DevOnly`). Each axis is a
  join-semilattice; the bottom (pure / deterministic / distributed-ready) is the identity.

- **`CompanionCapability.join` / `joinAll`** — the componentwise join of the whole record.
  Associative + commutative + idempotent, with `CompanionCapability.identity` the two-sided identity
  (so an undeclared companion, which contributes `identity`, never changes the join — GP 11).
  `joinAll []` = `identity`.

- **`CapabilitySignature = Map<ComponentId, CompanionCapability>`** — the external effect signature,
  keyed by the same stable `ComponentId` the [Phase 280](280-introspectable-composition-manifest.md)
  manifest enumerates. `CompanionCapability.resolve` looks a component up (undeclared → the identity);
  **`CompanionCapability.composedEffect`** folds the whole signature to the composition's effect class —
  the value the manifest surfaces (opt-in, read-only) as "what can this composition touch?".

## How to adopt (opt-in)

```fsharp
open ToolUp.Platform

// Declare each composed unit's capability, keyed by the manifest's ComponentId:
let signature: CapabilitySignature =
    Map [
        ComponentId.forCompanionImpl "IAuditSink" "splunk-archive", CompanionCapability.distributedEffecting
        ComponentId.forCompanionSlot "IJobScheduler",               CompanionCapability.devOnlyEffecting
        ComponentId.forCompanionSlot "IBlobStorage",                CompanionCapability.pure'
    ]

// The composed app's effect class — the static "what can this touch?" property:
let composed = CompanionCapability.composedEffect signature
// composed.Effect    = Effecting   (audit + jobs write)
// composed.Readiness = DevOnly     (the in-memory job scheduler contaminates it)
```

An undeclared component is simply absent from the map and contributes the identity, so `composedEffect`
equals the join of the *declared* capabilities — a fully-undeclared composition joins to "pure",
byte-for-byte the pre-282 posture.

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "EffectJoin"
cd samples/MinimalClient && dotnet fable -o output   # the join operators compile under Fable
```

## Rollback

Delete the Phase 296 blocks from `Shared/CompanionCapability.fs` (the three axis `join`s, the
`CapabilitySignature` type, and the `join` / `joinAll` / `resolve` / `composedEffect` members), delete
`InProcess/EffectJoinTests.fs` + its `<Compile>` and `Program.fs` registration. The Phase 282 descriptor
is unaffected.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — pure-value operators over the Phase 282 descriptor.
No consumer reads the join until it opts in; every deployment is byte-for-byte unchanged (GP 11/13).

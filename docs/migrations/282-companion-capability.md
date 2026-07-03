# Migration 282 — typed companion capability descriptors (`CompanionCapability`)

**Status:** additive — **new type, no consumer action required; a pre-282 deployment is byte-for-byte unchanged (GP 11).**

## What changes

A companion's **effect / determinism / distributed-readiness** posture was file-header prose
("dev-only vs distributed-ready") plus the six-portability-rules document — reviewer-enforced, never
machine-checkable. This phase makes it a typed, queryable *value*.

- **New type** `CompanionCapability` (in `ToolUp.Platform.Core`, `Shared/CompanionCapability.fs`):

  ```fsharp
  type CompanionCapability = {
      Effect: EffectClass              // Pure | Effecting
      Determinism: DeterminismSource   // Deterministic | Nondeterministic of Set<DeterminismFactor>
      Readiness: Readiness             // DistributedReady | DevOnly
  }
  ```

  Each axis is a small join-semilattice whose **bottom** (`Pure` / `Deterministic` /
  `DistributedReady`) is the join identity ([Phase 296](296-capability-effect-join.md) joins them
  componentwise). `DeterminismSource` normalises the empty factor set to `Deterministic`, so structural
  equality stays total (no `Nondeterministic Set.empty` alias for the bottom).

- **The conservative default is the identity.** `CompanionCapability.defaultCapability` (=
  `.identity`) is pure / deterministic / distributed-ready. An **undeclared** companion contributes it,
  so a composition of undeclared companions joins to "pure" and a pre-282 deployment is byte-for-byte
  unchanged. "Conservative" here means least-disruptive: the declaration is the opt-in, absence is the
  no-op.

- **Reference-companion posture constants** — `distributedEffecting` (a real cloud companion:
  effecting + external-state + distributed-ready) and `devOnlyEffecting` (the documented in-memory
  exception: effecting + external-state + dev-only), plus `pure'`. Fluent `withEffect` / `withDeterminism`
  / `withReadiness` declaration helpers and `isPure` / `isDistributedReady` predicates.

- **Keyed against the stable `ComponentId`** (Phase 279) the manifest (Phase 280) and preflight (Phase
  281) already correlate against — a capability signature (`Map<ComponentId, CompanionCapability>`)
  lines up component-for-component with the manifest, no separate key space, no drift-vs-reflection gap.

The descriptor is generic substrate (GP 1 — three DUs + a factor set, no vendor / domain type), a pure
value read on demand (GP 13 — a deployment that never reads it builds nothing and pays nothing), and it
is **read by the manifest + preflight, never enforced as a hard gate here** — [Phase
300](300-composition-capability-sandbox.md) is the opt-in runtime gate.

## How to adopt (opt-in)

A companion declares its real posture where a consumer builds its capability signature:

```fsharp
open ToolUp.Platform

// A real cloud companion — effecting, reads external state, distributed-ready:
let blobCap = CompanionCapability.distributedEffecting

// A dev-only in-memory reference impl (the documented exception):
let inMemoryJobsCap = CompanionCapability.devOnlyEffecting

// Or build one axis at a time from the identity:
let clockOnly =
    CompanionCapability.identity
    |> CompanionCapability.withDeterminism DeterminismSource.clock

// Key it against the same ComponentId the manifest enumerates the slot under:
let signature =
    Map [ ComponentId.forCompanionImpl "IAuditSink" "splunk-archive", blobCap ]
```

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "CompanionCapability"
cd samples/MinimalClient && dotnet fable -o output   # Core/Shared descriptor compiles under Fable
```

## Rollback

Delete `Shared/CompanionCapability.fs` + its `<Compile>` entry, delete
`InProcess/CompanionCapabilityTests.fs` + its `<Compile>` and `Program.fs` registration. No runtime
impact — no deployment reads the descriptor until it opts in.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in introspection value. An undeclared
companion takes the conservative default and every deployment is byte-for-byte unchanged (GP 11/13).

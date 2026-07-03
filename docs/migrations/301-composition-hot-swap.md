# Phase 301 — live composition hot-swap (`CompositionHotSwap`) (consumer migration)

**What changes.** A new operation `CompositionHotSwap.swap` mutates a **running** app's composition —
swap a declared companion implementation — **without a full redeploy**, targeted by the stable Phase
279 `ComponentId` and ordered by the Phase 291 lifecycle (init the new component, atomically
re-point the registry, dispose the old). A general forge capability: zero-downtime config/companion
swaps (rotate an `IBlobStorage` backend, flip an `IAIProvider`) + a faster dev loop (hot-reload a
module without restarting the host).

**Safe by construction (GP 4).**
- **Only declared composed components are swappable** — a swap targets an id already in the registry,
  never arbitrary code. An unknown id is `SwapRejected`.
- **In-flight requests finish on the old component** — the re-point is a single atomic map write; a
  caller that already resolved the old implementation keeps that reference, only *new* resolutions
  see the replacement (no mid-request swap).
- **Atomic with rollback** — a replacement that fails to initialise leaves the registry untouched
  (old stays live); a post-commit dispose failure rolls the registry back to the old implementation.

**Opt-in, off by default (GP 11 / GP 13).** Gated by `HotSwapMode` (default `NoHotSwap` → every swap
is `SwapRejected`, nothing is composed); a deployment that never enables it pays nothing and is
byte-for-byte unchanged. Every attempt emits a `HotSwapEvent` keyed by `ComponentId` for the audit /
telemetry trail. The gate is a value the composing deployment supplies — no `ServerConfig` field is
added, so the config surface is unchanged for non-adopters.

## The shape

```fsharp
type HotSwapMode = NoHotSwap | EnabledHotSwap
type HotSwapOutcome =
    | SwapApplied of ComponentId
    | SwapRejected of ComponentId * reason: string
    | SwapRolledBack of ComponentId * reason: string
type HotSwapEvent = { Component: ComponentId; Outcome: HotSwapOutcome }
type ComponentRegistry<'Impl>(initial: (ComponentId * 'Impl) seq)   // Resolve / Contains / Ids
```

## Performing a swap

```fsharp
let registry = ComponentRegistry<IBlobStorage>([ ComponentId.forCompanionSlot "IBlobStorage", oldStorage ])
let order = ComponentLifecycle.ofComponents [ ComponentId.forCompanionSlot "IBlobStorage" ]

let outcome =
    CompositionHotSwap.swap
        EnabledHotSwap emitEvent order registry
        initStorage disposeStorage
        (ComponentId.forCompanionSlot "IBlobStorage") newStorage
// SwapApplied  -> registry.Resolve now returns newStorage; oldStorage disposed; in-flight refs finish on old
```

## Verification

- `InProcess/CompositionHotSwapTests.fs`: an enabled swap re-points new traffic while an in-flight
  reference finishes on the old; a failed init rolls back cleanly (old stays live, not disposed); an
  undeclared id is rejected (no arbitrary-code injection); a cyclic lifecycle order is refused;
  gate-off refuses every swap and is unchanged; every attempt emits a `ComponentId`-keyed event.

## Rollback

Do not enable `EnabledHotSwap` / compose a `ComponentRegistry` — the default `NoHotSwap` refuses every
swap and nothing is composed. Or revert the Phase 301 forge commit; no persisted state is involved.

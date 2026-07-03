# Phase 290 — component health rollup by id (`ComponentHealthRollup`) (consumer migration)

**What changes.** A new read-only accessor `ComponentHealthRollup.forApp : ServerApp ->
Async<ComponentHealthRollup>` keys every registered `IHealthCheck` result by Phase 279 `ComponentId`
and aggregates them into a per-component rollup, so health reads **per-component** ("which companion
is degraded") instead of as an undifferentiated flat list. A probe's id is its Phase 279
companion-impl id (`companion:IHealthCheck/<Name>`) — the *same* id the Phase 280 manifest's
`IHealthCheck` companion entries carry — so the rollup attaches to the manifest by id-join without
widening the manifest shape.

A probe that cannot name itself (a blank `Name`) is retained under `Unkeyed`, not dropped — the
rollup is total over the registered probes.

**Scope.** Purely additive + on demand (GP 13). The rollup is computed only when a caller asks; a
deployment that never reads it runs no extra probe and allocates nothing (GP 11). Computing it never
mutates the probes or the app.

## The shape

```fsharp
type ComponentHealthRollup = {
    ByComponent: Map<ComponentId, HealthResult>   // each keyed probe's latest outcome
    Unkeyed: (string * HealthResult) list          // blank-Name probes, retained not dropped
}
```

## Reading the rollup

```fsharp
let! rollup = ComponentHealthRollup.forApp app
// rollup.ByComponent.[ComponentId.forCompanionImpl "IHealthCheck" "redis"] -> Healthy / Degraded / Unhealthy
ComponentHealthRollup.worst rollup   // the single worst keyed outcome (for a status board)

// Pure form over already-run outcomes (no probe execution):
ComponentHealthRollup.build [ probe, Healthy; other, Degraded "slow" ]
```

## Verification

- `InProcess/ComponentHealthRollupTests.fs`: a degraded companion surfaces under its `ComponentId`;
  the rollup key matches the manifest `IHealthCheck` companion entry id (id-join); a blank-name probe
  is retained under `Unkeyed`; `run` executes each probe; `worst` collapses to the single worst
  outcome; an app with no probes rolls up empty (GP 13).

## Rollback

Stop calling `ComponentHealthRollup.forApp` / `run` — nothing else references it and no behaviour
changes when unused. Or revert the Phase 290 forge commit; no persisted state is involved.

# Phase 291 — component lifecycle ordering by id (`ComponentLifecycle`) (consumer migration)

**What changes.** A deployment can declare **init / dispose ordering** keyed by Phase 279
`ComponentId`, so startup and shutdown run in a deterministic, dependency-respecting order (e.g. the
secret store initialises before the audit sink that reads it; dispose runs in reverse) rather than
relying on incidental registration order. The order is a partial order expressed as "init-before"
edges over the registered components.

`ComponentLifecycle.initSequence` is a **stable** topological sort — an unconstrained component keeps
its registration position, so a deployment that declares no edges gets exactly today's
registration-order behaviour (GP 11), and `disposeSequence` is that reversed. A declared **cycle**
cannot be satisfied and is rejected at compose (`ensureAcyclic`, or an `Error` from `initSequence`),
never left to surface as a runtime deadlock (GP 4).

**Scope.** Purely additive. A deployment that declares no ordering is byte-for-byte unchanged vs
prior registration-order behaviour.

## The shape

```fsharp
type ComponentOrder = { Components: ComponentId list; Edges: (ComponentId * ComponentId) list }
```

## Declaring an order

```fsharp
let order =
    ComponentLifecycle.ofComponents [ auditSinkId; secretStoreId ]   // registration order
    |> ComponentLifecycle.before secretStoreId auditSinkId           // secret store inits first

ComponentLifecycle.initSequence order      // Ok [ secretStoreId; auditSinkId ]
ComponentLifecycle.disposeSequence order   // Ok [ auditSinkId; secretStoreId ]  (reverse)
ComponentLifecycle.ensureAcyclic "composition" order   // raises on a cycle; no-op otherwise
ComponentLifecycle.runInit initEffect order            // apply an effect in init order
ComponentLifecycle.runDispose disposeEffect order      // apply an effect in dispose order
```

## Verification

- `InProcess/ComponentLifecycleTests.fs`: a declared "secret-store before audit-sink" order
  initialises in order + disposes in reverse; `runInit` / `runDispose` apply the effect in the
  resolved order; an undeclared order resolves to registration order (GP 11); an unconstrained
  component keeps its position (stable sort); a cyclic order fails with a readable `Error` /
  `ensureAcyclic` raises.

## Rollback

Stop declaring a `ComponentOrder` — registration order is the default. Or revert the Phase 291 forge
commit; no persisted state is involved.

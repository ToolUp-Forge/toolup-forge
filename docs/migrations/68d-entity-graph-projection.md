# Phase 68d — Entity→Graph projection bridge (consumer adoption)

**What this adds.** An opt-in bridge that projects your `IEntityStore`
records — and their declared relationship edges — into an `IGraphStore` as a
derived read-model, kept in sync by the entity-store lifecycle signal. You
write entities; the graph traversal surface comes for free. Full concept
guide: [`../graph/entity-projection.md`](../graph/entity-projection.md).

**Scope.** Additive and opt-in (GP 13). A deployment that does not adopt it
is unchanged — the entity store, the audit log, and the DI container are
byte-identical. Nothing is forced on any consumer.

## Prerequisites

- `ServerConfig.EntityStore = EnabledEntityStore` — a config flag, not a builder step.
- An `IGraphStore` in DI — the in-memory default
  (`GraphStore = InMemoryGraphStore`) suffices for dev; an engine companion
  (Neo4j / AGE) works unchanged for production.
- Entity types registered with `ServerApp.withEntity`. Relationships you
  want as edges are declared with `EntityRegistration.withRelationship`
  (Phase 19c).

## Recipe

**1. Add the package reference.**

```xml
<PackageReference Include="ToolUp.Graph.Projection" Version="$(ToolUpSdkVersion)" />
```

**2. Opt in on the config.**

```fsharp
let app =
    ServerApp.create "my-app"                   // with EntityStore = EnabledEntityStore on the config
    |> ServerApp.withEntity bookRegistration
    |> ServerApp.withEntity authorRegistration
    |> ServerApp.withEntityGraphProjection      // ← flips EntityGraphProjection = Enabled
```

**3. Wire the bridge.** The concrete bridge references `IEntityStore`, so —
like a graph engine companion — the deployment wires it (the SDK core
cannot reference it back without a project cycle). Enlist the entity types
to mirror, and register from a `ServiceConfig` extension (compose applies it
after the base stores + audit log are registered):

```fsharp
open ToolUp.Graph.Projection

let enrollments =
    [ ProjectedEntityType.ofRegistration bookRegistration
      ProjectedEntityType.ofRegistration authorRegistration ]

// EntityGraphProjectionCompose.wire:
//   - registers IEntityGraphProjection over the resolved IEntityStore + IGraphStore
//   - decorates the registered IAuditLog so EntityCreated/Updated/Deleted drive the projector
//   - no-ops when EntityGraphProjection = NoEntityGraphProjection (byte-identical)
let addProjection (services: IServiceCollection) =
    EntityGraphProjectionCompose.wire services app.Config enrollments
    services
```

Add `addProjection` through the `ComposeExtensions.ServiceConfig` hook your
app already uses for companion registration.

**4. (Optional) bootstrap once at startup.** If you are adding the bridge to
a deployment that already has entities, run one rebuild per scope so the
graph reflects the existing data:

```fsharp
let projection = provider.GetRequiredService<IEntityGraphProjection>()
let! report = projection.RebuildProjection scopeId
// report : { NodesUpserted; EdgesUpserted; OrphansRemoved }
```

After bootstrap, incremental sync keeps the graph current automatically —
every `Save` / `Delete` propagates. Re-run `RebuildProjection` any time to
heal drift; it is idempotent (a no-op over an unchanged store).

## Verification

- Save an entity, then `IGraphStore.GetNode(scopeId, NodeId "entity:{Type}:{Id}")`
  returns the projected node; `Neighbours` returns its declared-relationship
  edges.
- Delete the entity → its node (and incident edges) disappear.
- `RebuildProjection` twice → the second `ProjectionReport` is
  `ProjectionReport.isNoOp`.

## Rollback

Remove `ServerApp.withEntityGraphProjection` (or drop the `addProjection`
wiring). The entity store reverts to byte-identical prior behaviour; the
derived graph nodes can be left in place or cleared per scope with
`IGraphStore.DeleteNode`. No entity data is affected — the graph is a
derived read-model, never the source of truth.

## Not in scope

- **Graph → entity reverse projection.** The bridge is one-directional
  (entities are the system of record).
- **Transactional entity+graph atomicity.** The projection is
  eventually-consistent (mutation-driven + reconcilable), not atomic with
  the entity write. For atomic entity+graph writes use the AGE
  shared-transaction seam directly.
- **Custom projection** (computed properties, filtered projection,
  multi-entity-to-one-node). The default mapping is field-for-field +
  declared-relationship-for-edge.

# ToolUp.Graph.Projection

The **entity→graph projection bridge**. It projects your `IEntityStore`
records — and their declared relationship edges — into an `IGraphStore` as a
**derived read-model**, so a consumer who models their domain as entities +
relationships gets graph-traversal queries for free without maintaining two
parallel data models by hand.

`IEntityStore` stays the **system of record**; the graph is a queryable
projection derived from it. You write entities; the graph is kept in sync.

It is **opt-in and additive** (GP 13). A deployment that does not compose it
pays nothing, and its entity store behaves exactly as before.

## What projects

- **Each entity → a node.** Node label = the entity `Type`; `NodeId` =
  `entity:{Type}:{Id}` (deterministic — re-projecting yields the same id);
  properties = the record fields, mapped to graph `PropertyValue`s. Mapping
  honours the graph substrate's precision floor: integers → `PInt`
  (`int64`), reals and `decimal` → `PFloat` (`float`), timestamps →
  `PDateTime`.
- **Each declared relationship → an edge.** A Phase-19c relationship whose
  foreign key lives on the entity (an `Outgoing` foreign-key cardinality)
  projects one directed edge: label = the relationship name, `From` = the
  declaring entity, `To` = the foreign-key target. `Incoming` inverse views
  are projected from the entity that carries the key; `ManyToMany`
  join-resolved edges are out of scope.

The mapping is a **pure function** (`EntityProjection.projectEntity`),
shared by both the sync and rebuild paths so they never drift.

## Keeping in sync

- **Incremental** — the bridge subscribes to the entity-store lifecycle
  signal (`EntityCreated` / `EntityUpdated` / `EntityDeleted`): create /
  update upserts the node + edges; delete removes the node + incident edges.
  Deterministic ids make a re-apply a no-op. Sync failures surface as
  retryable data, never a throw, and a `lastProjectedVersion` per entity
  makes a missed signal reconcilable.
- **Rebuild** — `RebuildProjection(scopeId)` reconciles the whole scope:
  upsert every present entity, remove orphaned nodes whose source entity is
  gone. Returns a `ProjectionReport` (counts) and is idempotent (a no-op
  over an unchanged store). Use it to bootstrap over an existing entity
  store and to heal drift.

## Composition

```fsharp skip=fragment
open ToolUp.Graph.Projection

let app =
    ServerApp.empty
    |> ServerApp.withConfig { ServerConfig.defaults with EntityStore = EnabledEntityStore }
    |> ServerApp.withEntity bookRegistration
    |> ServerApp.withEntity authorRegistration
    |> ServerApp.withEntityGraphProjection   // opt in (requires an IGraphStore)

// Wire the bridge from a ServiceConfig extension (applied after the base
// stores + audit log are registered). No-op when not opted in.
let enrollments =
    [ ProjectedEntityType.ofRegistration bookRegistration
      ProjectedEntityType.ofRegistration authorRegistration ]

let addProjection (services: IServiceCollection) =
    EntityGraphProjectionCompose.wire services app.Config enrollments
    services
```

The bridge references `IEntityStore` (a server-tier type), so — like a
graph *engine* companion — the deployment wires it; the SDK core does not
reference it back. Server-only: it ships a DLL, not `fable/` source.

## Not in scope

Reverse graph→entity projection; transactional entity+graph atomicity;
custom projection (computed / filtered / multi-entity-to-one-node
mappings). The default mapping is field-for-field + declared-relationship-
for-edge.

## License

Apache-2.0. Part of the ToolUp Platform SDK.

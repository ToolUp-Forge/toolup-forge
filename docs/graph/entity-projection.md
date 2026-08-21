# Entity→Graph projection bridge (`ToolUp.Graph.Projection`)

The projection bridge makes an `IEntityStore` the **system of record** and
an `IGraphStore` a **derived read-model** over it. You model your domain
once — as entities with declared relationships — and query it two ways:
relational lookups through `IEntityStore.Query`, and graph traversals
through `IGraphStore.Query`. The graph is kept in sync for you; you never
write to both stores by hand.

It is **opt-in and additive** (GP 13). A deployment that does not compose it
pays nothing, and its entity store behaves exactly as it did before.

## The mental model: entities are the source of truth, the graph is derived

```
                writes                       projection (this bridge)
   consumer ───────────────▶  IEntityStore ─────────────────────────▶  IGraphStore
   (Save / Delete)            (system of record)                       (derived read-model)
        ▲                                                                    │
        └───────────────────  relational queries          graph traversals ─┘
```

- You **write only entities**. The graph is never written by hand.
- Every entity mutation propagates to the graph automatically.
- The graph is disposable: a `RebuildProjection` re-derives it from the
  entity store at any time.

This is the payoff of having both substrates: authorization graphs,
provenance lineage, recommendation neighbourhoods — anything you would
otherwise "stand up a separate graph data model" for becomes "add a
projection over the entities you already have".

## What projects

**Each entity becomes a node.**

| Entity | Node |
|---|---|
| entity `Type` (e.g. `"Book"`) | node label |
| `Id` | encoded into the node id |
| — | `NodeId` = `entity:{Type}:{Id}` (deterministic) |
| every record field | a node property, mapped to a `PropertyValue` |

Property mapping honours the graph substrate's **precision floor**
(portability rule 6): integers map to `PInt` (`int64`), reals and
**`decimal`** map to `PFloat` (`float`), timestamps to `PDateTime`. The
graph model has no decimal case — an engine (Kùzu / Neo4j / AGE)
standardises on 64-bit int + IEEE-754 double — so `decimal` is downcast
rather than promising a precision no engine can honour. A `null` / `None`
field is simply omitted.

**Each declared relationship becomes an edge.** A relationship declared on
the entity registration (`EntityRegistration.withRelationship`) whose
foreign key lives on *this* entity (an `Outgoing` foreign-key cardinality —
`OneToOne` / `OneToMany` / `ManyToOne`) projects one directed edge:

| Relationship | Edge |
|---|---|
| `Name` (e.g. `"writtenBy"`) | edge label |
| the declaring entity | `From` node |
| the foreign-key target | `To` node |
| — | deterministic `EdgeId` |

`Incoming` inverse views are projected from the entity that *carries* the
key (not duplicated here), and `ManyToMany` join-resolved relationships are
not projected (they need the join entity's data — out of scope for the
default field-for-field mapping).

The mapping is a **pure function** — `EntityProjection.projectEntity` —
testable without a live store, and shared by both the incremental-sync and
rebuild paths so the two can never drift.

## Keeping in sync

**Incremental (the steady state).** The bridge subscribes to the
entity-store lifecycle signal — `EntityCreated` / `EntityUpdated` /
`EntityDeleted`:

- **create / update** → upsert the node + its declared edges. Deterministic
  ids make a re-apply a no-op (idempotent).
- **delete** → remove the node and, by cascade, its incident edges.

Sync is ordered *within* a single entity (node then its edges) but makes no
ordering promise *across* entities (rule 5). A sync failure is surfaced as
retryable data (rule 3), never thrown — and the projection records a
`lastProjectedVersion` per entity so a missed or failed signal is
reconcilable rather than silently dropped.

**Rebuild (bootstrap + drift heal).** `RebuildProjection(scopeId)` walks
every enrolled entity type, projects all entities, and reconciles the graph
to match: upsert what is present, remove orphaned nodes whose source entity
is gone. It returns a `ProjectionReport` (`NodesUpserted` / `EdgesUpserted`
/ `OrphansRemoved`) for observability, and is idempotent — a second run over
an unchanged store is a no-op (`ProjectionReport.isNoOp`). Use it to:

- bootstrap a graph over an entity store that already has data, and
- heal drift after a missed mutation signal or an out-of-band graph change.

Rebuild is **scope-parameterised** because both stores are
scope-partitioned and a tenant's entities must project only into that
tenant's graph scope (structural isolation, GP 4). Bootstrap / heal one
scope at a time.

## When to use the bridge vs a hand-built graph

- **Use the bridge** when the graph *mirrors your entity domain* — the nodes
  are your entities and the edges are relationships you already declare.
  You get traversal queries for free with no second write path.
- **Hand-build the graph** (write to `IGraphStore` directly) when the graph
  is a *separate concern* from your entity model — a computed graph, an
  imported external graph, or a shape that does not correspond one-to-one to
  entities + declared relationships. The bridge is deliberately
  one-directional (entities are the source of truth); it never projects the
  graph back into entities.

## Composition

The bridge lives in its own package (`ToolUp.Graph.Projection`) because it
bridges `IEntityStore` (a server-tier type) into `IGraphStore` — so, like a
graph *engine* companion, the deployment wires it rather than the SDK core
referencing it. Opt in on the config, then wire the bridge with the entity
types you want mirrored:

```fsharp
open Microsoft.Extensions.DependencyInjection
open ToolUp.Graph.Projection

// 1. Opt in (requires ServerConfig.EntityStore = EnabledEntityStore and an
//    IGraphStore — the in-memory default suffices for dev).
let app =
    ServerApp.empty
    |> ServerApp.withConfig {
        ServerConfig.defaults with
            EntityStore = EnabledEntityStore
    }
    |> ServerApp.withEntity bookRegistration
    |> ServerApp.withEntity authorRegistration
    |> ServerApp.withEntityGraphProjection

// 2. Wire the bridge from a ServiceConfig extension (compose applies it
//    after the base stores + audit log are registered). `wire` no-ops when
//    the projection is not opted in.
let enrollments =
    [ ProjectedEntityType.ofRegistration bookRegistration
      ProjectedEntityType.ofRegistration authorRegistration ]

let withProjection (services: IServiceCollection) =
    EntityGraphProjectionCompose.wire services app.Config enrollments
    services

// 3. (Optional) bootstrap once at startup over existing data:
//    let projection = provider.GetRequiredService<IEntityGraphProjection>()
//    let! report = projection.RebuildProjection scopeId
```

When the projection is not composed, no projection runs and the entity
store is byte-identical to today (GP 13).

## See also

- [`graph-store.md`](graph-store.md) — the `IGraphStore` substrate.
- [`../migrations/68d-entity-graph-projection.md`](../migrations/68d-entity-graph-projection.md)
  — the consumer adoption recipe.

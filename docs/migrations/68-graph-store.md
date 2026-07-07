# Phase 68 — Graph store substrate (`IGraphStore`) (consumer migration)

**What changes.** A new graph-data substrate ships: the `IGraphStore`
interface (`ToolUp.Graph.Core`) plus a zero-dependency in-memory default
(`ToolUp.Graph.InMemory`). `ServerConfig` gains a `GraphStore:
GraphStoreMode` field defaulting to `InMemoryGraphStore`. The default
composition registers the in-memory `IGraphStore` via a **lazy** DI
factory — it is not constructed until something resolves `IGraphStore`.

**This is additive and opt-in — existing consumers need no change.** No
existing composition breaks, no wire format changes, and an app that never
touches a graph API allocates nothing (GP 13). The `GraphStore` field
defaults to `InMemoryGraphStore`; `fromEnv` inherits that default.

## Scope

Server-side substrate registration only. No client-tier change, no route
mounted by default (the graph store is a DI service consumers resolve, not
an auto-mounted API).

## To start using a graph store

Nothing to configure — resolve `IGraphStore` from DI and use it. The
in-memory default is already registered.

```fsharp
open ToolUp.Graph

// Resolve IGraphStore from the service provider (constructor-injected in
// a handler, or sp.GetService<IGraphStore>()).

// Model a small graph.
do! store.UpsertNode(scopeId, { Id = NodeId "a"; Labels = set [ "Node" ]; Properties = Map.empty })
do! store.UpsertNode(scopeId, { Id = NodeId "b"; Labels = set [ "Node" ]; Properties = Map.empty })
let! _ = store.UpsertEdge(scopeId, { Id = EdgeId "e"; Label = "LINK"; From = NodeId "a"; To = NodeId "b"; Properties = Map.empty })

// Traverse it with openCypher (parameterised).
let! result =
    store.Query(scopeId, CypherQuery.ofText "MATCH (a:Node)-[:LINK]->(b:Node) RETURN b")
```

`IGraphStore` methods are `scopeId`-scoped (tenant isolation is
structural) and return `Async<Result<_, GraphError>>` for the mutating +
query paths (`GetNode` / `Neighbours` return `option` / list directly). A
query outside the documented openCypher subset throws
`CypherSubsetException` naming the clause — see
[`docs/graph/graph-store.md`](../graph/graph-store.md) for the full subset.

## To use an engine-backed graph store (when a companion ships)

Register the companion's `IGraphStore` singleton and select
`CustomGraphStore`:

```fsharp
// 1. Add the engine companion's PackageReference.
// 2. Register its IGraphStore singleton in DI (per its README).
// 3. Select CustomGraphStore so compose leaves your singleton in place:
{ config with GraphStore = CustomGraphStore }
```

The same code — the same openCypher query text — runs against the engine
unchanged. The `IGraphStoreContract` conformance pack is the tested
guarantee that a query in the in-memory subset behaves identically on the
engine.

## Verification

- `dotnet build` — the new field defaults in place; no existing
  composition needs editing.
- Default composition resolves a working `IGraphStore` with zero external
  dependencies.
- An app that never resolves `IGraphStore` constructs no graph store (lazy
  factory — GP 13).

## Rollback

No rollback needed — the change is additive and defaulted. To explicitly
opt out of even the lazy registration, set `GraphStore = CustomGraphStore`
without registering a singleton (then `IGraphStore` simply does not
resolve).

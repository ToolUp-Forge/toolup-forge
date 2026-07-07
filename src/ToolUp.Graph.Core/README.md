# ToolUp.Graph.Core

The graph-data substrate contract for the ToolUp platform SDK: the
`IGraphStore` interface plus its Fable-compatible value model.

`IGraphStore` is the graph-shaped peer of `IEntityStore`. Where the entity
store answers relational-algebra lookups over indexed records,
`IGraphStore` answers **traversal** queries — variable-length paths,
neighbourhood expansion, reachability — that the relational layer cannot
express without N round-trips.

## The portability seam is openCypher

Every store — the zero-dependency in-memory default, an embedded engine, a
distributed cluster — answers the same **openCypher** query text. A
consumer develops against the in-memory default and deploys against an
engine companion with no domain-code change. `CypherQuery` is
parameterised (`$name` bindings supplied as data, never string-concatenated
into the query text), closing the injection-class hazard.

## Value model

| Type | Purpose |
|------|---------|
| `NodeId` / `EdgeId` | String-wrapped value identities — never live handles |
| `GraphNode` | `Id` + label `Set` + property `Map` |
| `GraphEdge` | `Id` + `Label` + `From`/`To` endpoints + property `Map` |
| `PropertyValue` | `PString` / `PInt` (int64) / `PFloat` / `PBool` / `PDateTime` — precision floor, no `decimal` |
| `Direction` | `Outgoing` / `Incoming` / `Both` |
| `CypherQuery` | `Text` + `Parameters` |
| `GraphResultSet` | ordered `Columns` + `Rows` of `Map<string, GraphValue>` |
| `GraphError` | failure as data (rule 3); `CypherSubsetException` for out-of-subset queries |

## Portability contract (GP 12 — six rules)

`IGraphStore` satisfies all six portability rules, so an engine companion
is a drop-in replacement validated against the same
`IGraphStoreContract` conformance pack:

1. **Identity by value** — `NodeId` / `EdgeId` / `scopeId: string`.
2. **Async at every boundary** — every method returns `Async<_>`.
3. **Retry/supervision as data** — transient faults are
   `GraphError.TransientFailure`; no callbacks.
4. **Stateless between calls** — results derive from `scopeId` + backing store.
5. **No cross-shard ordering** — row order promised only under `ORDER BY`.
6. **Precision at lower bound** — `int64` / `float` ceiling, no `decimal`.

Scope isolation (GP 4) is structural: every method takes `scopeId`, so a
cross-tenant read is unrepresentable.

## Provider tiers

- **In-memory default** (`ToolUp.Graph.InMemory`) — zero-dependency, the
  GP-2 default; interprets a documented openCypher subset (the portability
  floor).
- **Engine companions** — opt-in packages that bring full Cypher over an
  embedded or distributed engine; each runs the shared conformance pack
  unchanged.

Licensed under Apache-2.0.

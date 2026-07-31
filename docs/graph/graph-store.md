# Graph store substrate (`IGraphStore`)

`IGraphStore` is the platform's graph-data substrate — the graph-shaped
peer of `IEntityStore`. Where the entity store answers relational-algebra
lookups over indexed records, the graph store answers **traversal**
queries: variable-length paths, neighbourhood expansion, reachability.

The query language is **openCypher**, so the in-memory dev default, an
embedded engine, and a distributed cluster all answer the *same query
text*. You develop against the in-memory store and deploy against an
engine companion with no domain-code change.

## When to reach for it (vs `IEntityStore`)

Use `IEntityStore` for **single-hop, indexed lookups** — "fetch the record
by id", "every record where `status = active`". It is a relational store
with declared indexes; each predicate leaf is one indexed lookup.

Use `IGraphStore` when the question is **about the connections**, and
answering it relationally would take N round-trips or recursive queries:

- **Reachability** — "which nodes can reach this one through any chain of
  links?" (e.g. an org chart: who ultimately reports up to this manager).
- **Blast radius** — "everything downstream of this node."
- **Provenance / lineage** — "the shortest path from this output back to
  its source documents."
- **Neighbourhoods** — "the recommendation neighbourhood two hops out."

These are variable-length path questions. Expressed over a relational
store they become N+1 round-trips with hand-rolled cycle handling;
expressed as one Cypher `MATCH ...*1..3...` they are a single query the
substrate evaluates with a cycle-safe traversal.

## The openCypher portability floor

The in-memory default (`ToolUp.Graph.InMemory`) interprets a **documented
bounded subset** of openCypher — the *portability floor*. A query that
runs against the in-memory store is guaranteed to run against every engine
companion; the reverse is not promised.

The subset:

```
MATCH  (n[:Label] [{prop: <lit>, ...}])
     | (a[:L]) <rel> (b[:L])                -rel- is -[:T]->, <-[:T]-, or -[:T]-
[WHERE <var.prop <op> <lit>, combined with AND / OR / parentheses>]
RETURN <var | var.prop> [AS alias] (',' ...)*
[ORDER BY <var.prop> [ASC|DESC]]
[LIMIT <int>]
```

- **Variable-length paths** — `-[:T*lo..hi]->`. Traversal is cycle-safe: a
  visited-node set guarantees termination even on a cyclic graph.
- **Parameters** — `$name`, supplied as data on `CypherQuery.Parameters`,
  never string-concatenated into the query text (the same discipline SQL
  parameterisation enforces).
- **Comparison operators** — `=`, `<>`, `>`, `>=`, `<`, `<=`.

A query using a construct **outside** the subset (`CREATE` / `MERGE` /
`SET` / `DELETE` / `WITH` / `UNWIND` / `OPTIONAL` / aggregation functions /
multi-hop patterns / `RETURN *` / …) throws `CypherSubsetException`
**naming the clause** — never a silently-wrong result. That is the signal
to compose an engine-backed companion for full Cypher.

## Tiered provider model

| Tier | Package | Use |
|------|---------|-----|
| **In-memory default** | `ToolUp.Graph.InMemory` | Development, tests, small single-instance deployments. Zero dependency, GP-2 default, registered lazily. Not durable, not a full engine. |
| **Embedded engine** | (companion) | An embedded openCypher engine for durable single-node graphs with full Cypher. |
| **Distributed engine** | (companion) | A clustered graph database for large / multi-instance deployments. |
| **Colocated engine** | (companion) | A graph engine colocated with an existing relational store. |

Each engine companion registers its own `IGraphStore` singleton in DI and
the deployment selects `ServerConfig.GraphStore = CustomGraphStore`.

## Composition

```fsharp skip=fragment
// Default composition already registers the in-memory IGraphStore
// lazily — a consumer that never resolves IGraphStore pays nothing.
// ServerConfig.GraphStore defaults to InMemoryGraphStore.

// To use an engine companion instead: register its singleton and select
// CustomGraphStore.
{ config with GraphStore = CustomGraphStore }
```

`IGraphStore` is resolved from DI like any other substrate interface:

```fsharp skip=fragment
// Model an org chart, then ask a reachability question in one query.
do! store.UpsertNode(scopeId, { Id = NodeId "alice"; Labels = set [ "Person" ]; Properties = Map.empty })
// ... more nodes + REPORTS_TO edges ...

let! reports =
    store.Query(
        scopeId,
        CypherQuery.ofText
            "MATCH (m:Person {name: 'Alice'})<-[:REPORTS_TO*1..5]-(r:Person) RETURN r.name AS report")
```

## Portability guarantee (the conformance pack)

Every store — the in-memory default and every engine companion — runs the
same `IGraphStoreContract` conformance pack unchanged. It encodes the
six-rule portability audit (identity-by-value, async-at-every-boundary,
retry/supervision-as-data, stateless-between-invocations,
no-cross-shard-ordering-without-`ORDER BY`, precision-at-lower-bound),
tenant isolation, and the frozen **subset-floor corpus** (~20 queries, one
per construct) asserting an identical `GraphResultSet` shape across every
store. That is what makes "develop in-memory, deploy on an engine" a
tested guarantee rather than a hope.

## Scope isolation

Every method takes `scopeId` as its first parameter. A store derives its
storage partition from `scopeId`, so a cross-tenant read is
unrepresentable — the conformance pack asserts a query in one scope cannot
observe another scope's nodes.

## Deliberately out of scope

- **Full Cypher in the in-memory store** — that is what the engine
  companions bring. The in-memory store is a documented subset; shipping
  it to production as a complete engine would mislead.
- **Graph algorithms** (PageRank, community detection, centrality) — a
  traversal substrate, not an analytics suite.
- **Persistence of the in-memory store** — it is a dev / test / small-
  deployment default; durable storage is an engine companion's job.

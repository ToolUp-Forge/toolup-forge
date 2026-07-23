# ToolUp.Graph.AGE

The **Postgres-colocated** `IGraphStore` — openCypher graph queries over
[Apache AGE](https://age.apache.org/), an **Apache-2.0** PostgreSQL extension,
reached through the managed [Npgsql](https://www.npgsql.org/) driver. The tier
for the large population of consumers who **already run PostgreSQL** and want
graph capability **without a second datastore**: graph nodes and edges live in
the same Postgres instance as their relational data — the same backups, the same
connection pool, and — uniquely among the engine tiers — the same transaction
boundary.

A query developed against the zero-dependency in-memory default
(`ToolUp.Graph.InMemory`) runs **unchanged** here — the `IGraphStoreContract`
conformance pack is the tested swap guarantee.

The graph-tier ladder: **in-memory** (dev/test) → **AGE** (durable single-node
default, this package) → **Neo4j** (`ToolUp.Graph.Neo4j`, dedicated distributed
scale).

## Why hand-rolled over plain Npgsql (not a plugin)

A managed AGE Npgsql plugin exists (`Konnektr.Npgsql.Age`). This companion does
**not** depend on it, by deliberate choice:

- Its current release requires **Npgsql ≥ 10**, whereas this SDK pins Npgsql at
  the version its Postgres / TimescaleDB companions already share — adopting the
  plugin would force a repo-wide Npgsql major bump on every consumer for one
  opt-in companion (against GP 13's "opt-in costs nothing").
- Hand-rolling keeps the `cypher(...)`-wrapping + agtype-mapping seam
  (`CypherToAgeSql`) **pure and unit-testable** on a fresh checkout with no live
  server, and keeps the dependency graph to the one already-pinned driver (GP 1).

The `agtype` result cells are re-selected `::text` in the generated SQL, so plain
Npgsql reads them as strings — no agtype OID handling is needed at the driver.

## Prerequisite: the AGE extension

This companion composes against an **AGE-enabled** PostgreSQL. Installing the AGE
binary and enabling the extension is a consumer infrastructure prerequisite (out
of scope for the SDK):

```sql
CREATE EXTENSION IF NOT EXISTS age;
```

The companion creates each tenant graph on first use (`create_graph`); it does
not install the extension.

## Compose

```fsharp
open ToolUp.Graph
open ToolUp.Graph.AGE

// The connection string comes from ISecretStore / the fromEnv config helpers —
// never hard-coded (companion-authoring guide).
let graph : IGraphStore = AgeGraphStore.connect connectionString

// Register the singleton and select CustomGraphStore so the SDK leaves it in
// place (it registers no default under CustomGraphStore):
services.AddSingleton<IGraphStore>(graph) |> ignore
// { config with GraphStore = CustomGraphStore }
```

Consumer domain code then talks only to `IGraphStore` + openCypher — identical to
the in-memory and Neo4j tiers.

## Build-once / read-per-call

The `NpgsqlDataSource` is a pooled, **host-lifetime singleton**. Tenant context
is **never** snapshotted onto it — every call derives its AGE graph from the
*current* `scopeId` (`AgeGraph.nameFor`), and the AGE session preamble
(`LOAD 'age'; SET search_path = ag_catalog, "$user", public;`) is applied on
**every borrowed connection**, never once at construction. So a scope change
between two calls is honoured on the second, and a recycled pooled connection
still carries the preamble. The store implements `IDisposable`; a host disposes
the singleton on shutdown, closing the pool (when the store owns the data source
— `connect` / `connectWith`; `ofDataSource` leaves ownership with the consumer).

## Tenant isolation (GP 4)

| Mode | How | Arbitrary `Query` |
|---|---|---|
| `GraphPerTenant prefix` (default) | one AGE graph per scope — AGE namespaces graphs — name derived from the scope | runs **verbatim** — the graph is the boundary |
| `PropertyPartition graph` | a single shared AGE graph; reserved `_scope` property on every node/edge | a scope guard is injected into node patterns; **fail-closed** on a pattern it cannot scope |

`GraphPerTenant` is the clean, recommended isolation: a `cypher('<graphA>', …)`
call cannot name graph B's subgraph, so even arbitrary Cypher stays isolated.
`PropertyPartition` is the single-graph fallback — structured operations
constrain on `_scope`, and arbitrary `Query` Cypher has a `_scope` guard
rewritten into its node patterns (a pattern the guard cannot safely rewrite is
*refused*, never run unscoped).

```fsharp
// Single-graph posture:
let graph =
    AgeGraphStore.connectWith (AgeGraphStoreConfig.propertyPartition "graph") connectionString
```

## Shared-transaction seam (AGE-specific, NON-PORTABLE)

The unique AGE value-add: because graph data lives in the same Postgres as the
consumer's relational data, a graph write and a relational write can share **one
`NpgsqlTransaction`** and commit / roll back atomically.

This is **not** on the `IGraphStore` interface — promoting it would break Kùzu /
Neo4j portability, which make no cross-store-transaction promise. It is an opt-in
affordance (`AgeSharedTransaction`) a consumer reaches for explicitly, on a
connection + transaction it already opened (typically the same `NpgsqlDataSource`
that backs its `IEntityStore`, wired via `AgeGraphStore.ofDataSource`).

```fsharp
use! conn = dataSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask
use tx = conn.BeginTransaction()

let! graphName = AgeSharedTransaction.prepare config conn scopeId

// … the consumer's own relational write on (conn, tx) …
let! _ = AgeSharedTransaction.upsertNode config conn tx graphName scopeId node

// One commit — the relational row and the graph node land together, or neither.
do! tx.CommitAsync() |> Async.AwaitTask
```

A rollback rolls back both. Portable code that must run on any tier does **not**
use this seam.

## Retry-as-data (GP 12 rule 3)

Postgres transients — connection blips (SqlState class `08`), serialization
failures / deadlocks (class `40`), insufficient resources (`53`), operator
shutdowns (`57P0x`) — never cross the async boundary as a thrown exception. They
fold into `GraphError.TransientFailure`, the retryable value the caller loops on.
A class-`42` syntax / undefined-object error surfaces as
`GraphError.MalformedQuery`; everything else as `GraphError.StorageFailure`. Pure
reads (`GetNode` / `Neighbours`) model their only failure — absence — as
`option` / empty list.

## Safe parameter binding

Parameters ride a **single bound Npgsql parameter** (`@p`, cast `::agtype`) — an
agtype map the Cypher body reads via `$name`. Parameter *values* are never
string-interpolated into the `cypher(…)` body, so an injection-attempt value
(`'; DROP TABLE …`) is a literal map entry, never executed (covered by the
always-on `injection-attempt` unit test and the live conformance arm). The graph
name embedded in the SQL literal is a derived `[a-z0-9_]` identifier (never raw
scope text), so it is injection-safe by construction too.

## Divergences from the in-memory floor

- **Reserved property keys** `_id`, `_scope`, `_from`, `_to` carry substrate
  identity / partition metadata; user properties must avoid them.
- **AGE vertices are single-label.** A `GraphNode` is stored under its sole label
  (or the reserved `_v` sentinel when it has none / more than one). A node with
  multiple labels is stored under the sentinel, so a `:SecondLabel` pattern will
  not match it — the substrate value model is otherwise preserved. The subset
  floor (single-label nodes) is unaffected.
- **Timestamps** (`PDateTime`) round-trip as ISO-8601 strings — AGE has no native
  temporal agtype. A property written as `PDateTime` reads back as `PString`
  unless the consumer re-parses it.
- **Full-Cypher semantics.** AGE supports constructs the in-memory subset refuses
  (`CREATE` / `MERGE` / aggregation / multi-hop) — they run here rather than
  throwing `CypherSubsetException`. Variable-length `RETURN` uses Cypher's
  path-multiset semantics (add `DISTINCT` for the in-memory floor's node-set
  behaviour). This is the intended "engine exceeds the floor" property — the same
  two frozen-corpus cases the Neo4j companion notes (the out-of-subset `CREATE`
  throws-case and variable-length row counts) encode in-memory-specific
  semantics; they are handled by the shared conformance pack, not by editing the
  frozen corpus.

## Out of scope

Installing the AGE extension / operating the Postgres server; cross-store
transactions as a *portable* `IGraphStore` contract (the seam above is
AGE-specific by design); AGE features beyond openCypher (staying on the portable
subset keeps the swap guarantee).

Licensed under Apache-2.0. The bundled dependency (`Npgsql`, PostgreSQL
licence) is credited in the repository `NOTICE.md`; Apache AGE (Apache-2.0) is
the server-side extension the consumer operates.

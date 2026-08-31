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

- Hand-rolling keeps the `cypher(...)`-wrapping + agtype-mapping seam
  (`CypherToAgeSql`) **pure and unit-testable** on a fresh checkout with no live
  server, and keeps the dependency graph to the one already-pinned driver (GP 1).
- The plugin registers an `agtype` type handler at the **driver** level, so this
  opt-in companion's binding would sit underneath every other Postgres-backed
  companion sharing the pin. That is a deeper coupling than a SQL-string seam,
  and it is the coupling the original decision refused.

The `agtype` result cells are selected **uncast**, and the store reads them under
`AllResultTypesAreUnknown`, so plain Npgsql hands back each cell's agtype text
form — no agtype OID handling is needed at the driver.

### Re-evaluation — 2026-08-30 (Phase 607): keep the hand-rolled seam

Phase 607 advanced the repo-wide `Npgsql` pin from 9.0.3 to **10.0.3**, which
removed the original objection (the plugin's `Npgsql ≥ 10` floor is now met), and
re-evaluated the collapse against `Konnektr.Npgsql.Age` **2.0.0**. Two of the
three health criteria pass; the third does not, and it is the decisive one:

| Criterion | Finding |
|---|---|
| Licence | **Apache-2.0** — compatible with this SDK's licence and GP 2. Pass. |
| Npgsql compatibility | Depends on **Npgsql 10.0.3** exactly, with a `net10.0` target. Pass. |
| Maintenance cadence | Last push **2026-05-28** — three months stale, and the entire recent history is a single day's burst. 5 stars, 1 fork, 2 authors, 0 open issues. **Fail** for a dependency that would gate this repo's shared driver pin. |

The bus factor is the whole argument. Collapsing onto the plugin would make the
`Npgsql` version of *four* companions (Postgres entity store, Timescale, pgvector,
AGE) hostage to a 5-star package's release cadence: if Npgsql 11 ships and the
plugin does not follow, the repo either freezes its driver or drops the binding
under pressure. That is a strictly worse version of the bind Phase 68c avoided.

The other half of the decision is new evidence. Phase 607 provisioned an
AGE-enabled Postgres and ran this companion's `IGraphStoreContract` arm **for the
first time** — 68c shipped it env-gated against a machine with no Docker, so it
had never executed. It failed at 29 errored / 5 failed / 1 passed, on two latent
defects in the generated SQL, both since fixed and both pinned by the pure pack:

- `cypher()`'s third argument was passed as `@p::agtype`. AGE parses the call and
  requires a plain `Param` node there; the cast made every parameterised query
  fail with `22023: third argument of cypher function must be a parameter`. It is
  now passed bare, bound `NpgsqlDbType.Unknown`.
- Result columns were re-selected `::text`. `agtype::text` routes through
  `agtype_value_to_text`, which is scalar-only — vertex and edge cells failed
  outright, and string scalars came back unquoted (`carol`, not `"carol"`), which
  is not parseable JSON and folded to `VNull`. Columns are now selected uncast.

So the seam's awkward surface — the thing the plugin was meant to own — turned
out to be a dozen lines, and is now covered by a live conformance arm this
companion did not previously have. Adopting a stale third-party driver plugin to
avoid maintaining that is a poor trade.

**Re-evaluate when** the plugin shows sustained maintenance across an Npgsql major
(the honest test of the cadence risk), or when this seam next needs a change the
uncast/bare-param shape cannot express.

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

```fsharp skip=fragment
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

```fsharp skip=fragment
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

```fsharp skip=fragment
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
  semantics.

  **This paragraph used to end "they are handled by the shared conformance pack,
  not by editing the frozen corpus". They are not.** Phase 607 ran
  `GraphStoreContract` against a live AGE server for the first time and those
  exact cases failed: the pack asserts the in-memory laws unconditionally on
  every binding, with no engine-tier exemption. Four cases fail here — the
  `CREATE`-throws case and three variable-length row-count cases — and the Neo4j
  tier, which binds the same pack behind its own never-run env gate
  (`TOOLUP_TEST_NEO4J_URI`), will fail them identically the day it is run. The
  exemption mechanism this bullet described has to be built; until it is, an AGE
  live run is expected to report those four, and they are **not** AGE defects.

## Out of scope

Installing the AGE extension / operating the Postgres server; cross-store
transactions as a *portable* `IGraphStore` contract (the seam above is
AGE-specific by design); AGE features beyond openCypher (staying on the portable
subset keeps the swap guarantee).

Licensed under Apache-2.0. The bundled dependency (`Npgsql`, PostgreSQL
licence) is credited in the repository `NOTICE.md`; Apache AGE (Apache-2.0) is
the server-side extension the consumer operates.

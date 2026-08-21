# ToolUp.Graph.AGE — Postgres-colocated openCypher graph store

`ToolUp.Graph.AGE` implements the graph substrate's `IGraphStore` over
[Apache AGE](https://age.apache.org/) — an **Apache-2.0** PostgreSQL extension
that adds openCypher graph queries to an ordinary Postgres database — reached
through the managed [Npgsql](https://www.npgsql.org/) driver. It is the
**Postgres-colocated tier** of the graph provider model: the store you compose
when you already run PostgreSQL and want graph capability **without a second
datastore**.

See also: [`graph-store.md`](graph-store.md) (the `IGraphStore` substrate + the
zero-dependency in-memory default), [`neo4j.md`](neo4j.md) (the distributed
tier), and the companion package README (`src/ToolUp.Graph.AGE/README.md`).

## When to choose AGE

| Store | Shape | Reach for it when |
|---|---|---|
| **In-memory** (default) | zero-dependency, single-process, subset floor | development, tests, small single-instance deployments |
| **AGE** (this) | Postgres-colocated (Apache AGE extension) | you already run PostgreSQL; want graph alongside relational data — one deployment, one backup, one transaction boundary |
| **Neo4j** | distributed server, full Cypher | multi-reader/writer concurrency, horizontal read scale-out, an existing Neo4j estate |

Reach for AGE when you **already operate PostgreSQL** and standing up a separate
graph database (a Neo4j server) would mean a second thing to back up, monitor,
secure, and reason about transactionally. AGE collapses that: graph nodes and
edges live in the same Postgres instance as your relational data — the same
connection pool, the same backups, and — uniquely among the tiers — the option
of the **same transaction** (see the shared-transaction seam below).

It is Apache-2.0, so no paid tier (GP 2). Because AGE speaks openCypher, the
documented subset floor runs and the `IGraphStoreContract` conformance pack
passes unchanged: a query developed against the in-memory default runs against
AGE with **zero domain-code change**.

**AGE vs Neo4j.** AGE's openCypher coverage is narrower than the Neo4j
reference, and it scales as far as a single PostgreSQL instance does. When graph
data outgrows one node — high concurrent write throughput, read replicas, a very
large graph — the distributed tier (`ToolUp.Graph.Neo4j`) is the next step; the
value model and query text are identical, so the swap is a composition change.

## Prerequisite: install the AGE extension (out of SDK scope)

This companion composes against an **AGE-enabled** PostgreSQL. Installing the AGE
binary into the server and enabling the extension is a **consumer infrastructure
prerequisite** — the SDK documents it but does not automate it:

```sql
CREATE EXTENSION IF NOT EXISTS age;
```

The companion creates each tenant graph on first use (`create_graph`); it does
**not** install the extension or provision the server.

## Composition (a one-liner)

```fsharp
open ToolUp.Graph
open ToolUp.Graph.AGE

// Resolve the connection string from ISecretStore / the fromEnv config helpers
// at compose — never hard-code it (companion-authoring guide).
let graph : IGraphStore = AgeGraphStore.connect connectionString

services.AddSingleton<IGraphStore>(graph) |> ignore
// select CustomGraphStore so the SDK registers no in-memory default:
// { config with GraphStore = CustomGraphStore }
```

`AgeGraphStore.ofDataSource config dataSource` wraps an `NpgsqlDataSource` the
consumer already built — typically the same one that backs its `IEntityStore`,
which is what enables the shared-transaction seam. The store is a singleton; it
implements `IDisposable`, so the host closes the pool on shutdown (when the store
owns the data source — `connect` / `connectWith`).

## Build-once / read-per-call

The `NpgsqlDataSource` is a pooled, host-lifetime singleton. Tenant context is
never snapshotted onto it: every call derives its AGE graph from the *current*
`scopeId`, and the AGE session preamble
(`LOAD 'age'; SET search_path = ag_catalog, "$user", public;`) is applied on
**every borrowed connection**, never once at construction — so a scope change
between two calls is honoured on the second, and a recycled pooled connection
still carries the preamble.

## Tenant isolation

`AgeGraphStoreConfig.Isolation` selects how a `scopeId` maps onto AGE:

### `GraphPerTenant prefix` (default) — the clean isolation

AGE namespaces graphs (each graph is its own schema under `ag_catalog`), so each
tenant scope targets its **own AGE graph**, whose name is derived
deterministically from the scope. Cross-tenant reads are structurally
impossible: a `cypher('<graphA>', …)` call cannot name graph B's subgraph, so
even arbitrary `Query` Cypher runs **verbatim** and stays isolated.

### `PropertyPartition graph` — the single-graph fallback

For a deployment that prefers one shared graph (one schema, one backup unit).
Every node and edge carries a reserved `_scope` property; structured operations
constrain on it, and arbitrary `Query` Cypher has a `_scope` guard **injected
into its node patterns**. The guard is **fail-closed**: a pattern it cannot
safely rewrite is *refused* rather than run unscoped, so a shared graph never
leaks.

```fsharp
let graph =
    AgeGraphStore.connectWith (AgeGraphStoreConfig.propertyPartition "graph") connectionString
```

**Trade-off.** Graph-per-tenant is cleaner and runs arbitrary Cypher verbatim;
property-partition limits arbitrary `Query` to patterns the scope guard can
rewrite. Prefer `GraphPerTenant`.

## The shared-transaction seam (AGE-specific, NON-PORTABLE)

The unique AGE value-add: because graph data lives in the **same Postgres** as
the consumer's relational data, a graph write and a relational write can share
one `NpgsqlTransaction` and commit / roll back atomically.

This is deliberately **not** on the `IGraphStore` interface — promoting it would
break Neo4j (and any future embedded-engine) portability, none of which make a
cross-store-transaction promise. It is an opt-in affordance (`AgeSharedTransaction`)
a consumer reaches for explicitly, on a connection + transaction it already
opened (typically the same `NpgsqlDataSource` that backs its `IEntityStore`,
wired via `AgeGraphStore.ofDataSource`):

```fsharp
async {
    use! conn = dataSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask
    use tx = conn.BeginTransaction()

    let! graphName = AgeSharedTransaction.prepare config conn scopeId

    // … the consumer's own relational write on (conn, tx) …
    let! _ = AgeSharedTransaction.upsertNode config conn tx graphName scopeId node

    do! tx.CommitAsync() |> Async.AwaitTask // relational row + graph node land together, or neither
}
```

A rollback rolls back both. **Portability caveat:** code that must run on any
graph tier does not use this seam — it is AGE-only by construction. The base
`IGraphStore` contract makes no cross-store-transaction promise, so a query /
upsert issued through the interface behaves identically on every tier; only this
explicit seam couples graph + relational writes.

## Retry-as-data (GP 12 rule 3)

Postgres transients — connection blips (SqlState class `08`), serialization
failures / deadlocks (class `40`), insufficient resources (`53`), operator
shutdowns (`57P0x`) — never cross the async boundary as a thrown exception. They
fold into `GraphError.TransientFailure`, the retryable value a caller loops on. A
class-`42` syntax / undefined-object error surfaces as `GraphError.MalformedQuery`;
everything else as `GraphError.StorageFailure`. There is no `OnFailure` callback;
supervision semantics never leak through the signature.

## Safe parameter binding

Parameters ride a **single bound Npgsql parameter** (`@p`, cast `::agtype`) — an
agtype map the Cypher body reads via `$name`. Parameter *values* are never
string-interpolated into the `cypher(…)` body, so an injection-attempt value
(`'; DROP TABLE …`) is a literal map entry, never executed. The graph name
embedded in the SQL literal is a derived `[a-z0-9_]` identifier (never raw scope
text), injection-safe by construction. Covered by an always-on unit test and the
live conformance arm.

## Full Cypher vs the subset floor

The in-memory default interprets a bounded openCypher subset and throws
`CypherSubsetException` for anything beyond it. AGE runs those constructs
(`CREATE` / `MERGE` / aggregation / multi-hop patterns) rather than throwing —
the intended "engine exceeds the floor" property. Two frozen-corpus cases encode
**in-memory-specific** semantics rather than a universal contract, and the shared
conformance pack — not an edit to the frozen corpus — accounts for them:

- the out-of-subset `CREATE` throws-case is an in-memory floor property (AGE runs
  `CREATE`);
- variable-length `RETURN` uses Cypher's path-multiset semantics; add `DISTINCT`
  for the in-memory floor's node-set behaviour.

Two AGE-specific divergences from the value model (also in the package README):

- **AGE vertices are single-label.** A `GraphNode` is stored under its sole label
  (or a reserved `_v` sentinel when it has none / more than one), so a
  `:SecondLabel` pattern will not match a multi-label node. The subset floor
  (single-label fixtures) is unaffected.
- **Timestamps** (`PDateTime`) round-trip as ISO-8601 strings — AGE has no native
  temporal agtype.

## Verification

- `dotnet build ToolUp.Forge.sln` — the companion compiles; consumers add its
  `PackageReference` and register the singleton.
- The cypher()-wrapping / agtype-mapping / injection-binding / graph-name /
  scope-guard / transient-classification logic is covered by an always-on unit
  pack (`Graph/AgeConformance.fs`), green on a fresh checkout with no server.
- The full `IGraphStoreContract` conformance pack binds to a live AGE-enabled
  Postgres behind `TOOLUP_TEST_AGE_CONNSTRING`; unset, that arm reports a single
  skipped case, never a failure, so no server is required for CI.

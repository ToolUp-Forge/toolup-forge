# ToolUp.Graph.Neo4j — distributed openCypher graph store

`ToolUp.Graph.Neo4j` implements the graph substrate's `IGraphStore` over an
externally operated [Neo4j](https://neo4j.com/) server via the official
**Apache-2.0-licensed** `Neo4j.Driver`. It is the **distributed tier** of the
graph provider model: the store you compose when graph data outgrows a single
embedded process.

See also: [`graph-store.md`](graph-store.md) (the `IGraphStore` substrate + the
zero-dependency in-memory default) and the companion package README
(`src/ToolUp.Graph.Neo4j/README.md`).

## When to choose Neo4j

| Store | Shape | Reach for it when |
|---|---|---|
| **In-memory** (default) | zero-dependency, single-process, subset floor | development, tests, small single-instance deployments |
| **Kùzu** | embedded, single-node, on-disk, full Cypher | one durable process; large datasets that still fit a single node; no server to run |
| **Neo4j** (this) | distributed server, full Cypher | multi-reader/writer concurrency, horizontal read scale-out, hundreds of millions of nodes, an existing Neo4j estate |
| **AGE** | Postgres-colocated (Apache AGE) | graph alongside relational data in one PostgreSQL deployment |

Reach for Neo4j when you need **scale beyond one process** — high concurrent
write throughput, read replicas, a very large graph — or you **already run
Neo4j** and want the SDK to compose against it rather than migrate away.

Because Neo4j is the openCypher reference implementation, the documented subset
floor plus essentially all real-world Cypher run, and the `IGraphStoreContract`
conformance pack passes unchanged: a query developed against the in-memory
default (or Kùzu) runs against Neo4j with **zero domain-code change**.

## The driver-vs-server licensing boundary (GP 2)

The forge principle GP 2 — no paid-by-default dependency — applies honestly
here, and the two halves of "Neo4j" have different licences:

- **The .NET driver** (`Neo4j.Driver`) this package depends on is **Apache-2.0**.
  Composing against a Neo4j server carries **no licence cost in the SDK layer**.
- **The server** is the consumer's deployment choice, and its licensing is *not*
  something the SDK imposes:
  - **Community Edition** — GPLv3, free, hosts a **single database**.
  - **Enterprise / AuraDB** — commercial, supports **multiple databases**,
    clustering, and the operational tooling that motivates this tier.

The SDK never bundles, provisions, or requires a paid server. A consumer running
Community gets a fully working distributed graph via property-partition
isolation; multi-database (database-per-tenant) isolation needs Enterprise/Aura.
Vendor coupling is isolated in this companion (GP 1) — `Neo4j.Driver` never
reaches `ToolUp.Platform.*` or `ToolUp.Graph.Core`.

## Composition (a one-liner)

```fsharp
open ToolUp.Graph
open ToolUp.Graph.Neo4j

// Resolve the URI + credentials from ISecretStore / the fromEnv config helpers
// at compose — never hard-code them (companion-authoring guide).
let graph : IGraphStore =
    Neo4jGraphStore.connect "neo4j://graph.internal:7687" username password

services.AddSingleton<IGraphStore>(graph) |> ignore
// select CustomGraphStore so the SDK registers no in-memory default:
// { config with GraphStore = CustomGraphStore }
```

`Neo4jGraphStore.ofDriver config driver` wraps a driver the consumer already
built (custom TLS / auth-token management). The store is a singleton; it
implements `IDisposable`, so the host closes the connection pool on shutdown.

## Tenant isolation

`Neo4jGraphStoreConfig.Isolation` selects how a `scopeId` maps onto Neo4j:

### `MultiDatabase prefix` (default) — the clean isolation

Each tenant scope targets its **own Neo4j database**, whose name is derived
deterministically from the scope. Cross-tenant reads are structurally
impossible: a session bound to tenant A's database cannot name tenant B's
subgraph, so even arbitrary `Query` Cypher runs **verbatim** and stays isolated.

- **Minimum server: Neo4j 4.0** (multi-database). Creating a database is an
  Enterprise / AuraDB operation; provision the per-tenant databases as part of
  tenant onboarding (out of the SDK's scope).

### `PropertyPartition database` — the single-database fallback

For a server that hosts one database (Community). Every node and relationship
carries a reserved `_scope` property; structured operations
(`UpsertNode`/`UpsertEdge`/`GetNode`/`Neighbours`/`Delete*`) constrain on it, and
arbitrary `Query` Cypher has a `_scope` guard **injected into its node patterns**.
The guard is **fail-closed**: a pattern it cannot safely rewrite is *refused*
rather than run unscoped, so a shared multi-tenant database never leaks.

```fsharp
let graph =
    Neo4jGraphStore.connectWith
        (Neo4jGraphStoreConfig.propertyPartition "neo4j")
        uri username password
```

**Trade-off.** Multi-database is cleaner and runs arbitrary Cypher verbatim;
property-partition works on any edition but limits arbitrary `Query` to patterns
the scope guard can rewrite. Prefer `MultiDatabase` on any server that supports
it.

## Connection lifecycle, pool sizing, and cluster routing

`Neo4jGraphStoreConfig` (all values defaulted):

| Field | Default | Purpose |
|---|---|---|
| `MaxConnectionPoolSize` | 100 | ceiling on pooled connections; raise for high write concurrency |
| `ConnectionAcquisitionTimeout` | 60s | wait for a pooled connection before failing |
| `MaxTransactionRetryTime` | 30s | bounds the driver's internal retry before a transient surfaces as data |

**Causal-cluster routing.** A `neo4j://` (or `neo4j+s://`) URI enables routing —
the driver sends reads to followers and writes to the leader. `bolt://` pins one
instance. `GetNode` / `Neighbours` route as reads; mutations and arbitrary
`Query` route as writes (a write server serves reads too, whereas a read replica
rejects a write).

**Retry-as-data (GP 12 rule 3).** Cluster transients — connection blips, leader
re-election during a failover, deadlocks — never cross the async boundary as a
thrown `TransientException`. They fold into `GraphError.TransientFailure`, the
retryable value a caller loops on. There is no `OnFailure` callback; supervision
semantics never leak through the signature.

## Full Cypher vs the subset floor

The in-memory default interprets a bounded openCypher subset and throws
`CypherSubsetException` for anything beyond it. Neo4j is the openCypher
reference, so those constructs (`CREATE` / `MERGE` / aggregation / multi-hop
patterns) **run** here rather than throwing — the intended "engine exceeds the
floor" property. Variable-length `RETURN` uses Cypher's path-multiset semantics;
add `DISTINCT` for the in-memory floor's node-set behaviour. Neo4j-proprietary
extensions (GDS, APOC) are reachable through raw `Query` text at the consumer's
own portability cost — documented, not blessed; staying on openCypher keeps the
swap guarantee to Kùzu / AGE.

## Verification

- `dotnet build ToolUp.Forge.sln` — the companion compiles; consumers add its
  `PackageReference` and register the singleton.
- Translation logic (value mapping, the property-partition scope guard,
  database-name derivation, transient classification) is covered by an
  always-on unit pack (`Graph/Neo4jConformance.fs`), green on a fresh checkout.
- The full `IGraphStoreContract` conformance pack binds to a live Neo4j behind
  `TOOLUP_TEST_NEO4J_URI` (+ `_USER` / `_PASSWORD`); unset, that arm reports a
  single skipped case, never a failure, so no server is required for CI.

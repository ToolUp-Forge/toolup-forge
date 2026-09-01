# ToolUp.Graph.Neo4j

The **distributed-tier** `IGraphStore` — full openCypher over an externally
operated [Neo4j](https://neo4j.com/) server (single instance or causal
cluster), via the official **Apache-2.0-licensed** `Neo4j.Driver`. The tier a
consumer reaches for when graph data outgrows a single embedded node:
multi-reader / multi-writer concurrency, horizontal read scale-out, operational
tooling, very large datasets, or an existing Neo4j estate to compose against.

Neo4j is the openCypher reference implementation, so a query developed against
the zero-dependency in-memory default (`ToolUp.Graph.InMemory`) runs **unchanged**
here — the `IGraphStoreContract` conformance pack is the tested swap guarantee —
and full Cypher beyond the documented subset floor runs too.

## Licensing boundary (GP 2)

The **driver** is Apache-2.0 — composing against a Neo4j server the consumer
operates carries **no SDK-imposed paid tier**. The **server's** own licensing is
the consumer's deployment choice, not something this package imposes:

- **Neo4j Community Edition** — GPLv3, free, **single database** → use
  `PropertyPartition` isolation.
- **Neo4j Enterprise / AuraDB** — commercial, **multi-database** → use
  `MultiDatabase` isolation (the default).

This companion never provisions, operates, or clusters the server — that is the
consumer's infrastructure.

## Compose

```fsharp skip=fragment
open ToolUp.Graph
open ToolUp.Graph.Neo4j

// URI + credentials come from ISecretStore / the fromEnv config helpers —
// never hard-coded (companion-authoring guide).
let graph : IGraphStore =
    Neo4jGraphStore.connect "neo4j://graph.internal:7687" username password

// Register the singleton and select CustomGraphStore so the SDK leaves it in
// place (it registers no default under CustomGraphStore):
services.AddSingleton<IGraphStore>(graph) |> ignore
// { config with GraphStore = CustomGraphStore }
```

Consumer domain code then talks only to `IGraphStore` + openCypher — identical
to the in-memory and Kùzu tiers.

## Build-once / read-per-call

The `IDriver` is an expensive, pooled, **host-lifetime singleton**. Tenant
context (which database a call targets) is **never** snapshotted onto it at
construction — every `IGraphStore` call opens a fresh session for the *current*
`scopeId` (`SessionScope.openSession`), so a scope change between two calls is
honoured on the second. The store implements `IDisposable`; a host disposes the
singleton on shutdown, closing the connection pool (no leaked connections).

## Tenant isolation (GP 4)

| Mode | Server | How | Arbitrary `Query` |
|---|---|---|---|
| `MultiDatabase prefix` (default) | Neo4j **4.0+** (Enterprise / Aura) | database-per-tenant, name derived from the scope | runs **verbatim** — the database is the boundary |
| `PropertyPartition database` | any edition (incl. Community) | reserved `_scope` property on every node/relationship | a scope guard is injected into node patterns; **fail-closed** on a pattern it cannot scope |

`MultiDatabase` is the clean, recommended isolation: a session bound to tenant
A's database cannot name tenant B's subgraph, so even arbitrary Cypher stays
isolated. `PropertyPartition` is the single-database fallback — structured
operations constrain on `_scope`, and arbitrary `Query` Cypher has a `_scope`
guard rewritten into its node patterns (a pattern the guard cannot safely
rewrite is *refused*, never run unscoped). Prefer `MultiDatabase` on any server
that supports it.

```fsharp skip=fragment
// Community / single-database posture:
let graph =
    Neo4jGraphStore.connectWith
        (Neo4jGraphStoreConfig.propertyPartition "neo4j")
        uri username password
```

## Retry-as-data (GP 12 rule 3)

Causal-cluster transients — connection blips, leader re-election, deadlocks —
never cross the async boundary as a thrown `TransientException`. They fold into
`GraphError.TransientFailure`, the retryable value the caller loops on. Malformed
Cypher and missing parameters surface as `GraphError.MalformedQuery`; everything
else as `GraphError.StorageFailure`. Pure reads (`GetNode` / `Neighbours`) model
their only failure — absence — as `option` / empty list.

**This paragraph was true of the design and false of the code until Phase 752,
and the first live run is what found it.** Two defects, both twins of ones
Phase 607 found in the AGE companion:

- The store reaches the driver through `Async.AwaitTask`, which surfaces a
  faulted task as an `AggregateException` — so the driver's exception arrived
  **wrapped** and every classification arm fell through to `StorageFailure`.
  The whole retryable/non-retryable split this section describes was silently
  dead code. `classifyError` now unwraps first.
- The malformed-query arm matched only a status code containing `SyntaxError`,
  so a **missing parameter** — the exact case the conformance pack asserts —
  read as `StorageFailure`. It now matches the whole
  `Neo.ClientError.Statement.*` family (the Bolt analogue of the AGE binding's
  Postgres class-`42` test), with security, schema and transaction client-errors
  deliberately outside it.

Both are pinned negatively in the always-on unit pack, so neither can return
silently on a machine with no server.

## Connection config + causal-cluster routing

`Neo4jGraphStoreConfig` exposes pool sizing and the retry window (all defaulted):

- `MaxConnectionPoolSize` (default 100) — raise for high write concurrency.
- `ConnectionAcquisitionTimeout` (default 60s).
- `MaxTransactionRetryTime` (default 30s) — bounds the driver's internal retry
  before a transient surfaces as data.

Use a `neo4j://` (or `neo4j+s://`) URI for **causal-cluster routing**: the driver
routes reads to followers and writes to the leader. `bolt://` pins a single
instance. Read-shaped calls (`GetNode` / `Neighbours`) route as reads; mutations
and arbitrary `Query` route as writes (a write server serves reads too, whereas a
read replica would reject a write).

## Divergences from the in-memory floor

- **Reserved property keys** `_id`, `_scope`, `_from`, `_to` carry substrate
  identity / partition metadata; user properties must avoid them.
- **Re-upserting a node with different labels** unions labels (Cypher `SET` adds;
  it does not strip unknown labels). Re-upserting an edge to different endpoints
  MERGEs a new relationship rather than repointing the old one.
- **Full-Cypher semantics.** Neo4j supports constructs the in-memory subset
  refuses (`CREATE` / `MERGE` / aggregation / multi-hop) — they run here rather
  than throwing `CypherSubsetException`. Variable-length `RETURN` uses Cypher's
  path-multiset semantics (add `DISTINCT` for the in-memory floor's node-set
  behaviour). This is the intended "engine exceeds the floor" property, and
  since **Phase 752** the conformance pack knows it: this companion declares the
  `FullEngine` tier at bind time, so `CREATE` executing, a multi-hop pattern
  running, and the path-multiset row counts are asserted here as **full-engine
  laws**, while the in-memory binding is held to the interpreter-subset laws it
  actually implements. Before 752 the pack asserted the interpreter's laws on
  every binding, so this arm would have reported four failures the first time
  anyone ran it — which Phase 607 predicted from the AGE side, before a Neo4j
  server existed to prove it.

  Certified live at Phase 752 against `neo4j:5` in `PropertyPartition` mode:
  **38 passed / 0 failed** — the first execution of this arm since it shipped at
  Phase 68b.

## Out of scope

Operating / provisioning / clustering / backing up the Neo4j server;
Neo4j-proprietary extensions (GDS, APOC — call them through raw `Query` text at
your own portability cost); embedded Neo4j (GPLv3 — the embedded single-node tier
is Kùzu's job).

Licensed under Apache-2.0. The bundled dependency (`Neo4j.Driver`, Apache-2.0) is
credited in the repository `NOTICE.md`.

# Phase 68c — `ToolUp.Graph.AGE` companion (consumer migration)

**What changes.** A new **opt-in** engine companion ships: `ToolUp.Graph.AGE`
implements the Phase 68 `IGraphStore` over [Apache AGE](https://age.apache.org/),
an Apache-2.0 PostgreSQL extension, reached through the managed `Npgsql` driver.
It is the Postgres-colocated tier — graph data lives in the same PostgreSQL
instance as the consumer's relational data, with an opt-in shared-transaction
seam.

**This is additive — existing consumers need no change.** The default graph store
is still the zero-dependency in-memory one (`GraphStore = InMemoryGraphStore`);
nothing composes AGE until a consumer opts in. An app that never adds the package
is byte-for-byte unchanged (GP 11 / GP 13).

## Scope

Server-side `IGraphStore` companion registration only. No client-tier change, no
route mounted. The consumer resolves `IGraphStore` from DI exactly as with the
in-memory default; only the backing store changes.

## Prerequisite: the AGE extension

The target PostgreSQL must have Apache AGE installed and enabled — a consumer
infrastructure step, not automated by the SDK:

```sql
CREATE EXTENSION IF NOT EXISTS age;
```

The companion creates each tenant graph on first use; it does not install the
extension or provision the server.

## To adopt AGE

**1. Add the package reference** (server project):

```xml
<PackageReference Include="ToolUp.Graph.AGE" />
```

**2. Supply the Postgres connection string** via the `fromEnv` config helpers /
`ISecretStore` — never hard-coded:

```fsharp
open ToolUp.Graph
open ToolUp.Graph.AGE

// e.g. from TOOLUP_AGE_CONNSTRING / a secret store
let connectionString = secretStore.Get "age-postgres-connstring"

let graph : IGraphStore = AgeGraphStore.connect connectionString
```

**3. Register the singleton and select `CustomGraphStore`** — one line in compose
so the SDK leaves your singleton in place (it registers no default under
`CustomGraphStore`):

```fsharp
services.AddSingleton<IGraphStore>(graph) |> ignore

let config = { config with GraphStore = CustomGraphStore }
```

That is the whole change. The same code — the same openCypher query text — runs
against AGE unchanged; the `IGraphStoreContract` conformance pack is the tested
guarantee that a query in the in-memory subset behaves identically on the engine.

## Tenant isolation choice

- **`GraphPerTenant`** (default, `AgeGraphStore.connect`) — one AGE graph per
  scope; AGE namespaces graphs, so this is the clean isolation and runs arbitrary
  Cypher verbatim.
- **`PropertyPartition`** — a single shared graph (reserved `_scope` on every
  node/edge, scope guard injected into arbitrary `Query`):

  ```fsharp
  let graph =
      AgeGraphStore.connectWith (AgeGraphStoreConfig.propertyPartition "graph") connectionString
  ```

## Optional: the shared-transaction seam (AGE-specific, non-portable)

If the same PostgreSQL backs both your `IEntityStore` and this graph store, wire
the store over the shared data source (`AgeGraphStore.ofDataSource`) and enlist
graph writes in the same `NpgsqlTransaction` as relational writes so they commit
or roll back atomically:

```fsharp
use! conn = dataSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask
use tx = conn.BeginTransaction()
let! graphName = AgeSharedTransaction.prepare config conn scopeId
// … relational write on (conn, tx) …
let! _ = AgeSharedTransaction.upsertNode config conn tx graphName scopeId node
do! tx.CommitAsync() |> Async.AwaitTask
```

This is **AGE-only** — the base `IGraphStore` contract makes no cross-store
promise, so portable code that must run on any tier does not use it. See
[`docs/graph/age.md`](../graph/age.md) for the portability caveat, tenant
isolation trade-off, and connection lifecycle.

## Verification

- `dotnet build` — with the package referenced and the singleton registered, the
  build is clean; no existing composition needs editing.
- Resolve `IGraphStore` and run an existing query — it behaves as it did on the
  in-memory default (within the subset floor).
- To exercise the live conformance pack, point `TOOLUP_TEST_AGE_CONNSTRING` at a
  running AGE-enabled Postgres and run the SDK test suite.

## Rollback

Additive and opt-in: drop the `PackageReference`, remove the singleton
registration, and set `GraphStore` back to `InMemoryGraphStore` (or leave
`CustomGraphStore` with a different companion). No data-format lock-in at the SDK
layer — the value model (`GraphNode` / `GraphEdge` / openCypher) is identical
across every tier.

## Adoption matrix

Most cells are ⛔ N-A — this is an additive opt-in companion; no consumer is
forced onto it. The tier exists for the large population of Postgres-running
consumers who want graph without a second datastore. The workspace
`SDK-ADOPTION.md` row is regenerated from the consumer manifests + shipped
consumer-facing phases (do not hand-edit it).

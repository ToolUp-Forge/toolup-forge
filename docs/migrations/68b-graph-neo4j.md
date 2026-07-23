# Phase 68b — `ToolUp.Graph.Neo4j` companion (consumer migration)

**What changes.** A new **opt-in** engine companion ships: `ToolUp.Graph.Neo4j`
implements the Phase 68 `IGraphStore` over an externally operated Neo4j server
via the Apache-2.0 `Neo4j.Driver`. It is the distributed tier — full openCypher,
multi-reader/writer concurrency, horizontal read scale-out.

**This is additive — existing consumers need no change.** The default graph
store is still the zero-dependency in-memory one (`GraphStore =
InMemoryGraphStore`); nothing composes Neo4j until a consumer opts in. An app
that never adds the package is byte-for-byte unchanged (GP 11 / GP 13).

## Scope

Server-side `IGraphStore` companion registration only. No client-tier change, no
route mounted. The consumer resolves `IGraphStore` from DI exactly as with the
in-memory default; only the backing store changes.

## To adopt Neo4j

**1. Add the package reference** (server project):

```xml
<PackageReference Include="ToolUp.Graph.Neo4j" />
```

**2. Supply a bolt URI + credentials** via the `fromEnv` config helpers /
`ISecretStore` — never hard-coded:

```fsharp
open ToolUp.Graph
open ToolUp.Graph.Neo4j

// e.g. from TOOLUP_NEO4J_URI / TOOLUP_NEO4J_USER / a secret store
let uri      = configValue "TOOLUP_NEO4J_URI"        // "neo4j://graph.internal:7687"
let username = configValue "TOOLUP_NEO4J_USER"
let password = secretStore.Get "neo4j-password"

let graph : IGraphStore =
    Neo4jGraphStore.connect uri username password
```

**3. Register the singleton and select `CustomGraphStore`** — one line in
compose so the SDK leaves your singleton in place (it registers no default under
`CustomGraphStore`):

```fsharp
services.AddSingleton<IGraphStore>(graph) |> ignore

let config = { config with GraphStore = CustomGraphStore }
```

That is the whole change. The same code — the same openCypher query text — runs
against Neo4j unchanged; the `IGraphStoreContract` conformance pack is the tested
guarantee that a query in the in-memory subset behaves identically on the engine.

## Tenant isolation choice

- **`MultiDatabase`** (default, `Neo4jGraphStore.connect`) — database-per-tenant.
  The clean isolation; needs **Neo4j 4.0+ Enterprise / AuraDB** and the
  per-tenant databases provisioned as part of onboarding.
- **`PropertyPartition`** — a single shared database (Community-friendly):

  ```fsharp
  let graph =
      Neo4jGraphStore.connectWith
          (Neo4jGraphStoreConfig.propertyPartition "neo4j")
          uri username password
  ```

See [`docs/graph/neo4j.md`](../graph/neo4j.md) for the trade-off, connection /
pool config, causal-cluster routing, and the driver-vs-server licensing boundary.

## Verification

- `dotnet build` — with the package referenced and the singleton registered, the
  build is clean; no existing composition needs editing.
- Resolve `IGraphStore` and run an existing query — it behaves as it did on the
  in-memory default (within the subset floor).
- To exercise the live conformance pack, point `TOOLUP_TEST_NEO4J_URI` (+
  `_USER` / `_PASSWORD`) at a running Neo4j and run the SDK test suite.

## Rollback

Additive and opt-in: drop the `PackageReference`, remove the singleton
registration, and set `GraphStore` back to `InMemoryGraphStore` (or leave
`CustomGraphStore` with a different companion). No data-format lock-in at the SDK
layer — the value model (`GraphNode` / `GraphEdge` / openCypher) is identical
across every tier.

## Adoption matrix

Most cells are ⛔ N-A — this is an additive opt-in companion; no consumer is
forced onto it. The tier exists for consumers with an existing Neo4j estate or
distributed-scale needs. The workspace `SDK-ADOPTION.md` row is regenerated from
the consumer manifests + shipped consumer-facing phases (do not hand-edit it).

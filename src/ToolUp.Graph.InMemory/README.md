# ToolUp.Graph.InMemory

The zero-dependency default `IGraphStore` — the GP-2 floor. A consumer with
default `ServerConfig` composition gets a working graph store with no
external dependency and no added NuGet package.

## What it is

- **Per-scope adjacency lists** held as immutable `ScopeGraph` snapshots,
  one per tenant scope, so scopes are partitioned by construction (GP 4).
- **A bounded openCypher subset interpreter** — the documented portability
  floor. A query that runs here is guaranteed to run against every engine
  companion; the reverse is not promised.

## What it is not

- **Not a full Cypher engine.** Out-of-subset constructs throw
  `CypherSubsetException` naming the clause — never a silently-wrong
  result. Compose an engine companion (Kùzu / Neo4j / AGE) for full Cypher.
- **Not durable.** No snapshot-to-disk. It is a development / test /
  small-single-instance default; durable storage is what the engine
  companions bring.

## Supported openCypher subset

```
MATCH  (n[:Label] [{prop: <lit>, ...}])
     | (a[:L]) <rel> (b[:L])
[WHERE <var.prop <op> <lit>, combined with AND / OR / parentheses>]
RETURN <var | var.prop> [AS alias] (',' ...)*
[ORDER BY <var.prop> [ASC|DESC]]
[LIMIT <int>]
```

- `<rel>` is `-[:T]->`, `<-[:T]-`, or `-[:T]-` (undirected); the type `T`
  is optional and `[r:T]` binds the relationship variable (single hop only).
- Variable-length paths: `-[:T*lo..hi]->`. Traversal is **cycle-safe** — a
  visited-node set guarantees termination on cyclic graphs.
- `<lit>` is `'str'` / `"str"` / int / float / `true` / `false` / `$param`.
- Comparison operators: `=`, `<>`, `>`, `>=`, `<`, `<=`.

Out of subset (throws `CypherSubsetException`): `CREATE` / `MERGE` / `SET` /
`DELETE` / `WITH` / `UNWIND` / `OPTIONAL` / `CALL` / aggregation functions /
`RETURN *` / `SKIP` / `UNION` / multi-hop patterns / multiple comma-separated
patterns.

## Concurrency

Reads take the current immutable snapshot with no lock; per-scope mutations
serialise the read-modify-write behind a short monitor lock that wraps only
synchronous pure transforms (never held across an await). A concurrent
reader never observes a half-applied mutation.

Licensed under Apache-2.0.

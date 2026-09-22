# ToolUp.LogStores.Postgres

**Status: reservation marker.** This directory is reserved for the future Postgres-backed `ILogStore` companion (the distributed half of the self-hosted log-store substrate). No code lives here yet.

## What ships here

A Postgres-backed implementation of `ILogStore` (the Phase 828 log-store
contract). The shipped default, `SqliteLogStore` in
`ToolUp.Platform.Server`, is one database file belonging to one process on
one host: durable across restarts, bounded by retention, and exactly right
for the single-node, air-gapped or data-residency deployment the substrate
was built for. It is not right for a horizontally-scaled deployment, where
each instance would search only the lines it happened to emit.

This companion will keep the same three operations against a shared
Postgres database — `tsvector` + a GIN index in place of FTS5 for the
message text, ordinary B-tree indexes on `timestamp` / `level` / `logger`
/ `scope_id` / `correlation_id` for the filter pushdown — so every
instance appends to, and searches, one store.

The swap is contract-free by construction: every caller writes through the
`ILogger` decorator and reads through `ILogStore.Search`, neither of which
names a backend. A deployment selects the companion by registering its own
`ILogStore` singleton before `compose` runs; the default registration is a
`TryAddSingleton` and stands aside.

Two rules the companion must honour, because they are the portable
contract rather than SQLite conveniences:

- **Second-granular timestamps** (GP 12 rule 6). `Append` truncates with
  `LogRecord.truncateToSecond`; ordering within one second falls back to
  append order.
- **Whole-word, case-insensitive text match.** `LogSearchQuery.tokenise`
  defines what a word is, and the default FTS5 tokeniser was chosen to
  agree with it. A Postgres text-search configuration that stems (the
  `english` dictionary does) would match `payments` for `payment` and
  break the contract; the companion configures `simple` accordingly.

`ILogStoreContract` (in `ToolUp.Platform.Tests`) is the conformance bar —
bind it to the companion's own factory, as the in-tree SQLite and
in-memory bindings do.

## Directory layout mirrors existing distributed companions

- `src/TimeSeriesStores/Timescale/` (the Phase 161 companion — the closest
  analogue: one interface, an in-tree default, a SQL companion beside it)
- `src/RateLimiters/Redis/` (reservation marker)
- `src/LogStores/Postgres/` ← this directory

Each companion lives in its own subdirectory under the appropriate
substrate-family root, ships its own `.fsproj` with a `<PackageId>`
matching the directory's brand, and registers its `IHealthCheck` probe +
`IConfigValidator` alongside the implementation.

## Why this marker exists

Reserving the directory ahead of the implementation keeps the two things
a later reader needs: the **neighbourhood** (an operator planning a
multi-instance rollout can see that the distributed option is a named,
located thing rather than an open question), and the **layout symmetry**
(a contributor looking for the companion finds it at the path the other
substrate families would have them predict).

Watch for the first `.fsproj` to land in this directory.

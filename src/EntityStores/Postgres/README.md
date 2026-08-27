# ToolUp.EntityStores.Postgres

A PostgreSQL / JSONB-backed `IEntityStore` for ToolUp.Platform — typed entity
persistence with real database indexes and SQL predicate pushdown over
[Npgsql](https://www.npgsql.org/). It binds the same `IEntityStoreContract`
pack as the in-tree `BlobEntityStore`, so a deployment swaps stores by
replacing one DI registration; module code is unchanged (GP 3).

**Server-only companion.** The Npgsql dependency is isolated here and never
reaches `ToolUp.Platform.*` (GP 1). PostgreSQL and Npgsql are free software
(GP 2). Distributed-ready — the store is stateless between calls over a
connection pool (GP 12); `PostgresEntityStore.capability` declares
`DistributedReady`.

## Why this over `BlobEntityStore`

`BlobEntityStore` satisfies the contract but evaluates range predicates by
enumerating index keys and `Ne`/`Not` by complementing against all entity ids
— fine for development, painful at production cardinality. This store pushes
every predicate down to SQL over the `jsonb` payload, so range and negation
queries execute in the database engine.

## Schema

One table holds every version of every entity:

```sql
CREATE TABLE toolup_entities (
    scope_id    text        NOT NULL,
    entity_type text        NOT NULL,
    entity_id   text        NOT NULL,
    version     integer     NOT NULL,
    payload     jsonb       NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (scope_id, entity_type, entity_id, version)
);
```

The primary key gives head-version lookups (`MAX(version)` per
`(scope_id, entity_type, entity_id)`) a covering index for free. Every
statement carries `scope_id`, so scope isolation is a structural predicate,
not a post-filter (GP 4).

`PostgresEntityStore.create` runs `CREATE TABLE IF NOT EXISTS` (auto-migrate)
on construction. To manage migrations yourself, apply the DDL above and use
the `IConfigValidator` with `autoMigrate = false` to fail the preflight if the
table is absent.

## Declared indexes

Each declared single-field `EntityRegistration` index becomes a JSONB
expression index, created lazily on the first `Save` of each entity type:

```sql
CREATE INDEX tue_Order_CustomerId ON toolup_entities ((payload ->> 'CustomerId'))
    WHERE entity_type = 'Order';
```

## Composition

Resolve the connection string from `ISecretStore` (never read env vars in the
companion), build an `NpgsqlDataSource`, and register the store + its ops
surface:

```fsharp skip=fragment
let connString = // resolved from ISecretStore
let dataSource = NpgsqlDataSource.Create connString
let store = PostgresEntityStore.create dataSource registry   // IEntityStore

// ops surface
let health = PostgresEntityStoreOps.healthCheck dataSource
let preflight = PostgresEntityStoreOps.validator dataSource "toolup_entities" true
```

## Divergences from `BlobEntityStore`

These are the seams where the `IEntityStore` interface met an unnatural fit
over JSONB (the input the pre-1.0 interface review asked for, GP 12):

1. **Index name must equal the JSONB key.** SQL pushdown addresses a field by
   name (`payload ->> 'Owner'`), so a single-field index's `Name` must equal
   the record field it indexes — the common case (`withIndex "Owner"
   _.Owner`). `BlobEntityStore`'s opaque `Extract : 'T -> string` closure
   allows a name that differs from the field, or a computed key.
2. **Compound indexes do not push down.** A `withCompoundIndex` key is a
   pipe-joined string with no single JSONB path, so `Eq(compoundName, …)`
   can't translate to a `payload ->> …` expression. Compound-index expression
   indexes are skipped; a query over a compound index name would not validate
   as a single-field predicate anyway.
3. **`Delete` is a hard delete of all versions.** `BlobEntityStore` removes
   the head version and leaves history in its underlying object store; here
   history lives in the same table, so `Delete` removes every version. A
   subsequent `Get` / `ListVersions` returns `NotFound` / empty either way
   (the contract's observable behaviour is identical).
4. **Concurrent same-entity saves may conflict on the version primary key.**
   Two writers that read the same `MAX(version)` both attempt to insert the
   next version; the primary key rejects the loser (a `StorageFailure` the
   caller retries). `BlobEntityStore` has the analogous race, mediated by its
   object store's retry.

## Testing

The `IEntityStoreContract` pack and a Postgres predicate-pushdown suite run
against a live database when `TOOLUP_TEST_POSTGRES` is set to a connection
string; on a fresh checkout without it, those arms report Pending (never
Failed).

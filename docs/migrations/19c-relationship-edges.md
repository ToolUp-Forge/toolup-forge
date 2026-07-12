# Phase 19c — declarative relationship edges (consumer adoption)

**What changes.** `EntityRegistration<'T>` gains a `Relationships` field and a
`withRelationship` / `withRelationships` builder, plus `EntityQuery.relatedTo` /
`relatedToAny` query helpers. A relationship declares how one entity type links
to another (cardinality, direction, the foreign-key field) as first-class
registration metadata.

**This is additive and opt-in.** Existing registrations keep working
untouched — an entity with no declared relationship has an empty
`Relationships` list and a byte-identical index set to before. There is nothing
to migrate unless you want the new behaviour.

## Adopting it (optional)

Where a foreign-key field already exists on an entity, add a `withRelationship`
call to declare the link:

```fsharp
// Before — the foreign key is an implicit field; traversals are hand-written.
let orderRegistration =
    EntityRegistration.create<Order> "Order"
    |> EntityRegistration.withIndex "CustomerId" _.CustomerId

// After — the relationship is declared once; the index is implied.
let orderRegistration =
    EntityRegistration.create<Order> "Order"
    |> EntityRegistration.withRelationship {
        Name = "BelongsToCustomer"
        Target = "Customer"
        Cardinality = Cardinality.ManyToOne
        ForeignKeyField = "CustomerId"
        Direction = RelationshipDirection.Outgoing
        JoinEntity = None
    }
```

`withRelationship` auto-indexes the foreign-key field, so you can drop the
explicit `withIndex "CustomerId"` — but keeping it is harmless (the auto-index
is idempotent with an explicit index on the same field).

Then replace hand-written foreign-key filters with `relatedTo`:

```fsharp
// Before
EntityQuery.forType<Order> "Order" |> EntityQuery.where (Eq("CustomerId", customerId))

// After
EntityQuery.forType<Order> "Order" |> EntityQuery.where (EntityQuery.relatedTo belongsToCustomer customerId)
```

Both compile to the same indexed `Eq` predicate — `relatedTo` just keeps the
foreign-key field name in one place (the declaration).

## Verification

- `dotnet build ToolUp.Forge.sln` — the new field/builders are additive; no
  existing call site needs changing.
- A registration with no relationship declared produces an empty
  `Relationships` list and the same indexes as before.
- A relationship-aware query returns the same set the hand-written
  foreign-key filter did.

## Rollback

Remove the `withRelationship` calls and (if you dropped it) restore the
explicit `withIndex` on the foreign-key field. No stored data changes — the
relationship is registration metadata, and the auto-index writes the same leaf
blobs an explicit index would.

See [`docs/entity-store/relationships.md`](../entity-store/relationships.md)
for the full reference (cardinality semantics, many-to-many via a join entity,
and the graph-projection accessor).

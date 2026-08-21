# Declarative relationships in the Entity Store

The Entity Store lets a module store domain records and query them by declared
indexed fields. **Relationships** extend a registration with a first-class,
declarative description of how one entity type links to another — so the link
is registration metadata rather than an ad-hoc foreign-key field the consumer
traverses by hand.

Declaring a relationship once drives two things:

1. **Relationship-aware queries** — `EntityQuery.relatedTo` compiles a
   relationship + a related id into an ordinary indexed predicate, so "all
   `Order`s for `Customer` X" is a single indexed lookup, not a scan.
2. **An enumerable edge set** — `EntityRegistration.relationships` exposes the
   declared list, the seam a graph projection reads to emit an entity's edges.

Relationships are **additive and opt-in**: an entity with no declared
relationships behaves exactly as before, and costs nothing.

## Declaring a relationship

A relationship is declared on the entity that carries the foreign key, via
`EntityRegistration.withRelationship`:

```fsharp
open ToolUp.Platform.EntityTypes

type Order = {
    Id: EntityId
    Type: string
    Version: int
    CustomerId: string   // ← the foreign key
    Total: string
}

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

The `Relationship` record:

| Field | Meaning |
|---|---|
| `Name` | Unique within the declaring entity type. |
| `Target` | The entity type this relationship points at (`"Customer"`). |
| `Cardinality` | `OneToOne` / `OneToMany` / `ManyToOne` / `ManyToMany`. |
| `ForeignKeyField` | The field carrying the related id (see below). |
| `Direction` | `Outgoing` (this entity references the target — the common case) / `Incoming` (the inverse view). |
| `JoinEntity` | Required for `ManyToMany`; `None` otherwise. |

### Validation (at registration time)

`withRelationship` validates immediately — a bad declaration fails fast at
compose, not at query time:

- a duplicate relationship `Name` is rejected;
- for a foreign-key cardinality (`OneToOne` / `OneToMany` / `ManyToOne`), the
  `ForeignKeyField` must be a real field on the entity record (reflection
  check) — a typo is rejected with a message listing the available fields;
- `ManyToMany` requires a `JoinEntity` (and the foreign-key cardinalities
  require `JoinEntity = None`).

## The implied index (a cost to know about)

Declaring a foreign-key relationship **auto-registers an index** on the
`ForeignKeyField`, so relationship-aware queries are indexed lookups rather
than scans. This is the same secondary-index machinery an explicit
`withIndex` uses — declaring the relationship simply saves you from writing the
`withIndex` yourself. It is **idempotent** with an explicit `withIndex` on the
same field (no double-registration), so this is safe:

```fsharp
EntityRegistration.create<Order> "Order"
|> EntityRegistration.withIndex "CustomerId" _.CustomerId   // explicit
|> EntityRegistration.withRelationship belongsToCustomer     // same field — no duplicate
```

The cost: every `Save` extracts and writes the foreign-key index leaf. For a
`ManyToMany` relationship the foreign key lives on the join entity, so **no
index is added on the source side** — index the two keys on the join entity's
own registration instead.

## Relationship-aware queries

`EntityQuery.relatedTo` compiles a relationship + a related id into an `Eq`
predicate over the foreign-key field; `relatedToAny` produces an `In` over
several ids. Both execute through the existing query executor unchanged:

```fsharp
open ToolUp.Platform

// "all Orders for Customer c-1"
let ordersForCustomer = async {
    let q =
        EntityQuery.forType<Order> "Order"
        |> EntityQuery.where (EntityQuery.relatedTo belongsToCustomer "c-1")

    return! store.Query<Order>(scope, q)
}
```

Because the foreign-key field is auto-indexed, the query validates and runs as
an indexed lookup.

### Many-to-many (through a join entity)

A `ManyToMany` link is resolved through a declared **join entity** in two
indexed lookups. Model the association as its own entity carrying both foreign
keys, and index them:

```fsharp
type Enrollment = {
    Id: EntityId
    Type: string
    Version: int
    StudentId: string   // source key
    CourseId: string    // target key
}

let enrollmentRegistration =
    EntityRegistration.create<Enrollment> "Enrollment"
    |> EntityRegistration.withIndex "StudentId" _.StudentId
    |> EntityRegistration.withIndex "CourseId" _.CourseId

let enrolledInCourses = {
    Name = "EnrolledInCourses"
    Target = "Course"
    Cardinality = Cardinality.ManyToMany
    ForeignKeyField = "StudentId"   // on the JOIN entity
    Direction = RelationshipDirection.Outgoing
    JoinEntity = Some { EntityType = "Enrollment"; TargetKeyField = "CourseId" }
}
```

Resolving "the courses `s-1` is enrolled in":

```fsharp
let coursesFor (studentId: string) = async {
    // Leg 1 — query the join entity by the source key.
    let joinQuery =
        EntityQuery.forType<Enrollment> "Enrollment"
        |> EntityQuery.where (EntityQuery.relatedTo enrolledInCourses studentId)

    match! store.Query<Enrollment>(scope, joinQuery) with
    | Error e -> return Error e
    | Ok enrollments ->
        // Leg 2 — load the targets by their extracted ids.
        let courseIds = enrollments |> List.map _.CourseId

        let! courses =
            courseIds
            |> List.map (fun cid -> store.Get<Course>(scope, "Course", cid))
            |> Async.Sequential

        return Ok courses
}
```

## Graph projection

`EntityRegistration.relationships` returns the declared list as a pure read of
registration metadata:

```fsharp
let edges = EntityRegistration.relationships orderRegistration
```

If you also compose a graph store, these declarations become your graph edges
— the projection bridge reads exactly this accessor, so the relationship is
declared once and serves both relational and graph queries. If you use only
the relational store, the accessor is simply unused.

## Out of scope

- **Referential integrity** (cascade delete, orphan prevention). Declaring a
  relationship describes it; the store does not enforce foreign-key
  constraints. The Entity Store is intentionally not a relational DBMS.
- **Cross-store relationships.** Relationships are within one entity store.

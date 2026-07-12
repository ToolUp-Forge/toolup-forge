// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.EntityQuery

open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes

// ─── Entity query builder ───────────────────────────────────────────
//
// Fluent builder for `EntityQuery<'T>`. Construction is total; every
// field has a sensible default (`Where = None`, `Skip = 0`, `Take =
// Int32.MaxValue`). Validation against the entity's registered
// indexes happens via `validate` — call after the query is fully
// built and before passing it to `IEntityStore.Query`.

/// Start building a query for an entity type.
let forType<'T> (entityType: string) : EntityQuery<'T> = {
    EntityType = entityType
    Where = None
    OrderBy = None
    Skip = 0
    Take = System.Int32.MaxValue
}

/// Set the predicate. Replaces any prior `Where` (use `andWhere` /
/// `orWhere` to extend).
let where (predicate: Predicate) (q: EntityQuery<'T>) : EntityQuery<'T> = { q with Where = Some predicate }

/// AND the new predicate onto the existing one. If no existing
/// predicate, equivalent to `where`.
let andWhere (predicate: Predicate) (q: EntityQuery<'T>) : EntityQuery<'T> = {
    q with
        Where =
            match q.Where with
            | Some existing -> Some(And(existing, predicate))
            | None -> Some predicate
}

/// OR the new predicate onto the existing one. If no existing
/// predicate, equivalent to `where` (since `Or` with nothing on the
/// other side is meaningless; treat as a single-predicate query).
let orWhere (predicate: Predicate) (q: EntityQuery<'T>) : EntityQuery<'T> = {
    q with
        Where =
            match q.Where with
            | Some existing -> Some(Or(existing, predicate))
            | None -> Some predicate
}

/// Sort by an indexed field.
let orderBy (fieldName: string) (direction: SortDirection) (q: EntityQuery<'T>) : EntityQuery<'T> = {
    q with
        OrderBy = Some(fieldName, direction)
}

/// Skip the first N results.
let skip (n: int) (q: EntityQuery<'T>) : EntityQuery<'T> = { q with Skip = n }

/// Take at most N results.
let take (n: int) (q: EntityQuery<'T>) : EntityQuery<'T> = { q with Take = n }

// ─── Phase 19c — relationship-aware predicates ──────────────────────
//
// A declared `Relationship` (see `EntityTypes`) compiles to an ordinary
// indexed predicate over its foreign-key field, so relationship-aware
// queries execute through the existing `EntityQueryExecutor` with no new
// execution machinery — and validate, because `withRelationship`
// auto-indexes the foreign-key field.
//
// For a foreign-key cardinality (OneToOne / OneToMany / ManyToOne), run
// the predicate against the declaring entity type: "all Orders for
// Customer X" = `forType<Order> "Order" |> where (relatedTo rel X)`.
//
// For ManyToMany, the predicate targets the JOIN entity (its
// `ForeignKeyField` is the source key), so resolution is two indexed
// lookups: (1) query the join entity with `relatedTo rel sourceId`,
// (2) read `rel.JoinEntity.Value.TargetKeyField` off each join row and
// load those targets by id. The predicate here is step (1).

/// Predicate selecting the rows related to `relatedId` through
/// `relationship` — an `Eq` over the relationship's foreign-key field.
/// Indexed (19c auto-registers the index), so it executes through the
/// existing executor unchanged.
let relatedTo (relationship: Relationship) (relatedId: EntityId) : Predicate =
    Eq(relationship.ForeignKeyField, relatedId)

/// As `relatedTo`, for several related ids at once — an `In` over the
/// foreign-key field (union of indexed lookups). Useful for the second
/// leg of a ManyToMany resolution, or a batched parent lookup.
let relatedToAny (relationship: Relationship) (relatedIds: EntityId list) : Predicate =
    In(relationship.ForeignKeyField, relatedIds)

/// Walk the predicate tree, collecting every leaf field name.
let rec private collectLeafFields (p: Predicate) : string list =
    match p with
    | Eq(f, _)
    | Ne(f, _)
    | Gt(f, _)
    | Gte(f, _)
    | Lt(f, _)
    | Lte(f, _)
    | In(f, _) -> [ f ]
    | And(a, b)
    | Or(a, b) -> collectLeafFields a @ collectLeafFields b
    | Not inner -> collectLeafFields inner

/// Validate that every predicate leaf and the OrderBy field are
/// declared indexes on the entity registration. Returns the query
/// unchanged on `Ok`; the first encountered field-not-found case
/// otherwise.
let validate
    (q: EntityQuery<'T>)
    (registration: EntityRegistration<'T>)
    : Result<EntityQuery<'T>, IndexValidationError> =
    if q.EntityType <> registration.EntityType then
        Error(IndexValidationError.UnknownEntityType q.EntityType)
    else
        let indexNames = registration.Indexes |> List.map _.Name

        // Validate Where predicate fields.
        let predicateError =
            match q.Where with
            | None -> None
            | Some p ->
                collectLeafFields p
                |> List.tryFind (fun f -> not (List.contains f indexNames))
                |> Option.map NonIndexedField

        // Validate OrderBy field.
        let sortError =
            match q.OrderBy with
            | None -> None
            | Some(f, _) ->
                if List.contains f indexNames then
                    None
                else
                    Some(NonIndexedSortField f)

        match predicateError, sortError with
        | Some err, _ -> Error err
        | _, Some err -> Error err
        | None, None -> Ok q
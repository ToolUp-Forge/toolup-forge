// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.EntityQueryTypes

// ─── Entity query types ─────────────────────────────────────────────
//
// Typed predicate AST + sort/paging configuration for `IEntityStore.Query`.
// Restricted by design: predicates can reference declared indexes
// only — non-indexed fields fail at query-construction time, not at
// execution. This keeps query cost explicit (every leaf is one
// `BlobIndex.Lookup`).
//
// Out of scope (deferred follow-ups): aggregations (count/sum/avg),
// joins, full-text predicates, nested-record queries.

/// Predicate AST. Leaf nodes reference fields by string name; the
/// validator (`EntityQuery.validate`) checks that each leaf's field
/// is a declared index for the entity type at construction time.
///
/// Comparison ordering for `Gt`/`Gte`/`Lt`/`Gte` is **string ordering**
/// — values are stored as strings in the index segment. Numeric and
/// date comparisons require the index extractor to format values as
/// sortable strings (zero-padded numbers, ISO-8601 dates) so string
/// ordering matches semantic ordering. The SDK doesn't enforce this;
/// it's a contract between the entity author's index extractor and
/// the queries the module runs.
type Predicate =
    /// Equal: `field == value`. Resolves to `BlobIndex.Lookup value`.
    | Eq of fieldName: string * value: string
    /// Not-equal: `field != value`. Resolves to `ListAll - Eq(field, value)`.
    | Ne of fieldName: string * value: string
    /// Greater-than: `field > value` under string ordering.
    | Gt of fieldName: string * value: string
    /// Greater-than-or-equal.
    | Gte of fieldName: string * value: string
    /// Less-than.
    | Lt of fieldName: string * value: string
    /// Less-than-or-equal.
    | Lte of fieldName: string * value: string
    /// Set membership: `field IN [values]`. Resolves to a union of
    /// `BlobIndex.Lookup` calls.
    | In of fieldName: string * values: string list
    /// Phase 19b — full-text match: `field` (declared as a full-text
    /// field via `EntityRegistration.withFullTextField`) matches the
    /// bag-of-words `query`. Resolves to a BM25 lookup against the
    /// field's per-(entityType, field) sparse index within the scope.
    /// `fieldName` is validated against the registration's declared
    /// **full-text** fields (not its indexes) at query-construction
    /// time. v1 is whitespace-tokenised, lowercase, bag-of-words — no
    /// phrase queries, boolean operators, or stemming.
    | FullText of fieldName: string * query: string
    /// Conjunction. Both sides must hold.
    | And of Predicate * Predicate
    /// Disjunction. Either side must hold.
    | Or of Predicate * Predicate
    /// Negation. Resolves to `ListAll - inner`.
    | Not of Predicate

type SortDirection =
    | Ascending
    | Descending

/// Typed query against a single entity type. `'T` is a phantom-typed
/// witness — the `Predicate` AST itself is field-name-string-keyed,
/// and the entity store's `Query<'T>` accepts the typed instance to
/// shape the deserialised return type.
type EntityQuery<'T> = {
    EntityType: string
    Where: Predicate option
    OrderBy: (string * SortDirection) option
    Skip: int
    Take: int
}

/// Why query construction / validation failed.
type IndexValidationError =
    /// A predicate leaf references a field that isn't a declared
    /// index for the entity type. Includes the offending field name
    /// so callers can correct the registration or the query.
    | NonIndexedField of fieldName: string
    /// `OrderBy` references a non-indexed field.
    | NonIndexedSortField of fieldName: string
    /// The query's `EntityType` doesn't match any registered entity
    /// type.
    | UnknownEntityType of entityType: string
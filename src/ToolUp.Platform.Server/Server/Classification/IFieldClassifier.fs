// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Phase 41 — registry of field classifications + the DSAR lookup key.
/// A deployment declares classifications in F# code via
/// `FieldClassification.attach<'Entity>` (or loads them from a config
/// store) and the runtime registry answers three questions: what is
/// classified on an entity, is a given field classified, and which
/// classified (personal-data) fields exist for a DSAR subject.
///
/// **Six portability rules (GP 12).**
/// 1. *Identity by value* — entity names, field paths, subject ids are
///    strings; `FieldClassification` / `FieldHit` are immutable records.
/// 2. *Async at every boundary* — every method returns `Async<_>`.
/// 3. *Retry as data* — read-only lookups; no failure channel needed
///    beyond the empty list (an unknown entity classifies nothing).
/// 4. *Stateless between invocations* — the classifier resolves from its
///    backing registry / config store each call; no per-call state.
/// 5. *No cross-shard ordering* — classification is a pure lookup.
/// 6. *Precision N/A* — no timing primitive.
type IFieldClassifier =
    /// All field classifications declared for `entityName`. Empty when
    /// the entity has no classified fields.
    abstract Classify: entityName: string -> Async<FieldClassification list>

    /// DSAR helper — the personal-data (`Pii` / `Spi`) fields that apply
    /// to `subjectId`, as `FieldHit`s. Powers Phase 9h Article 15 export
    /// (which fields to pull) + Article 17 erasure (which to redact).
    abstract LookupBySubject: subjectId: string -> Async<FieldHit list>

    /// True when `(entityName, fieldPath)` carries a classification.
    abstract IsClassified: entityName: string * fieldPath: string -> Async<bool>
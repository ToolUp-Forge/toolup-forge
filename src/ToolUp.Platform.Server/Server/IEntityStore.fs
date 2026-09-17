module ToolUp.Platform.IEntityStore

open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes

// ─── Phase 19 — IEntityStore interface ──────────────────────────────
//
// Server-side abstraction for typed entity persistence + indexed
// lookup. Wraps `IDataObjectStore` for versioning and `BlobIndex`
// (Phase 9f) for indexes; modules consume `IEntityStore` and never
// see the underlying storage primitives directly.
//
// The store is **scope-isolated by construction** — every method
// takes `scopeId` (or implicitly the caller's resolved scope through
// DI for handler callers). Cross-scope reads are structurally
// impossible because the implementation derives its container path
// from `scopeId`. Same discipline as `IDataObjectStore` and
// `IBlobStorage`.
//
// Phase 806 — every MUTATING member also takes an `EntityPrincipal`: the
// principal on the call is the principal on the lifecycle audit row
// the store records, and the replay provenance an offline replay
// carries rides the same parameter. There is no ambient scope to read
// and nothing for an implementation to infer — a store that stamps
// anything but the actor it was handed is wrong by inspection, and an
// out-of-tree implementation gets a compile error rather than a wrong
// row. Callers build one with `EntityPrincipal.ofPrincipal` from
// `AccessContext.UserId` (a job passes the principal that scheduled
// it); `EntityPrincipal.system` is for writes no principal made, never a
// default.
//
// Six-rule portability audit (Phase 9c, Guiding Principle 12):
//   1. Identity by value      — `EntityId: string`, `entityType: string`,
//                               `scopeId: string`. No live handles.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry/supervision as data — no callbacks; errors flow through
//                                  `EntityError` DU.
//   4. Stateless between calls — implementations cache index resolution
//                                but the contract assumes nothing
//                                survives between calls. A grain that
//                                deactivates re-resolves correctly.
//   5. No cross-shard ordering — versions linearly numbered within a
//                                single `(entityType, id, scopeId)`
//                                tuple. Ordering across different
//                                entities or scopes is not promised.
//   6. Precision at lower bound — n/a (no time semantics; versions
//                                 are integer-monotonic).

/// Typed entity store. The `Query` method lands in Phase 19a alongside
/// `EntityQueryExecutor` — until then, callers compose
/// `FindByIndex` + `Get` for predicate-shaped lookups.
///
/// **Stateless contract.** No method assumes in-memory state survives
/// between calls. Every operation derives its result from parameters +
/// `IDataObjectStore` + `BlobIndex`. An Orleans grain or Akka actor
/// implementing this could deactivate / restart between any two calls
/// and behave identically (Phase 9c Rule 4).
type IEntityStore =
    /// Save an entity. On first save, the store assigns version 1
    /// (overwriting whatever the input record's `Version` field
    /// said); subsequent saves bump the version monotonically.
    /// Indexes for the entity type are updated atomically with the
    /// version write — a save that fails before completion leaves
    /// the previous version + indexes intact.
    ///
    /// Returns the assigned `EntityRef<'T>` so callers know what
    /// version they wrote. Callers use the returned `Version` for
    /// optimistic concurrency on subsequent reads.
    ///
    /// `actor` is stamped on the `EntityCreated` / `EntityUpdated` row
    /// as `UserId` (+ `OnBehalfOf`, + `Replay`) and as `CreatedBy` on
    /// the stored version (Phase 806).
    abstract Save<'T> :
        scopeId: string * actor: EntityPrincipal * entity: 'T -> Async<Result<EntityRef<'T>, EntityError>>

    /// Phase 753 — compare-and-set save. Persist `entity` as version
    /// `expectedVersion + 1` if — and only if — the entity's head
    /// version is `expectedVersion` right now. `expectedVersion = 0`
    /// means "the entity must not exist yet" (create-only). When the
    /// head has moved, returns `EntityError.VersionConflict` naming
    /// the expected and the actual head version, writes nothing, and
    /// leaves every index untouched; the caller re-reads, merges, and
    /// states the actual version as its new expectation.
    ///
    /// `Save` stays last-writer-wins (GP 11 — nothing about the
    /// existing method changes); this is the additive surface for
    /// callers that carried a version through a round trip — an
    /// offline queue, a co-editing session, a form the user opened an
    /// hour ago. Implementations MUST make two racing calls at the same
    /// `expectedVersion` resolve to exactly one `Ok` and one
    /// `VersionConflict`, with no version skipped or duplicated,
    /// wherever the wrapped storage can express an atomic claim; an
    /// implementation over storage that cannot documents the residual
    /// window in its own header.
    abstract SaveIfVersion<'T> :
        scopeId: string * actor: EntityPrincipal * entity: 'T * expectedVersion: int ->
            Async<Result<EntityRef<'T>, EntityError>>

    /// Fetch the latest version of an entity. Returns `NotFound` when
    /// the `(entityType, entityId)` pair has no head version in this
    /// scope.
    abstract Get<'T> : scopeId: string * entityType: string * entityId: EntityId -> Async<Result<'T, EntityError>>

    /// Fetch a specific historical version. Returns `NotFound` when
    /// the version doesn't exist (either deleted or never written).
    /// Useful for audit / rollback workflows.
    abstract GetVersion<'T> :
        scopeId: string * entityType: string * entityId: EntityId * version: int -> Async<Result<'T, EntityError>>

    /// List every version's `EntityRef` for a single entity, sorted
    /// ascending by `Version`. Empty when the entity has no versions
    /// — callers that need to distinguish "no versions" from "exists
    /// with one version" should check `List.length`.
    abstract ListVersions<'T> : scopeId: string * entityType: string * entityId: EntityId -> Async<EntityRef<'T> list>

    /// Delete the entity. Removes the head version's metadata and
    /// drops every declared index entry. Historical versions remain
    /// in the underlying `IDataObjectStore` (per Phase 7's deletion
    /// semantics).
    ///
    /// Idempotent: deleting a non-existent entity returns `Ok` without
    /// error. `actor` is stamped on the `EntityDeleted` row (Phase 806).
    abstract Delete:
        scopeId: string * actor: EntityPrincipal * entityType: string * entityId: EntityId ->
            Async<Result<unit, EntityError>>

    /// Phase 753 — compare-and-set delete, the twin of `SaveIfVersion`.
    /// Delete the entity if — and only if — its head version is
    /// `expectedVersion` right now (`0` = "must not exist", which makes
    /// the call an idempotent `Ok` exactly as `Delete` is). When the
    /// head has moved, returns `EntityError.VersionConflict` naming
    /// both versions and removes nothing. Since Phase 806 the compare
    /// and the removal are one act wherever the wrapped storage can
    /// express an atomic claim (`IConditionalDataObjectStore.DeleteIfVersion`
    /// over the default data-object store; the `DELETE` predicate over
    /// SQL): a `SaveIfVersion` and a `DeleteIfVersion` racing at the same
    /// expectation resolve to exactly one `Ok`. An implementation over
    /// storage that cannot documents the residual window in its own
    /// header.
    abstract DeleteIfVersion:
        scopeId: string * actor: EntityPrincipal * entityType: string * entityId: EntityId * expectedVersion: int ->
            Async<Result<unit, EntityError>>

    /// Look up entities by a single declared index. Returns every
    /// entity whose indexed value equals `value` (exact-match only;
    /// `>` / `<` / `In` flow through the Phase 19a query layer).
    /// Returns `Error InvalidIndex` when the index isn't declared
    /// for the entity type.
    abstract FindByIndex<'T> :
        scopeId: string * entityType: string * indexName: string * value: string ->
            Async<Result<EntityRef<'T> list, EntityError>>

    /// Count entities of a given type in the scope. Cheap when the
    /// store can answer from index totals; potentially expensive
    /// when it has to enumerate version metadata.
    abstract Count: scopeId: string * entityType: string -> Async<int>

    /// Page through every entity of a type. `skip` / `take` are
    /// applied after sorting by entity id ascending — deterministic
    /// across cold starts. Returns `EntityRef` (not the full record)
    /// so callers don't pay the download cost when they only need
    /// existence.
    abstract ListAll<'T> : scopeId: string * entityType: string * skip: int * take: int -> Async<EntityRef<'T> list>

    /// Predicate-shaped query — Phase 19a's relational layer over the
    /// declared indexes. The query is validated against the entity's
    /// registration before execution; non-indexed predicate leaves
    /// (or sort fields) return `Error InvalidIndex`.
    ///
    /// Returns the materialised entity records (not just refs) —
    /// callers typically want both shape verification and the
    /// payload. For ref-only output use `FindByIndex` or `ListAll`.
    abstract Query<'T> : scopeId: string * query: EntityQuery<'T> -> Async<Result<'T list, EntityError>>
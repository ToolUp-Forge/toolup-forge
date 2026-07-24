// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── IUserSchemaStore ────────────────────────────────────────────────
//
// Server-side store for user-authored schemas (Phase 7b): CRUD over
// `UserAuthoredSchema`, per-scope listing, version-history walk, and
// typed `MigrationPlan` execution. Scope-isolated by construction —
// every method takes `scopeId` and the implementation derives all
// storage keys from it, so Team A's schemas are structurally unreachable
// from Team B (GP 4).
//
// **Phase 9c portability rules** (GP 12, all six honoured):
//   1. Identity by value — `scopeId` / `schemaId` are strings,
//      `UserAuthoredSchema` / `MigrationOutcome` are value records; no
//      live handles.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry / supervision as data — none leak; failures are typed
//      `UserSchemaError` results.
//   4. Stateless handlers between invocations — the actor id is passed
//      per call (`actorUserId`), never closure-captured.
//   5. No cross-shard ordering promises — versioning is per
//      `(scopeId, schemaId)`; no cross-schema ordering guarantee.
//   6. Precision at the lower bound — no timing primitives.

/// Store for user-authored, versioned data schemas. The default
/// implementation (`BlobUserSchemaStore`) rides the Phase 7 versioned
/// object store (`IDataObjectStore`); a distributed implementation can
/// drop in without changing call sites.
type IUserSchemaStore =
    /// Commit a schema version. Assigns the next monotonic `Version`
    /// (overwriting the caller's), persists it append-only, and emits the
    /// `SchemaChanged` (+ `SchemaApproved` when `ProposedBy` is
    /// `AIWithApproval`) audit event attributed to `actorUserId`.
    abstract Save:
        scopeId: string * schema: UserAuthoredSchema * actorUserId: string ->
            Async<Result<UserAuthoredSchema, UserSchemaError>>

    /// Latest version of a schema in the scope. `SchemaNotFound` when it
    /// does not exist.
    abstract Get: scopeId: string * schemaId: SchemaId -> Async<Result<UserAuthoredSchema, UserSchemaError>>

    /// A specific historical version.
    abstract GetVersion:
        scopeId: string * schemaId: SchemaId * version: int -> Async<Result<UserAuthoredSchema, UserSchemaError>>

    /// Latest version of every schema in the scope.
    abstract List: scopeId: string -> Async<UserAuthoredSchema list>

    /// Full version history for a schema, oldest-first. Empty when the
    /// schema does not exist.
    abstract History: scopeId: string * schemaId: SchemaId -> Async<UserAuthoredSchema list>

    /// Delete a schema (all versions) in the scope. Idempotent — deleting
    /// an unknown schema returns `Ok`. Emits `SchemaChanged` (Deleted).
    abstract Delete: scopeId: string * schemaId: SchemaId * actorUserId: string -> Async<Result<unit, UserSchemaError>>

    /// Apply a typed `MigrationPlan` to a schema: evolve the schema to a
    /// new version (recording `EvolvedFrom` + the plan) and transform
    /// every stored instance of the schema (`AddField` populates the
    /// default; `RemoveField` / `RenameField` reshape; the type-change
    /// cases are schema-only). Emits `SchemaChanged` (Migrated). This is
    /// the substrate entry point the `SchemaMigrationJobHandler` calls so
    /// long-running migrations run through `IJobScheduler` and survive
    /// restarts.
    abstract ExecuteMigration:
        scopeId: string * schemaId: SchemaId * migrations: SchemaMigration list * actorUserId: string ->
            Async<Result<MigrationOutcome, UserSchemaError>>
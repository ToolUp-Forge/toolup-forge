// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Module data-migration types (Phase 10a) ─────────────────────
//
// When a module evolves the shape it persists — adds a field, renames
// a column, changes a DU case — every already-stored object for every
// team is written in the OLD shape. Without a first-class migration
// path the choices are all bad: a breaking release, best-effort
// backward compatibility smeared through every read path, or silently
// losing older teams' data.
//
// This file carries the Fable-safe half of the substrate: the version
// stamp, the migrator seam, the chain-resolution error DU, and the
// per-team status record the admin UI renders. It has no dependency
// beyond the BCL, so it compiles on both hosts (GP 10) and a client
// can read and display a team's migration progress without a
// server-only type crossing the wire.
//
// The server half — the registry, the status store, the background
// runner — lives in `ToolUp.Platform.Server`.

/// Monotonic, module-owned schema version for one data type. Version
/// `1` is the implicit floor: an object stored before this substrate
/// existed carries no stamp at all and is read as version 1, which is
/// what makes adoption free for a deployment that never opts in
/// (GP 11).
type SchemaVersion = int

/// The `DataObject.Metadata` key the runner stamps, plus the readers
/// and writers over it. The stamp lives in metadata rather than in
/// the content bytes deliberately: the store already returns metadata
/// for every object in a scope from a single `ListObjects` call, so
/// deciding whether an object is current costs no content download.
module MigrationMetadata =

    /// Metadata key carrying the object's schema version, as a decimal
    /// string. Underscore-prefixed to match the store's other reserved
    /// metadata keys (`_recovered_from`).
    [<Literal>]
    let SchemaVersionKey = "_schemaVersion"

    /// The version an unstamped object is read as.
    [<Literal>]
    let InitialVersion = 1

    /// `DataObject.CreatedBy` recorded on a version the runner wrote.
    /// Distinguishes a migration write from a user write in the
    /// preserved version history.
    [<Literal>]
    let MigrationPrincipal = "_platform.migration"

    /// Read an object's schema version from its metadata. An absent,
    /// malformed, or non-positive stamp reads as `InitialVersion` —
    /// never as an error, because the overwhelmingly common case is an
    /// object written before the module declared any version at all.
    let readVersion (metadata: Map<string, string>) : SchemaVersion =
        match metadata.TryFind SchemaVersionKey with
        | Some raw ->
            match Int32.TryParse raw with
            | true, v when v >= InitialVersion -> v
            | _ -> InitialVersion
        | None -> InitialVersion

    /// Stamp `version` onto a metadata map, replacing any prior stamp.
    let stampVersion (version: SchemaVersion) (metadata: Map<string, string>) : Map<string, string> =
        metadata |> Map.add SchemaVersionKey (string version)

/// One step of a module's schema evolution: a pure forward upgrade
/// from `FromVersion` to `ToVersion` for a single data type.
///
/// **The payload is erased on purpose.** A migrator is authored by the
/// module that owns the data type, and only that module knows the V_n
/// and V_(n+1) record shapes; the SDK never does. `Migrate` therefore
/// takes and returns `obj`, and the runner boxes the object's content
/// on the way in and unboxes it on the way out — a symmetric
/// same-module-known-type cast at a registry seam, the same shape as
/// `DataTypeDisplay.RenderSummary`. The runner accepts a `byte[]` or a
/// `string` back and refuses anything else with a typed failure rather
/// than writing bytes it cannot account for.
///
/// **Forward-only.** There is no downgrade direction, deliberately —
/// rollback is a deploy revert plus a snapshot restore, and a
/// reversible-migration contract costs every module author twice the
/// work for a case that is rare and better served by backups.
///
/// Portability audit (GP 12): identity by value (`string` data-type id
/// + `int` versions), async at the boundary, failure as a raised
/// exception the runner converts to `MigrationFailure` data, stateless
/// between invocations (every call receives its whole input as the
/// parameter), single-object (no cross-shard ordering claim), no
/// precision surface.
type IDataMigrator =
    /// Data type this migrator upgrades. Matches `DataType.Id`.
    abstract DataTypeId: string
    /// Version this migrator reads. Must be >= 1.
    abstract FromVersion: SchemaVersion
    /// Version this migrator writes. Must be > `FromVersion`.
    abstract ToVersion: SchemaVersion
    /// Upgrade one object's content. The runner supplies the stored
    /// content boxed (`byte[]`); the return must be a `byte[]` or a
    /// `string`. Raising is a per-object failure, not a run failure —
    /// see the failure policy on `MigrationStatus`.
    abstract Migrate: obj -> Async<obj>

/// Why a version chain could not be resolved for one data type.
/// Chain resolution is a pure function over the registered migrator
/// set, so every one of these is a *registration* defect discoverable
/// without touching storage.
type MigrationChainError =
    /// No registered migrator reads `atVersion`, so the chain cannot
    /// advance past it.
    | NoMigrationPath of dataTypeId: string * atVersion: SchemaVersion
    /// More than one registered migrator reads `atVersion`. The
    /// substrate refuses to pick one — two forward paths from the same
    /// version is an authoring error, not a preference.
    | AmbiguousMigrationStep of dataTypeId: string * atVersion: SchemaVersion * candidates: int
    /// A registered migrator does not advance (`ToVersion` <=
    /// `FromVersion`). Would loop forever if followed.
    | NonAdvancingMigrator of dataTypeId: string * fromVersion: SchemaVersion * toVersion: SchemaVersion
    /// A migrator's chain overshoots the declared current version — it
    /// would write an object at a version the module does not claim to
    /// read.
    | ChainOvershootsTarget of dataTypeId: string * reachedVersion: SchemaVersion * targetVersion: SchemaVersion

module MigrationChainError =

    /// Human-readable rendering, for logs, the failure-log viewer, and
    /// the API's error string.
    let describe (error: MigrationChainError) : string =
        match error with
        | NoMigrationPath(dataTypeId, atVersion) ->
            $"No migrator registered for '%s{dataTypeId}' reading schema version %d{atVersion}."
        | AmbiguousMigrationStep(dataTypeId, atVersion, candidates) ->
            $"%d{candidates} migrators registered for '%s{dataTypeId}' read schema version %d{atVersion}; exactly one is required."
        | NonAdvancingMigrator(dataTypeId, fromVersion, toVersion) ->
            $"Migrator for '%s{dataTypeId}' declares FromVersion %d{fromVersion} and ToVersion %d{toVersion}; ToVersion must be greater."
        | ChainOvershootsTarget(dataTypeId, reachedVersion, targetVersion) ->
            $"Migration chain for '%s{dataTypeId}' reaches schema version %d{reachedVersion}, past the declared current version %d{targetVersion}."

/// One object's migration failure, retained on the team's status blob
/// so an operator can see what broke without trawling logs.
type MigrationFailure = {
    ObjectId: string
    /// Version the object was at when the step raised. The object is
    /// still at this version — a failed migration never writes.
    AtVersion: SchemaVersion
    /// Message from the raised exception, or the typed refusal when
    /// the migrator returned an unusable payload. Diagnostic only —
    /// not a stable contract.
    Error: string
    OccurredAt: DateTime
}

/// Terminal state of the most recent pass over one (team, data type).
type MigrationRunState =
    /// No pass has run for this pair yet.
    | MigrationIdle
    /// A pass is in flight. A status blob left in this state across a
    /// restart means the process died mid-pass; the next pass resumes
    /// and overwrites it (objects already upgraded are stamped, so
    /// they are skipped rather than re-migrated).
    | MigrationInProgress
    /// Every object in scope reached the current version.
    | MigrationComplete
    /// The pass finished, but at least one object was left behind at
    /// its old version. The remaining objects were migrated.
    | MigrationCompleteWithFailures
    /// The pass could not start: the registered migrator set does not
    /// form a usable chain for this data type. No object was touched.
    | MigrationChainBlocked of reason: string

/// Per-team, per-data-type migration progress. Persisted as
/// `_platform/migrations/{teamId}/{dataTypeId}.json` and read by the
/// admin UI, which renders it as
/// "Migrating Media Optimisation V2→V3: 47/120 objects".
type MigrationStatus = {
    TeamId: string
    DataTypeId: string
    /// The version the module currently declares (`DataType.SchemaVersion`).
    TargetVersion: SchemaVersion
    /// Objects of this data type found in the team's scope.
    TotalObjects: int
    /// Objects the pass upgraded.
    MigratedObjects: int
    /// Objects that were already at `TargetVersion` when the pass
    /// looked. Counted separately from `MigratedObjects` so a repeat
    /// pass reads as "nothing to do" rather than as zero progress.
    AlreadyCurrentObjects: int
    /// Objects left at their old version by a raising migrator.
    FailedObjects: int
    State: MigrationRunState
    StartedAt: DateTime option
    CompletedAt: DateTime option
    /// Most recent failures, newest first, capped at
    /// `MigrationStatus.maxRetainedFailures`. A pass that fails on
    /// thousands of objects must not produce an unbounded blob.
    Failures: MigrationFailure list
}

module MigrationStatus =

    /// Ceiling on `MigrationStatus.Failures`. The counts stay exact;
    /// only the per-object detail is trimmed.
    [<Literal>]
    let maxRetainedFailures = 50

    /// The status of a (team, data type) pair no pass has visited.
    let idle (teamId: string) (dataTypeId: string) (targetVersion: SchemaVersion) : MigrationStatus = {
        TeamId = teamId
        DataTypeId = dataTypeId
        TargetVersion = targetVersion
        TotalObjects = 0
        MigratedObjects = 0
        AlreadyCurrentObjects = 0
        FailedObjects = 0
        State = MigrationIdle
        StartedAt = None
        CompletedAt = None
        Failures = []
    }

    /// Objects still short of `TargetVersion` after the recorded pass.
    let outstanding (status: MigrationStatus) : int =
        max 0 (status.TotalObjects - status.MigratedObjects - status.AlreadyCurrentObjects)

/// Payload of the `MigrationFailed` event the runner writes to
/// `IEventStore` under the failing team's scope, once per failed
/// object. Distinct from the log line: the log is for the operator
/// reading stdout, the event is for anything that queries the event
/// store after the fact.
type MigrationFailedEvent = {
    TeamId: string
    DataTypeId: string
    ObjectId: string
    /// Version the object remains at.
    AtVersion: SchemaVersion
    TargetVersion: SchemaVersion
    Error: string
}

/// Wire constants for the migration event family.
module MigrationEvents =

    /// `ModuleEvent.SourceModule` for every event this substrate writes.
    [<Literal>]
    let SourceModule = "_platform.migrations"

    /// `ModuleEvent.EventType` for a per-object migration failure.
    [<Literal>]
    let MigrationFailedEventType = "MigrationFailed"
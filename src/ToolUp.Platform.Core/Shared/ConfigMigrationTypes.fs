// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Config schema-evolution types (Phase 10b) ───────────────────
//
// `ModuleConfigSchema` already describes a typed, validated config
// surface, and `IConfigStore` validates every WRITE against it. Reads
// were unversioned: a persisted document was decoded against whatever
// schema the running release declares, with no record of the schema it
// was written against. That is correct exactly as long as schemas are
// append-only. The moment a module renames a field, tightens an
// `Int (min, max)` bound, or drops a `Choice` option, every saved
// document for every team silently loses the affected values — the
// admin sees "my config didn't stick", and nothing names the cause.
//
// This file carries the Fable-safe half: the reserved version stamp,
// the migrator seam, the chain-resolution error DU, and the two event
// payloads. BCL-only, so it compiles on both hosts (GP 10).
//
// The server half — the registry, the chain runner, the store
// decorator that applies it, the drift tracker — lives in
// `ToolUp.Platform.Server` (`Server/Config/ConfigMigrationRegistry.fs`).
//
// **Deliberately the same mental model as Phase 10a's `IDataMigrator`**
// (`Shared/MigrationTypes.fs`): one migrator is one forward step,
// `FromVersion` → `ToVersion`, resolved into a chain by a pure function
// over the registered set, forward-only, with failure as a per-subject
// event rather than a raised exception the caller must handle. A module
// author who has written a data migrator can write a config migrator
// without learning a second shape.
//
// **One deliberate difference, and it is a simplification.** A data
// migrator takes and returns `obj` — the sanctioned type-erasure
// boundary 7 — because only the owning module knows its V_n record
// shape. A config document has no such problem: its persistence shape
// is `Map<string, string>` (JSON-per-field) for every module, by
// construction. So `IConfigMigrator.Migrate` is typed end to end, and
// this substrate adds NO new erasure boundary.

/// The `ModuleConfigSchema` version an unstamped (legacy) config
/// document is read as, plus the reserved key carrying the stamp and
/// the readers / writers over it.
///
/// The stamp lives INSIDE the persisted document rather than beside it
/// because `IConfigStore` has exactly one persistence surface — the
/// flat `Map<string, string>` — and a sidecar would have to be kept
/// atomic with it by every implementation. A reserved key is atomic by
/// construction: it is written by the same put that writes the values.
module ConfigMigrationMetadata =

    /// Reserved key carrying the document's schema version, as a
    /// decimal string. Underscore-prefixed to sit in the reserved
    /// namespace no module-declared field may occupy — `ConfigKeys`
    /// already reserves the `_platform*` module keys on the same
    /// principle.
    ///
    /// Spelled `_schema_version` rather than Phase 10a's
    /// `_schemaVersion` because the two live in different namespaces
    /// (a config document's field map vs a `DataObject`'s metadata map)
    /// and can never collide; this spelling is the one Phase 10b's
    /// acceptance criteria names.
    [<Literal>]
    let SchemaVersionKey = "_schema_version"

    /// The version an unstamped document is read as. Every document
    /// written before this substrate existed is version 1 — which is
    /// what makes adoption free for a deployment that never declares a
    /// version (GP 11).
    [<Literal>]
    let InitialVersion = 1

    /// Every key the config-document namespace reserves. Reserved keys
    /// are carried in the persisted map, excluded from schema
    /// validation, and stripped before any typed projection — a module
    /// can never see one, and a module-declared field can never shadow
    /// one.
    let reservedKeys: Set<string> = Set.ofList [ SchemaVersionKey ]

    /// Is `key` reserved? An explicit set rather than an
    /// underscore-prefix rule: a prefix rule would retroactively
    /// reserve — and therefore silently discard — any already-persisted
    /// module field whose key happens to start with an underscore,
    /// which is a data-loss change dressed as a naming convention
    /// (GP 11). Growing the set is a deliberate, reviewable edit.
    let isReservedKey (key: string) : bool = reservedKeys.Contains key

    /// Read a document's schema version. An absent, malformed, or
    /// non-positive stamp reads as `InitialVersion` — never as an
    /// error, because the overwhelmingly common case is a document
    /// written before any version was declared.
    let readVersion (values: Map<string, string>) : SchemaVersion =
        match values.TryFind SchemaVersionKey with
        | Some raw ->
            // The value is stored as a bare decimal, but a document
            // hand-edited into the JSON-per-field convention could
            // carry `"2"`. Accept both rather than silently reading a
            // quoted stamp as version 1.
            let unquoted = raw.Trim().Trim('"')

            match Int32.TryParse unquoted with
            | true, v when v >= InitialVersion -> v
            | _ -> InitialVersion
        | None -> InitialVersion

    /// Stamp `version` onto a value map, replacing any prior stamp.
    let stampVersion (version: SchemaVersion) (values: Map<string, string>) : Map<string, string> =
        values |> Map.add SchemaVersionKey (string version)

    /// Drop every reserved key. Applied before any typed projection and
    /// before the admin UI sees a document, so `_schema_version` never
    /// renders as a phantom field and never reaches a module's record.
    let stripReserved (values: Map<string, string>) : Map<string, string> =
        values |> Map.filter (fun k _ -> not (isReservedKey k))

/// One step of a module's config schema evolution: a pure forward
/// upgrade of a persisted config document from `FromVersion` to
/// `ToVersion`.
///
/// **Typed, not erased.** The persisted shape is `Map<string, string>`
/// — JSON-encoded values keyed by field key — for every module in
/// every deployment, so a migrator needs no knowledge the SDK lacks and
/// no `obj` at the seam. A V1→V2 rename is
/// `values |> Map.remove "model_id" |> Map.add "model" v`.
///
/// **Forward-only**, for the same reason `IDataMigrator` is: rollback
/// is a deploy revert plus a blob restore, and a reversible-migration
/// contract costs every module author twice the work for a case that
/// is rare and better served by backups.
///
/// **Purity is the contract.** `Migrate` is invoked lazily on read, on
/// whichever request happens to touch the document first, possibly
/// concurrently on two nodes for the same document. Because the
/// function is pure, two concurrent runs produce identical bytes and
/// the racing write-backs are idempotent. A migrator that reads
/// external state breaks that property.
///
/// Portability audit (GP 12): identity by value (`string` module key +
/// `int` versions), async at the boundary, failure as a raised
/// exception the runner converts to `ConfigMigrationFailure` data,
/// stateless between invocations (the whole input arrives as the
/// parameter), single-document (no cross-scope ordering claim), no
/// precision surface.
type IConfigMigrator =
    /// Module config key this migrator upgrades. Matches
    /// `ModuleConfigEntry.ModuleKey` / `ServerModule.Name`.
    abstract ModuleKey: string
    /// Version this migrator reads. Must be >= 1.
    abstract FromVersion: SchemaVersion
    /// Version this migrator writes. Must be > `FromVersion`.
    abstract ToVersion: SchemaVersion
    /// Upgrade one document's JSON-per-field map. Raising is a
    /// per-document failure, not a process failure — the source
    /// document is left untouched and the module is handed the last
    /// version that resolved cleanly.
    abstract Migrate: Map<string, string> -> Async<Map<string, string>>

/// Why a version chain could not be resolved for one module key. Chain
/// resolution is a pure function over the registered migrator set, so
/// every one of these is a *registration* defect — discoverable at
/// compose time without touching storage.
///
/// Mirrors `MigrationChainError` (Phase 10a) case for case.
type ConfigMigrationChainError =
    /// No registered migrator reads `atVersion`, so the chain cannot
    /// advance past it.
    | NoConfigMigrationPath of moduleKey: string * atVersion: SchemaVersion
    /// More than one registered migrator reads `atVersion`. The
    /// substrate refuses to pick one — two forward paths from the same
    /// version is an authoring error, not a preference.
    | AmbiguousConfigMigrationStep of moduleKey: string * atVersion: SchemaVersion * candidates: int
    /// A registered migrator does not advance (`ToVersion` <=
    /// `FromVersion`). Would loop forever if followed.
    | NonAdvancingConfigMigrator of moduleKey: string * fromVersion: SchemaVersion * toVersion: SchemaVersion
    /// A migrator's chain overshoots the declared current version — it
    /// would write a document at a version the module does not claim to
    /// read.
    | ConfigChainOvershootsTarget of moduleKey: string * reachedVersion: SchemaVersion * targetVersion: SchemaVersion

module ConfigMigrationChainError =

    /// Human-readable rendering, for logs, the failure event, and the
    /// `/dev/inspect` panel.
    let describe (error: ConfigMigrationChainError) : string =
        match error with
        | NoConfigMigrationPath(moduleKey, atVersion) ->
            $"No config migrator registered for '%s{moduleKey}' reading schema version %d{atVersion}."
        | AmbiguousConfigMigrationStep(moduleKey, atVersion, candidates) ->
            $"%d{candidates} config migrators registered for '%s{moduleKey}' read schema version %d{atVersion}; exactly one is required."
        | NonAdvancingConfigMigrator(moduleKey, fromVersion, toVersion) ->
            $"Config migrator for '%s{moduleKey}' declares FromVersion %d{fromVersion} and ToVersion %d{toVersion}; ToVersion must be greater."
        | ConfigChainOvershootsTarget(moduleKey, reachedVersion, targetVersion) ->
            $"Config migration chain for '%s{moduleKey}' reaches schema version %d{reachedVersion}, past the declared current version %d{targetVersion}."

/// Payload of the `ConfigMigrationFailed` event written to
/// `IEventStore` under the failing scope, once per failed document
/// read. Distinct from the log line: the log is for the operator
/// reading stdout, the event is for anything querying the store after
/// the fact.
type ConfigMigrationFailedEvent = {
    ScopeId: string
    ModuleKey: string
    /// Version the persisted document remains at. A failed migration
    /// never writes, so this is the version still on disk.
    AtVersion: SchemaVersion
    /// Version the module currently declares.
    TargetVersion: SchemaVersion
    /// Message from the raised exception, the chain-resolution refusal,
    /// or the schema-validation refusal of the migrated result.
    /// Diagnostic only — not a stable contract.
    Error: string
}

/// Which side of a schema/document disagreement one drift observation
/// describes. A field RENAME produces one of each — the old key is
/// orphaned and the new key falls back to its default — so collapsing
/// them into a single shape would report a rename as two
/// indistinguishable events and lose the evidence that they are the
/// same edit.
type ConfigFieldDriftKind =
    /// A field the schema declares is absent from a NON-EMPTY persisted
    /// document, so the read silently substituted `DefaultJson`. This
    /// is the user-visible half: an admin-configured value appears not
    /// to stick.
    | MissingPersistedField
    /// A key the persisted document carries that the schema no longer
    /// declares. Its value is dropped on every typed read and would be
    /// rejected on the next write. This is the evidence half: it names
    /// the value that is about to be lost.
    | OrphanedPersistedKey

/// Payload of the `ModuleConfigFieldMigrationNeeded` event
/// (2026-05-06 ToolUp.Platform gap audit, Gap 7).
///
/// Complements the migration mechanism rather than duplicating it:
/// migration fixes drift a deployment has SHIPPED a migrator for, and
/// this names the drift it has not — a release carrying a renamed
/// schema whose migrator has not been authored yet. Without it the
/// failure mode is entirely silent: the module reads defaults, the
/// admin re-enters the value, and nothing anywhere records that a
/// migrator is owed.
type ConfigFieldMigrationNeededEvent = {
    ScopeId: string
    ModuleKey: string
    FieldName: string
    /// The persisted JSON value being dropped, for
    /// `OrphanedPersistedKey`. `None` for `MissingPersistedField` —
    /// there is, by definition, no persisted value to report.
    PersistedValue: string option
    Kind: ConfigFieldDriftKind
    /// The version stamped on the document that produced this
    /// observation, and the version the module declares. Equal versions
    /// mean no migration is pending and the drift is unaddressed;
    /// unequal means a chain exists but did not resolve it.
    AtVersion: SchemaVersion
    TargetVersion: SchemaVersion
}

/// One row of the `/dev/inspect` "Pending Config Migrations" panel:
/// how much drift has been observed for one module since process
/// start, so an author can prioritise which migrator to write first.
///
/// Counts are per-process and reset on restart, deliberately: this is a
/// diagnostic prioritisation signal, not an audit trail. The audit
/// trail is the event stream above, which is durable.
type ConfigDriftSummary = {
    ModuleKey: string
    /// Version the module currently declares.
    TargetVersion: SchemaVersion
    /// Distinct `(fieldName, kind)` pairs observed.
    DistinctFields: int
    /// Total observations, i.e. reads that substituted or dropped a
    /// value. Higher than `DistinctFields` when the same drift is hit
    /// repeatedly.
    Observations: int
    /// Field names observed missing from a non-empty document.
    MissingFields: string list
    /// Persisted keys observed with no declaring schema field.
    OrphanedKeys: string list
    /// Whether any migrator is registered for this module at all. A
    /// module with drift and no migrator is the panel's headline case.
    HasMigrators: bool
}

/// Wire constants for the config schema-evolution event family.
module ConfigMigrationEvents =

    /// `ModuleEvent.SourceModule` for every event this substrate
    /// writes. Sits in the reserved `_platform.*` namespace beside
    /// `_platform.migrations` (Phase 10a).
    [<Literal>]
    let SourceModule = "_platform.config"

    /// `ModuleEvent.EventType` for a config migration that failed. The
    /// document is unchanged; the module was handed the last cleanly
    /// resolved version.
    [<Literal>]
    let ConfigMigrationFailedEventType = "ConfigMigrationFailed"

    /// `ModuleEvent.EventType` for one observed field-level drift
    /// between a persisted document and the schema reading it.
    [<Literal>]
    let FieldMigrationNeededEventType = "ModuleConfigFieldMigrationNeeded"
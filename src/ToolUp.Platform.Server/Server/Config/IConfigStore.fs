namespace ToolUp.Platform

/// Scope-indexed persistent store for team (and user) configuration.
///
/// One logical document per `(scope, moduleKey)` pair. The persisted
/// shape is `Map<string, string>` where values are serialised JSON —
/// this keeps the blob layout admin-UI friendly (the form edits a
/// flat string/string map) and lets typed `Get<'T>` / `Set<'T>`
/// round-trip F# records and DUs through `FableConverters`.
///
/// Lives in the server layer (not shared) because implementations
/// depend on server-only infrastructure (`IBlobStorage`, serialisation).
/// The Fable.Remoting admin API (`IConfigApi`) exposes a strict subset
/// to the browser; clients never see this interface directly.
///
/// Scope isolation is the caller's responsibility: handlers must
/// resolve the authenticated caller's scope via
/// `AccessContext.configScope` and hand the result to this store.
/// A misrouted `StorageScope` would read or write another team's
/// document — the same trust boundary as every other blob-backed
/// store (`PermissionStore`, `BlobProviderProfile`,
/// `PersistentEventStore`).
/// **Phase 10b — the reserved-key contract, binding on every
/// implementation.** A persisted document may carry keys in the
/// reserved namespace (`ConfigMigrationMetadata.isReservedKey`; today
/// `_schema_version`) alongside its module-declared fields. Three rules
/// follow, and an implementation that breaks any of them silently loses
/// version provenance:
///
///  1. **Round-trip them.** A reserved key written into a document must
///     come back out of it. `Validate.map` exempts them from the
///     unknown-key rejection precisely so a schema-validating write can
///     carry one.
///  2. **`GetRaw` returns them; every typed path strips them.**
///     `GetRaw` is the raw persistence surface — the migration
///     decorator reads the version stamp through it, so an
///     implementation that filtered there would make schema evolution
///     structurally impossible to detect. `Get<'T>` and
///     `GetEffective<'T>` strip before projecting, so no reserved key
///     ever reaches a module's record.
///  3. **Never invent one.** The stamp is written by the migration
///     decorator, and only once a module declares
///     `ModuleConfigSchema.SchemaVersion > 1`. An implementation that
///     stamped on its own would make a document look migrated when
///     nothing had migrated it.
///
/// Consumers reach the decorated store through DI; a direct
/// construction of a raw implementation (tests, tooling) sees rule 2's
/// raw surface, which is the deliberate seam and not a leak.
type IConfigStore =
    /// Read the raw persisted JSON-per-field map for a scope/module.
    /// Returns an empty map when no document exists. Used by the
    /// admin UI (which edits the map directly) and by `GetEffective`.
    ///
    /// Includes reserved keys — see rule 2 of the reserved-key contract
    /// above. The DI-registered store is the migration decorator, whose
    /// `GetRaw` migrates first and then strips, so an admin-UI caller
    /// never sees one.
    abstract GetRaw: scope: StorageScope * moduleKey: string -> Async<Map<string, string>>

    /// Deserialise the persisted document for a scope/module into
    /// `'T`. Returns `None` when the document is missing or cannot be
    /// decoded into `'T` — callers should treat both as "fall back to
    /// schema defaults" rather than surfacing a parse error, which is
    /// what `GetEffective` already does.
    abstract Get<'T> : scope: StorageScope * moduleKey: string -> Async<'T option>

    /// Overlay persisted values on top of the schema's declared
    /// defaults and deserialise into `'T`. Missing keys fall through
    /// to `DefaultJson`; the merged object is what modules see when
    /// they `Init` with config. Never throws — a decode failure on a
    /// single key quietly falls back to the default for that key.
    abstract GetEffective<'T> : scope: StorageScope * moduleKey: string * schema: ModuleConfigSchema -> Async<'T>

    /// Validate `value` against `schema` and persist the raw per-field
    /// JSON map. Validation failures (type mismatch, missing
    /// `Required` field, out-of-bounds number, unknown choice option)
    /// surface as `Error msg`; persistence failures forward the
    /// underlying `IBlobStorage.Upload` error.
    ///
    /// Write validation mirrors the admin UI's client-side validator
    /// — the UI gives fast feedback; this is the authoritative gate.
    abstract Set<'T> :
        scope: StorageScope * moduleKey: string * value: 'T * schema: ModuleConfigSchema -> Async<Result<unit, string>>

    /// Replace the persisted raw field map directly. Used by the
    /// admin UI, which edits the flat `Map<string, string>` shape.
    /// Each value is validated against the schema field of the same
    /// key; unknown keys and validation failures return `Error msg`.
    abstract SetRaw:
        scope: StorageScope * moduleKey: string * values: Map<string, string> * schema: ModuleConfigSchema ->
            Async<Result<unit, string>>

    /// Remove the persisted document for a scope/module. Idempotent
    /// — succeeds when no document exists. `GetEffective` after a
    /// `Clear` returns schema defaults for every field.
    abstract Clear: scope: StorageScope * moduleKey: string -> Async<unit>

    /// Phase 9h — GDPR Article 17 erasure surface. Erase (or redact)
    /// config documents in `scopeId` whose field values name
    /// `subjectUserId`, per `policy`:
    ///
    ///  - `HardDelete` — delete the whole config document for any
    ///    `(scope, moduleKey)` with a matching value. `GetEffective`
    ///    subsequently returns schema defaults.
    ///  - `Tombstone` / `RetainPerCompliance` — keep the document
    ///    and its key set; replace only the field values that
    ///    contain `subjectUserId` with the JSON-encoded
    ///    `Erasure.TombstoneMarker`. Config is operational settings,
    ///    not the audit fabric, so neither policy refuses — both
    ///    redact identifying values "where possible" and preserve the
    ///    document shape.
    ///
    /// **Subject match.** Persisted values are JSON-encoded strings;
    /// "names the subject" is a substring match of `subjectUserId`
    /// within a field value. Blank subject is a zero-count no-op.
    ///
    /// **Scope isolation (GP 4).** `scopeId` is the
    /// `StorageScope.Container` of the request scope; only documents
    /// under `config/{scopeId}/` are read or mutated — another
    /// scope's documents are structurally unreachable.
    ///
    /// `dryRun = true` counts affected documents without mutating.
    /// Portability audit (GP 12): identity by value, async at
    /// boundary, failure as `ErasureError`, stateless, single-scope,
    /// precision declared above.
    abstract Erase:
        scopeId: string * subjectUserId: string * policy: ErasurePolicy * dryRun: bool ->
            Async<Result<ErasureSummary, ErasureError>>
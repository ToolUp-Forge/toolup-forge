// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ConfigMigrationRegistry

open System
open System.Collections.Concurrent
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── Config schema evolution — server half (Phase 10b) ───────────
//
// The Fable-safe half (the reserved `_schema_version` stamp, the
// `IConfigMigrator` seam, the chain-error DU, the event payloads) lives
// in `ToolUp.Platform.Core/Shared/ConfigMigrationTypes.fs`. This file
// carries the parts that need the server: chain resolution over the
// registered set, the runner that applies a chain with the failure
// policy, the per-process drift tracker the `/dev/inspect` panel reads,
// and the `IConfigStore` decorator that fuses all three onto the read
// path.
//
// **Why a decorator rather than an edit to `BlobConfigStore`.** The
// acceptance criterion is that the reserved key is honoured by ALL
// `IConfigStore` implementations — the blob-backed default, the
// in-memory doubles, and any future cloud variant. A decorator is the
// only shape that delivers that literally: it wraps whatever instance
// `compose` resolved, so an implementation gets versioning without
// knowing the substrate exists. Baking the same logic into
// `BlobConfigStore` would have made "honoured by all implementations" a
// convention every future author has to re-read and re-implement, and
// would have widened that type's constructor — which the Public-API
// gate reads as a removal (`?arg` folds into one ctor token).

// ─── Chain resolution ────────────────────────────────────────────

/// Pure resolution of a module's forward migration path. Mirrors
/// `MigrationChain` (Phase 10a) — same rules, same refusals, so a
/// module author debugging one reads the other without translation.
module ConfigMigrationChain =

    /// Validate the registered set for one module key, independent of
    /// any stored document. Catches the two authoring defects that make
    /// a chain unusable no matter where it starts.
    let validateSet (moduleKey: string) (migrators: IConfigMigrator list) : ConfigMigrationChainError list =
        let nonAdvancing =
            migrators
            |> List.filter (fun m -> m.ToVersion <= m.FromVersion)
            |> List.map (fun m -> NonAdvancingConfigMigrator(moduleKey, m.FromVersion, m.ToVersion))

        let ambiguous =
            migrators
            |> List.filter (fun m -> m.ToVersion > m.FromVersion)
            |> List.groupBy _.FromVersion
            |> List.filter (fun (_, group) -> List.length group > 1)
            |> List.map (fun (atVersion, group) ->
                AmbiguousConfigMigrationStep(moduleKey, atVersion, List.length group))

        nonAdvancing @ ambiguous

    /// Resolve the ordered step list carrying `fromVersion` up to
    /// `targetVersion`. An empty list means the document is already
    /// current — the overwhelmingly common case, and the one that must
    /// cost nothing.
    ///
    /// A document stamped ABOVE the declared target is NOT an error
    /// here: it means the process is running an older release than the
    /// one that last wrote, which is an ordinary rolling-deploy state.
    /// It resolves to no steps, and the read falls back to the schema's
    /// defaults for any field this release does not know — the
    /// pre-existing behaviour, plus a drift observation naming it.
    let resolve
        (moduleKey: string)
        (migrators: IConfigMigrator list)
        (fromVersion: SchemaVersion)
        (targetVersion: SchemaVersion)
        : Result<IConfigMigrator list, ConfigMigrationChainError> =

        match validateSet moduleKey migrators with
        | firstError :: _ -> Error firstError
        | [] ->
            let rec walk (current: SchemaVersion) (acc: IConfigMigrator list) =
                if current >= targetVersion then
                    Ok(List.rev acc)
                else
                    match migrators |> List.tryFind (fun m -> m.FromVersion = current) with
                    | None -> Error(NoConfigMigrationPath(moduleKey, current))
                    | Some step ->
                        if step.ToVersion > targetVersion then
                            Error(ConfigChainOvershootsTarget(moduleKey, step.ToVersion, targetVersion))
                        else
                            walk step.ToVersion (step :: acc)

            if fromVersion >= targetVersion then
                Ok []
            else
                walk fromVersion []

/// The declared config-schema surface of a composed deployment: which
/// version each module key currently claims, and which migrators are
/// registered against it.
///
/// Built once at compose time from `ServerConfig.ModuleConfigs` (the
/// schemas) and `ServerConfig.ConfigMigrations` (the steps). Immutable
/// and stateless, so it is safe as a singleton and safe to share across
/// requests.
type ConfigMigrationRegistry(entries: ModuleConfigEntry list, migrators: IConfigMigrator list) =

    let targetVersions =
        entries |> List.map (fun e -> e.ModuleKey, e.Schema.SchemaVersion) |> Map.ofList

    let schemas = entries |> List.map (fun e -> e.ModuleKey, e.Schema) |> Map.ofList

    let byModule = migrators |> List.groupBy _.ModuleKey |> Map.ofList

    /// The version `moduleKey` currently declares. An unregistered key
    /// reads as the implicit floor, which makes every path over an
    /// unknown module a no-op rather than a failure.
    member _.TargetVersion(moduleKey: string) : SchemaVersion =
        targetVersions
        |> Map.tryFind moduleKey
        |> Option.defaultValue ConfigMigrationMetadata.InitialVersion

    member _.TrySchema(moduleKey: string) : ModuleConfigSchema option = schemas.TryFind moduleKey

    member _.MigratorsFor(moduleKey: string) : IConfigMigrator list =
        byModule |> Map.tryFind moduleKey |> Option.defaultValue []

    member this.ResolveChain
        (moduleKey: string, fromVersion: SchemaVersion)
        : Result<IConfigMigrator list, ConfigMigrationChainError> =
        ConfigMigrationChain.resolve moduleKey (this.MigratorsFor moduleKey) fromVersion (this.TargetVersion moduleKey)

    /// Every registration defect across every module key. Pure over the
    /// registered set — no storage is touched — so a composition root
    /// can surface these at startup rather than on the first read that
    /// happens to hit a broken chain.
    member this.ValidateAll() : ConfigMigrationChainError list =
        byModule
        |> Map.toList
        |> List.collect (fun (moduleKey, ms) ->
            let setErrors = ConfigMigrationChain.validateSet moduleKey ms

            // Only walk the chain when the set itself is sound —
            // otherwise every module reports the same defect twice,
            // once as a set error and once as an unwalkable chain.
            if not (List.isEmpty setErrors) then
                setErrors
            else
                match this.ResolveChain(moduleKey, ConfigMigrationMetadata.InitialVersion) with
                | Ok _ -> []
                | Error e -> [ e ])

    /// Module keys carrying at least one registered migrator.
    member _.MigratableModules: string list = byModule |> Map.toList |> List.map fst

    /// Does any module declare a version above the implicit floor? When
    /// nothing does, the whole substrate is inert: no stamp is written,
    /// no chain is resolved, and every read is byte-for-byte its
    /// pre-Phase-10b self (GP 11).
    member _.IsInert: bool =
        Map.isEmpty byModule
        && targetVersions
           |> Map.forall (fun _ v -> v <= ConfigMigrationMetadata.InitialVersion)

// ─── Drift tracking ──────────────────────────────────────────────

/// Per-process counter of observed schema/document drift, keyed by
/// `(moduleKey, fieldName, kind)`.
///
/// Counts are in-memory and reset on restart, deliberately: this is the
/// prioritisation signal an author reads off `/dev/inspect` to decide
/// which migrator to write first. The durable record is the
/// `ModuleConfigFieldMigrationNeeded` event stream, which this tracker
/// does not replace.
///
/// Thread-safe by construction (`ConcurrentDictionary` +
/// `Interlocked`-style `AddOrUpdate`). Bounded: `maxTrackedFields`
/// caps the distinct keys retained, so a deployment whose schema and
/// documents have diverged wholesale cannot grow this without limit.
type ConfigDriftTracker() =

    let counts = ConcurrentDictionary<string * string * ConfigFieldDriftKind, int>()

    /// Ceiling on distinct `(moduleKey, fieldName, kind)` keys. Past
    /// this the tracker stops admitting NEW keys and keeps counting the
    /// ones it holds — a panel that names 500 fields has already made
    /// its point, and an unbounded one is a leak.
    static member val MaxTrackedFields = 500 with get

    member _.Record(moduleKey: string, fieldName: string, kind: ConfigFieldDriftKind) : unit =
        let key = (moduleKey, fieldName, kind)

        if counts.ContainsKey key || counts.Count < ConfigDriftTracker.MaxTrackedFields then
            counts.AddOrUpdate(key, 1, (fun _ existing -> existing + 1)) |> ignore

    /// Rows for the `/dev/inspect` panel, one per module key with
    /// observed drift, ordered by observation count so the module most
    /// in need of a migrator sorts first.
    member _.Summarise(registry: ConfigMigrationRegistry) : ConfigDriftSummary list =
        counts
        |> Seq.map (fun kvp ->
            let (moduleKey, fieldName, kind) = kvp.Key
            moduleKey, fieldName, kind, kvp.Value)
        |> Seq.groupBy (fun (moduleKey, _, _, _) -> moduleKey)
        |> Seq.map (fun (moduleKey, rows) ->
            let rowList = List.ofSeq rows

            let named kind =
                rowList
                |> List.filter (fun (_, _, k, _) -> k = kind)
                |> List.map (fun (_, fieldName, _, _) -> fieldName)
                |> List.distinct
                |> List.sort

            {
                ModuleKey = moduleKey
                TargetVersion = registry.TargetVersion moduleKey
                DistinctFields = List.length rowList
                Observations = rowList |> List.sumBy (fun (_, _, _, count) -> count)
                MissingFields = named MissingPersistedField
                OrphanedKeys = named OrphanedPersistedKey
                HasMigrators = not (List.isEmpty (registry.MigratorsFor moduleKey))
            })
        |> Seq.sortByDescending _.Observations
        |> List.ofSeq

    /// Total observations across every module. The panel's headline
    /// number, and the cheap "is there anything to look at" probe.
    member _.TotalObservations: int = counts.Values |> Seq.sum

// ─── The store decorator ─────────────────────────────────────────

/// Everything the decorator needs, gathered once at compose time.
/// A record rather than five constructor parameters so a future
/// addition does not retype the constructor token the Public-API gate
/// reads (the same reasoning as the `?arg` rule in `CLAUDE.md`).
type ConfigMigrationSupport = {
    Registry: ConfigMigrationRegistry
    Drift: ConfigDriftTracker
    /// Failure and drift events land here. `None` in a composition with
    /// no event store: the failure policy degrades to log-only, which
    /// is strictly better than refusing the read.
    EventStore: IEventStore option
    Logger: ILogger
}

/// `IConfigStore` decorator applying the Phase 10b read-path migration
/// chain, the reserved `_schema_version` stamp, and drift observation
/// to whatever store it wraps.
///
/// **Read path.** Load the raw document; read its stamp (absent = 1);
/// resolve the chain to the module's declared version; run it; write
/// the upgraded document back, stamped, through the inner store's own
/// validating `SetRaw`; hand the caller the migrated values with every
/// reserved key stripped.
///
/// **Failure policy.** A raising migrator, an unresolvable chain, or a
/// migrated document the schema then rejects are all the same outcome:
/// log through `ILogger`, write a `ConfigMigrationFailed` event, leave
/// the persisted document untouched, and return the last version that
/// resolved cleanly. The module sees a coherent older version, never a
/// half-applied one and never a `null`-shaped record. It is not a
/// raise: a config read that throws takes out `Init` for a module whose
/// only problem is that someone owes it a migrator.
///
/// **Why the partially-migrated document is not written back.** A
/// chain that reaches V2 and fails on V2→V3 could persist the V2 result
/// and save that work on the next read. It deliberately does not: a
/// document stamped V2 is indistinguishable from one whose migration
/// completed, so persisting it would convert a loud, repeating failure
/// into a silent stall at an intermediate version. Migrators are pure
/// (see `IConfigMigrator`), so re-running the successful prefix on the
/// next read is free of side effects and cheap.
///
/// **Concurrency.** Two nodes reading the same stale document both
/// migrate and both write back. Because migrators are pure the two
/// writes carry identical bytes, so last-writer-wins is
/// indistinguishable from a lock. Atomicity is the inner store's
/// single put — the values and their stamp are written together or not
/// at all, which is why the stamp is a reserved key inside the document
/// rather than a sidecar.
type MigratingConfigStore(inner: IConfigStore, support: ConfigMigrationSupport) =

    let eventJsonOptions = FableConverters.create ()
    let registry = support.Registry
    let logger = support.Logger

    let emitEvent (scopeId: string) (eventType: string) (payload: 'T) = async {
        match support.EventStore with
        | None -> ()
        | Some store ->
            let evt: ModuleEvent = {
                Id = Guid.NewGuid()
                OccurredAt = DateTime.UtcNow
                ScopeId = scopeId
                SourceModule = ConfigMigrationEvents.SourceModule
                EventType = eventType
                Payload = JsonSerializer.Serialize(payload, eventJsonOptions)
            }

            try
                do! store.Write evt
            with ex ->
                // An event-store failure must never promote a
                // recoverable config read into a failed one — the same
                // rule `MigrationRunner` follows for its own failure
                // events.
                logger.Error("[ConfigMigration] event=config_event_write_error", Some ex)
    }

    /// Observe every field-level disagreement between a NON-EMPTY
    /// persisted document and the schema reading it, after any
    /// migration has run.
    ///
    /// The non-empty guard is load-bearing: an unconfigured module
    /// falling through to its declared defaults is the normal case, not
    /// drift, and counting it would bury the real signal under one
    /// observation per module per read.
    let observeDrift
        (scope: StorageScope)
        (moduleKey: string)
        (atVersion: SchemaVersion)
        (values: Map<string, string>)
        =
        async {
            if Map.isEmpty values then
                return ()
            else
                match registry.TrySchema moduleKey with
                | None -> return ()
                | Some schema ->
                    let declared = schema.Fields |> List.map _.Key |> Set.ofList
                    let targetVersion = registry.TargetVersion moduleKey

                    let missing =
                        schema.Fields
                        |> List.filter (fun f -> not (Map.containsKey f.Key values))
                        |> List.map (fun f -> f.Key, None, MissingPersistedField)

                    let orphaned =
                        values
                        |> Map.toList
                        |> List.filter (fun (k, _) -> not (declared.Contains k))
                        |> List.map (fun (k, v) -> k, Some v, OrphanedPersistedKey)

                    for fieldName, persistedValue, kind in missing @ orphaned do
                        support.Drift.Record(moduleKey, fieldName, kind)

                        let payload: ConfigFieldMigrationNeededEvent = {
                            ScopeId = scope.ScopeId
                            ModuleKey = moduleKey
                            FieldName = fieldName
                            PersistedValue = persistedValue
                            Kind = kind
                            AtVersion = atVersion
                            TargetVersion = targetVersion
                        }

                        do! emitEvent scope.ScopeId ConfigMigrationEvents.FieldMigrationNeededEventType payload
        }

    let fail (scope: StorageScope) (moduleKey: string) (atVersion: SchemaVersion) (reason: string) = async {
        let targetVersion = registry.TargetVersion moduleKey

        logger.Warn
            $"[ConfigMigration] event=config_migration_failed scope={scope.Container} moduleKey='{moduleKey}' atVersion={atVersion} targetVersion={targetVersion}: {reason} — the persisted document is unchanged and the module is being handed schema version {atVersion}."

        let payload: ConfigMigrationFailedEvent = {
            ScopeId = scope.ScopeId
            ModuleKey = moduleKey
            AtVersion = atVersion
            TargetVersion = targetVersion
            Error = reason
        }

        do! emitEvent scope.ScopeId ConfigMigrationEvents.ConfigMigrationFailedEventType payload
    }

    /// The whole read path: load, migrate, write back, observe, strip.
    /// Returns the values the caller should see — reserved keys
    /// removed, so no caller can accidentally surface or persist one.
    let loadMigrated (scope: StorageScope) (moduleKey: string) : Async<Map<string, string>> = async {
        let! persisted = inner.GetRaw(scope, moduleKey)
        let atVersion = ConfigMigrationMetadata.readVersion persisted
        let targetVersion = registry.TargetVersion moduleKey
        let values = ConfigMigrationMetadata.stripReserved persisted

        if atVersion >= targetVersion || Map.isEmpty persisted then
            // Already current (the overwhelmingly common case), or
            // nothing persisted at all. No chain resolution, no
            // allocation beyond the strip.
            do! observeDrift scope moduleKey atVersion values
            return values
        else
            match registry.ResolveChain(moduleKey, atVersion) with
            | Error chainError ->
                do! fail scope moduleKey atVersion (ConfigMigrationChainError.describe chainError)
                do! observeDrift scope moduleKey atVersion values
                return values
            | Ok [] ->
                do! observeDrift scope moduleKey atVersion values
                return values
            | Ok steps ->
                // Run the chain, keeping the last version that resolved
                // cleanly. `lastGood` is what the caller gets whether or
                // not the chain completes.
                let mutable lastGood = values
                let mutable lastGoodVersion = atVersion
                let mutable failure: string option = None

                for step in steps do
                    if failure.IsNone then
                        try
                            let! upgraded = step.Migrate lastGood
                            // A migrator returning null (a possible
                            // shape from a Fable-authored or
                            // reflection-built migrator) would NRE on
                            // the next Map operation. Refuse it here,
                            // named, rather than surfacing an opaque
                            // NullReferenceException from the write.
                            if isNull (box upgraded) then
                                failure <-
                                    Some $"Migrator {step.FromVersion}->{step.ToVersion} returned a null value map."
                            else
                                lastGood <- ConfigMigrationMetadata.stripReserved upgraded
                                lastGoodVersion <- step.ToVersion
                        with ex ->
                            failure <- Some $"Migrator {step.FromVersion}->{step.ToVersion} raised: {ex.Message}"

                match failure with
                | Some reason ->
                    do! fail scope moduleKey lastGoodVersion reason
                    do! observeDrift scope moduleKey lastGoodVersion lastGood
                    return lastGood
                | None ->
                    // Chain complete. Write back atomically, stamped.
                    // The inner store validates the result against the
                    // current schema — a migrator that produced an
                    // invalid document is caught here rather than
                    // persisted, and is reported as the migration
                    // failure it is.
                    match registry.TrySchema moduleKey with
                    | None ->
                        do! observeDrift scope moduleKey lastGoodVersion lastGood
                        return lastGood
                    | Some schema ->
                        let stamped = ConfigMigrationMetadata.stampVersion lastGoodVersion lastGood
                        let! writeResult = inner.SetRaw(scope, moduleKey, stamped, schema)

                        match writeResult with
                        | Error msg ->
                            do! fail scope moduleKey atVersion $"migrated document was not persisted: {msg}"
                            // The chain itself succeeded, so the module
                            // still sees the upgraded values — only the
                            // persistence of them failed, and the next
                            // read will retry.
                            do! observeDrift scope moduleKey lastGoodVersion lastGood
                            return lastGood
                        | Ok() ->
                            logger.Info
                                $"[ConfigMigration] event=config_migrated scope={scope.Container} moduleKey='{moduleKey}' from={atVersion} to={lastGoodVersion} steps={List.length steps}"

                            do! observeDrift scope moduleKey lastGoodVersion lastGood
                            return lastGood
    }

    /// Stamp a document being written, when — and only when — the
    /// module has declared a version above the implicit floor.
    ///
    /// The guard is what makes adoption free (GP 11): a deployment
    /// where no schema declares a version writes exactly the bytes it
    /// wrote before this substrate existed. Stamping unconditionally
    /// would rewrite every config document in every deployment on first
    /// save, for a stamp that means nothing until a version is
    /// declared.
    let stampForWrite (moduleKey: string) (values: Map<string, string>) : Map<string, string> =
        let targetVersion = registry.TargetVersion moduleKey

        if targetVersion > ConfigMigrationMetadata.InitialVersion then
            ConfigMigrationMetadata.stampVersion targetVersion values
        else
            values

    /// The wrapped store, for callers that must reach the undecorated
    /// instance (the erasure handler walks blobs directly).
    member _.Inner: IConfigStore = inner

    interface IConfigStore with
        member _.GetRaw(scope, moduleKey) = loadMigrated scope moduleKey

        member _.Get<'T>(scope, moduleKey) : Async<'T option> = async {
            let! values = loadMigrated scope moduleKey

            if Map.isEmpty values then
                return None
            else
                return ConfigStore.tryProjectExact<'T> support.Logger.Warn scope.Container moduleKey values
        }

        member _.GetEffective<'T>(scope, moduleKey, schema) : Async<'T> = async {
            let! values = loadMigrated scope moduleKey

            return
                ConfigStore.projectToRecord<'T>
                    support.Logger.Warn
                    (sprintf "scope=%s moduleKey=%s" scope.Container moduleKey)
                    schema
                    values
        }

        member _.Set<'T>(scope, moduleKey, value: 'T, schema) = async {
            match ConfigStore.toRawMap value with
            | Error msg -> return Error msg
            | Ok asMap -> return! inner.SetRaw(scope, moduleKey, stampForWrite moduleKey asMap, schema)
        }

        member _.SetRaw(scope, moduleKey, values, schema) =
            // Strip first: an admin UI round-trip of a document read
            // through this decorator carries no reserved key, but a
            // caller that obtained one elsewhere must not be able to
            // forge a version stamp.
            let clean = ConfigMigrationMetadata.stripReserved values
            inner.SetRaw(scope, moduleKey, stampForWrite moduleKey clean, schema)

        member _.Clear(scope, moduleKey) = inner.Clear(scope, moduleKey)

        member _.Erase(scopeId, subjectUserId, policy, dryRun) =
            inner.Erase(scopeId, subjectUserId, policy, dryRun)

/// Wrap `inner` with the Phase 10b migration decorator.
///
/// Applied unconditionally by `compose`: the reserved-key contract must
/// hold for every read and every write, or a deployment that declares
/// its first schema version finds half its documents unstamped. When
/// nothing declares a version the decorator is inert — `IsInert` — and
/// every path reduces to a strip of an empty reserved set plus a
/// delegation (GP 13).
let decorate (support: ConfigMigrationSupport) (inner: IConfigStore) : IConfigStore =
    MigratingConfigStore(inner, support) :> IConfigStore

/// Build the support record from a composed `ServerConfig`. The
/// registry reads schemas from `ModuleConfigs` (populated by
/// `ServerApp.addModule` from each module's `ConfigSchema`) and
/// migrators from `ConfigMigrations` (populated by
/// `ServerModule.withConfigMigration`).
///
/// **Declared registration only — there is deliberately no DI leg**,
/// unlike Phase 10a's `MigrationRegistry`, which unions
/// `sp.GetServices(typeof<IDataMigrator>)`. The config store is
/// constructed BEFORE `app.Build()` — the Phase 6f transactional
/// dispatcher needs the same instance pre-DI — so no service provider
/// exists at the moment this registry is built. Deferring the store's
/// construction to get one would trade a working invariant for a
/// registration convenience.
///
/// Registration defects are surfaced as warnings at compose time rather
/// than raised: an unusable chain is a real defect, but refusing to
/// start over it would take down a deployment whose config reads would
/// otherwise degrade to the pre-Phase-10b behaviour.
let support (config: ServerConfig) (eventStore: IEventStore option) (logger: ILogger) : ConfigMigrationSupport =
    let registry =
        ConfigMigrationRegistry(config.ModuleConfigs, config.ConfigMigrations)

    for error in registry.ValidateAll() do
        logger.Warn
            $"[ConfigMigration] event=chain_registration_defect {ConfigMigrationChainError.describe error} Reads of the affected module's config will fall back to schema defaults until the registration is corrected."

    {
        Registry = registry
        Drift = ConfigDriftTracker()
        EventStore = eventStore
        Logger = logger
    }
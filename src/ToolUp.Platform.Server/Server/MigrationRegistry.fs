// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open ToolUp.Platform.FileProcessor

// ─── Migration chain resolution (Phase 10a) ──────────────────────
//
// A module ships one migrator per version step. To upgrade an object
// stored at V_n up to the module's current V_m, the substrate has to
// find the ordered run of steps that connects them. That resolution
// is a pure function over the registered migrator set — no storage,
// no I/O — so every way it can go wrong is a *registration* defect
// discoverable before a single object is read.
//
// Two classes of defect, deliberately handled differently:
//
//   * **Structural** (a migrator that does not advance; two migrators
//     reading the same version) — the set itself is unusable at any
//     version. The runner refuses to start a pass and records
//     `MigrationChainBlocked`, because guessing which of two forward paths
//     the author meant is not a decision a framework gets to make.
//   * **Per-object** (no migrator reads the version THIS object sits
//     at) — the set may be perfectly good for every other object in
//     the scope. That is a per-object failure under the standard
//     policy: log, emit `MigrationFailed`, leave the source untouched,
//     carry on with the rest.

module MigrationChain =

    /// Validate the migrator set for one data type independently of any
    /// stored object. Catches the two structural defects above; says
    /// nothing about reachability from a particular version.
    let validateSet (dataTypeId: string) (migrators: IDataMigrator list) : Result<unit, MigrationChainError> =
        match migrators |> List.tryFind (fun m -> m.ToVersion <= m.FromVersion) with
        | Some bad -> Error(NonAdvancingMigrator(dataTypeId, bad.FromVersion, bad.ToVersion))
        | None ->
            let duplicated =
                migrators
                |> List.countBy _.FromVersion
                |> List.tryFind (fun (_, count) -> count > 1)

            match duplicated with
            | Some(version, count) -> Error(AmbiguousMigrationStep(dataTypeId, version, count))
            | None -> Ok()

    /// Resolve the ordered run of steps that carries an object from
    /// `fromVersion` up to exactly `targetVersion`.
    ///
    /// `Ok []` when the object is already at (or past) the target —
    /// the caller treats that as "already current", not as an error,
    /// because an object stamped ahead of the running code is what a
    /// rollback looks like and refusing to serve it would be worse
    /// than leaving it alone.
    ///
    /// A step that would carry the chain PAST the target is refused
    /// (`ChainOvershootsTarget`) rather than truncated: writing an
    /// object at a version the module does not claim to read is the
    /// mixed-state outcome this substrate exists to prevent.
    let resolve
        (dataTypeId: string)
        (migrators: IDataMigrator list)
        (fromVersion: SchemaVersion)
        (targetVersion: SchemaVersion)
        : Result<IDataMigrator list, MigrationChainError> =
        match validateSet dataTypeId migrators with
        | Error e -> Error e
        | Ok() ->
            if fromVersion >= targetVersion then
                Ok []
            else
                // Every migrator advances and no version is read by two
                // of them, so a chain visits each migrator at most once
                // — the fuel below can only run out on a set that
                // `validateSet` would already have rejected.
                let rec walk (current: SchemaVersion) (acc: IDataMigrator list) (fuel: int) =
                    if current = targetVersion then
                        Ok(List.rev acc)
                    elif current > targetVersion then
                        Error(ChainOvershootsTarget(dataTypeId, current, targetVersion))
                    elif fuel <= 0 then
                        Error(NoMigrationPath(dataTypeId, current))
                    else
                        match migrators |> List.filter (fun m -> m.FromVersion = current) with
                        | [] -> Error(NoMigrationPath(dataTypeId, current))
                        | [ step ] -> walk step.ToVersion (step :: acc) (fuel - 1)
                        | many -> Error(AmbiguousMigrationStep(dataTypeId, current, List.length many))

                walk fromVersion [] (List.length migrators + 1)

// ─── MigrationRegistry ───────────────────────────────────────────

/// The deployment's view of "which data types declare a schema
/// version, and what upgrades exist for them".
///
/// Migrators reach the registry from two places, unioned:
///   * `DataType.Migrations` — the module's own declaration at
///     registration, which is where a module author naturally puts
///     them.
///   * DI-registered `IDataMigrator` singletons — the same escape
///     hatch connector companions use for `IDataSource`, so a
///     migrator can ship in a package that does not own the
///     `DataType` registration.
///
/// Duplicate instances (the same migrator declared both ways) are
/// collapsed by reference, so wiring a migrator twice is harmless
/// rather than an `AmbiguousMigrationStep`.
type MigrationRegistry(dataTypes: DataType list, diMigrators: IDataMigrator list) =

    let byId =
        dataTypes |> List.map (fun dt -> dt.Id, dt) |> List.distinctBy fst |> Map.ofList

    let migratorsFor (dataTypeId: string) =
        let declared =
            match byId.TryFind dataTypeId with
            | Some dt -> dt.Migrations
            | None -> []

        let injected = diMigrators |> List.filter (fun m -> m.DataTypeId = dataTypeId)

        // Reference-distinct: the same instance declared on the
        // `DataType` AND registered in DI is one migrator, not two.
        declared @ injected |> List.distinct

    /// Every registered data type, in registration order.
    member _.DataTypes: DataType list = dataTypes

    /// Look up a registered data type by id.
    member _.TryFind(dataTypeId: string) : DataType option = byId.TryFind dataTypeId

    /// The version the owning module currently declares for this data
    /// type. `None` when the data type is not registered here.
    member _.TargetVersion(dataTypeId: string) : SchemaVersion option =
        byId.TryFind dataTypeId |> Option.map _.SchemaVersion

    /// Every migrator that applies to this data type, from both
    /// sources.
    member _.MigratorsFor(dataTypeId: string) : IDataMigrator list = migratorsFor dataTypeId

    /// Resolve the step run carrying an object at `fromVersion` up to
    /// the data type's declared current version.
    member _.ResolveChain
        (dataTypeId: string, fromVersion: SchemaVersion)
        : Result<IDataMigrator list, MigrationChainError> =
        match byId.TryFind dataTypeId with
        | None -> Error(NoMigrationPath(dataTypeId, fromVersion))
        | Some dt -> MigrationChain.resolve dataTypeId (migratorsFor dataTypeId) fromVersion dt.SchemaVersion

    /// Structural validity of one data type's migrator set — the
    /// version-independent half. Used by the runner as its
    /// start-of-pass gate and by the API's `ChainProblem` projection.
    member _.ValidateSet(dataTypeId: string) : Result<unit, MigrationChainError> =
        MigrationChain.validateSet dataTypeId (migratorsFor dataTypeId)

    /// Data types that could have stored objects needing an upgrade —
    /// those declaring a version above the floor. A data type still at
    /// version 1 has nothing below it, so the runner skips it without
    /// a single blob read (GP 13).
    member _.MigratableDataTypes: DataType list =
        dataTypes
        |> List.filter (fun dt -> dt.SchemaVersion > MigrationMetadata.InitialVersion)

    /// The admin-facing projection: declared version per data type,
    /// plus the reason its chain is unusable when it is. A chain is
    /// reported as problematic when the set is structurally invalid,
    /// or when an object at the floor version could not be carried to
    /// the declared current version — the two things an operator
    /// wants to know before pressing "migrate".
    member this.DescribeDataTypes() : MigrationDataTypeInfo list =
        dataTypes
        |> List.map (fun dt ->
            let problem =
                if dt.SchemaVersion <= MigrationMetadata.InitialVersion then
                    None
                else
                    match
                        MigrationChain.resolve
                            dt.Id
                            (migratorsFor dt.Id)
                            MigrationMetadata.InitialVersion
                            dt.SchemaVersion
                    with
                    | Ok _ -> None
                    | Error e -> Some(MigrationChainError.describe e)

            {
                DataTypeId = dt.Id
                DisplayName = dt.Info.DisplayName
                CurrentVersion = dt.SchemaVersion
                ChainProblem = problem
            })
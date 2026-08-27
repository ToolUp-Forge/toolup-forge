# Phase 10a — `DataType` gains `SchemaVersion` + `Migrations` (consumer migration)

**What changes.** `ToolUp.Platform.FileProcessor.DataType` gains two fields: `SchemaVersion: int` (the schema version the module currently reads and writes for this data type) and `Migrations: IDataMigrator list` (the forward migrators the module ships for it). Together with the new opt-in `ServerConfig.DataMigrations` substrate, an object stored under an older schema version is upgraded forward on startup or on an operator's trigger, rather than being handled by ever-growing backward-compatibility branches in every read path.

**BREAKING for anyone constructing a `DataType` record literal.** F# records have no field defaults, so every construction site must add the two fields. Nothing else about the type changed, and a module that has never evolved its persisted shape has nothing further to do.

**Runtime behaviour is unchanged until you opt in.** `ServerConfig.DataMigrations` defaults to `NoDataMigrations`, which registers no registry, no status store, no background sweep, and mounts no `IDataMigrationApi` route (GP 11 / GP 13). `ClientConfig.MigrationAdmin` defaults to `NoMigrationAdmin` for the same reason.

## Diff to apply — the one-line case

Every `DataType` literal:

```fsharp
let salesData: DataType = {
    Info = { Id = "SalesData"; DisplayName = "Sales Data"; Schema = None }
    Id = "SalesData"
    SchemaVersion = DataTypes.initialSchemaVersion   // ← add (= 1)
    Migrations = []                                  // ← add
    Detect = detectSales
    Process = processSales
}
```

An object stored without a version stamp reads as version 1, so a data type left at `initialSchemaVersion` behaves exactly as it did before this phase.

## Shipping a schema evolution

1. **Bump the declared version** on the `DataType` and write the migrator for the step:

```fsharp
type SalesDataV1ToV2() =
    interface IDataMigrator with
        member _.DataTypeId = "SalesData"
        member _.FromVersion = 1
        member _.ToVersion = 2
        member _.Migrate(payload: obj) = async {
            let v1 = deserialiseV1 (payload :?> byte[])
            let v2 = { Region = v1.Region; Revenue = v1.Revenue; Currency = "GBP" }
            return box (serialiseV2 v2)
        }

let salesData = { salesData with SchemaVersion = 2; Migrations = [ SalesDataV1ToV2() ] }
```

`Migrate` receives the stored content boxed as `byte[]` and must return a `byte[]` or a `string`; any other payload is refused and the object is left where it was. One migrator per version step — the registry chains them, and refuses a set with a gap, a fork (two migrators reading the same version), a non-advancing step, or a chain that overshoots the declared version.

A migrator shipping in a package that does not own the `DataType` registration can instead register itself in DI (`services.AddSingleton<IDataMigrator>(…)`), the same escape hatch `IDataSource` connectors use. The registry unions both sources and collapses an instance wired twice.

2. **Opt the deployment in:**

```fsharp
{ ServerConfig.defaults with
    DataMigrations = EnabledDataMigrations }   // or ManualDataMigrations
```

- `EnabledDataMigrations` runs a sweep at startup across every team `ITeamStore.ListTeams` returns, gated on the process-profile matrix (a `WebOnly` silo does not migrate; a `WorkerOnly` one does).
- `ManualDataMigrations` registers everything except the sweep, so a pass starts only when an Owner / Admin presses the button.

3. **Optionally surface the admin module:** `ClientConfig.MigrationAdmin = DefaultMigrationAdmin`. It shows the declared version and per-scope progress ("Migrating Sales Data to V2: 47/120 objects"), the manual trigger, and the failure log.

## What a pass guarantees

- **Idempotent and resumable.** Each upgraded object is stamped `_schemaVersion` in the same `Save` as its new content, and the stamp is the only authority on whether work remains. A pass killed halfway resumes exactly; a repeat pass upgrades nothing.
- **History preserved.** A `Versioned` object keeps its pre-migration version readable (Phase 7); the migration write is attributed to `_platform.migration`.
- **Failure is per object.** A migrator that raises leaves that object at its old version, logs, writes a `MigrationFailed` event into the team's scope, and the pass continues. Fix the migrator and run again — only the objects left behind have work to do.
- **Readers never see a mixed state.** A `Save` publishes content and stamp together, so a concurrent read returns either the old version or the new one.

## Verification

1. `dotnet build` — the compiler names every `DataType` literal that still needs the two fields.
2. With `DataMigrations` unset, the deployment's startup log and route table are unchanged.
3. Contract pack: `InProcess/DataMigrationTests.fs` in `ToolUp.Platform.Tests` — chain resolution, the registry union, the status-blob layout, payload coercion, and the runner's idempotency + failure policy.

## Rollback

Set `DataMigrations = NoDataMigrations` (or leave it unset). Objects already upgraded stay upgraded — migrations are forward-only by design; recovering a pre-migration payload is `IDataObjectStore.Recover` against the preserved version, or a snapshot restore. The two `DataType` fields remain required by the type.

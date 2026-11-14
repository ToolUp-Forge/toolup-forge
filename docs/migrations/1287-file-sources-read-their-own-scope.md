# Phase 1287 — a saved data source reads only its own scope, and hides its secrets from members who cannot edit it

**Who this is for:** deployments that save `Csv`, `Excel` or `Parquet` data sources through
`IDataIngestionApi.SaveDataSource`, admin UIs that read data sources, and code that calls a file connector's
`readSettings`. **Breaking:** a config that names its own `container` is refused, and so is one already at rest
that names a container its scope does not own.

## What changes

1. **A file connector reads only the container of the scope its call runs under.** `Csv`, `Excel` and
   `Parquet` admit `ConnectionScope["container"]` only when it is `team-{id}` or `user-{id}` for the call's
   `DataSourceCallContext.ScopeId` (`Files.scopeContainer`). `_platform`, any `_`-prefixed container and
   another scope's container are refused with `SchemaMismatch`, before anything is listed or read.
2. **`prefix` and `extension` cannot leave the container.** Either is refused when it holds `..` or starts
   with `/` or `\`, and `extension` may hold no separator at all. A leading separator on `prefix` used to be
   trimmed; it is now refused.
3. **`container` is the platform's key.** `SaveDataSource` refuses a config whose `ConnectionScope` carries
   `container` and records the saving scope's own container in its place. `ListDataSources` and
   `GetDataSource` never return the key. A declared delivery seeds the principal's container for a pulled
   file source as it already did for a watch, and refuses a declaration that sets it (`LocationInConfig`).
4. **A reader who fails the write gate reads secret-bearing keys redacted.** Each one's value is the literal
   `<redacted>` (`DataSourceConfigWire.RedactedValue`); the key stays. The keys are named per connector in
   `DataSourceConfigWire.secretKeys` (`Sql.connection_string`, `Snowflake.private_key_file`,
   `Redshift.secret_arn`), and any key whose name holds `password`, `pwd`, `secret`, `token`, `private_key`,
   `connection_string`, `api_key` or `apikey` is redacted too. Owners, Admins and every caller outside
   `Team` / `MultiTeam` mode read the config unredacted.
5. **Surface.** `Files.readSettings` takes the scope id
   (`defaultExtension -> scopeId -> scope`), and so do `CsvDataSource.readSettings`,
   `ExcelDataSource.readSettings` and `ParquetDataSource.readSettings` (`scopeId -> scope`).
   `Files.scopeContainer` and `DataSourceConfigWire` (`ToolUp.Platform.Core`) are new.

## Do this

Save a file source without a container:

```diff
 let salesSource = {
     Id = "sales-csv"
     Kind = CsvDataSource.Kind
-    ConnectionScope = Map.ofList [ "container", "team-acme"; "prefix", "exports" ]
+    ConnectionScope = Map.ofList [ "prefix", "exports" ]
     ...
 }
```

Drop `container` from a declared scheduled pull of a file source; the seeding writes the principal's:

```diff
-    Source = { csvSource with ConnectionScope = Map.ofList [ "container", "exports" ] }
+    Source = { csvSource with ConnectionScope = Map.empty }
```

Move files the source should read into the scope's own container (`StorageScope.Container`, or
`ScopeKey.container` of its key). A source that read another scope's container cannot keep doing so: that
is the defect the phase closes.

Pass the call's scope id where you called `readSettings` yourself:

```diff
-CsvDataSource.readSettings config.ConnectionScope
+CsvDataSource.readSettings ctx.ScopeId config.ConnectionScope
```

An admin UI that shows a source to members should render `<redacted>` as "hidden", and must not write a
redacted value back (only a writer can save, and a writer is never sent one).

## Verify

- Re-save each API-saved file source without `container`, trigger a refresh, and read its run: `Succeeded`.
- A config at rest naming another scope's container now records a failed run whose error names the container.
- As a team Member, `GetDataSource` of a `Sql` source shows `connection_string = "<redacted>"`; as an Owner,
  the saved value.

## Rollback

Pin the previous SDK version (core and the `data-sources` group). Configs saved by this version carry the
container of the scope that saved them, which the previous version reads as before, so nothing at rest needs
undoing.

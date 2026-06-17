# Mapping-aware Data Manager — CSV → DataType column mapping (consumer adoption)

**What changes.** A new **opt-in** Data Manager adds a front mapping stage so an
arbitrary CSV can be coerced into any registered, schema-bearing `DataType`. The
built-in `FileManagerUI` only ingests a CSV whose header row already matches a
registered schema (header-based `DataType.Detect`); a CSV with renamed or extra
columns falls through to `"UnrecognisedData"`. The mapping manager lets the user:

1. upload any CSV,
2. pick the target data format (a `DataType` that publishes a `DataTypeSchema`),
3. review **smart auto-suggested** field→column matches — with a warning to
   double-check and an explicit "review these" list of `LowConfidence` /
   `TypeMismatch` / `Ambiguous` / `Unmatched` guesses — and override as needed,
4. confirm; the CSV is rewritten into the schema's canonical header shape and
   ingested as the target type.

**Frictionless re-import.** On upload the manager first checks whether the file's
structure is already known: if one or more **saved mappings** exist for its
column-structure fingerprint they are applied automatically (no wizard); otherwise
the file is offered to the server's native `DataType.Detect` and, if recognised,
ingested as-is. The mapping wizard only opens when the structure is genuinely new.

**One file → several data objects.** A column-structure persists **multiple**
mappings (one per target type), so a wide CSV can spawn several data objects. After
any import the result card offers **"Make additional mapping"**, which re-opens the
wizard on the same in-hand file to add another target; each mapped object is
uploaded under a type-suffixed name (`data__SalesData.csv`) so they don't collide.

Saved mappings are persisted per storage scope, keyed by `(fingerprint,
targetType)` — `fingerprint` being the order-/case-independent header set — and
re-applied on the next upload of the same shape.

**Two opt-ins (both default off — `GP 11`/`GP 13`, zero cost when unused):**

```fsharp
// client (renders the wizard)
ClientConfig.DataManager       = MappingDataManager            // or ConfiguredMappingDataManager cfg
// server (persists the maps + mounts IColumnMappingApi)
ServerConfig.ColumnMapping     = EnabledColumnMapping          // env: TOOLUP_COLUMN_MAPPING=enabled
```

**New surface (all additive):** `ColumnMappingMode` (`SDK.Shared.fs`),
`ServerConfig.ColumnMapping`; `DataManagerMode` cases `MappingDataManager` /
`ConfiguredMappingDataManager`; the pure engine `ColumnMapping` + types
`ColumnMappingTypes` (Core, Fable-safe); `IColumnMappingApi` wire contract;
server `IColumnMappingStore` + default `ColumnMappingStore.create` (over
`IDataObjectStore`). `FileManagement.AddFile` now honours an explicit, registered
`DataFileUpload.dataType` before falling back to header detection.

## Diff to apply

**No consumer action required.** Every change is additive and defaults to the
prior behaviour:

- Existing deployments keep `DefaultDataManager` / `NoColumnMapping` — the
  default `FileManagerUI` path and `AddFile` detection are **byte-for-byte
  unchanged** (`FileManagerUI` uploads with `dataType = "UnrecognisedData"`,
  which never matches a registered type, so detection always runs).
- To adopt, set the two flags above and register at least one schema-bearing
  `DataType` (a module that publishes `DataTypeInfo.Schema = Some …`):

```fsharp
let config =
    { ServerConfig.defaults with ColumnMapping = EnabledColumnMapping }
// client
let clientConfig =
    { ClientConfig.defaults with DataManager = MappingDataManager }
```

A consumer that ships its **own** `IColumnMappingStore` implements the four
`scopeId`-first async methods (`Save` / `Get` / `List` / `Delete`); none exist in
the tree today, and the default `IDataObjectStore`-backed store is registered
automatically when `EnabledColumnMapping`.

## Verification steps

- `dotnet build ToolUp.Forge.sln` — clean (additive `ServerConfig` field +
  `DataManagerMode` cases compile against every existing composition).
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` —
  `ColumnMatcher` (name similarity, type inference, fingerprint order-independence,
  flag classification, CSV rewrite) and `IColumnMappingStore contract`
  (round-trip, overwrite, list, idempotent delete, scope isolation) pass.
- Fable gate: `cd samples/MinimalClient && dotnet fable -o output` — the engine
  and wizard transpile (no server-only API leaks into the client path).
- End-to-end: enable both flags in a sample registering a schema-bearing
  `DataType`; upload a CSV with renamed/extra columns; confirm the auto-map
  pre-fills, the review list flags dodgy matches, Confirm ingests it as the
  target type, and a second upload of the same column shape auto-applies the
  saved map.

## Rollback

Revert the commit. The two flags default off and the new types are unreferenced
by any existing consumer, so removal is safe. The only persisted artefact is the
`_columnmapping__{fingerprint}` sidecar in `IDataObjectStore`; it shares the
file's container and is inert when the feature is disabled — no data-shape
migration to undo.

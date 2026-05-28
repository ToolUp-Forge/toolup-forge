namespace ToolUp.Platform

// ─── IDataIngestor ────────────────────────────────────────────────
//
// Server-side orchestrator interface for the data-ingestion substrate
// (Phase 10). One implementation per process — the SDK ships a
// default `DataIngestor` that:
//
//   1. Reads `DataSourceConfig` by id from `IDataSourceConfigStore`.
//   2. Resolves the matching `IDataSource` by `Kind`.
//   3. Resolves credentials via `ISecretStore.GetSecret(scopeId,
//      config.CredentialKey)`.
//   4. Calls `IDataSource.Connect` then `Query`.
//   5. Writes the returned bytes through `IDataObjectStore.Save`
//      with `Versioned` policy — each refresh creates a new version,
//      preserving history (Phase 7's immutability guarantee).
//   6. Records an `IngestionRun` for the attempt.
//   7. Emits `IngestionRunCompleted` / `IngestionRunFailed` events
//      to `IEventStore` under `SourceModule = "_platform.dataingestion"`
//      so audit + webhook subscribers pick them up.

type IDataIngestor =
    /// Execute a single ingestion attempt. Resolves the config, the
    /// connector, and credentials internally; the caller supplies
    /// only the scope, source id, and table.
    ///
    /// Returns `Ok IngestionRun` describing the completed attempt
    /// (whether the underlying source call succeeded or failed —
    /// the run is always recorded, the `Status` and `Error` fields
    /// distinguish outcomes). Returns `Error IngestionError` only
    /// for setup failures that prevent the attempt from running at
    /// all (config missing, connector unregistered).
    abstract RunIngestion:
        scopeId: string * sourceId: DataSourceId * table: string -> Async<Result<IngestionRun, IngestionError>>

    /// Read the most recent N ingestion-run rows for a source.
    /// Newest first. Backed by the same blob layout `RunIngestion`
    /// writes through. Empty list when no runs exist or when the
    /// source is unknown.
    abstract GetRecentRuns: scopeId: string * sourceId: DataSourceId * count: int -> Async<IngestionRun list>
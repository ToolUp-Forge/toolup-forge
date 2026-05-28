namespace ToolUp.Platform

// ─── IDataSourceConfigStore ──────────────────────────────────────
//
// Persistence interface for `DataSourceConfig` records. Mirrors the
// shape of `IConfigStore` (Phase 5a) but specialised for data-source
// configs because (a) the read path is hit on every ingestion call
// (perf-sensitive) and (b) the wire shape is a typed record, not the
// generic `Map<string,string>` `IConfigStore` carries.
//
// Default implementation: blob-backed at
// `_platform/data-sources/{scopeId}/{sourceId}.json`. Same scope-
// prefix discipline as `IJobStore`, `IConfigStore`, `IWebhookRegistry`
// — cross-scope reads are structurally impossible because the store
// never widens the prefix during a scoped operation.

type IDataSourceConfigStore =
    /// Persist a config — overwrites if `Id` already exists.
    abstract Save: scopeId: string * config: DataSourceConfig -> Async<unit>

    /// Read one config by id. `None` for unknown ids.
    abstract Get: scopeId: string * sourceId: DataSourceId -> Async<DataSourceConfig option>

    /// List every config in scope. Order is implementation-defined
    /// (the blob-backed default returns by `Id` ascending).
    abstract List: scopeId: string -> Async<DataSourceConfig list>

    /// Delete a config. Idempotent — deleting a non-existent id is
    /// a no-op. Does NOT cascade-delete `IngestionRun` history;
    /// runs remain for audit.
    abstract Delete: scopeId: string * sourceId: DataSourceId -> Async<unit>
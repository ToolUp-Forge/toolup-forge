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
// ─── Phase 1015 — how a declared source's data arrives ───────────
//
// A data source a request saves arrives by upload: someone signed in
// triggers each refresh, under the scope that request resolved. A
// deployment can instead DECLARE a source in its composition together with
// how its data arrives — pulled on a cadence, or ingested as it lands at a
// watched location — and the principal that delivery runs as. A principal,
// never a scope: at startup the deployment's own scope resolver resolves it
// (`DeclaredPrincipalResolver`, Phase 1005), and the delivery's jobs are
// scheduled under that resolution through the typed `Schedule` and the
// platform's scope carrier (Phase 935). No string becomes a scope (797).
//
// The declaration is compose-time only. `DataSourceConfig` itself carries
// no delivery: a request saving a config cannot name a principal to run
// as, and the declared source's config is seeded at its principal's scope
// through this store like any other.

/// How a declared data source's data arrives (Phase 1015).
[<RequireQualifiedAccess>]
type DataSourceDelivery =
    /// Someone uploads it, or a request triggers each refresh — today's
    /// behaviour and the default. Nothing is scheduled; a source previously
    /// declared with another delivery has that delivery's jobs retired.
    | Upload
    /// The scheduler pulls every table in the source's `Tables` on `cadence`
    /// (5-field cron), as the principal named `principal`. Each run commits
    /// through the ordinary ingestion path under the principal's scope.
    | ScheduledPull of cadence: string * principal: string
    /// Each new object at `location` — a blob prefix in the principal's own
    /// scope container — is ingested once, as the principal named
    /// `principal`. File connectors only.
    | Watched of location: string * principal: string

/// Readings of a `DataSourceDelivery`.
[<RequireQualifiedAccess>]
module DataSourceDelivery =

    /// The principal a delivery runs as; `None` for `Upload`.
    let principal (delivery: DataSourceDelivery) : string option =
        match delivery with
        | DataSourceDelivery.Upload -> None
        | DataSourceDelivery.ScheduledPull(_, principal)
        | DataSourceDelivery.Watched(_, principal) -> Some principal

    /// The delivery's name as it appears on its jobs and in refusals.
    let token (delivery: DataSourceDelivery) : string =
        match delivery with
        | DataSourceDelivery.Upload -> "upload"
        | DataSourceDelivery.ScheduledPull _ -> "scheduledPull"
        | DataSourceDelivery.Watched _ -> "watched"

/// A data source a deployment declares in its composition, with how its
/// data arrives (Phase 1015).
type DeclaredDataSource = {
    /// The source. Its `Id` is the declaration's stable identity: a
    /// re-deploy with the same id updates the config it seeded.
    Source: DataSourceConfig
    /// How its data arrives.
    Delivery: DataSourceDelivery
}

/// A deployment's declared data sources and the principals their
/// deliveries run as (Phase 1015).
type DeclaredDataSources = {
    /// The principals a delivery may run as.
    Principals: DeclaredPrincipal list
    /// The sources. An empty list retires every source declared before.
    Sources: DeclaredDataSource list
}

/// Where declared sources were seeded, and which watched objects have been
/// ingested (Phase 1015). The blob-backed default is
/// `DataSourceConfigStore.declaredDeliveryLedger`.
type IDeclaredDeliveryLedger =
    /// The declared sources seeded, as (source id, scope id) pairs. Empty
    /// when nothing was ever seeded; `Error` when the ledger is unreadable.
    abstract ReadSeeded: unit -> Async<Result<Set<DataSourceId * string>, string>>

    /// Replace the seeded set.
    abstract WriteSeeded: seeded: Set<DataSourceId * string> -> Async<Result<unit, string>>

    /// Whether the watched object with `identity` was ingested for the
    /// source in scope.
    abstract WasIngested: scopeId: string * sourceId: DataSourceId * identity: string -> Async<Result<bool, string>>

    /// Record the watched object with `identity` as ingested for the source
    /// in scope. Idempotent.
    abstract RecordIngested: scopeId: string * sourceId: DataSourceId * identity: string -> Async<Result<unit, string>>
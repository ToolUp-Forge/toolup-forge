namespace ToolUp.Platform

open DataManagementTypes

// ─── IDataCatalog ────────────────────────────────────────────────
//
// Server-side query surface for the data types a deployment supports
// (Phase 7a). Aggregates the `(moduleName, DataType) list` known at
// composition time and (for `ListObjects`) delegates into
// `IDataObjectStore` for content-side enumeration.
//
// **Stateless contract.** The catalog snapshots its module-to-type
// mapping at construction; the set of registered modules is fixed
// for the server's lifetime, so the snapshot is by-value
// equivalent to repeated lookups (Phase 9c Rule 4).
//
// **Async at every boundary.** Even the in-process implementation
// returns `Async<_>` from every method so a distributed
// implementation (Orleans grain, Akka actor) can drop in without
// changing call sites (Phase 9c Rule 2).
//
// **No live handles, no callbacks.** Identity is by value: type ids
// are `DataTypeId` (= `string`), scope ids are `string`,
// `DataObject` is a record (Phase 9c Rules 1 + 3).

/// Server-side query interface for the data-type catalog. Registered
/// in DI by `SDK.Server.compose`; consumed by `PlatformApi`'s
/// `GetDataCatalog` handler and (in future) AI tools that need to
/// enumerate available data shapes.
type IDataCatalog =
    /// Every registered data type, in declaration order. The same
    /// `Id` appearing in two modules is preserved as a single entry
    /// (the catalog deduplicates on `Id`); `GetProducers` returns
    /// both module names.
    abstract ListTypes: unit -> Async<DataTypeInfo list>

    /// Schema for a registered type, when one was published by its
    /// producer module. `None` for non-tabular types or types whose
    /// module hasn't documented columns yet.
    abstract GetSchema: typeId: DataTypeId -> Async<DataTypeSchema option>

    /// Module names that declared a `DataType` with this `Id`.
    /// Empty when the type isn't registered. Multiple entries are
    /// possible when modules share a cross-domain data shape.
    abstract GetProducers: typeId: DataTypeId -> Async<string list>

    /// Latest version of every stored data object whose `DataType`
    /// matches `typeId`, scoped to the caller's storage scope.
    /// Built on top of `IDataObjectStore.ListObjects` — empty when
    /// no objects exist or the type id is unknown.
    abstract ListObjects: scopeId: string * typeId: DataTypeId -> Async<DataObject list>
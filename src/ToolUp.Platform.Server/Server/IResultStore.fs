namespace ToolUp.Platform

open System

// ─── IResultStore ────────────────────────────────────────────────
//
// Server-side persistence surface for analytical-module outputs
// (Phase 8). Higher-level than `IDataObjectStore` — callers pass
// `(moduleName, resultType)` instead of cooking up an ObjectId, and
// the store enforces `StrictlyVersioned` policy + emits
// `AnalysisCompleted` events automatically.
//
// **Optional registration.** `compose` only registers `IResultStore`
// when `ServerConfig.ResultStore` is `InMemoryResultStore` or
// `PersistentResultStore`. The default `NoResultStore` leaves DI
// unaware — modules that resolve `IResultStore` via
// `ctx.RequestServices.GetService` receive `null` and must handle
// absence (typically by skipping persistence and continuing with
// in-memory state).
//
// **Phase 9c portability.** Identity by value (string `objectId`,
// int `version`, byte[] content). Async at every method. No
// callbacks. Within-shard ordering: versions are linear within a
// single `(scopeId, objectId)` — concurrent saves to the same
// `(moduleName, resultType)` resolve via the underlying
// `IDataObjectStore` retry. Cross-result ordering is not promised.

/// `ObjectId` convention used by `IResultStore` implementations.
/// Singleton per `(moduleName, resultType)` — the same module
/// repeatedly saving the same result type produces a single
/// versioned object whose history captures the audit trail.
/// Modules wanting multiple distinct results of the same type use
/// different `resultType` values (e.g. `"q1-sales"`, `"q2-sales"`).
///
/// The separator is `__` (double underscore) rather than `/`
/// because the underlying `IDataObjectStore` blob layout
/// (`{container}/objects/{objectId}/v{N}.json`) treats slashes as
/// path segments — a slash inside `objectId` would leak into the
/// blob path and break `parseObjectId`. `__` is alphanumeric-safe
/// across every blob backend and unambiguous in practice (module
/// names and result types are typically simple identifiers).
module ResultObjectId =
    [<Literal>]
    let private Separator = "__"

    let make (moduleName: string) (resultType: string) = moduleName + Separator + resultType

    let modulePrefix (moduleName: string) = moduleName + Separator

/// Server-side interface for saving and querying analytical results.
/// Registered in DI only when `ServerConfig.ResultStore` is
/// non-`NoResultStore`.
type IResultStore =
    /// Save a new version of the result identified by
    /// `(moduleName, resultType)`. The store derives the underlying
    /// `ObjectId` via `ResultObjectId.make` and calls into
    /// `IDataObjectStore.Save` with `StrictlyVersioned` policy
    /// (persistent variant) or stages the bytes in memory
    /// (in-memory variant). Emits `AnalysisCompleted` to
    /// `IEventStore` on success; emits one `LineageLink` per
    /// `inputs` entry when an `ILineageStore` is registered.
    abstract SaveResult:
        scopeId: string *
        moduleName: string *
        resultType: string *
        content: byte[] *
        createdBy: string *
        inputs: string list ->
            Async<Result<DataObject, DataObjectError>>

    /// Latest version of the result identified by
    /// `(moduleName, resultType)`. `Error NotFound` when no save has
    /// happened in this scope.
    abstract GetLatest:
        scopeId: string * moduleName: string * resultType: string -> Async<Result<DataObject * byte[], DataObjectError>>

    /// Every version's metadata for results produced by `moduleName`
    /// in this scope. Optional `dateRange` filters by
    /// `DataObject.CreatedAt`. Sorted by `CreatedAt` descending so
    /// admin UIs show newest-first by default.
    abstract ListResults:
        scopeId: string * moduleName: string * dateRange: (DateTime * DateTime) option -> Async<DataObject list>

    /// Diff two versions of a single result object (identified by
    /// the underlying `ObjectId`). MVP supports JSON content only —
    /// non-JSON returns `Error (StorageFailure "diff requires JSON content")`.
    /// `Changes = []` means the two versions are byte-for-byte
    /// identical at the field level (some content was re-saved
    /// without modification — possible but rare).
    abstract CompareVersions:
        scopeId: string * objectId: string * v1: int * v2: int -> Async<Result<ResultDiff, DataObjectError>>
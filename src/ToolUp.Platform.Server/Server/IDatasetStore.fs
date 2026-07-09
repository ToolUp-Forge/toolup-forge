// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 448 — IDatasetStore ──────────────────────────────────────────
//
// Server-side seam for **immutable, versioned, rectangular typed datasets**.
// A dataset version ("vintage") is the addressable unit downstream work pins
// to. The store rides `IDataObjectStore` for content storage — so blob dedup
// (identical bytes across versions share one content blob) and linear
// per-`(scope, dataset)` versioning are **inherited**, not reimplemented.
//
// **The wire encoding is a pluggable seam, not a hard Parquet dependency
// (GP 1).** The plan fixes Parquet as the production wire format for compute
// handoff (D7), but a Parquet SDK is a heavyweight vendor dependency that may
// not sit in `ToolUp.Platform.*` core (GP 1 — vendor deps live in
// companions). So dataset content is encoded through `IDatasetCodec`: the
// default composition ships the BCL-only `JsonFrameDatasetCodec`
// (`Format = "toolup-frame-v1"`, zero vendor dependency), and a Parquet
// companion registers a `ParquetDatasetCodec` (`Format = "parquet"`) when a
// deployment wants native-Parquet handoff to Python / R fit workers. The
// dataset store, its contract pack, and every consumer are codec-agnostic;
// swapping the codec swaps the wire format with no interface change. This is
// the same shape the SDK uses everywhere (`IBlobStorage` + cloud companions,
// `ITimeSeriesStore` + Timescale) and it satisfies the plan's design-risk
// mitigation (risk #1): a second codec is the cheap "second implementation"
// that proves the seam.
//
// **Six portability rules (GP 12).**
// 1. *Identity by value* — `scopeId` / `datasetId` are strings, versions are
//    ints, schema + rows + pages are records/DUs; a `DatasetContentRef` is a
//    value (scope + id + version + hash + format), not a live blob handle.
// 2. *Async at every boundary* — every method returns `Async<_>`.
// 3. *Behaviour as data* — a page's filters + ordering are the
//    `DatasetPageQuery` record (op + literal), never a caller callback;
//    failures are `DatasetError` data.
// 4. *Stateless between calls* — every call carries its full scope + id +
//    version; the store caches nothing across calls (it derives everything
//    from `IDataObjectStore`, which is itself restart-survivable).
// 5. *No cross-shard ordering* — versions are linear only within one
//    `(scopeId, datasetId)` shard; no ordering promised across datasets or
//    scopes. Row order within a version is the stored insertion order
//    (stable → paging is deterministic), never promised across versions.
// 6. *Precision at the lower bound* — timestamps are `DateTimeOffset`; a
//    codec coarser than its inputs documents that in its companion README.
//    `Int` cells are `int64`; a narrower backend documents any narrowing.

/// Pluggable dataset wire-format codec (GP 1 — keeps the vendor Parquet
/// dependency in a companion). Encodes a schema + rows to opaque content
/// bytes and back. A codec is stateless and pure with respect to its inputs
/// (rule 4). Implementations: `JsonFrameDatasetCodec` (BCL default),
/// `ParquetDatasetCodec` (companion).
type IDatasetCodec =
    /// Stable format id written into each version's metadata + surfaced on a
    /// `DatasetContentRef` (`"toolup-frame-v1"`, `"parquet"`, …). A reader
    /// compares it before attempting `Decode`.
    abstract Format: string

    /// Encode `schema` + `rows` to self-describing content bytes. The bytes
    /// carry enough to reconstruct both schema and rows on `Decode`.
    abstract Encode: schema: DatasetSchema * rows: DatasetRow list -> byte[]

    /// Decode content bytes back to `(schema, rows)`. `Error reason` when the
    /// bytes are malformed for this codec (the store lifts it to
    /// `DatasetError.StorageFailure`). A codec returns `Error` — never throws
    /// — for content it recognises as its own format but cannot parse.
    abstract Decode: content: byte[] -> Result<DatasetSchema * DatasetRow list, string>

/// A value-typed reference to a dataset version's content blob, for handing a
/// vintage to an external compute worker (the plan's `GetParquetRef`, made
/// **format-honest**: `Format` names the actual encoding rather than
/// asserting Parquet unconditionally, so a worker inspects the tag and a
/// non-Parquet default composition can never silently ship the wrong bytes to
/// a Parquet-expecting kernel). When a Parquet codec is composed,
/// `Format = "parquet"` and the blob is native Parquet — read by any
/// Python / R kernel directly.
type DatasetContentRef = {
    ScopeId: string
    DatasetId: string
    Version: int
    ContentHash: string
    Format: string
    RowCount: int64
}

/// Immutable, versioned, scope-isolated store of rectangular typed datasets.
/// Every method is team-scoped by `scopeId` (GP 4 — the scope is a structural
/// partition inherited from `IDataObjectStore`, not a post-filter). A new
/// vintage is a new version, never a mutation (GP 5); the versioning policy is
/// chosen at `Create` and honoured by `Delete`.
type IDatasetStore =
    /// Create a new dataset version from a schema + in-memory rows. The rows
    /// are validated against the schema (`SchemaMismatch` on any bad cell),
    /// encoded through the composed codec, and stored via `IDataObjectStore`
    /// under `policy`. Returns the new `DatasetVersion` (with its assigned
    /// `Version`, `ContentHash`, `Format`). A second `Create` for the same
    /// `datasetId` under `Versioned` / `StrictlyVersioned` yields a new
    /// version with prior versions intact.
    abstract Create:
        scopeId: string *
        datasetId: string *
        schema: DatasetSchema *
        rows: DatasetRow list *
        createdBy: string *
        metadata: Map<string, string> *
        policy: VersioningPolicy ->
            Async<Result<DatasetVersion, DatasetError>>

    /// Create a new dataset version from **pre-encoded content bytes** in a
    /// named `format` (e.g. a Parquet file produced upstream). The store does
    /// not re-encode; it records `schema` + `format` as metadata and stores
    /// the bytes verbatim. `ReadPage` on the result succeeds only when the
    /// composed codec's `Format` matches `format` (else `UnreadableFormat`);
    /// `GetContentRef` always works (handoff needs no decode).
    abstract CreateFromContent:
        scopeId: string *
        datasetId: string *
        schema: DatasetSchema *
        content: byte[] *
        format: string *
        rowCount: int64 *
        createdBy: string *
        metadata: Map<string, string> *
        policy: VersioningPolicy ->
            Async<Result<DatasetVersion, DatasetError>>

    /// Fetch a specific version's metadata (schema, row count, format, hash).
    /// Does not download the content blob. `VersionNotFound` /  `NotFound`
    /// per `IDataObjectStore`.
    abstract GetVersion:
        scopeId: string * datasetId: string * version: int -> Async<Result<DatasetVersion, DatasetError>>

    /// Fetch the latest version's metadata. `NotFound` when the dataset has
    /// no versions in the scope.
    abstract GetLatest: scopeId: string * datasetId: string -> Async<Result<DatasetVersion, DatasetError>>

    /// All versions' metadata for a dataset, ascending by `Version`. Empty
    /// list when the dataset does not exist.
    abstract ListVersions: scopeId: string * datasetId: string -> Async<DatasetVersion list>

    /// Read a paged, filtered window of typed rows from one version. Filters
    /// apply first (AND-combined), then `Offset` / `Limit` page over the
    /// matching rows in stored insertion order (deterministic). Requires the
    /// composed codec to read the version's `Format` (`UnreadableFormat`
    /// otherwise).
    abstract ReadPage:
        scopeId: string * datasetId: string * version: int * query: DatasetPageQuery ->
            Async<Result<DatasetPage, DatasetError>>

    /// A content reference for compute handoff — the blob coordinates +
    /// format tag for a worker to fetch the raw bytes (via `IDataObjectStore`
    /// / `IBlobStorage`) without the store materialising them. No decode, so
    /// it works regardless of the composed codec.
    abstract GetContentRef:
        scopeId: string * datasetId: string * version: int -> Async<Result<DatasetContentRef, DatasetError>>

    /// Delete all versions of a dataset, **honouring the versioning policy**:
    /// a `StrictlyVersioned` dataset (immutable vintages) returns
    /// `DeleteForbidden`; `Versioned` / `Unversioned` delete. Idempotent for
    /// a `Versioned` / `Unversioned` dataset that does not exist (`Ok ()`).
    abstract Delete: scopeId: string * datasetId: string -> Async<Result<unit, DatasetError>>
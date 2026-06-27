// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module DataManagementTypes

/// String identifier for a data type — each module defines its own constants
type DataTypeId = string

/// Coarse type classification for a column in a `DataTypeSchema`. Kept
/// deliberately small — modules that need richer typing (currency vs
/// percentage, integer vs float, time vs date) describe the nuance in
/// the column's `Description`. Adding cases later is non-breaking;
/// removing one is.
type ColumnType =
    | StringColumn
    | NumberColumn
    | DateColumn
    | BooleanColumn

/// Description of a single column in a tabular data type's schema.
/// `Required` means the column must be present in the source file
/// (header match); empty cells in a required column are still allowed
/// at the schema level — modules enforce non-empty if they need it.
type DataTypeColumn = {
    Name: string
    Type: ColumnType
    Required: bool
    Description: string option
}

/// Optional schema published by a module alongside its `DataTypeInfo`.
/// Surfaces in `IDataCatalog` so admin UIs and AI tools can discover
/// what columns a CSV upload of this type is expected to contain.
/// Modules that handle non-tabular data leave `Schema = None` on
/// `DataTypeInfo`.
type DataTypeSchema = {
    Description: string
    Columns: DataTypeColumn list
}

/// Shared metadata for a data type — declared once per module in SharedTypes,
/// referenced by both server-side DataType and client-side DataTypeDisplay.
type DataTypeInfo = {
    /// Unique identifier (e.g. "SalesData", "OptimisationData")
    Id: DataTypeId
    /// Human-readable name shown in the file manager UI (e.g. "Sales Data")
    DisplayName: string
    /// Optional schema description. `None` for non-tabular
    /// types or modules that haven't yet documented their columns.
    Schema: DataTypeSchema option
}

type DataFileUpload = {
    filename: string
    contents: string
    dataType: DataTypeId
}

/// Metadata about an uploaded file stored in the session
type UploadedFileInfo = {
    FileName: string
    DataType: DataTypeId
    SizeBytes: int64
    RowCount: int
    UploadedAt: System.DateTime
}

/// Request to upload a file to the session
type FileUploadRequest = { File: DataFileUpload }

/// Request to retrieve file content by name
type FileContentRequest = string

/// Response with file content
type FileContentResult = Result<DataFileUpload, string>

// ─── Data catalog wire types ─────────────────────────────────────

/// One entry in the platform's data-type catalog. Pairs a type's
/// declared `DataTypeInfo` (which now carries an optional `Schema`)
/// with the list of module names that registered it. A type can have
/// multiple producers when several modules declare the same `Id`
/// (e.g. shared cross-module data shapes).
type DataTypeCatalogEntry = {
    Info: DataTypeInfo
    Producers: string list
}

/// `PlatformApi.GetDataCatalog` response — every data type the
/// running platform supports, with schema (when published) and
/// producer-module names. Surfaces in admin UIs and AI-tool
/// discovery.
type DataCatalogResponse = { Types: DataTypeCatalogEntry list }

// ─── Ingestion-status surfacing (Phase 173) ──────────────────────

/// Per-file RAG ingestion status surfaced on the Data Manager file
/// lists (the built-in `FileManagerUI` and the mapping-aware
/// `MappingDataManagerUI`). Mirrors Knowledge Base's per-document
/// status badge so a Data Manager upload that the post-save
/// vectorisation hook skipped / rejected / dropped shows *why* it
/// isn't searchable, instead of looking indexed.
///
/// Identity-by-value, Fable-safe (no framework types) — round-trips
/// over the wire via `FableConverters` (server) / Fable.SimpleJson
/// (client) exactly like KB's `IngestionStatus` DU. A deployment that
/// composes no `VectorisationHandler`s never writes any of these, so
/// the file list renders with no status column (GP 13).
type FileIngestionStatus =
    /// No ingestion was attempted for this file — either no RAG is
    /// composed, or the file predates the status store. Rendered as
    /// "no badge" by the client (absence ⇒ unchanged layout).
    | NotIngested
    /// The document's chunks are enqueued / in flight; not yet fully
    /// searchable.
    | Pending
    /// Every chunk of the document has been indexed — fully searchable.
    | Indexed
    /// Ingestion will not / did not complete; `reason` carries the
    /// human-facing cause (e.g. "no handler for type X", "document too
    /// large", "ingestion queue full", or an `OnChunkFailed` error).
    | Failed of reason: string

/// Wire-format key for the per-file Data-Manager ingestion-status
/// `CustomNotification`, mirroring KB's `"KnowledgeBase.IngestionStatus"`.
/// Published by the SDK on each terminal transition (`Indexed` /
/// `Failed`) so the Data Manager badge updates live without a manual
/// refresh; the client subscribes to it via `NotificationClient`.
[<Literal>]
let DataManagerIngestionStatusKey = "DataManager.IngestionStatus"

/// Payload carried by the `DataManagerIngestionStatusKey`
/// `CustomNotification` — the file whose status changed and its new
/// terminal status. Serialised to JSON on the server and parsed on the
/// client to patch that file's badge in place.
type DataManagerIngestionUpdate = {
    FileName: string
    Status: FileIngestionStatus
}
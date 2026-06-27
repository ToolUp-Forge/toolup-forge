// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ProcessedDataTypes

open System
open DataManagementTypes
// Auth/audit attributes (`AllowAnonymous`, `Audit`) — this file is a
// top-level module, not inside `namespace ToolUp.Platform`, so the
// attribute namespace must be opened explicitly.
open ToolUp.Platform

/// Server-side processed payload for a data file. `TypeName` is a string
/// identifier — typically the fully-qualified F# type name — that routing
/// code can use to select a deserialiser. `Payload` is the JSON-serialised
/// form of the original module-specific result.
///
/// Replaces the earlier `obj`-based return of `IFileProcessor.DataType.Process`.
/// The explicit type tag and string payload remove silent type erasure at
/// any future network boundary (distributed processing, cross-process
/// analytics, external indexing), while staying lightweight for the
/// in-process case today. Consumers deserialise the payload using a
/// serialiser that matches what the producer used (System.Text.Json by
/// default; see module implementations for their choice).
type ProcessedData = { TypeName: string; Payload: string }

/// A processed file entry combining status with a type-erased summary.
/// Modules provide their own summary types via the Info field.
type ProcessedFileEntry = {
    FileName: string
    DataType: DataTypeId
    ProcessedAt: DateTime
    Info: obj option
    Error: string option
}

/// Minimal projection over `ProcessedFileEntry` for `Forms.dataSourcePicker`.
/// A separate type rather than reusing `ProcessedFileEntry` so the picker
/// stays decoupled from the heavier processed-data shape, and so the public
/// SDK surface is a named record — anonymous records don't unify across
/// assembly boundaries, a hard requirement once SDK Client lands as its own
/// DLL.
type DataSourceOption = { FileName: string }

/// Combined response from file upload including processing result
type FileUploadResponse = {
    FileInfo: UploadedFileInfo
    Processed: ProcessedFileEntry
}

type FileUploadResult = Result<FileUploadResponse, string>

/// Snapshot returned by `IFileManagementApi.ListFiles`. Combines the raw
/// uploaded-file metadata and the derived `ProcessedFileEntry` summaries
/// so the client hydrates both halves of its state (file list +
/// `ProcessedDataContext` payload) in one round trip on page load.
///
/// `Files` and `Processed` are both keyed by `FileName` (case-insensitive
/// on the server side via `StringComparer.OrdinalIgnoreCase`); a file is
/// always present in `Files`, but `Processed` may carry an `Error` and
/// no `Info` if the original `DataType.Process` call failed or its
/// `DataType` is no longer registered.
/// `Ingestion` (Phase 173) joins per-file RAG ingestion status
/// (fileName → status) onto the same round trip so the Data Manager
/// renders a status badge without a second read. Empty (`[]`) when no
/// RAG / ingestion-status store is composed — the client then renders
/// no status column, so a non-RAG deployment is byte-for-byte
/// unchanged (GP 13).
type FileListSnapshot = {
    Files: UploadedFileInfo list
    Processed: ProcessedFileEntry list
    Ingestion: (string * FileIngestionStatus) list
}

/// File Management API contract. Data-path methods are dispatcher-
/// anonymous by design: Anonymous-mode deployments upload/read/delete
/// within their session scope, and `StorageScope` isolation is the
/// enforcement.
type FileManagementApi = {
    // Phase 69g.tail — file upload carries bytes + optional DataType
    // processing (parse / extract). Conservative per-subject cap;
    // dormant until an `IRateLimitStore` is composed.
    [<AllowAnonymous>]
    [<RateLimit(30, RateLimitSeconds.perMinute)>]
    UploadFile: FileUploadRequest -> Async<FileUploadResult>
    [<AllowAnonymous>]
    ListFiles: unit -> Async<FileListSnapshot>
    [<AllowAnonymous>]
    GetFileContent: FileContentRequest -> Async<FileContentResult>
    [<AllowAnonymous>]
    DeleteFile: string -> Async<Result<unit, string>>
    /// Re-run `DataType.Process` on a previously-uploaded file's
    /// persisted bytes, overwrite the persisted `ProcessedFileEntry`
    /// sidecar, and return the new entry. The user-triggered escape
    /// hatch when an entry surfaces with an error after a server
    /// restart (e.g. detector logic changed, module renamed) — the
    /// alternative without this surface is delete + re-upload.
    /// Post-save hooks are NOT fired on reprocess; the raw bytes are
    /// unchanged, so RAG / vectorisation indexing is not invalidated.
    [<AllowAnonymous>]
    ReprocessFile: string -> Async<Result<ProcessedFileEntry, string>>
    /// Phase 220 — re-run the RAG vectorisation path for a single
    /// previously-uploaded file whose ingestion `Failed`, re-firing the
    /// post-save hooks against its persisted bytes so the ingestion-status
    /// store transitions back through `Pending` to its terminal state.
    /// Idempotent against a still-in-flight retry (a `Pending` file is a
    /// no-op `Ok`). `Error` when the file is absent or no vectorisation is
    /// composed (no RAG ⇒ nothing to re-ingest). Scope-isolated like every
    /// other method here — a caller can only retry files in its own scope.
    [<AllowAnonymous>]
    RetryIngestion: string -> Async<Result<unit, string>>
    /// Owner / Admin escape hatch — wipe every uploaded file plus its
    /// `_processed_entry__` sidecar in the caller's storage scope.
    /// Returns the count of files removed (zero on an empty store is
    /// still `Ok 0`). Server gates on `TeamRoles.canWriteTeamConfig`
    /// in `Team` / `MultiTeam` mode; single-user modes (`Anonymous`,
    /// `AuthenticatedEphemeral`, `Individual`) are ungated since the
    /// caller IS the data owner. Emits a single `DataStoreReset`
    /// audit event — not one `FileDeleted` per file. Dispatcher-
    /// anonymous because single-user modes are ungated by design;
    /// the handler's `canWriteTeamConfig` gate covers team modes.
    [<AllowAnonymous>]
    [<Audit "Custom:DataStoreReset">]
    ResetDataStore: unit -> Async<Result<int, string>>
}
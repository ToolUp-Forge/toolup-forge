// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ProcessedDataTypes

// Phase 817 — `ProcessedFileEntry.Info` is deprecated below, and the
// builders beside it are the one sanctioned place that still names it.
#nowarn "44"

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

/// A processed file entry combining status with the module's summary of
/// the file — what the Data Manager renders under the data type's heading.
///
/// **Phase 817 — the summary is a `ProcessedData` envelope, not an `obj`.**
/// `Info` carried the module's summary record BOXED: the module's
/// `DataType.Process` boxed it, the module's `RenderSummary` unboxed it,
/// and the SDK moved it opaquely between the two — the type-erasure
/// boundary the `CLAUDE.md` list numbers 2. An `obj` is an open point in
/// the type graph: no decoder the SDK can generate or prove can express
/// a type only the module knows, and the MsgPack reader has no `obj`
/// arm, so what a boxed record decoded to was whichever host's reflection
/// fallback ran. `Summary` closes the point at the wire with the envelope
/// [Phase 1c] already introduced beside it — the summary's type name and
/// its JSON — so the entry is two strings on every host, and the module
/// decodes its own summary with the type it knows.
///
/// Construct through `ProcessedFileEntry.summarised` / `.failed` rather
/// than a record literal: a literal must name `Info`, which warns now and
/// stops compiling when the field is removed in 1.0; the
/// builders do neither.
type ProcessedFileEntry = {
    FileName: string
    DataType: DataTypeId
    ProcessedAt: DateTime
    /// The module's summary, boxed. Filled by modules that predate Phase
    /// 817; the SDK still renders it through the module's `RenderSummary`.
    [<Obsolete("Type-erased summary — use ProcessedFileEntry.Summary (a ProcessedData envelope, built by ProcessedFileEntry.summarised) and DataTypeDisplay.typed instead. See docs/migrations/817-processed-file-entry-typed-summary.md. Info is removed in 1.0.")>]
    Info: obj option
    Error: string option
    /// Phase 817 — the module's summary as a typed envelope: the summary
    /// type's name and its JSON. `None` on a failed entry and on an entry
    /// a pre-817 module produced. Appended at the END of the record so the
    /// positional wire keeps every earlier field where it was.
    Summary: ProcessedData option
}

/// Builders for `ProcessedFileEntry` (Phase 817). Each names the fields a
/// caller has, and none names `Info` — so a module built on them neither
/// warns today nor breaks when `Info` goes.
[<RequireQualifiedAccess>]
module ProcessedFileEntry =

    /// A successfully processed file with its summary envelope. The
    /// envelope is built by the tier that has a JSON codec —
    /// `ProcessedDataCodec.encode` on the server — because this shared
    /// tier deliberately carries none (the `ModuleQueryCodec` precedent).
    let summarised (fileName: string) (dataType: DataTypeId) (processedAt: DateTime) (summary: ProcessedData) = {
        FileName = fileName
        DataType = dataType
        ProcessedAt = processedAt
        Info = None
        Error = None
        Summary = Some summary
    }

    /// A file that did not process: the error, and no summary.
    let failed (fileName: string) (dataType: DataTypeId) (processedAt: DateTime) (error: string) = {
        FileName = fileName
        DataType = dataType
        ProcessedAt = processedAt
        Info = None
        Error = Some error
        Summary = None
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

/// Identity + size of the server-side `SessionFileStore` backing the
/// caller's resolved `StorageScope`, returned by
/// `FileManagementApi.GetSessionInfo` (Phase 6p).
///
/// `Epoch` is minted when the store instance is CONSTRUCTED, so it
/// changes on every event that silently empties the server's view of
/// the caller's uploads: a process restart (the whole in-memory
/// dictionary is gone), the ephemeral-store TTL eviction
/// (`ServerConfig.EphemeralStoreEvictionMinutes`), or a scope-container
/// change. A client that cached an epoch and now reads a different one
/// knows its local file list is stale WITHOUT having to wait for a
/// data-consuming call to fail with "File 'X' not found in session".
///
/// `FileCount` is the number of files the store currently holds — the
/// cheap corroborating signal for a client that wants to render "0 files"
/// immediately rather than re-running `ListFiles`.
type SessionStoreInfo = { Epoch: Guid; FileCount: int }

/// Wire-format key for the session-store-reset `CustomNotification`
/// (Phase 6p), mirroring `DataManagerIngestionStatusKey`'s shape. A new
/// `Notification` DU case is deliberately NOT minted: the DU is a closed
/// union every host matches exhaustively and every SSE listener
/// enumerates by hand, and this event is a platform event with a
/// module-shaped payload — exactly what `CustomNotification` exists for.
[<Literal>]
let SessionStoreResetKey = "Platform.SessionStoreReset"

/// `Reason` value for the TTL-eviction transition — the only one the
/// server publishes. Shared by the publisher, the audit emission and the
/// client so the three cannot drift into three spellings of one event.
[<Literal>]
let SessionStoreResetReasonEvicted = "Evicted"

/// `Reason` value for the client-derived restart classification. A
/// restart takes the SSE connection with it, so this value never crosses
/// the channel; the client stamps it on the reconciliation it raises for
/// itself after finding an epoch the server never minted.
[<Literal>]
let SessionStoreResetReasonProcessRestart = "ProcessRestart"

/// Payload carried by the `SessionStoreResetKey` `CustomNotification`.
///
/// GP 4 — scope container only. No filenames, no file contents, no user
/// identity beyond the container the audit log already records.
///
/// `Reason` is the string vocabulary rather than a DU so a future cause
/// can be added without a wire break on the hosts that render it:
///
/// * `"Evicted"` — the store was dropped by the ephemeral-store TTL sweep
///   and has now been re-created. This is the only value the server
///   PUBLISHES, because it is the only transition with a live subscriber
///   to inform.
/// * `"ProcessRestart"` — reserved for the client-derived classification.
///   A restart takes the SSE connection down with it, so there is no
///   channel over which to announce it; the client discovers it by
///   comparing its cached epoch against `GetSessionInfo` on reconnect and
///   surfaces the same reconciliation.
type SessionStoreResetNotification = {
    Container: string
    PreviousEpoch: Guid option
    CurrentEpoch: Guid
    Reason: string
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
    /// Phase 6p — identity + file count of the server-side session store
    /// backing the caller's scope. Read-only, allocates nothing, and is
    /// the one call a healthy session makes that it did not make before
    /// (once, on mount): every other reconciliation path below is driven
    /// by an event that only fires when something was actually lost.
    ///
    /// Appended at the END of the record deliberately — Fable.Remoting
    /// puts a record's methods on the wire positionally, so inserting
    /// mid-record would re-point every route after it.
    [<AllowAnonymous>]
    GetSessionInfo: unit -> Async<SessionStoreInfo>
}
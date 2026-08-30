// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.DataSources.Common

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open DataManagementTypes

// ─── Shared support for the FILE-shaped IDataSource connectors ────
//
// The warehouse connectors above reach a network endpoint and speak a
// dialect. The file connectors (`Csv`, `Excel`, `Parquet`) do neither:
// they acquire BYTES and then parse a format. That difference is the
// whole of what this module adds — everything else (ConnectionScope
// reads, the error classifier, RFC 4180 emission, `ColumnType`
// classification) they share with the warehouse family unchanged.
//
// **Acquisition is `IBlobStorage`, always — never `System.IO`.** A
// connector that opened a path off disk would bypass the storage
// abstraction, and with it scope isolation (GP 4), the encryption-at-
// rest decorator, and every cloud backend. A deployment reading files
// off local disk composes `LocalFileStorage`; the connector cannot
// tell, and does not need to.
//
// **A source is a CONTAINER + PREFIX, and a table is a FILE in it.**
// `ListTables` lists the container under the prefix and strips the
// extension; `GetSchema` and `Query` map a table name back to its blob
// name. That is what makes `ListTables` scope-isolated for free: the
// container is scope-derived, so a source can never enumerate another
// tenant's files.

/// File acquisition and table↔blob naming for the file-shaped
/// connectors.
module Files =

    /// Parsed, validated view of the `ConnectionScope` keys every
    /// file connector shares. Format-specific keys (`delimiter`,
    /// `sheet`, …) are read by the connector that owns them.
    type FileSourceSettings = {
        /// Scope-derived container the files live in. Required — a
        /// connector never invents one (GP 4).
        Container: string
        /// Blob-name prefix within the container. `""` means the
        /// container root. A non-empty prefix is normalised to end
        /// with `/`.
        Prefix: string
        /// File extension that identifies this connector's files,
        /// lower-cased and dot-prefixed (`.csv`). `ListTables`
        /// filters on it and the table→blob mapping appends it.
        Extension: string
        /// How many data rows the type probe samples when inferring a
        /// schema. Formats carrying their own schema (Parquet) ignore
        /// it.
        SampleRows: int
    }

    [<Literal>]
    let private DefaultSampleRows = 1000

    /// Normalise a user-supplied extension to the `.ext` lower-case
    /// form the naming functions assume.
    let normaliseExtension (raw: string) : string =
        let trimmed = (if isNull raw then "" else raw).Trim().ToLowerInvariant()

        if trimmed = "" then ""
        elif trimmed.StartsWith '.' then trimmed
        else "." + trimmed

    /// Normalise a prefix to `""` or `"something/"`. Blob names are
    /// `/`-delimited on `IBlobStorage` regardless of host OS, so a
    /// backslash a Windows operator typed is folded to `/` rather
    /// than silently producing a prefix that matches nothing.
    let normalisePrefix (raw: string) : string =
        let trimmed = (if isNull raw then "" else raw).Replace('\\', '/').Trim().Trim('/')
        if trimmed = "" then "" else trimmed + "/"

    /// Read the shared file-source keys. `defaultExtension` is the
    /// connector's own (`.csv`, `.xlsx`, `.parquet`); an operator may
    /// override it for a deployment whose exports carry a different
    /// suffix.
    let readSettings
        (defaultExtension: string)
        (scope: Map<string, string>)
        : Result<FileSourceSettings, IngestionError> =
        ConnectionScope.require scope "container"
        |> Result.bind (fun container ->
            ConnectionScope.optionalInt scope "sample_rows"
            |> Result.bind (fun sample ->
                let sampleRows = defaultArg sample DefaultSampleRows

                if sampleRows <= 0 then
                    Error(SchemaMismatch $"ConnectionScope key 'sample_rows' must be positive; got %d{sampleRows}")
                else
                    Ok {
                        Container = container
                        Prefix = normalisePrefix (ConnectionScope.optionalOr scope "prefix" "")
                        Extension = normaliseExtension (ConnectionScope.optionalOr scope "extension" defaultExtension)
                        SampleRows = sampleRows
                    }))

    /// Reject a table name that could escape the configured prefix.
    ///
    /// The container is scope-derived and therefore safe, but the
    /// TABLE name arrives from a persisted config or a `Query`
    /// statement, and it is concatenated onto the prefix. A name
    /// carrying `..` or a separator would address a blob outside the
    /// configured prefix — inside the same scope, so not a tenant
    /// breach, but still a source reading files it was not pointed
    /// at. Refused rather than sanitised: silently rewriting an
    /// operator's name would make the failure appear later, as a
    /// missing file, somewhere less informative.
    let requireTableName (table: string) : Result<string, IngestionError> =
        let value = if isNull table then "" else table.Trim()

        if value = "" then
            Error(SchemaMismatch "table name is empty")
        elif value.Contains ".." || value.Contains '/' || value.Contains '\\' then
            Error(
                SchemaMismatch
                    $"table name '%s{value}' must name a file within the configured prefix — '/', '\\' and '..' are not permitted"
            )
        else
            Ok value

    /// Blob name for a table: `prefix + table + extension`.
    let blobName (settings: FileSourceSettings) (table: string) : string =
        settings.Prefix + table + settings.Extension

    /// Recover a table name from a blob name, or `None` when the blob
    /// is not one of this connector's files. Both halves matter:
    /// `ListTables` must not report the `.json` sidecar sitting beside
    /// the exports, and must not report a blob in a NESTED folder as
    /// though it were a sibling (its recovered name would collide with
    /// a real one and address the wrong file).
    let tableOf (settings: FileSourceSettings) (name: string) : string option =
        let name = if isNull name then "" else name

        if not (name.StartsWith(settings.Prefix, StringComparison.Ordinal)) then
            None
        else
            let relative = name.Substring settings.Prefix.Length

            if relative.Contains '/' then
                None
            elif
                settings.Extension <> ""
                && not (relative.EndsWith(settings.Extension, StringComparison.OrdinalIgnoreCase))
            then
                None
            else
                let stem = relative.Substring(0, relative.Length - settings.Extension.Length)
                if stem = "" then None else Some stem

    /// Enumerate this connector's tables in the configured container
    /// and prefix, sorted so the listing is stable between calls.
    let listTables
        (storage: IBlobStorage)
        (context: string)
        (settings: FileSourceSettings)
        : Async<Result<string list, IngestionError>> =
        Errors.guard context (fun () -> async {
            let! names = storage.List(settings.Container, settings.Prefix)

            return
                Ok(
                    names
                    |> List.choose (tableOf settings)
                    |> List.distinct
                    |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                )
        })

    /// Acquire one table's bytes. A missing file is `SourceUnreachable`
    /// naming the blob — the operator's fix is to place the file or
    /// correct the prefix, and both are easier when the message says
    /// which name was looked for.
    let download
        (storage: IBlobStorage)
        (context: string)
        (settings: FileSourceSettings)
        (table: string)
        : Async<Result<byte[], IngestionError>> =
        Errors.guard context (fun () -> async {
            match requireTableName table with
            | Error err -> return Error err
            | Ok table ->
                let name = blobName settings table
                let! downloaded = storage.Download(settings.Container, name)

                match downloaded with
                | Ok bytes -> return Ok bytes
                | Error message ->
                    return
                        Error(
                            SourceUnreachable
                                $"%s{context}: could not read '%s{name}' from container '%s{settings.Container}': %s{message}"
                        )
        })

    /// Does this table's file exist? Used by `Connect` and by the
    /// `GetSchema` pre-check, neither of which wants to pay for a
    /// download to answer an existence question.
    let exists
        (storage: IBlobStorage)
        (context: string)
        (settings: FileSourceSettings)
        (table: string)
        : Async<Result<bool, IngestionError>> =
        Errors.guard context (fun () -> async {
            match requireTableName table with
            | Error err -> return Error err
            | Ok table ->
                let! found = storage.Exists(settings.Container, blobName settings table)
                return Ok found
        })

/// Type-probe schema inference over sampled cell text.
///
/// The probe itself is `ToolUp.Platform.ColumnMapping.inferColumnType`
/// — SDK substrate the mapping UI already runs on, so a file source's
/// inferred schema and the same file's mapping preview cannot disagree.
/// This module adds only what a connector needs on top: transposing
/// sampled ROWS into per-column samples, and naming the inferred type
/// in the RAW-native-name slot the rest of the family fills with the
/// vendor's own spelling.
module TypeProbe =

    /// The native-type name a file connector records on
    /// `ColumnInfo.DataType`. There is no vendor type system to quote
    /// here, so the inferred coarse type is spelled out and marked as
    /// inferred — an admin UI showing `number (inferred)` beside a
    /// warehouse's `NUMERIC(38,9)` is telling the operator something
    /// true and useful, namely that this one is a guess.
    let nativeName (columnType: ColumnType) : string =
        match columnType with
        | StringColumn -> "string (inferred)"
        | NumberColumn -> "number (inferred)"
        | BooleanColumn -> "boolean (inferred)"
        | DateColumn -> "date (inferred)"

    /// Infer one column's type from its sampled cell text.
    let infer (samples: string list) : ColumnType = ColumnMapping.inferColumnType samples

    /// Build a `TableSchema` from a header row and sampled data rows.
    /// Rows shorter than the header contribute no sample for the
    /// missing columns rather than a blank one — a ragged export must
    /// not drag every trailing column to `StringColumn`.
    ///
    /// A column is reported nullable when any sampled cell is blank.
    /// That is an honest reading of a headers-and-values file: the
    /// format declares no nullability, so the only evidence available
    /// is whether a blank was actually seen.
    let schemaOf (table: string) (header: string list) (rows: string list seq) : TableSchema =
        let materialised = rows |> List.ofSeq

        let columns =
            header
            |> List.mapi (fun index name ->
                let samples =
                    materialised
                    |> List.choose (fun row -> if index < row.Length then Some row[index] else None)

                let nullable =
                    samples |> List.exists (fun cell -> String.IsNullOrWhiteSpace cell)
                    || samples.Length < materialised.Length

                let inferred = infer samples

                {
                    Name = name
                    DataType = nativeName inferred
                    Nullable = nullable
                })

        { TableName = table; Columns = columns }
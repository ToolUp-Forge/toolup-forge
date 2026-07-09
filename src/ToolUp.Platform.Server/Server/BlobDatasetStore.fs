// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 448 — BlobDatasetStore (default IDatasetStore) ───────────────
//
// The default `IDatasetStore`, layered over `IDataObjectStore`: each dataset
// version is one data object (content blob = encoded rows; metadata sidecar =
// schema + row count + format). Content dedup and linear per-`(scope, id)`
// versioning are **inherited** from `IDataObjectStore` — a `Create` whose
// bytes match an existing version writes only the metadata sidecar; a new
// vintage is a new object version, prior versions untouched (GP 5). The store
// holds no per-call state (rule 4): any instance over the same
// `IDataObjectStore` serves any dataset.
//
// The row bytes are encoded through the composed `IDatasetCodec`. The default
// codec here is `JsonFrameDatasetCodec` — a BCL-only self-describing JSON
// frame (`Format = "toolup-frame-v1"`), so the default composition pulls in
// **no vendor Parquet dependency** (GP 1 / GP 13). A deployment that wants
// native-Parquet compute handoff composes a Parquet companion codec via
// `BlobDatasetStore.createWithCodec`.

/// BCL-only default dataset codec: a self-describing JSON frame over the
/// shared `FableConverters` STJ converter set (the same wire the SDK uses for
/// non-Remoting JSON). Zero vendor dependency. `Encode` / `Decode` are pure
/// with respect to their inputs (GP 12 rule 4).
type JsonFrameDatasetCodec() =
    static let jsonOptions = FableConverters.create ()

    interface IDatasetCodec with
        member _.Format = "toolup-frame-v1"

        member _.Encode(schema: DatasetSchema, rows: DatasetRow list) : byte[] =
            // Frame = (schema, rows) tuple — the FableConverters set handles
            // tuples, so no dedicated (api-surface-polluting) frame record.
            JsonSerializer.SerializeToUtf8Bytes((schema, rows), jsonOptions)

        member _.Decode(content: byte[]) : Result<DatasetSchema * DatasetRow list, string> =
            try
                let schema, rows =
                    JsonSerializer.Deserialize<DatasetSchema * DatasetRow list>(content, jsonOptions)

                Ok(schema, rows)
            with ex ->
                Error ex.Message

/// Default blob-backed `IDatasetStore` over an `IDataObjectStore` and a
/// dataset codec. Construct via `BlobDatasetStore.create` (JSON-frame codec)
/// or `createWithCodec` (a Parquet companion codec).
type BlobDatasetStore(dataObjects: IDataObjectStore, codec: IDatasetCodec) =

    [<Literal>]
    static let DataType = "toolup.dataset"

    [<Literal>]
    static let SchemaKey = "dataset.schema"

    [<Literal>]
    static let RowCountKey = "dataset.rowCount"

    [<Literal>]
    static let FormatKey = "dataset.format"

    /// Cap on rows returned in a single page — a large-dataset guard until
    /// ranged reads land (see the 448.D design-review note in the phase body).
    [<Literal>]
    static let MaxPageLimit = 10000

    static let jsonOptions = FableConverters.create ()

    /// Reserved metadata keys the store owns; stripped from the user-facing
    /// `Metadata` on read.
    let isReservedKey (k: string) =
        k.StartsWith("dataset.", StringComparison.Ordinal)

    let buildMetadata (userMeta: Map<string, string>) (schema: DatasetSchema) (rowCount: int64) (format: string) =
        let schemaJson = JsonSerializer.Serialize(schema, jsonOptions)

        userMeta
        |> Map.filter (fun k _ -> not (isReservedKey k))
        |> Map.add SchemaKey schemaJson
        |> Map.add RowCountKey (string rowCount)
        |> Map.add FormatKey format

    let mapDataObjectError (e: DataObjectError) : DatasetError =
        match e with
        | DataObjectError.NotFound -> DatasetError.NotFound
        | DataObjectError.VersionNotFound v -> DatasetError.VersionNotFound v
        | DataObjectError.DeleteForbidden -> DatasetError.DeleteForbidden
        | DataObjectError.PolicyMismatch _ -> DatasetError.StorageFailure "dataset versioning policy mismatch"
        | DataObjectError.StorageFailure m -> DatasetError.StorageFailure m

    /// Reconstruct a `DatasetVersion` from a data object's metadata sidecar
    /// (no content download).
    let toVersion (dobj: DataObject) : Result<DatasetVersion, DatasetError> =
        match Map.tryFind SchemaKey dobj.Metadata with
        | None -> Error(DatasetError.StorageFailure "dataset metadata is missing its schema sidecar")
        | Some schemaJson ->
            try
                let schema = JsonSerializer.Deserialize<DatasetSchema>(schemaJson, jsonOptions)

                let rowCount =
                    match Map.tryFind RowCountKey dobj.Metadata with
                    | Some s ->
                        match Int64.TryParse s with
                        | true, n -> n
                        | _ -> 0L
                    | None -> 0L

                let format = Map.tryFind FormatKey dobj.Metadata |> Option.defaultValue codec.Format

                let userMeta = dobj.Metadata |> Map.filter (fun k _ -> not (isReservedKey k))

                Ok {
                    DatasetId = dobj.ObjectId
                    Version = dobj.Version
                    ScopeId = dobj.ScopeId
                    Schema = schema
                    RowCount = rowCount
                    Format = format
                    ContentHash = dobj.ContentHash
                    CreatedAt = DateTimeOffset(DateTime.SpecifyKind(dobj.CreatedAt, DateTimeKind.Utc))
                    CreatedBy = dobj.CreatedBy
                    Metadata = userMeta
                }
            with ex ->
                Error(DatasetError.StorageFailure $"dataset schema sidecar is unreadable: {ex.Message}")

    /// Find one version's metadata via `ListVersions` (no content download).
    /// Distinguishes `NotFound` (no versions at all) from `VersionNotFound`.
    let findVersion (scopeId: string) (datasetId: string) (version: int) : Async<Result<DataObject, DatasetError>> = async {
        let! versions = dataObjects.ListVersions(scopeId, datasetId)

        match versions |> List.tryFind (fun d -> d.Version = version) with
        | Some dobj -> return Ok dobj
        | None when List.isEmpty versions -> return Error DatasetError.NotFound
        | None -> return Error(DatasetError.VersionNotFound version)
    }

    /// Whether a comparison `sign` satisfies a filter op. `Null`-literal
    /// equality is handled by the caller before this is reached.
    let opHolds (op: DatasetFilterOp) (sign: int) : bool =
        match op with
        | DatasetFilterOp.Eq -> sign = 0
        | DatasetFilterOp.Ne -> sign <> 0
        | DatasetFilterOp.Lt -> sign < 0
        | DatasetFilterOp.Lte -> sign <= 0
        | DatasetFilterOp.Gt -> sign > 0
        | DatasetFilterOp.Gte -> sign >= 0

    /// Validate the filters against the schema, then apply them (AND-combined)
    /// preserving row order. `Error` on a filter whose column is absent or
    /// whose literal dtype does not match its column (→ `SchemaMismatch`).
    let applyFilters
        (schema: DatasetSchema)
        (filters: DatasetFilter list)
        (rows: DatasetRow list)
        : Result<DatasetRow list, string> =
        // Resolve + validate each filter to a (columnIndex, op, literal).
        let resolved =
            filters
            |> List.map (fun f ->
                match DatasetSchema.indexOf f.Column schema with
                | None -> Error $"filter column '{f.Column}' is not in the schema"
                | Some idx ->
                    let col = List.item idx schema.Columns

                    match f.Value with
                    | DatasetValue.Null -> Ok(idx, f.Op, f.Value)
                    | _ when DatasetValue.dtypeOf f.Value = Some col.DType -> Ok(idx, f.Op, f.Value)
                    | _ -> Error $"filter literal for column '{f.Column}' does not match its dtype")

        match
            resolved
            |> List.tryPick (fun r ->
                match r with
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some reason -> Error reason
        | None ->
            let preds =
                resolved
                |> List.choose (fun r ->
                    match r with
                    | Ok v -> Some v
                    | Error _ -> None)

            let rowPasses (row: DatasetRow) =
                preds
                |> List.forall (fun (idx, op, literal) ->
                    let cell = List.item idx row.Cells

                    match op, literal with
                    | DatasetFilterOp.Eq, DatasetValue.Null -> cell = DatasetValue.Null
                    | DatasetFilterOp.Ne, DatasetValue.Null -> cell <> DatasetValue.Null
                    | _ ->
                        match DatasetValue.compare cell literal with
                        | Some sign -> opHolds op sign
                        | None -> false)

            Ok(rows |> List.filter rowPasses)

    let save
        (scopeId: string)
        (datasetId: string)
        (content: byte[])
        (schema: DatasetSchema)
        (rowCount: int64)
        (format: string)
        (createdBy: string)
        (metadata: Map<string, string>)
        (policy: VersioningPolicy)
        : Async<Result<DatasetVersion, DatasetError>> =
        async {
            let meta = buildMetadata metadata schema rowCount format

            match! dataObjects.Save(scopeId, datasetId, content, DataType, createdBy, meta, policy) with
            | Ok dobj -> return toVersion dobj
            | Error e -> return Error(mapDataObjectError e)
        }

    interface IDatasetStore with
        member _.Create(scopeId, datasetId, schema, rows, createdBy, metadata, policy) = async {
            // Schema-mismatch rejection (448.E): every row must fit the schema.
            let firstBad =
                rows
                |> List.tryPick (fun r ->
                    match DatasetSchema.validateRow schema r with
                    | Ok() -> None
                    | Error e -> Some e)

            match firstBad with
            | Some reason -> return Error(DatasetError.SchemaMismatch reason)
            | None ->
                let content = codec.Encode(schema, rows)
                let rowCount = int64 (List.length rows)
                return! save scopeId datasetId content schema rowCount codec.Format createdBy metadata policy
        }

        member _.CreateFromContent(scopeId, datasetId, schema, content, format, rowCount, createdBy, metadata, policy) =
            // Pre-encoded bytes (e.g. an upstream Parquet file): store verbatim
            // in the declared `format`; no re-encode, no row validation (the
            // bytes are opaque to the store until a matching codec reads them).
            save scopeId datasetId content schema rowCount format createdBy metadata policy

        member _.GetVersion(scopeId, datasetId, version) = async {
            match! findVersion scopeId datasetId version with
            | Ok dobj -> return toVersion dobj
            | Error e -> return Error e
        }

        member _.GetLatest(scopeId, datasetId) = async {
            let! versions = dataObjects.ListVersions(scopeId, datasetId)

            match versions |> List.sortByDescending (fun d -> d.Version) with
            | latest :: _ -> return toVersion latest
            | [] -> return Error DatasetError.NotFound
        }

        member _.ListVersions(scopeId, datasetId) = async {
            let! versions = dataObjects.ListVersions(scopeId, datasetId)

            return
                versions
                |> List.sortBy (fun d -> d.Version)
                |> List.choose (fun d ->
                    match toVersion d with
                    | Ok v -> Some v
                    | Error _ -> None)
        }

        member _.ReadPage(scopeId, datasetId, version, query) = async {
            if query.Offset < 0L then
                return Error(DatasetError.InvalidPage "offset must be non-negative")
            elif query.Limit <= 0 then
                return Error(DatasetError.InvalidPage "limit must be positive")
            else
                match! findVersion scopeId datasetId version with
                | Error e -> return Error e
                | Ok dobj ->
                    let storedFormat =
                        Map.tryFind FormatKey dobj.Metadata |> Option.defaultValue codec.Format

                    if storedFormat <> codec.Format then
                        return Error(DatasetError.UnreadableFormat storedFormat)
                    else
                        match! dataObjects.GetContent(scopeId, dobj.ContentHash) with
                        | Error e -> return Error(mapDataObjectError e)
                        | Ok bytes ->
                            match codec.Decode bytes with
                            | Error reason -> return Error(DatasetError.StorageFailure reason)
                            | Ok(schema, rows) ->
                                match applyFilters schema query.Filters rows with
                                | Error reason -> return Error(DatasetError.SchemaMismatch reason)
                                | Ok filtered ->
                                    let total = int64 (List.length filtered)
                                    let count = List.length filtered

                                    let skipN =
                                        if query.Offset >= int64 count then
                                            count
                                        else
                                            int query.Offset

                                    let pageRows =
                                        filtered |> List.skip skipN |> List.truncate (min query.Limit MaxPageLimit)

                                    return
                                        Ok {
                                            Schema = schema
                                            Rows = pageRows
                                            Offset = query.Offset
                                            TotalRows = total
                                        }
        }

        member _.GetContentRef(scopeId, datasetId, version) = async {
            match! findVersion scopeId datasetId version with
            | Error e -> return Error e
            | Ok dobj ->
                match toVersion dobj with
                | Error e -> return Error e
                | Ok v ->
                    return
                        Ok {
                            ScopeId = scopeId
                            DatasetId = datasetId
                            Version = version
                            ContentHash = dobj.ContentHash
                            Format = v.Format
                            RowCount = v.RowCount
                        }
        }

        member _.Delete(scopeId, datasetId) = async {
            match! dataObjects.Delete(scopeId, datasetId) with
            | Ok() -> return Ok()
            | Error e -> return Error(mapDataObjectError e)
        }

module BlobDatasetStore =
    /// The default blob-backed dataset store with the BCL-only JSON-frame
    /// codec (no vendor dependency).
    let create (dataObjects: IDataObjectStore) : IDatasetStore =
        BlobDatasetStore(dataObjects, JsonFrameDatasetCodec()) :> IDatasetStore

    /// A blob-backed dataset store with an explicit codec (e.g. a Parquet
    /// companion codec for native compute handoff).
    let createWithCodec (dataObjects: IDataObjectStore) (codec: IDatasetCodec) : IDatasetStore =
        BlobDatasetStore(dataObjects, codec) :> IDatasetStore
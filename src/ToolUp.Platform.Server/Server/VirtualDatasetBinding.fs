// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// --- Phase 487 -- VirtualDatasetReader ----------------------------------
//
// The in-situ deployment model demands the opposite of Phase 448's
// materialised-Parquet assumption: the data stays in the deployment's own
// stores -- no copies, no transfers. A `Virtual` dataset version is defined
// as `(IDataSource ref, query spec, watermark)` and read through at page
// time; its "vintage" is a source watermark, not copied bytes.
//
// `VirtualDatasetReader` pages typed rows straight from an `IDataSource`
// (Phase 10, the read-through seam), checking schema conformance at bind
// time and on every read (source drift is a typed error naming the changed
// column). Compute handoff for a virtual version is **ephemeral
// materialisation** with declared retention (spill -> hand off -> delete,
// every step audited), never a silent durable copy.

/// Reads a virtual dataset version through an `IDataSource`. Constructed with
/// the connector, the codec that decodes the connector's row bytes, the
/// version's **declared** schema (the conformance contract), and the
/// persisted `DataSourceConfig` used to build the per-call context.
///
/// The connector's `Query` returns opaque bytes; this reader decodes them
/// through the composed `IDatasetCodec` (the in-situ connector emits rows in
/// the deployment's dataset wire format). Pushdown of the page window +
/// filter predicates is delegated to the connector via the binding's
/// `QuerySpec` where the connector supports it; the reader always applies the
/// requested `Filter`s + paging as a residual in memory, so correctness never
/// depends on connector pushdown.
type VirtualDatasetReader
    (source: IDataSource, codec: IDatasetCodec, declaredSchema: DatasetSchema, config: DataSourceConfig) =

    [<Literal>]
    static let MaxPageLimit = 10000

    /// Reserved metadata keys stamped on an ephemeral spill blob.
    [<Literal>]
    static let SpillMarkerKey = "dataset.spill"

    [<Literal>]
    static let SpillExpiryKey = "dataset.spill.expiresAt"

    [<Literal>]
    static let SpillWatermarkKey = "dataset.spill.watermark"

    let callContext (scopeId: string) : DataSourceCallContext = {
        ScopeId = scopeId
        Config = config
        Credential = None
    }

    /// Read-time conformance: every declared column must still be present in
    /// the source's returned schema with the same dtype. A missing or
    /// dtype-changed column is a typed error naming it (source drift).
    let checkConformance (actual: DatasetSchema) : Result<unit, string> =
        let actualByName = actual.Columns |> List.map (fun c -> c.Name, c) |> Map.ofList

        declaredSchema.Columns
        |> List.tryPick (fun d ->
            match Map.tryFind d.Name actualByName with
            | None -> Some(sprintf "source no longer provides column '%s'" d.Name)
            | Some a when a.DType <> d.DType ->
                Some(
                    sprintf
                        "source column '%s' changed dtype from %s to %s"
                        d.Name
                        (DatasetDType.name d.DType)
                        (DatasetDType.name a.DType)
                )
            | Some _ -> None)
        |> function
            | Some msg -> Error msg
            | None -> Ok()

    let opHolds (op: DatasetFilterOp) (sign: int) =
        match op with
        | DatasetFilterOp.Eq -> sign = 0
        | DatasetFilterOp.Ne -> sign <> 0
        | DatasetFilterOp.Lt -> sign < 0
        | DatasetFilterOp.Lte -> sign <= 0
        | DatasetFilterOp.Gt -> sign > 0
        | DatasetFilterOp.Gte -> sign >= 0

    /// Validate + apply the query's filters against the declared schema,
    /// preserving row order. Same semantics as the materialised store's
    /// `applyFilters` so a filtered read matches a materialised read.
    let applyFilters (filters: DatasetFilter list) (rows: DatasetRow list) : Result<DatasetRow list, string> =
        let resolved =
            filters
            |> List.map (fun f ->
                match DatasetSchema.indexOf f.Column declaredSchema with
                | None -> Error(sprintf "filter column '%s' is not in the schema" f.Column)
                | Some idx ->
                    let col = List.item idx declaredSchema.Columns

                    match f.Value with
                    | DatasetValue.Null -> Ok(idx, f.Op, f.Value)
                    | _ when DatasetValue.dtypeOf f.Value = Some col.DType -> Ok(idx, f.Op, f.Value)
                    | _ -> Error(sprintf "filter literal for column '%s' does not match its dtype" f.Column))

        match
            resolved
            |> List.tryPick (function
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some reason -> Error reason
        | None ->
            let preds =
                resolved
                |> List.choose (function
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

    /// Read through: query the source at the binding's watermark, decode, and
    /// check conformance. Returns the decoded rows (unfiltered, unpaged).
    let readThrough (scopeId: string) (binding: VirtualBinding) : Async<Result<DatasetRow list, DatasetError>> = async {
        match! source.Query(callContext scopeId, binding.QuerySpec) with
        | Error e -> return Error(DatasetError.StorageFailure(sprintf "virtual source read failed: %A" e))
        | Ok bytes ->
            match codec.Decode bytes with
            | Error reason ->
                return Error(DatasetError.StorageFailure(sprintf "virtual source decode failed: %s" reason))
            | Ok(actualSchema, rows) ->
                match checkConformance actualSchema with
                | Error drift -> return Error(DatasetError.SchemaMismatch drift)
                | Ok() -> return Ok rows
    }

    /// The declared schema of the virtual version (the conformance contract).
    member _.Schema: DatasetSchema = declaredSchema

    /// Bind-time conformance + reachability probe: confirm the source is
    /// reachable and its current schema conforms to the declared schema.
    /// `Ok ()` when a virtual version can be safely served from this binding.
    member _.Bind(scopeId: string, binding: VirtualBinding) : Async<Result<unit, DatasetError>> = async {
        match! source.Connect(callContext scopeId) with
        | Error e -> return Error(DatasetError.StorageFailure(sprintf "virtual source unreachable: %A" e))
        | Ok() ->
            match! readThrough scopeId binding with
            | Error e -> return Error e
            | Ok _ -> return Ok()
    }

    /// Page typed rows straight from the source with no durable copy. Filters
    /// apply first (AND-combined) then `Offset` / `Limit` page over the
    /// matching rows. Schema conformance is re-checked on every read (source
    /// drift -> `SchemaMismatch` naming the changed column).
    member _.ReadPage
        (scopeId: string, binding: VirtualBinding, query: DatasetPageQuery)
        : Async<Result<DatasetPage, DatasetError>> =
        async {
            if query.Offset < 0L then
                return Error(DatasetError.InvalidPage "offset must be non-negative")
            elif query.Limit <= 0 then
                return Error(DatasetError.InvalidPage "limit must be positive")
            else
                match! readThrough scopeId binding with
                | Error e -> return Error e
                | Ok rows ->
                    match applyFilters query.Filters rows with
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
                                Schema = declaredSchema
                                Rows = pageRows
                                Offset = query.Offset
                                TotalRows = total
                            }
        }

    /// Ephemeral materialisation for compute handoff -- the **declared,
    /// observable exception to zero-copy** (487.C). Reads the virtual version
    /// through, re-encodes it, and spills to a retention-bounded scratch blob
    /// under `spillDatasetId`, carrying the propagated `labels` (Phase 482)
    /// and a declared `ttl` after which the spill is eligible for deletion.
    /// Emits a `DatasetSpillCreated` audit row so the copy is never silent.
    /// Returns the `DatasetContentRef` a worker fetches the raw bytes with.
    member _.MaterialiseForHandoff
        (
            scopeId: string,
            binding: VirtualBinding,
            dataObjects: IDataObjectStore,
            spillDatasetId: string,
            ttl: TimeSpan,
            labels: DataProvenanceLabel list,
            audit: IAuditLog,
            actor: string
        ) : Async<Result<DatasetContentRef, DatasetError>> =
        async {
            match! readThrough scopeId binding with
            | Error e -> return Error e
            | Ok rows ->
                let bytes = codec.Encode(declaredSchema, rows)
                let rowCount = int64 (List.length rows)
                let expiresAt = DateTime.UtcNow.Add ttl

                let metadata =
                    Map [
                        SpillMarkerKey, "true"
                        SpillExpiryKey, expiresAt.ToString("o")
                        SpillWatermarkKey, binding.Watermark
                    ]
                    |> DataProvenanceLabels.writeInto labels

                match!
                    dataObjects.Save(scopeId, spillDatasetId, bytes, "toolup.dataset.spill", actor, metadata, Versioned)
                with
                | Error e -> return Error(DatasetError.StorageFailure(sprintf "%A" e))
                | Ok dobj ->
                    do!
                        audit.Record(
                            scopeId,
                            DatasetSpillCreated {
                                Actor = actor
                                ScopeId = scopeId
                                SpillDatasetId = spillDatasetId
                                Watermark = binding.Watermark
                                ExpiresAt = expiresAt
                                RowCount = rowCount
                            }
                        )

                    return
                        Ok {
                            ScopeId = scopeId
                            DatasetId = spillDatasetId
                            Version = dobj.Version
                            ContentHash = dobj.ContentHash
                            Format = codec.Format
                            RowCount = rowCount
                        }
        }

    /// Delete a spill blob and audit the deletion, closing the spill
    /// lifecycle (487.C). `reason` is `"ttl-expired"` for a retention sweep or
    /// `"explicit"` for an eager cleanup after handoff.
    member _.DeleteSpill
        (
            scopeId: string,
            spillDatasetId: string,
            dataObjects: IDataObjectStore,
            audit: IAuditLog,
            actor: string,
            reason: string
        ) : Async<Result<unit, DatasetError>> =
        async {
            match! dataObjects.Delete(scopeId, spillDatasetId) with
            | Error e -> return Error(DatasetError.StorageFailure(sprintf "%A" e))
            | Ok() ->
                do!
                    audit.Record(
                        scopeId,
                        DatasetSpillDeleted {
                            Actor = actor
                            ScopeId = scopeId
                            SpillDatasetId = spillDatasetId
                            Reason = reason
                        }
                    )

                return Ok()
        }

module VirtualDatasetReader =
    /// Construct a reader over the default JSON-frame codec (the in-situ
    /// connector emits rows in `toolup-frame-v1`). A deployment whose
    /// connector emits a different wire format constructs the reader with the
    /// matching codec.
    let create
        (source: IDataSource)
        (codec: IDatasetCodec)
        (declaredSchema: DatasetSchema)
        (config: DataSourceConfig)
        : VirtualDatasetReader =
        VirtualDatasetReader(source, codec, declaredSchema, config)
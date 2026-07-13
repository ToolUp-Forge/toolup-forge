// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text
open System.Security.Cryptography

// --- Phase 452 -- DatasetAssemblyExecutor -------------------------------
//
// Materialises a `DatasetAssemblySpec` into new Phase 448 dataset
// version(s), recording the spec + the source identities read as provenance
// on each produced version (452.B). Forge owns the *plumbing* -- resolve
// sources, join / resample / lag / window / filter / split -- and never
// computes a derived value outside the closed aggregate list (452.C / plan
// risk #3 / GP 1). Deterministic given identical sources, so replaying a
// spec against moved sources is the "new vintage" mechanism and the
// provenance shows exactly what moved.

/// The default assembly executor. Resolves dataset + time-series sources
/// natively; an optional `resolveExternal` seam handles entity / ingested
/// tables so their query semantics stay out of the assembly core (GP 1).
/// `timeSeries` is optional -- an assembly that never reads a series needs
/// no store. Every produced subset is written through `datasets.Create`, so
/// output provenance + immutability (GP 5) are inherited.
type DatasetAssemblyExecutor
    (
        datasets: IDatasetStore,
        ?timeSeries: ITimeSeriesStore,
        ?resolveExternal: ExternalTableBinding -> Async<Result<DatasetSchema * DatasetRow list, string>>
    ) =

    // Intermediate representation: named cells keyed by column, so joins /
    // resample / partitioned shifts read columns by name. Converted back to
    // positional `DatasetRow`s at write time.
    let cellsOf (schema: DatasetSchema) (row: DatasetRow) : Map<string, DatasetValue> =
        List.zip schema.Columns row.Cells
        |> List.map (fun (c, v) -> c.Name, v)
        |> Map.ofList

    let columnByName (columns: DatasetColumn list) (name: string) : DatasetColumn option =
        columns |> List.tryFind (fun c -> c.Name = name)

    // -- Source resolution --------------------------------------------------

    /// Read every row of a dataset vintage, paging in fixed windows until
    /// the filtered total is reached (mirrors the scorer's readAllRows).
    let readAllDatasetRows (r: DatasetVersionRef) : Async<Result<DatasetSchema * DatasetRow list, AssemblyError>> =
        let pageSize = 1000

        let rec loop (offset: int64) (acc: DatasetRow list) = async {
            let query: DatasetPageQuery = {
                Offset = offset
                Limit = pageSize
                Filters = []
            }

            match! datasets.ReadPage(r.ScopeId, r.DatasetId, r.Version, query) with
            | Error e -> return Error(AssemblyError.SourceUnavailable(DatasetError.describe e))
            | Ok page ->
                let acc = acc @ page.Rows
                let read = offset + int64 (List.length page.Rows)

                if List.isEmpty page.Rows || read >= page.TotalRows then
                    return Ok(page.Schema, acc)
                else
                    return! loop read acc
        }

        loop 0L []

    /// Materialise a time-series range into a long-format frame: a
    /// `PanelUnit` label column, a `PanelPeriod` timestamp column, and a
    /// plain value column.
    let resolveTimeSeries (b: TimeSeriesSourceBinding) : Async<Result<DatasetSchema * DatasetRow list, AssemblyError>> = async {
        match timeSeries with
        | None -> return Error(AssemblyError.SourceUnavailable "no ITimeSeriesStore wired for a TimeSeriesRange source")
        | Some ts ->
            match! ts.QueryRange(b.ScopeId, b.Series, b.From, b.Until, b.Downsample) with
            | Error e -> return Error(AssemblyError.SourceUnavailable(TimeSeriesError.describe e))
            | Ok points ->
                let schema: DatasetSchema = {
                    Columns = [
                        {
                            Name = b.UnitColumn
                            DType = DatasetDType.Categorical
                            Nullable = false
                            Role = DatasetColumnRole.PanelUnit
                        }
                        {
                            Name = b.PeriodColumn
                            DType = DatasetDType.Timestamp
                            Nullable = false
                            Role = DatasetColumnRole.PanelPeriod
                        }
                        {
                            Name = b.ValueColumn
                            DType = DatasetDType.Float
                            Nullable = false
                            Role = DatasetColumnRole.Plain
                        }
                    ]
                }

                let rows =
                    points
                    |> List.map (fun p -> {
                        Cells = [
                            DatasetValue.Categorical b.UnitLabel
                            DatasetValue.Timestamp p.Timestamp
                            DatasetValue.Float p.Value
                        ]
                    })

                return Ok(schema, rows)
    }

    let resolveSource
        (source: AssemblySource)
        : Async<Result<DatasetColumn list * Map<string, DatasetValue> list, AssemblyError>> =
        async {
            let toFrame (schema: DatasetSchema) (rows: DatasetRow list) =
                schema.Columns, rows |> List.map (cellsOf schema)

            match source with
            | AssemblySource.DatasetVersion r ->
                match! readAllDatasetRows r with
                | Error e -> return Error e
                | Ok(schema, rows) -> return Ok(toFrame schema rows)
            | AssemblySource.TimeSeriesRange b ->
                match! resolveTimeSeries b with
                | Error e -> return Error e
                | Ok(schema, rows) -> return Ok(toFrame schema rows)
            | AssemblySource.ExternalTable b ->
                match resolveExternal with
                | None -> return Error(AssemblyError.ResolverMissing b.Kind)
                | Some resolve ->
                    match! resolve b with
                    | Error msg -> return Error(AssemblyError.SourceUnavailable msg)
                    | Ok(schema, rows) -> return Ok(toFrame schema rows)
        }

    // -- Aggregation over typed cells ---------------------------------------

    let numOf =
        function
        | DatasetValue.Float f -> Some f
        | DatasetValue.Int i -> Some(float i)
        | _ -> None

    /// Fold a bucket of cells with a shared aggregate. Numeric folds skip
    /// `Null` and an all-null / non-numeric bucket folds to `Null` (never a
    /// silent zero). `Count` counts non-null cells; `First` / `Last` return
    /// the original typed cell.
    let foldCells (agg: TimeSeriesAggregation) (cells: DatasetValue list) : DatasetValue =
        match agg with
        | TimeSeriesAggregation.Count ->
            DatasetValue.Int(int64 (cells |> List.filter (fun c -> c <> DatasetValue.Null) |> List.length))
        | TimeSeriesAggregation.First -> cells |> List.tryHead |> Option.defaultValue DatasetValue.Null
        | TimeSeriesAggregation.Last -> cells |> List.tryLast |> Option.defaultValue DatasetValue.Null
        | TimeSeriesAggregation.Sum
        | TimeSeriesAggregation.Average
        | TimeSeriesAggregation.Min
        | TimeSeriesAggregation.Max ->
            let ns = cells |> List.choose numOf

            if List.isEmpty ns then
                DatasetValue.Null
            else
                DatasetValue.Float(
                    match agg with
                    | TimeSeriesAggregation.Sum -> List.sum ns
                    | TimeSeriesAggregation.Average -> List.average ns
                    | TimeSeriesAggregation.Min -> List.min ns
                    | TimeSeriesAggregation.Max -> List.max ns
                    | _ -> 0.0
                )

    /// dtype an aggregate produces (for the output column def).
    let aggDType =
        function
        | TimeSeriesAggregation.Count -> DatasetDType.Int
        | _ -> DatasetDType.Float

    // -- Ordering -----------------------------------------------------------

    /// Order rows by a (period) column using the total-ish cell compare.
    /// `List.sortWith` is stable, so equal-period rows keep input order.
    let sortByColumn (col: string) (rows: Map<string, DatasetValue> list) =
        rows
        |> List.sortWith (fun a b ->
            let av = Map.tryFind col a |> Option.defaultValue DatasetValue.Null
            let bv = Map.tryFind col b |> Option.defaultValue DatasetValue.Null

            match DatasetValue.compare av bv with
            | Some sign -> sign
            | None -> 0)

    /// Partition rows into groups keyed by their `keys` cell tuple,
    /// preserving first-appearance order of the groups.
    let partitionByKeys
        (keys: string list)
        (rows: Map<string, DatasetValue> list)
        : (Map<string, DatasetValue> list) list =
        let keyOf (r: Map<string, DatasetValue>) =
            keys
            |> List.map (fun k -> Map.tryFind k r |> Option.defaultValue DatasetValue.Null)

        let order = System.Collections.Generic.List<_>()

        let groups =
            System.Collections.Generic.Dictionary<_, System.Collections.Generic.List<Map<string, DatasetValue>>>(
                HashIdentity.Structural
            )

        for r in rows do
            let k = keyOf r

            match groups.TryGetValue k with
            | true, g -> g.Add r
            | false, _ ->
                let g = System.Collections.Generic.List<_>()
                g.Add r
                groups[k] <- g
                order.Add k

        [ for k in order -> List.ofSeq groups[k] ]

    // -- Transforms ---------------------------------------------------------

    let opHolds (op: DatasetFilterOp) (sign: int) =
        match op with
        | DatasetFilterOp.Eq -> sign = 0
        | DatasetFilterOp.Ne -> sign <> 0
        | DatasetFilterOp.Lt -> sign < 0
        | DatasetFilterOp.Lte -> sign <= 0
        | DatasetFilterOp.Gt -> sign > 0
        | DatasetFilterOp.Gte -> sign >= 0

    let requireColumn (columns: DatasetColumn list) (name: string) : Result<DatasetColumn, AssemblyError> =
        match columnByName columns name with
        | Some c -> Ok c
        | None -> Error(AssemblyError.UnknownColumn name)

    let applyJoin
        (columns: DatasetColumn list)
        (rows: Map<string, DatasetValue> list)
        (rightColumns: DatasetColumn list)
        (rightRows: Map<string, DatasetValue> list)
        (leftKeys: string list)
        (rightKeys: string list)
        (how: AssemblyJoinKind)
        : Result<DatasetColumn list * Map<string, DatasetValue> list, AssemblyError> =
        if List.length leftKeys <> List.length rightKeys then
            Error(AssemblyError.SchemaConflict "join key lists differ in length")
        elif List.isEmpty leftKeys then
            Error(AssemblyError.SchemaConflict "join requires at least one key column")
        else
            let missingLeft =
                leftKeys |> List.filter (fun k -> columnByName columns k |> Option.isNone)

            let missingRight =
                rightKeys |> List.filter (fun k -> columnByName rightColumns k |> Option.isNone)

            if not (List.isEmpty missingLeft) then
                Error(AssemblyError.UnknownColumn(sprintf "left join key(s) %s" (String.concat ", " missingLeft)))
            elif not (List.isEmpty missingRight) then
                Error(AssemblyError.UnknownColumn(sprintf "right join key(s) %s" (String.concat ", " missingRight)))
            else
                let rightKeySet = Set.ofList rightKeys

                let rightContributed =
                    rightColumns |> List.filter (fun c -> not (Set.contains c.Name rightKeySet))

                let leftNames = columns |> List.map _.Name |> Set.ofList

                let clash =
                    rightContributed |> List.tryFind (fun c -> Set.contains c.Name leftNames)

                match clash with
                | Some c ->
                    Error(
                        AssemblyError.SchemaConflict(sprintf "join right column '%s' clashes with a left column" c.Name)
                    )
                | None ->
                    let rightKeyOf (r: Map<string, DatasetValue>) =
                        rightKeys
                        |> List.map (fun k -> Map.tryFind k r |> Option.defaultValue DatasetValue.Null)

                    let rightIndex = rightRows |> List.groupBy rightKeyOf |> Map.ofList

                    let leftKeyOf (r: Map<string, DatasetValue>) =
                        leftKeys
                        |> List.map (fun k -> Map.tryFind k r |> Option.defaultValue DatasetValue.Null)

                    let mergeRight (left: Map<string, DatasetValue>) (right: Map<string, DatasetValue> option) =
                        rightContributed
                        |> List.fold
                            (fun (acc: Map<string, DatasetValue>) col ->
                                let v =
                                    match right with
                                    | Some r -> Map.tryFind col.Name r |> Option.defaultValue DatasetValue.Null
                                    | None -> DatasetValue.Null

                                Map.add col.Name v acc)
                            left

                    let outRows =
                        rows
                        |> List.collect (fun l ->
                            match Map.tryFind (leftKeyOf l) rightIndex with
                            | Some matches -> matches |> List.map (fun r -> mergeRight l (Some r))
                            | None ->
                                match how with
                                | AssemblyJoinKind.LeftOuter -> [ mergeRight l None ]
                                | AssemblyJoinKind.Inner -> [])

                    let outColumns =
                        columns
                        @ (rightContributed
                           |> List.map (fun c -> {
                               c with
                                   Nullable = true
                                   Role = DatasetColumnRole.Plain
                           }))

                    Ok(outColumns, outRows)

    let applyResample
        (columns: DatasetColumn list)
        (rows: Map<string, DatasetValue> list)
        (periodColumn: string)
        (bucket: TimeSpan)
        (partitionKeys: string list)
        (aggregations: AssemblyAggregation list)
        : Result<DatasetColumn list * Map<string, DatasetValue> list, AssemblyError> =
        match requireColumn columns periodColumn with
        | Error e -> Error e
        | Ok periodCol when periodCol.DType <> DatasetDType.Timestamp ->
            Error(AssemblyError.TypeMismatch(sprintf "resample period column '%s' must be Timestamp" periodColumn))
        | Ok periodCol ->
            let missingPk =
                partitionKeys |> List.filter (fun k -> columnByName columns k |> Option.isNone)

            let missingAgg =
                aggregations
                |> List.filter (fun a -> columnByName columns a.Column |> Option.isNone)

            let outCols () =
                (partitionKeys |> List.choose (columnByName columns))
                @ [ periodCol ]
                @ (aggregations
                   |> List.map (fun a -> {
                       Name = a.As
                       DType = aggDType a.Aggregation
                       Nullable = true
                       Role = DatasetColumnRole.Plain
                   }))

            if not (List.isEmpty missingPk) then
                Error(
                    AssemblyError.UnknownColumn(sprintf "resample partition key(s) %s" (String.concat ", " missingPk))
                )
            elif not (List.isEmpty missingAgg) then
                Error(
                    AssemblyError.UnknownColumn(
                        sprintf "resample column(s) %s" (missingAgg |> List.map _.Column |> String.concat ", ")
                    )
                )
            elif bucket.Ticks <= 0L then
                Error(AssemblyError.TypeMismatch "resample bucket must be positive")
            else
                let periods =
                    rows
                    |> List.choose (fun r ->
                        match Map.tryFind periodColumn r with
                        | Some(DatasetValue.Timestamp t) -> Some t
                        | _ -> None)

                if List.isEmpty periods then
                    Ok(outCols (), [])
                else
                    let origin = List.min periods
                    let bucketOf (t: DateTimeOffset) = (t - origin).Ticks / bucket.Ticks
                    let bucketStart (idx: int64) = origin.AddTicks(idx * bucket.Ticks)

                    let groupKey (r: Map<string, DatasetValue>) =
                        let pk =
                            partitionKeys
                            |> List.map (fun k -> Map.tryFind k r |> Option.defaultValue DatasetValue.Null)

                        let b =
                            match Map.tryFind periodColumn r with
                            | Some(DatasetValue.Timestamp t) -> bucketOf t
                            | _ -> 0L

                        pk, b

                    let order = System.Collections.Generic.List<_>()

                    let groups =
                        System.Collections.Generic.Dictionary<
                            _,
                            System.Collections.Generic.List<Map<string, DatasetValue>>
                         >(
                            HashIdentity.Structural
                        )

                    for r in rows do
                        let k = groupKey r

                        match groups.TryGetValue k with
                        | true, g -> g.Add r
                        | false, _ ->
                            let g = System.Collections.Generic.List<_>()
                            g.Add r
                            groups[k] <- g
                            order.Add k

                    let outRows = [
                        for (pk, bIdx) as k in order ->
                            let bucketRows = List.ofSeq groups[k]
                            let pkCells = List.zip partitionKeys pk

                            let aggCells =
                                aggregations
                                |> List.map (fun a ->
                                    let cells =
                                        bucketRows
                                        |> List.map (fun r ->
                                            Map.tryFind a.Column r |> Option.defaultValue DatasetValue.Null)

                                    a.As, foldCells a.Aggregation cells)

                            Map.ofList (pkCells @ [ periodColumn, DatasetValue.Timestamp(bucketStart bIdx) ] @ aggCells)
                    ]

                    Ok(outCols (), outRows)

    /// Shared per-partition ordered rewrite for Lag / Window -- order each
    /// partition by the period column, compute the new column per position,
    /// concatenate partitions in first-appearance order.
    let perPartitionOrdered
        (columns: DatasetColumn list)
        (rows: Map<string, DatasetValue> list)
        (partition: string list)
        (periodColumn: string)
        (sourceColumn: string)
        (asName: string)
        (compute: DatasetValue list -> int -> DatasetValue)
        : Result<DatasetColumn list * Map<string, DatasetValue> list, AssemblyError> =
        match requireColumn columns sourceColumn with
        | Error e -> Error e
        | Ok srcCol ->
            match requireColumn columns periodColumn with
            | Error e -> Error e
            | Ok _ ->
                let missingPk =
                    partition |> List.filter (fun k -> columnByName columns k |> Option.isNone)

                if not (List.isEmpty missingPk) then
                    Error(AssemblyError.UnknownColumn(sprintf "partition key(s) %s" (String.concat ", " missingPk)))
                else
                    let outRows =
                        partitionByKeys partition rows
                        |> List.collect (fun part ->
                            let ordered = sortByColumn periodColumn part

                            let colValues =
                                ordered
                                |> List.map (fun r ->
                                    Map.tryFind sourceColumn r |> Option.defaultValue DatasetValue.Null)

                            ordered |> List.mapi (fun i r -> Map.add asName (compute colValues i) r))

                    let outCols =
                        columns
                        @ [
                            {
                                Name = asName
                                DType = srcCol.DType
                                Nullable = true
                                Role = DatasetColumnRole.Plain
                            }
                        ]

                    Ok(outCols, outRows)

    let applyFilterTransform
        (columns: DatasetColumn list)
        (rows: Map<string, DatasetValue> list)
        (filters: DatasetFilter list)
        : Result<DatasetColumn list * Map<string, DatasetValue> list, AssemblyError> =
        let resolved =
            filters
            |> List.map (fun f ->
                match columnByName columns f.Column with
                | None -> Error(AssemblyError.UnknownColumn(sprintf "filter column '%s'" f.Column))
                | Some col ->
                    match f.Value with
                    | DatasetValue.Null -> Ok(f.Column, f.Op, f.Value)
                    | _ when DatasetValue.dtypeOf f.Value = Some col.DType -> Ok(f.Column, f.Op, f.Value)
                    | _ ->
                        Error(
                            AssemblyError.TypeMismatch(
                                sprintf "filter literal for column '%s' does not match its dtype" f.Column
                            )
                        ))

        match
            resolved
            |> List.tryPick (function
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some e -> Error e
        | None ->
            let preds =
                resolved
                |> List.choose (function
                    | Ok v -> Some v
                    | Error _ -> None)

            let rowPasses (r: Map<string, DatasetValue>) =
                preds
                |> List.forall (fun (col, op, literal) ->
                    let cell = Map.tryFind col r |> Option.defaultValue DatasetValue.Null

                    match op, literal with
                    | DatasetFilterOp.Eq, DatasetValue.Null -> cell = DatasetValue.Null
                    | DatasetFilterOp.Ne, DatasetValue.Null -> cell <> DatasetValue.Null
                    | _ ->
                        match DatasetValue.compare cell literal with
                        | Some sign -> opHolds op sign
                        | None -> false)

            Ok(columns, rows |> List.filter rowPasses)

    /// Apply one transform, threading a running list of source identities (a
    /// Join pulls a second source, whose identity must be recorded).
    let applyTransform
        (columns: DatasetColumn list, rows: Map<string, DatasetValue> list, sources: string list)
        (transform: AssemblyTransform)
        : Async<Result<DatasetColumn list * Map<string, DatasetValue> list * string list, AssemblyError>> =
        async {
            match transform with
            | AssemblyTransform.Join(right, leftKeys, rightKeys, how) ->
                match! resolveSource right with
                | Error e -> return Error e
                | Ok(rightCols, rightRows) ->
                    match applyJoin columns rows rightCols rightRows leftKeys rightKeys how with
                    | Error e -> return Error e
                    | Ok(c, r) -> return Ok(c, r, sources @ [ AssemblyProvenance.sourceIdentity right ])
            | AssemblyTransform.Resample(periodColumn, bucket, partitionKeys, aggregations) ->
                match applyResample columns rows periodColumn bucket partitionKeys aggregations with
                | Error e -> return Error e
                | Ok(c, r) -> return Ok(c, r, sources)
            | AssemblyTransform.Lag(column, by, partition, periodColumn, asName) ->
                let compute (values: DatasetValue list) (i: int) =
                    if i - by < 0 then
                        DatasetValue.Null
                    else
                        List.item (i - by) values

                match perPartitionOrdered columns rows partition periodColumn column asName compute with
                | Error e -> return Error e
                | Ok(c, r) -> return Ok(c, r, sources)
            | AssemblyTransform.Window(column, size, aggregation, partition, periodColumn, asName) ->
                let compute (values: DatasetValue list) (i: int) =
                    let lo = max 0 (i - size + 1)
                    foldCells aggregation values[lo..i]

                match perPartitionOrdered columns rows partition periodColumn column asName compute with
                | Error e -> return Error e
                | Ok(c, r) -> return Ok(c, r, sources)
            | AssemblyTransform.Filter filters ->
                match applyFilterTransform columns rows filters with
                | Error e -> return Error e
                | Ok(c, r) -> return Ok(c, r, sources)
        }

    // -- Split --------------------------------------------------------------

    /// Stable non-negative 63-bit hash of a value's canonical string, for
    /// deterministic unit-hash splitting (independent of process / runtime
    /// hashcode).
    let stableHash (s: string) : uint64 =
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes s)
        BitConverter.ToUInt64(bytes, 0)

    let cellText =
        function
        | DatasetValue.Text s
        | DatasetValue.Categorical s -> s
        | DatasetValue.Int i -> string i
        | DatasetValue.Float f -> string f
        | DatasetValue.Bool b -> string b
        | DatasetValue.Timestamp t -> t.ToString("o")
        | DatasetValue.Null -> " null"

    let splitRows
        (columns: DatasetColumn list)
        (rows: Map<string, DatasetValue> list)
        (split: AssemblySplit option)
        : Result<(string * Map<string, DatasetValue> list) list, AssemblyError> =
        match split with
        | None -> Ok [ "all", rows ]
        | Some(AssemblySplit.ByPeriodCutoff(periodColumn, cutoff, beforeName, afterName)) ->
            match columnByName columns periodColumn with
            | None -> Error(AssemblyError.UnknownColumn(sprintf "split period column '%s'" periodColumn))
            | Some _ ->
                let before, after =
                    rows
                    |> List.partition (fun r ->
                        match Map.tryFind periodColumn r with
                        | Some(DatasetValue.Timestamp t) -> t < cutoff
                        | _ -> false)

                Ok [ beforeName, before; afterName, after ]
        | Some(AssemblySplit.ByUnitHash(unitColumn, buckets)) ->
            match columnByName columns unitColumn with
            | None -> Error(AssemblyError.UnknownColumn(sprintf "split unit column '%s'" unitColumn))
            | Some _ when List.isEmpty buckets -> Error(AssemblyError.SchemaConflict "unit-hash split has no buckets")
            | Some _ ->
                let total = buckets |> List.sumBy (snd >> int64)

                if total <= 0L then
                    Error(AssemblyError.SchemaConflict "unit-hash split weights sum to zero")
                else
                    let bands =
                        buckets
                        |> List.fold
                            (fun (acc, running) (name, w) ->
                                let hi = running + int64 w
                                (acc @ [ name, running, hi ]), hi)
                            ([], 0L)
                        |> fst

                    let bucketFor (r: Map<string, DatasetValue>) =
                        let v = Map.tryFind unitColumn r |> Option.defaultValue DatasetValue.Null
                        let h = int64 (stableHash (cellText v) % uint64 total)

                        bands
                        |> List.tryFind (fun (_, lo, hi) -> h >= lo && h < hi)
                        |> Option.map (fun (n, _, _) -> n)
                        |> Option.defaultValue (buckets |> List.last |> fst)

                    let assigned = rows |> List.map (fun r -> bucketFor r, r)

                    Ok [
                        for (name, _) in buckets ->
                            name, (assigned |> List.filter (fun (n, _) -> n = name) |> List.map snd)
                    ]

    // -- Output -------------------------------------------------------------

    let applyRoles (roles: (string * DatasetColumnRole) list) (columns: DatasetColumn list) : DatasetColumn list =
        let roleMap = Map.ofList roles

        columns
        |> List.map (fun c ->
            match Map.tryFind c.Name roleMap with
            | Some r -> { c with Role = r }
            | None -> c)

    /// Convert a working frame to a positional `(schema, rows)` for the store.
    let toDataset
        (columns: DatasetColumn list)
        (roles: (string * DatasetColumnRole) list)
        (rows: Map<string, DatasetValue> list)
        : DatasetSchema * DatasetRow list =
        let schema = { Columns = applyRoles roles columns }

        let datasetRows =
            rows
            |> List.map (fun r -> {
                Cells =
                    columns
                    |> List.map (fun c -> Map.tryFind c.Name r |> Option.defaultValue DatasetValue.Null)
            })

        schema, datasetRows

    /// Labels of a source version (Phase 482 propagation): only
    /// dataset-version sources carry labels; a fresh time-series / external
    /// frame is unlabelled. A version derived from labelled inputs inherits
    /// the union of their labels, enforced here in the producing executor,
    /// not left to callers.
    let collectSourceLabels (source: AssemblySource) : Async<DataProvenanceLabel list> = async {
        match source with
        | AssemblySource.DatasetVersion r ->
            match! datasets.GetVersion(r.ScopeId, r.DatasetId, r.Version) with
            | Ok v -> return v.Labels
            | Error _ -> return []
        | _ -> return []
    }

    /// Provenance metadata stamped on a produced subset version -- the spec
    /// hash, the subset name, the source identities read (452.B), and the
    /// propagated privacy-provenance labels (Phase 482).
    let buildProvenance
        (spec: DatasetAssemblySpec)
        (subset: string)
        (sources: string list)
        (labels: DataProvenanceLabel list)
        : Map<string, string> =
        Map [
            AssemblyProvenance.SpecHashKey, AssemblyProvenance.specHash spec
            AssemblyProvenance.SubsetKey, subset
            AssemblyProvenance.SourcesKey, String.concat "\n" sources
        ]
        |> DataProvenanceLabels.writeInto labels

    interface IDatasetAssemblyExecutor with
        member _.Assemble
            (spec: DatasetAssemblySpec, createdBy: string)
            : Async<Result<Map<string, DatasetVersionRef>, AssemblyError>> =
            async {
                match! resolveSource spec.Base with
                | Error e -> return Error e
                | Ok(baseCols, baseRows) ->
                    let! baseLabels = collectSourceLabels spec.Base
                    let seed = baseCols, baseRows, [ AssemblyProvenance.sourceIdentity spec.Base ]

                    // Fold the transforms in order; a Join accumulates the
                    // right source's labels into the propagated union.
                    let rec runTransforms
                        (state: Result<DatasetColumn list * Map<string, DatasetValue> list * string list, AssemblyError>)
                        (labels: DataProvenanceLabel list)
                        (ts: AssemblyTransform list)
                        =
                        async {
                            match state, ts with
                            | Error e, _ -> return Error e
                            | Ok s, [] -> return Ok(s, labels)
                            | Ok s, t :: rest ->
                                let! next = applyTransform s t

                                let! labels' =
                                    match t with
                                    | AssemblyTransform.Join(right, _, _, _) -> async {
                                        let! rl = collectSourceLabels right
                                        return DataProvenanceLabel.union labels rl
                                      }
                                    | _ -> async { return labels }

                                return! runTransforms next labels' rest
                        }

                    match! runTransforms (Ok seed) baseLabels spec.Transforms with
                    | Error e -> return Error e
                    | Ok((columns, rows, sources), labels) ->
                        match splitRows columns rows spec.Split with
                        | Error e -> return Error e
                        | Ok subsets ->
                            // Write every subset; short-circuit on the first
                            // storage error.
                            let rec writeAll
                                (acc: Map<string, DatasetVersionRef>)
                                (pending: (string * Map<string, DatasetValue> list) list)
                                =
                                async {
                                    match pending with
                                    | [] -> return Ok acc
                                    | (subsetName, subsetRows) :: rest ->
                                        let schema, datasetRows = toDataset columns spec.OutputRoles subsetRows

                                        // Subset id uses a '.' separator (a
                                        // ':' is an illegal path char on
                                        // Windows-backed blob stores).
                                        let datasetId =
                                            match spec.Split with
                                            | None -> spec.OutputDatasetId
                                            | Some _ -> sprintf "%s.%s" spec.OutputDatasetId subsetName

                                        let metadata = buildProvenance spec subsetName sources labels

                                        match!
                                            datasets.Create(
                                                spec.Scope,
                                                datasetId,
                                                schema,
                                                datasetRows,
                                                createdBy,
                                                metadata,
                                                spec.Policy
                                            )
                                        with
                                        | Error e -> return Error(AssemblyError.StorageFailure(DatasetError.describe e))
                                        | Ok v ->
                                            let outRef: DatasetVersionRef = {
                                                ScopeId = v.ScopeId
                                                DatasetId = v.DatasetId
                                                Version = v.Version
                                            }

                                            return! writeAll (Map.add subsetName outRef acc) rest
                                }

                            return! writeAll Map.empty subsets
            }

module DatasetAssemblyExecutor =
    /// The default executor over a dataset store (no time-series / external
    /// sources wired).
    let create (datasets: IDatasetStore) : IDatasetAssemblyExecutor =
        DatasetAssemblyExecutor(datasets) :> IDatasetAssemblyExecutor

    /// The default executor with a time-series store wired (for
    /// `TimeSeriesRange` sources feeding `Resample`).
    let createWithTimeSeries (datasets: IDatasetStore) (timeSeries: ITimeSeriesStore) : IDatasetAssemblyExecutor =
        DatasetAssemblyExecutor(datasets, timeSeries) :> IDatasetAssemblyExecutor
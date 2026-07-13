// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DatasetAssemblyTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore

// --- Phase 452 -- DatasetAssemblyExecutor tests -------------------------
//
// Assembly is the algorithm-free "new vintage" path: join entities x time
// series, resample to a grain, lag/window over the panel period, filter,
// split train/holdout -- materialised as new Phase 448 versions carrying
// the spec + source identities as provenance. These tests exercise
// executor correctness across the source + transform space and the replay
// determinism the out-of-time evaluation story depends on.

let private freshStore () : IDatasetStore =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-assembly-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore
    BlobDatasetStore.create dataObjects

let private okv =
    function
    | Ok v -> v
    | Error(e: AssemblyError) -> failtestf "expected Ok; got %s" (AssemblyError.describe e)

let private okds =
    function
    | Ok v -> v
    | Error(e: DatasetError) -> failtestf "expected Ok; got %s" (DatasetError.describe e)

let private txt s = DatasetValue.Text s
let private cat s = DatasetValue.Categorical s
let private f (x: float) = DatasetValue.Float x

let private col name dtype role : DatasetColumn = {
    Name = name
    DType = dtype
    Nullable = false
    Role = role
}

/// Read every row of a produced version so a test can assert on content.
let private readAll (store: IDatasetStore) (r: DatasetVersionRef) = async {
    match! store.ReadPage(r.ScopeId, r.DatasetId, r.Version, DatasetPageQuery.firstPage 10000) with
    | Ok page -> return page
    | Error e -> return failtestf "read failed: %s" (DatasetError.describe e)
}

let private t (day: int) =
    DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero)

// A small entity-like dataset: unit -> region attribute.
let private unitsSchema: DatasetSchema = {
    Columns = [
        col "unit" DatasetDType.Categorical DatasetColumnRole.PanelUnit
        col "region" DatasetDType.Text DatasetColumnRole.Plain
    ]
}

let private unitRows = [ { Cells = [ cat "u1"; txt "north" ] }; { Cells = [ cat "u2"; txt "south" ] } ]

let tests =
    testList "DatasetAssembly" [
        testCaseAsync
            "join + resample + lag + filter + split lands versions whose provenance names every source; replay is a new vintage with the same spec hash"
        <| async {
            let store = freshStore ()
            let ts = InMemoryTimeSeriesStore.create ()
            let executor = DatasetAssemblyExecutor.createWithTimeSeries store ts

            // Seed the entity table + two daily series (one per unit).
            let! _ = store.Create("s", "units", unitsSchema, unitRows, "u", Map.empty, StrictlyVersioned)

            do!
                ts.Append("s", "sales:u1", [ for d in 1..4 -> { Timestamp = t d; Value = float d } ])
                |> Async.Ignore

            let unitsRef: DatasetVersionRef = {
                ScopeId = "s"
                DatasetId = "units"
                Version = 1
            }

            let spec: DatasetAssemblySpec = {
                Scope = "s"
                Base =
                    AssemblySource.TimeSeriesRange {
                        ScopeId = "s"
                        Series = "sales:u1"
                        From = t 1
                        Until = t 5
                        Downsample = None
                        UnitLabel = "u1"
                        UnitColumn = "unit"
                        PeriodColumn = "period"
                        ValueColumn = "sales"
                    }
                Transforms = [
                    // Join the region attribute on the unit key.
                    AssemblyTransform.Join(
                        AssemblySource.DatasetVersion unitsRef,
                        [ "unit" ],
                        [ "unit" ],
                        AssemblyJoinKind.LeftOuter
                    )
                    // Lag sales by one period within the unit.
                    AssemblyTransform.Lag("sales", 1, [ "unit" ], "period", "sales_lag1")
                    // Keep only rows where the lag is present (drops the first).
                    AssemblyTransform.Filter [
                        {
                            Column = "sales_lag1"
                            Op = DatasetFilterOp.Ne
                            Value = DatasetValue.Null
                        }
                    ]
                ]
                Split = Some(AssemblySplit.ByPeriodCutoff("period", t 4, "train", "holdout"))
                OutputDatasetId = "assembled"
                OutputRoles = [ "sales", DatasetColumnRole.Target ]
                Policy = Versioned
            }

            let! result = executor.Assemble(spec, "assembler")
            let produced = okv result

            Expect.containsAll (produced |> Map.toList |> List.map fst) [ "train"; "holdout" ] "both subsets produced"

            // Provenance names every source identity + the spec hash.
            let trainRef = produced["train"]
            let! trainVer = store.GetVersion(trainRef.ScopeId, trainRef.DatasetId, trainRef.Version)
            let tv = okds trainVer
            let sources = tv.Metadata[AssemblyProvenance.SourcesKey]
            Expect.stringContains sources "timeseries:s/sales:u1" "provenance names the time-series source"
            Expect.stringContains sources "dataset:s/units@v1" "provenance names the joined dataset source"

            Expect.equal
                tv.Metadata[AssemblyProvenance.SpecHashKey]
                (AssemblyProvenance.specHash spec)
                "the recorded spec hash matches the spec"

            // The lag filter dropped the first period; region joined in.
            let! trainPage = readAll store trainRef
            let regionIdx = DatasetSchema.indexOf "region" trainPage.Schema |> Option.get
            let lagIdx = DatasetSchema.indexOf "sales_lag1" trainPage.Schema |> Option.get

            Expect.isTrue
                (trainPage.Rows
                 |> List.forall (fun r -> List.item lagIdx r.Cells <> DatasetValue.Null))
                "every train row has a present lag (the leading null was filtered)"

            Expect.isTrue
                (trainPage.Rows
                 |> List.forall (fun r -> List.item regionIdx r.Cells = txt "north"))
                "region joined onto u1 rows"

            // Replay: append a new point, re-run the SAME spec -> a new vintage
            // (version 2) with an UNCHANGED spec hash.
            do! ts.Append("s", "sales:u1", [ { Timestamp = t 5; Value = 5.0 } ]) |> Async.Ignore
            let! replay = executor.Assemble(spec, "assembler")
            let replayed = okv replay
            let replayTrain = replayed["train"]
            Expect.equal replayTrain.Version 2 "replay produced a new immutable vintage"

            let! replayVer = store.GetVersion(replayTrain.ScopeId, replayTrain.DatasetId, replayTrain.Version)

            Expect.equal
                (okds replayVer).Metadata[AssemblyProvenance.SpecHashKey]
                (AssemblyProvenance.specHash spec)
                "replay's spec hash is unchanged -- only the sources moved"
        }

        testCaseAsync "resample folds each aggregate correctly over the bucket"
        <| async {
            let store = freshStore ()
            let tsStore = InMemoryTimeSeriesStore.create ()
            let executor = DatasetAssemblyExecutor.createWithTimeSeries store tsStore

            // Four daily points; resample to a 2-day bucket -> two buckets.
            do!
                tsStore.Append(
                    "s",
                    "m:u1",
                    [
                        for d in 1..4 ->
                            {
                                Timestamp = t d
                                Value = float (d * 10)
                            }
                    ]
                )
                |> Async.Ignore

            let spec: DatasetAssemblySpec = {
                Scope = "s"
                Base =
                    AssemblySource.TimeSeriesRange {
                        ScopeId = "s"
                        Series = "m:u1"
                        From = t 1
                        Until = t 5
                        Downsample = None
                        UnitLabel = "u1"
                        UnitColumn = "unit"
                        PeriodColumn = "period"
                        ValueColumn = "v"
                    }
                Transforms = [
                    AssemblyTransform.Resample(
                        "period",
                        TimeSpan.FromDays 2.0,
                        [ "unit" ],
                        [
                            {
                                Column = "v"
                                Aggregation = TimeSeriesAggregation.Sum
                                As = "v_sum"
                            }
                            {
                                Column = "v"
                                Aggregation = TimeSeriesAggregation.Count
                                As = "v_n"
                            }
                        ]
                    )
                ]
                Split = None
                OutputDatasetId = "resampled"
                OutputRoles = []
                Policy = Versioned
            }

            let! result = executor.Assemble(spec, "u")
            let ref = (okv result)["all"]
            let! page = readAll store ref

            let sumIdx = DatasetSchema.indexOf "v_sum" page.Schema |> Option.get
            let nIdx = DatasetSchema.indexOf "v_n" page.Schema |> Option.get

            let sums = page.Rows |> List.map (fun r -> List.item sumIdx r.Cells)
            let counts = page.Rows |> List.map (fun r -> List.item nIdx r.Cells)

            // Bucket 1 = days 1,2 -> 10+20=30; bucket 2 = days 3,4 -> 30+40=70.
            Expect.equal sums [ f 30.0; f 70.0 ] "sum per 2-day bucket"
            Expect.equal counts [ DatasetValue.Int 2L; DatasetValue.Int 2L ] "count per bucket"
        }

        testCaseAsync "join key mismatch is refused"
        <| async {
            let store = freshStore ()
            let executor = DatasetAssemblyExecutor.create store

            let! _ = store.Create("s", "left", unitsSchema, unitRows, "u", Map.empty, Versioned)

            let leftRef: DatasetVersionRef = {
                ScopeId = "s"
                DatasetId = "left"
                Version = 1
            }

            let spec: DatasetAssemblySpec = {
                Scope = "s"
                Base = AssemblySource.DatasetVersion leftRef
                Transforms = [
                    AssemblyTransform.Join(
                        AssemblySource.DatasetVersion leftRef,
                        [ "no_such_column" ],
                        [ "unit" ],
                        AssemblyJoinKind.Inner
                    )
                ]
                Split = None
                OutputDatasetId = "out"
                OutputRoles = []
                Policy = Versioned
            }

            match! executor.Assemble(spec, "u") with
            | Error(AssemblyError.UnknownColumn _) -> ()
            | other -> failtestf "expected UnknownColumn refusal; got %A" other
        }

        testCaseAsync "lag emits typed leading nulls, never silent zeros"
        <| async {
            let store = freshStore ()
            let tsStore = InMemoryTimeSeriesStore.create ()
            let executor = DatasetAssemblyExecutor.createWithTimeSeries store tsStore

            do!
                tsStore.Append("s", "x:u1", [ for d in 1..3 -> { Timestamp = t d; Value = float d } ])
                |> Async.Ignore

            let spec: DatasetAssemblySpec = {
                Scope = "s"
                Base =
                    AssemblySource.TimeSeriesRange {
                        ScopeId = "s"
                        Series = "x:u1"
                        From = t 1
                        Until = t 4
                        Downsample = None
                        UnitLabel = "u1"
                        UnitColumn = "unit"
                        PeriodColumn = "period"
                        ValueColumn = "x"
                    }
                Transforms = [ AssemblyTransform.Lag("x", 1, [ "unit" ], "period", "x_lag") ]
                Split = None
                OutputDatasetId = "lagged"
                OutputRoles = []
                Policy = Versioned
            }

            let! result = executor.Assemble(spec, "u")
            let! page = readAll store ((okv result)["all"])
            let lagIdx = DatasetSchema.indexOf "x_lag" page.Schema |> Option.get
            let lags = page.Rows |> List.map (fun r -> List.item lagIdx r.Cells)

            Expect.equal
                lags
                [ DatasetValue.Null; f 1.0; f 2.0 ]
                "leading null is a typed Null, then the shifted values"
        }

        testCaseAsync "unit-hash split is deterministic across replays"
        <| async {
            let store = freshStore ()
            let executor = DatasetAssemblyExecutor.create store

            let schema: DatasetSchema = {
                Columns = [
                    col "unit" DatasetDType.Categorical DatasetColumnRole.PanelUnit
                    col "v" DatasetDType.Float DatasetColumnRole.Plain
                ]
            }

            let rows = [ for i in 1..20 -> { Cells = [ cat $"u{i}"; f (float i) ] } ]
            let! _ = store.Create("s", "pool", schema, rows, "u", Map.empty, Versioned)

            let poolRef: DatasetVersionRef = {
                ScopeId = "s"
                DatasetId = "pool"
                Version = 1
            }

            let spec: DatasetAssemblySpec = {
                Scope = "s"
                Base = AssemblySource.DatasetVersion poolRef
                Transforms = []
                Split = Some(AssemblySplit.ByUnitHash("unit", [ "train", 8; "test", 2 ]))
                OutputDatasetId = "hashsplit"
                OutputRoles = []
                Policy = Versioned
            }

            let unitsOf (page: DatasetPage) =
                let idx = DatasetSchema.indexOf "unit" page.Schema |> Option.get
                page.Rows |> List.map (fun r -> List.item idx r.Cells) |> Set.ofList

            let! r1 = executor.Assemble(spec, "u")
            let! r2 = executor.Assemble(spec, "u")
            let m1 = okv r1
            let m2 = okv r2

            let! train1 = readAll store m1["train"]
            let! train2 = readAll store m2["train"]
            let! test1 = readAll store m1["test"]

            Expect.equal (unitsOf train1) (unitsOf train2) "the same units land in 'train' on every replay"
            // Partition is exhaustive + disjoint.
            Expect.isTrue (Set.intersect (unitsOf train1) (unitsOf test1) |> Set.isEmpty) "train and test are disjoint"

            Expect.equal
                (Set.union (unitsOf train1) (unitsOf test1) |> Set.count)
                20
                "every unit landed in exactly one subset"
        }
    ]
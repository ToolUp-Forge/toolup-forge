// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.VirtualDatasetTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra.DataObjectStore
open ToolUp.Platform.Infra
open ToolUp.Platform.Testing

// --- Phase 487 -- virtual (zero-copy) dataset bindings ------------------
//
// A Virtual dataset version reads through to the deployment's own store at
// page time -- no durable copy; its vintage is a source watermark. These
// tests exercise bind/read round-trips over an in-memory IDataSource,
// watermark-pinned re-reads, schema-drift refusal, and the audited
// ephemeral-spill lifecycle for compute handoff.

let private schema: DatasetSchema = {
    Columns = [
        {
            Name = "unit"
            DType = DatasetDType.Categorical
            Nullable = false
            Role = DatasetColumnRole.PanelUnit
        }
        {
            Name = "value"
            DType = DatasetDType.Float
            Nullable = false
            Role = DatasetColumnRole.Plain
        }
    ]
}

let private mkRow (i: int) : DatasetRow = {
    Cells = [ DatasetValue.Categorical $"u{i}"; DatasetValue.Float(float i) ]
}

let private sixRows = List.init 6 mkRow

let private codec = JsonFrameDatasetCodec() :> IDatasetCodec

/// An in-memory IDataSource keyed by query string -> a frame. A query with
/// no frame errors (source unreachable) -- lets a test drive the not-found
/// and drift paths.
type private InMemorySource(frames: Map<string, DatasetSchema * DatasetRow list>, ?refuseConnect: bool) =
    let refuse = defaultArg refuseConnect false

    interface IDataSource with
        member _.Kind = "InMemory"
        member _.Capabilities = ConnectorCapabilities.none

        member _.Connect _ = async {
            return
                if refuse then
                    Error(IngestionError.SourceUnreachable "refused (test double)")
                else
                    Ok()
        }

        member _.ListTables _ = async { return Ok [] }
        member _.GetSchema(_, _) = async { return Ok { TableName = ""; Columns = [] } }

        member _.Query(_, sql) = async {
            match Map.tryFind sql frames with
            | Some(s, rows) -> return Ok(codec.Encode(s, rows))
            | None -> return Error(IngestionError.SourceUnreachable(sprintf "no frame for query '%s'" sql))
        }

let private config: DataSourceConfig = {
    Id = "insitu"
    Name = "in-situ store"
    Kind = "InMemory"
    ConnectionScope = Map.empty
    CredentialKey = ""
    Tables = None
    Tags = Map.empty
}

let private binding: VirtualBinding = {
    SourceRef = "InMemory"
    QuerySpec = "SELECT * FROM units"
    Watermark = "snap-1"
    Fidelity = SnapshotFidelity.Exact
}

/// A source that records every call it receives. Given `binds`, it declares
/// the binding capability over those kinds and answers a bound statement
/// with the frame its text names; without it, it binds nothing.
type private RecordingSource(frames: Map<string, DatasetSchema * DatasetRow list>, binds: Set<DatasetDType> option) =
    let calls = ResizeArray<string>()
    let statements = ResizeArray<BoundStatement>()

    let frame (query: string) =
        match Map.tryFind query frames with
        | Some(s, rows) -> Ok(codec.Encode(s, rows))
        | None -> Error(IngestionError.SourceUnreachable(sprintf "no frame for query '%s'" query))

    let capabilities =
        match binds with
        | None -> ConnectorCapabilities.none
        | Some kinds -> {
            ConnectorCapabilities.none with
                Binds =
                    Some
                        { new IBindsStatements with
                            member _.BindableKinds = kinds

                            member _.QueryBound(_, statement) = async {
                                calls.Add "QueryBound"
                                statements.Add statement
                                return frame statement.Text |> Result.mapError BoundStatementError.Failed
                            }
                        }
          }

    /// Every call, in order: `Connect`, `Query`, `QueryBound`.
    member _.Calls = List.ofSeq calls

    /// Every bound statement the connector was asked to run.
    member _.Statements = List.ofSeq statements

    interface IDataSource with
        member _.Kind = "Recording"
        member _.Capabilities = capabilities

        member _.Connect _ = async {
            calls.Add "Connect"
            return Ok()
        }

        member _.ListTables _ = async { return Ok [] }
        member _.GetSchema(_, _) = async { return Ok { TableName = ""; Columns = [] } }

        member _.Query(_, sql) = async {
            calls.Add "Query"
            return frame sql
        }

let private allKinds = Set.ofList BoundValue.kinds

let private asOf = DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)

/// The shared statement: one value that does not vary with the vintage, and
/// the watermark named where the text depends on it.
let private shared: BoundStatement =
    BoundStatement.ofText "SELECT * FROM units WHERE region = @region AND as_of <= @watermark"
    |> BoundStatement.bind "region" (BoundValue.Text "EMEA-secret")

let private boundAt (watermark: BoundValue) : BoundVirtualBinding = {
    SourceRef = "Recording"
    Statement = shared
    Watermark = watermark
    Fidelity = SnapshotFidelity.Approximate
}

let private bound = boundAt (BoundValue.Timestamp asOf)

let private boundOnly: DataSourceConfig = {
    config with
        ConnectionScope = Map [ StatementPolicy.Key, "bound-only" ]
}

let private recordingOver (cfg: DataSourceConfig) (binds: Set<DatasetDType> option) =
    let src =
        RecordingSource(Map [ shared.Text, (schema, sixRows); binding.QuerySpec, (schema, sixRows) ], binds)

    src, VirtualDatasetReader(src, codec, schema, cfg)

let private recording = recordingOver config

let private refusalOf =
    function
    | Error(r: VirtualBindingRefusal) -> r
    | Ok() -> failtest "expected a definition refusal"

let private okv =
    function
    | Ok v -> v
    | Error(e: DatasetError) -> failtestf "expected Ok; got %s" (DatasetError.describe e)

let private freshDataObjects (dir: TempDir) =
    let blob = LocalFileStorage.LocalFileStorage(dir.Path) :> IBlobStorage
    DataObjectStore(blob) :> IDataObjectStore

let private reader (frames: Map<string, DatasetSchema * DatasetRow list>) =
    VirtualDatasetReader(InMemorySource frames, codec, schema, config)

let tests =
    testList "VirtualDataset" [
        testCaseAsync "binds + pages typed rows straight from the source with no durable copy"
        <| async {
            let r = reader (Map [ binding.QuerySpec, (schema, sixRows) ])

            match! r.Bind("s", binding) with
            | Ok() -> ()
            | Error e -> failtestf "bind failed: %s" (DatasetError.describe e)

            let! page = r.ReadPage("s", binding, { Offset = 2L; Limit = 2; Filters = [] })
            let p = okv page
            Expect.equal p.Rows [ mkRow 2; mkRow 3 ] "the offset window comes back typed"
            Expect.equal p.TotalRows 6L "total reflects the read-through row count"
            Expect.equal p.Schema schema "the page carries the declared schema"
        }

        testCaseAsync "a filtered read matches the materialised store's filter semantics"
        <| async {
            let r = reader (Map [ binding.QuerySpec, (schema, sixRows) ])

            let! page =
                r.ReadPage(
                    "s",
                    binding,
                    {
                        Offset = 0L
                        Limit = 100
                        Filters = [
                            {
                                Column = "value"
                                Op = DatasetFilterOp.Gte
                                Value = DatasetValue.Float 4.0
                            }
                        ]
                    }
                )

            let p = okv page
            Expect.equal p.Rows [ mkRow 4; mkRow 5 ] "only rows matching the filter"
            Expect.equal p.TotalRows 2L "total is the filtered count"
        }

        testCaseAsync "the same watermark re-reads identical rows (Exact fidelity)"
        <| async {
            let r = reader (Map [ binding.QuerySpec, (schema, sixRows) ])
            let! first = r.ReadPage("s", binding, DatasetPageQuery.firstPage 100)
            let! second = r.ReadPage("s", binding, DatasetPageQuery.firstPage 100)
            Expect.equal (okv first).Rows (okv second).Rows "a watermark-pinned re-read is reproducible"
        }

        testCaseAsync "source schema drift is a typed error naming the changed column"
        <| async {
            // The source now returns 'value' as Text instead of Float.
            let driftedSchema: DatasetSchema = {
                Columns = [
                    {
                        Name = "unit"
                        DType = DatasetDType.Categorical
                        Nullable = false
                        Role = DatasetColumnRole.PanelUnit
                    }
                    {
                        Name = "value"
                        DType = DatasetDType.Text
                        Nullable = false
                        Role = DatasetColumnRole.Plain
                    }
                ]
            }

            let driftedRows = [
                {
                    Cells = [ DatasetValue.Categorical "u0"; DatasetValue.Text "oops" ]
                }
            ]

            let r = reader (Map [ binding.QuerySpec, (driftedSchema, driftedRows) ])

            match! r.ReadPage("s", binding, DatasetPageQuery.firstPage 10) with
            | Error(DatasetError.SchemaMismatch reason) ->
                Expect.stringContains reason "value" "the error names the changed column"
            | other -> failtestf "expected SchemaMismatch naming 'value'; got %A" other
        }

        testCaseAsync "compute-handoff spill is created, referenceable, TTL-deleted, and both steps audited"
        <| async {
            let r = reader (Map [ binding.QuerySpec, (schema, sixRows) ])
            use dir = TempDir.create "virtual-test"
            let dataObjects = freshDataObjects dir
            let audit = TestAuditLog()

            let labels = [ DataProvenanceLabel.Classified "restricted" ]

            let! spill =
                r.MaterialiseForHandoff(
                    "s",
                    binding,
                    dataObjects,
                    "spill-1",
                    TimeSpan.FromHours 1.0,
                    labels,
                    audit,
                    "worker"
                )

            let ref = okv spill
            Expect.equal ref.RowCount 6L "the spill carries every read-through row"
            Expect.equal ref.Format codec.Format "the handoff ref names the spill's wire format"

            // The spilled bytes are fetchable by the ref (compute handoff).
            let! content = dataObjects.GetContent("s", ref.ContentHash)

            match content with
            | Ok bytes ->
                match codec.Decode bytes with
                | Ok(_, rows) -> Expect.equal (List.length rows) 6 "the spill blob round-trips the rows"
                | Error e -> failtestf "spill decode failed: %s" e
            | Error e -> failtestf "spill content unreadable: %A" e

            // TTL delete closes the lifecycle.
            match! r.DeleteSpill("s", "spill-1", dataObjects, audit, "sweeper", "ttl-expired") with
            | Ok() -> ()
            | Error e -> failtestf "spill delete failed: %s" (DatasetError.describe e)

            let created =
                audit.Events
                |> List.choose (fun e ->
                    match e with
                    | DatasetSpillCreated p -> Some p
                    | _ -> None)

            let deleted =
                audit.Events
                |> List.choose (fun e ->
                    match e with
                    | DatasetSpillDeleted p -> Some p
                    | _ -> None)

            Expect.equal created.Length 1 "one spill-created audit row"
            Expect.equal deleted.Length 1 "one spill-deleted audit row"
            Expect.equal created.Head.Watermark "snap-1" "the created row records the watermark"
            Expect.equal deleted.Head.Reason "ttl-expired" "the deleted row records the reason"
        }

        testCaseAsync "an unreachable source fails bind, not silently"
        <| async {
            let src =
                InMemorySource(Map [ binding.QuerySpec, (schema, sixRows) ], refuseConnect = true)

            let r = VirtualDatasetReader(src, codec, schema, config)

            match! r.Bind("s", binding) with
            | Error(DatasetError.StorageFailure _) -> ()
            | other -> failtestf "expected a bind failure; got %A" other
        }

        testCaseAsync "a bound binding reads through the binding capability, the watermark a parameter"
        <| async {
            let src, r = recording (Some allKinds)

            match! r.Bind("s", bound) with
            | Ok() -> ()
            | Error e -> failtestf "bind failed: %s" (DatasetError.describe e)

            let! page = r.ReadPage("s", bound, { Offset = 2L; Limit = 2; Filters = [] })
            Expect.equal (okv page).Rows [ mkRow 2; mkRow 3 ] "the bound read pages typed rows"
            Expect.equal src.Calls [ "Connect"; "QueryBound"; "QueryBound" ] "never through Query"

            let expected: BoundParameter list = [
                {
                    Name = "region"
                    Value = BoundValue.Text "EMEA-secret"
                }
                {
                    Name = BoundVirtualBinding.WatermarkParameter
                    Value = BoundValue.Timestamp asOf
                }
            ]

            Expect.equal
                (src.Statements |> List.map _.Parameters)
                [ expected; expected ]
                "the watermark is bound after the shared parameters, with its own kind"
        }

        testCaseAsync "a string binding over a connector that binds still reads through Query"
        <| async {
            let src, r = recording (Some allKinds)
            let! page = r.ReadPage("s", binding, DatasetPageQuery.firstPage 100)
            Expect.equal (okv page).TotalRows 6L "every row"
            Expect.equal src.Calls [ "Query" ] "the string form is today's read"
        }

        testCaseAsync "a bound binding over a connector that does not bind is refused at definition, nothing sent"
        <| async {
            let src, r = recording None

            Expect.equal
                (refusalOf (r.Define bound))
                (VirtualBindingRefusal.NotDeclared {
                    Kind = "Recording"
                    Capability = ConnectorCapability.Binds
                })
                "the named capability refusal"

            match! r.Bind("s", bound) with
            | Error(DatasetError.SchemaMismatch reason) -> Expect.stringContains reason "Binds" "names the capability"
            | other -> failtestf "expected the definition refusal; got %A" other

            Expect.isEmpty src.Calls "nothing reached the source"
        }

        test "the watermark is bound with its own kind, which the connector must declare" {
            let _, textOnly = recording (Some(Set.ofList [ DatasetDType.Text ]))

            Expect.equal
                (refusalOf (textOnly.Define bound))
                (VirtualBindingRefusal.Refused(
                    BindingRefusal.UnbindableKind(BoundVirtualBinding.WatermarkParameter, DatasetDType.Timestamp)
                ))
                "a timestamp watermark over a connector binding text only"

            let _, withInts =
                recording (Some(Set.ofList [ DatasetDType.Text; DatasetDType.Int ]))

            Expect.isOk (withInts.Define(boundAt (BoundValue.Int 42L))) "an integer snapshot id binds as Int"
        }

        test "a statement that does not name the watermark runs as it is" {
            let whole = {
                bound with
                    Statement = BoundStatement.ofText "SELECT * FROM units"
            }

            Expect.equal (BoundVirtualBinding.statement whole) whole.Statement "no watermark parameter is added"

            let longer = {
                bound with
                    Statement = BoundStatement.ofText "SELECT * FROM units WHERE w = @watermarks"
            }

            Expect.isEmpty
                (BoundVirtualBinding.statement longer).Parameters
                "a longer name that starts with the reserved one is not its marker"
        }

        test "the reserved name and a null watermark are refused at definition" {
            let _, r = recording (Some allKinds)

            let clash = {
                bound with
                    Statement =
                        shared
                        |> BoundStatement.bind BoundVirtualBinding.WatermarkParameter (BoundValue.Text "x")
            }

            Expect.equal (refusalOf (r.Define clash)) VirtualBindingRefusal.ReservedParameter "reserved"

            Expect.equal
                (refusalOf (r.Define(boundAt (BoundValue.Null DatasetDType.Timestamp))))
                VirtualBindingRefusal.NullWatermark
                "a vintage has a value"
        }

        test "two versions at two watermarks carry one statement text and differ in one parameter" {
            let first = BoundVirtualBinding.statement bound

            let second =
                BoundVirtualBinding.statement (boundAt (BoundValue.Timestamp(asOf.AddDays 1.0)))

            Expect.equal first.Text second.Text "one statement text"

            let differing =
                List.zip first.Parameters second.Parameters
                |> List.filter (fun (a, b) -> a <> b)
                |> List.map (fun (a, _) -> a.Name)

            Expect.equal differing [ BoundVirtualBinding.WatermarkParameter ] "only the watermark differs"
        }

        testCaseAsync "a bound spill audits the watermark as before and carries no other parameter value"
        <| async {
            let _, r = recording (Some allKinds)
            use dir = TempDir.create "virtual-bound-test"
            let dataObjects = freshDataObjects dir
            let audit = TestAuditLog()

            let! spill =
                r.MaterialiseForHandoff("s", bound, dataObjects, "spill-b", TimeSpan.FromHours 1.0, [], audit, "worker")

            Expect.equal (okv spill).RowCount 6L "the spill carries every row"

            let created =
                audit.Events
                |> List.choose (fun e ->
                    match e with
                    | DatasetSpillCreated p -> Some p
                    | _ -> None)

            Expect.equal created.Length 1 "one spill-created audit row, as for a string binding"

            Expect.equal
                created.Head.Watermark
                (asOf.ToString("o", Globalization.CultureInfo.InvariantCulture))
                "the watermark, as its text"

            Expect.isFalse
                ((sprintf "%A" created.Head).Contains "EMEA-secret")
                "no shared parameter value reaches the audit payload"
        }

        testCaseAsync "under a bound-only policy a string binding is refused when it is defined, naming the policy"
        <| async {
            let src, r = recordingOver boundOnly (Some allKinds)

            Expect.equal
                (refusalOf (r.Define binding))
                (VirtualBindingRefusal.StatementTextRefused "insitu")
                "the string form is refused by the source's policy"

            match! r.Bind("s", binding) with
            | Error(DatasetError.SchemaMismatch reason) ->
                Expect.stringContains reason "statement_policy=bound-only" "names the policy"
            | other -> failtestf "expected the policy refusal; got %A" other

            match! r.ReadPage("s", binding, DatasetPageQuery.firstPage 10) with
            | Error(DatasetError.SchemaMismatch _) -> ()
            | other -> failtestf "expected the policy refusal at page time too; got %A" other

            Expect.isEmpty src.Calls "nothing reached the source"
            Expect.isOk (r.Define bound) "the bound form is what a bound-only source admits"
        }

        test "an unreadable statement policy refuses a string binding by name" {
            let _, r =
                recordingOver
                    {
                        config with
                            ConnectionScope = Map [ StatementPolicy.Key, "bound" ]
                    }
                    None

            match refusalOf (r.Define binding) with
            | VirtualBindingRefusal.PolicyUnreadable reason ->
                Expect.stringContains reason StatementPolicy.Key "names the key"
            | other -> failtestf "expected PolicyUnreadable; got %A" other
        }
    ]
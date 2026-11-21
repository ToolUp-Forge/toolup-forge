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

/// The subject an audited reader reads for.
let private queryReader = AuditSubject.UserAudit(UserId "analyst-7")

/// Every `DataSourceQueried` row a log holds, with its actor and scope.
let private queriedRows (audit: TestAuditLog) =
    audit.Records
    |> List.choose (fun r ->
        DataSourceQueryAudit.tryRead r.Event
        |> Option.map (fun p -> p, r.Actor, r.Scope))

/// An event's payload as stored: a registrant kind's bytes, or a platform
/// case's serialisation and its printed form.
let private payloadText (event: AuditEvent) : string =
    match event with
    | Opaque o -> o.Payload
    | platform ->
        Text.Json.JsonSerializer.Serialize(platform, ToolUp.Remoting.Json.SystemTextJson.PlatformJson.options)
        + sprintf "%A" platform

/// A statement binding a value of every kind, each value distinctive enough
/// to be found in a payload by its text.
let private everyKind: BoundStatement =
    BoundStatement.ofText
        "SELECT * FROM units WHERE region = @region AND n < @cap AND r > @rate AND flag = @flag AND seen > @seen AND code = @code AND amount = @amount AND as_of <= @watermark"
    |> BoundStatement.bind "region" (BoundValue.Text "EMEA-secret")
    |> BoundStatement.bind "cap" (BoundValue.Int 918273645L)
    |> BoundStatement.bind "rate" (BoundValue.Float 2.718281828)
    |> BoundStatement.bind "flag" (BoundValue.Bool true)
    |> BoundStatement.bind "seen" (BoundValue.Timestamp(DateTimeOffset(2029, 3, 14, 15, 9, 26, TimeSpan.Zero)))
    |> BoundStatement.bind "code" (BoundValue.Categorical "cat-zeta-42")
    |> BoundStatement.bind "amount" (BoundValue.Decimal "31415.9265")

/// The text forms the shared values of `everyKind` could take in a payload.
let private sharedRenderings = [
    "EMEA-secret"
    "918273645"
    "2.718281828"
    "2029-03-14"
    "15:09:26"
    "cat-zeta-42"
    "31415.9265"
]

let private sweepInstant = DateTimeOffset(2031, 7, 19, 13, 47, 29, TimeSpan.Zero)

/// The text forms the sweep's watermark values could take in a payload.
let private watermarkRenderings = [ "2031-07-19"; "13:47:29"; "wm-quartz-5521" ]

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
        // --- the data-source query audit event --------------------------
        //
        // Every statement the reader runs against its source, or refuses to
        // run, is one `DataSourceQueried` row in the reader's log, attributed
        // to its subject. The statement is identified by a hash over its text
        // alone, and no parameter value reaches any payload.
        testList "the data-source query audit event" [
            testCaseAsync "a string read records one row per query: the source, the text's hash, and how much came back"
            <| async {
                let audit = TestAuditLog()
                let src = RecordingSource(Map [ binding.QuerySpec, (schema, sixRows) ], None)
                let r = VirtualDatasetReader(src, codec, schema, config, audit, queryReader)

                okv (Async.RunSynchronously(r.Bind("s", binding)))

                okv (Async.RunSynchronously(r.ReadPage("s", binding, DatasetPageQuery.firstPage 10)))
                |> ignore

                let rows = queriedRows audit
                Expect.equal rows.Length 2 "one row for the bind's read and one for the page's"

                let row, actor, scope = rows.Head
                Expect.equal actor queryReader "attributed to the reader's subject"
                Expect.equal scope (AuditScope.shard "s") "under the scope the read ran in"
                Expect.equal row.SourceId "insitu" "names the source"
                Expect.equal row.ConnectorKind "Recording" "names the connector"
                Expect.equal row.Form DataSourceStatementForm.Text "the text form"
                Expect.equal row.StatementHash (Sha256.hexOfUtf8 binding.QuerySpec) "the hash of the text"
                Expect.isEmpty row.Parameters "a string statement names no parameter"
                Expect.isNone row.StatementText "the text is not recorded by default"
                Expect.equal row.RowCount (Some 6L) "the rows decoded"
                let answer = codec.Encode(schema, sixRows)
                Expect.equal row.ByteCount (Some(int64 answer.Length)) "the answer's size"
                Expect.equal row.ContentHash (Some(Sha256.hex answer)) "the answer's hash"
                Expect.equal row.Outcome DataSourceQueryOutcome.Succeeded "succeeded"
            }

            testCaseAsync
                "two runs of one bound statement at different values share a hash and name the same parameters"
            <| async {
                let audit = TestAuditLog()
                let src = RecordingSource(Map [ shared.Text, (schema, sixRows) ], Some allKinds)
                let r = VirtualDatasetReader(src, codec, schema, config, audit, queryReader)
                let later = boundAt (BoundValue.Timestamp(asOf.AddDays 7.0))

                let! _ = r.ReadPage("s", bound, DatasetPageQuery.firstPage 10)
                let! _ = r.ReadPage("s", later, DatasetPageQuery.firstPage 10)

                match queriedRows audit |> List.map (fun (row, _, _) -> row) with
                | [ first; second ] ->
                    Expect.equal first.Form DataSourceStatementForm.Bound "the bound form"
                    Expect.equal first.StatementHash second.StatementHash "one question asked twice"
                    Expect.equal first.StatementHash (Sha256.hexOfUtf8 shared.Text) "the hash is over the text alone"

                    Expect.equal
                        first.Parameters
                        [
                            {
                                Name = "region"
                                Kind = DatasetDType.name DatasetDType.Text
                            }
                            {
                                Name = BoundVirtualBinding.WatermarkParameter
                                Kind = DatasetDType.name DatasetDType.Timestamp
                            }
                        ]
                        "each parameter by name and kind, the watermark among them"

                    Expect.equal second.Parameters first.Parameters "the same names and kinds"
                | other -> failtestf "expected two rows; got %A" other
            }

            testCaseAsync "a failed query is recorded by its error's class, never its message"
            <| async {
                let audit = TestAuditLog()
                let src = RecordingSource(Map.empty, None)
                let r = VirtualDatasetReader(src, codec, schema, config, audit, queryReader)

                match! r.ReadPage("s", binding, DatasetPageQuery.firstPage 10) with
                | Error(DatasetError.StorageFailure _) -> ()
                | other -> failtestf "expected the source failure; got %A" other

                match queriedRows audit with
                | [ row, _, _ ] ->
                    Expect.equal row.Outcome (DataSourceQueryOutcome.Failed "SourceUnreachable") "the class"
                    Expect.isNone row.ByteCount "no answer came back"
                    Expect.isNone row.RowCount "no rows"
                | other -> failtestf "expected one row; got %A" other
            }

            testCaseAsync "source drift is a failed query that still records what came back"
            <| async {
                let audit = TestAuditLog()
                let drifted = { Columns = [ schema.Columns.Head ] }

                let src =
                    RecordingSource(
                        Map [
                            binding.QuerySpec,
                            (drifted,
                             [
                                 {
                                     Cells = [ DatasetValue.Categorical "u0" ]
                                 }
                             ])
                        ],
                        None
                    )

                let r = VirtualDatasetReader(src, codec, schema, config, audit, queryReader)
                let! _ = r.ReadPage("s", binding, DatasetPageQuery.firstPage 10)

                match queriedRows audit with
                | [ row, _, _ ] ->
                    Expect.equal row.Outcome (DataSourceQueryOutcome.Failed "SchemaDrift") "drift"
                    Expect.isSome row.ByteCount "the answer's size"
                    Expect.isNone row.RowCount "no rows accepted"
                | other -> failtestf "expected one row; got %A" other
            }

            testCaseAsync "a statement refused before it is sent is recorded as refused, and nothing reaches the source"
            <| async {
                let audit = TestAuditLog()

                let src =
                    RecordingSource(Map [ binding.QuerySpec, (schema, sixRows) ], Some allKinds)

                let r = VirtualDatasetReader(src, codec, schema, boundOnly, audit, queryReader)

                let! _ = r.ReadPage("s", binding, DatasetPageQuery.firstPage 10)
                Expect.isEmpty src.Calls "nothing was sent"

                match queriedRows audit with
                | [ row, _, _ ] ->
                    Expect.equal row.Outcome (DataSourceQueryOutcome.Refused "StatementTextRefused") "refused by policy"
                    Expect.equal row.StatementHash (Sha256.hexOfUtf8 binding.QuerySpec) "the refused text's hash"
                | other -> failtestf "expected one row; got %A" other
            }

            testCaseAsync "recording statement text is refused for a source whose policy admits raw text"
            <| async {
                let audit = TestAuditLog()

                let optedIn = {
                    config with
                        ConnectionScope = Map [ StatementTextAudit.Key, "true" ]
                }

                let src =
                    RecordingSource(Map [ binding.QuerySpec, (schema, sixRows) ], Some allKinds)

                let r = VirtualDatasetReader(src, codec, schema, optedIn, audit, queryReader)

                match refusalOf (r.Define binding) with
                | VirtualBindingRefusal.StatementTextAuditRefused reason ->
                    Expect.stringContains reason StatementTextAudit.Key "names the opt-in"
                    Expect.stringContains reason StatementPolicy.Key "names the policy"
                | other -> failtestf "expected StatementTextAuditRefused; got %A" other

                Expect.isError (r.Define bound) "the bound form is refused under the same source"

                match! r.ReadPage("s", binding, DatasetPageQuery.firstPage 10) with
                | Error(DatasetError.SchemaMismatch _) -> ()
                | other -> failtestf "expected the refusal at page time; got %A" other

                Expect.isEmpty src.Calls "nothing was sent"

                match queriedRows audit with
                | [ row, _, _ ] ->
                    Expect.equal
                        row.Outcome
                        (DataSourceQueryOutcome.Refused DataSourceQueryAudit.StatementTextAuditRefused)
                        "refused"

                    Expect.isNone row.StatementText "a refused opt-in records no text"
                | other -> failtestf "expected one row; got %A" other
            }

            testCaseAsync "a bound-only source that opts in records its bound statement's text, and no value"
            <| async {
                let audit = TestAuditLog()

                let optedIn = {
                    boundOnly with
                        ConnectionScope = boundOnly.ConnectionScope |> Map.add StatementTextAudit.Key "TRUE"
                }

                let src = RecordingSource(Map [ shared.Text, (schema, sixRows) ], Some allKinds)
                let r = VirtualDatasetReader(src, codec, schema, optedIn, audit, queryReader)

                okv (Async.RunSynchronously(r.ReadPage("s", bound, DatasetPageQuery.firstPage 10)))
                |> ignore

                match queriedRows audit with
                | [ row, _, _ ] -> Expect.equal row.StatementText (Some shared.Text) "the text, with its markers"
                | other -> failtestf "expected one row; got %A" other
            }

            test "an unreadable or misspelt opt-in is refused by name, and off is the default" {
                Expect.equal (StatementTextAudit.read Map.empty) (Ok false) "absent is off"
                Expect.equal (StatementTextAudit.read (Map [ StatementTextAudit.Key, " " ])) (Ok false) "blank is off"
                Expect.equal (StatementTextAudit.read (Map [ StatementTextAudit.Key, "false" ])) (Ok false) "off"

                match StatementTextAudit.read (Map [ StatementTextAudit.Key, "yes" ]) with
                | Error reason -> Expect.stringContains reason "yes" "names the value"
                | Ok v -> failtestf "expected a refusal; got %b" v

                Expect.isError
                    (StatementTextAudit.read (Map [ StatementTextAudit.Key, "true"; StatementPolicy.Key, "bound" ]))
                    "an unreadable policy refuses the opt-in"
            }

            testCaseAsync
                "a reader built without a log still records a materialised read in the log it is handed: the hash and the names, no value"
            <| async {
                let _, r = recording (Some allKinds)
                use dir = TempDir.create "virtual-query-audit"
                let audit = TestAuditLog()

                let! spill =
                    r.MaterialiseForHandoff(
                        "s",
                        bound,
                        freshDataObjects dir,
                        "spill-q",
                        TimeSpan.FromHours 1.0,
                        [],
                        audit,
                        "worker"
                    )

                Expect.equal (okv spill).RowCount 6L "the spill carries every row"

                match queriedRows audit with
                | [ row, actor, _ ] ->
                    Expect.equal row.StatementHash (Sha256.hexOfUtf8 shared.Text) "the statement's hash"

                    Expect.equal
                        (row.Parameters |> List.map _.Name)
                        [ "region"; BoundVirtualBinding.WatermarkParameter ]
                        "the parameter names"

                    Expect.equal actor (AuditSubject.system "VirtualDataset") "the reader's default subject"
                | other -> failtestf "expected one query row; got %A" other

                let payloads = audit.Events |> List.map payloadText
                Expect.isFalse (payloads |> List.exists _.Contains("EMEA-secret")) "no shared parameter value"
            }

            testCaseAsync "a log that refuses the row withholds the rows the read returned"
            <| async {
                let refusing =
                    { new IAuditLog with
                        member _.Record(_, _, _) = async {
                            return Error(AuditWriteError.StoreUnavailable "store down (test double)")
                        }

                        member _.GetAuditTrail(_, _, _) = async { return [] }
                    }

                let src = RecordingSource(Map [ binding.QuerySpec, (schema, sixRows) ], None)
                let r = VirtualDatasetReader(src, codec, schema, config, refusing, queryReader)

                match! r.ReadPage("s", binding, DatasetPageQuery.firstPage 10) with
                | Error(DatasetError.StorageFailure reason) -> Expect.stringContains reason "store down" "says why"
                | other -> failtestf "expected the rows withheld; got %A" other
            }

            test "with audit off no payload is built" {
                let built = ref false

                let answer =
                    DataSourceQueryAudit.record (NoOpAuditLog()) "s" queryReader (fun () ->
                        built.Value <- true
                        failwith "no payload is built under NoOpAuditLog")
                    |> Async.RunSynchronously

                Expect.equal answer (Ok()) "the seam's answer"
                Expect.isFalse built.Value "nothing hashed, nothing serialised"
            }

            test "the family round-trips its payload through the stored bytes" {
                let payload =
                    DataSourceQueryAudit.payload
                        boundOnly
                        "Recording"
                        true
                        (DataSourceQueryAudit.ofBound (BoundVirtualBinding.statement bound))
                        (Some [| 1uy; 2uy |])
                        (Some 6L)
                        (DataSourceQueryOutcome.Failed "SchemaDrift")

                match DataSourceQueryAudit.event payload with
                | Opaque o ->
                    Expect.equal o.Kind DataSourceAuditEvent.DataSourceQueriedKind "the kind"
                    Expect.equal (DataSourceQueryAudit.tryRead (Opaque o)) (Some payload) "reads back"
                | other -> failtestf "expected an opaque registrant event; got %A" other
            }

            // The pack's sweep: every payload every scenario below emits is
            // read back as stored, and none carries a parameter value.
            testCaseAsync "no parameter value reaches any payload the reader emits"
            <| async {
                let audit = TestAuditLog()
                use dir = TempDir.create "virtual-query-sweep"
                let dataObjects = freshDataObjects dir
                let src = RecordingSource(Map [ everyKind.Text, (schema, sixRows) ], Some allKinds)

                let readerOver cfg =
                    VirtualDatasetReader(src, codec, schema, cfg, audit, queryReader)

                let at w = { boundAt w with Statement = everyKind }

                let optedIn = {
                    boundOnly with
                        ConnectionScope = boundOnly.ConnectionScope |> Map.add StatementTextAudit.Key "true"
                }

                let watermarks = [ BoundValue.Timestamp sweepInstant; BoundValue.Text "wm-quartz-5521" ]

                for cfg in [ config; optedIn ] do
                    let r = readerOver cfg

                    for w in watermarks do
                        let! _ = r.Bind("s", at w)
                        let! _ = r.ReadPage("s", at w, DatasetPageQuery.firstPage 10)

                        let! _ =
                            r.MaterialiseForHandoff(
                                "s",
                                at w,
                                dataObjects,
                                "spill-sweep",
                                TimeSpan.FromHours 1.0,
                                [],
                                audit,
                                "worker"
                            )

                        ()

                    // a refusal and a failure carry no value either
                    let! _ = r.ReadPage("s", at (BoundValue.Null DatasetDType.Text), DatasetPageQuery.firstPage 1)

                    let! _ =
                        r.ReadPage(
                            "s",
                            {
                                at watermarks.Head with
                                    Statement = {
                                        everyKind with
                                            Text = "SELECT @region_missing"
                                    }
                            },
                            DatasetPageQuery.firstPage 1
                        )

                    ()

                let queried = queriedRows audit
                Expect.isGreaterThanOrEqual queried.Length 16 "every scenario emitted its rows"

                let payloads = audit.Events |> List.map payloadText

                let queriedPayloads =
                    audit.Events
                    |> List.filter (fun e -> (DataSourceQueryAudit.tryRead e).IsSome)
                    |> List.map payloadText

                for value in sharedRenderings do
                    for payload in payloads do
                        Expect.isFalse
                            (payload.Contains value)
                            $"a shared parameter value ('{value}') reached a payload: {payload}"

                // The spill row records the watermark as the vintage identity, by
                // design; the query row records no watermark value.
                for value in watermarkRenderings do
                    for payload in queriedPayloads do
                        Expect.isFalse
                            (payload.Contains value)
                            $"a watermark value ('{value}') reached a query payload: {payload}"
            }
        ]
    ]
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.VirtualDatasetTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore

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

let private okv =
    function
    | Ok v -> v
    | Error(e: DatasetError) -> failtestf "expected Ok; got %s" (DatasetError.describe e)

/// Records every audit call so a test can assert on the spill lifecycle rows.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

let private freshDataObjects () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-virtual-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
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
            let dataObjects = freshDataObjects ()
            let audit = RecordingAuditLog()

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
                |> List.choose (fun (_, e) ->
                    match e with
                    | DatasetSpillCreated p -> Some p
                    | _ -> None)

            let deleted =
                audit.Events
                |> List.choose (fun (_, e) ->
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
    ]
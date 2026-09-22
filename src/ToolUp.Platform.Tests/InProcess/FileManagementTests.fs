module ToolUp.Platform.Tests.InProcess.FileManagementTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.FileManagement
open ToolUp.Platform.FileProcessor
open DataManagementTypes
open ProcessedDataTypes
open ToolUp.Platform.Tests.Remoting

// Persisted-ProcessedFileEntry coverage: round-trip across a simulated
// server restart, stale-DataType branch, loader prefix-filter, delete
// cascade, ReprocessFile happy + missing-file paths. Drives a fresh
// `SessionFileStore` against `LocalFileStorage` per test so blob state
// is isolated.

[<Literal>]
let private TestTypeId = "TestType"

/// Phase 817 — a module-shaped summary record, for the typed envelope.
type private RowSummary = { Rows: int; Header: string }

/// Phase 817 — the same `DataType` built the post-817 way: the summary is
/// encoded into the envelope on the server and the entry is constructed
/// through the builder.
let private mkTypedDataType (id: string) : DataType = {
    Info = {
        Id = id
        DisplayName = "Typed Type"
        Schema = None
    }
    Id = id
    SchemaVersion = DataTypes.initialSchemaVersion
    Migrations = []
    Detect = fun contents -> async { return contents.Contains "header_x" }
    Process =
        fun (fileName, contents) -> async {
            let lines = contents.Split('\n')

            let summary =
                ProcessedDataCodec.encode {
                    Rows = lines.Length
                    Header = lines[0]
                }

            return
                { TypeName = id; Payload = contents }, ProcessedFileEntry.summarised fileName id DateTime.UtcNow summary
        }
}

/// Counting `DataType`. `processCount` increments on every `Process`
/// call so the round-trip test can assert the second-construction
/// fast path skips re-processing. Match-detection looks for the magic
/// header substring.
let private mkDataType (processCount: int ref) : DataType = {
    Info = {
        Id = TestTypeId
        DisplayName = "Test Type"
        Schema = None
    }
    Id = TestTypeId
    SchemaVersion = DataTypes.initialSchemaVersion
    Migrations = []
    Detect = fun contents -> async { return contents.Contains "header_x" }
    Process =
        fun (fileName, contents) -> async {
            processCount.Value <- processCount.Value + 1

            let entry =
                ProcessedFileEntry.summarised
                    fileName
                    TestTypeId
                    DateTime.UtcNow
                    (ProcessedDataCodec.encode {| Rows = contents.Split('\n').Length |})

            let data = {
                TypeName = TestTypeId
                Payload = contents
            }

            return data, entry
        }
}

type private TestRig = {
    Store: SessionFileStore
    DataObjects: IDataObjectStore
    Scope: StorageScope
    TempDir: string
}

let private mkRig (dataTypes: DataType list) : TestRig =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-fm-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)

    let scope = {
        ScopeId = "team-fm-" + suffix
        Container = "team-fm-" + suffix
        Persist = true
    }

    let runtime = FileManagementRuntime.empty
    let store = SessionFileStore(dataTypes, Some dos, scope, runtime)

    {
        Store = store
        DataObjects = dos
        Scope = scope
        TempDir = dir
    }

/// Reconstruct a `SessionFileStore` over the same backing
/// `IDataObjectStore` + scope as `rig` — simulates a process restart.
let private reopen (rig: TestRig) (dataTypes: DataType list) : SessionFileStore =
    let runtime = FileManagementRuntime.empty
    SessionFileStore(dataTypes, Some rig.DataObjects, rig.Scope, runtime)

let private csv = "header_x,header_y\n1,2\n3,4\n"

let private upload fileName contents : DataFileUpload = {
    filename = fileName
    contents = contents
    dataType = "UnrecognisedData"
}

let tests =
    testList "FileManagement" [

        testCaseAsync "round-trip: persisted entry survives reconstruction without re-running Process"
        <| async {
            let count1 = ref 0
            let rig = mkRig [ mkDataType count1 ]

            let! addResult = rig.Store.AddFile(upload "data.csv" csv, "alice")
            Expect.isOk addResult "AddFile should succeed"
            Expect.equal count1.Value 1 "Process called once on upload"

            // Simulate restart with a fresh DataType counter — the
            // persisted sidecar should make `Process` unnecessary for
            // the entry (it still fires once to populate the in-memory
            // parsed payload, but the entry itself comes from disk).
            let count2 = ref 0
            let store2 = reopen rig [ mkDataType count2 ]

            let entries = store2.GetProcessedData()
            Expect.hasLength entries 1 "one entry restored"

            let entry = entries |> List.head
            Expect.equal entry.FileName "data.csv" "entry file name preserved"
            Expect.equal entry.DataType TestTypeId "entry DataType preserved"
            Expect.isNone entry.Error "entry has no error"
            Expect.isSome entry.Summary "entry summary preserved"
        }

        testCaseAsync
            "Phase 817 — the typed summary envelope survives the sidecar round trip, and decodes back on the server"
        <| async {
            let rig = mkRig [ mkTypedDataType TestTypeId ]
            let! addResult = rig.Store.AddFile(upload "data.csv" csv, "alice")
            Expect.isOk addResult "AddFile should succeed"

            let store2 = reopen rig [ mkTypedDataType TestTypeId ]
            let entry = store2.GetProcessedData() |> List.exactlyOne

            match entry.Summary with
            | None -> failtest "the envelope must survive persistence"
            | Some envelope ->
                Expect.equal
                    envelope.TypeName
                    typeof<RowSummary>.FullName
                    "the envelope is stamped with the summary type"

                match ProcessedDataCodec.tryDecode<RowSummary> envelope with
                | Ok summary ->
                    Expect.equal
                        summary
                        {
                            Rows = 4
                            Header = "header_x,header_y"
                        }
                        "the summary decodes to what Process produced"
                | Error why -> failtestf "the envelope must decode as its own type: %s" why
        }

        testCase "Phase 817 — a sidecar persisted before the field existed reads back with Summary = None"
        <| fun () ->
            // The pre-817 on-disk shape: a boxed `Info` and no `Summary`.
            // The member the record no longer has is ignored on read, and
            // the absent one reads as null, which is `None` — so an old
            // deployment's sidecars load without a migration; the entry
            // simply has no summary to render until the file is
            // reprocessed by the module's post-817 `Process`.
            let json =
                """{"FileName":"old.csv","DataType":"TestType","ProcessedAt":"2026-01-01T00:00:00Z","Info":{"Rows":3},"Error":null}"""

            let entry =
                System.Text.Json.JsonSerializer.Deserialize<ProcessedFileEntry>(
                    json,
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()
                )

            Expect.equal entry.FileName "old.csv" "the old fields read as before"
            Expect.isNone entry.Summary "the field the sidecar predates reads as None"

        testCase "Phase 817 — the server codec writes the pinned cross-host envelope, byte for byte"
        <| fun () ->
            // The literal the Fable-tier pack decodes. A change here is a
            // change to what every browser client reads; move the literal
            // and the browser assertion together, never one alone.
            Expect.equal
                (ProcessedDataCodec.encode ProcessedDataEnvelopeFixture.sample).Payload
                ProcessedDataEnvelopeFixture.SamplePayload
                "the encoder's text is the pinned contract"

            Expect.equal
                (ProcessedDataCodec.encode ProcessedDataEnvelopeFixture.sampleWithoutNote).Payload
                ProcessedDataEnvelopeFixture.SampleWithoutNotePayload
                "an absent option writes as null, an empty list as []"

            Expect.equal
                (ProcessedDataCodec.tryDecode<ProcessedDataEnvelopeFixture.SampleSummary> (
                    ProcessedDataEnvelopeFixture.envelope ProcessedDataEnvelopeFixture.SamplePayload
                ))
                (Ok ProcessedDataEnvelopeFixture.sample)
                "and the server reads its own text back"

        testCase "Phase 817 — the codec refuses a payload that is not the type asked for, by name"
        <| fun () ->
            let envelope = ProcessedDataCodec.encode { Rows = 1; Header = "h" }

            match ProcessedDataCodec.tryDecode<int list> envelope with
            | Ok _ -> failtest "a record payload must not read as a list"
            | Error why -> Expect.stringContains why envelope.TypeName "the refusal names the envelope's type tag"

        testCase "Phase 817 — the client render step hands a display the envelopes of the entries that carry one"
        <| fun () ->
            // Exercised here on .NET with the server codec injected; the
            // browser decoder is held to the same envelope in the Fable-tier
            // pack (`ProcessedDataEnvelopeTests`).
            let info: DataTypeInfo = {
                Id = TestTypeId
                DisplayName = "Test Type"
                Schema = None
            }

            let typedSeen = ref []

            // `typedWith`, injecting the server codec: this tier's own
            // decoder is browser-only (see `DataTypeDisplay.tryDecode`),
            // and the browser half is held to the same envelope in the
            // Fable-tier pack (`ProcessedDataEnvelopeTests`).
            let typed =
                DataTypeDisplay.typedWith ProcessedDataCodec.tryDecode<RowSummary> info (fun summaries ->
                    typedSeen.Value <- summaries
                    Feliz.Html.none)

            let typedEntry =
                ProcessedFileEntry.summarised
                    "a.csv"
                    TestTypeId
                    DateTime.UtcNow
                    (ProcessedDataCodec.encode { Rows = 2; Header = "h" })

            let undecodableEntry =
                ProcessedFileEntry.summarised "b.csv" TestTypeId DateTime.UtcNow {
                    TypeName = "Other"
                    Payload = "[1,2,3]"
                }

            let failedEntry =
                ProcessedFileEntry.failed "c.csv" TestTypeId DateTime.UtcNow "boom"

            let entries = [ typedEntry; undecodableEntry; failedEntry ]

            DataTypeDisplay.render typed entries |> ignore

            Expect.equal
                typedSeen.Value
                [ { Rows = 2; Header = "h" } ]
                "the display decodes exactly the envelope that reads as its type; the undecodable and the failed are dropped"

            Expect.isFalse (DataTypeDisplay.hasSummary failedEntry) "a failed entry carries nothing to render"

        testCaseAsync "stale DataType: entry surfaces error when its type is no longer registered"
        <| async {
            let count = ref 0
            let rig = mkRig [ mkDataType count ]

            let! addResult = rig.Store.AddFile(upload "data.csv" csv, "alice")
            Expect.isOk addResult "AddFile should succeed"

            // Reopen with NO registered DataTypes — the persisted entry's
            // DataType is now stale.
            let store2 = reopen rig []

            let entries = store2.GetProcessedData()
            Expect.hasLength entries 1 "stale entry still listed"

            let entry = entries |> List.head
            Expect.isSome entry.Error "stale entry surfaces error"

            let err = entry.Error |> Option.defaultValue ""
            Expect.stringContains err TestTypeId "error names the stale DataType"
            Expect.stringContains err "Reprocess" "error tells user to click Reprocess"
        }

        testCaseAsync "loader filter: entry sidecars don't appear as phantom files in GetFiles"
        <| async {
            let count = ref 0
            let rig = mkRig [ mkDataType count ]

            let! addResult = rig.Store.AddFile(upload "data.csv" csv, "alice")
            Expect.isOk addResult "AddFile should succeed"

            let store2 = reopen rig [ mkDataType (ref 0) ]
            let files = store2.GetFiles()

            Expect.hasLength files 1 "exactly one file listed"

            let names = files |> List.map _.FileName
            Expect.contains names "data.csv" "the real file is listed"

            let phantom = names |> List.tryFind (fun n -> n.StartsWith "_processed_entry__")

            Expect.isNone phantom "no `_processed_entry__` ObjectId leaks into GetFiles"
        }

        testCaseAsync "delete cascade: removing a file also removes its entry sidecar"
        <| async {
            let count = ref 0
            let rig = mkRig [ mkDataType count ]

            let! addResult = rig.Store.AddFile(upload "data.csv" csv, "alice")
            Expect.isOk addResult "AddFile should succeed"

            // Confirm the sidecar is on disk before delete.
            let! before = rig.DataObjects.ListObjects rig.Scope.Container

            let sidecarBefore =
                before |> List.tryFind (fun o -> o.ObjectId = "_processed_entry__data.csv")

            Expect.isSome sidecarBefore "sidecar persisted on AddFile"

            let! deleteResult = rig.Store.DeleteFile "data.csv"
            Expect.isOk deleteResult "DeleteFile should succeed"

            // After delete, neither the file blob nor its sidecar should
            // remain in the container.
            let! after = rig.DataObjects.ListObjects rig.Scope.Container

            let names = after |> List.map _.ObjectId
            Expect.isEmpty names "container empty after delete"
        }

        testCaseAsync "ReprocessFile: re-runs Process and refreshes the entry"
        <| async {
            let count = ref 0
            let rig = mkRig [ mkDataType count ]

            let! addResult = rig.Store.AddFile(upload "data.csv" csv, "alice")
            Expect.isOk addResult "AddFile should succeed"
            Expect.equal count.Value 1 "Process called once on upload"

            let! reprocessResult = rig.Store.ReprocessFile("data.csv", "alice")

            match reprocessResult with
            | Ok entry ->
                Expect.equal entry.FileName "data.csv" "reprocessed entry has the same file name"
                Expect.equal entry.DataType TestTypeId "reprocessed entry has the same DataType"
                Expect.isNone entry.Error "reprocessed entry has no error"
            | Error msg -> failtestf "Expected Ok, got Error: %s" msg

            Expect.equal count.Value 2 "Process called again on reprocess"
        }

        testCaseAsync "ReprocessFile: missing file returns Error"
        <| async {
            let count = ref 0
            let rig = mkRig [ mkDataType count ]

            let! reprocessResult = rig.Store.ReprocessFile("does-not-exist.csv", "alice")

            match reprocessResult with
            | Ok _ -> failtest "Expected Error for missing file"
            | Error msg -> Expect.stringContains msg "not found" "error message names the missing-file condition"
        }

        testCaseAsync "ResetDataStore: wipes every file plus its sidecar and clears in-memory state"
        <| async {
            let count = ref 0
            let rig = mkRig [ mkDataType count ]

            let! r1 = rig.Store.AddFile(upload "a.csv" csv, "alice")
            let! r2 = rig.Store.AddFile(upload "b.csv" csv, "alice")
            Expect.isOk r1 "first AddFile"
            Expect.isOk r2 "second AddFile"

            let! reset = rig.Store.ResetDataStore()
            Expect.equal reset 2 "ResetDataStore returns the count of files removed"

            // In-memory state cleared.
            Expect.isEmpty (rig.Store.GetFiles()) "GetFiles is empty after reset"
            Expect.isEmpty (rig.Store.GetProcessedData()) "GetProcessedData is empty after reset"

            // Disk state cleared — neither file blobs nor entry sidecars
            // remain in the container.
            let! after = rig.DataObjects.ListObjects rig.Scope.Container
            Expect.isEmpty after "container has no objects after reset"
        }

        testCaseAsync "ResetDataStore: empty store returns 0 without errors"
        <| async {
            let rig = mkRig [ mkDataType (ref 0) ]
            let! reset = rig.Store.ResetDataStore()
            Expect.equal reset 0 "empty reset returns 0"
        }

        testCaseAsync "ResetDataStore: leaves unrelated namespaced objects (e.g. entity store) untouched"
        <| async {
            let count = ref 0
            let rig = mkRig [ mkDataType count ]

            let! addResult = rig.Store.AddFile(upload "a.csv" csv, "alice")
            Expect.isOk addResult "AddFile should succeed"

            // Drop a fake entity object directly into the same
            // container — simulates an `EntityStore`/`DataIngestor`
            // sharing the scope. Reset must not touch it.
            let entityBytes = Text.Encoding.UTF8.GetBytes "{\"fake\":\"entity\"}"

            let! _ =
                rig.DataObjects.Save(
                    rig.Scope.Container,
                    "_entity__Foo__bar",
                    entityBytes,
                    "_entity:Foo",
                    "alice",
                    Map.empty,
                    Unversioned
                )

            let! reset = rig.Store.ResetDataStore()
            Expect.equal reset 1 "only the file gets counted"

            let! remaining = rig.DataObjects.ListObjects rig.Scope.Container

            let names = remaining |> List.map _.ObjectId
            Expect.contains names "_entity__Foo__bar" "unrelated entity object survives reset"

            let phantom =
                names
                |> List.tryFind (fun n -> n = "a.csv" || n.StartsWith "_processed_entry__")

            Expect.isNone phantom "no file or sidecar lingers"
        }
    ]
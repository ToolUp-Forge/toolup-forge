module ToolUp.Platform.Tests.InProcess.DataIngestorTests

open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── DataIngestor — the payload carries its schema (Phase 832) ────
//
// Drives the shipped `DataIngestor` over in-memory stores and the
// in-memory connector, then reads the STORE back: the schema object,
// the payload's `schema-ref` / `payload-format` / `schema-drift` keys,
// and the dedup claim (one schema blob per distinct schema), asserted
// against the blob listing rather than assumed.

let private scopeId = "scope-832"
let private sourceId = "sales"
let private table = "orders"

let private nullLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private noSecrets =
    { new ISecretStore with
        member _.GetSecret(_, _) = async { return None }
        member _.SetSecret(_, _, _) = async { return Ok() }
        member _.DeleteSecret(_, _) = async { return Ok() }
        member _.ListKeys _ = async { return [] }
    }

/// A connector whose `GetSchema` fails, delegating everything else.
let private failingSchema (inner: IDataSource) (throws: bool) =
    { new IDataSource with
        member _.Kind = inner.Kind
        member _.Connect ctx = inner.Connect ctx
        member _.ListTables ctx = inner.ListTables ctx

        member _.GetSchema(_, _) = async {
            if throws then
                return failwith "schema endpoint exploded"
            else
                return Error(SourceUnreachable "no schema endpoint")
        }

        member _.Query(ctx, sql) = inner.Query(ctx, sql)
    }

type private Rig = {
    Ingestor: IDataIngestor
    Store: IDataObjectStore
    Blobs: IBlobStorage
    Source: InMemoryDataSource.InMemoryDataSource
}

let private rigWith (wrap: IDataSource -> IDataSource) =
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let store = DataObjectStore.DataObjectStore(blobs) :> IDataObjectStore
    let configs = DataSourceConfigStore.create blobs
    let source = InMemoryDataSource.create ()

    configs.Save(
        scopeId,
        {
            Id = sourceId
            Name = "Sales"
            Kind = "InMemory"
            ConnectionScope = Map.empty
            CredentialKey = "unused"
            Tables = None
            Tags = Map.empty
        }
    )
    |> Async.RunSynchronously

    let ingestor =
        DataIngestor.create
            configs
            noSecrets
            store
            (InMemoryEventStore.InMemoryEventStore() :> IEventStore)
            [ wrap (source :> IDataSource) ]
            blobs
            nullLogger

    {
        Ingestor = ingestor
        Store = store
        Blobs = blobs
        Source = source
    }

let private rig () = rigWith id

let private schemaOf (columns: (string * string * bool) list) : TableSchema = {
    TableName = table
    Columns =
        columns
        |> List.map (fun (n, t, nullable) -> {
            Name = n
            DataType = t
            Nullable = nullable
        })
}

let private baseSchema =
    schemaOf [ "id", "INT64", false; "amount", "NUMERIC", true; "region", "STRING", true ]

let private seed (r: Rig) (csv: string) (schema: TableSchema) =
    r.Source.Seed(sourceId, table, Encoding.UTF8.GetBytes csv, schema)

/// Run one ingestion and return the payload version it wrote.
let private ingest (r: Rig) : DataObject =
    match r.Ingestor.RunIngestion(scopeId, sourceId, table) |> Async.RunSynchronously with
    | Ok run ->
        Expect.equal run.Status IngestionStatus.Succeeded $"the run succeeds (error: {run.Error})"

        let objectId =
            run.ResultObjectId |> Option.defaultWith (fun () -> failtest "no result object")

        match r.Store.Get(scopeId, objectId) |> Async.RunSynchronously with
        | Ok(payload, _) -> payload
        | Error e -> failtest $"payload unreadable: {e}"
    | Error e -> failtest $"ingestion refused: {e}"

/// Every content blob in the scope that parses as a schema — the store-
/// level count the dedup claim is asserted against.
let private schemaBlobCount (r: Rig) =
    let names = r.Blobs.List(scopeId, "objects/_content/") |> Async.RunSynchronously

    names
    |> List.filter (fun name ->
        match r.Blobs.Download(scopeId, name) |> Async.RunSynchronously with
        | Ok bytes -> (IngestedPayload.tryParseSchema bytes).IsSome
        | Error _ -> false)
    |> List.length

let private readSchema (r: Rig) (payload: DataObject) =
    IngestedPayload.readSchema r.Store payload |> Async.RunSynchronously

let tests =
    testList "DataIngestor — payload carries its schema (Phase 832)" [

        test "a run stores a schema object and a payload referencing it" {
            let r = rig ()
            seed r "id,amount,region\n1,2.5,EU\n" baseSchema
            let payload = ingest r

            Expect.equal
                (IngestedPayload.payloadFormat payload)
                (Some IngestedPayload.CurrentPayloadFormat)
                "payload-format is present"

            let schemaRef =
                IngestedPayload.schemaRef payload
                |> Option.defaultWith (fun () -> failtest "no schema-ref")

            let schemaVersions =
                r.Store.ListVersions(scopeId, IngestedPayload.schemaObjectId sourceId table)
                |> Async.RunSynchronously

            Expect.equal (schemaVersions |> List.map _.ContentHash) [ schemaRef ] "the ref names the schema object"
            Expect.equal (readSchema r payload) (Some baseSchema) "types and nullability read back without re-inference"
            Expect.equal (IngestedPayload.schemaDrift payload) None "no earlier schema: drift is unknown, not false"
        }

        test "two runs over an unchanged schema store ONE schema object and ONE schema blob" {
            let r = rig ()
            seed r "id,amount,region\n1,2.5,EU\n" baseSchema
            let first = ingest r
            // The DATA changes; the schema does not.
            seed r "id,amount,region\n1,2.5,EU\n2,9.0,US\n" baseSchema
            let second = ingest r

            let versions =
                r.Store.ListVersions(scopeId, IngestedPayload.schemaObjectId sourceId table)
                |> Async.RunSynchronously

            Expect.equal versions.Length 1 "one schema version"
            Expect.equal (schemaBlobCount r) 1 "one schema blob in the store"
            Expect.equal (IngestedPayload.schemaRef second) (IngestedPayload.schemaRef first) "same ref"
            Expect.equal (IngestedPayload.schemaDrift second) (Some false) "a data change is not schema drift"
        }

        test "a retyped column is drift" {
            let r = rig ()
            seed r "id,amount,region\n1,2.5,EU\n" baseSchema
            let first = ingest r

            let retyped =
                schemaOf [ "id", "INT64", false; "amount", "STRING", true; "region", "STRING", true ]

            seed r "id,amount,region\n1,2.5,EU\n" retyped
            let second = ingest r

            Expect.equal (IngestedPayload.schemaDrift second) (Some true) "drift reported"
            Expect.notEqual (IngestedPayload.schemaRef second) (IngestedPayload.schemaRef first) "ref changed"
            Expect.equal (readSchema r second) (Some retyped) "the new schema reads back"
            Expect.equal (readSchema r first) (Some baseSchema) "the earlier payload still reads its own schema"
            Expect.equal (schemaBlobCount r) 2 "one blob per distinct schema"
        }

        test "a nullability change is drift" {
            let r = rig ()
            seed r "id\n1\n" (schemaOf [ "id", "INT64", false ])
            ingest r |> ignore
            seed r "id\n1\n" (schemaOf [ "id", "INT64", true ])
            let second = ingest r
            Expect.equal (IngestedPayload.schemaDrift second) (Some true) "drift reported"
        }

        test "a GetSchema Error still ingests, with no schema-ref" {
            let r = rigWith (fun s -> failingSchema s false)
            seed r "id\n1\n" baseSchema
            let payload = ingest r
            Expect.equal (IngestedPayload.schemaRef payload) None "no schema-ref"
            Expect.equal (readSchema r payload) None "reads None"
            Expect.isSome (IngestedPayload.payloadFormat payload) "payload-format still written"
            Expect.equal (schemaBlobCount r) 0 "no schema object"
        }

        test "a GetSchema that throws still ingests, with no schema-ref" {
            let r = rigWith (fun s -> failingSchema s true)
            seed r "id\n1\n" baseSchema
            let payload = ingest r
            Expect.equal (IngestedPayload.schemaRef payload) None "no schema-ref"
        }

        test "a connector answering 'schema not available' (Columns = []) writes no schema-ref" {
            let r = rig ()
            r.Source.Seed(sourceId, table, Encoding.UTF8.GetBytes "id\n1\n")
            let payload = ingest r
            Expect.equal (IngestedPayload.schemaRef payload) None "no schema-ref"
            Expect.equal (schemaBlobCount r) 0 "no schema object"
        }

        test "a pre-phase payload (no keys) reads None" {
            let r = rig ()

            let legacy =
                r.Store.Save(
                    scopeId,
                    "_dataingestion__legacy__t",
                    Encoding.UTF8.GetBytes "id\n1\n",
                    "data-ingestion",
                    "_system",
                    Map.ofList [ "source-id", "legacy"; "table", "t"; "connector-kind", "InMemory" ],
                    Versioned
                )
                |> Async.RunSynchronously
                |> Result.defaultWith (fun e -> failtest $"save failed: {e}")

            Expect.equal (readSchema r legacy) None "schema"
            Expect.equal (IngestedPayload.payloadFormat legacy) None "payload-format"
            Expect.equal (IngestedPayload.schemaDrift legacy) None "drift"
        }

        test "canonical serialisation round-trips and is stable" {
            let bytes = IngestedPayload.serializeSchema baseSchema
            Expect.equal (IngestedPayload.tryParseSchema bytes) (Some baseSchema) "round-trip"
            Expect.equal bytes (IngestedPayload.serializeSchema baseSchema) "identical schemas, identical bytes"

            Expect.equal
                (IngestedPayload.tryParseSchema (Encoding.UTF8.GetBytes "not json"))
                None
                "malformed reads None"
        }
    ]
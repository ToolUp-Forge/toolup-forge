// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.PayloadFormatDeclarationTests

open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts
open ToolUp.DataSources
open ToolUp.DataSources.Athena
open ToolUp.DataSources.BigQuery
open ToolUp.DataSources.Common
open ToolUp.DataSources.Redshift
open ToolUp.DataSources.Snowflake
open ToolUp.DataSources.Sql
open ToolUp.DataSources.Synapse
open ToolUp.DataSources.Csv
open ToolUp.DataSources.Excel
open ToolUp.DataSources.Parquet
open ToolUp.DataSources.Tests.Support

// ─── Phase 834 / 1017 — every shipped connector declares what it is ───
//
// Binds the `IDeclaresPayloadFormat` pack and the Phase 1017
// `IDeclaresCredentialNeed` laws to each connector this project ships.
// Both declarations are constants of the connector, so the factories
// construct without credentials, a network or a seeded file — the same
// constructors the connectors' own "instance Kind" tests use. All nine
// tabular connectors render their result sets as RFC 4180 CSV.

let private blobs () =
    FakeBlobStorage.InMemoryBlobStorage() :> BlobStorage.IBlobStorage

let private nullLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A secret store that is unreachable: every operation throws, as the
/// vault client does when its endpoint cannot be resolved.
let private unreachableVault =
    { new ISecretStore with
        member _.GetSecret(_, _) = async { return failwith "secret store unreachable" }
        member _.SetSecret(_, _, _) = async { return failwith "secret store unreachable" }
        member _.DeleteSecret(_, _) = async { return failwith "secret store unreachable" }
        member _.ListKeys _ = async { return failwith "secret store unreachable" }
    }

let private gaTransport: GoogleAnalyticsDataSource.GoogleAnalyticsTransport = {
    MintAccessToken = fun _ -> async { return Error(SourceUnreachable "not called") }
    ListProperties = fun _ -> async { return Error(SourceUnreachable "not called") }
    RunReport = fun _ -> async { return Error(SourceUnreachable "not called") }
}

/// Every shipped connector, with the credential need its own comment
/// justifies. A connector added to the family is added here.
let private shippedConnectors: (string * CredentialNeed * (unit -> IDataSource)) list = [
    "AthenaDataSource", CredentialNeed.Optional, AthenaDataSource.createWithDefaultCredentials
    "BigQueryDataSource", CredentialNeed.Optional, BigQueryDataSource.createWithDefaultCredentials
    "RedshiftDataSource", CredentialNeed.Optional, RedshiftDataSource.createWithDefaultCredentials
    "SnowflakeDataSource", CredentialNeed.Optional, SnowflakeDataSource.createWithKeyFile
    "SqlDataSource", CredentialNeed.Optional, SqlDataSource.createWithoutSecrets
    "SynapseDataSource", CredentialNeed.Optional, SynapseDataSource.createWithDefaultCredentials
    "GoogleAnalyticsDataSource",
    CredentialNeed.Optional,
    (fun () ->
        GoogleAnalyticsDataSource.create
            unreachableVault
            gaTransport
            None
            GoogleAnalyticsDataSource.GoogleAnalyticsSourceConfig.standard)
    "InMemoryDataSource", CredentialNeed.NoCredential, (fun () -> InMemoryDataSource.create () :> IDataSource)
    "CsvDataSource", CredentialNeed.NoCredential, (fun () -> CsvDataSource.create (blobs ()))
    "ExcelDataSource", CredentialNeed.NoCredential, (fun () -> ExcelDataSource.create (blobs ()))
    "ParquetDataSource", CredentialNeed.NoCredential, (fun () -> ParquetDataSource.create (blobs ()))
]

/// The Phase 1017 laws for one connector: the need is declared
/// EXPLICITLY (an `Optional` assertion over the undeclared default would
/// pass vacuously), `CredentialNeed.declaredBy` answers what the
/// interface says, and the answer is stable across calls and instances.
let private credentialNeedLaws (name: string) (expected: CredentialNeed) (factory: unit -> IDataSource) =
    let declaration (source: IDataSource) =
        match box source with
        | :? IDeclaresCredentialNeed as declared -> Some declared.CredentialNeed
        | _ -> None

    testList $"{name} — IDeclaresCredentialNeed" [
        test "declares its credential need explicitly, not by the Optional default" {
            let source = factory ()
            Expect.isSome (declaration source) $"'{source.Kind}' must implement IDeclaresCredentialNeed"
        }

        test "declares the need the binding expects, and declaredBy agrees" {
            let source = factory ()
            Expect.equal (declaration source) (Some expected) "declared need"
            Expect.equal (CredentialNeed.declaredBy source) expected "declaredBy agrees"
        }

        test "the declaration is stable across calls and instances" {
            let source = factory ()
            let first = CredentialNeed.declaredBy source
            Expect.equal (CredentialNeed.declaredBy source) first "same instance, second call"
            Expect.equal (CredentialNeed.declaredBy (factory ())) first "a fresh instance"
        }
    ]

/// A connector that delegates everything to `inner` and declares
/// `need` (or nothing, for `None`), recording whether `Connect` was
/// called and the credential each call context carried.
let private probing
    (need: CredentialNeed option)
    (connected: bool ref)
    (seen: string option list ref)
    (inner: IDataSource)
    =
    let record (ctx: DataSourceCallContext) =
        seen.Value <- seen.Value @ [ ctx.Credential ]

    let source =
        { new IDataSource with
            member _.Kind = inner.Kind

            member _.Connect ctx =
                connected.Value <- true
                record ctx
                inner.Connect ctx

            member _.ListTables ctx = inner.ListTables ctx
            member _.GetSchema(ctx, t) = inner.GetSchema(ctx, t)

            member _.Query(ctx, sql) =
                record ctx
                inner.Query(ctx, sql)
        }

    match need with
    | None -> source
    | Some need ->
        { new IDataSource with
            member _.Kind = source.Kind
            member _.Connect ctx = source.Connect ctx
            member _.ListTables ctx = source.ListTables ctx
            member _.GetSchema(ctx, t) = source.GetSchema(ctx, t)
            member _.Query(ctx, sql) = source.Query(ctx, sql)
          interface IDeclaresCredentialNeed with
              member _.CredentialNeed = need
        }

let private scopeId = "scope-1017"
let private sourceId = "uploads"
let private table = "orders"

/// Run one ingestion of `table` through the shipped `DataIngestor` over
/// in-memory stores, with `connector` as the only registered connector
/// and `secrets` as the composed secret store.
let private ingest (secrets: ISecretStore) (config: DataSourceConfig) (storage: BlobStorage.IBlobStorage) connector =
    let configs = DataSourceConfigStore.create storage
    configs.Save(scopeId, config) |> Async.RunSynchronously

    let ingestor =
        DataIngestor.create
            configs
            secrets
            (DataObjectStore.DataObjectStore(storage) :> IDataObjectStore)
            (InMemoryEventStore.InMemoryEventStore() :> IEventStore)
            [ connector ]
            storage
            nullLogger

    ingestor.RunIngestion(scopeId, sourceId, table) |> Async.RunSynchronously

/// An in-memory connector seeded with one table, and its config.
let private seededInMemory () =
    let source = InMemoryDataSource.create ()
    source.SeedText(sourceId, table, "id,total\r\n1,10\r\n")
    let config = TestFakes.config sourceId "InMemory" []
    source :> IDataSource, config

let private ingestorTests =
    testList "DataIngestor honours the declared credential need (Phase 1017)" [

        test "a NONE connector (CSV) ingests while the secret store throws on every read" {
            let storage = FakeBlobStorage.InMemoryBlobStorage()
            storage.Put($"team-{sourceId}", $"exports/{table}.csv", Encoding.UTF8.GetBytes "id,total\r\n1,10\r\n")

            let config =
                TestFakes.config sourceId CsvDataSource.Kind [ "container", $"team-{sourceId}"; "prefix", "exports/" ]

            let connector = CsvDataSource.create storage

            match ingest unreachableVault config storage connector with
            | Ok run ->
                Expect.equal run.Status IngestionStatus.Succeeded $"the file ingests without the store: %A{run.Error}"
                Expect.isSome run.ResultObjectId "the payload was stored"
            | Error err -> failtestf "expected a run, got %A" err
        }

        test "a NONE connector's call contexts carry no credential" {
            let inner, config = seededInMemory ()
            let connected = ref false
            let seen = ref []
            let store = TestFakes.InMemorySecretStore()
            store.Put(scopeId, config.CredentialKey, "would-be-secret")
            let connector = probing (Some CredentialNeed.NoCredential) connected seen inner

            match ingest store config (blobs ()) connector with
            | Ok run -> Expect.equal run.Status IngestionStatus.Succeeded "ingested"
            | Error err -> failtestf "expected a run, got %A" err

            Expect.equal (store.ReadCount(scopeId, config.CredentialKey)) 0 "the store was never read"
            Expect.isNonEmpty seen.Value "the connector was called"
            Expect.all seen.Value Option.isNone "every context carried Credential = None"
        }

        test "a REQUIRED connector with no secret fails typed, before Connect" {
            let inner, config = seededInMemory ()
            let connected = ref false
            let seen = ref []
            let connector = probing (Some CredentialNeed.Required) connected seen inner

            match ingest (TestFakes.InMemorySecretStore()) config (blobs ()) connector with
            | Ok run ->
                Expect.equal run.Status IngestionStatus.Failed "the run failed"
                Expect.equal run.Error (Some(CredentialMissing config.CredentialKey)) "CredentialMissing naming the key"
                Expect.isNone run.ResultObjectId "nothing was persisted"
            | Error err -> failtestf "expected a failed run, got %A" err

            Expect.isFalse connected.Value "Connect was never called"
        }

        test "a REQUIRED connector with a blank secret fails the same way" {
            let inner, config = seededInMemory ()
            let connected = ref false
            let store = TestFakes.InMemorySecretStore()
            store.Put(scopeId, config.CredentialKey, "   ")
            let connector = probing (Some CredentialNeed.Required) connected (ref []) inner

            match ingest store config (blobs ()) connector with
            | Ok run -> Expect.equal run.Error (Some(CredentialMissing config.CredentialKey)) "blank is missing"
            | Error err -> failtestf "expected a failed run, got %A" err

            Expect.isFalse connected.Value "Connect was never called"
        }

        test "a REQUIRED connector with its secret receives it on the context" {
            let inner, config = seededInMemory ()
            let seen = ref []
            let store = TestFakes.InMemorySecretStore()
            store.Put(scopeId, config.CredentialKey, "s3cret")
            let connector = probing (Some CredentialNeed.Required) (ref false) seen inner

            match ingest store config (blobs ()) connector with
            | Ok run -> Expect.equal run.Status IngestionStatus.Succeeded "ingested"
            | Error err -> failtestf "expected a run, got %A" err

            Expect.all seen.Value (fun c -> c = Some "s3cret") "every context carried the secret"
        }

        test "an UNDECLARED connector keeps today's behaviour: the store is read, None permitted" {
            let inner, config = seededInMemory ()
            let seen = ref []
            let store = TestFakes.InMemorySecretStore()
            let connector = probing None (ref false) seen inner

            Expect.equal (CredentialNeed.declaredBy connector) CredentialNeed.Optional "undeclared reads as Optional"

            match ingest store config (blobs ()) connector with
            | Ok run -> Expect.equal run.Status IngestionStatus.Succeeded "a missing secret is permitted"
            | Error err -> failtestf "expected a run, got %A" err

            Expect.equal (store.ReadCount(scopeId, config.CredentialKey)) 1 "the store was read once"
            Expect.all seen.Value Option.isNone "the missing secret reached the context as None"
        }
    ]

let private resolveDeclaredTests =
    let config = TestFakes.config sourceId "Probe" []
    let ctx = TestFakes.context scopeId None config

    let declaring need =
        probing (Some need) (ref false) (ref []) (InMemoryDataSource.create ())

    testList "Credentials.resolveDeclared follows the declaration (Phase 1017)" [
        testCaseAsync "NONE resolves to None without reading the store"
        <| async {
            let! resolved =
                Credentials.resolveDeclared (declaring CredentialNeed.NoCredential) (Some unreachableVault) ctx

            Expect.equal resolved (Ok None) "no credential, no read"
        }

        testCaseAsync "REQUIRED with nothing stored is CredentialMissing"
        <| async {
            let! resolved =
                Credentials.resolveDeclared
                    (declaring CredentialNeed.Required)
                    (Some(TestFakes.InMemorySecretStore() :> ISecretStore))
                    ctx

            Expect.equal resolved (Error(CredentialMissing config.CredentialKey)) "names the key"
        }

        testCaseAsync "OPTIONAL with nothing stored is Ok None"
        <| async {
            let! resolved =
                Credentials.resolveDeclared
                    (declaring CredentialNeed.Optional)
                    (Some(TestFakes.InMemorySecretStore() :> ISecretStore))
                    ctx

            Expect.equal resolved (Ok None) "absence is permitted"
        }

        testCaseAsync "REQUIRED with a stored secret returns it"
        <| async {
            let store = TestFakes.InMemorySecretStore()
            store.Put(scopeId, config.CredentialKey, "s3cret")

            let! resolved =
                Credentials.resolveDeclared (declaring CredentialNeed.Required) (Some(store :> ISecretStore)) ctx

            Expect.equal resolved (Ok(Some "s3cret")) "the stored secret"
        }
    ]

let tests =
    testList "Connector declarations" [
        testList "IDeclaresPayloadFormat bindings" [
            IDeclaresPayloadFormatContract.tests "AthenaDataSource" PayloadFormat.Csv (fun () ->
                AthenaDataSource.createWithDefaultCredentials ())
            IDeclaresPayloadFormatContract.tests "BigQueryDataSource" PayloadFormat.Csv (fun () ->
                BigQueryDataSource.createWithDefaultCredentials ())
            IDeclaresPayloadFormatContract.tests "RedshiftDataSource" PayloadFormat.Csv (fun () ->
                RedshiftDataSource.createWithDefaultCredentials ())
            IDeclaresPayloadFormatContract.tests "SnowflakeDataSource" PayloadFormat.Csv (fun () ->
                SnowflakeDataSource.createWithKeyFile ())
            IDeclaresPayloadFormatContract.tests "SqlDataSource" PayloadFormat.Csv (fun () ->
                SqlDataSource.createWithoutSecrets ())
            IDeclaresPayloadFormatContract.tests "SynapseDataSource" PayloadFormat.Csv (fun () ->
                SynapseDataSource.createWithDefaultCredentials ())
            IDeclaresPayloadFormatContract.tests "CsvDataSource" PayloadFormat.Csv (fun () ->
                CsvDataSource.create (blobs ()))
            IDeclaresPayloadFormatContract.tests "ExcelDataSource" PayloadFormat.Csv (fun () ->
                ExcelDataSource.create (blobs ()))
            IDeclaresPayloadFormatContract.tests "ParquetDataSource" PayloadFormat.Csv (fun () ->
                ParquetDataSource.create (blobs ()))
        ]
        testList "IDeclaresCredentialNeed bindings" [
            for name, need, factory in shippedConnectors -> credentialNeedLaws name need factory
        ]
        ingestorTests
        resolveDeclaredTests
    ]
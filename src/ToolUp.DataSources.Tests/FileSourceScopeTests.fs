// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.FileSourceScopeTests

open System.Text
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Jobs
open ToolUp.DataSources.Csv
open ToolUp.DataSources.Excel
open ToolUp.DataSources.Parquet
open ToolUp.DataSources.Tests.Support

// ─── Phase 1287 — a saved data source reads only its own scope ────
//
// Two exposures, both pinned here before the fix:
//
//   1. A file connector read the `container` its saved config named, so
//      a config saved through `SaveDataSource` (or one already at rest)
//      could point a Csv, Excel or Parquet source at another team's
//      container, or at the platform's own, and ingest it into the
//      caller's scope. The connector now reads only the job scope's own
//      container, and `prefix` / `extension` cannot climb out of it.
//   2. `ListDataSources` / `GetDataSource` returned every
//      `ConnectionScope` key to any member of the scope, so a member who
//      cannot edit a source read its inline password. A caller who fails
//      the write gate now reads the secret-bearing keys redacted.

/// The bare scope id the ingestion job runs under, and its two
/// renderings: the team scope and the user scope of the same id.
let private JobScope = "acme"

let private teamContainer = ScopeKey.container (ScopeKey.Team(TeamId JobScope))
let private userContainer = ScopeKey.container (ScopeKey.User(UserId JobScope))
let private otherTeam = ScopeKey.container (ScopeKey.Team(TeamId "rival"))

/// The file connectors, each with a file of its own extension.
let private connectors: (string * (BlobStorage.IBlobStorage -> IDataSource) * string) list = [
    CsvDataSource.Kind, CsvDataSource.create, "exports/sales.csv"
    ExcelDataSource.Kind, ExcelDataSource.create, "exports/sales.xlsx"
    ParquetDataSource.Kind, ParquetDataSource.create, "exports/sales.parquet"
]

let private csvBytes = Encoding.UTF8.GetBytes "region,total\r\nnorth,10\r\n"

let private callIn (scopeId: string) (kind: string) (scope: (string * string) list) : DataSourceCallContext =
    TestFakes.config "src" kind scope |> TestFakes.context scopeId None

let private run (work: Async<'T>) = Async.RunSynchronously work

/// Is the error the connector's refusal of the configured location?
let private refused (result: Result<'T, IngestionError>) =
    match result with
    | Error(SchemaMismatch _) -> true
    | _ -> false

let private connectorScope =
    testList "the connector reads only the job scope's container" [
        for kind, create, blob in connectors do
            test $"%s{kind}: another team's container is refused, and nothing is listed" {
                let storage = FakeBlobStorage.InMemoryBlobStorage()
                storage.Put(otherTeam, blob, csvBytes)
                let source = create storage
                let ctx = callIn JobScope kind [ "container", otherTeam; "prefix", "exports" ]

                let listed = source.ListTables ctx |> run
                Expect.isTrue (refused listed) $"a foreign container is refused; got %A{listed}"
                Expect.isTrue (refused (source.Connect ctx |> run)) "Connect refuses it too"
            }

            test $"%s{kind}: the platform container is never readable" {
                let storage = FakeBlobStorage.InMemoryBlobStorage()
                storage.Put(ScopeKey.PlatformContainer, blob, csvBytes)
                let source = create storage

                let ctx =
                    callIn JobScope kind [ "container", ScopeKey.PlatformContainer; "prefix", "exports" ]

                let listed = source.ListTables ctx |> run
                Expect.isTrue (refused listed) $"the platform container is refused; got %A{listed}"
            }

            test $"%s{kind}: any underscore-prefixed container is refused" {
                let storage = FakeBlobStorage.InMemoryBlobStorage()
                storage.Put("_deployment", blob, csvBytes)
                let source = create storage
                let ctx = callIn JobScope kind [ "container", "_deployment"; "prefix", "exports" ]
                Expect.isTrue (refused (source.ListTables ctx |> run)) "a reserved container is refused"
            }

            test $"%s{kind}: the job scope's own container is read" {
                for own in [ teamContainer; userContainer ] do
                    let storage = FakeBlobStorage.InMemoryBlobStorage()
                    storage.Put(own, blob, csvBytes)
                    let source = create storage
                    let ctx = callIn JobScope kind [ "container", own; "prefix", "exports" ]

                    match source.ListTables ctx |> run with
                    | Ok tables -> Expect.equal tables [ "sales" ] $"the scope's own container %s{own} lists"
                    | Error e -> failtestf "the scope's own container %s was refused: %A" own e
            }

            test $"%s{kind}: a prefix or extension that climbs or starts at a separator is refused" {
                let storage = FakeBlobStorage.InMemoryBlobStorage()
                let source = create storage

                for key, value in
                    [
                        "prefix", ".."
                        "prefix", "exports/../.."
                        "prefix", "/exports"
                        "prefix", "\\exports"
                        "extension", "../csv"
                        "extension", "/csv"
                        "extension", "\\csv"
                    ] do
                    let ctx = callIn JobScope kind [ "container", teamContainer; key, value ]
                    let listed = source.ListTables ctx |> run
                    Expect.isTrue (refused listed) $"%s{key} '%s{value}' is refused; got %A{listed}"
            }

        test "Csv: a query against a foreign container reads no bytes" {
            let storage = FakeBlobStorage.InMemoryBlobStorage()
            storage.Put(ScopeKey.PlatformContainer, "exports/sales.csv", csvBytes)
            let source = CsvDataSource.create storage

            let ctx =
                callIn JobScope CsvDataSource.Kind [ "container", ScopeKey.PlatformContainer; "prefix", "exports" ]

            Expect.isTrue (refused (source.Query(ctx, "sales") |> run)) "the query is refused"
        }
    ]

// ─── The API: the location is the platform's, the secrets the writer's ──

/// A team with an owner and a plain member, over one blob store.
type private Rig = {
    Blobs: FakeBlobStorage.InMemoryBlobStorage
    Configs: IDataSourceConfigStore
    Teams: TeamManagement.ITeamStore
}

let private TeamName = "acme"
let private OwnerId = "owner-1"
let private MemberId = "member-1"

let private rig () : Rig =
    let blobs = FakeBlobStorage.InMemoryBlobStorage()

    let notifications =
        NotificationChannel.InMemoryNotificationChannel(None) :> INotificationChannel

    let teams =
        TeamManagement.TeamStore(blobs, notifications) :> TeamManagement.ITeamStore

    run (
        async {
            let! _ = teams.CreateTeam(TeamId TeamName, "Acme")
            let! _ = teams.AddMember(TeamId TeamName, UserId OwnerId, Owner)
            let! _ = teams.AddMember(TeamId TeamName, UserId MemberId, Member)
            return ()
        }
    )

    {
        Blobs = blobs
        Configs = DataSourceConfigStore.BlobDataSourceConfigStore(blobs) :> IDataSourceConfigStore
        Teams = teams
    }

let private apiFor (r: Rig) (userId: string) : IDataIngestionApi =
    let services = ServiceCollection()
    services.AddSingleton<IDataSourceConfigStore>(r.Configs) |> ignore
    services.AddSingleton<TeamManagement.ITeamStore>(r.Teams) |> ignore

    services.AddSingleton<AccessContext>(
        AccessContext.ofSubject ModulePermissions.Unrestricted (TeamMember(userId, TeamName))
    )
    |> ignore

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    DataIngestionApiHandler.dataIngestionApi ctx

let private sqlSource: DataSourceConfig = {
    Id = "warehouse"
    Name = "Warehouse"
    Kind = "Sql"
    ConnectionScope =
        Map.ofList [
            "backend", "postgresql"
            "connection_string", "Host=db;Username=etl;Password=hunter2"
            "schema", "public"
        ]
    CredentialKey = "warehouse-password"
    Tables = None
    Tags = Map.empty
}

let private csvSource (scope: (string * string) list) : DataSourceConfig = {
    Id = "sales"
    Name = "Sales"
    Kind = CsvDataSource.Kind
    ConnectionScope = Map.ofList scope
    CredentialKey = ""
    Tables = None
    Tags = Map.empty
}

let private apiScope =
    testList "the API owns the location and hides the secrets" [
        test "a container in an API-saved config is refused, and nothing is saved" {
            let r = rig ()
            let api = apiFor r OwnerId

            let saved =
                api.SaveDataSource(csvSource [ "container", otherTeam; "prefix", "exports" ])
                |> run

            Expect.isError saved "a container key is refused at SaveDataSource"
            Expect.isNone (r.Configs.Get(TeamName, "sales") |> run) "nothing was persisted"
        }

        test "an API-saved file source reads its own scope's container" {
            let r = rig ()
            let api = apiFor r OwnerId
            let saved = api.SaveDataSource(csvSource [ "prefix", "exports" ]) |> run
            Expect.isOk saved "a config without a container is admitted"
            r.Blobs.Put(teamContainer, "exports/sales.csv", csvBytes)
            r.Blobs.Put(otherTeam, "exports/rival.csv", csvBytes)

            match r.Configs.Get(TeamName, "sales") |> run with
            | None -> failtest "the config was not persisted"
            | Some config ->
                let source = CsvDataSource.create r.Blobs
                let ctx = TestFakes.context TeamName None config

                match source.ListTables ctx |> run with
                | Ok tables -> Expect.equal tables [ "sales" ] "it lists the caller's own files only"
                | Error e -> failtestf "the saved source could not read its own scope: %A" e
        }

        test "reads never carry the platform-derived container" {
            let r = rig ()
            let api = apiFor r OwnerId
            api.SaveDataSource(csvSource [ "prefix", "exports" ]) |> run |> ignore

            for config in
                (api.ListDataSources() |> run)
                @ Option.toList (api.GetDataSource "sales" |> run) do
                Expect.isFalse (config.ConnectionScope.ContainsKey "container") "no container on the wire"

            // So a writer's read-modify-write round-trips.
            match api.GetDataSource "sales" |> run with
            | Some config -> Expect.isOk (api.SaveDataSource config |> run) "the read config saves back"
            | None -> failtest "the source was not readable"
        }

        test "a member who cannot edit a source reads its secret-bearing keys redacted" {
            let r = rig ()
            r.Configs.Save(TeamName, sqlSource) |> run
            let reader = apiFor r MemberId

            let read =
                (reader.ListDataSources() |> run)
                @ Option.toList (reader.GetDataSource "warehouse" |> run)

            Expect.equal read.Length 2 "both reads return the source"

            for config in read do
                Expect.equal
                    (config.ConnectionScope.TryFind "connection_string")
                    (Some DataSourceConfigWire.RedactedValue)
                    "the connection string is redacted"

                Expect.equal (config.ConnectionScope.TryFind "schema") (Some "public") "a plain key is untouched"

                Expect.isFalse
                    (config.ConnectionScope |> Map.exists (fun _ v -> v.Contains "hunter2"))
                    "no value carries the password"
        }

        test "an owner reads the source unredacted" {
            let r = rig ()
            r.Configs.Save(TeamName, sqlSource) |> run
            let owner = apiFor r OwnerId

            match owner.GetDataSource "warehouse" |> run with
            | Some config ->
                Expect.equal
                    (config.ConnectionScope.TryFind "connection_string")
                    (sqlSource.ConnectionScope.TryFind "connection_string")
                    "the writer sees what it saved"
            | None -> failtest "the owner could not read the source"
        }
    ]

let tests = testList "FileSourceScope (Phase 1287)" [ connectorScope; apiScope ]
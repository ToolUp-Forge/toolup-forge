// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DeclaredDataSourcesTests

open System.Threading
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.Compose
open ToolUp.Platform.Infra

/// A connector of a kind and need; nothing here calls more than `Kind`.
let private connector (kind: string) (need: CredentialNeed) : IDataSource =
    { new IDataSource with
        member _.Kind = kind
        member _.Connect _ = failwith "not called"
        member _.ListTables _ = failwith "not called"
        member _.GetSchema(_, _) = failwith "not called"
        member _.Query(_, _) = failwith "not called"
        member _.Capabilities = failwith "not called"
      interface IDeclaresCredentialNeed with
          member _.CredentialNeed = need
    }

let private source: DataSourceFromSettings = {
    Id = "ds-rows"
    Kind = "Sql"
    ScopeSetting = "SCOPE"
    Connection = [ "backend", "BACKEND"; "connection_string", "CONNECTION" ]
    CredentialKeySetting = Some "CREDENTIAL_KEY"
    Tables = Some [ "rows" ]
}

let private read (values: (string * string) list) (need: CredentialNeed) (declared: DataSourceFromSettings) =
    DataSourceFromSettings.read
        (fun name -> values |> List.tryFind (fst >> (=) name) |> Option.map snd)
        [ connector "Sql" need ]
        declared

let private configured = [ "SCOPE", "team-1"; "BACKEND", "postgres"; "CONNECTION", "host=db" ]

let private refusedReason (reading: DataSourceSettingsReading) =
    match reading with
    | DataSourceSettingsReading.Refused reason -> reason
    | other -> failtestf "not refused: %A" other

type private RecordingStore() =
    let mutable saved: (string * DataSourceConfig) list = []
    member _.Saved = List.rev saved

    interface IDataSourceConfigStore with
        member _.Save(scope, config) = async { saved <- (scope, config) :: saved }
        member _.Get(_, _) = async.Return None
        member _.List _ = async.Return []
        member _.Delete(_, _) = async.Return()

[<Tests>]
let tests =
    testList "Declared data sources" [
        testCase "a source with its settings set reads as configured under its scope"
        <| fun _ ->
            match read configured CredentialNeed.Optional source with
            | DataSourceSettingsReading.Configured(scope, config) ->
                Expect.equal scope "team-1" "the scope the setting names"
                Expect.equal config.Id "ds-rows" "the id"
                Expect.equal config.Kind "Sql" "the kind"
                Expect.equal config.Tables (Some [ "rows" ]) "the tables"

                Expect.equal
                    config.ConnectionScope
                    (Map.ofList [ "backend", "postgres"; "connection_string", "host=db" ])
                    "the connection, under its own keys"

                Expect.equal config.CredentialKey "" "an Optional key may be unset"
            | other -> failtestf "%A" other

        testCase "a source with none of its connection settings set is disabled, and says which"
        <| fun _ ->
            match read [ "SCOPE", "team-1" ] CredentialNeed.Required source with
            | DataSourceSettingsReading.Disabled note ->
                Expect.stringContains note "ds-rows" "the source"
                Expect.stringContains note "BACKEND, CONNECTION" "the settings"
            | other -> failtestf "%A" other

        testCase "a source with no connection settings is never disabled"
        <| fun _ ->
            match read [ "SCOPE", "team-1" ] CredentialNeed.Optional { source with Connection = [] } with
            | DataSourceSettingsReading.Configured _ -> ()
            | other -> failtestf "%A" other

        testCase "a source set in part is refused, naming the missing setting and not the set one"
        <| fun _ ->
            let reason =
                read [ "SCOPE", "team-1"; "BACKEND", "postgres" ] CredentialNeed.Optional source
                |> refusedReason

            Expect.stringContains reason "CONNECTION is not set" "the missing setting"
            Expect.isFalse (reason.Contains "BACKEND") "not the one that is set"

        testCase "an unset scope setting is refused, naming it"
        <| fun _ ->
            let reason =
                read (configured |> List.filter (fst >> (<>) "SCOPE")) CredentialNeed.Optional source
                |> refusedReason

            Expect.stringContains reason "SCOPE" "named"

        testCase "the credential is a key into the secret store, read as the connector's need says"
        <| fun _ ->
            let withKey = ("CREDENTIAL_KEY", "rows-secret") :: configured

            let keyOf reading =
                match reading with
                | DataSourceSettingsReading.Configured(_, config) -> config.CredentialKey
                | other -> failtestf "%A" other

            Expect.equal (keyOf (read withKey CredentialNeed.Required source)) "rows-secret" "Required: the key"
            Expect.equal (keyOf (read withKey CredentialNeed.Optional source)) "rows-secret" "Optional: the key"
            Expect.equal (keyOf (read withKey CredentialNeed.NoCredential source)) "" "NoCredential: never read"

            let unset = read configured CredentialNeed.Required source |> refusedReason
            Expect.stringContains unset "CREDENTIAL_KEY is not set" "Required with the setting unset"

            let undeclared =
                read configured CredentialNeed.Required {
                    source with
                        CredentialKeySetting = None
                }
                |> refusedReason

            Expect.stringContains undeclared "declares no setting" "Required with no setting declared"

        testCase "a kind no connector serves is refused, once the source is configured"
        <| fun _ ->
            let unserved = { source with Kind = "Ghost" }

            Expect.stringContains
                (read configured CredentialNeed.Optional unserved |> refusedReason)
                "no connector"
                "named"

            match read [ "SCOPE", "team-1" ] CredentialNeed.Optional unserved with
            | DataSourceSettingsReading.Disabled _ -> ()
            | other -> failtestf "a disabled source needs no connector: %A" other

        testCase "seeding saves the configured, skips the disabled, and saves nothing if any is refused"
        <| fun _ ->
            let values = Map.ofList configured
            let setting name = Map.tryFind name values

            let disabled = {
                source with
                    Id = "ds-off"
                    Connection = [ "x", "UNSET" ]
            }

            let store = RecordingStore()

            DataSourceFromSettings.seed setting [ connector "Sql" CredentialNeed.Optional ] store None [
                source
                disabled
            ]
            |> Async.RunSynchronously

            Expect.equal (store.Saved |> List.map (fun (s, c) -> s, c.Id)) [ "team-1", "ds-rows" ] "only the configured"

            let refusedStore = RecordingStore()

            let partial = {
                source with
                    Id = "ds-part"
                    Connection = [ "backend", "BACKEND"; "x", "UNSET" ]
            }

            Expect.throwsT<DataSourceSettingsRefusedException>
                (fun () ->
                    DataSourceFromSettings.seed setting [ connector "Sql" CredentialNeed.Optional ] refusedStore None [
                        source
                        partial
                    ]
                    |> Async.RunSynchronously)
                "refused"

            Expect.isEmpty refusedStore.Saved "nothing is seeded"

        testCase "a repeated id is refused at the call"
        <| fun _ ->
            Expect.throwsT<DataSourceSettingsRefusedException>
                (fun () ->
                    ServerApp.empty
                    |> ServerApp.withDeclaredDataSources [ source; source ]
                    |> ignore)
                "refused"

        testCase "the combinator registers a one-shot seeder that starts over the composed services"
        <| fun _ ->
            let store = RecordingStore()
            let id = "DECLARED_SOURCE_TEST_" + System.Guid.NewGuid().ToString "N"
            System.Environment.SetEnvironmentVariable(id + "_SCOPE", "team-9")
            System.Environment.SetEnvironmentVariable(id + "_BACKEND", " postgres ")

            try
                let declared = {
                    source with
                        ScopeSetting = id + "_SCOPE"
                        Connection = [ "backend", id + "_BACKEND" ]
                        CredentialKeySetting = None
                }

                let app = ServerApp.empty |> ServerApp.withDeclaredDataSources [ declared ]
                let services = ServiceCollection()
                services.AddSingleton<IDataSourceConfigStore>(store) |> ignore

                services.AddSingleton<IDataSource>(connector "Sql" CredentialNeed.Optional)
                |> ignore

                (ServerApp.serviceConfig app).Value services |> ignore
                use provider = services.BuildServiceProvider()

                for hosted in provider.GetServices<IHostedService>() do
                    hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult()

                match store.Saved with
                | [ scope, config ] ->
                    Expect.equal scope "team-9" "the scope"
                    Expect.equal config.ConnectionScope.["backend"] "postgres" "the value, trimmed"
                | other -> failtestf "%A" other
            finally
                System.Environment.SetEnvironmentVariable(id + "_SCOPE", null)
                System.Environment.SetEnvironmentVariable(id + "_BACKEND", null)

        testCase "with no config store composed the seeder refuses the start, naming the remedy"
        <| fun _ ->
            let app = ServerApp.empty |> ServerApp.withDeclaredDataSources [ source ]
            let services = ServiceCollection()
            (ServerApp.serviceConfig app).Value services |> ignore
            use provider = services.BuildServiceProvider()

            let raised =
                Expect.throwsC
                    (fun () ->
                        for hosted in provider.GetServices<IHostedService>() do
                            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult())
                    (fun ex -> ex.Message)

            Expect.stringContains raised "DataIngestion" "the remedy"
    ]
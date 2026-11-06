// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Compose

open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.Config
open ToolUp.Platform.Infra

// ─── Declared data sources ──────────────────────────────
//
// A data source an application declares as data: its connector kind and the
// names of the settings that hold its scope, connection and credential key.
// Each is read when the application starts and seeded into
// `IDataSourceConfigStore`, so the ingestor can resolve it. A credential is
// never a setting's value here: the config carries the key `ISecretStore` holds
// it under, and whether a key is needed is the connector's own `CredentialNeed`.
//
// This reads per-deployment values from settings and seeds the config, nothing
// more: its data arrives by upload, or by whatever the application runs. A
// source whose config is known in code and whose delivery forge should schedule
// under a principal is `DeclaredDataSource` and
// `DeclaredDelivery.withDeclaredDataSources` (ToolUp.DataSources.Common).

/// One data source an application declares.
type DataSourceFromSettings = {
    /// The source's id within its scope, also its display name.
    Id: DataSourceId
    /// The connector kind (`IDataSource.Kind`).
    Kind: string
    /// The setting naming the scope the source is registered under.
    ScopeSetting: string
    /// Each `ConnectionScope` key and the setting that holds its value.
    Connection: (string * string) list
    /// The setting holding the `ISecretStore` key of the source's credential.
    /// The connector's `CredentialNeed` decides what becomes of it: `Required`
    /// refuses a start without one, `Optional` lets it be unset, and
    /// `NoCredential` never reads it.
    CredentialKeySetting: string option
    /// The tables to ingest, or `None` to discover them.
    Tables: string list option
}

/// What one declared source reads as at start.
[<RequireQualifiedAccess>]
type DataSourceSettingsReading =
    /// Configured: the scope it is registered under and its config.
    | Configured of scope: string * config: DataSourceConfig
    /// None of its connection settings is set, so it is declared but not
    /// configured. Setting them enables it at the next start.
    | Disabled of note: string
    /// It cannot start: a setting is missing, or nothing can run it.
    | Refused of reason: string

/// Raised at start when a declared source is refused. It names every refusal,
/// and nothing is seeded.
type DataSourceSettingsRefusedException(refusals: string list) =
    inherit InvalidOperationException("declared data sources refused: " + String.concat "; " refusals)

    /// Each source's refusal.
    member _.Refusals = refusals

[<RequireQualifiedAccess>]
module DataSourceFromSettings =

    /// Read one source through `setting` (a setting's name to its value, `None`
    /// when unset or blank) against the connectors composed. Pure.
    let read
        (setting: string -> string option)
        (connectors: IDataSource list)
        (source: DataSourceFromSettings)
        : DataSourceSettingsReading =
        let named = sprintf "data source '%s' (%s)" source.Id source.Kind

        let values =
            source.Connection |> List.map (fun (key, name) -> key, name, setting name)

        let isUnset (_, _, value: string option) = value.IsNone

        if not values.IsEmpty && List.forall isUnset values then
            DataSourceSettingsReading.Disabled(
                sprintf
                    "%s is disabled: none of %s is set; set them and restart to enable it"
                    named
                    (source.Connection |> List.map snd |> String.concat ", ")
            )
        else
            let missing = values |> List.filter isUnset |> List.map (fun (_, name, _) -> name)

            let connector =
                connectors |> List.tryFind (fun candidate -> candidate.Kind = source.Kind)

            match missing, setting source.ScopeSetting, connector with
            | _ :: _, _, _ ->
                DataSourceSettingsReading.Refused(
                    sprintf "%s is set in part: %s is not set" named (String.concat ", " missing)
                )
            | [], None, _ ->
                DataSourceSettingsReading.Refused(
                    sprintf "%s names its scope in %s, which is not set" named source.ScopeSetting
                )
            | [], _, None ->
                DataSourceSettingsReading.Refused(sprintf "%s: no connector of that kind is composed" named)
            | [], Some scope, Some connector ->
                let keyValue = source.CredentialKeySetting |> Option.bind setting

                let credentialKey =
                    match CredentialNeed.declaredBy connector, source.CredentialKeySetting, keyValue with
                    | CredentialNeed.NoCredential, _, _ -> Ok ""
                    | CredentialNeed.Optional, _, value -> Ok(value |> Option.defaultValue "")
                    | CredentialNeed.Required, _, Some key -> Ok key
                    | CredentialNeed.Required, Some name, None ->
                        Error(sprintf "%s needs a credential, and %s is not set" named name)
                    | CredentialNeed.Required, None, None ->
                        Error(sprintf "%s needs a credential, and it declares no setting for the key" named)

                match credentialKey with
                | Error reason -> DataSourceSettingsReading.Refused reason
                | Ok key ->
                    DataSourceSettingsReading.Configured(
                        scope,
                        {
                            Id = source.Id
                            Name = source.Id
                            Kind = source.Kind
                            ConnectionScope =
                                values
                                |> List.choose (fun (key, _, value) -> value |> Option.map (fun v -> key, v))
                                |> Map.ofList
                            CredentialKey = key
                            Tables = source.Tables
                            Tags = Map.empty
                        }
                    )

    /// A setting's value through the platform's resolution seam: trimmed, and
    /// `None` when unset or blank.
    let private settingValue (name: string) : string option =
        ConfigResolution.tryValue name
        |> Option.map _.Trim()
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    /// Read every source, refuse the start if any is refused, name each disabled
    /// one, and seed the rest. Nothing is seeded when anything is refused.
    let seed
        (setting: string -> string option)
        (connectors: IDataSource list)
        (store: IDataSourceConfigStore)
        (logger: ILogger option)
        (sources: DataSourceFromSettings list)
        : Async<unit> =
        async {
            let readings = sources |> List.map (read setting connectors)

            let refusals =
                readings
                |> List.choose (function
                    | DataSourceSettingsReading.Refused reason -> Some reason
                    | _ -> None)

            if not refusals.IsEmpty then
                raise (DataSourceSettingsRefusedException refusals)

            for reading in readings do
                match reading with
                | DataSourceSettingsReading.Configured(scope, config) ->
                    do! store.Save(scope, config)

                    logger
                    |> Option.iter _.Info(sprintf "data source '%s' seeded under scope %s" config.Id scope)
                | DataSourceSettingsReading.Disabled note -> logger |> Option.iter _.Warn(note)
                | DataSourceSettingsReading.Refused _ -> ()
        }

    let private seeder (sources: DataSourceFromSettings list) (services: IServiceProvider) : IHostedService =
        { new IHostedService with
            member _.StartAsync _ =
                async {
                    match services.GetService(typeof<IDataSourceConfigStore>) with
                    | :? IDataSourceConfigStore as store ->
                        let connectors =
                            services.GetServices(typeof<IDataSource>) |> Seq.cast<IDataSource> |> List.ofSeq

                        let logger =
                            match services.GetService(typeof<ILogger>) with
                            | :? ILogger as logger -> Some logger
                            | _ -> None

                        do! seed settingValue connectors store logger sources
                    | _ ->
                        raise (
                            DataSourceSettingsRefusedException [
                                "data sources are declared, and this deployment composes no IDataSourceConfigStore: enable ServerConfig.DataIngestion"
                            ]
                        )
                }
                |> Async.StartAsTask
                :> Task

            member _.StopAsync _ = Task.CompletedTask
        }

    /// The registration that seeds `sources` when the application starts: a
    /// one-shot at start on every process shape. A repeated id is refused now.
    let register (sources: DataSourceFromSettings list) : IServiceCollection -> IServiceCollection =
        match sources |> List.countBy _.Id |> List.filter (fun (_, count) -> count > 1) with
        | [] -> ()
        | repeats ->
            repeats
            |> List.map (fun (id, _) -> sprintf "data source '%s' is declared more than once" id)
            |> DataSourceSettingsRefusedException
            |> raise

        fun services ->
            Hosting.register services (BackgroundUnit.create "data-source-settings-seeder" OnceAtStart (seeder sources))
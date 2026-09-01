// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module MigrationStatusUI

open ToolUp.Elmish
open Feliz
open ToolUp.Platform

// ─── Phase 10a — built-in data-migration admin ──────────────────────
//
// Sidebar entry showing, for the caller's own scope, every registered
// data type's declared schema version and how far the stored objects
// have got towards it — "Migrating Media Optimisation V2→V3: 47/120
// objects" — with an Owner / Admin trigger per data type and the
// failure log a pass leaves behind.
//
// Everything the table renders is server-projected: the declared
// version and the chain-validity verdict come from
// `IDataMigrationApi.ListDataTypes`, the counts from `ListStatuses`.
// The client re-derives nothing, so a client that is a version behind
// cannot disagree with the server about whether a migration is safe to
// run.
//
// A deployment on `ServerConfig.DataMigrations = NoDataMigrations`
// mounts no route; `ClientConfig.MigrationAdmin` therefore defaults to
// `NoMigrationAdmin` and this module is not injected at all.

// ─── Model ──────────────────────────────────────────────────────────

type LoadState<'T> =
    | NotLoaded
    | Loading
    | Loaded of 'T
    | LoadError of string

type Model = {
    /// Registered data types with their declared versions and any
    /// chain problem the server found.
    DataTypes: LoadState<MigrationDataTypeInfo list>
    /// Latest recorded status per data type for the caller's scope.
    /// Missing keys render as "not yet run" rather than as an error.
    Statuses: Map<string, MigrationStatus>
    /// Data types with a manual trigger in flight — the button is
    /// disabled while one is.
    Triggering: Set<string>
    /// The data type whose failure log is expanded, if any. Clicking
    /// the same row twice collapses it.
    ExpandedFailures: string option
    /// Transient error from a trigger. Read failures land in
    /// `DataTypes` as `LoadError` instead.
    LastError: string option
}

type Msg =
    | Refresh
    | DataTypesLoaded of Result<MigrationDataTypeInfo list, string>
    | StatusesLoaded of MigrationStatus list
    | TriggerClicked of string
    | TriggerComplete of string * Result<MigrationStatus, string>
    | ToggleFailures of string
    | DismissError

// ─── API proxy ──────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see
// UserSession.fs + SDK.Client.fs installRequestGuard.
let private migrationApi: IDataMigrationApi =
    Api.makeProxy<IDataMigrationApi> (customOptions = UserSession.withRequestHeaders)

// ─── Init / update ──────────────────────────────────────────────────

let private loadDataTypesCmd () =
    Cmd.OfRemoting.call migrationApi.ListDataTypes () (Ok >> DataTypesLoaded) (fun e ->
        DataTypesLoaded(Error e.Message))

let private loadStatusesCmd () =
    // Best-effort — a status read failure leaves the counts blank
    // rather than pushing a banner over a table that still renders the
    // declared versions usefully.
    Cmd.OfRemoting.call migrationApi.ListStatuses () StatusesLoaded (fun _ -> StatusesLoaded [])

let private refreshCmd () =
    Cmd.batch [ loadDataTypesCmd (); loadStatusesCmd () ]

let init () =
    let model = {
        DataTypes = Loading
        Statuses = Map.empty
        Triggering = Set.empty
        ExpandedFailures = None
        LastError = None
    }

    model, refreshCmd ()

let update (msg: Msg) (model: Model) =
    match msg with
    | Refresh -> { model with DataTypes = Loading }, refreshCmd ()

    | DataTypesLoaded(Ok dataTypes) ->
        {
            model with
                DataTypes = Loaded dataTypes
        },
        Cmd.none

    | DataTypesLoaded(Error message) ->
        {
            model with
                DataTypes = LoadError message
        },
        Cmd.none

    | StatusesLoaded statuses ->
        {
            model with
                Statuses = statuses |> List.map (fun s -> s.DataTypeId, s) |> Map.ofList
        },
        Cmd.none

    | TriggerClicked dataTypeId ->
        let cmd =
            Cmd.OfRemoting.call
                migrationApi.TriggerMigration
                dataTypeId
                (fun result -> TriggerComplete(dataTypeId, result))
                (fun e -> TriggerComplete(dataTypeId, Error e.Message))

        {
            model with
                Triggering = model.Triggering |> Set.add dataTypeId
                LastError = None
        },
        cmd

    | TriggerComplete(dataTypeId, Ok status) ->
        // The trigger returns the status it persisted, so the row
        // updates without a second round-trip.
        {
            model with
                Triggering = model.Triggering |> Set.remove dataTypeId
                Statuses = model.Statuses |> Map.add dataTypeId status
        },
        Cmd.none

    | TriggerComplete(dataTypeId, Error message) ->
        {
            model with
                Triggering = model.Triggering |> Set.remove dataTypeId
                LastError = Some message
        },
        Cmd.none

    | ToggleFailures dataTypeId ->
        let next =
            match model.ExpandedFailures with
            | Some current when current = dataTypeId -> None
            | _ -> Some dataTypeId

        { model with ExpandedFailures = next }, Cmd.none

    | DismissError -> { model with LastError = None }, Cmd.none

// ─── View ───────────────────────────────────────────────────────────

let private statePill (msgs: MigrationStatusMessages) (state: MigrationRunState) =
    let label, cls =
        match state with
        | MigrationIdle -> msgs.NotYetRun, "bg-gray-100 text-gray-700 border-gray-200"
        | MigrationInProgress -> msgs.InProgressLabel, "bg-blue-100 text-blue-700 border-blue-200"
        | MigrationComplete -> msgs.UpToDate, "bg-green-100 text-green-700 border-green-200"
        | MigrationCompleteWithFailures -> msgs.CompletedWithFailures, "bg-yellow-100 text-yellow-700 border-yellow-200"
        | MigrationChainBlocked _ -> msgs.Blocked, "bg-red-100 text-red-700 border-red-200"

    Html.span [
        prop.className $"inline-block px-2 py-0.5 text-xs rounded border {cls}"
        prop.text label
    ]

/// The progress sentence the phase asked for, in the module's own
/// vocabulary: which version pair, and how far through the scope.
let private progressText
    (msgs: MigrationStatusMessages)
    (info: MigrationDataTypeInfo)
    (status: MigrationStatus option)
    =
    match status with
    | None -> msgs.NoPassRecorded
    | Some s ->
        let done' = s.MigratedObjects + s.AlreadyCurrentObjects

        match s.State with
        | MigrationChainBlocked reason -> reason
        | MigrationIdle -> msgs.NoPassRecorded
        | MigrationInProgress -> msgs.InProgressText info.DisplayName s.TargetVersion done' s.TotalObjects
        | MigrationComplete -> msgs.CompleteText s.TotalObjects s.TargetVersion
        | MigrationCompleteWithFailures ->
            msgs.CompleteWithFailuresText done' s.TotalObjects s.TargetVersion s.FailedObjects

let private errorBanner (msgs: MigrationStatusMessages) (message: string) (dispatch: Msg -> unit) =
    Html.div [
        prop.className
            "flex items-start justify-between p-3 mb-4 text-sm border rounded bg-red-50 border-red-200 text-red-800"
        prop.children [
            Html.span [ prop.text message ]
            Html.button [
                prop.className "ml-4 text-xs underline"
                prop.text msgs.Dismiss
                prop.onClick (fun _ -> dispatch DismissError)
            ]
        ]
    ]

let private failureRows (msgs: MigrationStatusMessages) (status: MigrationStatus) =
    if status.Failures.IsEmpty then
        Html.p [ prop.className "text-xs text-gray-500"; prop.text msgs.NoFailuresRecorded ]
    else
        Html.div [
            prop.className "space-y-1"
            prop.children [
                Html.p [
                    prop.className "text-xs text-gray-500"
                    prop.text (msgs.FailuresSummary(List.length status.Failures))
                ]
                Html.ul [
                    prop.className "text-xs font-mono space-y-1"
                    prop.children (
                        status.Failures
                        |> List.map (fun failure ->
                            Html.li [
                                prop.className "text-red-700"
                                prop.text (msgs.FailureLine failure.ObjectId failure.AtVersion failure.Error)
                            ])
                    )
                ]
            ]
        ]

let private dataTypeRow
    (msgs: MigrationStatusMessages)
    (model: Model)
    (dispatch: Msg -> unit)
    (info: MigrationDataTypeInfo)
    =
    let status = model.Statuses.TryFind info.DataTypeId
    let triggering = model.Triggering.Contains info.DataTypeId
    let expanded = model.ExpandedFailures = Some info.DataTypeId

    let failureCount = status |> Option.map _.FailedObjects |> Option.defaultValue 0

    Html.tbody [
        Html.tr [
            prop.className "border-b border-gray-100"
            prop.children [
                Html.td [
                    prop.className "py-2 pr-4"
                    prop.children [
                        Html.div [ prop.className "text-sm font-medium"; prop.text info.DisplayName ]
                        Html.div [ prop.className "text-xs text-gray-500 font-mono"; prop.text info.DataTypeId ]
                    ]
                ]
                Html.td [
                    prop.className "py-2 pr-4 text-sm"
                    prop.text (msgs.DeclaredVersion info.CurrentVersion)
                ]
                Html.td [
                    prop.className "py-2 pr-4 text-sm text-gray-700"
                    prop.text (progressText msgs info status)
                ]
                Html.td [
                    prop.className "py-2 pr-4"
                    prop.children [
                        match status with
                        | Some s -> statePill msgs s.State
                        | None -> statePill msgs MigrationIdle
                    ]
                ]
                Html.td [
                    prop.className "py-2 text-right space-x-3"
                    prop.children [
                        match info.ChainProblem with
                        | Some problem ->
                            Html.span [
                                prop.className "text-xs text-red-700"
                                prop.title problem
                                prop.text msgs.ChainIncomplete
                            ]
                        | None ->
                            Html.button [
                                prop.className "text-xs underline disabled:opacity-40 disabled:no-underline"
                                prop.disabled triggering
                                prop.text (if triggering then msgs.Migrating else msgs.MigrateNow)
                                prop.onClick (fun _ -> dispatch (TriggerClicked info.DataTypeId))
                            ]
                        if failureCount > 0 then
                            Html.button [
                                prop.className "text-xs underline text-red-700"
                                prop.text (
                                    if expanded then
                                        msgs.HideFailures
                                    else
                                        msgs.FailuresButton failureCount
                                )
                                prop.onClick (fun _ -> dispatch (ToggleFailures info.DataTypeId))
                            ]
                    ]
                ]
            ]
        ]
        if expanded then
            match status with
            | Some s ->
                Html.tr [
                    prop.className "border-b border-gray-100 bg-gray-50"
                    prop.children [
                        Html.td [ prop.colSpan 5; prop.className "p-3"; prop.children [ failureRows msgs s ] ]
                    ]
                ]
            | None -> Html.none
    ]

let private dataTypesTable
    (msgs: MigrationStatusMessages)
    (model: Model)
    (dispatch: Msg -> unit)
    (dataTypes: MigrationDataTypeInfo list)
    =
    if dataTypes.IsEmpty then
        Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.NoDataTypes ]
    else
        Html.table [
            prop.className "w-full text-left"
            prop.children [
                Html.thead [
                    Html.tr [
                        prop.className "border-b border-gray-200 text-xs uppercase text-gray-500"
                        prop.children [
                            Html.th [ prop.className "py-2 pr-4 font-medium"; prop.text msgs.ColumnDataType ]
                            Html.th [ prop.className "py-2 pr-4 font-medium"; prop.text msgs.ColumnDeclared ]
                            Html.th [ prop.className "py-2 pr-4 font-medium"; prop.text msgs.ColumnProgress ]
                            Html.th [ prop.className "py-2 pr-4 font-medium"; prop.text msgs.ColumnState ]
                            Html.th [ prop.className "py-2 font-medium text-right"; prop.text msgs.ColumnActions ]
                        ]
                    ]
                ]
                yield! (dataTypes |> List.map (dataTypeRow msgs model dispatch))
            ]
        ]

let private refreshButton (msgs: MigrationStatusMessages) (loading: bool) (dispatch: Msg -> unit) =
    Html.button [
        prop.className "px-3 py-1.5 text-sm border rounded border-gray-300 disabled:opacity-40"
        prop.disabled loading
        prop.text (if loading then msgs.Refreshing else msgs.Refresh)
        prop.onClick (fun _ -> dispatch Refresh)
    ]

/// Phase 751 — the module body as a React COMPONENT, for the same reason
/// `HealthMonitorUI.HealthMonitorBody` is one: a module's `view` is invoked
/// inline by the shell's own render, so a hook there would join the shell's
/// hook order and break the moment the active module changed.
[<ReactComponent>]
let private MigrationStatusBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).MigrationStatus

    let loading =
        match model.DataTypes with
        | Loading -> true
        | _ -> false

    Html.div [
        prop.className "flex-1 p-6 overflow-y-auto"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between mb-4"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.Heading ]
                            Html.p [ prop.className "text-xs text-gray-500"; prop.text msgs.Subheading ]
                        ]
                    ]
                    refreshButton msgs loading dispatch
                ]
            ]
            match model.LastError with
            | Some message -> Html.div [ prop.className "mb-4"; prop.children [ errorBanner msgs message dispatch ] ]
            | None -> Html.none
            match model.DataTypes with
            | NotLoaded
            | Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.LoadingDataTypes ]
            | LoadError message -> errorBanner msgs message dispatch
            | Loaded dataTypes -> dataTypesTable msgs model dispatch dataTypes
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    Html.div [
        prop.className "flex flex-col h-full"
        prop.children [ MigrationStatusBody model dispatch ]
    ]

// ─── Module creation ────────────────────────────────────────────────

let create (config: MigrationAdminConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Data Migrations"

    let icon =
        config
        |> Option.map _.Icon
        |> Option.defaultValue ToolUp.Platform.Icons.arrowUpwards

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.DataMigrations"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Platform Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
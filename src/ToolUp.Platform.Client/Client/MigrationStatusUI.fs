// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module MigrationStatusUI

open Fuaran.MVU
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── Built-in data-migration admin ──────────────────────────────────
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
// mounts no route; the `MigrationAdmin` shell slot therefore defaults
// to `SlotFill.Empty` and this module is not injected at all.

// ─── Model ──────────────────────────────────────────────────────────

type Model = {
    /// Registered data types with their declared versions and any
    /// chain problem the server found.
    DataTypes: Remote<MigrationDataTypeInfo list, string>
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
    /// `DataTypes` as `Remote.Failed` instead.
    LastError: Notice option
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

let internal loadDataTypes () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IDataMigrationApi.ListDataTypes"
            migrationApi.ListDataTypes
            ()
            (Result.mapError _.Message >> DataTypesLoaded)
    )
]

let internal loadStatuses () : Step<Msg, RemoteEffect<Msg>> list = [
    // Best-effort — a status read failure leaves the counts blank
    // rather than pushing a banner over a table that still renders the
    // declared versions usefully.
    Do(
        RemoteEffect.call "IDataMigrationApi.ListStatuses" migrationApi.ListStatuses () (function
            | Ok statuses -> StatusesLoaded statuses
            | Error _ -> StatusesLoaded [])
    )
]

let internal refresh () : Step<Msg, RemoteEffect<Msg>> list = loadDataTypes () @ loadStatuses ()

let internal trigger (dataTypeId: string) : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call "IDataMigrationApi.TriggerMigration" migrationApi.TriggerMigration dataTypeId (fun reply ->
            TriggerComplete(dataTypeId, RemoteEffect.flatten reply))
    )
]

let init () : Model * Step<Msg, RemoteEffect<Msg>> list =
    let model = {
        DataTypes = Remote.Loading
        Statuses = Map.empty
        Triggering = Set.empty
        ExpandedFailures = None
        LastError = None
    }

    (model, refresh ())

/// The transitions, with their remote calls as values.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | Refresh ->
        {
            model with
                DataTypes = Remote.Loading
        },
        refresh ()

    | DataTypesLoaded result ->
        {
            model with
                DataTypes = Remote.ofResult result
        },
        []

    | StatusesLoaded statuses ->
        {
            model with
                Statuses = statuses |> List.map (fun s -> s.DataTypeId, s) |> Map.ofList
        },
        []

    | TriggerClicked dataTypeId ->
        {
            model with
                Triggering = model.Triggering |> Set.add dataTypeId
                LastError = None
        },
        trigger dataTypeId

    | TriggerComplete(dataTypeId, Ok status) ->
        // The trigger returns the status it persisted, so the row
        // updates without a second round-trip.
        {
            model with
                Triggering = model.Triggering |> Set.remove dataTypeId
                Statuses = model.Statuses |> Map.add dataTypeId status
        },
        []

    | TriggerComplete(dataTypeId, Error message) ->
        {
            model with
                Triggering = model.Triggering |> Set.remove dataTypeId
                LastError = Some(Notice.Problem message)
        },
        []

    | ToggleFailures dataTypeId ->
        let next =
            match model.ExpandedFailures with
            | Some current when current = dataTypeId -> None
            | _ -> Some dataTypeId

        { model with ExpandedFailures = next }, []

    | DismissError -> { model with LastError = None }, []

// ─── View ───────────────────────────────────────────────────────────

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: MigrationStatusMessages
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./MigrationStatusTree.fs")

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "MigrationStatusTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.MigrationStatus
            Locale = ctx.Locale
        |}
    )

// ─── Module creation ────────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.DataMigrations"

let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Data Migrations"
        Icon = ToolUp.Platform.Icons.arrowUpwards
    }
    Group = Some "Platform Management"
    NavRole = Some ToolUp.Platform.NavRole.TeamOwnerAdmin
    Area = Product
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = fun _ -> init ()
    Update = update
    View = BuiltInView.Page view
    Extend = id
}
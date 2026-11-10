// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module DataIngestionUI

open System
open ToolUp.Elmish
open Feliz
open ToolUp.Platform
open Toolup.UIToolkit

// ─── Phase 10b — built-in data-ingestion admin ──────────────────────
//
// Sidebar entry that lists configured data sources, surfaces their
// credential status, and routes Connect / Disconnect actions through
// `IDataIngestionApi`. Per-Kind credential forms (OAuth Client ID +
// Secret entry, service-account upload, etc.) are contributed by
// connector companion packages via `DataSourceCredentialUIRegistry`.
//
// The module renders a static "Data Sources" tab; switching connector
// state (Connect / Disconnect) refreshes both the source list and the
// per-source credential statuses. Creation flows are delegated to
// the per-Kind credential UI plugin — the SDK admin module does not
// know the shape of any connector's `ConnectionScope` or credential
// material.

// ─── Model ──────────────────────────────────────────────────────────

type Model = {
    /// Configured data sources for the caller's scope.
    Sources: Remote<DataSourceConfig list, string>
    /// Credential status per data source. Loaded in parallel after
    /// the source list arrives. Missing entries render as "checking…"
    /// in the table.
    Statuses: Map<DataSourceId, CredentialStatus>
    /// Phase 10h — OAuth-refresh token-status per data source. Loaded
    /// in parallel alongside `Statuses` after the source list arrives.
    /// `None` for a key (the entry exists in the map with `None`
    /// payload) means the per-source server lookup returned no
    /// `IOAuthTokenRefresher` descriptor for the source (non-OAuth
    /// connector, or descriptor not yet registered); the column
    /// renders "—" in that case. A missing key means the lookup is
    /// still in flight; the column renders nothing rather than a
    /// noisy spinner so the row stays readable.
    TokenStatuses: Map<DataSourceId, TokenStatus option>
    /// The expanded row's data source id, if any. Clicking the same
    /// row twice collapses it.
    Expanded: DataSourceId option
    /// Transient error surfaced from Connect / Disconnect failures.
    LastError: Notice option
    /// Set of source ids currently mid-Disconnect — used to disable
    /// the button while the call is in flight.
    Disconnecting: Set<DataSourceId>
    /// When `Some kind`, the user has clicked an "Add data source"
    /// button for that Kind and the credential panel renders inline
    /// with `DataSource = None`. The credential UI is responsible for
    /// persisting a fresh `DataSourceConfig` via `IDataIngestionApi.
    /// SaveDataSource` and then writing credentials. `None` means
    /// no create flow is active.
    Creating: string option
}

type Msg =
    | RefreshSources
    | SourcesLoaded of Result<DataSourceConfig list, string>
    | StatusLoaded of DataSourceId * CredentialStatus
    | TokenStatusLoaded of DataSourceId * TokenStatus option
    | ToggleExpanded of DataSourceId
    | ConnectClicked of DataSourceId * string
    | ConnectUrlReady of DataSourceId * Result<string, string>
    | DisconnectClicked of DataSourceId
    | DisconnectComplete of DataSourceId * Result<unit, string>
    | DismissError
    | StartCreate of kind: string
    | CancelCreate

// ─── API proxy ──────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see `UserSession.withRequestHeaders` + `CsrfClient.installRequestGuard`.
let private dataIngestionApi: IDataIngestionApi =
    Api.makeProxy<IDataIngestionApi> (customOptions = UserSession.withRequestHeaders)

// ─── Init / update ──────────────────────────────────────────────────

let private loadSourcesCmd () =
    Cmd.OfRemoting.call dataIngestionApi.ListDataSources () (Ok >> SourcesLoaded) (fun e ->
        SourcesLoaded(Error e.Message))

let private loadStatusCmd (id: DataSourceId) =
    Cmd.OfRemoting.call dataIngestionApi.GetCredentialStatus id (fun s -> StatusLoaded(id, s)) (fun _ ->
        // Best-effort — status read failure renders as
        // "checking…" rather than a disruptive error banner.
        StatusLoaded(id, NotConfigured))

let private loadTokenStatusCmd (id: DataSourceId) =
    // Best-effort — a token-status read failure (server unavailable,
    // `IOAuthTokenRefresher` not registered, etc.) collapses to "no
    // descriptor", which the column renders as "—". Same noise-floor
    // rule as `loadStatusCmd`: failures never push a banner because
    // the column is supplementary, not load-bearing for the page.
    Cmd.OfRemoting.call dataIngestionApi.GetTokenStatus id (fun ts -> TokenStatusLoaded(id, ts)) (fun _ ->
        TokenStatusLoaded(id, None))

let init () =
    let model = {
        Sources = Remote.Loading
        Statuses = Map.empty
        TokenStatuses = Map.empty
        Expanded = None
        LastError = None
        Disconnecting = Set.empty
        Creating = None
    }

    model, loadSourcesCmd ()

let update (msg: Msg) (model: Model) =
    match msg with
    | RefreshSources ->
        {
            model with
                Sources = Remote.Loading
                Statuses = Map.empty
                TokenStatuses = Map.empty
                // Close the create panel after a successful save (the
                // credential UI calls Refresh() once persisted). Manual
                // refresh clicks also close it — same effect.
                Creating = None
        },
        loadSourcesCmd ()

    | SourcesLoaded(Ok sources) ->
        let statusCmds =
            sources
            |> List.collect (fun s -> [ loadStatusCmd s.Id; loadTokenStatusCmd s.Id ])
            |> Cmd.batch

        {
            model with
                Sources = Remote.Loaded sources
        },
        statusCmds

    | SourcesLoaded(Error msg) ->
        {
            model with
                Sources = Remote.Failed msg
        },
        Cmd.none

    | StatusLoaded(id, status) ->
        {
            model with
                Statuses = model.Statuses |> Map.add id status
        },
        Cmd.none

    | TokenStatusLoaded(id, tokenStatus) ->
        {
            model with
                TokenStatuses = model.TokenStatuses |> Map.add id tokenStatus
        },
        Cmd.none

    | ToggleExpanded id ->
        let next =
            match model.Expanded with
            | Some current when current = id -> None
            | _ -> Some id

        { model with Expanded = next }, Cmd.none

    | ConnectClicked(id, flowName) ->
        let cmd =
            Cmd.OfRemoting.call
                dataIngestionApi.BeginOAuth
                (id, flowName)
                (fun result -> ConnectUrlReady(id, result))
                (fun e -> ConnectUrlReady(id, Error e.Message))

        { model with LastError = None }, cmd

    | ConnectUrlReady(_, Ok url) ->
        // Navigate the browser to the upstream provider's authorize
        // endpoint. `assign` records a history entry; the user can
        // back-button to ToolUp if they cancel consent. Deferred through
        // a Cmd so `update` stays pure (effect runs after the model commits).
        model, Cmd.ofEffect (fun _ -> Browser.Dom.window.location.assign url)

    | ConnectUrlReady(_, Error msg) ->
        {
            model with
                LastError = Some(Notice.Problem msg)
        },
        Cmd.none

    | DisconnectClicked id ->
        let cmd =
            Cmd.OfRemoting.call dataIngestionApi.Disconnect id (fun result -> DisconnectComplete(id, result)) (fun e ->
                DisconnectComplete(id, Error e.Message))

        {
            model with
                Disconnecting = model.Disconnecting |> Set.add id
                LastError = None
        },
        cmd

    | DisconnectComplete(id, Ok()) ->
        // Re-load just this source's status so the row pill flips to
        // NeedsAuthorization. Re-load the token status too: Phase 10h
        // connectors unregister their `IOAuthTokenRefresher` descriptor
        // on `Disconnect`, so the column should collapse back to "—".
        {
            model with
                Disconnecting = model.Disconnecting |> Set.remove id
                TokenStatuses = model.TokenStatuses |> Map.remove id
        },
        Cmd.batch [ loadStatusCmd id; loadTokenStatusCmd id ]

    | DisconnectComplete(id, Error msg) ->
        {
            model with
                Disconnecting = model.Disconnecting |> Set.remove id
                LastError = Some(Notice.Problem msg)
        },
        Cmd.none

    | DismissError -> { model with LastError = None }, Cmd.none

    | StartCreate kind ->
        {
            model with
                Creating = Some kind
                LastError = None
        },
        Cmd.none

    | CancelCreate -> { model with Creating = None }, Cmd.none

// ─── View ───────────────────────────────────────────────────────────

let private statusPill (msgs: DataIngestionMessages) (status: CredentialStatus) =
    let label, cls =
        match status with
        | NotConfigured -> msgs.StatusNotConfigured, ("bg-gray-100 border-gray-200 " + Tokens.Text.secondary)
        | NeedsAuthorization -> msgs.StatusNeedsAuthorization, "bg-yellow-100 text-yellow-700 border-yellow-200"
        | Connected _ -> msgs.StatusConnected, "bg-green-100 text-green-700 border-green-200"
        | NeedsReauthorization _ -> msgs.StatusNeedsReauthorization, "bg-red-100 text-red-700 border-red-200"

    Html.span [
        prop.className $"inline-block text-xs px-2 py-0.5 rounded border font-medium {cls}"
        prop.text label
    ]

let private statusDetail (msgs: DataIngestionMessages) (status: CredentialStatus) =
    match status with
    | Connected at ->
        Html.span [
            prop.className ("text-xs " + Tokens.Text.muted)
            prop.text (msgs.SinceLabel(at.ToString("yyyy-MM-dd HH:mm") + " UTC"))
        ]
    | NeedsReauthorization reason -> Html.span [ prop.className "text-xs text-red-700"; prop.text reason ]
    | _ -> Html.none

// ─── Phase 10h — Token-status column ────────────────────────────────
//
// Last terminal `_platform.oauth.refresh` outcome + next scheduled
// refresh, derived server-side from `IOAuthTokenRefresher` +
// `IEventStore`. Three render shapes:
//
//   * `Map` entry absent → lookup still in flight → render nothing
//     (no spinner; the column is supplementary).
//   * `Some None` → no descriptor registered → render "—".
//   * `Some (Some status)` → render outcome pill + next-refresh detail.

let private tokenOutcomePill (msgs: DataIngestionMessages) (outcome: OAuthRefreshOutcome) =
    let label, cls =
        match outcome with
        | RefreshedOutcome _ -> msgs.OutcomeRefreshed, "bg-green-100 text-green-700 border-green-200"
        | TransientErrorOutcome _ -> msgs.OutcomeTransientError, "bg-yellow-100 text-yellow-700 border-yellow-200"
        | RequiresReauthOutcome _ -> msgs.OutcomeRequiresReauth, "bg-red-100 text-red-700 border-red-200"
        | DeadLetteredOutcome _ -> msgs.OutcomeDeadLettered, "bg-red-100 text-red-700 border-red-200"

    Html.span [
        prop.className $"inline-block text-xs px-2 py-0.5 rounded border font-medium {cls}"
        prop.text label
    ]

let private tokenStatusDetail (msgs: DataIngestionMessages) (status: TokenStatus) =
    // Two-line detail under the pill: the last-outcome timestamp (or
    // the failure reason for the error cases — operators want to see
    // why before they read when), then the next-scheduled-refresh.
    let outcomeLine =
        match status.LastOutcome, status.LastOutcomeAt with
        | Some(RefreshedOutcome _), Some at ->
            Html.span [
                prop.className ("text-xs " + Tokens.Text.muted)
                prop.text (msgs.AtLabel(at.ToString("yyyy-MM-dd HH:mm") + " UTC"))
            ]
        | Some(TransientErrorOutcome reason), _
        | Some(RequiresReauthOutcome reason), _
        | Some(DeadLetteredOutcome reason), _ -> Html.span [ prop.className "text-xs text-red-700"; prop.text reason ]
        | None, _ ->
            // Descriptor registered but no terminal audit row yet —
            // typically the first minute after Connect, before the
            // scheduled refresh fires.
            Html.span [
                prop.className ("text-xs " + Tokens.Text.muted)
                prop.text msgs.AwaitingFirstRefresh
            ]
        | _ -> Html.none

    let nextLine =
        match status.NextScheduledRefresh with
        | Some at ->
            Html.span [
                prop.className ("text-xs " + Tokens.Text.muted)
                prop.text (msgs.NextLabel(at.ToString("yyyy-MM-dd HH:mm") + " UTC"))
            ]
        | None -> Html.none

    Html.div [
        prop.className "flex flex-col gap-0.5"
        prop.children [ outcomeLine; nextLine ]
    ]

let private tokenStatusCell (msgs: DataIngestionMessages) (entry: TokenStatus option option) =
    match entry with
    | None ->
        // Lookup still pending. Empty cell keeps the row visually
        // calm; refreshing will populate it shortly.
        Html.none
    | Some None ->
        // No descriptor — non-OAuth connector or descriptor not yet
        // registered. Neutral dash, no styling.
        Html.span [ prop.className ("text-xs " + Tokens.Text.muted); prop.text "—" ]
    | Some(Some status) ->
        Html.div [
            prop.className "flex flex-col gap-0.5"
            prop.children [
                match status.LastOutcome with
                | Some outcome -> tokenOutcomePill msgs outcome
                | None ->
                    Html.span [
                        prop.className (
                            "inline-block text-xs px-2 py-0.5 rounded border font-medium bg-gray-100 border-gray-200 "
                            + Tokens.Text.secondary
                        )
                        prop.text msgs.OutcomePending
                    ]
                tokenStatusDetail msgs status
            ]
        ]

let private connectButton (msgs: DataIngestionMessages) (id: DataSourceId) (flowName: string) (dispatch: Msg -> unit) =
    Forms.Button.primaryWith Forms.Button.Enabled msgs.Connect (fun () -> dispatch (ConnectClicked(id, flowName)))

let private disconnectButton (msgs: DataIngestionMessages) (id: DataSourceId) (busy: bool) (dispatch: Msg -> unit) =
    Forms.Button.secondaryWith (Forms.Button.stateOf busy msgs.Disconnecting false) msgs.Disconnect (fun () ->
        dispatch (DisconnectClicked id))

/// Render the row's action button(s). For OAuth-shaped sources we
/// derive the flow name from the source's `Kind` by lowercasing +
/// kebab-casing — the convention companions follow (`Kind =
/// "GoogleAnalytics"` ↔ flow `Name = "google-analytics"`). Companions
/// that diverge from this convention can override by setting
/// `ConnectionScope["oauthFlow"]` to their flow name.
let private flowNameFor (config: DataSourceConfig) : string =
    match config.ConnectionScope |> Map.tryFind "oauthFlow" with
    | Some explicit -> explicit
    | None ->
        // PascalCase → kebab-case fallback. "GoogleAnalytics" →
        // "google-analytics"; "Stripe" → "stripe".
        let chars = config.Kind.ToCharArray()

        let buffer = System.Text.StringBuilder()

        for i in 0 .. chars.Length - 1 do
            let c = chars[i]

            if Char.IsUpper c && i > 0 then
                buffer.Append '-' |> ignore

            buffer.Append(Char.ToLowerInvariant c) |> ignore

        buffer.ToString()

let private rowActions
    (msgs: DataIngestionMessages)
    (config: DataSourceConfig)
    (status: CredentialStatus)
    (busy: bool)
    (dispatch: Msg -> unit)
    =
    Html.div [
        prop.className "flex gap-2 items-center"
        prop.children [
            match status with
            | NotConfigured
            | NeedsAuthorization
            | NeedsReauthorization _ -> connectButton msgs config.Id (flowNameFor config) dispatch
            | Connected _ -> disconnectButton msgs config.Id busy dispatch
        ]
    ]

let private credentialPanel (msgs: DataIngestionMessages) (config: DataSourceConfig) (refresh: unit -> unit) =
    match DataSourceCredentialUIRegistry.tryGet config.Kind with
    | Some renderer ->
        Html.div [
            prop.className "p-4 bg-gray-50 border-t border-border"
            prop.children [
                renderer {
                    DataSource = Some config
                    Refresh = refresh
                }
            ]
        ]
    | None ->
        Html.div [
            prop.className "p-4 bg-gray-50 border-t border-border"
            prop.children [
                Html.p [
                    prop.className ("text-sm " + Tokens.Text.secondary)
                    prop.text (msgs.NoCredentialUIForKind config.Kind)
                ]
            ]
        ]

/// The source table's columns. The name cell toggles the row's
/// credential panel, which renders under the table.
let private sourceColumns
    (msgs: DataIngestionMessages)
    (model: Model)
    (dispatch: Msg -> unit)
    : Data.Column<DataSourceConfig> list =
    let statusOf (config: DataSourceConfig) =
        model.Statuses |> Map.tryFind config.Id |> Option.defaultValue NotConfigured

    let mono (text: string) =
        Html.span [ prop.className [ "text-xs font-mono"; Tokens.Text.muted ]; prop.text text ]

    [
        {
            Header = msgs.ColumnStatus
            Align = Data.Left
            Cell =
                fun config ->
                    let status = statusOf config

                    Html.div [
                        prop.className "flex flex-col gap-0.5"
                        prop.children [ statusPill msgs status; statusDetail msgs status ]
                    ]
        }
        {
            Header = msgs.ColumnName
            Align = Data.Left
            Cell =
                fun config ->
                    Html.button [
                        prop.type' "button"
                        prop.ariaExpanded (model.Expanded = Some config.Id)
                        prop.className "font-medium text-left hover:underline"
                        prop.onClick (fun _ -> dispatch (ToggleExpanded config.Id))
                        prop.text config.Name
                    ]
        }
        {
            Header = msgs.ColumnId
            Align = Data.Left
            Cell = fun config -> mono config.Id
        }
        {
            Header = msgs.ColumnKind
            Align = Data.Left
            Cell = fun config -> mono config.Kind
        }
        {
            Header = msgs.ColumnTokenStatus
            Align = Data.Left
            Cell = fun config -> tokenStatusCell msgs (model.TokenStatuses |> Map.tryFind config.Id)
        }
        {
            Header = msgs.ColumnActions
            Align = Data.Left
            Cell =
                fun config ->
                    rowActions msgs config (statusOf config) (model.Disconnecting |> Set.contains config.Id) dispatch
        }
    ]

/// Inline credential panel for the create-flow. Renders the per-Kind
/// renderer with `DataSource = None` so the credential UI's "first
/// save" branch fires (it persists a fresh DataSourceConfig before
/// writing credentials). The Refresh callback flips `Creating` back
/// to None so the create panel collapses and the new row appears in
/// the table.
let private createCredentialPanel (msgs: DataIngestionMessages) (kind: string) (dispatch: Msg -> unit) =
    match DataSourceCredentialUIRegistry.tryGet kind with
    | Some renderer ->
        Html.div [
            prop.className "p-4 bg-gray-50 border border-border rounded-lg"
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between mb-3"
                    prop.children [
                        Html.h3 [
                            prop.className ("text-sm font-semibold " + Tokens.Text.primary)
                            prop.text (msgs.NewSourceHeading kind)
                        ]
                        Html.button [
                            prop.className ("text-xs hover:underline " + Tokens.Text.muted)
                            prop.text msgs.CancelCreate
                            prop.onClick (fun _ -> dispatch CancelCreate)
                        ]
                    ]
                ]
                renderer {
                    DataSource = None
                    Refresh = fun () -> dispatch RefreshSources
                }
            ]
        ]
    | None ->
        // Defensive: registry says this Kind exists but tryGet now
        // returns None (registration was overwritten). Surface a
        // helpful message rather than rendering nothing.
        Html.div [
            prop.className "p-4 bg-yellow-50 border border-yellow-200 rounded-lg text-sm text-yellow-800"
            prop.text (msgs.CredentialUIUnregistered kind)
        ]

/// Kind-selector strip. One button per registered Kind. Clicking
/// dispatches StartCreate, which expands the createCredentialPanel.
let private kindSelector (msgs: DataIngestionMessages) (dispatch: Msg -> unit) (creating: string option) =
    let kinds = DataSourceCredentialUIRegistry.registeredKinds ()

    if List.isEmpty kinds then
        Html.div [
            prop.className ("p-4 text-sm bg-gray-50 border border-border rounded-lg " + Tokens.Text.muted)
            prop.text msgs.NoCredentialUIsRegistered
        ]
    else
        Html.div [
            prop.className "flex items-center gap-2 flex-wrap"
            prop.children [
                Html.span [
                    prop.className ("text-sm mr-1 " + Tokens.Text.secondary)
                    prop.text msgs.AddDataSource
                ]
                for kind in kinds do
                    let isSelected = creating = Some kind

                    Html.button [
                        prop.className [
                            "px-3 py-1.5 text-sm font-medium rounded border transition-colors"
                            if isSelected then
                                "bg-brand text-white border-brand"
                            else
                                ("bg-white border-border hover:bg-gray-50 " + Tokens.Text.secondary)
                        ]
                        prop.text $"+ {kind}"
                        prop.onClick (fun _ ->
                            if isSelected then
                                dispatch CancelCreate
                            else
                                dispatch (StartCreate kind))
                    ]
            ]
        ]

let private sourcesTable
    (msgs: DataIngestionMessages)
    (model: Model)
    (dispatch: Msg -> unit)
    (sources: DataSourceConfig list)
    =
    if List.isEmpty sources then
        Html.div [
            prop.className ("text-sm space-y-3 " + Tokens.Text.muted)
            prop.children [
                Html.p [ prop.text msgs.NoSourcesYet ]
                kindSelector msgs dispatch model.Creating
                match model.Creating with
                | Some kind -> createCredentialPanel msgs kind dispatch
                | None -> Html.none
            ]
        ]
    else
        Html.div [
            prop.className "space-y-3"
            prop.children [
                // Add-data-source affordance above the existing-sources
                // table. Operators can wire a second connector or a
                // second instance of the same connector at any time.
                kindSelector msgs dispatch model.Creating
                match model.Creating with
                | Some kind -> createCredentialPanel msgs kind dispatch
                | None -> Html.none
                Html.div [
                    prop.className "border border-border rounded-lg overflow-hidden"
                    prop.children [
                        Data.table (sourceColumns msgs model dispatch) sources
                        match
                            model.Expanded
                            |> Option.bind (fun id -> sources |> List.tryFind (fun s -> s.Id = id))
                        with
                        | Some config ->
                            // Refresh just the source list + statuses; the
                            // user is editing this row so it stays expanded.
                            credentialPanel msgs config (fun () -> dispatch RefreshSources)
                        | None -> Html.none
                    ]
                ]
            ]
        ]

let private refreshButton (msgs: DataIngestionMessages) (loading: bool) (dispatch: Msg -> unit) =
    Forms.Button.secondaryWith (Forms.Button.stateOf loading msgs.Refreshing false) msgs.Refresh (fun () ->
        dispatch RefreshSources)

let private bodyView (msgs: DataIngestionMessages) (model: Model) (dispatch: Msg -> unit) =
    let loading = Remote.isLoading model.Sources
    let noticeMsgs = { Dismiss = msgs.DismissError }

    Html.div [
        prop.className "flex-1 p-6 overflow-y-auto"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between mb-4"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.SourcesHeading ]
                            Html.p [
                                prop.className ("text-xs " + Tokens.Text.muted)
                                prop.text msgs.OAuthFootnote
                            ]
                        ]
                    ]
                    refreshButton msgs loading dispatch
                ]
            ]
            match model.LastError with
            | Some _ ->
                Html.div [
                    prop.className "mb-4"
                    prop.children [
                        StateViews.notice noticeMsgs model.LastError (fun () -> dispatch DismissError)
                    ]
                ]
            | None -> Html.none
            match model.Sources with
            | Remote.NotAsked
            | Remote.Loading
            | Remote.Reloading _ -> Html.p [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
            | Remote.Failed msg ->
                StateViews.notice noticeMsgs (Some(Notice.Problem msg)) (fun () -> dispatch DismissError)
            | Remote.Loaded sources -> sourcesTable msgs model dispatch sources
        ]
    ]

/// The module body as a React COMPONENT, so it has a hook
/// site from which to read the resolved catalog. See `HealthMonitorUI`'s
/// equivalent for why a module's `view` cannot hold the hook itself.
[<ReactComponent>]
let private DataIngestionBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).DataIngestion

    Html.div [
        prop.className "flex flex-col h-full"
        prop.children [ bodyView msgs model dispatch ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement = DataIngestionBody model dispatch

// ─── Module creation ────────────────────────────────────────────────

/// Phase 879 — this built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `create` registers the module under it.
let moduleId = "_sdk.DataIngestion"

let create (config: ModuleLabel option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Data Sources"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.upload

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId moduleId
    |> ToolUp.Platform.ClientModule.withView (fun model ctx -> PageContent.FullWidth(view model ctx.Dispatch))
    // Phase 1112 — ingesting data is not team administration. The module
    // sits with the SDK's data managers ("Data Management", the group
    // `FileManagerUI` and `MappingDataManagerUI` default to) rather than
    // under "Team Management" with the people-and-permissions built-ins.
    // The nav role is unchanged: who may see it is a separate axis from
    // where it is filed. A deployment re-files it with
    // `ErasedModule.withGroup` on the created module.
    |> ToolUp.Platform.ClientModule.withGroup "Data Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
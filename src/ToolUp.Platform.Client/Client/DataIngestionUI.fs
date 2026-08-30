// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module DataIngestionUI

open System
open ToolUp.Elmish
open Feliz
open ToolUp.Platform

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

type LoadState<'T> =
    | NotLoaded
    | Loading
    | Loaded of 'T
    | LoadError of string

type Model = {
    /// Configured data sources for the caller's scope.
    Sources: LoadState<DataSourceConfig list>
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
    LastError: string option
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
        Sources = Loading
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
                Sources = Loading
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

        { model with Sources = Loaded sources }, statusCmds

    | SourcesLoaded(Error msg) -> { model with Sources = LoadError msg }, Cmd.none

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

    | ConnectUrlReady(_, Error msg) -> { model with LastError = Some msg }, Cmd.none

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
                LastError = Some msg
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
        | NotConfigured -> msgs.StatusNotConfigured, "bg-gray-100 text-gray-700 border-gray-200"
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
            prop.className "text-xs text-gray-500"
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
                prop.className "text-xs text-gray-500"
                prop.text (msgs.AtLabel(at.ToString("yyyy-MM-dd HH:mm") + " UTC"))
            ]
        | Some(TransientErrorOutcome reason), _
        | Some(RequiresReauthOutcome reason), _
        | Some(DeadLetteredOutcome reason), _ -> Html.span [ prop.className "text-xs text-red-700"; prop.text reason ]
        | None, _ ->
            // Descriptor registered but no terminal audit row yet —
            // typically the first minute after Connect, before the
            // scheduled refresh fires.
            Html.span [ prop.className "text-xs text-gray-500"; prop.text "awaiting first refresh" ]
        | _ -> Html.none

    let nextLine =
        match status.NextScheduledRefresh with
        | Some at ->
            Html.span [
                prop.className "text-xs text-gray-400"
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
        Html.span [ prop.className "text-xs text-gray-400"; prop.text "—" ]
    | Some(Some status) ->
        Html.div [
            prop.className "flex flex-col gap-0.5"
            prop.children [
                match status.LastOutcome with
                | Some outcome -> tokenOutcomePill msgs outcome
                | None ->
                    Html.span [
                        prop.className
                            "inline-block text-xs px-2 py-0.5 rounded border font-medium bg-gray-100 text-gray-700 border-gray-200"
                        prop.text msgs.OutcomePending
                    ]
                tokenStatusDetail msgs status
            ]
        ]

let private actionButton (label: string) (disabled: bool) (cls: string) (onClick: unit -> unit) =
    Html.button [
        prop.className [
            "px-3 py-1 text-xs font-medium rounded border transition-colors"
            cls
            if disabled then
                "opacity-50 cursor-not-allowed"
        ]
        prop.disabled disabled
        prop.text label
        prop.onClick (fun _ ->
            if not disabled then
                onClick ())
    ]

let private connectButton (msgs: DataIngestionMessages) (id: DataSourceId) (flowName: string) (dispatch: Msg -> unit) =
    actionButton msgs.Connect false "bg-brand text-white border-brand hover:bg-brand-dark" (fun () ->
        dispatch (ConnectClicked(id, flowName)))

let private disconnectButton (msgs: DataIngestionMessages) (id: DataSourceId) (busy: bool) (dispatch: Msg -> unit) =
    actionButton
        (if busy then msgs.Disconnecting else msgs.Disconnect)
        busy
        "bg-white text-gray-700 border-border hover:bg-gray-50"
        (fun () -> dispatch (DisconnectClicked id))

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
                    prop.className "text-sm text-gray-600"
                    prop.text (msgs.NoCredentialUIForKind config.Kind)
                ]
            ]
        ]

let private sourceRow
    (msgs: DataIngestionMessages)
    (model: Model)
    (dispatch: Msg -> unit)
    (config: DataSourceConfig)
    : ReactElement list =
    let status =
        model.Statuses |> Map.tryFind config.Id |> Option.defaultValue NotConfigured

    let tokenEntry = model.TokenStatuses |> Map.tryFind config.Id

    let isExpanded =
        match model.Expanded with
        | Some id -> id = config.Id
        | None -> false

    let busy = model.Disconnecting |> Set.contains config.Id

    [
        Html.tr [
            prop.className [ "border-t border-border hover:bg-gray-50 cursor-pointer" ]
            prop.onClick (fun _ -> dispatch (ToggleExpanded config.Id))
            prop.children [
                Html.td [
                    prop.className "px-3 py-2"
                    prop.children [
                        Html.div [
                            prop.className "flex flex-col gap-0.5"
                            prop.children [ statusPill msgs status; statusDetail msgs status ]
                        ]
                    ]
                ]
                Html.td [ prop.className "px-3 py-2 font-medium"; prop.text config.Name ]
                Html.td [
                    prop.className "px-3 py-2 text-xs text-gray-500 font-mono"
                    prop.text config.Id
                ]
                Html.td [
                    prop.className "px-3 py-2 text-xs text-gray-500 font-mono"
                    prop.text config.Kind
                ]
                Html.td [
                    prop.className "px-3 py-2"
                    prop.children [ tokenStatusCell msgs tokenEntry ]
                ]
                Html.td [
                    prop.className "px-3 py-2"
                    prop.onClick (fun e -> e.stopPropagation ())
                    prop.children [ rowActions msgs config status busy dispatch ]
                ]
            ]
        ]

        if isExpanded then
            Html.tr [
                prop.children [
                    Html.td [
                        prop.colSpan 6
                        prop.className "p-0"
                        prop.children [
                            credentialPanel msgs config (fun () ->
                                // Refresh just the source list +
                                // statuses; the user is editing this
                                // particular row so we keep it
                                // expanded.
                                dispatch RefreshSources)
                        ]
                    ]
                ]
            ]
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
                            prop.className "text-sm font-semibold text-gray-900"
                            prop.text (msgs.NewSourceHeading kind)
                        ]
                        Html.button [
                            prop.className "text-xs text-gray-500 hover:underline"
                            prop.text "cancel"
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
            prop.className "p-4 text-sm text-gray-500 bg-gray-50 border border-border rounded-lg"
            prop.text msgs.NoCredentialUIsRegistered
        ]
    else
        Html.div [
            prop.className "flex items-center gap-2 flex-wrap"
            prop.children [
                Html.span [ prop.className "text-sm text-gray-600 mr-1"; prop.text "Add data source:" ]
                for kind in kinds do
                    let isSelected = creating = Some kind

                    Html.button [
                        prop.className [
                            "px-3 py-1.5 text-sm font-medium rounded border transition-colors"
                            if isSelected then
                                "bg-brand text-white border-brand"
                            else
                                "bg-white text-gray-700 border-border hover:bg-gray-50"
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
            prop.className "text-sm text-gray-500 space-y-3"
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
                        Html.table [
                            prop.className "w-full text-sm"
                            prop.children [
                                Html.thead [
                                    prop.className "bg-gray-50"
                                    prop.children [
                                        Html.tr [
                                            prop.children [
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600 w-44"
                                                    prop.text msgs.ColumnStatus
                                                ]
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                    prop.text msgs.ColumnName
                                                ]
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                    prop.text "Id"
                                                ]
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                    prop.text msgs.ColumnKind
                                                ]
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600 w-48"
                                                    prop.text msgs.ColumnTokenStatus
                                                ]
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600 w-44"
                                                    prop.text msgs.ColumnActions
                                                ]
                                            ]
                                        ]
                                    ]
                                ]
                                Html.tbody [ prop.children (sources |> List.collect (sourceRow msgs model dispatch)) ]
                            ]
                        ]
                    ]
                ]
            ]
        ]

let private errorBanner (msg: string) (dispatch: Msg -> unit) =
    Html.div [
        prop.className
            "flex items-center justify-between p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm"
        prop.children [
            Html.span [ prop.text msg ]
            Html.button [
                prop.className "text-red-700 hover:underline text-xs"
                prop.text "dismiss"
                prop.onClick (fun _ -> dispatch DismissError)
            ]
        ]
    ]

let private refreshButton (msgs: DataIngestionMessages) (loading: bool) (dispatch: Msg -> unit) =
    Html.button [
        prop.className [
            "px-3 py-1.5 text-sm font-medium rounded border transition-colors"
            if loading then
                "bg-gray-100 text-gray-400 border-gray-200 cursor-not-allowed"
            else
                "bg-white text-gray-700 border-border hover:bg-gray-50"
        ]
        prop.disabled loading
        prop.text (if loading then msgs.Refreshing else msgs.Refresh)
        prop.onClick (fun _ -> dispatch RefreshSources)
    ]

let private bodyView (msgs: DataIngestionMessages) (model: Model) (dispatch: Msg -> unit) =
    let loading =
        match model.Sources with
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
                            Html.h2 [ prop.className "text-lg font-semibold"; prop.text "Data sources" ]
                            Html.p [ prop.className "text-xs text-gray-500"; prop.text msgs.OAuthFootnote ]
                        ]
                    ]
                    refreshButton msgs loading dispatch
                ]
            ]
            match model.LastError with
            | Some msg -> Html.div [ prop.className "mb-4"; prop.children [ errorBanner msg dispatch ] ]
            | None -> Html.none
            match model.Sources with
            | NotLoaded
            | Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text "Loading data sources..." ]
            | LoadError msg -> errorBanner msg dispatch
            | Loaded sources -> sourcesTable msgs model dispatch sources
        ]
    ]

/// Phase 444 — the module body as a React COMPONENT, so it has a hook
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

let create (config: DataIngestionAdminConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Data Sources"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.upload

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.DataIngestion"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Team Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module DataIngestionUI

open Fuaran.MVU
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── Built-in data-ingestion admin ──────────────────────────────────
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
    /// OAuth-refresh token-status per data source. Loaded
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

let private loadSources () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IDataIngestionApi.ListDataSources"
            dataIngestionApi.ListDataSources
            ()
            (Result.mapError _.Message >> SourcesLoaded)
    )
]

let private loadStatus (id: DataSourceId) : Step<Msg, RemoteEffect<Msg>> =
    Do(
        RemoteEffect.call "IDataIngestionApi.GetCredentialStatus" dataIngestionApi.GetCredentialStatus id (fun reply ->
            // Best-effort — status read failure renders as
            // "checking…" rather than a disruptive error banner.
            StatusLoaded(id, reply |> Result.defaultValue NotConfigured))
    )

let private loadTokenStatus (id: DataSourceId) : Step<Msg, RemoteEffect<Msg>> =
    // Best-effort — a token-status read failure (server unavailable,
    // `IOAuthTokenRefresher` not registered, etc.) collapses to "no
    // descriptor", which the column renders as "—". Same noise-floor
    // rule as `loadStatus`: failures never push a banner because
    // the column is supplementary, not load-bearing for the page.
    Do(
        RemoteEffect.call "IDataIngestionApi.GetTokenStatus" dataIngestionApi.GetTokenStatus id (fun reply ->
            TokenStatusLoaded(id, reply |> Result.defaultValue None))
    )

let init () : Model * Step<Msg, RemoteEffect<Msg>> list =
    ({
        Sources = Remote.Loading
        Statuses = Map.empty
        TokenStatuses = Map.empty
        Expanded = None
        LastError = None
        Disconnecting = Set.empty
        Creating = None
     },
     loadSources ())

/// The transitions, with their remote calls as values. The browser
/// navigation to an OAuth authorize URL is not among them: it answers no
/// message, which an effect value cannot yet say, so `update` keeps it.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
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
        loadSources ()

    | SourcesLoaded(Ok sources) ->
        {
            model with
                Sources = Remote.Loaded sources
        },
        sources |> List.collect (fun s -> [ loadStatus s.Id; loadTokenStatus s.Id ])

    | SourcesLoaded(Error msg) ->
        {
            model with
                Sources = Remote.Failed msg
        },
        []

    | StatusLoaded(id, status) ->
        {
            model with
                Statuses = model.Statuses |> Map.add id status
        },
        []

    | TokenStatusLoaded(id, tokenStatus) ->
        {
            model with
                TokenStatuses = model.TokenStatuses |> Map.add id tokenStatus
        },
        []

    | ToggleExpanded id ->
        let next =
            match model.Expanded with
            | Some current when current = id -> None
            | _ -> Some id

        { model with Expanded = next }, []

    | ConnectClicked(id, flowName) ->
        { model with LastError = None },
        [
            Do(
                RemoteEffect.call
                    "IDataIngestionApi.BeginOAuth"
                    dataIngestionApi.BeginOAuth
                    (id, flowName)
                    (fun reply -> ConnectUrlReady(id, RemoteEffect.flatten reply))
            )
        ]

    // `update` performs the navigation; see above.
    | ConnectUrlReady(_, Ok url) ->
        // Navigate the browser to the upstream provider's authorize
        // endpoint. `assign` records a history entry; the user can
        // back-button to ToolUp if they cancel consent.
        model,
        [
            Do(
                RemoteEffect.signal ("DataIngestionUI.authorize " + url) (fun () ->
                    Browser.Dom.window.location.assign url)
            )
        ]

    | ConnectUrlReady(_, Error msg) ->
        {
            model with
                LastError = Some(Notice.Problem msg)
        },
        []

    | DisconnectClicked id ->
        {
            model with
                Disconnecting = model.Disconnecting |> Set.add id
                LastError = None
        },
        [
            Do(
                RemoteEffect.call "IDataIngestionApi.Disconnect" dataIngestionApi.Disconnect id (fun reply ->
                    DisconnectComplete(id, RemoteEffect.flatten reply))
            )
        ]

    | DisconnectComplete(id, Ok()) ->
        // Re-load just this source's status so the row pill flips to
        // NeedsAuthorization. Re-load the token status too: OAuth-refresh
        // connectors unregister their `IOAuthTokenRefresher` descriptor
        // on `Disconnect`, so the column should collapse back to "—".
        {
            model with
                Disconnecting = model.Disconnecting |> Set.remove id
                TokenStatuses = model.TokenStatuses |> Map.remove id
        },
        [ loadStatus id; loadTokenStatus id ]

    | DisconnectComplete(id, Error msg) ->
        {
            model with
                Disconnecting = model.Disconnecting |> Set.remove id
                LastError = Some(Notice.Problem msg)
        },
        []

    | DismissError -> { model with LastError = None }, []

    | StartCreate kind ->
        {
            model with
                Creating = Some kind
                LastError = None
        },
        []

    | CancelCreate -> { model with Creating = None }, []

// ─── View ───────────────────────────────────────────────────────────

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: DataIngestionMessages
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./DataIngestionTree.fs")

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "DataIngestionTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.DataIngestion
            Locale = ctx.Locale
        |}
    )

// ─── Module creation ────────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.DataIngestion"

let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Data Sources"
        Icon = ToolUp.Platform.Icons.upload
    }
    Group = Some "Data Management"
    NavRole = Some ToolUp.Platform.NavRole.TeamOwnerAdmin
    Area = Product
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = fun _ -> init ()
    Update = update
    View = BuiltInView.Page view
    Extend = id
}
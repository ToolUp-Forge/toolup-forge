// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The data-ingestion admin as a Fuaran-UI tree, built and drawn in a file of
/// its own, which `DataIngestionUI`'s view mounts through
/// `TreeSurface.deferred` (the shell reads that module at boot; see
/// `TeamManagerTree` for the trade). Nothing imports this file statically.
///
/// A source's row shows the source and its own entries in the model (its
/// credential status, its token status, its disconnect in flight, whether its
/// credential panel is open), so a row is kept on those
/// (`KeepScoped.RowInputs`): one source's status arriving builds that row
/// alone. Row ids come from the source's id and every row message carries it.
///
/// A connector's credential form is the connector's own React element
/// (`DataSourceCredentialHandler`, contributed through
/// `DataSourceCredentialUIRegistry`), so it is hosted as an island: the tree
/// draws the panel around it and hands it the refresh it calls once it saved.
module internal DataIngestionTree

open System
open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open DataIngestionUI
open KeepScoped

/// An instant to the minute, written from its components.
let private minute (at: DateTime) =
    sprintf "%04d-%02d-%02d %02d:%02d UTC" at.Year at.Month at.Day at.Hour at.Minute

let private badge (id: string) (variant: BadgeVariant) (label: string) : Node<'Msg> =
    Fuaran.badge id {
        Label = Nodes.literal label
        Variant = variant
    }

let private statusBadge (id: string) (msgs: DataIngestionMessages) (status: CredentialStatus) =
    match status with
    | NotConfigured -> badge id BadgeVariant.Neutral msgs.StatusNotConfigured
    | NeedsAuthorization -> badge id BadgeVariant.Warning msgs.StatusNeedsAuthorization
    | Connected _ -> badge id BadgeVariant.Success msgs.StatusConnected
    | NeedsReauthorization _ -> badge id BadgeVariant.Critical msgs.StatusNeedsReauthorization

let private outcomeBadge (id: string) (msgs: DataIngestionMessages) (outcome: OAuthRefreshOutcome option) =
    match outcome with
    | Some(RefreshedOutcome _) -> badge id BadgeVariant.Success msgs.OutcomeRefreshed
    | Some(TransientErrorOutcome _) -> badge id BadgeVariant.Warning msgs.OutcomeTransientError
    | Some(RequiresReauthOutcome _) -> badge id BadgeVariant.Critical msgs.OutcomeRequiresReauth
    | Some(DeadLetteredOutcome _) -> badge id BadgeVariant.Critical msgs.OutcomeDeadLettered
    | None -> badge id BadgeVariant.Neutral msgs.OutcomePending

/// The flow a source's Connect begins: the source's own `oauthFlow` scope
/// entry, else its `Kind` kebab-cased, the convention companions follow
/// (`"GoogleAnalytics"` is the flow `"google-analytics"`).
let private flowNameFor (config: DataSourceConfig) : string =
    match config.ConnectionScope |> Map.tryFind "oauthFlow" with
    | Some explicit -> explicit
    | None ->
        config.Kind
        |> Seq.mapi (fun i c ->
            if Char.IsUpper c && i > 0 then
                "-" + string (Char.ToLowerInvariant c)
            else
                string (Char.ToLowerInvariant c))
        |> String.concat ""

// ─── A source's row ──────────────────────────────────────────────────

/// What one source's row reads: the source and its own entries in the model.
type private SourceRow = {
    Msgs: DataIngestionMessages
    Config: DataSourceConfig
    Status: CredentialStatus
    /// Absent while the lookup is in flight; `Some None` when no refresh
    /// descriptor is registered for the source.
    Token: TokenStatus option option
    Busy: bool
    Expanded: bool
}

let private inputs =
    RowInputs(
        (fun (row: SourceRow) -> row.Config.Id),
        fun a b ->
            sameObject a.Msgs b.Msgs
            && sameObject a.Config b.Config
            && a.Status = b.Status
            && a.Token = b.Token
            && a.Busy = b.Busy
            && a.Expanded = b.Expanded
    )

let private columns (msgs: DataIngestionMessages) : Nodes.Table.Column list = [
    Nodes.Table.column "status" msgs.ColumnStatus 3
    Nodes.Table.column "name" msgs.ColumnName 3
    Nodes.Table.column "id" msgs.ColumnId 3
    Nodes.Table.column "kind" msgs.ColumnKind 2
    Nodes.Table.column "token" msgs.ColumnTokenStatus 3
    Nodes.Table.column "actions" msgs.ColumnActions 2
]

/// The token-status cell: nothing while the lookup is in flight, a dash with
/// no descriptor, else the last outcome and the next refresh. An error
/// outcome shows its reason before its time: an operator wants the why first.
let private tokenCell (id: string) (msgs: DataIngestionMessages) (entry: TokenStatus option option) : Node<Msg> list =
    match entry with
    | None -> []
    | Some None -> [ Nodes.text (Nodes.Ids.child id "none") "—" ]
    | Some(Some status) -> [
        outcomeBadge (Nodes.Ids.child id "outcome") msgs status.LastOutcome
        match status.LastOutcome, status.LastOutcomeAt with
        | Some(RefreshedOutcome _), Some at -> Nodes.text (Nodes.Ids.child id "at") (msgs.AtLabel(minute at))
        | Some(TransientErrorOutcome reason), _
        | Some(RequiresReauthOutcome reason), _
        | Some(DeadLetteredOutcome reason), _ -> Nodes.text (Nodes.Ids.child id "reason") reason
        | None, _ -> Nodes.text (Nodes.Ids.child id "awaiting") msgs.AwaitingFirstRefresh
        | _ -> ()
        match status.NextScheduledRefresh with
        | Some at -> Nodes.text (Nodes.Ids.child id "next") (msgs.NextLabel(minute at))
        | None -> ()
      ]

let private sourceRows =
    Keep.create (fun (row: SourceRow) ->
        let msgs = row.Msgs
        let config = row.Config
        let id = Nodes.Ids.keyed "ingestion-source" config.Id

        let cell part children =
            Nodes.Table.cell (Nodes.Ids.child id part) children

        Nodes.Table.row id (columns msgs) [
            cell "status" [
                statusBadge (Nodes.Ids.child id "status-badge") msgs row.Status
                match row.Status with
                | Connected at -> Nodes.text (Nodes.Ids.child id "since") (msgs.SinceLabel(minute at))
                | NeedsReauthorization reason -> Nodes.text (Nodes.Ids.child id "reason") reason
                | _ -> ()
            ]
            // The name toggles the source's credential panel, drawn under the table.
            cell "name" [
                Nodes.Actions.button
                    (Nodes.Ids.child id "toggle")
                    ButtonVariant.Tertiary
                    config.Name
                    false
                    (ToggleExpanded config.Id)
                |> Node.withExtraAttribute "aria-expanded" (if row.Expanded then "true" else "false")
            ]
            cell "id" [ Nodes.text (Nodes.Ids.child id "id-text") config.Id ]
            cell "kind" [ Nodes.text (Nodes.Ids.child id "kind-text") config.Kind ]
            cell "token" (tokenCell (Nodes.Ids.child id "token") msgs row.Token)
            cell "actions" [
                match row.Status with
                | NotConfigured
                | NeedsAuthorization
                | NeedsReauthorization _ ->
                    Nodes.Actions.primary
                        (Nodes.Ids.child id "connect")
                        msgs.Connect
                        (ConnectClicked(config.Id, flowNameFor config))
                | Connected _ ->
                    Nodes.Actions.button
                        (Nodes.Ids.child id "disconnect")
                        ButtonVariant.Secondary
                        (if row.Busy then msgs.Disconnecting else msgs.Disconnect)
                        row.Busy
                        (DisconnectClicked config.Id)
            ]
        ])

// ─── The credential panels ───────────────────────────────────────────

/// The open source's credential panel: its connector's form, hosted, handed
/// the refresh it calls once it saved (the row stays open while it is
/// edited), else a line saying no form is registered for its kind.
let private credentialPanel (msgs: DataIngestionMessages) (config: DataSourceConfig) (dispatch: Msg -> unit) =
    let id = Nodes.Ids.keyed "ingestion-credentials" config.Id

    Fuaran.box id {
        Defaults.box with
            Role = BoxRole.Card
            Children = [
                match DataSourceCredentialUIRegistry.tryGet config.Kind with
                | Some form ->
                    TreeLowering.island (Nodes.Ids.child id "form") (fun () ->
                        form {
                            DataSource = Some config
                            Refresh = fun () -> dispatch RefreshSources
                        })
                | None -> Nodes.text (Nodes.Ids.child id "none") (msgs.NoCredentialUIForKind config.Kind)
            ]
    }

/// The create flow's panel: the chosen kind's form with no source, which
/// saves a fresh one before its credentials; its refresh closes the panel and
/// lists the new source.
let private createPanel (msgs: DataIngestionMessages) (kind: string) (dispatch: Msg -> unit) =
    match DataSourceCredentialUIRegistry.tryGet kind with
    | Some form ->
        Fuaran.box "ingestion-create" {
            Defaults.box with
                Role = BoxRole.Card
                Layout = BoxLayout.Flex(Orientation.Vertical, false, Some 12)
                Children = [
                    Fuaran.box "ingestion-create-header" {
                        Defaults.box with
                            Layout = BoxLayout.Grid(2, Some "minmax(0, 1fr) auto", Some 16)
                            Children = [
                                Nodes.heading "ingestion-create-title" 3 (msgs.NewSourceHeading kind)
                                Nodes.Actions.button
                                    "ingestion-create-cancel"
                                    ButtonVariant.Tertiary
                                    msgs.CancelCreate
                                    false
                                    CancelCreate
                            ]
                    }
                    TreeLowering.island "ingestion-create-form" (fun () ->
                        form {
                            DataSource = None
                            Refresh = fun () -> dispatch RefreshSources
                        })
                ]
        }
    | None ->
        // The registry listed the kind, but its form was since replaced: say
        // so rather than draw nothing.
        Fuaran.callout "ingestion-create" {
            Defaults.callout with
                Body = Nodes.literal (msgs.CredentialUIUnregistered kind)
                Tone = ToneVariant.Warning
        }

/// One add button per registered kind; the chosen kind's button closes the
/// create flow again. The kinds are strings, which cannot key a `Keep`, so the
/// strip is kept whole on them and on the kind being created.
let private kindSelector =
    One(
        (fun (msgs: DataIngestionMessages, kinds: string list, creating: string option) (m2, k2, c2) ->
            sameObject msgs m2 && kinds = k2 && creating = c2),
        fun (msgs, kinds, creating) ->
            match kinds with
            | [] -> Nodes.text "ingestion-kinds" msgs.NoCredentialUIsRegistered
            | kinds ->
                Nodes.Layout.row "ingestion-kinds" [
                    Nodes.text "ingestion-kinds-label" msgs.AddDataSource
                    for kind in kinds do
                        let chosen = creating = Some kind

                        Nodes.Actions.button
                            (Nodes.Ids.keyed "ingestion-kind" kind)
                            (if chosen then
                                 ButtonVariant.Primary
                             else
                                 ButtonVariant.Secondary)
                            ("+ " + kind)
                            false
                            (if chosen then CancelCreate else StartCreate kind)
                ]
    )

// ─── The page ────────────────────────────────────────────────────────

let private sources
    (msgs: DataIngestionMessages)
    (model: Model)
    (dispatch: Msg -> unit)
    (configs: DataSourceConfig list)
    : Node<Msg> list =
    [
        if List.isEmpty configs then
            Nodes.text "ingestion-none" msgs.NoSourcesYet

        kindSelector.Node(msgs, DataSourceCredentialUIRegistry.registeredKinds (), model.Creating)

        match model.Creating with
        | Some kind -> createPanel msgs kind dispatch
        | None -> ()

        if not (List.isEmpty configs) then
            let rows =
                configs
                |> List.map (fun config -> {
                    Msgs = msgs
                    Config = config
                    Status = model.Statuses |> Map.tryFind config.Id |> Option.defaultValue NotConfigured
                    Token = model.TokenStatuses |> Map.tryFind config.Id
                    Busy = model.Disconnecting.Contains config.Id
                    Expanded = model.Expanded = Some config.Id
                })
                |> inputs.Of

            Nodes.Table.table "ingestion-table" msgs.SourcesHeading (columns msgs) (Keep.list sourceRows rows)

            match
                model.Expanded
                |> Option.bind (fun id -> configs |> List.tryFind (fun c -> c.Id = id))
            with
            | Some config -> credentialPanel msgs config dispatch
            | None -> ()
    ]

/// The page: the heading and its refresh, the notice, then the sources or the
/// state they are in. Every node but the hosted forms sends its message
/// through the tree; a form calls `dispatch` with the refresh.
let tree (msgs: DataIngestionMessages) (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
    let loading = Remote.isLoading model.Sources

    let banner (tone: ToneVariant) (text: string) =
        Nodes.Actions.banner "ingestion-banner" tone text msgs.DismissError DismissError

    Nodes.Layout.column "ingestion" [
        Fuaran.box "ingestion-header" {
            Defaults.box with
                Layout = BoxLayout.Grid(2, Some "minmax(0, 1fr) auto", Some 16)
                Children = [
                    Nodes.Layout.column "ingestion-titles" [
                        Nodes.heading "ingestion-heading" 2 msgs.SourcesHeading
                        Nodes.text "ingestion-footnote" msgs.OAuthFootnote
                    ]
                    Nodes.Actions.button
                        "ingestion-refresh"
                        ButtonVariant.Secondary
                        (if loading then msgs.Refreshing else msgs.Refresh)
                        loading
                        RefreshSources
                ]
        }
        match model.LastError with
        | Some(Notice.Problem text) -> banner ToneVariant.Critical text
        | Some(Notice.Info text) -> banner ToneVariant.Info text
        | None -> ()
        match model.Sources with
        | Remote.NotAsked
        | Remote.Loading
        | Remote.Reloading _ -> Nodes.text "ingestion-loading" msgs.Loading
        | Remote.Failed message ->
            Nodes.Actions.banner "ingestion-failed" ToneVariant.Critical message msgs.DismissError DismissError
        | Remote.Loaded configs -> yield! sources msgs model dispatch configs
    ]

/// The deferred component `DataIngestionUI`'s view mounts.
let Surface (props: DataIngestionUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Model props.Dispatch)
    )
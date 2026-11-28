// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The platform-users admin as a Fuaran-UI tree, built and drawn in a file of
/// its own, which `PlatformUsersUI`'s view mounts through `TreeSurface.deferred`
/// (the shell reads that module at boot; see `TeamManagerTree` for the trade).
/// Nothing imports this file statically.
///
/// The principal rows and the offboard dialog's two tables are kept, every row
/// id comes from the record's identity and every row message carries it. What
/// stays React is an island: the dialog's reason and confirmation-token fields
/// hold display state in a hook, committed on leaving the field or on Enter,
/// so free typing does not round-trip through `update`.
module internal PlatformUsersTree

open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open PlatformUsersUI
open KeepScoped

let private callout (id: string) (tone: ToneVariant) (heading: string option) (body: string) : Node<'Msg> =
    Fuaran.callout id {
        Defaults.callout with
            Body = Nodes.literal body
            Tone = tone
            Heading = heading |> Option.map Nodes.literal
    }

let private badge (id: string) (variant: BadgeVariant) (label: string) : Node<'Msg> =
    Fuaran.badge id {
        Label = Nodes.literal label
        Variant = variant
    }

/// The list's filter: a checkbox chip sending `msg` when it is toggled. A chip
/// whose change carries a message dispatches it and writes no filter store.
let private filter (id: string) (label: string) (value: bool) (msg: Msg) : Node<Msg> =
    Fuaran.filters id [
        {
            Kind = FormFieldKind.checkboxControl (Binding.Static(Some value), onChange = (fun _ -> Action.dispatch msg))
            Label = Nodes.literal label
            Name = id
        }
    ]

/// One record of a list: a card with what the record is on the left and its
/// actions on the right, on a two-track grid so the actions keep their width.
let private listRow (id: string) (children: Node<'Msg> list) : Node<'Msg> =
    Fuaran.box id {
        Defaults.box with
            Role = BoxRole.Card
            Children = [
                Fuaran.box (Nodes.Ids.child id "grid") {
                    Defaults.box with
                        Layout = BoxLayout.Grid(2, Some "minmax(0, 1fr) auto", Some 16)
                        Children = children
                }
            ]
    }

// ─── The principal rows ──────────────────────────────────────────────

/// Membership summary, "3 teams · Admin, Member", or that there is none.
let private membershipLabel (msgs: PlatformUsersMessages) (p: PrincipalSummary) : string =
    if p.TeamLess then
        msgs.NoTeams
    else
        let roles =
            p.Memberships
            |> List.map (fun (_, role) -> TeamRoles.displayName role)
            |> List.distinct
            |> String.concat ", "

        msgs.MembershipSummary (List.length p.Memberships) roles

/// A date as its explicit components: custom-format rendering is unreliable
/// under Fable across runtimes.
let private formatDate (at: System.DateTime) =
    sprintf "%04d-%02d-%02d" at.Year at.Month at.Day

type private RowContext = {
    Msgs: PlatformUsersMessages
    Directory: UserDirectory.Model
}

/// One principal: who it is, what the substrate holds of it, and the three
/// ways into its offboard dialog.
let private principalRows =
    Rows(
        (fun (a: RowContext) b -> sameObject a.Msgs b.Msgs && sameObject a.Directory b.Directory),
        fun context (p: PrincipalSummary) ->
            let msgs = context.Msgs
            let id = Nodes.Ids.keyed "principal" p.UserId
            let primary, secondary = UserDirectory.labels context.Directory p.UserId

            let lastSeen = p.LastSeenAt |> Option.map formatDate |> Option.defaultValue "—"

            listRow id [
                Nodes.Layout.column (Nodes.Ids.child id "about") [
                    Nodes.Layout.row (Nodes.Ids.child id "title") [
                        Nodes.text (Nodes.Ids.child id "name") primary
                        if p.TeamLess then
                            badge (Nodes.Ids.child id "team-less") BadgeVariant.Warning msgs.TeamLessBadge
                        if p.HasUserScopeData then
                            badge (Nodes.Ids.child id "has-data") BadgeVariant.Neutral msgs.HasDataBadge
                    ]
                    match secondary with
                    | Some email -> Nodes.text (Nodes.Ids.child id "email") email
                    | None -> ()
                    Nodes.text (Nodes.Ids.child id "summary") (msgs.RowSubtitle (membershipLabel msgs p) lastSeen)
                    Nodes.text (Nodes.Ids.child id "id") p.UserId
                ]
                Nodes.Layout.row (Nodes.Ids.child id "actions") [
                    Nodes.Actions.secondary
                        (Nodes.Ids.child id "preview")
                        msgs.PreviewAction
                        (OpenOffboard(p.UserId, PlainOffboard))
                    Nodes.Actions.secondary
                        (Nodes.Ids.child id "export")
                        msgs.ExportOffboardAction
                        (OpenOffboard(p.UserId, ExportOffboard))
                    Nodes.Actions.danger
                        (Nodes.Ids.child id "offboard")
                        msgs.OffboardAction
                        (OpenOffboard(p.UserId, PlainOffboard))
                ]
            ]
    )

// ─── The offboard dialog's tables ────────────────────────────────────

let private previewColumns (msgs: PlatformUsersMessages) : Nodes.Table.Column list = [
    Nodes.Table.column "hook" msgs.ColumnHook 3
    Nodes.Table.column "affects" msgs.ColumnWouldAffect 2
    Nodes.Table.column "detail" msgs.ColumnDetail 4
]

/// What each hook would remove, per hook.
let private previewRows =
    Rows(
        (fun (a: PlatformUsersMessages) b -> sameObject a b),
        fun msgs (item: LifecyclePreviewItem) ->
            let id = Nodes.Ids.keyed "preview" item.HookName

            Nodes.Table.row id (previewColumns msgs) [
                Nodes.Table.cell (Nodes.Ids.child id "hook") [ Nodes.text (Nodes.Ids.child id "name") item.HookName ]
                Nodes.Table.cell (Nodes.Ids.child id "affects") [
                    if not item.HasPreview then
                        badge (Nodes.Ids.child id "count") BadgeVariant.Neutral msgs.NoPreviewBadge
                    elif item.WouldAffect = 0 then
                        badge (Nodes.Ids.child id "count") BadgeVariant.Success "0"
                    else
                        badge (Nodes.Ids.child id "count") BadgeVariant.Warning (string item.WouldAffect)
                ]
                Nodes.Table.cell (Nodes.Ids.child id "detail") [
                    Nodes.text (Nodes.Ids.child id "detail-text") item.Detail
                ]
            ]
    )

let private outcomeColumns (msgs: PlatformUsersMessages) : Nodes.Table.Column list = [
    Nodes.Table.column "hook" msgs.ColumnHook 3
    Nodes.Table.column "result" msgs.ColumnResult 2
    Nodes.Table.column "detail" msgs.ColumnDetail 4
]

/// How each hook ended, per hook.
let private outcomeRows =
    Rows(
        (fun (a: PlatformUsersMessages) b -> sameObject a b),
        fun msgs (outcome: LifecycleHookOutcome) ->
            let id = Nodes.Ids.keyed "outcome" outcome.HookName

            let result, detail =
                match outcome.Result with
                | LifecycleHookResult.Completed ->
                    badge (Nodes.Ids.child id "result-badge") BadgeVariant.Success msgs.OutcomeCompleted, ""
                | LifecycleHookResult.Skipped reason ->
                    badge (Nodes.Ids.child id "result-badge") BadgeVariant.Warning msgs.OutcomeSkipped, reason
                | LifecycleHookResult.Failed error ->
                    badge (Nodes.Ids.child id "result-badge") BadgeVariant.Critical msgs.OutcomeFailed, error

            Nodes.Table.row id (outcomeColumns msgs) [
                Nodes.Table.cell (Nodes.Ids.child id "hook") [ Nodes.text (Nodes.Ids.child id "name") outcome.HookName ]
                Nodes.Table.cell (Nodes.Ids.child id "result") [ result ]
                Nodes.Table.cell (Nodes.Ids.child id "detail") [ Nodes.text (Nodes.Ids.child id "detail-text") detail ]
            ]
    )

let private summary
    (msgs: PlatformUsersMessages)
    (result: LifecycleSummary)
    (archive: LifecycleExportArchive option)
    : Node<Msg> list =
    [
        Nodes.Layout.row "offboard-counts" [
            badge
                "offboard-completed"
                BadgeVariant.Success
                (msgs.CompletedCount(LifecycleSummary.completedCount result))
            badge "offboard-skipped" BadgeVariant.Warning (msgs.SkippedCount(LifecycleSummary.skippedCount result))
            badge "offboard-failed" BadgeVariant.Critical (msgs.FailedCount(LifecycleSummary.failedCount result))
            badge "offboard-elapsed" BadgeVariant.Neutral (sprintf "%d ms" (int result.TotalElapsedMs))
        ]
        match archive with
        | Some a ->
            Nodes.Layout.column "offboard-archive" [
                callout
                    "offboard-archive-written"
                    ToneVariant.Info
                    (Some(msgs.ExportArchiveWritten a.SegmentCount))
                    (sprintf "%s/%s" a.Container a.BlobPath)
                Nodes.text "offboard-archive-hash" ("sha256: " + a.ContentHash)
            ]
        | None -> ()
        if List.isEmpty result.Outcomes then
            Nodes.text "offboard-no-hooks" msgs.NoHooksRan
        else
            Nodes.Table.table
                "offboard-outcomes"
                msgs.OffboardCompleteTitle
                (outcomeColumns msgs)
                (outcomeRows.List msgs result.Outcomes)
    ]

// ─── The offboard dialog ─────────────────────────────────────────────

let private primaryLabel (msgs: PlatformUsersMessages) (state: OffboardModalState) =
    if state.Busy then
        msgs.Working
    else
        match state.Step, state.Kind with
        | Confirming, _ -> msgs.ConfirmOffboard
        | _, PlainOffboard -> msgs.OffboardAction
        | _, ExportOffboard -> msgs.ExportOffboardAction

/// The per-principal offboard flow: compose a reason, preview the impact,
/// confirm with a token where the server asks for one, then the outcome.
let private offboard
    (msgs: PlatformUsersMessages)
    (label: string)
    (state: OffboardModalState)
    (dispatch: Msg -> unit)
    : Node<Msg> =
    let heading =
        match state.Step with
        | Completed _ -> msgs.OffboardCompleteTitle
        | _ ->
            match state.Kind with
            | PlainOffboard -> msgs.OffboardTitle
            | ExportOffboard -> msgs.ExportOffboardTitle

    let subject =
        Nodes.Layout.row "offboard-subject" [
            Nodes.text "offboard-subject-label" (msgs.SubjectLabel label)
            Nodes.text "offboard-subject-scope" state.ScopeId
        ]

    let error =
        match state.Error with
        | Some(Notice.Problem text) -> [ callout "offboard-error" ToneVariant.Critical None text ]
        | Some(Notice.Info text) -> [ callout "offboard-error" ToneVariant.Info None text ]
        | None -> []

    let reason () =
        TreeIslands.textField
            "offboard-reason"
            msgs.ReasonLabel
            msgs.ReasonPlaceholder
            state.Reason
            (SetOffboardReason >> dispatch)

    let cancel = Nodes.Actions.secondary "offboard-cancel" msgs.Cancel CloseOffboard

    let submit =
        Nodes.Actions.button
            "offboard-submit"
            ButtonVariant.Destructive
            (primaryLabel msgs state)
            state.Busy
            SubmitOffboard

    let body, actions =
        match state.Step with
        | Compose ->
            [ subject; reason (); yield! error ],
            [
                cancel
                Nodes.Actions.button
                    "offboard-preview"
                    ButtonVariant.Secondary
                    msgs.PreviewImpact
                    state.Busy
                    RequestPreview
                submit
            ]
        | Previewed preview ->
            [
                subject
                Nodes.text "offboard-preview-summary" (msgs.PreviewSummary preview.TotalWouldAffect)
                Nodes.Table.table
                    "offboard-preview-table"
                    msgs.PreviewImpact
                    (previewColumns msgs)
                    (previewRows.List msgs preview.Items)
                reason ()
                yield! error
            ],
            [ cancel; submit ]
        | Confirming ->
            [
                subject
                callout
                    "offboard-confirmation"
                    ToneVariant.Warning
                    (Some msgs.ConfirmationRequiredHeading)
                    msgs.ConfirmationRequiredBody
                match state.Kind with
                | ExportOffboard -> Nodes.text "offboard-confirmation-export" msgs.ExportConfirmationNote
                | PlainOffboard -> ()
                Nodes.Layout.row "offboard-token" [
                    TreeIslands.textField
                        "offboard-token-field"
                        msgs.ConfirmationTokenLabel
                        msgs.ConfirmationTokenPlaceholder
                        state.Token
                        (SetOffboardToken >> dispatch)
                    Nodes.Actions.button
                        "offboard-token-request"
                        ButtonVariant.Secondary
                        msgs.RequestToken
                        state.Busy
                        RequestToken
                ]
                yield! error
            ],
            [ cancel; submit ]
        | Completed(result, archive) ->
            [ subject; yield! summary msgs result archive ],
            [ Nodes.Actions.primary "offboard-close" msgs.Close CloseOffboard ]

    Fuaran.modal "offboard" {
        Defaults.modal with
            Open = Binding.Static(Some true)
            Heading = Some(Nodes.literal heading)
            OnDismiss = Some(Action.dispatch CloseOffboard)
            Children = body @ [ Nodes.Layout.row "offboard-actions" actions ]
    }

// ─── The page ────────────────────────────────────────────────────────

/// The page: the filter, the principals and an open offboard dialog.
/// `dispatch` reaches the dialog's islands only; every node sends its message
/// through the tree.
let tree (msgs: PlatformUsersMessages) (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
    Nodes.Layout.column "platform-users" [
        Nodes.heading "platform-users-heading" 2 msgs.Heading
        Nodes.text "platform-users-subheading" msgs.Subheading
        match model.Error with
        | Some(Notice.Problem text) ->
            Nodes.Actions.banner "platform-users-error" ToneVariant.Critical text msgs.Dismiss DismissError
        | Some(Notice.Info text) ->
            Nodes.Actions.banner "platform-users-error" ToneVariant.Info text msgs.Dismiss DismissError
        | None -> ()
        Nodes.Layout.row "principals-filter" [
            filter "principals-team-less" msgs.TeamLessOnly model.TeamLessOnly ToggleTeamLessOnly
            Nodes.Actions.button "principals-refresh" ButtonVariant.Tertiary msgs.Refresh false LoadPrincipals
        ]
        match model.Principals with
        | None -> Nodes.text "principals-loading" msgs.LoadingPrincipals
        | Some [] ->
            // No principals, or the offboard substrate is off (the banner says why).
            Nodes.Layout.column "principals-none" [
                Nodes.text "principals-none-heading" msgs.NoPrincipalsHeading
                Nodes.text "principals-none-body" msgs.NoPrincipalsBody
            ]
        | Some principals ->
            let visible =
                if model.TeamLessOnly then
                    principals |> List.filter _.TeamLess
                else
                    principals

            if List.isEmpty visible then
                Nodes.text "principals-none-team-less" msgs.NoTeamLessPrincipals
            else
                Nodes.Layout.column
                    "principals"
                    (principalRows.List
                        {
                            Msgs = msgs
                            Directory = model.Directory
                        }
                        visible)
        match model.Offboard with
        | Some state -> offboard msgs (fst (UserDirectory.labels model.Directory state.UserId)) state dispatch
        | None -> ()
    ]

/// The deferred component `PlatformUsersUI`'s view mounts.
let Surface (props: PlatformUsersUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Model props.Dispatch)
    )
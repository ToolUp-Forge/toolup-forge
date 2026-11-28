// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The data-subject-request admin as a Fuaran-UI tree, built and drawn in a
/// file of its own, which `DataSubjectRequestAdminUI`'s view mounts through
/// `TreeSurface.deferred` (the shell reads that module at boot; see
/// `TeamManagerTree` for the trade). Nothing imports this file statically.
///
/// The erasure preview and the run summary each list one row per handler, from
/// a map that arrives whole with its preview or summary and never changes in
/// place, so each panel is kept whole on the record it shows, its row ids from
/// the handler's name. The subject, team and reason fields and the async-mode
/// checkbox are islands (`TreeIslands`).
module internal DataSubjectRequestAdminTree

open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open ToolUp.Platform.DataSubjectRequestApi
open DataSubjectRequestAdminUI
open KeepScoped

let private badge (id: string) (variant: BadgeVariant) (label: string) : Node<'Msg> =
    Fuaran.badge id {
        Label = Nodes.literal label
        Variant = variant
    }

/// One handler of a preview or a run: its name on the left, what it did or
/// would do on the right.
let private handlerRow (id: string) (name: string) (outcome: Node<'Msg>) : Node<'Msg> =
    Fuaran.box id {
        Defaults.box with
            Role = BoxRole.Card
            Children = [
                Fuaran.box (Nodes.Ids.child id "grid") {
                    Defaults.box with
                        Layout = BoxLayout.Grid(2, Some "minmax(0, 1fr) auto", Some 16)
                        Children = [ Nodes.text (Nodes.Ids.child id "name") name; outcome ]
                }
            ]
    }

/// The subject, team and reason, shared by both flows. The catalog names each
/// by its placeholder alone, so a label only assistive technology reads names
/// it by the same text.
let private subjectFields (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    let field (id: string) (placeholder: string) (value: string) (msg: string -> Msg) =
        TreeIslands.hiddenLabelTextField id placeholder placeholder value (msg >> dispatch)

    Nodes.Layout.column "dsr-subject" [
        field "dsr-subject-id" msgs.SubjectPlaceholder model.SubjectInput SetSubjectInput
        field "dsr-subject-team" msgs.TeamPlaceholder model.TeamInput SetTeamInput
        field "dsr-subject-reason" msgs.ReasonPlaceholder model.ReasonInput SetReasonInput
    ]

/// A button that shows `busyLabel` while its own call is in flight and is
/// inert while any call is.
let private submit
    (id: string)
    (variant: ButtonVariant)
    (label: string)
    (busyLabel: string)
    (mine: bool)
    (model: Model)
    msg
    =
    Nodes.Actions.button id variant (if mine then busyLabel else label) (model.Busy <> Idle) msg

// ─── Export ──────────────────────────────────────────────────────────

/// The in-flight background export: its ticket's status, auto-polled, with a
/// cancel while it is still being prepared. The download starts by itself when
/// the ticket is ready.
let private activeTicket (msgs: DataSubjectRequestAdminMessages) (model: Model) : Node<Msg> list =
    match model.ActiveTicket, model.TicketStatus with
    | Some ticket, Some status -> [
        handlerRow
            "dsr-ticket"
            msgs.BackgroundExportHeading
            (Nodes.Layout.row "dsr-ticket-status" [
                Nodes.text "dsr-ticket-line" (msgs.TicketLine ticket (ticketStatusLabel msgs status))
                match status with
                | ExportStatus.Preparing -> Nodes.Actions.secondary "dsr-ticket-cancel" msgs.Cancel CancelAsyncExport
                | _ -> ()
            ])
      ]
    | _ -> []

let private exportTab (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    Nodes.Panel.card "dsr-export" msgs.ExportPanelTitle [
        Nodes.text "dsr-export-body" msgs.ExportPanelBody
        subjectFields msgs model dispatch
        TreeIslands.checkbox "dsr-export-async" msgs.AsyncModeLabel model.AsyncMode false (fun () ->
            dispatch (SetAsyncMode(not model.AsyncMode)))
        Nodes.Layout.column "dsr-export-actions" [
            submit
                "dsr-export-submit"
                ButtonVariant.Primary
                msgs.RequestExport
                msgs.Exporting
                (model.Busy = RunningExport)
                model
                SubmitExport
            if model.Busy = RunningExport then
                Nodes.text "dsr-export-aggregating" msgs.AggregatingSegments
        ]
        yield! activeTicket msgs model
    ]

// ─── Erase ───────────────────────────────────────────────────────────

/// The policy override: the deployment default or one of the three policies,
/// a radio each with what it means under it, the chosen one checked.
let private policyChoice (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    let choice (label: string) (description: string) (value: ErasurePolicy option) : TreeIslands.Radio = {
        Label = label
        Description = description
        Chosen = (model.OverridePolicy = value)
        OnChoose = fun () -> dispatch (SetOverridePolicy value)
    }

    Nodes.Layout.column "dsr-policy" [
        Nodes.text "dsr-policy-prompt" msgs.OverridePolicyPrompt
        TreeIslands.radios "dsr-policy-choices" "dsr-policy" [
            choice msgs.UseDeploymentDefault msgs.UseDeploymentDefaultDescription None
            for policy in
                [
                    ErasurePolicy.HardDelete
                    ErasurePolicy.Tombstone
                    ErasurePolicy.RetainPerCompliance
                ] do
                choice (policyLabel msgs policy) (policyDescription msgs policy) (Some policy)
        ]
    ]

type private PreviewContext = {
    Msgs: DataSubjectRequestAdminMessages
    Preview: ErasurePreview
    Busy: Busy
}

/// The pending erasure: what each handler would remove, then the confirm.
let private previewPanel =
    One(
        (fun (a: PreviewContext) b -> sameObject a.Msgs b.Msgs && sameObject a.Preview b.Preview && a.Busy = b.Busy),
        fun context ->
            let msgs = context.Msgs
            let preview = context.Preview
            let total = preview.PerHandlerCounts.Values |> Seq.sumBy _.RecordsAffected

            Nodes.Panel.card "dsr-preview" msgs.PreviewPanelTitle [
                Fuaran.box "dsr-preview-head" {
                    Defaults.box with
                        Layout = BoxLayout.Grid(2, Some "minmax(0, 1fr) auto", Some 16)
                        Children = [
                            Nodes.Layout.column "dsr-preview-request" [
                                Nodes.text "dsr-preview-id" (msgs.RequestIdLine preview.Request.Id)
                                Nodes.text
                                    "dsr-preview-summary"
                                    (msgs.PreviewSummaryLine
                                        (policyLabel msgs preview.Request.Policy)
                                        total
                                        preview.PerHandlerCounts.Count)
                            ]
                            Nodes.Actions.button
                                "dsr-preview-cancel"
                                ButtonVariant.Secondary
                                msgs.Cancel
                                (context.Busy = RunningConfirm)
                                CancelPreview
                        ]
                }
                if preview.PerHandlerCounts.IsEmpty then
                    Nodes.text "dsr-preview-empty" msgs.PreviewEmpty
                else
                    Nodes.Layout.column "dsr-preview-handlers" [
                        for KeyValue(name, summary) in preview.PerHandlerCounts ->
                            let id = Nodes.Ids.keyed "dsr-preview-handler" name

                            handlerRow
                                id
                                name
                                (Nodes.text
                                    (Nodes.Ids.child id "affected")
                                    (msgs.HandlerRecordsAffected summary.RecordsAffected))
                    ]
                Nodes.Layout.column "dsr-preview-confirm" [
                    Nodes.text "dsr-preview-footnote" msgs.ConfirmIrreversibleFootnote
                    Nodes.Actions.button
                        "dsr-preview-confirm-erase"
                        ButtonVariant.Destructive
                        (if context.Busy = RunningConfirm then
                             msgs.Confirming
                         else
                             msgs.ConfirmErase)
                        (context.Busy <> Idle)
                        SubmitConfirm
                ]
            ]
    )

type private SummaryContext = {
    Msgs: DataSubjectRequestAdminMessages
    Summary: ErasureRunSummary
}

/// The confirmed erasure: when it ran, and how each handler ended.
let private runSummaryPanel =
    One(
        (fun (a: SummaryContext) b -> sameObject a.Msgs b.Msgs && sameObject a.Summary b.Summary),
        fun context ->
            let msgs = context.Msgs
            let summary = context.Summary

            let overall =
                if summary.OverallSuccess then
                    msgs.OverallSuccess
                else
                    msgs.OverallPartialFailure

            Nodes.Panel.card "dsr-run" msgs.RunSummaryPanelTitle [
                Nodes.text
                    "dsr-run-line"
                    (msgs.RunSummaryLine
                        (universal summary.StartedAt.UtcDateTime)
                        (universal summary.CompletedAt.UtcDateTime)
                        overall)
                Nodes.Layout.column "dsr-run-handlers" [
                    for KeyValue(name, outcome) in summary.PerHandler ->
                        let id = Nodes.Ids.keyed "dsr-run-handler" name

                        let outcomeBadge =
                            match outcome with
                            | Ok s ->
                                badge
                                    (Nodes.Ids.child id "outcome")
                                    BadgeVariant.Success
                                    (msgs.HandlerRecordsAffected s.RecordsAffected)
                            | Error err ->
                                badge (Nodes.Ids.child id "outcome") BadgeVariant.Critical (ErasureError.toMessage err)

                        handlerRow id name outcomeBadge
                ]
            ]
    )

let private eraseTab (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    Nodes.Layout.column "dsr-erase-tab" [
        Nodes.Panel.card "dsr-erase" msgs.ErasePanelTitle [
            Nodes.text "dsr-erase-body" msgs.ErasePanelBody
            subjectFields msgs model dispatch
            policyChoice msgs model dispatch
            Nodes.Layout.column "dsr-erase-actions" [
                submit
                    "dsr-erase-preview"
                    ButtonVariant.Primary
                    msgs.PreviewErase
                    msgs.Previewing
                    (model.Busy = RunningPreview)
                    model
                    SubmitPreview
                if model.PendingPreview.IsSome then
                    Nodes.text "dsr-erase-pending" msgs.PendingPreviewHint
            ]
        ]
        match model.PendingPreview with
        | Some preview ->
            previewPanel.Node {
                Msgs = msgs
                Preview = preview
                Busy = model.Busy
            }
        | None -> ()
        match model.LastRunSummary with
        | Some summary -> runSummaryPanel.Node { Msgs = msgs; Summary = summary }
        | None -> ()
    ]

// ─── The page ────────────────────────────────────────────────────────

let private tabs = [ ExportTab; EraseTab ]

/// The page: the notice, then the two tabs with the active one's panel.
/// `dispatch` reaches the islands only; every node sends its message through
/// the tree.
let tree (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
    let panel (tab: Tab) =
        if tab <> model.ActiveTab then
            Nodes.Layout.column (sprintf "dsr-tab-%A" tab) []
        else
            Nodes.Layout.column (sprintf "dsr-tab-%A" tab) [
                match tab with
                | ExportTab -> exportTab msgs model dispatch
                | EraseTab -> eraseTab msgs model dispatch
            ]

    Nodes.Layout.column "dsr" [
        match model.Banner with
        | Some(Notice.Problem text) ->
            Nodes.Actions.banner "dsr-banner" ToneVariant.Critical text msgs.DismissBanner DismissBanner
        | Some(Notice.Info text) ->
            Nodes.Actions.banner "dsr-banner" ToneVariant.Info text msgs.DismissBanner DismissBanner
        | None -> ()
        Fuaran.tabs "dsr-tabs" {
            Defaults.tabs with
                ActiveIndex = Binding.Static(Some(List.findIndex ((=) model.ActiveTab) tabs))
                OnSelect = Some(fun i -> Action.dispatch (SwitchTab tabs[i]))
                TabHeaders =
                    Some [
                        for label in [ msgs.TabExport; msgs.TabErase ] ->
                            {
                                Label = Nodes.literal label
                                Icon = None
                                Disabled = None
                            }
                    ]
                Children = List.map panel tabs
        }
    ]

/// The deferred component `DataSubjectRequestAdminUI`'s view mounts.
let Surface (props: DataSubjectRequestAdminUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Model props.Dispatch)
    )
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module DataSubjectRequestAdminUI

open Fuaran.MVU
open System
open ToolUp.Elmish
open Feliz
open Browser
open Browser.Types
open Fable.Core.JsInterop
open Toolup.UIToolkit
open ToolUp.Platform
open ToolUp.Platform.DataSubjectRequestApi

// ─── DSR admin module (client) ───────────────────────────────────────
//
// Admin-facing UI for GDPR Article 15 (export) + Article 17 (erase).
// Two-tab layout — "Export" + "Erase" — mirrors the API's two flows:
//
//   - Export: fill subject + reason, submit, browser downloads the
//     export archive bytes. One round trip.
//
//   - Erase: fill subject + reason (+ optional policy override),
//     submit Preview to see per-store affected counts, then Confirm
//     to execute. The preview's `Request.Id` is the correlation token
//     the server's in-process preview cache hands back to Confirm.
//
// Owner / Admin gating is enforced server-side by the handler; the
// sidebar entry renders for every authenticated caller in
// non-Anonymous modes and the API surface refuses non-admin writes
// with a `Result.Error` that the banner displays inline. Anonymous
// mode is filtered out at module registration in
// `SDK.Client.prepareModules` (no persistent scope to attach a
// request to).

// ─── Model ───────────────────────────────────────────────────────────

type Tab =
    | ExportTab
    | EraseTab

/// What the page is currently doing. Disables form submission while a
/// round-trip is in flight so the admin can't double-submit.
type Busy =
    | Idle
    | RunningExport
    | RunningPreview
    | RunningConfirm

type Model = {
    ActiveTab: Tab
    /// Shared form state — subject id and reason fields are reused
    /// across both flows. Team id is optional everywhere (empty =
    /// `None`, populated = `Some <trimmed>`).
    SubjectInput: string
    TeamInput: string
    ReasonInput: string
    /// Erase-only — overrides the deployment policy default for a
    /// single request. `None` ⇒ use deployment default.
    OverridePolicy: ErasurePolicy option
    /// Latest erasure preview, kept until the admin Confirms,
    /// Cancels, or starts a new Preview. Drives the Confirm panel.
    PendingPreview: ErasurePreview option
    /// Latest confirmed-erasure summary. Persisted so the admin can
    /// inspect per-handler counts after confirmation lands.
    LastRunSummary: ErasureRunSummary option
    /// When true, the Export tab submits through the async
    /// background-job path (`RequestExportAsync` → poll `GetExportStatus`
    /// → `DownloadExport`) so large-tenant exports don't hit the HTTP
    /// timeout. Requires `DataSubjectRequests = Enabled { Async = true }`
    /// server-side; if disabled there, the first call returns an inline
    /// error in the banner.
    AsyncMode: bool
    /// In-flight background-export ticket + its last polled status.
    ActiveTicket: ExportTicket option
    TicketStatus: ExportStatus option
    Busy: Busy
    /// Status notice after a flow completes; cleared on the next action or
    /// dismissed by the user.
    Banner: Notice option
    /// This module's section of `ClientModuleContext.Messages`,
    /// kept from `init` so `update` authors reader-facing text in the
    /// session's language (a locale switch re-initialises the module).
    Messages: DataSubjectRequestAdminMessages
}

type Msg =
    | SwitchTab of Tab
    | SetSubjectInput of string
    | SetTeamInput of string
    | SetReasonInput of string
    | SetOverridePolicy of ErasurePolicy option
    | SubmitExport
    | ExportResolved of Result<byte[], string>
    | SetAsyncMode of bool
    | AsyncExportStarted of Result<ExportTicket, string>
    | PollTicket
    | TicketStatusResolved of Result<ExportStatus, string>
    | DownloadResolved of Result<byte[], string>
    | CancelAsyncExport
    | CancelResolved of Result<unit, string>
    | SubmitPreview
    | PreviewResolved of Result<ErasurePreview, string>
    | CancelPreview
    | SubmitConfirm
    | ConfirmResolved of Result<ErasureRunResult, string>
    | DismissBanner

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see `UserSession.withRequestHeaders` + `CsrfClient.installRequestGuard`.
let private dsrApi: IDataSubjectRequestApi =
    Api.makeProxy<IDataSubjectRequestApi> (customOptions = UserSession.withRequestHeaders)

// ─── Helpers ─────────────────────────────────────────────────────────

let private trimToOption (s: string) =
    let t = s.Trim()
    if t = "" then None else Some t

/// Trigger a browser download for the export archive returned by the
/// API. The MVP envelope is JSON `{ segments: [...] }`; the file
/// extension is `.json` until we ship the optional zip-pack mode.
let private downloadExport (subject: string) (bytes: byte[]) =
    let blobObj: obj =
        emitJsExpr (bytes) "new Blob([new Uint8Array($0)], { type: 'application/json' })"

    let url: string = emitJsExpr blobObj "URL.createObjectURL($0)"
    let anchor = document.createElement "a" :?> HTMLAnchorElement
    anchor.href <- url

    let stamp = DateTime.UtcNow.ToString "yyyy-MM-dd"
    let safeSubject = subject.Replace("/", "_").Replace("\\", "_")
    anchor?download <- sprintf "dsr-export-%s-%s.json" safeSubject stamp

    document.body.appendChild anchor |> ignore
    anchor.click ()
    document.body.removeChild anchor |> ignore
    emitJsExpr<unit> url "URL.revokeObjectURL($0)"

/// Display projection of `ErasurePolicy`. The DU itself stays wire-shaped
/// (parsed, matched, sent to the API); this is only the rendered label.
let private policyLabel (msgs: DataSubjectRequestAdminMessages) =
    function
    | ErasurePolicy.HardDelete -> msgs.PolicyHardDeleteLabel
    | ErasurePolicy.Tombstone -> msgs.PolicyTombstoneLabel
    | ErasurePolicy.RetainPerCompliance -> msgs.PolicyRetainPerComplianceLabel

let private policyDescription (msgs: DataSubjectRequestAdminMessages) =
    function
    | ErasurePolicy.HardDelete -> msgs.PolicyHardDeleteDescription
    | ErasurePolicy.Tombstone -> msgs.PolicyTombstoneDescription
    | ErasurePolicy.RetainPerCompliance -> msgs.PolicyRetainPerComplianceDescription

// ─── Init / update ───────────────────────────────────────────────────

let init (ctx: ClientModuleContext) : Model * Cmd<Msg> =
    let model = {
        ActiveTab = ExportTab
        SubjectInput = ""
        TeamInput = ""
        ReasonInput = ""
        OverridePolicy = None
        PendingPreview = None
        LastRunSummary = None
        AsyncMode = false
        ActiveTicket = None
        TicketStatus = None
        Busy = Idle
        Banner = None
        Messages = ctx.Messages.DataSubjectRequestAdmin
    }

    model, Cmd.none

type internal Steps = Step<Msg, RemoteEffect<Msg>> list

/// Re-poll the active background-export ticket after a short
/// delay. The loop runs while a ticket stays `Preparing`.
let internal pollDelay: Steps = [
    Do(
        RemoteEffect.legacy "DataSubjectRequestAdminUI.pollDelay" (fun dispatch ->
            Async.StartImmediate(
                async {
                    do! Async.Sleep 1500
                    dispatch PollTicket
                }
            ))
    )
]

let internal requestExport (input: ExportRequestInput) : Steps = [
    Do(
        RemoteEffect.call
            "IDataSubjectRequestApi.RequestExport"
            dsrApi.RequestExport
            input
            (RemoteEffect.flatten >> ExportResolved)
    )
]

let internal requestExportAsync (input: ExportRequestInput) : Steps = [
    Do(
        RemoteEffect.call
            "IDataSubjectRequestApi.RequestExportAsync"
            dsrApi.RequestExportAsync
            input
            (RemoteEffect.flatten >> AsyncExportStarted)
    )
]

let internal getExportStatus (ticket: ExportTicket) : Steps = [
    Do(
        RemoteEffect.call
            "IDataSubjectRequestApi.GetExportStatus"
            dsrApi.GetExportStatus
            ticket
            (RemoteEffect.flatten >> TicketStatusResolved)
    )
]

let internal downloadTicket (ticket: ExportTicket) : Steps = [
    Do(
        RemoteEffect.call
            "IDataSubjectRequestApi.DownloadExport"
            dsrApi.DownloadExport
            ticket
            (RemoteEffect.flatten >> DownloadResolved)
    )
]

let internal cancelExport (ticket: ExportTicket) : Steps = [
    Do(
        RemoteEffect.call
            "IDataSubjectRequestApi.CancelExport"
            dsrApi.CancelExport
            ticket
            (RemoteEffect.flatten >> CancelResolved)
    )
]

let internal previewErasure (input: ErasureRequestInput) : Steps = [
    Do(
        RemoteEffect.call
            "IDataSubjectRequestApi.PreviewErasure"
            dsrApi.PreviewErasure
            input
            (RemoteEffect.flatten >> PreviewResolved)
    )
]

let internal confirmErasure (requestId: DataSubjectRequestId) : Steps = [
    Do(
        RemoteEffect.call
            "IDataSubjectRequestApi.ConfirmErasure"
            dsrApi.ConfirmErasure
            requestId
            (RemoteEffect.flatten >> ConfirmResolved)
    )
]

/// The browser download of an export archive. It dispatches nothing.
let internal download (subject: string) (bytes: byte[]) : Steps = [
    Do(RemoteEffect.legacy "DataSubjectRequestAdminUI.downloadExport" (fun _ -> downloadExport subject bytes))
]

let private exportInput (model: Model) : ExportRequestInput = {
    SubjectUserId = model.SubjectInput.Trim()
    TeamId = trimToOption model.TeamInput
    Reason = model.ReasonInput.Trim()
}

let private erasureInput (model: Model) : ErasureRequestInput = {
    SubjectUserId = model.SubjectInput.Trim()
    TeamId = trimToOption model.TeamInput
    Reason = model.ReasonInput.Trim()
    OverridePolicy = model.OverridePolicy
}

let private validateForm (model: Model) =
    let msgs = model.Messages

    if model.SubjectInput.Trim() = "" then
        Some msgs.SubjectRequired
    elif model.ReasonInput.Trim() = "" then
        Some msgs.ReasonRequired
    else
        None

/// The background-export ticket's lifecycle: queued, polled while it is
/// `Preparing`, downloaded once `Ready`, or ended (failed, cancelled,
/// expired). `update` routes exactly these messages here.
let private stepBackgroundExport (msg: Msg) (model: Model) : Model * Steps =
    let msgs = model.Messages

    let ticketEnded (status: ExportStatus) (banner: Notice) =
        {
            model with
                Busy = Idle
                ActiveTicket = None
                TicketStatus = Some status
                Banner = Some banner
        },
        []

    match msg with
    | AsyncExportStarted(Ok ticket) ->
        {
            model with
                Busy = RunningExport
                ActiveTicket = Some ticket
                TicketStatus = Some ExportStatus.Preparing
                Banner = Some(Notice.Info msgs.BackgroundExportQueued)
        },
        pollDelay

    | AsyncExportStarted(Result.Error err) ->
        {
            model with
                Busy = Idle
                Banner = Some(Notice.Problem err)
        },
        []

    | PollTicket ->
        match model.ActiveTicket with
        | None -> model, []
        | Some ticket -> model, getExportStatus ticket

    | TicketStatusResolved(Ok status) ->
        match status with
        | ExportStatus.Ready _ ->
            match model.ActiveTicket with
            | Some ticket ->
                {
                    model with
                        TicketStatus = Some status
                },
                downloadTicket ticket
            | None ->
                {
                    model with
                        TicketStatus = Some status
                },
                []
        | ExportStatus.Preparing ->
            {
                model with
                    TicketStatus = Some status
            },
            pollDelay
        | ExportStatus.Failed reason -> ticketEnded status (Notice.Problem(msgs.ExportFailed reason))
        | ExportStatus.Cancelled -> ticketEnded status (Notice.Info msgs.ExportCancelled)
        | ExportStatus.Expired
        | ExportStatus.Unknown -> ticketEnded status (Notice.Problem msgs.ExportTicketExpiredOrUnknown)

    | TicketStatusResolved(Result.Error err) ->
        {
            model with
                Busy = Idle
                ActiveTicket = None
                Banner = Some(Notice.Problem err)
        },
        []

    | DownloadResolved(Ok bytes) ->
        let subject = model.SubjectInput.Trim()

        {
            model with
                Busy = Idle
                ActiveTicket = None
                TicketStatus = None
                Banner = Some(Notice.Info(msgs.BackgroundExportReady bytes.Length))
        },
        download subject bytes

    | DownloadResolved(Result.Error err) ->
        {
            model with
                Busy = Idle
                ActiveTicket = None
                Banner = Some(Notice.Problem err)
        },
        []

    | CancelAsyncExport ->
        match model.ActiveTicket with
        | None -> model, []
        | Some ticket -> model, cancelExport ticket

    | CancelResolved(Ok()) ->
        {
            model with
                Busy = Idle
                ActiveTicket = None
                TicketStatus = Some ExportStatus.Cancelled
                Banner = Some(Notice.Info msgs.ExportCancelled)
        },
        []

    | CancelResolved(Result.Error err) ->
        {
            model with
                Banner = Some(Notice.Problem err)
        },
        []

    | _ -> model, []

/// The transitions, with their remote calls as values.
let internal step (msg: Msg) (model: Model) : Model * Steps =
    // The catalog is a pure input carried on the model from `init`.
    let msgs = model.Messages

    match msg with
    | SwitchTab tab ->
        // Keep form contents; clear any pending preview to avoid
        // confusion when switching contexts.
        {
            model with
                ActiveTab = tab
                PendingPreview = None
                Banner = None
        },
        []

    | SetSubjectInput value -> { model with SubjectInput = value }, []

    | SetTeamInput value -> { model with TeamInput = value }, []

    | SetReasonInput value -> { model with ReasonInput = value }, []

    | SetOverridePolicy policy -> { model with OverridePolicy = policy }, []

    | SubmitExport ->
        match validateForm model with
        | Some err ->
            {
                model with
                    Banner = Some(Notice.Problem err)
            },
            []
        | None when model.AsyncMode ->
            {
                model with
                    Busy = RunningExport
                    Banner = None
                    ActiveTicket = None
                    TicketStatus = None
            },
            requestExportAsync (exportInput model)
        | None ->
            {
                model with
                    Busy = RunningExport
                    Banner = None
            },
            requestExport (exportInput model)

    | SetAsyncMode value -> { model with AsyncMode = value }, []

    | AsyncExportStarted _
    | PollTicket
    | TicketStatusResolved _
    | DownloadResolved _
    | CancelAsyncExport
    | CancelResolved _ -> stepBackgroundExport msg model

    | ExportResolved(Ok bytes) ->
        let subject = model.SubjectInput.Trim()

        {
            model with
                Busy = Idle
                Banner = Some(Notice.Info(msgs.ExportReady bytes.Length))
        },
        download subject bytes

    | ExportResolved(Result.Error err) ->
        {
            model with
                Busy = Idle
                Banner = Some(Notice.Problem err)
        },
        []

    | SubmitPreview ->
        match validateForm model with
        | Some err ->
            {
                model with
                    Banner = Some(Notice.Problem err)
            },
            []
        | None ->
            {
                model with
                    Busy = RunningPreview
                    Banner = None
                    PendingPreview = None
            },
            previewErasure (erasureInput model)

    | PreviewResolved(Ok preview) ->
        {
            model with
                Busy = Idle
                PendingPreview = Some preview
                Banner = None
        },
        []

    | PreviewResolved(Result.Error err) ->
        {
            model with
                Busy = Idle
                Banner = Some(Notice.Problem err)
        },
        []

    | CancelPreview ->
        {
            model with
                PendingPreview = None
                Banner = None
        },
        []

    | SubmitConfirm ->
        match model.PendingPreview with
        | None ->
            {
                model with
                    Banner = Some(Notice.Problem msgs.RunPreviewFirst)
            },
            []
        | Some preview ->
            {
                model with
                    Busy = RunningConfirm
                    Banner = None
            },
            confirmErasure preview.Request.Id

    | ConfirmResolved(Ok(Completed summary)) ->
        let banner =
            if summary.OverallSuccess then
                Some(Notice.Info(msgs.EraseConfirmedSuccess summary.PerHandler.Count))
            else
                Some(Notice.Problem(msgs.EraseConfirmedPartialFailure summary.PerHandler.Count))

        {
            model with
                Busy = Idle
                LastRunSummary = Some summary
                PendingPreview = None
                Banner = banner
        },
        []

    | ConfirmResolved(Ok(Refused reason)) ->
        {
            model with
                Busy = Idle
                Banner = Some(Notice.Problem(msgs.EraseRefused reason))
        },
        []

    | ConfirmResolved(Ok(NotImplemented detail)) ->
        {
            model with
                Busy = Idle
                Banner = Some(Notice.Problem(msgs.EraseNotImplemented detail))
        },
        []

    | ConfirmResolved(Result.Error err) ->
        {
            model with
                Busy = Idle
                Banner = Some(Notice.Problem err)
        },
        []

    | DismissBanner -> { model with Banner = None }, []

/// The transitions, on today's loop.
let update (msg: Msg) (model: Model) : Model * Cmd<Msg> = step msg model |> RemoteEffect.run

// ─── View ────────────────────────────────────────────────────────────

let private tabBar (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className [ "px-4"; Tokens.Bg.card ]
        prop.children [
            Tabs.bar [ ExportTab, msgs.TabExport; EraseTab, msgs.TabErase ] model.ActiveTab (SwitchTab >> dispatch)
        ]
    ]

let private subjectFormRows (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex flex-col gap-3"
        prop.children [
            Forms.Input.text model.SubjectInput (fun v -> dispatch (SetSubjectInput v)) msgs.SubjectPlaceholder

            Forms.Input.text model.TeamInput (fun v -> dispatch (SetTeamInput v)) msgs.TeamPlaceholder

            Forms.Input.text model.ReasonInput (fun v -> dispatch (SetReasonInput v)) msgs.ReasonPlaceholder
        ]
    ]

let private ticketStatusLabel (msgs: DataSubjectRequestAdminMessages) =
    function
    | ExportStatus.Preparing -> msgs.TicketPreparing
    | ExportStatus.Ready size -> msgs.TicketReady size
    | ExportStatus.Failed reason -> msgs.TicketFailed reason
    | ExportStatus.Cancelled -> msgs.TicketCancelled
    | ExportStatus.Expired -> msgs.TicketExpired
    | ExportStatus.Unknown -> msgs.TicketUnknown

/// The in-flight background-export panel. Shows the active
/// ticket's status (auto-polled) with a cancel control. Download happens
/// automatically when the ticket flips to `Ready`.
let private activeTicketPanel (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    match model.ActiveTicket, model.TicketStatus with
    | Some ticket, Some status ->
        Html.div [
            prop.className "mt-4 p-3 border border-border rounded bg-gray-50"
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between"
                    prop.children [
                        Html.div [
                            prop.children [
                                Html.div [
                                    prop.className "text-sm font-medium"
                                    prop.text msgs.BackgroundExportHeading
                                ]
                                Html.div [
                                    prop.className "text-xs text-muted"
                                    prop.text (msgs.TicketLine ticket (ticketStatusLabel msgs status))
                                ]
                            ]
                        ]
                        match status with
                        | ExportStatus.Preparing ->
                            Forms.Button.secondaryWith Forms.Button.Enabled msgs.Cancel (fun () ->
                                dispatch CancelAsyncExport)
                        | _ -> Html.none
                    ]
                ]
            ]
        ]
    | _ -> Html.none

let private exportTabView (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    let submitState =
        Forms.Button.stateOf (model.Busy = RunningExport) msgs.Exporting (model.Busy <> Idle)

    Layout.Panel.panel msgs.ExportPanelTitle [
        Html.p [
            prop.className "text-sm text-text-secondary mb-3"
            prop.text msgs.ExportPanelBody
        ]
        subjectFormRows msgs model dispatch
        Html.label [
            prop.className "mt-3 flex items-center gap-2 text-sm cursor-pointer"
            prop.children [
                Html.input [
                    prop.type' "checkbox"
                    prop.isChecked model.AsyncMode
                    prop.onChange (fun (v: bool) -> dispatch (SetAsyncMode v))
                ]
                Html.span [ prop.text msgs.AsyncModeLabel ]
            ]
        ]
        Html.div [
            prop.className "mt-4 flex items-center gap-3"
            prop.children [
                Forms.Button.primaryWith submitState msgs.RequestExport (fun () -> dispatch SubmitExport)
                if model.Busy = RunningExport then
                    Html.span [ prop.className "text-xs text-muted"; prop.text msgs.AggregatingSegments ]
            ]
        ]
        activeTicketPanel msgs model dispatch
    ]

let private policyRadio (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    let radio (selected: bool) (label: string) (description: string) (onSelect: unit -> unit) =
        Html.label [
            prop.className [
                "flex items-start gap-2 p-3 border rounded cursor-pointer transition-colors"
                if selected then
                    "border-brand bg-brand/5"
                else
                    "border-border hover:bg-gray-50"
            ]
            prop.onClick (fun _ -> onSelect ())
            prop.children [
                Html.input [
                    prop.type' "radio"
                    prop.isChecked selected
                    prop.readOnly true
                    prop.className "mt-1"
                ]
                Html.div [
                    prop.children [
                        Html.div [ prop.className "text-sm font-medium"; prop.text label ]
                        Html.div [ prop.className "text-xs text-muted"; prop.text description ]
                    ]
                ]
            ]
        ]

    Html.div [
        prop.className "flex flex-col gap-2"
        prop.children [
            Html.span [ prop.className "text-xs text-muted"; prop.text msgs.OverridePolicyPrompt ]
            radio
                (model.OverridePolicy = None)
                msgs.UseDeploymentDefault
                msgs.UseDeploymentDefaultDescription
                (fun () -> dispatch (SetOverridePolicy None))
            for policy in
                [
                    ErasurePolicy.HardDelete
                    ErasurePolicy.Tombstone
                    ErasurePolicy.RetainPerCompliance
                ] do
                radio
                    (model.OverridePolicy = Some policy)
                    (policyLabel msgs policy)
                    (policyDescription msgs policy)
                    (fun () -> dispatch (SetOverridePolicy(Some policy)))
        ]
    ]

let private previewPanel
    (msgs: DataSubjectRequestAdminMessages)
    (preview: ErasurePreview)
    (model: Model)
    (dispatch: Msg -> unit)
    =
    let total = preview.PerHandlerCounts.Values |> Seq.sumBy _.RecordsAffected

    let confirmState =
        Forms.Button.stateOf (model.Busy = RunningConfirm) msgs.Confirming (model.Busy <> Idle)

    let cancelState =
        if model.Busy = RunningConfirm then
            Forms.Button.Disabled
        else
            Forms.Button.Enabled

    Layout.Panel.panel msgs.PreviewPanelTitle [
        Html.div [
            prop.className "flex items-center justify-between mb-3"
            prop.children [
                Html.div [
                    prop.children [
                        Html.div [ prop.className "text-sm"; prop.text (msgs.RequestIdLine preview.Request.Id) ]
                        Html.div [
                            prop.className "text-xs text-muted"
                            prop.text (
                                msgs.PreviewSummaryLine
                                    (policyLabel msgs preview.Request.Policy)
                                    total
                                    preview.PerHandlerCounts.Count
                            )
                        ]
                    ]
                ]
                Forms.Button.secondaryWith cancelState msgs.Cancel (fun () -> dispatch CancelPreview)
            ]
        ]
        if preview.PerHandlerCounts.IsEmpty then
            Html.p [ prop.className "text-sm text-muted py-2"; prop.text msgs.PreviewEmpty ]
        else
            Html.div [
                prop.className "flex flex-col gap-1 mb-4"
                prop.children [
                    for KeyValue(name, summary) in preview.PerHandlerCounts do
                        Html.div [
                            prop.className "flex items-center justify-between p-2 border border-border rounded text-sm"
                            prop.children [
                                Html.span [ prop.className "font-medium"; prop.text name ]
                                Html.span [
                                    prop.className "text-xs text-muted"
                                    prop.text (msgs.HandlerRecordsAffected summary.RecordsAffected)
                                ]
                            ]
                        ]
                ]
            ]
        Html.div [
            prop.className "flex items-center gap-3"
            prop.children [
                Forms.Button.dangerWith confirmState msgs.ConfirmErase (fun () -> dispatch SubmitConfirm)
                Html.span [
                    prop.className "text-xs text-muted"
                    prop.text msgs.ConfirmIrreversibleFootnote
                ]
            ]
        ]
    ]

let private runSummaryPanel (msgs: DataSubjectRequestAdminMessages) (summary: ErasureRunSummary) =
    let perHandlerRow (name: string) (outcome: Result<ErasureSummary, ErasureError>) =
        let body, badgeClass =
            match outcome with
            | Result.Ok s -> msgs.HandlerRecordsAffected s.RecordsAffected, "bg-green-100 text-green-700"
            | Result.Error err -> ErasureError.toMessage err, "bg-red-100 text-red-700"

        Html.div [
            prop.className "flex items-center justify-between p-2 border border-border rounded text-sm"
            prop.children [
                Html.span [ prop.className "font-medium"; prop.text name ]
                Html.span [ prop.className $"text-xs px-2 py-0.5 rounded {badgeClass}"; prop.text body ]
            ]
        ]

    let started = summary.StartedAt.ToString "u"
    let completed = summary.CompletedAt.ToString "u"

    let overall =
        if summary.OverallSuccess then
            msgs.OverallSuccess
        else
            msgs.OverallPartialFailure

    Layout.Panel.panel msgs.RunSummaryPanelTitle [
        Html.div [
            prop.className "text-xs text-muted mb-2"
            prop.text (msgs.RunSummaryLine started completed overall)
        ]
        Html.div [
            prop.className "flex flex-col gap-1"
            prop.children [
                for KeyValue(name, outcome) in summary.PerHandler do
                    perHandlerRow name outcome
            ]
        ]
    ]

let private eraseTabView (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    let previewState =
        Forms.Button.stateOf (model.Busy = RunningPreview) msgs.Previewing (model.Busy <> Idle)

    Html.div [
        prop.className "flex flex-col gap-4"
        prop.children [
            Layout.Panel.panel msgs.ErasePanelTitle [
                Html.p [
                    prop.className "text-sm text-text-secondary mb-3"
                    prop.text msgs.ErasePanelBody
                ]
                subjectFormRows msgs model dispatch
                Html.div [ prop.className "mt-3"; prop.children [ policyRadio msgs model dispatch ] ]
                Html.div [
                    prop.className "mt-4 flex items-center gap-3"
                    prop.children [
                        Forms.Button.primaryWith previewState msgs.PreviewErase (fun () -> dispatch SubmitPreview)
                        if model.PendingPreview.IsSome then
                            Html.span [ prop.className "text-xs text-muted"; prop.text msgs.PendingPreviewHint ]
                    ]
                ]
            ]

            match model.PendingPreview with
            | Some preview -> previewPanel msgs preview model dispatch
            | None -> Html.none

            match model.LastRunSummary with
            | Some summary -> runSummaryPanel msgs summary
            | None -> Html.none
        ]
    ]

/// The module's page, over the catalog section the view hands it.
let private body (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let body =
        Html.div [
            prop.className "flex flex-col"
            prop.children [
                tabBar msgs model dispatch
                Html.div [
                    prop.className "p-4"
                    prop.children [
                        match model.ActiveTab with
                        | ExportTab -> exportTabView msgs model dispatch
                        | EraseTab -> eraseTabView msgs model dispatch
                    ]
                ]
            ]
        ]

    // 0.5.6 — FullWidth render. The notice stacks above the body.
    Html.div [
        prop.className "flex flex-col gap-3 h-full"
        prop.children [
            StateViews.notice { Dismiss = msgs.DismissBanner } model.Banner (fun () -> dispatch DismissBanner)
            body
        ]
    ]

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(body ctx.Messages.DataSubjectRequestAdmin model ctx.Dispatch)

// ─── Module creation ─────────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.DataSubjectRequests"

/// The built-in DSR admin module (`ShellSlots.builtIn` builds its module). The
/// shell's `prepareModules` in `SDK.Client.fs` injects this when
/// `ClientConfig.Surfaces` declares any authenticated surface
/// (`ClientConfig.requiresAnyAuth`) and the `DataSubjectRequestAdmin`
/// shell slot is filled by it. Owner / Admin gating is the
/// server-side handler's job; the sidebar entry shows for every
/// authenticated caller and the API itself rejects non-admin writes.
let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Data subject requests"
        Icon = ToolUp.Platform.Icons.users
    }
    Group = Some "Platform Management"
    NavRole = Some ToolUp.Platform.NavRole.PlatformAdminOnly
    Area = Product
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = init
    Update = update
    View = BuiltInView.Page view
    Extend = id
}
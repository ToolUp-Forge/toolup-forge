// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module DataSubjectRequestAdminUI

open System
open ToolUp.Elmish
open Feliz
open Browser
open Browser.Types
open Fable.Core.JsInterop
open Toolup.UIToolkit
open ToolUp.Platform
open ToolUp.Platform.DataSubjectRequestApi

// ─── Phase 9h — DSR admin module (client) ────────────────────────────
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

/// Inline status banner after a flow completes. Lives at the page
/// bottom; dismissed on next action or via the `×` button.
type Banner =
    | NoBanner
    | OkBanner of message: string
    | ErrorBanner of message: string

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
    /// Phase 9h.A — when true, the Export tab submits through the async
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
    Banner: Banner
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

let private policyLabel =
    function
    | ErasurePolicy.HardDelete -> "Hard delete"
    | ErasurePolicy.Tombstone -> "Tombstone"
    | ErasurePolicy.RetainPerCompliance -> "Retain per compliance"

let private policyDescription =
    function
    | ErasurePolicy.HardDelete ->
        "Remove records entirely. Breaks event-log integrity for the subject — only valid where no compliance-driven retention applies."
    | ErasurePolicy.Tombstone ->
        "Replace user-identifying fields with markers; preserve shape and version chain. Fits most GDPR / CCPA / DPDPA regimes."
    | ErasurePolicy.RetainPerCompliance ->
        "Redact only where possible; audit / event records survive. For jurisdictions where retention legally overrides erasure."

// ─── Init / update ───────────────────────────────────────────────────

let init () : Model * Cmd<Msg> =
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
        Banner = NoBanner
    }

    model, Cmd.none

/// Phase 9h.A — re-poll the active background-export ticket after a short
/// delay. The loop runs while a ticket stays `Preparing`.
let private pollDelay: Cmd<Msg> =
    Cmd.OfAsync.perform (fun () -> Async.Sleep 1500) () (fun _ -> PollTicket)

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
    if model.SubjectInput.Trim() = "" then
        Some "Subject user id is required."
    elif model.ReasonInput.Trim() = "" then
        Some "Reason is required (lands in audit)."
    else
        None

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | SwitchTab tab ->
        // Keep form contents; clear any pending preview to avoid
        // confusion when switching contexts.
        {
            model with
                ActiveTab = tab
                PendingPreview = None
                Banner = NoBanner
        },
        Cmd.none

    | SetSubjectInput value -> { model with SubjectInput = value }, Cmd.none

    | SetTeamInput value -> { model with TeamInput = value }, Cmd.none

    | SetReasonInput value -> { model with ReasonInput = value }, Cmd.none

    | SetOverridePolicy policy -> { model with OverridePolicy = policy }, Cmd.none

    | SubmitExport ->
        match validateForm model with
        | Some err -> { model with Banner = ErrorBanner err }, Cmd.none
        | None when model.AsyncMode ->
            let cmd =
                Cmd.OfRemoting.call dsrApi.RequestExportAsync (exportInput model) AsyncExportStarted (fun ex ->
                    AsyncExportStarted(Result.Error ex.Message))

            {
                model with
                    Busy = RunningExport
                    Banner = NoBanner
                    ActiveTicket = None
                    TicketStatus = None
            },
            cmd
        | None ->
            let cmd =
                Cmd.OfRemoting.call dsrApi.RequestExport (exportInput model) ExportResolved (fun ex ->
                    ExportResolved(Result.Error ex.Message))

            {
                model with
                    Busy = RunningExport
                    Banner = NoBanner
            },
            cmd

    | SetAsyncMode value -> { model with AsyncMode = value }, Cmd.none

    | AsyncExportStarted(Ok ticket) ->
        {
            model with
                Busy = RunningExport
                ActiveTicket = Some ticket
                TicketStatus = Some ExportStatus.Preparing
                Banner = OkBanner "Background export queued — assembling segments…"
        },
        pollDelay

    | AsyncExportStarted(Result.Error err) ->
        {
            model with
                Busy = Idle
                Banner = ErrorBanner err
        },
        Cmd.none

    | PollTicket ->
        match model.ActiveTicket with
        | None -> model, Cmd.none
        | Some ticket ->
            let cmd =
                Cmd.OfRemoting.call dsrApi.GetExportStatus ticket TicketStatusResolved (fun ex ->
                    TicketStatusResolved(Result.Error ex.Message))

            model, cmd

    | TicketStatusResolved(Ok status) ->
        match status with
        | ExportStatus.Ready _ ->
            match model.ActiveTicket with
            | Some ticket ->
                let cmd =
                    Cmd.OfRemoting.call dsrApi.DownloadExport ticket DownloadResolved (fun ex ->
                        DownloadResolved(Result.Error ex.Message))

                {
                    model with
                        TicketStatus = Some status
                },
                cmd
            | None ->
                {
                    model with
                        TicketStatus = Some status
                },
                Cmd.none
        | ExportStatus.Preparing ->
            {
                model with
                    TicketStatus = Some status
            },
            pollDelay
        | ExportStatus.Failed reason ->
            {
                model with
                    Busy = Idle
                    ActiveTicket = None
                    TicketStatus = Some status
                    Banner = ErrorBanner $"Export failed: {reason}"
            },
            Cmd.none
        | ExportStatus.Cancelled ->
            {
                model with
                    Busy = Idle
                    ActiveTicket = None
                    TicketStatus = Some status
                    Banner = OkBanner "Export cancelled."
            },
            Cmd.none
        | ExportStatus.Expired
        | ExportStatus.Unknown ->
            {
                model with
                    Busy = Idle
                    ActiveTicket = None
                    TicketStatus = Some status
                    Banner = ErrorBanner "Export ticket expired or unknown — re-submit."
            },
            Cmd.none

    | TicketStatusResolved(Result.Error err) ->
        {
            model with
                Busy = Idle
                ActiveTicket = None
                Banner = ErrorBanner err
        },
        Cmd.none

    | DownloadResolved(Ok bytes) ->
        let subject = model.SubjectInput.Trim()

        {
            model with
                Busy = Idle
                ActiveTicket = None
                TicketStatus = None
                Banner = OkBanner $"Background export ready — {bytes.Length} bytes downloaded."
        },
        Cmd.ofEffect (fun _ -> downloadExport subject bytes)

    | DownloadResolved(Result.Error err) ->
        {
            model with
                Busy = Idle
                ActiveTicket = None
                Banner = ErrorBanner err
        },
        Cmd.none

    | CancelAsyncExport ->
        match model.ActiveTicket with
        | None -> model, Cmd.none
        | Some ticket ->
            let cmd =
                Cmd.OfRemoting.call dsrApi.CancelExport ticket CancelResolved (fun ex ->
                    CancelResolved(Result.Error ex.Message))

            model, cmd

    | CancelResolved(Ok()) ->
        {
            model with
                Busy = Idle
                ActiveTicket = None
                TicketStatus = Some ExportStatus.Cancelled
                Banner = OkBanner "Export cancelled."
        },
        Cmd.none

    | CancelResolved(Result.Error err) -> { model with Banner = ErrorBanner err }, Cmd.none

    | ExportResolved(Ok bytes) ->
        let subject = model.SubjectInput.Trim()

        {
            model with
                Busy = Idle
                Banner = OkBanner $"Export ready — {bytes.Length} bytes downloaded."
        },
        Cmd.ofEffect (fun _ -> downloadExport subject bytes)

    | ExportResolved(Result.Error err) ->
        {
            model with
                Busy = Idle
                Banner = ErrorBanner err
        },
        Cmd.none

    | SubmitPreview ->
        match validateForm model with
        | Some err -> { model with Banner = ErrorBanner err }, Cmd.none
        | None ->
            let cmd =
                Cmd.OfRemoting.call dsrApi.PreviewErasure (erasureInput model) PreviewResolved (fun ex ->
                    PreviewResolved(Result.Error ex.Message))

            {
                model with
                    Busy = RunningPreview
                    Banner = NoBanner
                    PendingPreview = None
            },
            cmd

    | PreviewResolved(Ok preview) ->
        {
            model with
                Busy = Idle
                PendingPreview = Some preview
                Banner = NoBanner
        },
        Cmd.none

    | PreviewResolved(Result.Error err) ->
        {
            model with
                Busy = Idle
                Banner = ErrorBanner err
        },
        Cmd.none

    | CancelPreview ->
        {
            model with
                PendingPreview = None
                Banner = NoBanner
        },
        Cmd.none

    | SubmitConfirm ->
        match model.PendingPreview with
        | None ->
            {
                model with
                    Banner = ErrorBanner "Run Preview first."
            },
            Cmd.none
        | Some preview ->
            let cmd =
                Cmd.OfRemoting.call dsrApi.ConfirmErasure preview.Request.Id ConfirmResolved (fun ex ->
                    ConfirmResolved(Result.Error ex.Message))

            {
                model with
                    Busy = RunningConfirm
                    Banner = NoBanner
            },
            cmd

    | ConfirmResolved(Ok(Completed summary)) ->
        let banner =
            if summary.OverallSuccess then
                OkBanner $"Erase confirmed — {summary.PerHandler.Count} handler(s) ran successfully."
            else
                ErrorBanner
                    $"Erase ran with partial failures — {summary.PerHandler.Count} handler(s); inspect per-handler results."

        {
            model with
                Busy = Idle
                LastRunSummary = Some summary
                PendingPreview = None
                Banner = banner
        },
        Cmd.none

    | ConfirmResolved(Ok(Refused reason)) ->
        {
            model with
                Busy = Idle
                Banner = ErrorBanner $"Refused: {reason}"
        },
        Cmd.none

    | ConfirmResolved(Ok(NotImplemented detail)) ->
        {
            model with
                Busy = Idle
                Banner = ErrorBanner $"Not implemented: {detail}"
        },
        Cmd.none

    | ConfirmResolved(Result.Error err) ->
        {
            model with
                Busy = Idle
                Banner = ErrorBanner err
        },
        Cmd.none

    | DismissBanner -> { model with Banner = NoBanner }, Cmd.none

// ─── View ────────────────────────────────────────────────────────────

let private tabButton (label: string) (active: bool) (onClick: unit -> unit) =
    Html.button [
        prop.className [
            "px-4 py-2 text-sm font-medium border-b-2 transition-colors"
            if active then
                "border-brand text-brand"
            else
                "border-transparent text-gray-500 hover:text-gray-700"
        ]
        prop.text label
        prop.onClick (fun _ -> onClick ())
    ]

let private tabBar (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex gap-1 border-b border-border bg-white px-4"
        prop.children [
            tabButton "Export (Article 15)" (model.ActiveTab = ExportTab) (fun () -> dispatch (SwitchTab ExportTab))
            tabButton "Erase (Article 17)" (model.ActiveTab = EraseTab) (fun () -> dispatch (SwitchTab EraseTab))
        ]
    ]

let private subjectFormRows (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex flex-col gap-3"
        prop.children [
            Forms.Input.text
                model.SubjectInput
                (fun v -> dispatch (SetSubjectInput v))
                "Subject user id (e.g. identity-provider sub claim)"

            Forms.Input.text
                model.TeamInput
                (fun v -> dispatch (SetTeamInput v))
                "Team id (optional — leave blank for cross-team)"

            Forms.Input.text
                model.ReasonInput
                (fun v -> dispatch (SetReasonInput v))
                "Reason (ticket / case / regulator inquiry — lands in audit)"
        ]
    ]

let private ticketStatusLabel =
    function
    | ExportStatus.Preparing -> "Preparing — assembling segments…"
    | ExportStatus.Ready size -> $"Ready — {size} bytes; downloading…"
    | ExportStatus.Failed reason -> $"Failed — {reason}"
    | ExportStatus.Cancelled -> "Cancelled"
    | ExportStatus.Expired -> "Expired"
    | ExportStatus.Unknown -> "Unknown"

/// Phase 9h.A — in-flight background-export panel. Shows the active
/// ticket's status (auto-polled) with a cancel control. Download happens
/// automatically when the ticket flips to `Ready`.
let private activeTicketPanel (model: Model) (dispatch: Msg -> unit) =
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
                                Html.div [ prop.className "text-sm font-medium"; prop.text "Background export" ]
                                Html.div [
                                    prop.className "text-xs text-muted"
                                    prop.text $"Ticket {ticket} • {ticketStatusLabel status}"
                                ]
                            ]
                        ]
                        match status with
                        | ExportStatus.Preparing ->
                            Forms.Button.secondary "Cancel" (fun () -> dispatch CancelAsyncExport)
                        | _ -> Html.none
                    ]
                ]
            ]
        ]
    | _ -> Html.none

let private exportTabView (model: Model) (dispatch: Msg -> unit) =
    let submitLabel =
        match model.Busy with
        | RunningExport -> "Exporting…"
        | _ -> "Request export"

    Layout.Panel.panel "Article 15 — data export" [
        Html.p [
            prop.className "text-sm text-text-secondary mb-3"
            prop.text
                "Streams every record across every registered exporter that names the subject. Scope-isolated when a Team id is supplied."
        ]
        subjectFormRows model dispatch
        Html.label [
            prop.className "mt-3 flex items-center gap-2 text-sm cursor-pointer"
            prop.children [
                Html.input [
                    prop.type' "checkbox"
                    prop.isChecked model.AsyncMode
                    prop.onChange (fun (v: bool) -> dispatch (SetAsyncMode v))
                ]
                Html.span [
                    prop.text
                        "Run as a background job (large exports — returns a ticket, polls until ready, then downloads). Requires async DSR enabled server-side."
                ]
            ]
        ]
        Html.div [
            prop.className "mt-4 flex items-center gap-3"
            prop.children [
                Forms.Button.primary submitLabel (fun () -> dispatch SubmitExport)
                if model.Busy = RunningExport then
                    Html.span [
                        prop.className "text-xs text-muted"
                        prop.text "Aggregating segments from every store…"
                    ]
            ]
        ]
        activeTicketPanel model dispatch
    ]

let private policyRadio (model: Model) (dispatch: Msg -> unit) =
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
            Html.span [
                prop.className "text-xs text-muted"
                prop.text "Override deployment default for this request (optional):"
            ]
            radio
                (model.OverridePolicy = None)
                "Use deployment default"
                "Apply the policy set in ServerConfig."
                (fun () -> dispatch (SetOverridePolicy None))
            for policy in
                [
                    ErasurePolicy.HardDelete
                    ErasurePolicy.Tombstone
                    ErasurePolicy.RetainPerCompliance
                ] do
                radio (model.OverridePolicy = Some policy) (policyLabel policy) (policyDescription policy) (fun () ->
                    dispatch (SetOverridePolicy(Some policy)))
        ]
    ]

let private previewPanel (preview: ErasurePreview) (model: Model) (dispatch: Msg -> unit) =
    let total = preview.PerHandlerCounts.Values |> Seq.sumBy _.RecordsAffected

    let confirmLabel =
        match model.Busy with
        | RunningConfirm -> "Confirming…"
        | _ -> "Confirm erase"

    Layout.Panel.panel "Preview — review then confirm" [
        Html.div [
            prop.className "flex items-center justify-between mb-3"
            prop.children [
                Html.div [
                    prop.children [
                        Html.div [ prop.className "text-sm"; prop.text $"Request id: {preview.Request.Id}" ]
                        Html.div [
                            prop.className "text-xs text-muted"
                            prop.text
                                $"Policy: {policyLabel preview.Request.Policy} • Total affected: {total} across {preview.PerHandlerCounts.Count} handler(s)"
                        ]
                    ]
                ]
                Forms.Button.secondary "Cancel" (fun () -> dispatch CancelPreview)
            ]
        ]
        if preview.PerHandlerCounts.IsEmpty then
            Html.p [
                prop.className "text-sm text-muted py-2"
                prop.text "No handlers registered or no records matched — confirm is a no-op."
            ]
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
                                    prop.text $"{summary.RecordsAffected} record(s)"
                                ]
                            ]
                        ]
                ]
            ]
        Html.div [
            prop.className "flex items-center gap-3"
            prop.children [
                Forms.Button.primary confirmLabel (fun () -> dispatch SubmitConfirm)
                Html.span [
                    prop.className "text-xs text-muted"
                    prop.text "Confirmation is irreversible under HardDelete and event-store Tombstone."
                ]
            ]
        ]
    ]

let private runSummaryPanel (summary: ErasureRunSummary) =
    let perHandlerRow (name: string) (outcome: Result<ErasureSummary, ErasureError>) =
        let body, badgeClass =
            match outcome with
            | Result.Ok s -> $"{s.RecordsAffected} record(s)", "bg-green-100 text-green-700"
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
            "success"
        else
            "partial failure"

    Layout.Panel.panel "Last run summary" [
        Html.div [
            prop.className "text-xs text-muted mb-2"
            prop.text $"Started {started} • Completed {completed} • Overall: {overall}"
        ]
        Html.div [
            prop.className "flex flex-col gap-1"
            prop.children [
                for KeyValue(name, outcome) in summary.PerHandler do
                    perHandlerRow name outcome
            ]
        ]
    ]

let private eraseTabView (model: Model) (dispatch: Msg -> unit) =
    let previewLabel =
        match model.Busy with
        | RunningPreview -> "Previewing…"
        | _ -> "Preview erase"

    Html.div [
        prop.className "flex flex-col gap-4"
        prop.children [
            Layout.Panel.panel "Article 17 — data erasure" [
                Html.p [
                    prop.className "text-sm text-text-secondary mb-3"
                    prop.text
                        "Erase or redact every record naming the subject across every registered handler. Two-phase — Preview shows per-store affected counts; Confirm executes."
                ]
                subjectFormRows model dispatch
                Html.div [ prop.className "mt-3"; prop.children [ policyRadio model dispatch ] ]
                Html.div [
                    prop.className "mt-4 flex items-center gap-3"
                    prop.children [
                        Forms.Button.primary previewLabel (fun () -> dispatch SubmitPreview)
                        if model.PendingPreview.IsSome then
                            Html.span [
                                prop.className "text-xs text-muted"
                                prop.text "Pending preview ready below — confirm or cancel."
                            ]
                    ]
                ]
            ]

            match model.PendingPreview with
            | Some preview -> previewPanel preview model dispatch
            | None -> Html.none

            match model.LastRunSummary with
            | Some summary -> runSummaryPanel summary
            | None -> Html.none
        ]
    ]

let private bannerView (model: Model) (dispatch: Msg -> unit) =
    let render (cls: string) (msg: string) =
        Html.div [
            prop.className $"mt-3 p-3 border rounded text-sm flex items-center justify-between {cls}"
            prop.children [
                Html.span [ prop.text msg ]
                Html.button [
                    prop.className "text-xs hover:underline"
                    prop.text "dismiss"
                    prop.onClick (fun _ -> dispatch DismissBanner)
                ]
            ]
        ]

    match model.Banner with
    | NoBanner -> Html.none
    | OkBanner msg -> render "bg-green-50 border-green-200 text-green-700" msg
    | ErrorBanner msg -> render "bg-red-50 border-red-200 text-red-700" msg

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let body =
        Html.div [
            prop.className "flex flex-col"
            prop.children [
                tabBar model dispatch
                Html.div [
                    prop.className "p-4"
                    prop.children [
                        match model.ActiveTab with
                        | ExportTab -> exportTabView model dispatch
                        | EraseTab -> eraseTabView model dispatch
                    ]
                ]
            ]
        ]

    // 0.5.6 — FullWidth render. Banner stacks above the body.
    Html.div [
        prop.className "flex flex-col gap-3 h-full"
        prop.children [ bannerView model dispatch; body ]
    ]

// ─── Module creation ─────────────────────────────────────────────────

/// Create the built-in DSR admin module as an `ErasedModule`. The
/// shell's `prepareModules` in `SDK.Client.fs` injects this when
/// `ClientConfig.Surfaces` declares any authenticated surface
/// (`ClientConfig.requiresAnyAuth`) and `ClientConfig.DataSubjectRequestAdmin`
/// is not `NoDataSubjectRequestAdmin`. Owner / Admin gating is the
/// server-side handler's job; the sidebar entry shows for every
/// authenticated caller and the API itself rejects non-admin writes.
let create (config: DataSubjectRequestAdminConfig option) : ErasedModule =
    let name =
        config |> Option.map _.Name |> Option.defaultValue "Data subject requests"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.users

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.DataSubjectRequests"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Platform Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.PlatformAdminOnly
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
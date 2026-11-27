// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module DataSubjectRequestAdminUI

open Fuaran.MVU
open System
open Fable.Core
open Browser
open Browser.Types
open Fable.Core.JsInterop
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
let internal policyLabel (msgs: DataSubjectRequestAdminMessages) =
    function
    | ErasurePolicy.HardDelete -> msgs.PolicyHardDeleteLabel
    | ErasurePolicy.Tombstone -> msgs.PolicyTombstoneLabel
    | ErasurePolicy.RetainPerCompliance -> msgs.PolicyRetainPerComplianceLabel

let internal policyDescription (msgs: DataSubjectRequestAdminMessages) =
    function
    | ErasurePolicy.HardDelete -> msgs.PolicyHardDeleteDescription
    | ErasurePolicy.Tombstone -> msgs.PolicyTombstoneDescription
    | ErasurePolicy.RetainPerCompliance -> msgs.PolicyRetainPerComplianceDescription

// ─── Init / update ───────────────────────────────────────────────────

let init (ctx: ClientModuleContext) : Model * Step<Msg, RemoteEffect<Msg>> list =
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

    model, []

type internal Steps = Step<Msg, RemoteEffect<Msg>> list

/// Re-poll the active background-export ticket after a short
/// delay. The loop runs while a ticket stays `Preparing`.
let internal pollDelay: Steps = [ Do(RemoteEffect.delay "DataSubjectRequestAdminUI.pollDelay" 1500 PollTicket) ]

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
    Do(RemoteEffect.signal "DataSubjectRequestAdminUI.downloadExport" (fun () -> downloadExport subject bytes))
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
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
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

// ─── View ────────────────────────────────────────────────────────────

let internal ticketStatusLabel (msgs: DataSubjectRequestAdminMessages) =
    function
    | ExportStatus.Preparing -> msgs.TicketPreparing
    | ExportStatus.Ready size -> msgs.TicketReady size
    | ExportStatus.Failed reason -> msgs.TicketFailed reason
    | ExportStatus.Cancelled -> msgs.TicketCancelled
    | ExportStatus.Expired -> msgs.TicketExpired
    | ExportStatus.Unknown -> msgs.TicketUnknown

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: DataSubjectRequestAdminMessages
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./DataSubjectRequestAdminTree.fs")

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "DataSubjectRequestAdminTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.DataSubjectRequestAdmin
            Locale = ctx.Locale
        |}
    )

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
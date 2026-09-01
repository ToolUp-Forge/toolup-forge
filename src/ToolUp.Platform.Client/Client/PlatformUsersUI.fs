// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module PlatformUsersUI

open System
open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Phase 544 — platform user-management admin module ───────────────
//
// The operator surface `TenantLifecycleAdminUI` was missing: instead of
// paste-a-scope-id diagnostics, this panel enumerates every principal the
// substrate has evidence for (Phase 543 `IPlatformTenantApi.ListPrincipals`
// — a derived, read-only projection over the membership blobs, `user-*`
// scopes and `UserLoggedIn` audit trail), flags team-less ones, and drives
// the *existing* Phase 54-family offboard flow per row against the
// `user-<id>` scope. No new destructive machinery — UI composition over
// shipped substrate.
//
// **Offboard flow (per row).** Preview (`PreviewDeprovision`, read-only) →
// the destructive call (`DeprovisionTenant` plain, or `ExportThenDeprovision`
// export-first) → the returned `LifecycleSummary`. When the server's
// `ServerConfig.TenantOffboardConfirmation` is `TokenConfirmation` /
// `TwoPersonRule` the token-less call is refused with
// `Error "offboard confirmation required"`; the modal detects that and
// surfaces a confirmation step — the operator mints a token
// (`RequestDeprovisionToken`, TokenConfirmation) or pastes one a second
// admin minted (TwoPersonRule) and completes via
// `DeprovisionTenantConfirmed`.
//
// **Gating (Platform-Admin).** Sidebar group "Platform Management" — the
// shell's role gate hides the whole group from non-admin callers, exactly
// like `_sdk.TenantLifecycleAdmin` / `_sdk.HealthMonitor`. Every
// `IPlatformTenantApi` method is Platform-Admin gated server-side, so a
// non-admin who reaches the module by direct navigation gets a uniform
// error banner, never a silent read (GP 4).
//
// **Opt-in / zero-cost (GP 11/13).** Injected only when
// `ClientConfig.PlatformUsers = DefaultPlatformUsers`; the default
// `NoPlatformUsers` omits it entirely. On a `NoTenantLifecycle` deployment
// the offboard endpoints 404, so the per-row actions surface an error
// banner rather than a blank page — the list itself still renders.

// ─── Model ───────────────────────────────────────────────────────────

/// Which destructive path a row's offboard modal takes.
type OffboardKind =
    /// Plain offboard — `DeprovisionTenant` (or, under a confirmation
    /// policy, `DeprovisionTenantConfirmed`).
    | PlainOffboard
    /// Export-then-erase — `ExportThenDeprovision`: a durable data export
    /// is written before the erasure sweep runs. Not available under a
    /// confirmation policy (there is no confirmed export path — the modal
    /// falls back to the plain confirmed offboard and says so).
    | ExportOffboard

/// Where the per-row offboard modal is in its flow.
type OffboardStep =
    /// Reason entry + the initial Preview / offboard buttons.
    | Compose
    /// A read-only `PreviewDeprovision` result is shown; proceed or back.
    | Previewed of LifecyclePreview
    /// The server demanded a confirmation token (Phase 54i). The operator
    /// mints one or pastes one from a second admin, then confirms.
    | Confirming
    /// Terminal — the offboard's `LifecycleSummary` (+ export archive when
    /// the export path ran).
    | Completed of LifecycleSummary * LifecycleExportArchive option

/// Open-modal state for one principal's offboard. `None` = closed.
type OffboardModalState = {
    /// The principal's user id (the row this modal was opened from).
    UserId: string
    /// The lifecycle scope — always `"user-" + UserId`.
    ScopeId: string
    Kind: OffboardKind
    /// Operator-supplied offboard reason (audited). Required (non-blank).
    Reason: string
    /// Confirmation token — minted via `RequestDeprovisionToken` or pasted
    /// by a second admin. Only used in the `Confirming` step.
    Token: string
    Step: OffboardStep
    /// A destructive / preview / mint call is in flight — disables the
    /// action buttons so a double-click can't fire twice.
    Busy: bool
    /// Inline error inside the modal (transport failure, blank reason,
    /// bad token). Distinct from the page-level `Error`.
    Error: string option
}

type Model = {
    /// Every principal the substrate has evidence for. `None` before the
    /// first `ListPrincipals` completes (drives the loading state).
    Principals: PrincipalSummary list option
    /// True while a `ListPrincipals` call is in flight.
    Loading: bool
    /// Show only team-less principals — the cleanup workflow's entry point.
    TeamLessOnly: bool
    /// Resolved directory entries keyed by user id (id → display name +
    /// email), populated lazily via `IUserDirectoryApi.ResolveUsers` after
    /// the principal list loads. Ids absent from the map render as the raw
    /// id — the list stays functional, just less friendly. Same
    /// resolve-on-miss pattern as `TeamManagerUI`.
    Directory: Map<string, UserSummary>
    /// Page-level error banner (list load failure, non-admin caller,
    /// disabled substrate). Cleared by `DismissError` or the next load.
    Error: string option
    /// Per-row offboard modal. `None` = closed.
    Offboard: OffboardModalState option
}

type Msg =
    | LoadPrincipals
    | PrincipalsLoaded of Result<PrincipalSummary list, string>
    | ToggleTeamLessOnly
    /// Reverse directory resolution (id → name/email) completed.
    | DirectoryResolved of Result<UserSummary list, string>
    | DismissError
    // ─── Per-row offboard flow ────────────────────────────────────────
    | OpenOffboard of userId: string * OffboardKind
    | CloseOffboard
    | SetOffboardReason of string
    | SetOffboardToken of string
    | RequestPreview
    | PreviewLoaded of Result<LifecyclePreview, string>
    | RequestToken
    | TokenMinted of Result<OffboardConfirmation, string>
    | SubmitOffboard
    | OffboardDone of Result<LifecycleSummary, string>
    | ExportOffboardDone of Result<ExportThenDeprovisionResult, string>

// ─── API proxies ─────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see
// TenantLifecycleAdminUI.fs. `IPlatformTenantApi` resolves under the
// reserved `/api/_platform/tenants/` prefix, so the proxy carries its own
// route builder rather than the default `/api/{type}/{method}`.
let private tenantApi: IPlatformTenantApi =
    Api.makeProxy<IPlatformTenantApi> (
        routeBuilder = PlatformTenantApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

// Reverse directory lookup (id → email/name). No-op for an empty list or
// when no directory companion is wired (degrades to raw ids). Mirrors
// `TeamManagerUI` / `PlatformAdminUI`.
let private directoryApi: IUserDirectoryApi =
    Api.makeProxy<IUserDirectoryApi> (customOptions = UserSession.withRequestHeaders)

/// Errors fold to a silent `DirectoryResolved(Error _)` — a directory
/// hiccup just leaves the raw ids on screen, never blocks the page.
let private resolveDirectoryCmd (ids: string list) =
    let cleaned = ids |> List.filter (fun id -> not (String.IsNullOrWhiteSpace id))

    if List.isEmpty cleaned then
        Cmd.none
    else
        Cmd.OfRemoting.call directoryApi.ResolveUsers cleaned DirectoryResolved (fun ex ->
            DirectoryResolved(Error ex.Message))

/// The signed-in operator's user id — the `actorUserId` attributed to
/// every offboard the panel fires.
let private actorUserId () = UserSession.getUserId ()

/// The server refuses a token-less destructive call with
/// `Error "offboard confirmation required"` when a confirmation policy is
/// active (Phase 54i). Detect that specific refusal so the modal can
/// switch to its confirmation step rather than surfacing it as a plain
/// transport error. Matched case-insensitively on the stable phrase.
let private isConfirmationRequired (err: string) =
    err.ToLower().Contains "confirmation required"

// ─── Init ────────────────────────────────────────────────────────────

let init () : Model * Cmd<Msg> =
    {
        Principals = None
        Loading = true
        TeamLessOnly = false
        Directory = Map.empty
        Error = None
        Offboard = None
    },
    Cmd.OfRemoting.call tenantApi.ListPrincipals () PrincipalsLoaded (fun e -> PrincipalsLoaded(Error e.Message))

// ─── Update helpers ──────────────────────────────────────────────────

/// Fire the destructive call appropriate to the modal's current step +
/// kind. In `Confirming` we always use the confirmed plain path (the
/// export path has no confirmed variant); otherwise the plain or export
/// path per `Kind`.
let private submitCmd (m: OffboardModalState) : Cmd<Msg> =
    let reason = m.Reason.Trim()
    let actor = actorUserId ()

    match m.Step with
    | Confirming ->
        Cmd.OfRemoting.call
            tenantApi.DeprovisionTenantConfirmed
            (m.ScopeId, actor, reason, m.Token.Trim())
            OffboardDone
            (fun e -> OffboardDone(Error e.Message))
    | _ ->
        match m.Kind with
        | PlainOffboard ->
            Cmd.OfRemoting.call tenantApi.DeprovisionTenant (m.ScopeId, actor, reason) OffboardDone (fun e ->
                OffboardDone(Error e.Message))
        | ExportOffboard ->
            Cmd.OfRemoting.call tenantApi.ExportThenDeprovision (m.ScopeId, actor, reason) ExportOffboardDone (fun e ->
                ExportOffboardDone(Error e.Message))

/// Apply a destructive-call result (shared by the plain + export folds):
/// success → `Completed`; a confirmation-required refusal (only when not
/// already confirming) → switch to the `Confirming` step; anything else →
/// inline error.
let private applyOffboardResult
    (summary: LifecycleSummary option)
    (archive: LifecycleExportArchive option)
    (err: string option)
    (model: Model)
    : Model * Cmd<Msg> =
    match model.Offboard with
    | None -> model, Cmd.none
    | Some m ->
        match summary, err with
        | Some s, _ ->
            {
                model with
                    Offboard =
                        Some {
                            m with
                                Step = Completed(s, archive)
                                Busy = false
                                Error = None
                        }
            },
            Cmd.none
        | None, Some e when m.Step <> Confirming && isConfirmationRequired e ->
            {
                model with
                    Offboard =
                        Some {
                            m with
                                Step = Confirming
                                Busy = false
                                Error = None
                        }
            },
            Cmd.none
        | None, Some e ->
            {
                model with
                    Offboard = Some { m with Busy = false; Error = Some e }
            },
            Cmd.none
        | None, None ->
            {
                model with
                    Offboard = Some { m with Busy = false }
            },
            Cmd.none

// ─── Update ──────────────────────────────────────────────────────────

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | LoadPrincipals ->
        {
            model with
                Loading = true
                Error = None
        },
        Cmd.OfRemoting.call tenantApi.ListPrincipals () PrincipalsLoaded (fun e -> PrincipalsLoaded(Error e.Message))

    | PrincipalsLoaded(Ok principals) ->
        // Resolve the ids we don't already have a directory entry for.
        // Errors are silent — the list still renders with raw ids.
        let unresolved =
            principals
            |> List.map _.UserId
            |> List.filter (fun id -> not (Map.containsKey id model.Directory))
            |> List.distinct

        {
            model with
                Principals = Some principals
                Loading = false
                Error = None
        },
        resolveDirectoryCmd unresolved

    | PrincipalsLoaded(Error e) ->
        {
            model with
                Principals = Some []
                Loading = false
                Error = Some e
        },
        Cmd.none

    | ToggleTeamLessOnly ->
        {
            model with
                TeamLessOnly = not model.TeamLessOnly
        },
        Cmd.none

    | DirectoryResolved(Ok summaries) ->
        let directory =
            summaries
            |> List.fold (fun acc (s: UserSummary) -> Map.add s.UserId s acc) model.Directory

        { model with Directory = directory }, Cmd.none

    // Silent — a directory failure just leaves raw ids on screen.
    | DirectoryResolved(Error _) -> model, Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

    | OpenOffboard(userId, kind) ->
        {
            model with
                Offboard =
                    Some {
                        UserId = userId
                        ScopeId = "user-" + userId
                        Kind = kind
                        Reason = ""
                        Token = ""
                        Step = Compose
                        Busy = false
                        Error = None
                    }
        },
        Cmd.none

    | CloseOffboard -> { model with Offboard = None }, Cmd.none

    | SetOffboardReason reason ->
        {
            model with
                Offboard = model.Offboard |> Option.map (fun m -> { m with Reason = reason })
        },
        Cmd.none

    | SetOffboardToken token ->
        {
            model with
                Offboard = model.Offboard |> Option.map (fun m -> { m with Token = token })
        },
        Cmd.none

    | RequestPreview ->
        match model.Offboard with
        | Some m when not m.Busy ->
            {
                model with
                    Offboard = Some { m with Busy = true; Error = None }
            },
            Cmd.OfRemoting.call tenantApi.PreviewDeprovision m.ScopeId PreviewLoaded (fun e ->
                PreviewLoaded(Error e.Message))
        | _ -> model, Cmd.none

    | PreviewLoaded(Ok preview) ->
        {
            model with
                Offboard =
                    model.Offboard
                    |> Option.map (fun m -> {
                        m with
                            Step = Previewed preview
                            Busy = false
                            Error = None
                    })
        },
        Cmd.none

    | PreviewLoaded(Error e) ->
        {
            model with
                Offboard = model.Offboard |> Option.map (fun m -> { m with Busy = false; Error = Some e })
        },
        Cmd.none

    | RequestToken ->
        match model.Offboard with
        | Some m when not m.Busy ->
            {
                model with
                    Offboard = Some { m with Busy = true; Error = None }
            },
            Cmd.OfRemoting.call tenantApi.RequestDeprovisionToken (m.ScopeId, m.Reason.Trim()) TokenMinted (fun e ->
                TokenMinted(Error e.Message))
        | _ -> model, Cmd.none

    | TokenMinted(Ok confirmation) ->
        {
            model with
                Offboard =
                    model.Offboard
                    |> Option.map (fun m -> {
                        m with
                            Token = confirmation.Token
                            Busy = false
                            Error = None
                    })
        },
        Cmd.none

    | TokenMinted(Error e) ->
        {
            model with
                Offboard = model.Offboard |> Option.map (fun m -> { m with Busy = false; Error = Some e })
        },
        Cmd.none

    | SubmitOffboard ->
        match model.Offboard with
        | Some m when m.Busy -> model, Cmd.none
        | Some m when m.Reason.Trim() = "" ->
            {
                model with
                    Offboard =
                        Some {
                            m with
                                Error = Some(MessageCatalog.english.PlatformUsers.ReasonRequired)
                        }
            },
            Cmd.none
        | Some m when m.Step = Confirming && m.Token.Trim() = "" ->
            {
                model with
                    Offboard =
                        Some {
                            m with
                                Error = Some(MessageCatalog.english.PlatformUsers.TokenRequired)
                        }
            },
            Cmd.none
        | Some m ->
            {
                model with
                    Offboard = Some { m with Busy = true; Error = None }
            },
            submitCmd m
        | None -> model, Cmd.none

    | OffboardDone(Ok summary) -> applyOffboardResult (Some summary) None None model

    | OffboardDone(Error e) -> applyOffboardResult None None (Some e) model

    | ExportOffboardDone(Ok result) -> applyOffboardResult (Some result.Summary) (Some result.Archive) None model

    | ExportOffboardDone(Error e) -> applyOffboardResult None None (Some e) model

// ─── View helpers ────────────────────────────────────────────────────

/// Best-effort human label for a principal, mirroring `TeamManagerUI`:
/// directory-resolved name / email, else the raw id. `PlatformUsersUI`
/// never renders "self" specially — every row is another principal.
let private displayLabels (directory: Map<string, UserSummary>) (userId: string) : string * string option =
    match Map.tryFind userId directory with
    | Some s ->
        let name = s.DisplayName |> Option.filter (String.IsNullOrWhiteSpace >> not)
        let email = s.Email |> Option.filter (String.IsNullOrWhiteSpace >> not)

        match name, email with
        | Some n, _ -> n, email
        | None, Some e -> e, None
        | None, None -> userId, None
    | None -> userId, None

/// Membership summary — "3 teams · Admin, Member" — or a team-less badge.
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

let private lastSeenLabel (p: PrincipalSummary) : string =
    match p.LastSeenAt with
    | Some at -> at.ToString "yyyy-MM-dd"
    | None -> "—"

let private badge (cls: string) (label: string) =
    Html.span [
        prop.className $"inline-block text-xs px-2 py-0.5 rounded {cls}"
        prop.text label
    ]

let private previewResultBadge (msgs: PlatformUsersMessages) (item: LifecyclePreviewItem) =
    if not item.HasPreview then
        badge "bg-gray-100 text-gray-500" msgs.NoPreviewBadge
    elif item.WouldAffect = 0 then
        badge "bg-green-50 text-green-700" "0"
    else
        badge "bg-amber-50 text-amber-800" (string item.WouldAffect)

let private summaryResultBadge (msgs: PlatformUsersMessages) (result: LifecycleHookResult) =
    match result with
    | LifecycleHookResult.Completed -> badge "bg-green-100 text-green-700" msgs.OutcomeCompleted
    | LifecycleHookResult.Skipped _ -> badge "bg-yellow-100 text-yellow-800" msgs.OutcomeSkipped
    | LifecycleHookResult.Failed _ -> badge "bg-red-100 text-red-700" msgs.OutcomeFailed

let private summaryResultDetail (result: LifecycleHookResult) =
    match result with
    | LifecycleHookResult.Completed -> ""
    | LifecycleHookResult.Skipped reason -> reason
    | LifecycleHookResult.Failed error -> error

// ─── Modal sub-views ─────────────────────────────────────────────────

let private modalShell (children: ReactElement list) =
    Html.div [
        prop.className "fixed inset-0 bg-black/40 flex items-center justify-center z-50"
        prop.children [
            Html.div [
                prop.className
                    "bg-white rounded-lg shadow-lg p-6 w-full max-w-lg space-y-4 max-h-[85vh] overflow-y-auto"
                prop.children children
            ]
        ]
    ]

let private modalError (state: OffboardModalState) =
    match state.Error with
    | Some msg -> Html.p [ prop.className "text-sm text-red-600"; prop.text msg ]
    | None -> Html.none

let private kindTitle (msgs: PlatformUsersMessages) =
    function
    | PlainOffboard -> msgs.OffboardTitle
    | ExportOffboard -> msgs.ExportOffboardTitle

let private primaryActionLabel (msgs: PlatformUsersMessages) (state: OffboardModalState) =
    if state.Busy then
        msgs.Working
    else
        match state.Step, state.Kind with
        | Confirming, _ -> msgs.ConfirmOffboard
        | _, PlainOffboard -> msgs.OffboardAction
        | _, ExportOffboard -> msgs.ExportOffboardAction

let private primaryButton (msgs: PlatformUsersMessages) (state: OffboardModalState) (dispatch: Msg -> unit) =
    Html.button [
        prop.disabled state.Busy
        prop.className [
            "px-4 py-2 rounded-lg text-sm font-medium transition-colors"
            if state.Busy then
                "bg-gray-300 text-gray-500 cursor-not-allowed"
            else
                "bg-red-600 text-white hover:bg-red-700"
        ]
        prop.text (primaryActionLabel msgs state)
        prop.onClick (fun _ -> dispatch SubmitOffboard)
    ]

let private reasonField (msgs: PlatformUsersMessages) (state: OffboardModalState) (dispatch: Msg -> unit) =
    Html.div [
        prop.children [
            Html.label [
                prop.className "block text-xs font-medium text-gray-700 mb-1"
                prop.text msgs.ReasonLabel
            ]
            // Per-keystroke binding into modal state — same idiom as
            // TeamManagerUI's single-input modals; the toolkit input
            // commits on blur and would drop the typed value here.
            Html.textarea [
                prop.value state.Reason
                prop.placeholder msgs.ReasonPlaceholder
                prop.rows 2
                prop.onChange (fun (v: string) -> dispatch (SetOffboardReason v))
                prop.className
                    "border border-border rounded-lg px-3 py-2 w-full text-sm focus:outline-none focus:border-brand"
            ]
        ]
    ]

let private previewTable (msgs: PlatformUsersMessages) (preview: LifecyclePreview) =
    Html.div [
        prop.className "border border-border rounded-lg overflow-hidden"
        prop.children [
            Html.table [
                prop.className "w-full text-xs"
                prop.children [
                    Html.thead [
                        prop.className "bg-gray-50"
                        prop.children [
                            Html.tr [
                                prop.children [
                                    Html.th [
                                        prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                        prop.text msgs.ColumnHook
                                    ]
                                    Html.th [
                                        prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                        prop.text msgs.ColumnWouldAffect
                                    ]
                                    Html.th [
                                        prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                        prop.text msgs.ColumnDetail
                                    ]
                                ]
                            ]
                        ]
                    ]
                    Html.tbody [
                        prop.children [
                            for item in preview.Items ->
                                Html.tr [
                                    prop.className "border-t border-border"
                                    prop.children [
                                        Html.td [ prop.className "px-3 py-2 font-mono"; prop.text item.HookName ]
                                        Html.td [
                                            prop.className "px-3 py-2"
                                            prop.children [ previewResultBadge msgs item ]
                                        ]
                                        Html.td [
                                            prop.className "px-3 py-2 text-gray-600 break-all"
                                            prop.text item.Detail
                                        ]
                                    ]
                                ]
                        ]
                    ]
                ]
            ]
        ]
    ]

let private summaryTable
    (msgs: PlatformUsersMessages)
    (summary: LifecycleSummary)
    (archive: LifecycleExportArchive option)
    =
    Html.div [
        prop.className "space-y-3"
        prop.children [
            Html.div [
                prop.className "flex gap-2 flex-wrap"
                prop.children [
                    badge "bg-green-50 text-green-700" (msgs.CompletedCount(LifecycleSummary.completedCount summary))
                    badge "bg-yellow-50 text-yellow-800" (msgs.SkippedCount(LifecycleSummary.skippedCount summary))
                    badge "bg-red-50 text-red-700" (msgs.FailedCount(LifecycleSummary.failedCount summary))
                    badge "bg-gray-100 text-gray-600" (sprintf "%d ms" (int summary.TotalElapsedMs))
                ]
            ]

            match archive with
            | Some a ->
                Html.div [
                    prop.className "text-xs bg-blue-50 border border-blue-200 rounded-lg p-3 text-blue-800 break-all"
                    prop.children [
                        Html.p [
                            prop.className "font-medium mb-1"
                            prop.text (msgs.ExportArchiveWritten a.SegmentCount)
                        ]
                        Html.p [ prop.text (sprintf "%s/%s" a.Container a.BlobPath) ]
                        Html.p [
                            prop.className "font-mono text-blue-600 mt-1"
                            prop.text (sprintf "sha256: %s" a.ContentHash)
                        ]
                    ]
                ]
            | None -> Html.none

            if List.isEmpty summary.Outcomes then
                Html.p [ prop.className "text-xs text-gray-500"; prop.text msgs.NoHooksRan ]
            else
                Html.div [
                    prop.className "border border-border rounded-lg overflow-hidden"
                    prop.children [
                        Html.table [
                            prop.className "w-full text-xs"
                            prop.children [
                                Html.thead [
                                    prop.className "bg-gray-50"
                                    prop.children [
                                        Html.tr [
                                            prop.children [
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                    prop.text msgs.ColumnHook
                                                ]
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                    prop.text msgs.ColumnResult
                                                ]
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                    prop.text msgs.ColumnDetail
                                                ]
                                            ]
                                        ]
                                    ]
                                ]
                                Html.tbody [
                                    prop.children [
                                        for outcome in summary.Outcomes ->
                                            Html.tr [
                                                prop.className "border-t border-border"
                                                prop.children [
                                                    Html.td [
                                                        prop.className "px-3 py-2 font-mono"
                                                        prop.text outcome.HookName
                                                    ]
                                                    Html.td [
                                                        prop.className "px-3 py-2"
                                                        prop.children [ summaryResultBadge msgs outcome.Result ]
                                                    ]
                                                    Html.td [
                                                        prop.className "px-3 py-2 text-gray-600 break-all"
                                                        prop.text (summaryResultDetail outcome.Result)
                                                    ]
                                                ]
                                            ]
                                    ]
                                ]
                            ]
                        ]
                    ]
                ]
        ]
    ]

let private offboardModalView
    (msgs: PlatformUsersMessages)
    (state: OffboardModalState)
    (label: string)
    (dispatch: Msg -> unit)
    =
    let heading =
        match state.Step with
        | Completed _ -> msgs.OffboardCompleteTitle
        | _ -> kindTitle msgs state.Kind

    let subject =
        Html.p [
            prop.className "text-sm text-muted"
            prop.children [
                Html.span [ prop.text (msgs.SubjectLabel label) ]
                Html.span [ prop.className "font-mono text-xs break-all"; prop.text state.ScopeId ]
            ]
        ]

    let footer (children: ReactElement list) =
        Html.div [ prop.className "flex justify-end gap-3 pt-2"; prop.children children ]

    modalShell [
        Html.h3 [ prop.className "text-lg font-semibold"; prop.text heading ]
        subject

        match state.Step with
        | Compose ->
            reasonField msgs state dispatch
            modalError state

            footer [
                Forms.Button.secondary msgs.Cancel (fun () -> dispatch CloseOffboard)
                Html.button [
                    prop.disabled state.Busy
                    prop.className [
                        "px-4 py-2 rounded-lg text-sm font-medium border transition-colors"
                        if state.Busy then
                            "border-border text-gray-400 cursor-not-allowed"
                        else
                            "border-border text-text hover:bg-gray-50"
                    ]
                    prop.text msgs.PreviewImpact
                    prop.onClick (fun _ -> dispatch RequestPreview)
                ]
                primaryButton msgs state dispatch
            ]

        | Previewed preview ->
            Html.p [
                prop.className "text-sm text-muted"
                prop.text (msgs.PreviewSummary preview.TotalWouldAffect)
            ]

            previewTable msgs preview
            reasonField msgs state dispatch
            modalError state

            footer [
                Forms.Button.secondary msgs.Cancel (fun () -> dispatch CloseOffboard)
                primaryButton msgs state dispatch
            ]

        | Confirming ->
            Html.div [
                prop.className "text-sm bg-amber-50 border border-amber-200 rounded-lg p-3 text-amber-800"
                prop.children [
                    Html.p [
                        prop.className "font-medium mb-1"
                        prop.text msgs.ConfirmationRequiredHeading
                    ]
                    Html.p [ prop.text msgs.ConfirmationRequiredBody ]
                    match state.Kind with
                    | ExportOffboard -> Html.p [ prop.className "mt-1"; prop.text msgs.ExportConfirmationNote ]
                    | PlainOffboard -> Html.none
                ]
            ]

            Html.div [
                prop.className "flex items-end gap-2"
                prop.children [
                    Html.div [
                        prop.className "flex-1"
                        prop.children [
                            Html.label [
                                prop.className "block text-xs font-medium text-gray-700 mb-1"
                                prop.text msgs.ConfirmationTokenLabel
                            ]
                            Html.input [
                                prop.type' "text"
                                prop.value state.Token
                                prop.placeholder msgs.ConfirmationTokenPlaceholder
                                prop.onChange (fun (v: string) -> dispatch (SetOffboardToken v))
                                prop.className
                                    "border border-border rounded-lg px-3 py-2 w-full text-xs font-mono focus:outline-none focus:border-brand"
                            ]
                        ]
                    ]
                    Html.button [
                        prop.disabled state.Busy
                        prop.className [
                            "px-3 py-2 rounded-lg text-xs font-medium border transition-colors whitespace-nowrap"
                            if state.Busy then
                                "border-border text-gray-400 cursor-not-allowed"
                            else
                                "border-border text-text hover:bg-gray-50"
                        ]
                        prop.text msgs.RequestToken
                        prop.onClick (fun _ -> dispatch RequestToken)
                    ]
                ]
            ]

            modalError state

            footer [
                Forms.Button.secondary msgs.Cancel (fun () -> dispatch CloseOffboard)
                primaryButton msgs state dispatch
            ]

        | Completed(summary, archive) ->
            summaryTable msgs summary archive
            footer [ Forms.Button.primary msgs.Close (fun () -> dispatch CloseOffboard) ]
    ]

// ─── List row ────────────────────────────────────────────────────────

let private principalRow
    (msgs: PlatformUsersMessages)
    (directory: Map<string, UserSummary>)
    (p: PrincipalSummary)
    (dispatch: Msg -> unit)
    =
    let primaryLabel, secondaryLabel = displayLabels directory p.UserId

    Html.div [
        prop.className "flex items-center justify-between p-3 border border-border rounded-lg mb-2 gap-3 flex-wrap"
        prop.children [
            Html.div [
                prop.className "flex flex-col min-w-0"
                prop.children [
                    Html.div [
                        prop.className "flex items-center gap-2 flex-wrap"
                        prop.children [
                            Html.span [ prop.className "font-medium break-all"; prop.text primaryLabel ]
                            if p.TeamLess then
                                badge "bg-orange-100 text-orange-700" msgs.TeamLessBadge
                            if p.HasUserScopeData then
                                badge "bg-gray-100 text-gray-600" msgs.HasDataBadge
                        ]
                    ]
                    match secondaryLabel with
                    | Some email -> Html.span [ prop.className "text-xs text-muted break-all"; prop.text email ]
                    | None -> Html.none
                    Html.span [
                        prop.className "text-xs text-muted"
                        prop.text (msgs.RowSubtitle (membershipLabel msgs p) (lastSeenLabel p))
                    ]
                    Html.span [
                        prop.className "text-xs text-gray-400 font-mono break-all"
                        prop.text p.UserId
                    ]
                ]
            ]
            Html.div [
                prop.className "flex gap-2 flex-wrap"
                prop.children [
                    Forms.Button.secondary msgs.PreviewAction (fun () ->
                        dispatch (OpenOffboard(p.UserId, PlainOffboard)))
                    Forms.Button.secondary msgs.ExportOffboardAction (fun () ->
                        dispatch (OpenOffboard(p.UserId, ExportOffboard)))
                    Html.button [
                        prop.className
                            "px-3 py-1.5 text-sm rounded-lg border border-red-200 text-red-700 hover:bg-red-50 transition-colors"
                        prop.text msgs.OffboardAction
                        prop.onClick (fun _ -> dispatch (OpenOffboard(p.UserId, PlainOffboard)))
                    ]
                ]
            ]
        ]
    ]

// ─── View ────────────────────────────────────────────────────────────

/// Phase 751 — the module body as a React COMPONENT, for the same reason
/// `HealthMonitorUI.HealthMonitorBody` is one: a module's `view` is invoked
/// inline by the shell's own render, so a hook called there joins the
/// shell's hook order and breaks the moment the active module changes. A
/// component of its own has a stable identity and its own hook site.
[<ReactComponent>]
let private PlatformUsersBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).PlatformUsers

    let errorBanner =
        match model.Error with
        | Some msg ->
            Html.div [
                prop.className
                    "mb-4 p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm flex items-center justify-between"
                prop.children [
                    Html.span [ prop.text msg ]
                    Html.button [
                        prop.className "text-xs text-red-600 hover:underline"
                        prop.text msgs.Dismiss
                        prop.onClick (fun _ -> dispatch DismissError)
                    ]
                ]
            ]
        | None -> Html.none

    let filterBar =
        Html.div [
            prop.className "flex items-center justify-between mb-4 flex-wrap gap-2"
            prop.children [
                Html.label [
                    prop.className "flex items-center gap-2 text-sm text-gray-700 cursor-pointer"
                    prop.children [
                        Html.input [
                            prop.type' "checkbox"
                            prop.isChecked model.TeamLessOnly
                            prop.onChange (fun (_: bool) -> dispatch ToggleTeamLessOnly)
                        ]
                        Html.span [ prop.text msgs.TeamLessOnly ]
                    ]
                ]
                Html.button [
                    prop.className "text-xs text-brand hover:underline"
                    prop.text msgs.Refresh
                    prop.onClick (fun _ -> dispatch LoadPrincipals)
                ]
            ]
        ]

    let listPane =
        match model.Principals with
        | None -> Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.LoadingPrincipals ]
        | Some principals ->
            let visible =
                if model.TeamLessOnly then
                    principals |> List.filter _.TeamLess
                else
                    principals

            if List.isEmpty principals then
                // Empty / degraded — no principals, or the offboard
                // substrate is disabled (the error banner carries the why).
                Html.div [
                    prop.className "bg-white rounded-lg border border-border p-6 text-center"
                    prop.children [
                        Html.p [ prop.className "text-sm text-gray-600"; prop.text msgs.NoPrincipalsHeading ]
                        Html.p [ prop.className "text-xs text-gray-400 mt-1"; prop.text msgs.NoPrincipalsBody ]
                    ]
                ]
            elif List.isEmpty visible then
                Html.p [
                    prop.className "text-sm text-gray-500 py-4"
                    prop.text msgs.NoTeamLessPrincipals
                ]
            else
                Html.div [
                    prop.children [
                        for p in visible do
                            principalRow msgs model.Directory p dispatch
                    ]
                ]

    let selfLabelForModal (state: OffboardModalState) =
        let primary, _ = displayLabels model.Directory state.UserId
        primary

    Html.div [
        prop.className "p-6 max-w-3xl"
        prop.children [
            Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text msgs.Heading ]
            Html.p [ prop.className "text-sm text-gray-600 mb-4"; prop.text msgs.Subheading ]
            errorBanner
            filterBar
            listPane

            match model.Offboard with
            | Some state -> offboardModalView msgs state (selfLabelForModal state) dispatch
            | None -> Html.none
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement = PlatformUsersBody model dispatch

// ─── Module creation ─────────────────────────────────────────────────

/// Create the built-in platform-users admin as an `ErasedModule`.
/// Registered under the `_sdk.` namespace in the "Platform Management"
/// sidebar group; the shell's role filter hides the group from non-admin
/// callers, and every `IPlatformTenantApi` method is Platform-Admin gated
/// server-side regardless (GP 4). Injected only when
/// `ClientConfig.PlatformUsers = DefaultPlatformUsers` (GP 11/13).
let create (config: PlatformUsersConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Users"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.users

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.PlatformUsers"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Platform Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.PlatformAdminOnly
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
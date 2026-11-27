// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module PlatformUsersUI

open Fuaran.MVU
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── Platform user-management admin module ──────────────────────────
//
// The operator surface `TenantLifecycleAdminUI` was missing: instead of
// paste-a-scope-id diagnostics, this panel enumerates every principal the
// substrate has evidence for (`IPlatformTenantApi.ListPrincipals`
// — a derived, read-only projection over the membership blobs, `user-*`
// scopes and `UserLoggedIn` audit trail), flags team-less ones, and drives
// the *existing* tenant offboard flow per row against the
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
// `ClientConfig.Chrome.PlatformUsers = DefaultPlatformUsers`; the default
// `NoPlatformUsers` omits it entirely. On a `FeatureMode.Disabled` deployment
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
    /// The server demanded a confirmation token. The operator
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
    /// bad token), as a `Notice.Problem`. Distinct from the page-level `Error`.
    Error: Notice option
}

type Model = {
    /// Every principal the substrate has evidence for. `None` before the
    /// first `ListPrincipals` completes (drives the loading state).
    Principals: PrincipalSummary list option
    /// True while a `ListPrincipals` call is in flight.
    Loading: bool
    /// Show only team-less principals — the cleanup workflow's entry point.
    TeamLessOnly: bool
    /// The reverse directory (id → display name + email) the rows render
    /// through, resolved after the principal list loads. Ids it does not
    /// hold render raw — the list stays functional, just less friendly.
    Directory: UserDirectory.Model
    /// Page-level error banner (list load failure, non-admin caller,
    /// disabled substrate), as a `Notice.Problem`. Cleared by `DismissError`
    /// or the next load.
    Error: Notice option
    /// Per-row offboard modal. `None` = closed.
    Offboard: OffboardModalState option
    /// This module's section of `ClientModuleContext.Messages`,
    /// kept from `init` so `update` authors reader-facing text in the
    /// session's language (a locale switch re-initialises the module).
    Messages: PlatformUsersMessages
}

type Msg =
    | LoadPrincipals
    | PrincipalsLoaded of Result<PrincipalSummary list, string>
    | ToggleTeamLessOnly
    | DirectoryMsg of UserDirectory.Msg
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

/// The signed-in operator's user id — the `actorUserId` attributed to
/// every offboard the panel fires.
let private actorUserId () = UserSession.getUserId ()

/// The server refuses a token-less destructive call with
/// `Error "offboard confirmation required"` when a confirmation policy is
/// active. Detect that specific refusal so the modal can
/// switch to its confirmation step rather than surfacing it as a plain
/// transport error. Matched case-insensitively on the stable phrase.
let private isConfirmationRequired (err: string) =
    err.ToLower().Contains "confirmation required"

// ─── Effects ─────────────────────────────────────────────────────────

type internal Steps = Step<Msg, RemoteEffect<Msg>> list

let internal loadPrincipals () : Steps = [
    Do(
        RemoteEffect.call
            "IPlatformTenantApi.ListPrincipals"
            tenantApi.ListPrincipals
            ()
            (RemoteEffect.flatten >> PrincipalsLoaded)
    )
]

let internal preview (scopeId: string) : Steps = [
    Do(
        RemoteEffect.call
            "IPlatformTenantApi.PreviewDeprovision"
            tenantApi.PreviewDeprovision
            scopeId
            (RemoteEffect.flatten >> PreviewLoaded)
    )
]

let internal requestToken (scopeId: string) (reason: string) : Steps = [
    Do(
        RemoteEffect.call
            "IPlatformTenantApi.RequestDeprovisionToken"
            tenantApi.RequestDeprovisionToken
            (scopeId, reason)
            (RemoteEffect.flatten >> TokenMinted)
    )
]

/// The directory's steps in this screen's message type.
let private lift (steps: Step<UserDirectory.Msg, RemoteEffect<UserDirectory.Msg>> list) : Steps =
    Step.mapAll DirectoryMsg (RemoteEffect.map DirectoryMsg) steps

/// Ask the directory for `ids`; it skips blank and already-known ids.
let internal resolve (ids: string list) (model: Model) : Steps =
    UserDirectory.resolve ids model.Directory |> lift

// ─── Init ────────────────────────────────────────────────────────────

let init (ctx: ClientModuleContext) : Model * Step<Msg, RemoteEffect<Msg>> list =
    ({
        Principals = None
        Loading = true
        TeamLessOnly = false
        Directory = UserDirectory.empty
        Error = None
        Offboard = None
        Messages = ctx.Messages.PlatformUsers
     },
     loadPrincipals ())

// ─── Update helpers ──────────────────────────────────────────────────

/// Fire the destructive call appropriate to the modal's current step +
/// kind. In `Confirming` we always use the confirmed plain path (the
/// export path has no confirmed variant); otherwise the plain or export
/// path per `Kind`.
let internal submit (m: OffboardModalState) : Steps =
    let reason = m.Reason.Trim()
    let actor = actorUserId ()

    match m.Step with
    | Confirming -> [
        Do(
            RemoteEffect.call
                "IPlatformTenantApi.DeprovisionTenantConfirmed"
                tenantApi.DeprovisionTenantConfirmed
                (m.ScopeId, actor, reason, m.Token.Trim())
                (RemoteEffect.flatten >> OffboardDone)
        )
      ]
    | _ ->
        match m.Kind with
        | PlainOffboard -> [
            Do(
                RemoteEffect.call
                    "IPlatformTenantApi.DeprovisionTenant"
                    tenantApi.DeprovisionTenant
                    (m.ScopeId, actor, reason)
                    (RemoteEffect.flatten >> OffboardDone)
            )
          ]
        | ExportOffboard -> [
            Do(
                RemoteEffect.call
                    "IPlatformTenantApi.ExportThenDeprovision"
                    tenantApi.ExportThenDeprovision
                    (m.ScopeId, actor, reason)
                    (RemoteEffect.flatten >> ExportOffboardDone)
            )
          ]

/// Apply a destructive-call result (shared by the plain + export folds):
/// success → `Completed`; a confirmation-required refusal (only when not
/// already confirming) → switch to the `Confirming` step; anything else →
/// inline error.
let private applyOffboardResult
    (summary: LifecycleSummary option)
    (archive: LifecycleExportArchive option)
    (err: string option)
    (model: Model)
    : Model * Steps =
    match model.Offboard with
    | None -> model, []
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
            []
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
            []
        | None, Some e ->
            {
                model with
                    Offboard =
                        Some {
                            m with
                                Busy = false
                                Error = Some(Notice.Problem e)
                        }
            },
            []
        | None, None ->
            {
                model with
                    Offboard = Some { m with Busy = false }
            },
            []

// ─── Update ──────────────────────────────────────────────────────────

/// The transitions, with their remote calls as values.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | LoadPrincipals ->
        {
            model with
                Loading = true
                Error = None
        },
        loadPrincipals ()

    | PrincipalsLoaded(Ok principals) ->
        // A principal the reload did not change stays the record the page
        // already holds, so its row is not built again.
        let principals =
            Memo.keepEqual
                (fun (p: PrincipalSummary) -> p.UserId)
                (model.Principals |> Option.defaultValue [])
                principals

        {
            model with
                Principals = Some principals
                Loading = false
                Error = None
        },
        resolve (principals |> List.map _.UserId) model

    | PrincipalsLoaded(Error e) ->
        {
            model with
                Principals = Some []
                Loading = false
                Error = Some(Notice.Problem e)
        },
        []

    | ToggleTeamLessOnly ->
        {
            model with
                TeamLessOnly = not model.TeamLessOnly
        },
        []

    | DirectoryMsg subMsg ->
        let directory, steps = UserDirectory.update subMsg model.Directory
        { model with Directory = directory }, lift steps

    | DismissError -> { model with Error = None }, []

    | OpenOffboard(userId, kind) ->
        {
            model with
                Offboard =
                    Some {
                        UserId = userId
                        ScopeId = ScopeKey.container (ScopeKey.User(UserId userId))
                        Kind = kind
                        Reason = ""
                        Token = ""
                        Step = Compose
                        Busy = false
                        Error = None
                    }
        },
        []

    | CloseOffboard -> { model with Offboard = None }, []

    | SetOffboardReason reason ->
        {
            model with
                Offboard = model.Offboard |> Option.map (fun m -> { m with Reason = reason })
        },
        []

    | SetOffboardToken token ->
        {
            model with
                Offboard = model.Offboard |> Option.map (fun m -> { m with Token = token })
        },
        []

    | RequestPreview ->
        match model.Offboard with
        | Some m when not m.Busy ->
            {
                model with
                    Offboard = Some { m with Busy = true; Error = None }
            },
            preview m.ScopeId
        | _ -> model, []

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
        []

    | PreviewLoaded(Error e) ->
        {
            model with
                Offboard =
                    model.Offboard
                    |> Option.map (fun m -> {
                        m with
                            Busy = false
                            Error = Some(Notice.Problem e)
                    })
        },
        []

    | RequestToken ->
        match model.Offboard with
        | Some m when not m.Busy ->
            {
                model with
                    Offboard = Some { m with Busy = true; Error = None }
            },
            requestToken m.ScopeId (m.Reason.Trim())
        | _ -> model, []

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
        []

    | TokenMinted(Error e) ->
        {
            model with
                Offboard =
                    model.Offboard
                    |> Option.map (fun m -> {
                        m with
                            Busy = false
                            Error = Some(Notice.Problem e)
                    })
        },
        []

    | SubmitOffboard ->
        match model.Offboard with
        | Some m when m.Busy -> model, []
        | Some m when m.Reason.Trim() = "" ->
            {
                model with
                    Offboard =
                        Some {
                            m with
                                Error = Some(Notice.Problem model.Messages.ReasonRequired)
                        }
            },
            []
        | Some m when m.Step = Confirming && m.Token.Trim() = "" ->
            {
                model with
                    Offboard =
                        Some {
                            m with
                                Error = Some(Notice.Problem model.Messages.TokenRequired)
                        }
            },
            []
        | Some m ->
            {
                model with
                    Offboard = Some { m with Busy = true; Error = None }
            },
            submit m
        | None -> model, []

    | OffboardDone(Ok summary) -> applyOffboardResult (Some summary) None None model

    | OffboardDone(Error e) -> applyOffboardResult None None (Some e) model

    | ExportOffboardDone(Ok result) -> applyOffboardResult (Some result.Summary) (Some result.Archive) None model

    | ExportOffboardDone(Error e) -> applyOffboardResult None None (Some e) model

// ─── View ────────────────────────────────────────────────────────────

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: PlatformUsersMessages
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./PlatformUsersTree.fs")

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "PlatformUsersTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.PlatformUsers
            Locale = ctx.Locale
        |}
    )

// ─── Module creation ─────────────────────────────────────────────────

/// This built-in's module id, reserved under the `_sdk.` namespace.
let moduleId = "_sdk.PlatformUsers"

/// The built-in platform-users admin (`ShellSlots.builtIn` builds its module).
/// Registered under the `_sdk.` namespace in the "Platform Management"
/// sidebar group; the shell's role filter hides the group from non-admin
/// callers, and every `IPlatformTenantApi` method is Platform-Admin gated
/// server-side regardless (GP 4). Injected only when
/// `ClientConfig.Chrome.PlatformUsers = DefaultPlatformUsers` (GP 11/13).
let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Users"
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
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module PlatformAdminUI

open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Phase 4b — built-in Platform Admin module ───────────────────────
//
// SDK-built-in admin module surfacing `PlatformAdminApi`'s role-
// management endpoints: list current Platform Admins, assign new
// admins, revoke existing ones. Auto-injected by the shell whenever
// the caller's `Model.PlatformRole = Some PlatformAdmin` (the sidebar
// gate added in commit 4f.2 hides the "Platform Admin" group otherwise).
//
// **Two tabs:**
//   - "Admins" — the current admin list with assign / revoke controls.
//   - "Settings" — placeholder for future deployment-wide runtime
//     configuration (planned Phase 4b deferred follow-up: runtime
//     mutation of `ServerConfig.PlatformKnowledgeBase` and similar
//     knobs through this surface).
//
// **Server-side gating.** Read methods (`ListPlatformAdmins`) are open
// to any authenticated caller. Write methods (Assign / Revoke) are
// gated server-side on `canModifyPlatformConfig`; the client-side gate
// (sidebar visibility) is UX-only — non-admin callers landing on the
// page directly via URL would still see the API refuse writes.
//
// Sibling KB content admin (`PlatformKnowledgeAdminUI`) ships from the
// KnowledgeBase companion in commit 4f.4. Both modules sit under the
// "Platform Admin" sidebar group alongside the re-grouped HealthMonitor
// (4f.1).

// ─── Model ───────────────────────────────────────────────────────────

type Tab =
    | AdminsTab
    | TeamsTab
    | SettingsTab

type LoadState<'T> =
    | NotLoaded
    | Loading
    | Loaded of 'T
    | LoadError of string

type Model = {
    /// Currently rendered section.
    ActiveTab: Tab
    /// Live admin list from `ListPlatformAdmins`. Refreshed on shell
    /// init and after every successful Assign / Revoke.
    Admins: LoadState<string list>
    /// Free-text input for the Assign Admin form. Local React state
    /// would be cleaner per the SDK convention, but we keep it on the
    /// Elmish model here so the AssignError banner can pair with the
    /// last attempted value without a separate ref.
    AssignInput: string
    /// Inline error from the most recent Assign attempt — `None` when
    /// no attempt yet, or after a successful one. Cleared on input
    /// edit so the banner doesn't linger after the user retypes.
    AssignError: string option
    /// Pending Assign confirmation. `Some userId` = the "are you sure?"
    /// modal is open for that user id; the modal's Confirm button
    /// dispatches the actual `AssignPlatformAdmin` call. `None` = no
    /// pending confirmation. Granting `PlatformAdmin` is irreversible
    /// from the recipient's perspective (they immediately gain
    /// deployment-wide configuration rights), so the confirm step is
    /// a deliberate UX brake on accidental clicks.
    AssignConfirm: string option
    /// Inline error from a Revoke attempt, keyed by user id so the
    /// banner can render next to the right row.
    RevokeError: Map<string, string>
    /// Create-team form: name input.
    NewTeamName: string
    /// Create-team form: initial-owner user id. Empty string = no
    /// selection yet; the operator can populate via the directory
    /// typeahead OR by clicking "Self" (which writes their own user
    /// id into this field).
    NewTeamOwnerUserId: string
    /// Inline error from the most recent CreateTeamWithOwner attempt.
    CreateTeamError: string option
    /// Tracks whether a CreateTeamWithOwner call is in flight, so
    /// the submit button can disable to prevent double-fire.
    CreateTeamInFlight: bool
    /// Phase 4b deferred follow-up — current runtime PlatformKnowledgeBase
    /// state. Loaded on shell init via `GetPlatformKnowledgeBase`;
    /// updated optimistically after a successful `Set`.
    PlatformKnowledgeBase: LoadState<PlatformKnowledgeBaseMode>
    /// Inline error from a SetPlatformKnowledgeBase attempt.
    SettingsError: string option
}

type Msg =
    | SwitchTab of Tab
    | RefreshAdmins
    | AdminsLoaded of Result<string list, string>
    | UpdateAssignInput of string
    | RequestAssignConfirm
    | CancelAssignConfirm
    | ConfirmAssign
    | AssignResolved of targetUserId: string * Result<unit, string>
    | RequestRevoke of targetUserId: string
    | RevokeResolved of targetUserId: string * Result<unit, string>
    | SetNewTeamName of string
    | SetNewTeamOwnerUserId of string
    | UseSelfAsTeamOwner
    | SubmitCreateTeam
    | CreateTeamResolved of Result<TeamInfo, string>
    | PlatformKnowledgeBaseLoaded of Result<PlatformKnowledgeBaseMode, string>
    | TogglePlatformKnowledgeBase of PlatformKnowledgeBaseMode
    | TogglePlatformKnowledgeBaseResolved of requested: PlatformKnowledgeBaseMode * Result<unit, string>

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see UserSession.fs:342 + SDK.Client.fs installRequestGuard.
let private platformAdminApi: PlatformAdminApi =
    Api.makeProxy<PlatformAdminApi> (customOptions = UserSession.withRequestHeaders)

let private teamApi: TeamApi =
    Api.makeProxy<TeamApi> (customOptions = UserSession.withRequestHeaders)

// ─── Init / commands ─────────────────────────────────────────────────

let private loadAdminsCmd () =
    Cmd.OfRemoting.call platformAdminApi.ListPlatformAdmins () (fun list -> AdminsLoaded(Ok list)) (fun ex ->
        AdminsLoaded(Error ex.Message))

let private assignCmd (targetUserId: string) =
    Cmd.OfRemoting.call
        platformAdminApi.AssignPlatformAdmin
        targetUserId
        (fun result -> AssignResolved(targetUserId, result))
        (fun ex -> AssignResolved(targetUserId, Error ex.Message))

let private revokeCmd (targetUserId: string) =
    Cmd.OfRemoting.call
        platformAdminApi.RevokePlatformAdmin
        targetUserId
        (fun result -> RevokeResolved(targetUserId, result))
        (fun ex -> RevokeResolved(targetUserId, Error ex.Message))

let private loadPlatformKnowledgeBaseCmd () =
    Cmd.OfRemoting.call
        platformAdminApi.GetPlatformKnowledgeBase
        ()
        (fun mode -> PlatformKnowledgeBaseLoaded(Ok mode))
        (fun ex -> PlatformKnowledgeBaseLoaded(Error ex.Message))

let private setPlatformKnowledgeBaseCmd (mode: PlatformKnowledgeBaseMode) =
    Cmd.OfRemoting.call
        platformAdminApi.SetPlatformKnowledgeBase
        mode
        (fun result -> TogglePlatformKnowledgeBaseResolved(mode, result))
        (fun ex -> TogglePlatformKnowledgeBaseResolved(mode, Error ex.Message))

let private createTeamCmd (request: CreateTeamRequest) =
    Cmd.OfRemoting.call teamApi.CreateTeamWithOwner request CreateTeamResolved (fun ex ->
        CreateTeamResolved(Error ex.Message))

let init () =
    let model = {
        ActiveTab = AdminsTab
        Admins = Loading
        AssignInput = ""
        AssignError = None
        AssignConfirm = None
        RevokeError = Map.empty
        NewTeamName = ""
        NewTeamOwnerUserId = ""
        CreateTeamError = None
        CreateTeamInFlight = false
        PlatformKnowledgeBase = Loading
        SettingsError = None
    }

    model, Cmd.batch [ loadAdminsCmd (); loadPlatformKnowledgeBaseCmd () ]

// ─── Update ──────────────────────────────────────────────────────────

let update (msg: Msg) (model: Model) =
    match msg with
    | SwitchTab tab -> { model with ActiveTab = tab }, Cmd.none

    | RefreshAdmins -> { model with Admins = Loading }, loadAdminsCmd ()

    | AdminsLoaded(Ok list) ->
        {
            model with
                Admins = Loaded(list |> List.sort)
        },
        Cmd.none

    | AdminsLoaded(Error msg) -> { model with Admins = LoadError msg }, Cmd.none

    | UpdateAssignInput input ->
        {
            model with
                AssignInput = input
                AssignError = None
        },
        Cmd.none

    | RequestAssignConfirm ->
        let trimmed = model.AssignInput.Trim()

        if String.length trimmed = 0 then
            {
                model with
                    AssignError = Some "Enter a user id."
            },
            Cmd.none
        else
            {
                model with
                    AssignError = None
                    AssignConfirm = Some trimmed
            },
            Cmd.none

    | CancelAssignConfirm -> { model with AssignConfirm = None }, Cmd.none

    | ConfirmAssign ->
        match model.AssignConfirm with
        | None -> model, Cmd.none
        | Some targetUserId ->
            {
                model with
                    AssignConfirm = None
                    AssignError = None
            },
            assignCmd targetUserId

    | AssignResolved(_, Ok()) ->
        {
            model with
                AssignInput = ""
                AssignError = None
                AssignConfirm = None
                Admins = Loading
        },
        loadAdminsCmd ()

    | AssignResolved(_, Error msg) -> { model with AssignError = Some msg }, Cmd.none

    | RequestRevoke targetUserId ->
        {
            model with
                RevokeError = model.RevokeError |> Map.remove targetUserId
        },
        revokeCmd targetUserId

    | RevokeResolved(_, Ok()) -> { model with Admins = Loading }, loadAdminsCmd ()

    | RevokeResolved(targetUserId, Error msg) ->
        {
            model with
                RevokeError = model.RevokeError |> Map.add targetUserId msg
        },
        Cmd.none

    | SetNewTeamName name ->
        {
            model with
                NewTeamName = name
                CreateTeamError = None
        },
        Cmd.none

    | SetNewTeamOwnerUserId userId ->
        {
            model with
                NewTeamOwnerUserId = userId
                CreateTeamError = None
        },
        Cmd.none

    | UseSelfAsTeamOwner ->
        let selfId = UserSession.getUserId ()

        {
            model with
                NewTeamOwnerUserId = selfId
                CreateTeamError = None
        },
        Cmd.none

    | SubmitCreateTeam ->
        let name = model.NewTeamName.Trim()
        let ownerId = model.NewTeamOwnerUserId.Trim()

        if String.length name = 0 then
            {
                model with
                    CreateTeamError = Some "Team name can't be empty."
            },
            Cmd.none
        elif String.length ownerId = 0 then
            {
                model with
                    CreateTeamError = Some "Pick an initial owner (or use \"Self\")."
            },
            Cmd.none
        else
            {
                model with
                    CreateTeamError = None
                    CreateTeamInFlight = true
            },
            createTeamCmd {
                Name = name
                InitialOwnerUserId = ownerId
            }

    | CreateTeamResolved(Ok _) ->
        {
            model with
                NewTeamName = ""
                NewTeamOwnerUserId = ""
                CreateTeamError = None
                CreateTeamInFlight = false
        },
        Cmd.none

    | CreateTeamResolved(Error msg) ->
        {
            model with
                CreateTeamError = Some msg
                CreateTeamInFlight = false
        },
        Cmd.none

    | PlatformKnowledgeBaseLoaded(Ok mode) ->
        {
            model with
                PlatformKnowledgeBase = Loaded mode
                SettingsError = None
        },
        Cmd.none

    | PlatformKnowledgeBaseLoaded(Error msg) ->
        {
            model with
                PlatformKnowledgeBase = LoadError msg
        },
        Cmd.none

    | TogglePlatformKnowledgeBase requested ->
        { model with SettingsError = None }, setPlatformKnowledgeBaseCmd requested

    | TogglePlatformKnowledgeBaseResolved(requested, Ok()) ->
        {
            model with
                PlatformKnowledgeBase = Loaded requested
                SettingsError = None
        },
        Cmd.none

    | TogglePlatformKnowledgeBaseResolved(_, Error msg) -> { model with SettingsError = Some msg }, Cmd.none

// ─── View helpers ────────────────────────────────────────────────────

let private errorBanner (message: string) =
    Html.div [
        prop.className "px-3 py-2 my-2 rounded bg-red-50 border border-red-200 text-sm text-red-700"
        prop.text message
    ]

let private adminRow (model: Model) (dispatch: Msg -> unit) (userId: string) =
    let revokeError = model.RevokeError |> Map.tryFind userId

    Html.div [
        prop.className "py-2 px-3 border-b border-border last:border-0"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between gap-3"
                prop.children [
                    Html.span [ prop.className "text-sm font-mono text-text-primary"; prop.text userId ]
                    Html.button [
                        prop.className
                            "px-3 py-1 text-xs font-medium rounded border border-red-300 text-red-700 hover:bg-red-50"
                        prop.text "Revoke"
                        prop.onClick (fun _ -> dispatch (RequestRevoke userId))
                    ]
                ]
            ]
            match revokeError with
            | Some msg -> errorBanner msg
            | None -> Html.none
        ]
    ]

let private adminsTabView (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "p-4 flex flex-col gap-4"
        prop.children [
            // Assign form
            Html.div [
                prop.className "flex flex-col gap-2 p-3 rounded border border-border bg-white"
                prop.children [
                    Html.h3 [
                        prop.className "text-sm font-medium text-text-primary"
                        prop.text "Assign Platform Admin"
                    ]
                    Html.p [
                        prop.className "text-xs text-text-secondary"
                        prop.text
                            "Start typing a name or email — directory matches appear as you type. Pick one to capture their stable identity-provider user id. Advanced: paste a raw user id (e.g. an Entra `oid`) to assign directly."
                    ]
                    Html.div [
                        prop.className "flex gap-2 items-start"
                        prop.children [
                            Html.div [
                                prop.className "flex-1"
                                prop.children [
                                    UserDirectoryTypeahead.userTypeahead
                                        model.AssignInput
                                        (fun v -> dispatch (UpdateAssignInput v))
                                        "Name, email, or user id"
                                        UserDirectoryTypeahead.pickUserId
                                ]
                            ]
                            Html.button [
                                prop.className
                                    "px-3 py-1 text-sm font-medium rounded bg-brand text-white hover:bg-brand-dark"
                                prop.text "Assign"
                                prop.onClick (fun _ -> dispatch RequestAssignConfirm)
                            ]
                        ]
                    ]
                    match model.AssignError with
                    | Some msg -> errorBanner msg
                    | None -> Html.none
                ]
            ]

            // Admin list
            Html.div [
                prop.className "rounded border border-border bg-white"
                prop.children [
                    Html.div [
                        prop.className "flex items-center justify-between px-3 py-2 border-b border-border"
                        prop.children [
                            Html.h3 [
                                prop.className "text-sm font-medium text-text-primary"
                                prop.text "Current Platform Admins"
                            ]
                            Html.button [
                                prop.className "text-xs text-brand hover:underline"
                                prop.text "Refresh"
                                prop.onClick (fun _ -> dispatch RefreshAdmins)
                            ]
                        ]
                    ]
                    match model.Admins with
                    | NotLoaded
                    | Loading -> Html.p [ prop.className "text-sm text-text-secondary p-3"; prop.text "Loading..." ]
                    | LoadError msg -> errorBanner msg
                    | Loaded [] ->
                        Html.p [
                            prop.className "text-sm text-text-secondary p-3"
                            prop.text "No Platform Admins configured."
                        ]
                    | Loaded admins ->
                        Html.div [
                            prop.className "divide-y divide-border"
                            prop.children (admins |> List.map (adminRow model dispatch))
                        ]
                ]
            ]
        ]
    ]

/// "Are you sure?" overlay for `AssignPlatformAdmin`. Grants are
/// effectively irreversible once observed (the recipient may have
/// already used the new role to mutate deployment configuration),
/// so the confirm step is a deliberate brake on accidental fires —
/// e.g. an operator typing-Enter on the wrong autocomplete row.
let private assignConfirmModalView (targetUserId: string) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "fixed inset-0 bg-black/40 flex items-center justify-center z-50"
        prop.children [
            Html.div [
                prop.className "bg-white rounded-lg shadow-lg p-6 w-full max-w-md space-y-4"
                prop.children [
                    Html.h3 [ prop.className "text-lg font-semibold"; prop.text "Grant Platform Admin?" ]
                    Html.p [
                        prop.className "text-sm text-text-secondary"
                        prop.text
                            "Platform Admins can change deployment-wide configuration, manage every team, and assign / revoke other Platform Admins. The grant takes effect immediately and the recipient retains any team-level roles they already hold."
                    ]
                    Html.div [
                        prop.className "rounded border border-border bg-gray-50 px-3 py-2"
                        prop.children [
                            Html.span [ prop.className "text-xs text-text-secondary"; prop.text "User id: " ]
                            Html.span [ prop.className "text-sm font-mono text-text-primary"; prop.text targetUserId ]
                        ]
                    ]
                    Html.div [
                        prop.className "flex justify-end gap-3 pt-2"
                        prop.children [
                            Html.button [
                                prop.className
                                    "px-4 py-2 rounded text-sm font-medium border border-border text-text-primary hover:bg-gray-50"
                                prop.text "Cancel"
                                prop.onClick (fun _ -> dispatch CancelAssignConfirm)
                            ]
                            Html.button [
                                prop.className
                                    "px-4 py-2 rounded text-sm font-medium bg-brand text-white hover:bg-brand-dark"
                                prop.text "Grant Platform Admin"
                                prop.onClick (fun _ -> dispatch ConfirmAssign)
                            ]
                        ]
                    ]
                ]
            ]
        ]
    ]

let private teamsTabView (model: Model) (dispatch: Msg -> unit) =
    let selfId = UserSession.getUserId ()
    let selfDisplay = UserSession.getDisplayName () |> Option.defaultValue selfId
    let ownerIsSelf = model.NewTeamOwnerUserId.Trim() = selfId

    Html.div [
        prop.className "p-4 flex flex-col gap-4"
        prop.children [
            Html.div [
                prop.className "flex flex-col gap-3 p-3 rounded border border-border bg-white"
                prop.children [
                    Html.h3 [
                        prop.className "text-sm font-medium text-text-primary"
                        prop.text "Create a team"
                    ]
                    Html.p [
                        prop.className "text-xs text-text-secondary"
                        prop.text
                            "Spin up a new team and name its initial Owner. The Owner is the only role that can add or remove Team Admins; everything else (Members, Admins, ownership transfer) is managed within the team or by another Platform Admin."
                    ]

                    Html.div [
                        prop.className "flex flex-col gap-1"
                        prop.children [
                            Html.label [
                                prop.className "text-xs font-medium text-text-secondary"
                                prop.text "Team name"
                            ]
                            Html.input [
                                prop.className "px-2 py-1 text-sm border border-border rounded"
                                prop.placeholder "e.g. Marketing Analytics"
                                prop.value model.NewTeamName
                                prop.onChange (fun (v: string) -> dispatch (SetNewTeamName v))
                            ]
                        ]
                    ]

                    Html.div [
                        prop.className "flex flex-col gap-1"
                        prop.children [
                            Html.label [
                                prop.className "text-xs font-medium text-text-secondary"
                                prop.text "Initial owner"
                            ]
                            Html.div [
                                prop.className "flex gap-2 items-start"
                                prop.children [
                                    Html.div [
                                        prop.className "flex-1"
                                        prop.children [
                                            UserDirectoryTypeahead.userTypeahead
                                                model.NewTeamOwnerUserId
                                                (fun v -> dispatch (SetNewTeamOwnerUserId v))
                                                "Name, email, or user id"
                                                UserDirectoryTypeahead.pickUserId
                                        ]
                                    ]
                                    Html.button [
                                        prop.className [
                                            "px-3 py-1 text-sm font-medium rounded border transition-colors"
                                            if ownerIsSelf then
                                                "bg-brand/10 text-brand border-brand"
                                            else
                                                "bg-white text-text-primary border-border hover:bg-gray-50"
                                        ]
                                        prop.title (sprintf "Make yourself (%s) the owner" selfDisplay)
                                        prop.text (if ownerIsSelf then "Self ✓" else "Self")
                                        prop.onClick (fun _ -> dispatch UseSelfAsTeamOwner)
                                    ]
                                ]
                            ]
                            if ownerIsSelf then
                                Html.span [
                                    prop.className "text-xs text-text-secondary"
                                    prop.text (sprintf "You (%s) will become the team's initial Owner." selfDisplay)
                                ]
                        ]
                    ]

                    Html.div [
                        prop.className "flex justify-end gap-2"
                        prop.children [
                            Html.button [
                                prop.disabled model.CreateTeamInFlight
                                prop.className [
                                    "px-3 py-1 text-sm font-medium rounded"
                                    if model.CreateTeamInFlight then
                                        "bg-gray-300 text-gray-500 cursor-not-allowed"
                                    else
                                        "bg-brand text-white hover:bg-brand-dark"
                                ]
                                prop.text (
                                    if model.CreateTeamInFlight then
                                        "Creating…"
                                    else
                                        "Create team"
                                )
                                prop.onClick (fun _ -> dispatch SubmitCreateTeam)
                            ]
                        ]
                    ]

                    match model.CreateTeamError with
                    | Some msg -> errorBanner msg
                    | None -> Html.none
                ]
            ]
        ]
    ]

let private platformKnowledgeBaseLabel (mode: PlatformKnowledgeBaseMode) =
    match mode with
    | EnabledPlatformKnowledgeBase -> "Enabled"
    | NoPlatformKnowledgeBase -> "Disabled"

let private platformKnowledgeBaseToggle (model: Model) (dispatch: Msg -> unit) =
    let renderToggle (current: PlatformKnowledgeBaseMode) =
        let other =
            match current with
            | EnabledPlatformKnowledgeBase -> NoPlatformKnowledgeBase
            | NoPlatformKnowledgeBase -> EnabledPlatformKnowledgeBase

        let buttonLabel =
            match current with
            | EnabledPlatformKnowledgeBase -> "Disable"
            | NoPlatformKnowledgeBase -> "Enable"

        let buttonClass =
            match current with
            | EnabledPlatformKnowledgeBase ->
                "px-3 py-1 text-xs font-medium rounded border border-red-300 text-red-700 hover:bg-red-50"
            | NoPlatformKnowledgeBase -> "px-3 py-1 text-xs font-medium rounded bg-brand text-white hover:bg-brand-dark"

        Html.div [
            prop.className "flex items-center justify-between gap-3"
            prop.children [
                Html.div [
                    prop.className "flex flex-col gap-0.5"
                    prop.children [
                        Html.span [
                            prop.className "text-sm font-medium text-text-primary"
                            prop.text "Platform Knowledge Base"
                        ]
                        Html.span [
                            prop.className "text-xs text-text-secondary"
                            prop.text (
                                sprintf
                                    "Currently %s. When enabled, platform-scope KB content is universally readable for authenticated users; when disabled, RAG retrieval filters it out (admin uploads still work)."
                                    (platformKnowledgeBaseLabel current)
                            )
                        ]
                    ]
                ]
                Html.button [
                    prop.className buttonClass
                    prop.text buttonLabel
                    prop.onClick (fun _ -> dispatch (TogglePlatformKnowledgeBase other))
                ]
            ]
        ]

    Html.div [
        prop.className "rounded border border-border bg-white p-4"
        prop.children [
            Html.h3 [
                prop.className "text-sm font-medium text-text-primary mb-3"
                prop.text "Platform Knowledge Base"
            ]
            match model.PlatformKnowledgeBase with
            | NotLoaded
            | Loading ->
                Html.p [
                    prop.className "text-sm text-text-secondary"
                    prop.text "Loading current state…"
                ]
            | LoadError msg -> errorBanner msg
            | Loaded current -> renderToggle current
            match model.SettingsError with
            | Some msg -> errorBanner msg
            | None -> Html.none
        ]
    ]

let private settingsTabView (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "p-4 flex flex-col gap-4"
        prop.children [
            platformKnowledgeBaseToggle model dispatch
            Html.div [
                prop.className "rounded border border-border bg-white p-4"
                prop.children [
                    Html.h3 [
                        prop.className "text-sm font-medium text-text-primary mb-2"
                        prop.text "Other Settings"
                    ]
                    Html.p [
                        prop.className "text-sm text-text-secondary"
                        prop.text
                            "Additional runtime-mutable deployment knobs land here as future Phase 4b follow-ups expose them. Today: PlatformKnowledgeBase only."
                    ]
                ]
            ]
        ]
    ]

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
            tabButton "Admins" (model.ActiveTab = AdminsTab) (fun () -> dispatch (SwitchTab AdminsTab))
            tabButton "Teams" (model.ActiveTab = TeamsTab) (fun () -> dispatch (SwitchTab TeamsTab))
            tabButton "Settings" (model.ActiveTab = SettingsTab) (fun () -> dispatch (SwitchTab SettingsTab))
        ]
    ]

/// View factory closed over the active `ClientConfig` so the
/// Phase 61 public-utility widgets can read consent / ad / premium
/// substrate state. The standard view body is unchanged; the
/// public-utility widgets append below the existing tab content when
/// `ClientConfig.PlatformAdminProfile = PublicUtilityPlatformAdminProfile`.
let private viewWith (config: ClientConfig) (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
    let content =
        match model.ActiveTab with
        | AdminsTab -> adminsTabView model dispatch
        | TeamsTab -> teamsTabView model dispatch
        | SettingsTab -> settingsTabView model dispatch

    let publicUtilitySection =
        ToolUp.Platform.PlatformAdmin.PublicUtilityWidgets.render config

    let body =
        Html.div [
            prop.className "flex flex-col h-full"
            prop.children [
                tabBar model dispatch
                content
                publicUtilitySection
                match model.AssignConfirm with
                | Some targetUserId -> assignConfirmModalView targetUserId dispatch
                | None -> Html.none
            ]
        ]

    body, Html.none

// ─── Module creation ─────────────────────────────────────────────────

/// Create the Platform Management module as an `ErasedModule`.
/// Auto-injected by `SDK.Client.run` in any non-Anonymous mode unless
/// `PlatformAdmin = NoPlatformAdmin` (config field name retained for
/// backwards compatibility; the display name + sidebar group both
/// surface as "Platform Management"). The shell's sidebar filter
/// (4f.2) hides the "Platform Management" group from non-admin
/// callers; the module itself does no client-side gating beyond the
/// standard API-error surface (server-side `canModifyPlatformConfig`
/// is the actual enforcement).
///
/// Module Id (`_sdk.PlatformAdmin`) is preserved so the server-side
/// `ServerConfig.ModuleNames` keys (RBAC permission entries) keyed
/// to it don't have to migrate. The display name and group label
/// are presentation only.
let create (config: PlatformAdminConfig option) (clientConfig: ClientConfig) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Platform Management"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.users

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.PlatformAdmin"
    |> ToolUp.Platform.ClientModule.withView (viewWith clientConfig)
    |> ToolUp.Platform.ClientModule.withGroup "Platform Management"
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.PlatformAIKeysAdminUI

open Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform
open ToolUp.AI

// ─── Phase 70 — Platform Admin AI keys module ───────────────────
//
// SDK-built-in admin module surfacing `PlatformAIKeysApi`. Lets a
// Platform Admin manage platform-scope AI keys (default for every
// request) and per-team override keys (precedence over platform-
// scope for that team's users).
//
// **Sidebar group: "Platform Admin"** — sits alongside the existing
// `_sdk.PlatformAdmin`, `_sdk.PlatformKnowledgeAdmin`, and
// `_sdk.HealthMonitor` modules. The shell's role gate hides the
// entire group from non-admin callers; the API itself also rejects
// non-admin writes with `"platform admin role required"` so a
// non-admin who somehow reaches the module via direct navigation
// gets a clear error rather than a silent failure.
//
// **The current key value is never displayed.** Stored keys appear
// only as "Key stored" / "No key" badges. Rotation means typing the
// new value into the input; there is no "show me what's stored"
// affordance by design.

// ─── Model ──────────────────────────────────────────────────────

type Banner =
    | Success of string
    | Failure of string

type Model = {
    Descriptors: AIProviderDescriptor list
    PlatformStatuses: PlatformAIKeyStatus list
    Teams: PlatformAIKeysTeamView list
    /// Currently-selected team for the per-team keys section. `None`
    /// hides the per-team table; selecting from the dropdown loads
    /// statuses for that team.
    SelectedTeamId: string option
    TeamStatuses: TeamAIKeyStatus list
    Banner: Banner option
    Loading: bool
}

type Msg =
    | LoadDescriptors of ApiCall<unit, AIProviderDescriptor list>
    | LoadPlatformStatuses of ApiCall<unit, PlatformAIKeyStatus list>
    | LoadTeams of ApiCall<unit, PlatformAIKeysTeamView list>
    | SelectTeam of string option
    | LoadTeamStatuses of ApiCall<string, TeamAIKeyStatus list>
    | SetPlatformKey of providerId: string * apiKey: string
    | DeletePlatformKey of providerId: string
    | TestPlatformKey of providerId: string
    | SetTeamKey of teamId: string * providerId: string * apiKey: string
    | DeleteTeamKey of teamId: string * providerId: string
    | TestTeamKey of teamId: string * providerId: string
    | ActionFinished of Result<unit, string> * successMsg: string
    | ApiError of string
    | DismissBanner

let private api =
    Api.makeProxy<PlatformAIKeysApi> (customOptions = UserSession.withRequestHeaders)

// ─── Init ───────────────────────────────────────────────────────

let init () : Model * Cmd<Msg> =
    {
        Descriptors = []
        PlatformStatuses = []
        Teams = []
        SelectedTeamId = None
        TeamStatuses = []
        Banner = None
        Loading = true
    },
    Cmd.batch [
        Cmd.OfRemoting.call api.ListPlatformDescriptors () (Finished >> LoadDescriptors) (fun ex -> ApiError ex.Message)
        Cmd.OfRemoting.call api.ListPlatformKeyStatuses () (Finished >> LoadPlatformStatuses) (fun ex ->
            ApiError ex.Message)
        Cmd.OfRemoting.call api.ListTeams () (Finished >> LoadTeams) (fun ex -> ApiError ex.Message)
    ]

// ─── Update ─────────────────────────────────────────────────────

let private refreshPlatform () =
    Cmd.OfRemoting.call api.ListPlatformKeyStatuses () (Finished >> LoadPlatformStatuses) (fun ex ->
        ApiError ex.Message)

let private refreshTeam (teamId: string) =
    Cmd.OfRemoting.call api.ListTeamKeyStatuses teamId (Finished >> LoadTeamStatuses) (fun ex -> ApiError ex.Message)

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | LoadDescriptors(Start()) ->
        { model with Loading = true },
        Cmd.OfRemoting.call api.ListPlatformDescriptors () (Finished >> LoadDescriptors) (fun ex -> ApiError ex.Message)
    | LoadDescriptors(Finished ds) ->
        {
            model with
                Descriptors = ds
                Loading = false
        },
        Cmd.none

    | LoadPlatformStatuses(Start()) -> { model with Loading = true }, refreshPlatform ()
    | LoadPlatformStatuses(Finished ss) ->
        {
            model with
                PlatformStatuses = ss
                Loading = false
        },
        Cmd.none

    | LoadTeams(Start()) ->
        { model with Loading = true },
        Cmd.OfRemoting.call api.ListTeams () (Finished >> LoadTeams) (fun ex -> ApiError ex.Message)
    | LoadTeams(Finished ts) ->
        {
            model with
                Teams = ts
                Loading = false
        },
        Cmd.none

    | SelectTeam teamIdOpt ->
        let cmd =
            match teamIdOpt with
            | Some teamId -> refreshTeam teamId
            | None -> Cmd.none

        {
            model with
                SelectedTeamId = teamIdOpt
                TeamStatuses = []
        },
        cmd

    | LoadTeamStatuses(Start teamId) -> { model with Loading = true }, refreshTeam teamId
    | LoadTeamStatuses(Finished ss) ->
        {
            model with
                TeamStatuses = ss
                Loading = false
        },
        Cmd.none

    | SetPlatformKey(providerId, apiKey) ->
        { model with Loading = true },
        Cmd.OfRemoting.call
            api.SetPlatformKey
            {
                ProviderId = providerId
                ApiKey = apiKey
            }
            (fun r -> ActionFinished(r, $"Saved platform key for {providerId}."))
            (fun ex -> ApiError ex.Message)

    | DeletePlatformKey providerId ->
        { model with Loading = true },
        Cmd.OfRemoting.call
            api.DeletePlatformKey
            providerId
            (fun r -> ActionFinished(r, $"Removed platform key for {providerId}."))
            (fun ex -> ApiError ex.Message)

    | TestPlatformKey providerId ->
        { model with Loading = true },
        Cmd.OfRemoting.call
            api.TestPlatformKey
            providerId
            (fun r -> ActionFinished(r, $"Platform key for {providerId} accepted by the provider."))
            (fun ex -> ApiError ex.Message)

    | SetTeamKey(teamId, providerId, apiKey) ->
        { model with Loading = true },
        Cmd.OfRemoting.call
            api.SetTeamKey
            {
                TeamId = teamId
                ProviderId = providerId
                ApiKey = apiKey
            }
            (fun r -> ActionFinished(r, $"Saved team key for {providerId} on team {teamId}."))
            (fun ex -> ApiError ex.Message)

    | DeleteTeamKey(teamId, providerId) ->
        { model with Loading = true },
        Cmd.OfRemoting.call
            api.DeleteTeamKey
            (teamId, providerId)
            (fun r -> ActionFinished(r, $"Removed team key for {providerId} on team {teamId}."))
            (fun ex -> ApiError ex.Message)

    | TestTeamKey(teamId, providerId) ->
        { model with Loading = true },
        Cmd.OfRemoting.call
            api.TestTeamKey
            (teamId, providerId)
            (fun r -> ActionFinished(r, $"Team key for {providerId} on team {teamId} accepted."))
            (fun ex -> ApiError ex.Message)

    | ActionFinished(Ok(), successMsg) ->
        let refreshCmd =
            match model.SelectedTeamId with
            | Some tid -> Cmd.batch [ refreshPlatform (); refreshTeam tid ]
            | None -> refreshPlatform ()

        {
            model with
                Banner = Some(Success successMsg)
                Loading = false
        },
        refreshCmd

    | ActionFinished(Error err, _) ->
        {
            model with
                Banner = Some(Failure err)
                Loading = false
        },
        Cmd.none

    | ApiError msg ->
        {
            model with
                Banner = Some(Failure msg)
                Loading = false
        },
        Cmd.none

    | DismissBanner -> { model with Banner = None }, Cmd.none

// ─── View ───────────────────────────────────────────────────────

let private bannerView (banner: Banner) (dispatch: Msg -> unit) =
    let className, text =
        match banner with
        | Success msg -> "bg-green-50 border-green-200 text-green-700", msg
        | Failure msg -> "bg-red-50 border-red-200 text-red-700", msg

    Html.div [
        prop.className $"p-3 border rounded text-sm cursor-pointer mb-3 {className}"
        prop.onClick (fun _ -> dispatch DismissBanner)
        prop.text text
    ]

/// Rendered once per provider. Local React state for the password
/// input; dispatch on explicit button press only. The password is
/// never bound to the Elmish model — that would leak it into
/// devtools snapshots.
[<ReactComponent>]
let private KeyRow
    (descriptor: AIProviderDescriptor)
    (hasKey: bool)
    (loading: bool)
    (onSave: string -> unit)
    (onDelete: unit -> unit)
    (onTest: unit -> unit)
    =
    let pendingKey, setPendingKey = React.useState ""
    let canSave = not loading && pendingKey.Trim() <> ""

    Html.div [
        prop.className "border rounded-lg p-3 mb-2 flex flex-col gap-2"
        prop.children [
            Html.div [
                prop.className "flex items-baseline gap-3"
                prop.children [
                    Html.span [ prop.className "font-medium text-sm"; prop.text descriptor.DisplayName ]
                    Html.span [ prop.className "text-xs text-gray-500 font-mono"; prop.text descriptor.Id ]
                    if hasKey then
                        Html.span [
                            prop.className "inline-block px-2 py-0.5 rounded text-xs bg-green-100 text-green-700"
                            prop.text "✓ Key stored"
                        ]
                    else
                        Html.span [
                            prop.className "inline-block px-2 py-0.5 rounded text-xs bg-gray-100 text-gray-600"
                            prop.text "No key"
                        ]
                ]
            ]
            Html.div [
                prop.className "flex items-center gap-2"
                prop.children [
                    Html.input [
                        prop.type'.password
                        prop.className
                            "flex-1 border border-gray-300 rounded-lg px-3 py-2 text-sm font-mono focus:outline-none focus:ring-2 focus:ring-violet-400"
                        prop.placeholder (
                            if hasKey then
                                "Enter new key to rotate (current key is not displayed)"
                            else
                                "Paste the API key"
                        )
                        prop.value pendingKey
                        prop.onChange setPendingKey
                        prop.disabled loading
                    ]
                    Html.button [
                        prop.className [
                            "px-3 py-2 rounded-lg text-sm font-medium transition-colors"
                            if canSave then
                                "bg-violet-500 hover:bg-violet-600 text-white"
                            else
                                "bg-gray-200 text-gray-400 cursor-not-allowed"
                        ]
                        prop.disabled (not canSave)
                        prop.onClick (fun _ ->
                            if canSave then
                                onSave (pendingKey.Trim())
                                setPendingKey "")
                        prop.text (if hasKey then "Rotate" else "Save")
                    ]
                    Html.button [
                        prop.className
                            "px-3 py-2 rounded-lg text-sm font-medium text-gray-600 hover:bg-gray-100 disabled:opacity-50 disabled:cursor-not-allowed"
                        prop.disabled (loading || not hasKey)
                        prop.onClick (fun _ -> onTest ())
                        prop.text "Test"
                    ]
                    Html.button [
                        prop.className
                            "px-3 py-2 rounded-lg text-sm font-medium text-red-600 hover:bg-red-50 disabled:opacity-50 disabled:cursor-not-allowed"
                        prop.disabled (loading || not hasKey)
                        prop.onClick (fun _ -> onDelete ())
                        prop.text "Remove"
                    ]
                ]
            ]
        ]
    ]

let private platformSection (model: Model) (dispatch: Msg -> unit) =
    Layout.Panel.panel "Platform-wide AI keys" [
        Html.p [
            prop.className "text-sm text-gray-600 mb-3"
            prop.text
                "Default keys for every request. Per-team overrides (configured below) take precedence over these when a team's user resolves the corresponding provider."
        ]

        if model.Descriptors.IsEmpty then
            Html.div [
                prop.className "p-4 bg-amber-50 border border-amber-200 rounded text-sm text-amber-800"
                prop.text
                    "No platform AI providers are wired in this deployment. Add platform-provider bundles to the composition root to manage keys here."
            ]
        else
            Html.div [
                prop.children [
                    for descriptor in model.Descriptors do
                        let hasKey =
                            model.PlatformStatuses
                            |> List.tryFind (fun s -> s.ProviderId = descriptor.Id)
                            |> Option.map _.HasKey
                            |> Option.defaultValue false

                        KeyRow
                            descriptor
                            hasKey
                            model.Loading
                            (fun apiKey -> dispatch (SetPlatformKey(descriptor.Id, apiKey)))
                            (fun () -> dispatch (DeletePlatformKey descriptor.Id))
                            (fun () -> dispatch (TestPlatformKey descriptor.Id))
                ]
            ]
    ]

let private teamSection (model: Model) (dispatch: Msg -> unit) =
    Layout.Panel.panel "Per-team AI keys" [
        Html.p [
            prop.className "text-sm text-gray-600 mb-3"
            prop.text
                "Override the platform-wide keys for a specific team. Useful when a team brings its own paid plan or has a stricter compliance posture that requires a separate vendor account."
        ]

        if model.Teams.IsEmpty then
            Html.p [
                prop.className "text-sm text-gray-500 py-2"
                prop.text "No teams configured in this deployment."
            ]
        else
            Html.div [
                prop.children [
                    Html.div [
                        prop.className "mb-3"
                        prop.children [
                            Html.label [
                                prop.className "block text-sm font-medium text-gray-700 mb-1"
                                prop.text "Team"
                            ]
                            Html.select [
                                prop.className
                                    "w-full border border-gray-300 rounded-lg px-3 py-2 text-sm bg-white focus:outline-none focus:ring-2 focus:ring-violet-400"
                                prop.value (model.SelectedTeamId |> Option.defaultValue "")
                                prop.onChange (fun (v: string) ->
                                    let opt = if v = "" then None else Some v
                                    dispatch (SelectTeam opt))
                                prop.disabled model.Loading
                                prop.children [
                                    Html.option [ prop.value ""; prop.text "Select a team…" ]
                                    for t in model.Teams do
                                        Html.option [ prop.value t.TeamId; prop.text $"{t.DisplayName} ({t.TeamId})" ]
                                ]
                            ]
                        ]
                    ]

                    match model.SelectedTeamId with
                    | Some teamId ->
                        Html.div [
                            prop.children [
                                for descriptor in model.Descriptors do
                                    let hasKey =
                                        model.TeamStatuses
                                        |> List.tryFind (fun s -> s.ProviderId = descriptor.Id)
                                        |> Option.map _.HasKey
                                        |> Option.defaultValue false

                                    KeyRow
                                        descriptor
                                        hasKey
                                        model.Loading
                                        (fun apiKey -> dispatch (SetTeamKey(teamId, descriptor.Id, apiKey)))
                                        (fun () -> dispatch (DeleteTeamKey(teamId, descriptor.Id)))
                                        (fun () -> dispatch (TestTeamKey(teamId, descriptor.Id)))
                            ]
                        ]
                    | None -> Html.none
                ]
            ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
    let inputPanel =
        match model.Banner with
        | Some banner ->
            Layout.Panel.panel "AI keys" [
                bannerView banner dispatch
                Html.p [
                    prop.className "text-sm text-gray-600"
                    prop.text "Manage AI provider API keys for this deployment. Use the panels on the right."
                ]
            ]
        | None ->
            Layout.Panel.panel "AI keys" [
                Html.p [
                    prop.className "text-sm text-gray-600"
                    prop.text
                        "Manage AI provider API keys for this deployment. The current key value is never displayed — once stored, the only way to retrieve it is from the source (Anthropic / OpenAI / Google console)."
                ]
            ]

    let outputPanel =
        Html.div [ prop.children [ platformSection model dispatch; teamSection model dispatch ] ]

    inputPanel, outputPanel

// ─── Module registration ────────────────────────────────────────

/// Create the Platform Admin AI keys module as an ErasedModule.
/// Auto-appended by `AIClientConfig.appendAssistantModule` alongside
/// the assistant + settings modules; the sidebar role gate hides it
/// from non-admin callers.
let create () : ErasedModule =
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = "AI Keys"
        Icon = ToolUp.AI.Icons.aiSettings
    }
    |> ToolUp.Platform.ClientModule.withId "_ai.PlatformAIKeys"
    |> ToolUp.Platform.ClientModule.withView view
    |> ToolUp.Platform.ClientModule.withGroup "Platform Admin"
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
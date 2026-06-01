// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.AISettingsUI

open System
open Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform
open ToolUp.AI

// ─── Model ────────────────────────────────────────────────────────

type Status =
    /// Green banner shown briefly after a successful mutation.
    | SuccessMsg of string
    /// Red banner shown until dismissed or superseded.
    | ErrorMsg of string

type Model = {
    /// Providers the deployment allows users to configure. Loaded from
    /// `AISettingsApi.ListAvailable`. Empty list means the deployment
    /// is `PlatformOnly` (managed-by-deployment mode) — under
    /// PlatformOnly the UI hides the BYOK instance form and instead
    /// renders a minimal model-picker driven by `PlatformDescriptor`.
    Available: AIProviderDescriptor list
    /// Descriptor of the deployment's platform-configured provider.
    /// `Some` under PlatformOnly / PermissiveWithPlatformFallback when
    /// the factory has a platform provider wired. Drives the
    /// PlatformOnly model-picker dropdown.
    PlatformDescriptor: AIProviderDescriptor option
    /// Current scope's configured providers. Loaded from `GetMyConfig`.
    Config: AIUserConfigView
    /// When Some, the form is in edit mode targeting this instance.
    /// When None, the form is in add mode. Label is fixed during edit
    /// (the label is the primary key across config + secret storage;
    /// renaming would orphan the secret). Provider, model, and key
    /// are all mutable during edit.
    Editing: AIProviderInstanceView option
    /// Transient status banner (success / error).
    Status: Status option
    /// True while an async op is in flight — disables buttons.
    Loading: bool
}

type Msg =
    | LoadAvailable of ApiCall<unit, AIProviderDescriptor list>
    | LoadPlatformDescriptor of ApiCall<unit, AIProviderDescriptor option>
    | LoadConfig of ApiCall<unit, AIUserConfigView>
    | StartEdit of label: string
    | CancelEdit
    | SaveInstance of AIProviderInstanceSaveRequest
    | SaveFinished of Result<unit, string>
    | DeleteInstance of label: string
    | DeleteFinished of Result<unit, string>
    | SetActive of label: string option
    | SetActiveFinished of Result<unit, string>
    | TestInstance of label: string
    | TestFinished of label: string * Result<unit, string>
    | SavePlatformModelOverride of string option
    | SavePlatformModelOverrideFinished of Result<unit, string>
    | ApiError of string
    | DismissStatus

let private api =
    Api.makeProxy<AISettingsApi> (customOptions = UserSession.withRequestHeaders)

// ─── Init ─────────────────────────────────────────────────────────

let init () : Model * Cmd<Msg> =
    {
        Available = []
        PlatformDescriptor = None
        Config = AIUserConfigView.empty
        Editing = None
        Status = None
        Loading = true
    },
    Cmd.batch [
        Cmd.OfRemoting.call api.ListAvailable () (Finished >> LoadAvailable) (fun ex -> ApiError ex.Message)
        Cmd.OfRemoting.call api.GetPlatformDescriptor () (Finished >> LoadPlatformDescriptor) (fun ex ->
            ApiError ex.Message)
        Cmd.OfRemoting.call api.GetMyConfig () (Finished >> LoadConfig) (fun ex -> ApiError ex.Message)
    ]

// ─── Update ───────────────────────────────────────────────────────

/// Re-fetch the current config after a successful mutation. Cheaper
/// than optimistically updating the client model and avoids drift if
/// the server makes its own adjustments (e.g. clearing active when
/// the active instance is deleted).
let private refetchConfig () =
    Cmd.OfRemoting.call api.GetMyConfig () (Finished >> LoadConfig) (fun ex -> ApiError ex.Message)

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | LoadAvailable(Start()) ->
        { model with Loading = true },
        Cmd.OfRemoting.call api.ListAvailable () (Finished >> LoadAvailable) (fun ex -> ApiError ex.Message)

    | LoadAvailable(Finished descriptors) ->
        {
            model with
                Available = descriptors
                Loading = false
        },
        Cmd.none

    | LoadPlatformDescriptor(Start()) ->
        { model with Loading = true },
        Cmd.OfRemoting.call api.GetPlatformDescriptor () (Finished >> LoadPlatformDescriptor) (fun ex ->
            ApiError ex.Message)

    | LoadPlatformDescriptor(Finished descriptor) ->
        {
            model with
                PlatformDescriptor = descriptor
                Loading = false
        },
        Cmd.none

    | LoadConfig(Start()) -> { model with Loading = true }, refetchConfig ()

    | LoadConfig(Finished cfg) ->
        {
            model with
                Config = cfg
                Loading = false
        },
        Cmd.none

    | StartEdit label ->
        // Look up the current view for this label — the form will
        // prefill from it. Stale Editing (instance deleted out from
        // under us) is filtered by the None case; the form falls
        // back to add mode.
        let target =
            model.Config.ConfiguredProviders |> List.tryFind (fun i -> i.Label = label)

        { model with Editing = target }, Cmd.none

    | CancelEdit -> { model with Editing = None }, Cmd.none

    | SaveInstance req ->
        { model with Loading = true },
        Cmd.OfRemoting.call api.SaveInstance req SaveFinished (fun ex -> ApiError ex.Message)

    | SaveFinished(Ok()) ->
        // Clear Editing so the form returns to add mode after a
        // successful edit-save. Config is re-fetched to pick up the
        // server's updates (UpdatedAt, etc.).
        {
            model with
                Editing = None
                Status = Some(SuccessMsg "Provider saved.")
                Loading = false
        },
        refetchConfig ()

    | SaveFinished(Error err) ->
        {
            model with
                Status = Some(ErrorMsg err)
                Loading = false
        },
        Cmd.none

    | DeleteInstance label ->
        { model with Loading = true },
        Cmd.OfRemoting.call api.DeleteInstance label DeleteFinished (fun ex -> ApiError ex.Message)

    | DeleteFinished(Ok()) ->
        {
            model with
                Status = Some(SuccessMsg "Provider removed.")
                Loading = false
        },
        refetchConfig ()

    | DeleteFinished(Error err) ->
        {
            model with
                Status = Some(ErrorMsg err)
                Loading = false
        },
        Cmd.none

    | SetActive labelOpt ->
        { model with Loading = true },
        Cmd.OfRemoting.call api.SetActive labelOpt SetActiveFinished (fun ex -> ApiError ex.Message)

    | SetActiveFinished(Ok()) -> { model with Loading = false }, refetchConfig ()

    | SetActiveFinished(Error err) ->
        {
            model with
                Status = Some(ErrorMsg err)
                Loading = false
        },
        Cmd.none

    | TestInstance label ->
        { model with Loading = true },
        Cmd.OfRemoting.call api.TestConnection label (fun r -> TestFinished(label, r)) (fun ex -> ApiError ex.Message)

    | TestFinished(label, Ok()) ->
        {
            model with
                Status = Some(SuccessMsg $"'{label}' connection verified — the API key is accepted.")
                Loading = false
        },
        Cmd.none

    | TestFinished(label, Error err) ->
        {
            model with
                Status = Some(ErrorMsg $"'{label}': {err}")
                Loading = false
        },
        Cmd.none

    | SavePlatformModelOverride modelOpt ->
        { model with Loading = true },
        Cmd.OfRemoting.call api.SetPlatformModelOverride modelOpt SavePlatformModelOverrideFinished (fun ex ->
            ApiError ex.Message)

    | SavePlatformModelOverrideFinished(Ok()) ->
        {
            model with
                Status = Some(SuccessMsg "AI model preference saved.")
                Loading = false
        },
        refetchConfig ()

    | SavePlatformModelOverrideFinished(Error err) ->
        {
            model with
                Status = Some(ErrorMsg err)
                Loading = false
        },
        Cmd.none

    | ApiError msg ->
        {
            model with
                Status = Some(ErrorMsg msg)
                Loading = false
        },
        Cmd.none

    | DismissStatus -> { model with Status = None }, Cmd.none

// ─── Provider form (add + edit modes) ───────────────────────────
//
// Text inputs follow the CLAUDE.md rule: local React state, not
// Elmish model. Dispatch to Elmish happens on submit only.
//
// The form is keyed on the edit target (label or "add") so React
// re-mounts it when the user switches between add and edit or
// between edit targets. That means useState's initial values are
// evaluated fresh each time — no need for a useEffect sync.

[<ReactComponent>]
let private ProviderForm
    (editing: AIProviderInstanceView option)
    (available: AIProviderDescriptor list)
    (existingLabels: Set<string>)
    (loading: bool)
    (onSubmit: AIProviderInstanceSaveRequest -> unit)
    (onCancel: unit -> unit)
    =
    let firstId = available |> List.tryHead |> Option.map _.Id |> Option.defaultValue ""

    // Initial values depend on mode. In edit mode, label/provider/
    // model prefill from the instance; API key stays empty (Some =
    // replace, None = keep existing).
    let initialLabel = editing |> Option.map _.Label |> Option.defaultValue ""

    let initialProvider =
        editing |> Option.map _.ProviderId |> Option.defaultValue firstId

    let initialModel = editing |> Option.bind _.Model |> Option.defaultValue ""

    let label, setLabel = React.useState initialLabel
    let providerId, setProviderId = React.useState initialProvider
    let model, setModel = React.useState initialModel
    let apiKey, setApiKey = React.useState ""

    let isEditMode = editing.IsSome

    let selectedDescriptor = available |> List.tryFind (fun d -> d.Id = providerId)

    // Label clash check only matters in add mode. In edit mode the
    // label is fixed (the form disables the input) so it cannot clash.
    let labelClash = not isEditMode && existingLabels.Contains label

    // In edit mode the API key is optional — empty means "keep the
    // existing stored key". In add mode it's required.
    let apiKeyRequired = not isEditMode
    let apiKeyProvided = apiKey.Trim() <> ""

    let canSubmit =
        not loading
        && label.Trim() <> ""
        && providerId <> ""
        && (not apiKeyRequired || apiKeyProvided)
        && not labelClash

    let handleSubmit () =
        if canSubmit then
            let modelTrimmed = model.Trim()

            let modelOpt = if modelTrimmed = "" then None else Some modelTrimmed

            let apiKeyOpt = if apiKeyProvided then Some(apiKey.Trim()) else None

            onSubmit {
                Label = label.Trim()
                ProviderId = providerId
                Model = modelOpt
                ApiKey = apiKeyOpt
            }

            // Only clear inputs in add mode. Edit mode's inputs reset
            // via remount (handled at the view level via prop.key).
            if not isEditMode then
                setLabel ""
                setModel ""
                setApiKey ""

    Html.div [
        prop.className "space-y-3"
        prop.children [
            // Label
            Html.div [
                prop.children [
                    Html.label [
                        prop.className "block text-sm font-medium text-gray-700 mb-1"
                        prop.text "Label"
                    ]
                    Html.input [
                        prop.className [
                            "w-full border rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-violet-400"
                            if isEditMode then
                                "border-gray-200 bg-gray-50 text-gray-600"
                            else
                                "border-gray-300"
                        ]
                        prop.placeholder "e.g. Personal Claude, Work GPT-4"
                        prop.value label
                        prop.onChange setLabel
                        prop.disabled (loading || isEditMode)
                    ]
                    if labelClash then
                        Html.p [
                            prop.className "mt-1 text-xs text-red-600"
                            prop.text "A provider with this label already exists — pick a different name."
                        ]
                    elif isEditMode then
                        Html.p [
                            prop.className "mt-1 text-xs text-gray-500"
                            prop.text "Label is fixed during edit. Delete and re-add to rename."
                        ]
                ]
            ]

            // Provider dropdown
            Html.div [
                prop.children [
                    Html.label [
                        prop.className "block text-sm font-medium text-gray-700 mb-1"
                        prop.text "Provider"
                    ]
                    Html.select [
                        prop.className
                            "w-full border border-gray-300 rounded-lg px-3 py-2 text-sm bg-white focus:outline-none focus:ring-2 focus:ring-violet-400"
                        prop.value providerId
                        prop.onChange setProviderId
                        prop.disabled loading
                        prop.children [
                            for d in available do
                                Html.option [ prop.value d.Id; prop.text d.DisplayName ]
                        ]
                    ]
                ]
            ]

            // Model (optional, with datalist of known values + freetext)
            Html.div [
                prop.children [
                    Html.label [
                        prop.className "block text-sm font-medium text-gray-700 mb-1"
                        prop.text "Model (optional)"
                    ]
                    Html.input [
                        prop.className
                            "w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-violet-400"
                        prop.placeholder (
                            selectedDescriptor
                            |> Option.map (fun d -> $"Default: {d.DefaultModel}")
                            |> Option.defaultValue "Leave empty for default"
                        )
                        prop.value model
                        prop.onChange setModel
                        prop.disabled loading
                        prop.list "model-suggestions"
                    ]
                    match selectedDescriptor with
                    | Some d when not d.SupportedModels.IsEmpty ->
                        Html.datalist [
                            prop.id "model-suggestions"
                            prop.children [
                                for m in d.SupportedModels do
                                    Html.option [ prop.value m ]
                            ]
                        ]
                    | _ -> Html.none
                ]
            ]

            // API key
            Html.div [
                prop.children [
                    Html.label [
                        prop.className "block text-sm font-medium text-gray-700 mb-1"
                        prop.text (if isEditMode then "API Key (optional)" else "API Key")
                    ]
                    Html.input [
                        prop.type'.password
                        prop.className
                            "w-full border border-gray-300 rounded-lg px-3 py-2 text-sm font-mono focus:outline-none focus:ring-2 focus:ring-violet-400"
                        prop.placeholder (
                            if isEditMode then
                                "Leave empty to keep the existing key"
                            else
                                "Paste your API key"
                        )
                        prop.value apiKey
                        prop.onChange setApiKey
                        prop.disabled loading
                    ]
                    Html.p [
                        prop.className "mt-1 text-xs text-gray-500"
                        prop.text (
                            if isEditMode then
                                "Only provided when rotating. The existing key stays in place otherwise."
                            else
                                "The key is written to the server's encrypted secret store and is never returned to the browser."
                        )
                    ]
                ]
            ]

            // Submit / Cancel
            Html.div [
                prop.className "pt-2 flex items-center gap-3"
                prop.children [
                    Html.button [
                        prop.className [
                            "px-4 py-2 rounded-lg text-sm font-medium transition-colors"
                            if canSubmit then
                                "bg-violet-500 hover:bg-violet-600 text-white"
                            else
                                "bg-gray-200 text-gray-400 cursor-not-allowed"
                        ]
                        prop.disabled (not canSubmit)
                        prop.onClick (fun _ -> handleSubmit ())
                        prop.text (
                            match loading, isEditMode with
                            | true, _ -> "Saving..."
                            | false, true -> "Save changes"
                            | false, false -> "Add provider"
                        )
                    ]

                    if isEditMode then
                        Html.button [
                            prop.className "px-4 py-2 rounded-lg text-sm font-medium text-gray-600 hover:bg-gray-100"
                            prop.disabled loading
                            prop.onClick (fun _ -> onCancel ())
                            prop.text "Cancel"
                        ]
                ]
            ]
        ]
    ]

// ─── Platform model picker (PlatformOnly branch) ────────────────
//
// Minimal form: just a model dropdown and a Save button. Provider
// identity and API-key source stay locked — only the model choice
// is user-editable.
// Text-input pattern follows the CLAUDE.md rule: local React state,
// dispatch only on explicit Save.

[<ReactComponent>]
let private PlatformModelPicker
    (descriptor: AIProviderDescriptor)
    (currentOverride: string option)
    (loading: bool)
    (onSave: string option -> unit)
    =
    // Empty string sentinel = "use default". Stored override maps
    // to itself; None maps to empty.
    let initial = currentOverride |> Option.defaultValue ""

    let selected, setSelected = React.useState initial

    // React.useState initial is captured at mount — if `currentOverride`
    // changes after a refetch (e.g. a successful save), sync via
    // useEffect so the dropdown reflects the fresh server state.
    React.useEffect ((fun () -> setSelected initial), [| box initial |])

    let effectiveModel = if selected = "" then descriptor.DefaultModel else selected

    let pending = selected <> initial

    let handleSave () =
        if not loading && pending then
            let toSave = if selected = "" then None else Some selected
            onSave toSave

    Html.div [
        prop.className "space-y-3"
        prop.children [
            Html.p [
                prop.className "text-sm text-gray-600"
                prop.text
                    $"AI is managed by this deployment using {descriptor.DisplayName}. Provider identity and API key are locked, but you can pick which model to use from the supported list below."
            ]

            Html.div [
                prop.children [
                    Html.label [
                        prop.className "block text-sm font-medium text-gray-700 mb-1"
                        prop.text "Model"
                    ]
                    Html.select [
                        prop.className
                            "w-full border border-gray-300 rounded-lg px-3 py-2 text-sm bg-white focus:outline-none focus:ring-2 focus:ring-violet-400"
                        prop.value selected
                        prop.onChange (fun (v: string) -> setSelected v)
                        prop.disabled loading
                        prop.children [
                            Html.option [ prop.value ""; prop.text $"Default ({descriptor.DefaultModel})" ]
                            for m in descriptor.SupportedModels do
                                if m <> descriptor.DefaultModel then
                                    Html.option [ prop.value m; prop.text m ]
                        ]
                    ]
                    Html.p [
                        prop.className "mt-1 text-xs text-gray-500"
                        prop.text $"Effective model: {effectiveModel}"
                    ]
                ]
            ]

            Html.div [
                prop.className "pt-2 flex items-center gap-3"
                prop.children [
                    Html.button [
                        prop.className [
                            "px-4 py-2 rounded-lg text-sm font-medium transition-colors"
                            if not loading && pending then
                                "bg-violet-500 hover:bg-violet-600 text-white"
                            else
                                "bg-gray-200 text-gray-400 cursor-not-allowed"
                        ]
                        prop.disabled (loading || not pending)
                        prop.onClick (fun _ -> handleSave ())
                        prop.text (if loading then "Saving..." else "Save model preference")
                    ]
                ]
            ]
        ]
    ]

// ─── View ─────────────────────────────────────────────────────────

let private statusBanner (status: Status) (dispatch: Msg -> unit) =
    let className, text =
        match status with
        | SuccessMsg msg -> "bg-green-50 border-green-200 text-green-700", msg
        | ErrorMsg msg -> "bg-red-50 border-red-200 text-red-700", msg

    Html.div [
        prop.className $"p-3 border rounded text-sm cursor-pointer mb-3 {className}"
        prop.onClick (fun _ -> dispatch DismissStatus)
        prop.text text
    ]

let private instanceRow
    (instance: AIProviderInstanceView)
    (activeLabel: string option)
    (editingLabel: string option)
    (loading: bool)
    (dispatch: Msg -> unit)
    =
    let isActive = activeLabel = Some instance.Label
    let isEditing = editingLabel = Some instance.Label

    let providerName = instance.ProviderId
    let modelText = instance.Model |> Option.defaultValue "(default)"

    Html.div [
        prop.className [
            "border rounded-lg p-3 mb-2 flex items-center gap-3"
            if isEditing then
                "border-violet-400 bg-violet-50 ring-2 ring-violet-200"
            elif isActive then
                "border-violet-300 bg-violet-50"
            else
                "border-gray-200 bg-white"
        ]
        prop.children [
            // Active radio
            Html.input [
                prop.type'.radio
                prop.className "w-4 h-4 accent-violet-500"
                prop.isChecked isActive
                prop.disabled loading
                prop.onChange (fun (_: bool) ->
                    if not isActive then
                        dispatch (SetActive(Some instance.Label)))
            ]

            // Labels
            Html.div [
                prop.className "flex-1 min-w-0"
                prop.children [
                    Html.div [
                        prop.className "flex items-baseline gap-2"
                        prop.children [
                            Html.span [ prop.className "font-medium text-sm"; prop.text instance.Label ]
                            Html.span [
                                prop.className "text-xs text-gray-500 font-mono truncate"
                                prop.text $"{providerName} · {modelText}"
                            ]
                        ]
                    ]
                    Html.div [
                        prop.className "flex items-center gap-2 mt-1"
                        prop.children [
                            if instance.HasApiKey then
                                Html.span [
                                    prop.className
                                        "inline-block px-2 py-0.5 rounded text-xs bg-green-100 text-green-700"
                                    prop.text "\u2713 Key stored"
                                ]
                            else
                                Html.span [
                                    prop.className "inline-block px-2 py-0.5 rounded text-xs bg-red-100 text-red-700"
                                    prop.text "Missing key"
                                ]
                            if isEditing then
                                Html.span [
                                    prop.className
                                        "inline-block px-2 py-0.5 rounded text-xs bg-violet-100 text-violet-700"
                                    prop.text "Editing"
                                ]
                        ]
                    ]
                ]
            ]

            // Edit
            Html.button [
                prop.className
                    "text-gray-500 hover:text-violet-600 text-sm px-2 py-1 rounded disabled:opacity-50 disabled:cursor-not-allowed"
                prop.disabled (loading || isEditing)
                prop.onClick (fun _ -> dispatch (StartEdit instance.Label))
                prop.text "Edit"
            ]

            // Test — only enabled when an API key is stored.
            Html.button [
                prop.className
                    "text-gray-500 hover:text-violet-600 text-sm px-2 py-1 rounded disabled:opacity-50 disabled:cursor-not-allowed"
                prop.disabled (loading || not instance.HasApiKey)
                prop.onClick (fun _ -> dispatch (TestInstance instance.Label))
                prop.text "Test"
            ]

            // Delete
            Html.button [
                prop.className
                    "text-gray-400 hover:text-red-600 text-sm p-1 disabled:opacity-50 disabled:cursor-not-allowed"
                prop.disabled loading
                prop.onClick (fun _ -> dispatch (DeleteInstance instance.Label))
                prop.text "Remove"
            ]
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
    let existingLabels =
        model.Config.ConfiguredProviders |> List.map _.Label |> Set.ofList

    let inputPanel =
        Layout.Panel.panel "Configured providers" [
            match model.Status with
            | Some s -> statusBanner s dispatch
            | None -> ()

            if model.Config.ConfiguredProviders.IsEmpty then
                Html.p [
                    prop.className "text-sm text-gray-500 py-4"
                    prop.text
                        "No providers configured yet. Add one using the form on the right — the factory will fall back to the platform default (if any) until you activate one."
                ]
            else
                Html.div [
                    prop.children [
                        for instance in model.Config.ConfiguredProviders do
                            let editingLabel = model.Editing |> Option.map _.Label

                            instanceRow instance model.Config.ActiveProviderLabel editingLabel model.Loading dispatch

                        Html.div [
                            prop.className "mt-3 pt-3 border-t border-gray-200"
                            prop.children [
                                Html.label [
                                    prop.className "flex items-center gap-2 text-sm text-gray-600 cursor-pointer"
                                    prop.children [
                                        Html.input [
                                            prop.type'.radio
                                            prop.className "w-4 h-4 accent-gray-400"
                                            prop.isChecked model.Config.ActiveProviderLabel.IsNone
                                            prop.disabled model.Loading
                                            prop.onChange (fun (_: bool) ->
                                                if model.Config.ActiveProviderLabel.IsSome then
                                                    dispatch (SetActive None))
                                        ]
                                        Html.span [ prop.text "None active (fall back to platform default)" ]
                                    ]
                                ]
                            ]
                        ]
                    ]
                ]
        ]

    let panelTitle =
        match model.Editing with
        | Some e -> $"Edit: {e.Label}"
        | None -> "Add provider"

    // Keying the form on the edit target forces React to remount it
    // when the user switches between add and edit (or between edit
    // targets). useState's initial values are re-evaluated on mount,
    // so prefilled values sync without a useEffect dance.
    let formKey =
        match model.Editing with
        | Some e -> $"edit-{e.Label}"
        | None -> "add"

    let outputPanel =
        match model.Available.IsEmpty, model.PlatformDescriptor with
        | true, Some descriptor ->
            Layout.Panel.panel "AI model" [
                PlatformModelPicker
                    descriptor
                    model.Config.PlatformModelOverride
                    model.Loading
                    (SavePlatformModelOverride >> dispatch)
            ]
        | true, None ->
            Layout.Panel.panel "AI settings" [
                Html.div [
                    prop.className "p-4 bg-amber-50 border border-amber-200 rounded text-sm text-amber-800"
                    prop.text
                        "AI is not configured for this deployment. Contact your administrator to enable an AI provider."
                ]
            ]
        | false, _ ->
            Layout.Panel.panel panelTitle [
                Html.div [
                    prop.key formKey
                    prop.children [
                        ProviderForm
                            model.Editing
                            model.Available
                            existingLabels
                            model.Loading
                            (SaveInstance >> dispatch)
                            (fun () -> dispatch CancelEdit)
                    ]
                ]
            ]

    inputPanel, outputPanel

// ─── Module registration ──────────────────────────────────────────

/// Create the AI settings module as an ErasedModule. Mirrors the
/// `AIAssistantUI.create` pattern; appended alongside the assistant
/// module by `AIClientConfig.appendAssistantModule` when AI is
/// enabled.
let create () : ErasedModule =
    // Companion-provided built-in — `Id` is reserved under the `_ai.`
    // namespace so it can never collide with an app's RBAC-managed
    // `ServerConfig.ModuleNames` key.
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = "AI Settings"
        Icon = ToolUp.AI.Icons.aiSettings
    }
    |> ToolUp.Platform.ClientModule.withId "_ai.AISettings"
    |> ToolUp.Platform.ClientModule.withView view
    |> ToolUp.Platform.ClientModule.withGroup "Admin"
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
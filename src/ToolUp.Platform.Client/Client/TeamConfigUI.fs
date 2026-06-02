// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module TeamConfigUI

open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Model ───────────────────────────────────────────────────────────

/// Status line rendered under the form after a save / clear attempt.
/// Cleared automatically on the next select-module or server round-trip.
type FormStatus =
    | Idle
    | Saving
    | Saved
    | Failed of string

/// Which top-level section of the admin UI is on screen. The two sections
/// share the same page layout but load independently — switching tabs
/// does not re-fetch the other section.
type Tab =
    | ConfigTab
    | FlagsTab

type Model = {
    /// Currently rendered section. Configuration is the default so
    /// existing muscle memory is preserved.
    ActiveTab: Tab
    /// List of modules the server is willing to surface — `_platform`
    /// plus every `ServerConfig.ModuleConfigs` entry. Empty until the
    /// initial fetch lands.
    Modules: ModuleConfigEntry list
    /// Persisted values per moduleKey. Loaded lazily on first selection
    /// to keep shell start cheap; the main shell's config preload
    /// already covers the active-module path.
    Values: Map<string, Map<string, string>>
    /// The module whose form is currently on screen. `None` until a
    /// selection is made or `Modules` lands (we auto-pick the first
    /// entry so admins don't stare at an empty page).
    Selected: string option
    /// Banner shown at the top of the page for list-level failures
    /// (e.g. ListModules rejected in Anonymous mode).
    Error: string option
    /// Per-module status indicator; cleared by `DismissStatus` or on
    /// the next successful reload.
    Status: Map<string, FormStatus>
    /// True once `ListModules` has completed, regardless of payload.
    /// Distinguishes "still loading" from "deployment declared no
    /// configurable modules".
    Loaded: bool
    /// Flags declared at `register()` / `ServerConfig.FeatureFlags`.
    /// Empty until `LoadFeatureFlags` completes.
    DeclaredFlags: FeatureFlag list
    /// Overrides at the caller's admin scope (Team in Team mode, User
    /// otherwise). Keys not in this map have no override at the admin
    /// scope — reads fall through.
    FlagOverrides: Map<string, FlagValue>
    /// Per-flag save/clear status. Keyed by flag key.
    FlagStatus: Map<string, FormStatus>
    /// List-level error (e.g. Anonymous mode rejecting GetOverrides).
    FlagsError: string option
    /// True once the initial flags fetch has completed, regardless of
    /// payload. Distinguishes "loading" from "deployment declared no
    /// flags".
    FlagsLoaded: bool
}

type Msg =
    | SwitchTab of Tab
    | LoadModules
    | ModulesLoaded of ModuleConfigEntry list
    | ListFailed of string
    | SelectModule of moduleKey: string
    | ModuleValuesLoaded of moduleKey: string * Result<Map<string, string>, string>
    | SubmitSave of moduleKey: string * values: Map<string, string>
    | SaveCompleted of moduleKey: string * Result<unit, string>
    | SubmitClear of moduleKey: string
    | ClearCompleted of moduleKey: string * Result<unit, string>
    | DismissError
    | DismissStatus of moduleKey: string
    | FlagsBootLoaded of Result<FeatureFlag list * Map<string, FlagValue>, string>
    | SubmitSetFlag of key: string * value: FlagValue
    | SetFlagCompleted of key: string * Result<unit, string>
    | SubmitClearFlag of key: string
    | ClearFlagCompleted of key: string * Result<unit, string>
    | DismissFlagsError
    | DismissFlagStatus of key: string

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see UserSession.fs:342 + SDK.Client.fs installRequestGuard.
let private configApi: IConfigApi =
    Api.makeProxy<IConfigApi> (customOptions = UserSession.withRequestHeaders)

let private featureFlagApi: IFeatureFlagApi =
    Api.makeProxy<IFeatureFlagApi> (customOptions = UserSession.withRequestHeaders)

// ─── Init ────────────────────────────────────────────────────────────

/// Combined initial fetch for the Flags tab — declared flags and
/// current overrides in one round-trip so the page renders without a
/// loading flicker once either tab is opened. `GetOverrides` carries
/// its own `Result`; `ListDeclared` is infallible, so the outer async
/// only surfaces `Error` when `GetOverrides` rejects.
let private loadFlagsBoot () = async {
    let! declared = featureFlagApi.ListDeclared()
    let! overrides = featureFlagApi.GetOverrides()
    return overrides |> Result.map (fun m -> declared, m)
}

let private loadFlagsCmd () =
    Cmd.OfRemoting.call loadFlagsBoot () FlagsBootLoaded (fun e -> FlagsBootLoaded(Error e.Message))

let init () =
    let model = {
        ActiveTab = ConfigTab
        Modules = []
        Values = Map.empty
        Selected = None
        Error = None
        Status = Map.empty
        Loaded = false
        DeclaredFlags = []
        FlagOverrides = Map.empty
        FlagStatus = Map.empty
        FlagsError = None
        FlagsLoaded = false
    }

    let loadModules =
        Cmd.OfRemoting.call configApi.ListModules () ModulesLoaded (fun e -> ListFailed e.Message)

    let loadFlags = loadFlagsCmd ()

    model, Cmd.batch [ loadModules; loadFlags ]

// ─── Helpers ─────────────────────────────────────────────────────────

/// Fetch the persisted values for a module. Errors fall through to
/// `ModuleValuesLoaded (Error _)` so the page can render a banner
/// under the selected form rather than swallowing the failure.
let private loadValues (moduleKey: string) =
    Cmd.OfRemoting.call
        configApi.GetModuleConfig
        moduleKey
        (fun result ->
            match result with
            | Ok view -> ModuleValuesLoaded(moduleKey, Ok view.Values)
            | Error e -> ModuleValuesLoaded(moduleKey, Error e))
        (fun e -> ModuleValuesLoaded(moduleKey, Error e.Message))

// ─── Update ──────────────────────────────────────────────────────────

let update (msg: Msg) (model: Model) =
    match msg with
    | LoadModules -> model, Cmd.OfRemoting.call configApi.ListModules () ModulesLoaded (fun e -> ListFailed e.Message)

    | ModulesLoaded modules ->
        // Auto-select the first entry so the admin lands directly on a
        // form rather than having to pick from a dropdown with one
        // option. `_platform` comes first in the server-registered
        // order.
        let selected = modules |> List.tryHead |> Option.map _.ModuleKey

        let newModel = {
            model with
                Modules = modules
                Selected = selected
                Loaded = true
        }

        let cmd =
            match selected with
            | Some key -> loadValues key
            | None -> Cmd.none

        newModel, cmd

    | ListFailed err ->
        {
            model with
                Error = Some err
                Loaded = true
        },
        Cmd.none

    | SelectModule key ->
        // Re-fetch every time the user switches modules — cheap, and
        // avoids stale-value surprises if another admin saved between
        // the initial load and now.
        { model with Selected = Some key }, loadValues key

    | ModuleValuesLoaded(key, Ok values) ->
        {
            model with
                Values = model.Values |> Map.add key values
                Status = model.Status |> Map.add key Idle
        },
        Cmd.none

    | ModuleValuesLoaded(key, Error err) ->
        {
            model with
                Status = model.Status |> Map.add key (Failed err)
        },
        Cmd.none

    | SubmitSave(key, values) ->
        let cmd =
            Cmd.OfRemoting.call
                configApi.SaveModuleConfig
                (key, values)
                (fun result -> SaveCompleted(key, result))
                (fun e -> SaveCompleted(key, Error e.Message))

        {
            model with
                Status = model.Status |> Map.add key Saving
        },
        cmd

    | SaveCompleted(key, Ok()) ->
        {
            model with
                Status = model.Status |> Map.add key Saved
        },
        loadValues key

    | SaveCompleted(key, Error err) ->
        {
            model with
                Status = model.Status |> Map.add key (Failed err)
        },
        Cmd.none

    | SubmitClear key ->
        let cmd =
            Cmd.OfRemoting.call configApi.ClearModuleConfig key (fun result -> ClearCompleted(key, result)) (fun e ->
                ClearCompleted(key, Error e.Message))

        {
            model with
                Status = model.Status |> Map.add key Saving
        },
        cmd

    | ClearCompleted(key, Ok()) ->
        {
            model with
                Status = model.Status |> Map.add key Saved
        },
        loadValues key

    | ClearCompleted(key, Error err) ->
        {
            model with
                Status = model.Status |> Map.add key (Failed err)
        },
        Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

    | DismissStatus key ->
        {
            model with
                Status = model.Status |> Map.remove key
        },
        Cmd.none

    | SwitchTab tab -> { model with ActiveTab = tab }, Cmd.none

    | FlagsBootLoaded(Ok(declared, overrides)) ->
        {
            model with
                DeclaredFlags = declared
                FlagOverrides = overrides
                FlagsLoaded = true
                FlagsError = None
        },
        Cmd.none

    | FlagsBootLoaded(Error err) ->
        {
            model with
                FlagsLoaded = true
                FlagsError = Some err
        },
        Cmd.none

    | SubmitSetFlag(key, value) ->
        let cmd =
            Cmd.OfRemoting.call
                featureFlagApi.SetOverride
                (key, value)
                (fun result -> SetFlagCompleted(key, result))
                (fun e -> SetFlagCompleted(key, Error e.Message))

        {
            model with
                FlagStatus = model.FlagStatus |> Map.add key Saving
        },
        cmd

    | SetFlagCompleted(key, Ok()) ->
        // Optimistically reload overrides so the UI shows the persisted
        // value rather than trusting the submitted draft — mirrors the
        // ConfigTab pattern where a successful save refetches values.
        let reload = loadFlagsCmd ()

        {
            model with
                FlagStatus = model.FlagStatus |> Map.add key Saved
        },
        reload

    | SetFlagCompleted(key, Error err) ->
        {
            model with
                FlagStatus = model.FlagStatus |> Map.add key (Failed err)
        },
        Cmd.none

    | SubmitClearFlag key ->
        let cmd =
            Cmd.OfRemoting.call
                featureFlagApi.ClearOverride
                key
                (fun result -> ClearFlagCompleted(key, result))
                (fun e -> ClearFlagCompleted(key, Error e.Message))

        {
            model with
                FlagStatus = model.FlagStatus |> Map.add key Saving
        },
        cmd

    | ClearFlagCompleted(key, Ok()) ->
        let reload = loadFlagsCmd ()

        {
            model with
                FlagStatus = model.FlagStatus |> Map.add key Saved
        },
        reload

    | ClearFlagCompleted(key, Error err) ->
        {
            model with
                FlagStatus = model.FlagStatus |> Map.add key (Failed err)
        },
        Cmd.none

    | DismissFlagsError -> { model with FlagsError = None }, Cmd.none

    | DismissFlagStatus key ->
        {
            model with
                FlagStatus = model.FlagStatus |> Map.remove key
        },
        Cmd.none

// ─── JSON helpers ────────────────────────────────────────────────────

/// Per-field round-trip between the persisted JSON string and the
/// display string shown in the form. The persisted layer is JSON so
/// `Set<'T>` / `GetEffective<'T>` can decode typed values; the form
/// keeps everything as text to avoid committing to a per-kind input
/// type when the differences are mostly cosmetic (number vs text).
/// Decode a JSON literal into the plain display string. Strings and
/// Choices are stored as JSON string literals (`"hello"`) so we strip
/// the surrounding quotes; numbers and booleans are their own JSON
/// representation and are returned unchanged.
let private displayFromJson (kind: ConfigFieldKind) (json: string) : string =
    match kind with
    | ConfigFieldKind.String _
    | ConfigFieldKind.Choice _ ->
        try
            let parsed = Fable.Core.JS.JSON.parse json
            string parsed
        with _ ->
            json
    | _ -> json

/// Encode the display string back into a JSON literal suitable for
/// persistence. Numbers and booleans are already valid JSON literals
/// (`true`, `42`, `3.14`); strings and Choices need wrapping.
let private jsonFromDisplay (kind: ConfigFieldKind) (display: string) : string =
    match kind with
    | ConfigFieldKind.Bool -> if display = "true" then "true" else "false"
    | ConfigFieldKind.Int _
    | ConfigFieldKind.Float _ ->
        // Pass through — server-side validator rejects anything that
        // isn't a valid JSON number.
        display.Trim()
    | ConfigFieldKind.String _
    | ConfigFieldKind.Choice _ -> Fable.Core.JS.JSON.stringify display

// ─── Form component ──────────────────────────────────────────────────

/// Per-module form. Holds its own React state for every field so text
/// inputs don't round-trip through Elmish on every keystroke — matches
/// the pattern documented in CLAUDE.md (`UIToolkit.Forms.Input`).
/// `onSave` and `onClear` fire only on explicit button clicks.
/// PascalCase name follows React component conventions so react-refresh
/// can pick up edits during dev without a full reload.
[<ReactComponent>]
let private ModuleForm
    (entry: ModuleConfigEntry)
    (persisted: Map<string, string>)
    (status: FormStatus)
    (onSave: Map<string, string> -> unit)
    (onClear: unit -> unit)
    (onDismissStatus: unit -> unit)
    =

    // Working copy per field, seeded from persisted values falling back
    // to the declared default. Re-seeded whenever the persisted map
    // identity changes (save round-trip completes).
    let seedOne (field: ConfigFieldSchema) =
        let raw =
            persisted |> Map.tryFind field.Key |> Option.defaultValue field.DefaultJson

        displayFromJson field.Kind raw

    let seed () =
        entry.Schema.Fields |> List.map (fun f -> f.Key, seedOne f) |> Map.ofList

    let values, setValues = React.useState seed

    React.useEffect (
        (fun () -> setValues (seed ())),
        // `persisted` is a Map; React needs an identity-changing dep so
        // we hash via Map's ToString (stable on Fable for value
        // equality).
        [| box (string persisted) |]
    )

    let setField (key: string) (v: string) = setValues (values |> Map.add key v)

    let fieldInput (field: ConfigFieldSchema) =
        let current = values |> Map.tryFind field.Key |> Option.defaultValue ""

        match field.Kind with
        | ConfigFieldKind.Bool ->
            let isOn = current = "true"

            Html.input [
                prop.type' "checkbox"
                prop.isChecked isOn
                prop.onChange (fun (b: bool) -> setField field.Key (if b then "true" else "false"))
            ]

        | ConfigFieldKind.Int(min, max) ->
            Html.input [
                prop.type' "number"
                prop.step 1
                match min with
                | Some m -> prop.min m
                | None -> ()
                match max with
                | Some m -> prop.max m
                | None -> ()
                prop.value current
                prop.onChange (fun (v: string) -> setField field.Key v)
                prop.className "border border-border rounded-lg px-4 py-2 focus:outline-none focus:border-brand w-full"
            ]

        | ConfigFieldKind.Float(min, max) ->
            Html.input [
                prop.type' "number"
                prop.step 0.01
                match min with
                | Some m -> prop.min m
                | None -> ()
                match max with
                | Some m -> prop.max m
                | None -> ()
                prop.value current
                prop.onChange (fun (v: string) -> setField field.Key v)
                prop.className "border border-border rounded-lg px-4 py-2 focus:outline-none focus:border-brand w-full"
            ]

        | ConfigFieldKind.String _ ->
            Html.input [
                prop.type' "text"
                prop.value current
                prop.onChange (fun (v: string) -> setField field.Key v)
                prop.className "border border-border rounded-lg px-4 py-2 focus:outline-none focus:border-brand w-full"
            ]

        | ConfigFieldKind.Choice options ->
            Html.select [
                prop.value current
                prop.onChange (fun (v: string) -> setField field.Key v)
                prop.className
                    "border border-border rounded-lg px-4 py-2 focus:outline-none focus:border-brand w-full bg-white"
                prop.children [ for o in options -> Html.option [ prop.value o; prop.text o ] ]
            ]

    let row (field: ConfigFieldSchema) =
        Html.div [
            prop.className "mb-4"
            prop.children [
                Html.label [
                    prop.className "block text-sm font-medium text-gray-700 mb-1"
                    prop.children [
                        Html.text field.DisplayName
                        if field.Required then
                            Html.span [ prop.className "text-red-500 ml-1"; prop.text "*" ]
                    ]
                ]
                fieldInput field
                match field.Description with
                | Some d -> Html.p [ prop.className "text-xs text-gray-500 mt-1"; prop.text d ]
                | None -> Html.none
            ]
        ]

    let statusBanner =
        match status with
        | Idle -> Html.none
        | Saving -> Html.div [ prop.className "mt-3 text-sm text-gray-500"; prop.text "Saving..." ]
        | Saved ->
            Html.div [
                prop.className
                    "mt-3 p-2 bg-green-50 border border-green-200 rounded text-green-700 text-sm flex items-center justify-between"
                prop.children [
                    Html.span [ prop.text "Saved." ]
                    Html.button [
                        prop.className "text-xs text-green-700 hover:underline"
                        prop.text "dismiss"
                        prop.onClick (fun _ -> onDismissStatus ())
                    ]
                ]
            ]
        | Failed err ->
            Html.div [
                prop.className
                    "mt-3 p-2 bg-red-50 border border-red-200 rounded text-red-700 text-sm flex items-center justify-between"
                prop.children [
                    Html.span [ prop.text err ]
                    Html.button [
                        prop.className "text-xs text-red-700 hover:underline"
                        prop.text "dismiss"
                        prop.onClick (fun _ -> onDismissStatus ())
                    ]
                ]
            ]

    let onSaveClick () =
        let encoded =
            entry.Schema.Fields
            |> List.choose (fun field ->
                let display = values |> Map.tryFind field.Key |> Option.defaultValue ""
                // Optional fields with an empty display value get
                // omitted from the save payload — the server then
                // falls back to the declared default on reads. This
                // lets an admin revert a single field to default
                // without wiping the whole schema via Clear.
                if not field.Required && display = "" then
                    None
                else
                    Some(field.Key, jsonFromDisplay field.Kind display))
            |> Map.ofList

        onSave encoded

    Html.div [
        prop.className "bg-white rounded-lg border border-border p-6"
        prop.children [
            Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text entry.DisplayName ]
            Html.p [
                prop.className "text-xs text-gray-500 mb-4"
                prop.text $"Key: {entry.ModuleKey}"
            ]
            match entry.Schema.Fields with
            | [] ->
                Html.p [
                    prop.className "text-sm text-gray-500"
                    prop.text "This module has no editable configuration."
                ]
            | fields ->
                Html.div [ prop.children (fields |> List.map row) ]

                Html.div [
                    prop.className "flex gap-2 mt-4"
                    prop.children [
                        Forms.Button.primary "Save" onSaveClick
                        Forms.Button.secondary "Clear all" onClear
                    ]
                ]

                statusBanner
        ]
    ]

// ─── Feature-flag form component ─────────────────────────────────────

/// Per-flag editor. Holds draft state locally (React.useState) so a
/// Bool checkbox toggle or a Variant dropdown change does not
/// round-trip through Elmish on every keystroke — matches the
/// `ModuleForm` pattern. `onSave` and `onClear` fire only on explicit
/// button clicks. PascalCase name follows React component conventions
/// so react-refresh picks up edits without a full reload.
[<ReactComponent>]
let private FlagRow
    (flag: FeatureFlag)
    (overrideOpt: FlagValue option)
    (status: FormStatus)
    (onSave: FlagValue -> unit)
    (onClear: unit -> unit)
    (onDismissStatus: unit -> unit)
    =
    // Seed the draft from the existing override if any, else the
    // declared default. Re-seeds when the override identity changes
    // (save / clear round-trip completes).
    let seedValue () : FlagValue =
        match overrideOpt with
        | Some v -> v
        | None -> flag.DefaultValue

    let draft, setDraft = React.useState seedValue

    React.useEffect (
        (fun () -> setDraft (seedValue ())),
        // `overrideOpt` is a value — stable for equality under Fable.
        [| box (string overrideOpt) |]
    )

    let input =
        match draft with
        | FlagValue.Bool current ->
            Html.label [
                prop.className "inline-flex items-center gap-2"
                prop.children [
                    Html.input [
                        prop.type' "checkbox"
                        prop.isChecked current
                        prop.onChange (fun (b: bool) -> setDraft (FlagValue.Bool b))
                    ]
                    Html.span [
                        prop.className "text-sm text-gray-700"
                        prop.text (if current then "Enabled" else "Disabled")
                    ]
                ]
            ]

        | FlagValue.Variant(options, current) ->
            Html.select [
                prop.value current
                prop.onChange (fun (v: string) -> setDraft (FlagValue.Variant(options, v)))
                prop.className
                    "border border-border rounded-lg px-4 py-2 focus:outline-none focus:border-brand bg-white"
                prop.children [ for o in options -> Html.option [ prop.value o; prop.text o ] ]
            ]

    let statusBanner =
        match status with
        | Idle -> Html.none
        | Saving -> Html.div [ prop.className "mt-3 text-sm text-gray-500"; prop.text "Saving..." ]
        | Saved ->
            Html.div [
                prop.className
                    "mt-3 p-2 bg-green-50 border border-green-200 rounded text-green-700 text-sm flex items-center justify-between"
                prop.children [
                    Html.span [ prop.text "Saved." ]
                    Html.button [
                        prop.className "text-xs text-green-700 hover:underline"
                        prop.text "dismiss"
                        prop.onClick (fun _ -> onDismissStatus ())
                    ]
                ]
            ]
        | Failed err ->
            Html.div [
                prop.className
                    "mt-3 p-2 bg-red-50 border border-red-200 rounded text-red-700 text-sm flex items-center justify-between"
                prop.children [
                    Html.span [ prop.text err ]
                    Html.button [
                        prop.className "text-xs text-red-700 hover:underline"
                        prop.text "dismiss"
                        prop.onClick (fun _ -> onDismissStatus ())
                    ]
                ]
            ]

    let defaultDisplay =
        match flag.DefaultValue with
        | FlagValue.Bool b -> if b then "true" else "false"
        | FlagValue.Variant(_, v) -> v

    let badge =
        match overrideOpt with
        | Some _ ->
            Html.span [
                prop.className "inline-block text-xs px-2 py-0.5 rounded bg-brand text-white"
                prop.text "Overridden"
            ]
        | None ->
            Html.span [
                prop.className "inline-block text-xs px-2 py-0.5 rounded bg-gray-100 text-gray-600"
                prop.text "Using default"
            ]

    Html.div [
        prop.className "bg-white rounded-lg border border-border p-4 mb-3"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between mb-1"
                prop.children [
                    Html.div [
                        prop.className "flex items-center gap-2"
                        prop.children [
                            Html.span [ prop.className "text-sm font-semibold"; prop.text flag.Key ]
                            badge
                        ]
                    ]
                    match flag.Owner with
                    | Some owner -> Html.span [ prop.className "text-xs text-gray-500"; prop.text $"Owner: {owner}" ]
                    | None -> Html.none
                ]
            ]
            if not (System.String.IsNullOrWhiteSpace flag.Description) then
                Html.p [ prop.className "text-xs text-gray-500 mb-2"; prop.text flag.Description ]
            Html.p [
                prop.className "text-xs text-gray-400 mb-3"
                prop.text $"Default: {defaultDisplay}"
            ]
            input
            Html.div [
                prop.className "flex gap-2 mt-3"
                prop.children [
                    Forms.Button.primary "Save override" (fun () -> onSave draft)
                    Forms.Button.secondary "Clear override" (fun () ->
                        if overrideOpt.IsSome then
                            onClear ())
                ]
            ]
            statusBanner
        ]
    ]

let private flagsTabView (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex-1 p-6 overflow-y-auto"
        prop.children [
            Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text "Feature flags" ]
            Html.p [
                prop.className "text-xs text-gray-500 mb-4"
                prop.text
                    "Set overrides at your admin scope. Cleared overrides fall through to the next layer (User → Team → Platform → declared default)."
            ]
            match model.FlagsError with
            | Some msg ->
                Html.div [
                    prop.className
                        "mb-4 p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm flex items-center justify-between"
                    prop.children [
                        Html.span [ prop.text msg ]
                        Html.button [
                            prop.className "text-xs text-red-600 hover:underline"
                            prop.text "dismiss"
                            prop.onClick (fun _ -> dispatch DismissFlagsError)
                        ]
                    ]
                ]
            | None -> Html.none
            if not model.FlagsLoaded then
                Html.p [ prop.className "text-sm text-gray-500"; prop.text "Loading flags..." ]
            elif List.isEmpty model.DeclaredFlags then
                Html.p [
                    prop.className "text-sm text-gray-500"
                    prop.text
                        "No feature flags declared. Modules surface flags automatically when they list them at register() time."
                ]
            else
                Html.div [
                    prop.children [
                        for flag in model.DeclaredFlags ->
                            let overrideOpt = model.FlagOverrides |> Map.tryFind flag.Key

                            let status = model.FlagStatus |> Map.tryFind flag.Key |> Option.defaultValue Idle

                            FlagRow
                                flag
                                overrideOpt
                                status
                                (fun v -> dispatch (SubmitSetFlag(flag.Key, v)))
                                (fun () -> dispatch (SubmitClearFlag flag.Key))
                                (fun () -> dispatch (DismissFlagStatus flag.Key))
                    ]
                ]
        ]
    ]

// ─── View ────────────────────────────────────────────────────────────

let private sidebar (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "w-64 border-r border-border bg-gray-50 p-4"
        prop.children [
            Html.h3 [
                prop.className "text-xs font-semibold uppercase tracking-wide text-gray-500 mb-2"
                prop.text "Modules"
            ]
            if not model.Loaded then
                Html.p [ prop.className "text-sm text-gray-500"; prop.text "Loading..." ]
            elif List.isEmpty model.Modules then
                Html.p [ prop.className "text-sm text-gray-500"; prop.text "No configurable modules." ]
            else
                Html.div [
                    prop.className "flex flex-col gap-1"
                    prop.children [
                        for entry in model.Modules ->
                            let isSelected = model.Selected = Some entry.ModuleKey

                            Html.button [
                                prop.className [
                                    "text-left px-3 py-2 rounded"
                                    if isSelected then
                                        "bg-brand text-white"
                                    else
                                        "hover:bg-gray-100 text-gray-700"
                                ]
                                prop.text entry.DisplayName
                                prop.onClick (fun _ -> dispatch (SelectModule entry.ModuleKey))
                            ]
                    ]
                ]
        ]
    ]

let private detail (model: Model) (dispatch: Msg -> unit) =
    match model.Selected with
    | None ->
        Html.div [
            prop.className "flex-1 p-6"
            prop.children [
                Html.p [
                    prop.className "text-sm text-gray-500"
                    prop.text (
                        if model.Loaded then
                            "Select a module to configure."
                        else
                            "Loading modules..."
                    )
                ]
            ]
        ]
    | Some key ->
        match model.Modules |> List.tryFind (fun m -> m.ModuleKey = key) with
        | None ->
            Html.div [
                prop.className "flex-1 p-6"
                prop.children [
                    Html.p [
                        prop.className "text-sm text-red-500"
                        prop.text $"Module '{key}' is not available for configuration."
                    ]
                ]
            ]
        | Some entry ->
            let persisted = model.Values |> Map.tryFind key |> Option.defaultValue Map.empty
            let status = model.Status |> Map.tryFind key |> Option.defaultValue Idle

            Html.div [
                prop.className "flex-1 p-6"
                prop.children [
                    ModuleForm
                        entry
                        persisted
                        status
                        (fun values -> dispatch (SubmitSave(key, values)))
                        (fun () -> dispatch (SubmitClear key))
                        (fun () -> dispatch (DismissStatus key))
                ]
            ]

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
            tabButton "Configuration" (model.ActiveTab = ConfigTab) (fun () -> dispatch (SwitchTab ConfigTab))
            tabButton "Feature flags" (model.ActiveTab = FlagsTab) (fun () -> dispatch (SwitchTab FlagsTab))
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
    let errorBanner =
        match model.Error with
        | Some msg ->
            Html.div [
                prop.className
                    "mt-3 p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm flex items-center justify-between"
                prop.children [
                    Html.span [ prop.text msg ]
                    Html.button [
                        prop.className "text-xs text-red-600 hover:underline"
                        prop.text "dismiss"
                        prop.onClick (fun _ -> dispatch DismissError)
                    ]
                ]
            ]
        | None -> Html.none

    let content =
        match model.ActiveTab with
        | ConfigTab ->
            Html.div [
                prop.className "flex flex-1 min-h-0"
                prop.children [ sidebar model dispatch; detail model dispatch ]
            ]
        | FlagsTab -> flagsTabView model dispatch

    let body =
        Html.div [
            prop.className "flex flex-col h-full"
            prop.children [ tabBar model dispatch; content ]
        ]

    body, errorBanner

// ─── Module creation ─────────────────────────────────────────────────

/// Create the built-in configuration admin as an `ErasedModule`. The
/// shell's `prepareModules` injects this when the deployment runs in
/// any non-Anonymous mode — Anonymous has no persistent scope so the
/// form would fail every read.
let create (config: TeamConfigConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Configuration"

    let icon =
        config
        |> Option.map _.Icon
        |> Option.defaultValue ToolUp.Platform.Icons.teamConfig

    let route = "/" + name.ToLower().Replace(" ", "-")

    // SDK-built-in — reserved under the `_sdk.` Id namespace so it can
    // never collide with an app's RBAC-managed `ServerConfig.ModuleNames`.
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.TeamConfig"
    |> ToolUp.Platform.ClientModule.withView view
    |> ToolUp.Platform.ClientModule.withGroup "Admin"
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
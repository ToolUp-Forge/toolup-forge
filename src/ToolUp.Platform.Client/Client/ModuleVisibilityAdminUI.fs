// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ModuleVisibilityAdminUI

open ToolUp.Elmish
open Feliz
open ToolUp.Platform

// ─── Module-visibility profile editor (client-tier admin slice) ──────
//
// The admin surface for the module-visibility substrate. The server half
// is complete and gated — `IModuleVisibilityApi` lists the registered
// modules from the composed module surface, reads the caller's profile,
// and saves / clears it under the platform-admin / team-owner gate with
// an audit event — but until this module existed nothing rendered it, so
// a deployment curated by calling the API directly.
//
// **The candidate list is the deployment's, not ours.**
// `ListRegisteredModules` projects `ServerConfig.ModuleNames`, so a
// module added to the composition appears here without anyone
// remembering to add it anywhere. The editor never carries a list of its
// own.
//
// **The resolved profile is rendered alongside the editable one.** A
// profile is composed `Platform → Team → User` and every layer may only
// remove, so the profile an operator edits is not necessarily the
// answer their callers get: a platform-scoped layer above them may
// already have narrowed the surface. Without the resolution beside the
// editor, "I allowed it and it still does not appear" is unanswerable
// from the UI, and the operator's next move is to widen a list that was
// never the constraint.
//
// **Gating.** `NavRole.TeamOwnerAdmin` — the same authority
// `ModuleVisibilityApiHandler`'s write gate enforces, so the sidebar
// affordance only renders for callers the save would succeed for.
// Opt-in and zero-cost (GP 13): a deployment on the default
// `ServerConfig.ModuleVisibility = NoModuleVisibility` never sets
// `ClientConfig.ModuleVisibilityAdmin`, and the API's routes 404 anyway.

// ─── Model ───────────────────────────────────────────────────────────

/// Which shape of rule the editor is currently expressing. The stored
/// `ModuleVisibilityRule` carries its ids inline; the editor needs the
/// discriminator on its own so a caller can flip Allow ⇄ Deny while
/// keeping the selection they have built up.
[<RequireQualifiedAccess>]
type RuleKind =
    | Allow
    | Deny

type Model = {
    /// Every module id this deployment registers, in composition order —
    /// the candidate universe, straight from `ListRegisteredModules`.
    RegisteredModuleIds: string list
    /// The profile currently stored at the caller's admin scope, as last
    /// loaded. `None` when that scope declares none.
    StoredProfile: ModuleVisibilityProfile option
    /// What the server would resolve for this caller right now, across
    /// every contributing layer. `None` means no layer declares a
    /// profile — the unconfigured deployment.
    Resolved: ModuleVisibilityResolution option
    /// True once `GetResolvedVisibility` has answered, so the view can
    /// tell "still fetching" from "resolved to nothing".
    ResolvedLoaded: bool
    /// Editor: the rule shape being expressed.
    Kind: RuleKind
    /// Editor: the module ids named by the rule, in the order the
    /// operator declared them. A list rather than a set because an
    /// `Allow` list IS an ordered curation (`ModuleVisibilityResolution`
    /// carries that order through), and a set would silently discard it.
    Selection: string list
    /// Editor seed for the free-text note. The live input value is React
    /// state inside `EditorForm` (per the MVU rule on text inputs); this
    /// field is what it seeds from on load, never a per-keystroke mirror.
    Note: string
    /// Per-page exclusions carried through from the loaded profile.
    ///
    /// Not editable here — this slice curates at module granularity —
    /// but round-tripped rather than dropped. `SetProfile` replaces the
    /// whole document, so omitting them would silently discard an
    /// operator's page-level narrowing the moment anyone used this
    /// editor, which is data loss disguised as a save.
    CarriedExcludedEntryIds: string list
    /// True once `GetProfile` has answered — the editor's seed has
    /// landed and the form may render.
    Loaded: bool
    /// True while a save / clear is in flight.
    Busy: bool
    /// Error banner. Cleared by `DismissError` or the next mutation.
    Error: string option
    /// Transient confirmation after a save / clear.
    Status: string option
}

type Msg =
    /// Fetch all three surfaces — candidates, stored profile, resolution.
    | Load
    | RegisteredLoaded of Result<string list, string>
    | ProfileLoaded of Result<ModuleVisibilityProfile option, string>
    | ResolutionLoaded of Result<ModuleVisibilityResolution option, string>
    | SetKind of RuleKind
    | ToggleModule of string
    /// Save the edited profile. Carries the note from the form's local
    /// React state — text inputs do not round-trip through Elmish per
    /// keystroke.
    | Save of note: string
    | SaveCompleted of Result<unit, string>
    | Clear
    | ClearCompleted of Result<unit, string>
    | DismissError
    | DismissStatus

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see
// WebhookAdminUI.fs:73. `ModuleVisibilityApi.routeBuilder` is the default
// `/api/{type}/{method}` shape, so no override is needed.
let private visibilityApi: IModuleVisibilityApi =
    Api.makeProxy<IModuleVisibilityApi> (customOptions = UserSession.withRequestHeaders)

// ─── Commands ────────────────────────────────────────────────────────

let private loadRegisteredCmd () =
    Cmd.OfRemoting.call visibilityApi.ListRegisteredModules () (Ok >> RegisteredLoaded) (fun e ->
        RegisteredLoaded(Error e.Message))

let private loadProfileCmd () =
    Cmd.OfRemoting.call visibilityApi.GetProfile () ProfileLoaded (fun e -> ProfileLoaded(Error e.Message))

let private loadResolutionCmd () =
    Cmd.OfRemoting.call visibilityApi.GetResolvedVisibility () (Ok >> ResolutionLoaded) (fun e ->
        ResolutionLoaded(Error e.Message))

let private loadAllCmd () =
    Cmd.batch [ loadRegisteredCmd (); loadProfileCmd (); loadResolutionCmd () ]

// ─── Editor seeding ──────────────────────────────────────────────────

/// Seed the editor fields from a stored profile.
///
/// **A scope with no profile starts on the identity rule — an empty
/// DENY list.** `Deny []` removes nothing, so a fresh save is a no-op
/// the operator can then narrow deliberately. `Allow []` is the same
/// shape and the opposite meaning: it selects nothing, so one click on
/// Save would hide every governed module at that scope. Defaulting to
/// the destructive twin of an identical-looking form is exactly the kind
/// of accident a curation surface must not have.
let private seedEditor (profile: ModuleVisibilityProfile option) =
    match profile with
    | Some p ->
        let kind, ids =
            match p.Rule with
            | ModuleVisibilityRule.Allow ids -> RuleKind.Allow, ids
            | ModuleVisibilityRule.Deny ids -> RuleKind.Deny, ids

        kind, ids, (p.Note |> Option.defaultValue ""), p.ExcludedEntryIds
    | None -> RuleKind.Deny, [], "", []

let private ruleOf (kind: RuleKind) (ids: string list) : ModuleVisibilityRule =
    match kind with
    | RuleKind.Allow -> ModuleVisibilityRule.Allow ids
    | RuleKind.Deny -> ModuleVisibilityRule.Deny ids

/// The `SetProfile` payload for the current editor state.
///
/// Lifted out of `update` and made public so the `ExcludedEntryIds`
/// round-trip is pinned by a test rather than only by reading the save
/// arm — it is the one part of the payload the editor does not surface,
/// and therefore the one a future change could drop without anyone
/// noticing until an operator's page-level narrowing had gone.
let profileInput (note: string) (model: Model) : ModuleVisibilityProfileInput =
    let note = note.Trim()

    {
        Rule = ruleOf model.Kind model.Selection
        ExcludedEntryIds = model.CarriedExcludedEntryIds
        Note = if note = "" then None else Some note
    }

// ─── Init ────────────────────────────────────────────────────────────

let init () : Model * Cmd<Msg> =
    {
        RegisteredModuleIds = []
        StoredProfile = None
        Resolved = None
        ResolvedLoaded = false
        Kind = RuleKind.Deny
        Selection = []
        Note = ""
        CarriedExcludedEntryIds = []
        Loaded = false
        Busy = false
        Error = None
        Status = None
    },
    loadAllCmd ()

// ─── Update ──────────────────────────────────────────────────────────

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | Load -> { model with Error = None }, loadAllCmd ()

    | RegisteredLoaded(Ok ids) -> { model with RegisteredModuleIds = ids }, Cmd.none

    | RegisteredLoaded(Error err) -> { model with Error = Some err }, Cmd.none

    | ProfileLoaded(Ok profile) ->
        let kind, selection, note, excluded = seedEditor profile

        {
            model with
                StoredProfile = profile
                Kind = kind
                Selection = selection
                Note = note
                CarriedExcludedEntryIds = excluded
                Loaded = true
        },
        Cmd.none

    | ProfileLoaded(Error err) ->
        {
            model with
                Loaded = true
                Error = Some err
        },
        Cmd.none

    | ResolutionLoaded(Ok resolution) ->
        {
            model with
                Resolved = resolution
                ResolvedLoaded = true
        },
        Cmd.none

    | ResolutionLoaded(Error err) ->
        {
            model with
                ResolvedLoaded = true
                Error = Some err
        },
        Cmd.none

    // Flipping the rule shape keeps the selection: the operator has
    // named the same modules, and is deciding whether that naming means
    // "only these" or "everything but these".
    | SetKind kind -> { model with Kind = kind }, Cmd.none

    | ToggleModule moduleId ->
        let selection =
            if List.contains moduleId model.Selection then
                model.Selection |> List.filter (fun id -> id <> moduleId)
            else
                // Append rather than prepend — the declared order is the
                // operator's curation, and they built it front-to-back.
                model.Selection @ [ moduleId ]

        { model with Selection = selection }, Cmd.none

    | Save note ->
        if model.Busy || not model.Loaded then
            model, Cmd.none
        else
            let input = profileInput note model

            {
                model with
                    Busy = true
                    Error = None
                    Status = None
            },
            Cmd.OfRemoting.call visibilityApi.SetProfile input SaveCompleted (fun e -> SaveCompleted(Error e.Message))

    | SaveCompleted(Ok()) ->
        // Reload rather than patch the model locally: the resolution is
        // the server's answer across every layer, and a locally-applied
        // save would show the operator their own edit as though it were
        // the resolved outcome.
        {
            model with
                Busy = false
                Status = Some "Profile saved."
        },
        Cmd.ofMsg Load

    | SaveCompleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some err
        },
        Cmd.none

    | Clear ->
        if model.Busy || not model.Loaded then
            model, Cmd.none
        else
            {
                model with
                    Busy = true
                    Error = None
                    Status = None
            },
            Cmd.OfRemoting.call visibilityApi.ClearProfile () ClearCompleted (fun e -> ClearCompleted(Error e.Message))

    | ClearCompleted(Ok()) ->
        {
            model with
                Busy = false
                Status = Some "Profile cleared — this scope no longer contributes a layer."
        },
        Cmd.ofMsg Load

    | ClearCompleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some err
        },
        Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

    | DismissStatus -> { model with Status = None }, Cmd.none

// ─── View helpers ────────────────────────────────────────────────────

let private banner (cls: string) (text: string) (onDismiss: unit -> unit) =
    Html.div [
        prop.className $"mb-4 p-3 border rounded text-sm flex items-center justify-between {cls}"
        prop.children [
            Html.span [ prop.text text ]
            Html.button [
                prop.className "text-xs hover:underline"
                prop.text "dismiss"
                prop.onClick (fun _ -> onDismiss ())
            ]
        ]
    ]

let private kindButton (label: string) (description: string) (active: bool) (onClick: unit -> unit) =
    Html.button [
        prop.className [
            "flex-1 text-left px-3 py-2 rounded-lg border transition-colors"
            if active then
                "border-brand bg-brand/5"
            else
                "border-border bg-white hover:border-brand"
        ]
        prop.onClick (fun _ -> onClick ())
        prop.children [
            Html.div [ prop.className "text-sm font-semibold"; prop.text label ]
            Html.div [ prop.className "text-xs text-gray-500"; prop.text description ]
        ]
    ]

/// One row of the candidate list: the module id, its membership of the
/// edited rule, and — once the resolution has landed — whether the
/// server currently admits it.
let private moduleRow
    (resolution: ModuleVisibilityResolution option)
    (selected: bool)
    (moduleId: string)
    (onToggle: unit -> unit)
    =
    let admittedBadge =
        match resolution with
        | None -> Html.none
        | Some r ->
            if ModuleVisibility.admitsModule r moduleId then
                Html.span [
                    prop.className "text-xs px-2 py-0.5 rounded bg-green-100 text-green-700"
                    prop.text "visible now"
                ]
            else
                Html.span [
                    prop.className "text-xs px-2 py-0.5 rounded bg-gray-200 text-gray-600"
                    prop.text "hidden now"
                ]

    Html.label [
        prop.className "flex items-center gap-3 px-3 py-2 border-t border-border cursor-pointer hover:bg-gray-50"
        prop.children [
            Html.input [
                prop.type' "checkbox"
                prop.isChecked selected
                prop.onChange (fun (_: bool) -> onToggle ())
            ]
            Html.span [ prop.className "font-mono text-xs flex-1 break-all"; prop.text moduleId ]
            admittedBadge
        ]
    ]

/// The resolved answer, rendered beside the editable profile. See the
/// header note — this is what makes "I allowed it and it still does not
/// appear" answerable without leaving the page.
let private resolvedPane (model: Model) =
    let body =
        match model.Resolved with
        | None when not model.ResolvedLoaded -> Html.p [ prop.className "text-sm text-gray-500"; prop.text "Loading…" ]
        | None ->
            Html.p [
                prop.className "text-sm text-gray-600"
                prop.text
                    "No layer declares a profile, so every registered module is surfaced. Saving a profile below makes this scope the first contributing layer."
            ]
        | Some r ->
            let list (label: string) (items: string list) =
                Html.div [
                    prop.className "mb-3"
                    prop.children [
                        Html.div [
                            prop.className "text-xs font-medium text-gray-600 mb-1"
                            prop.text $"{label} ({List.length items})"
                        ]
                        if List.isEmpty items then
                            Html.p [ prop.className "text-xs text-gray-400"; prop.text "none" ]
                        else
                            Html.div [
                                prop.className "flex flex-wrap gap-1"
                                prop.children [
                                    for item in items ->
                                        Html.span [
                                            prop.className
                                                "font-mono text-xs px-2 py-0.5 rounded bg-gray-100 text-gray-700 break-all"
                                            prop.text item
                                        ]
                                ]
                            ]
                    ]
                ]

            Html.div [
                prop.children [
                    list "Governed modules" r.GovernedModuleIds
                    list "Selected after every layer" r.SelectedModuleIds
                    list "Excluded pages / entries" r.ExcludedEntryIds
                    list "Contributing scopes" (r.ContributingScopes |> List.map FlagScope.slug)
                ]
            ]

    Html.div [
        prop.className "bg-white rounded-lg border border-border p-4"
        prop.children [
            Html.h3 [ prop.className "text-sm font-semibold mb-1"; prop.text "Resolved for you" ]
            Html.p [
                prop.className "text-xs text-gray-500 mb-3"
                prop.text
                    "Composed platform → team → user; each layer may only remove. An outer layer can already have narrowed what your profile allows."
            ]
            body
        ]
    ]

/// The note field plus the two mutating actions. The note is local React
/// state so free-typing does not round-trip through Elmish (CLAUDE.md MVU
/// rule); `onSave` carries its value at submit time. Seeded once from
/// `model.Note` on mount — a reload after a save must not overwrite what
/// the operator is mid-way through typing.
[<ReactComponent>]
let private EditorForm (model: Model) (onSave: string -> unit) (onClear: unit -> unit) =
    let note, setNote = React.useState model.Note

    Html.div [
        prop.children [
            Html.label [
                prop.className "block text-xs font-medium text-gray-700 mb-1"
                prop.text "Note (why this profile exists)"
            ]
            Html.input [
                prop.type' "text"
                prop.value note
                prop.placeholder "e.g. this deployment ships the finance family only"
                prop.onChange (fun (v: string) -> setNote v)
                prop.className
                    "border border-border rounded-lg px-4 py-2 focus:outline-none focus:border-brand w-full text-sm mb-4"
            ]

            Html.div [
                prop.className "flex gap-2"
                prop.children [
                    Html.button [
                        prop.className [
                            "px-4 py-2 text-sm rounded-lg text-white transition-colors"
                            if model.Busy then
                                "bg-gray-300 cursor-not-allowed"
                            else
                                "bg-brand hover:bg-brand-dark cursor-pointer"
                        ]
                        prop.disabled model.Busy
                        prop.text (if model.Busy then "Working…" else "Save profile")
                        prop.onClick (fun _ -> onSave note)
                    ]
                    Html.button [
                        prop.className [
                            "px-4 py-2 text-sm rounded-lg border transition-colors"
                            if model.Busy || model.StoredProfile.IsNone then
                                "border-border text-gray-400 cursor-not-allowed"
                            else
                                "border-border text-gray-700 hover:border-red-300 hover:text-red-600 cursor-pointer"
                        ]
                        prop.disabled (model.Busy || model.StoredProfile.IsNone)
                        prop.text "Clear profile"
                        prop.onClick (fun _ -> onClear ())
                    ]
                ]
            ]
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let errorBanner =
        match model.Error with
        | Some msg -> banner "bg-red-50 border-red-200 text-red-700" msg (fun () -> dispatch DismissError)
        | None -> Html.none

    let statusBanner =
        match model.Status with
        | Some msg -> banner "bg-green-50 border-green-200 text-green-700" msg (fun () -> dispatch DismissStatus)
        | None -> Html.none

    let candidateRows =
        if List.isEmpty model.RegisteredModuleIds then
            [
                Html.p [
                    prop.className "px-3 py-3 text-xs text-gray-500"
                    prop.text
                        "This deployment registers no curatable modules. The SDK's own admin surfaces are deliberately absent from the governed set, so a profile can never hide the surface it is administered from."
                ]
            ]
        else
            [
                for moduleId in model.RegisteredModuleIds ->
                    moduleRow model.Resolved (List.contains moduleId model.Selection) moduleId (fun () ->
                        dispatch (ToggleModule moduleId))
            ]

    let candidateHeader =
        Html.div [
            prop.className "px-3 py-2 bg-gray-50 text-xs font-medium text-gray-600"
            prop.text
                $"Registered modules ({List.length model.RegisteredModuleIds}) — {List.length model.Selection} named"
        ]

    let editor =
        if not model.Loaded then
            Html.p [ prop.className "text-sm text-gray-500"; prop.text "Loading…" ]
        else
            Html.div [
                prop.className "bg-white rounded-lg border border-border p-4 mb-4"
                prop.children [
                    Html.h3 [ prop.className "text-sm font-semibold mb-1"; prop.text "Your profile" ]
                    Html.p [
                        prop.className "text-xs text-gray-500 mb-3"
                        prop.text
                            "Stored at your admin scope — the active team in team mode, your own scope otherwise. Modules this deployment does not register are ignored."
                    ]

                    Html.div [
                        prop.className "flex gap-2 mb-4"
                        prop.children [
                            kindButton
                                "Allow"
                                "Surface only the modules named below."
                                (model.Kind = RuleKind.Allow)
                                (fun () -> dispatch (SetKind RuleKind.Allow))
                            kindButton
                                "Deny"
                                "Surface everything except the modules named below."
                                (model.Kind = RuleKind.Deny)
                                (fun () -> dispatch (SetKind RuleKind.Deny))
                        ]
                    ]

                    Html.div [
                        prop.className "border border-border rounded-lg overflow-hidden mb-4"
                        prop.children (candidateHeader :: candidateRows)
                    ]

                    EditorForm model (fun note -> dispatch (Save note)) (fun () -> dispatch Clear)
                ]
            ]

    Html.div [
        prop.className "p-6 max-w-3xl"
        prop.children [
            Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text "Module visibility" ]
            Html.p [
                prop.className "text-sm text-gray-600 mb-4"
                prop.text "Curate which of this deployment's registered modules are surfaced at your scope."
            ]
            errorBanner
            statusBanner
            editor
            resolvedPane model
        ]
    ]

// ─── Module creation ─────────────────────────────────────────────────

/// Create the built-in module-visibility profile editor as an
/// `ErasedModule`. Injected by the shell's `prepareModules` when the
/// deployment opts in via `ClientConfig.ModuleVisibilityAdmin`; the
/// sidebar role filter hides it from callers below
/// `NavRole.TeamOwnerAdmin`, which is the same authority the server's
/// write gate enforces.
let create (config: ModuleVisibilityAdminConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Module Visibility"

    let icon =
        config
        |> Option.map _.Icon
        |> Option.defaultValue ToolUp.Platform.Icons.settings

    // SDK-built-in — reserved under the `_sdk.` Id namespace so it can
    // never collide with an app's RBAC-managed `ServerConfig.ModuleNames`,
    // and so a profile stated over that list can never hide the surface
    // profiles are administered from.
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.ModuleVisibilityAdmin"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Team Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
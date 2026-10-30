// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ModuleVisibilityAdminUI

open ToolUp.Elmish
open Feliz
open Fuaran.UI
open Fuaran.UI.Types
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
    /// Editor: the free-text note. Phase 1160 lifted it out of the form's
    /// local React state, so it does round-trip per keystroke.
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
    | SetNote of string
    /// Save the edited profile.
    | Save of note: string
    | SaveCompleted of Result<unit, string>
    | Clear
    | ClearCompleted of Result<unit, string>
    | DismissError
    | DismissStatus

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see
// `WebhookAdminUI`. `ModuleVisibilityApi.routeBuilder` is the default
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
                Status = Some MessageCatalog.english.ModuleVisibilityAdmin.ProfileSaved
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
                Status = Some MessageCatalog.english.ModuleVisibilityAdmin.ProfileCleared
        },
        Cmd.ofMsg Load

    | ClearCompleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some err
        },
        Cmd.none

    | SetNote note -> { model with Note = note }, Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

    | DismissStatus -> { model with Status = None }, Cmd.none

// ─── View helpers ────────────────────────────────────────────────────
// ─── View ────────────────────────────────────────────────────────────

/// One candidate as a checkbox field: its membership of the edited rule, and,
/// once the resolution has landed, whether the server currently admits it.
let private moduleField
    (msgs: ModuleVisibilityAdminMessages)
    (resolution: ModuleVisibilityResolution option)
    (selection: string list)
    (moduleId: string)
    : FormField<Msg> =
    {
        Id = "module-" + moduleId
        Kind =
            FormFieldKind.checkboxControl (
                Binding.Static(Some(List.contains moduleId selection)),
                onChange = (fun (_: bool) -> Action.dispatch (ToggleModule moduleId))
            )
        Label = TreeLowering.literal moduleId
        Required = false
        Help =
            resolution
            |> Option.map (fun r ->
                TreeLowering.literal (
                    if ModuleVisibility.admitsModule r moduleId then
                        msgs.VisibleNow
                    else
                        msgs.HiddenNow
                ))
        Rule = None
    }

/// The rule shape, the note and every candidate as one form. Submitting saves.
let private editorForm (msgs: ModuleVisibilityAdminMessages) (model: Model) : Node<Msg> =
    let kindOf (value: string option) =
        if value = Some "deny" then
            RuleKind.Deny
        else
            RuleKind.Allow

    let kindField: FormField<Msg> = {
        Id = "rule-kind"
        Kind =
            FormFieldKind.segmentedChoiceControl (
                Binding.Static(
                    Some [
                        {
                            Value = "allow"
                            Label = msgs.AllowLabel
                        }
                        {
                            Value = "deny"
                            Label = msgs.DenyLabel
                        }
                    ]
                ),
                Binding.Static(Some(if model.Kind = RuleKind.Deny then "deny" else "allow")),
                onChange = (fun value -> Action.dispatch (SetKind(kindOf value)))
            )
        Label = TreeLowering.literal msgs.YourProfileHeading
        Required = false
        Help =
            Some(
                TreeLowering.literal (
                    if model.Kind = RuleKind.Deny then
                        msgs.DenyDescription
                    else
                        msgs.AllowDescription
                )
            )
        Rule = None
    }

    let noteField: FormField<Msg> = {
        Id = "note"
        Kind =
            FormFieldKind.textControl (
                Binding.Static(Some model.Note),
                onChange = (fun text -> Action.dispatch (SetNote text))
            )
        Label = TreeLowering.literal msgs.NoteLabel
        Required = false
        Help = Some(TreeLowering.literal msgs.NotePlaceholder)
        Rule = None
    }

    let candidates =
        model.RegisteredModuleIds
        |> List.map (moduleField msgs model.Resolved model.Selection)

    Fuaran.form "editor" {
        Defaults.form with
            Fields = kindField :: candidates @ [ noteField ]
            OnSubmit = Action.dispatch (Save model.Note)
            SubmitLabel = TreeLowering.literal (if model.Busy then msgs.Working else msgs.SaveProfile)
            Disabled = Some(binding.query "busy" unbox<bool>)
    }

/// The resolved answer, rendered beside the editable profile. See the header
/// note: this is what makes "I allowed it and it still does not appear"
/// answerable without leaving the page.
let private resolvedPane (msgs: ModuleVisibilityAdminMessages) (model: Model) : Node<Msg> =
    let section (id: string) (label: int -> string) (items: string list) =
        TreeLowering.column id [
            TreeLowering.text (id + "-label") (label (List.length items))
            if List.isEmpty items then
                TreeLowering.text (id + "-empty") msgs.NoItems
            else
                Fuaran.list (id + "-items") items
        ]

    let body =
        match model.Resolved with
        | None when not model.ResolvedLoaded -> [ TreeLowering.text "resolved-loading" msgs.Loading ]
        | None -> [ TreeLowering.text "resolved-none" msgs.NoResolutionYet ]
        | Some r -> [
            section "governed" msgs.GovernedModules r.GovernedModuleIds
            section "selected" msgs.SelectedAfterEveryLayer r.SelectedModuleIds
            section "excluded" msgs.ExcludedEntries r.ExcludedEntryIds
            section "scopes" msgs.ContributingScopes (r.ContributingScopes |> List.map FlagScope.slug)
          ]

    TreeLowering.card
        "resolved"
        msgs.ResolvedForYouHeading
        (TreeLowering.text "resolved-help" msgs.ResolvedForYouHelp :: body)

/// The whole surface as a value. Pure: everything it shows comes from `model`,
/// so a test can read the tree without a DOM.
let private surface (msgs: ModuleVisibilityAdminMessages) (model: Model) : Node<Msg> =
    TreeLowering.column "module-visibility" [
        TreeLowering.heading "heading" 2 msgs.Heading
        TreeLowering.text "subheading" msgs.Subheading

        match model.Error with
        | Some text -> TreeLowering.banner "error" ToneVariant.Critical text msgs.Dismiss DismissError
        | None -> ()

        match model.Status with
        | Some text -> TreeLowering.banner "status" ToneVariant.Success text msgs.Dismiss DismissStatus
        | None -> ()

        if model.Loaded then
            TreeLowering.card "editor-card" msgs.YourProfileHeading [
                TreeLowering.text "editor-help" msgs.YourProfileHelp
                TreeLowering.text
                    "registered-header"
                    (msgs.RegisteredModulesHeader model.RegisteredModuleIds.Length model.Selection.Length)

                if List.isEmpty model.RegisteredModuleIds then
                    TreeLowering.text "no-modules" msgs.NoCuratableModules

                editorForm msgs model

                TreeLowering.button
                    "clear"
                    ButtonVariant.Destructive
                    msgs.ClearProfile
                    (model.Busy || model.StoredProfile.IsNone)
                    Clear
            ]
        else
            TreeLowering.text "loading" msgs.Loading

        resolvedPane msgs model
    ]

/// The form's disabled flag is the one value projected through the host seam.
let internal projection: IHostStateProjection<Model> =
    IHostStateProjection.ofFunc (fun model -> HostBindingSources.ofQueryResults (Map [ "busy", box model.Busy ]))

/// Phase 751 - the module body as a React COMPONENT, for the same reason
/// `HealthMonitorUI.HealthMonitorBody` is one: a module's `view` is invoked
/// inline by the shell's own render, so the catalog hook needs its own site.
[<ReactComponent>]
let private ModuleVisibilityAdminBody (model: Model) (sources: HostBindingSources) (host: ClientHostCapabilities<Msg>) =
    let msgs = (MessageCatalogProvider.useMessages ()).ModuleVisibilityAdmin
    TreeLowering.lowerHosted sources host (surface msgs model)

let internal view (model: Model) (_: Msg -> unit) (host: ClientHostCapabilities<Msg>) (sources: HostBindingSources) =
    ModuleVisibilityAdminBody model sources host
// ─── Module creation ─────────────────────────────────────────────────

/// Phase 879 — this built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `create` registers the module under it.
let moduleId = "_sdk.ModuleVisibilityAdmin"

/// Create the built-in module-visibility profile editor as an
/// `ErasedModule`. Injected by the shell's `prepareModules` when the
/// deployment opts in via `ClientConfig.ModuleVisibilityAdmin`; the
/// sidebar role filter hides it from callers below
/// `NavRole.TeamOwnerAdmin`, which is the same authority the server's
/// write gate enforces.
let create (config: ModuleLabel option) : ErasedModule =
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
    |> ToolUp.Platform.ClientModule.withId moduleId
    |> ClientBoundHostView.withBoundElementView projection view
    |> ToolUp.Platform.ClientModule.withGroup "Team Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
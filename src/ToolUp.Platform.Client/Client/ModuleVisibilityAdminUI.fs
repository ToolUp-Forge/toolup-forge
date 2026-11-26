// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ModuleVisibilityAdminUI

open Fuaran.MVU
open Fable.Core
open Fable.Core.JsInterop
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
    /// Error notice. Cleared by `DismissError` or the next mutation.
    Error: Notice option
    /// Transient confirmation after a save / clear.
    Status: Notice option
    /// This module's section of `ClientModuleContext.Messages`,
    /// kept from `init` so `update` authors reader-facing text in the
    /// session's language (a locale switch re-initialises the module).
    Messages: ModuleVisibilityAdminMessages
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
// `WebhookAdminUI`. `ModuleVisibilityApi.routeBuilder` is the default
// `/api/{type}/{method}` shape, so no override is needed.
let private visibilityApi: IModuleVisibilityApi =
    Api.makeProxy<IModuleVisibilityApi> (customOptions = UserSession.withRequestHeaders)

// ─── Commands ────────────────────────────────────────────────────────

let internal loadRegistered () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IModuleVisibilityApi.ListRegisteredModules"
            visibilityApi.ListRegisteredModules
            ()
            (Result.mapError _.Message >> RegisteredLoaded)
    )
]

let internal loadProfile () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IModuleVisibilityApi.GetProfile"
            visibilityApi.GetProfile
            ()
            (RemoteEffect.flatten >> ProfileLoaded)
    )
]

let internal loadResolution () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IModuleVisibilityApi.GetResolvedVisibility"
            visibilityApi.GetResolvedVisibility
            ()
            (Result.mapError _.Message >> ResolutionLoaded)
    )
]

let internal loadAll () : Step<Msg, RemoteEffect<Msg>> list =
    loadRegistered () @ loadProfile () @ loadResolution ()

let internal saveProfile (input: ModuleVisibilityProfileInput) : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IModuleVisibilityApi.SetProfile"
            visibilityApi.SetProfile
            input
            (RemoteEffect.flatten >> SaveCompleted)
    )
]

let internal clearProfile () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IModuleVisibilityApi.ClearProfile"
            visibilityApi.ClearProfile
            ()
            (RemoteEffect.flatten >> ClearCompleted)
    )
]

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

let init (ctx: ClientModuleContext) : Model * Step<Msg, RemoteEffect<Msg>> list =
    ({
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
        Messages = ctx.Messages.ModuleVisibilityAdmin
     },
     loadAll ())

// ─── Update ──────────────────────────────────────────────────────────

/// The transitions, with their remote calls as values.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | Load -> { model with Error = None }, loadAll ()

    | RegisteredLoaded(Ok ids) -> { model with RegisteredModuleIds = ids }, []

    | RegisteredLoaded(Error err) ->
        {
            model with
                Error = Some(Notice.Problem err)
        },
        []

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
        []

    | ProfileLoaded(Error err) ->
        {
            model with
                Loaded = true
                Error = Some(Notice.Problem err)
        },
        []

    | ResolutionLoaded(Ok resolution) ->
        {
            model with
                Resolved = resolution
                ResolvedLoaded = true
        },
        []

    | ResolutionLoaded(Error err) ->
        {
            model with
                ResolvedLoaded = true
                Error = Some(Notice.Problem err)
        },
        []

    // Flipping the rule shape keeps the selection: the operator has
    // named the same modules, and is deciding whether that naming means
    // "only these" or "everything but these".
    | SetKind kind -> { model with Kind = kind }, []

    | ToggleModule moduleId ->
        let selection =
            if List.contains moduleId model.Selection then
                model.Selection |> List.filter (fun id -> id <> moduleId)
            else
                // Append rather than prepend — the declared order is the
                // operator's curation, and they built it front-to-back.
                model.Selection @ [ moduleId ]

        { model with Selection = selection }, []

    | Save note ->
        if model.Busy || not model.Loaded then
            model, []
        else
            let input = profileInput note model

            {
                model with
                    Busy = true
                    Error = None
                    Status = None
            },
            saveProfile input

    | SaveCompleted(Ok()) ->
        // Reload rather than patch the model locally: the resolution is
        // the server's answer across every layer, and a locally-applied
        // save would show the operator their own edit as though it were
        // the resolved outcome.
        {
            model with
                Busy = false
                Status = Some(Notice.Info model.Messages.ProfileSaved)
        },
        [ Then Load ]

    | SaveCompleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
        },
        []

    | Clear ->
        if model.Busy || not model.Loaded then
            model, []
        else
            {
                model with
                    Busy = true
                    Error = None
                    Status = None
            },
            clearProfile ()

    | ClearCompleted(Ok()) ->
        {
            model with
                Busy = false
                Status = Some(Notice.Info model.Messages.ProfileCleared)
        },
        [ Then Load ]

    | ClearCompleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
        },
        []

    | DismissError -> { model with Error = None }, []

    | DismissStatus -> { model with Status = None }, []

// ─── View ────────────────────────────────────────────────────────────

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: ModuleVisibilityAdminMessages
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./ModuleVisibilityAdminTree.fs")

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "ModuleVisibilityAdminTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.ModuleVisibilityAdmin
            Locale = ctx.Locale
        |}
    )

// ─── Module creation ─────────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.ModuleVisibilityAdmin"

/// Create the built-in module-visibility profile editor as an
/// `ErasedModule`. Injected by the shell's `prepareModules` when the
/// deployment opts in via `ClientConfig.ModuleVisibilityAdmin`; the
/// sidebar role filter hides it from callers below
/// `NavRole.TeamOwnerAdmin`, which is the same authority the server's
/// write gate enforces.
let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Module Visibility"
        Icon = ToolUp.Platform.Icons.settings
    }
    Group = Some "Team Management"
    NavRole = Some ToolUp.Platform.NavRole.TeamOwnerAdmin
    Area = Product
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = init
    Update = update
    View = BuiltInView.Page view
    Extend = id
}
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ProviderProfileUI

open ToolUp.Elmish
open Feliz
open ToolUp.Platform

// ─── The reusable BYOK provider-profile component (Phase 44) ─────────
//
// One well-tested BYOK management surface that any ToolUp app embeds,
// instead of every consumer hand-rolling its own provider form. It
// renders the whole of a `ProviderProfile`: the configured entries with
// their tags and live-health badges, the add / edit / remove form with a
// paste-key field, the per-surface routing defaults with per-context
// overrides, and the ordered fallback chain.
//
// **It carries no dependency on `ToolUp.AI`, and that is the entire
// point of the Phase 42.B decoupling.** It composes over
// `IProviderProfileApi` — the Phase 44 transport across the canonical
// `IProviderProfile` store — and takes "verify this key" as a
// CONSUMER-SUPPLIED DELEGATE. The AI assistant wires an AI-backed
// verifier; a knowledge-rental gateway wires its own; a consumer with
// no verifier gets a clearly-labelled "not verified" state and entries
// that still persist. Nothing here knows what a provider is FOR (GP 1).
//
// **Composition surface — three things and no other wiring:**
//
//   ProviderProfileConfig.create [ "rental.gateway" ]
//   |> ProviderProfileConfig.withVerify myVerifier     // optional
//
// then `init` / `update` / `view` threaded through the host's own
// Elmish tree, exactly as `ModuleVisibilityAdminUI` is. `create`
// defaults the API to the standard proxy, so the common case supplies
// only the surface keys.
//
// **MVU discipline.** `update` is pure; every effect is a `Cmd`. The
// editor's text inputs are React-local state inside `EditorForm` and
// reach Elmish once, at submit — the `CLAUDE.md` rule, and the reason
// the model carries a SEED (`Editing`) rather than a per-keystroke
// mirror.

// ─── Composition surface ─────────────────────────────────────────────

/// The candidate an embedding app's verify delegate is handed: the
/// entry as the form currently holds it, INCLUDING the pasted key.
///
/// Deliberately not `ProviderEntry` (the stored shape the phase spec
/// named): that record carries `SecretKeyName` — a server-side
/// placement detail the browser never holds — and carries no key
/// material at all, so a delegate given one could not verify anything.
/// What a verifier needs is exactly what the user just typed.
type ProviderCandidate = {
    Label: string
    ProviderId: string
    Model: string option
    Tags: string list
    /// The key the user pasted in this edit. `None` when they are
    /// editing metadata against a key the server already holds — a
    /// delegate that cannot see the stored key answers for what it can
    /// reach, which is why the component does not demand verification
    /// in that case.
    ApiKey: string option
}

/// What an embedding app supplies. Three fields, matching the phase's
/// composition contract: the transport, an optional verifier, and the
/// surface keys this app routes.
type ProviderProfileConfig = {
    /// The transport. Defaulted by `create`; overridable for a host
    /// that proxies through its own route builder or headers.
    Api: IProviderProfileApi
    /// "Verify this key" — returns the provider's model list on
    /// success, or a human-readable reason. `None` (the default) is the
    /// no-AI, no-network case: entries persist and are labelled "not
    /// verified".
    Verify: (ProviderCandidate -> Async<Result<string list, string>>) option
    /// The routing surfaces this app cares about, e.g.
    /// `[ "ai.assistant" ]` or `[ "rental.gateway" ]`. The routing
    /// editor renders one block per key; an app that routes nothing
    /// passes `[]` and gets entries + fallback only.
    Surfaces: string list
}

/// Header freshness is the `CsrfClient` request-guard's job — see
/// `WebhookAdminUI`. `ProviderProfileApi.routeBuilder` is the default
/// `/api/{type}/{method}` shape, so no override is needed.
let private defaultApi: IProviderProfileApi =
    Api.makeProxy<IProviderProfileApi> (customOptions = UserSession.withRequestHeaders)

module ProviderProfileConfig =
    /// The common case: the standard proxy, no verifier, the app's own
    /// surface keys.
    let create (surfaces: string list) : ProviderProfileConfig = {
        Api = defaultApi
        Verify = None
        Surfaces = surfaces
    }

    /// Supply the "verify this key" delegate. Without one the component
    /// renders the `NotVerified` state and never blocks a save.
    let withVerify
        (verify: ProviderCandidate -> Async<Result<string list, string>>)
        (config: ProviderProfileConfig)
        : ProviderProfileConfig =
        { config with Verify = Some verify }

    /// Override the transport — for a host that proxies the API through
    /// its own route builder or header set.
    let withApi (api: IProviderProfileApi) (config: ProviderProfileConfig) : ProviderProfileConfig = {
        config with
            Api = api
    }

// ─── Model ───────────────────────────────────────────────────────────

/// Where the verify delegate got to for the entry being edited. Reset
/// to `NotAttempted` whenever the editor opens or the candidate's key
/// changes, so a stale green tick can never authorise a different key.
[<RequireQualifiedAccess>]
type VerificationState =
    | NotAttempted
    | Running
    | Passed of models: string list
    | Refused of reason: string

/// The editor's seed. `None` means the editor is closed.
type EditorSeed = {
    /// The label being edited, or `None` for a brand-new entry. Held
    /// separately from the draft label so a rename is expressible: the
    /// original identifies which entry is being replaced.
    Original: string option
    Label: string
    ProviderId: string
    Model: string
    Tags: string
    /// True when the server already holds a credential for `Original` —
    /// the key field then reads "leave blank to keep the stored key".
    HasStoredCredential: bool
    /// True for an OAuth-connected entry: the key field is not editable,
    /// because the credential is minted and refreshed by the OAuth
    /// substrate rather than pasted.
    OAuthConnected: bool
}

module EditorSeed =
    let blank: EditorSeed = {
        Original = None
        Label = ""
        ProviderId = ""
        Model = ""
        Tags = ""
        HasStoredCredential = false
        OAuthConnected = false
    }

    let ofEntry (entry: ProviderEntryView) : EditorSeed = {
        Original = Some entry.Label
        Label = entry.Label
        ProviderId = entry.ProviderId
        Model = entry.Model |> Option.defaultValue ""
        Tags = String.concat ", " entry.Tags
        HasStoredCredential = entry.HasCredential
        OAuthConnected = entry.Origin = CredentialOrigin.OAuthConnected
    }

type Model = {
    /// The profile as last loaded from the server.
    Profile: ProviderProfileView
    /// True once `GetProfile` has answered, so the view can tell "still
    /// fetching" from "resolved to nothing configured".
    Loaded: bool
    /// The editor seed, or `None` when the editor is closed.
    Editing: EditorSeed option
    /// Where the verify delegate got to for the open editor.
    Verification: VerificationState
    /// True while a mutation is in flight.
    Busy: bool
    Error: string option
    Status: string option
}

type Msg =
    | Load
    | ProfileLoaded of Result<ProviderProfileView, string>
    | BeginAdd
    | BeginEdit of string
    | CancelEdit
    /// Run the consumer's verify delegate against the form's current
    /// candidate. Carries the candidate because the form's fields are
    /// React-local until submit.
    | Verify of ProviderCandidate
    | VerifyCompleted of Result<string list, string>
    | Save of ProviderCandidate
    | SaveCompleted of Result<unit, string>
    | Remove of string
    | Mutated of Result<unit, string> * confirmation: string
    | SetRoute of RoutingRule
    | ClearRoute of surface: string * context: string option
    | SetFallback of string list
    | DismissError
    | DismissStatus

// ─── Commands ────────────────────────────────────────────────────────

let private loadCmd (config: ProviderProfileConfig) =
    Cmd.OfRemoting.call config.Api.GetProfile () ProfileLoaded (fun e -> ProfileLoaded(Error e.Message))

let private toInput (candidate: ProviderCandidate) : ProviderEntryInput = {
    Label = candidate.Label
    ProviderId = candidate.ProviderId
    Model = candidate.Model
    Tags = candidate.Tags
    ApiKey = candidate.ApiKey
}

/// Split a comma-separated tag field into the stored list. Blank
/// segments are dropped rather than stored, so a trailing comma does
/// not persist an empty tag that no filter can ever match.
let parseTags (raw: string) : string list =
    raw.Split(',')
    |> Array.map _.Trim()
    |> Array.filter (fun t -> t <> "")
    |> List.ofArray

/// The optional-model field: blank means "the provider's default",
/// which the store spells `None`.
let private optionalText (raw: string) : string option =
    let trimmed = raw.Trim()

    if trimmed = "" then None else Some trimmed

/// Whether the component must refuse to save.
///
/// Only when the embedding app CAN verify (a delegate is supplied), the
/// user pasted a key in this edit, and verification has not passed. A
/// consumer with no delegate is never blocked — that is the whole
/// no-AI-dependency posture — and a metadata edit against a stored key
/// is not blocked either, since no delegate can see a key it was not
/// given.
let saveBlocked (config: ProviderProfileConfig) (verification: VerificationState) (candidate: ProviderCandidate) =
    match config.Verify, candidate.ApiKey with
    | Some _, Some _ ->
        match verification with
        | VerificationState.Passed _ -> false
        | _ -> true
    | _ -> false

// ─── Update ──────────────────────────────────────────────────────────

let init (config: ProviderProfileConfig) : Model * Cmd<Msg> =
    {
        Profile = ProviderProfileView.empty
        Loaded = false
        Editing = None
        Verification = VerificationState.NotAttempted
        Busy = false
        Error = None
        Status = None
    },
    loadCmd config

let update (config: ProviderProfileConfig) (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | Load -> { model with Error = None }, loadCmd config

    | ProfileLoaded(Ok profile) ->
        {
            model with
                Profile = profile
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

    | BeginAdd ->
        {
            model with
                Editing = Some EditorSeed.blank
                Verification = VerificationState.NotAttempted
                Error = None
        },
        Cmd.none

    | BeginEdit label ->
        match model.Profile.Entries |> List.tryFind (fun e -> e.Label = label) with
        | None -> model, Cmd.none
        | Some entry ->
            {
                model with
                    Editing = Some(EditorSeed.ofEntry entry)
                    // A previous entry's green tick must never carry over
                    // to a different entry's key.
                    Verification = VerificationState.NotAttempted
                    Error = None
            },
            Cmd.none

    | CancelEdit ->
        {
            model with
                Editing = None
                Verification = VerificationState.NotAttempted
        },
        Cmd.none

    | Verify candidate ->
        match config.Verify with
        // No delegate: nothing to run. The view renders `NotVerified`
        // and never offers the action, so this arm is only reachable
        // from a host dispatching the message itself.
        | None -> model, Cmd.none
        | Some verify ->
            {
                model with
                    Verification = VerificationState.Running
            },
            Cmd.OfAsync.either verify candidate VerifyCompleted (fun e -> VerifyCompleted(Error e.Message))

    | VerifyCompleted(Ok models) ->
        {
            model with
                Verification = VerificationState.Passed models
        },
        Cmd.none

    | VerifyCompleted(Error reason) ->
        {
            model with
                Verification = VerificationState.Refused reason
        },
        Cmd.none

    | Save candidate ->
        if model.Busy then
            model, Cmd.none
        elif saveBlocked config model.Verification candidate then
            // Belt-and-braces: the view already disables the action.
            // Restating it here keeps the refusal true for a host that
            // dispatches `Save` itself.
            model, Cmd.none
        else
            let saveCmd =
                Cmd.OfRemoting.call config.Api.SaveEntry (toInput candidate) SaveCompleted (fun e ->
                    SaveCompleted(Error e.Message))

            // A rename replaces the entry under a new label, so the old
            // one is removed after the new one lands. Ordering matters:
            // removing first would delete the stored credential the new
            // entry may be relying on when no key was re-pasted.
            let renamedFrom =
                match model.Editing with
                | Some seed when seed.Original <> None && seed.Original <> Some candidate.Label -> seed.Original
                | _ -> None

            let cmds =
                match renamedFrom with
                | None -> saveCmd
                | Some previous ->
                    Cmd.batch [
                        saveCmd
                        Cmd.OfRemoting.call config.Api.RemoveEntry previous (fun r -> Mutated(r, "")) (fun e ->
                            Mutated(Error e.Message, ""))
                    ]

            {
                model with
                    Busy = true
                    Error = None
                    Status = None
            },
            cmds

    | SaveCompleted(Ok()) ->
        // Record what the consumer's delegate observed, so the health
        // badge reflects a verification the server never ran itself.
        let recordCmd =
            match model.Verification, model.Editing with
            | VerificationState.Passed models, Some seed ->
                Cmd.OfRemoting.call
                    config.Api.RecordVerification
                    (seed.Label, ProviderVerificationOutcome.Verified models)
                    (fun r -> Mutated(r, ""))
                    (fun e -> Mutated(Error e.Message, ""))
            | VerificationState.Refused reason, Some seed ->
                Cmd.OfRemoting.call
                    config.Api.RecordVerification
                    (seed.Label, ProviderVerificationOutcome.Failed reason)
                    (fun r -> Mutated(r, ""))
                    (fun e -> Mutated(Error e.Message, ""))
            | _ -> Cmd.none

        {
            model with
                Busy = false
                Editing = None
                Verification = VerificationState.NotAttempted
                Status = Some "saved"
        },
        // Reload rather than patch locally: `UpdatedAt`, the health
        // record and the credential flag are the server's answers, and
        // showing our own optimistic edit as though it were theirs is
        // how a failed write reads as a success.
        Cmd.batch [ recordCmd; Cmd.ofMsg Load ]

    | SaveCompleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some err
        },
        Cmd.none

    | Remove label ->
        if model.Busy then
            model, Cmd.none
        else
            {
                model with
                    Busy = true
                    Error = None
                    Status = None
            },
            Cmd.OfRemoting.call config.Api.RemoveEntry label (fun r -> Mutated(r, "removed")) (fun e ->
                Mutated(Error e.Message, "removed"))

    | SetRoute rule ->
        { model with Busy = true; Error = None },
        Cmd.OfRemoting.call config.Api.SetRoute rule (fun r -> Mutated(r, "routing")) (fun e ->
            Mutated(Error e.Message, "routing"))

    | ClearRoute(surface, context) ->
        { model with Busy = true; Error = None },
        Cmd.OfRemoting.call config.Api.ClearRoute (surface, context) (fun r -> Mutated(r, "routing")) (fun e ->
            Mutated(Error e.Message, "routing"))

    | SetFallback ordered ->
        { model with Busy = true; Error = None },
        Cmd.OfRemoting.call config.Api.SetFallback ordered (fun r -> Mutated(r, "fallback")) (fun e ->
            Mutated(Error e.Message, "fallback"))

    | Mutated(Ok(), confirmation) ->
        let status =
            if confirmation = "" then
                model.Status
            else
                Some confirmation

        {
            model with
                Busy = false
                Status = status
        },
        Cmd.ofMsg Load

    | Mutated(Error err, _) ->
        {
            model with
                Busy = false
                Error = Some err
        },
        Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

    | DismissStatus -> { model with Status = None }, Cmd.none

// ─── View helpers ────────────────────────────────────────────────────

let private banner (dismiss: string) (cls: string) (text: string) (onDismiss: unit -> unit) =
    Html.div [
        prop.className $"mb-4 p-3 border rounded text-sm flex items-center justify-between {cls}"
        prop.children [
            Html.span [ prop.text text ]
            Html.button [
                prop.className "text-xs hover:underline"
                prop.text dismiss
                prop.onClick (fun _ -> onDismiss ())
            ]
        ]
    ]

/// Health badge. Advisory by the store's own contract — resolution does
/// not gate on it — so the badge warns and never blocks.
let private healthBadge (msgs: ProviderProfileMessages) (health: ProviderHealth) =
    let label, cls =
        match health.Status with
        | ProviderHealthStatus.Healthy -> msgs.HealthHealthy, "bg-green-100 text-green-700"
        | ProviderHealthStatus.Degraded -> msgs.HealthDegraded, "bg-amber-100 text-amber-700"
        | ProviderHealthStatus.Unhealthy -> msgs.HealthUnhealthy, "bg-red-100 text-red-700"
        | ProviderHealthStatus.NeedsReauthorization -> msgs.HealthNeedsReauthorization, "bg-red-100 text-red-700"
        | ProviderHealthStatus.Unknown -> msgs.HealthUnknown, "bg-gray-200 text-gray-600"

    Html.span [ prop.className $"text-xs px-2 py-0.5 rounded {cls}"; prop.text label ]

let private tagChip (tag: string) =
    Html.span [
        prop.className "text-xs px-2 py-0.5 rounded bg-gray-100 text-gray-700"
        prop.text tag
    ]

let private entryRow
    (msgs: ProviderProfileMessages)
    (entry: ProviderEntryView)
    (onEdit: unit -> unit)
    (onRemove: unit -> unit)
    =
    Html.div [
        prop.className "flex items-start gap-3 px-3 py-3 border-t border-border"
        prop.children [
            Html.div [
                prop.className "flex-1 min-w-0"
                prop.children [
                    Html.div [
                        prop.className "flex items-center gap-2 flex-wrap"
                        prop.children [
                            Html.span [ prop.className "text-sm font-semibold break-all"; prop.text entry.Label ]
                            healthBadge msgs entry.Health
                            if entry.Origin = CredentialOrigin.OAuthConnected then
                                Html.span [
                                    prop.className "text-xs px-2 py-0.5 rounded bg-blue-100 text-blue-700"
                                    prop.text msgs.OAuthConnected
                                ]
                            elif not entry.HasCredential then
                                Html.span [
                                    prop.className "text-xs px-2 py-0.5 rounded bg-amber-100 text-amber-700"
                                    prop.text msgs.NoCredential
                                ]
                        ]
                    ]
                    Html.div [
                        prop.className "text-xs text-gray-500 font-mono break-all mt-0.5"
                        prop.text (
                            match entry.Model with
                            | Some m -> $"{entry.ProviderId} · {m}"
                            | None -> entry.ProviderId
                        )
                    ]
                    if not (List.isEmpty entry.Tags) then
                        Html.div [
                            prop.className "flex flex-wrap gap-1 mt-1"
                            prop.children [ for tag in entry.Tags -> tagChip tag ]
                        ]
                ]
            ]
            Html.div [
                prop.className "flex items-center gap-2 shrink-0"
                prop.children [
                    Html.button [
                        prop.className "text-xs hover:underline"
                        prop.text msgs.Edit
                        prop.onClick (fun _ -> onEdit ())
                    ]
                    Html.button [
                        prop.className "text-xs text-red-600 hover:underline"
                        prop.text msgs.Remove
                        prop.onClick (fun _ -> onRemove ())
                    ]
                ]
            ]
        ]
    ]

/// The add / edit form. Text inputs are React-local state and reach
/// Elmish once, at submit or at Verify — the `CLAUDE.md` MVU rule.
/// Seeded once per open editor; the `key` on the caller's side is what
/// remounts it when the edited entry changes.
[<ReactComponent>]
let private EditorForm
    (seed: EditorSeed)
    (canVerify: bool)
    (verification: VerificationState)
    (busy: bool)
    (onVerify: ProviderCandidate -> unit)
    (onSave: ProviderCandidate -> unit)
    (onCancel: unit -> unit)
    =
    let msgs = (MessageCatalogProvider.useMessages ()).ProviderProfile
    let label, setLabel = React.useState seed.Label
    let providerId, setProviderId = React.useState seed.ProviderId
    let model, setModel = React.useState seed.Model
    let tags, setTags = React.useState seed.Tags
    let apiKey, setApiKey = React.useState ""
    // The key text the last Verify was run against. Editing the key
    // after a green tick must not leave that tick authorising a
    // DIFFERENT key, and the parent's Verification state alone cannot
    // tell the difference — it has never seen the key.
    let verifiedFor, setVerifiedFor = React.useState ""

    let candidate: ProviderCandidate = {
        Label = label.Trim()
        ProviderId = providerId.Trim()
        Model = optionalText model
        Tags = parseTags tags
        ApiKey = optionalText apiKey
    }

    let blocked =
        canVerify
        && candidate.ApiKey.IsSome
        && (match verification with
            | VerificationState.Passed _ -> verifiedFor <> apiKey
            | _ -> true)

    let field (fieldLabel: string) (value: string) (placeholder: string) (onChange: string -> unit) =
        Html.label [
            prop.className "block mb-3"
            prop.children [
                Html.span [
                    prop.className "block text-xs font-medium text-gray-600 mb-1"
                    prop.text fieldLabel
                ]
                Html.input [
                    prop.className "w-full px-2 py-1.5 border border-border rounded text-sm"
                    prop.value value
                    prop.placeholder placeholder
                    prop.onChange onChange
                ]
            ]
        ]

    Html.div [
        prop.className "bg-white rounded-lg border border-border p-4 mb-4"
        prop.children [
            field msgs.LabelField label "" setLabel
            field msgs.ProviderIdField providerId "" setProviderId
            field msgs.ModelField model msgs.ModelPlaceholder setModel
            field msgs.TagsField tags msgs.TagsPlaceholder setTags

            if seed.OAuthConnected then
                Html.p [ prop.className "text-xs text-gray-500 mb-3"; prop.text msgs.OAuthKeyHelp ]
            else
                Html.div [
                    prop.children [
                        Html.label [
                            prop.className "block mb-1"
                            prop.children [
                                Html.span [
                                    prop.className "block text-xs font-medium text-gray-600 mb-1"
                                    prop.text msgs.ApiKeyField
                                ]
                                Html.input [
                                    prop.className "w-full px-2 py-1.5 border border-border rounded text-sm font-mono"
                                    prop.type' "password"
                                    prop.value apiKey
                                    prop.placeholder msgs.ApiKeyPlaceholder
                                    prop.onChange setApiKey
                                ]
                            ]
                        ]
                        if seed.HasStoredCredential then
                            Html.p [ prop.className "text-xs text-gray-500 mb-2"; prop.text msgs.ApiKeyKeepHelp ]
                        Html.div [
                            prop.className "flex items-center gap-2 mb-3 flex-wrap"
                            prop.children [
                                if canVerify then
                                    Html.button [
                                        prop.className
                                            "text-xs px-2 py-1 border border-border rounded hover:border-brand"
                                        prop.disabled (
                                            candidate.ApiKey.IsNone || verification = VerificationState.Running
                                        )
                                        prop.text (
                                            if verification = VerificationState.Running then
                                                msgs.Verifying
                                            else
                                                msgs.Verify
                                        )
                                        prop.onClick (fun _ ->
                                            setVerifiedFor apiKey
                                            onVerify candidate)
                                    ]
                                else
                                    Html.span [
                                        prop.className "text-xs px-2 py-0.5 rounded bg-gray-200 text-gray-600"
                                        prop.text msgs.NotVerified
                                    ]
                                if not canVerify then
                                    Html.span [ prop.className "text-xs text-gray-500"; prop.text msgs.NotVerifiedHelp ]
                                match verification with
                                | VerificationState.Passed models when verifiedFor = apiKey ->
                                    Html.span [
                                        prop.className "text-xs text-green-700"
                                        prop.text (msgs.VerifiedModels(List.length models))
                                    ]
                                | VerificationState.Refused reason ->
                                    Html.span [
                                        prop.className "text-xs text-red-600"
                                        prop.text (msgs.VerificationFailed reason)
                                    ]
                                | _ -> Html.none
                            ]
                        ]
                    ]
                ]

            if blocked then
                Html.p [
                    prop.className "text-xs text-amber-700 mb-2"
                    prop.text msgs.VerifyBeforeSaving
                ]

            Html.div [
                prop.className "flex items-center gap-2"
                prop.children [
                    Html.button [
                        prop.className "px-3 py-1.5 rounded bg-brand text-white text-sm disabled:opacity-50"
                        prop.disabled (busy || blocked || candidate.Label = "" || candidate.ProviderId = "")
                        prop.text msgs.Save
                        prop.onClick (fun _ -> onSave candidate)
                    ]
                    Html.button [
                        prop.className "px-3 py-1.5 rounded border border-border text-sm"
                        prop.text msgs.Cancel
                        prop.onClick (fun _ -> onCancel ())
                    ]
                ]
            ]
        ]
    ]

/// One surface's routing block: the default rule, plus any per-context
/// overrides. A context-specific rule wins over the default — the
/// `ProviderProfile.resolveEntry` semantics this editor round-trips.
[<ReactComponent>]
let private SurfaceRouting
    (surface: string)
    (entries: ProviderEntryView list)
    (routing: RoutingRule list)
    (onSet: RoutingRule -> unit)
    (onClear: string * string option -> unit)
    =
    let msgs = (MessageCatalogProvider.useMessages ()).ProviderProfile
    let newContext, setNewContext = React.useState ""

    let forSurface = routing |> List.filter (fun r -> r.Surface = surface)
    let defaultRule = forSurface |> List.tryFind (fun r -> r.Context = None)
    let overrides = forSurface |> List.filter (fun r -> r.Context <> None)

    let picker (context: string option) (selected: string option) =
        Html.select [
            prop.className "px-2 py-1 border border-border rounded text-sm"
            prop.value (selected |> Option.defaultValue "")
            prop.onChange (fun (v: string) ->
                if v = "" then
                    onClear (surface, context)
                else
                    onSet {
                        Surface = surface
                        Context = context
                        EntryLabel = v
                    })
            prop.children [
                Html.option [ prop.value ""; prop.text msgs.NoRoute ]
                for entry in entries -> Html.option [ prop.value entry.Label; prop.text entry.Label ]
            ]
        ]

    Html.div [
        prop.className "border-t border-border px-3 py-3"
        prop.children [
            Html.div [
                prop.className "text-sm font-semibold font-mono break-all mb-2"
                prop.text (msgs.SurfaceHeading surface)
            ]
            Html.div [
                prop.className "flex items-center gap-2 mb-3 flex-wrap"
                prop.children [
                    Html.span [ prop.className "text-xs text-gray-600"; prop.text msgs.DefaultRoute ]
                    picker None (defaultRule |> Option.map _.EntryLabel)
                ]
            ]
            Html.div [
                prop.className "text-xs font-medium text-gray-600 mb-1"
                prop.text msgs.ContextOverrides
            ]
            if List.isEmpty overrides then
                Html.p [ prop.className "text-xs text-gray-400 mb-2"; prop.text msgs.NoOverrides ]
            else
                Html.div [
                    prop.children [
                        for rule in overrides ->
                            Html.div [
                                prop.className "flex items-center gap-2 mb-2 flex-wrap"
                                prop.children [
                                    Html.span [
                                        prop.className "text-xs font-mono break-all"
                                        prop.text (rule.Context |> Option.defaultValue "")
                                    ]
                                    picker rule.Context (Some rule.EntryLabel)
                                    Html.button [
                                        prop.className "text-xs text-red-600 hover:underline"
                                        prop.text msgs.RemoveOverride
                                        prop.onClick (fun _ -> onClear (surface, rule.Context))
                                    ]
                                ]
                            ]
                    ]
                ]
            Html.div [
                prop.className "flex items-center gap-2 flex-wrap"
                prop.children [
                    Html.input [
                        prop.className "px-2 py-1 border border-border rounded text-sm"
                        prop.value newContext
                        prop.placeholder msgs.ContextPlaceholder
                        prop.onChange setNewContext
                    ]
                    Html.button [
                        prop.className "text-xs px-2 py-1 border border-border rounded hover:border-brand"
                        prop.disabled (newContext.Trim() = "" || List.isEmpty entries)
                        prop.text msgs.AddOverride
                        prop.onClick (fun _ ->
                            match entries with
                            | [] -> ()
                            | first :: _ ->
                                onSet {
                                    Surface = surface
                                    Context = Some(newContext.Trim())
                                    EntryLabel = first.Label
                                }

                                setNewContext "")
                    ]
                ]
            ]
        ]
    ]

/// The ordered fallback chain. Empty means fail-fast, which is a
/// legitimate cost-control choice rather than an unconfigured state —
/// so the empty case says so instead of nagging.
let private fallbackPane (msgs: ProviderProfileMessages) (model: Model) (dispatch: Msg -> unit) =
    let ordered = model.Profile.Fallback.Ordered

    let unused =
        model.Profile.Entries
        |> List.map _.Label
        |> List.filter (fun l -> not (List.contains l ordered))

    let move (index: int) (delta: int) =
        let target = index + delta

        if target >= 0 && target < List.length ordered then
            ordered
            |> List.mapi (fun i l ->
                if i = index then List.item target ordered
                elif i = target then List.item index ordered
                else l)
            |> SetFallback
            |> dispatch

    Html.div [
        prop.className "bg-white rounded-lg border border-border p-4"
        prop.children [
            Html.h3 [ prop.className "text-sm font-semibold mb-1"; prop.text msgs.FallbackHeading ]
            Html.p [ prop.className "text-xs text-gray-500 mb-3"; prop.text msgs.FallbackHelp ]
            if List.isEmpty ordered then
                Html.p [ prop.className "text-xs text-gray-400 mb-3"; prop.text msgs.NoFallback ]
            else
                Html.div [
                    prop.className "mb-3"
                    prop.children [
                        for index, label in List.indexed ordered do
                            let isFirst = index <= 0
                            let isLast = index >= List.length ordered - 1

                            Html.div [
                                prop.className "flex items-center gap-2 mb-1 flex-wrap"
                                prop.children [
                                    Html.span [ prop.className "text-xs text-gray-400"; prop.text $"{index + 1}." ]
                                    Html.span [ prop.className "text-sm font-mono break-all flex-1"; prop.text label ]
                                    Html.button [
                                        prop.className "text-xs hover:underline"
                                        prop.text msgs.MoveUp
                                        prop.disabled isFirst
                                        prop.onClick (fun _ -> move index (-1))
                                    ]
                                    Html.button [
                                        prop.className "text-xs hover:underline"
                                        prop.text msgs.MoveDown
                                        prop.disabled isLast
                                        prop.onClick (fun _ -> move index 1)
                                    ]
                                    Html.button [
                                        prop.className "text-xs text-red-600 hover:underline"
                                        prop.text msgs.RemoveFromFallback
                                        prop.onClick (fun _ ->
                                            ordered |> List.filter (fun l -> l <> label) |> SetFallback |> dispatch)
                                    ]
                                ]
                            ]
                    ]
                ]
            Html.div [
                prop.className "flex flex-wrap gap-1"
                prop.children [
                    for label in unused ->
                        Html.button [
                            prop.className "text-xs px-2 py-1 border border-border rounded hover:border-brand"
                            prop.text $"{msgs.AddToFallback}: {label}"
                            prop.onClick (fun _ -> dispatch (SetFallback(ordered @ [ label ])))
                        ]
                ]
            ]
        ]
    ]

// ─── View ────────────────────────────────────────────────────────────

/// The whole BYOK surface. Host it wherever the embedding app wants —
/// an admin page, a settings tab, an onboarding step.
[<ReactComponent>]
let ProviderProfilePanel (config: ProviderProfileConfig) (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).ProviderProfile

    let statusText =
        model.Status
        |> Option.map (fun s ->
            match s with
            | "removed" -> msgs.Removed
            | "routing" -> msgs.RoutingSaved
            | "fallback" -> msgs.FallbackSaved
            | _ -> msgs.Saved)

    Html.div [
        prop.className "space-y-4"
        prop.children [
            match model.Error with
            | Some err ->
                banner msgs.Dismiss "bg-red-50 border-red-200 text-red-700" err (fun () -> dispatch DismissError)
            | None -> Html.none

            match statusText with
            | Some text ->
                banner msgs.Dismiss "bg-green-50 border-green-200 text-green-700" text (fun () ->
                    dispatch DismissStatus)
            | None -> Html.none

            Html.div [
                prop.className "bg-white rounded-lg border border-border"
                prop.children [
                    Html.div [
                        prop.className "p-4"
                        prop.children [
                            Html.h3 [ prop.className "text-sm font-semibold mb-1"; prop.text msgs.ProvidersHeading ]
                            Html.p [ prop.className "text-xs text-gray-500"; prop.text msgs.ProvidersHelp ]
                        ]
                    ]
                    if not model.Loaded then
                        Html.p [ prop.className "px-4 pb-4 text-sm text-gray-500"; prop.text msgs.Loading ]
                    elif List.isEmpty model.Profile.Entries then
                        Html.p [ prop.className "px-4 pb-4 text-sm text-gray-600"; prop.text msgs.NoProviders ]
                    else
                        Html.div [
                            prop.children [
                                for entry in model.Profile.Entries ->
                                    entryRow msgs entry (fun () -> dispatch (BeginEdit entry.Label)) (fun () ->
                                        dispatch (Remove entry.Label))
                            ]
                        ]
                    Html.div [
                        prop.className "px-4 py-3 border-t border-border"
                        prop.children [
                            Html.button [
                                prop.className "text-xs px-2 py-1 border border-border rounded hover:border-brand"
                                prop.text msgs.AddProvider
                                prop.onClick (fun _ -> dispatch BeginAdd)
                            ]
                        ]
                    ]
                ]
            ]

            match model.Editing with
            | Some seed ->
                Html.div [
                    // Remount the form when the edited entry changes, so
                    // its React-local seed is re-read rather than kept.
                    prop.key (seed.Original |> Option.defaultValue "<new>")
                    prop.children [
                        EditorForm
                            seed
                            config.Verify.IsSome
                            model.Verification
                            model.Busy
                            (Verify >> dispatch)
                            (Save >> dispatch)
                            (fun () -> dispatch CancelEdit)
                    ]
                ]
            | None -> Html.none

            Html.div [
                prop.className "bg-white rounded-lg border border-border"
                prop.children [
                    Html.div [
                        prop.className "p-4"
                        prop.children [
                            Html.h3 [ prop.className "text-sm font-semibold mb-1"; prop.text msgs.RoutingHeading ]
                            Html.p [ prop.className "text-xs text-gray-500"; prop.text msgs.RoutingHelp ]
                        ]
                    ]
                    if List.isEmpty config.Surfaces then
                        Html.p [ prop.className "px-4 pb-4 text-sm text-gray-600"; prop.text msgs.NoSurfaces ]
                    else
                        Html.div [
                            prop.children [
                                for surface in config.Surfaces ->
                                    SurfaceRouting
                                        surface
                                        model.Profile.Entries
                                        model.Profile.Routing
                                        (SetRoute >> dispatch)
                                        (ClearRoute >> dispatch)
                            ]
                        ]
                ]
            ]

            fallbackPane msgs model dispatch
        ]
    ]

/// Plain `view` alias for a host threading the component through its
/// own Elmish tree — the `init` / `update` / `view` triple the module
/// convention expects.
let view (config: ProviderProfileConfig) (model: Model) (dispatch: Msg -> unit) =
    ProviderProfilePanel config model dispatch
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.NarrativePromptAdminUI

open System
open ToolUp.Elmish
open Feliz
open ToolUp.Platform
open ToolUp.AI

// ─── Phase 1111 — a report's wording, edited in the platform ─────────
//
// A view over `NarrativePromptApi` for an Owner or Admin of the active team:
// the team's grounded narratives; for one, its declared wording beside the
// wording in force, an editor that saves a new version, the earlier
// versions, a reset to the declaration, and "Regenerate with this prompt",
// which runs the narrative now through the job scheduler and shows the run's
// outcome when it lands. The grounding rules the wording sits inside are
// shown read-only — nothing here can change them, and a report that breaks
// them is refused whatever the wording says.
//
// **Off by default.** The API is mounted only by the server's
// `NarrativePromptHandler.withPromptAdmin`, so the module is not appended by
// any SDK default: a deployment that composes the server half adds `create`
// to its module list and `adminTiles` to
// `ClientConfig.Handlers.AdminTileContributors` (docs/migrations/1111).

// ─── Pure decisions ─────────────────────────────────────────────────

/// How many times a regeneration polls for its run before it stops waiting.
let maxPolls = 60

/// Delay between polls, in milliseconds. A report is written over minutes,
/// so the wait is up to five.
let pollIntervalMs = 5000

/// Whether the narrative has a run newer than the one recorded when the
/// regeneration was asked for.
let runLanded (previous: DateTime option) (view: NarrativePromptView) : bool =
    match view.LastRun, previous with
    | Some run, Some before -> run.OccurredAt > before
    | Some _, None -> true
    | None, _ -> false

/// The guidance a save carries: only the sections whose text differs from
/// the declaration (a section left as declared keeps the declared guidance).
let changedGuidance
    (declared: NarrativeDeclaredSection list)
    (edited: Map<string, string>)
    : NarrativeSectionGuidance list =
    declared
    |> List.choose (fun section ->
        match edited.TryFind section.SectionId with
        | Some text when text.Trim() <> "" && text.Trim() <> section.Guidance.Trim() ->
            Some {
                SectionId = section.SectionId
                Guidance = text.Trim()
            }
        | _ -> None)

/// The wording an editor opens on: the version in force, else the
/// declaration.
let wordingInForce (view: NarrativePromptView) : string * Map<string, string> =
    let declared =
        view.DeclaredSections
        |> List.map (fun s -> s.SectionId, s.Guidance)
        |> Map.ofList

    match view.Current with
    | Some version ->
        version.Prompt,
        version.Guidance
        |> List.fold (fun acc g -> Map.add g.SectionId g.Guidance acc) declared
    | None -> view.DeclaredPrompt, declared

// ─── Model ──────────────────────────────────────────────────────────

type LoadState<'T> =
    | Loading
    | Loaded of 'T
    | LoadError of string

/// Where a regeneration asked for on the open narrative stands.
[<RequireQualifiedAccess>]
type RegenerateProgress =
    | Confirming
    | Starting
    /// Accepted; polling for a run newer than `previous`.
    | Awaiting of previous: DateTime option * polls: int
    | Landed of NarrativeLastRun
    | StillRunning
    | Refused of string

type Model = {
    Narratives: LoadState<NarrativePromptSummary list>
    /// The open narrative, if any.
    Open: LoadState<NarrativePromptView> option
    Versions: NarrativePromptVersion list
    Regenerate: RegenerateProgress option
    Busy: bool
    Banner: Result<string, string> option
}

type Msg =
    | Refresh
    | NarrativesLoaded of Result<NarrativePromptSummary list, string>
    | OpenNarrative of runKey: string
    | NarrativeLoaded of Result<NarrativePromptView, string>
    | VersionsLoaded of Result<NarrativePromptVersion list, string>
    | Back
    | Save of SaveNarrativePromptRequest
    | Reset of runKey: string
    | Changed of what: string * Result<NarrativePromptView, string>
    | RegenerateRequested
    | RegenerateCancelled
    | RegenerateConfirmed of runKey: string
    | RegenerateAccepted of Result<Guid, string>
    | Poll of runKey: string
    | Polled of Result<NarrativePromptView, string>
    | DismissBanner

// Header freshness is the CsrfClient request-guard's job — see
// UserSession.fs + SDK.Client.fs installRequestGuard.
let private api =
    Api.makeProxy<NarrativePromptApi> (customOptions = UserSession.withRequestHeaders)

let private failure (e: exn) = Error e.Message

let private loadList () =
    Cmd.OfRemoting.call api.ListNarratives () NarrativesLoaded (failure >> NarrativesLoaded)

let private loadVersions (runKey: string) =
    Cmd.OfRemoting.call api.ListVersions runKey VersionsLoaded (failure >> VersionsLoaded)

let private pollCmd (runKey: string) : Cmd<Msg> =
    Cmd.OfAsync.perform (fun () -> Async.Sleep pollIntervalMs) () (fun _ -> Poll runKey)

let init () : Model * Cmd<Msg> =
    {
        Narratives = Loading
        Open = None
        Versions = []
        Regenerate = None
        Busy = false
        Banner = None
    },
    loadList ()

let private openKey (model: Model) : string option =
    match model.Open with
    | Some(Loaded view) -> Some view.RunKey
    | _ -> None

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | Refresh -> { model with Narratives = Loading }, loadList ()
    | NarrativesLoaded(Ok narratives) ->
        {
            model with
                Narratives = Loaded narratives
        },
        Cmd.none
    | NarrativesLoaded(Error reason) ->
        {
            model with
                Narratives = LoadError reason
        },
        Cmd.none

    | OpenNarrative runKey ->
        {
            model with
                Open = Some Loading
                Versions = []
                Regenerate = None
                Banner = None
        },
        Cmd.batch [
            Cmd.OfRemoting.call api.GetNarrative runKey NarrativeLoaded (failure >> NarrativeLoaded)
            loadVersions runKey
        ]
    | NarrativeLoaded(Ok view) -> { model with Open = Some(Loaded view) }, Cmd.none
    | NarrativeLoaded(Error reason) ->
        {
            model with
                Open = Some(LoadError reason)
        },
        Cmd.none
    | VersionsLoaded(Ok versions) -> { model with Versions = versions }, Cmd.none
    | VersionsLoaded(Error reason) ->
        {
            model with
                Banner = Some(Error reason)
        },
        Cmd.none

    | Back ->
        {
            model with
                Open = None
                Versions = []
                Regenerate = None
                Banner = None
        },
        loadList ()

    | Save request ->
        {
            model with
                Busy = true
                Banner = None
        },
        Cmd.OfRemoting.call api.SavePrompt request (fun r -> Changed("Saved a new version.", r)) (fun e ->
            Changed("", Error e.Message))
    | Reset runKey ->
        {
            model with
                Busy = true
                Banner = None
        },
        Cmd.OfRemoting.call
            api.ResetPrompt
            runKey
            (fun r -> Changed("The declared wording is back in force.", r))
            (fun e -> Changed("", Error e.Message))
    | Changed(what, Ok view) ->
        {
            model with
                Busy = false
                Open = Some(Loaded view)
                Banner = Some(Ok what)
        },
        loadVersions view.RunKey
    | Changed(_, Error reason) ->
        {
            model with
                Busy = false
                Banner = Some(Error reason)
        },
        Cmd.none

    | RegenerateRequested ->
        match openKey model with
        | Some _ ->
            {
                model with
                    Regenerate = Some RegenerateProgress.Confirming
            },
            Cmd.none
        | None -> model, Cmd.none
    | RegenerateCancelled -> { model with Regenerate = None }, Cmd.none
    | RegenerateConfirmed runKey ->
        match model.Regenerate, openKey model with
        | Some RegenerateProgress.Confirming, Some key when key = runKey ->
            {
                model with
                    Regenerate = Some RegenerateProgress.Starting
            },
            Cmd.OfRemoting.call api.Regenerate runKey RegenerateAccepted (failure >> RegenerateAccepted)
        | _ -> { model with Regenerate = None }, Cmd.none
    | RegenerateAccepted(Ok _) ->
        match model.Open, openKey model with
        | Some(Loaded view), Some key ->
            let previous = view.LastRun |> Option.map _.OccurredAt

            {
                model with
                    Regenerate = Some(RegenerateProgress.Awaiting(previous, 0))
            },
            pollCmd key
        | _ -> { model with Regenerate = None }, Cmd.none
    | RegenerateAccepted(Error reason) ->
        {
            model with
                Regenerate = Some(RegenerateProgress.Refused reason)
        },
        Cmd.none
    | Poll runKey ->
        match model.Regenerate, openKey model with
        | Some(RegenerateProgress.Awaiting _), Some key when key = runKey ->
            model, Cmd.OfRemoting.call api.GetNarrative runKey Polled (failure >> Polled)
        | _ -> model, Cmd.none
    | Polled(Ok view) ->
        match model.Regenerate with
        | Some(RegenerateProgress.Awaiting(previous, _)) when runLanded previous view ->
            {
                model with
                    Open = Some(Loaded view)
                    Regenerate = view.LastRun |> Option.map RegenerateProgress.Landed
            },
            Cmd.none
        | Some(RegenerateProgress.Awaiting(_, polls)) when polls + 1 >= maxPolls ->
            {
                model with
                    Regenerate = Some RegenerateProgress.StillRunning
            },
            Cmd.none
        | Some(RegenerateProgress.Awaiting(previous, polls)) ->
            {
                model with
                    Regenerate = Some(RegenerateProgress.Awaiting(previous, polls + 1))
            },
            pollCmd view.RunKey
        | _ -> model, Cmd.none
    | Polled(Error reason) ->
        match model.Regenerate with
        | Some(RegenerateProgress.Awaiting _) ->
            {
                model with
                    Banner = Some(Error reason)
            },
            Cmd.none
        | _ -> model, Cmd.none
    | DismissBanner -> { model with Banner = None }, Cmd.none

// ─── View ───────────────────────────────────────────────────────────

let private formatTime (at: DateTime) = at.ToString "yyyy-MM-dd HH:mm"

let private versionLabel (version: int) =
    if version = 0 then
        "declared wording"
    else
        sprintf "version %d" version

let private banner (model: Model) (dispatch: Msg -> unit) =
    match model.Banner with
    | None -> Html.none
    | Some result ->
        let text, tone =
            match result with
            | Ok text -> text, "bg-green-50 border-green-200 text-green-800"
            | Error text -> text, "bg-red-50 border-red-200 text-red-800"

        Html.div [
            prop.className $"mb-4 p-3 border rounded text-sm flex items-start justify-between gap-3 {tone}"
            prop.children [
                Html.span [ prop.text text ]
                Html.button [
                    prop.className "text-xs underline"
                    prop.text "Dismiss"
                    prop.onClick (fun _ -> dispatch DismissBanner)
                ]
            ]
        ]

let private listView (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between mb-4"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h2 [ prop.className "text-lg font-semibold"; prop.text "Report wording" ]
                            Html.p [
                                prop.className "text-xs text-gray-500"
                                prop.text
                                    "The instruction and section guidance of each AI-written report, for this team. The grounding rules are fixed."
                            ]
                        ]
                    ]
                    Html.button [
                        prop.className "px-3 py-1.5 text-sm border rounded border-gray-300"
                        prop.text "Refresh"
                        prop.onClick (fun _ -> dispatch Refresh)
                    ]
                ]
            ]
            match model.Narratives with
            | Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text "Loading…" ]
            | LoadError reason -> Html.p [ prop.className "text-sm text-red-700"; prop.text reason ]
            | Loaded [] ->
                Html.p [
                    prop.className "text-sm text-gray-500"
                    prop.text "This deployment declares no AI-written reports."
                ]
            | Loaded narratives ->
                Html.ul [
                    prop.className "divide-y border rounded"
                    prop.children [
                        for n in narratives do
                            Html.li [
                                prop.key n.RunKey
                                prop.className "p-3 flex items-center justify-between"
                                prop.children [
                                    Html.div [
                                        prop.children [
                                            Html.p [ prop.className "font-medium text-sm"; prop.text n.DisplayName ]
                                            Html.p [
                                                prop.className "text-xs text-gray-500"
                                                prop.text $"{n.RunKey} · {versionLabel n.CurrentVersion}"
                                            ]
                                        ]
                                    ]
                                    Html.button [
                                        prop.className "px-3 py-1.5 text-sm border rounded border-gray-300"
                                        prop.text "Open"
                                        prop.onClick (fun _ -> dispatch (OpenNarrative n.RunKey))
                                    ]
                                ]
                            ]
                    ]
                ]
        ]
    ]

/// The editor holds its text in React state and dispatches only on Save.
/// Keyed by the version in force, so a save or reset re-opens it on the new
/// wording.
[<ReactComponent>]
let private WordingEditor (view: NarrativePromptView) (busy: bool) (dispatch: Msg -> unit) =
    let initialPrompt, initialGuidance = wordingInForce view
    let prompt, setPrompt = React.useState initialPrompt
    let guidance, setGuidance = React.useState initialGuidance

    let canSave = not busy && prompt.Trim() <> ""

    Html.div [
        prop.className "border rounded p-4 mb-4"
        prop.children [
            Html.h3 [ prop.className "font-medium mb-2"; prop.text "Wording in force" ]
            Html.label [
                prop.className "block text-sm font-medium text-gray-700 mb-1"
                prop.text "Instruction"
            ]
            Html.textarea [
                prop.className "w-full border border-gray-300 rounded px-3 py-2 text-sm mb-1"
                prop.rows 4
                prop.maxLength NarrativePromptLimits.MaxPromptLength
                prop.value prompt
                prop.onChange setPrompt
                prop.disabled busy
            ]
            Html.p [
                prop.className "text-xs text-gray-500 mb-3"
                prop.text $"Declared: {view.DeclaredPrompt}"
            ]
            for section in view.DeclaredSections do
                Html.div [
                    prop.key section.SectionId
                    prop.className "mb-3"
                    prop.children [
                        Html.label [
                            prop.className "block text-sm font-medium text-gray-700 mb-1"
                            prop.text $"{section.Heading} ({section.SectionId})"
                        ]
                        Html.textarea [
                            prop.className "w-full border border-gray-300 rounded px-3 py-2 text-sm mb-1"
                            prop.rows 2
                            prop.maxLength NarrativePromptLimits.MaxGuidanceLength
                            prop.value (guidance.TryFind section.SectionId |> Option.defaultValue section.Guidance)
                            prop.onChange (fun (text: string) -> setGuidance (Map.add section.SectionId text guidance))
                            prop.disabled busy
                        ]
                        Html.p [
                            prop.className "text-xs text-gray-500"
                            prop.text $"Declared: {section.Guidance}"
                        ]
                    ]
                ]
            Html.div [
                prop.className "flex gap-2"
                prop.children [
                    Html.button [
                        prop.className "px-3 py-1.5 text-sm rounded bg-violet-500 text-white disabled:opacity-40"
                        prop.disabled (not canSave)
                        prop.text "Save as a new version"
                        prop.onClick (fun _ ->
                            if canSave then
                                dispatch (
                                    Save {
                                        RunKey = view.RunKey
                                        Prompt = prompt.Trim()
                                        Guidance = changedGuidance view.DeclaredSections guidance
                                        BasedOnVersion = view.LatestVersion
                                    }
                                ))
                    ]
                    Html.button [
                        prop.className "px-3 py-1.5 text-sm border rounded border-gray-300 disabled:opacity-40"
                        prop.disabled (busy || view.Current.IsNone)
                        prop.text "Reset to the declared wording"
                        prop.onClick (fun _ -> dispatch (Reset view.RunKey))
                    ]
                ]
            ]
        ]
    ]

let private regenerateView (model: Model) (view: NarrativePromptView) (dispatch: Msg -> unit) =
    let button =
        Html.button [
            prop.className "px-3 py-1.5 text-sm border rounded border-gray-300"
            prop.text "Regenerate with this prompt"
            prop.onClick (fun _ -> dispatch RegenerateRequested)
        ]

    let outcome (run: NarrativeLastRun) =
        Html.p [
            prop.className "text-sm"
            prop.text (
                sprintf
                    "%s at %s (%s, %s)%s"
                    run.Outcome
                    (formatTime run.OccurredAt)
                    run.Trigger
                    (versionLabel run.PromptVersion)
                    (run.Detail |> Option.map (sprintf ": %s") |> Option.defaultValue "")
            )
        ]

    Html.div [
        prop.className "border rounded p-4 mb-4"
        prop.children [
            Html.h3 [ prop.className "font-medium mb-2"; prop.text "Last run" ]
            match view.LastRun with
            | Some run -> outcome run
            | None -> Html.p [ prop.className "text-sm text-gray-500"; prop.text "No run in this team yet." ]
            Html.div [
                prop.className "mt-3"
                prop.children [
                    match model.Regenerate with
                    | None -> button
                    | Some RegenerateProgress.Confirming ->
                        Html.div [
                            prop.className "flex items-center gap-2 text-sm"
                            prop.children [
                                Html.span [
                                    prop.text
                                        "Run this report now with the wording in force? It is published, or refused, exactly as a scheduled run."
                                ]
                                Html.button [
                                    prop.className "px-3 py-1.5 rounded bg-violet-500 text-white"
                                    prop.text "Run now"
                                    prop.onClick (fun _ -> dispatch (RegenerateConfirmed view.RunKey))
                                ]
                                Html.button [
                                    prop.className "px-3 py-1.5 border rounded border-gray-300"
                                    prop.text "Cancel"
                                    prop.onClick (fun _ -> dispatch RegenerateCancelled)
                                ]
                            ]
                        ]
                    | Some RegenerateProgress.Starting ->
                        Html.p [ prop.className "text-sm text-gray-500"; prop.text "Starting…" ]
                    | Some(RegenerateProgress.Awaiting _) ->
                        Html.p [
                            prop.className "text-sm text-gray-500"
                            prop.text "Running — a report takes a few minutes to write."
                        ]
                    | Some(RegenerateProgress.Landed run) -> Html.div [ prop.children [ outcome run; button ] ]
                    | Some RegenerateProgress.StillRunning ->
                        Html.div [
                            prop.children [
                                Html.p [
                                    prop.className "text-sm text-gray-500"
                                    prop.text
                                        "Still running. Its result will appear here, and a run that fails for good is listed under Background jobs."
                                ]
                                button
                            ]
                        ]
                    | Some(RegenerateProgress.Refused reason) ->
                        Html.div [
                            prop.children [ Html.p [ prop.className "text-sm text-red-700"; prop.text reason ]; button ]
                        ]
                ]
            ]
        ]
    ]

let private versionsView (versions: NarrativePromptVersion list) =
    Html.div [
        prop.className "border rounded p-4 mb-4"
        prop.children [
            Html.h3 [ prop.className "font-medium mb-2"; prop.text "Earlier versions" ]
            if versions.IsEmpty then
                Html.p [
                    prop.className "text-sm text-gray-500"
                    prop.text "None saved; the declared wording is in force."
                ]
            else
                Html.ul [
                    prop.className "divide-y"
                    prop.children [
                        for v in versions do
                            Html.li [
                                prop.key v.Version
                                prop.className "py-2 text-sm"
                                prop.children [
                                    Html.p [
                                        prop.className "text-xs text-gray-500"
                                        prop.text (
                                            sprintf
                                                "Version %d · %s · %s%s"
                                                v.Version
                                                v.Author
                                                (formatTime v.SavedAt)
                                                (if v.IsReset then " · reset to the declared wording" else "")
                                        )
                                    ]
                                    if not v.IsReset then
                                        Html.p [ prop.className "whitespace-pre-wrap"; prop.text v.Prompt ]

                                        for g in v.Guidance do
                                            Html.p [
                                                prop.className "text-xs whitespace-pre-wrap"
                                                prop.text $"{g.SectionId}: {g.Guidance}"
                                            ]
                                ]
                            ]
                    ]
                ]
        ]
    ]

let private detailView (model: Model) (view: NarrativePromptView) (dispatch: Msg -> unit) =
    Html.div [
        prop.children [
            Html.button [
                prop.className "text-sm underline mb-3"
                prop.text "← All reports"
                prop.onClick (fun _ -> dispatch Back)
            ]
            Html.h2 [ prop.className "text-lg font-semibold"; prop.text view.DisplayName ]
            Html.p [
                prop.className "text-xs text-gray-500 mb-4"
                prop.text
                    $"{view.Title} · {view.RunKey} · {versionLabel (view.Current |> Option.map _.Version |> Option.defaultValue 0)} in force"
            ]
            banner model dispatch
            Html.div [
                prop.key (string view.LatestVersion)
                prop.children [ WordingEditor view model.Busy dispatch ]
            ]
            Html.div [
                prop.className "border rounded p-4 mb-4 bg-gray-50"
                prop.children [
                    Html.h3 [ prop.className "font-medium mb-1"; prop.text "Grounding rules (fixed)" ]
                    Html.p [
                        prop.className "text-xs text-gray-500 mb-2"
                        prop.text
                            "The model receives these after the wording, on every run. They cannot be edited here, and a report that breaks one is refused, never published."
                    ]
                    Html.pre [ prop.className "text-xs whitespace-pre-wrap"; prop.text view.Rules ]
                ]
            ]
            regenerateView model view dispatch
            versionsView model.Versions
        ]
    ]

[<ReactComponent>]
let private NarrativePromptBody (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex-1 p-6 overflow-y-auto"
        prop.children [
            match model.Open with
            | None -> listView model dispatch
            | Some Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text "Loading…" ]
            | Some(LoadError reason) ->
                Html.div [
                    prop.children [
                        Html.button [
                            prop.className "text-sm underline mb-3"
                            prop.text "← All reports"
                            prop.onClick (fun _ -> dispatch Back)
                        ]
                        Html.p [ prop.className "text-sm text-red-700"; prop.text reason ]
                    ]
                ]
            | Some(Loaded view) -> detailView model view dispatch
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    Html.div [
        prop.className "flex flex-col h-full"
        prop.children [ NarrativePromptBody model dispatch ]
    ]

// ─── Module creation ────────────────────────────────────────────────

/// This module's id.
let moduleId = "_ai.NarrativePrompts"

/// The module: Team Management, visible to a team Owner or Admin. Add it to
/// the module list of a deployment whose server composed
/// `NarrativePromptHandler.withPromptAdmin`.
let create () : ErasedModule =
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = "Report wording"
        Icon = ToolUp.AI.Icons.aiSettings
    }
    |> ToolUp.Platform.ClientModule.withId moduleId
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Team Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register

/// The "Report wording" administration tile, through
/// `ClientConfig.Handlers.AdminTileContributors`. It is shown only to a
/// caller who can see the module.
let adminTiles: IAdminTileContributor =
    { new IAdminTileContributor with
        member _.Tiles() = [
            {
                OwnerModuleId = moduleId
                Widget = {
                    Id = "_ai.tile.narrative-prompts"
                    Title = "Report wording"
                    Icon = ToolUp.AI.Icons.aiSettings
                    Weight = 26
                    Body =
                        AdminTileBody.summary
                            "_ai.admin.narrative-prompts"
                            "The instruction and section guidance of each AI-written report, with earlier versions, a reset, and a regenerate."
                }
            }
        ]
    }
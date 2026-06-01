// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module KnowledgeBaseView

open Feliz
open Elmish
open ToolUp.Platform
open ClientModel
open SharedTypes

// Status / source / file-type badges live in `KnowledgeListView.Badges`
// — referenced here as `KnowledgeListView.Badges.statusBadge` etc. so the
// Documents page, Notes page, Platform Library page, and the
// Platform Admin module all render identically.

// ─── Upload zone ──────────────────────────────────────────────────

[<ReactComponent>]
let private UploadZone (model: Model) (dispatch: Msg -> unit) =
    // `dragActive` is ephemeral UI state — belongs in React, not the
    // Elmish model (per CLAUDE.md's "text inputs use React.useState"
    // rule, generalised: any state that's purely visual feedback for
    // an in-progress interaction stays local to the component).
    let dragActive, setDragActive = React.useState false

    let processFile (file: Browser.Types.File) =
        let reader = Browser.Dom.FileReader.Create()

        reader.onload <-
            fun _ ->
                let buf = reader.result :?> Fable.Core.JS.ArrayBuffer
                let uint8 = Fable.Core.JS.Constructors.Uint8Array.Create(buf)
                let bytes = Array.init (int uint8.length) (fun i -> byte uint8[i])
                dispatch (UploadRequested(bytes, file.name))

        reader.readAsArrayBuffer file

    Html.div [
        prop.className [
            "border-2 border-dashed rounded-lg p-8 text-center transition-colors"
            if dragActive && not model.Uploading then
                "border-blue-500 bg-blue-50"
            else
                "border-gray-300 hover:border-blue-400"
        ]
        // `preventDefault` on dragenter / dragover is mandatory: without it
        // the browser's default behaviour is to open the dragged file in
        // the tab, which navigates away and discards the drop entirely.
        prop.onDragEnter (fun ev ->
            ev.preventDefault ()

            if not model.Uploading then
                setDragActive true)
        prop.onDragOver (_.preventDefault())
        prop.onDragLeave (fun _ -> setDragActive false)
        prop.onDrop (fun ev ->
            ev.preventDefault ()
            setDragActive false

            if not model.Uploading then
                let files = ev.dataTransfer.files

                for i in 0 .. (int files.length) - 1 do
                    processFile files[i])
        prop.children [
            Html.p [
                prop.className "text-sm font-medium text-gray-700 mb-1"
                prop.text (
                    if dragActive && not model.Uploading then
                        "Drop files to upload"
                    else
                        "Upload documents to your knowledge base"
                )
            ]
            Html.p [
                prop.className "text-xs text-gray-500 mb-4"
                prop.text "PDF, PPTX, DOCX, XLSX, CSV, TXT  ·  drop files here or click to choose"
            ]
            Html.label [
                prop.className
                    "inline-flex items-center px-4 py-2 bg-blue-600 text-white text-sm font-medium rounded-md hover:bg-blue-700 cursor-pointer"
                prop.children [
                    Html.span [ prop.text (if model.Uploading then "Uploading…" else "Choose files") ]
                    Html.input [
                        prop.type' "file"
                        prop.className "hidden"
                        prop.accept ".pdf,.pptx,.docx,.xlsx,.csv,.txt"
                        prop.multiple true
                        prop.disabled model.Uploading
                        prop.onClick (fun ev ->
                            let input = ev.target :?> Browser.Types.HTMLInputElement
                            input.value <- "")
                        prop.onChange (fun (files: Browser.Types.File list) ->
                            for file in files do
                                processFile file)
                    ]
                ]
            ]
        ]
    ]

// ─── Error banner ─────────────────────────────────────────────────

let private errorBanner (model: Model) (dispatch: Msg -> unit) : ReactElement =
    match model.UploadError with
    | None -> Html.none
    | Some err ->
        Html.div [
            prop.className "bg-red-50 border border-red-200 rounded-md px-4 py-3 flex items-start gap-3"
            prop.children [
                Html.p [ prop.className "text-sm text-red-700 flex-1"; prop.text err ]
                Html.button [
                    prop.className "text-red-400 hover:text-red-600 text-lg"
                    prop.text "×"
                    prop.onClick (fun _ -> dispatch DismissError)
                ]
            ]
        ]

// ─── Team Documents list ──────────────────────────────────────────
//
// Uses the shared `KnowledgeListView` component for filtering / grouping
// and uniform badge rendering. Notes are filtered out at the source
// because they have their own dedicated page (the Documents list shows
// uploads + narratives only).

let private deleteRowAction (dispatch: Msg -> unit) (doc: KnowledgeDocument) : ReactElement =
    Html.button [
        prop.className "text-xs text-red-600 hover:text-red-800 font-medium"
        prop.text "Delete"
        prop.onClick (fun _ -> dispatch (DeleteRequested doc.Id))
    ]

let private documentList (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let nonNote =
        model.Documents
        |> List.filter (fun d ->
            match d.Source with
            | Note _ -> false
            | _ -> true)

    let config: KnowledgeListView.KnowledgeListConfig = {
        EmptyStateText = "No documents uploaded yet. Drop files into the upload zone above to get started."
        RowAction = Some(deleteRowAction dispatch)
        InstanceKey = "team-documents"
    }

    KnowledgeListView.KnowledgeListView config nonNote

// ─── Main view ────────────────────────────────────────────────────

/// Hosts the KB main panel inside a React component so we can subscribe
/// to the notification stream on mount. Server-side `IngestNarrative`
/// publishes `DataRefreshed("KnowledgeBase", _)` after every commit; we
/// react by re-fetching the document list so the new entry appears
/// without requiring the user to click Refresh.
///
/// Re-fetch on every mount as well: a Save-to-KB click on another module
/// fires the notification while this component is unmounted (subscription
/// disposed). The Elmish model state survives in `ModuleStates`, so `init`
/// doesn't re-run on navigation back — without a mount-time refresh the
/// user would see a stale list.
[<ReactComponent>]
let private MainPanel (model: Model) (dispatch: Msg -> unit) =
    React.useEffectOnce (fun () ->
        dispatch (LoadDocuments(Start()))

        let dispose =
            NotificationClient.subscribe (fun envelope ->
                match envelope.Notification with
                | Notification.DataRefreshed("KnowledgeBase", _) -> dispatch (LoadDocuments(Start()))
                | _ -> ())

        FsReact.createDisposable (fun () -> dispose ()))

    Html.div [
        prop.className "p-6 space-y-6"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h1 [
                                prop.className "text-xl font-semibold text-gray-900"
                                prop.text "Knowledge Base"
                            ]
                            Html.p [
                                prop.className "text-sm text-gray-500 mt-1"
                                prop.text "Upload documents to make them searchable by the AI assistant"
                            ]
                        ]
                    ]
                    Html.div [
                        prop.className "flex items-center gap-3"
                        prop.children [
                            Html.button [
                                prop.className "text-sm text-gray-500 hover:text-gray-700"
                                prop.text "Reload"
                                prop.title "Reload the document list from the server"
                                prop.onClick (fun _ -> dispatch (LoadDocuments(Start())))
                            ]
                            Html.button [
                                prop.className
                                    "text-sm font-medium text-violet-600 hover:text-violet-800 disabled:text-gray-300 disabled:cursor-not-allowed"
                                prop.disabled model.RefreshingAIContext
                                prop.title
                                    "Push the current document inventory to the AI assistant. Use after deleting or editing documents to ensure subsequent AI replies reflect only the current set."
                                prop.text (
                                    if model.RefreshingAIContext then
                                        "Syncing…"
                                    else
                                        "Refresh AI context"
                                )
                                prop.onClick (fun _ -> dispatch RefreshAIContextRequested)
                            ]
                            Html.button [
                                prop.className
                                    "text-sm font-medium text-red-600 hover:text-red-800 disabled:text-gray-300 disabled:cursor-not-allowed"
                                prop.disabled (model.Resetting || model.Documents.IsEmpty)
                                prop.text (if model.Resetting then "Resetting…" else "Reset index")
                                prop.title
                                    "Wipe every document, status, and embedded chunk in this scope. This cannot be undone."
                                prop.onClick (fun _ ->
                                    let scopeLabel =
                                        if model.Documents.IsEmpty then
                                            ""
                                        else
                                            sprintf
                                                " %d document%s will be permanently removed."
                                                model.Documents.Length
                                                (if model.Documents.Length = 1 then "" else "s")

                                    let prompt =
                                        "Reset the knowledge base?"
                                        + scopeLabel
                                        + " Other team members will lose access to these documents too."

                                    if Browser.Dom.window.confirm prompt then
                                        dispatch ResetIndexRequested)
                            ]
                        ]
                    ]
                ]
            ]
            errorBanner model dispatch
            UploadZone model dispatch
            documentList model dispatch
        ]
    ]

// ─── Platform Library page (read-only view of Platform KB) ────────
//
// Every authenticated user can see the cross-team Platform Knowledge
// Base content from this page — transparency over what reference
// material the AI assistant draws from. Writes (upload / delete /
// promote) live exclusively in the separate Platform Admin module,
// gated server-side on `canModifyPlatformConfig`. Mount-time refresh
// keeps the list current; there is no server-side live-refresh
// notification today (admin uploads are infrequent — a manual Reload
// button covers the gap).

let private platformLibraryErrorBanner (model: Model) (dispatch: Msg -> unit) : ReactElement =
    match model.PlatformDocsLoadError with
    | None -> Html.none
    | Some err ->
        Html.div [
            prop.className "bg-red-50 border border-red-200 rounded-md px-4 py-3 flex items-start gap-3"
            prop.children [
                Html.p [ prop.className "text-sm text-red-700 flex-1"; prop.text err ]
                Html.button [
                    prop.className "text-red-400 hover:text-red-600 text-lg"
                    prop.text "×"
                    prop.onClick (fun _ -> dispatch DismissError)
                ]
            ]
        ]

[<ReactComponent>]
let private PlatformLibraryPanel (model: Model) (dispatch: Msg -> unit) =
    React.useEffectOnce (fun () ->
        dispatch (LoadPlatformDocuments(Start()))

        // Subscribe to platform-wide refresh fan-out so any Platform Admin
        // upload / delete / promote in another session refreshes this view
        // without requiring a manual Reload. Server publishes
        // `DataRefreshed("PlatformKnowledgeBase", _)` on the reserved
        // `_platform` scope and `NotificationHandler.writePlatformEnvelope`
        // forwards the envelope to every connected SSE client.
        let dispose =
            NotificationClient.subscribe (fun envelope ->
                match envelope.Notification with
                | Notification.DataRefreshed("PlatformKnowledgeBase", _) -> dispatch (LoadPlatformDocuments(Start()))
                | _ -> ())

        FsReact.createDisposable (fun () -> dispose ()))

    let config: KnowledgeListView.KnowledgeListConfig = {
        EmptyStateText =
            "No Platform Knowledge Base content yet. Platform Admins can add cross-team reference material from the Platform Admin section."
        RowAction = None
        InstanceKey = "platform-library"
    }

    Html.div [
        prop.className "p-6 space-y-6"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h1 [
                                prop.className "text-xl font-semibold text-gray-900"
                                prop.text "Platform Library"
                            ]
                            Html.p [
                                prop.className "text-sm text-gray-500 mt-1"
                                prop.text
                                    "Cross-team reference material the AI assistant draws from. Read-only — managed by Platform Admins."
                            ]
                        ]
                    ]
                    Html.button [
                        prop.className "text-sm text-gray-500 hover:text-gray-700"
                        prop.disabled model.PlatformDocsLoading
                        prop.text (if model.PlatformDocsLoading then "Loading…" else "Reload")
                        prop.title "Re-fetch the Platform KB content from the server."
                        prop.onClick (fun _ -> dispatch (LoadPlatformDocuments(Start())))
                    ]
                ]
            ]
            platformLibraryErrorBanner model dispatch
            KnowledgeListView.KnowledgeListView config model.PlatformDocuments
        ]
    ]

// ─── Notes page ───────────────────────────────────────────────────

let private notesErrorBanner (model: Model) (dispatch: Msg -> unit) : ReactElement =
    match model.NoteSaveError with
    | None -> Html.none
    | Some err ->
        Html.div [
            prop.className "bg-red-50 border border-red-200 rounded-md px-4 py-3 flex items-start gap-3"
            prop.children [
                Html.p [ prop.className "text-sm text-red-700 flex-1"; prop.text err ]
                Html.button [
                    prop.className "text-red-400 hover:text-red-600 text-lg"
                    prop.text "×"
                    prop.onClick (fun _ -> dispatch DismissError)
                ]
            ]
        ]

/// Inline note editor. Title + body drafts live in `React.useState` —
/// the Elmish model only tracks open/closed. Pre-populated from the
/// existing document when editing.
[<ReactComponent>]
let private NoteEditor
    (target: NoteEditorTarget)
    (existing: KnowledgeDocument option)
    (saving: bool)
    (dispatch: Msg -> unit)
    =
    // Body isn't carried on `KnowledgeDocument` — when editing, the user
    // re-types the body. A future `GetNote` API could pre-fill it.
    let initialTitle =
        match existing with
        | Some doc ->
            match doc.Source with
            | Note src -> src.Title
            | _ -> ""
        | None -> ""

    let titleDraft, setTitleDraft = React.useState initialTitle
    let bodyDraft, setBodyDraft = React.useState ""
    let trimmedTitle = titleDraft.Trim()
    let trimmedBody = bodyDraft.Trim()
    let canSave = not saving && trimmedTitle.Length > 0 && trimmedBody.Length > 0

    let heading =
        match target with
        | CreateNew -> "New note"
        | EditExisting _ -> "Edit note"

    Html.div [
        prop.className "bg-white border border-gray-200 rounded-lg p-5 space-y-4"
        prop.children [
            Html.h3 [ prop.className "text-base font-semibold text-gray-900"; prop.text heading ]
            Html.div [
                prop.className "space-y-1"
                prop.children [
                    Html.label [ prop.className "block text-xs font-medium text-gray-600"; prop.text "Title" ]
                    Html.input [
                        prop.className
                            "w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-400"
                        prop.placeholder "What is this note about?"
                        prop.value titleDraft
                        prop.onChange setTitleDraft
                        prop.disabled saving
                    ]
                ]
            ]
            Html.div [
                prop.className "space-y-1"
                prop.children [
                    Html.label [
                        prop.className "block text-xs font-medium text-gray-600"
                        prop.text "Body (markdown)"
                    ]
                    Html.textarea [
                        prop.className
                            "w-full px-3 py-2 border border-gray-300 rounded-md text-sm font-mono focus:outline-none focus:ring-2 focus:ring-blue-400"
                        prop.rows 12
                        prop.placeholder
                            "Use blank lines between paragraphs — each paragraph becomes one retrievable chunk."
                        prop.value bodyDraft
                        prop.onChange setBodyDraft
                        prop.disabled saving
                    ]
                ]
            ]
            Html.div [
                prop.className "flex items-center justify-end gap-2"
                prop.children [
                    Html.button [
                        prop.className "px-3 py-1.5 text-sm text-gray-600 hover:text-gray-800 disabled:text-gray-300"
                        prop.text "Cancel"
                        prop.disabled saving
                        prop.onClick (fun _ -> dispatch CloseNoteEditor)
                    ]
                    Html.button [
                        prop.className
                            "px-3 py-1.5 bg-blue-600 text-white text-sm font-medium rounded-md hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed"
                        prop.text (if saving then "Saving…" else "Save note")
                        prop.disabled (not canSave)
                        prop.onClick (fun _ -> dispatch (SaveNote(target, trimmedTitle, trimmedBody)))
                    ]
                ]
            ]
        ]
    ]

let private noteRow (doc: KnowledgeDocument) (dispatch: Msg -> unit) : ReactElement =
    let title, author, createdAt, lastEditedAt =
        match doc.Source with
        | Note src -> src.Title, src.Author, src.CreatedAt, src.LastEditedAt
        | _ -> doc.FileName, doc.UploadedBy, doc.UploadedAt, None

    let timestampLine =
        match lastEditedAt with
        | Some t ->
            sprintf "Created %s · edited %s" (createdAt.ToString("yyyy-MM-dd HH:mm")) (t.ToString("yyyy-MM-dd HH:mm"))
        | None -> sprintf "Created %s" (createdAt.ToString("yyyy-MM-dd HH:mm"))

    Html.div [
        prop.key doc.Id
        prop.className "bg-white border border-gray-200 rounded-lg p-4 hover:border-blue-300 transition-colors"
        prop.children [
            Html.div [
                prop.className "flex items-start justify-between gap-4"
                prop.children [
                    Html.div [
                        prop.className "flex-1 min-w-0 space-y-1"
                        prop.children [
                            Html.div [
                                prop.className "flex items-center gap-2"
                                prop.children [
                                    Html.h4 [
                                        prop.className "text-sm font-semibold text-gray-900 truncate"
                                        prop.text title
                                    ]
                                    KnowledgeListView.Badges.statusBadge doc.Status
                                ]
                            ]
                            Html.p [
                                prop.className "text-xs text-gray-500"
                                prop.text (sprintf "by %s · %s" author timestampLine)
                            ]
                        ]
                    ]
                    Html.div [
                        prop.className "flex items-center gap-3 shrink-0"
                        prop.children [
                            Html.button [
                                prop.className "text-xs text-blue-600 hover:text-blue-800 font-medium"
                                prop.text "Edit"
                                prop.onClick (fun _ -> dispatch (OpenNoteEditor(EditExisting doc.Id)))
                            ]
                            Html.button [
                                prop.className "text-xs text-red-600 hover:text-red-800 font-medium"
                                prop.text "Delete"
                                prop.onClick (fun _ ->
                                    if Browser.Dom.window.confirm (sprintf "Delete note \"%s\"?" title) then
                                        dispatch (DeleteRequested doc.Id))
                            ]
                        ]
                    ]
                ]
            ]
        ]
    ]

[<ReactComponent>]
let private NotesPanel (model: Model) (dispatch: Msg -> unit) =
    React.useEffectOnce (fun () ->
        dispatch (LoadDocuments(Start()))

        let dispose =
            NotificationClient.subscribe (fun envelope ->
                match envelope.Notification with
                | Notification.DataRefreshed("KnowledgeBase", _) -> dispatch (LoadDocuments(Start()))
                | _ -> ())

        FsReact.createDisposable (fun () -> dispose ()))

    let notes =
        model.Documents
        |> List.filter (fun d ->
            match d.Source with
            | Note _ -> true
            | _ -> false)
        |> List.sortByDescending (fun d ->
            match d.Source with
            | Note src -> src.LastEditedAt |> Option.defaultValue src.CreatedAt
            | _ -> d.UploadedAt)

    let editorElement =
        match model.NoteEditor with
        | None -> Html.none
        | Some target ->
            let existing =
                match target with
                | CreateNew -> None
                | EditExisting docId -> notes |> List.tryFind (fun d -> d.Id = docId)

            NoteEditor target existing model.SavingNote dispatch

    Html.div [
        prop.className "p-6 space-y-6"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h1 [ prop.className "text-xl font-semibold text-gray-900"; prop.text "Notes" ]
                            Html.p [
                                prop.className "text-sm text-gray-500 mt-1"
                                prop.text
                                    "Free-form team prose — decisions, conventions, context. Each blank-line-separated paragraph becomes a retrievable chunk."
                            ]
                        ]
                    ]
                    Html.button [
                        prop.className
                            "inline-flex items-center px-3 py-1.5 bg-blue-600 text-white text-sm font-medium rounded-md hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed"
                        prop.text "New note"
                        prop.disabled (model.NoteEditor.IsSome)
                        prop.onClick (fun _ -> dispatch (OpenNoteEditor CreateNew))
                    ]
                ]
            ]
            notesErrorBanner model dispatch
            editorElement
            (if notes.IsEmpty && model.NoteEditor.IsNone then
                 Html.div [
                     prop.className "bg-gray-50 border border-dashed border-gray-300 rounded-lg p-8 text-center"
                     prop.children [
                         Html.p [
                             prop.className "text-sm text-gray-600"
                             prop.text "No notes yet. Click \"New note\" to capture team context for the AI assistant."
                         ]
                     ]
                 ]
             else
                 Html.div [
                     prop.className "space-y-3"
                     prop.children (notes |> List.map (fun doc -> noteRow doc dispatch))
                 ])
        ]
    ]

// ─── AI Context page ──────────────────────────────────────────────

let private aiContextErrorBanner (model: Model) (dispatch: Msg -> unit) : ReactElement =
    match model.AIContextSaveError, model.AIContextLoadError with
    | None, None -> Html.none
    | err, _ ->
        let message =
            err |> Option.orElse model.AIContextLoadError |> Option.defaultValue ""

        Html.div [
            prop.className "bg-red-50 border border-red-200 rounded-md px-4 py-3 flex items-start gap-3"
            prop.children [
                Html.p [ prop.className "text-sm text-red-700 flex-1"; prop.text message ]
                Html.button [
                    prop.className "text-red-400 hover:text-red-600 text-lg"
                    prop.text "×"
                    prop.onClick (fun _ -> dispatch DismissError)
                ]
            ]
        ]

[<ReactComponent>]
let private AIContextEditor (initialBody: string) (saving: bool) (dispatch: Msg -> unit) =
    let bodyDraft, setBodyDraft = React.useState initialBody

    Html.div [
        prop.className "bg-white border border-gray-200 rounded-lg p-5 space-y-4"
        prop.children [
            Html.div [
                prop.className "space-y-1"
                prop.children [
                    Html.label [
                        prop.className "block text-xs font-medium text-gray-600"
                        prop.text "Standing context (markdown)"
                    ]
                    Html.textarea [
                        prop.className
                            "w-full px-3 py-2 border border-gray-300 rounded-md text-sm font-mono focus:outline-none focus:ring-2 focus:ring-blue-400"
                        prop.rows 16
                        prop.placeholder
                            "Team mission, naming conventions, response style, constraints… The AI sees this on every message."
                        prop.value bodyDraft
                        prop.onChange setBodyDraft
                        prop.disabled saving
                    ]
                    Html.p [
                        prop.className "text-xs text-gray-500"
                        prop.text "Leave empty and Save to clear the standing context."
                    ]
                ]
            ]
            Html.div [
                prop.className "flex items-center justify-end gap-2"
                prop.children [
                    Html.button [
                        prop.className "px-3 py-1.5 text-sm text-gray-600 hover:text-gray-800 disabled:text-gray-300"
                        prop.text "Cancel"
                        prop.disabled saving
                        prop.onClick (fun _ -> dispatch CloseAIContextEditor)
                    ]
                    Html.button [
                        prop.className
                            "px-3 py-1.5 bg-blue-600 text-white text-sm font-medium rounded-md hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed"
                        prop.text (if saving then "Saving…" else "Save")
                        prop.disabled saving
                        prop.onClick (fun _ -> dispatch (SaveAIContext bodyDraft))
                    ]
                ]
            ]
        ]
    ]

[<ReactComponent>]
let private AIContextPanel (model: Model) (dispatch: Msg -> unit) =
    // Phase 66 Stream B.8 — read resolved subject kind directly;
    // `AnonymousKind` has no persistent scope so the standing-context
    // panel is unavailable. The retiring `getMode ()` call collapsed
    // every authenticated mode into the catch-all branch — same
    // behaviour preserved by treating `UserKind` / `TeamMemberKind` /
    // `ClaimBearerKind` as the "has-context" branch.
    let subjectKind = UserSession.getSubjectKind ()

    let body =
        match subjectKind with
        | AnonymousKind ->
            Html.div [
                prop.className "bg-gray-50 border border-dashed border-gray-300 rounded-lg p-8 text-center"
                prop.children [
                    Html.p [
                        prop.className "text-sm text-gray-600"
                        prop.text "Standing AI context is unavailable in anonymous mode — no persistent scope."
                    ]
                ]
            ]
        | UserKind
        | TeamMemberKind
        | ClaimBearerKind ->
            match model.AIContext with
            | None -> Html.div [ prop.className "text-sm text-gray-500"; prop.text "Loading…" ]
            | Some entryOpt ->
                if model.AIContextEditorOpen then
                    let initialBody = entryOpt |> Option.map _.Body |> Option.defaultValue ""

                    AIContextEditor initialBody model.SavingAIContext dispatch
                else
                    let header, content =
                        match entryOpt with
                        | None ->
                            Html.p [
                                prop.className "text-xs text-gray-500"
                                prop.text "No standing context written yet."
                            ],
                            Html.div [
                                prop.className
                                    "bg-gray-50 border border-dashed border-gray-300 rounded-lg p-8 text-center"
                                prop.children [
                                    Html.p [
                                        prop.className "text-sm text-gray-600"
                                        prop.text "The AI assistant has no team-curated standing context yet."
                                    ]
                                ]
                            ]
                        | Some entry ->
                            Html.p [
                                prop.className "text-xs text-gray-500"
                                prop.text (
                                    sprintf
                                        "Last updated %s by %s"
                                        (entry.UpdatedAt.ToString("yyyy-MM-dd HH:mm"))
                                        entry.UpdatedBy
                                )
                            ],
                            Html.pre [
                                prop.className
                                    "bg-white border border-gray-200 rounded-md p-4 text-sm font-mono whitespace-pre-wrap text-gray-800"
                                prop.text entry.Body
                            ]

                    Html.div [ prop.className "space-y-3"; prop.children [ header; content ] ]

    let editButton =
        match subjectKind, model.AIContext, model.AIContextEditorOpen with
        | AnonymousKind, _, _
        | _, None, _
        | _, _, true -> Html.none
        | _, Some _, false ->
            Html.button [
                prop.className
                    "inline-flex items-center px-3 py-1.5 bg-blue-600 text-white text-sm font-medium rounded-md hover:bg-blue-700"
                prop.text "Edit"
                prop.onClick (fun _ -> dispatch OpenAIContextEditor)
            ]

    Html.div [
        prop.className "p-6 space-y-6 max-w-3xl"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h1 [
                                prop.className "text-xl font-semibold text-gray-900"
                                prop.text "Standing AI Context"
                            ]
                            Html.p [
                                prop.className "text-sm text-gray-500 mt-1"
                                prop.text
                                    "Team-curated context the AI assistant sees on every message — like a CLAUDE.md for your team."
                            ]
                        ]
                    ]
                    editButton
                ]
            ]
            aiContextErrorBanner model dispatch
            body
        ]
    ]

// ─── Per-page views ───────────────────────────────────────────────

let private documentsView (model: Model) (dispatch: Msg -> unit) : PageContent =
    PageContent.FullWidth(MainPanel model dispatch)

let private notesView (model: Model) (dispatch: Msg -> unit) : PageContent =
    PageContent.FullWidth(NotesPanel model dispatch)

let private aiContextView (model: Model) (dispatch: Msg -> unit) : PageContent =
    PageContent.FullWidth(AIContextPanel model dispatch)

let private platformLibraryView (model: Model) (dispatch: Msg -> unit) : PageContent =
    PageContent.FullWidth(PlatformLibraryPanel model dispatch)

// ─── Module registration ──────────────────────────────────────────

/// Companion-exported NarrativeCommit handler. Add to
/// `ClientConfig.Handlers.NarrativeCommitHandler` (as `Some
/// KnowledgeBaseView.narrativeCommitHandler`) to enable
/// `NarrativeRenderer`'s "Save to Knowledge Base" button.
///
/// Phase 13a — replaces the legacy `installNarrativeCommit ()`
/// module-load side effect. The value reference from the consumer's
/// `ClientConfig` pulls the KB client module into the Fable import
/// graph automatically — no separate boot-time call needed.
let narrativeCommitHandler: NarrativeCommitHandler =
    ClientModel.narrativeCommitHandler

let private documentsPage: PageConfig = {
    Route = "/documents"
    Title = "Documents"
    Icon = ToolUp.KnowledgeBase.Icons.document
}

let private notesPage: PageConfig = {
    Route = "/notes"
    Title = "Notes"
    Icon = ToolUp.KnowledgeBase.Icons.note
}

let private aiContextPage: PageConfig = {
    Route = "/ai-context"
    Title = "AI Context"
    Icon = ToolUp.KnowledgeBase.Icons.aiContext
}

let private platformLibraryPage: PageConfig = {
    Route = "/platform-library"
    Title = "Platform Library"
    Icon = ToolUp.KnowledgeBase.Icons.knowledge
}

let register () : ToolUp.Platform.ErasedModule =
    ToolUp.Platform.ClientModule.create {
        Init = ClientModel.init
        Update = ClientModel.update
        Name = "Knowledge Base"
        Icon = ToolUp.KnowledgeBase.Icons.knowledge
    }
    |> ToolUp.Platform.ClientModule.withPages [
        documentsPage, documentsView
        notesPage, notesView
        platformLibraryPage, platformLibraryView
        aiContextPage, aiContextView
    ]
    |> ToolUp.Platform.ClientModule.withGroup "Knowledge"
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
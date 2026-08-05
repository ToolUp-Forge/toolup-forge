// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module KnowledgeBaseView

open Feliz
open ToolUp.Elmish
open ToolUp.Platform
open ClientModel
open SharedTypes

// Status / source / file-type badges live in `KnowledgeListView.Badges`
// — referenced here as `KnowledgeListView.Badges.statusBadge` etc. so the
// Documents page, Notes page, Platform Library page, and the
// Platform Admin module all render identically.

// ─── Upload zone ──────────────────────────────────────────────────

/// Read one local file's bytes. Ephemeral view work — the same
/// `FileReader` round-trip the single-file path has always done, lifted
/// out so the bulk path can wait for several at once.
let private readFileBytes (file: Browser.Types.File) : Async<byte[]> =
    Async.FromContinuations(fun (resolve, reject, _) ->
        let reader = Browser.Dom.FileReader.Create()

        reader.onload <-
            fun _ ->
                let buf = reader.result :?> Fable.Core.JS.ArrayBuffer
                let uint8 = Fable.Core.JS.Constructors.Uint8Array.Create(buf)
                resolve (Array.init (int uint8.length) (fun i -> byte uint8[i]))

        reader.onerror <- fun _ -> reject (exn (sprintf "Could not read '%s'" file.name))

        reader.readAsArrayBuffer file)

/// `true` when a selected file should be expanded server-side rather
/// than stored as itself.
let private isArchiveName (name: string) : bool = name.ToLower().EndsWith ".zip"

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

    /// Phase 511 — route a selection to the right surface.
    ///
    /// **A single non-archive file still takes the pre-511 path,
    /// unchanged** (GP 11): same `UploadDocument` call, same
    /// multipart-optimised wire, same spinner. The batch surface is
    /// entered only when the selection genuinely IS a batch — several
    /// files, or an archive to expand — which is exactly when N separate
    /// round-trips and N toasts were the problem.
    let processSelection (files: Browser.Types.File list) =
        match files with
        | [] -> ()
        | [ single ] when not (isArchiveName single.name) -> processFile single
        | files ->
            async {
                try
                    let! sources =
                        files
                        |> List.map (fun file -> async {
                            let! bytes = readFileBytes file

                            return
                                if isArchiveName file.name then
                                    BulkImportSource.Archive(file.name, bytes)
                                else
                                    BulkImportSource.File(file.name, bytes)
                        })
                        |> Async.Sequential

                    dispatch (BulkImportRequested(List.ofArray sources))
                with ex ->
                    dispatch (BulkImportFailed ex.Message)
            }
            |> Async.StartImmediate

    Html.div [
        prop.className [
            "border-2 border-dashed rounded-lg p-8 text-center transition-colors"
            if dragActive && not (model.Uploading || model.BulkImporting) then
                "border-blue-500 bg-blue-50"
            else
                "border-gray-300 hover:border-blue-400"
        ]
        // `preventDefault` on dragenter / dragover is mandatory: without it
        // the browser's default behaviour is to open the dragged file in
        // the tab, which navigates away and discards the drop entirely.
        prop.onDragEnter (fun ev ->
            ev.preventDefault ()

            if not (model.Uploading || model.BulkImporting) then
                setDragActive true)
        prop.onDragOver (_.preventDefault())
        prop.onDragLeave (fun _ -> setDragActive false)
        prop.onDrop (fun ev ->
            ev.preventDefault ()
            setDragActive false

            if not (model.Uploading || model.BulkImporting) then
                let files = ev.dataTransfer.files
                processSelection [ for i in 0 .. (int files.length) - 1 -> files[i] ])
        prop.children [
            Html.p [
                prop.className "text-sm font-medium text-gray-700 mb-1"
                prop.text (
                    if dragActive && not (model.Uploading || model.BulkImporting) then
                        "Drop files to upload"
                    else
                        "Upload documents to your knowledge base"
                )
            ]
            Html.p [
                prop.className "text-xs text-gray-500 mb-4"
                prop.text "PDF, PPTX, DOCX, XLSX, CSV, TXT — or a .zip to expand  ·  drop files here or click to choose"
            ]
            Html.label [
                prop.className
                    "inline-flex items-center px-4 py-2 bg-blue-600 text-white text-sm font-medium rounded-md hover:bg-blue-700 cursor-pointer"
                prop.children [
                    Html.span [
                        prop.text (
                            if model.BulkImporting then "Importing…"
                            elif model.Uploading then "Uploading…"
                            else "Choose files"
                        )
                    ]
                    Html.input [
                        prop.type' "file"
                        prop.className "hidden"
                        prop.accept ".pdf,.pptx,.docx,.xlsx,.csv,.txt,.zip"
                        prop.multiple true
                        prop.disabled (model.Uploading || model.BulkImporting)
                        prop.onClick (fun ev ->
                            let input = ev.target :?> Browser.Types.HTMLInputElement
                            input.value <- "")
                        prop.onChange processSelection
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

// ─── Batch import roll-up (Phase 511.D) ───────────────────────────
//
// The batch's single completion surface. One panel summarising N items
// replaces N transient toasts: a migration that refused 12 of 500 files
// needs the operator to be able to READ which 12 and why, which a toast
// stream structurally cannot provide.
//
// Per-item ingestion progress is deliberately NOT duplicated here — an
// admitted document joins the ordinary document list below and reports
// its extraction / embedding lifecycle there, through the same status
// badges every other upload uses.

let private bulkImportPanel (model: Model) (dispatch: Msg -> unit) : ReactElement =
    match model.BulkImportError, model.BulkReport with
    | Some err, _ ->
        Html.div [
            prop.className "bg-red-50 border border-red-200 rounded-md px-4 py-3 flex items-start gap-3"
            prop.children [
                Html.p [
                    prop.className "text-sm text-red-700 flex-1"
                    prop.text (sprintf "Batch import failed: %s" err)
                ]
                Html.button [
                    prop.className "text-red-400 hover:text-red-600 text-lg"
                    prop.text "×"
                    prop.onClick (fun _ -> dispatch DismissBulkReport)
                ]
            ]
        ]
    | None, None -> Html.none
    | None, Some report ->
        let refusals =
            report.Items
            |> List.choose (fun item ->
                match item.Outcome with
                | BulkItemOutcome.Refused reason -> Some(item.Source, reason)
                // A per-item POLICY refusal arrives as an admitted
                // document whose status is `UploadRejected` — same
                // outcome for the reader, so it is listed alongside.
                | BulkItemOutcome.Admitted doc ->
                    match doc.Status with
                    | IngestionStatus.UploadRejected reason -> Some(item.Source, reason)
                    | _ -> None)

        Html.div [
            prop.className [
                "border rounded-md px-4 py-3"
                if List.isEmpty refusals then
                    "bg-green-50 border-green-200"
                else
                    "bg-amber-50 border-amber-200"
            ]
            prop.children [
                Html.div [
                    prop.className "flex items-start gap-3"
                    prop.children [
                        Html.p [
                            prop.className "text-sm font-medium text-gray-800 flex-1"
                            prop.text (
                                sprintf
                                    "Batch import complete — %d of %d item(s) imported, %d refused."
                                    report.Imported
                                    (List.length report.Items)
                                    report.Refused
                            )
                        ]
                        Html.button [
                            prop.className "text-gray-400 hover:text-gray-600 text-lg"
                            prop.text "×"
                            prop.onClick (fun _ -> dispatch DismissBulkReport)
                        ]
                    ]
                ]
                if not (List.isEmpty refusals) then
                    Html.ul [
                        prop.className "mt-2 space-y-1"
                        prop.children [
                            for source, reason in refusals ->
                                Html.li [
                                    prop.className "text-xs text-gray-700"
                                    prop.children [
                                        Html.span [ prop.className "font-medium"; prop.text source ]
                                        Html.span [ prop.text (sprintf " — %s" reason) ]
                                    ]
                                ]
                        ]
                    ]
            ]
        ]

// ─── Team Documents list ──────────────────────────────────────────
//
// Uses the shared `KnowledgeListView` component for filtering / grouping
// and uniform badge rendering. Notes are filtered out at the source
// because they have their own dedicated page (the Documents list shows
// uploads + narratives only).

/// Phase 502.C — inline tag editor for one document row.
///
/// The draft lives in `React.useState` and is dispatched only on submit
/// (Enter or Save), per the MVU discipline in `CLAUDE.md`: a
/// per-keystroke `Msg` here would re-render the whole document table on
/// every character.
///
/// Comma-separated because that is what a one-line input can express
/// without inventing a chip widget; the SERVER normalises (trim,
/// lower-case, collapse whitespace to `-`, de-duplicate, cap), and the
/// row re-renders from the document the server returns — so the user
/// sees the canonical tag, which is the one a retrieval filter will
/// actually match, rather than what they typed.
[<ReactComponent>]
let private TagEditor (doc: KnowledgeDocument) (dispatch: Msg -> unit) =
    let editing, setEditing = React.useState false
    let draft, setDraft = React.useState (String.concat ", " doc.Tags)

    let submit () =
        let tags =
            draft.Split(',')
            |> Array.map (fun t -> t.Trim())
            |> Array.filter (fun t -> t <> "")
            |> List.ofArray

        dispatch (SetTagsRequested(doc.Id, tags))
        setEditing false

    if editing then
        Html.div [
            prop.className "inline-flex items-center gap-1"
            prop.children [
                Html.input [
                    prop.className "w-40 px-2 py-1 text-xs border border-gray-300 rounded"
                    prop.placeholder "policy, 2024"
                    prop.value draft
                    prop.autoFocus true
                    prop.onChange setDraft
                    prop.onKeyDown (fun e ->
                        if e.key = "Enter" then
                            submit ()
                        elif e.key = "Escape" then
                            setDraft (String.concat ", " doc.Tags)
                            setEditing false)
                ]
                Html.button [
                    prop.className "text-xs text-blue-600 hover:text-blue-800 font-medium"
                    prop.text "Save"
                    prop.onClick (fun _ -> submit ())
                ]
                Html.button [
                    prop.className "text-xs text-gray-500 hover:text-gray-700"
                    prop.text "Cancel"
                    prop.onClick (fun _ ->
                        setDraft (String.concat ", " doc.Tags)
                        setEditing false)
                ]
            ]
        ]
    else
        Html.button [
            prop.className "text-xs text-gray-600 hover:text-gray-900 font-medium"
            prop.text (if List.isEmpty doc.Tags then "Add tags" else "Edit tags")
            prop.onClick (fun _ ->
                setDraft (String.concat ", " doc.Tags)
                setEditing true)
        ]

let private deleteRowAction (dispatch: Msg -> unit) (doc: KnowledgeDocument) : ReactElement =
    Html.div [
        prop.className "inline-flex items-center gap-3"
        prop.children [
            // Phase 502.C — narrative documents cannot carry tags (their
            // chunks are the owning module's, so a tag could never reach
            // a retrieval filter and the server refuses outright). Not
            // offering the affordance is better than offering one that
            // always errors.
            match doc.Source with
            | FromNarrative _ -> Html.none
            | UploadedFile
            | Note _ -> TagEditor doc dispatch
            // Phase 636 — the drawer trigger, present only where there
            // is history to show. On a deployment that never composed
            // `withDocumentVersioning` every document is version 1, so
            // this action column is byte-for-byte what it was (GP 11).
            if KnowledgeListView.hasVersionHistory doc then
                Html.button [
                    prop.className "text-xs text-gray-600 hover:text-gray-900 font-medium"
                    prop.text "History"
                    prop.title (sprintf "Version %d — show this document's version history" doc.Version)
                    prop.onClick (fun _ -> dispatch (OpenVersionHistory(doc.Id, doc.FileName)))
                ]
            Html.button [
                prop.className "text-xs text-red-600 hover:text-red-800 font-medium"
                prop.text "Delete"
                prop.onClick (fun _ -> dispatch (DeleteRequested doc.Id))
            ]
        ]
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

// ─── Version-history drawer (Phase 636) ───────────────────────────
//
// What Phase 510 put on the wire, rendered. A versioned re-upload
// supersedes in place — same document id, incremented `Version` — so
// without this the only visible evidence that a document HAS a history
// was that its size or timestamp had quietly changed.
//
// Mounted only while `Model.VersionHistory` is `Some`, which only
// happens after a click on the row's "History" action, which itself
// only exists above version 1. A single-version or unversioned
// deployment therefore never renders one byte of this (GP 11 / GP 13).

/// Per-version original access.
///
/// **The current version is downloadable; a superseded one is not**, and
/// that asymmetry is the wire's, not a UI shortcut.
/// `KnowledgeApi.GetOriginalDelivery` addresses a *lineage*
/// (`docId -> …`), so it resolves the live original; a superseded
/// version's bytes are preserved at
/// `KnowledgeDocumentVersion.OriginalBlobName` but no client-reachable
/// method fetches them. Offering a button that could only ever return
/// the wrong version's bytes would be worse than offering none, so the
/// row says plainly what is kept and why it cannot be fetched yet.
let private versionRow (state: VersionHistoryState) (dispatch: Msg -> unit) (version: KnowledgeDocumentVersion) =
    let isCurrent = version.SupersededAt.IsNone
    let isDownloading = state.Downloading = Some version.Version

    Html.div [
        prop.key (sprintf "%s:v%d" version.DocumentId version.Version)
        prop.className "flex items-start justify-between gap-4 px-4 py-3 border-b border-gray-100 last:border-b-0"
        prop.children [
            Html.div [
                prop.className "min-w-0 space-y-1"
                prop.children [
                    Html.div [
                        prop.className "flex items-center gap-2"
                        prop.children [
                            Html.span [
                                prop.className "text-sm font-medium text-gray-900"
                                prop.text (sprintf "v%d" version.Version)
                            ]
                            if isCurrent then
                                Html.span [
                                    prop.className
                                        "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-700"
                                    prop.text "Current"
                                ]
                            Html.span [ prop.className "text-xs text-gray-500 truncate"; prop.text version.FileName ]
                        ]
                    ]
                    Html.p [
                        prop.className "text-xs text-gray-500"
                        prop.text (
                            sprintf
                                "%s · %s · %d chunk%s · by %s"
                                (version.UploadedAt.ToString("yyyy-MM-dd HH:mm"))
                                (KnowledgeListView.formatSize version.SizeBytes)
                                version.ChunkCount
                                (if version.ChunkCount = 1 then "" else "s")
                                version.UploadedBy
                        )
                    ]
                    match version.SupersededAt with
                    | Some supersededAt ->
                        Html.p [
                            prop.className "text-xs text-gray-400"
                            prop.text (sprintf "Superseded %s" (supersededAt.ToString("yyyy-MM-dd HH:mm")))
                        ]
                    | None -> Html.none
                ]
            ]
            Html.div [
                prop.className "shrink-0"
                prop.children [
                    if isCurrent then
                        Html.button [
                            prop.className
                                "text-xs text-blue-600 hover:text-blue-800 font-medium disabled:text-gray-300 disabled:cursor-not-allowed"
                            prop.disabled (state.Downloading.IsSome)
                            prop.text (if isDownloading then "Opening…" else "Open original")
                            prop.title "Fetch this version's original document"
                            prop.onClick (fun _ -> dispatch (DownloadVersionRequested version))
                        ]
                    else
                        Html.span [
                            prop.className "text-xs text-gray-400"
                            prop.title
                                "This version's original bytes are preserved, but the API addresses a document rather than a version — there is no per-version fetch yet."
                            prop.text "Original preserved"
                        ]
                ]
            ]
        ]
    ]

[<ReactComponent>]
let private VersionHistoryDrawer (state: VersionHistoryState) (dispatch: Msg -> unit) =
    let body =
        match state.LoadError, state.Versions with
        | Some err, _ ->
            Html.p [
                prop.className "px-4 py-6 text-sm text-red-700"
                prop.text (sprintf "Couldn't load the version history: %s" err)
            ]
        | None, None -> Html.p [ prop.className "px-4 py-6 text-sm text-gray-500"; prop.text "Loading…" ]
        // An empty list is a real answer: the document is not visible in
        // this scope (the server returns `[]` rather than an existence
        // signal — GP 4). Say so in the same words either way.
        | None, Some [] ->
            Html.p [
                prop.className "px-4 py-6 text-sm text-gray-500"
                prop.text "No version history is available for this document."
            ]
        | None, Some versions ->
            Html.div [
                prop.className "divide-y divide-gray-100"
                prop.children (versions |> List.map (versionRow state dispatch))
            ]

    Html.div [
        prop.className "fixed inset-0 z-40 flex justify-end"
        prop.children [
            // Click-away backdrop. `aria-hidden` because the close
            // affordance a keyboard user reaches is the button below.
            Html.div [
                prop.className "absolute inset-0 bg-gray-900/20"
                prop.ariaHidden true
                prop.onClick (fun _ -> dispatch CloseVersionHistory)
            ]
            Html.div [
                prop.className
                    "relative w-full max-w-md h-full bg-white border-l border-gray-200 shadow-xl overflow-y-auto"
                prop.role "dialog"
                prop.ariaLabel (sprintf "Version history for %s" state.FileName)
                prop.children [
                    Html.div [
                        prop.className
                            "sticky top-0 bg-white border-b border-gray-200 px-4 py-3 flex items-start justify-between gap-3"
                        prop.children [
                            Html.div [
                                prop.className "min-w-0"
                                prop.children [
                                    Html.h3 [
                                        prop.className "text-sm font-semibold text-gray-900"
                                        prop.text "Version history"
                                    ]
                                    Html.p [ prop.className "text-xs text-gray-500 truncate"; prop.text state.FileName ]
                                ]
                            ]
                            Html.button [
                                prop.className "text-gray-400 hover:text-gray-600 text-lg leading-none"
                                prop.ariaLabel "Close version history"
                                prop.text "×"
                                prop.onClick (fun _ -> dispatch CloseVersionHistory)
                            ]
                        ]
                    ]
                    match state.DownloadError with
                    | Some err ->
                        Html.p [
                            prop.className "px-4 py-2 text-xs text-red-700 bg-red-50 border-b border-red-100"
                            prop.text err
                        ]
                    | None -> Html.none
                    body
                ]
            ]
        ]
    ]

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
            bulkImportPanel model dispatch
            UploadZone model dispatch
            documentList model dispatch
            // Phase 636 — absent unless a "History" action was clicked,
            // which itself only exists above version 1.
            match model.VersionHistory with
            | Some state -> VersionHistoryDrawer state dispatch
            | None -> Html.none
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

/// Build the Knowledge Base `ErasedModule`, optionally re-branded.
/// Phase 1e — the parameterised form behind `KnowledgeBaseMode`'s
/// `DefaultKnowledgeBase` (`None`) and `ConfiguredKnowledgeBase`
/// (`Some cfg`) cases, mirroring `FileManagerUI.create`'s
/// `DataManagerConfig option` shape.
///
/// `create None` is byte-for-byte the historical `register ()` module —
/// same name, icon, group, and therefore the same derived
/// `Definition.Id` ("KnowledgeBase"), which is the RBAC key in
/// `ServerConfig.ModuleNames` (GP 11).
let create (config: ToolUp.KnowledgeBase.KnowledgeBaseConfig option) : ToolUp.Platform.ErasedModule =
    // Phase 102–107 tail — broker the "view original" affordance to the
    // AI Sources panel. Composing the Knowledge Base in is what makes a
    // citation's original openable; a deployment without it leaves the
    // bridge unregistered and the panel renders as before (GP 11).
    ToolUp.Platform.OriginalDocumentBridge.register ClientModel.originalDocumentOpener

    let name = config |> Option.map _.Name |> Option.defaultValue "Knowledge Base"

    let icon =
        config
        |> Option.map _.Icon
        |> Option.defaultValue ToolUp.KnowledgeBase.Icons.knowledge

    let group = config |> Option.bind _.Group |> Option.defaultValue "Knowledge"

    ToolUp.Platform.ClientModule.create {
        Init = ClientModel.init
        Update = ClientModel.update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withPages [
        documentsPage, documentsView
        notesPage, notesView
        platformLibraryPage, platformLibraryView
        aiContextPage, aiContextView
    ]
    |> ToolUp.Platform.ClientModule.withGroup group
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register

/// The stock Knowledge Base module, unbranded. Kept as the direct
/// registration path for consumers that list modules by hand; the
/// `KnowledgeBaseMode`-driven path calls `create` instead.
let register () : ToolUp.Platform.ErasedModule = create None
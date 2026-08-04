// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ClientModel

open ToolUp.Platform
open ToolUp.Elmish
open SharedTypes
open PlatformKnowledgeApi
open ToolUp.Remoting.Client
open Fable.Core
open Fable.Core.JsInterop

// Alias for the neutral Platform-side knowledge types (Phase 102–107).
// Not `open`ed because `SourceLocator` / `ChunkOrigin` case names
// collide with the KB-side `SourceLocation` / `KnowledgeSource` DUs.
module VK = ToolUp.Platform.VectorKnowledgeTypes

/// Which note the editor panel is currently authoring. `CreateNew`
/// means the editor was opened from the "New note" button; `EditExisting`
/// carries the docId of the note being edited.
type NoteEditorTarget =
    | CreateNew
    | EditExisting of docId: string

type Model = {
    Documents: KnowledgeDocument list
    Uploading: bool
    UploadError: string option
    LoadError: string option
    Resetting: bool

    /// True while a `RefreshAIContextRequested` round-trip is in flight.
    /// Drives the Refresh button's spinner / disabled state. The server
    /// re-publishes the inventory notification synchronously so the
    /// in-flight window is short, but the busy indicator avoids
    /// double-clicks.
    RefreshingAIContext: bool

    // ── Notes ──
    /// Open editor target; `None` means the editor panel is closed.
    /// Title + body drafts live in `React.useState` inside the view —
    /// the model only tracks open/closed and which note is being edited.
    NoteEditor: NoteEditorTarget option
    SavingNote: bool
    NoteSaveError: string option

    // ── Standing AI context ──
    /// `None` until the first GetAIContext returns. After that, `Some None`
    /// means the team has no standing context written; `Some (Some e)` is
    /// the loaded entry.
    AIContext: AIContextEntry option option
    AIContextLoadError: string option
    AIContextEditorOpen: bool
    SavingAIContext: bool
    AIContextSaveError: string option

    // ── Platform Library (read-only view of the Platform Knowledge Base) ──
    /// Cross-team documents the Platform Admin has curated. Loaded on
    /// first visit to the Platform Library page; refreshed on subsequent
    /// visits + on `DataRefreshed("PlatformKnowledgeBase", _)` notifications.
    /// Writes happen exclusively through the separate `_sdk.PlatformKnowledgeAdmin`
    /// module (gated on `canModifyPlatformConfig`) — this list is read-only
    /// here so non-admin users get transparent visibility into the shared
    /// reference content the AI assistant will draw from.
    PlatformDocuments: KnowledgeDocument list
    PlatformDocsLoaded: bool
    PlatformDocsLoading: bool
    PlatformDocsLoadError: string option

    // ── Bulk import (Phase 511) ──
    /// True while an `ImportBatch` round-trip is in flight. Separate from
    /// `Uploading` so the single-file path's spinner semantics are
    /// untouched (GP 11).
    BulkImporting: bool
    /// The last batch's per-item report, kept until dismissed. This is
    /// the batch's roll-up view: N documents, one summary, one place to
    /// read what was refused and why — instead of N transient toasts a
    /// user cannot scroll back through.
    BulkReport: BulkImportReport option
    BulkImportError: string option
}

type Msg =
    | LoadDocuments of ApiCall<unit, KnowledgeDocument list>
    | UploadRequested of byte[] * string
    | UploadCompleted of ApiCall<byte[] * string, KnowledgeDocument>
    | DeleteRequested of string
    | DocumentDeleted of string
    /// The upload / delete RPC threw (network, 413, 500, expired auth) — without
    /// these the `Cmd.OfAsync.perform` swallowed the error and the spinner hung.
    | UploadFailed of string
    | DeleteFailed of string
    | PollStatuses
    | StatusPolled of string * IngestionStatus
    | ResetIndexRequested
    | ResetIndexCompleted of Result<unit, string>
    | RefreshAIContextRequested
    | RefreshAIContextCompleted
    | DismissError

    // ── Notes ──
    | OpenNoteEditor of NoteEditorTarget
    | CloseNoteEditor
    /// Carries the title + body drafts pulled from React-local state at
    /// click time — the model never stores per-keystroke input.
    | SaveNote of NoteEditorTarget * title: string * body: string
    | NoteSaved of Result<KnowledgeDocument, string>

    // ── Standing AI context ──
    | LoadAIContext of ApiCall<unit, AIContextEntry option>
    | OpenAIContextEditor
    | CloseAIContextEditor
    | SaveAIContext of body: string
    | AIContextSaved of Result<AIContextEntry, string>

    // ── Platform Library ──
    | LoadPlatformDocuments of ApiCall<unit, KnowledgeDocument list>
    | PlatformDocumentsLoadFailed of string

    // ── Bulk import (Phase 511) ──
    /// Submit many sources — plain files, archives to expand, or URLs —
    /// as ONE `ImportBatch` call. The view reads the local files itself
    /// (a `FileReader` round-trip is ephemeral view work, exactly as the
    /// single-file path already treats it) and dispatches the assembled
    /// sources.
    | BulkImportRequested of BulkImportSource list
    | BulkImportCompleted of BulkImportReport
    | BulkImportFailed of string
    | DismissBulkReport

// `withMultipartOptimization` is required for the `byte[]` argument on
// `UploadDocument`: Fable.Remoting's default JSON transport encodes byte
// arrays as a JSON array of ints, which the byte[] converter cannot
// deserialise into `System.Byte[]` (it expects base64). Multipart
// sends each byte[]
// arg as `application/octet-stream` and the rest as JSON parts.
let private knowledgeApi =
    Api.makeProxy<KnowledgeApi> (customOptions = (UserSession.withRequestHeaders >> Remoting.withMultipartOptimization))

/// Read-only proxy used by the team-side "Platform Library" page. Writes
/// to the Platform Knowledge Base flow through the separate
/// `_sdk.PlatformKnowledgeAdmin` module's proxy, which is identical at
/// the wire level but kept distinct so an audit by call-site distinguishes
/// the two surfaces.
let private platformKnowledgeApi =
    Api.makeProxy<IPlatformKnowledgeApi> (customOptions = UserSession.withRequestHeaders)

let init () =
    {
        Documents = []
        Uploading = false
        UploadError = None
        LoadError = None
        Resetting = false
        RefreshingAIContext = false

        NoteEditor = None
        SavingNote = false
        NoteSaveError = None

        AIContext = None
        AIContextLoadError = None
        AIContextEditorOpen = false
        SavingAIContext = false
        AIContextSaveError = None

        PlatformDocuments = []
        PlatformDocsLoaded = false
        PlatformDocsLoading = false
        PlatformDocsLoadError = None

        BulkImporting = false
        BulkReport = None
        BulkImportError = None
    },
    Cmd.batch [ Cmd.ofMsg (LoadDocuments(Start())); Cmd.ofMsg (LoadAIContext(Start())) ]

let private isTerminal (status: IngestionStatus) =
    match status with
    | IngestionStatus.Complete _
    | IngestionStatus.Failed _
    // Phase 119 — both are end-states; neither progresses, so the poll
    // loop must not keep waiting on them.
    | IngestionStatus.UploadRejected _
    | IngestionStatus.UnsupportedFormat _
    // Phase 500 — terminal: no OCR companion is composed, so nothing
    // further will happen to this document. Polling it forever would
    // keep the 2s loop alive on a state that cannot change.
    | IngestionStatus.OcrUnavailable _ -> true
    | _ -> false

let private hasNonTerminal (docs: KnowledgeDocument list) =
    docs |> List.exists (fun d -> not (isTerminal d.Status))

/// Schedule a delayed `PollStatuses` dispatch. Used to drive a 2s polling
/// loop while at least one document is non-terminal — the loop self-terminates
/// once every doc reaches `Complete` / `Failed`.
let private schedulePoll () =
    Cmd.OfAsync.perform (fun () -> Async.Sleep 2000) () (fun () -> PollStatuses)

let update (msg: Msg) (model: Model) =
    match msg with
    | LoadDocuments(Start()) ->
        model, Cmd.OfAsync.perform (fun () -> knowledgeApi.GetDocuments()) () (fun docs -> LoadDocuments(Finished docs))

    | LoadDocuments(Finished docs) ->
        let pollCmd = if hasNonTerminal docs then schedulePoll () else Cmd.none

        {
            model with
                Documents = docs
                LoadError = None
        },
        pollCmd

    | UploadRequested(bytes, fileName) ->
        {
            model with
                Uploading = true
                UploadError = None
        },
        Cmd.OfAsync.either
            (fun (b, n) -> knowledgeApi.UploadDocument b n)
            (bytes, fileName)
            (fun doc -> UploadCompleted(Finished doc))
            (fun ex -> UploadFailed ex.Message)

    | UploadCompleted(Finished doc) ->
        match doc.Status with
        // Phase 119 — a policy rejection never persisted anything, so it
        // must NOT join the document list. Surface the reason as an upload
        // error instead; no poll loop is needed (nothing is ingesting).
        | IngestionStatus.UploadRejected reason ->
            {
                model with
                    Uploading = false
                    UploadError = Some reason
            },
            Cmd.none
        | _ ->
            let docs =
                model.Documents |> List.filter (fun d -> d.Id <> doc.Id) |> List.append [ doc ]

            // Newly-uploaded doc starts non-terminal — kick the poll loop in case
            // it had naturally stopped because every prior doc was already terminal.
            {
                model with
                    Documents = docs
                    Uploading = false
            },
            schedulePoll ()

    | UploadCompleted _ ->
        {
            model with
                Uploading = false
                UploadError = Some "Upload failed"
        },
        Cmd.none

    // ── Bulk import (Phase 511) ──

    | BulkImportRequested sources ->
        {
            model with
                BulkImporting = true
                BulkImportError = None
                BulkReport = None
        },
        Cmd.OfAsync.either
            (fun (req: BulkImportRequest) -> knowledgeApi.ImportBatch req)
            { Sources = sources }
            BulkImportCompleted
            (fun ex -> BulkImportFailed ex.Message)

    | BulkImportCompleted report ->
        // Merge every admitted document into the list, replacing any
        // existing entry with the same id — a dedup hit and a versioned
        // supersede both return an id already present, and appending
        // blindly would show it twice.
        let admitted =
            report.Items
            |> List.choose (fun item ->
                match item.Outcome with
                | BulkItemOutcome.Admitted doc ->
                    match doc.Status with
                    | IngestionStatus.UploadRejected _ -> None
                    | _ -> Some doc
                | BulkItemOutcome.Refused _ -> None)

        let admittedIds = admitted |> List.map _.Id |> Set.ofList

        let docs =
            (model.Documents |> List.filter (fun d -> not (admittedIds.Contains d.Id)))
            @ admitted

        {
            model with
                Documents = docs
                BulkImporting = false
                BulkReport = Some report
        },
        // Freshly-admitted documents start non-terminal; restart the poll
        // loop in case it had stopped because everything was terminal.
        (if List.isEmpty admitted then Cmd.none else schedulePoll ())

    | BulkImportFailed reason ->
        {
            model with
                BulkImporting = false
                BulkImportError = Some reason
        },
        Cmd.none

    | DismissBulkReport ->
        {
            model with
                BulkReport = None
                BulkImportError = None
        },
        Cmd.none

    | DeleteRequested docId ->
        model,
        Cmd.OfAsync.either (fun id -> knowledgeApi.DeleteDocument id) docId (fun _ -> DocumentDeleted docId) (fun ex ->
            DeleteFailed ex.Message)

    | DocumentDeleted docId ->
        let docs = model.Documents |> List.filter (fun d -> d.Id <> docId)
        { model with Documents = docs }, Cmd.none

    | UploadFailed reason ->
        {
            model with
                Uploading = false
                UploadError = Some reason
        },
        Cmd.none

    | DeleteFailed reason ->
        {
            model with
                LoadError = Some(sprintf "Couldn't delete the document: %s" reason)
        },
        Cmd.none

    | PollStatuses ->
        // Fire all status fetches in parallel and queue the next poll cycle.
        // Scheduling here (rather than in `StatusPolled`) keeps one poll loop
        // running per session — N parallel `StatusPolled` returns would
        // otherwise each schedule their own next cycle and fan out.
        let nonTerminal =
            model.Documents |> List.filter (fun d -> not (isTerminal d.Status))

        let fetchCmds =
            nonTerminal
            |> List.map (fun doc ->
                Cmd.OfAsync.perform knowledgeApi.GetStatus doc.Id (fun status -> StatusPolled(doc.Id, status)))

        let nextPoll =
            if not nonTerminal.IsEmpty then
                schedulePoll ()
            else
                Cmd.none

        model, Cmd.batch (nextPoll :: fetchCmds)

    | StatusPolled(docId, status) ->
        let docs =
            model.Documents
            |> List.map (fun d -> if d.Id = docId then { d with Status = status } else d)

        { model with Documents = docs }, Cmd.none

    | ResetIndexRequested ->
        { model with Resetting = true }, Cmd.OfAsync.perform knowledgeApi.ResetIndex () ResetIndexCompleted

    | ResetIndexCompleted(Ok()) ->
        // Server publishes `DataRefreshed("KnowledgeBase", _)` after the wipe;
        // the view's notification subscription reloads the document list.
        // Eagerly clearing here keeps the UI feeling responsive while the
        // round-trip completes.
        {
            model with
                Documents = []
                Resetting = false
                UploadError = None
                LoadError = None
        },
        Cmd.none

    | ResetIndexCompleted(Error reason) ->
        {
            model with
                Resetting = false
                UploadError = Some(sprintf "Reset failed: %s" reason)
        },
        Cmd.none

    | RefreshAIContextRequested ->
        {
            model with
                RefreshingAIContext = true
        },
        Cmd.OfAsync.perform knowledgeApi.RefreshAIContext () (fun () -> RefreshAIContextCompleted)

    | RefreshAIContextCompleted ->
        {
            model with
                RefreshingAIContext = false
        },
        Cmd.none

    | DismissError ->
        {
            model with
                UploadError = None
                LoadError = None
                NoteSaveError = None
                AIContextSaveError = None
                AIContextLoadError = None
        },
        Cmd.none

    // ── Notes ──

    | OpenNoteEditor target ->
        {
            model with
                NoteEditor = Some target
                NoteSaveError = None
        },
        Cmd.none

    | CloseNoteEditor ->
        {
            model with
                NoteEditor = None
                NoteSaveError = None
        },
        Cmd.none

    | SaveNote(target, title, body) ->
        let cmd =
            match target with
            | CreateNew ->
                Cmd.OfAsync.perform (fun (t, b) -> knowledgeApi.AddNote { Title = t; Body = b }) (title, body) NoteSaved
            | EditExisting docId ->
                Cmd.OfAsync.perform
                    (fun (id, t, b) -> knowledgeApi.UpdateNote { DocId = id; Title = t; Body = b })
                    (docId, title, body)
                    NoteSaved

        {
            model with
                SavingNote = true
                NoteSaveError = None
        },
        cmd

    | NoteSaved(Ok doc) ->
        let docs =
            model.Documents |> List.filter (fun d -> d.Id <> doc.Id) |> List.append [ doc ]

        {
            model with
                Documents = docs
                SavingNote = false
                NoteEditor = None
                NoteSaveError = None
        },
        // Newly-saved note starts non-terminal — make sure the poll loop
        // is running. `schedulePoll` is idempotent w.r.t. state, the loop
        // self-terminates when no doc is non-terminal.
        schedulePoll ()

    | NoteSaved(Error reason) ->
        {
            model with
                SavingNote = false
                NoteSaveError = Some reason
        },
        Cmd.none

    // ── Standing AI context ──

    | LoadAIContext(Start()) ->
        model,
        Cmd.OfAsync.perform (fun () -> knowledgeApi.GetAIContext()) () (fun entry -> LoadAIContext(Finished entry))

    | LoadAIContext(Finished entry) ->
        {
            model with
                AIContext = Some entry
                AIContextLoadError = None
        },
        Cmd.none

    | OpenAIContextEditor ->
        {
            model with
                AIContextEditorOpen = true
                AIContextSaveError = None
        },
        Cmd.none

    | CloseAIContextEditor ->
        {
            model with
                AIContextEditorOpen = false
                AIContextSaveError = None
        },
        Cmd.none

    | SaveAIContext body ->
        {
            model with
                SavingAIContext = true
                AIContextSaveError = None
        },
        Cmd.OfAsync.perform knowledgeApi.SetAIContext body AIContextSaved

    | AIContextSaved(Ok entry) ->
        {
            model with
                AIContext = Some(Some entry)
                AIContextEditorOpen = false
                SavingAIContext = false
                AIContextSaveError = None
        },
        Cmd.none

    | AIContextSaved(Error reason) ->
        {
            model with
                SavingAIContext = false
                AIContextSaveError = Some reason
        },
        Cmd.none

    // ── Platform Library ──

    | LoadPlatformDocuments(Start()) ->
        {
            model with
                PlatformDocsLoading = true
                PlatformDocsLoadError = None
        },
        Cmd.OfAsync.either
            (fun () -> platformKnowledgeApi.ListPlatformDocuments())
            ()
            (fun docs -> LoadPlatformDocuments(Finished docs))
            (fun ex -> PlatformDocumentsLoadFailed ex.Message)

    | LoadPlatformDocuments(Finished docs) ->
        {
            model with
                PlatformDocuments = docs |> List.sortByDescending _.UploadedAt
                PlatformDocsLoaded = true
                PlatformDocsLoading = false
                PlatformDocsLoadError = None
        },
        Cmd.none

    | PlatformDocumentsLoadFailed reason ->
        {
            model with
                PlatformDocsLoaded = true
                PlatformDocsLoading = false
                PlatformDocsLoadError = Some reason
        },
        Cmd.none

// ─── Narrative → KB commit handler (Phase 13a) ─────────────────────
//
// `narrativeCommitHandler` is a value the SDK reads from
// `ClientConfig.Handlers.NarrativeCommitHandler` at boot. It brokers
// the `Save to Knowledge Base` button in `NarrativeRenderer`.
//
// Was (legacy):
//   let installNarrativeCommit () : unit =
//       ... do Toolup.NarrativeCommit.install { Submit = submit }
//
// Now:
//   let narrativeCommitHandler : NarrativeCommitHandler = { Submit = submit }
//
// Consumer migration:
//   // was: KnowledgeBaseView.installNarrativeCommit ()
//   // now: Handlers = { ... NarrativeCommitHandler =
//   //                          Some KnowledgeBaseView.narrativeCommitHandler }

let private submitNarrative
    (doc: ToolUp.Platform.Narrative.NarrativeDocument)
    (overwrite: bool)
    : Async<NarrativeCommitResult> =
    async {
        let! result =
            knowledgeApi.IngestNarrative {
                Document = doc
                Overwrite = overwrite
            }

        match result with
        | Ok kbDoc -> return NarrativeCommitResult.Committed(kbDoc.Id, kbDoc.FileName)
        | Error SharedTypes.MissingProvenance -> return NarrativeCommitResult.MissingProvenance
        | Error(SharedTypes.DuplicateExists existing) ->
            let generatedAt =
                match existing.Source with
                | FromNarrative src -> src.GeneratedAt
                | UploadedFile
                | Note _ -> existing.UploadedAt

            return NarrativeCommitResult.Duplicate(existing.FileName, generatedAt)
        | Error(SharedTypes.IngestFailed reason) -> return NarrativeCommitResult.Failed reason
    }

/// Companion-exported NarrativeCommit handler. Add to
/// `ClientConfig.Handlers.NarrativeCommitHandler` (as `Some
/// KnowledgeBaseView.narrativeCommitHandler`) to enable
/// `NarrativeRenderer`'s "Save to Knowledge Base" button.
let narrativeCommitHandler: NarrativeCommitHandler = { Submit = submitNarrative }

// ─── "View original" opener (Phase 102–107 client tail) ────────────
//
// Brokered to the AI Sources panel via
// `ToolUp.Platform.OriginalDocumentBridge` (registered in
// `KnowledgeBaseView.register`). Fetches the original bytes through the
// scope-gated `GetOriginalDocument` API and opens them in a new tab,
// honouring a PDF page locator as a `#page=N` deep link where present;
// other locator kinds — and a popup-blocked tab — degrade to a plain
// open / download. Absence and denial come back as benign inline
// messages: an out-of-scope id is reported generically so the
// affordance can't be used to probe for document existence (GP 4 / GP 9).

[<Fable.Core.Emit("new Blob([new Uint8Array($0)], { type: $1 })")>]
let private makeOriginalBlob (bytes: byte[]) (mime: string) : obj = jsNative

[<Fable.Core.Emit("URL.createObjectURL($0)")>]
let private createOriginalObjectUrl (blob: obj) : string = jsNative

// Keep the object URL alive long enough for the new tab / download to
// read it, then release it.
[<Fable.Core.Emit("setTimeout(function () { URL.revokeObjectURL($0) }, 60000)")>]
let private revokeOriginalUrlLater (url: string) : unit = jsNative

// Returns the opened Window, or null when the browser blocked the popup.
[<Fable.Core.Emit("window.open($0, '_blank')")>]
let private openOriginalTab (url: string) : obj = jsNative

/// PDF viewers honour a `#page=N` fragment; other locator kinds have no
/// portable in-document anchor, so they degrade to opening at the top.
let private pageFragment (location: VK.SourceLocator option) : string =
    match location with
    | Some(VK.SourceLocator.Page n) -> sprintf "#page=%d" n
    | _ -> ""

let private openOriginal (original: OriginalDocument) (location: VK.SourceLocator option) : unit =
    let blob = makeOriginalBlob original.Content original.ContentType
    let url = createOriginalObjectUrl blob
    let win = openOriginalTab (url + pageFragment location)

    if isNull win then
        // Popup blocked — fall back to a download (loses the page deep
        // link but still delivers the file to the user).
        let a = Browser.Dom.document.createElement "a" :?> Browser.Types.HTMLAnchorElement

        a.href <- url
        a?download <- original.FileName
        Browser.Dom.document.body.appendChild a |> ignore
        a.click ()
        Browser.Dom.document.body.removeChild a |> ignore

    revokeOriginalUrlLater url

/// Companion-exported "view original" opener. Registered into
/// `ToolUp.Platform.OriginalDocumentBridge` by `KnowledgeBaseView.register`
/// so the AI Sources panel can open a citation's original document
/// without a compile-time edge onto the Knowledge Base.
let originalDocumentOpener: ToolUp.Platform.OriginalDocumentBridge.OriginalDocumentOpener =
    fun (originalRef: VK.OriginalDocumentRef) -> async {
        let! result = knowledgeApi.GetOriginalDocument originalRef.DocumentId

        match result with
        | Ok original ->
            openOriginal original originalRef.Location
            return Ok()
        // Out-of-scope is deliberately indistinguishable from absent —
        // no existence oracle (GP 4).
        | Error NotInScope -> return Error "This source isn't available."
        | Error NoOriginalAvailable -> return Error "This source has no original document to open."
        | Error(OriginalRetrievalFailed _) -> return Error "Couldn't open the original document. Please try again."
    }
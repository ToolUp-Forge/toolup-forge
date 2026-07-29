// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module PlatformKnowledgeAdminUI

open ToolUp.Elmish
open Feliz
open Fable.Core.JsInterop
open Toolup.UIToolkit
open ToolUp.Platform
open SharedTypes
open PlatformKnowledgeApi

// ─── Phase 4b — built-in Platform Knowledge Base admin module ────────
//
// SDK-built-in admin module surfacing `IPlatformKnowledgeApi` (Phase 4b
// commit 4e). Lets a Platform Admin upload, list, and delete documents
// in the cross-team Platform Knowledge Base. Sister module to the
// team-side `KnowledgeBase` (multi-page) module — separate registration
// because the audience and scope are different (Platform Admins,
// `_platform/knowledge/...` storage).
//
// **Sidebar group: "Platform Admin".** Sits alongside the role-mgmt
// `_sdk.PlatformAdmin` module and the re-grouped `_sdk.HealthMonitor`.
// The shell-level role gate (commit 4f.2) hides the entire group from
// non-admin callers.
//
// **Server-side gating.** Every write method on `IPlatformKnowledgeApi`
// is gated on `canModifyPlatformConfig`. `ListPlatformDocuments` is
// available to authenticated callers (the toggle gate from commit 5
// will tighten read access for non-admins later); admins always see
// the list so they can manage content.
//
// **Storage layout.** Per `_platform/knowledge/{docId}/{filename}` raw
// blobs and `_platform/knowledge/index.json` for the document list.
// Vectorisation routes chunks to `VectorScope.Platform`; the existing
// `IIngestionStatusObserver` keys on the storage container so progress
// notifications flow back to the uploading admin without further
// plumbing.

// ─── Model ───────────────────────────────────────────────────────────

type LoadState<'T> =
    | NotLoaded
    | Loading
    | Loaded of 'T
    | LoadError of string

type Model = {
    /// Live document list from `ListPlatformDocuments`. Refreshed on
    /// shell init, after every upload, and after every delete.
    Documents: LoadState<KnowledgeDocument list>
    /// `true` while an upload is in flight. Disables the input so the
    /// user can't double-submit; cleared on `UploadResolved`.
    Uploading: bool
    /// Inline error from the most recent upload attempt — `None` when
    /// no attempt yet, or after a successful one.
    UploadError: string option
    /// Inline error from a delete attempt, keyed by docId so the
    /// banner can render next to the right row.
    DeleteError: Map<string, string>
}

type Msg =
    | RefreshDocs
    | DocsLoaded of Result<KnowledgeDocument list, string>
    | UploadFile of bytes: byte[] * fileName: string
    | UploadResolved of Result<KnowledgeDocument, string>
    | RequestDelete of docId: string
    | DeleteResolved of docId: string * Result<unit, string>

// ─── API proxy ───────────────────────────────────────────────────────

let private platformKnowledgeApi =
    Api.makeProxy<IPlatformKnowledgeApi> (customOptions = UserSession.withRequestHeaders)

// ─── Init / commands ─────────────────────────────────────────────────

let private loadDocsCmd () =
    Cmd.OfRemoting.call platformKnowledgeApi.ListPlatformDocuments () (fun list -> DocsLoaded(Ok list)) (fun ex ->
        DocsLoaded(Error ex.Message))

let private uploadCmd (bytes: byte[]) (fileName: string) =
    Cmd.OfRemoting.call
        (fun () -> platformKnowledgeApi.UploadPlatformDocument bytes fileName)
        ()
        UploadResolved
        (fun ex -> UploadResolved(Error ex.Message))

let private deleteCmd (docId: string) =
    Cmd.OfRemoting.call
        platformKnowledgeApi.DeletePlatformDocument
        docId
        (fun result -> DeleteResolved(docId, result))
        (fun ex -> DeleteResolved(docId, Error ex.Message))

let init () =
    let model = {
        Documents = Loading
        Uploading = false
        UploadError = None
        DeleteError = Map.empty
    }

    model, loadDocsCmd ()

// ─── Update ──────────────────────────────────────────────────────────

let update (msg: Msg) (model: Model) =
    match msg with
    | RefreshDocs -> { model with Documents = Loading }, loadDocsCmd ()

    | DocsLoaded(Ok list) ->
        {
            model with
                Documents = Loaded(list |> List.sortByDescending _.UploadedAt)
        },
        Cmd.none

    | DocsLoaded(Error msg) -> { model with Documents = LoadError msg }, Cmd.none

    | UploadFile(bytes, fileName) ->
        {
            model with
                Uploading = true
                UploadError = None
        },
        uploadCmd bytes fileName

    | UploadResolved(Ok _) ->
        {
            model with
                Uploading = false
                UploadError = None
                Documents = Loading
        },
        loadDocsCmd ()

    | UploadResolved(Error msg) ->
        {
            model with
                Uploading = false
                UploadError = Some msg
        },
        Cmd.none

    | RequestDelete docId ->
        {
            model with
                DeleteError = model.DeleteError |> Map.remove docId
        },
        deleteCmd docId

    | DeleteResolved(_, Ok()) -> { model with Documents = Loading }, loadDocsCmd ()

    | DeleteResolved(docId, Error msg) ->
        {
            model with
                DeleteError = model.DeleteError |> Map.add docId msg
        },
        Cmd.none

// ─── View helpers ────────────────────────────────────────────────────

let private errorBanner (message: string) =
    Html.div [
        prop.className "px-3 py-2 my-2 rounded bg-red-50 border border-red-200 text-sm text-red-700"
        prop.text message
    ]

/// Per-row action for the shared KnowledgeListView: Delete button + any
/// inline error from the most-recent delete attempt against this row.
let private deleteRowAction (model: Model) (dispatch: Msg -> unit) (doc: KnowledgeDocument) =
    let deleteError = model.DeleteError |> Map.tryFind doc.Id

    Html.div [
        prop.className "flex flex-col items-end gap-1"
        prop.children [
            Html.button [
                prop.className "text-xs text-red-600 hover:text-red-800 font-medium"
                prop.text "Delete"
                prop.onClick (fun _ -> dispatch (RequestDelete doc.Id))
            ]
            match deleteError with
            | Some msg ->
                Html.span [
                    prop.className "text-xs text-red-600 max-w-[14rem] text-right"
                    prop.text msg
                ]
            | None -> Html.none
        ]
    ]

let private uploadInput (model: Model) (dispatch: Msg -> unit) =
    Html.input [
        prop.type' "file"
        prop.disabled model.Uploading
        prop.className "block text-sm text-text-secondary"
        prop.onChange (fun (e: Browser.Types.Event) ->
            let target = e.target :?> Browser.Types.HTMLInputElement
            let files = target.files

            if files.length > 0 then
                let file = files.item 0

                let reader = Browser.Dom.FileReader.Create()

                reader.onload <-
                    fun _ ->
                        let buffer: Fable.Core.JS.ArrayBuffer = unbox reader.result
                        let bytes: byte[] = unbox (createNew Fable.Core.JS.Constructors.Uint8Array buffer)
                        dispatch (UploadFile(bytes, file.name))

                reader.readAsArrayBuffer file
                target.value <- "")
    ]

let private uploadPanel (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex flex-col gap-2 p-3 rounded border border-border bg-white"
        prop.children [
            Html.h3 [
                prop.className "text-sm font-medium text-text-primary"
                prop.text "Upload to Platform Knowledge Base"
            ]
            Html.p [
                prop.className "text-xs text-text-secondary"
                prop.text
                    "Documents uploaded here are available to every authenticated user across every team in the deployment when the Platform Knowledge Base is enabled. Use for cross-team reference content (regulatory PDFs, deployment-wide style guides, shared methodology)."
            ]
            uploadInput model dispatch
            if model.Uploading then
                Html.p [ prop.className "text-xs text-text-secondary"; prop.text "Uploading…" ]
            match model.UploadError with
            | Some msg -> errorBanner msg
            | None -> Html.none
        ]
    ]

let private documentsPanel (model: Model) (dispatch: Msg -> unit) =
    let headerRow =
        Html.div [
            prop.className "flex items-center justify-between"
            prop.children [
                Html.h3 [
                    prop.className "text-sm font-medium text-text-primary"
                    prop.text "Platform Knowledge Base documents"
                ]
                Html.button [
                    prop.className "text-xs text-brand hover:underline"
                    prop.text "Refresh"
                    prop.onClick (fun _ -> dispatch RefreshDocs)
                ]
            ]
        ]

    let listBody =
        match model.Documents with
        | NotLoaded
        | Loading -> Html.p [ prop.className "text-sm text-text-secondary p-3"; prop.text "Loading…" ]
        | LoadError msg -> errorBanner msg
        | Loaded docs ->
            let config: KnowledgeListView.KnowledgeListConfig = {
                EmptyStateText = "No documents in the Platform Knowledge Base yet."
                RowAction = Some(deleteRowAction model dispatch)
                InstanceKey = "platform-admin"
            }

            KnowledgeListView.KnowledgeListView config docs

    Html.div [ prop.className "flex flex-col gap-3"; prop.children [ headerRow; listBody ] ]

/// Hosts the admin body inside a React component so the panel can
/// subscribe to the platform-wide `DataRefreshed("PlatformKnowledgeBase",
/// _)` fan-out. Without this, an admin watching the list in one tab
/// wouldn't see another admin's upload / delete / promote until they
/// clicked Refresh. The publisher (the admin doing the action) reloads
/// the list locally via `loadDocsCmd ()` in `update`; this subscription
/// covers the cross-session case.
[<ReactComponent>]
let private AdminPanel (model: Model) (dispatch: Msg -> unit) =
    React.useEffectOnce (fun () ->
        let dispose =
            NotificationClient.subscribe (fun envelope ->
                match envelope.Notification with
                | Notification.DataRefreshed("PlatformKnowledgeBase", _) -> dispatch RefreshDocs
                | _ -> ())

        FsReact.createDisposable (fun () -> dispose ()))

    Html.div [
        prop.className "p-4 flex flex-col gap-4 h-full overflow-y-auto"
        prop.children [ uploadPanel model dispatch; documentsPanel model dispatch ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
    AdminPanel model dispatch, Html.none

// ─── Module registration ─────────────────────────────────────────────

/// Register the Platform Knowledge Base admin module. Apps that list
/// their modules by hand call this in `Client.fs` alongside
/// `KnowledgeBaseView.register ()`.
///
/// Phase 1e — `KnowledgeBaseClientConfig.withKnowledgeBase` now appends
/// this module alongside the team-side one on
/// `DefaultKnowledgeBase` / `ConfiguredKnowledgeBase`. The v1 note here
/// deferred auto-injection on the grounds that the team-side module was
/// also deployment-explicit; `KnowledgeBaseMode` closes that, and the
/// registration shape stays consistent by moving together. The append is
/// unconditional because the sidebar entry is role-gated at render time
/// by its "Platform Management" group — the same argument
/// `AIClientConfig.appendAssistantModule` makes for `PlatformAIKeysAdminUI`.
///
/// Reserved Id `_sdk.PlatformKnowledgeAdmin`. Group "Platform Admin".
/// The shell-level role gate (commit 4f.2) hides the sidebar entry
/// from non-admin callers.
let register () : ToolUp.Platform.ErasedModule =
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = "Platform KB"
        Icon = ToolUp.KnowledgeBase.Icons.knowledge
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.PlatformKnowledgeAdmin"
    |> ToolUp.Platform.ClientModule.withView view
    |> ToolUp.Platform.ClientModule.withGroup "Platform Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.PlatformAdminOnly
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
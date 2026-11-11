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

type Model = {
    /// Live document list from `ListPlatformDocuments`. Refreshed on
    /// shell init, after every upload, and after every delete.
    Documents: Remote<KnowledgeDocument list, string>
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
        Documents = Remote.Loading
        Uploading = false
        UploadError = None
        DeleteError = Map.empty
    }

    model, loadDocsCmd ()

// ─── Update ──────────────────────────────────────────────────────────

let update (msg: Msg) (model: Model) =
    match msg with
    | RefreshDocs ->
        {
            model with
                Documents = Remote.Loading
        },
        loadDocsCmd ()

    | DocsLoaded result ->
        {
            model with
                Documents = result |> Result.map (List.sortByDescending _.UploadedAt) |> Remote.ofResult
        },
        Cmd.none

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
                Documents = Remote.Loading
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

    | DeleteResolved(_, Ok()) ->
        {
            model with
                Documents = Remote.Loading
        },
        loadDocsCmd ()

    | DeleteResolved(docId, Error msg) ->
        {
            model with
                DeleteError = model.DeleteError |> Map.add docId msg
        },
        Cmd.none

// ─── View helpers ────────────────────────────────────────────────────

/// Per-row action for the shared KnowledgeListView: Delete button + any
/// inline error from the most-recent delete attempt against this row.
let private deleteRowAction
    (msgs: KnowledgeAdminMessages)
    (model: Model)
    (dispatch: Msg -> unit)
    (doc: KnowledgeDocument)
    =
    let deleteError = model.DeleteError |> Map.tryFind doc.Id

    Html.div [
        prop.className "flex flex-col items-end gap-1"
        prop.children [
            Forms.Button.dangerWith Forms.Button.Enabled msgs.Delete (fun () -> dispatch (RequestDelete doc.Id))
            match deleteError with
            | Some msg ->
                Html.span [
                    prop.className [ "text-xs max-w-[14rem] text-right"; Tokens.Text.danger ]
                    prop.text msg
                ]
            | None -> Html.none
        ]
    ]

let private uploadInput (model: Model) (dispatch: Msg -> unit) =
    Html.input [
        prop.type' "file"
        prop.disabled model.Uploading
        prop.className [ "block text-sm"; Tokens.Text.secondary ]
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

let private uploadPanel (msgs: KnowledgeAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className [ "flex flex-col gap-2 p-3 rounded border border-border"; Tokens.Bg.card ]
        prop.children [
            Html.h3 [
                prop.className [ "text-sm font-medium"; Tokens.Text.primary ]
                prop.text msgs.UploadHeading
            ]
            Html.p [
                prop.className [ "text-xs"; Tokens.Text.secondary ]
                prop.text msgs.UploadDescription
            ]
            uploadInput model dispatch
            if model.Uploading then
                Html.p [
                    prop.className [ "text-xs"; Tokens.Text.secondary ]
                    prop.text msgs.Uploading
                ]
            match model.UploadError with
            | Some msg -> StateViews.noticeStatic (Notice.Problem msg)
            | None -> Html.none
        ]
    ]

let private documentsPanel (msgs: KnowledgeAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    let headerRow =
        Html.div [
            prop.className "flex items-center justify-between"
            prop.children [
                Html.h3 [
                    prop.className [ "text-sm font-medium"; Tokens.Text.primary ]
                    prop.text msgs.DocumentsHeading
                ]
                Forms.Button.secondaryWith
                    (Forms.Button.stateOf (Remote.isLoading model.Documents) msgs.Loading false)
                    msgs.Refresh
                    (fun () -> dispatch RefreshDocs)
            ]
        ]

    let listBody =
        match model.Documents with
        | Remote.NotAsked
        | Remote.Loading -> Html.p [ prop.className [ "text-sm p-3"; Tokens.Text.muted ]; prop.text msgs.Loading ]
        | Remote.Failed msg -> StateViews.noticeStatic (Notice.Problem msg)
        | Remote.Reloading docs
        | Remote.Loaded docs ->
            let config: KnowledgeListView.KnowledgeListConfig = {
                EmptyStateText = msgs.EmptyState
                RowAction = Some(deleteRowAction msgs model dispatch)
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
let private AdminPanel (msgs: KnowledgeAdminMessages) (model: Model) (dispatch: Msg -> unit) =

    React.useEffectOnce (fun () ->
        let dispose =
            NotificationClient.subscribe (fun envelope ->
                match envelope.Notification with
                | Notification.DataRefreshed("PlatformKnowledgeBase", _) -> dispatch RefreshDocs
                | _ -> ())

        FsReact.createDisposable (fun () -> dispose ()))

    Html.div [
        prop.className "p-4 flex flex-col gap-4 h-full overflow-y-auto"
        prop.children [ uploadPanel msgs model dispatch; documentsPanel msgs model dispatch ]
    ]

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.SplitPanel(AdminPanel ctx.Messages.KnowledgeBase.Admin model ctx.Dispatch, Html.none)

// ─── Module registration ─────────────────────────────────────────────

/// The Platform Knowledge Base admin module. Apps that list their modules
/// by hand add `ShellSlots.builtIn PlatformKnowledgeAdminUI.builtIn None`
/// in `Client.fs` alongside `KnowledgeBaseView.register ()`.
///
/// Phase 1e — `KnowledgeBaseClientConfig.withKnowledgeBase` now appends
/// this module alongside the team-side one on
/// `DefaultKnowledgeBase` / `ConfiguredKnowledgeBase`. The v1 note here
/// deferred auto-injection on the grounds that the team-side module was
/// also deployment-explicit; `KnowledgeBaseMode` closes that, and the
/// registration shape stays consistent by moving together. The append is
/// unconditional because the sidebar entry is role-gated at render time
/// by its "Platform Management" group — the same argument
/// the AI slots (`AIClientConfig.slotExtensions`) makes for `PlatformAIKeysAdminUI`.
///
/// Reserved Id `_sdk.PlatformKnowledgeAdmin`. Group "Platform Admin".
/// The shell-level role gate (commit 4f.2) hides the sidebar entry
/// from non-admin callers.
let builtIn: BuiltIn<Model, Msg> = {
    Id = "_sdk.PlatformKnowledgeAdmin"
    Defaults = {
        Name = "Platform KB"
        Icon = ToolUp.KnowledgeBase.Icons.knowledge
    }
    Group = Some "Platform Management"
    NavRole = Some ToolUp.Platform.NavRole.PlatformAdminOnly
    Area = Product
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = fun _ -> init ()
    Update = update
    View = BuiltInView.Page view
    Extend = id
}
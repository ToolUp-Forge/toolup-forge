// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module FileManagerUI

open ToolUp.Elmish
open Feliz
open Fable.Core.JsInterop
open Fable.SimpleJson
open Toolup.UIToolkit
open DataManagementTypes
open ProcessedDataTypes
open GeneralUITypes
open ToolUp.Platform
open ToolUp.Platform.AgGrid

// ─── Model ────────────────────────────────────────────────────────

type Model = {
    UploadedFiles: Map<DataTypeId, UploadedFileInfo list>
    CurrentUpload: RemoteData<UploadedFileInfo>
    ProcessedData: ProcessedFileEntry list
    /// Per-file RAG ingestion status (fileName → status), hydrated from
    /// `FileListSnapshot.Ingestion` and patched live via
    /// `DataManagerIngestionStatusKey` notifications (Phase 173). Empty
    /// when no RAG is composed ⇒ the status column is not rendered.
    IngestionStatus: Map<string, FileIngestionStatus>
    /// Client-side filter over the file list by ingestion status
    /// (Phase 220). `AllFiles` by default.
    StatusFilter: IngestionStatusFilter
    ErrorMessage: string option
    /// True while the initial (or post-delete refresh) `ListFiles` call
    /// is in flight. Drives the configured loading indicator in the
    /// "Uploaded Files" panel so an in-flight fetch is visually distinct
    /// from a genuinely-empty scope (both have `UploadedFiles = empty`).
    FilesLoading: bool
}

type Msg =
    | SelectFile of ApiCall<Browser.Types.File, DataFileUpload>
    | UploadFile of ApiCall<DataFileUpload, FileUploadResult>
    /// Hydrate the UI from the server's persisted state. Fires once on
    /// init so the user sees their existing files AND the matching
    /// processed-entry summaries (the server is the source of truth on
    /// every load — `Model.ProcessedData` would otherwise reset to `[]`
    /// on every page reload). Also re-fires after `DeleteFile` so the
    /// row drops out without us having to track which `fileName` the
    /// `Async<Result<unit, _>>` response corresponds to.
    | LoadFiles of ApiCall<unit, FileListSnapshot>
    /// Remove a file by name. Server-side cascades to processed-data,
    /// blob, and audit emission. We refresh the list on success rather
    /// than mutating the local map, so the source of truth stays the
    /// server.
    | DeleteFile of ApiCall<string, Result<unit, string>>
    /// User-triggered reprocess of a previously-uploaded file. Server
    /// re-runs `DataType.Process` on the persisted bytes, overwrites
    /// the entry sidecar, and returns the new entry. The escape hatch
    /// when an entry surfaced with an error after a server restart
    /// (e.g. detector logic changed, module renamed).
    | ReprocessFile of ApiCall<string, Result<ProcessedFileEntry, string>>
    /// Owner / Admin escape hatch — wipe every uploaded file in the
    /// scope. Server gates on `TeamRoles.canWriteTeamConfig` in Team
    /// modes; Member-role callers see the resulting `Error` surfaced
    /// in `ErrorMessage`. Confirmation dialog fires before the
    /// `Start` action lands here.
    | ResetDataStore of ApiCall<unit, Result<int, string>>
    /// A `DataManagerIngestionStatusKey` notification arrived — patch the
    /// named file's ingestion badge in place (no refetch). Fired from the
    /// `IngestionStatusSubscriber` component's notification handler.
    | IngestionStatusChanged of fileName: string * status: FileIngestionStatus
    /// Phase 220 — one-click re-ingest of a `Failed` file. No optimistic
    /// state change: the badge re-renders from the store via the live
    /// `Pending → Indexed` notification.
    | RetryIngestion of ApiCall<string, Result<unit, string>>
    /// Phase 220 — narrow the file list to one ingestion status (client-side).
    | SetStatusFilter of IngestionStatusFilter
    | ApiError of string
    | DismissError

// Header freshness is the CsrfClient request-guard's job — see `UserSession.withRequestHeaders` + `CsrfClient.installRequestGuard`.
let private fileApi: FileManagementApi =
    Api.makeProxy<FileManagementApi> (customOptions = UserSession.withRequestHeaders)

let init () =
    {
        UploadedFiles = Map.empty
        CurrentUpload = NotStarted
        ProcessedData = []
        IngestionStatus = Map.empty
        StatusFilter = AllFiles
        ErrorMessage = None
        FilesLoading = true
    },
    Cmd.ofMsg (LoadFiles(Start()))

let update msg model =
    match msg with
    | SelectFile action ->
        match action with
        | Start file ->
            // Read via a direct effect, not `Cmd.OfAsync.perform` +
            // `Async.FromContinuations`. Under Fable 5 that async path
            // silently no-ops (the deferred async start never runs the
            // reader), so the upload hangs with no error. An effect runs
            // synchronously and dispatches straight from the reader callbacks.
            {
                model with
                    CurrentUpload = Loading None
            },
            Cmd.ofEffect (fun dispatch ->
                let reader = Browser.Dom.FileReader.Create()

                reader.onload <-
                    fun _ ->
                        let upload = {
                            filename = file.name
                            contents = reader.result :?> string
                            dataType = "UnrecognisedData"
                        }

                        dispatch (SelectFile(Finished upload))

                reader.onerror <-
                    fun _ -> dispatch (ApiError(sprintf "Couldn't read '%s' — the file may be unreadable." file.name))

                reader.readAsText file)

        | Finished dataFileUpload -> model, Cmd.ofMsg (UploadFile(Start dataFileUpload))

    | UploadFile action ->
        match action with
        | Start dataFileUpload ->
            model,
            Cmd.OfRemoting.call fileApi.UploadFile { File = dataFileUpload } (Finished >> UploadFile) (fun ex ->
                ApiError ex.Message)

        | Finished result ->
            match result with
            | Ok response ->
                let fileInfo = response.FileInfo
                let processedData = model.ProcessedData @ [ response.Processed ]

                let existing =
                    model.UploadedFiles |> Map.tryFind fileInfo.DataType |> Option.defaultValue []

                {
                    model with
                        UploadedFiles = model.UploadedFiles |> Map.add fileInfo.DataType (existing @ [ fileInfo ])
                        CurrentUpload = Loaded fileInfo
                        ProcessedData = processedData
                },
                Cmd.none
            | Error errorMsg ->
                {
                    model with
                        CurrentUpload = NotStarted
                        ErrorMessage = Some errorMsg
                },
                Cmd.none

    | LoadFiles(Start()) ->
        { model with FilesLoading = true },
        Cmd.OfRemoting.call fileApi.ListFiles () (Finished >> LoadFiles) (fun ex -> ApiError ex.Message)

    | LoadFiles(Finished snapshot) ->
        // Server is the source of truth — both the file list and the
        // processed-entry summaries are replaced wholesale. The prior
        // "filter local list by valid filenames" pruning is no longer
        // needed: a deleted file's entry won't appear in
        // `snapshot.Processed`, so the assignment alone is sufficient.
        let grouped = snapshot.Files |> List.groupBy _.DataType |> Map.ofList

        {
            model with
                UploadedFiles = grouped
                ProcessedData = snapshot.Processed
                IngestionStatus = Map.ofList snapshot.Ingestion
                FilesLoading = false
        },
        Cmd.none

    | DeleteFile(Start fileName) ->
        model, Cmd.OfRemoting.call fileApi.DeleteFile fileName (Finished >> DeleteFile) (fun ex -> ApiError ex.Message)

    | DeleteFile(Finished(Ok())) ->
        // Re-fetch rather than splicing locally — keeps the client view
        // in sync with the server's processed-data and event-store side
        // effects of a delete (cascading audit emit, etc.) without us
        // having to mirror that logic.
        model, Cmd.ofMsg (LoadFiles(Start()))

    | DeleteFile(Finished(Error msg)) ->
        {
            model with
                ErrorMessage = Some(sprintf "Delete failed: %s" msg)
        },
        Cmd.none

    | ReprocessFile(Start fileName) ->
        model,
        Cmd.OfRemoting.call fileApi.ReprocessFile fileName (Finished >> ReprocessFile) (fun ex -> ApiError ex.Message)

    | ReprocessFile(Finished(Ok entry)) ->
        // Replace the existing entry in `ProcessedData` (matched by
        // `FileName`); append if none existed. The server has already
        // overwritten the persisted sidecar by the time we get here.
        let updated =
            let withoutOld =
                model.ProcessedData |> List.filter (fun e -> e.FileName <> entry.FileName)

            withoutOld @ [ entry ]

        { model with ProcessedData = updated }, Cmd.none

    | ReprocessFile(Finished(Error msg)) ->
        {
            model with
                ErrorMessage = Some(sprintf "Reprocess failed: %s" msg)
        },
        Cmd.none

    | ResetDataStore(Start()) ->
        model,
        Cmd.OfRemoting.call fileApi.ResetDataStore () (Finished >> ResetDataStore) (fun ex -> ApiError ex.Message)

    | ResetDataStore(Finished(Ok _)) ->
        // Re-fetch the file list rather than splicing locally — keeps
        // the client view in sync with whatever the server actually
        // wiped (defensive against count mismatches and partial
        // failures the API doesn't surface).
        {
            model with
                UploadedFiles = Map.empty
                ProcessedData = []
        },
        Cmd.ofMsg (LoadFiles(Start()))

    | ResetDataStore(Finished(Error msg)) ->
        {
            model with
                ErrorMessage = Some(sprintf "Reset failed: %s" msg)
        },
        Cmd.none

    | IngestionStatusChanged(fileName, status) ->
        {
            model with
                IngestionStatus = model.IngestionStatus |> Map.add fileName status
        },
        Cmd.none

    | RetryIngestion(Start fileName) ->
        model,
        Cmd.OfRemoting.call fileApi.RetryIngestion fileName (Finished >> RetryIngestion) (fun ex -> ApiError ex.Message)

    | RetryIngestion(Finished(Ok())) ->
        // No optimistic state — the server transitions the store to
        // `Pending` and the live notification flips the badge.
        model, Cmd.none

    | RetryIngestion(Finished(Error msg)) ->
        {
            model with
                ErrorMessage = Some(sprintf "Re-ingestion failed: %s" msg)
        },
        Cmd.none

    | SetStatusFilter filter -> { model with StatusFilter = filter }, Cmd.none

    | ApiError errorMsg ->
        {
            model with
                CurrentUpload = NotStarted
                ErrorMessage = Some errorMsg
                FilesLoading = false
        },
        Cmd.none

    | DismissError -> { model with ErrorMessage = None }, Cmd.none

// ─── View ─────────────────────────────────────────────────────────

/// Renders the deployment's configured loading indicator
/// (`ClientConfig.LoadingIndicator`) read from context. A
/// `[<ReactComponent>]` because `useIndicator` is a hook and must run in
/// a component body; mounted in the "Uploaded Files" panel while the
/// initial `ListFiles` is in flight.
[<ReactComponent>]
let private LoadingSlot () = LoadingIndicatorContext.useIndicator ()

type private UploadedFileRow = {
    DataType: DataTypeId
    Info: UploadedFileInfo
}

let private formatSize (bytes: int64) =
    if bytes < 1024L then
        $"{bytes} B"
    elif bytes < 1024L * 1024L then
        sprintf "%.1f KB" (float bytes / 1024.0)
    else
        sprintf "%.1f MB" (float bytes / (1024.0 * 1024.0))

let private labelFor (displays: DataTypeDisplay list) (dataTypeId: DataTypeId) =
    displays
    |> List.tryFind (fun d -> d.Info.Id = dataTypeId)
    |> Option.map _.Info.DisplayName
    |> Option.defaultValue dataTypeId

let private processedDataSection (displays: DataTypeDisplay list) (entries: ProcessedFileEntry list) =
    let grouped =
        entries
        |> List.choose (fun e ->
            match e.Info with
            | Some info ->
                displays
                |> List.tryFind (fun d -> d.Info.Id = e.DataType)
                |> Option.map (fun d -> d, info)
            | None -> None)
        |> List.groupBy (fun (d, _) -> d.Info.Id)

    let errors =
        entries
        |> List.choose (fun e -> e.Error |> Option.map (fun err -> e.FileName, err))

    Html.div [
        prop.className "space-y-4"
        prop.children [
            for (_, items) in grouped do
                let display = items |> List.head |> fst
                let infos = items |> List.map snd

                Typography.subheading display.Info.DisplayName
                display.RenderSummary infos

            if errors.Length > 0 then
                Typography.subheading "Processing Errors"

                for (fileName, error) in errors do
                    Html.div [
                        prop.className "flex items-center gap-2 py-1"
                        prop.children [
                            Html.span [ prop.className "text-red-600 text-sm"; prop.text "✗" ]
                            Html.span [ prop.className "text-sm text-gray-700"; prop.text $"{fileName}: {error}" ]
                        ]
                    ]
        ]
    ]

/// Per-file ingestion-status badge (Phase 173) — mirrors Knowledge
/// Base's per-document badge so a Data Manager file that isn't
/// searchable shows *why*. `NotIngested` renders nothing (absence ⇒
/// no badge), which is also the no-RAG case.
let private ingestionBadge (status: FileIngestionStatus) =
    match status with
    | FileIngestionStatus.Indexed ->
        Html.span [
            prop.className
                "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-700"
            prop.title "Indexed — searchable from the knowledge base."
            prop.text "Indexed"
        ]
    | FileIngestionStatus.Pending ->
        Html.span [
            prop.className "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-blue-100 text-blue-700"
            prop.title "Vectorisation in progress — not yet searchable."
            prop.text "Indexing…"
        ]
    | FileIngestionStatus.Failed reason ->
        Html.span [
            prop.className "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-red-100 text-red-700"
            prop.title reason
            prop.text "Not indexed"
        ]
    | FileIngestionStatus.NotIngested -> Html.none

/// Owns the live-update subscription for ingestion-status badges.
/// `React.useEffectOnce` subscribes on mount and disposes on unmount
/// (mirrors KB's `KbStatusBanner` lifecycle) so the badge flips
/// `Indexing… → Indexed` without a manual refresh. Renders nothing.
[<ReactComponent>]
let private IngestionStatusSubscriber (dispatch: Msg -> unit) =
    React.useEffectOnce (fun () ->
        let dispose =
            NotificationClient.subscribe (fun envelope ->
                match envelope.Notification with
                | Notification.CustomNotification(key, json) when key = DataManagerIngestionStatusKey ->
                    try
                        let update = Json.parseAs<DataManagerIngestionUpdate> json
                        dispatch (IngestionStatusChanged(update.FileName, update.Status))
                    with _ ->
                        ()
                | _ -> ())

        FsReact.createDisposable dispose)

    Html.none

/// Phase 220 — status-filter dropdown over the file list. Labels match
/// the badge vocabulary so the filter reads the same as what it selects.
let private filterLabel =
    function
    | AllFiles -> "All"
    | OnlyIndexed -> "Indexed"
    | OnlyPending -> "Indexing"
    | OnlyFailed -> "Not indexed"
    | OnlyNotIndexed -> "Not attempted"

let private allFilters = [ AllFiles; OnlyIndexed; OnlyPending; OnlyFailed; OnlyNotIndexed ]

let private statusFilterControl (model: Model) dispatch =
    Html.div [
        prop.className "flex items-center gap-2 mb-3"
        prop.children [
            Html.span [ prop.className "text-sm text-gray-600"; prop.text "Filter by index status:" ]
            Html.select [
                prop.className "border border-gray-300 rounded px-2 py-1 text-sm"
                prop.value (filterLabel model.StatusFilter)
                prop.onChange (fun (v: string) ->
                    match allFilters |> List.tryFind (fun f -> filterLabel f = v) with
                    | Some f -> dispatch (SetStatusFilter f)
                    | None -> ())
                prop.children [
                    for f in allFilters do
                        Html.option [ prop.value (filterLabel f); prop.text (filterLabel f) ]
                ]
            ]
        ]
    ]

let private view (displays: DataTypeDisplay list) model dispatch =
    let inputPanel =
        Layout.Panel.panel "Data Upload" [
            Layout.Panel.panelSection "Upload Files" [
                Html.div [
                    prop.className "flex items-center gap-4 flex-nowrap"
                    prop.children [
                        FilePicker.FilePicker(
                            true,
                            ".csv",
                            (fun files ->
                                for file in files do
                                    dispatch (SelectFile(Start file))),
                            Html.span [
                                prop.className [
                                    "cursor-pointer"
                                    Tokens.Colours.brand
                                    Tokens.Colours.brandText
                                    "px-6 py-2.5"
                                    Tokens.Typography.buttonText
                                    "rounded-lg"
                                    "hover:bg-brand-dark"
                                    "transition-colors"
                                    "inline-block"
                                    "text-center"
                                    "whitespace-nowrap"
                                    "flex-shrink-0"
                                ]
                                prop.text "CHOOSE FILES"
                            ]
                        )

                        Html.span [
                            prop.className "text-base text-gray-500"
                            prop.text "Select CSV files to upload — file types are detected automatically"
                        ]
                    ]
                ]
            ]
        ]

    let outputPanel =
        let allFiles =
            model.UploadedFiles
            |> Map.toList
            |> List.collect (fun (dataType, files) ->
                files |> List.map (fun info -> { DataType = dataType; Info = info }))
            |> Array.ofList

        // Phase 220 — client-side status filter over the already-fetched
        // status set (no round trip). `AllFiles` (and the no-RAG case,
        // where every status is `None`) passes everything through.
        let filteredFiles =
            allFiles
            |> Array.filter (fun row ->
                FileIngestionStatus.matchesFilter
                    model.StatusFilter
                    (model.IngestionStatus |> Map.tryFind row.Info.FileName))

        Layout.Panel.panel "Uploaded Files" [
            // Owns the ingestion-status live-update subscription (renders
            // nothing); mounted unconditionally so it subscribes even when
            // the file list is momentarily empty.
            IngestionStatusSubscriber dispatch

            // Phase 220 — status filter (only when RAG is composed and there
            // is at least one file to filter).
            if not (Map.isEmpty model.IngestionStatus) && allFiles.Length > 0 then
                statusFilterControl model dispatch

            match filteredFiles with
            | [||] when model.FilesLoading -> LoadingSlot()
            | [||] when allFiles.Length = 0 ->
                Html.p [ prop.className "text-gray-500"; prop.text "No files uploaded yet." ]
            | [||] ->
                Html.p [
                    prop.className "text-gray-500"
                    prop.text "No files match the selected index-status filter."
                ]
            | rows ->
                Html.div [
                    prop.className ThemeClass.Balham
                    prop.children [
                        AgGrid.grid [
                            !!(prop.custom ("theme", "legacy"))
                            AgGrid.domLayout AutoHeight
                            AgGrid.columnDefs [
                                ColumnDef.create [
                                    ColumnDef.headerName "Data Type"
                                    ColumnDef.valueGetter (fun r -> labelFor displays r.DataType)
                                ]
                                ColumnDef.create [
                                    ColumnDef.headerName "File Name"
                                    ColumnDef.valueGetter _.Info.FileName
                                ]
                                ColumnDef.create [
                                    ColumnDef.headerName "Uploaded"
                                    ColumnDef.valueGetter _.Info.UploadedAt
                                    ColumnDef.valueFormatter (fun p ->
                                        match p.value with
                                        | Some dt -> dt.ToString("yyyy/MM/dd HH:mm")
                                        | None -> "")
                                ]
                                ColumnDef.create [
                                    ColumnDef.headerName "Rows"
                                    ColumnDef.valueGetter _.Info.RowCount
                                    ColumnDef.columnType NumericColumn
                                ]
                                ColumnDef.create [
                                    ColumnDef.headerName "Size"
                                    ColumnDef.valueGetter _.Info.SizeBytes
                                    ColumnDef.valueFormatter (fun p ->
                                        match p.value with
                                        | Some bytes -> formatSize bytes
                                        | None -> "")
                                ]
                                // Ingestion-status column (Phase 173) — only
                                // when the deployment composes RAG (the snapshot
                                // then carries status). Absent ⇒ no column, so a
                                // non-RAG deployment renders exactly as before.
                                if not (Map.isEmpty model.IngestionStatus) then
                                    ColumnDef.create [
                                        ColumnDef.headerName "Search index"
                                        ColumnDef.cellRenderer (fun (p: ICellRendererParams<UploadedFileRow, obj>) ->
                                            match p.data with
                                            | Some row ->
                                                let status = model.IngestionStatus |> Map.tryFind row.Info.FileName

                                                Html.div [
                                                    prop.className "flex items-center gap-2"
                                                    prop.children [
                                                        match status with
                                                        | Some s -> ingestionBadge s
                                                        | None -> Html.none
                                                        // Phase 220 — one-click re-ingest on a Failed file.
                                                        if FileIngestionStatus.isRetryable status then
                                                            Html.button [
                                                                prop.className
                                                                    "text-xs text-blue-600 hover:text-blue-800 hover:underline"
                                                                prop.title
                                                                    "Re-run vectorisation for this file's persisted bytes."
                                                                prop.text "Retry"
                                                                prop.onClick (fun _ ->
                                                                    dispatch (RetryIngestion(Start row.Info.FileName)))
                                                            ]
                                                    ]
                                                ]
                                            | None -> Html.none)
                                    ]
                                ColumnDef.create [
                                    ColumnDef.headerName ""
                                    ColumnDef.cellRenderer (fun (p: ICellRendererParams<UploadedFileRow, obj>) ->
                                        match p.data with
                                        | Some row ->
                                            Html.div [
                                                prop.className "flex items-center gap-3"
                                                prop.children [
                                                    Html.button [
                                                        prop.className
                                                            "text-sm text-blue-600 hover:text-blue-800 hover:underline"
                                                        prop.title
                                                            "Re-run processing on this file's persisted bytes. Use this when the file's processed summary is missing or shows a stale-DataType error after a deploy."
                                                        prop.text "Reprocess"
                                                        prop.onClick (fun _ ->
                                                            dispatch (ReprocessFile(Start row.Info.FileName)))
                                                    ]
                                                    Html.button [
                                                        prop.className
                                                            "text-sm text-red-600 hover:text-red-800 hover:underline"
                                                        prop.title
                                                            "Delete this file. The processed data is removed from this scope and the underlying blob is purged."
                                                        prop.text "Delete"
                                                        prop.onClick (fun _ ->
                                                            let prompt =
                                                                sprintf
                                                                    "Delete %s? This removes the file from this scope and any analyses that depend on it will lose access."
                                                                    row.Info.FileName

                                                            if Browser.Dom.window.confirm prompt then
                                                                dispatch (DeleteFile(Start row.Info.FileName)))
                                                    ]
                                                ]
                                            ]
                                        | None -> Html.none)
                                ]
                            ]
                            AgGrid.rowData rows
                            AgGrid.onGridReady _.AutoSizeAllColumns()
                            AgGrid.enableCellTextSelection true
                            AgGrid.ensureDomOrder true
                            AgGrid.getRowId (fun r -> $"{r.DataType}-{r.Info.FileName}")
                        ]
                    ]
                ]

                if model.ProcessedData.Length > 0 then
                    Misc.divider
                    processedDataSection displays model.ProcessedData

                // Owner / Admin escape hatch. Shown only when there's
                // something to reset; non-Owner-Admin clicks land on
                // the server-side gate and surface the resulting
                // `Error` in `ErrorMessage`. The dialog's file count
                // matches what the server will actually wipe — both
                // numbers come from the same `UploadedFiles` map.
                let totalFileCount =
                    model.UploadedFiles |> Map.toList |> List.sumBy (fun (_, fs) -> fs.Length)

                Misc.divider

                Html.div [
                    prop.className "flex items-center justify-between gap-4"
                    prop.children [
                        Html.div [
                            prop.className "text-sm text-gray-600"
                            prop.text
                                "Reset removes every uploaded file and its derived data from this scope. Owner / Admin only on team deployments."
                        ]
                        Html.button [
                            prop.className
                                "text-sm px-3 py-1.5 rounded border border-red-300 text-red-700 hover:bg-red-50 hover:border-red-400 transition-colors whitespace-nowrap"
                            prop.title
                                "Wipe every file, processed-data summary, and entry sidecar in this scope. This cannot be undone."
                            prop.text "Reset data store"
                            prop.onClick (fun _ ->
                                let prompt =
                                    sprintf
                                        "Reset the data store? This permanently deletes %d file%s and every derived summary in this scope. Analyses depending on this data will lose access. This cannot be undone."
                                        totalFileCount
                                        (if totalFileCount = 1 then "" else "s")

                                if Browser.Dom.window.confirm prompt then
                                    dispatch (ResetDataStore(Start())))
                        ]
                    ]
                ]

            match model.ErrorMessage with
            | Some msg ->
                Html.div [
                    prop.className "mt-4 p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm"
                    prop.text msg
                ]
            | None -> ()
        ]

    inputPanel, outputPanel

// ─── Module creation ──────────────────────────────────────────────

/// Create the built-in file manager as an ErasedModule.
/// The DataTypeDisplay list determines how summaries are rendered.
/// Optional DataManagerConfig overrides the default name, icon, and
/// group. The defaults — "File Upload" in group "Data Management" —
/// keep file ingestion separate from any RAG / knowledge-base
/// companion that might also be wired in (those declare their own
/// "Knowledge" group).
let create (displays: DataTypeDisplay list) (config: DataManagerConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "File Upload"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.upload

    let group = config |> Option.bind _.Group |> Option.defaultValue "Data Management"

    let route = "/" + name.ToLower().Replace(" ", "-")

    // SDK-built-in module — `Id` is reserved under the `_sdk.` namespace so
    // it can never collide with an app's RBAC-managed `ServerConfig.ModuleNames`
    // key. Apps that swap in an `ExternalDataManager` set their own Id.
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.DataManager"
    |> ToolUp.Platform.ClientModule.withView (view displays)
    |> ToolUp.Platform.ClientModule.withProcessedData _.ProcessedData
    |> ToolUp.Platform.ClientModule.withGroup group
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
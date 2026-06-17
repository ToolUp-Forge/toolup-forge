// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Mapping-aware Data Manager — the opt-in replacement for the built-in
/// `FileManagerUI` that adds a front mapping stage so an arbitrary CSV
/// can be coerced into any registered, schema-bearing `DataType`:
///
///   upload CSV → pick the target format → auto-mapped fields (review
///   the flagged guesses, override as needed) → confirm.
///
/// On confirm the CSV is rewritten into the target schema's canonical
/// header shape (`ColumnMapping.rewriteCsv`) and uploaded with an
/// explicit `dataType`, so the existing `DataType.Process` runs
/// unchanged. The confirmed mapping is persisted via `IColumnMappingApi`
/// keyed by the source CSV's column-structure fingerprint, so the same
/// shape auto-applies on the next upload.
///
/// Selected by `ClientConfig.DataManager = MappingDataManager`; pair with
/// `ServerConfig.ColumnMapping = EnabledColumnMapping` to back the store.
module MappingDataManagerUI

open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open DataManagementTypes
open ProcessedDataTypes
open ColumnMappingTypes
open ColumnMappingApi
open ToolUp.Platform

// ─── Model ────────────────────────────────────────────────────────

type WizardStep =
    | PickTarget
    | ReviewMapping

type Wizard = {
    FileName: string
    RawContents: string
    Headers: string list
    Samples: Map<string, string list>
    Fingerprint: string
    Step: WizardStep
    TargetTypeId: DataTypeId option
    Suggestion: MappingSuggestion option
    /// User edits over the auto-suggestion: field name → chosen column
    /// (`None` = explicitly unmapped). Fields absent from the map use
    /// the suggestion's `SuggestedColumn`.
    Overrides: Map<string, string option>
    ReusedSaved: bool
    Saving: bool
}

/// The CSV currently in hand. Retained across imports so the user can
/// spawn additional data objects from the same file via "Make additional
/// mapping" without re-uploading.
type HeldFile = {
    FileName: string
    RawContents: string
    Headers: string list
    Samples: Map<string, string list>
    Fingerprint: string
}

type Model = {
    UploadedFiles: UploadedFileInfo list
    ProcessedData: ProcessedFileEntry list
    /// The file currently being worked (present from upload until the
    /// user dismisses the import result).
    Held: HeldFile option
    Wizard: Wizard option
    /// Target-type ids just imported (drives the result card + the
    /// "Make additional mapping" affordance). `None` when not showing a
    /// result.
    LastImport: DataTypeId list option
    Busy: bool
    Error: string option
}

type Msg =
    | LoadFiles
    | FilesLoaded of FileListSnapshot
    | SelectFile of Browser.Types.File
    | FileChosen of fileName: string * contents: string
    | MappingsFetched of ColumnMapping list
    | NativeUploaded of FileUploadResult
    | ImportFinished of Result<DataTypeId list, string>
    | MakeAdditionalMapping
    | DismissImport
    | SelectTarget of DataTypeId
    | ChangeFormat
    | OverrideColumn of field: string * column: string option
    | CancelWizard
    | ConfirmMapping
    | DeleteFile of string
    | ReprocessFile of string
    | Reprocessed of Result<ProcessedFileEntry, string>
    | ResetDataStore
    | ResetDone of Result<int, string>
    | ApiError of string
    | DismissError

let private fileApi: FileManagementApi =
    Api.makeProxy<FileManagementApi> (customOptions = UserSession.withRequestHeaders)

let private mappingApi: IColumnMappingApi =
    Api.makeProxy<IColumnMappingApi> (customOptions = UserSession.withRequestHeaders)

// ─── Mapping helpers (pure) ───────────────────────────────────────

let private schemaFor (displays: DataTypeDisplay list) (typeId: DataTypeId) : DataTypeSchema option =
    displays
    |> List.tryPick (fun d -> if d.Info.Id = typeId then d.Info.Schema else None)

/// The chosen source column for a field — user override if present,
/// otherwise the auto-suggestion.
let private chosenColumn (w: Wizard) (field: FieldSuggestion) : string option =
    match Map.tryFind field.Field.Name w.Overrides with
    | Some ov -> ov
    | None -> field.SuggestedColumn

/// field name → source column, for every field that resolves to a column.
let private effectiveMapping (w: Wizard) : Map<string, string> =
    match w.Suggestion with
    | None -> Map.empty
    | Some s ->
        s.Fields
        |> List.choose (fun f -> chosenColumn w f |> Option.map (fun c -> f.Field.Name, c))
        |> Map.ofList

/// Required fields that still have no column — block confirmation.
let private unresolvedRequired (w: Wizard) : string list =
    match w.Suggestion with
    | None -> []
    | Some s ->
        s.Fields
        |> List.filter (fun f -> f.Field.Required && (chosenColumn w f).IsNone)
        |> List.map (fun f -> f.Field.Name)

/// A fresh wizard at the target-picker step for a held file.
let private wizardFor (held: HeldFile) : Wizard = {
    FileName = held.FileName
    RawContents = held.RawContents
    Headers = held.Headers
    Samples = held.Samples
    Fingerprint = held.Fingerprint
    Step = PickTarget
    TargetTypeId = None
    Suggestion = None
    Overrides = Map.empty
    ReusedSaved = false
    Saving = false
}

/// Disambiguate the uploaded file name per target type so several mapped
/// imports of one source file land as distinct data objects rather than
/// overwriting each other (`AddFile` keys by file name). `data.csv` +
/// `SalesData` → `data__SalesData.csv`.
let private importedFileName (baseName: string) (typeId: DataTypeId) : string =
    let dot = baseName.LastIndexOf '.'

    if dot > 0 then
        baseName.Substring(0, dot) + "__" + typeId + baseName.Substring dot
    else
        baseName + "__" + typeId

/// Rewrite + upload each mapping as its own data object. Returns the
/// target-type ids imported, or the first error. Mappings whose target
/// type is no longer registered are skipped.
let private importMappings
    (displays: DataTypeDisplay list)
    (held: HeldFile)
    (mappings: ColumnMapping list)
    : Async<Result<DataTypeId list, string>> =
    async {
        let mutable err = None
        let mutable imported = []

        for m in mappings do
            if err.IsNone then
                match schemaFor displays m.TargetTypeId with
                | Some schema ->
                    let rewritten = ColumnMapping.rewriteCsv schema m.FieldToColumn held.RawContents

                    let upload = {
                        filename = importedFileName held.FileName m.TargetTypeId
                        contents = rewritten
                        dataType = m.TargetTypeId
                    }

                    match! fileApi.UploadFile { File = upload } with
                    | Ok _ -> imported <- imported @ [ m.TargetTypeId ]
                    | Error e -> err <- Some e
                | None -> ()

        match err with
        | Some e -> return Error e
        | None -> return Ok imported
    }

/// Persist a freshly-confirmed mapping, then import it as its own data
/// object.
let private saveAndImport
    (displays: DataTypeDisplay list)
    (held: HeldFile)
    (mapping: ColumnMapping)
    : Async<Result<DataTypeId list, string>> =
    async {
        match! mappingApi.SaveMapping mapping with
        | Error e -> return Error e
        | Ok() -> return! importMappings displays held [ mapping ]
    }

// ─── Update ───────────────────────────────────────────────────────

let init () =
    {
        UploadedFiles = []
        ProcessedData = []
        Held = None
        Wizard = None
        LastImport = None
        Busy = false
        Error = None
    },
    Cmd.ofMsg LoadFiles

let update (displays: DataTypeDisplay list) (msg: Msg) (model: Model) =
    match msg with
    | LoadFiles -> model, Cmd.OfRemoting.call fileApi.ListFiles () FilesLoaded (fun ex -> ApiError ex.Message)

    | FilesLoaded snapshot ->
        {
            model with
                UploadedFiles = snapshot.Files
                ProcessedData = snapshot.Processed
        },
        Cmd.none

    | SelectFile file ->
        let read () = async {
            let reader = Browser.Dom.FileReader.Create()

            let! content =
                Async.FromContinuations(fun (cont, _, _) ->
                    reader.onload <- fun _ -> cont (reader.result :?> string)
                    reader.readAsText file)

            return file.name, content
        }

        model, Cmd.OfAsync.perform read () (fun (n, c) -> FileChosen(n, c))

    | FileChosen(fileName, contents) ->
        let headers, samples = ColumnMapping.parsePreview 20 contents
        let fingerprint = ColumnMapping.Fingerprint.ofHeaders headers

        let held = {
            FileName = fileName
            RawContents = contents
            Headers = headers
            Samples = samples
            Fingerprint = fingerprint
        }

        {
            model with
                Held = Some held
                Wizard = None
                LastImport = None
                Busy = true
                Error = None
        },
        Cmd.OfRemoting.call mappingApi.GetMappings fingerprint MappingsFetched (fun ex -> ApiError ex.Message)

    | MappingsFetched saved ->
        match model.Held with
        | None -> { model with Busy = false }, Cmd.none
        | Some held ->
            // Frictionless re-import when the structure is already known:
            // apply every saved mapping (skipping ones whose target type
            // is no longer registered). With none usable, let the server
            // attempt native detection of the file as-is.
            let importable =
                saved |> List.filter (fun m -> (schemaFor displays m.TargetTypeId).IsSome)

            if not importable.IsEmpty then
                model,
                Cmd.OfAsync.either (importMappings displays held) importable ImportFinished (fun ex ->
                    ApiError ex.Message)
            else
                let upload = {
                    filename = held.FileName
                    contents = held.RawContents
                    dataType = "UnrecognisedData"
                }

                model,
                Cmd.OfRemoting.call fileApi.UploadFile { File = upload } NativeUploaded (fun ex -> ApiError ex.Message)

    | NativeUploaded result ->
        match result with
        | Ok resp when resp.Processed.DataType <> "UnrecognisedData" && resp.Processed.Error.IsNone ->
            // The file natively matched a registered type — frictionless
            // import done; nothing to map.
            {
                model with
                    Busy = false
                    LastImport = Some [ resp.Processed.DataType ]
            },
            Cmd.ofMsg LoadFiles
        | Ok resp ->
            // Not recognised: drop the stray `UnrecognisedData` upload and
            // open the mapping wizard for manual mapping.
            let stray = resp.FileInfo.FileName

            let wizard = model.Held |> Option.map wizardFor

            {
                model with
                    Busy = false
                    Wizard = wizard
                    LastImport = None
            },
            Cmd.OfRemoting.call fileApi.DeleteFile stray (fun _ -> LoadFiles) (fun ex -> ApiError ex.Message)
        | Error e ->
            {
                model with
                    Busy = false
                    Error = Some e
            },
            Cmd.none

    | ImportFinished(Ok labels) ->
        if labels.IsEmpty then
            // Nothing imported (e.g. every saved mapping went stale) — fall
            // back to the wizard.
            {
                model with
                    Busy = false
                    Wizard = model.Held |> Option.map wizardFor
            },
            Cmd.none
        else
            {
                model with
                    Busy = false
                    Wizard = None
                    LastImport = Some labels
            },
            Cmd.ofMsg LoadFiles

    | ImportFinished(Error msg) ->
        {
            model with
                Busy = false
                Wizard = model.Wizard |> Option.map (fun w -> { w with Saving = false })
                Error = Some msg
        },
        Cmd.none

    | MakeAdditionalMapping ->
        match model.Held with
        | Some held ->
            {
                model with
                    Wizard = Some(wizardFor held)
                    LastImport = None
            },
            Cmd.none
        | None -> model, Cmd.none

    | DismissImport ->
        {
            model with
                Held = None
                LastImport = None
        },
        Cmd.none

    | SelectTarget typeId ->
        match model.Wizard with
        | Some w ->
            match schemaFor displays typeId with
            | Some schema ->
                let suggestion = ColumnMapping.suggest typeId schema w.Headers w.Samples

                let w' = {
                    w with
                        TargetTypeId = Some typeId
                        Suggestion = Some suggestion
                        Overrides = Map.empty
                        ReusedSaved = false
                        Step = ReviewMapping
                }

                { model with Wizard = Some w' }, Cmd.none
            | None ->
                {
                    model with
                        Error = Some "The selected data type publishes no schema, so it can't be mapped."
                },
                Cmd.none
        | None -> model, Cmd.none

    | ChangeFormat ->
        match model.Wizard with
        | Some w ->
            {
                model with
                    Wizard =
                        Some {
                            w with
                                Step = PickTarget
                                TargetTypeId = None
                                Suggestion = None
                                Overrides = Map.empty
                                ReusedSaved = false
                        }
            },
            Cmd.none
        | None -> model, Cmd.none

    | OverrideColumn(field, col) ->
        match model.Wizard with
        | Some w ->
            {
                model with
                    Wizard =
                        Some {
                            w with
                                Overrides = Map.add field col w.Overrides
                        }
            },
            Cmd.none
        | None -> model, Cmd.none

    | CancelWizard ->
        {
            model with
                Wizard = None
                Held = None
        },
        Cmd.none

    | ConfirmMapping ->
        match model.Wizard, model.Held with
        | Some w, Some held when w.TargetTypeId.IsSome ->
            let typeId = w.TargetTypeId.Value

            match schemaFor displays typeId with
            | Some _ ->
                let record = {
                    Fingerprint = w.Fingerprint
                    TargetTypeId = typeId
                    FieldToColumn = effectiveMapping w
                    SourceHeaders = w.Headers
                    CreatedBy = ""
                    CreatedAt = System.DateTime.UtcNow
                }

                // Save the mapping (additive — keyed by fingerprint+type),
                // then import this one as its own data object.
                {
                    model with
                        Wizard = Some { w with Saving = true }
                },
                Cmd.OfAsync.either (saveAndImport displays held) record ImportFinished (fun ex -> ApiError ex.Message)
            | None ->
                {
                    model with
                        Error = Some "The selected data type publishes no schema."
                },
                Cmd.none
        | _ -> model, Cmd.none

    | DeleteFile fileName ->
        model, Cmd.OfRemoting.call fileApi.DeleteFile fileName (fun _ -> LoadFiles) (fun ex -> ApiError ex.Message)

    | ReprocessFile fileName ->
        model, Cmd.OfRemoting.call fileApi.ReprocessFile fileName Reprocessed (fun ex -> ApiError ex.Message)

    | Reprocessed(Ok entry) ->
        // The server has already overwritten the persisted sidecar by the
        // time we get here — replace the entry in `ProcessedData` (matched
        // by `FileName`), or append if none existed.
        let updated =
            (model.ProcessedData |> List.filter (fun e -> e.FileName <> entry.FileName))
            @ [ entry ]

        { model with ProcessedData = updated }, Cmd.none

    | Reprocessed(Error msg) ->
        {
            model with
                Error = Some(sprintf "Reprocess failed: %s" msg)
        },
        Cmd.none

    | ResetDataStore -> model, Cmd.OfRemoting.call fileApi.ResetDataStore () ResetDone (fun ex -> ApiError ex.Message)

    | ResetDone(Ok _) ->
        // Re-fetch rather than trusting the local view — keeps the client
        // in sync with whatever the server actually wiped.
        {
            model with
                UploadedFiles = []
                ProcessedData = []
        },
        Cmd.ofMsg LoadFiles

    | ResetDone(Error msg) ->
        {
            model with
                Error = Some(sprintf "Reset failed: %s" msg)
        },
        Cmd.none

    | ApiError errorMsg ->
        let wizard = model.Wizard |> Option.map (fun w -> { w with Saving = false })

        {
            model with
                Wizard = wizard
                Busy = false
                Error = Some errorMsg
        },
        Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

// ─── View ─────────────────────────────────────────────────────────

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

let private columnTypeName =
    function
    | StringColumn -> "Text"
    | NumberColumn -> "Number"
    | DateColumn -> "Date"
    | BooleanColumn -> "Boolean"

/// (badge text, tailwind colour classes, is-warning) for a match flag.
let private flagBadge =
    function
    | Confident -> "OK", "text-green-700 bg-green-50 border-green-200", false
    | LowConfidence -> "Low confidence", "text-amber-700 bg-amber-50 border-amber-200", true
    | TypeMismatch -> "Type mismatch", "text-amber-700 bg-amber-50 border-amber-200", true
    | Ambiguous -> "Ambiguous", "text-amber-700 bg-amber-50 border-amber-200", true
    | Unmatched -> "Unmatched", "text-red-700 bg-red-50 border-red-200", true

let private columnSelect (w: Wizard) (field: FieldSuggestion) dispatch =
    let current = chosenColumn w field |> Option.defaultValue ""

    Html.select [
        prop.className "border border-gray-300 rounded px-2 py-1 text-sm w-full"
        prop.value current
        prop.onChange (fun (v: string) ->
            let col = if v = "" then None else Some v
            dispatch (OverrideColumn(field.Field.Name, col)))
        prop.children [
            Html.option [ prop.value ""; prop.text "— not mapped —" ]
            for h in w.Headers do
                Html.option [ prop.value h; prop.text h ]
        ]
    ]

let private reviewListView (suggestion: MappingSuggestion) =
    let flagged = suggestion.Fields |> List.filter (fun f -> f.Flag <> Confident)

    if flagged.IsEmpty then
        Html.none
    else
        Html.div [
            prop.className "mt-3 p-3 rounded border border-amber-200 bg-amber-50"
            prop.children [
                Html.div [
                    prop.className "text-sm font-semibold text-amber-800 mb-1"
                    prop.text "⚠ Auto-mapped — please double-check these fields"
                ]
                Html.ul [
                    prop.className "list-disc list-inside text-sm text-amber-800 space-y-0.5"
                    prop.children [
                        for f in flagged do
                            let label, _, _ = flagBadge f.Flag

                            let detail =
                                match f.SuggestedColumn with
                                | Some c -> $"→ guessed \"{c}\""
                                | None -> "→ no column found"

                            Html.li [ prop.text $"{f.Field.Name} ({label}) {detail}" ]
                    ]
                ]
            ]
        ]

let private mappingGridView (w: Wizard) (suggestion: MappingSuggestion) dispatch =
    Html.div [
        prop.className "overflow-x-auto"
        prop.children [
            Html.table [
                prop.className "w-full text-sm border-collapse"
                prop.children [
                    Html.thead [
                        Html.tr [
                            prop.className "text-left text-gray-600 border-b border-gray-200"
                            prop.children [
                                Html.th [ prop.className "py-2 pr-3"; prop.text "Target field" ]
                                Html.th [ prop.className "py-2 pr-3"; prop.text "Type" ]
                                Html.th [ prop.className "py-2 pr-3"; prop.text "CSV column" ]
                                Html.th [ prop.className "py-2 pr-3"; prop.text "Match" ]
                            ]
                        ]
                    ]
                    Html.tbody [
                        prop.children [
                            for f in suggestion.Fields do
                                let badgeText, badgeClass, _ = flagBadge f.Flag

                                Html.tr [
                                    prop.className "border-b border-gray-100 align-top"
                                    prop.children [
                                        Html.td [
                                            prop.className "py-2 pr-3"
                                            prop.children [
                                                Html.span [ prop.className "font-medium"; prop.text f.Field.Name ]
                                                if f.Field.Required then
                                                    Html.span [
                                                        prop.className "text-red-600 ml-0.5"
                                                        prop.title "Required"
                                                        prop.text "*"
                                                    ]
                                            ]
                                        ]
                                        Html.td [
                                            prop.className "py-2 pr-3 text-gray-500"
                                            prop.text (columnTypeName f.Field.Type)
                                        ]
                                        Html.td [
                                            prop.className "py-2 pr-3 min-w-48"
                                            prop.children [ columnSelect w f dispatch ]
                                        ]
                                        Html.td [
                                            prop.className "py-2 pr-3"
                                            prop.children [
                                                Html.span [
                                                    prop.className
                                                        $"inline-block px-2 py-0.5 rounded border text-xs {badgeClass}"
                                                    prop.text badgeText
                                                ]
                                            ]
                                        ]
                                    ]
                                ]
                        ]
                    ]
                ]
            ]
        ]
    ]

let private wizardView (displays: DataTypeDisplay list) (w: Wizard) dispatch =
    let header =
        Html.div [
            prop.className "flex items-center justify-between"
            prop.children [
                Typography.subheading $"Map: {w.FileName}"
                Html.button [
                    prop.className "text-sm text-gray-600 hover:text-gray-900 hover:underline"
                    prop.text "Cancel"
                    prop.onClick (fun _ -> dispatch CancelWizard)
                ]
            ]
        ]

    let body =
        match w.Step with
        | PickTarget ->
            let tabular = displays |> List.filter (fun d -> d.Info.Schema.IsSome)

            Html.div [
                prop.className "space-y-3"
                prop.children [
                    Html.p [
                        prop.className "text-sm text-gray-600"
                        prop.text $"Detected {w.Headers.Length} columns. Choose the data format to map this CSV into:"
                    ]
                    match tabular with
                    | [] ->
                        Html.p [
                            prop.className "text-sm text-gray-500"
                            prop.text "No schema-bearing data types are registered in this deployment."
                        ]
                    | ds ->
                        Html.div [
                            prop.className "flex flex-col gap-2"
                            prop.children [
                                for d in ds do
                                    Html.button [
                                        prop.className
                                            "text-left px-3 py-2 rounded border border-gray-300 hover:border-brand hover:bg-gray-50 transition-colors"
                                        prop.onClick (fun _ -> dispatch (SelectTarget d.Info.Id))
                                        prop.children [
                                            Html.div [
                                                prop.className "font-medium text-sm"
                                                prop.text d.Info.DisplayName
                                            ]
                                            match d.Info.Schema with
                                            | Some schema ->
                                                Html.div [
                                                    prop.className "text-xs text-gray-500"
                                                    prop.text schema.Description
                                                ]
                                            | None -> ()
                                        ]
                                    ]
                            ]
                        ]
                ]
            ]
        | ReviewMapping ->
            match w.Suggestion with
            | None -> Html.none
            | Some suggestion ->
                let blockers = unresolvedRequired w

                Html.div [
                    prop.className "space-y-3"
                    prop.children [
                        Html.div [
                            prop.className "flex items-center justify-between"
                            prop.children [
                                Html.div [
                                    prop.className "text-sm text-gray-600"
                                    prop.text $"Mapping to: {labelFor displays suggestion.TargetTypeId}"
                                ]
                                Html.button [
                                    prop.className "text-xs text-gray-500 hover:underline"
                                    prop.text "Change format"
                                    prop.onClick (fun _ -> dispatch ChangeFormat)
                                ]
                            ]
                        ]

                        if w.ReusedSaved then
                            Html.div [
                                prop.className "p-2 rounded border border-blue-200 bg-blue-50 text-sm text-blue-800"
                                prop.text "Reused a saved mapping for this column structure. Review before confirming."
                            ]

                        reviewListView suggestion
                        mappingGridView w suggestion dispatch

                        if not blockers.IsEmpty then
                            Html.div [
                                prop.className "text-sm text-red-700"
                                prop.text (
                                    "Required field"
                                    + (if blockers.Length = 1 then "" else "s")
                                    + " still unmapped: "
                                    + String.concat ", " blockers
                                )
                            ]

                        Html.div [
                            prop.className "flex items-center gap-3 pt-1"
                            prop.children [
                                Html.button [
                                    prop.className [
                                        "px-4 py-2 rounded-lg text-sm"
                                        Tokens.Typography.buttonText
                                        if blockers.IsEmpty && not w.Saving then
                                            Tokens.Colours.brand
                                            + " "
                                            + Tokens.Colours.brandText
                                            + " hover:bg-brand-dark cursor-pointer"
                                        else
                                            "bg-gray-200 text-gray-500 cursor-not-allowed"
                                    ]
                                    prop.disabled (not blockers.IsEmpty || w.Saving)
                                    prop.text (if w.Saving then "Importing…" else "Confirm & import")
                                    prop.onClick (fun _ ->
                                        if blockers.IsEmpty && not w.Saving then
                                            dispatch ConfirmMapping)
                                ]
                                Html.span [
                                    prop.className "text-xs text-gray-500"
                                    prop.text "The mapping is saved and reused for CSVs with the same columns."
                                ]
                            ]
                        ]
                    ]
                ]

    Layout.Panel.panel "Column Mapping" [ Layout.Panel.panelSection "" [ header; Misc.divider; body ] ]

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

    Html.div [
        prop.className "space-y-4"
        prop.children [
            for (_, items) in grouped do
                let display = items |> List.head |> fst
                let infos = items |> List.map snd
                Typography.subheading display.Info.DisplayName
                display.RenderSummary infos
        ]
    ]

let private filesView (displays: DataTypeDisplay list) (model: Model) dispatch =
    match model.UploadedFiles with
    | [] -> Html.p [ prop.className "text-gray-500"; prop.text "No files imported yet." ]
    | files ->
        Html.div [
            prop.className "space-y-4"
            prop.children [
                Html.table [
                    prop.className "w-full text-sm border-collapse"
                    prop.children [
                        Html.thead [
                            Html.tr [
                                prop.className "text-left text-gray-600 border-b border-gray-200"
                                prop.children [
                                    Html.th [ prop.className "py-2 pr-3"; prop.text "Data Type" ]
                                    Html.th [ prop.className "py-2 pr-3"; prop.text "File Name" ]
                                    Html.th [ prop.className "py-2 pr-3"; prop.text "Rows" ]
                                    Html.th [ prop.className "py-2 pr-3"; prop.text "Size" ]
                                    Html.th [ prop.className "py-2 pr-3"; prop.text "" ]
                                ]
                            ]
                        ]
                        Html.tbody [
                            prop.children [
                                for f in files do
                                    Html.tr [
                                        prop.className "border-b border-gray-100"
                                        prop.children [
                                            Html.td [
                                                prop.className "py-2 pr-3"
                                                prop.text (labelFor displays f.DataType)
                                            ]
                                            Html.td [ prop.className "py-2 pr-3"; prop.text f.FileName ]
                                            Html.td [ prop.className "py-2 pr-3"; prop.text (string f.RowCount) ]
                                            Html.td [ prop.className "py-2 pr-3"; prop.text (formatSize f.SizeBytes) ]
                                            Html.td [
                                                prop.className "py-2 pr-3"
                                                prop.children [
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
                                                                    dispatch (ReprocessFile f.FileName))
                                                            ]
                                                            Html.button [
                                                                prop.className
                                                                    "text-sm text-red-600 hover:text-red-800 hover:underline"
                                                                prop.text "Delete"
                                                                prop.onClick (fun _ ->
                                                                    let prompt =
                                                                        sprintf
                                                                            "Delete %s? Analyses depending on it will lose access."
                                                                            f.FileName

                                                                    if Browser.Dom.window.confirm prompt then
                                                                        dispatch (DeleteFile f.FileName))
                                                            ]
                                                        ]
                                                    ]
                                                ]
                                            ]
                                        ]
                                    ]
                            ]
                        ]
                    ]
                ]

                if not model.ProcessedData.IsEmpty then
                    Misc.divider
                    processedDataSection displays model.ProcessedData

                // Owner / Admin escape hatch — same server-gated reset as
                // the built-in Data Manager. Shown only when there's
                // something to wipe; non-Owner-Admin clicks land on the
                // server-side gate and surface the resulting `Error`.
                let totalFileCount = model.UploadedFiles.Length

                Misc.divider

                Html.div [
                    prop.className "flex items-center justify-between gap-4"
                    prop.children [
                        Html.div [
                            prop.className "text-sm text-gray-600"
                            prop.text
                                "Reset removes every imported file and its derived data from this scope. Owner / Admin only on team deployments."
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
                                        "Reset the data store? This permanently deletes %d file%s and every derived summary in this scope. This cannot be undone."
                                        totalFileCount
                                        (if totalFileCount = 1 then "" else "s")

                                if Browser.Dom.window.confirm prompt then
                                    dispatch ResetDataStore)
                        ]
                    ]
                ]
            ]
        ]

/// Result card shown after a frictionless (saved-mapping or native)
/// import — confirms what landed and offers to spawn another data object
/// from the same file.
let private importResultCard (displays: DataTypeDisplay list) (labels: DataTypeId list) (canAddMore: bool) dispatch =
    Layout.Panel.panel "Imported" [
        Html.div [
            prop.className "space-y-3"
            prop.children [
                Html.div [
                    prop.className "p-3 rounded border border-green-200 bg-green-50 text-sm text-green-800"
                    prop.text (
                        sprintf "✓ Imported as %s." (labels |> List.map (labelFor displays) |> String.concat ", ")
                    )
                ]
                Html.div [
                    prop.className "text-sm text-gray-600"
                    prop.text
                        "A single file can map to several formats — add another mapping to spawn an additional data object from the same file."
                ]
                Html.div [
                    prop.className "flex items-center gap-3"
                    prop.children [
                        if canAddMore then
                            Html.button [
                                prop.className [
                                    Tokens.Colours.brand
                                    Tokens.Colours.brandText
                                    "px-4 py-2 rounded-lg text-sm cursor-pointer hover:bg-brand-dark transition-colors"
                                    Tokens.Typography.buttonText
                                ]
                                prop.text "Make additional mapping"
                                prop.onClick (fun _ -> dispatch MakeAdditionalMapping)
                            ]
                        Html.button [
                            prop.className
                                "text-sm px-4 py-2 rounded-lg border border-gray-300 text-gray-700 hover:bg-gray-50"
                            prop.text "Done"
                            prop.onClick (fun _ -> dispatch DismissImport)
                        ]
                    ]
                ]
            ]
        ]
    ]

let private view (displays: DataTypeDisplay list) (model: Model) dispatch =
    let inputPanel =
        Layout.Panel.panel "Import CSV" [
            Layout.Panel.panelSection "Upload a file" [
                Html.div [
                    prop.className "flex items-center gap-4 flex-nowrap"
                    prop.children [
                        FilePicker.FilePicker(
                            false,
                            ".csv",
                            (fun files -> files |> Seq.truncate 1 |> Seq.iter (fun file -> dispatch (SelectFile file))),
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
                                prop.text "CHOOSE CSV"
                            ]
                        )
                        Html.span [
                            prop.className "text-base text-gray-500"
                            prop.text (
                                if model.Busy then
                                    "Checking for a known structure…"
                                else
                                    "Upload any CSV. Known structures re-import automatically; new ones open the mapper."
                            )
                        ]
                    ]
                ]
            ]
        ]

    let outputPanel =
        Html.div [
            prop.children [
                match model.Wizard with
                | Some w -> wizardView displays w dispatch
                | None ->
                    match model.LastImport with
                    | Some labels -> importResultCard displays labels model.Held.IsSome dispatch
                    | None -> ()

                    Layout.Panel.panel "Imported Files" [ filesView displays model dispatch ]

                match model.Error with
                | Some msg ->
                    Html.div [
                        prop.className
                            "mt-4 p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm flex items-center justify-between"
                        prop.children [
                            Html.span [ prop.text msg ]
                            Html.button [
                                prop.className "text-red-500 hover:text-red-700 ml-3"
                                prop.text "✕"
                                prop.onClick (fun _ -> dispatch DismissError)
                            ]
                        ]
                    ]
                | None -> ()
            ]
        ]

    inputPanel, outputPanel

// ─── Module creation ──────────────────────────────────────────────

/// Create the mapping-aware Data Manager as an `ErasedModule`. `displays`
/// supplies the registered data types (and their schemas) the wizard maps
/// into — same list the built-in `FileManagerUI` receives. Optional
/// `DataManagerConfig` overrides the name / icon / sidebar group.
let create (displays: DataTypeDisplay list) (config: DataManagerConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Import & Map"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.upload

    let group = config |> Option.bind _.Group |> Option.defaultValue "Data Management"

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update displays
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.MappingDataManager"
    |> ToolUp.Platform.ClientModule.withView (view displays)
    |> ToolUp.Platform.ClientModule.withProcessedData _.ProcessedData
    |> ToolUp.Platform.ClientModule.withGroup group
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
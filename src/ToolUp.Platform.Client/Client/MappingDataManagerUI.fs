// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Mapping-aware Data Manager — the opt-in replacement for the built-in
/// `FileManagerUI` that adds a front mapping stage so an arbitrary CSV
/// can be coerced into any registered, schema-bearing `DataType`:
///
///   upload CSV → pick the target format → auto-mapped fields (review
///   the flagged guesses, override as needed) → confirm.
///
/// A data-quality scan + remediation step runs first; on confirm the CSV
/// is rewritten (remediation applied) into the target schema's canonical
/// header shape and uploaded with an explicit `dataType`, so the existing
/// `DataType.Process` runs unchanged. The confirmed **conversion recipe**
/// (mapping + remediation) is persisted via `IConversionApi`, keyed by the
/// source CSV's column-structure, so the same shape auto-applies next time;
/// each produced object also gets a `ConversionRecord` (provenance) marking
/// the conversion on the ingestion.
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
    /// Data-quality scan + remediation, shown first when issues are found.
    | ReviewData
    | PickTarget
    | ReviewMapping
    /// Phase 218 — dry-run validation preview shown after the mapping is
    /// confirmed and before commit: per-row / per-cell errors of the
    /// *mapped* CSV under the chosen schema.
    | ReviewValidation

type Wizard = {
    FileName: string
    RawContents: string
    Headers: string list
    Samples: Map<string, string list>
    Fingerprint: string
    Step: WizardStep
    /// Per-column data-quality scan (drives the ReviewData step).
    Profiles: ColumnProfile list
    /// Columns the user opted OUT of remediation for (default: all
    /// safe fixes on, so absence = enabled).
    DisabledFixes: Set<string>
    /// Chosen day/month order for each ambiguous-date column. A column
    /// with an ambiguous date and no entry here blocks "Continue".
    DateOrders: Map<string, DateOrder>
    TargetTypeId: DataTypeId option
    Suggestion: MappingSuggestion option
    /// User edits over the auto-suggestion: field name → chosen column
    /// (`None` = explicitly unmapped). Fields absent from the map use
    /// the suggestion's `SuggestedColumn`.
    Overrides: Map<string, string option>
    ReusedSaved: bool
    Saving: bool
    /// Phase 218 — the dry-run validation report for the confirmed
    /// mapping (populated on the `ReviewValidation` step). `None` until
    /// the mapping is confirmed and validated.
    Validation: DryRunReport option
    /// `true` while the dry-run `ValidateConversion` round-trip is in
    /// flight (drives the "Validating…" affordance on confirm).
    Validating: bool
}

/// The CSV the mapping wizard is currently working on. Populated when a
/// "New Mapping" is started against an uploaded file (its bytes are
/// re-fetched via `GetFileContent`); cleared on cancel / confirm. The
/// upload pipeline threads its own `HeldFile` through messages rather than
/// using `Model.Held`, so concurrent multi-file uploads don't clobber it.
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
    /// Per-object conversion provenance, joined to the file list to mark
    /// which objects were produced by a conversion (+ their steps).
    Records: ConversionRecord list
    /// The file the mapping wizard is working on (set when a "New Mapping"
    /// is started; cleared on cancel / confirm).
    Held: HeldFile option
    Wizard: Wizard option
    Busy: bool
    Error: string option
    /// Phase 245 — the data type ids whose owning module is available
    /// (mappable) to the caller's team, from the availability-filtered
    /// `GetDataCatalog`. `None` until the catalog loads (no filtering yet,
    /// so the wizard never blanks on a slow fetch); `Some ids` filters the
    /// mapping target picker + the saved-mapping auto-reuse so a module
    /// the team has marked `Unavailable` is never offered.
    AllowedTypeIds: Set<DataTypeId> option
}

type Msg =
    | LoadFiles
    | FilesLoaded of FileListSnapshot
    | RecordsLoaded of ConversionRecord list
    | SelectFile of Browser.Types.File
    | FileChosen of fileName: string * contents: string
    | MappingsFetched of HeldFile * Conversion list
    | CatalogLoaded of DataCatalogResponse
    /// Catalog fetch failed — leave `AllowedTypeIds = None` (unfiltered;
    /// the server still enforces availability) rather than blanking the
    /// picker.
    | CatalogLoadFailed
    | NativeUploaded of FileUploadResult
    | ImportFinished of Result<DataTypeId list, string>
    /// Re-fetch an uploaded file's bytes and open the mapping wizard on it —
    /// the per-row "New Mapping" action, available for every uploaded file.
    | StartMapping of fileName: string
    | FileContentLoaded of FileContentResult
    | ToggleColumnFixes of column: string
    | SetDateOrder of column: string * order: DateOrder
    | ProceedToMapping
    | SelectTarget of DataTypeId
    | ChangeFormat
    | OverrideColumn of field: string * column: string option
    | CancelWizard
    /// Confirm the mapping → run the dry-run validation (no commit yet).
    | ConfirmMapping
    | ValidationFinished of Result<DryRunReport, string>
    /// Return from the validation preview to revise the mapping.
    | BackToMapping
    /// Commit the validated conversion (save recipe + import the object).
    | CommitConversion
    | DeleteFile of string
    | ReprocessFile of string
    | Reprocessed of Result<ProcessedFileEntry, string>
    | ResetDataStore
    | ResetDone of Result<int, string>
    | ApiError of string
    | DismissError

let private fileApi: FileManagementApi =
    Api.makeProxy<FileManagementApi> (customOptions = UserSession.withRequestHeaders)

let private conversionApi: IConversionApi =
    Api.makeProxy<IConversionApi> (customOptions = UserSession.withRequestHeaders)

let private dataCatalogApi: DataCatalogApi =
    Api.makeProxy<DataCatalogApi> (customOptions = UserSession.withRequestHeaders)

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
        |> List.map _.Field.Name

// ─── Data-quality (ReviewData step) helpers ───────────────────────

let private hasAmbiguousDate (p: ColumnProfile) =
    p.Issues |> List.exists (fun i -> i.Kind = AmbiguousDateFormat)

/// The remediation transforms currently chosen for a column: its safe
/// fixes (unless opted out) plus the chosen date order (for ambiguous
/// date columns).
let private columnTransforms (w: Wizard) (p: ColumnProfile) : CellTransform list =
    let safe =
        if w.DisabledFixes.Contains p.Column then
            []
        else
            p.Issues |> List.filter _.Safe |> List.collect _.Suggested

    let dateChoice =
        if hasAmbiguousDate p then
            match w.DateOrders |> Map.tryFind p.Column with
            | Some order -> [ ParseDateToIso order ]
            | None -> []
        else
            []

    safe @ dateChoice

/// source column → its chosen transforms (omitting empty), for the
/// `ColumnMapping` record and for remediating samples before `suggest`.
let private wizardTransforms (w: Wizard) : Map<string, CellTransform list> =
    w.Profiles
    |> List.choose (fun p ->
        match columnTransforms w p with
        | [] -> None
        | ts -> Some(p.Column, ts))
    |> Map.ofList

/// Ambiguous-date columns still awaiting an order choice — block Continue.
let private unresolvedDates (w: Wizard) : string list =
    w.Profiles
    |> List.filter (fun p -> hasAmbiguousDate p && (w.DateOrders |> Map.tryFind p.Column).IsNone)
    |> List.map _.Column

/// Samples with the chosen transforms applied — so `suggest` sees clean,
/// correctly-typed values.
let private remediatedSamples (w: Wizard) : Map<string, string list> =
    let transforms = wizardTransforms w

    w.Samples
    |> Map.map (fun col vals ->
        match transforms |> Map.tryFind col with
        | Some ts -> vals |> List.map (ColumnMapping.applyTransforms ts)
        | None -> vals)

/// Column display label with its detected unit, so `$` vs `£` columns
/// stay distinguishable in the mapping dropdowns.
let private columnLabel (profiles: ColumnProfile list) (header: string) : string =
    match profiles |> List.tryFind (fun p -> p.Column = header) with
    | Some { DetectedUnit = Some unit } -> sprintf "%s (%s)" header unit
    | _ -> header

/// A fresh wizard for a held file: profiles its columns and starts on the
/// ReviewData step when there are issues, else jumps straight to mapping.
let private wizardFor (held: HeldFile) : Wizard =
    let profiles =
        held.Headers
        |> List.map (fun h -> ColumnMapping.profileColumn h (held.Samples |> Map.tryFind h |> Option.defaultValue []))

    let hasIssues = profiles |> List.exists (fun p -> not p.Issues.IsEmpty)

    {
        FileName = held.FileName
        RawContents = held.RawContents
        Headers = held.Headers
        Samples = held.Samples
        Fingerprint = held.Fingerprint
        Step = (if hasIssues then ReviewData else PickTarget)
        Profiles = profiles
        DisabledFixes = Set.empty
        DateOrders = Map.empty
        TargetTypeId = None
        Suggestion = None
        Overrides = Map.empty
        ReusedSaved = false
        Saving = false
        Validation = None
        Validating = false
    }

/// The confirmed conversion recipe for the wizard's current state — the
/// field mapping + data-quality remediation for the chosen target type.
/// Pure over wizard state, so both the dry-run validate and the commit
/// derive the identical recipe (the validated shape is the committed
/// shape). `None` until a target type is chosen.
let private buildConversion (w: Wizard) : Conversion option =
    w.TargetTypeId
    |> Option.map (fun typeId -> {
        Fingerprint = w.Fingerprint
        TargetTypeId = typeId
        Mapping = effectiveMapping w
        Remediation = wizardTransforms w
        SourceHeaders = w.Headers
        CreatedBy = ""
        CreatedAt = System.DateTime.UtcNow
    })

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

/// Human-readable per-column remediation steps for a conversion's
/// provenance record.
let private remediationSteps (conversion: Conversion) : string list =
    conversion.Remediation
    |> Map.toList
    |> List.choose (fun (col, ts) -> ColumnMapping.describeColumnRemediation col ts)

/// Rewrite + upload each conversion as its own data object, then record
/// its provenance (`RecordConversion`). Returns the target-type ids
/// imported, or the first error. Conversions whose target type is no
/// longer registered are skipped.
let private importConversions
    (displays: DataTypeDisplay list)
    (held: HeldFile)
    (conversions: Conversion list)
    : Async<Result<DataTypeId list, string>> =
    async {
        let mutable err = None
        let mutable imported = []

        for c in conversions do
            if err.IsNone then
                match schemaFor displays c.TargetTypeId with
                | Some schema ->
                    let rewritten =
                        ColumnMapping.rewriteCsv schema c.Mapping c.Remediation held.RawContents

                    let producedFile = importedFileName held.FileName c.TargetTypeId

                    let upload = {
                        filename = producedFile
                        contents = rewritten
                        dataType = c.TargetTypeId
                    }

                    match! fileApi.UploadFile { File = upload } with
                    | Ok _ ->
                        imported <- imported @ [ c.TargetTypeId ]

                        // Mark the conversion on the produced object —
                        // provenance + audit. Best-effort: a failed record
                        // doesn't fail the import.
                        let record: ConversionRecord = {
                            ProducedFile = producedFile
                            SourceFile = held.FileName
                            Fingerprint = c.Fingerprint
                            TargetTypeId = c.TargetTypeId
                            Mapping = c.Mapping
                            RemediationSteps = remediationSteps c
                            ConvertedBy = ""
                            ConvertedAt = System.DateTime.UtcNow
                        }

                        do! conversionApi.RecordConversion record |> Async.Ignore
                    | Error e -> err <- Some e
                | None -> ()

        match err with
        | Some e -> return Error e
        | None -> return Ok imported
    }

/// Persist a freshly-confirmed conversion recipe, then import it as its
/// own data object (recording provenance).
let private saveAndImport
    (displays: DataTypeDisplay list)
    (held: HeldFile)
    (conversion: Conversion)
    : Async<Result<DataTypeId list, string>> =
    async {
        match! conversionApi.SaveConversion conversion with
        | Error e -> return Error e
        | Ok() -> return! importConversions displays held [ conversion ]
    }

/// Delete a set of files in sequence, stopping at the first failure. Used
/// by the row Delete to remove an uploaded file together with every data
/// object derived from it (produced conversions are hidden from the file
/// list, so they have no Delete affordance of their own).
let private deleteFiles (names: string list) : Async<Result<unit, string>> = async {
    let mutable err = None

    for name in names do
        if err.IsNone then
            match! fileApi.DeleteFile name with
            | Ok() -> ()
            | Error e -> err <- Some e

    match err with
    | Some e -> return Error e
    | None -> return Ok()
}

// ─── Update ───────────────────────────────────────────────────────

let init () =
    {
        UploadedFiles = []
        ProcessedData = []
        Records = []
        Held = None
        Wizard = None
        Busy = false
        Error = None
        AllowedTypeIds = None
    },
    Cmd.ofMsg LoadFiles

let update (displays: DataTypeDisplay list) (msg: Msg) (model: Model) =
    match msg with
    | LoadFiles ->
        model,
        Cmd.batch [
            Cmd.OfRemoting.call fileApi.ListFiles () FilesLoaded (fun ex -> ApiError ex.Message)
            Cmd.OfRemoting.call conversionApi.ListConversionRecords () RecordsLoaded (fun _ -> RecordsLoaded [])
            // Phase 245 — the availability-filtered catalog. A type whose
            // owning module the team marked `Unavailable` is absent here, so
            // it is never offered as a mapping target. A failed fetch leaves
            // `AllowedTypeIds = None` (no filtering) rather than blanking the
            // picker.
            Cmd.OfRemoting.call dataCatalogApi.GetDataCatalog () CatalogLoaded (fun _ -> CatalogLoadFailed)
        ]

    | FilesLoaded snapshot ->
        {
            model with
                UploadedFiles = snapshot.Files
                ProcessedData = snapshot.Processed
        },
        Cmd.none

    | RecordsLoaded records -> { model with Records = records }, Cmd.none

    | CatalogLoaded response ->
        let allowed = response.Types |> List.map (fun e -> e.Info.Id) |> Set.ofList

        {
            model with
                AllowedTypeIds = Some allowed
        },
        Cmd.none

    | CatalogLoadFailed -> model, Cmd.none

    | SelectFile file ->
        // Read the file via a direct effect rather than the previous
        // `Cmd.OfAsync.perform` + `Async.FromContinuations` pairing. Under
        // Fable 5 that async path silently no-ops here — the deferred async
        // start never runs the reader (no FileReader is even constructed),
        // so `FileChosen` is never dispatched and the import appears to hang
        // forever with no spinner, no network, and no error (`OfAsync.perform`
        // swallows exceptions). An effect runs synchronously when the runtime
        // execs the Cmd, and we dispatch straight from the reader callbacks —
        // no trampoline, no swallowed errors.
        model,
        Cmd.ofEffect (fun dispatch ->
            let reader = Browser.Dom.FileReader.Create()
            reader.onload <- fun _ -> dispatch (FileChosen(file.name, reader.result :?> string))

            reader.onerror <-
                fun _ -> dispatch (ApiError(sprintf "Couldn't read '%s' — the file may be unreadable." file.name))

            reader.readAsText file)

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

        // The upload pipeline carries its `HeldFile` through the messages
        // (not `Model.Held`) so concurrent multi-file uploads don't clobber
        // one another.
        { model with Busy = true; Error = None },
        Cmd.OfRemoting.call
            conversionApi.GetConversions
            fingerprint
            (fun saved -> MappingsFetched(held, saved))
            (fun ex -> ApiError ex.Message)

    | MappingsFetched(held, saved) ->
        // Frictionless re-import when the structure is already known: apply
        // every saved mapping (skipping ones whose target type is no longer
        // registered, or whose owning module is `Unavailable` to this team
        // per the availability-filtered catalog). With none usable, upload
        // the file as-is so the server attempts native detection — recognised
        // or not, the file then lands in the list to be mapped later via
        // "New Mapping".
        let isAvailable (typeId: DataTypeId) =
            match model.AllowedTypeIds with
            | Some allowed -> allowed.Contains typeId
            | None -> true

        let importable =
            saved
            |> List.filter (fun m -> (schemaFor displays m.TargetTypeId).IsSome && isAvailable m.TargetTypeId)

        if not importable.IsEmpty then
            model,
            Cmd.OfAsync.either (importConversions displays held) importable ImportFinished (fun ex ->
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
        | Ok _ ->
            // Recognised → the file now carries its detected type; not
            // recognised → it persists as a normal row to be mapped later
            // via "New Mapping". Either way, just refresh the list.
            { model with Busy = false }, Cmd.ofMsg LoadFiles
        | Error e ->
            {
                model with
                    Busy = false
                    Error = Some e
            },
            Cmd.none

    | ImportFinished(Ok _) ->
        // `importable` / `saveAndImport` only ever carry registered,
        // schema-bearing target types, so a successful import always
        // produces at least one object. Clear any wizard and refresh.
        {
            model with
                Busy = false
                Wizard = None
                Held = None
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

    | StartMapping fileName ->
        // Re-fetch the uploaded file's bytes, then open the wizard on it.
        { model with Busy = true; Error = None },
        Cmd.OfRemoting.call fileApi.GetFileContent fileName FileContentLoaded (fun ex -> ApiError ex.Message)

    | FileContentLoaded(Ok upload) ->
        let headers, samples = ColumnMapping.parsePreview 20 upload.contents
        let fingerprint = ColumnMapping.Fingerprint.ofHeaders headers

        let held = {
            FileName = upload.filename
            RawContents = upload.contents
            Headers = headers
            Samples = samples
            Fingerprint = fingerprint
        }

        {
            model with
                Busy = false
                Held = Some held
                Wizard = Some(wizardFor held)
        },
        Cmd.none

    | FileContentLoaded(Error e) ->
        {
            model with
                Busy = false
                Error = Some e
        },
        Cmd.none

    | ToggleColumnFixes column ->
        match model.Wizard with
        | Some w ->
            let disabled =
                if w.DisabledFixes.Contains column then
                    Set.remove column w.DisabledFixes
                else
                    Set.add column w.DisabledFixes

            {
                model with
                    Wizard = Some { w with DisabledFixes = disabled }
            },
            Cmd.none
        | None -> model, Cmd.none

    | SetDateOrder(column, order) ->
        match model.Wizard with
        | Some w ->
            {
                model with
                    Wizard =
                        Some {
                            w with
                                DateOrders = Map.add column order w.DateOrders
                        }
            },
            Cmd.none
        | None -> model, Cmd.none

    | ProceedToMapping ->
        match model.Wizard with
        | Some w ->
            {
                model with
                    Wizard = Some { w with Step = PickTarget }
            },
            Cmd.none
        | None -> model, Cmd.none

    | SelectTarget typeId ->
        match model.Wizard with
        | Some w ->
            match schemaFor displays typeId with
            | Some schema ->
                // suggest against remediated samples so cleaned columns
                // read with their true types.
                let suggestion = ColumnMapping.suggest typeId schema w.Headers (remediatedSamples w)

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
        // Phase 218 — confirm no longer commits directly: it runs the
        // dry-run validation first (rewrite → schema validate, no write,
        // no `DataType.Process`) and shows the per-row/per-cell report.
        match model.Wizard, model.Held with
        | Some w, Some held when w.TargetTypeId.IsSome ->
            match schemaFor displays w.TargetTypeId.Value, buildConversion w with
            | Some _, Some conversion ->
                let request: DryRunValidationRequest = {
                    Conversion = conversion
                    RawCsv = held.RawContents
                }

                {
                    model with
                        Wizard = Some { w with Validating = true }
                        Error = None
                },
                Cmd.OfAsync.either conversionApi.ValidateConversion request ValidationFinished (fun ex ->
                    ApiError ex.Message)
            | _ ->
                {
                    model with
                        Error = Some "The selected data type publishes no schema."
                },
                Cmd.none
        | _ -> model, Cmd.none

    | ValidationFinished(Ok report) ->
        match model.Wizard with
        | Some w ->
            {
                model with
                    Wizard =
                        Some {
                            w with
                                Validating = false
                                Validation = Some report
                                Step = ReviewValidation
                        }
            },
            Cmd.none
        | None -> model, Cmd.none

    | ValidationFinished(Error msg) ->
        {
            model with
                Wizard = model.Wizard |> Option.map (fun w -> { w with Validating = false })
                Error = Some msg
        },
        Cmd.none

    | BackToMapping ->
        {
            model with
                Wizard =
                    model.Wizard
                    |> Option.map (fun w -> {
                        w with
                            Step = ReviewMapping
                            Validation = None
                    })
        },
        Cmd.none

    | CommitConversion ->
        // The validated conversion is committed: save the recipe (additive —
        // keyed by fingerprint+type), then import it as its own data object
        // (recording provenance). The conversion is rebuilt from the same
        // pure wizard state that was validated, so the committed shape is the
        // validated shape.
        match model.Wizard, model.Held with
        | Some w, Some held when w.TargetTypeId.IsSome ->
            match schemaFor displays w.TargetTypeId.Value, buildConversion w with
            | Some _, Some conversion ->
                {
                    model with
                        Wizard = Some { w with Saving = true }
                },
                Cmd.OfAsync.either (saveAndImport displays held) conversion ImportFinished (fun ex ->
                    ApiError ex.Message)
            | _ ->
                {
                    model with
                        Error = Some "The selected data type publishes no schema."
                },
                Cmd.none
        | _ -> model, Cmd.none

    | DeleteFile fileName ->
        // Cascade to every data object derived from this upload — produced
        // conversions are hidden from the file list, so deleting the source
        // is the way they're removed.
        let derived =
            model.Records
            |> List.filter (fun r -> r.SourceFile = fileName)
            |> List.map _.ProducedFile
            |> List.distinct

        model,
        Cmd.OfAsync.either
            deleteFiles
            (fileName :: derived)
            (function
            | Ok() -> LoadFiles
            | Error e -> ApiError e)
            (fun ex -> ApiError ex.Message)

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
        let wizard =
            model.Wizard
            |> Option.map (fun w -> {
                w with
                    Saving = false
                    Validating = false
            })

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

/// File-list "Data Type" column label — renders the detect sentinel as a
/// readable "Unrecognised" rather than the raw `UnrecognisedData` id.
let private dataTypeLabel (displays: DataTypeDisplay list) (dataTypeId: DataTypeId) =
    if dataTypeId = "UnrecognisedData" then
        "Unrecognised"
    else
        labelFor displays dataTypeId

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
                Html.option [ prop.value h; prop.text (columnLabel w.Profiles h) ]
        ]
    ]

// ─── ReviewData step ──────────────────────────────────────────────

let private dateOrderName =
    function
    | DayFirst -> "Day first (DD/MM)"
    | MonthFirst -> "Month first (MM/DD)"
    | YearFirst -> "ISO (YYYY-MM-DD)"

let private reviewDataView (w: Wizard) dispatch =
    let problemColumns = w.Profiles |> List.filter (fun p -> not p.Issues.IsEmpty)
    let blockers = unresolvedDates w

    let columnCard (p: ColumnProfile) =
        let enabled = not (w.DisabledFixes.Contains p.Column)
        let ambiguous = hasAmbiguousDate p
        let chosenOrder = w.DateOrders |> Map.tryFind p.Column

        // before/after preview on the first example value
        let example = p.Issues |> List.collect _.Examples |> List.tryHead

        Html.div [
            prop.className "p-3 rounded border border-gray-200 space-y-2"
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between gap-3"
                    prop.children [
                        Html.div [
                            prop.children [
                                Html.span [ prop.className "font-medium text-sm"; prop.text p.Column ]
                                match p.DetectedUnit with
                                | Some u ->
                                    Html.span [
                                        prop.className "ml-2 text-xs text-gray-500"
                                        prop.text $"unit {u} → kept in label"
                                    ]
                                | None -> ()
                            ]
                        ]
                        // opt-out toggle for the safe fixes (dates excepted)
                        if p.Issues |> List.exists _.Safe then
                            Html.label [
                                prop.className "flex items-center gap-1.5 text-xs text-gray-600 cursor-pointer"
                                prop.children [
                                    Html.input [
                                        prop.type' "checkbox"
                                        prop.isChecked enabled
                                        prop.onChange (fun (_: bool) -> dispatch (ToggleColumnFixes p.Column))
                                    ]
                                    Html.span [ prop.text "Apply fixes" ]
                                ]
                            ]
                    ]
                ]

                for issue in p.Issues do
                    Html.div [
                        prop.className "text-xs text-gray-600"
                        prop.children [
                            Html.span [
                                prop.className (
                                    if issue.NeedsChoice then
                                        "text-amber-700 font-medium"
                                    else
                                        "text-gray-600"
                                )
                                prop.text ((if issue.NeedsChoice then "⚠ " else "• ") + issue.Detail)
                            ]
                            if not issue.Examples.IsEmpty then
                                Html.span [
                                    prop.className "ml-1 text-gray-400"
                                    prop.text (
                                        sprintf "e.g. %s" (String.concat ", " (issue.Examples |> List.truncate 3))
                                    )
                                ]
                        ]
                    ]

                // ambiguous-date order chooser (required)
                if ambiguous then
                    Html.div [
                        prop.className "flex items-center gap-3 pt-1"
                        prop.children [
                            for order in [ DayFirst; MonthFirst; YearFirst ] do
                                let selected = (chosenOrder = Some order)

                                Html.label [
                                    prop.className "flex items-center gap-1.5 text-xs cursor-pointer"
                                    prop.children [
                                        Html.input [
                                            prop.type' "radio"
                                            prop.name $"dateorder-{p.Column}"
                                            prop.isChecked selected
                                            prop.onChange (fun (_: bool) -> dispatch (SetDateOrder(p.Column, order)))
                                        ]
                                        Html.span [ prop.text (dateOrderName order) ]
                                    ]
                                ]
                        ]
                    ]

                // before → after preview
                match example with
                | Some raw ->
                    let after = ColumnMapping.applyTransforms (columnTransforms w p) raw

                    if after <> raw then
                        Html.div [
                            prop.className "text-xs text-gray-500"
                            prop.text $"preview: \"{raw}\" → \"{after}\""
                        ]
                | None -> ()
            ]
        ]

    Html.div [
        prop.className "space-y-3"
        prop.children [
            Html.p [
                prop.className "text-sm text-gray-600"
                prop.text
                    "We scanned the data for problems before mapping. Safe fixes are pre-selected; ambiguous dates need a choice."
            ]
            for p in problemColumns do
                columnCard p

            if not blockers.IsEmpty then
                Html.div [
                    prop.className "text-sm text-amber-700"
                    prop.text ("Choose a date order for: " + String.concat ", " blockers)
                ]

            Html.button [
                prop.className [
                    "px-4 py-2 rounded-lg text-sm"
                    Tokens.Typography.buttonText
                    if blockers.IsEmpty then
                        Tokens.Colours.brand
                        + " "
                        + Tokens.Colours.brandText
                        + " hover:bg-brand-dark cursor-pointer"
                    else
                        "bg-gray-200 text-gray-500 cursor-not-allowed"
                ]
                prop.disabled (not blockers.IsEmpty)
                prop.text "Continue to mapping"
                prop.onClick (fun _ ->
                    if blockers.IsEmpty then
                        dispatch ProceedToMapping)
            ]
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

// ─── ReviewValidation step (Phase 218) ────────────────────────────

/// The dry-run validation preview. Reuses the data-quality review's
/// severity vocabulary (`ColumnIssue` / `Safe` / `NeedsChoice`): a
/// policy-blocked commit reads like a `NeedsChoice` blocker (red, must
/// act), a warn-only failure reads like a non-`Safe` advisory (amber,
/// proceed at your discretion), and a clean report reads as `Safe`
/// (green). Errors are grouped by column to match the mapping-review
/// surface.
let private validationView (report: DryRunReport) (saving: bool) dispatch =
    let hasFailures = report.FailedRows > 0 || not report.RowIssues.IsEmpty

    let summaryBadge =
        if not hasFailures then
            "border-green-200 bg-green-50 text-green-800",
            sprintf "All %d row%s validated cleanly." report.TotalRows (if report.TotalRows = 1 then "" else "s")
        elif report.CommitBlocked then
            "border-red-200 bg-red-50 text-red-800",
            sprintf
                "%d of %d row%s would fail — commit is blocked until the mapping or source is fixed."
                report.FailedRows
                report.TotalRows
                (if report.TotalRows = 1 then "" else "s")
        else
            "border-amber-200 bg-amber-50 text-amber-800",
            sprintf
                "%d of %d row%s would fail. You can fix the mapping or import anyway."
                report.FailedRows
                report.TotalRows
                (if report.TotalRows = 1 then "" else "s")

    let badgeClass, summaryText = summaryBadge

    let byColumn = report.CellIssues |> List.groupBy _.Column

    let columnCard (column: string, issues: DryRunCellIssue list) =
        Html.div [
            prop.className "p-3 rounded border border-amber-200 bg-amber-50 space-y-1"
            prop.children [
                Html.div [
                    prop.className "text-sm font-medium text-amber-800"
                    prop.text (
                        sprintf "%s — %d failing cell%s" column issues.Length (if issues.Length = 1 then "" else "s")
                    )
                ]
                for issue in issues |> List.truncate 5 do
                    let reason =
                        match issue.Violation with
                        | Some v -> v
                        | None -> sprintf "expected %s" issue.Expected

                    Html.div [
                        prop.className "text-xs text-amber-700"
                        prop.text (sprintf "row %d: \"%s\" — %s" issue.Row issue.Actual reason)
                    ]
            ]
        ]

    Html.div [
        prop.className "space-y-3"
        prop.children [
            Html.div [
                prop.className $"p-3 rounded border text-sm {badgeClass}"
                prop.text summaryText
            ]

            for issue in report.RowIssues do
                Html.div [ prop.className "text-sm text-red-700"; prop.text issue.Detail ]

            for group in byColumn do
                columnCard group

            if report.Truncated then
                Html.div [
                    prop.className "text-xs text-gray-500"
                    prop.text "Showing a sample of the failing cells — more exist than are listed here."
                ]

            Html.div [
                prop.className "flex items-center gap-3 pt-1"
                prop.children [
                    Html.button [
                        prop.className [
                            "px-4 py-2 rounded-lg text-sm"
                            Tokens.Typography.buttonText
                            if (not report.CommitBlocked) && not saving then
                                Tokens.Colours.brand
                                + " "
                                + Tokens.Colours.brandText
                                + " hover:bg-brand-dark cursor-pointer"
                            else
                                "bg-gray-200 text-gray-500 cursor-not-allowed"
                        ]
                        prop.disabled (report.CommitBlocked || saving)
                        prop.text (if saving then "Importing…" else "Import")
                        prop.onClick (fun _ ->
                            if (not report.CommitBlocked) && not saving then
                                dispatch CommitConversion)
                    ]
                    Html.button [
                        prop.className "text-sm text-gray-600 hover:text-gray-900 hover:underline"
                        prop.disabled saving
                        prop.text "Back to mapping"
                        prop.onClick (fun _ ->
                            if not saving then
                                dispatch BackToMapping)
                    ]
                ]
            ]
        ]
    ]

let private wizardView (displays: DataTypeDisplay list) (allowedTypeIds: Set<DataTypeId> option) (w: Wizard) dispatch =
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
        | ReviewValidation ->
            match w.Validation with
            | Some report -> validationView report w.Saving dispatch
            | None -> Html.none
        | ReviewData -> reviewDataView w dispatch
        | PickTarget ->
            // Schema-bearing types only, and — once the availability-filtered
            // catalog has loaded — only those whose owning module is mappable
            // for this team (a module marked `Unavailable` is never offered).
            let tabular =
                displays
                |> List.filter (fun d ->
                    d.Info.Schema.IsSome
                    && (match allowedTypeIds with
                        | Some allowed -> allowed.Contains d.Info.Id
                        | None -> true))

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
                                        if blockers.IsEmpty && not w.Validating then
                                            Tokens.Colours.brand
                                            + " "
                                            + Tokens.Colours.brandText
                                            + " hover:bg-brand-dark cursor-pointer"
                                        else
                                            "bg-gray-200 text-gray-500 cursor-not-allowed"
                                    ]
                                    prop.disabled (not blockers.IsEmpty || w.Validating)
                                    prop.text (if w.Validating then "Validating…" else "Confirm & validate")
                                    prop.onClick (fun _ ->
                                        if blockers.IsEmpty && not w.Validating then
                                            dispatch ConfirmMapping)
                                ]
                                Html.span [
                                    prop.className "text-xs text-gray-500"
                                    prop.text "We check every row against the format before importing."
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
    // Show genuine uploads only — a confirmed mapping uploads its rewritten
    // CSV as a produced file (`X__Type.csv`); that belongs in the data-object
    // section beneath, not as another row here. So the file count tracks
    // uploads, while the data-object count tracks mappings.
    let producedNames = model.Records |> List.map _.ProducedFile |> Set.ofList

    let rawFiles =
        model.UploadedFiles
        |> List.filter (fun f -> not (producedNames.Contains f.FileName))

    match rawFiles with
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
                                    // An upload that has produced no data object — neither a
                                    // natively-recognised entry of its own nor any mapped
                                    // conversion derived from it — is flagged so the user maps
                                    // it via New Mapping.
                                    let hasOwnObject =
                                        model.ProcessedData
                                        |> List.exists (fun e -> e.FileName = f.FileName && e.Info.IsSome)

                                    let hasDerived = model.Records |> List.exists (fun r -> r.SourceFile = f.FileName)

                                    let needsMapping = not (hasOwnObject || hasDerived)

                                    Html.tr [
                                        prop.className [
                                            "border-b border-gray-100"
                                            if needsMapping then
                                                "bg-pink-50"
                                        ]
                                        prop.children [
                                            Html.td [
                                                prop.className "py-2 pr-3"
                                                prop.text (dataTypeLabel displays f.DataType)
                                            ]
                                            Html.td [
                                                prop.className "py-2 pr-3"
                                                prop.children [
                                                    Html.span [ prop.text f.FileName ]
                                                    match
                                                        model.Records
                                                        |> List.tryFind (fun r -> r.ProducedFile = f.FileName)
                                                    with
                                                    | Some r ->
                                                        let tip =
                                                            let steps =
                                                                if r.RemediationSteps.IsEmpty then
                                                                    "no remediation"
                                                                else
                                                                    String.concat "; " r.RemediationSteps

                                                            sprintf "Converted from %s — %s" r.SourceFile steps

                                                        Html.span [
                                                            prop.className
                                                                "ml-2 inline-block px-1.5 py-0.5 rounded border border-blue-200 bg-blue-50 text-blue-700 text-xs"
                                                            prop.title tip
                                                            prop.text "Converted"
                                                        ]
                                                    | None -> ()
                                                ]
                                            ]
                                            Html.td [ prop.className "py-2 pr-3"; prop.text (string f.RowCount) ]
                                            Html.td [ prop.className "py-2 pr-3"; prop.text (formatSize f.SizeBytes) ]
                                            Html.td [
                                                prop.className "py-2 pr-3"
                                                prop.children [
                                                    Html.div [
                                                        prop.className "flex items-center gap-3"
                                                        prop.children [
                                                            Html.button [
                                                                prop.className "text-sm text-brand hover:underline"
                                                                prop.title
                                                                    "Map this file's columns to a known format to produce a data object. Available for every file — map an unrecognised file for the first time, or spawn an additional data object from an already-mapped one."
                                                                prop.text "New Mapping"
                                                                prop.onClick (fun _ ->
                                                                    dispatch (StartMapping f.FileName))
                                                            ]
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
                                                                            "Delete %s? Any data objects mapped from it are removed too, and analyses depending on them will lose access."
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

let private view (displays: DataTypeDisplay list) (model: Model) dispatch =
    let inputPanel =
        Layout.Panel.panel "Import CSV" [
            Layout.Panel.panelSection "Upload a file" [
                Html.div [
                    prop.className "flex items-center gap-4 flex-nowrap"
                    prop.children [
                        FilePicker.FilePicker(
                            true,
                            ".csv",
                            (fun files -> files |> List.iter (fun file -> dispatch (SelectFile file))),
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
                                    "Upload one or more CSVs. Known structures re-import automatically; unrecognised ones land below — use New Mapping to map them."
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
                | Some w -> wizardView displays model.AllowedTypeIds w dispatch
                | None -> Layout.Panel.panel "Imported Files" [ filesView displays model dispatch ]

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
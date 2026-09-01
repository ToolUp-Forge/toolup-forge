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
open Fable.SimpleJson
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
    /// Phase 219 — derived/computed columns the user has added: a schema
    /// field produced from a `ColumnExpr` over source columns rather than a
    /// 1:1 map. Auto-suggestion never produces these (explicit user intent),
    /// so they start empty and only grow via the derived-column builder.
    Derived: DerivedColumn list
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
    /// Per-file RAG ingestion status (fileName → status), hydrated from
    /// `FileListSnapshot.Ingestion` and patched live via
    /// `DataManagerIngestionStatusKey` notifications (Phase 173). Empty
    /// when no RAG is composed ⇒ no status column.
    IngestionStatus: Map<string, FileIngestionStatus>
    /// Client-side filter over the file list by ingestion status
    /// (Phase 220). `AllFiles` by default.
    StatusFilter: IngestionStatusFilter
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
    /// A `DataManagerIngestionStatusKey` notification arrived — patch the
    /// named file's ingestion badge in place (no refetch).
    | IngestionStatusChanged of fileName: string * status: FileIngestionStatus
    /// Phase 220 — one-click re-ingest of a `Failed` file. No optimistic
    /// state change: the badge re-renders from the store via the live
    /// notification.
    | RetryIngestionMsg of fileName: string
    | RetryIngestionDone of Result<unit, string>
    /// Phase 220 — narrow the file list to one ingestion status (client-side).
    | SetStatusFilter of IngestionStatusFilter
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
    /// Phase 219 — add a derived/computed column (replaces any existing
    /// derived column for the same target field).
    | AddDerivedColumn of DerivedColumn
    /// Phase 219 — drop the derived column for a target field.
    | RemoveDerivedColumn of field: string
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

/// The schema fields produced by a derived expression (so they're not also
/// drawn from a 1:1 mapped column — derived wins).
let private derivedFieldSet (w: Wizard) : Set<string> =
    w.Derived |> List.map _.Field |> Set.ofList

/// field name → source column, for every field that resolves to a column.
/// Derived fields are excluded — their value comes from the `ColumnExpr`,
/// not a 1:1 map.
let private effectiveMapping (w: Wizard) : Map<string, string> =
    match w.Suggestion with
    | None -> Map.empty
    | Some s ->
        let derived = derivedFieldSet w

        s.Fields
        |> List.filter (fun f -> not (derived.Contains f.Field.Name))
        |> List.choose (fun f -> chosenColumn w f |> Option.map (fun c -> f.Field.Name, c))
        |> Map.ofList

/// Required fields that still have no column AND no derived expression —
/// block confirmation. A derived column satisfies a required field.
let private unresolvedRequired (w: Wizard) : string list =
    match w.Suggestion with
    | None -> []
    | Some s ->
        let derived = derivedFieldSet w

        s.Fields
        |> List.filter (fun f ->
            f.Field.Required
            && (chosenColumn w f).IsNone
            && not (derived.Contains f.Field.Name))
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
        Derived = []
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
        Derived = w.Derived
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
    let remediation =
        conversion.Remediation
        |> Map.toList
        |> List.choose (fun (col, ts) -> ColumnMapping.describeColumnRemediation col ts)

    let derived =
        conversion.Derived
        |> List.map (fun d ->
            MessageCatalog.english.MappingDataManager.DerivedRemediationStep(ColumnMapping.describeDerivedColumn d))

    remediation @ derived

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
                        ColumnMapping.rewriteCsvWithDerived schema c.Mapping c.Remediation c.Derived held.RawContents

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
        IngestionStatus = Map.empty
        StatusFilter = AllFiles
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
                IngestionStatus = Map.ofList snapshot.Ingestion
        },
        Cmd.none

    | RecordsLoaded records -> { model with Records = records }, Cmd.none

    | IngestionStatusChanged(fileName, status) ->
        {
            model with
                IngestionStatus = model.IngestionStatus |> Map.add fileName status
        },
        Cmd.none

    | RetryIngestionMsg fileName ->
        model, Cmd.OfRemoting.call fileApi.RetryIngestion fileName RetryIngestionDone (fun ex -> ApiError ex.Message)

    // No optimistic state — the server transitions the store to `Pending`
    // and the live notification flips the badge.
    | RetryIngestionDone(Ok()) -> model, Cmd.none

    | RetryIngestionDone(Error msg) ->
        {
            model with
                Error = Some(MessageCatalog.english.MappingDataManager.ReingestionFailed msg)
        },
        Cmd.none

    | SetStatusFilter filter -> { model with StatusFilter = filter }, Cmd.none

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
                fun _ -> dispatch (ApiError(MessageCatalog.english.MappingDataManager.FileReadFailed file.name))

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
                        Derived = []
                        ReusedSaved = false
                        Step = ReviewMapping
                }

                { model with Wizard = Some w' }, Cmd.none
            | None ->
                {
                    model with
                        Error = Some MessageCatalog.english.MappingDataManager.NoSchemaCannotMap
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
                                Derived = []
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

    | AddDerivedColumn derived ->
        match model.Wizard with
        | Some w ->
            // Replace any existing derived column for the same field, then
            // clear a now-stale 1:1 override for it (derived wins).
            let next =
                (w.Derived |> List.filter (fun d -> d.Field <> derived.Field)) @ [ derived ]

            {
                model with
                    Wizard =
                        Some {
                            w with
                                Derived = next
                                Overrides = Map.add derived.Field None w.Overrides
                        }
            },
            Cmd.none
        | None -> model, Cmd.none

    | RemoveDerivedColumn field ->
        match model.Wizard with
        | Some w ->
            {
                model with
                    Wizard =
                        Some {
                            w with
                                Derived = w.Derived |> List.filter (fun d -> d.Field <> field)
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
                        Error = Some MessageCatalog.english.MappingDataManager.NoSchemaPublished
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
                        Error = Some MessageCatalog.english.MappingDataManager.NoSchemaPublished
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
                Error = Some(MessageCatalog.english.MappingDataManager.ReprocessFailed msg)
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
                Error = Some(MessageCatalog.english.MappingDataManager.ResetFailed msg)
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

let private formatSize (msgs: MappingDataManagerMessages) (bytes: int64) =
    if bytes < 1024L then
        $"{bytes} {msgs.UnitBytes}"
    elif bytes < 1024L * 1024L then
        sprintf "%.1f %s" (float bytes / 1024.0) msgs.UnitKilobytes
    else
        sprintf "%.1f %s" (float bytes / (1024.0 * 1024.0)) msgs.UnitMegabytes

let private labelFor (displays: DataTypeDisplay list) (dataTypeId: DataTypeId) =
    displays
    |> List.tryFind (fun d -> d.Info.Id = dataTypeId)
    |> Option.map _.Info.DisplayName
    |> Option.defaultValue dataTypeId

/// File-list "Data Type" column label — renders the detect sentinel as a
/// readable "Unrecognised" rather than the raw `UnrecognisedData` id.
/// File-list "Data Type" column label — renders the detect sentinel as a
/// readable localised label rather than the raw `UnrecognisedData` id.
let private dataTypeLabel (msgs: MappingDataManagerMessages) (displays: DataTypeDisplay list) (dataTypeId: DataTypeId) =
    if dataTypeId = "UnrecognisedData" then
        msgs.UnrecognisedLabel
    else
        labelFor displays dataTypeId

let private columnTypeName (msgs: MappingDataManagerMessages) =
    function
    | StringColumn -> msgs.TypeText
    | NumberColumn -> msgs.TypeNumber
    | DateColumn -> msgs.TypeDate
    | BooleanColumn -> msgs.TypeBoolean

/// (badge text, tailwind colour classes, is-warning) for a match flag.
let private flagBadge (msgs: MappingDataManagerMessages) =
    function
    | Confident -> msgs.MatchConfident, "text-green-700 bg-green-50 border-green-200", false
    | LowConfidence -> msgs.MatchLowConfidence, "text-amber-700 bg-amber-50 border-amber-200", true
    | TypeMismatch -> msgs.MatchTypeMismatch, "text-amber-700 bg-amber-50 border-amber-200", true
    | Ambiguous -> msgs.MatchAmbiguous, "text-amber-700 bg-amber-50 border-amber-200", true
    | Unmatched -> msgs.MatchUnmatched, "text-red-700 bg-red-50 border-red-200", true

let private columnSelect (msgs: MappingDataManagerMessages) (w: Wizard) (field: FieldSuggestion) dispatch =
    let current = chosenColumn w field |> Option.defaultValue ""

    Html.select [
        prop.className "border border-gray-300 rounded px-2 py-1 text-sm w-full"
        prop.value current
        prop.onChange (fun (v: string) ->
            let col = if v = "" then None else Some v
            dispatch (OverrideColumn(field.Field.Name, col)))
        prop.children [
            Html.option [ prop.value ""; prop.text msgs.NotMappedOption ]
            for h in w.Headers do
                Html.option [ prop.value h; prop.text (columnLabel w.Profiles h) ]
        ]
    ]

// ─── ReviewData step ──────────────────────────────────────────────

let private dateOrderName (msgs: MappingDataManagerMessages) =
    function
    | DayFirst -> msgs.DateOrderDayFirst
    | MonthFirst -> msgs.DateOrderMonthFirst
    | YearFirst -> msgs.DateOrderYearFirst

let private reviewDataView (msgs: MappingDataManagerMessages) (w: Wizard) dispatch =
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
                                        prop.text (msgs.UnitKeptInLabel u)
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
                                    Html.span [ prop.text msgs.ApplyFixes ]
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
                                        msgs.ExampleValues(String.concat ", " (issue.Examples |> List.truncate 3))
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
                                        Html.span [ prop.text (dateOrderName msgs order) ]
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
                            prop.text (msgs.PreviewBeforeAfter raw after)
                        ]
                | None -> ()
            ]
        ]

    Html.div [
        prop.className "space-y-3"
        prop.children [
            Html.p [ prop.className "text-sm text-gray-600"; prop.text msgs.ReviewDataIntro ]
            for p in problemColumns do
                columnCard p

            if not blockers.IsEmpty then
                Html.div [
                    prop.className "text-sm text-amber-700"
                    prop.text (msgs.ChooseDateOrderFor(String.concat ", " blockers))
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
                prop.text msgs.ContinueToMapping
                prop.onClick (fun _ ->
                    if blockers.IsEmpty then
                        dispatch ProceedToMapping)
            ]
        ]
    ]

let private reviewListView (msgs: MappingDataManagerMessages) (suggestion: MappingSuggestion) =
    let flagged = suggestion.Fields |> List.filter (fun f -> f.Flag <> Confident)

    if flagged.IsEmpty then
        Html.none
    else
        Html.div [
            prop.className "mt-3 p-3 rounded border border-amber-200 bg-amber-50"
            prop.children [
                Html.div [
                    prop.className "text-sm font-semibold text-amber-800 mb-1"
                    prop.text msgs.AutoMappedWarningHeading
                ]
                Html.ul [
                    prop.className "list-disc list-inside text-sm text-amber-800 space-y-0.5"
                    prop.children [
                        for f in flagged do
                            let label, _, _ = flagBadge msgs f.Flag

                            let detail =
                                match f.SuggestedColumn with
                                | Some c -> msgs.GuessedColumn c
                                | None -> msgs.NoColumnFound

                            Html.li [ prop.text $"{f.Field.Name} ({label}) {detail}" ]
                    ]
                ]
            ]
        ]

let private mappingGridView (msgs: MappingDataManagerMessages) (w: Wizard) (suggestion: MappingSuggestion) dispatch =
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
                                Html.th [ prop.className "py-2 pr-3"; prop.text msgs.TargetField ]
                                Html.th [ prop.className "py-2 pr-3"; prop.text msgs.ColumnType ]
                                Html.th [ prop.className "py-2 pr-3"; prop.text msgs.ColumnCsvColumn ]
                                Html.th [ prop.className "py-2 pr-3"; prop.text msgs.ColumnMatch ]
                            ]
                        ]
                    ]
                    Html.tbody [
                        prop.children [
                            for f in suggestion.Fields do
                                let badgeText, badgeClass, _ = flagBadge msgs f.Flag

                                let derivedFor = w.Derived |> List.tryFind (fun d -> d.Field = f.Field.Name)

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
                                                        prop.title msgs.RequiredTooltip
                                                        prop.text "*"
                                                    ]
                                            ]
                                        ]
                                        Html.td [
                                            prop.className "py-2 pr-3 text-gray-500"
                                            prop.text (columnTypeName msgs f.Field.Type)
                                        ]
                                        Html.td [
                                            prop.className "py-2 pr-3 min-w-48"
                                            prop.children [
                                                match derivedFor with
                                                | Some d ->
                                                    Html.span [
                                                        prop.className "text-xs font-mono text-blue-800"
                                                        prop.text (ColumnMapping.describeColumnExpr d.Expr)
                                                    ]
                                                | None -> columnSelect msgs w f dispatch
                                            ]
                                        ]
                                        Html.td [
                                            prop.className "py-2 pr-3"
                                            prop.children [
                                                match derivedFor with
                                                | Some _ ->
                                                    Html.span [
                                                        prop.className
                                                            "inline-block px-2 py-0.5 rounded border text-xs text-blue-700 bg-blue-50 border-blue-200"
                                                        prop.text msgs.DerivedBadge
                                                    ]
                                                | None ->
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

// ─── Derived-column builder (Phase 219) ───────────────────────────

/// The expression kinds the minimal builder offers. Each draws from source
/// columns only (leaves are `SourceColumn` / `Constant`) — the persisted
/// `ColumnExpr` supports nesting, but the builder keeps to the flat common
/// cases (split a "Full Name", join a composite key, a literal column).
/// The keys are the `kind` state's machine values and stay unlocalised;
/// only the paired label is.
let private derivedKinds (msgs: MappingDataManagerMessages) = [
    "concat", msgs.DerivedKindConcat
    "splittake", msgs.DerivedKindSplitTake
    "substring", msgs.DerivedKindSubstring
    "constant", msgs.DerivedKindConstant
]

/// Phase 751 — the builder is already a `[<ReactComponent>]` (it holds
/// `React.useState` hooks), so the catalog hook joins the existing hook
/// order rather than needing a wrapper of its own.
[<ReactComponent>]
let private DerivedColumnBuilder (fields: string list) (headers: string list) (onAdd: DerivedColumn -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).MappingDataManager
    let field, setField = React.useState ""
    let kind, setKind = React.useState "concat"
    let colA, setColA = React.useState ""
    let colB, setColB = React.useState ""
    // separator (concat) / delimiter (splittake) / unused (substring/constant)
    let textParam, setTextParam = React.useState " "
    // literal value for the Constant kind.
    let constValue, setConstValue = React.useState ""
    let numA, setNumA = React.useState "0" // split index / substring start
    let numB, setNumB = React.useState "1" // substring length

    let parseInt (s: string) =
        match System.Int32.TryParse s with
        | true, v -> v
        | _ -> 0

    let buildExpr () : ColumnExpr option =
        match kind with
        | "constant" -> Some(Constant constValue)
        | "concat" when colA <> "" && colB <> "" -> Some(Concat([ SourceColumn colA; SourceColumn colB ], textParam))
        | "splittake" when colA <> "" -> Some(SplitTake(SourceColumn colA, textParam, parseInt numA))
        | "substring" when colA <> "" -> Some(Substring(SourceColumn colA, parseInt numA, parseInt numB))
        | _ -> None

    let canAdd = field <> "" && (buildExpr ()).IsSome

    let labelledSelect
        (label: string)
        (value: string)
        (placeholder: string)
        (options: string list)
        (onPick: string -> unit)
        =
        Html.label [
            prop.className "flex flex-col gap-0.5 text-xs text-gray-600"
            prop.children [
                Html.span [ prop.text label ]
                Html.select [
                    prop.className "border border-gray-300 rounded px-2 py-1 text-sm"
                    prop.value value
                    prop.onChange onPick
                    prop.children [
                        Html.option [ prop.value ""; prop.text placeholder ]
                        for o in options do
                            Html.option [ prop.value o; prop.text o ]
                    ]
                ]
            ]
        ]

    let textInput (label: string) (value: string) (onType: string -> unit) =
        Html.label [
            prop.className "flex flex-col gap-0.5 text-xs text-gray-600"
            prop.children [
                Html.span [ prop.text label ]
                Html.input [
                    prop.className "border border-gray-300 rounded px-2 py-1 text-sm w-28"
                    prop.value value
                    prop.onChange onType
                ]
            ]
        ]

    Html.div [
        prop.className "mt-3 p-3 rounded border border-gray-200 bg-gray-50 space-y-2"
        prop.children [
            Html.div [
                prop.className "text-sm font-medium text-gray-700"
                prop.text msgs.AddDerivedColumnHeading
            ]
            Html.div [
                prop.className "flex flex-wrap items-end gap-3"
                prop.children [
                    labelledSelect msgs.TargetField field msgs.FieldPlaceholder fields setField
                    labelledSelect msgs.DerivedFromLabel kind "" (derivedKinds msgs |> List.map fst) setKind
                    // Per-kind inputs.
                    match kind with
                    | "constant" -> textInput msgs.ValueLabel constValue setConstValue
                    | "concat" ->
                        labelledSelect msgs.ColumnALabel colA msgs.ColumnPlaceholder headers setColA
                        labelledSelect msgs.ColumnBLabel colB msgs.ColumnPlaceholder headers setColB
                        textInput msgs.SeparatorLabel textParam setTextParam
                    | "splittake" ->
                        labelledSelect msgs.ColumnLabel colA msgs.ColumnPlaceholder headers setColA
                        textInput msgs.DelimiterLabel textParam setTextParam
                        textInput msgs.PartNumberLabel numA setNumA
                    | "substring" ->
                        labelledSelect msgs.ColumnLabel colA msgs.ColumnPlaceholder headers setColA
                        textInput msgs.StartLabel numA setNumA
                        textInput msgs.LengthLabel numB setNumB
                    | _ -> Html.none

                    Html.button [
                        prop.className [
                            "px-3 py-1.5 rounded text-sm"
                            Tokens.Typography.buttonText
                            if canAdd then
                                Tokens.Colours.brand
                                + " "
                                + Tokens.Colours.brandText
                                + " hover:bg-brand-dark cursor-pointer"
                            else
                                "bg-gray-200 text-gray-500 cursor-not-allowed"
                        ]
                        prop.disabled (not canAdd)
                        prop.text msgs.AddButton
                        prop.onClick (fun _ ->
                            match field, buildExpr () with
                            | f, Some expr when f <> "" ->
                                onAdd { Field = f; Expr = expr }
                                // reset the per-kind inputs, keep the kind selected
                                setField ""
                                setColA ""
                                setColB ""
                                setConstValue ""
                            | _ -> ())
                    ]
                ]
            ]
            Html.div [
                prop.className "text-xs text-gray-400"
                prop.text msgs.DerivedColumnsFootnote
            ]
        ]
    ]

/// Lists the derived columns already added (with a remove affordance) and
/// the builder. Shown on the mapping-review step beneath the field grid.
let private derivedColumnsView (msgs: MappingDataManagerMessages) (w: Wizard) (suggestion: MappingSuggestion) dispatch =
    let fieldNames = suggestion.Fields |> List.map _.Field.Name

    Html.div [
        prop.className "space-y-2"
        prop.children [
            if not w.Derived.IsEmpty then
                Html.div [
                    prop.className "space-y-1"
                    prop.children [
                        for d in w.Derived do
                            Html.div [
                                prop.className
                                    "flex items-center justify-between gap-3 p-2 rounded border border-blue-200 bg-blue-50 text-sm"
                                prop.children [
                                    Html.span [
                                        prop.className "text-blue-800 font-mono text-xs"
                                        prop.text (ColumnMapping.describeDerivedColumn d)
                                    ]
                                    Html.button [
                                        prop.className "text-xs text-red-600 hover:text-red-800 hover:underline"
                                        prop.text msgs.RemoveButton
                                        prop.onClick (fun _ -> dispatch (RemoveDerivedColumn d.Field))
                                    ]
                                ]
                            ]
                    ]
                ]

            DerivedColumnBuilder fieldNames w.Headers (fun dc -> dispatch (AddDerivedColumn dc))
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
let private validationView (msgs: MappingDataManagerMessages) (report: DryRunReport) (saving: bool) dispatch =
    let hasFailures = report.FailedRows > 0 || not report.RowIssues.IsEmpty

    let summaryBadge =
        if not hasFailures then
            "border-green-200 bg-green-50 text-green-800", msgs.AllRowsValidatedCleanly report.TotalRows
        elif report.CommitBlocked then
            "border-red-200 bg-red-50 text-red-800", msgs.RowsFailBlocked report.FailedRows report.TotalRows
        else
            "border-amber-200 bg-amber-50 text-amber-800", msgs.RowsFailWarn report.FailedRows report.TotalRows

    let badgeClass, summaryText = summaryBadge

    let byColumn = report.CellIssues |> List.groupBy _.Column

    let columnCard (column: string, issues: DryRunCellIssue list) =
        Html.div [
            prop.className "p-3 rounded border border-amber-200 bg-amber-50 space-y-1"
            prop.children [
                Html.div [
                    prop.className "text-sm font-medium text-amber-800"
                    prop.text (msgs.FailingCellsHeading column issues.Length)
                ]
                for issue in issues |> List.truncate 5 do
                    let reason =
                        match issue.Violation with
                        | Some v -> v
                        | None -> msgs.ExpectedValue issue.Expected

                    Html.div [
                        prop.className "text-xs text-amber-700"
                        prop.text (msgs.RowIssueDetail issue.Row issue.Actual reason)
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
                Html.div [ prop.className "text-xs text-gray-500"; prop.text msgs.TruncatedCellsNote ]

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
                        prop.text (if saving then msgs.Importing else msgs.ImportButton)
                        prop.onClick (fun _ ->
                            if (not report.CommitBlocked) && not saving then
                                dispatch CommitConversion)
                    ]
                    Html.button [
                        prop.className "text-sm text-gray-600 hover:text-gray-900 hover:underline"
                        prop.disabled saving
                        prop.text msgs.BackToMapping
                        prop.onClick (fun _ ->
                            if not saving then
                                dispatch BackToMapping)
                    ]
                ]
            ]
        ]
    ]

let private wizardView
    (msgs: MappingDataManagerMessages)
    (displays: DataTypeDisplay list)
    (allowedTypeIds: Set<DataTypeId> option)
    (w: Wizard)
    dispatch
    =
    let header =
        Html.div [
            prop.className "flex items-center justify-between"
            prop.children [
                Typography.subheading (msgs.MapFileNameHeading w.FileName)
                Html.button [
                    prop.className "text-sm text-gray-600 hover:text-gray-900 hover:underline"
                    prop.text msgs.CancelButton
                    prop.onClick (fun _ -> dispatch CancelWizard)
                ]
            ]
        ]

    let body =
        match w.Step with
        | ReviewValidation ->
            match w.Validation with
            | Some report -> validationView msgs report w.Saving dispatch
            | None -> Html.none
        | ReviewData -> reviewDataView msgs w dispatch
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
                        prop.text (msgs.DetectedColumnsPrompt w.Headers.Length)
                    ]
                    match tabular with
                    | [] ->
                        Html.p [
                            prop.className "text-sm text-gray-500"
                            prop.text msgs.NoSchemaTypesRegistered
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
                let derivedErrors = ColumnMapping.validateDerivedColumns w.Headers w.Derived
                let canConfirm = blockers.IsEmpty && derivedErrors.IsEmpty && not w.Validating

                Html.div [
                    prop.className "space-y-3"
                    prop.children [
                        Html.div [
                            prop.className "flex items-center justify-between"
                            prop.children [
                                Html.div [
                                    prop.className "text-sm text-gray-600"
                                    prop.text (msgs.MappingToLabel(labelFor displays suggestion.TargetTypeId))
                                ]
                                Html.button [
                                    prop.className "text-xs text-gray-500 hover:underline"
                                    prop.text msgs.ChangeFormatButton
                                    prop.onClick (fun _ -> dispatch ChangeFormat)
                                ]
                            ]
                        ]

                        if w.ReusedSaved then
                            Html.div [
                                prop.className "p-2 rounded border border-blue-200 bg-blue-50 text-sm text-blue-800"
                                prop.text msgs.ReusedSavedMappingNote
                            ]

                        reviewListView msgs suggestion
                        mappingGridView msgs w suggestion dispatch
                        derivedColumnsView msgs w suggestion dispatch

                        if not blockers.IsEmpty then
                            Html.div [
                                prop.className "text-sm text-red-700"
                                prop.text (msgs.RequiredFieldsUnmapped blockers.Length (String.concat ", " blockers))
                            ]

                        for e in derivedErrors do
                            Html.div [
                                prop.className "text-sm text-red-700"
                                prop.text (msgs.DerivedColumnError e.Field e.Detail)
                            ]

                        Html.div [
                            prop.className "flex items-center gap-3 pt-1"
                            prop.children [
                                Html.button [
                                    prop.className [
                                        "px-4 py-2 rounded-lg text-sm"
                                        Tokens.Typography.buttonText
                                        if canConfirm then
                                            Tokens.Colours.brand
                                            + " "
                                            + Tokens.Colours.brandText
                                            + " hover:bg-brand-dark cursor-pointer"
                                        else
                                            "bg-gray-200 text-gray-500 cursor-not-allowed"
                                    ]
                                    prop.disabled (not canConfirm)
                                    prop.text (
                                        if w.Validating then
                                            msgs.Validating
                                        else
                                            msgs.ConfirmAndValidateButton
                                    )
                                    prop.onClick (fun _ ->
                                        if canConfirm then
                                            dispatch ConfirmMapping)
                                ]
                                Html.span [
                                    prop.className "text-xs text-gray-500"
                                    prop.text msgs.ValidateEveryRowNote
                                ]
                            ]
                        ]
                    ]
                ]

    Layout.Panel.panel msgs.ColumnMappingPanelTitle [ Layout.Panel.panelSection "" [ header; Misc.divider; body ] ]

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

/// Per-file ingestion-status badge (Phase 173) — parity with the
/// built-in `FileManagerUI` and KB. `NotIngested` renders nothing.
let private ingestionBadge (msgs: MappingDataManagerMessages) (status: FileIngestionStatus) =
    match status with
    | FileIngestionStatus.Indexed ->
        Html.span [
            prop.className
                "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-700"
            prop.title msgs.IndexedTooltip
            prop.text msgs.IndexedBadge
        ]
    | FileIngestionStatus.Pending ->
        Html.span [
            prop.className "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-blue-100 text-blue-700"
            prop.title msgs.IndexingTooltip
            prop.text msgs.IndexingBadge
        ]
    | FileIngestionStatus.Failed reason ->
        Html.span [
            prop.className "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-red-100 text-red-700"
            prop.title reason
            prop.text msgs.NotIndexedBadge
        ]
    | FileIngestionStatus.NotIngested -> Html.none

/// Owns the live-update subscription for ingestion-status badges
/// (renders nothing). Mirrors KB's `KbStatusBanner` `useEffectOnce`
/// subscribe/dispose lifecycle.
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

/// Phase 220 — status-filter dropdown over the file list.
let private filterLabel (msgs: MappingDataManagerMessages) =
    function
    | AllFiles -> msgs.FilterAll
    | OnlyIndexed -> msgs.IndexedBadge
    | OnlyPending -> msgs.FilterIndexing
    | OnlyFailed -> msgs.NotIndexedBadge
    | OnlyNotIndexed -> msgs.FilterNotAttempted

let private allFilters = [ AllFiles; OnlyIndexed; OnlyPending; OnlyFailed; OnlyNotIndexed ]

let private statusFilterControl (msgs: MappingDataManagerMessages) (model: Model) dispatch =
    Html.div [
        prop.className "flex items-center gap-2 mb-3"
        prop.children [
            Html.span [ prop.className "text-sm text-gray-600"; prop.text msgs.FilterByIndexStatus ]
            Html.select [
                prop.className "border border-gray-300 rounded px-2 py-1 text-sm"
                prop.value (filterLabel msgs model.StatusFilter)
                prop.onChange (fun (v: string) ->
                    match allFilters |> List.tryFind (fun f -> filterLabel msgs f = v) with
                    | Some f -> dispatch (SetStatusFilter f)
                    | None -> ())
                prop.children [
                    for f in allFilters do
                        Html.option [ prop.value (filterLabel msgs f); prop.text (filterLabel msgs f) ]
                ]
            ]
        ]
    ]

let private filesView (msgs: MappingDataManagerMessages) (displays: DataTypeDisplay list) (model: Model) dispatch =
    // Show genuine uploads only — a confirmed mapping uploads its rewritten
    // CSV as a produced file (`X__Type.csv`); that belongs in the data-object
    // section beneath, not as another row here. So the file count tracks
    // uploads, while the data-object count tracks mappings.
    let producedNames = model.Records |> List.map _.ProducedFile |> Set.ofList

    let rawFiles =
        model.UploadedFiles
        |> List.filter (fun f -> not (producedNames.Contains f.FileName))

    // Phase 220 — client-side status filter over the already-fetched status
    // set. `AllFiles` (and the no-RAG case) passes everything through.
    let displayFiles =
        rawFiles
        |> List.filter (fun f ->
            FileIngestionStatus.matchesFilter model.StatusFilter (model.IngestionStatus |> Map.tryFind f.FileName))

    match rawFiles with
    | [] -> Html.p [ prop.className "text-gray-500"; prop.text msgs.NoFilesImportedYet ]
    | _ ->
        Html.div [
            prop.className "space-y-4"
            prop.children [
                if not (Map.isEmpty model.IngestionStatus) then
                    statusFilterControl msgs model dispatch

                if displayFiles.IsEmpty then
                    Html.p [ prop.className "text-gray-500"; prop.text msgs.NoFilesMatchFilter ]

                Html.table [
                    prop.className "w-full text-sm border-collapse"
                    prop.children [
                        Html.thead [
                            Html.tr [
                                prop.className "text-left text-gray-600 border-b border-gray-200"
                                prop.children [
                                    Html.th [ prop.className "py-2 pr-3"; prop.text msgs.ColumnDataType ]
                                    Html.th [ prop.className "py-2 pr-3"; prop.text msgs.ColumnFileName ]
                                    Html.th [ prop.className "py-2 pr-3"; prop.text msgs.ColumnRows ]
                                    Html.th [ prop.className "py-2 pr-3"; prop.text msgs.ColumnSize ]
                                    // Ingestion-status column header — only when
                                    // RAG is composed (the snapshot carries status).
                                    if not (Map.isEmpty model.IngestionStatus) then
                                        Html.th [ prop.className "py-2 pr-3"; prop.text msgs.ColumnSearchIndex ]
                                    Html.th [ prop.className "py-2 pr-3"; prop.text "" ]
                                ]
                            ]
                        ]
                        Html.tbody [
                            prop.children [
                                for f in displayFiles do
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
                                                prop.text (dataTypeLabel msgs displays f.DataType)
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
                                                                    msgs.NoRemediationLabel
                                                                else
                                                                    String.concat "; " r.RemediationSteps

                                                            msgs.ConvertedFromTooltip r.SourceFile steps

                                                        Html.span [
                                                            prop.className
                                                                "ml-2 inline-block px-1.5 py-0.5 rounded border border-blue-200 bg-blue-50 text-blue-700 text-xs"
                                                            prop.title tip
                                                            prop.text msgs.ConvertedBadge
                                                        ]
                                                    | None -> ()
                                                ]
                                            ]
                                            Html.td [ prop.className "py-2 pr-3"; prop.text (string f.RowCount) ]
                                            Html.td [
                                                prop.className "py-2 pr-3"
                                                prop.text (formatSize msgs f.SizeBytes)
                                            ]
                                            if not (Map.isEmpty model.IngestionStatus) then
                                                let status = model.IngestionStatus |> Map.tryFind f.FileName

                                                Html.td [
                                                    prop.className "py-2 pr-3"
                                                    prop.children [
                                                        Html.div [
                                                            prop.className "flex items-center gap-2"
                                                            prop.children [
                                                                match status with
                                                                | Some s -> ingestionBadge msgs s
                                                                | None -> Html.none
                                                                // Phase 220 — one-click re-ingest on Failed.
                                                                if FileIngestionStatus.isRetryable status then
                                                                    Html.button [
                                                                        prop.className
                                                                            "text-xs text-blue-600 hover:text-blue-800 hover:underline"
                                                                        prop.title msgs.RetryIngestionTooltip
                                                                        prop.text msgs.RetryButton
                                                                        prop.onClick (fun _ ->
                                                                            dispatch (RetryIngestionMsg f.FileName))
                                                                    ]
                                                            ]
                                                        ]
                                                    ]
                                                ]
                                            Html.td [
                                                prop.className "py-2 pr-3"
                                                prop.children [
                                                    Html.div [
                                                        prop.className "flex items-center gap-3"
                                                        prop.children [
                                                            Html.button [
                                                                prop.className "text-sm text-brand hover:underline"
                                                                prop.title msgs.NewMappingTooltip
                                                                prop.text msgs.NewMappingButton
                                                                prop.onClick (fun _ ->
                                                                    dispatch (StartMapping f.FileName))
                                                            ]
                                                            Html.button [
                                                                prop.className
                                                                    "text-sm text-blue-600 hover:text-blue-800 hover:underline"
                                                                prop.title msgs.ReprocessTooltip
                                                                prop.text msgs.ReprocessButton
                                                                prop.onClick (fun _ ->
                                                                    dispatch (ReprocessFile f.FileName))
                                                            ]
                                                            Html.button [
                                                                prop.className
                                                                    "text-sm text-red-600 hover:text-red-800 hover:underline"
                                                                prop.text msgs.DeleteButton
                                                                prop.onClick (fun _ ->
                                                                    let prompt = msgs.DeleteFileConfirm f.FileName

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
                        Html.div [ prop.className "text-sm text-gray-600"; prop.text msgs.ResetScopeNote ]
                        Html.button [
                            prop.className
                                "text-sm px-3 py-1.5 rounded border border-red-300 text-red-700 hover:bg-red-50 hover:border-red-400 transition-colors whitespace-nowrap"
                            prop.title msgs.ResetDataStoreTooltip
                            prop.text msgs.ResetDataStoreButton
                            prop.onClick (fun _ ->
                                let prompt = msgs.ResetConfirm totalFileCount

                                if Browser.Dom.window.confirm prompt then
                                    dispatch ResetDataStore)
                        ]
                    ]
                ]
            ]
        ]

// ─── Module body panes (Phase 751) ─────────────────────────────────
//
// `view` returns a `ReactElement * ReactElement` tuple (input pane, output
// pane) rather than a single tree, so unlike `HealthMonitorUI`'s
// single-component wrapper, each pane needs its OWN React component —
// `[<ReactComponent>]` requires a single `ReactElement` return, so one
// component cannot own both halves of the tuple. `view` itself stays a
// plain (non-hook) function invoked inline by the shell's own render, and
// each pane's component calls `useMessages ()` independently.

[<ReactComponent>]
let private ImportPanel (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let msgs = (MessageCatalogProvider.useMessages ()).MappingDataManager

    Layout.Panel.panel msgs.ImportCsvPanelTitle [
        Layout.Panel.panelSection msgs.UploadFileSectionTitle [
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
                            prop.text msgs.ChooseCsvButton
                        ]
                    )
                    Html.span [
                        prop.className "text-base text-gray-500"
                        prop.text (
                            if model.Busy then
                                msgs.CheckingKnownStructure
                            else
                                msgs.UploadHelpText
                        )
                    ]
                ]
            ]
        ]
    ]

[<ReactComponent>]
let private FilesOrWizardPanel (displays: DataTypeDisplay list) (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let msgs = (MessageCatalogProvider.useMessages ()).MappingDataManager

    Html.div [
        prop.children [
            // Owns the ingestion-status live-update subscription (renders
            // nothing); mounted unconditionally so it subscribes even
            // while the wizard is open or the list is empty.
            IngestionStatusSubscriber dispatch

            match model.Wizard with
            | Some w -> wizardView msgs displays model.AllowedTypeIds w dispatch
            | None -> Layout.Panel.panel msgs.ImportedFilesPanelTitle [ filesView msgs displays model dispatch ]

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

let private view (displays: DataTypeDisplay list) (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
    ImportPanel model dispatch, FilesOrWizardPanel displays model dispatch

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
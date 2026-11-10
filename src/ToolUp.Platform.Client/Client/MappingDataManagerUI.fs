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
/// Fills the data-manager shell slot when it is
/// `SlotFill.Configured (DataManagerChoice.ColumnMapping _)`; pair with
/// `ServerConfig.ColumnMapping = FeatureMode.Enabled` to back the store.
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

// ─── The held file ────────────────────────────────────────────────

/// A CSV read for mapping: its bytes, its parsed preview and the
/// fingerprint of its column structure. The upload pipeline threads one
/// through its messages (so concurrent multi-file uploads don't clobber
/// one another), and the mapping wizard holds the one it works on.
type HeldFile = {
    FileName: string
    RawContents: string
    Headers: string list
    Samples: Map<string, string list>
    Fingerprint: string
}

/// Parse a CSV's preview and fingerprint into a `HeldFile`.
let private heldOf (fileName: string) (contents: string) : HeldFile =
    let headers, samples = ColumnMapping.parsePreview 20 contents

    {
        FileName = fileName
        RawContents = contents
        Headers = headers
        Samples = samples
        Fingerprint = ColumnMapping.Fingerprint.ofHeaders headers
    }

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
/// readable localised label rather than the raw `UnrecognisedData` id.
let private dataTypeLabel (msgs: MappingDataManagerMessages) (displays: DataTypeDisplay list) (dataTypeId: DataTypeId) =
    if dataTypeId = "UnrecognisedData" then
        msgs.UnrecognisedLabel
    else
        labelFor displays dataTypeId

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
let private remediationSteps (msgs: MappingDataManagerMessages) (conversion: Conversion) : string list =
    let remediation =
        conversion.Remediation
        |> Map.toList
        |> List.choose (fun (col, ts) -> ColumnMapping.describeColumnRemediation col ts)

    let derived =
        conversion.Derived
        |> List.map (fun d -> msgs.DerivedRemediationStep(ColumnMapping.describeDerivedColumn d))

    remediation @ derived

/// Rewrite + upload each conversion as its own data object, then record
/// its provenance (`RecordConversion`). Returns the target-type ids
/// imported, or the first error. Conversions whose target type is no
/// longer registered are skipped.
let private importConversions
    (msgs: MappingDataManagerMessages)
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
                            RemediationSteps = remediationSteps msgs c
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
    (msgs: MappingDataManagerMessages)
    (displays: DataTypeDisplay list)
    (held: HeldFile)
    (conversion: Conversion)
    : Async<Result<DataTypeId list, string>> =
    async {
        match! conversionApi.SaveConversion conversion with
        | Error e -> return Error e
        | Ok() -> return! importConversions msgs displays held [ conversion ]
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

// ─── The mapping wizard ───────────────────────────────────────────

/// The mapping wizard as a sub-model: review the data quality, pick the
/// target format, review the auto-mapped fields, preview the dry-run
/// validation, commit. The parent holds it as an option and routes
/// `WizardMsg` to it; what the parent must act on (closed, imported, an
/// error to show) comes back as an `Outcome`, never through the parent's
/// model.
module Wizard =

    type Step =
        /// Data-quality scan + remediation, shown first when issues are found.
        | ReviewData
        | PickTarget
        | ReviewMapping
        /// Dry-run validation preview shown after the mapping is confirmed
        /// and before commit: per-row / per-cell errors of the *mapped* CSV
        /// under the chosen schema.
        | ReviewValidation

    type Model = {
        File: HeldFile
        Step: Step
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
        /// Derived/computed columns the user has added: a schema field
        /// produced from a `ColumnExpr` over source columns rather than a
        /// 1:1 map. Auto-suggestion never produces these (explicit user
        /// intent), so they start empty and only grow via the builder.
        Derived: DerivedColumn list
        ReusedSaved: bool
        Saving: bool
        /// The dry-run validation report for the confirmed mapping
        /// (populated on the `ReviewValidation` step).
        Validation: DryRunReport option
        /// `true` while the dry-run `ValidateConversion` round-trip is in
        /// flight (drives the "Validating…" affordance on confirm).
        Validating: bool
    }

    type Msg =
        | ToggleColumnFixes of column: string
        | SetDateOrder of column: string * order: DateOrder
        | ProceedToMapping
        | SelectTarget of DataTypeId
        | ChangeFormat
        | OverrideColumn of field: string * column: string option
        /// Add a derived/computed column (replaces any existing derived
        /// column for the same target field).
        | AddDerivedColumn of DerivedColumn
        /// Drop the derived column for a target field.
        | RemoveDerivedColumn of field: string
        | Cancel
        /// Confirm the mapping → run the dry-run validation (no commit yet).
        | ConfirmMapping
        | ValidationFinished of Result<DryRunReport, string>
        /// Return from the validation preview to revise the mapping.
        | BackToMapping
        /// Commit the validated conversion (save recipe + import the object).
        | CommitConversion
        | CommitFinished of Result<DataTypeId list, string>
        | CallFailed of string

    /// What the parent acts on after a wizard step.
    type Outcome =
        /// Nothing for the parent to do.
        | Stayed
        /// A round-trip started: clear any error the parent shows.
        | ErrorCleared
        /// Show this error; the wizard stays open.
        | Errored of string
        /// The user cancelled: close the wizard.
        | Cancelled
        /// The conversion was committed: close the wizard and refresh.
        | Imported

    /// The chosen source column for a field — user override if present,
    /// otherwise the auto-suggestion.
    let private chosenColumn (w: Model) (field: FieldSuggestion) : string option =
        match Map.tryFind field.Field.Name w.Overrides with
        | Some ov -> ov
        | None -> field.SuggestedColumn

    /// The schema fields produced by a derived expression (so they're not also
    /// drawn from a 1:1 mapped column — derived wins).
    let private derivedFieldSet (w: Model) : Set<string> =
        w.Derived |> List.map _.Field |> Set.ofList

    /// field name → source column, for every field that resolves to a column.
    /// Derived fields are excluded — their value comes from the `ColumnExpr`,
    /// not a 1:1 map.
    let private effectiveMapping (w: Model) : Map<string, string> =
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
    let private unresolvedRequired (w: Model) : string list =
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
    let private columnTransforms (w: Model) (p: ColumnProfile) : CellTransform list =
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
    let private wizardTransforms (w: Model) : Map<string, CellTransform list> =
        w.Profiles
        |> List.choose (fun p ->
            match columnTransforms w p with
            | [] -> None
            | ts -> Some(p.Column, ts))
        |> Map.ofList

    /// Ambiguous-date columns still awaiting an order choice — block Continue.
    let private unresolvedDates (w: Model) : string list =
        w.Profiles
        |> List.filter (fun p -> hasAmbiguousDate p && (w.DateOrders |> Map.tryFind p.Column).IsNone)
        |> List.map _.Column

    /// Samples with the chosen transforms applied — so `suggest` sees clean,
    /// correctly-typed values.
    let private remediatedSamples (w: Model) : Map<string, string list> =
        let transforms = wizardTransforms w

        w.File.Samples
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
    let init (file: HeldFile) : Model =
        let profiles =
            file.Headers
            |> List.map (fun h ->
                ColumnMapping.profileColumn h (file.Samples |> Map.tryFind h |> Option.defaultValue []))

        let hasIssues = profiles |> List.exists (fun p -> not p.Issues.IsEmpty)

        {
            File = file
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
    let private buildConversion (w: Model) : Conversion option =
        w.TargetTypeId
        |> Option.map (fun typeId -> {
            Fingerprint = w.File.Fingerprint
            TargetTypeId = typeId
            Mapping = effectiveMapping w
            Remediation = wizardTransforms w
            SourceHeaders = w.File.Headers
            Derived = w.Derived
            CreatedBy = ""
            CreatedAt = System.DateTime.UtcNow
        })

    /// The conversion to validate or commit, when a target with a schema is
    /// chosen; `Error` names why not (`None` when no target is chosen yet).
    let private readyConversion (msgs: MappingDataManagerMessages) (displays: DataTypeDisplay list) (w: Model) =
        match w.TargetTypeId with
        | None -> Error None
        | Some typeId ->
            match schemaFor displays typeId, buildConversion w with
            | Some _, Some conversion -> Ok conversion
            | _ -> Error(Some msgs.NoSchemaPublished)

    let private resetMapping (w: Model) : Model = {
        w with
            TargetTypeId = None
            Suggestion = None
            Overrides = Map.empty
            Derived = []
            ReusedSaved = false
    }

    let update
        (msgs: MappingDataManagerMessages)
        (displays: DataTypeDisplay list)
        (msg: Msg)
        (model: Model)
        : Model * Cmd<Msg> * Outcome =
        match msg with
        | ToggleColumnFixes column ->
            let disabled =
                if model.DisabledFixes.Contains column then
                    Set.remove column model.DisabledFixes
                else
                    Set.add column model.DisabledFixes

            { model with DisabledFixes = disabled }, Cmd.none, Stayed

        | SetDateOrder(column, order) ->
            {
                model with
                    DateOrders = Map.add column order model.DateOrders
            },
            Cmd.none,
            Stayed

        | ProceedToMapping -> { model with Step = PickTarget }, Cmd.none, Stayed

        | SelectTarget typeId ->
            match schemaFor displays typeId with
            | Some schema ->
                // suggest against remediated samples so cleaned columns
                // read with their true types.
                let suggestion =
                    ColumnMapping.suggest typeId schema model.File.Headers (remediatedSamples model)

                {
                    resetMapping model with
                        TargetTypeId = Some typeId
                        Suggestion = Some suggestion
                        Step = ReviewMapping
                },
                Cmd.none,
                Stayed
            | None -> model, Cmd.none, Errored msgs.NoSchemaCannotMap

        | ChangeFormat ->
            {
                resetMapping model with
                    Step = PickTarget
            },
            Cmd.none,
            Stayed

        | OverrideColumn(field, col) ->
            {
                model with
                    Overrides = Map.add field col model.Overrides
            },
            Cmd.none,
            Stayed

        | AddDerivedColumn derived ->
            // Replace any existing derived column for the same field, then
            // clear a now-stale 1:1 override for it (derived wins).
            let next =
                (model.Derived |> List.filter (fun d -> d.Field <> derived.Field)) @ [ derived ]

            {
                model with
                    Derived = next
                    Overrides = Map.add derived.Field None model.Overrides
            },
            Cmd.none,
            Stayed

        | RemoveDerivedColumn field ->
            {
                model with
                    Derived = model.Derived |> List.filter (fun d -> d.Field <> field)
            },
            Cmd.none,
            Stayed

        | Cancel -> model, Cmd.none, Cancelled

        | ConfirmMapping ->
            // Confirm does not commit: it runs the dry-run validation first
            // (rewrite → schema validate, no write, no `DataType.Process`)
            // and shows the per-row/per-cell report.
            match readyConversion msgs displays model with
            | Ok conversion ->
                let request: DryRunValidationRequest = {
                    Conversion = conversion
                    RawCsv = model.File.RawContents
                }

                { model with Validating = true },
                Cmd.OfAsync.either conversionApi.ValidateConversion request ValidationFinished (fun ex ->
                    CallFailed ex.Message),
                ErrorCleared
            | Error(Some e) -> model, Cmd.none, Errored e
            | Error None -> model, Cmd.none, Stayed

        | ValidationFinished(Ok report) ->
            {
                model with
                    Validating = false
                    Validation = Some report
                    Step = ReviewValidation
            },
            Cmd.none,
            Stayed

        | ValidationFinished(Error e) -> { model with Validating = false }, Cmd.none, Errored e

        | BackToMapping ->
            {
                model with
                    Step = ReviewMapping
                    Validation = None
            },
            Cmd.none,
            Stayed

        | CommitConversion ->
            // The validated conversion is committed: save the recipe
            // (additive — keyed by fingerprint+type), then import it as its
            // own data object (recording provenance). The conversion is
            // rebuilt from the same pure wizard state that was validated, so
            // the committed shape is the validated shape.
            match readyConversion msgs displays model with
            | Ok conversion ->
                { model with Saving = true },
                Cmd.OfAsync.either (saveAndImport msgs displays model.File) conversion CommitFinished (fun ex ->
                    CallFailed ex.Message),
                Stayed
            | Error(Some e) -> model, Cmd.none, Errored e
            | Error None -> model, Cmd.none, Stayed

        | CommitFinished(Ok _) -> { model with Saving = false }, Cmd.none, Imported

        | CommitFinished(Error e) -> { model with Saving = false }, Cmd.none, Errored e

        | CallFailed e ->
            {
                model with
                    Saving = false
                    Validating = false
            },
            Cmd.none,
            Errored e

    // ─── View ─────────────────────────────────────────────────────────

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

    let private columnSelect (msgs: MappingDataManagerMessages) (w: Model) (field: FieldSuggestion) dispatch =
        let current = chosenColumn w field |> Option.defaultValue ""

        Html.select [
            prop.className "border border-gray-300 rounded px-2 py-1 text-sm w-full"
            prop.value current
            prop.onChange (fun (v: string) ->
                let col = if v = "" then None else Some v
                dispatch (OverrideColumn(field.Field.Name, col)))
            prop.children [
                Html.option [ prop.value ""; prop.text msgs.NotMappedOption ]
                for h in w.File.Headers do
                    Html.option [ prop.value h; prop.text (columnLabel w.Profiles h) ]
            ]
        ]

    // ─── ReviewData step ──────────────────────────────────────────────

    let private dateOrderName (msgs: MappingDataManagerMessages) =
        function
        | DayFirst -> msgs.DateOrderDayFirst
        | MonthFirst -> msgs.DateOrderMonthFirst
        | YearFirst -> msgs.DateOrderYearFirst

    let private reviewDataView (msgs: MappingDataManagerMessages) (w: Model) dispatch =
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
                                            prop.className ("ml-2 text-xs " + Tokens.Text.muted)
                                            prop.text (msgs.UnitKeptInLabel u)
                                        ]
                                    | None -> ()
                                ]
                            ]
                            // opt-out toggle for the safe fixes (dates excepted)
                            if p.Issues |> List.exists _.Safe then
                                Html.label [
                                    prop.className (
                                        "flex items-center gap-1.5 text-xs cursor-pointer " + Tokens.Text.secondary
                                    )
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
                            prop.className ("text-xs " + Tokens.Text.secondary)
                            prop.children [
                                Html.span [
                                    prop.className (
                                        if issue.NeedsChoice then
                                            "text-amber-700 font-medium"
                                        else
                                            Tokens.Text.secondary
                                    )
                                    prop.text ((if issue.NeedsChoice then "⚠ " else "• ") + issue.Detail)
                                ]
                                if not issue.Examples.IsEmpty then
                                    Html.span [
                                        prop.className ("ml-1 " + Tokens.Text.muted)
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
                                                prop.onChange (fun (_: bool) ->
                                                    dispatch (SetDateOrder(p.Column, order)))
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
                                prop.className ("text-xs " + Tokens.Text.muted)
                                prop.text (msgs.PreviewBeforeAfter raw after)
                            ]
                    | None -> ()
                ]
            ]

        Html.div [
            prop.className "space-y-3"
            prop.children [
                Html.p [
                    prop.className ("text-sm " + Tokens.Text.secondary)
                    prop.text msgs.ReviewDataIntro
                ]
                for p in problemColumns do
                    columnCard p

                if not blockers.IsEmpty then
                    Html.div [
                        prop.className "text-sm text-amber-700"
                        prop.text (msgs.ChooseDateOrderFor(String.concat ", " blockers))
                    ]

                Forms.Button.primaryWith
                    (if blockers.IsEmpty then
                         Forms.Button.Enabled
                     else
                         Forms.Button.Disabled)
                    msgs.ContinueToMapping
                    (fun () -> dispatch ProceedToMapping)
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

    let private mappingGridView (msgs: MappingDataManagerMessages) (w: Model) (suggestion: MappingSuggestion) dispatch =
        let derivedFor (f: FieldSuggestion) =
            w.Derived |> List.tryFind (fun d -> d.Field = f.Field.Name)

        let fieldCell (f: FieldSuggestion) =
            Html.div [
                Html.span [ prop.className "font-medium"; prop.text f.Field.Name ]
                if f.Field.Required then
                    Html.span [
                        prop.className "text-red-600 ml-0.5"
                        prop.title msgs.RequiredTooltip
                        prop.text "*"
                    ]
            ]

        let columnCell (f: FieldSuggestion) =
            Html.div [
                prop.className "min-w-48"
                prop.children [
                    match derivedFor f with
                    | Some d ->
                        Html.span [
                            prop.className "text-xs font-mono text-blue-800"
                            prop.text (ColumnMapping.describeColumnExpr d.Expr)
                        ]
                    | None -> columnSelect msgs w f dispatch
                ]
            ]

        let matchCell (f: FieldSuggestion) =
            match derivedFor f with
            | Some _ ->
                Html.span [
                    prop.className
                        "inline-block px-2 py-0.5 rounded border text-xs text-blue-700 bg-blue-50 border-blue-200"
                    prop.text msgs.DerivedBadge
                ]
            | None ->
                let badgeText, badgeClass, _ = flagBadge msgs f.Flag

                Html.span [
                    prop.className $"inline-block px-2 py-0.5 rounded border text-xs {badgeClass}"
                    prop.text badgeText
                ]

        Data.table
            [
                {
                    Header = msgs.TargetField
                    Align = Data.Left
                    Cell = fieldCell
                }
                {
                    Header = msgs.ColumnType
                    Align = Data.Left
                    Cell =
                        fun f ->
                            Html.span [
                                prop.className Tokens.Text.muted
                                prop.text (columnTypeName msgs f.Field.Type)
                            ]
                }
                {
                    Header = msgs.ColumnCsvColumn
                    Align = Data.Left
                    Cell = columnCell
                }
                {
                    Header = msgs.ColumnMatch
                    Align = Data.Left
                    Cell = matchCell
                }
            ]
            suggestion.Fields

    // ─── Derived-column builder ───────────────────────────────────────

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

    /// The builder is a `[<ReactComponent>]` (it holds `React.useState`
    /// hooks), so the catalog hook joins the existing hook order rather than
    /// needing a wrapper of its own.
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
            | "concat" when colA <> "" && colB <> "" ->
                Some(Concat([ SourceColumn colA; SourceColumn colB ], textParam))
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
                prop.className ("flex flex-col gap-0.5 text-xs " + Tokens.Text.secondary)
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
                prop.className ("flex flex-col gap-0.5 text-xs " + Tokens.Text.secondary)
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
                    prop.className ("text-sm font-medium " + Tokens.Text.secondary)
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

                        Forms.Button.primaryWith
                            (if canAdd then
                                 Forms.Button.Enabled
                             else
                                 Forms.Button.Disabled)
                            msgs.AddButton
                            (fun () ->
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
                Html.div [
                    prop.className ("text-xs " + Tokens.Text.muted)
                    prop.text msgs.DerivedColumnsFootnote
                ]
            ]
        ]

    /// Lists the derived columns already added (with a remove affordance) and
    /// the builder. Shown on the mapping-review step beneath the field grid.
    let private derivedColumnsView
        (msgs: MappingDataManagerMessages)
        (w: Model)
        (suggestion: MappingSuggestion)
        dispatch
        =
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

                DerivedColumnBuilder fieldNames w.File.Headers (fun dc -> dispatch (AddDerivedColumn dc))
            ]
        ]

    // ─── ReviewValidation step ────────────────────────────────────────

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
                    Html.div [
                        prop.className ("text-xs " + Tokens.Text.muted)
                        prop.text msgs.TruncatedCellsNote
                    ]

                Html.div [
                    prop.className "flex items-center gap-3 pt-1"
                    prop.children [
                        Forms.Button.primaryWith
                            (Forms.Button.stateOf saving msgs.Importing report.CommitBlocked)
                            msgs.ImportButton
                            (fun () -> dispatch CommitConversion)
                        Forms.Button.secondaryWith
                            (if saving then
                                 Forms.Button.Disabled
                             else
                                 Forms.Button.Enabled)
                            msgs.BackToMapping
                            (fun () -> dispatch BackToMapping)
                    ]
                ]
            ]
        ]

    let view
        (msgs: MappingDataManagerMessages)
        (displays: DataTypeDisplay list)
        (allowedTypeIds: Set<DataTypeId> option)
        (w: Model)
        dispatch
        =
        let header =
            Html.div [
                prop.className "flex items-center justify-between"
                prop.children [
                    Typography.subheading (msgs.MapFileNameHeading w.File.FileName)
                    Html.button [
                        prop.className (
                            "text-sm hover:text-[var(--text-strong)] hover:underline "
                            + Tokens.Text.secondary
                        )
                        prop.text msgs.CancelButton
                        prop.onClick (fun _ -> dispatch Cancel)
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
                            prop.className ("text-sm " + Tokens.Text.secondary)
                            prop.text (msgs.DetectedColumnsPrompt w.File.Headers.Length)
                        ]
                        match tabular with
                        | [] ->
                            Html.p [
                                prop.className ("text-sm " + Tokens.Text.muted)
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
                                                        prop.className ("text-xs " + Tokens.Text.muted)
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
                    let derivedErrors = ColumnMapping.validateDerivedColumns w.File.Headers w.Derived
                    let canConfirm = blockers.IsEmpty && derivedErrors.IsEmpty

                    Html.div [
                        prop.className "space-y-3"
                        prop.children [
                            Html.div [
                                prop.className "flex items-center justify-between"
                                prop.children [
                                    Html.div [
                                        prop.className ("text-sm " + Tokens.Text.secondary)
                                        prop.text (msgs.MappingToLabel(labelFor displays suggestion.TargetTypeId))
                                    ]
                                    Html.button [
                                        prop.className ("text-xs hover:underline " + Tokens.Text.muted)
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
                                    prop.text (
                                        msgs.RequiredFieldsUnmapped blockers.Length (String.concat ", " blockers)
                                    )
                                ]

                            for e in derivedErrors do
                                Html.div [
                                    prop.className "text-sm text-red-700"
                                    prop.text (msgs.DerivedColumnError e.Field e.Detail)
                                ]

                            Html.div [
                                prop.className "flex items-center gap-3 pt-1"
                                prop.children [
                                    Forms.Button.primaryWith
                                        (Forms.Button.stateOf w.Validating msgs.Validating (not canConfirm))
                                        msgs.ConfirmAndValidateButton
                                        (fun () -> dispatch ConfirmMapping)
                                    Html.span [
                                        prop.className ("text-xs " + Tokens.Text.muted)
                                        prop.text msgs.ValidateEveryRowNote
                                    ]
                                ]
                            ]
                        ]
                    ]

        Layout.Panel.panel msgs.ColumnMappingPanelTitle [ Layout.Panel.panelSection "" [ header; Misc.divider; body ] ]

// ─── Model ────────────────────────────────────────────────────────

type Model = {
    UploadedFiles: UploadedFileInfo list
    ProcessedData: ProcessedFileEntry list
    /// Per-file RAG ingestion status (fileName → status), hydrated from
    /// `FileListSnapshot.Ingestion` and patched live via
    /// `DataManagerIngestionStatusKey` notifications. Empty
    /// when no RAG is composed ⇒ no status column.
    IngestionStatus: Map<string, FileIngestionStatus>
    /// Client-side filter over the file list by ingestion status.
    /// `AllFiles` by default.
    StatusFilter: IngestionStatusFilter
    /// Per-object conversion provenance, joined to the file list to mark
    /// which objects were produced by a conversion (+ their steps).
    Records: ConversionRecord list
    /// The open mapping wizard, if any.
    Wizard: Wizard.Model option
    Busy: bool
    Error: string option
    /// The data type ids whose owning module is available
    /// (mappable) to the caller's team, from the availability-filtered
    /// `GetDataCatalog`. `None` until the catalog loads (no filtering yet,
    /// so the wizard never blanks on a slow fetch); `Some ids` filters the
    /// mapping target picker + the saved-mapping auto-reuse so a module
    /// the team has marked `Unavailable` is never offered.
    AllowedTypeIds: Set<DataTypeId> option
    /// This module's section of `ClientModuleContext.Messages`,
    /// kept from `init` so `update` authors reader-facing text in the
    /// session's language (a locale switch re-initialises the module).
    Messages: MappingDataManagerMessages
}

type Msg =
    | LoadFiles
    | FilesLoaded of FileListSnapshot
    | RecordsLoaded of ConversionRecord list
    /// A `DataManagerIngestionStatusKey` notification arrived — patch the
    /// named file's ingestion badge in place (no refetch).
    | IngestionStatusChanged of fileName: string * status: FileIngestionStatus
    /// One-click re-ingest of a `Failed` file. No optimistic state change:
    /// the badge re-renders from the store via the live notification.
    | RetryIngestionMsg of fileName: string
    | RetryIngestionDone of Result<unit, string>
    /// The server-side session store this tab was talking to is gone. Same
    /// event, same subscriber component and same posture as
    /// `FileManagerUI.SessionStoreCleared`: drop the file-derived state
    /// rather than refetch it. The in-flight mapping wizard goes with it
    /// — it is holding a file the server no longer has, so leaving it
    /// open would only let the user finish a mapping that cannot be
    /// applied.
    | SessionStoreCleared
    /// Narrow the file list to one ingestion status (client-side).
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
    /// A message for the open mapping wizard.
    | WizardMsg of Wizard.Msg
    | DeleteFile of string
    | ReprocessFile of string
    | Reprocessed of Result<ProcessedFileEntry, string>
    | ResetDataStore
    | ResetDone of Result<int, string>
    | ApiError of string
    | DismissError

// ─── Update ───────────────────────────────────────────────────────

let init (ctx: ClientModuleContext) =
    {
        UploadedFiles = []
        ProcessedData = []
        IngestionStatus = Map.empty
        StatusFilter = AllFiles
        Records = []
        Wizard = None
        Busy = false
        Error = None
        AllowedTypeIds = None
        Messages = ctx.Messages.MappingDataManager
    },
    Cmd.ofMsg LoadFiles

/// Read a chosen file through a direct effect rather than
/// `Cmd.OfAsync.perform` + `Async.FromContinuations`: under Fable 5 that
/// async path silently no-ops (the deferred start never runs the reader,
/// so `FileChosen` is never dispatched and the import hangs with no error).
/// An effect runs synchronously when the runtime execs the Cmd, and the
/// reader callbacks dispatch straight back — no trampoline, no swallowed
/// errors.
let private readFile (msgs: MappingDataManagerMessages) (file: Browser.Types.File) : Cmd<Msg> =
    Cmd.ofEffect (fun dispatch ->
        let reader = Browser.Dom.FileReader.Create()
        reader.onload <- fun _ -> dispatch (FileChosen(file.name, reader.result :?> string))
        reader.onerror <- fun _ -> dispatch (ApiError(msgs.FileReadFailed file.name))
        reader.readAsText file)

/// Frictionless re-import when the structure is already known: apply every
/// saved mapping whose target type is still registered and whose owning
/// module is available to this team. With none usable, upload the file
/// as-is so the server attempts native detection — recognised or not, it
/// then lands in the list to be mapped later via "New Mapping".
let private importOrUpload (model: Model) (displays: DataTypeDisplay list) (held: HeldFile) (saved: Conversion list) =
    let isAvailable (typeId: DataTypeId) =
        match model.AllowedTypeIds with
        | Some allowed -> allowed.Contains typeId
        | None -> true

    let importable =
        saved
        |> List.filter (fun m -> (schemaFor displays m.TargetTypeId).IsSome && isAvailable m.TargetTypeId)

    if not importable.IsEmpty then
        Cmd.OfAsync.either (importConversions model.Messages displays held) importable ImportFinished (fun ex ->
            ApiError ex.Message)
    else
        let upload = {
            filename = held.FileName
            contents = held.RawContents
            dataType = "UnrecognisedData"
        }

        Cmd.OfRemoting.call fileApi.UploadFile { File = upload } NativeUploaded (fun ex -> ApiError ex.Message)

/// Route a message to the open wizard and act on its outcome.
let private updateWizard (displays: DataTypeDisplay list) (wizardMsg: Wizard.Msg) (model: Model) =
    match model.Wizard with
    | None -> model, Cmd.none
    | Some wizard ->
        let wizard, wizardCmd, outcome =
            Wizard.update model.Messages displays wizardMsg wizard

        let cmd = Cmd.map WizardMsg wizardCmd

        match outcome with
        | Wizard.Stayed -> { model with Wizard = Some wizard }, cmd
        | Wizard.ErrorCleared ->
            {
                model with
                    Wizard = Some wizard
                    Error = None
            },
            cmd
        | Wizard.Errored e ->
            {
                model with
                    Wizard = Some wizard
                    Error = Some e
            },
            cmd
        | Wizard.Cancelled -> { model with Wizard = None }, cmd
        | Wizard.Imported ->
            {
                model with
                    Wizard = None
                    Busy = false
            },
            Cmd.batch [ cmd; Cmd.ofMsg LoadFiles ]

let update (displays: DataTypeDisplay list) (msg: Msg) (model: Model) =
    match msg with
    | LoadFiles ->
        model,
        Cmd.batch [
            Cmd.OfRemoting.call fileApi.ListFiles () FilesLoaded (fun ex -> ApiError ex.Message)
            Cmd.OfRemoting.call conversionApi.ListConversionRecords () RecordsLoaded (fun _ -> RecordsLoaded [])
            // The availability-filtered catalog: a type whose owning module
            // the team marked `Unavailable` is absent here, so it is never
            // offered as a mapping target.
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
                Error = Some(model.Messages.ReingestionFailed msg)
        },
        Cmd.none

    | SetStatusFilter filter -> { model with StatusFilter = filter }, Cmd.none

    | SessionStoreCleared ->
        {
            model with
                UploadedFiles = []
                ProcessedData = []
                IngestionStatus = Map.empty
                Records = []
                Wizard = None
                Busy = false
        },
        Cmd.none

    | CatalogLoaded response ->
        let allowed = response.Types |> List.map (fun e -> e.Info.Id) |> Set.ofList

        {
            model with
                AllowedTypeIds = Some allowed
        },
        Cmd.none

    | CatalogLoadFailed -> model, Cmd.none

    | SelectFile file -> model, readFile model.Messages file

    | FileChosen(fileName, contents) ->
        let held = heldOf fileName contents

        { model with Busy = true; Error = None },
        Cmd.OfRemoting.call
            conversionApi.GetConversions
            held.Fingerprint
            (fun saved -> MappingsFetched(held, saved))
            (fun ex -> ApiError ex.Message)

    | MappingsFetched(held, saved) -> model, importOrUpload model displays held saved

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

    | ImportFinished(Ok _) -> { model with Busy = false }, Cmd.ofMsg LoadFiles

    | ImportFinished(Error msg) ->
        {
            model with
                Busy = false
                Error = Some msg
        },
        Cmd.none

    | StartMapping fileName ->
        // Re-fetch the uploaded file's bytes, then open the wizard on it.
        // The session-epoch pre-flight turns a store evicted while the Data
        // Manager sat open into the same reconciliation every other path
        // produces (the list clears, the toast names the cause) instead of
        // a per-file "not found in session" error. A failed epoch READ does
        // not block the call: `check` swallows it.
        { model with Busy = true; Error = None },
        Cmd.OfRemoting.call
            (fun name -> SessionEpoch.preflight (fileApi.GetFileContent name))
            fileName
            FileContentLoaded
            (fun ex -> ApiError ex.Message)

    | FileContentLoaded(Ok upload) ->
        {
            model with
                Busy = false
                Wizard = Some(Wizard.init (heldOf upload.filename upload.contents))
        },
        Cmd.none

    | FileContentLoaded(Error e) ->
        {
            model with
                Busy = false
                Error = Some e
        },
        Cmd.none

    | WizardMsg wizardMsg -> updateWizard displays wizardMsg model

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
                Error = Some(model.Messages.ReprocessFailed msg)
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
                Error = Some(model.Messages.ResetFailed msg)
        },
        Cmd.none

    | ApiError errorMsg ->
        {
            model with
                Busy = false
                Error = Some errorMsg
        },
        Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

// ─── View ─────────────────────────────────────────────────────────


let private processedDataSection (displays: DataTypeDisplay list) (entries: ProcessedFileEntry list) =
    // Grouped by data type, rendered through the display's
    // own step (typed envelope or legacy `Info`); see `DataTypeDisplay.render`.
    let grouped =
        entries
        |> List.filter DataTypeDisplay.hasSummary
        |> List.choose (fun e ->
            displays
            |> List.tryFind (fun d -> d.Info.Id = e.DataType)
            |> Option.map (fun d -> d, e))
        |> List.groupBy (fun (d, _) -> d.Info.Id)

    Html.div [
        prop.className "space-y-4"
        prop.children [
            for (_, items) in grouped do
                let display = items |> List.head |> fst
                let groupEntries = items |> List.map snd
                Typography.subheading display.Info.DisplayName
                DataTypeDisplay.render display groupEntries
        ]
    ]

/// Per-file ingestion-status badge — parity with the
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

        // Rides the same mount as the ingestion subscription;
        // see `FileManagerUI`'s twin.
        let disposeReset = SessionEpoch.subscribe (fun _ -> dispatch SessionStoreCleared)

        FsReact.createDisposable (fun () ->
            dispose ()
            disposeReset ()))

    Html.none

/// Status-filter dropdown over the file list.
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
            Html.span [
                prop.className ("text-sm " + Tokens.Text.secondary)
                prop.text msgs.FilterByIndexStatus
            ]
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

/// The file-name cell: the name, and a "converted" badge (with the
/// conversion's provenance as its tooltip) on a produced file.
let private fileNameCell (msgs: MappingDataManagerMessages) (model: Model) (f: UploadedFileInfo) =
    Html.div [
        Html.span [ prop.text f.FileName ]
        match model.Records |> List.tryFind (fun r -> r.ProducedFile = f.FileName) with
        | Some r ->
            let steps =
                if r.RemediationSteps.IsEmpty then
                    msgs.NoRemediationLabel
                else
                    String.concat "; " r.RemediationSteps

            Html.span [
                prop.className
                    "ml-2 inline-block px-1.5 py-0.5 rounded border border-blue-200 bg-blue-50 text-blue-700 text-xs"
                prop.title (msgs.ConvertedFromTooltip r.SourceFile steps)
                prop.text msgs.ConvertedBadge
            ]
        | None -> ()
    ]

/// The search-index cell: the ingestion badge, and a one-click re-ingest
/// on a retryable status.
let private ingestionCell (msgs: MappingDataManagerMessages) (model: Model) dispatch (f: UploadedFileInfo) =
    let status = model.IngestionStatus |> Map.tryFind f.FileName

    Html.div [
        prop.className "flex items-center gap-2"
        prop.children [
            match status with
            | Some s -> ingestionBadge msgs s
            | None -> Html.none
            if FileIngestionStatus.isRetryable status then
                Html.button [
                    prop.className "text-xs text-blue-600 hover:text-blue-800 hover:underline"
                    prop.title msgs.RetryIngestionTooltip
                    prop.text msgs.RetryButton
                    prop.onClick (fun _ -> dispatch (RetryIngestionMsg f.FileName))
                ]
        ]
    ]

/// The row actions: New Mapping, Reprocess, Delete (confirmed).
let private fileActionsCell (msgs: MappingDataManagerMessages) dispatch (f: UploadedFileInfo) =
    Html.div [
        prop.className "flex items-center gap-3"
        prop.children [
            Html.button [
                prop.className "text-sm text-brand hover:underline"
                prop.title msgs.NewMappingTooltip
                prop.text msgs.NewMappingButton
                prop.onClick (fun _ -> dispatch (StartMapping f.FileName))
            ]
            Html.button [
                prop.className "text-sm text-blue-600 hover:text-blue-800 hover:underline"
                prop.title msgs.ReprocessTooltip
                prop.text msgs.ReprocessButton
                prop.onClick (fun _ -> dispatch (ReprocessFile f.FileName))
            ]
            Html.button [
                prop.className "text-sm text-red-600 hover:text-red-800 hover:underline"
                prop.text msgs.DeleteButton
                prop.onClick (fun _ ->
                    if Browser.Dom.window.confirm (msgs.DeleteFileConfirm f.FileName) then
                        dispatch (DeleteFile f.FileName))
            ]
        ]
    ]

let private filesTable
    (msgs: MappingDataManagerMessages)
    (displays: DataTypeDisplay list)
    (model: Model)
    dispatch
    (files: UploadedFileInfo list)
    =
    // An upload that has produced no data object — neither a
    // natively-recognised entry of its own nor any mapped conversion derived
    // from it — is flagged so the user maps it via New Mapping.
    let needsMapping (f: UploadedFileInfo) =
        let hasOwnObject =
            model.ProcessedData
            |> List.exists (fun e -> e.FileName = f.FileName && DataTypeDisplay.hasSummary e)

        let hasDerived = model.Records |> List.exists (fun r -> r.SourceFile = f.FileName)
        not (hasOwnObject || hasDerived)

    let column header cell : Data.Column<UploadedFileInfo> = {
        Header = header
        Align = Data.Left
        Cell = cell
    }

    let text (render: UploadedFileInfo -> string) =
        fun f -> Html.span [ prop.text (render f) ]

    Data.table
        [
            column msgs.ColumnDataType (fun f ->
                Html.span [
                    prop.className [
                        if needsMapping f then
                            "px-1 rounded bg-pink-50"
                    ]
                    prop.text (dataTypeLabel msgs displays f.DataType)
                ])
            column msgs.ColumnFileName (fileNameCell msgs model)
            column msgs.ColumnRows (text (fun f -> string f.RowCount))
            column msgs.ColumnSize (text (fun f -> formatSize msgs f.SizeBytes))
            // The search-index column only when RAG is composed (the
            // snapshot carries status).
            if not (Map.isEmpty model.IngestionStatus) then
                column msgs.ColumnSearchIndex (ingestionCell msgs model dispatch)
            column "" (fileActionsCell msgs dispatch)
        ]
        files


let private filesView (msgs: MappingDataManagerMessages) (displays: DataTypeDisplay list) (model: Model) dispatch =
    // Show genuine uploads only — a confirmed mapping uploads its rewritten
    // CSV as a produced file (`X__Type.csv`); that belongs in the data-object
    // section beneath, not as another row here. So the file count tracks
    // uploads, while the data-object count tracks mappings.
    let producedNames = model.Records |> List.map _.ProducedFile |> Set.ofList

    let rawFiles =
        model.UploadedFiles
        |> List.filter (fun f -> not (producedNames.Contains f.FileName))

    // Client-side status filter over the already-fetched status
    // set. `AllFiles` (and the no-RAG case) passes everything through.
    let displayFiles =
        rawFiles
        |> List.filter (fun f ->
            FileIngestionStatus.matchesFilter model.StatusFilter (model.IngestionStatus |> Map.tryFind f.FileName))

    match rawFiles with
    | [] -> Html.p [ prop.className Tokens.Text.muted; prop.text msgs.NoFilesImportedYet ]
    | _ ->
        Html.div [
            prop.className "space-y-4"
            prop.children [
                if not (Map.isEmpty model.IngestionStatus) then
                    statusFilterControl msgs model dispatch

                if displayFiles.IsEmpty then
                    Html.p [ prop.className Tokens.Text.muted; prop.text msgs.NoFilesMatchFilter ]

                filesTable msgs displays model dispatch displayFiles

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
                            prop.className ("text-sm " + Tokens.Text.secondary)
                            prop.text msgs.ResetScopeNote
                        ]
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

// ─── Module body panes ─────────────────────────────────────────
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
                        prop.className ("text-base " + Tokens.Text.muted)
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
            | Some w -> Wizard.view msgs displays model.AllowedTypeIds w (WizardMsg >> dispatch)
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

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `create` registers the module under it.
let moduleId = "_sdk.MappingDataManager"

/// Create the mapping-aware Data Manager as an `ErasedModule`. `displays`
/// supplies the registered data types (and their schemas) the wizard maps
/// into — same list the built-in `FileManagerUI` receives. The
/// `DataManagerConfig` overrides the name / icon / sidebar group.
let create (displays: DataTypeDisplay list) (config: DataManagerConfig) : ErasedModule =
    let name = config.Label |> Option.map _.Name |> Option.defaultValue "Import & Map"

    let icon =
        config.Label
        |> Option.map _.Icon
        |> Option.defaultValue ToolUp.Platform.Icons.upload

    let group = config.Group |> Option.defaultValue "Data Management"

    ToolUp.Platform.ClientModule.create {
        Init = fun () -> init ToolUp.Platform.ClientModuleContext.empty
        Update = update displays
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withContextInit init
    |> ToolUp.Platform.ClientModule.withId moduleId
    |> ToolUp.Platform.ClientModule.withView (fun model ctx -> PageContent.SplitPanel(view displays model ctx.Dispatch))
    |> ToolUp.Platform.ClientModule.withProcessedData _.ProcessedData
    |> ToolUp.Platform.ClientModule.withGroup group
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register
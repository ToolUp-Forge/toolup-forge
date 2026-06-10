// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ContentAuthoring.ContentTypeBridge

open System
open ToolUp.Platform.Narrative
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.FormValidator
open ToolUp.PublicRendering

// ─── Phase 89 — content type = FormSchema, content entry = Submission ─
//
// The elegant equivalence at the heart of the CMS: a **content type is a
// `FormSchema`** (fields, types, validators), a **content entry is a
// `Submission`** (a `Map<fieldKey, FieldValue>`), and publishing an
// entry is **projecting that submission to a `PublicPage`** whose body is
// a `NarrativeDocument` assembled from the entry's fields. ToolUp.Forms
// is consumed unforked — validation (required-field + type + rule
// enforcement, field-level errors) comes straight from
// `FormValidator.validate`; this bridge only adds the field → narrative
// projection.
//
// A `ContentTypeMapping` declares which field is the title / slug /
// description and which fields (in order) become the page body, so one
// generic projector serves any content type a deployment defines.

/// Why a submission could not be projected to a page.
type ContentTypeBridgeError =
    /// The submission was authored against a different schema id.
    | SchemaMismatch of expectedFormId: string * submissionFormId: string
    /// The Forms validation engine rejected the entry. Carries the
    /// per-field errors verbatim (the same `FieldError list` a form
    /// submission would surface).
    | EntryValidationFailed of FieldError list
    /// The mapping's title field is absent from the submission.
    | MissingTitle of fieldKey: string

/// Declares how a content type's `FormSchema` fields map onto a
/// `PublicPage`. `TitleField` is mandatory; the rest are optional /
/// ordered. Immutable (GP 5) — build via `ContentTypeMapping.create`
/// and the `with*` helpers.
type ContentTypeMapping = {
    /// Field key whose value becomes the page `Title`.
    TitleField: string
    /// Field key whose value becomes the slug. `None` derives a slug
    /// from the title.
    SlugField: string option
    /// Field key whose value becomes the page `Description` / subtitle.
    /// `None` leaves the description empty.
    DescriptionField: string option
    /// Field keys, in order, projected into the narrative body — one
    /// `NarrativeSection` per field (heading = the field's `DisplayName`).
    BodyFields: string list
    /// Layout the rendered page uses.
    Layout: LayoutName
    /// Collection the page belongs to (`"case-studies"`, `"services"`…).
    Collection: string option
}

module ContentTypeMapping =
    let create (titleField: string) (layout: LayoutName) : ContentTypeMapping = {
        TitleField = titleField
        SlugField = None
        DescriptionField = None
        BodyFields = []
        Layout = layout
        Collection = None
    }

    let withSlugField (field: string) (m: ContentTypeMapping) = { m with SlugField = Some field }
    let withDescriptionField (field: string) (m: ContentTypeMapping) = { m with DescriptionField = Some field }
    let withBodyFields (fields: string list) (m: ContentTypeMapping) = { m with BodyFields = fields }
    let withCollection (collection: string) (m: ContentTypeMapping) = { m with Collection = Some collection }

/// Map a submission's workflow `State` to a page `PublishStatus` — the
/// lifecycle equivalence (Phase 89). An in-progress `Draft` submission
/// projects to a `Draft` page; a plain `Submitted` one to `Published`; a
/// workflow `Custom` state maps by name (`"published"` / `"archived"` /
/// review-ish states → draft). Scheduled-at timing is not carried on the
/// submission, so a `"scheduled"` workflow state projects to `Draft`
/// until the lifecycle layer stamps the publish time.
let private statusOf (state: SubmissionState) : PublishStatus =
    match state with
    | SubmissionState.Draft -> PublishStatus.Draft
    | SubmissionState.Submitted -> Published
    | SubmissionState.Custom s ->
        match s.Trim().ToLowerInvariant() with
        | "published" -> Published
        | "archived" -> Archived
        | _ -> PublishStatus.Draft

/// Render a `FieldValue` to its plain-text projection (used for title /
/// slug / description and scalar body fields). Deterministic — invariant
/// formatting, no culture-sensitive ops.
let private valueText (v: FieldValue) : string =
    match v with
    | TextValue s -> s
    | NumberValue n -> n.ToString(Globalization.CultureInfo.InvariantCulture)
    | DateValue d -> d.ToString("yyyy-MM-dd")
    | DateTimeValue dt -> dt.ToString("u")
    | BoolValue b -> if b then "Yes" else "No"
    | ChoiceValue c -> c
    | MultiChoiceValue cs -> String.concat ", " cs
    | FileValue f -> f
    | EntityRefValue e -> e
    | NestedSubmissionValue s -> s

/// Lowercase + collapse non-alphanumeric runs to `-` + trim edge dashes.
/// Deterministic, Fable-safe (no `Regex`).
let private slugify (s: string) : string =
    let lower = s.ToLowerInvariant()
    let sb = Text.StringBuilder()
    let mutable lastDash = false

    for c in lower do
        if Char.IsLetterOrDigit c then
            sb.Append c |> ignore
            lastDash <- false
        elif not lastDash && sb.Length > 0 then
            sb.Append '-' |> ignore
            lastDash <- true

    let r = sb.ToString()
    if r.EndsWith "-" then r.Substring(0, r.Length - 1) else r

/// Project one mapped body field into a `NarrativeSection`. Text values
/// split on blank lines into paragraphs; multi-choice values render as a
/// bullet list; scalar values render as a single paragraph. Absent
/// fields are skipped.
let private bodySection (schema: FormSchema) (values: Map<string, FieldValue>) (key: string) : NarrativeSection option =
    match Map.tryFind key values with
    | None -> None
    | Some value ->
        let heading =
            schema.Fields
            |> List.tryFind (fun f -> f.Key = key)
            |> Option.map (fun f -> f.DisplayName)
            |> Option.defaultValue key

        let elements =
            match value with
            | TextValue s ->
                s.Replace("\r\n", "\n").Split([| "\n\n" |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.toList
                |> List.map (fun para -> Narrative.paragraph [ Narrative.text (para.Trim()) ])
            | MultiChoiceValue cs -> [ Narrative.bullets (cs |> List.map (fun c -> [ Narrative.text c ])) ]
            | other -> [ Narrative.paragraph [ Narrative.text (valueText other) ] ]

        Some {
            Id = slugify key
            Heading = heading
            Subheading = None
            Elements = elements
        }

/// Project a validated content entry (`Submission`) to a `PublicPage`.
///
/// Steps: (1) confirm the submission targets this schema; (2) validate
/// the field values through the Forms engine (`FormValidator.validate`)
/// — an invalid entry returns `EntryValidationFailed` with field-level
/// errors, exactly as a form submission would; (3) project the mapped
/// fields into a `NarrativeDocument` and wrap it in a `PublicPage`.
///
/// `validators` is the deployment's `CustomValidatorRegistry` (the same
/// one `FormsServerApp` holds); pass `Map.empty` when the schema uses no
/// named custom validators.
let project
    (validators: CustomValidatorRegistry)
    (schema: FormSchema)
    (mapping: ContentTypeMapping)
    (submission: Submission)
    : Result<PublicPage, ContentTypeBridgeError> =

    if submission.FormId <> schema.Id then
        Error(SchemaMismatch(schema.Id, submission.FormId))
    else
        match validate validators schema submission.Values with
        | Error errs -> Error(EntryValidationFailed errs)
        | Ok() ->
            match Map.tryFind mapping.TitleField submission.Values with
            | None -> Error(MissingTitle mapping.TitleField)
            | Some titleValue ->
                let title = valueText titleValue

                let slug =
                    mapping.SlugField
                    |> Option.bind (fun k -> Map.tryFind k submission.Values)
                    |> Option.map valueText
                    |> Option.defaultWith (fun () -> slugify title)

                let slug = if slug = "" then slugify title else slug

                let description =
                    mapping.DescriptionField
                    |> Option.bind (fun k -> Map.tryFind k submission.Values)
                    |> Option.map valueText
                    |> Option.defaultValue ""

                let sections =
                    mapping.BodyFields |> List.choose (bodySection schema submission.Values)

                let document = {
                    Title = title
                    Subtitle = if description = "" then None else Some description
                    Sections = sections
                    Provenance = None
                    Lang = None
                    CanonicalUrl = None
                }

                Ok {
                    Slug = Slug slug
                    Title = title
                    Description = description
                    Body = Narrative document
                    Layout = mapping.Layout
                    Frontmatter = Map.empty
                    PublishedAt = Some submission.SubmittedAt
                    Collection = mapping.Collection
                    Status = statusOf submission.State
                }
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ContentAuthoringTests

open System
open Expecto
open ToolUp.Platform.Narrative
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.PublicRendering
open ToolUp.ContentAuthoring
open ToolUp.ContentAuthoring.ContentTypeBridge

// ─── Phase 89 — content-type bridge (FormSchema/Submission → PublicPage)
//
// The CMS cornerstone: a content type IS a `FormSchema`, a content entry
// IS a `Submission`, and `ContentTypeBridge.project` validates the entry
// through the Forms engine (unforked) and projects it to a `PublicPage`
// with a `Narrative` body. These tests cover the projection, the
// validation-rejection path (field-level errors), schema mismatch, and
// slug derivation / override.

let private field key display required = {
    Key = key
    DisplayName = display
    Description = None
    Kind = TextField None
    Required = required
    Validators = []
}

/// A "Case Study" content type, expressed as a `FormSchema`.
let private caseStudySchema: FormSchema = {
    Id = "case-study"
    Type = "FormSchema"
    Version = 1
    DisplayName = "Case Study"
    Description = None
    Fields = [
        field "title" "Title" true
        field "summary" "Summary" true
        field "challenge" "The Challenge" false
        field "outcome" "The Outcome" false
    ]
    Visibility = Internal
}

let private submissionOf (values: Map<string, FieldValue>) : Submission = {
    Id = "s1"
    Type = "Submission"
    Version = 1
    FormId = "case-study"
    SchemaVersion = 1
    SubmittedAt = DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero)
    Author = AuthenticatedUser "u1"
    Values = values
    State = Submitted
    WorkflowId = None
}

let private mapping =
    ContentTypeMapping.create "title" (LayoutName "page")
    |> ContentTypeMapping.withDescriptionField "summary"
    |> ContentTypeMapping.withBodyFields [ "challenge"; "outcome" ]
    |> ContentTypeMapping.withCollection "case-studies"

[<Tests>]
let tests =
    testList "ContentAuthoring (Phase 89)" [
        test "a valid entry projects to a published PublicPage" {
            let values =
                Map.ofList [
                    "title", TextValue "Acme Rebrand"
                    "summary", TextValue "How we doubled conversions."
                    "challenge", TextValue "The old brand was tired."
                    "outcome", TextValue "Conversions up 112%."
                ]

            match ContentTypeBridge.project Map.empty caseStudySchema mapping (submissionOf values) with
            | Error e -> failtestf "expected Ok, got %A" e
            | Ok page ->
                Expect.equal page.Title "Acme Rebrand" "title from title field"
                Expect.equal page.Slug (Slug "acme-rebrand") "slug derived from title"
                Expect.equal page.Description "How we doubled conversions." "description from summary field"
                Expect.equal page.Collection (Some "case-studies") "collection"
                Expect.equal page.Layout (LayoutName "page") "layout"

                Expect.equal
                    page.PublishedAt
                    (Some(DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero)))
                    "published at = submitted at"

                match page.Body with
                | Narrative doc ->
                    Expect.equal doc.Title "Acme Rebrand" "doc title"
                    Expect.equal doc.Subtitle (Some "How we doubled conversions.") "doc subtitle = description"
                    Expect.equal (List.length doc.Sections) 2 "two body sections (challenge + outcome)"
                    Expect.equal doc.Sections.[0].Heading "The Challenge" "first section heading = field DisplayName"
                    Expect.equal doc.Sections.[1].Heading "The Outcome" "second section heading"
                | other -> failtestf "expected Narrative body, got %A" other
        }

        test "an invalid entry is rejected by the Forms engine with field-level errors" {
            // `summary` is required but omitted.
            let values = Map.ofList [ "title", TextValue "Incomplete" ]

            match ContentTypeBridge.project Map.empty caseStudySchema mapping (submissionOf values) with
            | Error(ContentTypeBridge.EntryValidationFailed errs) ->
                Expect.isTrue
                    (errs |> List.exists (fun e -> e.FieldKey = "summary" && e.Code = "required"))
                    "required-field error for summary"
            | other -> failtestf "expected EntryValidationFailed, got %A" other
        }

        test "a submission for a different schema is a SchemaMismatch" {
            let sub = {
                submissionOf (Map.ofList [ "title", TextValue "X"; "summary", TextValue "Y" ]) with
                    FormId = "some-other-type"
            }

            match ContentTypeBridge.project Map.empty caseStudySchema mapping sub with
            | Error(ContentTypeBridge.SchemaMismatch("case-study", "some-other-type")) -> ()
            | other -> failtestf "expected SchemaMismatch, got %A" other
        }

        test "an explicit slug field overrides the title-derived slug" {
            let schemaWithSlug = {
                caseStudySchema with
                    Fields = caseStudySchema.Fields @ [ field "slug" "Slug" false ]
            }

            let mappingWithSlug = mapping |> ContentTypeMapping.withSlugField "slug"

            let values =
                Map.ofList [
                    "title", TextValue "A Long Marketing Title"
                    "summary", TextValue "s"
                    "slug", TextValue "custom-slug"
                ]

            match ContentTypeBridge.project Map.empty schemaWithSlug mappingWithSlug (submissionOf values) with
            | Ok page -> Expect.equal page.Slug (Slug "custom-slug") "explicit slug wins"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        test "a multi-choice body field renders as a bullet list" {
            let schemaWithTags = {
                caseStudySchema with
                    Fields =
                        caseStudySchema.Fields
                        @ [
                            {
                                Key = "tags"
                                DisplayName = "Tags"
                                Description = None
                                Kind = MultiChoiceField [ "brand"; "web"; "seo" ]
                                Required = false
                                Validators = []
                            }
                        ]
            }

            let mappingWithTags = mapping |> ContentTypeMapping.withBodyFields [ "tags" ]

            let values =
                Map.ofList [
                    "title", TextValue "T"
                    "summary", TextValue "s"
                    "tags", MultiChoiceValue [ "brand"; "web" ]
                ]

            match ContentTypeBridge.project Map.empty schemaWithTags mappingWithTags (submissionOf values) with
            | Ok page ->
                match page.Body with
                | Narrative doc ->
                    match doc.Sections.[0].Elements with
                    | [ BulletList items ] -> Expect.equal (List.length items) 2 "two bullets"
                    | other -> failtestf "expected a single BulletList, got %A" other
                | other -> failtestf "expected Narrative body, got %A" other
            | Error e -> failtestf "expected Ok, got %A" e
        }
    ]
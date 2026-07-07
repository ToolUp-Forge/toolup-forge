// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ContentAuthoringTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityStore
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Secrets
open ToolUp.Platform.ShareTokenStore
open ToolUp.Platform.Tests.Contracts
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

// ─── Lifecycle helpers ────────────────────────────────────────────────

let private now = DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero)

let private pageWith (status: PublishStatus) (slug: string) : PublicPage = {
    Slug = Slug slug
    Title = slug
    Description = ""
    Body = Html ""
    Layout = LayoutName "page"
    Frontmatter = Map.empty
    PublishedAt = None
    Collection = None
    Status = status
    Audience = PageAudience.Public
}

type private NullLogger() =
    interface ILogger with
        member _.Debug(_) = ()
        member _.Info(_) = ()
        member _.Warn(_) = ()
        member _.Error(_, _) = ()

type private InMemorySecretStore() =
    let d =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(c, n) = async {
            match d.TryGetValue((c, n)) with
            | true, v -> return Some v
            | _ -> return None
        }

        member _.SetSecret(c, n, v) = async {
            d[(c, n)] <- v
            return Ok()
        }

        member _.DeleteSecret(c, n) = async {
            d.TryRemove((c, n)) |> ignore
            return Ok()
        }

        member _.ListKeys(_) = async { return [] }

let private mkPageStore () : IEntityStore =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-cms-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    registry.Register<PublicPageEntity>(PublicPageEntity.registration)
    BlobEntityStore(dos, blob, registry, None) :> IEntityStore

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
                    Expect.equal doc.Sections[0].Heading "The Challenge" "first section heading = field DisplayName"
                    Expect.equal doc.Sections[1].Heading "The Outcome" "second section heading"
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
                    match doc.Sections[0].Elements with
                    | [ BulletList items ] -> Expect.equal (List.length items) 2 "two bullets"
                    | other -> failtestf "expected a single BulletList, got %A" other
                | other -> failtestf "expected Narrative body, got %A" other
            | Error e -> failtestf "expected Ok, got %A" e
        }

        // ─── Lifecycle (Phase 89) ─────────────────────────────────────
        test "isPubliclyVisible gates serving by publish status" {
            Expect.isTrue (PublicPage.isPubliclyVisible now (pageWith Published "p")) "published is visible"
            Expect.isFalse (PublicPage.isPubliclyVisible now (pageWith PublishStatus.Draft "p")) "draft is hidden"
            Expect.isFalse (PublicPage.isPubliclyVisible now (pageWith Archived "p")) "archived is hidden"

            Expect.isFalse
                (PublicPage.isPubliclyVisible now (pageWith (Scheduled(now.AddHours 1.0)) "p"))
                "future-scheduled is hidden"

            Expect.isTrue
                (PublicPage.isPubliclyVisible now (pageWith (Scheduled(now.AddHours(-1.0))) "p"))
                "past-scheduled is visible"
        }

        test "promoteIfDue publishes a due scheduled page and stamps PublishedAt" {
            let promoted =
                ContentLifecycle.promoteIfDue now (pageWith (Scheduled(now.AddMinutes(-1.0))) "p")

            Expect.equal promoted.Status Published "due page promoted to Published"
            Expect.equal promoted.PublishedAt (Some now) "PublishedAt stamped to now"

            let future = pageWith (Scheduled(now.AddHours 1.0)) "f"

            Expect.equal
                (ContentLifecycle.promoteIfDue now future).Status
                (Scheduled(now.AddHours 1.0))
                "future page unchanged"
        }

        test "statusForState maps workflow states to publish status" {
            Expect.equal (ContentLifecycle.statusForState "published") Published "published"
            Expect.equal (ContentLifecycle.statusForState "archived") Archived "archived"
            Expect.equal (ContentLifecycle.statusForState "in-review") PublishStatus.Draft "in-review → draft"
            Expect.equal (ContentLifecycle.statusForState "draft") PublishStatus.Draft "draft"
        }

        test "editorialWorkflow runs draft → in-review → published with a guarded approval" {
            let wf = ContentLifecycle.editorialWorkflow
            Expect.equal wf.InitialState "draft" "starts in draft"

            Expect.isTrue
                (wf.Transitions |> List.exists (fun t -> t.From = "draft" && t.To = "in-review"))
                "draft → in-review"

            Expect.isTrue
                (wf.Transitions
                 |> List.exists (fun t ->
                     t.From = "in-review"
                     && t.To = "published"
                     && t.Guard = Some ContentLifecycle.approveGuard))
                "in-review → published, guarded by the approval predicate"
        }

        testCaseAsync "runScheduledPublishSweep promotes only due scheduled pages"
        <| async {
            let store = mkPageStore ()
            let scope = PublicPageEntity.PublicScope

            let! _ = store.Save(scope, PublicPageEntity.fromPage (pageWith (Scheduled(now.AddMinutes(-5.0))) "due"))
            let! _ = store.Save(scope, PublicPageEntity.fromPage (pageWith (Scheduled(now.AddHours 5.0)) "future"))
            let! _ = store.Save(scope, PublicPageEntity.fromPage (pageWith PublishStatus.Draft "draftpage"))

            let! promoted = ContentLifecycle.runScheduledPublishSweep store now
            Expect.equal promoted [ "due" ] "only the due scheduled page is promoted"

            let! due = store.Get<PublicPageEntity>(scope, PublicPageEntity.EntityTypeName, "due")

            match due with
            | Ok e -> Expect.equal e.Page.Status Published "the due page is now Published in the store"
            | Error err -> failtestf "due page missing: %A" err
        }

        // ─── Versioning (Phase 89) ────────────────────────────────────
        testCaseAsync "page revisions list, read, and restore append-only"
        <| async {
            let store = mkPageStore ()
            let scope = PublicPageEntity.PublicScope

            // Two edits of the same slug → two versions.
            let v1 = {
                pageWith Published "about" with
                    Title = "About v1"
            }

            let v2 = {
                pageWith Published "about" with
                    Title = "About v2"
            }

            let! _ = store.Save(scope, PublicPageEntity.fromPage v1)
            let! _ = store.Save(scope, PublicPageEntity.fromPage v2)

            let! revisions = PublicPageRevisions.list store "about"
            Expect.equal (List.length revisions) 2 "two revisions listed"
            Expect.equal revisions[0].Version 2 "newest version first"

            // Read the prior revision.
            let! rev1 = PublicPageRevisions.get store "about" 1

            match rev1 with
            | Ok page -> Expect.equal page.Title "About v1" "revision 1 has the original title"
            | Error e -> failtestf "get rev1 failed: %A" e

            // Restore v1 → appends a new current version with v1 content.
            let! restored = PublicPageRevisions.restore store "about" 1
            Expect.isOk restored "restore ok"

            let! revisionsAfter = PublicPageRevisions.list store "about"
            Expect.equal (List.length revisionsAfter) 3 "restore appended a third version (history preserved)"

            let! currentPage = PublicPageRevisions.current store "about"

            match currentPage with
            | Ok page -> Expect.equal page.Title "About v1" "current is now the restored v1 content"
            | Error e -> failtestf "current failed: %A" e
        }

        // ─── Shareable preview links (Phase 89) ───────────────────────
        testCaseAsync "a preview token grants PublicPage access for its slug"
        <| async {
            let blob = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let secrets = InMemorySecretStore() :> ISecretStore

            let store =
                BlobShareTokenStore(blob, secrets, None, NullLogger()) :> IShareTokenStore

            match! ContentPreview.issuePreviewToken store "team-x" "about" "u1" (TimeSpan.FromHours 1.0) with
            | Error e -> failtestf "issue failed: %A" e
            | Ok url ->
                Expect.stringContains url "/preview?token=" "preview url shape"
                let token = Uri.UnescapeDataString(url.Substring(url.IndexOf "token=" + 6))

                match! store.Validate token with
                | Ok claim ->
                    Expect.equal claim.ResourceKind "PublicPage" "token grants PublicPage access"
                    Expect.equal claim.ResourceId "about" "for the 'about' slug"
                | Error e -> failtestf "validate failed: %A" e
        }

        // ─── Authoring admin API (Phase 89) ───────────────────────────
        testCaseAsync "content admin API saves, lists, and transitions pages"
        <| async {
            let store = mkPageStore ()
            let api = ContentAdminApiImpl.create store

            let! _ = api.SavePage(pageWith PublishStatus.Draft "intro")
            let! _ = api.SavePage(pageWith Published "home")

            let! pages = api.ListPages()
            Expect.equal (List.length pages) 2 "two pages listed"

            Expect.isTrue
                (pages |> List.exists (fun p -> p.Slug = "intro" && p.Status = "draft"))
                "intro listed as draft"

            // Transition intro: draft → published.
            let! transition = api.SetStatus("intro", Published)
            Expect.isOk transition "set-status ok"

            let! reread = api.GetPage "intro"

            match reread with
            | Some p -> Expect.equal p.Status Published "intro is now Published"
            | None -> failtest "intro page missing after transition"

            // Editing intro again creates a second revision.
            let! _ =
                api.SavePage(
                    {
                        pageWith Published "intro" with
                            Title = "Intro v2"
                    }
                )

            let! revisions = api.ListRevisions "intro"
            Expect.isGreaterThanOrEqual (List.length revisions) 2 "intro has at least two revisions"
        }
    ]
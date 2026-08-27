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

// ─── Phase 198 — draft preview-link minting ───────────────────────────

let private mkShareTokenStore () : IShareTokenStore =
    let blob = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
    let secrets = InMemorySecretStore() :> ISecretStore
    BlobShareTokenStore(blob, secrets, None, NullLogger()) :> IShareTokenStore

let private previewScope = "team-x"
let private previewBaseUrl = "https://cms.example.test"

/// An editor holding the editorial approval role — the caller the mint
/// surface exists for.
let private approver = {
    AccessContext.unrestricted (TeamMember("editor-1", previewScope)) with
        ModulePermissions = Map[ContentLifecycle.approveGuard, [ ModulePermission.Admin ]]
}

/// An authenticated team member whose deployment HAS configured RBAC but
/// who does not hold the approval role.
let private nonApprover = {
    AccessContext.unrestricted (TeamMember("viewer-1", previewScope)) with
        ModulePermissions = Map["viewer", [ ModulePermission.Read ]]
}

/// A share-token bearer — a resolved, non-anonymous subject that must
/// still never be able to mint a further preview link.
let private claimBearer =
    AccessContext.unrestricted (
        ClaimBearer {
            TokenId = "tok-1"
            ScopeId = previewScope
            ResourceKind = ContentPreview.resourceKind
            ResourceId = "roadmap"
            AttributedHandle = None
            IssuedBy = "editor-1"
            IssuedAt = now
            ExpiresAt = now.AddHours 1.0
            UseLimit = None
            UsedCount = 0
            Revoked = false
            RateLimit = None
        }
    )

/// Fixtures that need the Giraffe / ASP.NET surface. Scoped to a nested
/// module so `Giraffe.ViewEngine`'s element functions (`body`, `summary`,
/// `title`, …) do not shadow anything in the Phase 89 tests above.
module private PreviewRoute =
    open System.Threading.Tasks
    open Microsoft.AspNetCore.Http
    open Microsoft.Extensions.DependencyInjection
    open Giraffe
    open Giraffe.ViewEngine

    let layouts: Map<LayoutName, PublicPage -> XmlNode> =
        Map[(LayoutName "page", (fun (p: PublicPage) -> html [] [ body [] [ str p.Title ] ]))]

    let private mkApi (pages: PublicPage list) : IPublicContentApi =
        let bySlug = pages |> List.map (fun p -> Slug.value p.Slug, p) |> Map.ofList

        { new IPublicContentApi with
            member _.GetPage slug = async { return Map.tryFind slug bySlug }
            member _.ListPages _ = async { return pages }

            member this.ListPagesPublic(now, prefix) =
                PublicContentApi.defaultListPagesPublic this now prefix

            member _.GetCollection _ = async { return [] }
            member _.GetPageInContext(slug, _ctx) = async { return Map.tryFind slug bySlug }
        }

    /// Drive the SHIPPED Phase 89 `/preview` route once, against the
    /// deployment's real `IShareTokenStore` (or none). Returns the status
    /// code the route set — `None` when the handler DECLINED, which in a
    /// live pipeline falls through to a 404 — plus the response body.
    let run (store: IShareTokenStore option) (pages: PublicPage list) (token: string) : int option * string =
        let services = ServiceCollection()

        store
        |> Option.iter (fun s -> services.AddSingleton<IShareTokenStore>(s) |> ignore)

        let ctx = DefaultHttpContext()
        ctx.RequestServices <- services.BuildServiceProvider()
        ctx.Request.Path <- PathString "/preview"
        ctx.Request.QueryString <- QueryString("?token=" + Uri.EscapeDataString token)
        ctx.Response.Body <- new MemoryStream()
        let finalFunc: HttpFunc = fun c -> Task.FromResult(Some c)

        let outcome =
            (ContentPreview.previewHandler (mkApi pages) layouts finalFunc ctx).GetAwaiter().GetResult()

        ctx.Response.Body.Seek(0L, SeekOrigin.Begin) |> ignore
        let body = (new StreamReader(ctx.Response.Body)).ReadToEnd()

        match outcome with
        | Some _ -> Some ctx.Response.StatusCode, body
        | None -> None, body

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

        // ─── Draft preview-link minting (Phase 198) ───────────────────
        //
        // The admin half of the Phase 89 preview surface. The load-
        // bearing property across these cases is that there is ONE
        // preview-token format: everything minted here is validated by
        // the shipped `/preview` route, unmodified.

        test "the preview mint guard IS the editorial approval guard" {
            // Minting a link that bypasses the publish-visibility filter
            // is the same editorial authority as approving the publish.
            // The two spellings live in different packages (PublicRendering
            // cannot reference the authoring companion that depends on
            // it), so this pins them together.
            Expect.equal
                ContentPreview.mintGuard
                ContentLifecycle.approveGuard
                "preview minting reuses the editorial approval guard, not a second gate"
        }

        testCaseAsync "an approver mints a link the existing /preview route validates and renders as a Draft"
        <| async {
            let store = mkShareTokenStore ()
            let draft = pageWith PublishStatus.Draft "roadmap"

            match!
                ContentPreview.mintPreviewLink
                    (Some store)
                    previewBaseUrl
                    approver
                    (MintPreviewLinkRequest.forSlug "roadmap")
            with
            | Error decline -> failtestf "expected a minted link, got %A" decline
            | Ok link ->
                Expect.stringStarts link.Path "/preview?token=" "site-relative path shape"
                Expect.equal link.Url (previewBaseUrl + link.Path) "absolute url is the base url + the path"
                Expect.equal link.Slug "roadmap" "link names the requested slug"
                Expect.equal link.IssuedBy "editor-1" "issuer is the authenticated caller, not a request field"

                Expect.isTrue (link.ExpiresAt > DateTimeOffset.UtcNow) "the link has not already expired on the way out"

                // Validation parity: the token is an ordinary
                // `IShareTokenStore` PublicPage claim — no second format.
                match! store.Validate link.Token with
                | Error e -> failtestf "the minted token failed the shipped validation path: %A" e
                | Ok claim ->
                    Expect.equal claim.ResourceKind ContentPreview.resourceKind "PublicPage claim"
                    Expect.equal claim.ResourceId "roadmap" "scoped to the requested slug"
                    Expect.equal claim.ScopeId previewScope "scope derived from the caller, not the request"

                // End to end: the shipped route renders the DRAFT.
                let status, body = PreviewRoute.run (Some store) [ draft ] link.Token
                Expect.equal status (Some 200) "the /preview route serves the minted link"
                Expect.stringContains body "roadmap" "the unpublished page's content is rendered"
        }

        testCaseAsync "an expired minted link no longer renders the draft"
        <| async {
            let store = mkShareTokenStore ()
            let draft = pageWith PublishStatus.Draft "roadmap"

            let request =
                MintPreviewLinkRequest.forSlug "roadmap"
                |> MintPreviewLinkRequest.withTtl (TimeSpan.FromMilliseconds 1.0)

            match! ContentPreview.mintPreviewLink (Some store) previewBaseUrl approver request with
            | Error decline -> failtestf "expected a minted link, got %A" decline
            | Ok link ->
                do! Async.Sleep 60

                let status, body = PreviewRoute.run (Some store) [ draft ] link.Token
                Expect.notEqual status (Some 200) "an expired link does not serve the page"
                Expect.isFalse (body.Contains "<body>") "no page body is rendered for an expired link"
        }

        testCaseAsync "a token scoped to another resource kind does not reach the preview route"
        <| async {
            let store = mkShareTokenStore ()
            let draft = pageWith PublishStatus.Draft "roadmap"

            // A perfectly valid share token — for a different surface.
            let! issued =
                store.Issue {
                    ScopeId = previewScope
                    ResourceKind = "forms.publishable"
                    ResourceId = "roadmap"
                    AttributedHandle = None
                    IssuedBy = "editor-1"
                    ExpiresAt = Some(DateTimeOffset.UtcNow.AddHours 1.0)
                    UseLimit = Some None
                    RateLimit = None
                }

            match issued with
            | Error e -> failtestf "issue failed: %A" e
            | Ok token ->
                let status, body = PreviewRoute.run (Some store) [ draft ] token.Token
                Expect.notEqual status (Some 200) "a wrong-scope token does not serve the page"
                Expect.isFalse (body.Contains "<body>") "no page body is rendered for a wrong-scope token"
        }

        testCaseAsync "with no IShareTokenStore registered the mint declines and the preview route is unchanged"
        <| async {
            let draft = pageWith PublishStatus.Draft "roadmap"

            // GP 13 — a deployment that never enables previews pays
            // nothing for this surface existing, and gets a typed answer
            // rather than a 500.
            match!
                ContentPreview.mintPreviewLink None previewBaseUrl approver (MintPreviewLinkRequest.forSlug "roadmap")
            with
            | Error PreviewLinkDecline.PreviewsNotEnabled -> ()
            | other -> failtestf "expected PreviewsNotEnabled, got %A" other

            // …and the Phase 89 route behaves exactly as before: it
            // declines, so the request 404s like any unknown path.
            let status, _ = PreviewRoute.run None [ draft ] "anything"
            Expect.equal status None "the route declines with no store registered, as pre-198"
        }

        testCaseAsync "a caller without the approval role is denied, and nothing is minted"
        <| async {
            let store = mkShareTokenStore ()
            let request = MintPreviewLinkRequest.forSlug "roadmap"

            for label, caller in
                [
                    "an authenticated non-approver", nonApprover
                    "an anonymous visitor", AccessContext.unrestricted (AnonymousSession "s1")
                    "a share-token bearer", claimBearer
                ] do
                match! ContentPreview.mintPreviewLink (Some store) previewBaseUrl caller request with
                | Error PreviewLinkDecline.Unauthorised -> ()
                | other -> failtestf "%s should be Unauthorised, got %A" label other

            // Default-deny is structural, not cosmetic: no claim was
            // written for any of the three refused callers.
            let! claims = store.ListByResource(previewScope, ContentPreview.resourceKind, "roadmap")
            Expect.isEmpty claims "a refused mint issues no token"
        }

        test "the mint gate itself denies anonymous and claim-bearer callers" {
            Expect.isTrue (ContentPreview.canMintPreviewLink approver) "an approver may mint"

            Expect.isFalse
                (ContentPreview.canMintPreviewLink (AccessContext.unrestricted (AnonymousSession "s1")))
                "an anonymous caller may never mint"

            Expect.isFalse
                (ContentPreview.canMintPreviewLink claimBearer)
                "a preview-link bearer may not mint a further link off its own authority"

            Expect.isFalse (ContentPreview.canMintPreviewLink nonApprover) "a configured non-approver may not mint"

            // GP 11 — a deployment that never configured RBAC is not
            // silently locked out of its own authoring surface.
            Expect.isTrue
                (ContentPreview.canMintPreviewLink (AccessContext.unrestricted (TeamMember("editor-1", previewScope))))
                "an authenticated editor in an unconfigured-RBAC deployment may mint"
        }

        testCaseAsync "an unmintable request is a typed InvalidRequest, never an exception"
        <| async {
            let store = mkShareTokenStore ()

            let cases = [
                "empty slug",
                {
                    MintPreviewLinkRequest.forSlug " " with
                        Ttl = TimeSpan.FromHours 1.0
                }
                "non-positive ttl",
                MintPreviewLinkRequest.forSlug "roadmap"
                |> MintPreviewLinkRequest.withTtl TimeSpan.Zero
                "ttl past the maximum",
                MintPreviewLinkRequest.forSlug "roadmap"
                |> MintPreviewLinkRequest.withTtl (MintPreviewLinkRequest.MaxTtl + TimeSpan.FromDays 1.0)
            ]

            for label, request in cases do
                match! ContentPreview.mintPreviewLink (Some store) previewBaseUrl approver request with
                | Error(PreviewLinkDecline.InvalidRequest _) -> ()
                | other -> failtestf "%s should be an InvalidRequest, got %A" label other
        }

        testCaseAsync "an attributed mint carries the recipient handle onto the token claim"
        <| async {
            let store = mkShareTokenStore ()

            let request =
                MintPreviewLinkRequest.forSlug "roadmap"
                |> MintPreviewLinkRequest.withAttribution "client-contact"

            match! ContentPreview.mintPreviewLink (Some store) previewBaseUrl approver request with
            | Error decline -> failtestf "expected a minted link, got %A" decline
            | Ok link ->
                match! store.Validate link.Token with
                | Error e -> failtestf "validate failed: %A" e
                | Ok claim ->
                    Expect.equal claim.AttributedHandle (Some "client-contact") "attribution rides the claim"
                    Expect.equal claim.TokenId link.TokenId "the link exposes the claim id for revocation"
        }
    ]
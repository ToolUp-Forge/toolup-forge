# CMS authoring — content types as Forms

The CMS is built by **reusing `ToolUp.Forms`**, not by adding a new
editor engine. The equivalence:

| CMS concept | Forms primitive |
|---|---|
| **Content type** (e.g. "Case Study") | a `FormSchema` (fields + types + validators) |
| **Content entry** | a `Submission` (`Map<fieldKey, FieldValue>`) |
| **Publishing an entry** | projecting the submission to a `PublicPage` |
| **Draft → review → publish lifecycle** (planned) | a `WorkflowDefinition` |

Validation, structured field types, and (once wired) the editorial
workflow state machine come free from Forms. `ToolUp.ContentAuthoring`
(Phase 89) adds only the **content-type bridge**: it validates an entry
through the Forms engine and projects it to a `ToolUp.PublicRendering`
`PublicPage` with a `Narrative` body.

`ToolUp.ContentAuthoring` is an opt-in companion that references both
`ToolUp.Forms` and `ToolUp.PublicRendering` — so a deployment using
either of those that *doesn't* author content pulls in nothing extra.

## Defining a content type

A content type is a `FormSchema`:

```fsharp
open ToolUp.Forms.FormSchema

let caseStudy: FormSchema = {
    Id = "case-study"; Type = "FormSchema"; Version = 1
    DisplayName = "Case Study"; Description = None
    Fields = [
        { Key = "title";     DisplayName = "Title";         Description = None; Kind = TextField None;                       Required = true;  Validators = [] }
        { Key = "summary";   DisplayName = "Summary";       Description = None; Kind = TextField None;                       Required = true;  Validators = [] }
        { Key = "challenge"; DisplayName = "The Challenge"; Description = None; Kind = TextField None;                       Required = false; Validators = [] }
        { Key = "tags";      DisplayName = "Tags";          Description = None; Kind = MultiChoiceField ["brand";"web";"seo"]; Required = false; Validators = [] }
    ]
    Visibility = Internal
}
```

Every Forms field kind is available — `TextField`, `NumberField`,
`DateField`, `ChoiceField`, `MultiChoiceField`, `EntityRefField`,
`NestedFormField`, … — and every Forms validation rule (`Regex`,
`NumberRange`, `LengthRange`, named `Custom`).

## Projecting an entry to a page

A content entry is a `Submission`. `ContentTypeBridge.project` validates
it through the Forms engine and, on success, projects it to a
`PublicPage`. A `ContentTypeMapping` says which field is the title /
slug / description and which fields become the body:

```fsharp skip=fragment
open ToolUp.ContentAuthoring.ContentTypeBridge

let mapping =
    ContentTypeMapping.create "title" (LayoutName "page")
    |> ContentTypeMapping.withDescriptionField "summary"
    |> ContentTypeMapping.withBodyFields [ "challenge"; "tags" ]
    |> ContentTypeMapping.withCollection "case-studies"

match ContentTypeBridge.project customValidators caseStudy mapping submission with
| Ok page ->
    // page.Body is ContentBody.Narrative — render it via the existing
    // PublicRendering layout / NarrativeLayout path, or store it on the
    // IEntityStore<PublicPage> overlay so the page handler serves it.
| Error (EntryValidationFailed errors) ->
    // the SAME field-level FieldError list a form submission surfaces —
    // { FieldKey; Code; Message } per failure.
| Error (SchemaMismatch (expected, actual)) -> ...
| Error (MissingTitle key) -> ...
```

The projection is pure and deterministic:

- The **title** field becomes `PublicPage.Title` and the document title.
- The **slug** is taken from the mapped slug field, or derived from the
  title (invariant lowercasing + `-`-collapsing).
- The **description** field becomes `PublicPage.Description` and the
  document subtitle.
- Each **body field** becomes a `NarrativeSection` (heading = the field's
  `DisplayName`): text splits into paragraphs on blank lines, a
  multi-choice value renders as a bullet list, scalars render as a single
  paragraph.
- `PublishedAt` is the submission's `SubmittedAt`.

Because validation runs through `FormValidator.validate` unforked, an
invalid entry is rejected with exactly the field-level errors a form
submission would produce — required-field, type, and rule violations all
surface per field.

## Publish lifecycle

`PublicPage.Status` is a `PublishStatus` — `Draft` / `Scheduled of at` /
`Published` / `Archived` — defaulting to `Published` at every existing
construction site (GP 11: a page built without an explicit status serves
exactly as before). The markdown loader reads an optional `status:`
frontmatter key. The public page handler filters non-visible pages to a
404 via `PublicPage.isPubliclyVisible now page` (`Draft` / `Archived` /
not-yet-due `Scheduled` are hidden); a signed preview link bypasses it.

`ContentLifecycle` (in `ToolUp.ContentAuthoring`) supplies the editorial
side:

- `editorialWorkflow` — the Forms `WorkflowDefinition`
  `draft → in-review → published` (+ unpublish / archive / restore), with
  the approval transitions guarded by the registered `approveGuard`
  predicate (role-gating, GP 4). Register with
  `FormsServerApp.withWorkflow` / `withGuard`.
- Pure transitions — `publishAt` / `schedule` / `archive` / `toDraft`,
  and `statusForState` mapping a workflow state to a `PublishStatus`.
- `runScheduledPublishSweep store now` — promotes every `Scheduled`-and-due
  page to `Published`. A deployment registers it as a recurring
  `IJobScheduler` job so scheduled content goes live without a redeploy.

## Versioning

The `IEntityStore` overlay keeps an append-only version per `Save`.
`PublicPageRevisions` (in `ToolUp.PublicRendering`) is the convenience
layer: `list` (newest first), `get version`, `current`, and `restore`.
`restore` is itself append-only — it writes the chosen revision's content
as a new current version, so history (including the restore) is preserved.

## Shareable preview links

`ContentPreview` reuses the `IShareTokenStore` substrate (the same
HMAC-signed tokens behind publishable Forms surveys) to share an
unpublished page without full auth:

```fsharp skip=fragment
let! url = ContentPreview.issuePreviewToken shareTokenStore scopeId "case-studies/acme" "editor-1" (TimeSpan.FromDays 3.0)
//  /preview?token=… — renders the Draft for 3 days, gated by the signature
```

The `/preview` route (mounted automatically by the PublicRendering
companion, before the catch-all page handler) validates the token and
renders the referenced page **bypassing the visibility filter** — so an
editor previews a Draft — with `noindex` / `no-store` headers. No token,
an invalid/expired token, or no `IShareTokenStore` → the route declines
(404). The page is never reachable without a valid signature (GP 4).

## Authoring admin API

`IContentAdminApi` (Fable.Remoting, `/api/content-admin/*`) is the
server surface a "Content" / "Pages" admin module drives —
`ListPages` / `GetPage` / `SavePage` / `SetStatus` / `ListRevisions` /
`RestoreRevision` over the page overlay, reusing the lifecycle +
versioning substrate above. Mount it with
`ContentAdminCompose.withContentAdmin`. The Fable sidebar **client**
module (a rich block editor emitting Phase 87 `NarrativeElement` trees +
Phase 88 media pickers) binds this contract and is the one remaining
iterative piece — the backend, render path, lifecycle, versioning, and
preview it needs are all in place.

## See also

- [`ContentTypeBridge`](../../src/ContentAuthoring/Server/ContentTypeBridge.fs) — the projector.
- [`narrative-elements.md`](narrative-elements.md) — the block vocabulary entry bodies use (Phase 87).
- [`media-library.md`](../companions/media-library.md) — media a content entry references (Phase 88).

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

```fsharp
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

## Roadmap (Phase 89 follow-on)

The bridge is the foundation. The remaining authoring-lifecycle pieces
are follow-on work on this companion:

- **Publish lifecycle** — `PublicPage.Status` (`Draft` / `Scheduled` /
  `Published` / `Archived`, defaulting to `Published` for back-compat,
  GP 11); a Draft is not publicly served until its workflow reaches
  Published; scheduled publish fires via `IJobScheduler`. The editorial
  review/approval is a Forms `WorkflowDefinition` with role-gated
  transitions (GP 4).
- **Versioning** — append-only revisions on the `IEntityStore<PublicPage>`
  overlay (which already version-bumps on every `Save`), with
  restore-to-revision.
- **Authoring admin module** — an optional Fable SDK module ("Content" /
  "Pages" sidebar): list/edit entries, a block editor emitting the Phase
  87 `NarrativeElement` trees + Phase 88 media-library pickers, status
  transitions, preview.
- **Shareable preview links** — reuse `IShareTokenStore` (the Forms
  publishable-survey substrate) for token-gated draft/preview URLs
  without full auth.

## See also

- [`ContentTypeBridge`](../../src/ContentAuthoring/Server/ContentTypeBridge.fs) — the projector.
- [`narrative-elements.md`](narrative-elements.md) — the block vocabulary entry bodies use (Phase 87).
- [`media-library.md`](../companions/media-library.md) — media a content entry references (Phase 88).

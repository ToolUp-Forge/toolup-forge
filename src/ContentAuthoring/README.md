# ToolUp.ContentAuthoring

CMS authoring bridge for ToolUp (Phase 89). A content type **is** a
`ToolUp.Forms` `FormSchema`, a content entry **is** a `Submission`, and
publishing projects a validated submission to a `ToolUp.PublicRendering`
`PublicPage` with a `Narrative` body. Forms is consumed unforked —
validation comes straight from the Forms engine; the editorial lifecycle
(draft / scheduled / archived, revisions, preview share-links) rides the
shipped `PublicPage.Status` + `PublicPageRevisions` + `IShareTokenStore`
substrate.

Opt-in companion: PublicRendering / Forms consumers that don't author
content pay nothing.

See [`docs/platform/cms-authoring.md`](https://github.com/ToolUp-Forge/toolup-forge/blob/main/docs/platform/cms-authoring.md)
for the authoring model and composition guide.

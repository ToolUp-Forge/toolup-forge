namespace ToolUp.PublicRendering

open System
open ToolUp.Platform.Narrative

// `ContentRoot` and `PublicRenderingMode` live in
// `ToolUp.Platform.Core.Shared.SDK.Shared.fs` so `ServerConfig` can
// reference the mode field. They are re-opened here implicitly via
// `ToolUp.Platform` (consumers `open ToolUp.PublicRendering` to get
// `Slug` / `LayoutName` / `PublicPage` / `Redirect`; the platform
// types arrive from the standard `open ToolUp.Platform` in compose
// roots).

/// URL path segment(s) identifying a page. No leading slash; nested
/// slugs (e.g. `"services/consulting"`) encode a directory shape.
/// Matched case-sensitively (filesystem convention).
type Slug = Slug of string

module Slug =
    let value (Slug s) = s

/// Layout key registered at compose time via
/// `PublicRenderingServerApp.withLayout`. Layouts are F# functions —
/// `PublicPage -> XmlNode` — not file paths. Unknown names fall back
/// to the first-registered layout at render time.
type LayoutName = LayoutName of string

module LayoutName =
    let value (LayoutName n) = n

/// Editorial publish state of a page (Phase 89). `Published` is the
/// default for every page that predates the lifecycle (GP 11 — a page
/// constructed without an explicit status is served exactly as before).
/// `Draft` and `Archived` are not served to the public; `Scheduled at`
/// is served once the wall clock passes `at`. The public-serving filter
/// (`PublicPage.isPubliclyVisible`) enforces this; authorised preview
/// paths (admin surface, signed preview token) bypass it.
type PublishStatus =
    | Draft
    | Scheduled of at: DateTimeOffset
    | Published
    | Archived

module PublishStatus =
    /// Wire-stable token (admin filters, frontmatter round-trip).
    let token =
        function
        | Draft -> "draft"
        | Scheduled _ -> "scheduled"
        | Published -> "published"
        | Archived -> "archived"

/// Rendered body of a page. The three variants cover the three main
/// authoring paths a publishable site uses:
///
/// - `Markdown` — file-backed `.md` content loaded by
///   `MarkdownContentLoader`. The canonical baseline.
/// - `Html` — pre-rendered HTML fragment. Layouts surface the string
///   verbatim; used for content that arrived as HTML already (legacy
///   imports, hand-authored marketing pages).
/// - `Narrative` — typed `NarrativeDocument`. Programmatic pages
///   (status dashboards, pricing tables that mirror runtime config),
///   AI-emitted pages (the LLM populates a typed structure rather
///   than round-tripping through markdown), and analytical posts
///   whose bodies use the Narrative element set natively (Paragraph
///   + Table + KeyValueGrid + Callout + Metric) all live here.
///
/// Layouts inspect `Body` and dispatch — see `NarrativeLayout` for the
/// shipped helper that renders the `Narrative` branch into a
/// Giraffe.ViewEngine fragment with optional schema.org JSON-LD
/// derived from the document's `Provenance`.
type ContentBody =
    | Markdown of source: string
    | Html of fragment: string
    | Narrative of document: NarrativeDocument

/// Static page / news article / event / etc. Frontmatter is open by
/// design: well-known keys (`og:image`, `author`, `date`, `sitemap`,
/// …) are documented; arbitrary keys flow through to layouts
/// unchanged.
type PublicPage = {
    Slug: Slug
    Title: string
    Description: string
    Body: ContentBody
    Layout: LayoutName
    Frontmatter: Map<string, string>
    PublishedAt: DateTimeOffset option
    /// Collection this page belongs to (`"news"`, `"events"`, `"team"`…).
    /// `None` for standalone top-level pages. Used by `GetCollection`.
    Collection: string option
    /// Phase 89 — editorial publish state. Defaults to `Published` for
    /// every existing page-construction site (GP 11); the CMS authoring
    /// layer sets `Draft` / `Scheduled` / `Archived` and the public
    /// serving path filters non-visible pages out.
    Status: PublishStatus
}

module PublicPage =
    /// Whether a page should be served to an unauthenticated public
    /// request at `now`. `Published` always; `Scheduled` once its time
    /// arrives; `Draft` / `Archived` never. Authorised preview paths
    /// (admin, signed token) bypass this check.
    let isPubliclyVisible (now: DateTimeOffset) (page: PublicPage) : bool =
        match page.Status with
        | Published -> true
        | Scheduled at -> now >= at
        | Draft
        | Archived -> false

/// `redirects.csv` row — legacy URL → new URL + HTTP status code.
/// Query strings on the incoming URL are preserved across the
/// redirect (critical for SEO continuity when porting WordPress /
/// WebForms sites).
type Redirect = {
    From: string
    To: string
    StatusCode: int
}
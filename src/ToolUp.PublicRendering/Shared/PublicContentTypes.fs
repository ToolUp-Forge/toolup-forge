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

/// Phase 86 — visibility audience of a page: who may see it once it is
/// `Published`. `Public` (the default, GP 11) is served to anonymous
/// requests exactly as pre-86; the other cases gate the page behind the
/// request's resolved `AccessContext` at serve time (the
/// `PublicPageHandler` runs the authorization pre-check, and non-`Public`
/// pages are excluded from `sitemap.xml` / Atom feeds / static export).
/// Settable via the `audience:` frontmatter key or a content source's
/// synthesised page.
[<RequireQualifiedAccess>]
type PageAudience =
    /// Anonymous-visible. The default — a page that declares no
    /// `audience:` is served to everyone, byte-for-byte pre-86.
    | Public
    /// Any resolved (non-anonymous) principal. Anonymous → 401.
    | Authenticated
    /// Gated to principals holding at least one of the named roles
    /// (matched against the module-route RBAC surface —
    /// `AccessContext.canAccessModule`). An empty list gates to "any
    /// authenticated". A principal without a matching role → 403.
    | ScopeGated of roles: string list
    /// Gated to the principal whose own scope (user / team / claim scope)
    /// matches `relationship` — the client-portal case (a page for client
    /// X is visible only to X). A non-matching principal → 403.
    | ClientGated of relationship: string

module PageAudience =
    /// Wire-stable token (admin filters, frontmatter round-trip).
    let token =
        function
        | PageAudience.Public -> "public"
        | PageAudience.Authenticated -> "authenticated"
        | PageAudience.ScopeGated _ -> "scope"
        | PageAudience.ClientGated _ -> "client"

    /// Parse an `audience:` frontmatter value. Recognised forms
    /// (case-insensitive on the prefix; values trimmed):
    ///   - absent / `""` / `"public"`   → `Public`
    ///   - `"authenticated"`            → `Authenticated`
    ///   - `"scope:editor,admin"`       → `ScopeGated ["editor"; "admin"]`
    ///   - `"client:acme"`              → `ClientGated "acme"`
    /// Anything unrecognised → `Public` (fail-open to the pre-86 default
    /// per GP 11 — a typo'd `audience:` never silently hides a page; an
    /// operator hiding a page uses `status:` or an explicit recognised
    /// value).
    let parse (raw: string option) : PageAudience =
        match raw |> Option.map (fun s -> s.Trim()) with
        | None
        | Some "" -> PageAudience.Public
        | Some v ->
            let lower = v.ToLowerInvariant()

            if lower = "public" then
                PageAudience.Public
            elif lower = "authenticated" then
                PageAudience.Authenticated
            elif lower.StartsWith "scope:" then
                v.Substring(6).Split(',')
                |> Array.map (fun s -> s.Trim())
                |> Array.filter (fun s -> s <> "")
                |> Array.toList
                |> PageAudience.ScopeGated
            elif lower.StartsWith "client:" then
                PageAudience.ClientGated(v.Substring(7).Trim())
            else
                PageAudience.Public

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
    /// Phase 86 — visibility audience. Defaults to `Public` for every
    /// existing page-construction site (GP 11), so a deployment with only
    /// public pages is served exactly as pre-86. Non-`Public` pages are
    /// gated by the handler's authorization pre-check and excluded from
    /// `sitemap.xml` / feeds / static export.
    Audience: PageAudience
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

    /// Phase 86 — whether a page is anonymously visible (`Audience.Public`).
    /// Non-`Public` (gated) pages are excluded from `sitemap.xml`, Atom
    /// feeds, and static export so a gated slug never leaks to a crawler.
    let isPublic (page: PublicPage) : bool =
        match page.Audience with
        | PageAudience.Public -> true
        | _ -> false

    // ─── Phase 90 — taxonomy (tags / categories) ─────────────────────
    //
    // Tags generalise the single-valued `Collection` into a multi-valued
    // taxonomy for tag pages, related-content, and faceted browse. They
    // are derived from the open `Frontmatter` `"tags"` key (a
    // comma-separated list) rather than carried as a dedicated record
    // field, so EVERY existing `PublicPage`-construction site is
    // unaffected (GP 11) — a page with no `tags:` frontmatter simply has
    // no tags. A markdown author writes `tags: news, product, launch` in
    // frontmatter; a programmatic page sets the same key in its
    // `Frontmatter` map. `TaxonomyHandler` + `NavTree` consume these
    // helpers; nothing here changes how an existing page renders.

    /// The page's tags, parsed from the `"tags"` frontmatter key
    /// (comma-separated, trimmed, empties dropped). `[]` when the key is
    /// absent. Order follows the authored list.
    let tags (page: PublicPage) : string list =
        match page.Frontmatter.TryFind "tags" with
        | Some raw ->
            raw.Split(',')
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (fun s -> s <> "")
            |> Array.toList
        | None -> []

    /// Whether the page carries `tag` (case-insensitive).
    let hasTag (tag: string) (page: PublicPage) : bool =
        tags page
        |> List.exists (fun t -> String.Equals(t, tag, StringComparison.OrdinalIgnoreCase))

/// `redirects.csv` row — legacy URL → new URL + HTTP status code.
/// Query strings on the incoming URL are preserved across the
/// redirect (critical for SEO continuity when porting WordPress /
/// WebForms sites).
type Redirect = {
    From: string
    To: string
    StatusCode: int
}
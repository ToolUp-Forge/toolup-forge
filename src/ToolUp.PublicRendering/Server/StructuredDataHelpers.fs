namespace ToolUp.PublicRendering

open System.Text.Json
open ToolUp.Platform.Narrative

/// JSON-LD emitters for the five most common schema.org types on
/// content sites. Each returns a JSON string ready to embed as
/// `<script type="application/ld+json">{...}</script>` inside a
/// Giraffe.ViewEngine layout.
///
/// All emitters read from `PublicPage.Frontmatter`; missing keys
/// degrade to empty strings rather than throwing. Layout authors
/// register them by name via
/// `PublicRenderingServerApp.withStructuredDataBuilder` and call
/// out from the layout body.
module StructuredDataHelpers =
    let private opt = Option.defaultValue ""

    let private serialise (payload: obj) : string =
        JsonSerializer.Serialize(payload, JsonSerializerOptions(WriteIndented = false))

    /// `Article` schema — news posts, blog entries, long-form pages.
    /// Frontmatter keys read: `author`, `og:image`, `description`,
    /// `date` (also used for `PublishedAt`).
    let article (page: PublicPage) : string =
        let author = page.Frontmatter |> Map.tryFind "author" |> opt
        let image = page.Frontmatter |> Map.tryFind "og:image" |> opt

        let datePublished =
            page.PublishedAt
            |> Option.map (fun d -> d.ToString("o"))
            |> Option.defaultValue ""

        serialise (
            dict [
                "@context", box "https://schema.org"
                "@type", box "Article"
                "headline", box page.Title
                "description", box page.Description
                "author", box (dict [ "@type", box "Person"; "name", box author ])
                "image", box image
                "datePublished", box datePublished
            ]
        )

    /// `Person` schema — team / about / author pages. Frontmatter
    /// keys read: `name` (default: page title), `jobTitle`, `og:image`,
    /// `email`, `url`.
    let person (page: PublicPage) : string =
        let name = page.Frontmatter |> Map.tryFind "name" |> Option.defaultValue page.Title

        let jobTitle = page.Frontmatter |> Map.tryFind "jobTitle" |> opt
        let image = page.Frontmatter |> Map.tryFind "og:image" |> opt
        let email = page.Frontmatter |> Map.tryFind "email" |> opt
        let url = page.Frontmatter |> Map.tryFind "url" |> opt

        serialise (
            dict [
                "@context", box "https://schema.org"
                "@type", box "Person"
                "name", box name
                "jobTitle", box jobTitle
                "image", box image
                "email", box email
                "url", box url
            ]
        )

    /// `Event` schema. Frontmatter keys read: `startDate`, `endDate`,
    /// `location`, `og:image`, `description`.
    let event (page: PublicPage) : string =
        let startDate = page.Frontmatter |> Map.tryFind "startDate" |> opt
        let endDate = page.Frontmatter |> Map.tryFind "endDate" |> opt
        let location = page.Frontmatter |> Map.tryFind "location" |> opt
        let image = page.Frontmatter |> Map.tryFind "og:image" |> opt

        serialise (
            dict [
                "@context", box "https://schema.org"
                "@type", box "Event"
                "name", box page.Title
                "description", box page.Description
                "startDate", box startDate
                "endDate", box endDate
                "location", box (dict [ "@type", box "Place"; "name", box location ])
                "image", box image
            ]
        )

    /// `Organization` schema — typical for the homepage or about
    /// page of a marketing site. Frontmatter keys: `name`, `url`,
    /// `og:image` (treated as `logo`), `description`.
    let organization (page: PublicPage) : string =
        let name = page.Frontmatter |> Map.tryFind "name" |> Option.defaultValue page.Title

        let url = page.Frontmatter |> Map.tryFind "url" |> opt
        let logo = page.Frontmatter |> Map.tryFind "og:image" |> opt

        serialise (
            dict [
                "@context", box "https://schema.org"
                "@type", box "Organization"
                "name", box name
                "url", box url
                "logo", box logo
                "description", box page.Description
            ]
        )

    /// `Article` schema derived from a `NarrativeDocument`'s
    /// `Provenance`. Returns `None` when the document has no
    /// provenance (no `datePublished`, no identifier, nothing to
    /// emit). Used by `NarrativeLayout.articleJsonLd` for pages whose
    /// body is `ContentBody.Narrative`.
    ///
    /// `headline` / `description` come from the *page* (so the
    /// surrounding `PublicPage.Title` / `Description` set by the
    /// markdown loader's frontmatter or the layout's compose path
    /// drive what crawlers see); `datePublished` /
    /// `dateModified` / `identifier` / `provider` come from the
    /// document's `Provenance`.
    let articleFromNarrative (page: PublicPage) (doc: NarrativeDocument) : string option =
        match doc.Provenance with
        | None -> None
        | Some prov ->
            let datePublished = prov.GeneratedAt.ToString("o")

            let headline =
                if System.String.IsNullOrWhiteSpace page.Title then
                    doc.Title
                else
                    page.Title

            Some(
                serialise (
                    dict [
                        "@context", box "https://schema.org"
                        "@type", box "Article"
                        "headline", box headline
                        "description", box page.Description
                        "datePublished", box datePublished
                        "dateModified", box datePublished
                        "identifier", box prov.SettingsKey
                        "provider", box (dict [ "@type", box "Organization"; "name", box prov.ModuleId ])
                    ]
                )
            )

    /// Open Graph meta tags derived from a Narrative-bodied page.
    /// Returns a list of `(property, content)` pairs ready to emit as
    /// `<meta property="..." content="..." />` — Giraffe.ViewEngine
    /// layouts map over the list with `meta [ _property p; _content c ]`.
    ///
    /// Always emits `og:title`, `og:type`, `og:description`. Adds
    /// `og:url` when the document carries a `CanonicalUrl`,
    /// `og:locale` when `Lang` is set, and
    /// `article:published_time` / `article:modified_time` when
    /// `Provenance.GeneratedAt` is present.
    let openGraphFromNarrative (page: PublicPage) (doc: NarrativeDocument) : (string * string) list = [
        "og:title",
        (if System.String.IsNullOrWhiteSpace page.Title then
             doc.Title
         else
             page.Title)
        "og:type", "article"
        "og:description",
        (if System.String.IsNullOrWhiteSpace page.Description then
             doc.Subtitle |> Option.defaultValue ""
         else
             page.Description)
        match doc.CanonicalUrl with
        | Some url -> "og:url", url
        | None -> ()
        match doc.Lang with
        | Some tag -> "og:locale", tag.Replace("-", "_")
        | None -> ()
        match doc.Provenance with
        | Some prov ->
            let stamp = prov.GeneratedAt.ToString("o")
            "article:published_time", stamp
            "article:modified_time", stamp
        | None -> ()
    ]

    /// Twitter card meta tags derived from a Narrative-bodied page.
    /// Returns a list of `(name, content)` pairs ready to emit as
    /// `<meta name="..." content="..." />`. Twitter prefers `name=`
    /// over `property=` for its card-specific tags (the platform
    /// historically supported both but the standard is `name=`).
    ///
    /// Always emits `twitter:card` (`summary_large_image` by default —
    /// the richest card a Narrative page is likely to populate),
    /// `twitter:title`, `twitter:description`. Layouts that want
    /// `twitter:site` / `twitter:creator` should supply those via
    /// their own per-layout block (they're site-config, not document-
    /// config).
    let twitterCardFromNarrative (page: PublicPage) (doc: NarrativeDocument) : (string * string) list = [
        "twitter:card", "summary_large_image"
        "twitter:title",
        (if System.String.IsNullOrWhiteSpace page.Title then
             doc.Title
         else
             page.Title)
        "twitter:description",
        (if System.String.IsNullOrWhiteSpace page.Description then
             doc.Subtitle |> Option.defaultValue ""
         else
             page.Description)
        match page.Frontmatter |> Map.tryFind "og:image" with
        | Some img -> "twitter:image", img
        | None -> ()
    ]

    /// `BreadcrumbList` schema — emitted from the layout, takes the
    /// explicit (name, url) segment list since breadcrumbs derive
    /// from the layout's site structure, not the page itself.
    let breadcrumb (segments: (string * string) list) : string =
        let itemListElement =
            segments
            |> List.mapi (fun i (name, url) ->
                dict [
                    "@type", box "ListItem"
                    "position", box (i + 1)
                    "name", box name
                    "item", box url
                ]
                :> obj)

        serialise (
            dict [
                "@context", box "https://schema.org"
                "@type", box "BreadcrumbList"
                "itemListElement", box itemListElement
            ]
        )

    /// Phase 96 — `SiteNavigationElement` schema (as an `ItemList`) from a
    /// flattened `(name, url)` navigation list. Lets a search engine
    /// understand the site's navigation hierarchy. Emitted from the layout
    /// off the (audience-filtered) nav tree — see
    /// `NavLayout.headStructuredData`.
    let siteNavigation (items: (string * string) list) : string =
        let itemListElement =
            items
            |> List.mapi (fun i (name, url) ->
                dict [
                    "@type", box "SiteNavigationElement"
                    "position", box (i + 1)
                    "name", box name
                    "url", box url
                ]
                :> obj)

        serialise (
            dict [
                "@context", box "https://schema.org"
                "@type", box "ItemList"
                "itemListElement", box itemListElement
            ]
        )
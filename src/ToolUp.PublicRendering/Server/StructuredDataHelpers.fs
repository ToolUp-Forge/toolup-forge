namespace ToolUp.PublicRendering

open System.Text.Json

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
namespace ToolUp.PublicRendering

open System.Text.Json
open System.Text.Encodings.Web
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

    // ─── Typed JSON-value builder (2026-07 idiom sweep) ─────────────
    //
    // A minimal typed JSON tree replaces the former 116
    // `dict [ "@type", box … ]` type-erasure sites — the same
    // `jobj`/`jstr` builder shape proven in the AI wire layer
    // (`AIProviders/*/…Wire.fs`), replicated locally rather than
    // referencing that package (an SSR content package must not depend
    // on the AI wire format). Serialization still goes through STJ's
    // `Utf8JsonWriter` with the same encoder options the previous
    // `JsonSerializer.Serialize` path used, so the emitted JSON is
    // byte-identical.

    /// Typed JSON value for the JSON-LD emitters. Only the shapes the
    /// schema.org payloads need: strings, integer positions, objects
    /// (insertion-ordered field lists), arrays.
    type private JsonLd =
        | JString of string
        | JInt of int
        | JObject of (string * JsonLd) list
        | JArray of JsonLd list

    let private jstr (s: string) = JString s
    let private jint (i: int) = JInt i
    let private jobj (fields: (string * JsonLd) list) = JObject fields
    let private jarr (items: JsonLd list) = JArray items

    // JSON-LD is embedded verbatim in a `<script type="application/ld+json">`
    // block. STJ's default `JavaScriptEncoder` is ASCII-safe but escapes every
    // non-ASCII rune and the HTML-significant set (`<`, `>`, `&`, `+`, `'`) to
    // `\uXXXX` — valid JSON, but it bloats human-authored content (a `°`, an
    // arrow, an `&` in prose) into noise and diverges byte-for-byte from the
    // minimal escaping a hand-rolled emitter produces. Use
    // `UnsafeRelaxedJsonEscaping` (passes UTF-8 + `<`/`>`/`&` through) and then
    // re-apply the ONE escape that matters for `<script>` embedding: rewrite
    // `</` → `<\/` so a literal `</script>` inside a string value cannot
    // terminate the surrounding block (the relaxed encoder would otherwise emit
    // it verbatim — an XSS breakout). The `</`-rewrite only ever matches inside
    // string values; JSON structure contains no `</`.
    let private writerOptions =
        JsonWriterOptions(Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private serialise (payload: JsonLd) : string =
        use stream = new System.IO.MemoryStream()
        use writer = new Utf8JsonWriter(stream, writerOptions)

        let rec write (value: JsonLd) =
            match value with
            | JString s ->
                // A null string serialises as JSON `null` — matches what
                // `JsonSerializer.Serialize` emitted for a boxed null on
                // the previous erased-`dict` path.
                if isNull s then
                    writer.WriteNullValue()
                else
                    writer.WriteStringValue s
            | JInt i -> writer.WriteNumberValue i
            | JObject fields ->
                writer.WriteStartObject()

                for name, fieldValue in fields do
                    writer.WritePropertyName name
                    write fieldValue

                writer.WriteEndObject()
            | JArray items ->
                writer.WriteStartArray()

                for item in items do
                    write item

                writer.WriteEndArray()

        write payload
        writer.Flush()

        System.Text.Encoding.UTF8.GetString(stream.ToArray()).Replace("</", "<\\/")

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
            jobj [
                "@context", jstr "https://schema.org"
                "@type", jstr "Article"
                "headline", jstr page.Title
                "description", jstr page.Description
                "author", jobj [ "@type", jstr "Person"; "name", jstr author ]
                "image", jstr image
                "datePublished", jstr datePublished
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
            jobj [
                "@context", jstr "https://schema.org"
                "@type", jstr "Person"
                "name", jstr name
                "jobTitle", jstr jobTitle
                "image", jstr image
                "email", jstr email
                "url", jstr url
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
            jobj [
                "@context", jstr "https://schema.org"
                "@type", jstr "Event"
                "name", jstr page.Title
                "description", jstr page.Description
                "startDate", jstr startDate
                "endDate", jstr endDate
                "location", jobj [ "@type", jstr "Place"; "name", jstr location ]
                "image", jstr image
            ]
        )

    /// Parse a comma- / newline-separated frontmatter value into a
    /// trimmed, non-empty string list (`[]` when the key is absent or
    /// blank). Shared by `organization`'s `sameAs` reader.
    let private csvList (raw: string option) : string list =
        match raw with
        | None -> []
        | Some value ->
            value.Split([| ','; '\n' |])
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (fun s -> s <> "")
            |> Array.toList

    /// `Organization` schema — typical for the homepage or about
    /// page of a marketing site. Frontmatter keys: `name`, `url`,
    /// `og:image` (treated as `logo`), `description`, and (Phase 151)
    /// `sameAs` (comma- / newline-separated social / knowledge-graph
    /// profile URLs → JSON-LD `sameAs` array). `sameAs` is omitted when
    /// the key is absent, so an existing `organization` call is
    /// byte-for-byte unchanged (GP 11).
    let organization (page: PublicPage) : string =
        let name = page.Frontmatter |> Map.tryFind "name" |> Option.defaultValue page.Title

        let url = page.Frontmatter |> Map.tryFind "url" |> opt
        let logo = page.Frontmatter |> Map.tryFind "og:image" |> opt
        let sameAs = page.Frontmatter |> Map.tryFind "sameAs" |> csvList

        let baseFields = [
            "@context", jstr "https://schema.org"
            "@type", jstr "Organization"
            "name", jstr name
            "url", jstr url
            "logo", jstr logo
            "description", jstr page.Description
        ]

        // Append `sameAs` only when present so the absent-key output is
        // byte-for-byte the pre-151 shape (GP 11).
        let fields =
            if List.isEmpty sameAs then
                baseFields
            else
                baseFields @ [ "sameAs", jarr (sameAs |> List.map jstr) ]

        serialise (jobj fields)

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
                    jobj [
                        "@context", jstr "https://schema.org"
                        "@type", jstr "Article"
                        "headline", jstr headline
                        "description", jstr page.Description
                        "datePublished", jstr datePublished
                        "dateModified", jstr datePublished
                        "identifier", jstr prov.SettingsKey
                        "provider", jobj [ "@type", jstr "Organization"; "name", jstr prov.ModuleId ]
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
                jobj [
                    "@type", jstr "ListItem"
                    "position", jint (i + 1)
                    "name", jstr name
                    "item", jstr url
                ])

        serialise (
            jobj [
                "@context", jstr "https://schema.org"
                "@type", jstr "BreadcrumbList"
                "itemListElement", jarr itemListElement
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
                jobj [
                    "@type", jstr "SiteNavigationElement"
                    "position", jint (i + 1)
                    "name", jstr name
                    "url", jstr url
                ])

        serialise (
            jobj [
                "@context", jstr "https://schema.org"
                "@type", jstr "ItemList"
                "itemListElement", jarr itemListElement
            ]
        )

    // ─── Phase 151 — site-level + rich-result emitters ───────────────
    //
    // The schema.org types content / learning / catalog sites win rich
    // results with, plus the two highest-value site-level emitters absent
    // pre-151: `WebSite` + `SearchAction` (the Google sitelinks search box)
    // and (above) `Organization.sameAs`. Each mirrors the existing pure
    // `-> string` emitter shape — callable from a layout exactly like
    // `article` / `breadcrumb` — and degrades missing values to empty
    // strings rather than throwing.

    /// `WebSite` schema with an optional `SearchAction` `potentialAction` —
    /// the Google sitelinks search box. Site-level: emit once (typically
    /// on the home page / in the shared head). `searchUrlTemplate` is the
    /// site's search URL with the literal `{search_term_string}` token,
    /// e.g. `"https://example.com/search?q={search_term_string}"`; `None`
    /// omits `potentialAction` (a plain `WebSite` node).
    let webSite (name: string) (url: string) (searchUrlTemplate: string option) : string =
        let baseFields = [
            "@context", jstr "https://schema.org"
            "@type", jstr "WebSite"
            "name", jstr name
            "url", jstr url
        ]

        let fields =
            match searchUrlTemplate with
            | Some template ->
                let action =
                    jobj [
                        "@type", jstr "SearchAction"
                        "target", jobj [ "@type", jstr "EntryPoint"; "urlTemplate", jstr template ]
                        "query-input", jstr "required name=search_term_string"
                    ]

                baseFields @ [ "potentialAction", action ]
            | None -> baseFields

        serialise (jobj fields)

    /// `FAQPage` schema from `(question, answer)` pairs — each pair becomes
    /// a `Question` with an `acceptedAnswer`. An empty list emits an
    /// `FAQPage` with an empty `mainEntity` rather than throwing.
    let faqPage (items: (string * string) list) : string =
        let mainEntity =
            items
            |> List.map (fun (q, a) ->
                jobj [
                    "@type", jstr "Question"
                    "name", jstr q
                    "acceptedAnswer", jobj [ "@type", jstr "Answer"; "text", jstr a ]
                ])

        serialise (
            jobj [
                "@context", jstr "https://schema.org"
                "@type", jstr "FAQPage"
                "mainEntity", jarr mainEntity
            ]
        )

    /// `HowTo` schema from a name + ordered step texts — each step becomes
    /// a positioned `HowToStep`.
    let howTo (name: string) (steps: string list) : string =
        let stepElements =
            steps
            |> List.mapi (fun i text -> jobj [ "@type", jstr "HowToStep"; "position", jint (i + 1); "text", jstr text ])

        serialise (
            jobj [
                "@context", jstr "https://schema.org"
                "@type", jstr "HowTo"
                "name", jstr name
                "step", jarr stepElements
            ]
        )

    /// `LearningResource` schema — the SERP `LearningResource` rich result for
    /// an educational reference page. `name` / `description` / `teaches` are
    /// the caller's; `learningResourceType` / `educationalLevel` / `inLanguage`
    /// carry the common reference-page defaults (`"Reference"` / `"Beginner"` /
    /// `"en"`) — pass the richer overload when a page needs other values.
    let learningResource (name: string) (description: string) (teaches: string) : string =
        serialise (
            jobj [
                "@context", jstr "https://schema.org"
                "@type", jstr "LearningResource"
                "name", jstr name
                "description", jstr description
                "learningResourceType", jstr "Reference"
                "educationalLevel", jstr "Beginner"
                "teaches", jstr teaches
                "inLanguage", jstr "en"
            ]
        )

    /// `LearningResource` schema with explicit `learningResourceType` /
    /// `educationalLevel` / `inLanguage` — for pages that aren't the
    /// beginner-reference default the 3-arg `learningResource` assumes.
    let learningResourceWith
        (name: string)
        (description: string)
        (teaches: string)
        (learningResourceType: string)
        (educationalLevel: string)
        (inLanguage: string)
        : string =
        serialise (
            jobj [
                "@context", jstr "https://schema.org"
                "@type", jstr "LearningResource"
                "name", jstr name
                "description", jstr description
                "learningResourceType", jstr learningResourceType
                "educationalLevel", jstr educationalLevel
                "teaches", jstr teaches
                "inLanguage", jstr inLanguage
            ]
        )

    /// `Course` schema — name / provider (an `Organization`) / description.
    let course (name: string) (provider: string) (description: string) : string =
        serialise (
            jobj [
                "@context", jstr "https://schema.org"
                "@type", jstr "Course"
                "name", jstr name
                "provider", jobj [ "@type", jstr "Organization"; "name", jstr provider ]
                "description", jstr description
            ]
        )

    /// `ItemList` schema from ordered `(name, url)` entries — the
    /// listing / index-page emitter (each entry a positioned `ListItem`
    /// with a `url`). Distinct from `siteNavigation` (which emits
    /// `SiteNavigationElement` items for the site's nav chrome).
    let itemList (items: (string * string) list) : string =
        let itemListElement =
            items
            |> List.mapi (fun i (name, url) ->
                jobj [
                    "@type", jstr "ListItem"
                    "position", jint (i + 1)
                    "name", jstr name
                    "url", jstr url
                ])

        serialise (
            jobj [
                "@context", jstr "https://schema.org"
                "@type", jstr "ItemList"
                "itemListElement", jarr itemListElement
            ]
        )

    /// `Product` schema — name / description with an optional `offers`
    /// (`(price, priceCurrency)`) and optional `aggregateRating`
    /// (`(ratingValue, reviewCount)`). Each optional block is omitted when
    /// `None`, so a minimal product emits just name + description.
    let product
        (name: string)
        (description: string)
        (offers: (string * string) option)
        (aggregateRating: (string * string) option)
        : string =
        let baseFields = [
            "@context", jstr "https://schema.org"
            "@type", jstr "Product"
            "name", jstr name
            "description", jstr description
        ]

        let withOffers =
            match offers with
            | Some(price, currency) ->
                baseFields
                @ [
                    "offers", jobj [ "@type", jstr "Offer"; "price", jstr price; "priceCurrency", jstr currency ]
                ]
            | None -> baseFields

        let withRating =
            match aggregateRating with
            | Some(ratingValue, reviewCount) ->
                withOffers
                @ [
                    "aggregateRating",
                    jobj [
                        "@type", jstr "AggregateRating"
                        "ratingValue", jstr ratingValue
                        "reviewCount", jstr reviewCount
                    ]
                ]
            | None -> withOffers

        serialise (jobj withRating)

    /// `VideoObject` schema — name / description / thumbnail URL / upload
    /// date (ISO-8601) / content URL. Missing values pass through as the
    /// supplied (possibly empty) strings rather than throwing.
    let videoObject
        (name: string)
        (description: string)
        (thumbnailUrl: string)
        (uploadDate: string)
        (contentUrl: string)
        : string =
        serialise (
            jobj [
                "@context", jstr "https://schema.org"
                "@type", jstr "VideoObject"
                "name", jstr name
                "description", jstr description
                "thumbnailUrl", jstr thumbnailUrl
                "uploadDate", jstr uploadDate
                "contentUrl", jstr contentUrl
            ]
        )
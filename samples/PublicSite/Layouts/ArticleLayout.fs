module PublicSite.Layouts.ArticleLayout

open Giraffe.ViewEngine
open ToolUp.PublicRendering

/// News / blog post layout. Renders the markdown body with the
/// publication date in a header, plus an `Article` JSON-LD block.
let render (page: PublicPage) : XmlNode =
    let bodyHtml =
        match page.Body with
        | Html fragment -> rawText fragment
        | Markdown source -> rawText source

    let publishedString =
        match page.PublishedAt with
        | Some d -> d.ToString "yyyy-MM-dd"
        | None -> ""

    let structuredData = StructuredDataHelpers.article page

    let articleBody =
        article [ _class "post" ] [
            header [ _class "post-header" ] [
                h1 [] [ str page.Title ]
                time [ _datetime publishedString ] [ str publishedString ]
            ]
            div [ _class "post-content" ] [ bodyHtml ]
            script [ _type "application/ld+json" ] [ rawText structuredData ]
        ]

    BaseLayout.render page articleBody
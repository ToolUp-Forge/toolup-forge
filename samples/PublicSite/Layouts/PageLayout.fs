module PublicSite.Layouts.PageLayout

open Giraffe.ViewEngine
open ToolUp.PublicRendering

/// Marketing-page layout. The page's `ContentBody.Html` fragment is
/// rendered inside a centred container; `Organization` JSON-LD is
/// emitted in `<head>`-shaped position via a hidden `<script>` block
/// at the bottom of the body so the structure stays simple.
let render (page: PublicPage) : XmlNode =
    let bodyHtml =
        match page.Body with
        | Html fragment -> rawText fragment
        | Markdown source -> rawText source
        // Phase 80 bodies reach this layout when a narrative page picks
        // the marketing template; render it the same way NarrativeLayout
        // does rather than dropping the body silently.
        | Narrative doc -> rawText (ToolUp.Platform.Narrative.NarrativeHtml.render doc)

    let structuredData = StructuredDataHelpers.organization page

    let pageBody =
        article [ _class "page" ] [
            header [ _class "page-header" ] [ h1 [] [ str page.Title ] ]
            div [ _class "page-content" ] [ bodyHtml ]
            script [ _type "application/ld+json" ] [ rawText structuredData ]
        ]

    BaseLayout.render page pageBody
module MultiSitePublic.Layouts.SiteBLayout

open Giraffe.ViewEngine
open ToolUp.PublicRendering

/// Site B's bespoke "page" layout, registered via
/// `PublicSiteDef.Layouts` so it applies on site B's hosts only —
/// proof that layouts resolve per site. The `data-layout="siteb"`
/// marker makes the override curl-visible: pages served with
/// `Host: siteb.example` carry it, while site A (inheriting the
/// shared layout) renders `data-layout="shared"`.
let render (page: PublicPage) : XmlNode =
    let bodyHtml =
        match page.Body with
        | Html fragment -> rawText fragment
        | Markdown source -> rawText source
        | Narrative _ -> rawText ""

    html [] [
        head [] [
            meta [ _charset "utf-8" ]
            meta [ _name "viewport"; _content "width=device-width, initial-scale=1" ]
            title [] [ str (page.Title + " — Site B") ]
            meta [ _name "description"; _content page.Description ]
        ]
        body [ attr "data-layout" "siteb" ] [
            header [ _class "siteb-banner" ] [ str "Site B — bespoke layout" ]
            main [ _class "site-main" ] [ h1 [] [ str page.Title ]; div [ _class "page-content" ] [ bodyHtml ] ]
            footer [ _class "site-footer" ] [
                a [ _href "/sitemap.xml" ] [ str "Sitemap" ]
                Text " · "
                a [ _href "/feed.atom" ] [ str "Atom feed" ]
            ]
        ]
    ]
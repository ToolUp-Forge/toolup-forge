module MultiSitePublic.Layouts.SharedLayout

open Giraffe.ViewEngine
open ToolUp.PublicRendering

/// The compose-registered "page" layout. The default site uses it,
/// and so does any satellite that declares no layouts of its own
/// (site A in this sample) — `PublicSiteDef.Layouts = Map.empty`
/// inherits the shared `withLayout` registrations. Deliberately
/// minimal markup: the multi-site demonstration lives in Program.fs,
/// not here.
let render (page: PublicPage) : XmlNode =
    let bodyHtml =
        match page.Body with
        | Html fragment -> rawText fragment
        | Markdown source -> rawText source
        // Satellites are markdown-file-backed (Phase 114 v1 scope),
        // so file-loaded pages never carry a Narrative body here.
        | Narrative _ -> rawText ""

    html [] [
        head [] [
            meta [ _charset "utf-8" ]
            meta [ _name "viewport"; _content "width=device-width, initial-scale=1" ]
            title [] [ str page.Title ]
            meta [ _name "description"; _content page.Description ]
        ]
        body [ attr "data-layout" "shared" ] [
            main [ _class "site-main" ] [ h1 [] [ str page.Title ]; div [ _class "page-content" ] [ bodyHtml ] ]
            footer [ _class "site-footer" ] [
                a [ _href "/sitemap.xml" ] [ str "Sitemap" ]
                Text " · "
                a [ _href "/feed.atom" ] [ str "Atom feed" ]
            ]
        ]
    ]
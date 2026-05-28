module PublicSite.Layouts.BaseLayout

open Giraffe.ViewEngine
open ToolUp.PublicRendering

/// Common shell — `<html>` / `<head>` / `<body>` skeleton + nav +
/// footer. Page / Article layouts compose this with their body
/// content. Styling stays opinion-free: every classname is supplied
/// directly so a consumer can swap Tailwind / Pico / hand-CSS without
/// touching layout code.
let private nav =
    nav [ _class "site-nav" ] [
        a [ _href "/" ] [ str "Home" ]
        a [ _href "/about" ] [ str "About" ]
        a [ _href "/services" ] [ str "Services" ]
        a [ _href "/news" ] [ str "News" ]
        a [ _href "/contact" ] [ str "Contact" ]
    ]

let private footer =
    footer [ _class "site-footer" ] [
        Text "Built with ToolUp.Platform — SSR via "
        a [ _href "https://github.com/giraffe-fsharp/Giraffe.ViewEngine" ] [ str "Giraffe.ViewEngine" ]
        Text " · "
        a [ _href "/sitemap.xml" ] [ str "Sitemap" ]
    ]

let render (page: PublicPage) (bodyContent: XmlNode) : XmlNode =
    html [] [
        head [] [
            meta [ _charset "utf-8" ]
            meta [ _name "viewport"; _content "width=device-width, initial-scale=1" ]
            title [] [ str page.Title ]
            meta [ _name "description"; _content page.Description ]

            // Optional OpenGraph image from frontmatter
            match Map.tryFind "og:image" page.Frontmatter with
            | Some img -> meta [ _property "og:image"; _content img ]
            | None -> rawText ""
        ]
        body [] [ nav; main [ _class "site-main" ] [ bodyContent ]; footer ]
    ]
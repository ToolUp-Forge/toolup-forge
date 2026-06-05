module ToolUp.BrandKit.PageChrome

open Giraffe.ViewEngine
open ToolUp.BrandKit.Wordmark

/// Page chrome primitives — sticky brand header + page footer. The
/// "drop in once and stop worrying" helpers most pages need. Consumers
/// wanting fully-custom chrome can opt out and hand-roll.
/// What sits on the left of the header — either a monogram URL or a
/// full wordmark spec. The brand book's design hand-off typically
/// names one canonical choice per app (monogram for compact / favicon
/// contexts, wordmark for full-width page headers).
type BrandLockup =
    | Monogram of url: string * altText: string
    | Wordmark of WordmarkSpec

/// A nav item in the header navigation.
///   `Label` — link text
///   `Href`  — link URL
type NavItem = { Label: string; Href: string }

/// Header spec.
///   `Brand` — what to render on the left
///   `Nav`   — nav items in the centre / right
///   `Right` — additional XmlNodes pinned to the far right (toggle
///             buttons, a sign-in pill, etc.)
type HeaderSpec = {
    Brand: BrandLockup
    Nav: NavItem list
    Right: XmlNode list
}

/// Render the sticky brand header per the spec.
let pageHeader (spec: HeaderSpec) : XmlNode =
    let brandNode =
        match spec.Brand with
        | Monogram(url, alt) ->
            img [
                _class "bk-header-monogram"
                _src url
                _alt alt
                attr "width" "32"
                attr "height" "32"
            ]
        | Wordmark spec -> wordmark spec

    let navNode =
        nav
            [ _class "bk-header-nav" ]
            (spec.Nav
             |> List.map (fun item -> a [ _href item.Href; _class "bk-header-nav-link" ] [ str item.Label ]))

    let rightNode = div [ _class "bk-header-right" ] spec.Right

    tag "header" [ _class "bk-header" ] [ brandNode; navNode; rightNode ]

/// Footer spec.
///   `Copyright` — copyright line text (e.g. "© 2026 Diametrical Ltd")
///   `Links`     — link items (terms / privacy / contact / etc.)
type FooterSpec = {
    Copyright: string
    Links: NavItem list
}

/// Render the page footer per the spec.
let pageFooter (spec: FooterSpec) : XmlNode =
    let linksNode =
        nav
            [ _class "bk-footer-links" ]
            (spec.Links
             |> List.map (fun item -> a [ _href item.Href; _class "bk-footer-link" ] [ str item.Label ]))

    tag "footer" [ _class "bk-footer" ] [ span [ _class "bk-footer-copyright" ] [ str spec.Copyright ]; linksNode ]
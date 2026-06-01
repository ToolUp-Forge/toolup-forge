namespace ToolUp.PublicRendering

open Giraffe.ViewEngine

/// "Powered by ToolUp Forge" attribution helpers — the canonical
/// pattern every ToolUp-built website renders, mirroring the SDK
/// shell's sidebar footer convention in regular apps. Two pieces:
///
///   1. `generatorMeta` — `<meta name="generator">` for invisible
///      attribution. Indexed by Wappalyzer / BuiltWith / etc., so
///      any site running `ToolUp.PublicRendering` is discoverable as
///      a forge consumer without anything visible on the page itself.
///      Drop into your `<head>`.
///
///   2. `poweredByBadge` (+ `poweredByBadgeWith`) — visible footer
///      badge. Renders the forge brand wordmark wrapped in a link,
///      with a small "Powered by" leading label. The wordmark adapts
///      to `prefers-color-scheme` via a `<picture>` element — dark-text
///      wordmark for light mode, white-text wordmark for dark mode —
///      so no consumer-side CSS work is needed for either posture.
///
/// The badge expects the forge brand wordmark assets to be served
/// from `Options.AssetPrefix` (default `/repo/`):
///
///   - `icon-wordmark-transparent-dark-text-1024.png`  (light mode)
///   - `icon-wordmark-transparent-1024.png`            (dark mode)
///
/// Both transparent-background, so any consumer-side surface colour
/// shows through. Copy the iconset from `Brand Assets/logos/iconset/repo/`
/// in this repo into your `wwwroot/repo/`, or set `Options.AssetPrefix`
/// to the URL where you host them (e.g. `"https://toolup-forge.io/repo/"`
/// for hot-linked attribution).
///
/// Per the WordPress / Hugo / Astro convention, this is a default
/// that consumers can keep, vary, or remove — not a licence term.
/// Apache 2.0 doesn't require visible attribution.
module Attribution =

    type Options = {
        /// Where the badge links to. Default: `https://toolup-forge.io/`.
        LinkTo: string
        /// URL prefix for the wordmark assets. Default: `/repo/`.
        /// The two transparent wordmark filenames are appended to it.
        AssetPrefix: string
        /// CSS height value for the wordmark image. Default: `"3rem"`
        /// (48px). Match this to your footer's typography rhythm.
        WordmarkHeight: string
    }

    module Options =
        /// Forge defaults — link to `https://toolup-forge.io/`, assets
        /// expected under `/repo/`, wordmark rendered at 3rem (48px).
        let defaults: Options = {
            LinkTo = "https://toolup-forge.io/"
            AssetPrefix = "/repo/"
            WordmarkHeight = "3rem"
        }

    /// `<meta name="generator">` tag declaring the page was built
    /// with ToolUp Forge. Invisible to humans; indexed by site-tech
    /// scanners. Drop into your `<head>` alongside the other meta tags.
    let generatorMeta: XmlNode =
        meta [ _name "generator"; _content "ToolUp Forge — toolup-forge.io" ]

    /// Customisable "Powered by ToolUp Forge" footer badge — see
    /// `Options` for the knobs.
    let poweredByBadgeWith (opts: Options) : XmlNode =
        let prefix = opts.AssetPrefix.TrimEnd('/')
        let lightSrc = sprintf "%s/icon-wordmark-transparent-dark-text-1024.png" prefix
        let darkSrc = sprintf "%s/icon-wordmark-transparent-1024.png" prefix

        // Minimum-viable inline styles — just enough to make the badge
        // render correctly out of the box. Layout flexbox for the
        // inline arrangement, fixed wordmark height (the asset is
        // square — 48px tall puts the wordmark text at readable size),
        // small uppercase tracking on the "Powered by" label.
        // Consumers can override via parent selectors (`a[title="Powered by ToolUp Forge"] { ... }`)
        // or by hand-rolling their own wrapper.
        let linkStyle =
            "display:inline-flex;align-items:center;gap:0.75rem;text-decoration:none;color:inherit"

        let labelStyle =
            "font-size:0.75rem;text-transform:uppercase;letter-spacing:0.05em;opacity:0.7"

        let imgStyle = sprintf "height:%s;width:auto" opts.WordmarkHeight

        a [ _href opts.LinkTo; _title "Powered by ToolUp Forge"; attr "style" linkStyle ] [
            span [ attr "style" labelStyle ] [ str "Powered by" ]
            picture [] [
                source [ _media "(prefers-color-scheme: dark)"; _srcset darkSrc ]
                img [
                    _src lightSrc
                    _alt "ToolUp Forge"
                    _width "48"
                    _height "48"
                    attr "style" imgStyle
                ]
            ]
        ]

    /// "Powered by ToolUp Forge" footer badge with forge defaults.
    /// Drop straight into your footer markup; no surrounding wrapper
    /// required. Use `poweredByBadgeWith` for custom link target,
    /// asset prefix, or wordmark height.
    let poweredByBadge: XmlNode = poweredByBadgeWith Options.defaults
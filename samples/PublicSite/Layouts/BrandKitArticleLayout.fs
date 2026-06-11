module PublicSite.Layouts.BrandKitArticleLayout

open ToolUp.BrandKit
open ToolUp.BrandKit.Layouts
open ToolUp.PublicRendering

// Phase 92 — the "~20 lines to a brand-correct page" demo: compose a
// ready BrandKit layout (`Layout.article`) with PublicRendering's body
// renderer. The chrome is built once from BrandKit page-chrome
// primitives; the layout function itself is a one-record glue.

let private chrome = {
    Chrome.create "PublicSite" with
        Stylesheets = [ "/brand.css" ]
        Header =
            Some(
                PageChrome.pageHeader {
                    Brand =
                        PageChrome.Wordmark {
                            Stem = "Public"
                            Emphasis = "Site"
                            EmphasisColour = "#6B5FBF"
                            Tail = None
                        }
                    Nav = [ { Label = "Home"; Href = "/" }; { Label = "About"; Href = "/about" } ]
                    Right = []
                }
            )
        Footer =
            Some(
                PageChrome.pageFooter {
                    Copyright = "© 2026 PublicSite"
                    Links = [ { Label = "Terms"; Href = "/terms" } ]
                }
            )
}

let render (page: PublicPage) =
    Layout.article
        {
            chrome with
                Title = page.Title
                Description = Some page.Description
        }
        {
            Eyebrow = page.Collection |> Option.map (fun c -> c.ToUpperInvariant())
            Title = page.Title
            Lede = None
            Meta =
                page.PublishedAt
                |> Option.map (fun d -> Pill.pill (d.ToString "yyyy-MM-dd"))
                |> Option.toList
            Hero = None
            Body = NarrativeLayout.renderBody page
            Aside = None
            Breadcrumb = None
        }
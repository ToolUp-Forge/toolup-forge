module ToolUp.Platform.Tests.InProcess.FelizLayoutAdapterTests

open Expecto
open Giraffe.ViewEngine
open Feliz.ViewEngine
open ToolUp.PublicRendering

/// Phase 92 — Feliz.ViewEngine layout adapter tests. A layout authored
/// in the Feliz DSL (`PublicPage -> ReactElement`) adapts into the
/// Giraffe `XmlNode` registry via `FelizLayout.toGiraffe` /
/// `withFelizLayout`; the page handler's document render must add the
/// doctype exactly once around the adapter's raw-text output.

let private page = {
    Slug = Slug "feliz-demo"
    Title = "Feliz Demo"
    Description = "Rendered by Feliz.ViewEngine"
    Body = ContentBody.Html "<p>ignored — the layout owns the body here</p>"
    Layout = LayoutName "feliz"
    Frontmatter = Map.empty
    PublishedAt = None
    Collection = None
    Status = Published
    Audience = PageAudience.Public
}

/// A representative Feliz-DSL layout: reads `PublicPage` fields,
/// uses fragments, aria props, and CSS-variable inline style — the
/// shared-component subset documented in the evaluation doc.
let private felizLayout (page: PublicPage) : ReactElement =
    Html.html [
        prop.lang "en"
        prop.children [
            Html.head [ prop.children [ Html.title [ prop.text page.Title ] ] ]
            Html.body [
                prop.children [
                    Html.main [
                        prop.id "main"
                        prop.children [
                            Html.h1 [
                                prop.className "feliz-title"
                                prop.style [ style.color "var(--bk-ink)" ]
                                prop.text page.Title
                            ]
                            Html.nav [
                                prop.ariaLabel "Primary"
                                prop.children [ Html.a [ prop.href "/"; prop.text "Home" ] ]
                            ]
                        ]
                    ]
                ]
            ]
        ]
    ]

let tests =
    testList "ToolUp.PublicRendering — Feliz.ViewEngine layout adapter (Phase 92)" [

        testCase "toGiraffe: document render carries exactly one doctype around the Feliz markup"
        <| fun _ ->
            let adapted = FelizLayout.toGiraffe felizLayout

            let html = adapted page |> RenderView.AsString.htmlDocument

            Expect.equal ((html.Split "<!DOCTYPE").Length - 1) 1 "exactly one doctype"
            Expect.isTrue (html.TrimStart().StartsWith "<!DOCTYPE html>") "doctype leads the document"
            Expect.stringContains html "<title>Feliz Demo</title>" "layout reads PublicPage fields"
            Expect.stringContains html "class=\"feliz-title\"" "Feliz className renders"
            Expect.stringContains html "color:var(--bk-ink)" "CSS-variable inline style renders"
            Expect.stringContains html "aria-label=\"Primary\"" "aria props render"

        testCase "withFelizLayout: registers into the same registry as Giraffe layouts"
        <| fun _ ->
            let app =
                PublicRenderingCompose.PublicRenderingServerApp.create ()
                |> PublicRenderingCompose.PublicRenderingServerApp.withLayout (LayoutName "page") (fun p ->
                    html [] [ body [] [ str p.Title ] ])
                |> PublicRenderingCompose.PublicRenderingServerApp.withFelizLayout (LayoutName "feliz") felizLayout

            Expect.equal (Map.count app.Layouts) 2 "both layouts registered"

            let stored = Map.find (LayoutName "feliz") app.Layouts
            let html = stored page |> RenderView.AsString.htmlDocument

            Expect.stringContains html "feliz-title" "the registered layout is the adapted Feliz one"
    ]
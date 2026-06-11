module PublicSite.Layouts.FelizPageLayout

// Phase 92 — Feliz.ViewEngine layout + the shared-component pattern.
//
// The conditional `open` below is the whole trick: a presentational
// component authored against the Feliz DSL compiles BOTH client-side
// (Feliz proper → React, when a Fable SPA includes this file) and
// server-side (Feliz.ViewEngine → HTML string, here). PublicSite is
// SSR-only so only the server branch is exercised; a hybrid
// deployment adds this file to its Client fsproj unchanged. See
// docs/design/feliz-viewengine-evaluation.md for the subset contract
// (presentational markup only — no hooks, no event handlers).
#if FABLE_COMPILER
open Feliz
#else
open Feliz.ViewEngine
#endif

/// Shared presentational component — compiles under Feliz and
/// Feliz.ViewEngine alike.
let heroCard (title: string) (lede: string) =
    Html.section [
        prop.className "feliz-hero"
        prop.children [
            Html.h1 [
                prop.className "feliz-hero-title"
                prop.style [ style.color "var(--bk-ink)" ]
                prop.text title
            ]
            Html.p [ prop.className "feliz-hero-lede"; prop.text lede ]
        ]
    ]

// ─── Server-only below: the PublicPage layout registration shape ────
#if !FABLE_COMPILER
open ToolUp.PublicRendering

/// `PublicPage -> ReactElement` — registered via
/// `PublicRenderingServerApp.withFelizLayout`. The page body (markdown
/// / HTML / Narrative) is rendered by the SDK's Giraffe-side body
/// renderer and embedded as raw HTML, showing the two DSLs composing.
let render (page: PublicPage) : ReactElement =
    let bodyHtml =
        NarrativeLayout.renderBody page
        |> Giraffe.ViewEngine.RenderView.AsString.htmlNode

    Html.html [
        prop.lang "en"
        prop.children [
            Html.head [
                prop.children [
                    Html.meta [ prop.charset "utf-8" ]
                    Html.title [ prop.text page.Title ]
                    Html.meta [ prop.name "description"; prop.content page.Description ]
                ]
            ]
            Html.body [
                prop.children [
                    heroCard page.Title page.Description
                    Html.main [
                        prop.id "main"
                        prop.className "feliz-main"
                        prop.dangerouslySetInnerHTML bodyHtml
                    ]
                ]
            ]
        ]
    ]
#endif
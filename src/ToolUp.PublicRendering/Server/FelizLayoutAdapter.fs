namespace ToolUp.PublicRendering

open Giraffe.ViewEngine

// ─── Phase 92 — Feliz.ViewEngine layout adapter ──────────────────────
//
// Lets a layout authored in the Feliz DSL (`PublicPage ->
// Feliz.ViewEngine.ReactElement`) slot into the same layout registry
// as Giraffe.ViewEngine layouts. The adapter renders the element to an
// HTML string with `Render.htmlView` (which emits NO doctype) and
// wraps it in `rawText`, so the page handler's
// `RenderView.AsString.htmlDocument` call — which prepends the single
// `<!DOCTYPE html>` — and the Phase 111 head-metadata injection both
// work unchanged on the result. Giraffe.ViewEngine stays the
// zero-extra-dependency default (GP 2); Feliz layouts are opt-in via
// `PublicRenderingServerApp.withFelizLayout`.
//
// Why Feliz.ViewEngine: a deployment with a Fable SPA can author
// presentational components once against the Feliz DSL and compile
// them both client-side (Feliz proper → React) and server-side
// (Feliz.ViewEngine → HTML string) behind an
// `#if FABLE_COMPILER` conditional `open` — the shared-component
// story evaluated in `docs/design/feliz-viewengine-evaluation.md`.

[<RequireQualifiedAccess>]
module FelizLayout =
    /// Adapt a Feliz.ViewEngine layout to the Giraffe `XmlNode` shape
    /// the layout registry stores. The returned node is the rendered
    /// HTML wrapped as raw text — downstream document rendering adds
    /// the doctype exactly once.
    let toGiraffe (layout: PublicPage -> Feliz.ViewEngine.ReactElement) : PublicPage -> XmlNode =
        fun page -> layout page |> Feliz.ViewEngine.Render.htmlView |> rawText
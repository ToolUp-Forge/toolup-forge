// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Reporting.HtmlPdf

open System.Text
open Microsoft.Playwright
open ToolUp.Reporting

// ─── Phase 126 — HTML→PDF renderer ───────────────────────────────
//
// The other PDF road beside the (deferred, QuestPDF-shaped)
// code-layout sub-companion: render an HTML template — print CSS
// respected — to paginated PDF via headless Chromium.
//
// **Format registration decision** (coexistence with the shipped
// `HtmlRenderer`): this renderer claims `TemplateFormat.Pdf`. A
// template targeting it declares `Format = Pdf` and carries a
// print-CSS **HTML body** — exactly the "renderer-specific shape"
// the `ReportTemplate.Body` doc reserves for `Pdf`. The shipped
// `HtmlRenderer` keeps `Html` (HTML in, HTML out), so the two
// resolve unambiguously through `RendererRegistry`. A future
// QuestPDF renderer would also claim `Pdf` with a different body
// shape — the registry's last-registered-wins semantics make that a
// per-deployment choice, not a conflict.
//
// Placeholder semantics are inherited wholesale: the body is filled
// through the shipped `HtmlRenderer` (same escaping, `_raw` bypass,
// `Table` → <table>, `Image` → data-URL <img> — data URLs print
// without any network fetch), then printed.

[<RequireQualifiedAccess>]
module HtmlPdfReportRenderer =

    [<Literal>]
    let Name = "HtmlPdfReportRenderer"

    let private toPdfOptions (options: PdfRenderOptions) : PagePdfOptions =
        let pdf = PagePdfOptions(PrintBackground = options.PrintBackground)

        options.Format |> Option.iter (fun format -> pdf.Format <- format)

        options.Scale
        |> Option.iter (fun scale -> pdf.Scale <- System.Nullable(float32 scale))

        pdf.PreferCSSPageSize <- options.PreferCssPageSize

        options.Margins
        |> Option.iter (fun margins ->
            pdf.Margin <-
                Margin(Top = margins.Top, Bottom = margins.Bottom, Left = margins.Left, Right = margins.Right))

        match options.HeaderTemplate, options.FooterTemplate with
        | None, None -> ()
        | header, footer ->
            pdf.DisplayHeaderFooter <- true
            // Chromium renders both bands once the flag is on; an
            // empty template keeps the unused band blank.
            pdf.HeaderTemplate <- header |> Option.defaultValue "<span></span>"
            pdf.FooterTemplate <- footer |> Option.defaultValue "<span></span>"

        pdf

    let internal renderHtmlToPdf (host: BrowserHost) (options: PdfRenderOptions) (html: string) =
        host.WithPage(fun page -> task {
            let waitUntil =
                if options.WaitForNetworkIdle then
                    WaitUntilState.NetworkIdle
                else
                    WaitUntilState.Load

            do! page.SetContentAsync(html, PageSetContentOptions(WaitUntil = waitUntil))
            return! page.PdfAsync(toPdfOptions options)
        })

    /// Create the renderer with explicit configuration. The browser
    /// launches lazily on the first render (GP 13 — composing the
    /// renderer costs nothing until a PDF is actually produced).
    let createWith (config: HtmlPdfRendererConfig) : IReportRenderer =
        let host = BrowserHost(config.BrowserIdleTimeout, config.LaunchArgs)

        // Substitution semantics inherited from the shipped
        // HtmlRenderer — one source of truth for placeholder
        // behaviour across the Html and Pdf roads.
        let htmlRenderer = HtmlRenderer.create ()

        { new IReportRenderer with
            member _.SupportedFormats = [ Pdf ]
            member _.Name = Name

            member _.Render(template, values) = async {
                let! filled = htmlRenderer.Render(template, values)

                match filled with
                | Error error -> return Error error
                | Ok htmlBytes ->
                    try
                        let html = Encoding.UTF8.GetString htmlBytes

                        let! pdf = renderHtmlToPdf host config.RenderOptions html |> Async.AwaitTask

                        return Ok pdf
                    with ex ->
                        // Browser crash mid-render (or launch failure)
                        // lands here as a typed error — never a hung
                        // Async. The host relaunches on the next
                        // render.
                        return Error(RendererFailure(Name, ex.Message))
            }
        }

    /// Create the renderer with default options (print-CSS-wins, warm
    /// browser with a 5-minute idle timeout). Register it against the
    /// reporting registry at composition time:
    /// `registry |> ReportingCompose.withRenderer (HtmlPdfReportRenderer.create ())`.
    let create () : IReportRenderer =
        createWith HtmlPdfRendererConfig.defaults
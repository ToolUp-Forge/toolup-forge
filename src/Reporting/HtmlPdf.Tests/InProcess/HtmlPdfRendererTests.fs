// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Reporting.HtmlPdf.Tests.HtmlPdfRendererTests

open System
open System.Text
open Expecto
open ToolUp.Reporting
open ToolUp.Reporting.HtmlPdf
open ToolUp.Reporting.HtmlPdf.Tests

// ─── PDF assertions via PdfPig ───────────────────────────────────

let private pdfPages (bytes: byte[]) : string list =
    use doc = UglyToad.PdfPig.PdfDocument.Open bytes

    [
        for page in doc.GetPages() do
            page.GetWords() |> Seq.map _.Text |> String.concat " "
    ]

/// Text projection for the shared contract pack: all pages' extracted
/// text, joined.
let private pdfText (bytes: byte[]) : string = pdfPages bytes |> String.concat "\n"

let private pdfPageSize (bytes: byte[]) : float * float =
    use doc = UglyToad.PdfPig.PdfDocument.Open bytes
    let page = doc.GetPage 1
    float page.Width, float page.Height

let private mkTemplate (body: string) (placeholders: PlaceholderSchema list) : ReportTemplate = {
    Id = "fixture-template"
    DisplayName = "Fixture"
    Format = Pdf
    Body = Encoding.UTF8.GetBytes body
    Placeholders = placeholders
    Version = 1
}

let private render (renderer: IReportRenderer) template values =
    renderer.Render(template, values) |> Async.RunSynchronously

let private expectOk result =
    match result with
    | Ok(bytes: byte[]) -> bytes
    | Error e -> failtestf "expected Ok, got %s" (RenderError.toMessage e)

// A5 portrait in PDF points (ISO 216: 148mm × 210mm).
let private a5WidthPt, a5HeightPt = 419.5, 595.3

let private printCssFixture =
    """<!doctype html>
<html>
<head>
<style>
@page { size: A5; margin: 15mm; }
.page-break { page-break-after: always; }
h1 { font-family: sans-serif; }
</style>
</head>
<body>
<h1>FIRST-PAGE-MARKER {{title}}</h1>
<div class="page-break"></div>
<h1>SECOND-PAGE-MARKER</h1>
</body>
</html>"""

// ─── Tests ───────────────────────────────────────────────────────

let private ungated =
    testList "without a browser" [
        testCase "validation errors surface before any browser launch"
        <| fun () ->
            // The factory does not launch Chromium (lazy host), so the
            // placeholder-validation error path works on machines with
            // no browser installed at all.
            let renderer = HtmlPdfReportRenderer.create ()

            let template =
                mkTemplate "Hello {{name}}" [
                    {
                        Key = "name"
                        DisplayName = "Name"
                        Kind = Text
                        Required = true
                    }
                ]

            match render renderer template Map.empty with
            | Error(MissingRequiredPlaceholder "name") -> ()
            | other -> failtestf "expected MissingRequiredPlaceholder, got %A" other

        testCase "GP 1 — no Playwright reference reaches core packages (strip-imports proof)"
        <| fun () ->
            let referencesOf (t: Type) =
                t.Assembly.GetReferencedAssemblies() |> Array.map _.Name

            for coreType in [ typeof<TemplateFormat>; typeof<ToolUp.Platform.ILogger> ] do
                Expect.isFalse
                    (referencesOf coreType
                     |> Array.exists (fun name -> name <> null && name.StartsWith "Microsoft.Playwright"))
                    (sprintf "%s's assembly must not reference Playwright" (coreType.Assembly.GetName().Name))
    ]

let private gatedRendering =
    BrowserGate.gated "with a browser" [
        // Shared contract conformance, projected through PDF text
        // extraction (raw PDF bytes carry compressed streams +
        // creation timestamps, so neither substring search nor byte
        // equality is meaningful).
        ToolUp.Platform.Tests.Contracts.IReportRendererContract.testsWith
            "HtmlPdfReportRenderer"
            HtmlPdfReportRenderer.create
            Pdf
            pdfText

        testCase "print CSS drives pagination, page size and footer"
        <| fun () ->
            let renderer =
                HtmlPdfReportRenderer.createWith {
                    HtmlPdfRendererConfig.defaults with
                        RenderOptions = {
                            PdfRenderOptions.defaults with
                                FooterTemplate =
                                    Some
                                        "<div style=\"font-size:10px;width:100%;text-align:center;\"><span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span></div>"
                        }
                }

            let template =
                mkTemplate printCssFixture [
                    {
                        Key = "title"
                        DisplayName = "Title"
                        Kind = Text
                        Required = true
                    }
                ]

            let bytes =
                render renderer template (Map [ "title", TextValue "Quarterly" ]) |> expectOk

            let pages = pdfPages bytes
            Expect.equal pages.Length 2 "page-break-after produced two pages"
            Expect.stringContains pages[0] "FIRST-PAGE-MARKER Quarterly" "first page content + substitution"
            Expect.stringContains pages[1] "SECOND-PAGE-MARKER" "second page content"
            Expect.stringContains pages[1] "2 of 2" "footer page numbering rendered"

            // @page size A5 wins (PreferCssPageSize default true).
            let width, height = pdfPageSize bytes
            Expect.isTrue (abs (width - a5WidthPt) < 3.0) (sprintf "A5 width from @page CSS, got %f" width)
            Expect.isTrue (abs (height - a5HeightPt) < 3.0) (sprintf "A5 height from @page CSS, got %f" height)

        testCase "renderer failure is typed, never thrown"
        <| fun () ->
            let renderer =
                HtmlPdfReportRenderer.createWith {
                    HtmlPdfRendererConfig.defaults with
                        RenderOptions = {
                            PdfRenderOptions.defaults with
                                Format = Some "NotAPaperSize"
                        }
                }

            let template = mkTemplate "<p>hello</p>" []

            match render renderer template Map.empty with
            | Error(RendererFailure("HtmlPdfReportRenderer", _)) -> ()
            | other -> failtestf "expected RendererFailure, got %A" other

        testCase "a dead browser relaunches on the next render (crash recovery)"
        <| fun () ->
            let host = BrowserHost(TimeSpan.FromMinutes 5.0, [])

            try
                let renderOnce () =
                    HtmlPdfReportRenderer.renderHtmlToPdf host PdfRenderOptions.defaults "<p>RECOVERY-MARKER</p>"
                    |> _.GetAwaiter().GetResult()

                let first = renderOnce ()
                Expect.stringContains (pdfText first) "RECOVERY-MARKER" "first render OK"
                Expect.isTrue host.HasLiveBrowserForTests "browser is warm"

                // Simulate the crash: the process dies, the host's
                // handle goes disconnected.
                host.KillBrowserForTests().GetAwaiter().GetResult()
                Expect.isFalse host.HasLiveBrowserForTests "browser is dead"

                let second = renderOnce ()
                Expect.stringContains (pdfText second) "RECOVERY-MARKER" "relaunched render OK"
                Expect.isTrue host.HasLiveBrowserForTests "browser relaunched"
            finally
                host.Shutdown()
    ]

let tests =
    testList "HtmlPdf" [
        ungated
        // Browser tests share warm Chromium instances; run them
        // sequenced so lifecycle assertions (crash recovery) don't
        // interleave with concurrent renders.
        testSequenced gatedRendering
    ]
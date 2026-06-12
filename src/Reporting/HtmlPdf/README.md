# ToolUp.Reporting.HtmlPdf

HTML→PDF rendering sub-companion for `ToolUp.Reporting`: render an **HTML report
template — print CSS respected — to paginated PDF** via Playwright / headless
Chromium, through the standard `IReportRenderer` / `RendererRegistry` path.

This is the second PDF road beside the (deferred, QuestPDF-shaped) code-layout
sub-companion, and they serve different template authors: QuestPDF templates are
*code layouts*; this renderer takes the **HTML your `HtmlRenderer` templates
already are** and prints them. `@page` size and margins, page breaks, and
header/footer bands all behave as the CSS says.

## Quick start

```fsharp
open ToolUp.Reporting
open ToolUp.Reporting.HtmlPdf

// Compose time — register beside the zero-dep defaults:
let registry =
    ReportingCompose.buildDefaultRegistry ()
    |> ReportingCompose.withRenderer (HtmlPdfReportRenderer.create ())

// Template: Format = Pdf, body = print-CSS HTML with {{placeholders}}.
let template = {
    Id = "quarterly-report"
    DisplayName = "Quarterly report"
    Format = Pdf
    Body = File.ReadAllBytes "quarterly.html"
    Placeholders = [ { Key = "period"; DisplayName = "Period"; Kind = Text; Required = true } ]
    Version = 1
}

// Render through the standard path — PDF bytes out.
let! result = registry.TryResolve(Pdf).Value.Render(template, Map [ "period", TextValue "Q2 2026" ])
```

## Format registration (how this coexists with `HtmlRenderer`)

This renderer claims **`TemplateFormat.Pdf`**. A template targeting it declares
`Format = Pdf` and carries a print-CSS **HTML body** — exactly the
"renderer-specific shape" `ReportTemplate.Body` reserves for `Pdf`. The shipped
`HtmlRenderer` keeps `Html` (HTML in, HTML out), so the registry resolves the two
unambiguously. A future QuestPDF renderer would also claim `Pdf` with a different
body shape; `RendererRegistry`'s last-registered-wins makes that a per-deployment
choice.

Placeholder semantics are inherited wholesale — the body is filled through the
shipped `HtmlRenderer` (same escaping, `_raw` bypass, `Table` → `<table>`,
`Image` → data-URL `<img>`, which prints with no network fetch) and then printed.

## Print-CSS fidelity and options

Defaults are **print-CSS-wins**: a template that declares `@page { size: A5;
margin: 15mm }` renders exactly so (`PreferCssPageSize` defaults true), page
breaks honour `page-break-*` / `break-*` rules, and `PrintBackground` is on.
`PdfRenderOptions` covers templates that don't speak for themselves: paper
`Format`, `Margins`, `Scale`, and Chromium `HeaderTemplate` / `FooterTemplate`
bands (with the `pageNumber` / `totalPages` / `date` / `title` span classes).

## Browser lifecycle

Chromium launches **lazily on the first render** (composing the renderer costs
nothing — GP 13), stays warm across renders (launch is seconds-class; a warm
render is milliseconds-class), closes after `BrowserIdleTimeout` (default 5
minutes), and **relaunches transparently after a crash**: a render against a dead
browser fails as a typed `RenderError` (never a hung Async) and the next render
brings a fresh instance up.

## Deployment-image implications (read before composing)

This sub-companion is for deployments that **accept a browser in the image**. If
yours can't, use the QuestPDF road instead — that is the entire trade between the
two PDF renderers.

- **Chromium must exist in the container.** The NuGet package ships the driver,
  not the browser. Either run `playwright install chromium` (or
  `playwright install --with-deps chromium` on Debian/Ubuntu to pull the OS
  libraries) during image build, or base the runtime stage on the official
  `mcr.microsoft.com/playwright/dotnet` image which has both preinstalled.
- **Image-size cost:** headless Chromium plus its OS dependencies adds roughly
  300–400 MB to a Linux image. Budget for it deliberately.
- **`PLAYWRIGHT_BROWSERS_PATH`:** set it when you install browsers into a custom
  location (e.g. a shared layer) so the driver finds them at runtime; otherwise
  the default per-user cache path applies.
- **Sandbox posture:** Chromium's sandbox cannot run as root without user
  namespaces. Prefer a non-root container user (the official image does this).
  Only if your platform forces root, pass
  `LaunchArgs = [ "--no-sandbox" ]` — and treat that as accepting reduced
  process isolation for whatever HTML you render.
- **Trust posture:** templates are operator-authored. This sub-companion does no
  HTML sanitisation — do not feed it untrusted end-user HTML, particularly in a
  `--no-sandbox` container.

## Testing

The contract + fixture pack lives in `src/Reporting/HtmlPdf.Tests/` and is
browser-gated: on a checkout without a Playwright-managed Chromium the rendering
arms report **Pending, not Failed** (install with
`pwsh <test-output>/playwright.ps1 install chromium` to run them). Validation
error paths and the strip-imports proof run everywhere — the browser is lazy, so
no test needs it until something actually prints.

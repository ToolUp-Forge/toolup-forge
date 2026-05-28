namespace ToolUp.Reporting

// ─── Phase 23 — IReportRenderer interface ────────────────────────────
//
// Renderer abstraction. One impl per format (or one impl handling
// multiple formats). The companion's default `MarkdownRenderer` and
// `HtmlRenderer` cover the zero-dep formats; `Pdf` / `Docx` / `Xlsx`
// / `Pptx` ship in sub-companions and register against the same
// interface.
//
// Six-rule portability audit (Phase 9c, Guiding Principle 12):
//   1. Identity by value      — `TemplateId: string`, `Map<string,
//                               PlaceholderValue>` for values; the
//                               output is `byte[]`. No live handles.
//   2. Async at every boundary — `Render` returns `Async<Result<...>>`.
//   3. Retry/supervision as data — failures flow through `RenderError`
//                                  DU; no callbacks. Renderers that
//                                  call retry-able external services
//                                  surface their RetryPolicy as a
//                                  config field.
//   4. Stateless between calls — implementations cache parsed templates
//                                but the contract assumes nothing
//                                survives between calls. A grain that
//                                deactivates re-parses correctly.
//   5. No cross-shard ordering — `Render` produces the same bytes for
//                                the same (template, values) tuple
//                                regardless of which node served it.
//   6. Precision at lower bound — n/a (no time semantics).

type IReportRenderer =
    /// Formats this renderer claims. The compose-time registry uses
    /// this to route render requests; multiple renderers claiming the
    /// same format are allowed (last-registered wins, log a warning
    /// at compose time).
    abstract SupportedFormats: TemplateFormat list

    /// Stable name for telemetry / audit / error messages
    /// (`"MarkdownRenderer"`, `"PdfRenderer"`, etc.).
    abstract Name: string

    /// Render the template with the supplied placeholder values.
    /// Returns the rendered bytes on success or a typed `RenderError`
    /// on failure.
    abstract Render:
        template: ReportTemplate * values: Map<string, PlaceholderValue> -> Async<Result<byte[], RenderError>>
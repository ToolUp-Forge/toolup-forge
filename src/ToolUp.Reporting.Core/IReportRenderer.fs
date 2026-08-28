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

// ─── Structural narrative expansion (Phase 534, closing 575's spillover) ──
//
// `PlaceholderValue.NarrativeValue` is resolved by the report API
// handler before any renderer runs — that is the Phase 564 disclosure
// export door, and it must stay first. What the handler ALSO did was
// project every resolved narrative to text, which meant the one format
// that had gained native Word structures (Phase 575's
// `NarrativeOoxml` projection, reached through
// `DocxReportRenderer.createWith`) was the one format the shipped API
// path denied them to: a narrative exported as `.docx` arrived as
// flattened markdown prose.
//
// The fix cannot be "keep the `NarrativeValue` when the format is
// `Docx`". Format does not decide it — the RENDERER does. A deployment
// composing some other `Docx` renderer, one that never learned about
// this case, would receive a value `PlaceholderSubstitution.validate`
// reads as a kind mismatch, and a working deployment would break on an
// SDK upgrade it did not ask for.
//
// So the renderer declares it. This is a SEPARATE interface rather than
// a member on `IReportRenderer`, and deliberately: adding an abstract
// member would break every implementation in and out of tree, including
// ones forge cannot see, to express something almost none of them have
// an opinion about. A renderer that does not implement this interface
// is treated exactly as before (GP 11).

/// Declared by a renderer that expands a `PlaceholderValue.NarrativeValue`
/// into the output format's own structures — Word headings, tables,
/// numbered lists — rather than consuming it as text.
///
/// The report API handler consults this AFTER the disclosure door has
/// run: what reaches a structural renderer is the redacted document,
/// including the "Withheld values" section the door appends, which is
/// an ordinary `NarrativeSection` and so survives the projection like
/// any other content. The door's position in the pipeline is the
/// invariant; which shape the value arrives in is not.
type IStructuralNarrativeRenderer =
    /// The subset of this renderer's `SupportedFormats` for which a
    /// narrative is expanded structurally. A renderer serving several
    /// formats may expand for some and flatten for the rest, so this is
    /// a list rather than a flag — and it is *this renderer's* claim,
    /// never an inference from the format alone.
    abstract StructuralNarrativeFormats: TemplateFormat list

// ─── Narrative component registry (Phase 534, closing 575's spillover) ──
//
// A narrative `Component(name, props)` block is resolved by a
// caller-supplied registry: Reporting must not name a rendering
// companion (GP 1), so the chart renderer that draws one is a
// composition-root concern. Phase 575 defined that registry inside the
// Docx sub-companion, where `ReportingCompose` — which cannot reference
// a sub-companion without inverting the dependency — could not offer a
// place to declare it. Every deployment therefore got the no-registry
// constructor and every component took its data-table degradation.
//
// The registry shape is renderer-neutral (a name and a string prop bag
// in, a picture or a shrug out), so it belongs here, in the tier both
// the compose surface and the sub-companions can see. The
// sub-companion adapts it to its own projection type; see
// `DocxReportRenderer.createWithComponents`.

/// What a component renderer produced for one `Component` block.
type ReportComponentResult =
    /// An SVG document. Formats that embed vector graphics do so
    /// natively; the rest fall back to their own degradation.
    | ComponentSvg of svg: string
    /// A raster image and its MIME type.
    | ComponentImage of bytes: byte[] * mimeType: string
    /// Nothing was produced. The block takes the same
    /// data-table / alt-text degradation an unregistered component
    /// takes — never a silent drop, and never an error.
    | ComponentFallback

/// Resolves a narrative `Component(name, props)` block to a picture, or
/// declines. Declining is a first-class answer: a registry that serves
/// charts and is handed a map block should return `ComponentFallback`,
/// not raise.
type ReportComponentRegistry = string -> Map<string, string> -> ReportComponentResult

module ReportComponentRegistry =
    /// The registry a deployment that registers nothing has: every
    /// component degrades. This is the default the compose surface
    /// carries, so composing the subscription/report surfaces without
    /// mentioning components behaves exactly as Phase 575 shipped
    /// (GP 11).
    let empty: ReportComponentRegistry = fun _ _ -> ComponentFallback
namespace ToolUp.Reporting

open System

// ─── Phase 23 — Reporting public types ───────────────────────────────
//
// Shared types for the Reporting companion. Lives in Core so consumers
// (templates authored elsewhere, renderers in sub-companions, the
// future Forms-driven template designer, the Excel converter portal's
// runtime export wiring) reference one type module.

/// Format the renderer produces. Each value maps to one (or more)
/// `IReportRenderer` impls — the platform's `MarkdownRenderer` /
/// `HtmlRenderer` defaults handle `Markdown` + `Html` in process;
/// `Pdf` / `Docx` / `Xlsx` / `Pptx` ship in sub-companions and are
/// resolved through the renderer registry only when the matching
/// PackageReference is present.
type TemplateFormat =
    | Markdown
    | Html
    | Pdf
    | Docx
    | Xlsx
    | Pptx

/// Stable identifier for a template. Persisted alongside the template
/// blob in `IEntityStore`; included in `ReportRendered` audit events.
type TemplateId = string

/// Placeholder kinds the renderer surface knows how to fill.
/// Unsupported kinds for a given format surface as a `RenderError` at
/// render time rather than a compile-time check — different renderers
/// support different shapes (e.g. `Markdown` doesn't support
/// `Image`-by-bytes; emit a base64 data URL instead).
type PlaceholderKind =
    /// Plain string; HTML-escaped automatically by `HtmlRenderer`.
    | Text
    /// Number with optional culture-aware formatting hint
    /// (`"N0"` / `"C2"` etc — handed to `String.Format`).
    | Number of formatHint: string option
    /// Date with optional format string (e.g. `"yyyy-MM-dd"`).
    | Date of formatHint: string option
    /// Inline image — bytes plus MIME type. Not supported by every
    /// format; renderers reject with `UnsupportedPlaceholderKind`.
    | Image of mimeType: string
    /// Tabular data — `ColumnSchema` declares the columns; row data
    /// supplied as a list of `Map<columnKey, PlaceholderValue>`.
    | Table of columns: ColumnSchema list

/// One column in a `Table`-shaped placeholder.
and ColumnSchema = {
    /// Map key the row's value is keyed by.
    Key: string
    /// Header text rendered above the column.
    DisplayName: string
    /// Cell-content kind. Nesting `Table` inside `Table` is rejected.
    Kind: PlaceholderKind
}

/// Schema entry on a `ReportTemplate`. Each placeholder declares its
/// key (the `{{key}}` token in the template body), display name (for
/// any future template-designer UI), kind, and whether the value is
/// required at render time.
type PlaceholderSchema = {
    Key: string
    DisplayName: string
    Kind: PlaceholderKind
    Required: bool
}

/// Concrete value supplied at render time. The DU mirrors
/// `PlaceholderKind` one-for-one. Supplying a value whose case
/// doesn't match the schema's kind surfaces as `PlaceholderTypeMismatch`.
type PlaceholderValue =
    | TextValue of string
    | NumberValue of double
    | DateValue of DateTimeOffset
    | ImageValue of bytes: byte[] * mimeType: string
    | TableValue of rows: Map<string, PlaceholderValue> list

/// A typed template — the body bytes + schema + identifying metadata.
/// Persisted via `IReportTemplateStore` (which delegates to
/// `IEntityStore` for versioning).
type ReportTemplate = {
    Id: TemplateId
    DisplayName: string
    Format: TemplateFormat
    /// Raw template bytes. For `Markdown` / `Html` the body is plain
    /// UTF-8 text; for `Docx` / `Xlsx` / `Pptx` it's a vendor-format
    /// document containing `{{key}}` tokens; for `Pdf` it's a renderer-
    /// specific shape (QuestPDF DSL captured as JSON, for example).
    Body: byte[]
    /// Typed slots the renderer fills.
    Placeholders: PlaceholderSchema list
    /// Monotonic version stamp managed by `IEntityStore`. Callers
    /// don't set this — it's overwritten on save.
    Version: int
}

/// Render-time failures. Each case carries enough context for the API
/// handler to surface a meaningful 4xx response.
type RenderError =
    /// The format isn't supported by any registered renderer.
    | NoRendererForFormat of TemplateFormat
    /// A required placeholder wasn't supplied.
    | MissingRequiredPlaceholder of key: string
    /// A supplied placeholder's value type doesn't match its schema.
    | PlaceholderTypeMismatch of key: string * expected: string * got: string
    /// A renderer-specific failure (template parse error, vendor SDK
    /// exception, etc). Carries the renderer's name and a short
    /// human-readable reason.
    | RendererFailure of renderer: string * reason: string
    /// A placeholder kind isn't supported by this renderer (e.g.
    /// `Image`-by-bytes against the bare `MarkdownRenderer`).
    | UnsupportedPlaceholderKind of renderer: string * key: string * kind: string

module RenderError =
    let toMessage =
        function
        | NoRendererForFormat fmt -> $"No renderer registered for format %A{fmt}"
        | MissingRequiredPlaceholder k -> $"Required placeholder '{k}' not supplied"
        | PlaceholderTypeMismatch(k, e, g) -> $"Placeholder '{k}': expected {e}, got {g}"
        | RendererFailure(r, reason) -> $"Renderer '{r}' failed: {reason}"
        | UnsupportedPlaceholderKind(r, k, kind) ->
            $"Renderer '{r}' does not support placeholder kind '{kind}' (key: {k})"
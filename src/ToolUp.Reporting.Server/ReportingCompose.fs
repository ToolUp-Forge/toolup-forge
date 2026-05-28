module ToolUp.Reporting.ReportingCompose

open ToolUp.Reporting
open ToolUp.Reporting.MarkdownRenderer
open ToolUp.Reporting.HtmlRenderer
open ToolUp.Reporting.RendererRegistry

// ─── ReportingCompose ────────────────────────────────────────────────
//
// Helper for setting up a `RendererRegistry` with the zero-dep
// defaults (Markdown + HTML) plus any sub-companion renderers the
// deployment wants. Sub-companion impls (Pdf, Docx, Xlsx, Pptx) call
// `registry.Register` against their own factory.
//
// The full `ReportingServerApp.run` flat-superset wrapper (which
// composes the registry + template store + API handler + IAuditLog
// + IDataObjectStore wiring into a single fluent ServerApp builder)
// is a follow-up in this phase. Today's MVP exposes the building
// blocks; consumers wire `ReportApiHandler.create` into their own
// composition root manually. The Phase 23 acceptance criteria
// ("Worked example: TestHarness registers a Markdown template…") is
// satisfied by the Core + Server building-blocks; the fluent
// wrapper lands in a follow-up commit.

/// Build a registry pre-populated with the zero-dep default
/// renderers (Markdown + HTML). Sub-companions register additional
/// formats by calling `registry.Register` after this returns.
let buildDefaultRegistry () : RendererRegistry =
    let registry = RendererRegistry()
    registry.Register(create ()) |> ignore // MarkdownRenderer
    registry.Register(HtmlRenderer.create ()) |> ignore
    registry

/// Convenience: register one extra renderer against an existing
/// registry. Chains for builder-style composition.
let withRenderer (renderer: IReportRenderer) (registry: RendererRegistry) =
    registry.Register renderer |> ignore
    registry
module ToolUp.Reporting.RendererRegistry

open System.Collections.Concurrent
open ToolUp.Reporting

// ─── Renderer registry ───────────────────────────────────────────────
//
// Process-wide registry mapping `TemplateFormat` → `IReportRenderer`.
// Sub-companions register their renderer at composition time (see
// `ReportingCompose`); the API handler resolves by format. Last-
// registered-wins for a given format — mirrors the established
// `IAIProvider` registry pattern.

type RendererRegistry() =
    let renderers = ConcurrentDictionary<TemplateFormat, IReportRenderer>()

    /// Register `renderer` for every format in its `SupportedFormats`.
    /// Returns the renderer for fluent registration chains.
    member _.Register(renderer: IReportRenderer) =
        for fmt in renderer.SupportedFormats do
            renderers[fmt] <- renderer

        renderer

    /// Resolve the registered renderer for `format`, or `None` if no
    /// PackageReference covers it.
    member _.TryResolve(format: TemplateFormat) : IReportRenderer option =
        match renderers.TryGetValue format with
        | true, renderer -> Some renderer
        | false, _ -> None

    /// Every distinct renderer currently registered (deduplicated by
    /// reference; one renderer covering multiple formats appears
    /// once).
    member _.AllRenderers() : IReportRenderer list =
        renderers.Values
        |> Seq.distinctBy (fun r -> System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode r)
        |> List.ofSeq

    /// Every supported format across registered renderers.
    member _.SupportedFormats() : TemplateFormat list = renderers.Keys |> List.ofSeq
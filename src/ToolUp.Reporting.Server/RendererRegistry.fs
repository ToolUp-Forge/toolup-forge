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
    ///
    /// The raw lookup. `Route` is what the render path uses — see below
    /// for why the two are not the same question.
    member _.TryResolve(format: TemplateFormat) : IReportRenderer option =
        match renderers.TryGetValue format with
        | true, renderer -> Some renderer
        | false, _ -> None

    /// Phase 647 — resolve `format` for the Track-A render path, or the
    /// typed refusal that explains why it did not resolve.
    ///
    /// Two refusals, and the distinction is the point: a format nothing
    /// registered is `NoRendererForFormat` ("add the PackageReference"),
    /// while a deck-tier-served format is `FormatServedByDeckTier`
    /// ("decks come from elsewhere; there is no package to add"). Sending
    /// a consumer looking for a package that deliberately does not exist
    /// is the failure this routing exists to prevent.
    ///
    /// The deck-tier decision is `DeckExport.route`'s, not this type's —
    /// one predicate, so a second resolve site cannot answer differently.
    member this.Route(format: TemplateFormat) : Result<IReportRenderer, RenderError> =
        DeckExport.route format (this.TryResolve format)

    /// Every distinct renderer currently registered (deduplicated by
    /// reference; one renderer covering multiple formats appears
    /// once).
    member _.AllRenderers() : IReportRenderer list =
        renderers.Values
        |> Seq.distinctBy (fun r -> System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode r)
        |> List.ofSeq

    /// Every supported format across registered renderers.
    member _.SupportedFormats() : TemplateFormat list = renderers.Keys |> List.ofSeq
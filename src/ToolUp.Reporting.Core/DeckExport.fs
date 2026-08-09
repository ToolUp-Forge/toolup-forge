// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 647 — the deck-export routing posture.
///
/// `TemplateFormat` enumerates `Pptx`, and this SDK has never had a
/// renderer behind it. There are two ways to resolve that, and they are
/// not equally good:
///
///   1. **Fill it.** Ship a token-fill deck renderer — text into shapes,
///      a picture per chart — and the gap closes today.
///   2. **Route it.** Declare that deck output is produced by a
///      downstream document-emission tier, and make a Track-A render
///      request for the format refuse with a typed error that names
///      where decks come from.
///
/// This module is (2), and the reason is that a deck is not a rendered
/// template. A useful deck is a REGENERABLE PROJECTION of governed
/// results: rebuild it when a number is superseded and the slides move
/// with it, because the slides reference the results rather than
/// containing a screenshot of them. That property lives in the document
/// tier that emits typed deck parts, and a commodity renderer here would
/// obsolete itself the day that tier ships — while, in the meantime,
/// being the thing consumers built against.
///
/// So the posture is stated once, structurally:
///
///   * `DeckExport.servedFormats` is the (currently single-member) set of
///     formats this SDK deliberately does not render.
///   * `DeckExport.route` is the ONE decision function; the renderer
///     registry's `Route` calls it, so "which formats are deck-tier
///     served" is not a condition duplicated at each resolve site.
///   * The refusal is a VALUE, not an exception. A deployment that
///     registers its own deck renderer is not broken at compose time —
///     it is told, per render, that the Track-A path does not route
///     there. Nothing here reaches into a consumer's composition.
///
/// What this SDK contributes to a deck is the other half of the seam:
/// deterministic, provenance-stamped chart artifacts any export tier can
/// embed — see [`ChartArtifact`](ChartArtifact.fs).
namespace ToolUp.Reporting

[<RequireQualifiedAccess>]
module DeckExport =

    /// The formats served by the downstream deck export tier rather than
    /// by an `IReportRenderer` here.
    ///
    /// A list rather than a predicate over a wildcard: a reader can see
    /// the whole of what is routed away, and adding to it is a visible
    /// diff rather than a widened condition.
    let servedFormats: TemplateFormat list = [ Pptx ]

    /// Whether `format` is served by the deck tier.
    let isServed (format: TemplateFormat) : bool = List.contains format servedFormats

    /// The single routing decision, shared by every resolve site.
    ///
    /// `resolved` is what the registry found for the format (`None` when
    /// nothing is registered). The deck-tier check comes FIRST and does
    /// not consult it: routing away is unconditional, so a deck renderer
    /// that happened to be registered cannot silently become the answer
    /// — which is exactly the "never token-filled" property, expressed as
    /// control flow rather than as a convention someone has to keep.
    let route (format: TemplateFormat) (resolved: 'renderer option) : Result<'renderer, RenderError> =
        if isServed format then
            Error(FormatServedByDeckTier format)
        else
            match resolved with
            | Some renderer -> Ok renderer
            | None -> Error(NoRendererForFormat format)
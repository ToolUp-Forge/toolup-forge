# Reporting

*`ToolUp.Reporting.Core` / `ToolUp.Reporting.Server`.* Typed-template
document generation: a `ReportTemplate` (body bytes + a declared
placeholder schema) is rendered against a `Map<string, PlaceholderValue>`
by an `IReportRenderer` resolved from the template's `TemplateFormat`.

> Companion package — a deployment that composes no reporting surface
> pays nothing (GP 13).

## The render path

```fsharp skip=fragment
let registry =
    ReportingCompose.buildDefaultRegistry ()          // Markdown + Html, zero-dep
    |> ReportingCompose.withRenderer myPdfRenderer    // sub-companion renderers

let api =
    ReportApiHandler.createWithDisclosureGate gate principal templateStore registry storeBlob audit config scopeId
```

`IReportApi.Render` resolves the template, **routes** its format (below),
resolves any `NarrativeValue` placeholders through the `FactExport`
disclosure door ([facts.md](facts.md)), runs the renderer, and returns the
bytes inline or as a stored blob by byte budget.

| Format | Rendered by |
|---|---|
| `Markdown` / `Html` | in-process, zero-dependency renderers in `ToolUp.Reporting.Core` |
| `Pdf` / `Docx` / `Xlsx` | a sub-companion renderer the deployment adds by `PackageReference` |
| `Pptx` | **nothing here — served by the deck export tier.** See below. |

## Deck export

**This SDK does not render decks, and that is a decision rather than a
gap.** `TemplateFormat` enumerates `Pptx`, and a render request for it
refuses with the typed `RenderError.FormatServedByDeckTier` — distinct
from `NoRendererForFormat`, which would send a reader looking for a
package that deliberately does not exist. Deck output for an application
built on this SDK is produced by a **downstream document-emission tier**:
a separate consumer that reads the deployment's exported result set and
emits typed deck parts.

The reason is that a useful deck is not a rendered template but a
*regenerable projection of governed results* — rebuild it after a number
is superseded and the slides move with it, because the slides reference
the results rather than containing a picture of them. A token-fill
renderer here (text into shapes, an image per chart) would close the gap
today and obsolete itself the day that tier ships, having in the meantime
become the thing consumers built against. So the format is routed, not
filled.

The routing is one decision function, `DeckExport.route`, called by
`RendererRegistry.Route`; it checks the deck-tier set *before* the
registry, so a deck renderer somebody registered anyway cannot silently
become the answer. The refusal is a value, not an exception — nothing
throws at composition, and a deployment that brings its own renderer is
told per render that this path does not route there.

### What the deck tier consumes

Everything an export tier binds against is already a wire surface; it
takes no dependency on the renderer stack:

| Input | Type / seam | Carries |
|---|---|---|
| Narrative | `NarrativeDocument` (`ToolUp.Platform.Core`) | the prose, sections, tables and `Metric` spans; crosses the client/server boundary already |
| Fact refs | `InlineSpan.Metric(label, value, factRef)` | an opaque fact id per quoted number — see [facts.md](facts.md) |
| Provenance | `IProvenanceGraph` | the walkable `narrative -> fact -> result -> data object` chain, with each fact node's disclosure class — see [provenance-chain.md](provenance-chain.md) |
| Egress policy | `IFactDisclosureGate` at `FactExport` | the one gate deciding which values may leave as a document |
| Charts | `ChartArtifact` (below) | deterministic, provenance-stamped rendered bytes |

A consumer reads the narrative, resolves the fact refs it cites, checks
them at the export door, and asks for a chart artifact per chart it wants
to embed.

## The chart-artifact handoff

The one thing an export tier cannot reasonably re-derive is the
deployment's own **chart grammar** — the declared deterministic
server-side renderer that published pages and bounded server-rendered
views already draw through. A second renderer over there would be a second
grammar, and two grammars disagree slowly: the deck's chart and the page's
chart of the same series drift a scale, then a number. So the grammar
stays here and the **artifact** crosses.

```fsharp skip=fragment
// Composition wires the deployment's own renderer in as data — the
// grammar lives in another companion, and a companion never reaches
// into another (GP 1).
let renderer: ChartArtifactRenderer = {
    MediaType = "image/svg+xml"
    Render =
        fun props ->
            NarrativeCharts.renderChart props
            |> RenderView.AsString.htmlNode
            |> Encoding.UTF8.GetBytes
}

let artifact =
    ChartArtifact.render renderer {
        Kind = "bar"
        Title = Some "Revenue"
        Points = [ { Label = "Jan"; Value = 10.0 }; { Label = "Feb"; Value = 20.0 } ]
        Binding = {
            ArtifactKey = Some resultId          // which stored result the series came from
            DatasetVintage = Some datasetVersion // would it redraw the same today?
        }
    }
```

`ChartArtifact.render` draws nothing itself: it builds the prop bag the
grammar already reads and hands it to the supplied function. What keeps
that from drifting into a parallel grammar is `ChartArtifact.props`, which
emits the same prop keys and the same point encoding the grammar's own
projector emits — pinned by a conformance test rather than by this
paragraph.

The result carries:

- `Content` + `MediaType` — the embeddable bytes under the renderer's
  declared type;
- `ContentHash` — `sha256:` over those bytes, so a consumer regenerating a
  deck can tell "unchanged" from "unchanged so far as I can see";
- `Metadata` — the grammar identity and prop-format version, the kind,
  title and point count, and the two provenance fields. Both provenance
  fields are optional and neither is invented: a chart over an ad-hoc
  series honestly has no binding, and a metadata field filled with a
  plausible-looking value is worse than an absent one.
  `ChartArtifact.isBound` is the predicate to gate on when a deployment's
  policy requires a full binding — it demands *both* fields, because a key
  without a vintage says which computation drew the chart but not whether
  it would draw the same today, and a vintage without a key says the
  opposite.

Determinism is a property of the grammar, not a promise added here: the
same spec through the same renderer produces byte-identical content and an
identical hash.

## See also

- [facts.md](facts.md) — the fact store, disclosure classes, and the
  `FactExport` egress door the render path runs.
- [provenance-chain.md](provenance-chain.md) — walking a quoted number
  back to its source.
- [narrative-elements.md](narrative-elements.md) — the narrative document
  model and its `Component` block seam, which is where charts live.

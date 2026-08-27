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
| `Pdf` | `ToolUp.Reporting.HtmlPdf` (HTML template → paginated PDF via headless Chromium) |
| `Docx` | `ToolUp.Reporting.Docx` (structural-model template fill; styles/numbering/tables preserved) |
| `Xlsx` | `ToolUp.Reporting.Xlsx` (token templates + cell-address-map writes into visually-authored workbooks) |
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
| Provenance (in-process) | `IProvenanceGraph` | the walkable `narrative -> fact -> result -> data object` chain, with each fact node's disclosure class — see [provenance-chain.md](provenance-chain.md) |
| Provenance (out-of-process) | `IProvenanceQueryApi` | the same chain as a typed read-only remoting contract, disclosure-filtered and cap-bounded, for a tier that does not compile the server assembly — see [provenance-chain.md](provenance-chain.md#out-of-process-consumers--the-read-only-wire-contract) |
| Egress policy | `IFactDisclosureGate` at `FactExport` | the one gate deciding which values may leave as a document |
| Charts | `ChartArtifact` (below) | deterministic, provenance-stamped rendered bytes |
| All of the above, paired | `ChartExportBundle` ([below](#the-export-bundle)) | one call: the disclosed document plus a block-keyed artifact per chart block, and a typed refusal for each block that produced none |

A consumer reads the narrative, resolves the fact refs it cites, checks
them at the export door, and asks for a chart artifact per chart it wants
to embed. The **export bundle** is that sequence as a single call, and is
the surface to reach for first — the rows above it are what to bind
against when a tier needs one piece rather than the set.

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

### The binding rides the chart block

A chart in a narrative document declares its own binding, so an export
tier reading that document recovers the same two identifiers without a
side channel — and a chart says what it is a claim about exactly once,
whether it reaches a reader as a document block or as a rendered artifact.
`NarrativeFromData.chartWith` takes the binding and emits it as declared
props alongside the grammar's existing ones; `NarrativeFromData.chartBinding`
(narrative side) and `ChartArtifact.bindingOf` (artifact side) read it back
from any prop bag, and `ChartArtifact.props` emits the identical pair from a
spec's `Binding` — the conformance test pins the two halves against each
other across bound, half-bound and unbound cases.

| Prop | Carries |
|---|---|
| `chart.kind` | the grammar's kind token (`line` / `bar` / `area`) |
| `chart.title` | optional visible caption |
| `chart.points` | the series, `label=value;…`, invariant culture |
| `chart.artifactKey` | optional — which stored result the series came from |
| `chart.datasetVintage` | optional — the vintage of the dataset underneath it |

An absent member emits **no prop at all** rather than an empty one, so an
unbound chart's prop bag is byte-identical to the pre-binding form (GP 11)
and an absent prop reads as "declares no binding" rather than "bound to
nothing". A declared binding also changes no drawn byte: the chart renderer
reads the three grammar props and draws from those, because a binding is a
claim a reader resolves rather than a mark on the canvas. Half a binding is
a legitimate thing to declare — whether it is *sufficient* is the consuming
tier's policy, and `ChartArtifact.isBound` is the predicate that demands
both.

## The export bundle

A chart block in a narrative document gives a reader the chart *props*.
The grammar that turns props into pixels is server-side, so a tier holding
the document can read every chart in it and render none — and nothing
paired a block with its artifact even where it could. The bundle is that
pairing, in one call:

```fsharp skip=fragment
// No fact tier composed — the pure pairing.
let bundle = ChartExportBundle.ofDocument renderer document

// With the fact tier composed: the document goes through the SAME
// `FactExport` door a rendered report goes through, and the artifacts are
// paired with the disclosed document.
let! bundle =
    NarrativeExportBundle.createWithDisclosureGate gate principal renderer scopeId document

bundle.Document        // the (disclosed) document
bundle.Charts          // Map<"chart:N", ChartArtifact>
bundle.Gaps            // the blocks that produced no artifact, and why
```

`bundle.Charts` and `bundle.Gaps` partition the document's chart blocks
exactly: every block is in one or the other.

### Keys are positional, and the walk is public

A key is `"chart:N"` for the zero-based index of the block in document
order — sections in declared order, elements within a section in declared
order, and a container's nested body (`Card` / `Accordion` / `Tabs`)
walked depth-first *at the point the container appears*. Positional rather
than declared, because the chart grammar has no chart id and inventing one
here would be the second grammar the handoff exists not to be; and because
a document may legitimately draw the same chart twice, which a
content-derived key would collapse into one entry.

What makes that usable rather than merely deterministic is that the walk
is public: `ChartExportBundle.blocks document` yields the same keys in the
same order, so a consumer never reimplements the traversal. The failure
mode of positional keying is two walks that disagree, and there is only
one walk.

The caveat, stated once: **a key identifies a position in this document,
not a chart across revisions.** Insert a chart into an earlier section and
every later key shifts. A tier that needs identity across revisions keys
on the block's declared binding (`ChartArtifact.bindingOf`) — which is
what a binding is for. Keys index the bundle's own `Document` field, which
is the disclosed one wherever a gate ran, so the pairing holds after the
door rather than before it.

### A bundle is an export surface, not a side door

`createWithDisclosureGate` calls the report handler's own export door
rather than reproducing it, and the pin for that is the surface the gate
records: `FactExport`, the same one the render path checks at. A value
this principal may not egress is redacted in the bundle's document exactly
as it would be in a rendered report, and the withheld-values note travels
with it. `ChartExportBundle.ofDocument` (and the `NarrativeExportBundle.create`
that names it on the server surface) is the honest counterpart for a
deployment composing no fact tier: no gate to consult, nothing paid for a
door it does not have (GP 13).

### Partial rather than failed

One unrenderable block does not fail the bundle. A tier that asked for
eleven charts is better served by ten plus a typed statement about the
eleventh than by an exception carrying none, so refusals are collected per
block:

| Refusal | Means |
|---|---|
| `ChartBlockHasNoSeries` | the block declares no usable series (`chart.points` absent, empty, or decoding to nothing). Deliberately stricter than the page grammar, which draws a "no data" placeholder — an export tier embedding a placeholder it did not ask for is worse off than one told the block is empty |
| `ChartRendererFailed reason` | the composition-supplied renderer raised. The renderer belongs to the deployment, so its own message is carried rather than an invented one |

Each gap carries the block's key, ordinal and section id, so it is
actionable without re-walking the document. `ChartExportBundle.isComplete`
is the predicate for a tier whose own policy is all-or-nothing.

Determinism runs end to end: the walk, the keys and every artifact's bytes
are functions of the document alone — nothing here reads a store or a
clock — so the same document through the same renderer yields a
byte-identical bundle.

## See also

- [facts.md](facts.md) — the fact store, disclosure classes, and the
  `FactExport` egress door the render path runs.
- [provenance-chain.md](provenance-chain.md) — walking a quoted number
  back to its source.
- [narrative-elements.md](narrative-elements.md) — the narrative document
  model and its `Component` block seam, which is where charts live.

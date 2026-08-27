# ToolUp.Reporting.Docx

DOCX rendering sub-companion for `ToolUp.Reporting`: fills `.docx` report templates
(`{{key}}` placeholder tokens in document text) and returns the filled document bytes.
Implements `IReportRenderer` for `TemplateFormat.Docx` and registers through the standard
`RendererRegistry` path.

Built on `ToolUp.OpenXml`'s structural model — `Import` → substitution over runs →
`Emit` — rather than string surgery inside the package XML. That altitude is what
preserves the template's styles, numbering, tables, comments and unmodelled parts
(carried opaquely, per the import residue report), and keeps the door open to emitting
fills as native tracked changes later.

## Usage

```fsharp skip=fragment
open ToolUp.Reporting

// At composition time (consumer side):
ReportingServerApp.create config
|> ReportingServerApp.withReportRenderer (ToolUp.Reporting.Docx.DocxReportRenderer.create ())
```

Author templates as ordinary `.docx` files containing `{{key}}` tokens; declare each key
in the template's `Placeholders` schema. Scalar kinds (`Text` / `Number` / `Date`) honour
their format hints via the shared substitution machinery.

## Semantics worth knowing

- **Split tokens re-join.** Word routinely splits a typed token across runs (spell-check,
  edit history). Adjacent runs with identical formatting are coalesced before
  substitution, so those tokens still match. A token split across a *formatting boundary*
  (e.g. half the key bolded) is left as authored — fix the template.
- **Tables.** A `Table`-kind placeholder whose token is the entire paragraph renders as a
  native Word table (bold header row + one row per data entry). An inline table token
  renders as tab-separated text.
- **Images.** `Image`-kind values render as a bracketed text marker — the structural
  model does not carry image parts. Use the HTML→PDF renderer for image-bearing output.
- **Unknown tokens pass through** unchanged, so schema/template drift stays visible.
- **Invalid template bytes** (not a readable `.docx`) surface as `RendererFailure`, never
  an exception.

## Narrative projection

A `NarrativeValue` placeholder whose token is the entire paragraph expands through
`NarrativeOoxml` at that anchor, so a narrative document reaches the `.docx` as **native
Word structures** rather than flattened prose: styled heading paragraphs, bulleted and
numbered list items backed by real numbering definitions, native tables for `Table` /
`KeyValueGrid`, a shaded single-cell table for each `Callout`, monospace paragraphs for
`CodeBlock`, an indented block for `Blockquote`, and a bottom-bordered paragraph for
`Divider`. Every element kind is projected — the ones Word has no analogue for (`Video` /
`Audio` / `Embed` / `ImageGallery`, and inline `Image`) degrade to their caption, title or
alt text plus the source URL, so nothing is dropped silently. A narrative token in an
*inline* position has no anchor to expand into and takes the plaintext projection instead.

`NarrativeOoxml.project` / `projectWith` are public, so the same projection is usable
without going through a template.

### Component blocks

`Component(name, props)` blocks resolve through a caller-supplied registry, so this package
names no rendering companion of its own:

```fsharp skip=fragment
open ToolUp.Reporting.Docx

let renderers name props =
    if name = "chart" then
        NarrativeOoxml.ComponentResult.Svg(myChartRenderer props)
    else
        NarrativeOoxml.ComponentResult.Fallback

DocxReportRenderer.createWith renderers
```

`create ()` is `createWith` with an empty registry. An unregistered name — or a registered
renderer returning `Fallback` — degrades to the component's data table (its props rendered
as a two-column table), which is what keeps the content readable when no renderer is wired.

`Svg` and `Image` results are not yet embedded as figures: the structural model carries no
figure-emit capability, so both degrade to a paragraph stating the figure was not embedded
followed by the same data table. That is deliberate and stated rather than silent; when a
figure emitter lands, those two branches become the embed and the rest of the projection is
unchanged.

### Lists and the numbering part

Projected list items reference numbering instances this package defines, chosen so they
cannot collide with an id the template already declares, and spliced into the template's own
numbering part (or a fresh one) at emit. A render that carried no narrative leaves the
numbering part exactly as the template had it.

## Dependencies

`ToolUp.Reporting.Core` (the renderer contract) and `ToolUp.OpenXml` (the structural
model; the `DocumentFormat.OpenXml` vendor dependency lives there — this package adds no
vendor dependency of its own).

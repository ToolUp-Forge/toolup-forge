# ToolUp.OpenXml

A shared structural / revision layer over `DocumentFormat.OpenXml` for round-trip
document work: import a `.docx` into an immutable typed model, edit it
programmatically, and emit a **native Word redline** — `w:ins` / `w:del` tracked
changes and comments, attributed per author and timestamp, that a reviewer can
accept or reject in Word.

Wrapping OOXML usually happens at the wrong altitude: read-side text extraction
discards structure (headings, tables, numbering, comments all flatten to text),
and write-side template fill can't express "change this sentence and show the
change". This companion is the layer both ends sit on:

- **Package / parts / styles / numbering helpers** (`Package`) — the plumbing
  every OOXML consumer otherwise re-derives: open / create packages, reach or
  create the main / styles / numbering / comments parts with relationship wiring
  handled.
- **Structure-preserving import** (`Import`) — `.docx` → `DocModel` + an explicit
  **lossy-residue report** naming everything the model did not capture, instead of
  silently dropping it.
- **Emission** (`Emit`) — `DocModel` → `.docx`; import → emit round-trips the
  captured structure (headings, styled runs, tables, numbering, styles, comments,
  section properties).
- **Revision + comment emission** (`Revisions` + `Emit`) — an edit-set type
  (insert / delete / replace at model addresses, plus comments) lowered to native
  tracked changes.

The companion is **additive by construction** (GP 13): it references nothing in
`ToolUp.Platform.*` and nothing references it; its only dependency is the OpenXml
SDK (GP 1 — the vendor dep stays here). The model is immutable records throughout
(GP 5); edits produce new values; no live OpenXml SDK handle leaks through the
model (GP 12 rule 1).

## Quick start — import, redline, emit

```fsharp
open ToolUp.OpenXml

// 1. Import: typed structural model + honest residue report.
let imported = Import.fromBytes (File.ReadAllBytes "contract.docx")

for entry in imported.Residue.Entries do
    printfn "%s at %s — %s (%A)" entry.ElementKind entry.Location entry.Reason entry.Disposition

// 2. Edit as tracked changes.
let edits = {
    Author = { Name = "Review Bot"; Initials = Some "RB" }
    Timestamp = DateTimeOffset.UtcNow
    Edits = [
        ReplaceText({ Section = 0; Block = 4 }, "thirty (30) days", "sixty (60) days")
        AddComment({ Section = 0; Block = 4 }, "Extended per the agreed redline.")
        DeleteParagraph { Section = 0; Block = 9 }
    ]
}

match Revisions.applyTracked edits imported.Model with
| Ok revised ->
    // 3. Emit: opens in Word as a native redline.
    File.WriteAllBytes("contract.redline.docx", Emit.toBytes revised)
| Error problem -> printfn "edit failed: %A" problem
```

## The structural model

`DocModel` describes a WordprocessingML document at the altitude round-trip work
needs: sections → block elements, plus the document-level parts.

| Model element | Captures |
|---|---|
| `Heading (level, paragraph)` | paragraphs styled `Heading1`–`Heading9` |
| `Paragraph` | styled runs (bold / italic / underline / strike + style ids), comment anchors, revision marks |
| `ListItem (numberingRef, paragraph)` | `w:numPr` numbering id + indent level |
| `Table` | rows → cells → nested blocks |
| `OpaqueBlock` | any block-level element outside the vocabulary, carried verbatim |
| `Section.RawProperties` | `w:sectPr` (page size, margins, columns) |
| `StyleDefinitions` / `NumberingDefinitions` | parsed identity rows + the verbatim part XML |
| `Comment` | id, author, initials, date, text |
| `RevisionMark` | pre-existing `w:ins` / `w:del` on runs and paragraph marks |

Two capture tiers keep the model small *and* the round trip honest: parsed
conveniences (the booleans, levels and ids above) for reading and editing, plus
verbatim `OuterXml` payloads (`RawProperties` / `RawXml`) that emission re-attaches
unchanged — fonts, colours, table widths and style details survive even though the
model doesn't decompose them.

## The residue report

Full-fidelity capture of every OOXML element is a non-goal; the residue report is
the honesty mechanism. Import returns it as a first-class value — never a log
line — naming every element outside the model's vocabulary:

- **`CarriedOpaque`** — block-level strangers (content controls, embedded
  objects): preserved verbatim as `OpaqueBlock`s and re-emitted unchanged, but
  opaque to the model.
- **`Dropped`** — inline strangers the run list cannot host (drawings inside
  runs, bookmarks, field codes; hyperlink wrappers are flattened to their text
  with the target reported dropped): absent from emitted output.

Spec noise with no document content (`w:proofErr`, `w:lastRenderedPageBreak`,
`w:bookmarkEnd` — each bookmark is reported once at its start marker) is
deliberately not reported.

## Tracked changes

`Revisions.applyTracked` applies an `EditSet` as a pure model → model transform;
errors come back as data (`AddressOutOfRange` / `NotAParagraphBlock` /
`TextNotFound`). `ReplaceText` matches across formatting-run boundaries: the runs
covering the match are split at the boundaries and marked deleted (each keeping
its own formatting), and the replacement is inserted with the first covered run's
formatting. Emission allocates document-unique revision ids and stamps
`w:author` + `w:date` on every `w:ins` / `w:del`. Whole-paragraph insert / delete
also marks the paragraph mark (`w:pPr/w:rPr/w:ins|w:del`), so accepting the
change adds / removes the paragraph itself.

## Documented edges

- Comment bodies flatten to plain text on import (multi-paragraph bodies join
  with newlines); emission recreates a single-paragraph body.
- Comment anchors are paragraph-grained: emission wraps the paragraph's runs in
  one `commentRangeStart` / `End` pair per comment.
- Tabs and line breaks inside runs normalise to `'\t'` / `'\n'` in run text and
  lower back to `w:tab` / `w:br` on emission.
- A numbered heading stays a `Heading`; its numbering reference round-trips via
  the raw paragraph properties.
- Programmatic models (no captured raw payloads) get generated parts on emission:
  a minimal styles part covering the heading levels in use, a decimal numbering
  definition per numbering id in use, and bordered table defaults.

## Custom parts (out-of-band OPC parts)

Sometimes a consumer needs to carry an extra payload *inside* the `.docx`
package — a structured sidecar the document body doesn't model — and read it
back on import, without post-processing the finished bytes through a second OPC
pass. `Emit.toBytesWith` / `Import`'s `CustomParts` fold this into the single
emit / import pass:

```fsharp
let part = {
    PartUri = "/myapp/tree.xml"                           // honoured verbatim
    ContentType = "application/vnd.myapp.doc-tree+xml"
    RelationshipType = "http://example.test/relationships/doc-tree"
    Content = "<tree><node id=\"1\">root</node></tree>"   // UTF-8 XML, carried opaquely
}

let bytes = Emit.toBytesWith [ part ] model               // toBytes = toBytesWith []

let imported = Import.fromBytes bytes
imported.CustomParts = [ part ]                            // round-trips intact
```

- Each part is written with a content-type **Override** in `[Content_Types].xml`
  and a **package-root relationship** (`TargetMode=Internal`) of the given
  `RelationshipType`, so OPC-aware editors (Word, LibreOffice) preserve the part
  untouched on their own round-trip.
- The caller's `PartUri` is honoured **verbatim** — do **not** place parts under
  `/customXml/`; Word renumbers and owns that space (`item1.xml` + `itemProps`).
- Re-emitting a part whose URI already exists **replaces** it and dedupes its
  relationship rather than duplicating either (last write wins).
- The content is carried opaquely — the model never parses or validates it — and
  the document parts and their existing relationships are untouched (purely
  additive, GP 13). `toBytes` / `toBytesWith []` emit no custom part, so existing
  callers are unaffected.

The `Emit.toBytes` signature is unchanged (it delegates to `toBytesWith []`);
`toStreamWith` is the streaming twin. `Package.attachCustomParts` /
`Package.readCustomParts` expose the OPC-level plumbing for lower-level consumers.

## Out of scope

XLSX / PPTX structural models (docx-first; the package / parts helpers already
serve any OOXML flavour), WYSIWYG editing surfaces, rendering / preview, and
full-fidelity capture of every OOXML element (see the residue report).

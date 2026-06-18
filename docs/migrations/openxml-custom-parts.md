# ToolUp.OpenXml — out-of-band custom OPC parts (consumer migration)

**What changes.** `ToolUp.OpenXml` gains a generic custom-parts capability: attach
arbitrary extra OPC parts to the `.docx` package on emit and read them back on
import, folded into the single emit/import pass. Previously a consumer needing an
out-of-band sidecar part (e.g. a structured payload travelling inside the package)
had to post-process the finished `byte[]` through a second `System.IO.Packaging`
pass.

**Scope.** A new `CustomPart` record, a `CustomParts` field on `ImportedDocument`,
additive `Emit.toBytesWith` / `Emit.toStreamWith` entry points, and the OPC-level
plumbing helpers `Package.attachCustomParts` / `Package.readCustomParts`.

**This is an additive opt-in.** `Emit.toBytes` / `Emit.toStream` keep their
signatures and now delegate to the `[]` path — byte-for-byte equivalent to a
custom-part-free emit, so existing callers are unaffected. The lone in-tree
consumer (`ToolUp.KnowledgeBase.Server`) reads only `ImportedDocument.Model` and
recompiles unchanged. The SDK-ADOPTION matrix row is all-⛔ N-A — no pinned
consumer attaches custom parts today.

## New surface

```fsharp
/// An out-of-band OPC part carried alongside the document parts.
type CustomPart = {
    PartUri: string            // package-relative, honoured verbatim, e.g. "/myapp/tree.xml"
    ContentType: string        // [Content_Types].xml Override, e.g. "application/vnd.myapp.doc-tree+xml"
    RelationshipType: string   // package-root relationship type (TargetMode=Internal)
    Content: string            // UTF-8 XML payload, carried opaquely
}

// Emit — additive; toBytes/toStream unchanged (delegate to the [] path).
val Emit.toBytesWith:  CustomPart list -> DocModel -> byte[]
val Emit.toStreamWith: CustomPart list -> DocModel -> System.IO.Stream -> unit

// Import — new field on the existing result record.
type ImportedDocument = {
    Model: DocModel
    Residue: ResidueReport
    CustomParts: CustomPart list   // empty for a document with no such parts
}

// OPC-level plumbing (for lower-level consumers).
val Package.attachCustomParts: System.IO.Stream -> CustomPart list -> unit
val Package.readCustomParts:   WordprocessingDocument -> CustomPart list
```

## Usage

```fsharp
let part = {
    PartUri = "/myapp/tree.xml"
    ContentType = "application/vnd.myapp.doc-tree+xml"
    RelationshipType = "http://example.test/relationships/doc-tree"
    Content = "<tree><node id=\"1\">root</node></tree>"
}

let bytes    = Emit.toBytesWith [ part ] model
let imported = Import.fromBytes bytes
// imported.CustomParts = [ part ]
```

## Semantics

- Each part is written at its **verbatim** `PartUri` with a content-type
  **Override** in `[Content_Types].xml` and a **package-root** relationship
  (`TargetMode=Internal`) of the given `RelationshipType`, so OPC-aware editors
  (Word, LibreOffice) preserve the part untouched on their own round-trip.
- Do **not** place parts under `/customXml/` — Word renumbers and owns that space
  (`item1.xml` + `itemProps`). Pick your own package path.
- Re-emitting a part whose URI already exists **replaces** it and dedupes its
  relationship (last write wins); a `CustomPart list` carrying the same URI twice
  keeps the last.
- Purely additive: the document parts and their existing relationships are
  untouched. The content is carried opaquely — the model never parses or
  validates it.
- Import surfaces custom parts as the OpenXml SDK's `ExtendedPart`s — the parts
  reached by a package-root relationship the SDK does not itself recognise.
  Standard document / styles / numbering / comments / core+extended-properties
  parts are SDK-typed and excluded.

## Verification

```bash
cd toolup-forge
dotnet build src/ToolUp.OpenXml/ToolUp.OpenXml.fsproj          # 0 errors
dotnet run --project src/ToolUp.OpenXml.Tests/ToolUp.OpenXml.Tests.fsproj
# Expecto pack green; the "CustomParts" list covers round-trip, back-compat,
# multiple parts, replace-by-URI, OPC+OOXML validity, and the content-type
# override + root relationship (validated via System.IO.Packaging).
```

A plain emit is unchanged: `Import.fromBytes (Emit.toBytes model)` returns
`CustomParts = []` and carries no extra part or relationship.

## Rollback

Revert the feature commit (`aa14827`). The change is additive — reverting removes
the `CustomPart` type, the `CustomParts` field, and the `*With` entry points; no
consumer-side data migration is required because no consumer attaches custom parts
yet.

## Out of scope

- Non-XML / binary part payloads (the `Content` field is a UTF-8 string; a binary
  variant is deferred until a consumer demands it).
- Modelling the part content — custom parts are opaque by design; structure is the
  caller's concern.

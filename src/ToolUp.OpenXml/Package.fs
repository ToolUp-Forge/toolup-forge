// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Package / parts plumbing over `DocumentFormat.OpenXml.Packaging` —
/// the shared layer every OOXML consumer otherwise re-derives:
/// open / create a Wordprocessing package, reach (or create) the
/// main, styles, numbering and comments parts with the relationship
/// wiring handled. `Import` and `Emit` sit on these helpers; they
/// are public because consumers doing lower-level OOXML work need
/// exactly the same plumbing.
///
/// These helpers hand back live OpenXml SDK part objects — they are
/// the plumbing tier, not the model tier. The structural model
/// (`DocModel`) never carries any of them (GP 12 rule 1).
module ToolUp.OpenXml.Package

open System
open System.IO
open System.IO.Packaging
open System.Text
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging

/// Open an existing `.docx` read-only. The caller owns disposal of
/// the returned document (and the stream outlives it).
let openRead (stream: Stream) : WordprocessingDocument =
    WordprocessingDocument.Open(stream, false)

/// Open an existing `.docx` for in-place editing.
let openEdit (stream: Stream) : WordprocessingDocument =
    WordprocessingDocument.Open(stream, true)

/// Create a new `.docx` package on the stream with an initialised
/// main part (`w:document/w:body`). The caller owns disposal.
let create (stream: Stream) : WordprocessingDocument =
    let doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document)

    let main = doc.AddMainDocumentPart()
    // Explicit append: the `Document(child)` ctor overload set
    // resolves a single composite argument to IEnumerable and copies
    // the child's children instead of attaching the child.
    let document = Wordprocessing.Document()
    document.AppendChild(Wordprocessing.Body()) |> ignore
    main.Document <- document
    doc

/// The main document part. Present on every well-formed `.docx`;
/// raises a descriptive error rather than a null-reference for a
/// package that lacks one.
let mainPart (doc: WordprocessingDocument) : MainDocumentPart =
    match doc.MainDocumentPart with
    | null -> failwith "Package has no main document part — not a WordprocessingML document."
    | part -> part

/// The styles part, when the document has one.
let stylesPart (doc: WordprocessingDocument) : StyleDefinitionsPart option =
    match (mainPart doc).StyleDefinitionsPart with
    | null -> None
    | part -> Some part

/// The styles part, creating (and wiring the relationship for) an
/// empty one when absent.
let ensureStylesPart (doc: WordprocessingDocument) : StyleDefinitionsPart =
    match stylesPart doc with
    | Some part -> part
    | None ->
        let part = (mainPart doc).AddNewPart<StyleDefinitionsPart>()
        part.Styles <- Wordprocessing.Styles()
        part

/// The numbering part, when the document has one.
let numberingPart (doc: WordprocessingDocument) : NumberingDefinitionsPart option =
    match (mainPart doc).NumberingDefinitionsPart with
    | null -> None
    | part -> Some part

/// The numbering part, creating an empty one when absent.
let ensureNumberingPart (doc: WordprocessingDocument) : NumberingDefinitionsPart =
    match numberingPart doc with
    | Some part -> part
    | None ->
        let part = (mainPart doc).AddNewPart<NumberingDefinitionsPart>()
        part.Numbering <- Wordprocessing.Numbering()
        part

/// The comments part, when the document has one.
let commentsPart (doc: WordprocessingDocument) : WordprocessingCommentsPart option =
    match (mainPart doc).WordprocessingCommentsPart with
    | null -> None
    | part -> Some part

/// The comments part, creating an empty one when absent.
let ensureCommentsPart (doc: WordprocessingDocument) : WordprocessingCommentsPart =
    match commentsPart doc with
    | Some part -> part
    | None ->
        let part = (mainPart doc).AddNewPart<WordprocessingCommentsPart>()
        part.Comments <- Wordprocessing.Comments()
        part

// ─── Out-of-band custom parts (OPC level) ────────────────────────
//
// Custom parts are arbitrary extra package parts the document
// vocabulary does not model — a structured sidecar payload the caller
// attaches and reads back. They sit at the OPC layer (a part + a
// content-type override + a package-root relationship), not inside
// w:document, so they are manipulated directly over
// `System.IO.Packaging` rather than through the WordprocessingML SDK
// object tree. Emission attaches them after the SDK has finalised the
// document parts; import surfaces them as `ExtendedPart`s the SDK
// preserves for any root relationship it does not itself recognise.

/// Attach each custom part to an already-written `.docx` package on
/// the stream: one part at its verbatim `PartUri`, a content-type
/// override in `[Content_Types].xml`, and a package-root relationship
/// (`TargetMode=Internal`) of the given `RelationshipType`. Existing
/// document parts and relationships are untouched (purely additive).
/// Re-emitting a part whose URI already exists replaces the part and
/// dedupes its root relationship rather than duplicating either, so a
/// `CustomPart list` carrying the same URI twice keeps the last.
///
/// The stream must be seekable and writable; it is left open and its
/// content reflects the attached parts on return.
let attachCustomParts (stream: Stream) (parts: CustomPart list) : unit =
    match parts with
    | [] -> ()
    | _ ->
        stream.Position <- 0L

        use package =
            System.IO.Packaging.Package.Open(stream, FileMode.Open, FileAccess.ReadWrite)

        for part in parts do
            let partUri = PackUriHelper.CreatePartUri(Uri(part.PartUri, UriKind.Relative))

            // Replace-by-URI: drop any root relationship already
            // targeting this URI and the part itself, so a re-emit
            // dedupes instead of stacking duplicates.
            package.GetRelationships()
            |> Seq.filter (fun rel ->
                rel.TargetMode = TargetMode.Internal
                && PackUriHelper.ResolvePartUri(rel.SourceUri, rel.TargetUri) = partUri)
            |> Seq.toArray
            |> Array.iter (fun rel -> package.DeleteRelationship rel.Id)

            if package.PartExists partUri then
                package.DeletePart partUri

            let opcPart = package.CreatePart(partUri, part.ContentType)
            let bytes = Encoding.UTF8.GetBytes part.Content
            use partStream = opcPart.GetStream(FileMode.Create, FileAccess.Write)
            partStream.Write(bytes, 0, bytes.Length)

            package.CreateRelationship(partUri, TargetMode.Internal, part.RelationshipType)
            |> ignore

/// Read every out-of-band custom part the package carries — the parts
/// reached by a package-root relationship the WordprocessingML SDK
/// does not itself recognise, which it preserves as `ExtendedPart`s.
/// The standard document, styles, numbering, comments and
/// core/extended-properties parts are SDK-typed parts and are excluded.
let readCustomParts (doc: WordprocessingDocument) : CustomPart list =
    doc.Parts
    |> Seq.choose (fun pair ->
        match pair.OpenXmlPart with
        | :? ExtendedPart as ext ->
            use partStream = ext.GetStream(FileMode.Open, FileAccess.Read)
            use reader = new StreamReader(partStream, Encoding.UTF8)

            Some {
                PartUri = ext.Uri.ToString()
                ContentType = ext.ContentType
                RelationshipType = ext.RelationshipType
                Content = reader.ReadToEnd()
            }
        | _ -> None)
    |> List.ofSeq
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

open System.IO
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
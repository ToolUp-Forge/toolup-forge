// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Package / parts plumbing over `DocumentFormat.OpenXml.Packaging`
/// for SpreadsheetML — create a workbook package, add worksheet /
/// shared-string / styles parts under *caller-chosen* relationship
/// ids, and normalise the finished package so identical models emit
/// identical bytes.
///
/// These helpers hand back live OpenXml SDK part objects — they are
/// the plumbing tier, not the model tier. The structural model
/// (`WorkbookModel`) never carries any of them.
module ToolUp.OpenXml.Spreadsheet.Package

open System
open System.IO
open System.IO.Compression
open System.Xml.Linq
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet

/// The OPC relationships namespace, as it appears in every `.rels`
/// part.
let private relationshipsNamespace =
    XNamespace.Get "http://schemas.openxmlformats.org/package/2006/relationships"

/// The package-root relationships part. Its relationship ids are
/// referenced by no other part, which is what makes rewriting them
/// safe (see `normalise`).
[<Literal>]
let private RootRelationshipsEntry = "_rels/.rels"

/// The fixed timestamp stamped on every ZIP entry. 1980-01-01 is the
/// floor of the MS-DOS date encoding ZIP uses, so it is the one
/// instant every writer can represent exactly.
let private fixedEntryTimestamp = DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)

/// Create a new `.xlsx` package on the stream with an initialised
/// workbook part. The caller owns disposal.
let create (stream: Stream) : SpreadsheetDocument =
    let doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook)
    let workbookPart = doc.AddWorkbookPart()
    workbookPart.Workbook <- Workbook()
    doc

/// Open an existing `.xlsx` read-only. The caller owns disposal (and
/// the stream outlives it).
let openRead (stream: Stream) : SpreadsheetDocument = SpreadsheetDocument.Open(stream, false)

/// The workbook part. Present on every well-formed `.xlsx`; raises a
/// descriptive error rather than a null-reference for a package that
/// lacks one.
let workbookPart (doc: SpreadsheetDocument) : WorkbookPart =
    match doc.WorkbookPart with
    | null -> failwith "Package has no workbook part — not a SpreadsheetML workbook."
    | part -> part

/// Add a worksheet part under an explicit relationship id.
///
/// The explicit id is load-bearing, not tidiness: `AddNewPart<T>()`
/// with no id mints a random one (`R` + a fresh GUID) which lands
/// verbatim in the emitted `.rels` XML, so two emits of one model
/// would differ in bytes while agreeing in every other respect.
let addWorksheetPart (workbook: WorkbookPart) (relationshipId: string) : WorksheetPart =
    workbook.AddNewPart<WorksheetPart>(relationshipId)

/// Add the shared-string table part under an explicit relationship id
/// (see `addWorksheetPart` on why the id is explicit).
let addSharedStringTablePart (workbook: WorkbookPart) (relationshipId: string) : SharedStringTablePart =
    workbook.AddNewPart<SharedStringTablePart>(relationshipId)

/// Add the workbook styles part under an explicit relationship id
/// (see `addWorksheetPart` on why the id is explicit).
let addStylesPart (workbook: WorkbookPart) (relationshipId: string) : WorkbookStylesPart =
    workbook.AddNewPart<WorkbookStylesPart>(relationshipId)

/// Rewrite the package-root relationship ids to `rId1`, `rId2`, … in
/// document order.
///
/// `SpreadsheetDocument.Create` + `AddWorkbookPart()` mints the
/// workbook's root relationship id itself, with no id-taking overload
/// to intercept it, and the id it mints is random. Root relationship
/// ids are referenced by no part content — nothing in
/// `[Content_Types].xml`, the workbook, or any worksheet names them —
/// so renaming them is a pure normalisation.
let private normaliseRootRelationships (content: byte[]) : byte[] =
    use input = new MemoryStream(content)
    let document = XDocument.Load input

    match document.Root with
    | null -> content
    | root ->
        root.Elements(relationshipsNamespace + "Relationship")
        |> Seq.iteri (fun index relationship ->
            relationship.SetAttributeValue(XName.Get "Id", sprintf "rId%d" (index + 1)))

        use output = new MemoryStream()
        document.Save(output, SaveOptions.DisableFormatting)
        output.ToArray()

let private readEntries (packageBytes: byte[]) : (string * byte[]) list =
    use source = new MemoryStream(packageBytes)
    use archive = new ZipArchive(source, ZipArchiveMode.Read)

    archive.Entries
    |> Seq.map (fun entry ->
        use entryStream = entry.Open()
        use buffer = new MemoryStream()
        entryStream.CopyTo buffer
        entry.FullName, buffer.ToArray())
    |> Seq.toList

let private writeEntries (output: Stream) (entries: (string * byte[]) list) : unit =
    use archive = new ZipArchive(output, ZipArchiveMode.Create, true)

    for name, content in entries do
        let entry = archive.CreateEntry(name, CompressionLevel.Optimal)
        entry.LastWriteTime <- fixedEntryTimestamp
        use entryStream = entry.Open()
        entryStream.Write(content, 0, content.Length)

/// Normalise an emitted package so identical models produce identical
/// bytes.
///
/// Two facts about the produced package are otherwise wall-clock- or
/// GUID-dependent, and both are invisible until you diff the bytes:
///
///  * every ZIP entry carries a last-write timestamp, which the
///    framework defaults to *now*; and
///  * the package-root relationship id is a fresh GUID per emit.
///
/// This pass rebuilds the archive with entries in ordinal name order
/// (which puts `[Content_Types].xml` first, as convention expects), a
/// fixed entry timestamp, and normalised root relationship ids.
/// Nothing semantic changes: ZIP entry order carries no meaning in
/// OPC, and the rewritten ids are unreferenced.
let normalise (packageBytes: byte[]) : byte[] =
    let entries =
        readEntries packageBytes
        |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right))
        |> List.map (fun (name, content) ->
            if name = RootRelationshipsEntry then
                name, normaliseRootRelationships content
            else
                name, content)

    use output = new MemoryStream()
    writeEntries output entries
    output.ToArray()
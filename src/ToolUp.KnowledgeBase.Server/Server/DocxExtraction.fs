module KnowledgeBase.DocxExtraction

open System
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.Chunking
open ToolUp.OpenXml
open SharedTypes
open KnowledgeBase.ServerJsonHelpers

// ─── Phase 124 — opt-in structured DOCX extraction ───────────────
//
// The default DOCX path (`ServerExtractors.extractDocx`) flattens a
// document to per-heading text. This module offers a structured
// alternative built on the `ToolUp.OpenXml` import model: the full
// heading PATH (e.g. `Intro › Background`) keys each chunk, list
// items keep their bullet shape, and tables become citable
// `chunkSpreadsheet`-format chunks instead of being skipped (the
// flat path only walks top-level paragraphs, so table content never
// reaches the index).
//
// GP 11: the structured mode is reachable only by explicit
// compose-time opt-in below — the default pipeline stays
// byte-for-byte on the flat path.

[<RequireQualifiedAccess>]
type DocxExtractionMode =
    | Flat
    | Structured

// Compose-time write-once flag, read per upload. Static mutable is
// the same sanctioned shape as the KB ingestion `statusCache` and
// the `NarrativeCommit.install` registration point: a deployment
// calls the opt-in once during composition, before the server
// accepts uploads; it is not a per-request toggle.
let mutable private mode = DocxExtractionMode.Flat

/// Compose-time opt-in: route `.docx` ingestion through the
/// structured (heading-path-aware) extractor. Call once during
/// composition, before the server starts accepting uploads.
let enableStructuredDocxExtraction () = mode <- DocxExtractionMode.Structured

/// Restore the default flat path (primarily for tests).
let resetToFlatDocxExtraction () = mode <- DocxExtractionMode.Flat

let internal currentMode () = mode

/// Structured extraction: import the document's structural model,
/// then chunk per heading path. Mirrors the flat extractor's chunk
/// shape (same metadata keys, same `Section` source locations) so
/// downstream retrieval and citation surfaces need no changes.
let internal extract (docId: string) (fileName: string) (bytes: byte[]) : (TextChunk * SourceReference) list =
    let imported = Import.fromBytes bytes
    let config = ChunkingConfig.defaults
    let results = ResizeArray<TextChunk * SourceReference>()

    let makeSrc (location: SourceLocation) : SourceReference = {
        DocumentId = docId
        DocumentName = fileName
        FileType = "docx"
        Location = location
        IndexedAt = DateTimeOffset.UtcNow
    }

    let makeChunk (header: string) (content: string) (src: SourceReference) : TextChunk = {
        Content = String.concat "\n" [ sprintf "[%s — %s]" fileName header; content ]
        Metadata =
            Map.ofList [
                "_source", toJson src
                "dataTypeId", "KnowledgeDocument"
                ChunkMetadata.OriginKey, ChunkOrigin.toMetadataValue Document
                ChunkMetadata.LocationHintKey, header
            ]
    }

    // Heading path: the stack of (level, text) headings above the
    // current position; `Heading 1 › Heading 2` keys the chunks.
    let headingPath = ResizeArray<int * string>()

    let currentHeading () =
        if headingPath.Count = 0 then
            "Document"
        else
            headingPath |> Seq.map snd |> String.concat " › "

    let pendingText = ResizeArray<string>()

    let flushText () =
        if pendingText.Count > 0 then
            let text = String.concat "\n" pendingText
            pendingText.Clear()
            let heading = currentHeading ()
            let pieces = splitByTokens config text
            let parts = if pieces.IsEmpty then [ text ] else pieces
            let total = parts.Length

            for i, piece in List.indexed parts do
                let header =
                    if total = 1 then
                        sprintf "Section: \"%s\"" heading
                    else
                        sprintf "Section: \"%s\" (part %d of %d)" heading (i + 1) total

                // `SharedTypes.Section` qualified: the open of
                // `ToolUp.OpenXml` brings a `Section` record type into
                // scope that would otherwise shadow the union case.
                let src = makeSrc (SharedTypes.Section heading)
                results.Add(makeChunk header piece src, src)

    let flushTable (table: TableModel) =
        let cellText (cell: TableCell) =
            cell.Blocks |> List.map Block.text |> String.concat " " |> _.Trim()

        match table.Rows with
        | [] -> ()
        | [ onlyRow ] ->
            // A single-row table has no data rows to chunk — fold its
            // cells into the running section text.
            pendingText.Add(onlyRow.Cells |> List.map cellText |> String.concat "\t")
        | headerRow :: dataRows ->
            let heading = currentHeading ()

            let sheetData: SheetData = {
                SheetName = heading
                Headers = headerRow.Cells |> List.map cellText |> Array.ofList
                // 1-based source rows as a spreadsheet shows them:
                // header row is 1, first data row is 2.
                Rows =
                    dataRows
                    |> List.mapi (fun i row -> (i + 2, row.Cells |> List.map cellText |> Array.ofList))
            }

            let pieces = chunkSpreadsheet config sheetData
            let total = pieces.Length

            for i, piece in List.indexed pieces do
                let header =
                    if total = 1 then
                        sprintf "Section: \"%s\" (table)" heading
                    else
                        sprintf "Section: \"%s\" (table, part %d of %d)" heading (i + 1) total

                let src = makeSrc (SharedTypes.Section heading)
                results.Add(makeChunk header piece src, src)

    for section in imported.Model.Sections do
        for block in section.Blocks do
            match block with
            | Heading(level, paragraph) ->
                let text = (ParagraphModel.text paragraph).Trim()

                if text.Length > 0 then
                    flushText ()

                    while headingPath.Count > 0 && fst headingPath[headingPath.Count - 1] >= level do
                        headingPath.RemoveAt(headingPath.Count - 1)

                    headingPath.Add(level, text)
            | Paragraph paragraph ->
                let text = (ParagraphModel.text paragraph).Trim()

                if text.Length > 0 then
                    pendingText.Add text
            | ListItem(_, paragraph) ->
                let text = (ParagraphModel.text paragraph).Trim()

                if text.Length > 0 then
                    pendingText.Add("- " + text)
            | Table table ->
                flushText ()
                flushTable table
            | OpaqueBlock _ -> ()

    flushText ()
    List.ofSeq results
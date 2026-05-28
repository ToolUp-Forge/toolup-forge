module KnowledgeBase.ServerNotes

open System
open System.Text.RegularExpressions
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.Chunking
open SharedTypes
open KnowledgeBase.ServerJsonHelpers
open KnowledgeBase.ServerExtractors

// ─── Note ingestion helpers ───────────────────────────────────────

/// Sanitise a note title into a usable filename component. Mirrors
/// the same rule used by `IngestNarrative` for narrative titles.
let sanitiseTitle (title: string) =
    title
    |> String.map (fun c ->
        if Char.IsLetterOrDigit c || c = '-' || c = ' ' then
            c
        else
            '-')

/// Split a note body into one chunk per paragraph (blank-line
/// separated). Short bodies (≤ 200 trimmed chars) collapse to a single
/// chunk so very small notes don't fragment unnecessarily.
let chunkNoteBody (body: string) : string list =
    let normalised = body.Replace("\r\n", "\n").Replace("\r", "\n")
    let trimmed = normalised.Trim()

    if trimmed.Length = 0 then
        []
    elif trimmed.Length <= 200 then
        [ trimmed ]
    else
        normalised.Split([| "\n\n" |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        |> Array.filter (fun p -> p.Length > 0)
        |> Array.toList

/// Produce the chunk content + source reference for paragraph `i` of a note.
let buildNoteChunk
    (docId: string)
    (fileName: string)
    (title: string)
    (paragraphCount: int)
    (i: int)
    (paragraph: string)
    : TextChunk * SourceReference =
    let header =
        if paragraphCount <= 1 then
            sprintf "Note: \"%s\"" title
        else
            sprintf "Note: \"%s\" — paragraph %d" title (i + 1)

    let src: SourceReference = {
        DocumentId = docId
        DocumentName = fileName
        FileType = "note"
        Location = Section title
        IndexedAt = DateTimeOffset.UtcNow
    }

    let chunk = makeChunk ChunkOrigin.Note fileName header paragraph src
    chunk, src

// ─── Standing AI context blob ─────────────────────────────────────
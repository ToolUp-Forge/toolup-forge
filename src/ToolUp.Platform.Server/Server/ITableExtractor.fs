module ToolUp.Platform.ITableExtractor

/// One table recovered from a document. Shape-compatible with
/// `ToolUp.RAG.Chunking.SheetData` — the RAG layer adapts records of this
/// type into `SheetData` for `chunkSpreadsheet`, so a PDF table and an
/// XLSX sheet land in the index with identical chunk shapes (citable row
/// ranges, repeated column headers, sheet/page header line).
///
/// Row indices are 1-based as they appear in the source document. The
/// header row is index 1; the first data row is 2 — matching how analysts
/// cite cells.
type ExtractedTable = {
    /// Optional source location. For PDFs this is the page number; for
    /// other formats (e.g. multi-table HTML) the implementation chooses a
    /// stable convention. `None` means "table position not known" — the
    /// chunk is still indexable, but loses precise round-trip provenance.
    Page: int option
    /// Column headers, in source column order.
    Headers: string array
    /// (1-based source row index, cell values) pairs. Preserved verbatim
    /// so retrieved chunks are citable back to a specific row range.
    Rows: (int * string array) list
}

/// Optional companion for table extraction. Sits inside the PDF / DOCX /
/// HTML extraction path: when the extractor finds a tabular region, it
/// hands the document off to `ExtractTables` which returns structured
/// `ExtractedTable` values. The RAG layer adapts those into `SheetData`
/// and chunks them with `chunkSpreadsheet`, preserving row identity and
/// column-header context — the same chunk shape XLSX/CSV ingestion uses.
///
/// Off by default. The no-op implementation in `ToolUp.RAG` returns the
/// empty list for every input — extractors that hit it fall through to
/// their default token-split chunking. Deployments that need structured
/// table extraction ship a companion under `src/TableExtractors/<Name>/`
/// (Camelot, Adobe PDF Extract, Azure Form Recognizer).
///
/// Phase 9c portability:
/// 1. Identity by value — inputs are `byte[]` + `mimeType: string`.
/// 2. Async at every boundary.
/// 3. No callback / supervision hooks.
/// 4. Stateless between calls.
/// 5. No cross-call ordering promises — each call independent. Within
///    a single call, returned tables are ordered by source position
///    (page-major), so the caller's downstream chunking is deterministic.
/// 6. Precision: table extraction is heuristic. The contract promises
///    a best-effort row/column shape, not pixel-accurate cell boundaries.
type ITableExtractor =
    /// Stable identifier for trace / observability — e.g. `"noop"`,
    /// `"camelot-lattice"`. Never serialised on the wire.
    abstract Name: string

    /// Find tables in `documentBytes`. Returns one `ExtractedTable` per
    /// detected region; an empty list means no tables were found (or the
    /// implementation is the no-op default). Implementations should
    /// return tables in source-position order.
    abstract ExtractTables: documentBytes: byte[] -> mimeType: string -> Async<ExtractedTable list>
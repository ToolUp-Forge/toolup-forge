module ToolUp.Platform.IOcrProvider

/// One page of OCR-extracted text. Page numbering is 1-based to round-trip
/// back into `KnowledgeBase.SourceReference.Location = Page n` without
/// rebasing on the consumer side.
type OcrPage = { PageNumber: int; Text: string }

/// Optional companion for OCR (Optical Character Recognition). Sits in
/// front of the PDF / image extraction path: when a document has no
/// extractable text layer (a scanned PDF, a photographed page), the
/// extractor falls back to `ExtractText` to recover its content.
///
/// Off by default. The no-op implementation in `ToolUp.RAG` returns
/// `IsScanned = false` for every input — extractors that hit it never
/// fall back, preserving the byte-equivalent behaviour of the
/// pre-Phase-14i path. Deployments that need OCR ship a companion under
/// `src/Ocr/<Name>/` (Tesseract, Azure Form Recognizer, AWS Textract).
///
/// Phase 9c portability:
/// 1. Identity by value — inputs are `byte[]` + `mimeType: string`. No
///    file paths, no streams, no live handles.
/// 2. Async at every boundary.
/// 3. No callback / supervision hooks.
/// 4. Stateless between calls — model files are immutable infrastructure,
///    not state. Implementations may cache loaded models internally; that
///    is implementation detail, not part of the contract.
/// 5. No cross-call ordering promises — each call independent.
/// 6. Precision: OCR is inherently lossy. The contract promises
///    best-effort extraction, not character-level fidelity.
type IOcrProvider =
    /// Stable identifier for trace / observability — e.g. `"noop"`,
    /// `"tesseract-eng"`. Never serialised on the wire.
    abstract Name: string

    /// Cheap heuristic — does this document look like a scanned image
    /// without a text layer? Implementations that don't actually run OCR
    /// (the no-op default) return `false` so callers stay on their normal
    /// text-extraction path.
    abstract IsScanned: documentBytes: byte[] -> mimeType: string -> Async<bool>

    /// Run OCR over the document. Returns one `OcrPage` per page in source
    /// order. Pages with no recovered text may be omitted entirely or
    /// returned with empty `Text` — callers should not assume page numbers
    /// are contiguous.
    abstract ExtractText: documentBytes: byte[] -> mimeType: string -> Async<OcrPage list>
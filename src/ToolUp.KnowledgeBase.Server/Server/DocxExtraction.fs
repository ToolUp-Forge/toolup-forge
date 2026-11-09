module KnowledgeBase.DocxExtraction

// ─── Which DOCX extractor a deployment composes ──────────────────
//
// The default DOCX extractor flattens a document to per-heading text. The
// structured one reads the `ToolUp.OpenXml` model: the full heading PATH
// (e.g. `Intro › Background`) keys each chunk, list items keep their bullet
// shape, and tables become citable table chunks. Both are `Extractor`s in
// `ServerExtractors`; the mode is a field of the extraction's deps
// (`ExtractDeps.Docx`) that selects one of them at the call site.
//
// GP 11: the structured mode is reachable only by explicit compose-time
// opt-in — the default stays on the flat extractor.

[<RequireQualifiedAccess>]
type DocxExtractionMode =
    | Flat
    | Structured

// The mode the two setters below compose. The setters take no deps, so
// what they set is held here and read once per upload, where the deps are
// built (`ExtractDeps.composed`); nothing else reads it.
let mutable private composed = DocxExtractionMode.Flat

/// Compose-time opt-in: route `.docx` ingestion through the structured
/// (heading-path-aware) extractor. Call once during composition, before
/// the server starts accepting uploads.
let enableStructuredDocxExtraction () =
    composed <- DocxExtractionMode.Structured

/// Restore the default flat extractor (primarily for tests).
let resetToFlatDocxExtraction () = composed <- DocxExtractionMode.Flat

/// The mode the deployment composed.
let internal composedMode () = composed
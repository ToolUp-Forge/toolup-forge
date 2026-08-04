module ToolUp.RAG.Chunking

open System
open System.Text.RegularExpressions
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Token counting ───────────────────────────────────────────────
//
// Chunkers need a budget to enforce — `MaxTokens`. We don't ship a real
// tokeniser by default to avoid adding `Microsoft.ML.Tokenizers` (or any
// other tokeniser package) as a hard dependency on every RAG deployment.
// The default `HeuristicTokenCounter` uses ≈4 characters per cl100k_base
// token, which is the well-known rule of thumb for English prose. It is
// conservative within ±25% for most input.
//
// Deployments wanting tight context-window utilisation (e.g. packing the
// last few hundred tokens of a 2k-token chunk into a constrained embedder)
// supply their own `ITokenCounter` — typically a thin wrapper over the
// embedder's actual tokeniser — via `ChunkingConfig.Counter`. The interface
// is deliberately minimal so a wrapper is one line.

/// Counts tokens in a string. Implementations are expected to be cheap
/// (called many times per chunk during sentence packing).
type ITokenCounter =
    abstract CountTokens: text: string -> int

/// Default heuristic counter: ≈4 characters per token. Suitable for
/// chunk-budget enforcement on English prose; less accurate on heavily
/// numeric content (each digit is typically its own token in BPE) and
/// CJK text (where the character count is closer to the token count).
/// For workloads dominated by either, supply a real tokeniser via
/// `ChunkingConfig.Counter`.
type HeuristicTokenCounter() =
    interface ITokenCounter with
        member _.CountTokens text =
            if String.IsNullOrEmpty text then
                0
            else
                (text.Length + 3) / 4

let defaultTokenCounter: ITokenCounter = HeuristicTokenCounter() :> ITokenCounter

// ─── Chunking config ──────────────────────────────────────────────

/// Chunker tuning. `MaxTokens` is the hard upper bound on a single chunk's
/// token count — the chunker never emits a chunk larger than this. `MinTokens`
/// is the floor for emitting a chunk at all (smaller fragments are dropped
/// or merged). `OverlapTokens` is the size of the context window carried
/// from the end of one chunk into the start of the next, preserving recall
/// across chunk boundaries — useful for prose where a sentence answering
/// the query may straddle a chunk break.
type ChunkingConfig = {
    /// Hard cap on tokens per chunk. Default 512 — sized to fit comfortably
    /// inside small-context embedders (e.g. `text-embedding-3-small` at
    /// 8k tokens) while leaving room for query / system overhead.
    MaxTokens: int
    /// Tokens of overlap carried between adjacent prose chunks. Default 64
    /// (≈2 sentences). Set to 0 for tabular data where row identity is
    /// what matters and overlap is meaningless.
    OverlapTokens: int
    /// Drop chunks smaller than this. Default 16 — below this, a chunk
    /// rarely carries a complete idea worth retrieving on its own.
    MinTokens: int
    /// Token counter implementation. Defaults to `HeuristicTokenCounter`.
    Counter: ITokenCounter
}

module ChunkingConfig =
    let defaults: ChunkingConfig = {
        MaxTokens = 512
        OverlapTokens = 64
        MinTokens = 16
        Counter = defaultTokenCounter
    }

    /// Tabular variant: zero overlap, smaller floor. Use for spreadsheet
    /// row-group chunking where each chunk's identity is the row range,
    /// not a sliding prose window.
    let tabular: ChunkingConfig = {
        defaults with
            OverlapTokens = 0
            MinTokens = 1
    }

    /// Cross-field validation for a `ChunkingConfig`. Returns the original
    /// config on success or a list of human-readable rule violations.
    /// Caught early, a misconfigured chunker silently produces empty or
    /// pathological chunks (e.g. `MaxTokens = 50, OverlapTokens = 100`
    /// leaves no forward progress per pass). `VectorisationHandler`
    /// authors should call this at handler-construction time and surface
    /// `Error`s as startup faults rather than letting them reach
    /// `splitByTokens`.
    let validate (config: ChunkingConfig) : Result<ChunkingConfig, string list> =
        let errors = ResizeArray<string>()

        if config.MaxTokens < 1 then
            errors.Add(sprintf "MaxTokens must be >= 1 (got %d)." config.MaxTokens)

        if config.MinTokens < 1 then
            errors.Add(sprintf "MinTokens must be >= 1 (got %d)." config.MinTokens)

        if config.OverlapTokens < 0 then
            errors.Add(sprintf "OverlapTokens must be >= 0 (got %d)." config.OverlapTokens)

        if config.MinTokens > config.MaxTokens then
            errors.Add(
                sprintf
                    "MinTokens (%d) must be <= MaxTokens (%d) — otherwise every chunk is rejected by the size floor."
                    config.MinTokens
                    config.MaxTokens
            )

        if config.OverlapTokens >= config.MaxTokens then
            errors.Add(
                sprintf
                    "OverlapTokens (%d) must be < MaxTokens (%d) — otherwise each chunk's overlap consumes the entire next chunk and no forward progress is made."
                    config.OverlapTokens
                    config.MaxTokens
            )

        if errors.Count = 0 then
            Ok config
        else
            Error(List.ofSeq errors)

// ─── Phase 505 — character-offset spans ───────────────────────────
//
// A citation used to be chunk-granular: "this answer came from somewhere
// inside chunk 7 of report.pdf". That is enough to *name* a source and not
// enough to *open it at the spot* — the chunk is hundreds of characters and
// the reader has to re-find the sentence the model actually used.
//
// The chunkers below therefore emit, alongside each chunk's text, the
// `[start, end)` character range of the SOURCE TEXT that chunk was derived
// from. The span is the primitive; carrying it through chunk metadata into
// the citation contract is what lets a preview surface scroll to and
// highlight the exact region.
//
// Two properties are deliberate:
//
//   * **The span slices the source, not the chunk.** A chunk's text is
//     whitespace-normalised (sentences are re-joined with single spaces),
//     so it is not necessarily byte-identical to the source region it came
//     from. The span's `Text` is always the true source slice — that is
//     what a highlight has to line up with.
//   * **A span is optional, and its absence is the pre-505 behaviour
//     exactly (GP 11).** A producer that does not know its offsets emits
//     `Span = None`, the metadata key is never stamped, and the citation
//     stays chunk-granular. Nothing is guessed to fill the gap (GP 9).
//
// The `*WithSpans` functions are the implementations; the original
// `splitByTokens` / `chunkSpreadsheet` remain as text-only projections of
// them, so every existing caller is untouched and cannot drift from the
// span-aware path.

/// One emitted chunk plus the source range it was derived from. `Span` is
/// `None` when the producer had no source text to anchor into.
type ChunkPiece = {
    Text: string
    Span: SourceSpan option
}

module ChunkPiece =
    /// Project back to the text-only shape the pre-505 chunkers returned.
    let text (piece: ChunkPiece) : string = piece.Text

/// Tighten a `[s, e)` slice of `text` past leading / trailing whitespace,
/// returning the narrowed bounds — the offset-preserving equivalent of
/// `String.Trim()`. `None` when the slice is entirely whitespace.
let private tightenBounds (text: string) (s: int) (e: int) : (int * int) option =
    let mutable a = s
    let mutable b = e

    while a < b && Char.IsWhiteSpace text[a] do
        a <- a + 1

    while b > a && Char.IsWhiteSpace text[b - 1] do
        b <- b - 1

    if a < b then Some(a, b) else None

/// Offset-preserving equivalent of
/// `slice.Split(separators, StringSplitOptions.RemoveEmptyEntries)` over
/// `text[fromIdx .. untilIdx)`. Returns `(token, start, end)` triples whose
/// offsets are absolute in `text`.
let private wordsWithOffsets
    (separators: char array)
    (text: string)
    (fromIdx: int)
    (untilIdx: int)
    : (string * int * int) list =
    let out = ResizeArray<string * int * int>()
    let mutable i = fromIdx

    while i < untilIdx do
        if Array.contains text[i] separators then
            i <- i + 1
        else
            let start = i

            while i < untilIdx && not (Array.contains text[i] separators) do
                i <- i + 1

            out.Add(text.Substring(start, i - start), start, i)

    List.ofSeq out

// ─── Sentence segmentation ────────────────────────────────────────
//
// Regex-based, no NLP dependency. Splits on `.`, `!`, `?` followed by
// whitespace, preserving the terminator with the preceding sentence.
// Imperfect on edge cases (Mr., e.g., decimals like 3.14) but cheap and
// good enough for chunk-boundary selection — token-level packing within
// the boundary still applies.

let private sentenceSplitter =
    Regex(@"(?<=[.!?])\s+(?=[A-Z""'\(\[])", RegexOptions.Compiled)

/// Sentence segmentation that keeps each sentence's absolute `[start, end)`
/// offsets in `text` (Phase 505). Offsets are what the chunk spans are
/// ultimately built from, so the split has to be done positionally rather
/// than by `Regex.Split` (which discards where each piece came from).
///
/// The regex matches the *separator* between sentences, so the sentences
/// are the gaps between successive matches; each gap is then tightened past
/// its whitespace so `text.Substring(start, end - start)` is exactly the
/// returned sentence.
let splitBySentenceWithOffsets (text: string) : (string * int * int) list =
    if String.IsNullOrWhiteSpace text then
        []
    else
        let gaps = ResizeArray<int * int>()
        let mutable cursor = 0

        for m in sentenceSplitter.Matches text do
            gaps.Add(cursor, m.Index)
            cursor <- m.Index + m.Length

        gaps.Add(cursor, text.Length)

        gaps
        |> Seq.choose (fun (s, e) -> tightenBounds text s e)
        |> Seq.map (fun (s, e) -> text.Substring(s, e - s), s, e)
        |> List.ofSeq

/// Split text into sentences using a simple terminator-based heuristic.
/// Each returned sentence retains its trailing punctuation. Empty inputs
/// return an empty list. Multi-line input is treated as one paragraph;
/// callers wanting paragraph-aware splitting should split on `\n\n` first.
let splitBySentence (text: string) : string list =
    splitBySentenceWithOffsets text |> List.map (fun (s, _, _) -> s)

// ─── Token-aware splitting ────────────────────────────────────────

/// Split a single block of text into chunks each within `config.MaxTokens`,
/// breaking at sentence boundaries when possible. Sentences longer than
/// `MaxTokens` are themselves split mid-text at a whitespace boundary —
/// pathological input (single-token sentences) hits a hard character split.
/// Adjacent chunks share `config.OverlapTokens` of trailing context.
///
/// Returns an empty list when the input is shorter than `MinTokens` (the
/// caller can decide whether to merge it with a sibling chunk or drop).
///
/// Phase 505: this is the span-aware implementation. Each emitted piece
/// carries the `[start, end)` range of `text` its units were drawn from —
/// the first unit's start to the last unit's end, so the span covers the
/// contiguous source region including any inter-sentence whitespace the
/// joined chunk text normalised away. `splitByTokens` is the text-only
/// projection of this function, so the two can never disagree.
let splitByTokensWithSpans (config: ChunkingConfig) (text: string) : ChunkPiece list =
    if String.IsNullOrWhiteSpace text then
        []
    else
        let counter = config.Counter
        let totalTokens = counter.CountTokens text

        if totalTokens < config.MinTokens then
            []
        elif totalTokens <= config.MaxTokens then
            match tightenBounds text 0 text.Length with
            | None -> []
            | Some(s, e) -> [
                {
                    Text = text.Substring(s, e - s)
                    Span = SourceSpan.create text s e
                }
              ]
        else
            let sentences = splitBySentenceWithOffsets text

            // If sentence split returned nothing useful, fall back to a
            // word-boundary split — guarantees we make progress even on
            // input with no punctuation.
            let units =
                if sentences.IsEmpty then
                    wordsWithOffsets [| ' '; '\t'; '\n' |] text 0 text.Length
                else
                    sentences

            let chunks = ResizeArray<ChunkPiece>()
            let current = ResizeArray<string * int * int>()
            let mutable currentTokens = 0

            // Build one chunk out of a run of offset-carrying units: the
            // text is the units re-joined with single spaces (unchanged
            // from the pre-505 behaviour), the span is the source range
            // from the first unit's start to the last unit's end.
            let pieceOf (units: (string * int * int) seq) : ChunkPiece =
                let arr = Array.ofSeq units
                let body = arr |> Array.map (fun (t, _, _) -> t) |> String.concat " "
                let s = arr |> Array.map (fun (_, a, _) -> a) |> Array.min
                let e = arr |> Array.map (fun (_, _, b) -> b) |> Array.max

                {
                    Text = body.Trim()
                    Span = SourceSpan.create text s e
                }

            // Flush the current accumulator into a chunk and seed the next
            // chunk with `OverlapTokens` worth of trailing units.
            let flush () =
                if current.Count > 0 then
                    chunks.Add(pieceOf current)

                    // Build the overlap seed by walking backwards from the
                    // last unit until we have at least OverlapTokens.
                    if config.OverlapTokens > 0 then
                        let seed = ResizeArray<string * int * int>()
                        let mutable seedTokens = 0
                        let mutable i = current.Count - 1

                        while i >= 0 && seedTokens < config.OverlapTokens do
                            let (unitText, _, _) as u = current[i]
                            seed.Insert(0, u)
                            seedTokens <- seedTokens + counter.CountTokens unitText
                            i <- i - 1

                        current.Clear()
                        current.AddRange(seed)
                        currentTokens <- seedTokens
                    else
                        current.Clear()
                        currentTokens <- 0

            for (unitText, unitStart, unitEnd) as unit in units do
                let unitTokens = counter.CountTokens unitText

                if unitTokens > config.MaxTokens then
                    // The unit itself is bigger than a whole chunk. Flush
                    // what we have, then split the unit at whitespace.
                    flush ()

                    let words = wordsWithOffsets [| ' '; '\t' |] text unitStart unitEnd

                    let inner = ResizeArray<string * int * int>()
                    let mutable innerTokens = 0

                    for (wordText, _, _) as w in words do
                        let wt = counter.CountTokens wordText

                        if innerTokens + wt > config.MaxTokens && inner.Count > 0 then
                            chunks.Add(pieceOf inner)
                            inner.Clear()
                            innerTokens <- 0

                        inner.Add w
                        innerTokens <- innerTokens + wt

                    if inner.Count > 0 then
                        // Push remainder back into the accumulator so the
                        // next sentence can join it (and pick up overlap).
                        current.AddRange(inner)
                        currentTokens <- innerTokens

                elif currentTokens + unitTokens > config.MaxTokens then
                    flush ()
                    current.Add unit
                    currentTokens <- currentTokens + unitTokens
                else
                    current.Add unit
                    currentTokens <- currentTokens + unitTokens

            flush ()

            chunks
            |> Seq.filter (fun c -> counter.CountTokens c.Text >= config.MinTokens || chunks.Count = 1)
            |> Seq.toList

/// Text-only projection of `splitByTokensWithSpans` — the pre-505 shape,
/// preserved verbatim for every existing caller.
let splitByTokens (config: ChunkingConfig) (text: string) : string list =
    splitByTokensWithSpans config text |> List.map ChunkPiece.text

// ─── Spreadsheet chunking ─────────────────────────────────────────

/// One chunkable sheet's worth of tabular data. Row indices are 1-based as
/// they appear in the source spreadsheet (the header row is index 1, the
/// first data row is 2 — matching how analysts cite cells).
type SheetData = {
    SheetName: string
    /// Column headers, in source column order. Repeated in every emitted
    /// chunk so the embedder has the schema context for any row range.
    Headers: string array
    /// (1-based source row index, cell values) pairs. The chunker preserves
    /// these indices in the chunk header so retrieved chunks are citable
    /// back to a specific row range.
    Rows: (int * string array) list
}

/// Format one row as `Col1: val | Col2: val | ...`. Repeats the header
/// labels with the values so a single chunk's text is self-describing —
/// without this, BM25 / dense retrieval over a chunk that lost its column
/// header context can't tell `"42"` is in the `Quantity` column.
let formatRow (headers: string array) (values: string array) =
    headers
    |> Array.mapi (fun i h ->
        let v = if i < values.Length then values[i] else ""
        sprintf "%s: %s" h v)
    |> String.concat " | "

/// Optional source-text context for spreadsheet span capture (Phase 505).
///
/// A row-group chunk's *text* is synthesised — `Sheet "…", rows N–M` plus a
/// re-rendered `Col: val | …` body — so it has no offsets of its own. The
/// offsets have to come from the producer that parsed the source, and only
/// some producers have them: a CSV extractor holds the raw text it parsed
/// and can say where each row started, whereas an XLSX extractor is handed
/// a decoded cell grid with no character stream behind it at all.
///
/// So this is a capability the producer either has or does not, and the
/// answer is honest either way: supply a context and the row-group chunks
/// carry real spans into the real source; supply `None` and they carry no
/// span, which is the pre-505 chunk-granular citation exactly (GP 11). No
/// third option where the chunker invents offsets into its own rendering.
type SpanContext = {
    /// The raw source text the offsets index into.
    SourceText: string
    /// 1-based source row index → `[start, end)` offsets of that row in
    /// `SourceText`. Rows absent from the map contribute no offsets; a
    /// chunk whose rows are all absent gets no span.
    RowOffsets: Map<int, int * int>
}

/// Span covering the rows in `indices`, from the earliest row start to the
/// latest row end. `None` when no row in the group has a recorded offset —
/// a partially-known group still anchors to the rows that are known, which
/// is strictly better than nothing and never wider than the group itself.
let private spanForRows (ctx: SpanContext option) (indices: int seq) : SourceSpan option =
    match ctx with
    | None -> None
    | Some c ->
        let known = indices |> Seq.choose c.RowOffsets.TryFind |> Array.ofSeq

        if known.Length = 0 then
            None
        else
            let s = known |> Array.map fst |> Array.min
            let e = known |> Array.map snd |> Array.max
            SourceSpan.create c.SourceText s e

/// Chunk a `SheetData` into prose-form chunks of `<= MaxTokens` each.
/// Each chunk has the shape:
///
///     Sheet "Name", rows N–M of T
///     Columns: A, B, C
///     A: 1 | B: 2 | C: 3
///     A: 4 | B: 5 | C: 6
///     ...
///
/// The header line (`Sheet "..."`, `rows N–M of T`) is emitted verbatim and
/// counts toward the chunk's token budget. Rows are packed greedily; a row
/// that would overflow the budget on its own (a very wide sheet) is still
/// emitted — the embedder will truncate at its own max input, but the row
/// is preserved as a citable unit.
///
/// Phase 505: each piece additionally carries the source span of its row
/// range when `ctx` supplies row offsets; `ctx = None` (every shipped
/// caller today) yields `Span = None` and byte-identical chunk text.
let chunkSpreadsheetWithSpans (config: ChunkingConfig) (ctx: SpanContext option) (data: SheetData) : ChunkPiece list =
    if data.Rows.IsEmpty || data.Headers.Length = 0 then
        []
    else
        let counter = config.Counter
        let totalRows = data.Rows.Length
        let columnsLine = sprintf "Columns: %s" (String.concat ", " data.Headers)

        let chunks = ResizeArray<ChunkPiece>()
        let buffer = ResizeArray<string * int>() // (formatted row text, source row index)
        let mutable bufferTokens = 0

        // Header line varies with the row range — recompute on each flush.
        let buildHeaderLine (startRow: int) (endRow: int) =
            sprintf "Sheet \"%s\", rows %d\u2013%d of %d" data.SheetName startRow endRow totalRows

        let flush () =
            if buffer.Count > 0 then
                let startRow = snd buffer[0]
                let endRow = snd buffer[buffer.Count - 1]
                let headerLine = buildHeaderLine startRow endRow
                let body = buffer |> Seq.map fst |> String.concat "\n"
                let text = String.concat "\n" [ headerLine; columnsLine; body ]

                chunks.Add {
                    Text = text
                    Span = spanForRows ctx (buffer |> Seq.map snd)
                }

                buffer.Clear()
                bufferTokens <- 0

        // Pre-compute the fixed overhead — Sheet header (worst-case width)
        // + Columns line — so per-row packing knows the available budget.
        // Use a pessimistic rendering of the sheet header (using the last
        // row index as the worst case) so the budget is never undercounted.
        let pessimisticHeaderTokens =
            let line = buildHeaderLine totalRows totalRows
            counter.CountTokens line + counter.CountTokens columnsLine + 2

        let rowBudget = config.MaxTokens - pessimisticHeaderTokens

        if rowBudget <= 0 then
            // Pathological case: header alone exceeds the budget. Emit each
            // row as its own chunk and let the embedder cope.
            for (idx, values) in data.Rows do
                let row = formatRow data.Headers values
                let line = buildHeaderLine idx idx
                let text = String.concat "\n" [ line; columnsLine; row ]

                chunks.Add {
                    Text = text
                    Span = spanForRows ctx [ idx ]
                }
        else
            for (idx, values) in data.Rows do
                let row = formatRow data.Headers values
                let rowTokens = counter.CountTokens row + 1 // +1 for the joining newline

                if bufferTokens + rowTokens > rowBudget && buffer.Count > 0 then
                    flush ()

                buffer.Add((row, idx))
                bufferTokens <- bufferTokens + rowTokens

            flush ()

        chunks |> Seq.toList

/// Text-only projection of `chunkSpreadsheetWithSpans` — the pre-505 shape,
/// preserved verbatim for every existing caller.
let chunkSpreadsheet (config: ChunkingConfig) (data: SheetData) : string list =
    chunkSpreadsheetWithSpans config None data |> List.map ChunkPiece.text

// ─── Optional contextual header ───────────────────────────────────

/// Optional: prepend a short contextual summary to each chunk via a
/// summariser companion. Off by default — summarisation is a per-chunk
/// AI call, expensive at ingestion time. When `summariser = None` the
/// caller should not wrap chunks with this and pay nothing.
///
/// Pattern is "anthropic-contextual-retrieval": each chunk gets a 50-100
/// token preamble describing its place in the document, which lifts
/// retrieval recall on documents where chunks are otherwise indistinguishable
/// (boilerplate-heavy contracts, repetitive financial tables). See
/// `ITextSummariser` in core for the extension point.
let withContextualHeader
    (summariser: ToolUp.Platform.ITextSummariser.ITextSummariser option)
    (documentContext: string)
    (chunk: string)
    : Async<string> =
    async {
        match summariser with
        | None -> return chunk
        | Some s ->
            let! header = s.Summarise documentContext chunk
            return String.concat "\n" [ header; chunk ]
    }
module ToolUp.RAG.Chunking

open System
open System.Text.RegularExpressions

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

// ─── Sentence segmentation ────────────────────────────────────────
//
// Regex-based, no NLP dependency. Splits on `.`, `!`, `?` followed by
// whitespace, preserving the terminator with the preceding sentence.
// Imperfect on edge cases (Mr., e.g., decimals like 3.14) but cheap and
// good enough for chunk-boundary selection — token-level packing within
// the boundary still applies.

let private sentenceSplitter =
    Regex(@"(?<=[.!?])\s+(?=[A-Z""'\(\[])", RegexOptions.Compiled)

/// Split text into sentences using a simple terminator-based heuristic.
/// Each returned sentence retains its trailing punctuation. Empty inputs
/// return an empty list. Multi-line input is treated as one paragraph;
/// callers wanting paragraph-aware splitting should split on `\n\n` first.
let splitBySentence (text: string) : string list =
    if String.IsNullOrWhiteSpace text then
        []
    else
        sentenceSplitter.Split(text.Trim())
        |> Array.filter (fun s -> s.Trim().Length > 0)
        |> Array.toList

// ─── Token-aware splitting ────────────────────────────────────────

/// Split a single block of text into chunks each within `config.MaxTokens`,
/// breaking at sentence boundaries when possible. Sentences longer than
/// `MaxTokens` are themselves split mid-text at a whitespace boundary —
/// pathological input (single-token sentences) hits a hard character split.
/// Adjacent chunks share `config.OverlapTokens` of trailing context.
///
/// Returns an empty list when the input is shorter than `MinTokens` (the
/// caller can decide whether to merge it with a sibling chunk or drop).
let splitByTokens (config: ChunkingConfig) (text: string) : string list =
    if String.IsNullOrWhiteSpace text then
        []
    else
        let counter = config.Counter
        let totalTokens = counter.CountTokens text

        if totalTokens < config.MinTokens then
            []
        elif totalTokens <= config.MaxTokens then
            [ text.Trim() ]
        else
            let sentences = splitBySentence text

            // If sentence split returned nothing useful, fall back to a
            // word-boundary split — guarantees we make progress even on
            // input with no punctuation.
            let units =
                if sentences.IsEmpty then
                    text.Split([| ' '; '\t'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.toList
                else
                    sentences

            let chunks = ResizeArray<string>()
            let current = ResizeArray<string>()
            let mutable currentTokens = 0

            // Flush the current accumulator into a chunk and seed the next
            // chunk with `OverlapTokens` worth of trailing units.
            let flush () =
                if current.Count > 0 then
                    let text = String.concat " " current
                    chunks.Add(text.Trim())

                    // Build the overlap seed by walking backwards from the
                    // last unit until we have at least OverlapTokens.
                    if config.OverlapTokens > 0 then
                        let seed = ResizeArray<string>()
                        let mutable seedTokens = 0
                        let mutable i = current.Count - 1

                        while i >= 0 && seedTokens < config.OverlapTokens do
                            let u = current[i]
                            seed.Insert(0, u)
                            seedTokens <- seedTokens + counter.CountTokens u
                            i <- i - 1

                        current.Clear()
                        current.AddRange(seed)
                        currentTokens <- seedTokens
                    else
                        current.Clear()
                        currentTokens <- 0

            for unit in units do
                let unitTokens = counter.CountTokens unit

                if unitTokens > config.MaxTokens then
                    // The unit itself is bigger than a whole chunk. Flush
                    // what we have, then split the unit at whitespace.
                    flush ()

                    let words =
                        unit.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.toList

                    let inner = ResizeArray<string>()
                    let mutable innerTokens = 0

                    for w in words do
                        let wt = counter.CountTokens w

                        if innerTokens + wt > config.MaxTokens && inner.Count > 0 then
                            chunks.Add((String.concat " " inner).Trim())
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
            |> Seq.filter (fun c -> counter.CountTokens c >= config.MinTokens || chunks.Count = 1)
            |> Seq.toList

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
let chunkSpreadsheet (config: ChunkingConfig) (data: SheetData) : string list =
    if data.Rows.IsEmpty || data.Headers.Length = 0 then
        []
    else
        let counter = config.Counter
        let totalRows = data.Rows.Length
        let columnsLine = sprintf "Columns: %s" (String.concat ", " data.Headers)

        let chunks = ResizeArray<string>()
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
                chunks.Add(text)
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
                chunks.Add(text)
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
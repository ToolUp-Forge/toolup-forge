// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `ISparseAnalyzer` implementation for space-delimited European languages:
/// stemming (English, via the Snowball/Porter2 algorithm), stop-word removal,
/// and diacritic folding.
///
/// **Posture: production-ready, in-process, stateless.** No I/O, no external
/// service, no state between calls — the analyzer is a pure function of its
/// options and the input string, so it satisfies GP 12 rule 4 trivially and
/// contributes `CompanionCapability.identity` (pure / deterministic /
/// distributed-ready), which is why it declares no posture descriptor: the
/// undeclared default is already the correct one.
///
/// **What is and is not shipped.** The built-in stemmer covers ENGLISH. The
/// other six languages get stop-word removal and diacritic folding, which is
/// most of the query-side win and none of the morphology; a per-language
/// stemmer plugs in through `CustomStemmer` without touching this package.
/// `BuiltInStemmer` on a non-English language is refused at `create` rather
/// than silently degrading to no stemming — an analyzer that quietly does less
/// than asked shows up as unexplained recall, months later.
module ToolUp.SparseIndices.Snowball.SnowballAnalyzer

open System
open System.Globalization
open System.Text
open ToolUp.RAG.SparseAnalysis
open ToolUp.SparseIndices.Snowball.StopWords

/// How (and whether) terms are reduced to a stem.
type StemmingMode =
    /// Leave surface forms alone. Stop-word removal and folding still apply.
    | NoStemming
    /// The shipped Snowball/Porter2 English stemmer. English only.
    | BuiltInStemmer
    /// A caller-supplied stemmer. `id` becomes part of the analyzer id, so it
    /// MUST change whenever the function's behaviour does — otherwise a
    /// persisted index keeps terms from the old one and the mismatch is
    /// invisible.
    | CustomStemmer of id: string * stem: (string -> string)

/// Analyzer configuration. Every field participates in the analyzer id, so any
/// change invalidates a persisted snapshot and forces a re-analysis rather
/// than mixing vocabularies.
type SnowballOptions = {
    Language: Language
    Stemming: StemmingMode
    /// Drop the language's stop words. Applied BEFORE folding, so a folding
    /// and a non-folding deployment drop exactly the same words.
    RemoveStopWords: bool
    /// Strip combining marks (`café` → `cafe`, `schön` → `schon`). Makes a
    /// query typed without accents match text that has them, which is the
    /// common case for a keyboard that does not make them easy.
    FoldDiacritics: bool
    /// Drop terms shorter than this after analysis. Default 1 (inert). Raise
    /// it for corpora dense with single-letter noise; note it applies to the
    /// query side too, so a legitimate one-character term stops being
    /// searchable.
    MinTermLength: int
}

module SnowballOptions =

    /// Defaults for a language: stop-word removal and folding on, and the
    /// built-in stemmer only where one exists (English). Picking these per
    /// language is what keeps `create` from having to refuse the obvious call.
    let forLanguage (language: Language) : SnowballOptions = {
        Language = language
        Stemming = (if language = English then BuiltInStemmer else NoStemming)
        RemoveStopWords = true
        FoldDiacritics = true
        MinTermLength = 1
    }

    /// English with the built-in stemmer — the common case.
    let english = forLanguage English

// ─── Analysis ─────────────────────────────────────────────────────

let private foldDiacritics (term: string) =
    let decomposed = term.Normalize NormalizationForm.FormD
    let sb = StringBuilder decomposed.Length

    for c in decomposed do
        if CharUnicodeInfo.GetUnicodeCategory c <> UnicodeCategory.NonSpacingMark then
            sb.Append c |> ignore

    sb.ToString().Normalize NormalizationForm.FormC

let private stemmerFor (mode: StemmingMode) : (string -> string) option =
    match mode with
    | NoStemming -> None
    | BuiltInStemmer -> Some Porter2.stem
    | CustomStemmer(_, stem) -> Some stem

let private stemmingTag (mode: StemmingMode) =
    match mode with
    | NoStemming -> "nostem"
    | BuiltInStemmer -> "porter2"
    | CustomStemmer(id, _) -> "custom:" + id

/// The analyzer id. Deliberately readable — it appears verbatim in the
/// persisted snapshot and in the log line the index emits when it re-analyses
/// a corpus, and "why did my index rebuild" is answered by reading it.
let idFor (options: SnowballOptions) =
    let parts = [
        "snowball"
        tag options.Language
        stemmingTag options.Stemming
        (if options.RemoveStopWords then "stop" else "nostop")
        (if options.FoldDiacritics then "fold" else "nofold")
        sprintf "min%d" options.MinTermLength
    ]

    String.Join("+", parts)

let private analyseWith (options: SnowballOptions) =
    let stopWords =
        if options.RemoveStopWords then
            StopWords.forLanguage options.Language
        else
            Set.empty

    let stem = stemmerFor options.Stemming

    fun (text: string) ->
        let words = tokeniseWords text

        // Stop-word removal runs on the raw lower-cased token, before folding
        // or stemming: the lists are written in the language's own orthography
        // and `être` must match the list entry whether or not this deployment
        // folds.
        let withoutStopWords =
            if stopWords.IsEmpty then
                words
            else
                match words |> List.filter (fun w -> not (stopWords.Contains w)) with
                // A query made entirely of stop words ("to be or not to be")
                // would otherwise analyse to nothing and return zero results —
                // worse than the terms being low-value. Keep the original
                // terms in that case. Symmetric by construction: this is one
                // function, used on both sides.
                | [] -> words
                | kept -> kept

        withoutStopWords
        |> List.map (fun w ->
            let folded = if options.FoldDiacritics then foldDiacritics w else w

            match stem with
            | Some f -> f folded
            | None -> folded)
        |> List.filter (fun w -> w.Length >= options.MinTermLength)

// ─── Construction ─────────────────────────────────────────────────

/// Raised when the requested configuration cannot be served. Named rather than
/// a bare `invalidArg` so a composition root can catch this specifically.
exception SnowballAnalyzerConfigurationException of message: string

let private fail message =
    raise (SnowballAnalyzerConfigurationException message)

/// Build the analyzer. Options are validated here — before any index is built
/// against them — because the alternative is discovering the misconfiguration
/// as a retrieval-quality regression rather than as an error.
let create (options: SnowballOptions) : ISparseAnalyzer =
    match options.Stemming with
    | BuiltInStemmer when options.Language <> English ->
        fail (
            sprintf
                "ToolUp.SparseIndices.Snowball ships a built-in stemmer for English only; language '%s' requested BuiltInStemmer. Either use NoStemming (stop-word removal and diacritic folding still apply) or supply CustomStemmer(id, stem) with a stemmer for that language."
                (tag options.Language)
        )
    | CustomStemmer(id, _) when String.IsNullOrWhiteSpace id ->
        fail
            "CustomStemmer requires a non-empty id — it is folded into the analyzer id, which keys the persisted index snapshot; an empty id would let two different stemmers share one index."
    | _ -> ()

    if options.MinTermLength < 1 then
        fail (sprintf "MinTermLength must be at least 1 (got %d)." options.MinTermLength)

    let analyseText = analyseWith options
    let analyzerId = idFor options

    { new ISparseAnalyzer with
        member _.Id = analyzerId
        member _.Analyse text = analyseText text
    }

/// `create (SnowballOptions.forLanguage language)` — the one-liner for the
/// common case.
let forLanguage (language: Language) : ISparseAnalyzer =
    create (SnowballOptions.forLanguage language)

/// English with stemming, stop-word removal and folding.
let english () : ISparseAnalyzer = create SnowballOptions.english
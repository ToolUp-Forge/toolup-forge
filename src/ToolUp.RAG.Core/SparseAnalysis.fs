// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 501 — the analyzer seam for the sparse (lexical) retrieval index.
///
/// The in-tree BM25 index tokenises on Unicode letter/digit runs and
/// lower-cases. That is correct for English business prose and code-like
/// identifiers and wrong for almost everything else: an inflected language
/// splits one concept across a dozen surface forms, and a non-space-delimited
/// script (Chinese, Japanese, Thai) collapses a whole clause into a single
/// "word". `ISparseAnalyzer` is the seam a language-aware companion plugs
/// into; the shipped default (`SparseAnalysis.identity`) reproduces today's
/// behaviour exactly, so an existing deployment that upgrades is byte-for-byte
/// unchanged until it composes one (GP 11).
///
/// **Why the interface is synchronous.** GP 12 rule 2 ("async at every
/// boundary") governs infrastructure interfaces a distributed framework could
/// plausibly implement. An analyzer is a pure function on a string — no I/O, no
/// identity, no state between calls — sitting on the per-chunk index hot path
/// and the per-query path. It takes the same documented exemption as
/// `IMetricsSink`: sync, write-only-shaped, nothing to await. An analyzer that
/// genuinely needs I/O (a hosted segmenter, a remote dictionary) belongs behind
/// its own async seam, not here — it would put a network round-trip inside
/// every chunk indexed.
module ToolUp.RAG.SparseAnalysis

open System
open System.Text.RegularExpressions

// ─── The seam ─────────────────────────────────────────────────────

/// Tokenisation + normalisation strategy for the sparse index. One analyzer
/// serves BOTH the index-time and query-time paths — see `AnalysedText` for
/// why that is structural rather than conventional.
type ISparseAnalyzer =
    /// Stable identifier for this analyzer AND its configuration. It is
    /// recorded in the index's persisted snapshot and compared on load, so
    /// two analyzers that would produce different terms MUST report different
    /// ids. A companion that exposes options folds them into the id (e.g.
    /// `snowball:en+stem+stop`); an id that ignores its own options silently
    /// keeps stale terms across a configuration change, which reads as a
    /// mysterious recall collapse rather than as an error.
    abstract Id: string

    /// Analyse raw text into the term sequence the index stores (index time)
    /// or searches for (query time). Order is preserved for callers that care;
    /// BM25 itself is order-insensitive. Must be a pure function of `text` —
    /// the index calls it on the ingestion hot path and on every query, and a
    /// stateful analyzer would make the two sides diverge.
    abstract Analyse: text: string -> string list

// ─── Analysed text — the symmetry guarantee ───────────────────────

/// Terms produced by an analyzer, tagged with the id of the analyzer that
/// produced them.
///
/// The constructor is **private to this module**, so the only way to obtain an
/// `AnalysedText` anywhere in the estate is `SparseAnalysis.analyse`, which
/// takes an `ISparseAnalyzer`. The BM25 index's internal term paths accept
/// nothing else — there is no overload taking a `string list`. That is what
/// makes "the same analyzer runs at index time and at query time" a property
/// of the types rather than a rule someone has to remember: a caller cannot
/// hand-roll terms for one side, and the carried `AnalyzerId` lets the index
/// refuse terms from a different analyzer (and lets a persisted snapshot
/// detect that it was written by one).
///
/// The failure this prevents is silent: an index built with stemming and
/// queried without it simply stops matching, returns fewer results, and looks
/// like a corpus problem.
[<Sealed>]
type AnalysedText private (analyzerId: string, terms: string list) =
    /// `ISparseAnalyzer.Id` of the analyzer that produced these terms.
    member _.AnalyzerId = analyzerId

    /// The analysed terms, in source order.
    member _.Terms = terms

    /// Assembly-internal factory. `private` on the primary constructor makes
    /// it reachable only from inside this type, so `analyse` below needs a
    /// door; `internal` keeps that door shut to every OTHER assembly — the
    /// index, the companions and the test packs all live outside this one.
    static member internal Of(analyzerId: string, terms: string list) = AnalysedText(analyzerId, terms)

/// Run `analyzer` over `text`. The sole producer of `AnalysedText`, so every
/// term that reaches the index passes through here with an analyzer in hand.
let analyse (analyzer: ISparseAnalyzer) (text: string) : AnalysedText =
    AnalysedText.Of(analyzer.Id, analyzer.Analyse text)

// ─── The default (identity) analyzer ──────────────────────────────

/// Id of the shipped default analyzer. "Identity" is with respect to the
/// pre-Phase-501 index behaviour — the analyzer still tokenises and
/// lower-cases; it applies no *language* processing. A snapshot written before
/// Phase 501 carries no analyzer id and is treated as this one, because that
/// is exactly what wrote it.
[<Literal>]
let IdentityAnalyzerId = "identity"

let private tokenPattern = Regex(@"[\p{L}\p{N}]+", RegexOptions.Compiled)

/// Lower-case and split into Unicode-letter / digit runs. Punctuation,
/// whitespace, and symbols are dropped (`SKU-1234` becomes `["sku"; "1234"]`).
/// This is the pre-Phase-501 tokenisation, verbatim; companions build on it
/// rather than reimplementing it, so the word-boundary rule stays one
/// definition.
let tokeniseWords (text: string) : string list =
    if String.IsNullOrEmpty text then
        []
    else
        [
            for m in tokenPattern.Matches(text) do
                m.Value.ToLowerInvariant()
        ]

/// The default analyzer: today's behaviour, no language processing. Composed
/// when a deployment names none, so an upgrade changes nothing (GP 11).
let identity: ISparseAnalyzer =
    { new ISparseAnalyzer with
        member _.Id = IdentityAnalyzerId
        member _.Analyse text = tokeniseWords text
    }

/// Build an analyzer from a function. For a deployment with one local rule
/// (a synonym map, a domain-specific token split) that does not warrant a
/// companion package. `analyzerId` must change whenever `analyseText` would
/// produce different terms — see `ISparseAnalyzer.Id`.
let create (analyzerId: string) (analyseText: string -> string list) : ISparseAnalyzer =
    if String.IsNullOrWhiteSpace analyzerId then
        invalidArg (nameof analyzerId) "An analyzer id must be non-empty — it keys the persisted index snapshot."

    { new ISparseAnalyzer with
        member _.Id = analyzerId
        member _.Analyse text = analyseText text
    }
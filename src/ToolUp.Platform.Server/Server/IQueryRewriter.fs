module ToolUp.Platform.IQueryRewriter

/// Outcome of a single conversation-aware rewrite attempt (Phase 506).
///
/// A rewriter is asked "given these prior turns, does this query stand on
/// its own?" and answers with one of two shapes. There is deliberately no
/// failure case: a rewriter that cannot reach its model raises, and the
/// pipeline degrades to the raw query — a rewrite is an enhancement, never
/// a precondition for retrieval.
type QueryRewrite =
    /// The query already stands on its own — no anaphora to resolve, so no
    /// rewrite (and, for a provider-backed rewriter, no provider call).
    /// The pipeline searches the caller's original query unchanged.
    | QuerySelfContained
    /// The query was resolved against the conversation history. The payload
    /// is the standalone query the pipeline should search with. A rewriter
    /// returning whitespace, or the original string, is treated by the
    /// pipeline as `QuerySelfContained` — an empty rewrite must never
    /// replace a usable query.
    | QueryRewritten of rewritten: string

/// Resolves a follow-up query ("what about it?", "and the second one?")
/// into a standalone query, using the prior conversation turns the caller
/// supplied on `RetrievalRequest.History`.
///
/// Retrieval embeds the query text; a follow-up whose subject lives only in
/// the previous turn embeds to nothing useful, so multi-turn recall
/// collapses even when the corpus holds the answer. This seam is the stage
/// that closes that gap, and it is opt-in: no rewriter wired ⇒ the pipeline
/// is byte-for-byte its pre-506 self (GP 11 / GP 13).
///
/// Implementations live wherever their dependency does — the shipped
/// provider-backed default (`ToolUp.RAG.ProviderQueryRewriter`) sits beside
/// the RAG pipeline because it needs an `IAIProvider`; a deployment with a
/// local classifier or a rules table implements the same two members with
/// no provider at all.
///
/// Satisfies the six portability rules:
/// 1. Identity by value — `string` in, `QueryRewrite` (a DU of strings) out.
/// 2. Async at every boundary — `Rewrite` returns `Async<_>`.
/// 3. No callback / supervision hooks; bounding is the caller's
///    `RetrievalPipelineOptions.QueryRewriteTimeoutMs`, expressed as data.
/// 4. Stateless between calls — every call is fully parameterised; a
///    rewriter must not carry conversation state across invocations.
/// 5. No cross-call ordering promises.
/// 6. Precision: none claimed — a rewrite is best-effort by contract, and
///    the caller degrades to the raw query on any failure.
type IQueryRewriter =
    /// Stable identifier for traces and eval attribution — e.g.
    /// `"provider-rewrite"`, `"rules-v1"`. Never serialised on the wire.
    abstract Name: string

    /// Resolve `query` against `history` (prior conversation turns, oldest
    /// first, already bounded by the caller).
    ///
    /// Implementations MUST:
    /// - Return `QuerySelfContained` when the query needs no history, and
    ///   spend nothing doing so where the check is decidable locally — a
    ///   self-contained query must not cost a provider call.
    /// - Return a *standalone* query in `QueryRewritten` — one that would
    ///   retrieve correctly with no history at all.
    /// - Never return a rewrite that drops the caller's intent. Widening
    ///   ("revenue" → "revenue, profit and headcount") costs precision on
    ///   every subsequent stage.
    ///
    /// Implementations MAY raise. The caller treats any exception — and any
    /// overrun of its own timeout — as "no rewrite", searches the raw
    /// query, and records the degradation on the retrieval trace.
    abstract Rewrite: query: string -> history: string list -> Async<QueryRewrite>

/// Stable trace-value literals for `RetrievalTrace.RewriteDecision`.
/// Stringly-typed for the same reason `RetrievalTrace.Stages` is: the
/// retrieval-trace event is read by admin UIs and eval harnesses that do
/// not reference the SDK. Match on these constants rather than the literal
/// text.
module QueryRewriteDecision =
    /// A rewriter ran and judged the query self-contained. The raw query
    /// was searched; no `RewrittenQueryHash` accompanies this decision.
    [<Literal>]
    let SelfContained = "SelfContained"

    /// A rewriter produced a standalone query, and that query was searched.
    /// `RetrievalTrace.RewrittenQueryHash` carries its SHA256.
    [<Literal>]
    let Rewritten = "Rewritten"

    /// A rewriter was wired and ran, but raised or exceeded the configured
    /// timeout. Retrieval proceeded on the raw query — the degradation the
    /// contract promises, made visible rather than silent.
    [<Literal>]
    let Failed = "Failed"

/// Local, provider-free "does this query stand on its own?" heuristic.
///
/// Lives on the seam rather than inside one implementation so every
/// rewriter — and the contract pack — classifies the same way, and so a
/// deployment can reuse the gate in front of a rewriter of its own. It is
/// deliberately a *cheap gate*, not a parser: its job is to keep the
/// overwhelmingly common self-contained query away from a paid model call,
/// and to be honest about the rest.
///
/// Bias: when in doubt, say "follow-up". A false positive costs one bounded
/// provider call that returns `QuerySelfContained`; a false negative costs
/// the retrieval the whole stage exists to fix.
module QueryDependence =

    /// Tokens that, appearing as a standalone word, indicate the query
    /// refers to something named earlier rather than in the query itself.
    /// Pronouns, demonstratives and bare ordinals ("the second one").
    let private anaphora =
        set [
            "it"
            "its"
            "it's"
            "they"
            "them"
            "their"
            "theirs"
            "he"
            "him"
            "his"
            "she"
            "her"
            "hers"
            "that"
            "this"
            "those"
            "these"
            "there"
            "then"
            "one"
            "ones"
            "same"
            "former"
            "latter"
            "above"
            "first"
            "second"
            "third"
            "fourth"
            "fifth"
            "last"
            "next"
            "previous"
            "other"
            "another"
            "else"
        ]

    /// Openers that make the query a continuation of the prior turn even
    /// when it carries no pronoun ("and the pricing?", "also for Q3").
    ///
    /// Deliberately excludes the interrogatives (`what` / `how` / `which`
    /// / `who` / `when` / `where`): a question word opens most standalone
    /// searches too, so treating it as a continuation marker would classify
    /// "what is the widget retention policy?" as a follow-up and spend a
    /// provider call on every well-formed question in the deployment. A
    /// bare interrogative ("why?", "how?") is still caught — by the
    /// short-query rule below, which is what actually distinguishes it.
    let private continuationOpeners =
        set [ "and"; "but"; "so"; "also"; "plus"; "ok"; "okay"; "yeah"; "yes"; "no" ]

    let private isWordChar (c: char) =
        System.Char.IsLetterOrDigit c || c = '\''

    /// Lowercased word tokens of `text`. Apostrophes are kept inside a word
    /// so `it's` stays one token and matches the `anaphora` entry.
    let private words (text: string) : string list =
        if System.String.IsNullOrWhiteSpace text then
            []
        else
            let sb = System.Text.StringBuilder()
            let acc = ResizeArray<string>()

            for c in text do
                if isWordChar c then
                    sb.Append(System.Char.ToLowerInvariant c) |> ignore
                elif sb.Length > 0 then
                    acc.Add(sb.ToString())
                    sb.Clear() |> ignore

            if sb.Length > 0 then
                acc.Add(sb.ToString())

            List.ofSeq acc

    /// Word count below which a query is treated as a follow-up regardless
    /// of its vocabulary. A three-word query is rarely a standalone search
    /// intent in a conversation that already has turns behind it.
    [<Literal>]
    let ShortQueryWordCount = 4

    /// Does this query appear to depend on prior conversation turns?
    ///
    /// `true` for anaphoric ("what about it?"), ordinal ("the second one"),
    /// continuation-opener ("and the pricing?") and very short queries;
    /// `false` for a query that names its own subject.
    ///
    /// History-independent by design — the caller only consults this when
    /// it actually has history, and a rewriter that mixed the two would be
    /// harder to test than the two rules separately.
    let isFollowUp (query: string) : bool =
        match words query with
        | [] -> false // Nothing to rewrite; the pipeline handles the empty query.
        | tokens ->
            let short = List.length tokens < ShortQueryWordCount
            let anaphoric = tokens |> List.exists anaphora.Contains

            let continuation =
                match tokens with
                | head :: _ -> continuationOpeners.Contains head
                | [] -> false

            short || anaphoric || continuation

    /// The complement of `isFollowUp`, for call sites that read better in
    /// the positive.
    let isSelfContained (query: string) : bool = not (isFollowUp query)
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IRetrievalPipeline

// ─── Phase 91 — RAG-backed answer pages ──────────────────────────────
//
// `RagAnswerSource` turns the retrieval pipeline into a public/gated SSR
// surface: a slug under a configured prefix (e.g. `/answers/how-do-i-
// deploy`) is resolved at request time by running
// `IRetrievalPipeline.Retrieve` and rendering a *cited* `Narrative`
// answer. It is an ordinary `IContentSource` (Phase 83), so it composes
// via `withContentSource` and caches via the Phase 84 render cache
// (answer pages are expensive — set a `withRenderCacheDefaultPolicy`).
//
// **Grounding (the load-bearing safety property).** By default the answer
// is EXTRACTIVE — the retrieved chunks rendered as cited blockquotes plus
// the top chunk as the lead. Extractive answers are grounded *by
// construction*: every word shown came from a retrieved source, so there
// is no hallucination surface and no LLM dependency (GP 13). A deployment
// that wants synthesized prose wires a `RagSynthesisHook` to its own
// `IAIProvider`; the SDK never calls an LLM itself.
//
// **`StrictlyGrounded`.** With `StrictGrounding = true`, a query that
// retrieves no match at or above `MinScore` renders a graceful *no-answer*
// page — a Warning callout stating the KB has nothing relevant — rather
// than any answer at all, even when a synthesis hook is present. This is
// the SSR analogue of RAG's `GroundingMode.StrictlyGrounded`: the surface
// refuses to speculate. (The contract is modelled locally rather than by
// referencing `ToolUp.RAG.Server.GroundingMode` so PublicRendering's
// dependency graph stays free of the RAG companion — GP 1.)
//
// **Scope isolation (GP 4).** Retrieval is run with the request's resolved
// `AccessContext`, so the pipeline's own team-scope validation applies — a
// caller only ever retrieves chunks from scopes it is authorised for.

/// Optional answer-synthesis hook. Given the user's query and the
/// retrieved matches, produce synthesized answer prose. `None` (the
/// default) yields the extractive answer (grounded by construction).
type RagSynthesisHook = string -> VectorMatch list -> Async<string>

/// Compose-time configuration for a `RagAnswerSource`.
type RagAnswerConfig = {
    /// Slug prefix the source claims. `"answers"` makes the source resolve
    /// `answers/<question>` (the tail is de-slugified into a query —
    /// hyphens / slashes → spaces, percent-decoded). A request that is not
    /// under the prefix falls through (`None`).
    SlugPrefix: string
    /// Vector scopes to query. The pipeline validates each against the
    /// caller's `AccessContext`, so an unauthorised scope simply yields no
    /// matches (GP 4).
    Scopes: VectorScope list
    /// Number of chunks to retrieve.
    TopK: int
    /// Minimum fused relevance score a match must clear to be treated as
    /// grounded. Matches below this are dropped before the grounding
    /// decision.
    MinScore: float
    /// When `true`, a query with no grounded match renders a no-answer
    /// page instead of an answer — never speculative prose, even with a
    /// synthesis hook present (the `StrictlyGrounded` contract).
    StrictGrounding: bool
    /// Optional LLM synthesis. `None` → extractive cited answer.
    Synthesis: RagSynthesisHook option
}

module RagAnswerConfig =
    /// A config claiming `slugPrefix`, querying `scopes`, with sensible
    /// defaults (TopK 5, MinScore 0.0, non-strict, extractive). Tune with
    /// the `with*` helpers.
    let create (slugPrefix: string) (scopes: VectorScope list) : RagAnswerConfig = {
        SlugPrefix = slugPrefix
        Scopes = scopes
        TopK = 5
        MinScore = 0.0
        StrictGrounding = false
        Synthesis = None
    }

    let withTopK (k: int) (c: RagAnswerConfig) : RagAnswerConfig = { c with TopK = k }

    let withMinScore (s: float) (c: RagAnswerConfig) : RagAnswerConfig = { c with MinScore = s }

    /// Refuse to answer (render a no-answer page) when retrieval finds no
    /// grounded match — the `StrictlyGrounded` contract.
    let strictlyGrounded (c: RagAnswerConfig) : RagAnswerConfig = { c with StrictGrounding = true }

    /// Wire an LLM synthesis hook (the deployment's own `IAIProvider`). The
    /// SDK default is extractive (no LLM).
    let withSynthesis (hook: RagSynthesisHook) (c: RagAnswerConfig) : RagAnswerConfig = { c with Synthesis = Some hook }

module RagAnswerSource =

    /// De-slugify a slug tail under `prefix` into a natural-language query.
    /// `"answers/how-do-i-deploy"` (prefix `"answers"`) → `Some "how do i
    /// deploy"`. Returns `None` when the slug is not under the prefix, or
    /// is the bare prefix with no question.
    let queryFromSlug (prefix: string) (slug: string) : string option =
        let p = prefix.Trim('/')
        let s = slug.Trim('/')

        if p = "" || not (s.StartsWith(p + "/")) then
            None
        else
            let tail = s.Substring(p.Length + 1)

            let q =
                System.Uri.UnescapeDataString(tail).Replace('-', ' ').Replace('/', ' ').Trim()

            if q = "" then None else Some q

    /// Human-readable citation label for a match — the location hint
    /// (`"Page 4"`, `"Slide 12"`) if present, else the origin classification,
    /// else a generic fallback.
    let private citeLabel (m: VectorMatch) : string =
        match m.Metadata.TryFind ChunkMetadata.LocationHintKey with
        | Some h when h <> "" -> h
        | _ ->
            match m.Metadata.TryFind ChunkMetadata.OriginKey with
            | Some o when o <> "" -> o
            | _ -> "Knowledge base"

    let private scoreBadge (score: float) : string =
        sprintf "%d%% match" (int (System.Math.Round(score * 100.0)))

    /// Build the cited answer document. The `Answer` section is the
    /// synthesized prose (when a hook ran) or the extractive lead (the top
    /// chunk); the `Sources` section lists every grounded match as a cited
    /// blockquote so a reader can audit the grounding.
    let private answerDoc (query: string) (matches: VectorMatch list) (synth: string option) : NarrativeDocument =
        let answerElements =
            match synth with
            | Some prose -> [ Narrative.paragraph [ Narrative.text prose ] ]
            | None ->
                match matches with
                | lead :: _ -> [ Narrative.paragraph [ Narrative.text lead.Content ] ]
                | [] -> []

        let sourceElements =
            matches
            |> List.map (fun m ->
                Narrative.blockquote (Some(citeLabel m + " · " + scoreBadge m.Score)) [ Narrative.text m.Content ])

        Narrative.create query
        |> Narrative.subtitle "Answered from the knowledge base"
        |> Narrative.section "Answer" "answer" answerElements
        |> Narrative.section "Sources" "sources" sourceElements
        |> Narrative.withProvenance "rag" None "rag-answer" []

    /// The graceful no-answer document — a Warning callout, never any
    /// speculative prose. Rendered under `StrictGrounding` on a retrieval
    /// miss, and whenever there is nothing grounded to extract.
    let private noAnswerDoc (query: string) : NarrativeDocument =
        Narrative.create query
        |> Narrative.subtitle "No grounded answer available"
        |> Narrative.section "No answer" "no-answer" [
            Narrative.callout Severity.Warning [
                Narrative.text (
                    sprintf
                        "The knowledge base has no content relevant to “%s”. No answer is shown rather than a speculative one."
                        query
                )
            ]
        ]
        |> Narrative.withProvenance "rag" None "rag-answer" []

    /// Construct a RAG answer-page content source over a retrieval pipeline.
    /// Register it with `PublicRenderingServerApp.withContentSource`. The
    /// produced pages are `Narrative`-bodied and cache through the Phase 84
    /// render cache like any other content-source page.
    let create (pipeline: IRetrievalPipeline) (config: RagAnswerConfig) : IContentSource =
        ContentSource.create (fun (Slug s) ctx -> async {
            match queryFromSlug config.SlugPrefix s with
            | None -> return None
            | Some query ->
                let request: RetrievalRequest = {
                    Query = query
                    Scopes = config.Scopes
                    TopK = config.TopK
                    Merge = Interleaved
                    Filters = None
                    History = None
                    AdaptiveK = None
                    OriginFilter = None
                    ActiveModule = None
                    FactClause = None
                }

                let! matches = pipeline.Retrieve request ctx
                let grounded = matches |> List.filter (fun m -> m.Score >= config.MinScore)

                if List.isEmpty grounded then
                    // No grounded match. Under strict grounding we refuse
                    // outright. Otherwise, a synthesis hook may still
                    // answer from general knowledge (the consumer's LLM
                    // decides); with no hook there is nothing to extract,
                    // so we also render the no-answer page rather than
                    // fabricate.
                    match config.StrictGrounding, config.Synthesis with
                    | false, Some hook ->
                        let! prose = hook query []
                        return Some(ContentBody.Narrative(answerDoc query [] (Some prose)))
                    | _ -> return Some(ContentBody.Narrative(noAnswerDoc query))
                else
                    let! synth = async {
                        match config.Synthesis with
                        | Some hook ->
                            let! prose = hook query grounded
                            return Some prose
                        | None -> return None
                    }

                    return Some(ContentBody.Narrative(answerDoc query grounded synth))
        })

// ─── Phase 91 — suggested-questions FAQ surface ──────────────────────
//
// A public FAQ index that seeds the `RagAnswerSource` answer pages: each
// suggested question is rendered as a link to `/<answersPrefix>/<slug>`,
// where the slug round-trips through `RagAnswerSource.queryFromSlug` back
// to the question text, so a click lands on the grounded answer page.
//
// **Dependency isolation (GP 1).** The questions arrive through an
// injected provider the deployment fills from its KB's
// `KnowledgeApi.GetSuggestedQuestions` — PublicRendering never references
// the KnowledgeBase companion. **Scope isolation (GP 4):** the provider
// receives the request `AccessContext`, so suggestions are scoped to the
// caller.

/// Read-port for suggested questions — the deployment wires its KB's
/// `GetSuggestedQuestions` (scoped by `AccessContext`, GP 4). Returns the
/// question strings to seed the FAQ over.
type SuggestedQuestionsProvider = AccessContext -> Async<string list>

/// Compose-time configuration for a `SuggestedQuestionsSource`.
type SuggestedQuestionsConfig = {
    /// Slug prefix the FAQ index claims (`"faq"` serves `/faq` and
    /// `/faq/index`).
    SlugPrefix: string
    /// The `RagAnswerSource` prefix to link each question into, so a click
    /// lands on the grounded answer page (`"answers"` →
    /// `/answers/<question-slug>`).
    AnswersPrefix: string
    /// Title rendered on the FAQ index page.
    Title: string
}

module SuggestedQuestionsConfig =
    let create (slugPrefix: string) (answersPrefix: string) : SuggestedQuestionsConfig = {
        SlugPrefix = slugPrefix
        AnswersPrefix = answersPrefix
        Title = "Frequently asked questions"
    }

    let withTitle (title: string) (c: SuggestedQuestionsConfig) : SuggestedQuestionsConfig = { c with Title = title }

module SuggestedQuestionsSource =

    /// Slugify a question into an answer-page tail. Lowercases, maps every
    /// non-alphanumeric run to a single `-`, and trims — the inverse of
    /// `RagAnswerSource.queryFromSlug`'s de-slugify (hyphens → spaces), so
    /// `"How do I deploy?"` → `"how-do-i-deploy"` round-trips back to the
    /// query `"how do i deploy"`.
    let questionSlug (question: string) : string =
        let lowered = question.Trim().ToLowerInvariant()

        let cleaned =
            lowered
            |> String.map (fun c -> if System.Char.IsLetterOrDigit c then c else '-')

        cleaned.Split([| '-' |], System.StringSplitOptions.RemoveEmptyEntries)
        |> String.concat "-"

    /// The FAQ index document — each question a link into its answer page.
    let private faqDoc (config: SuggestedQuestionsConfig) (questions: string list) : NarrativeDocument =
        let answersPrefix = config.AnswersPrefix.Trim('/')

        let baseDoc = Narrative.create config.Title

        if List.isEmpty questions then
            baseDoc
            |> Narrative.section "Questions" "questions" [
                Narrative.callout Severity.Info [ Narrative.text "No suggested questions are available yet." ]
            ]
            |> Narrative.withProvenance "rag" None "faq" []
        else
            let items =
                questions
                |> List.choose (fun q ->
                    let slug = questionSlug q
                    // A question that slugs to empty (punctuation-only) is
                    // dropped rather than linked to a bare prefix.
                    if slug = "" then
                        None
                    else
                        Some [ Narrative.link ("/" + answersPrefix + "/" + slug) [ Narrative.text q ] ])

            baseDoc
            |> Narrative.section "Questions" "questions" [ Narrative.bullets items ]
            |> Narrative.withProvenance "rag" None "faq" []

    /// Construct a suggested-questions FAQ content source over a provider.
    /// Register it with `PublicRenderingServerApp.withContentSource`
    /// alongside the `RagAnswerSource` whose `answersPrefix` it links into.
    let create (provider: SuggestedQuestionsProvider) (config: SuggestedQuestionsConfig) : IContentSource =
        ContentSource.create (fun (Slug s) ctx -> async {
            let p = config.SlugPrefix.Trim('/')
            let slug = s.Trim('/')

            if p <> "" && (slug = p || slug = p + "/index") then
                let! questions = provider ctx
                return Some(ContentBody.Narrative(faqDoc config questions))
            else
                return None
        })
module ToolUp.RAG.ProviderQueryRewriter

open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.IQueryRewriter

// ─── Provider-backed conversation-aware query rewriter (Phase 506) ───
//
// The shipped `IQueryRewriter`. Two stages, in this order, and the order is
// the whole cost story:
//
//   1. `QueryDependence.isFollowUp` — a local, provider-free gate. The
//      overwhelming majority of turns in a knowledge-base deployment are
//      self-contained questions, and none of them may cost a model call.
//      This is what makes the stage affordable to leave on.
//   2. Only for what survives the gate: one bounded provider call that
//      resolves the follow-up against the recent turns.
//
// Everything about the call is deliberately small — a short system prompt,
// `MaxAttempts = 1`, a caller-set wall-clock timeout, a trimmed history
// window, and an output the prompt constrains to a single line. A rewrite
// is worth about one cheap completion and no more; a rewriter that cost a
// meaningful fraction of the answering call would not be wired in.

/// Default number of trailing conversation turns handed to the model. Four
/// is enough to resolve almost every anaphor (the referent is nearly always
/// in the immediately-preceding assistant turn) and short enough that the
/// prompt stays cheap on a long conversation. Callers with a different
/// budget pass their own.
[<Literal>]
let DefaultHistoryTurns = 4

/// Default per-call wall-clock bound, milliseconds. The rewrite sits in
/// front of retrieval, which sits in front of the answering call, so its
/// latency is paid by the user on every follow-up turn. Two seconds is
/// generous for a short completion on a small model and short enough that
/// a wedged provider degrades to raw-query retrieval rather than stalling
/// the turn. Clamped through `RetryPolicy.clampTimeoutMs`, whose floor is
/// 1 s.
[<Literal>]
let DefaultTimeoutMs = 2_000

/// Upper bound on the characters of any single history turn included in the
/// prompt. A pasted document in a prior turn must not turn a cheap rewrite
/// into an expensive one; the referent an anaphor needs is at the start of
/// a turn in practice, so truncating the tail costs almost nothing.
[<Literal>]
let MaxHistoryTurnChars = 600

/// Upper bound on an accepted rewrite. A model that ignored the "one line"
/// instruction and returned a paragraph has not produced a search query;
/// embedding it would be worse than embedding the raw follow-up. Over this
/// length the rewrite is discarded and the raw query searched.
[<Literal>]
let MaxRewrittenChars = 400

/// The rewrite instruction. Constrains the model to emit the standalone
/// query and nothing else — no preamble, no quotes, no explanation — so the
/// response needs no parsing beyond a trim. It is also told to echo the
/// query unchanged when nothing needs resolving, which is the second line
/// of defence behind the local gate: a query the heuristic misclassified as
/// a follow-up comes back identical and is reported `QuerySelfContained`.
let rewriteSystemPrompt =
    "You rewrite the final user message of a conversation into a standalone search query.\n\
     Resolve pronouns and references (\"it\", \"that one\", \"the second\") against the earlier turns.\n\
     Rules:\n\
     - Reply with the rewritten query ONLY. No preamble, no quotes, no explanation.\n\
     - Preserve the user's intent exactly. Do not broaden the question or add topics they did not ask about.\n\
     - If the final message already stands on its own, reply with it unchanged.\n\
     - Keep it to one line."

let private truncateTurn (text: string) =
    if System.String.IsNullOrEmpty text then ""
    elif text.Length <= MaxHistoryTurnChars then text
    else text.Substring(0, MaxHistoryTurnChars) + "…"

/// Render the bounded history window plus the query as the single user
/// message. One message rather than a replayed multi-turn transcript: the
/// task is a text transformation, not a continuation of the conversation,
/// and a flat rendering keeps the rewriter immune to the role conventions
/// of whichever provider is composed.
let buildPrompt (historyTurns: int) (query: string) (history: string list) : string =
    let recent =
        history
        |> List.filter (System.String.IsNullOrWhiteSpace >> not)
        |> fun turns ->
            let drop = max 0 (List.length turns - historyTurns)
            turns |> List.skip drop
        |> List.map truncateTurn

    let transcript =
        recent
        |> List.mapi (fun i t -> sprintf "%d. %s" (i + 1) t)
        |> String.concat "\n"

    sprintf "Earlier turns (oldest first):\n%s\n\nFinal user message:\n%s" transcript query

/// Provider-backed `IQueryRewriter`.
///
/// `provider` is any composed `IAIProvider` — pass the deployment's cheapest
/// model where the factory can resolve one separately from the answering
/// model. `historyTurns` and `timeoutMs` bound the call; both have defaults
/// tuned for an interactive turn. `logger`, when supplied, records a
/// degradation at `Warn` — the pipeline already records the decision on the
/// retrieval trace, so this is for the operator tailing logs, not the
/// primary signal.
///
/// Stateless between calls (portability rule 4): the provider handle is
/// immutable infrastructure, and no conversation state is retained.
type ProviderQueryRewriter(provider: IAIProvider, ?historyTurns: int, ?timeoutMs: int, ?logger: ILogger) =

    let turns = defaultArg historyTurns DefaultHistoryTurns
    let timeout = RetryPolicy.clampTimeoutMs (defaultArg timeoutMs DefaultTimeoutMs)

    // `MaxAttempts = 1`: a rewrite is an enhancement with a fallback that
    // always works, so a retry buys a marginally better query at the cost of
    // multiplying the latency the user waits on. Fail fast and search raw.
    let policy = {
        RetryPolicy.noRetry with
            Timeout = Some(System.TimeSpan.FromMilliseconds(float timeout))
    }

    interface IQueryRewriter with
        member _.Name = "provider-rewrite"

        member _.Rewrite query history = async {
            // Stage 1 — the free gate. A self-contained query never reaches
            // the provider, which is what keeps this stage affordable to
            // leave enabled for every deployment.
            if QueryDependence.isSelfContained query then
                return QuerySelfContained
            else
                let userMessage = AIProviderMessage.text "user" (buildPrompt turns query history)

                let! result = provider.SendMessage([ userMessage ], [], Some rewriteSystemPrompt, None, policy)

                match result with
                | Error err ->
                    // Surfaced, not swallowed: the pipeline catches this and
                    // records `Failed` on the trace, then searches raw. An
                    // `Ok`-with-empty-content return here would be reported
                    // as a clean self-contained decision, which would hide a
                    // broken provider behind a plausible-looking trace.
                    logger
                    |> Option.iter (fun l -> l.Warn(sprintf "[QueryRewrite] provider error: %A" err))

                    return failwithf "Query rewrite failed: %A" err
                | Ok response ->
                    let rewritten =
                        if isNull response.Content then
                            ""
                        else
                            response.Content.Trim().Trim('"')

                    // An empty, unchanged, or absurdly long response is not a
                    // usable rewrite. Reporting it as self-contained (rather
                    // than raising) is the honest classification: retrieval
                    // proceeds on the raw query either way, and a model that
                    // echoed the query back did exactly what the prompt asked.
                    if
                        rewritten = ""
                        || rewritten = query.Trim()
                        || rewritten.Length > MaxRewrittenChars
                    then
                        return QuerySelfContained
                    else
                        return QueryRewritten rewritten
        }

/// Construct the shipped provider-backed rewriter as the seam type. Wire it
/// into DI (`services.AddSingleton<IQueryRewriter>(…)`) before `withRAG`
/// composes the retrieval pipeline; compose picks it up from there.
let create (provider: IAIProvider) : IQueryRewriter =
    ProviderQueryRewriter(provider) :> IQueryRewriter

/// As `create`, with the history window and per-call timeout set explicitly.
let createWith (provider: IAIProvider) (historyTurns: int) (timeoutMs: int) (logger: ILogger) : IQueryRewriter =
    ProviderQueryRewriter(provider, historyTurns = historyTurns, timeoutMs = timeoutMs, logger = logger)
    :> IQueryRewriter
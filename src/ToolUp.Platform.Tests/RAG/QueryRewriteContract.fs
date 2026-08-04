// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.RAG.QueryRewriteContract

open System
open System.IO
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IQueryRewriter
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IRetrievalTracer
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Phase 506 — conversation-aware retrieval / query rewrite ────────
//
// `RetrievalRequest.History` shipped documented as "used by query-rewrite
// stages". No such stage existed, so the field was inert: a multi-turn
// conversation retrieved as a sequence of unrelated first turns, and a
// follow-up whose subject lived only in the previous turn ("what about the
// tolerance for it?") went to whichever document happened to share its
// remaining words — often the wrong one, with no error and no signal.
//
// The load-bearing assertion in this file is `follow-up retrieval goes to
// the WRONG document RAW and the right one REWRITTEN`, asserted as a pair.
// Either half alone is unfalsifiable: "rewritten retrieval finds the doc"
// passes just as happily against a pipeline that finds everything, and
// "raw retrieval misses" passes against one that finds nothing. The pair is
// what pins the stage to the defect it exists to close.
//
// Everything else here guards the cost and safety envelope — no rewriter
// wired changes nothing, a self-contained query spends no provider call, a
// broken rewriter degrades instead of failing the turn.

// ── Deterministic fixed-vocabulary embedder ──
//
// One dimension per known word — NOT a hashed bag-of-words.
// `String.GetHashCode()` is randomised per process on .NET, so a hashing
// embedder builds a different vector space on every run and any assertion
// about RANKING becomes a coin flip on which words happened to collide.
// This pack's first full `VerifyAll` caught exactly that, after the same
// assertion had passed twice in isolation. A fixed vocabulary is
// reproducible here, on CI, and on another machine, and it makes the
// corpus arithmetic below something a reader can check by hand.

let private vocab = [| "widget"; "sprocket"; "tolerance"; "calibration"; "quarterly" |]

let private dim = vocab.Length

let private bow (text: string) : float32[] =
    let v = Array.zeroCreate<float32> dim

    let words =
        text.ToLowerInvariant().Split([| ' '; '\n'; '\t'; '.'; ','; '?'; '#' |], StringSplitOptions.RemoveEmptyEntries)

    for w in words do
        match Array.tryFindIndex ((=) w) vocab with
        | Some i -> v[i] <- v[i] + 1.0f
        | None -> ()

    v

type private BowEmbedder() =
    interface IEmbeddingProvider with
        member _.GenerateEmbedding text = async { return bow text }
        member _.GenerateEmbeddings texts = async { return texts |> Seq.map bow |> Seq.toArray }
        member _.Dimensions = dim
        member _.ProviderId = "test"
        member _.ModelId = "bow-v1"

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

// ── Test rewriters ──

/// A rewriter that always substitutes `rewritten`, counting its calls so a
/// test can assert the stage did NOT run as well as that it did.
type private FixedRewriter(rewritten: string) =
    let calls = ref 0
    member _.Calls = calls.Value

    interface IQueryRewriter with
        member _.Name = "fixed-test-rewriter"

        member _.Rewrite _query _history = async {
            Interlocked.Increment calls |> ignore
            return QueryRewritten rewritten
        }

/// Applies the shipped `QueryDependence` gate before substituting, which is
/// the shape every real implementation has: the cheap local check first, the
/// expensive call only for what survives it. `Calls` counts only what got
/// past the gate — i.e. what a provider-backed rewriter would have paid for.
type private GatedRewriter(rewritten: string) =
    let calls = ref 0
    member _.Calls = calls.Value

    interface IQueryRewriter with
        member _.Name = "gated-test-rewriter"

        member _.Rewrite query _history = async {
            if QueryDependence.isSelfContained query then
                return QuerySelfContained
            else
                Interlocked.Increment calls |> ignore
                return QueryRewritten rewritten
        }

/// Raises. Stands in for every way a real rewriter fails: provider down,
/// auth expired, malformed response, quota exhausted.
type private ThrowingRewriter() =
    interface IQueryRewriter with
        member _.Name = "throwing-test-rewriter"
        member _.Rewrite _query _history = async { return failwith "provider unavailable" }

/// Sleeps past any sane timeout. Stands in for a wedged provider — the case
/// a `try/with` alone does not cover.
type private HangingRewriter() =
    interface IQueryRewriter with
        member _.Name = "hanging-test-rewriter"

        member _.Rewrite _query _history = async {
            do! Async.Sleep 30_000
            return QueryRewritten "never gets here"
        }

/// Captures the last trace so the observability assertions can read the
/// decision the pipeline recorded rather than inferring it from results.
type private CapturingTracer() =
    let last: RetrievalTrace option ref = ref None
    member _.Last = last.Value

    interface IRetrievalTracer with
        member _.Trace trace _ctx =
            last.Value <- Some trace
            async.Return()

        member _.Miss _ _ = async.Return()

// ── The corpus ──
//
// Two documents that BOTH discuss tolerance. That overlap is the point:
// the conversation is about widgets, and the follow-up "what about the
// tolerance for it?" drops the subject, leaving only a word both documents
// share — and which the sprocket document carries at a higher weight.
//
// The arithmetic, so the assertions below are not taken on trust
// (cosine over the vocabulary vectors):
//
//   widget-policy   = {widget:4, tolerance:1}                     |d| = √17
//   sprocket-policy = {sprocket:2, tolerance:1, calibration:1,
//                      quarterly:1}                               |d| = √7
//
//   raw       "…tolerance…"        = {tolerance:1}
//               → widget   1/√17          = 0.243
//               → sprocket 1/√7           = 0.378   ← WRONG document wins
//   rewritten "widget tolerance"   = {widget:1, tolerance:1}
//               → widget   5/(√17·√2)     = 0.858   ← right document wins
//               → sprocket 1/(√7·√2)      = 0.267
//
// Retrieving the wrong document is a sharper statement of the defect than
// retrieving nothing: the deployment answers confidently about sprockets
// when the user asked about widgets, and nothing anywhere reports a fault.

let private corpusRows: (string * string) list = [
    "widget-policy", "widget assembly guidance widget tolerance widget production widget line"
    "sprocket-policy", "sprocket calibration tolerance measured quarterly by the sprocket team"
]

let private followUpQuery = "what about the tolerance for it?"
let private rewrittenQuery = "widget tolerance"
let private selfContainedQuery = "sprocket calibration tolerance quarterly"

let private history = [
    "Tell me about the widget production line."
    "Widget assembly is covered by the widget guidance."
]

// ── Harness ──

type private Bound = {
    Pipeline: IRetrievalPipeline
    Tracer: CapturingTracer
    Dispose: unit -> unit
}

let private access =
    AccessContext.unrestricted (Subject.AnonymousSession "query-rewrite")

/// Build a default pipeline over the shared corpus with the given rewriter
/// (and optional timeout override), seeded through `IRetrievalPipeline.Index`
/// — the production ingestion path.
let private bindPipeline (rewriter: IQueryRewriter option) (timeoutMs: int option) : Bound =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-query-rewrite-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage

    let store =
        new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(
            storage,
            logger = SilentLogger(),
            flushIntervalMs = 60000
        )

    let tracer = CapturingTracer()

    let options = {
        ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions.defaults with
            QueryRewriteTimeoutMs =
                timeoutMs
                |> Option.defaultValue
                    ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions.defaults.QueryRewriteTimeoutMs
    }

    let pipeline =
        new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(
            store :> IVectorStore,
            BowEmbedder(),
            options = options,
            tracer = (tracer :> IRetrievalTracer),
            ?queryRewriter = rewriter
        )
        :> IRetrievalPipeline

    for id, body in corpusRows do
        pipeline.Index id { Content = body; Metadata = Map.empty } Deployment
        |> Async.RunSynchronously

    {
        Pipeline = pipeline
        Tracer = tracer
        Dispose =
            fun () ->
                (store :> IDisposable).Dispose()

                try
                    Directory.Delete(tempDir, true)
                with _ ->
                    ()
    }

/// Retrieve and return the winning chunk id (the pipeline is asked for
/// TopK = 1 so "did retrieval find the right document?" is a single value,
/// not a ranking judgement).
let private topHit (bound: Bound) (query: string) (hist: string list option) : string option =
    let request = {
        RetrievalRequest.create query [ Deployment ] 1 MergeStrategy.Interleaved with
            History = hist
    }

    bound.Pipeline.Retrieve request access
    |> Async.RunSynchronously
    |> List.tryHead
    |> Option.map _.ChunkId

let private withBound (rewriter: IQueryRewriter option) (timeoutMs: int option) (f: Bound -> unit) =
    let bound = bindPipeline rewriter timeoutMs

    try
        f bound
    finally
        bound.Dispose()

[<Tests>]
let tests =
    testList "Phase 506 — conversation-aware query rewrite" [

        // ── The defect, asserted as a pair ──

        test "a follow-up query misses raw and hits once rewritten" {
            // Half one: the raw follow-up has lost its subject, so the only
            // word left to score on is one the OTHER document weights more
            // heavily — and retrieval confidently returns the wrong
            // document. This is the multi-turn failure the phase exists to
            // fix, and asserting it FIRST is what stops the second half
            // from being vacuous. Asserted as an equality, not a
            // not-equality: "it went to sprocket-policy" is a claim that
            // can only hold one way, where "it did not go to widget-policy"
            // would also be satisfied by retrieving nothing at all.
            withBound None None (fun bound ->
                let raw = topHit bound followUpQuery (Some history)

                Expect.equal
                    raw
                    (Some "sprocket-policy")
                    "the raw follow-up, subject-less, lands on the wrong document")

            // Half two: the same query, same corpus, same TopK, with a
            // rewriter that resolves the anaphor.
            withBound (Some(FixedRewriter(rewrittenQuery) :> IQueryRewriter)) None (fun bound ->
                let rewritten = topHit bound followUpQuery (Some history)

                Expect.equal
                    rewritten
                    (Some "widget-policy")
                    "the rewritten follow-up retrieves the document the conversation was about")
        }

        // ── Cost envelope ──

        test "a self-contained query is not rewritten" {
            let rewriter = GatedRewriter(rewrittenQuery)

            withBound (Some(rewriter :> IQueryRewriter)) None (fun bound ->
                let got = topHit bound selfContainedQuery (Some history)

                Expect.equal got (Some "sprocket-policy") "the self-contained query retrieves on its own terms"

                Expect.equal
                    rewriter.Calls
                    0
                    "a query that names its own subject must not cost a provider call, even mid-conversation")

            // Control: the SAME rewriter does fire for a follow-up, so the
            // zero above is the gate working rather than the rewriter never
            // being reachable.
            let control = GatedRewriter(rewrittenQuery)

            withBound (Some(control :> IQueryRewriter)) None (fun bound ->
                topHit bound followUpQuery (Some history) |> ignore
                Expect.equal control.Calls 1 "a follow-up does reach the rewriter")
        }

        test "history-less requests never reach the rewriter" {
            let rewriter = FixedRewriter(rewrittenQuery)

            withBound (Some(rewriter :> IQueryRewriter)) None (fun bound ->
                topHit bound followUpQuery None |> ignore
                topHit bound followUpQuery (Some []) |> ignore

                Expect.equal
                    rewriter.Calls
                    0
                    "History = None and History = Some [] are both 'first turn' — nothing to resolve against")
        }

        // ── GP 11: unconfigured ⇒ unchanged ──

        test "no rewriter composed leaves the stage list and results untouched" {
            let baseline =
                bindPipeline None None
                |> fun b ->
                    try
                        let r = topHit b selfContainedQuery None
                        r, b.Tracer.Last |> Option.map _.Stages
                    finally
                        b.Dispose()

            // Same pipeline shape, but now with history on the request and
            // still no rewriter: the request field alone must change nothing.
            let withHistory =
                bindPipeline None None
                |> fun b ->
                    try
                        let r = topHit b selfContainedQuery (Some history)
                        r, b.Tracer.Last |> Option.map _.Stages
                    finally
                        b.Dispose()

            Expect.equal (fst withHistory) (fst baseline) "results are identical"
            Expect.equal (snd withHistory) (snd baseline) "the stage list is identical"

            Expect.isFalse
                (snd baseline |> Option.defaultValue [] |> List.contains "QueryRewrite")
                "no QueryRewrite stage is recorded when no rewriter is wired"
        }

        test "no rewriter composed records no rewrite decision on the trace" {
            withBound None None (fun bound ->
                topHit bound followUpQuery (Some history) |> ignore
                let trace = Expect.wantSome bound.Tracer.Last "a trace was emitted"

                Expect.isNone
                    trace.RewriteDecision
                    "RewriteDecision stays None for every pre-506 deployment — the field is absent, not 'SelfContained'"

                Expect.isNone trace.RewrittenQueryHash "and no rewritten hash accompanies it")
        }

        // ── Graceful degradation ──

        test "a throwing rewriter degrades to the raw query rather than failing retrieval" {
            withBound (Some(ThrowingRewriter() :> IQueryRewriter)) None (fun bound ->
                // The assertion is that this returns at all. A rewrite is an
                // enhancement over a path that already works; it must never
                // be able to break it.
                let got = topHit bound selfContainedQuery (Some history)
                Expect.equal got (Some "sprocket-policy") "retrieval completed on the raw query"

                let trace = Expect.wantSome bound.Tracer.Last "a trace was emitted"

                Expect.equal
                    trace.RewriteDecision
                    (Some QueryRewriteDecision.Failed)
                    "the degradation is recorded, not silent — a failed rewrite and an absent one must be distinguishable")
        }

        test "a hanging rewriter is bounded by QueryRewriteTimeoutMs" {
            let sw = Diagnostics.Stopwatch.StartNew()

            withBound (Some(HangingRewriter() :> IQueryRewriter)) (Some 300) (fun bound ->
                let got = topHit bound selfContainedQuery (Some history)
                sw.Stop()

                Expect.equal got (Some "sprocket-policy") "retrieval completed on the raw query"

                Expect.isLessThan
                    sw.Elapsed.TotalSeconds
                    20.0
                    "the 30s rewriter did not hold the turn — the timeout fired"

                let trace = Expect.wantSome bound.Tracer.Last "a trace was emitted"

                Expect.equal
                    trace.RewriteDecision
                    (Some QueryRewriteDecision.Failed)
                    "a timeout is a failure like any other, and is recorded as one")
        }

        // ── Observability (506.C) ──

        test "a rewrite records its decision, its stage and its hash" {
            withBound (Some(FixedRewriter(rewrittenQuery) :> IQueryRewriter)) None (fun bound ->
                topHit bound followUpQuery (Some history) |> ignore
                let trace = Expect.wantSome bound.Tracer.Last "a trace was emitted"

                Expect.equal trace.RewriteDecision (Some QueryRewriteDecision.Rewritten) "the decision is on the trace"

                Expect.contains trace.Stages "QueryRewrite" "the stage appears in the realised pipeline shape"

                Expect.equal
                    trace.RewrittenQueryHash
                    (Some(ToolUp.RAG.RetrievalTracers.hashQuery rewrittenQuery))
                    "the rewritten query is identified by hash"

                Expect.notEqual
                    trace.RewrittenQueryHash
                    (Some trace.QueryHash)
                    "…and is distinguishable from the raw query's hash"

                Expect.isFalse
                    (trace.Stages |> List.exists (fun s -> s.Contains rewrittenQuery))
                    "the plaintext rewritten query is never on the trace"

                Expect.isSome
                    (trace.StageTimings |> List.tryFind (fst >> (=) "QueryRewrite"))
                    "the rewrite is metered like any other substantive stage")
        }

        test "a self-contained decision records no rewritten hash" {
            withBound (Some(GatedRewriter(rewrittenQuery) :> IQueryRewriter)) None (fun bound ->
                topHit bound selfContainedQuery (Some history) |> ignore
                let trace = Expect.wantSome bound.Tracer.Last "a trace was emitted"

                Expect.equal
                    trace.RewriteDecision
                    (Some QueryRewriteDecision.SelfContained)
                    "the rewriter ran and declined — visibly different from never having run"

                Expect.isNone trace.RewrittenQueryHash "nothing was substituted, so nothing is hashed")
        }

        // ── The shared gate ──
        //
        // `QueryDependence` is on the seam so every rewriter classifies
        // alike. Pinned here because getting it wrong is not a test failure
        // anywhere else — it is a bill: a gate that says "follow-up" too
        // eagerly buys a provider call on every well-formed question in the
        // deployment.

        testList "QueryDependence.isFollowUp" [
            test "anaphoric queries are follow-ups" {
                for q in
                    [
                        "what about its retention period?"
                        "and the second one?"
                        "why does that happen for them"
                        "how long do they keep it"
                    ] do
                    Expect.isTrue (QueryDependence.isFollowUp q) $"'{q}' depends on prior turns"
            }

            test "self-contained questions are not follow-ups" {
                // The interrogative-opener trap: `what` / `how` / `which`
                // open most standalone searches too, so treating them as
                // continuation markers would classify almost every
                // well-formed question in a deployment as a follow-up.
                for q in
                    [
                        "what is the widget retention period"
                        "how are sprocket calibration tolerances measured"
                        "which team owns the archival policy"
                        "sprocket calibration tolerance measured quarterly"
                    ] do
                    Expect.isFalse (QueryDependence.isFollowUp q) $"'{q}' names its own subject"
            }

            test "very short queries are follow-ups regardless of vocabulary" {
                for q in [ "why?"; "the sprocket one"; "more detail" ] do
                    Expect.isTrue (QueryDependence.isFollowUp q) $"'{q}' is too short to stand alone"
            }

            test "an empty query is not a follow-up" {
                Expect.isFalse (QueryDependence.isFollowUp "") "nothing to resolve"
                Expect.isFalse (QueryDependence.isFollowUp "   ") "nothing to resolve"
            }
        ]

        // ── The shipped provider-backed rewriter ──

        testList "ProviderQueryRewriter" [
            test "a self-contained query never reaches the provider" {
                let calls = ref 0

                let provider =
                    { new IAIProvider with
                        member _.Capabilities = Unchecked.defaultof<AIProviderCapabilities>

                        member _.SendMessage(_, _, _, _, _) = async {
                            Interlocked.Increment calls |> ignore

                            return
                                Ok {
                                    Content = rewrittenQuery
                                    ToolCalls = []
                                    StopReason = "end_turn"
                                    Usage = None
                                }
                        }

                        member this.SendStructuredMessage(m, t, s, sch, r) =
                            IAIProviderDefaults.sendStructuredViaFallback this m t s sch r
                    }

                let rewriter = ToolUp.RAG.ProviderQueryRewriter.create provider

                let selfContained =
                    rewriter.Rewrite selfContainedQuery history |> Async.RunSynchronously

                Expect.equal selfContained QuerySelfContained "the local gate answered without a provider call"
                Expect.equal calls.Value 0 "no provider call was spent"

                // Control — the same provider IS reachable for a follow-up.
                let followUp = rewriter.Rewrite followUpQuery history |> Async.RunSynchronously
                Expect.equal followUp (QueryRewritten rewrittenQuery) "the follow-up was rewritten"
                Expect.equal calls.Value 1 "exactly one provider call, and only for the follow-up"
            }

            test "an echoed or empty response is reported self-contained, not substituted" {
                let respondWith (content: string) =
                    { new IAIProvider with
                        member _.Capabilities = Unchecked.defaultof<AIProviderCapabilities>

                        member _.SendMessage(_, _, _, _, _) = async {
                            return
                                Ok {
                                    Content = content
                                    ToolCalls = []
                                    StopReason = "end_turn"
                                    Usage = None
                                }
                        }

                        member this.SendStructuredMessage(m, t, s, sch, r) =
                            IAIProviderDefaults.sendStructuredViaFallback this m t s sch r
                    }

                let run (content: string) =
                    (ToolUp.RAG.ProviderQueryRewriter.create (respondWith content)).Rewrite followUpQuery history
                    |> Async.RunSynchronously

                Expect.equal (run "") QuerySelfContained "an empty rewrite must not replace a usable query"
                Expect.equal (run "   ") QuerySelfContained "nor a whitespace one"

                Expect.equal
                    (run followUpQuery)
                    QuerySelfContained
                    "a model that echoed the query back did what the prompt asked — that is not a rewrite"

                Expect.equal
                    (run (String.replicate 200 "long "))
                    QuerySelfContained
                    "a paragraph is not a search query; embedding it would be worse than the raw follow-up"
            }

            test "a provider error is raised so the pipeline can record the degradation" {
                let provider =
                    { new IAIProvider with
                        member _.Capabilities = Unchecked.defaultof<AIProviderCapabilities>

                        member _.SendMessage(_, _, _, _, _) = async {
                            return Error(AIProviderError.PermanentClient(401, "unauthorised"))
                        }

                        member this.SendStructuredMessage(m, t, s, sch, r) =
                            IAIProviderDefaults.sendStructuredViaFallback this m t s sch r
                    }

                let rewriter = ToolUp.RAG.ProviderQueryRewriter.create provider

                // Deliberately NOT swallowed into `QuerySelfContained`: that
                // would report a broken provider as a clean decision, and the
                // operator would see a plausible trace for a stage that has
                // not worked in weeks.
                Expect.throws
                    (fun () -> rewriter.Rewrite followUpQuery history |> Async.RunSynchronously |> ignore)
                    "a provider error surfaces to the pipeline"
            }

            test "the prompt carries the trailing history window and the query" {
                // Sentinel tokens, not "oldest"/"newest": the prompt's own
                // header says "Earlier turns (oldest first)", so an
                // "oldest"-shaped sentinel makes the drop assertion pass or
                // fail on the header rather than on the window. (It failed
                // on exactly that the first time this test ran.)
                let prompt =
                    ToolUp.RAG.ProviderQueryRewriter.buildPrompt 2 followUpQuery [
                        "turn-alpha"
                        "turn-beta"
                        "turn-gamma"
                    ]

                Expect.stringContains prompt "turn-gamma" "the most recent turn is included"
                Expect.stringContains prompt "turn-beta" "…up to the window size"
                Expect.isFalse (prompt.Contains "turn-alpha") "and older turns are dropped, not truncated in"
                Expect.stringContains prompt followUpQuery "the query itself is present"
            }
        ]
    ]
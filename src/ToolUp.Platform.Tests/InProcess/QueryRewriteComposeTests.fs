// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.QueryRewriteComposeTests

open System
open System.IO
open System.Threading
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IQueryRewriter
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Phase 506 — the compose pickup for `IQueryRewriter` ─────────────
//
// `QueryRewriteContract.fs` covers the pipeline stage, the seam, the
// `QueryDependence` gate and the shipped provider-backed rewriter. What it
// does not cover — and what actually decides whether the stage runs at all
// in a real deployment — is the DI probe in `composeWithRAG`: the step that
// looks for a registered `IQueryRewriter` and threads it into the pipeline
// constructor. Until this file, that step was covered only by "it compiles".
//
// The probe is deliberately a probe rather than a `RAGServerApp` field (the
// opt-in already has a home in DI, so the compose surface grows no knob),
// which is precisely why it needs a test of its own: there is no builder
// call whose absence would be noticed. Dropped, mistyped, or ordered after
// the pipeline is constructed, the stage silently never fires and every
// contract-pack assertion still passes.
//
// Shape copied from `FactResolverComposeTests` (Phase 558), which tests the
// sibling pickup in the same compose block.

// ── Deterministic fixed-vocabulary embedder ──
//
// One dimension per known word, NOT a hashed bag-of-words:
// `String.GetHashCode()` is randomised per process, so a hashing embedder
// makes any RANKING assertion a coin flip on which words collided. Same
// vocabulary and corpus arithmetic as `QueryRewriteContract`, for the same
// reason — a reader can check the cosines by hand.

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

/// Always substitutes `rewritten`, counting calls so a test can assert the
/// stage did NOT run as well as that it did.
type private FixedRewriter(rewritten: string) =
    let calls = ref 0
    member _.Calls = calls.Value

    interface IQueryRewriter with
        member _.Name = "compose-test-rewriter"

        member _.Rewrite _query _history = async {
            Interlocked.Increment calls |> ignore
            return QueryRewritten rewritten
        }

// ── The corpus ──
//
// Both documents discuss tolerance; only one is about widgets, and the
// sprocket document carries `tolerance` at the higher weight. So the
// subject-less follow-up lands on the WRONG document raw and the right one
// rewritten — a pair, because either half alone is unfalsifiable.

let private corpusRows: (string * string) list = [
    "widget-policy", "widget assembly guidance widget tolerance widget production widget line"
    "sprocket-policy", "sprocket calibration tolerance measured quarterly by the sprocket team"
]

let private followUpQuery = "what about the tolerance for it?"
let private rewrittenQuery = "widget tolerance"

let private history = [
    "Tell me about the widget production line."
    "Widget assembly is covered by the widget guidance."
]

let private access =
    AccessContext.unrestricted (Subject.AnonymousSession "query-rewrite-compose")

// ── Harness ──

/// Build the DI a composed deployment presents to the RAG compose probe:
/// a rewriter registered as a singleton, or nothing at all.
let private builtProviderWith (rewriter: IQueryRewriter option) : ServiceProvider =
    let services = ServiceCollection()

    match rewriter with
    | Some r -> services.AddSingleton<IQueryRewriter>(r) |> ignore
    | None -> ()

    services.BuildServiceProvider()

/// Mirror of `RAGCompose`'s Phase 506 pickup, verbatim in shape: probe the
/// built provider for an `IQueryRewriter` and hand the *option* to the
/// pipeline constructor. An absent registration must produce `None` — an
/// omitted optional argument — never `Some null`.
let private queryRewriterPickup (sp: ServiceProvider) : IQueryRewriter option =
    match sp.GetService(typeof<IQueryRewriter>) with
    | :? IQueryRewriter as r -> Some r
    | _ -> None

type private Bound = {
    Pipeline: IRetrievalPipeline
    Dispose: unit -> unit
}

let private newVectorStore () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-query-rewrite-compose-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage

    let store =
        new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(
            storage,
            logger = SilentLogger(),
            flushIntervalMs = 60000
        )

    store, tempDir

/// Seed the shared corpus through the production ingestion path.
let private seed (pipeline: IRetrievalPipeline) =
    for id, body in corpusRows do
        pipeline.Index id { Content = body; Metadata = Map.empty } Deployment
        |> Async.RunSynchronously

/// A pipeline built the way the compose block builds it: constructor arg
/// supplied from the DI probe, present exactly when a rewriter is
/// registered.
let private composedOver (sp: ServiceProvider) : Bound =
    let store, tempDir = newVectorStore ()

    let pipeline =
        new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(
            store :> IVectorStore,
            BowEmbedder(),
            ?queryRewriter = queryRewriterPickup sp
        )
        :> IRetrievalPipeline

    seed pipeline

    {
        Pipeline = pipeline
        Dispose =
            fun () ->
                (store :> IDisposable).Dispose()

                try
                    Directory.Delete(tempDir, true)
                with _ ->
                    ()
    }

/// A pipeline that never had query-rewrite wiring at all — the control for
/// the GP 13 half.
let private neverWired () : Bound =
    let store, tempDir = newVectorStore ()

    let pipeline =
        new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(store :> IVectorStore, BowEmbedder()) :> IRetrievalPipeline

    seed pipeline

    {
        Pipeline = pipeline
        Dispose =
            fun () ->
                (store :> IDisposable).Dispose()

                try
                    Directory.Delete(tempDir, true)
                with _ ->
                    ()
    }

let private withBound (bound: Bound) (f: Bound -> unit) =
    try
        f bound
    finally
        bound.Dispose()

/// Retrieve at TopK = 1 so "did the stage fire?" is a single value rather
/// than a ranking judgement.
let private topHit (bound: Bound) (query: string) (hist: string list option) : string option =
    let request = {
        RetrievalRequest.create query [ Deployment ] 1 MergeStrategy.Interleaved with
            History = hist
    }

    bound.Pipeline.Retrieve request access
    |> Async.RunSynchronously
    |> List.tryHead
    |> Option.map _.ChunkId

// ── The tests ──

let tests =
    testList "Phase 506 query-rewrite compose pickup" [

        test "a registered IQueryRewriter reaches the composed pipeline — and an unregistered one does not" {
            // Half one: registered in DI, picked up by the probe, and the
            // follow-up now lands on the right document. The call counter
            // is what distinguishes "the stage ran" from "retrieval got
            // lucky", so both are asserted.
            let rewriter = FixedRewriter(rewrittenQuery)

            withBound (composedOver (builtProviderWith (Some(rewriter :> IQueryRewriter)))) (fun bound ->
                let hit = topHit bound followUpQuery (Some history)

                Expect.equal hit (Some "widget-policy") "the registered rewriter resolved the anaphor before retrieval"

                Expect.equal rewriter.Calls 1 "the composed pipeline actually invoked the registered rewriter")

            // Half two: the SAME compose path with nothing registered. The
            // probe finds no rewriter, the stage never runs, and the raw
            // follow-up goes confidently to the wrong document. Asserted as
            // an equality — "not widget-policy" would also be satisfied by
            // retrieving nothing at all.
            withBound (composedOver (builtProviderWith None)) (fun bound ->
                let hit = topHit bound followUpQuery (Some history)

                Expect.equal
                    hit
                    (Some "sprocket-policy")
                    "no registration ⇒ no rewrite ⇒ the subject-less query lands on the wrong document")
        }

        test "no registration ⇒ the constructor argument is omitted, not passed as a null (GP 11 / GP 13)" {
            // The pickup itself: `GetService` returns null for an
            // unregistered interface, and the type test must turn that into
            // `None` so `?queryRewriter` is genuinely absent. A pickup that
            // produced `Some null` would compile, and the pipeline would
            // then null-reference on the first request carrying History.
            use sp = builtProviderWith None
            Expect.isNone (queryRewriterPickup sp) "an unregistered rewriter probes to None"

            // And the behavioural half: a pipeline composed from that empty
            // provider retrieves byte-identically to one that never had the
            // argument at all.
            let composed = composedOver (builtProviderWith None)
            let plain = neverWired ()

            try
                let request = {
                    RetrievalRequest.create followUpQuery [ Deployment ] 2 MergeStrategy.Interleaved with
                        History = Some history
                }

                let composedResults =
                    composed.Pipeline.Retrieve request access |> Async.RunSynchronously

                let plainResults = plain.Pipeline.Retrieve request access |> Async.RunSynchronously

                Expect.equal
                    (composedResults |> List.map _.ChunkId)
                    (plainResults |> List.map _.ChunkId)
                    "byte-identical retrieval, History and all"
            finally
                composed.Dispose()
                plain.Dispose()
        }
    ]
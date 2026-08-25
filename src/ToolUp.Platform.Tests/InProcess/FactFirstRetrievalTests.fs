// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FactFirstRetrievalTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI.SystemPromptBuilder
open ToolUp.RAG

// ─── Phase 522 — fact-first retrieval ────────────────────────────────
//
// The retrieval pipeline resolves a (subject, metric, period) fact clause
// BEFORE vector search and merges the exact fact hits ahead of the
// similarity chunks as a distinct `ChunkOrigin.Fact` origin. Covered here:
// clause-absent parity, fact-hit merge order (fact is the top source),
// stale-fact freshness + supersession surfacing, scope isolation (the
// resolver is handed the caller's own scope), the fact→narrative join
// boost, and the `RAGPromptBuilder` facts block + source projection.

// ── Shared harness (mirrors KnowledgeUserScopeIsolationTests) ──

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

let private unitVec: float32 array =
    Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

type private ConstantEmbedder() =
    interface IEmbeddingProvider with
        member _.GenerateEmbedding _ = async { return unitVec }

        member _.GenerateEmbeddings texts =
            batchedFallback (fun _ -> async { return unitVec }) texts

        member _.Dimensions = 8
        member _.ProviderId = "test"
        member _.ModelId = "constant-v1"

/// Fake fact resolver: returns a fixed list and records the scope it was
/// asked to resolve under (so a test can pin that the pipeline hands it the
/// caller's own scope, never a caller-supplied one).
type private RecordingResolver(facts: ResolvedFact list) =
    let mutable lastScope: string option = None
    member _.LastScope = lastScope

    interface IFactResolver with
        member _.Resolve(scopeId, _clause) = async {
            lastScope <- Some scopeId
            return facts
        }

let private chunkWith (meta: (string * string) list) (content: string) : TextChunk = {
    Content = content
    Metadata = Map.ofList meta
}

let private newStore () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-fact-first-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage

    let store =
        new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(
            storage,
            logger = SilentLogger(),
            flushIntervalMs = 60000
        )

    store :> IVectorStore, store :> IDisposable

let private pipelineWith (store: IVectorStore) (resolver: IFactResolver option) : IRetrievalPipeline =
    match resolver with
    | Some r ->
        new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(store, ConstantEmbedder(), factResolver = r)
        :> IRetrievalPipeline
    | None -> new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(store, ConstantEmbedder()) :> IRetrievalPipeline

let private sampleClause: FactClause = {
    SubjectHierarchy = "brand"
    SubjectPath = [ "acme" ]
    Metric = "revenue"
    PeriodFrom = None
    PeriodTo = None
    AsOf = None
}

let private freshFact: ResolvedFact = {
    FactId = "fact-1"
    Rendering = "£21,800"
    Freshness = FactFresh
    SupersededBy = None
    Metric = "revenue"
}

let private staleFact: ResolvedFact = {
    FactId = "fact-old"
    Rendering = "£19,000"
    Freshness = FactStale "2026-06-01T00:00:00Z"
    SupersededBy = Some "fact-new"
    Metric = "revenue"
}

let private isFact (m: VectorMatch) =
    m.Metadata.TryFind ChunkMetadata.OriginKey = Some "Fact"

let private uctx (userId: string) =
    AccessContext.unrestricted (AuthenticatedUser userId)

let private withClause (req: RetrievalRequest) = {
    req with
        FactClause = Some sampleClause
}

let tests =
    testList "Phase 522 fact-first retrieval" [

        // ── Clause-absent parity ──────────────────────────────────────
        testAsync "no fact clause ⇒ no fact resolution, chunk-only results (byte-identical)" {
            let store, dispose = newStore ()

            try
                do! store.Upsert (User "u") "c1" unitVec (chunkWith [] "a chunk")
                // Resolver is wired, but the request carries NO clause.
                let resolver = RecordingResolver [ freshFact ]
                let pipeline = pipelineWith store (Some(resolver :> IFactResolver))
                let request = RetrievalRequest.create "q" [ User "u" ] 10 Interleaved

                let! results = pipeline.Retrieve request (uctx "u")

                Expect.isFalse (results |> List.exists isFact) "no fact origin without a clause"
                Expect.equal (results |> List.map _.ChunkId) [ "c1" ] "just the seeded chunk"
                Expect.isNone resolver.LastScope "the resolver is never called without a clause"
            finally
                dispose.Dispose()
        }

        testAsync "a clause with NO resolver wired ⇒ dormant (no facts, GP 13)" {
            let store, dispose = newStore ()

            try
                do! store.Upsert (User "u") "c1" unitVec (chunkWith [] "a chunk")
                let pipeline = pipelineWith store None // no resolver
                let request = withClause (RetrievalRequest.create "q" [ User "u" ] 10 Interleaved)

                let! results = pipeline.Retrieve request (uctx "u")

                Expect.isFalse (results |> List.exists isFact) "clause is inert without a resolver"
                Expect.equal (results |> List.map _.ChunkId) [ "c1" ] "chunk-only, unchanged"
            finally
                dispose.Dispose()
        }

        // ── Fact-hit merge order: the fact is the top source ──────────
        testAsync "a resolved fact merges ahead of the similarity chunks as the top source" {
            let store, dispose = newStore ()

            try
                do! store.Upsert (User "u") "c1" unitVec (chunkWith [] "a chunk")
                let resolver = RecordingResolver [ freshFact ]
                let pipeline = pipelineWith store (Some(resolver :> IFactResolver))
                let request = withClause (RetrievalRequest.create "q" [ User "u" ] 10 Interleaved)

                let! results = pipeline.Retrieve request (uctx "u")

                match results with
                | top :: rest ->
                    Expect.isTrue (isFact top) "the top source is the resolved fact"
                    Expect.equal top.Content "£21,800" "the exact fact value, not a chunk paraphrase"
                    Expect.equal (top.Metadata.TryFind ChunkMetadata.FactFreshnessKey) (Some "Fresh") "fresh-stamped"
                    Expect.equal (rest |> List.map _.ChunkId) [ "c1" ] "the chunk follows the fact"
                | [] -> failtest "expected a fact + chunk, got nothing"
            finally
                dispose.Dispose()
        }

        // ── Stale-fact surfacing ──────────────────────────────────────
        testAsync "a stale fact carries its staleness marker + supersession pointer" {
            let store, dispose = newStore ()

            try
                let resolver = RecordingResolver [ staleFact ]
                let pipeline = pipelineWith store (Some(resolver :> IFactResolver))
                // No chunks seeded — a fact clause is answerable with no KB.
                let request = withClause (RetrievalRequest.create "q" [ User "u" ] 10 Interleaved)

                let! results = pipeline.Retrieve request (uctx "u")

                match results with
                | [ fact ] ->
                    Expect.equal
                        (fact.Metadata.TryFind ChunkMetadata.FactFreshnessKey)
                        (Some "Stale:2026-06-01T00:00:00Z")
                        "stale-stamped with the since-instant"

                    Expect.equal
                        (fact.Metadata.TryFind ChunkMetadata.FactSupersededByKey)
                        (Some "fact-new")
                        "carries the supersession pointer"

                    // Projection onto the wire-format RetrievedSource.
                    let source = RAGPromptBuilder.toRetrievedSource 240 fact
                    Expect.equal source.Origin Fact "origin is Fact"
                    Expect.equal source.FactId (Some "fact-old") "fact id projected"
                    Expect.equal source.FactRendering (Some "£19,000") "rendering projected"
                    Expect.equal source.FactFreshness (Some(FactStale "2026-06-01T00:00:00Z")) "freshness projected"
                    Expect.equal source.FactSupersededBy (Some "fact-new") "supersession pointer projected"
                | other -> failtestf "expected exactly one fact, got %A" other
            finally
                dispose.Dispose()
        }

        // ── Scope isolation (RAG-side of GP 4) ────────────────────────
        testAsync "the resolver is handed the caller's own scope, never a caller-supplied one" {
            let store, dispose = newStore ()

            try
                let resolver = RecordingResolver [ freshFact ]
                let pipeline = pipelineWith store (Some(resolver :> IFactResolver))
                let request = withClause (RetrievalRequest.create "q" [ User "bob" ] 10 Interleaved)

                let! _ = pipeline.Retrieve request (uctx "bob")

                Expect.equal resolver.LastScope (Some "bob") "resolver scoped to the authenticated caller's id"
            finally
                dispose.Dispose()
        }

        // ── Fact→narrative join (522.E) ───────────────────────────────
        testAsync "a chunk citing a resolved fact outranks a chunk that does not (fact→narrative join)" {
            let store, dispose = newStore ()

            try
                // Both chunks embed to the same vector, so base similarity is
                // identical — only the join boost can reorder them.
                do!
                    store.Upsert
                        (User "u")
                        "linked"
                        unitVec
                        (chunkWith [ ChunkMetadata.FactRefsKey, "fact-1" ] "cites fact-1")

                do! store.Upsert (User "u") "plain" unitVec (chunkWith [] "cites nothing")

                let resolver = RecordingResolver [ freshFact ]
                let pipeline = pipelineWith store (Some(resolver :> IFactResolver))
                let request = withClause (RetrievalRequest.create "q" [ User "u" ] 10 Interleaved)

                let! results = pipeline.Retrieve request (uctx "u")

                let chunkOrder = results |> List.filter (isFact >> not) |> List.map _.ChunkId

                Expect.equal
                    chunkOrder
                    [ "linked"; "plain" ]
                    "the fact-linked narrative chunk is preferred over generic similarity"
            finally
                dispose.Dispose()
        }

        // ── RAGPromptBuilder facts block + sources (522.C/D) ──────────
        testAsync "withRetrieval renders a distinct facts block (quote-verbatim) with facts leading the sources" {
            // Stub pipeline returns a fact ahead of a chunk (the shape the
            // real pipeline produces); exercises the builder's fact handling
            // without a vector store.
            let factMatch: VectorMatch = {
                ChunkId = "fact:fact-1"
                Content = "£21,800"
                Score = 1.0
                Scope = Team "t"
                Metadata =
                    Map.ofList [
                        ChunkMetadata.OriginKey, ChunkOrigin.toMetadataValue Fact
                        ChunkMetadata.FactIdKey, "fact-1"
                        ChunkMetadata.FactRenderingKey, "£21,800"
                        ChunkMetadata.FactMetricKey, "revenue"
                        ChunkMetadata.FactFreshnessKey, "Fresh"
                    ]
            }

            let chunkMatch: VectorMatch = {
                ChunkId = "c1"
                Content = "some supporting prose"
                Score = 0.4
                Scope = Team "t"
                Metadata = Map.ofList [ ChunkMetadata.OriginKey, "Narrative" ]
            }

            let stub =
                { new IRetrievalPipeline with
                    member _.Retrieve _ _ = async { return [ factMatch; chunkMatch ] }
                    member _.Index _ _ _ = async { return () }
                    member _.DeleteByScope _ = async { return () }
                }

            let sources = ref []

            let ctx: PromptContext = {
                Access = AccessContext.unrestricted (AuthenticatedUser "u")
                ActiveModule = None
                ActivePage = None
                ActivePageNarrative = None
                ModuleContexts = Map.empty
                CurrentMessage = Some "what was revenue?"
                ConversationHistory = []
                RetrievalFilters = None
                RetrievedSources = sources
                ShortCircuit = ref None
                PlannedAnswerId = ref None
            }

            let builder =
                RAGPromptBuilder.withRetrieval RetrievalDefaults.defaults None None stub

            let! prompt = builder ctx

            Expect.stringContains
                prompt
                "quote these numbers verbatim"
                "the facts block carries the verbatim-quote instruction"

            Expect.stringContains prompt "[F1] revenue: £21,800" "the fact renders in the facts block"
            Expect.stringContains prompt "context from" "the narrative chunk context still renders below the facts"

            // Sources: the fact leads, with its fact fields populated.
            match sources.Value with
            | fst :: _ ->
                Expect.equal fst.Origin Fact "the fact is the first source"
                Expect.equal fst.FactId (Some "fact-1") "fact id surfaced on the source"
                Expect.equal fst.FactRendering (Some "£21,800") "fact rendering surfaced on the source"
            | [] -> failtest "expected retrieved sources"
        }

        // ── MinScore never gates a fact ───────────────────────────────
        testAsync "a fact is never dropped by the MinScore gate (facts are exact, not similarity)" {
            let factMatch: VectorMatch = {
                ChunkId = "fact:fact-1"
                Content = "£21,800"
                // A deliberately low score — below any plausible MinScore —
                // to prove facts bypass the chunk score gate entirely.
                Score = 0.01
                Scope = Team "t"
                Metadata =
                    Map.ofList [
                        ChunkMetadata.OriginKey, ChunkOrigin.toMetadataValue Fact
                        ChunkMetadata.FactIdKey, "fact-1"
                        ChunkMetadata.FactRenderingKey, "£21,800"
                        ChunkMetadata.FactMetricKey, "revenue"
                        ChunkMetadata.FactFreshnessKey, "Fresh"
                    ]
            }

            let stub =
                { new IRetrievalPipeline with
                    member _.Retrieve _ _ = async { return [ factMatch ] }
                    member _.Index _ _ _ = async { return () }
                    member _.DeleteByScope _ = async { return () }
                }

            let sources = ref []

            let ctx: PromptContext = {
                Access = AccessContext.unrestricted (AuthenticatedUser "u")
                ActiveModule = None
                ActivePage = None
                ActivePageNarrative = None
                ModuleContexts = Map.empty
                CurrentMessage = Some "what was revenue?"
                ConversationHistory = []
                RetrievalFilters = None
                RetrievedSources = sources
                ShortCircuit = ref None
                PlannedAnswerId = ref None
            }

            // A high MinScore that would drop any real chunk.
            let defaults = {
                RetrievalDefaults.defaults with
                    MinScore = Some 0.5
            }

            let builder = RAGPromptBuilder.withRetrieval defaults None None stub

            let! prompt = builder ctx

            Expect.stringContains prompt "[F1] revenue: £21,800" "the low-scored fact still surfaces despite MinScore"
            Expect.equal (sources.Value |> List.map _.FactId) [ Some "fact-1" ] "the fact is the sole surfaced source"
        }
    ]
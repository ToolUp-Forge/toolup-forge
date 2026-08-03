// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.RAG.MetadataFilterContract

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.StaticCorpus

// ─── Phase 502 — metadata-filtered retrieval, parity pack ────────────
//
// `RetrievalRequest.Filters` is a SHARED contract field (GP 10): every
// shipped `IRetrievalPipeline` must mean the same thing by it. It was
// documented and honoured by the static-corpus pipeline — with a contract
// test — but the default `RetrievalPipeline.Retrieve` never read it, so a
// caller passing `Filters` got a full, unfiltered result set with zero
// signal. A filter is a narrowing / isolation intent (GP 4): silently
// ignoring one puts exactly the content the caller asked to exclude in
// front of the model.
//
// This file is therefore written as a PACK, not a per-impl test: one set
// of behaviours, bound to both shipped pipelines, so the two can never
// diverge on this field again. `filterBehaviours` below is the contract;
// anything asserted only of one implementation lives in its own list at
// the bottom and says why.

// ── Deterministic bag-of-words embedder ──
//
// Each word bumps the dimension it hashes to, so lexical overlap becomes
// cosine similarity. Enough to rank a small corpus without a real model,
// and identical for both pipelines (the static corpus is packed with
// embeddings from this same function).

let private dim = 32

let private bow (text: string) : float32[] =
    let v = Array.zeroCreate<float32> dim

    let words =
        text.ToLowerInvariant().Split([| ' '; '\n'; '\t'; '.'; ','; '#' |], StringSplitOptions.RemoveEmptyEntries)

    for w in words do
        let h = (abs (w.GetHashCode())) % dim
        v[h] <- v[h] + 1.0f

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

// ── The shared corpus ──
//
// Two documents, two tags, and one deliberately UNKEYED chunk. Every body
// carries the word "widget", so the single query below matches all six —
// which is what makes a filter's narrowing observable rather than
// confounded with a query that simply missed.

let private corpusRows: (string * (string * string) list * string) list = [
    "a1", [ "documentId", "doc-a"; "tag", "policy" ], "widget policy alpha retention rules"
    "a2", [ "documentId", "doc-a"; "tag", "policy" ], "widget policy beta retention rules"
    "a3", [ "documentId", "doc-a"; "tag", "guide" ], "widget guide gamma setup steps"
    "b1", [ "documentId", "doc-b"; "tag", "policy" ], "widget policy delta pricing rules"
    "b2", [ "documentId", "doc-b"; "tag", "guide" ], "widget guide epsilon setup steps"
    // No `documentId`, no `tag`. Pins the strict-absence rule: a chunk that
    // cannot prove it belongs to the requested slice is excluded, not
    // admitted. (Deliberately the opposite of `OriginFilter`, which keeps
    // chunks carrying no `_origin` stamp.)
    "u1", [], "widget untagged zeta loose note"
]

let private query = "widget"

/// Every chunk id in the corpus — the "no filter at all" expectation, and
/// the negative control that keeps every filtered assertion below from
/// passing vacuously on an empty result set.
let private allIds = corpusRows |> List.map (fun (id, _, _) -> id) |> Set.ofList

let private docAIds = Set.ofList [ "a1"; "a2"; "a3" ]

// ── Harness ──

/// One bound pipeline plus a disposer. `Retrieve` is curried through a
/// small helper so each behaviour reads as "these filters, this TopK".
type private Bound = {
    Name: string
    Pipeline: IRetrievalPipeline
    Dispose: unit -> unit
}

let private access =
    AccessContext.unrestricted (Subject.AnonymousSession "metadata-filter")

let private retrieve (bound: Bound) (filters: Map<string, string> option) (topK: int) : Set<string> =
    let request = {
        RetrievalRequest.create query [ Deployment ] topK MergeStrategy.Interleaved with
            Filters = filters
    }

    bound.Pipeline.Retrieve request access
    |> Async.RunSynchronously
    |> List.map _.ChunkId
    |> Set.ofList

/// The default pipeline: an `InMemoryVectorStore` seeded through
/// `IRetrievalPipeline.Index`, which is the production ingestion path.
let private bindDefaultPipeline () : Bound =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-metadata-filter-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage

    let store =
        new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(
            storage,
            logger = SilentLogger(),
            flushIntervalMs = 60000
        )

    let pipeline =
        new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(store :> IVectorStore, BowEmbedder()) :> IRetrievalPipeline

    for id, meta, body in corpusRows do
        pipeline.Index
            id
            {
                Content = body
                Metadata = Map.ofList meta
            }
            Deployment
        |> Async.RunSynchronously

    {
        Name = "default RetrievalPipeline"
        Pipeline = pipeline
        Dispose =
            fun () ->
                (store :> IDisposable).Dispose()

                try
                    Directory.Delete(tempDir, true)
                with _ ->
                    ()
    }

/// The static-corpus pipeline: the same rows packed as a read-only
/// `StaticCorpus`, embeddings precomputed with the same `bow`.
let private bindStaticCorpusPipeline () : Bound =
    let corpus: StaticCorpus = {
        Chunks = [|
            for id, meta, body in corpusRows ->
                {
                    Id = id
                    Source = sprintf "%s.md" id
                    HeadingPath = [ "# Docs" ]
                    Body = body
                    Embedding = bow body
                    Metadata = Map.ofList meta
                }
        |]
        EmbeddingModel = "bow-v1"
        EmbeddingDimensions = dim
        BuiltUtc = DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc)
        PackerVersion = "502.0.0"
    }

    {
        Name = "StaticCorpusRetrievalPipeline"
        Pipeline = StaticCorpusRetrievalPipeline.create (BowEmbedder()) corpus
        Dispose = id
    }

// ── The contract ──

let private filterBehaviours (bind: unit -> Bound) =
    // Bound once and reused: seeding the default pipeline embeds + indexes
    // six chunks, and every behaviour below is a read.
    let bound = bind ()

    testList bound.Name [

        // Negative control. Every filtered assertion below is "a smaller
        // set came back"; without this one they would all pass just as
        // happily against a pipeline that returned nothing at all.
        test "no filter returns the whole corpus (control)" {
            let got = retrieve bound None 10
            Expect.equal got allIds "an unfiltered query matches every chunk"
        }

        test "a documentId filter returns only that document's chunks" {
            let got = retrieve bound (Some(Map.ofList [ "documentId", "doc-a" ])) 10
            Expect.equal got docAIds "only doc-a chunks survive the filter"
        }

        // The assertion the original defect fails. A pipeline that ignores
        // `Filters` returns the full corpus here — which is precisely the
        // "content they meant to exclude reaches the model" failure, and
        // it is silent: no error, no empty result, just the wrong answer.
        test "a filter matching nothing returns nothing, not everything" {
            let got =
                retrieve bound (Some(Map.ofList [ "documentId", "doc-does-not-exist" ])) 10

            Expect.isEmpty got "an unmatched filter excludes the corpus rather than passing it through"
        }

        test "multiple filter keys are AND-combined" {
            let got =
                retrieve bound (Some(Map.ofList [ "documentId", "doc-a"; "tag", "policy" ])) 10

            Expect.equal got (Set.ofList [ "a1"; "a2" ]) "both pairs must match, not either"
        }

        test "a chunk missing the filtered key is excluded" {
            let got = retrieve bound (Some(Map.ofList [ "tag", "policy" ])) 10
            Expect.equal got (Set.ofList [ "a1"; "a2"; "b1" ]) "tag=policy chunks only"
            Expect.isFalse (got.Contains "u1") "the chunk carrying no `tag` key does not pass the filter"
        }

        // Filtering must not cost recall INSIDE the slice the caller
        // scoped to. TopK=3 against the whole corpus returns an arbitrary
        // three of six; the same TopK filtered to doc-a must return all
        // three of doc-a, not three-minus-whatever-the-store-ranked-higher.
        test "recall within the filtered set is unaffected" {
            let unfiltered = retrieve bound None 3
            Expect.equal unfiltered.Count 3 "TopK caps the unfiltered query at 3 of 6"

            let filtered = retrieve bound (Some(Map.ofList [ "documentId", "doc-a" ])) 3
            Expect.equal filtered docAIds "every in-filter chunk is recalled at the same TopK"
        }

        // An empty map constrains nothing, so it must behave as `None`
        // rather than as "match chunks with no metadata" or "match none".
        test "an empty filter map is a no-op" {
            let got = retrieve bound (Some Map.empty) 10
            Expect.equal got allIds "Some (empty map) behaves exactly as None"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 502 — metadata-filtered retrieval" [
        filterBehaviours bindDefaultPipeline
        filterBehaviours bindStaticCorpusPipeline
    ]
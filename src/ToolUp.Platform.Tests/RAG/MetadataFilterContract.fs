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

// ─── Phase 502.D — reaching the filter from the prompt path ──────────
//
// 502.A taught both pipelines to HONOUR `Filters`; it did not make the
// field reachable from the AI path. `RAGPromptBuilder.withRetrievalToolAware`
// constructs its own `RetrievalRequest`, so a deployment composing
// `RAGServerApp` — and the AI panel talking to it — could not scope a query
// however willing the pipeline was. The behaviours below pin the two
// carriers that close that (`RetrievalDefaults.Filters`, the operator's
// deployment-wide bound; `PromptContext.RetrievalFilters`, the per-turn one
// arriving from `AIMessageRequest`) and the rule for combining them.
//
// The stub pipeline CAPTURES the request rather than answering it: what is
// under test is the value the builder hands the pipeline, which no
// end-to-end retrieval assertion can isolate from ranking.

/// Records the last `RetrievalRequest` it was handed and returns nothing,
/// so the assertions read the builder's output directly.
let private capturingPipeline (sink: RetrievalRequest option ref) =
    { new IRetrievalPipeline with
        member _.Retrieve request _ = async {
            sink.Value <- Some request
            return []
        }

        member _.Index _ _ _ = async { return () }
        member _.DeleteByScope _ = async { return () }
    }

let private promptContextWith (perRequest: Map<string, string> option) : ToolUp.AI.SystemPromptBuilder.PromptContext = {
    Access = access
    ActiveModule = None
    ActivePage = None
    ActivePageNarrative = None
    ModuleContexts = Map.empty
    CurrentMessage = Some query
    ConversationHistory = []
    RetrievalFilters = perRequest
    RetrievedSources = ref []
    ShortCircuit = ref None
    PlannedAnswerId = ref None
}

/// Run the tool-aware builder with the given deployment + per-request
/// filters and return the `Filters` it put on the request.
let private filtersReachingPipeline
    (deployment: Map<string, string> option)
    (perRequest: Map<string, string> option)
    : Map<string, string> option =
    let captured: RetrievalRequest option ref = ref None

    let defaults = {
        RetrievalDefaults.defaults with
            Filters = deployment
    }

    ToolUp.RAG.RAGPromptBuilder.withRetrievalToolAware
        defaults
        None
        None
        ToolUp.RAG.RAGPromptBuilder.ToolFraming.none
        (capturingPipeline captured)
        (promptContextWith perRequest)
    |> Async.RunSynchronously
    |> ignore

    match captured.Value with
    | None -> failwith "the builder never called Retrieve — the capture is vacuous"
    | Some request -> request.Filters

let private promptPathBehaviours =
    testList "502.D — filters reach the pipeline from the prompt path" [

        // The control for every case below: without it, "the expected
        // filter arrived" cannot be distinguished from a builder that
        // always sends the same thing.
        test "an unscoped turn sends no filter at all" {
            let got = filtersReachingPipeline None None

            Expect.isNone
                got
                "neither carrier set ⇒ Filters = None, so the request is byte-identical to its pre-502.D shape"
        }

        test "a per-request filter reaches the pipeline" {
            let got = filtersReachingPipeline None (Some(Map.ofList [ "documentId", "doc-a" ]))
            Expect.equal got (Some(Map.ofList [ "documentId", "doc-a" ])) "PromptContext.RetrievalFilters is threaded"
        }

        test "a deployment filter reaches the pipeline" {
            let got = filtersReachingPipeline (Some(Map.ofList [ "tag", "policy" ])) None
            Expect.equal got (Some(Map.ofList [ "tag", "policy" ])) "RetrievalDefaults.Filters is threaded"
        }

        // Both are narrowing intents, so the merge must constrain at least
        // as much as either input — never drop a key one of them set.
        test "disjoint keys from both carriers are combined" {
            let got =
                filtersReachingPipeline
                    (Some(Map.ofList [ "tag", "policy" ]))
                    (Some(Map.ofList [ "documentId", "doc-a" ]))

            Expect.equal
                got
                (Some(Map.ofList [ "tag", "policy"; "documentId", "doc-a" ]))
                "the merged filter carries both keys"
        }

        // The security-relevant half: a deployment filter is an operator
        // bound, and `RetrievalFilters` is client-supplied. If a request
        // could overwrite a key the operator set, an operator scoping a
        // deployment to one slice would be scoping nothing.
        test "the deployment value wins a key both carriers set" {
            let got =
                filtersReachingPipeline
                    (Some(Map.ofList [ "tag", "policy" ]))
                    (Some(Map.ofList [ "tag", "anything-else" ]))

            Expect.equal
                got
                (Some(Map.ofList [ "tag", "policy" ]))
                "a per-request filter cannot relax a deployment bound"
        }

        // The merge helper in isolation, so the rule is pinned
        // independently of the builder that happens to call it today.
        test "mergeFilters is a union with deployment precedence" {
            Expect.isNone (RetrievalDefaults.mergeFilters None None) "None/None stays None"

            Expect.equal
                (RetrievalDefaults.mergeFilters
                    (Some(Map.ofList [ "k", "d" ]))
                    (Some(Map.ofList [ "k", "r"; "j", "r" ])))
                (Some(Map.ofList [ "k", "d"; "j", "r" ]))
                "shared key takes the deployment value, unshared keys survive"
        }
    ]

// ─── Phase 502.D — the wire hop ──────────────────────────────────────
//
// `AIMessageRequest` is the client→server Remoting contract, so widening it
// is only safe if a client compiled before 502.D — which sends no
// `RetrievalFilters` property at all — still deserialises. An `option`
// field absorbs the absent property to `None`; this pins that rather than
// assuming it, because the failure mode is a deserialisation throw on every
// message from an un-upgraded client, which no in-process test would see.
//
// The legacy payload is DERIVED by stripping the new property from a
// serialised current request rather than hand-written, so it is the real
// pre-502.D wire shape by construction and cannot drift from it.

let private wireBehaviours =
    let jsonOptions = ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

    let current: ToolUp.AI.AIMessageRequest = {
        ConversationId = Guid("00000000-0000-0000-0000-0000000000aa")
        Content = "what is the retention policy?"
        ActiveModule = None
        ActivePage = None
        ActivePageNarrative = None
        OverrideProviderLabel = None
        Surface = ToolUp.AI.SidePanel
        RetrievalFilters = Some(Map.ofList [ "documentId", "doc-a" ])
    }

    testList "502.D — AIMessageRequest wire compatibility" [

        test "a legacy payload with no RetrievalFilters deserialises to None" {
            let json = Text.Json.JsonSerializer.Serialize(current, jsonOptions)

            Expect.stringContains json "RetrievalFilters" "the property is on the wire for a current client (control)"

            // Strip the whole property, reproducing what a pre-502.D client
            // emits. Round-tripping through a node lets the rest of the
            // payload stay exactly as the serialiser wrote it.
            let doc = Text.Json.JsonDocument.Parse json

            let legacy =
                let buffer = new IO.MemoryStream()
                use writer = new Text.Json.Utf8JsonWriter(buffer)
                writer.WriteStartObject()

                for property in doc.RootElement.EnumerateObject() do
                    if not (property.Name.Equals("RetrievalFilters", StringComparison.OrdinalIgnoreCase)) then
                        property.WriteTo writer

                writer.WriteEndObject()
                writer.Flush()
                Text.Encoding.UTF8.GetString(buffer.ToArray())

            Expect.isFalse
                (legacy.Contains "RetrievalFilters")
                "the derived legacy payload really is missing the property"

            let decoded =
                Text.Json.JsonSerializer.Deserialize<ToolUp.AI.AIMessageRequest>(legacy, jsonOptions)

            Expect.isNone decoded.RetrievalFilters "an absent property absorbs to None rather than throwing"
            Expect.equal decoded.Content current.Content "the rest of the legacy payload is unaffected"
            Expect.equal decoded.Surface current.Surface "including the DU-shaped Surface field"
        }

        test "a current payload round-trips its filter" {
            let json = Text.Json.JsonSerializer.Serialize(current, jsonOptions)

            let decoded =
                Text.Json.JsonSerializer.Deserialize<ToolUp.AI.AIMessageRequest>(json, jsonOptions)

            Expect.equal
                decoded.RetrievalFilters
                current.RetrievalFilters
                "Map<string,string> option survives the Remoting JSON contract"
        }
    ]

// ── 502.C — the tag vocabulary against the shipped filter ──
//
// 502.A/B pinned that `Filters` narrows; 502.D pinned that it reaches
// the pipeline from the prompt path. What was missing was a VOCABULARY
// — a way for a user to say "only policy documents" — and the claim
// 502.C makes is that adding one needed no pipeline change at all.
//
// These behaviours are what makes that claim checkable rather than
// asserted. The corpus below is stamped through
// `KnowledgeTags.metadataPairs`, i.e. the same function the KB
// ingestion path calls, and then filtered through the same
// `RetrievalPipeline` every other behaviour in this file uses. Nothing
// is hand-written except one assertion pinning the concrete key string
// — without which a silent change to the convention would simply move
// both sides together and prove nothing.

let private tagRows: (string * string list * string) list = [
    "t-hr1", [ "policy"; "hr" ], "widget policy alpha leave entitlement"
    "t-hr2", [ "policy"; "hr" ], "widget policy beta leave entitlement"
    "t-fin", [ "policy"; "finance" ], "widget policy gamma expense limits"
    "t-guide", [ "guide" ], "widget guide delta setup steps"
    // Untagged: the strict-absence control. A chunk with no `_tag.*` key
    // must not be admitted by ANY tag filter.
    "t-none", [], "widget epsilon loose note"
]

let private bindTagPipeline () : Bound =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-tag-filter-" + Guid.NewGuid().ToString("N"))

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

    for id, tags, body in tagRows do
        pipeline.Index
            id
            {
                Content = body
                // Stamped exactly as the KB stamps a document's chunks.
                Metadata = SharedTypes.KnowledgeTags.metadataPairs tags |> Map.ofList
            }
            Deployment
        |> Async.RunSynchronously

    {
        Name = "502.C — tag vocabulary"
        Pipeline = pipeline
        Dispose =
            fun () ->
                (store :> IDisposable).Dispose()

                try
                    Directory.Delete(tempDir, true)
                with _ ->
                    ()
    }

let private tagFilter (tags: string list) =
    Some(SharedTypes.KnowledgeTags.metadataPairs tags |> Map.ofList)

let private tagBehaviours =
    let bound = bindTagPipeline ()
    let allTagIds = tagRows |> List.map (fun (id, _, _) -> id) |> Set.ofList

    testList bound.Name [

        // The one hand-written assertion in this block. Everything else
        // derives its keys from `metadataPairs`, so without this the
        // whole section would follow a convention change instead of
        // catching it.
        test "the chunk-metadata key convention is `_tag.{tag}` = \"true\"" {
            Expect.equal (SharedTypes.KnowledgeTags.metadataKey "policy") "_tag.policy" "the stamped key shape"

            Expect.equal
                (SharedTypes.KnowledgeTags.metadataPairs [ "policy" ])
                [ "_tag.policy", "true" ]
                "presence is the signal; the value exists only because the filter compares values"
        }

        // Negative control — without it every narrowing assertion below
        // would pass against a pipeline that returned nothing.
        test "no filter returns the whole tag corpus (control)" {
            Expect.equal (retrieve bound None 10) allTagIds "an unfiltered query reaches every chunk"
        }

        test "filtering on one tag returns only that tag's chunks" {
            Expect.equal
                (retrieve bound (tagFilter [ "guide" ]) 10)
                (Set.ofList [ "t-guide" ])
                "only the guide-tagged chunk comes back"
        }

        test "a tag shared by several documents returns all of them" {
            Expect.equal
                (retrieve bound (tagFilter [ "policy" ]) 10)
                (Set.ofList [ "t-hr1"; "t-hr2"; "t-fin" ])
                "the policy tag spans two documents, and both are in scope"
        }

        // The multi-tag arm. `t-fin` carries `policy` but not `hr`, so it
        // is the chunk that proves the combination is AND rather than OR
        // — an OR would return it and the assertion would fail.
        test "two tags AND together — a chunk carrying only one of them is excluded" {
            Expect.equal
                (retrieve bound (tagFilter [ "policy"; "hr" ]) 10)
                (Set.ofList [ "t-hr1"; "t-hr2" ])
                "policy AND hr excludes the finance-tagged policy chunk"
        }

        test "an untagged chunk is excluded by every tag filter (strict absence)" {
            let policyResults = retrieve bound (tagFilter [ "policy" ]) 10
            let guideResults = retrieve bound (tagFilter [ "guide" ]) 10

            Expect.isFalse (policyResults.Contains "t-none") "the untagged chunk cannot prove it belongs to the slice"
            Expect.isFalse (guideResults.Contains "t-none") "and the same holds for any other tag"

            Expect.isTrue
                ((retrieve bound None 10).Contains "t-none")
                "but it IS reachable unfiltered — so its exclusion is the filter's doing, not a missing chunk"
        }

        test "a tag no document carries returns nothing, not everything" {
            Expect.isEmpty
                (retrieve bound (tagFilter [ "nonexistent" ]) 10)
                "an unmatched tag narrows to empty — the silent no-op 502.A removed would return the whole corpus here"
        }

        // Normalisation is load-bearing for retrieval, not cosmetic: the
        // stamp is written from the normalised form, so a filter built
        // from raw user input has to normalise identically or it matches
        // nothing at all.
        test "a filter built from unnormalised user input still matches" {
            Expect.equal
                (retrieve bound (tagFilter [ "  POLICY  " ]) 10)
                (Set.ofList [ "t-hr1"; "t-hr2"; "t-fin" ])
                "casing and surrounding whitespace are normalised on both sides, so the query still hits"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 502 — metadata-filtered retrieval" [
        filterBehaviours bindDefaultPipeline
        filterBehaviours bindStaticCorpusPipeline
        promptPathBehaviours
        wireBehaviours
        tagBehaviours
    ]
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.HnswFidelityTests

open System
open System.Diagnostics
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Tests.Support
open ToolUp.RAG.VectorStores.Hnsw

// ─── Phase 14k WS5 — HNSW recall-fidelity + latency smoke ───────
//
// The strict acceptance bounds ("HNSW recall@10 within 2% of flat
// scan; p95 < 50 ms on a 500k representative corpus") were deferred by
// Phase 14k and measured by Phase 970 with the `--hnsw-scale` benchmark
// mode; the measured trade-off is in the RAG technical guide's scale
// story. The recall bound is pinned below by an OPT-IN case that runs
// only when `TOOLUP_HNSW_SCALE_CORPUS` names a corpus. What runs by
// default is the CI-safe, dependency-free smoke:
//
//   • a hard correctness invariant HNSW must satisfy regardless of
//     ANN approximation — self-retrieval (an indexed vector queried
//     back returns itself at rank 1);
//   • a loose overlap@10-vs-exact-cosine bound that catches a
//     catastrophic graph regression without flaking on the inherent
//     approximate-NN looseness of random high-dim vectors;
//   • a loose latency ceiling that catches a pathological slowdown
//     (NOT the 500k/50ms perf bound — Phase 970 measures that, opt-in).
//
// Ground truth is computed in-test by brute-force cosine, so this
// needs no `InMemoryVectorStore` reference (keeps the test project's
// lean dep surface — and flat-scan cosine *is* the exact NN that
// InMemoryVectorStore would compute anyway).

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

let private newStoreWith (parameters: HnswVectorStore.HnswParameters) =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-hnsw-fidelity-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage

    let store =
        new HnswVectorStore.HnswVectorStore(
            storage,
            SilentLogger() :> ILogger,
            flushIntervalMs = 50,
            parameters = parameters
        )

    store :> IVectorStore, store :> IDisposable

let private newStore () =
    newStoreWith HnswVectorStore.HnswParameters.defaults

let private chunk i : TextChunk = {
    Content = $"synthetic chunk {i}"
    Metadata = Map.empty
}

/// Deterministic unit-normalised random vector (cosine == dot).
let private mkVec (rng: Random) (dim: int) : float32[] =
    let raw = Array.init dim (fun _ -> rng.NextDouble() - 0.5)
    let norm = sqrt (Array.sumBy (fun x -> x * x) raw)

    raw |> Array.map (fun x -> float32 (x / (norm + 1e-12)))

let private dot (a: float32[]) (b: float32[]) =
    let mutable s = 0.0f

    for i in 0 .. a.Length - 1 do
        s <- s + a[i] * b[i]

    s

/// Exact top-K chunk ids by cosine — the NN ground truth.
let private exactTopK (corpus: (string * float32[])[]) (query: float32[]) (k: int) : Set<string> =
    corpus
    |> Array.map (fun (id, v) -> id, dot query v)
    |> Array.sortByDescending snd
    |> Array.truncate k
    |> Array.map fst
    |> Set.ofArray

// ─── Sequenced — HNSW.Net shared static state ───────────────────────
//
// `HNSW.Net` carries shared static state (most prominently
// `DefaultRandomGenerator.Instance`, but apparently more — even after
// the per-store RNG fix in `HnswVectorStore.fs`, concurrent execution
// of this testList against other testListS still surfaces intermittent
// `Graph.Core..ctor` failures inside `Graph.AddItems`). The package
// gives us no API to inspect what else is shared, so the safe posture
// is to keep HNSW-touching testListS off the parallel run queue.
//
// `testSequenced` here pairs with the same wrapper on
// `HnswVectorStoreTests` and prevents either from running concurrently
// with any other testList. The DSR contract pack also uses HNSW
// inline; sequencing the two dedicated HNSW lists removes the bulk of
// the cross-test surface without globally serialising the suite.
//
// In-isolation runs of either list pass cleanly 4/4 every time, which
// is why this is a sequencing decision, not a "skip the test" decision.
//
// The per-store RNG in `HnswVectorStore.fs` is also now seeded
// (`graphBuildSeed`), so the recall-fidelity assertions below see a
// byte-identical graph on every run — the recall numbers in the
// budget checks are reproducible, not stochastic.

let tests =
    testSequenced
    <| testList "HnswFidelity" [
        testAsync "self-retrieval: an indexed vector queried back ranks itself #1" {
            let store, dispose = newStore ()

            try
                let rng = Random 1337
                let dim = 48
                let n = 800
                let scope = Team "fid"

                let corpus = Array.init n (fun i -> string i, mkVec rng dim)

                for (id, v) in corpus do
                    do! store.Upsert scope id v (chunk id)

                // Cosine of an identical unit vector is 1.0 — the global
                // maximum — so HNSW MUST surface it at rank 1 no matter
                // how lossy the graph is. This is the non-negotiable
                // correctness floor.
                for probeIdx in [ 0; 137; 399; 642; 799 ] do
                    let id, v = corpus[probeIdx]
                    let! hits = store.Search [ scope ] v 5

                    match hits with
                    | top :: _ -> Expect.equal top.ChunkId id $"vector {id} must self-retrieve at rank 1"
                    | [] -> failtest $"vector {id} returned no hits"
            finally
                dispose.Dispose()
        }

        testAsync "overlap@10 vs exact cosine stays above the catastrophic-regression floor" {
            let store, dispose = newStore ()

            try
                let rng = Random 4242
                let dim = 48
                let n = 1500
                let scope = Team "fid"

                let corpus = Array.init n (fun i -> string i, mkVec rng dim)

                for (id, v) in corpus do
                    do! store.Upsert scope id v (chunk id)

                let queries = Array.init 25 (fun _ -> mkVec rng dim)
                let mutable overlapSum = 0.0

                for q in queries do
                    let exact = exactTopK corpus q 10
                    let! hits = store.Search [ scope ] q 10
                    let got = hits |> List.map _.ChunkId |> Set.ofList
                    overlapSum <- overlapSum + float (Set.count (Set.intersect exact got)) / 10.0

                let meanOverlap = overlapSum / float queries.Length

                // Loose floor: default HNSW (M=16) on uniform random
                // high-dim vectors is a deliberately hard ANN case.
                // ≥0.5 catches a broken graph without flaking on the
                // inherent approximate-NN looseness; the strict 2%
                // acceptance bound needs a representative corpus — the
                // opt-in Phase 970 case at the end of this list.
                Expect.isGreaterThan
                    meanOverlap
                    0.5
                    $"mean overlap@10 vs exact cosine was {meanOverlap:F3} — below the catastrophic-regression floor"
            finally
                dispose.Dispose()
        }

        testAsync "dimension-mismatch defence: mixed-dim chunks in one scope do not corrupt retrieval" {
            // Phase 14k WS5 audit slice. The TECHNICAL_GUIDE documents
            // a one-graph-per-scope contract that implies one embedding
            // dimensionality per scope. A future `IImageEmbedder`
            // companion that lands image vectors at a different
            // dimensionality MUST route to a dimension-isolated
            // partition; this test pins the *current* defence — if a
            // mixed-dim chunk does slip into a single scope (stale
            // chunk from an embedding-model swap, or the partition
            // boundary leaking before the contract is enforced), the
            // store must not corrupt retrieval of the well-formed
            // chunks. Concretely: the dominant-dimension corpus must
            // still self-retrieve correctly, and the mismatched-dim
            // intruder must never surface in a same-dim query's
            // ranked results.
            let store, dispose = newStore ()

            try
                let rng = Random 0xD1F1
                let textDim = 48 // matches the rest of this test file
                let imageDim = 96 // simulated CLIP-shaped intruder
                let scope = Team "mixed-dim"

                let textCorpus = Array.init 20 (fun i -> string i, mkVec rng textDim)

                for (id, v) in textCorpus do
                    do! store.Upsert scope id v (chunk id)

                // The intruder upsert is the dimension-violation. It
                // may or may not throw depending on HNSW.Net's build-
                // time strictness; either outcome satisfies the
                // contract ("mixed dims do not corrupt the index"), so
                // wrap defensively. The downstream search must succeed
                // regardless.
                let intruderId = "intruder-96d"
                let intruder = mkVec rng imageDim

                try
                    do! store.Upsert scope intruderId intruder (chunk intruderId)
                with _ ->
                    ()

                // Search with a query at the *dominant* dimensionality.
                // The 48-dim corpus must continue to self-retrieve
                // correctly AND the 96-dim intruder must not surface
                // (filtered by cosineDistance returning 1.0 for
                // mismatched lengths; see HnswVectorStore.fs).
                let queryId, queryVec = textCorpus[7]
                let! hits = store.Search [ scope ] queryVec 5

                Expect.isNonEmpty hits "48-dim corpus must still respond to 48-dim query"

                match hits with
                | top :: _ -> Expect.equal top.ChunkId queryId "48-dim self-retrieval unaffected by mixed-dim intruder"
                | [] -> failtest "no hits returned"

                let intruderRanked = hits |> List.exists (fun h -> h.ChunkId = intruderId)

                Expect.isFalse
                    intruderRanked
                    "96-dim intruder must not surface in 48-dim query results — mismatched-length cosineDistance returns 1.0, ranking it last and out of the top-K window"
            finally
                dispose.Dispose()
        }

        testAsync "mixed-dim fixture: HNSW recall-fidelity matches flat scan, cross-dim purity holds" {
            // Phase 14k WS5 Item 2 — fixture-driven multi-dim recall-fidelity.
            // Loads `eval-mixed-dim.json` (250 text@96 + 250 image@192
            // chunks under a single scope; 10 anchor-perturbed queries
            // per kind), seeds HNSW with the entire mixed-dim corpus,
            // and asserts three things:
            //
            //   1. HNSW's top-10 matches the same-dim flat-scan top-10
            //      within a 0.6 mean-overlap floor (same shape as the
            //      overlap@10 vs exact cosine test above, computed
            //      against the matching-dim subset of the corpus only —
            //      mismatched-dim chunks are filtered by cosineDistance
            //      returning 1.0 and never reach the flat top-10).
            //   2. HNSW's anchor recall@10 is within the Phase 14k
            //      acceptance budget of the flat-scan anchor recall@10.
            //      Phase 14k pins the absolute bound at 2% on a 500k
            //      corpus with tuned efSearch; this 500-chunk smoke
            //      uses a 10% absolute-gap budget to absorb the noise
            //      a default-M / untuned-efSearch HNSW exhibits on
            //      synthetic 96-dim random vectors.
            //   3. NO text-kind chunk surfaces in any image-query
            //      top-10, and vice versa. This is the structural
            //      cross-dim purity check at 500-chunk scale — the
            //      existing 21-chunk dimension-mismatch defence test
            //      above pins the same property at smoke scale.
            let fixture = MixedDimFixture.load (MixedDimFixture.defaultPath ())
            let store, dispose = newStore ()

            try
                let scope = Team(fixture.Metadata.scope.Substring("team:".Length))
                let metadata = Map.empty

                for c in fixture.Corpus do
                    let chunk: TextChunk = {
                        Content = c.ChunkId
                        Metadata = metadata
                    }

                    do! store.Upsert scope c.ChunkId c.Vector chunk

                let mutable hnswAnchorHits = 0
                let mutable flatAnchorHits = 0
                let mutable overlapSum = 0.0
                let mutable crossDimLeakSeen = false

                for q in fixture.Queries do
                    let! hits = store.Search [ scope ] q.Vector 10
                    let hitIds = hits |> List.map _.ChunkId
                    let hitSet = Set.ofList hitIds

                    let flat = MixedDimFixture.exactTopK fixture.Corpus q.Kind q.Vector 10

                    if hitSet.Contains q.AnchorChunkId then
                        hnswAnchorHits <- hnswAnchorHits + 1

                    if flat.Contains q.AnchorChunkId then
                        flatAnchorHits <- flatAnchorHits + 1

                    let overlap = float (Set.count (Set.intersect flat hitSet)) / 10.0
                    overlapSum <- overlapSum + overlap

                    let oppositeKindPrefix =
                        match q.Kind with
                        | MixedDimFixture.Text -> "image-"
                        | MixedDimFixture.Image -> "text-"

                    if hitIds |> List.exists (fun id -> id.StartsWith oppositeKindPrefix) then
                        crossDimLeakSeen <- true

                let queryCount = float fixture.Queries.Length
                let hnswAnchorRecall = float hnswAnchorHits / queryCount
                let flatAnchorRecall = float flatAnchorHits / queryCount
                let recallGap = flatAnchorRecall - hnswAnchorRecall
                let meanOverlap = overlapSum / queryCount

                Expect.isGreaterThanOrEqual
                    meanOverlap
                    0.6
                    $"mean overlap@10 vs same-dim flat scan was {meanOverlap:F3} — below the recall-fidelity floor on the mixed-dim corpus"

                Expect.isLessThanOrEqual
                    recallGap
                    0.10
                    $"HNSW anchor recall@10 {hnswAnchorRecall:F3} lagged flat-scan anchor recall@10 {flatAnchorRecall:F3} by {recallGap:F3} — outside the Phase 14k recall-fidelity budget"

                Expect.isFalse
                    crossDimLeakSeen
                    "cross-dim leakage: a same-dim query surfaced an opposite-kind chunk in its top-10 (mixed-dim purity invariant violated)"
            finally
                dispose.Dispose()
        }

        testAsync
            "latency smoke: HNSW stays far below a brute-force flat scan (no pathological O(n)-per-query regression)" {
            // Phase 1007 — re-sized from 10k vectors at the default profile to
            // 5k at `HnswParameters.fastBuild`. The old build cost ~9 minutes
            // of every full gate (539 s measured 2026-09-12, 56 % of the
            // gate) while asserting nothing about the build: the case pins
            // the QUERY path, which is the same code under either profile,
            // and HNSW.Net's build is super-linear in n, so halving n is most
            // of the saving. The profiles' recall is measured by the
            // benchmark and pinned for the defaults by the opt-in corpus
            // case below.
            let store, dispose = newStoreWith HnswVectorStore.HnswParameters.fastBuild

            try
                let rng = Random 99
                let dim = 48
                let n = 5000
                let scope = Team "lat"

                // Retain the corpus so the assertion can time an in-test
                // brute-force flat scan as a machine-speed baseline.
                let corpus = Array.zeroCreate<string * float32[]> n

                for i in 0 .. n - 1 do
                    let v = mkVec rng dim
                    corpus[i] <- (string i, v)
                    do! store.Upsert scope (string i) v (chunk (string i))

                // Warm one query (first query pays the lazy graph build).
                let! _ = store.Search [ scope ] (mkVec rng dim) 10

                let probes = 50
                let queries = Array.init probes (fun _ -> mkVec rng dim)

                // Brute-force flat-scan baseline — the exact O(n)-per-query
                // work a linear-scan store does. Timed on THIS machine so
                // the ceiling tracks current machine speed / load rather
                // than a fixed millisecond wall.
                let flatSw = Stopwatch.StartNew()

                for q in queries do
                    exactTopK corpus q 10 |> ignore

                flatSw.Stop()
                let flatMeanMs = flatSw.Elapsed.TotalMilliseconds / float probes

                let meanSearchMs () = async {
                    let sw = Stopwatch.StartNew()

                    for q in queries do
                        let! _ = store.Search [ scope ] q 10
                        ()

                    sw.Stop()
                    return sw.Elapsed.TotalMilliseconds / float probes
                }

                let! hnswMeanMs = meanSearchMs ()

                // NOT the 500k/<50ms perf bound (Phase 970 measures that,
                // opt-in, with the benchmark). This only trips on a pathological
                // O(n)-per-query regression: a graph that has degenerated to
                // a full scan does far more node work than a healthy
                // O(log n · M) traversal, so its latency lands well above
                // this multiple of the flat-scan baseline. The ratio is
                // machine-speed-invariant — both timings inflate together
                // under load — unlike the old fixed 150 ms wall, which
                // false-positived at 158 ms on a busy box. Measured healthy
                // ratio is ~3.3x (even on a loaded machine), so the 100x
                // ceiling keeps ~30x of headroom while still tripping on an
                // O(n) full-scan degeneration. The flat baseline is floored
                // at 1 ms so a sub-millisecond scan on a fast idle machine
                // can't drive the ceiling below the old 150 ms.
                let ceilingMs = max 1.0 flatMeanMs * 100.0

                Expect.isLessThan
                    hnswMeanMs
                    ceilingMs
                    $"HNSW mean query latency {hnswMeanMs:F1} ms over a {n}-vector graph was >100x the {flatMeanMs:F2} ms flat-scan baseline — pathological O(n)-per-query slowdown"

                // Phase 1007 — the same bound with a fifth of the graph
                // RETIRED (deleted, under the default rebuild threshold, so
                // no rebuild): the search skips retired nodes inside the
                // traversal, and a filter that degenerated into a scan of the
                // retired set would land here.
                for i in 0..5 .. n - 1 do
                    do! store.DeleteChunk scope (string i)

                let! retiredMeanMs = meanSearchMs ()

                Expect.isLessThan
                    retiredMeanMs
                    ceilingMs
                    $"HNSW mean query latency {retiredMeanMs:F1} ms over a {n}-vector graph with a fifth of its nodes retired was >100x the {flatMeanMs:F2} ms flat-scan baseline — the retired-node filter has degenerated"
            finally
                dispose.Dispose()
        }

        // Phase 970 — the recall bound at representative scale, OPT-IN.
        // `TOOLUP_HNSW_SCALE_CORPUS` names an extracted BEIR-format dataset
        // directory (`corpus.jsonl` + `queries.jsonl`, e.g. the `scifact`
        // directory the benchmark's BEIR cache holds). Unset, the case is
        // skipped, so the default suite is unchanged in duration: the graph
        // build alone costs tens of milliseconds per chunk at the shipped
        // defaults. `TOOLUP_HNSW_SCALE_MAX_CHUNKS` caps the corpus (default
        // 3000) and `TOOLUP_HNSW_SCALE_MAX_QUERIES` the queries (default 200).
        //
        // What it pins: at the SHIPPED `HnswParameters.defaults`, HNSW's
        // recall@10 against an exact flat scan over the same local-embedder
        // vectors is at least 0.98 — Phase 14k's "within 2 %". Recall is the
        // ann-benchmarks definition (a result counts when its cosine reaches
        // the K-th exact cosine, less 1e-6), so duplicate documents tying at
        // the boundary are not misses. Latency is reported, not asserted:
        // the p95 bound is NOT met at the default, and the guide states the
        // measured trade-off rather than a bound this test would fudge.
        testAsync "opt-in scale check: recall@10 within 2% of a flat scan on a named corpus (TOOLUP_HNSW_SCALE_CORPUS)" {
            match Environment.GetEnvironmentVariable "TOOLUP_HNSW_SCALE_CORPUS" with
            | null
            | "" -> skiptest "TOOLUP_HNSW_SCALE_CORPUS is unset — the representative-scale check is opt-in"
            | dir ->
                let intVar name fallback =
                    match Int32.TryParse(Environment.GetEnvironmentVariable name) with
                    | true, n when n > 0 -> n
                    | _ -> fallback

                let maxChunks = intVar "TOOLUP_HNSW_SCALE_MAX_CHUNKS" 3000
                let maxQueries = intVar "TOOLUP_HNSW_SCALE_MAX_QUERIES" 200

                let str (e: Text.Json.JsonElement) (name: string) =
                    match e.TryGetProperty name with
                    | true, v when v.ValueKind = Text.Json.JsonValueKind.String -> v.GetString()
                    | _ -> ""

                let readJsonl (file: string) (limit: int) (text: Text.Json.JsonElement -> string) =
                    File.ReadLines(Path.Combine(dir, file))
                    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                    |> Seq.truncate limit
                    |> Seq.map (fun line ->
                        use doc = Text.Json.JsonDocument.Parse line
                        str doc.RootElement "_id", text doc.RootElement)
                    |> Seq.toArray

                // The benchmark's indexing convention: title, blank line, text.
                let corpusDocs =
                    readJsonl "corpus.jsonl" maxChunks (fun e ->
                        let title = str e "title"
                        let text = str e "text"

                        if String.IsNullOrWhiteSpace title then
                            text
                        else
                            title + "\n\n" + text)

                let queryDocs = readJsonl "queries.jsonl" maxQueries (fun e -> str e "text")

                // Corpus first, then queries, as the benchmark embeds them.
                let embedder = LocalEmbeddingProvider.create ()
                let! corpusVecs = embedder.GenerateEmbeddings(corpusDocs |> Array.map snd)
                let! queryVecs = embedder.GenerateEmbeddings(queryDocs |> Array.map snd)

                let unit (v: float32[]) =
                    let norm = sqrt (Array.sumBy (fun x -> float x * float x) v)

                    if norm = 0.0 then
                        v
                    else
                        v |> Array.map (fun x -> float32 (float x / norm))

                let corpus = Array.map2 (fun (id, _) v -> id, unit v) corpusDocs corpusVecs
                let store, dispose = newStore ()

                try
                    let scope = Team "scale"

                    for (id, v) in corpus do
                        do! store.Upsert scope id v (chunk id)

                    let latencies = ResizeArray<float>()
                    let recalls = ResizeArray<float>()

                    for qv in queryVecs do
                        let q = unit qv

                        let topScores =
                            corpus
                            |> Array.map (fun (_, v) -> dot q v)
                            |> Array.sortDescending
                            |> Array.truncate 10

                        let sw = Stopwatch.StartNew()
                        let! hits = store.Search [ scope ] q 10
                        sw.Stop()
                        latencies.Add sw.Elapsed.TotalMilliseconds

                        if topScores.Length > 0 then
                            let threshold = float topScores[topScores.Length - 1] - 1e-6
                            let found = hits |> List.filter (fun h -> h.Score >= threshold) |> List.length
                            recalls.Add(float (min found topScores.Length) / float topScores.Length)

                    // The first search paid the lazy graph build; it is not a query latency.
                    let steady = latencies |> Seq.skip 1 |> Seq.sort |> Seq.toArray

                    let p95 =
                        if steady.Length = 0 then
                            0.0
                        else
                            steady[max 0 (int (ceil (0.95 * float steady.Length)) - 1)]

                    let recall = Seq.average recalls

                    printfn
                        "[hnsw-scale] %s: %d chunks, %d queries, recall@10 %.4f, p95 %.1f ms (efSearch %d, M %d)"
                        dir
                        corpus.Length
                        queryVecs.Length
                        recall
                        p95
                        HnswVectorStore.HnswParameters.defaults.EfSearch
                        HnswVectorStore.HnswParameters.defaults.M

                    Expect.isGreaterThanOrEqual
                        recall
                        0.98
                        $"HNSW recall@10 {recall:F4} against a flat scan of {corpus.Length} chunks is more than 2 percent below it at the shipped defaults"
                finally
                    dispose.Dispose()
        }
    ]
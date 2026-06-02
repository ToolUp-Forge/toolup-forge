// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Support.MixedDimFixture

open System
open System.IO
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 14k WS5 — mixed-dim fixture loader ──────────────────────
//
// Reads the metadata-only JSON fixture at `fixtures/eval-mixed-dim.json`
// (co-located with `eval-baseline.json` per the Phase 14k WS5 unblock
// criterion) and deterministically expands it to a 500-chunk corpus
// + anchor-perturbed query set. The fixture declares the corpus shape;
// the loader is the canonical generator so the test side (this module)
// and the benchmark side (Runner/MixedDimFixture.fs in the Benchmarks
// project) see byte-identical data.
//
// Algorithm — pinned across both loaders:
//   • One `System.Random(seed)` is drawn for the entire fixture.
//   • Corpus order: text chunks first (textCount × textDim), then
//     image chunks (imageCount × imageDim). Each vector is uniform
//     random in [-0.5, 0.5)^dim, then unit-normalised.
//   • Chunk ids: "text-000" .. "text-{textCount-1}" zero-padded to
//     three digits, then "image-000" .. similar.
//   • Queries: per kind, queryCountPerKind anchors at corpus indices
//     0, stride, 2*stride, ... Each query vector = anchor vector +
//     (queryNoiseScale * gaussian noise), renormalised. The anchor
//     chunkId is the query's single relevant-chunk id.

[<CLIMutable>]
type FixtureMetadata = {
    name: string
    description: string
    seed: int
    scope: string
    textDim: int
    imageDim: int
    textCount: int
    imageCount: int
    queryCountPerKind: int
    queryNoiseScale: float
    queryAnchorStride: int
}

type ChunkKind =
    | Text
    | Image

type Chunk = {
    ChunkId: string
    Kind: ChunkKind
    Dim: int
    Vector: float32[]
}

type Query = {
    QueryId: string
    Kind: ChunkKind
    Dim: int
    Vector: float32[]
    AnchorChunkId: string
}

type Fixture = {
    Metadata: FixtureMetadata
    Corpus: Chunk[]
    Queries: Query[]
}

let private normalise (v: float[]) : float32[] =
    let norm = sqrt (Array.sumBy (fun x -> x * x) v)
    v |> Array.map (fun x -> float32 (x / (norm + 1e-12)))

let private randomVec (rng: Random) (dim: int) : float32[] =
    Array.init dim (fun _ -> rng.NextDouble() - 0.5) |> normalise

let private gaussian (rng: Random) : float =
    // Box-Muller transform — two uniform samples → one standard normal.
    // Deterministic when `rng` is seeded; matches the benchmark loader.
    let u1 = max 1e-12 (rng.NextDouble())
    let u2 = rng.NextDouble()
    sqrt (-2.0 * log u1) * cos (2.0 * Math.PI * u2)

let private perturb (rng: Random) (noiseScale: float) (anchor: float32[]) : float32[] =
    let perturbed = anchor |> Array.map (fun x -> float x + noiseScale * gaussian rng)

    normalise perturbed

let private idText i = sprintf "text-%03d" i
let private idImage i = sprintf "image-%03d" i

let private chunkIdAt kind i =
    match kind with
    | Text -> idText i
    | Image -> idImage i

let load (path: string) : Fixture =
    if not (File.Exists path) then
        failwithf "Mixed-dim fixture not found at %s" path

    let json = File.ReadAllText path
    let meta = JsonSerializer.Deserialize<FixtureMetadata>(json, FableConverters.shared)
    let rng = Random meta.seed

    let textCorpus =
        Array.init meta.textCount (fun i -> {
            ChunkId = idText i
            Kind = Text
            Dim = meta.textDim
            Vector = randomVec rng meta.textDim
        })

    let imageCorpus =
        Array.init meta.imageCount (fun i -> {
            ChunkId = idImage i
            Kind = Image
            Dim = meta.imageDim
            Vector = randomVec rng meta.imageDim
        })

    let corpus = Array.append textCorpus imageCorpus

    let mkQueries (kind: ChunkKind) (sourceCorpus: Chunk[]) =
        Array.init meta.queryCountPerKind (fun qi ->
            let anchorIdx = (qi * meta.queryAnchorStride) % sourceCorpus.Length
            let anchor = sourceCorpus[anchorIdx]

            let kindTag =
                match kind with
                | Text -> "t"
                | Image -> "i"

            {
                QueryId = sprintf "q%s-%02d" kindTag qi
                Kind = kind
                Dim = anchor.Dim
                Vector = perturb rng meta.queryNoiseScale anchor.Vector
                AnchorChunkId = chunkIdAt kind anchorIdx
            })

    let textQueries = mkQueries Text textCorpus
    let imageQueries = mkQueries Image imageCorpus
    let queries = Array.append textQueries imageQueries

    {
        Metadata = meta
        Corpus = corpus
        Queries = queries
    }

/// Resolve the on-disk fixture path. Prefer the copy that MSBuild
/// drops next to the test runner (`AppContext.BaseDirectory/fixtures/`);
/// fall back to walking up to the source tree so this also works under
/// `dotnet fsi` / debugger invocations that don't run the copy step.
let defaultPath () : string =
    let runner =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "eval-mixed-dim.json")

    if File.Exists runner then
        runner
    else
        // src/ToolUp.Platform.Tests/bin/Debug/net10.0  →  src/
        let srcRoot =
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."))

        Path.Combine(srcRoot, "ToolUp.RAG.Evaluation", "fixtures", "eval-mixed-dim.json")

/// Exact top-K chunk ids from a same-dim subset, computed by brute-force
/// cosine. The HNSW vector store is also free to surface mismatched-dim
/// chunks (in practice they're filtered by `cosineDistance` returning
/// 1.0 — see `HnswVectorStore.cosineDistance`); the flat baseline this
/// function returns restricts to the matching dim by construction.
let exactTopK (corpus: Chunk[]) (queryKind: ChunkKind) (queryVec: float32[]) (k: int) : Set<string> =
    let sameDim =
        corpus |> Array.filter (fun c -> c.Kind = queryKind && c.Dim = queryVec.Length)

    sameDim
    |> Array.map (fun c ->
        let mutable s = 0.0f

        for i in 0 .. queryVec.Length - 1 do
            s <- s + queryVec[i] * c.Vector[i]

        c.ChunkId, s)
    |> Array.sortByDescending snd
    |> Array.truncate k
    |> Array.map fst
    |> Set.ofArray
/// Inlined subset of `ToolUp.RAG.Benchmarks.BeirLoader` (Phase 14q) reused
/// by the Phase 122 ablation gate. Living here rather than via project
/// reference for the same reason `Benchmarks/EvalCore.fs` inlines the eval
/// metrics in the other direction: both projects import
/// `ToolUp.Platform.Server.props`, so each compiles its own copy of the
/// injected server sources and a cross-reference would surface two CLR
/// types with the same name. Symbols mirror the upstream definitions —
/// keep them in sync if `BeirLoader` changes shape.
///
/// **Why the subset is derived at run time, not committed.** The BEIR
/// corpora carry their own licences (SciFact is CC BY-NC) — they are not
/// redistributable inside this Apache-2.0 repo. Phase 14q's loader
/// downloads from the public BEIR mirror into a local cache for exactly
/// that reason, and the ablation gate inherits the posture: download (or
/// reuse the cache), then derive a deterministic CI-sized subset
/// in-process.
module ToolUp.RAG.Evaluation.BeirSubset

open System
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Text.Json.Nodes
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.Evaluation.EvalTypes

// ─── Cache + download (mirrors `BeirLoader.cacheRoot` / `download`) ──

/// Same env override as the benchmark runner so one shared cache serves
/// both consumers (`TOOLUP_BEIR_CACHE`); falls back to a per-project
/// `bin/.../data/beir/` cache near the executable.
let cacheRoot =
    let envOverride =
        Environment.GetEnvironmentVariable ToolUp.Platform.ConfigKeys.Names.beirCache

    if not (String.IsNullOrWhiteSpace envOverride) then
        envOverride
    else
        Path.Combine(AppContext.BaseDirectory, "data", "beir")

let private datasetUrl (name: string) =
    sprintf "https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/%s.zip" name

let download (name: string) : Async<string> = async {
    let extractedDir = Path.Combine(cacheRoot, name)

    if
        Directory.Exists extractedDir
        && File.Exists(Path.Combine(extractedDir, "corpus.jsonl"))
    then
        return extractedDir
    else
        Directory.CreateDirectory cacheRoot |> ignore
        let zipPath = Path.Combine(cacheRoot, sprintf "%s.zip" name)

        if not (File.Exists zipPath) then
            printfn "[ablation] Downloading %s..." (datasetUrl name)
            use client = new HttpClient(Timeout = TimeSpan.FromMinutes 5.0)
            let! response = client.GetAsync(datasetUrl name) |> Async.AwaitTask
            response.EnsureSuccessStatusCode() |> ignore
            let! bytes = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
            File.WriteAllBytes(zipPath, bytes)

        ZipFile.ExtractToDirectory(zipPath, cacheRoot, overwriteFiles = true)
        return extractedDir
}

// ─── JSONL / TSV parsers (mirror `BeirLoader.parse*`) ────────────

type private BeirDoc = {
    Id: string
    Title: string
    Text: string
}

let private stringFrom (o: JsonObject) (key: string) : string =
    match o[key] with
    | null -> ""
    | n -> n.GetValue<string>()

let private parseJsonl (path: string) (build: JsonObject -> 'T) : 'T list =
    let items = ResizeArray()
    use reader = new StreamReader(path)
    let mutable line = reader.ReadLine()

    while not (isNull line) do
        if line.Trim() <> "" then
            items.Add(build (JsonNode.Parse line :?> JsonObject))

        line <- reader.ReadLine()

    items |> List.ofSeq

let private parseQrelsTsv (path: string) : (string * string * int) list =
    let qrels = ResizeArray()
    use reader = new StreamReader(path)
    let mutable isFirst = true
    let mutable line = reader.ReadLine()

    while not (isNull line) do
        if isFirst then
            isFirst <- false
        elif line.Trim() <> "" then
            let parts = line.Split('\t')

            if parts.Length >= 3 then
                match Int32.TryParse parts[2] with
                | true, score -> qrels.Add(parts[0], parts[1], score)
                | _ -> ()

        line <- reader.ReadLine()

    qrels |> List.ofSeq

// ─── Deterministic subset derivation ─────────────────────────────

/// How the CI-sized subset is carved out of the full dataset. Every choice
/// below is deterministic — sorted ids, fixed counts — so two runs (and two
/// machines) derive the byte-identical fixture from the same BEIR drop.
type SubsetSpec = {
    /// Queries to evaluate (taken in sorted-id order from the labelled set).
    QueryCount: int
    /// Total corpus size including every relevant doc; topped up with
    /// distractors in sorted-id order.
    CorpusSize: int
    /// Number of distractor docs to duplicate under a `-dup` chunk id.
    /// Exact-content duplicates have identical tf / doc-length, so they
    /// produce genuine BM25 score ties — exercising the sparse index's
    /// tie-break under the determinism assertion. (They do NOT tie in the
    /// vector store: the local embedder is an incremental TF-IDF, so the
    /// second occurrence embeds against different df state — vector-store
    /// tie-break coverage is the store-level contract test's job, per the
    /// TIDY-UP "RAG retrieval + ingestion edge guards" bundle.) Duplicated
    /// docs are never relevant for any selected query, so quality metrics
    /// are unaffected.
    DuplicateCount: int
}

let defaultSpec = {
    QueryCount = 24
    CorpusSize = 150
    DuplicateCount = 5
}

/// Download (or reuse the cached copy of) `datasetName` and derive the
/// deterministic ablation fixture per `spec`. Content is indexed under
/// `VectorScope.Deployment` (BEIR has no team semantics), matching the
/// benchmark runner's convention.
let loadFixture (datasetName: string) (spec: SubsetSpec) : Async<Fixture> = async {
    let! dir = download datasetName

    let docs =
        parseJsonl (Path.Combine(dir, "corpus.jsonl")) (fun o -> {
            Id = stringFrom o "_id"
            Title = stringFrom o "title"
            Text = stringFrom o "text"
        })

    let queries =
        parseJsonl (Path.Combine(dir, "queries.jsonl")) (fun o -> stringFrom o "_id", stringFrom o "text")

    let qrels = parseQrelsTsv (Path.Combine(dir, "qrels", "test.tsv"))

    let docsById = docs |> List.map (fun d -> d.Id, d) |> Map.ofList

    // Relevant doc ids per query (BEIR convention: binarise at score >= 1),
    // restricted to docs that actually exist in the corpus.
    let relevantByQuery =
        qrels
        |> List.filter (fun (_, docId, score) -> score >= 1 && docsById.ContainsKey docId)
        |> List.groupBy (fun (queryId, _, _) -> queryId)
        |> List.map (fun (queryId, rs) -> queryId, rs |> List.map (fun (_, docId, _) -> docId) |> Set.ofList)
        |> Map.ofList

    let queryTextById = queries |> Map.ofList

    // First `QueryCount` labelled queries in sorted-id order.
    let selectedQueries =
        relevantByQuery
        |> Map.toList
        |> List.filter (fun (queryId, relevant) -> queryTextById.ContainsKey queryId && not relevant.IsEmpty)
        |> List.sortBy fst
        |> List.truncate spec.QueryCount

    let relevantDocIds =
        selectedQueries |> List.collect (snd >> Set.toList) |> Set.ofList

    // Top up with distractors in sorted-id order until `CorpusSize`.
    let distractors =
        docs
        |> List.filter (fun d -> not (relevantDocIds.Contains d.Id))
        |> List.sortBy _.Id
        |> List.truncate (max 0 (spec.CorpusSize - relevantDocIds.Count))

    let selectedDocs =
        (docs |> List.filter (fun d -> relevantDocIds.Contains d.Id)) @ distractors
        |> List.sortBy _.Id

    let toEntry (suffix: string) (d: BeirDoc) : CorpusEntry = {
        ChunkId = d.Id + suffix
        Content =
            if String.IsNullOrWhiteSpace d.Title then
                d.Text
            else
                sprintf "%s\n\n%s" d.Title d.Text
        Scope = Deployment
        Metadata = Map.empty
    }

    // Exact-content duplicates of the first distractors (sorted-id order)
    // under distinct chunk ids — guaranteed score ties at every stage.
    let duplicates =
        distractors |> List.truncate spec.DuplicateCount |> List.map (toEntry "-dup")

    let corpus = (selectedDocs |> List.map (toEntry "")) @ duplicates

    let labelled =
        selectedQueries
        |> List.map (fun (queryId, relevant) -> {
            Id = queryId
            Query = queryTextById[queryId]
            Scopes = [ Deployment ]
            RelevantChunkIds = relevant
            // Phase 502.E — BEIR queries carry no metadata scope; the
            // ablation gate measures pipeline composition, not filtering.
            Filters = None
        })

    return {
        Name = sprintf "beir-%s-ablation-subset" datasetName
        Description =
            sprintf
                "Deterministic %d-doc / %d-query subset of BEIR %s (+%d duplicate distractors as the BM25 tie net) — Phase 122 ablation gate"
                corpus.Length
                labelled.Length
                datasetName
                duplicates.Length
        Corpus = corpus
        Queries = labelled
    }
}
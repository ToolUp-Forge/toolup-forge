module ToolUp.RAG.Benchmarks.BeirLoader

open System
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Text.Json.Nodes
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.Benchmarks.EvalCore
open ToolUp.RAG.Benchmarks.BeirTypes

// ─── Cache layout ────────────────────────────────────────────────

/// Where downloaded BEIR datasets live. Per-process cwd is wrong (vanishes on
/// reboot); the project's `bin/Debug/net10.0/data/beir/` keeps the cache near
/// the executable so `dotnet clean` doesn't wipe it. Operator can override
/// via env var if they want to share a cache across multiple checkouts.
let cacheRoot =
    let envOverride = Environment.GetEnvironmentVariable "TOOLUP_BEIR_CACHE"

    if not (String.IsNullOrWhiteSpace envOverride) then
        envOverride
    else
        Path.Combine(AppContext.BaseDirectory, "data", "beir")

/// BEIR's canonical mirror at TU Darmstadt. URLs are stable; the public
/// distribution has been live since the 2021 paper. If the mirror moves,
/// this is the one place to update.
let private datasetUrl (name: string) =
    sprintf "https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/%s.zip" name

// ─── Download + unpack ───────────────────────────────────────────

/// Download `{name}.zip` if not already cached and extract it. Returns the
/// directory containing `corpus.jsonl`, `queries.jsonl`, and `qrels/test.tsv`.
/// Idempotent — second call with the same `name` is a directory-exists check
/// + return.
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
            printfn "[beir] Downloading %s..." (datasetUrl name)
            use client = new HttpClient(Timeout = TimeSpan.FromMinutes 5.0)
            let! response = client.GetAsync(datasetUrl name) |> Async.AwaitTask
            response.EnsureSuccessStatusCode() |> ignore
            let! bytes = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
            File.WriteAllBytes(zipPath, bytes)
            printfn "[beir] Downloaded %d bytes to %s" bytes.Length zipPath

        printfn "[beir] Extracting %s..." zipPath
        ZipFile.ExtractToDirectory(zipPath, cacheRoot, overwriteFiles = true)
        // BEIR archives extract to `{name}/`; if the layout differs (some
        // datasets nest under an extra dir) the loader below will surface
        // the missing-file error clearly.
        return extractedDir
}

// ─── JSONL streaming parsers ─────────────────────────────────────

/// Stringify a JsonNode property, returning "" when absent or null. The
/// JsonValue.GetValue<string>() form is the canonical STJ path; .ToString()
/// on a JsonValue holding a string returns the string unquoted, matching
/// the prior Newtonsoft behaviour.
let private stringFrom (o: JsonObject) (key: string) : string =
    match o.[key] with
    | null -> ""
    | n -> n.GetValue<string>()

/// Parse `corpus.jsonl` — one `{"_id": "...", "title": "...", "text": "..."}`
/// object per line. Streams line-by-line so memory peak is one line rather
/// than file size. FiQA's corpus is ~80 MB; loading whole-file via
/// `JsonSerializer.Deserialize<BeirCorpusDoc list>` would blow up.
let parseCorpusJsonl (path: string) : BeirCorpusDoc list =
    let docs = ResizeArray()
    use reader = new StreamReader(path)

    let mutable line = reader.ReadLine()

    while not (isNull line) do
        if line.Trim() <> "" then
            let o = JsonNode.Parse line :?> JsonObject

            docs.Add {
                Id = stringFrom o "_id"
                Title = stringFrom o "title"
                Text = stringFrom o "text"
            }

        line <- reader.ReadLine()

    docs |> List.ofSeq

/// Parse `queries.jsonl` — same shape as corpus minus `title`.
let parseQueriesJsonl (path: string) : BeirQuery list =
    let queries = ResizeArray()
    use reader = new StreamReader(path)

    let mutable line = reader.ReadLine()

    while not (isNull line) do
        if line.Trim() <> "" then
            let o = JsonNode.Parse line :?> JsonObject

            queries.Add {
                Id = stringFrom o "_id"
                Text = stringFrom o "text"
            }

        line <- reader.ReadLine()

    queries |> List.ofSeq

/// Parse `qrels/test.tsv` — three tab-separated columns: query-id, corpus-id,
/// relevance. Skips the header line. Lines with non-integer scores are
/// dropped (defensive — BEIR qrels are clean but downstream regenerators
/// sometimes aren't).
let parseQrelsTsv (path: string) : BeirQrel list =
    let qrels = ResizeArray()
    use reader = new StreamReader(path)
    let mutable isFirst = true

    let mutable line = reader.ReadLine()

    while not (isNull line) do
        if isFirst then
            // Header: "query-id\tcorpus-id\tscore" — skip.
            isFirst <- false
        elif line.Trim() <> "" then
            let parts = line.Split('\t')

            if parts.Length >= 3 then
                match Int32.TryParse parts[2] with
                | true, score ->
                    qrels.Add {
                        QueryId = parts[0]
                        CorpusId = parts[1]
                        Score = score
                    }
                | _ -> ()

        line <- reader.ReadLine()

    qrels |> List.ofSeq

// ─── Top-level loader ────────────────────────────────────────────

/// Download (if needed) and parse a BEIR dataset by name. The qrels file is
/// expected at `qrels/test.tsv` — the standard BEIR test split. Some datasets
/// also ship train/dev splits; those are out of scope for this v1.
let load (name: string) : Async<BeirDataset> = async {
    let! dir = download name

    let corpusPath = Path.Combine(dir, "corpus.jsonl")
    let queriesPath = Path.Combine(dir, "queries.jsonl")
    let qrelsPath = Path.Combine(dir, "qrels", "test.tsv")

    if not (File.Exists corpusPath) then
        failwithf "BEIR loader: corpus.jsonl not found at %s" corpusPath

    if not (File.Exists queriesPath) then
        failwithf "BEIR loader: queries.jsonl not found at %s" queriesPath

    if not (File.Exists qrelsPath) then
        failwithf "BEIR loader: qrels/test.tsv not found at %s" qrelsPath

    let corpus = parseCorpusJsonl corpusPath
    let queries = parseQueriesJsonl queriesPath
    let qrels = parseQrelsTsv qrelsPath

    // Filter the queries down to those that have at least one labelled qrel
    // — BEIR ships full query sets but qrels often cover a subset (the test
    // split). Scoring against unlabeled queries is meaningless: every result
    // would count as a miss.
    let labelledQueryIds = qrels |> List.map _.QueryId |> Set.ofList

    let labelledQueries =
        queries |> List.filter (fun q -> labelledQueryIds.Contains q.Id)

    return {
        Name = name
        Corpus = corpus
        Queries = labelledQueries
        Qrels = qrels
    }
}

// ─── Replicate-N synth ───────────────────────────────────────────

/// Replicate the corpus N times with perturbed IDs (`{origId}-rep0`, …,
/// `{origId}-rep{N-1}`). Queries are unchanged; **qrels are emptied** so
/// quality metrics return 0 — replicate-mode runs are exclusively for
/// stressing the vector store at scale.
///
/// Combined with `CachingEmbeddingProvider`, replicates of identical content
/// hit the cache: one underlying embedder call yields N upserts. This is the
/// trick that lets us benchmark a 570k-chunk index without paying for 570k
/// OpenAI calls.
let replicate (n: int) (ds: BeirDataset) : BeirDataset =
    if n <= 1 then
        ds
    else
        let replicated = [
            for k in 0 .. n - 1 do
                for doc in ds.Corpus do
                    {
                        doc with
                            Id = sprintf "%s-rep%d" doc.Id k
                    }
        ]

        {
            ds with
                Corpus = replicated
                Qrels = []
        }

// ─── Adapter into the eval project's Fixture shape ──────────────

/// Adapt a `BeirDataset` to the existing `ToolUp.RAG.Evaluation.Fixture`
/// record. Concatenates `Title + "\n\n" + Text` for each chunk's content
/// (BEIR convention: title is a meaningful retrieval signal for SciFact /
/// NFCorpus / etc.), scopes everything to `VectorScope.Deployment`, and
/// builds each query's `RelevantChunkIds` set from the qrels at score >= 1.
let toFixture (ds: BeirDataset) : Fixture =
    let qrelsByQuery =
        ds.Qrels
        |> List.filter (fun q -> q.Score >= 1)
        |> List.groupBy _.QueryId
        |> Map.ofList

    let corpus =
        ds.Corpus
        |> List.map (fun doc ->
            let content =
                if String.IsNullOrWhiteSpace doc.Title then
                    doc.Text
                else
                    sprintf "%s\n\n%s" doc.Title doc.Text

            {
                ChunkId = doc.Id
                Content = content
                Scope = Deployment
                Metadata = Map.empty
            })

    let queries =
        ds.Queries
        |> List.map (fun q ->
            let relevant =
                qrelsByQuery
                |> Map.tryFind q.Id
                |> Option.map (fun rs -> rs |> List.map _.CorpusId |> Set.ofList)
                |> Option.defaultValue Set.empty

            {
                Id = q.Id
                Query = q.Text
                Scopes = [ Deployment ]
                RelevantChunkIds = relevant
            })

    {
        Name = ds.Name
        Description = sprintf "BEIR dataset (%d docs, %d queries)" corpus.Length queries.Length
        Corpus = corpus
        Queries = queries
    }
module ToolUp.RAG.Evaluation.FixtureLoader

open System.IO
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.Evaluation.EvalTypes

// JSON-on-disk shape. The FableConverters STJ converter set handles
// CLIMutable records natively; the `VectorScope` DU is stored as a flat
// string form (`"platform"`, `"deployment"`, `"team:<id>"`) parsed
// explicitly below so fixtures stay readable alongside `EventStoreRetrievalTracer`
// payloads without going through a DU converter.

let private jsonOptions = FableConverters.create ()

[<CLIMutable>]
type JsonCorpusEntry = {
    chunkId: string
    content: string
    scope: string
    metadata: System.Collections.Generic.Dictionary<string, string>
}

[<CLIMutable>]
type JsonQuery = {
    id: string
    query: string
    scopes: string array
    relevantChunkIds: string array
    /// Phase 502.E — optional per-query metadata filter. Absent in every
    /// fixture written before 502.E, and `[<CLIMutable>]` + STJ leave an
    /// absent object as `null`, which `toLabelledQuery` folds to `None` —
    /// the same defensive shape `metadata` on a corpus entry already used.
    /// So an existing fixture loads unchanged, with no schema version and
    /// no migration.
    filters: System.Collections.Generic.Dictionary<string, string>
}

[<CLIMutable>]
type JsonFixture = {
    name: string
    description: string
    corpus: JsonCorpusEntry array
    queries: JsonQuery array
}

let private parseScope (raw: string) : VectorScope =
    let trimmed = raw.Trim().ToLowerInvariant()

    if trimmed = "platform" then
        Platform
    elif trimmed = "deployment" then
        Deployment
    elif trimmed.StartsWith "team:" then
        Team(trimmed.Substring(5))
    elif trimmed.StartsWith "user:" then
        User(trimmed.Substring(5))
    else
        failwithf "Unknown scope: '%s'. Expected 'platform', 'deployment', 'team:<id>', or 'user:<id>'." raw

let private toCorpusEntry (j: JsonCorpusEntry) : CorpusEntry = {
    ChunkId = j.chunkId
    Content = j.content
    Scope = parseScope j.scope
    Metadata =
        if isNull j.metadata then
            Map.empty
        else
            j.metadata |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
}

let private toLabelledQuery (j: JsonQuery) : LabelledQuery = {
    Id = j.id
    Query = j.query
    Scopes = j.scopes |> Array.map parseScope |> Array.toList
    RelevantChunkIds = Set.ofArray j.relevantChunkIds
    // An absent `filters` key, and an explicitly-empty one, both mean "no
    // filter" — `Some Map.empty` would only differ from `None` by putting a
    // vacuous value on the wire.
    Filters =
        if isNull j.filters || j.filters.Count = 0 then
            None
        else
            Some(j.filters |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)
}

let load (path: string) : Fixture =
    let json = File.ReadAllText path
    let parsed = JsonSerializer.Deserialize<JsonFixture>(json, jsonOptions)

    let corpus = if isNull (box parsed.corpus) then [||] else parsed.corpus
    let queries = if isNull (box parsed.queries) then [||] else parsed.queries

    {
        Name = parsed.name
        Description = parsed.description
        Corpus = corpus |> Array.map toCorpusEntry |> Array.toList
        Queries = queries |> Array.map toLabelledQuery |> Array.toList
    }
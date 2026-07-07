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
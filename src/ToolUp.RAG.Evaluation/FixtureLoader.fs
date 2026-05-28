module ToolUp.RAG.Evaluation.FixtureLoader

open System.IO
open Newtonsoft.Json
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.Evaluation.EvalTypes

// JSON-on-disk shape. Newtonsoft cannot round-trip the `VectorScope` DU
// without a converter, so fixtures use a flat string form (`"platform"`,
// `"deployment"`, `"team:<id>"`) and we parse / unparse explicitly. Same
// convention as `EventStoreRetrievalTracer`'s payload — reading a fixture
// alongside a retrieval trace stays unambiguous.

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
    else
        failwithf "Unknown scope: '%s'. Expected 'platform', 'deployment', or 'team:<id>'." raw

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
    let parsed = JsonConvert.DeserializeObject<JsonFixture>(json)

    let corpus = if isNull (box parsed.corpus) then [||] else parsed.corpus
    let queries = if isNull (box parsed.queries) then [||] else parsed.queries

    {
        Name = parsed.name
        Description = parsed.description
        Corpus = corpus |> Array.map toCorpusEntry |> Array.toList
        Queries = queries |> Array.map toLabelledQuery |> Array.toList
    }
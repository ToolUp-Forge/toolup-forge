module ToolUp.RAG.InMemoryBM25Index

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.RegularExpressions
open System.Threading
open Newtonsoft.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.ISparseIndex

// ─── Tokenisation ─────────────────────────────────────────────────

let private tokenPattern = Regex(@"[\p{L}\p{N}]+", RegexOptions.Compiled)

/// Lower-case and split into Unicode-letter / digit runs. Punctuation,
/// whitespace, and symbols are dropped. Sufficient for English-language
/// business documents and code-like identifiers (`SKU-1234` becomes
/// `["sku"; "1234"]`); language-aware tokenisers belong in companion
/// implementations under `src/SparseIndices/`.
let private tokenise (text: string) : string list =
    if String.IsNullOrEmpty text then
        []
    else
        [
            for m in tokenPattern.Matches(text) do
                m.Value.ToLowerInvariant()
        ]

// ─── BM25 parameters ──────────────────────────────────────────────

/// Standard BM25 constants. `K1` controls term-frequency saturation;
/// `B` controls length normalisation. Lucene defaults — robust across
/// short notes and long PDF pages without retuning.
let private K1 = 1.2f
let private B = 0.75f

// ─── Serialisable index entry ─────────────────────────────────────

[<CLIMutable>]
type private DocEntry = {
    ChunkId: string
    Length: int
    Tokens: string array
    Content: string
    Metadata: Map<string, string>
}

[<CLIMutable>]
type private ScopeSnapshot = { Docs: DocEntry array }

let private scopeToKey (scope: VectorScope) =
    match scope with
    | Platform -> "platform"
    | Deployment -> "deployment"
    | Team teamId -> $"team:{teamId}"

let private blobName (scope: VectorScope) = $"_rag/{scopeToKey scope}/bm25.json"

let private jsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(Fable.Remoting.Json.FableJsonConverter())
    s

let private toJson o =
    JsonConvert.SerializeObject(o, jsonSettings)

let private fromJson<'T> (s: string) =
    JsonConvert.DeserializeObject<'T>(s, jsonSettings)

// ─── Per-scope index state ────────────────────────────────────────

/// A scope's lexical index. Each scope owns its own state so cross-scope
/// queries fan out and union without ever sharing posting lists. Mutated
/// only under `lockObj`; read paths take the lock briefly to snapshot
/// what they need.
type private ScopeIndex() =
    let lockObj = obj ()

    // chunkId → (tokens, length, content, metadata)
    let docs = Dictionary<string, string array * int * string * Map<string, string>>()

    // term → (chunkId → term frequency)
    let postings = Dictionary<string, Dictionary<string, int>>()

    let mutable totalLength = 0L

    let removeFromPostings chunkId =
        match docs.TryGetValue chunkId with
        | false, _ -> 0
        | true, (tokens, length, _, _) ->
            for term in tokens do
                match postings.TryGetValue term with
                | true, entry ->
                    if entry.Remove chunkId && entry.Count = 0 then
                        postings.Remove term |> ignore
                | false, _ -> ()

            length

    member _.Upsert(chunkId: string, content: string, metadata: Map<string, string>) =
        lock lockObj (fun () ->
            let removedLength = removeFromPostings chunkId

            let tokens = tokenise content |> List.toArray
            let length = tokens.Length

            // Per-document term frequencies — counted once for the entire
            // document so a `Search` over many query terms sees a single
            // hash-table lookup per (term, chunk) pair.
            let tfPerTerm = Dictionary<string, int>()

            for term in tokens do
                match tfPerTerm.TryGetValue term with
                | true, n -> tfPerTerm[term] <- n + 1
                | false, _ -> tfPerTerm[term] <- 1

            for KeyValue(term, tf) in tfPerTerm do
                let entry =
                    match postings.TryGetValue term with
                    | true, e -> e
                    | false, _ ->
                        let e = Dictionary<string, int>()
                        postings[term] <- e
                        e

                entry[chunkId] <- tf

            docs[chunkId] <- (tokens, length, content, metadata)
            totalLength <- totalLength - int64 removedLength + int64 length)

    member _.Delete(chunkId: string) =
        lock lockObj (fun () ->
            let removedLength = removeFromPostings chunkId
            docs.Remove chunkId |> ignore
            totalLength <- totalLength - int64 removedLength)

    member _.IsEmpty = lock lockObj (fun () -> docs.Count = 0)

    member _.DocCount = lock lockObj (fun () -> docs.Count)

    /// Score every document containing at least one query term using BM25.
    /// Returns `(chunkId, score, content, metadata)` tuples.
    member _.Score(queryTerms: string list) : (string * float * string * Map<string, string>) list =
        lock lockObj (fun () ->
            let n = docs.Count

            if n = 0 || queryTerms.IsEmpty then
                []
            else
                let avgDl = float32 totalLength / float32 n

                let scores = Dictionary<string, float32>()

                for term in queryTerms |> List.distinct do
                    match postings.TryGetValue term with
                    | false, _ -> ()
                    | true, postingEntry ->
                        let df = postingEntry.Count
                        // Lucene-style BM25 IDF: ln(1 + (N − df + 0.5) / (df + 0.5)).
                        // Always positive for df ≤ N, avoiding the negative-IDF
                        // pathology in classic BM25 when a term appears in a
                        // majority of documents.
                        let idf = log (1.0f + (float32 (n - df) + 0.5f) / (float32 df + 0.5f))

                        for KeyValue(chunkId, tf) in postingEntry do
                            match docs.TryGetValue chunkId with
                            | false, _ -> ()
                            | true, (_, length, _, _) ->
                                let dl = float32 length
                                let tfFloat = float32 tf
                                let norm = 1.0f - B + B * (dl / avgDl)
                                let contribution = idf * (tfFloat * (K1 + 1.0f)) / (tfFloat + K1 * norm)

                                match scores.TryGetValue chunkId with
                                | true, existing -> scores[chunkId] <- existing + contribution
                                | false, _ -> scores[chunkId] <- contribution

                [
                    for KeyValue(chunkId, score) in scores do
                        match docs.TryGetValue chunkId with
                        | true, (_, _, content, metadata) -> yield (chunkId, float score, content, metadata)
                        | false, _ -> ()
                ])

    member _.Snapshot() : DocEntry array =
        lock lockObj (fun () -> [|
            for KeyValue(chunkId, (tokens, length, content, metadata)) in docs do
                {
                    ChunkId = chunkId
                    Length = length
                    Tokens = tokens
                    Content = content
                    Metadata = metadata
                }
        |])

    member _.LoadSnapshot(snapshot: ScopeSnapshot) =
        lock lockObj (fun () ->
            docs.Clear()
            postings.Clear()
            totalLength <- 0L

            if not (isNull (box snapshot)) && not (isNull snapshot.Docs) then
                for entry in snapshot.Docs do
                    let tokens = if isNull entry.Tokens then [||] else entry.Tokens
                    let length = if entry.Length > 0 then entry.Length else tokens.Length

                    let tfPerTerm = Dictionary<string, int>()

                    for term in tokens do
                        match tfPerTerm.TryGetValue term with
                        | true, n -> tfPerTerm[term] <- n + 1
                        | false, _ -> tfPerTerm[term] <- 1

                    for KeyValue(term, tf) in tfPerTerm do
                        let postingEntry =
                            match postings.TryGetValue term with
                            | true, e -> e
                            | false, _ ->
                                let e = Dictionary<string, int>()
                                postings[term] <- e
                                e

                        postingEntry[entry.ChunkId] <- tf

                    let metadata =
                        if isNull (box entry.Metadata) then
                            Map.empty
                        else
                            entry.Metadata

                    docs[entry.ChunkId] <- (tokens, length, entry.Content, metadata)
                    totalLength <- totalLength + int64 length)

// ─── In-memory sparse index ───────────────────────────────────────

/// In-memory BM25 sparse index backed by `IBlobStorage` for persistence.
/// Sibling to `InMemoryVectorStore`: each scope has its own inverted index
/// serialised to `_rag/{scopeKey}/bm25.json`.
///
/// Persistence is debounced — `Upsert`, `DeleteChunk`, and `DeleteByScope`
/// mark the affected scope dirty and return immediately; a background loop
/// flushes the dirty set at a bounded cadence. This matches
/// `InMemoryVectorStore`'s amortisation strategy so a 10,000-chunk bulk
/// load triggers O(scopes) persistence passes, not O(chunks).
///
/// Tokenisation is intentionally simple (Unicode word runs, lowercased).
/// Stemming, stop-word removal, and language-specific analysers belong in
/// companion implementations under `src/SparseIndices/` — the
/// `ISparseIndex` interface is Phase 9c-clean and accepts richer backends
/// (Tantivy, Lucene.NET, Meilisearch) without changing call sites.
///
/// Suitable for deployments with up to ~50,000 chunks; for larger corpora
/// switch to a companion. The store implements `IDisposable`; ASP.NET
/// Core's DI container disposes it during shutdown, which performs one
/// final synchronous flush so no acknowledged writes are lost.
type InMemoryBM25Index(storage: IBlobStorage, ?logger: ILogger, ?flushIntervalMs: int) =

    let log =
        logger
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    let flushMs = defaultArg flushIntervalMs 2000

    let scopes = ConcurrentDictionary<string, ScopeIndex>()

    let dirty = ConcurrentDictionary<string, VectorScope>()

    let cts = new CancellationTokenSource()

    let getOrCreateScopeIndex (scope: VectorScope) =
        scopes.GetOrAdd(scopeToKey scope, fun _ -> ScopeIndex())

    let markDirty (scope: VectorScope) = dirty[scopeToKey scope] <- scope

    let persistScope (scope: VectorScope) = async {
        let scopeKey = scopeToKey scope

        match scopes.TryGetValue scopeKey with
        | false, _ -> ()
        | true, idx ->
            let snapshot = { Docs = idx.Snapshot() }
            let bytes = toJson snapshot |> System.Text.Encoding.UTF8.GetBytes
            let! result = storage.Upload("_rag", blobName scope, bytes)

            match result with
            | Ok _ -> ()
            | Error e ->
                log.Warn $"[InMemoryBM25Index] Failed to persist scope {scopeKey}: {e} — will retry on next flush."
                markDirty scope
    }

    let flushAll () = async {
        let captured = ResizeArray()

        for kvp in dirty do
            match dirty.TryRemove(kvp.Key) with
            | true, scope -> captured.Add scope
            | _ -> ()

        for scope in captured do
            try
                do! persistScope scope
            with ex ->
                log.Error($"[InMemoryBM25Index] Flush failed for {scopeToKey scope}", Some ex)
                markDirty scope
    }

    let loadScope (scope: VectorScope) = async {
        match! storage.Download("_rag", blobName scope) with
        | Ok bytes ->
            try
                let json = System.Text.Encoding.UTF8.GetString bytes
                let snapshot = fromJson<ScopeSnapshot> json
                let idx = getOrCreateScopeIndex scope
                idx.LoadSnapshot snapshot
            with ex ->
                log.Warn $"[InMemoryBM25Index] Corrupt index for {scopeToKey scope}: {ex.Message} — starting empty."
        | Error _ -> ()
    }

    do
        loadScope Platform |> Async.RunSynchronously
        loadScope Deployment |> Async.RunSynchronously

    let flushLoop = async {
        while not cts.IsCancellationRequested do
            try
                do! Async.Sleep flushMs

                if not cts.IsCancellationRequested then
                    do! flushAll ()
            with
            | :? OperationCanceledException -> ()
            | ex -> log.Error("[InMemoryBM25Index] Flush loop error", Some ex)
    }

    do Async.Start(flushLoop, cts.Token)

    interface ISparseIndex with

        member _.Upsert scope chunkId chunk = async {
            let idx = getOrCreateScopeIndex scope
            idx.Upsert(chunkId, chunk.Content, chunk.Metadata)
            markDirty scope
        }

        member _.Search scopes' query topK = async {
            // Lazy-load Team scopes on first access. Platform/Deployment are
            // already loaded at construction.
            for scope in scopes' do
                match scope with
                | Team _ ->
                    let scopeKey = scopeToKey scope

                    if not (scopes.ContainsKey scopeKey) then
                        do! loadScope scope
                | _ -> ()

            let queryTerms = tokenise query

            if queryTerms.IsEmpty then
                return []
            else
                let results =
                    scopes'
                    |> List.collect (fun scope ->
                        match scopes.TryGetValue(scopeToKey scope) with
                        | false, _ -> []
                        | true, idx ->
                            idx.Score queryTerms
                            |> List.map (fun (chunkId, score, content, metadata) -> {
                                ChunkId = chunkId
                                Content = content
                                Score = score
                                Scope = scope
                                Metadata = metadata
                            }))
                    // Total-order sort: `ScopeIndex.Score` yields from a
                    // Dictionary in unspecified order and equal BM25 scores
                    // are common (short fixture docs). A score-only sort
                    // would freeze that non-deterministic order. Tie-break
                    // on the unique `(Scope, ChunkId)`.
                    |> List.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)
                    |> List.truncate topK

                return results
        }

        member _.DeleteByScope scope = async {
            scopes.TryRemove(scopeToKey scope) |> ignore
            markDirty scope
        }

        member _.DeleteChunk scope chunkId = async {
            match scopes.TryGetValue(scopeToKey scope) with
            | true, idx ->
                idx.Delete chunkId
                markDirty scope
            | false, _ -> ()
        }

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()

            try
                flushAll () |> Async.RunSynchronously
            with ex ->
                log.Error("[InMemoryBM25Index] Final flush on dispose failed", Some ex)

            cts.Dispose()
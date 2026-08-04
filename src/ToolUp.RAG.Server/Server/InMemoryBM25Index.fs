module ToolUp.RAG.InMemoryBM25Index

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.ISparseIndex
open ToolUp.RAG.SparseAnalysis

// ─── Tokenisation ─────────────────────────────────────────────────
//
// Phase 501 — tokenisation moved behind `ISparseAnalyzer`
// (`ToolUp.RAG.SparseAnalysis`). The pre-501 rule (Unicode letter/digit runs,
// lower-cased) is now `SparseAnalysis.identity` and remains the default, so an
// index constructed without an analyzer behaves byte-for-byte as before
// (GP 11). Language-aware analyzers ship as companions under
// `src/SparseIndices/`.

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

/// Phase 501 — `AnalyzerId` records which analyzer produced the persisted
/// `DocEntry.Tokens`. A snapshot written by a different analyzer than the one
/// now composed is re-analysed from `DocEntry.Content` on load rather than
/// trusted: index-time terms that disagree with query-time terms do not fail,
/// they just stop matching, and a deployment that turns stemming on would
/// otherwise search a stemmed vocabulary against unstemmed postings until
/// every document happened to be re-ingested.
///
/// A pre-501 snapshot has no such field, so `AnalyzerId` deserialises `null`;
/// that is read as `SparseAnalysis.IdentityAnalyzerId`, because the identity
/// analyzer IS what wrote it. With the default analyzer composed, such a
/// snapshot therefore loads its tokens unchanged (GP 11).
[<CLIMutable>]
type private ScopeSnapshot = {
    Docs: DocEntry array
    AnalyzerId: string
}

let private scopeToKey (scope: VectorScope) =
    match scope with
    | Platform -> "platform"
    | Deployment -> "deployment"
    | Team teamId -> $"team:{teamId}"
    | User userId -> $"user:{userId}"

let private blobName (scope: VectorScope) = $"_rag/{scopeToKey scope}/bm25.json"

let private jsonOptions = FableConverters.create ()

let private toJson o =
    JsonSerializer.Serialize(o, jsonOptions)

let private fromJson<'T> (s: string) =
    JsonSerializer.Deserialize<'T>(s, jsonOptions)

// ─── Per-scope index state ────────────────────────────────────────

/// A scope's lexical index. Each scope owns its own state so cross-scope
/// queries fan out and union without ever sharing posting lists. Mutated
/// only under `lockObj`; read paths take the lock briefly to snapshot
/// what they need.
///
/// Phase 501 — both term-bearing entry points (`Upsert`, `Score`) take an
/// `AnalysedText`, never a `string list`. `AnalysedText` has a private
/// constructor in `ToolUp.RAG.SparseAnalysis`, so the only way to reach either
/// is through an `ISparseAnalyzer`; `analyzerId` is the id this scope's
/// postings were built with, and terms from any other analyzer are refused
/// rather than scored against them.
type private ScopeIndex(analyzerId: string) =
    let lockObj = obj ()

    let expect (label: string) (analysed: AnalysedText) =
        if analysed.AnalyzerId <> analyzerId then
            // Unreachable through the public surface — the owning index binds
            // exactly one analyzer and analyses both sides with it. Kept as a
            // fail-loud floor because the alternative failure mode (scoring
            // one analyzer's query terms against another's postings) is
            // silent, and reads as "retrieval got worse" months later.
            invalidOp
                $"[InMemoryBM25Index] %s{label} received terms from analyzer '%s{analysed.AnalyzerId}' but this index was built by '%s{analyzerId}'. Index-time and query-time analysis must use the same analyzer."

        analysed.Terms

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

    member _.Upsert(chunkId: string, analysed: AnalysedText, content: string, metadata: Map<string, string>) =
        lock lockObj (fun () ->
            let removedLength = removeFromPostings chunkId

            let tokens = expect "Upsert" analysed |> List.toArray
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
    member _.Score(analysedQuery: AnalysedText) : (string * float * string * Map<string, string>) list =
        let queryTerms = expect "Score" analysedQuery

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

    /// Rehydrate from a persisted snapshot. `reanalyse` is the owning index's
    /// single analysis path; it is invoked per entry only when the snapshot's
    /// recorded analyzer differs from this index's, in which case the stored
    /// tokens are stale by construction and the entry's retained `Content` is
    /// re-analysed instead. Returns the number of entries re-analysed so the
    /// caller can say so once, rather than per chunk.
    member _.LoadSnapshot(snapshot: ScopeSnapshot, reanalyse: string -> AnalysedText) : int =
        lock lockObj (fun () ->
            docs.Clear()
            postings.Clear()
            totalLength <- 0L

            let mutable reanalysed = 0

            // A pre-Phase-501 snapshot has no AnalyzerId field, so STJ leaves
            // it null — and the identity analyzer is exactly what wrote it.
            let snapshotAnalyzerId =
                if isNull (box snapshot) || String.IsNullOrEmpty snapshot.AnalyzerId then
                    IdentityAnalyzerId
                else
                    snapshot.AnalyzerId

            let tokensMatch = snapshotAnalyzerId = analyzerId

            if not (isNull (box snapshot)) && not (isNull snapshot.Docs) then
                for entry in snapshot.Docs do
                    let stored = if isNull entry.Tokens then [||] else entry.Tokens

                    let tokens =
                        if tokensMatch then
                            stored
                        else
                            reanalysed <- reanalysed + 1
                            expect "LoadSnapshot" (reanalyse entry.Content) |> List.toArray

                    let length =
                        if tokensMatch && entry.Length > 0 then
                            entry.Length
                        else
                            tokens.Length

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
                    totalLength <- totalLength + int64 length

            reanalysed)

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
/// Tokenisation is pluggable as of Phase 501. The default analyzer
/// (`SparseAnalysis.identity`) is the pre-501 rule — Unicode word runs,
/// lower-cased — so an index constructed without one behaves exactly as
/// before (GP 11). Stemming, stop-word removal and CJK segmentation arrive by
/// composing an `ISparseAnalyzer` from a companion under `src/SparseIndices/`
/// (`ToolUp.SparseIndices.Snowball`, `ToolUp.SparseIndices.Cjk`); the
/// `ISparseIndex` interface itself is Phase 9c-clean and accepts richer
/// backends (Tantivy, Lucene.NET, Meilisearch) without changing call sites.
///
/// The analyzer is applied on BOTH sides by construction: this type binds one
/// analyzer, exposes a single `analyse` path, and the per-scope index accepts
/// only the `AnalysedText` that path produces. The persisted snapshot records
/// the analyzer id and is re-analysed on load when it disagrees, so composing
/// an analyzer over an existing corpus does not leave the postings in one
/// vocabulary and the queries in another.
///
/// Suitable for deployments with up to ~50,000 chunks; for larger corpora
/// switch to a companion. The store implements `IDisposable`; ASP.NET
/// Core's DI container disposes it during shutdown, which performs one
/// final synchronous flush so no acknowledged writes are lost.
type InMemoryBM25Index(storage: IBlobStorage, ?logger: ILogger, ?flushIntervalMs: int, ?analyzer: ISparseAnalyzer) =

    let log =
        logger
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    let flushMs = defaultArg flushIntervalMs 2000

    /// The one analyzer this index owns. Every term it stores and every term
    /// it searches for comes from `analyse` below — there is no second path.
    let analyzer = defaultArg analyzer SparseAnalysis.identity

    let analyse (text: string) = SparseAnalysis.analyse analyzer text

    let scopes = ConcurrentDictionary<string, ScopeIndex>()

    let dirty = ConcurrentDictionary<string, VectorScope>()

    let cts = new CancellationTokenSource()

    let getOrCreateScopeIndex (scope: VectorScope) =
        scopes.GetOrAdd(scopeToKey scope, fun _ -> ScopeIndex(analyzer.Id))

    let markDirty (scope: VectorScope) = dirty[scopeToKey scope] <- scope

    let persistScope (scope: VectorScope) = async {
        let scopeKey = scopeToKey scope

        match scopes.TryGetValue scopeKey with
        | false, _ ->
            // Phase 115 — a dirty scope that is absent from the in-memory
            // map was removed by `DeleteByScope` (dirty-marking is the
            // only way to reach here). Pre-115 this short-circuited,
            // leaving the stale `bm25.json` at rest — the deleted corpus
            // (full chunk text included) resurrected on the next process
            // restart. Delete the persisted snapshot so the scope-level
            // delete survives restart, mirroring what the vector store
            // achieves by persisting an empty list.
            let! result = storage.Delete("_rag", blobName scope)

            match result with
            | Ok _ -> ()
            | Error e ->
                log.Warn
                    $"[InMemoryBM25Index] Failed to delete persisted snapshot for removed scope {scopeKey}: {e} — will retry on next flush."

                markDirty scope
        | true, idx ->
            let snapshot = {
                Docs = idx.Snapshot()
                AnalyzerId = analyzer.Id
            }

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
                let reanalysed = idx.LoadSnapshot(snapshot, analyse)

                if reanalysed > 0 then
                    // Said once per scope, at Info: this is the expected
                    // (and correct) response to composing a new analyzer over
                    // an existing corpus, not a fault.
                    log.Info
                        $"[InMemoryBM25Index] Scope {scopeToKey scope}: re-analysed {reanalysed} chunk(s) — the persisted snapshot was written by a different analyzer than the composed '{analyzer.Id}'."

                    // The re-analysed postings only exist in memory until the
                    // next flush; mark dirty so the snapshot on disk catches
                    // up rather than re-triggering this on every restart.
                    markDirty scope
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

    /// Id of the composed analyzer — `SparseAnalysis.IdentityAnalyzerId` when
    /// none was supplied. Surfaced so an operator (or a test) can confirm from
    /// the outside which vocabulary the postings are in; it is the same value
    /// stamped into every persisted snapshot.
    member _.AnalyzerId = analyzer.Id

    interface ISparseIndex with

        member _.Upsert scope chunkId chunk = async {
            let idx = getOrCreateScopeIndex scope
            idx.Upsert(chunkId, analyse chunk.Content, chunk.Content, chunk.Metadata)
            markDirty scope
        }

        member _.Search scopes' query topK = async {
            // Lazy-load Team and User scopes on first access. Platform/
            // Deployment are already loaded at construction.
            for scope in scopes' do
                match scope with
                | Team _
                | User _ ->
                    let scopeKey = scopeToKey scope

                    if not (scopes.ContainsKey scopeKey) then
                        do! loadScope scope
                | _ -> ()

            // The SAME `analyse` the index-time path uses — one binding, one
            // analyzer, no way to reach the postings with anything else.
            let analysedQuery = analyse query

            if analysedQuery.Terms.IsEmpty then
                return []
            else
                let results =
                    scopes'
                    |> List.collect (fun scope ->
                        match scopes.TryGetValue(scopeToKey scope) with
                        | false, _ -> []
                        | true, idx ->
                            idx.Score analysedQuery
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

        member _.Erase(scope, subjectUserId, policy, dryRun) = async {
            // Phase 115 — same matching contract as
            // `IVectorStore.eraseSubject`: the subject id appears in the
            // chunk content or in any metadata value. BM25 keeps the full
            // chunk text + metadata per entry, so the match is exact, not
            // approximate. No tombstone tier exists here — every policy
            // collapses to a hard delete (noted in the summary); the
            // persisted `bm25.json` follows on the next flush via
            // `markDirty`, so the erased text also leaves blob storage.
            if Erasure.isBlankSubject subjectUserId then
                return
                    Result.Ok {
                        HandlerName = "sparse-index"
                        RecordsAffected = 0
                        Note = Some "blank subject — no-op"
                    }
            else
                // Team and User scopes are lazy-loaded on first access (same
                // as `Search`) — erasure must see the persisted corpus even
                // when this process has never searched the scope.
                match scope with
                | Team _
                | User _ ->
                    if not (scopes.ContainsKey(scopeToKey scope)) then
                        do! loadScope scope
                | _ -> ()

                match scopes.TryGetValue(scopeToKey scope) with
                | false, _ ->
                    return
                        Result.Ok {
                            HandlerName = "sparse-index"
                            RecordsAffected = 0
                            Note = Some "scope empty — nothing to erase"
                        }
                | true, idx ->
                    let matched =
                        idx.Snapshot()
                        |> Array.filter (fun entry ->
                            entry.Content.Contains subjectUserId
                            || entry.Metadata |> Map.exists (fun _ v -> v.Contains subjectUserId))
                        |> Array.map _.ChunkId

                    if dryRun || Array.isEmpty matched then
                        return
                            Result.Ok {
                                HandlerName = "sparse-index"
                                RecordsAffected = matched.Length
                                Note = Some(sprintf "%d chunk(s) would be erased in scope" matched.Length)
                            }
                    else
                        for chunkId in matched do
                            idx.Delete chunkId

                        markDirty scope

                        // `policy` intentionally unused beyond documentation:
                        // a lexical index holds no soft-delete tier, so
                        // SoftDelete and HardDelete are the same operation.
                        ignore policy

                        return
                            Result.Ok {
                                HandlerName = "sparse-index"
                                RecordsAffected = matched.Length
                                Note =
                                    Some(
                                        sprintf
                                            "%d chunk(s) hard-deleted (lexical index has no tombstone tier)"
                                            matched.Length
                                    )
                            }
        }

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()

            try
                flushAll () |> Async.RunSynchronously
            with ex ->
                log.Error("[InMemoryBM25Index] Final flush on dispose failed", Some ex)

            cts.Dispose()
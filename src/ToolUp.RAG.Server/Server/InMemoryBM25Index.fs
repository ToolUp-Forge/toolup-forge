module ToolUp.RAG.InMemoryBM25Index

open System
open System.Collections.Generic
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra.ISparseIndex
open ToolUp.RAG.SparseAnalysis
open ToolUp.Platform.Infra.IRagTelemetry
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose

// ─── Tokenisation ─────────────────────────────────────────────────
//
// Tokenisation is behind `ISparseAnalyzer` (`ToolUp.RAG.SparseAnalysis`). The
// default, `SparseAnalysis.identity`, is Unicode letter/digit runs,
// lower-cased. Language-aware analyzers ship as companions under
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

/// `AnalyzerId` records which analyzer produced the persisted
/// `DocEntry.Tokens`. A snapshot written by a different analyzer than the one
/// now composed is re-analysed from `DocEntry.Content` on load rather than
/// trusted: index-time terms that disagree with query-time terms do not fail,
/// they just stop matching, and a deployment that turns stemming on would
/// otherwise search a stemmed vocabulary against unstemmed postings until
/// every document happened to be re-ingested.
///
/// A snapshot written before analyzers were pluggable has no such field, so `AnalyzerId` deserialises `null`;
/// that is read as `SparseAnalysis.IdentityAnalyzerId`, because the identity
/// analyzer IS what wrote it. With the default analyzer composed, such a
/// snapshot therefore loads its tokens unchanged (GP 11).
[<CLIMutable>]
type private ScopeSnapshot = {
    Docs: DocEntry array
    AnalyzerId: string
}

// ─── Per-scope index state ────────────────────────────────────────

/// A scope's lexical index. Each scope owns its own state so cross-scope
/// queries fan out and union without ever sharing posting lists. The owning
/// `ScopedSnapshotIndex` serialises writers and lets readers run together, so
/// the type holds no lock of its own.
///
/// Both term-bearing entry points (`Upsert`, `Score`) take an
/// `AnalysedText`, never a `string list`. `AnalysedText` has a private
/// constructor in `ToolUp.RAG.SparseAnalysis`, so the only way to reach either
/// is through an `ISparseAnalyzer`; `analyzerId` is the id this scope's
/// postings were built with, and terms from any other analyzer are refused
/// rather than scored against them.
type private ScopeIndex(analyzerId: string) =
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
        (let removedLength = removeFromPostings chunkId

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
        (let removedLength = removeFromPostings chunkId
         docs.Remove chunkId |> ignore
         totalLength <- totalLength - int64 removedLength)

    /// Drop every document.
    member _.Clear() =
        docs.Clear()
        postings.Clear()
        totalLength <- 0L

    member _.IsEmpty = (docs.Count = 0)

    member _.DocCount = (docs.Count)

    /// Score every document containing at least one query term using BM25.
    /// Returns `(chunkId, score, content, metadata)` tuples.
    member _.Score(analysedQuery: AnalysedText) : (string * float * string * Map<string, string>) list =
        let queryTerms = expect "Score" analysedQuery

        (let n = docs.Count

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
        ([|
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
        (docs.Clear()
         postings.Clear()
         totalLength <- 0L

         let mutable reanalysed = 0

         // A snapshot written before analyzers were pluggable has no AnalyzerId field, so STJ leaves
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

let private layout = SnapshotLayout.rag "bm25.json"

/// In-memory BM25 sparse index backed by `IBlobStorage` for persistence, over
/// the shared `ScopedSnapshotIndex` (the same hydration, flush and corruption
/// law as `InMemoryVectorStore`): each scope has its own inverted index,
/// serialised to `_rag/{scopeKey}/bm25.json`. A scope holding no document has
/// no snapshot at rest: its blob is deleted at the next flush, so a scope-level
/// delete survives a restart.
///
/// Persistence is debounced — a mutation marks the scope dirty and returns;
/// the index flushes the dirty set every `flushIntervalMs` (two seconds by
/// default), so a 10,000-chunk bulk load costs O(scopes) persistence passes,
/// not O(chunks).
///
/// Tokenisation is pluggable. The default analyzer (`SparseAnalysis.identity`)
/// is Unicode word runs, lower-cased. Stemming, stop-word removal and CJK
/// segmentation arrive by composing an `ISparseAnalyzer` from a companion under
/// `src/SparseIndices/` (`ToolUp.SparseIndices.Snowball`,
/// `ToolUp.SparseIndices.Cjk`).
///
/// The analyzer is applied on BOTH sides by construction: this type binds one
/// analyzer, exposes a single `analyse` path, and the per-scope index accepts
/// only the `AnalysedText` that path produces. The persisted snapshot records
/// the analyzer id and is re-analysed on load when it disagrees, so composing
/// an analyzer over an existing corpus does not leave the postings in one
/// vocabulary and the queries in another.
///
/// A corrupt snapshot follows `corruptPolicy` (resolved from
/// `TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION` when not supplied) and is reported
/// through `telemetry`, `auditLog` and `notifications`, once per scope.
///
/// Suitable for deployments with up to ~50,000 chunks; for larger corpora
/// switch to a companion. The store implements `IDisposable`: disposing it
/// performs one final flush so no acknowledged write is lost.
type InMemoryBM25Index
    (
        storage: IBlobStorage,
        ?logger: ILogger,
        ?flushIntervalMs: int,
        ?analyzer: ISparseAnalyzer,
        ?telemetry: IRagTelemetry,
        ?auditLog: IAuditLog,
        ?notifications: INotificationChannel,
        ?corruptPolicy: CorruptPolicy
    ) =

    let log =
        logger
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    /// The one analyzer this index owns. Every term it stores and every term
    /// it searches for comes from `analyse` below — there is no second path.
    let analyzer = defaultArg analyzer SparseAnalysis.identity

    let analyse (text: string) = SparseAnalysis.analyse analyzer text

    /// The snapshot-analyzer mismatch is re-analysed here, on the way in; the
    /// re-analysed postings exist only in memory until the next flush, so the
    /// scope is reported stale and written back rather than re-analysed on
    /// every restart.
    let materialise (scope: VectorScope) (snapshot: ScopeSnapshot) =
        let idx = ScopeIndex(analyzer.Id)
        let reanalysed = idx.LoadSnapshot(snapshot, analyse)

        if reanalysed > 0 then
            // Said once per scope, at Info: the expected (and correct)
            // response to composing a new analyzer over an existing corpus.
            log.Info
                $"[InMemoryBM25Index] Scope {VectorScope.key scope}: re-analysed {reanalysed} chunk(s) — the persisted snapshot was written by a different analyzer than the composed '{analyzer.Id}'."

        idx, reanalysed > 0

    /// A scope that holds no document persists no rows: its blob is deleted.
    let snapshot (_: VectorScope) (idx: ScopeIndex) =
        if idx.IsEmpty then
            None
        else
            Some {
                Docs = idx.Snapshot()
                AnalyzerId = analyzer.Id
            }

    let index =
        new ScopedSnapshotIndex<ScopeSnapshot, ScopeIndex>(
            storage
            , layout
            , BlobCodec.json<ScopeSnapshot>
            , (fun _ -> ScopeIndex(analyzer.Id))
            , materialise
            , snapshot
            , SnapshotPolicy.create {
                Policy =
                    corruptPolicy
                    |> Option.defaultWith (fun () -> CorruptPolicy.resolveWith ConfigResolution.tryValue)
                Telemetry = telemetry
                AuditLog = defaultArg auditLog (NoOpAuditLog() :> IAuditLog)
                Notifications = notifications
              }
              |> fun policy -> {
                  policy with
                      FlushIntervalMs = defaultArg flushIntervalMs 2000
              }
            , "InMemoryBM25Index"
            , log
        )

    /// Id of the composed analyzer — `SparseAnalysis.IdentityAnalyzerId` when
    /// none was supplied. Surfaced so an operator (or a test) can confirm from
    /// the outside which vocabulary the postings are in; it is the same value
    /// stamped into every persisted snapshot.
    member _.AnalyzerId = analyzer.Id

    /// Load the platform and deployment scopes now (see
    /// `ScopedSnapshotIndex.Start`).
    member _.Start() = index.Start()

    /// The postings are this process's in-memory maps, flushed
    /// to blob storage but searched from memory: each replica holds its own.
    interface ISparseIndexLocality with
        member _.IndexLocality =
            Some ToolUp.Platform.Infra.IVectorStore.VectorIndexLocality.InProcess

    interface ISparseIndex with

        member _.Upsert scope chunkId chunk =
            let analysed = analyse chunk.Content

            index.Mutate(
                scope,
                fun idx ->
                    idx.Upsert(chunkId, analysed, chunk.Content, chunk.Metadata)
                    (), true
            )

        member _.Search scopes query topK = async {
            for scope in scopes do
                do! index.EnsureLoadedForSearch scope

            // The SAME `analyse` the index-time path uses — one binding, one
            // analyzer, no way to reach the postings with anything else.
            let analysedQuery = analyse query

            if analysedQuery.Terms.IsEmpty then
                return []
            else
                return
                    scopes
                    |> List.collect (fun scope ->
                        index.Read(scope, (fun idx -> idx.Score analysedQuery))
                        |> Option.defaultValue []
                        |> List.map (fun (chunkId, score, content, metadata) -> {
                            ChunkId = chunkId
                            Content = content
                            Score = score
                            Scope = scope
                            Metadata = metadata
                        }))
                    // Total-order sort: `ScopeIndex.Score` yields from a
                    // Dictionary in unspecified order and equal BM25 scores
                    // are common (short fixture docs). Tie-break on the unique
                    // `(Scope, ChunkId)`.
                    |> List.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)
                    |> List.truncate topK
        }

        member _.DeleteByScope scope =
            index.Mutate(
                scope,
                fun idx ->
                    idx.Clear()
                    (), true
            )

        member _.DeleteChunk scope chunkId =
            index.Mutate(
                scope,
                fun idx ->
                    idx.Delete chunkId
                    (), true
            )

        member _.Erase(scope, subjectUserId, policy, dryRun) = async {
            // Same matching contract as `IVectorStore.eraseSubject`: the
            // subject id appears in the chunk content or in any metadata
            // value. BM25 keeps the full chunk text + metadata per entry, so
            // the match is exact, not approximate. No tombstone tier exists
            // here — every policy collapses to a hard delete (noted in the
            // summary); the persisted `bm25.json` follows at the next flush,
            // so the erased text also leaves blob storage.
            ignore policy

            let summary count note : ErasureSummary = {
                HandlerName = "sparse-index"
                RecordsAffected = count
                Note = Some note
            }

            if Erasure.isBlankSubject subjectUserId.Value then
                return Result.Ok(summary 0 "blank subject — no-op")
            else
                // A snapshot that exists but cannot be read is the
                // interface's own typed failure, which the orchestrator retries.
                let! erased =
                    index.Mutate(
                        scope,
                        fun idx ->
                            let matched =
                                idx.Snapshot()
                                |> Array.filter (fun entry ->
                                    entry.Content.Contains subjectUserId.Value
                                    || entry.Metadata |> Map.exists (fun _ v -> v.Contains subjectUserId.Value))
                                |> Array.map _.ChunkId

                            let empty = idx.IsEmpty
                            let erase = not dryRun && not (Array.isEmpty matched)

                            if erase then
                                for chunkId in matched do
                                    idx.Delete chunkId

                            (empty, matched.Length, erase), erase
                    )
                    |> Async.Catch

                match erased with
                | Choice2Of2(:? RagScopeSnapshotUnreadableException as ex) ->
                    return Result.Error(ErasureError.StoreUnreachable("sparse-index", ex.Message))
                | Choice2Of2 ex ->
                    Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
                    return Unchecked.defaultof<_>
                | Choice1Of2(true, _, _) -> return Result.Ok(summary 0 "scope empty — nothing to erase")
                | Choice1Of2(false, count, true) ->
                    return
                        Result.Ok(
                            summary
                                count
                                (sprintf "%d chunk(s) hard-deleted (lexical index has no tombstone tier)" count)
                        )
                | Choice1Of2(false, count, false) ->
                    return Result.Ok(summary count (sprintf "%d chunk(s) would be erased in scope" count))
        }

    interface IDisposable with
        member _.Dispose() = (index :> IDisposable).Dispose()
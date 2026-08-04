module ToolUp.RAG.Evaluation.Metrics

open ToolUp.RAG.Evaluation.EvalTypes

// ─── Recall@K ─────────────────────────────────────────────────────

/// Recall@K — fraction of queries for which at least one relevant chunk
/// appears in the top K results. The harness's default since it's the
/// most direct quality metric for downstream RAG: if the right chunk
/// isn't in top-K, the LLM cannot cite it.
///
/// Per-query is `1.0` (any relevant within K) or `0.0` (none). Aggregate
/// over the fixture is the mean.
let recallAt (k: int) (results: QueryResult list) : float =
    if results.IsEmpty then
        0.0
    else
        let hits =
            results
            |> List.sumBy (fun r ->
                if r.RelevantRanks |> List.exists (fun rank -> rank <= k) then
                    1.0
                else
                    0.0)

        hits / float results.Length

// ─── nDCG@K ───────────────────────────────────────────────────────

/// Discounted cumulative gain. With binary relevance (each relevant chunk
/// contributes 1, non-relevant 0), this reduces to summing `1 / log2(rank + 1)`
/// over relevant ranks within K. Normalised by the ideal ordering — all
/// available relevant matches packed at the top — so the metric lives in
/// `[0, 1]`.
///
/// nDCG rewards ranking quality, not just presence: a relevant chunk at
/// rank 1 scores higher than the same chunk at rank 5. Use Recall@K when
/// you only care that the LLM has access to the right chunk; use nDCG@K
/// when ranking quality matters (e.g. budget-constrained K).
let ndcgAt (k: int) (results: QueryResult list) : float =
    if results.IsEmpty then
        0.0
    else
        let perQuery (r: QueryResult) : float =
            let relevantInTop = r.RelevantRanks |> List.filter (fun rank -> rank <= k)

            if relevantInTop.IsEmpty then
                0.0
            else
                let dcg =
                    relevantInTop
                    |> List.sumBy (fun rank -> 1.0 / System.Math.Log2(float rank + 1.0))

                let totalRelevant = r.RelevantRanks.Length
                let idealK = min totalRelevant k

                let idealDcg =
                    [ 1..idealK ]
                    |> List.sumBy (fun rank -> 1.0 / System.Math.Log2(float rank + 1.0))

                if idealDcg = 0.0 then 0.0 else dcg / idealDcg

        results |> List.averageBy perQuery

// ─── Mean Reciprocal Rank ─────────────────────────────────────────

/// Mean reciprocal rank. For each query, take `1 / (rank of first relevant
/// match)`, or `0` if none was retrieved. Average across queries. MRR is
/// most useful for queries with a single canonical correct answer (factoid
/// lookups, exact-document retrieval); for multi-answer queries Recall and
/// nDCG capture more.
let mrr (results: QueryResult list) : float =
    if results.IsEmpty then
        0.0
    else
        let perQuery (r: QueryResult) : float =
            match r.RelevantRanks with
            | [] -> 0.0
            | ranks -> 1.0 / float (List.min ranks)

        results |> List.averageBy perQuery

// ─── Aggregation ──────────────────────────────────────────────────

let buildReport (fixtureName: string) (results: QueryResult list) : EvalReport = {
    FixtureName = fixtureName
    QueryCount = results.Length
    RecallAt1 = recallAt 1 results
    RecallAt5 = recallAt 5 results
    RecallAt10 = recallAt 10 results
    NdcgAt5 = ndcgAt 5 results
    NdcgAt10 = ndcgAt 10 results
    Mrr = mrr results
    AvgLatencyMs =
        if results.IsEmpty then
            0.0
        else
            results |> List.averageBy (fun r -> float r.LatencyMs)
    // Phase 502.E — count QUERIES that leaked, not leaked chunks: one
    // query returning five out-of-filter chunks is one broken query, and
    // scaling the number with corpus size would make the signal read as
    // worse on a bigger fixture for the same defect.
    FilterViolationCount = results |> List.sumBy (fun r -> if r.FilterViolations.IsEmpty then 0 else 1)
    PerQuery = results
}
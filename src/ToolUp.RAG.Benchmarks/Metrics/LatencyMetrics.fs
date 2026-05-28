module ToolUp.RAG.Benchmarks.LatencyMetrics

/// Latency profile derived from per-query wall-clock timings. p50 / p95 / p99
/// chosen as the standard percentile triple for retrieval-system benchmarks
/// (production dashboards typically alert on p95 or p99). `Avg` complements
/// the percentiles for sanity-checking; `Count` is the sample size so a
/// single-query run can't produce misleading "0 ms p99" rows.
type LatencyProfile = {
    P50: int64
    P95: int64
    P99: int64
    Avg: float
    Count: int
}

let empty: LatencyProfile = {
    P50 = 0L
    P95 = 0L
    P99 = 0L
    Avg = 0.0
    Count = 0
}

/// Compute percentile with the "nearest-rank" method: index at
/// `ceil(p × n) - 1` of the sorted sample. Standard for small samples
/// (BEIR test sets are 200–650 queries; nothing fancy needed).
let private percentile (p: float) (sorted: int64 array) =
    if sorted.Length = 0 then
        0L
    else
        let idx = max 0 (int (ceil (p * float sorted.Length)) - 1)
        sorted[min idx (sorted.Length - 1)]

/// Build a `LatencyProfile` from a list of per-query latencies in ms.
/// Empty input → `empty` (all zeros). Sorts in O(n log n); cheap for the
/// expected query counts.
let profile (samples: int64 list) : LatencyProfile =
    if samples.IsEmpty then
        empty
    else
        let sorted = samples |> List.toArray |> Array.sort

        {
            P50 = percentile 0.50 sorted
            P95 = percentile 0.95 sorted
            P99 = percentile 0.99 sorted
            Avg = (samples |> List.sumBy float) / float samples.Length
            Count = samples.Length
        }
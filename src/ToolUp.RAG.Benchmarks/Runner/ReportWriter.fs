module ToolUp.RAG.Benchmarks.ReportWriter

open System.Globalization
open System.IO
open ToolUp.RAG.Benchmarks.BenchmarkRunner

/// CSV header — column order kept stable so downstream tooling (spreadsheets,
/// `csvkit`, `pandas`) can match by name. Match `RunRow`'s field order.
let csvHeader =
    "dataset,embedder,vector_store,reranker,mmr,topK,replicate,queries_evaluated,ndcg_at_10,recall_at_100,mrr_at_10,ingest_seconds,query_p50_ms,query_p95_ms,query_p99_ms,wall_clock_seconds"

/// Format a float with `InvariantCulture` so the output is locale-independent
/// (some EU locales would emit `0,653` which breaks downstream CSV parsers).
let private f (value: float) =
    value.ToString("0.####", CultureInfo.InvariantCulture)

let private formatRow (row: RunRow) =
    sprintf
        "%s,%s,%s,%s,%s,%d,%d,%d,%s,%s,%s,%s,%d,%d,%d,%s"
        row.Dataset
        row.Embedder
        row.VectorStore
        row.Reranker
        row.Mmr
        row.TopK
        row.Replicate
        row.QueriesEvaluated
        (f row.NdcgAt10)
        (f row.RecallAt100)
        (f row.MrrAt10)
        (f row.IngestSeconds)
        row.QueryP50Ms
        row.QueryP95Ms
        row.QueryP99Ms
        (f row.WallClockSeconds)

/// Append `row` to `path`. Creates the file with the header if it doesn't
/// exist yet so successive `--out same.csv` invocations build a matrix.
let appendRow (path: string) (row: RunRow) : unit =
    let dir = Path.GetDirectoryName path

    if not (System.String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore

    let fileExists = File.Exists path

    use writer = new StreamWriter(path, append = true)

    if not fileExists then
        writer.WriteLine csvHeader

    writer.WriteLine(formatRow row)
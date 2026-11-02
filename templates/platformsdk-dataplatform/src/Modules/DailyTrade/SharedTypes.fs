module DailyTrade.SharedTypes

open ToolUp.Platform
open ProcessedDataTypes

/// One delivery of the daily trading extract, as the browser uploads it:
/// the file's name and its text (CSV, one row per site and channel per day).
type UploadRequest = { FileName: string; Csv: string }

/// One fact table the delivery refreshed, and how many rows it committed.
type TableWritten = { TableId: string; Rows: int }

/// What the delivery did. A rejected delivery writes nothing and names every
/// problem; an accepted one names the tables it refreshed and the outcome of
/// each grounded narrative it triggered.
type UploadResult = {
    Accepted: bool
    Problems: string list
    Tables: TableWritten list
    Narratives: string list
}

/// What `Upload` returns to the client: the result, plus the
/// `ProcessedFileEntry` the client keeps and publishes through
/// `ClientModule.withProcessedData` while this module fills the shell's
/// data-manager slot. The server builds the entry; the client only carries it.
type UploadResponse = {
    Result: UploadResult
    Processed: ProcessedFileEntry option
}

type DailyTradeApi = {
    /// A delivery writes facts into the caller's own scope, so it needs a
    /// signed-in, team-bound subject.
    [<TenantScoped>]
    Upload: UploadRequest -> Async<UploadResponse>
}
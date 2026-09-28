module MyDataManager.SharedTypes

open ProcessedDataTypes

type IngestRequest = { SourceUri: string; Format: string }

type IngestResult = { DatasetId: string; RowCount: int }

/// What `Ingest` returns to the client: the ingest summary, plus the
/// `ProcessedFileEntry` envelope the client keeps on its Model and
/// publishes through `ClientModule.withProcessedData` once this module
/// fills the data-manager shell slot — see docs/platform/modules.md,
/// "Replacing a built-in — shell slots", the data-manager slot's
/// contract. The server builds `Processed` (via `ProcessedDataCodec.encode`
/// — see Server.fs); the client only carries it, because the shared
/// tier deliberately carries no JSON codec of its own.
type IngestResponse = {
    Result: IngestResult
    Processed: ProcessedFileEntry
}

type MyDataManagerApi = {
    Ingest: IngestRequest -> Async<IngestResponse>
}
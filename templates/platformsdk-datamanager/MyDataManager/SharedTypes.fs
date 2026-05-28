module MyDataManager.SharedTypes

type IngestRequest = { SourceUri: string; Format: string }

type IngestResult = { DatasetId: string; RowCount: int }

type MyDataManagerApi = {
    Ingest: IngestRequest -> Async<IngestResult>
}
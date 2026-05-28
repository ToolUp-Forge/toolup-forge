module MyDataManager.Server

open ToolUp.Platform
open MyDataManager.SharedTypes

// IDataSource skeleton — implement Connect / FetchSchema / Pull against
// your upstream system. The composition root wires this into the
// platform via ExternalDataManager mode (see Client.fs registration).
type MyDataSource() =
    interface IDataSource with
        member _.Id = "my-data-manager"
        member _.DisplayName = "My Data Manager"

        member _.Probe() = async { return Ok() }

        member _.Pull(_: IDataSourcePullRequest) = async { return Ok [] }

let dataSource: IDataSource = MyDataSource()

let private ingest (request: IngestRequest) : Async<IngestResult> = async {
    return {
        DatasetId = sprintf "%s::%s" request.SourceUri request.Format
        RowCount = 0
    }
}

let routes: MyDataManagerApi = { Ingest = ingest }
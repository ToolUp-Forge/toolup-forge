module MyDataManager.ClientModel

open ToolUp.Elmish
open ToolUp.Platform
open ProcessedDataTypes
open MyDataManager.SharedTypes

type Model = {
    SelectedFile: string option
    LastIngest: IngestResult option
    /// Every file this module has ingested, kept so
    /// `ClientModule.withProcessedData` (ClientView.fs) has something to
    /// publish once this module fills the data-manager shell slot — see
    /// docs/platform/modules.md, "Replacing a built-in — shell slots",
    /// the data-manager slot's contract. Never reset to `[]` on a single
    /// ingest: a data module elsewhere in the shell may depend on an
    /// entry this module published earlier in the session.
    Processed: ProcessedFileEntry list
}

type Msg =
    | FileSelected of string
    | Ingest
    | IngestSucceeded of IngestResponse
    | IngestFailed of string

let init () : Model * Cmd<Msg> =
    {
        SelectedFile = None
        LastIngest = None
        Processed = []
    },
    Cmd.none

let private api: MyDataManagerApi =
    Api.makeProxy<MyDataManagerApi> (customOptions = UserSession.withRequestHeaders)

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | FileSelected uri -> { model with SelectedFile = Some uri }, Cmd.none
    | Ingest ->
        match model.SelectedFile with
        | None -> model, Cmd.none
        | Some uri ->
            let cmd =
                Cmd.OfRemoting.call api.Ingest { SourceUri = uri; Format = "csv" } IngestSucceeded (fun ex ->
                    IngestFailed ex.Message)

            model, cmd
    | IngestSucceeded response ->
        {
            model with
                LastIngest = Some response.Result
                Processed = model.Processed @ [ response.Processed ]
        },
        Cmd.none
    | IngestFailed _ -> model, Cmd.none
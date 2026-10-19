module DailyTrade.ClientModel

open ToolUp.Elmish
open ToolUp.Platform
open ProcessedDataTypes
open DailyTrade.SharedTypes

type Model = {
    /// The file the user picked, read as text, waiting to be uploaded.
    Pending: UploadRequest option
    Uploading: bool
    LastResult: UploadResult option
    Error: string option
    /// Every delivery this module has ingested, kept so
    /// `ClientModule.withProcessedData` (ClientView.fs) has something to
    /// publish while this module fills the data-manager shell slot. Never
    /// reset on a single upload: another module may depend on an entry this
    /// module published earlier in the session.
    Processed: ProcessedFileEntry list
}

type Msg =
    | FileRead of fileName: string * text: string
    | Upload
    | Uploaded of UploadResponse
    | UploadFailed of string

let init () : Model * Cmd<Msg> =
    {
        Pending = None
        Uploading = false
        LastResult = None
        Error = None
        Processed = []
    },
    Cmd.none

let private api: DailyTradeApi =
    Api.makeProxy<DailyTradeApi> (customOptions = UserSession.withRequestHeaders)

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | FileRead(fileName, text) ->
        {
            model with
                Pending = Some { FileName = fileName; Csv = text }
                Error = None
        },
        Cmd.none
    | Upload ->
        match model.Pending with
        | None -> model, Cmd.none
        | Some request ->
            {
                model with
                    Uploading = true
                    Error = None
            },
            Cmd.OfRemoting.call api.Upload request Uploaded (fun ex -> UploadFailed ex.Message)
    | Uploaded response ->
        {
            model with
                Uploading = false
                LastResult = Some response.Result
                Processed = model.Processed @ Option.toList response.Processed
        },
        Cmd.none
    | UploadFailed error ->
        {
            model with
                Uploading = false
                Error = Some error
        },
        Cmd.none
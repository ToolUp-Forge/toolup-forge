module DailyTrade.ClientModel

open Fuaran.MVU
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

let init () : Model * Step<Msg, RemoteEffect<Msg>> list =
    {
        Pending = None
        Uploading = false
        LastResult = None
        Error = None
        Processed = []
    },
    []

let private api: DailyTradeApi =
    Api.makeProxy<DailyTradeApi> (customOptions = UserSession.withRequestHeaders)

let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | FileRead(fileName, text) ->
        {
            model with
                Pending = Some { FileName = fileName; Csv = text }
                Error = None
        },
        []
    | Upload ->
        match model.Pending with
        | None -> model, []
        | Some request ->
            {
                model with
                    Uploading = true
                    Error = None
            },
            [
                Do(
                    RemoteEffect.call "DailyTradeApi.Upload" api.Upload request (function
                        | Ok response -> Uploaded response
                        | Error ex -> UploadFailed ex.Message)
                )
            ]
    | Uploaded response ->
        {
            model with
                Uploading = false
                LastResult = Some response.Result
                Processed = model.Processed @ Option.toList response.Processed
        },
        []
    | UploadFailed error ->
        {
            model with
                Uploading = false
                Error = Some error
        },
        []
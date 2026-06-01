module MyDataManager.ClientModel

open Elmish
open ToolUp.Platform
open MyDataManager.SharedTypes

type Model = {
    SelectedFile: string option
    LastIngest: IngestResult option
}

type Msg =
    | FileSelected of string
    | Ingest
    | IngestSucceeded of IngestResult
    | IngestFailed of string

let init () : Model * Cmd<Msg> =
    {
        SelectedFile = None
        LastIngest = None
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
    | IngestSucceeded result -> { model with LastIngest = Some result }, Cmd.none
    | IngestFailed _ -> model, Cmd.none
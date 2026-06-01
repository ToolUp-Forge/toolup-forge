module MyModule.ClientModel

open Elmish
open ToolUp.Platform
open MyModule.SharedTypes

type Model = { LastResponse: string option }

type Msg =
    | SubmitEcho of string
    | EchoSucceeded of EchoResponse
    | EchoFailed of string

let init () : Model * Cmd<Msg> = { LastResponse = None }, Cmd.none

let private myModuleApi: MyModuleApi =
    Api.makeProxy<MyModuleApi> (customOptions = UserSession.withRequestHeaders)

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | SubmitEcho text ->
        let cmd =
            Cmd.OfRemoting.call myModuleApi.Echo { Text = text } EchoSucceeded (fun ex -> EchoFailed ex.Message)

        model, cmd
    | EchoSucceeded response ->
        {
            model with
                LastResponse = Some response.Echoed
        },
        Cmd.none
    | EchoFailed err ->
        {
            model with
                LastResponse = Some(sprintf "Error: %s" err)
        },
        Cmd.none
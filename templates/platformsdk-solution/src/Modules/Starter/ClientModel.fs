module Starter.ClientModel

open Fuaran.MVU
open ToolUp.Platform
open Starter.SharedTypes

type Model = { LastResponse: string option }

type Msg =
    | SubmitEcho of string
    | EchoSucceeded of EchoResponse
    | EchoFailed of string

let init () : Model * Step<Msg, RemoteEffect<Msg>> list = { LastResponse = None }, []

let private starterApi: StarterApi =
    Api.makeProxy<StarterApi> (customOptions = UserSession.withRequestHeaders)

let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | SubmitEcho text ->
        let call =
            RemoteEffect.call "StarterApi.Echo" starterApi.Echo { Text = text } (function
                | Ok response -> EchoSucceeded response
                | Error ex -> EchoFailed ex.Message)

        model, [ Do call ]
    | EchoSucceeded response ->
        {
            model with
                LastResponse = Some response.Echoed
        },
        []
    | EchoFailed err ->
        {
            model with
                LastResponse = Some(sprintf "Error: %s" err)
        },
        []
module Starter.Server

open Starter.SharedTypes

let private echo (request: EchoRequest) : Async<EchoResponse> = async {
    return {
        Echoed = sprintf "Echo: %s" request.Text
    }
}

let routes: StarterApi = { Echo = echo }
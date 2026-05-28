module Starter.SharedTypes

type EchoRequest = { Text: string }
type EchoResponse = { Echoed: string }

type StarterApi = {
    Echo: EchoRequest -> Async<EchoResponse>
}
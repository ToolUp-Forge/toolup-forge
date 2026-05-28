module MyModule.SharedTypes

type EchoRequest = { Text: string }
type EchoResponse = { Echoed: string }

type MyModuleApi = {
    Echo: EchoRequest -> Async<EchoResponse>
}
module MyModule.Server

open MyModule.SharedTypes

let private echo (request: EchoRequest) : Async<EchoResponse> = async {
    return {
        Echoed = sprintf "Echo: %s" request.Text
    }
}

let routes: MyModuleApi = { Echo = echo }
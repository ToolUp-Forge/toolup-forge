module NativeClient.Program

open System
open System.Net.Http
open System.Text
open System.Text.Json
open HelloWorld.Module.SharedTypes

// ─── Phase 244 — non-web reference client ────────────────────────────
//
// A plain .NET *console* consumer of the SAME typed contract the web
// client and server use (`HelloWorld.Module.SharedTypes`, source-linked
// here — proving the contract is framework-neutral: no Fable, no
// browser, no ASP.NET Core needed to call it). It talks the documented
// ToolUp.Remoting wire over BCL HttpClient:
//
//   POST {baseUrl}/{ApiTypeName}/{MethodName}
//   request body  = the method's arguments as a JSON array  → [ {"Text": "..."} ]
//   response body = the result value directly               → {"Echoed": "..."}
//
// The Echo contract is plain records (no F# DU / Option), so System.Text.Json
// alone suffices. A contract using DUs / Option / Map needs the shared
// `ToolUp.Remoting.Json.SystemTextJson.FableConverters` set (see
// docs/platform/native-clients.md) — the single seam a richer native
// client would add.

/// Per-request identity seam (docs/platform/native-clients.md): read the
/// current token from your own store on EVERY call, never bake it into a
/// long-lived client. The HelloWorld Echo endpoint is `[<AllowAnonymous>]`,
/// so this demo sends none.
let private applyIdentity (_req: HttpRequestMessage) : unit = ()

let private jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

/// Call the typed `Echo` method over the ToolUp.Remoting wire.
let callEcho (http: HttpClient) (baseUrl: string) (text: string) : Async<EchoResponse> = async {
    // Arguments serialise as a JSON array — one element per method arg.
    let request: EchoRequest = { Text = text }
    let body = JsonSerializer.Serialize([| request |], jsonOptions)

    use msg =
        new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/HelloWorldApi/Echo")

    applyIdentity msg
    msg.Content <- new StringContent(body, Encoding.UTF8, "application/json")

    let! resp = http.SendAsync msg |> Async.AwaitTask
    let! payload = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

    if not resp.IsSuccessStatusCode then
        return failwithf "Echo failed (%d): %s" (int resp.StatusCode) payload

    // Response is the bare result value. Parse the one field directly
    // — robust regardless of F#-record deserialisation support.
    use doc = JsonDocument.Parse payload

    return {
        Echoed = doc.RootElement.GetProperty("Echoed").GetString()
    }
}

/// Show the exact request bytes a call produces — runnable with no
/// server, so the sample builds + runs in CI and documents the wire.
let private demo (text: string) : unit =
    let body =
        JsonSerializer.Serialize([| ({ Text = text }: EchoRequest) |], jsonOptions)

    printfn "Reference ToolUp.Remoting request for HelloWorldApi.Echo:"
    printfn "  POST <baseUrl>/HelloWorldApi/Echo"
    printfn "  body: %s" body
    printfn "  (pass a base URL as the first arg to round-trip against a running HelloWorld server)"

[<EntryPoint>]
let main argv =
    let text =
        argv |> Array.tryItem 1 |> Option.defaultValue "hello from a native .NET client"

    match argv |> Array.tryHead with
    | None
    | Some "" ->
        demo text
        0
    | Some baseUrl ->
        use http = new HttpClient()
        let result = callEcho http baseUrl text |> Async.RunSynchronously
        printfn "Echoed: %s" result.Echoed
        0
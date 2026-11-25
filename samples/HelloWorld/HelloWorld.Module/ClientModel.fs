module HelloWorld.Module.ClientModel

open Fuaran.MVU
open ToolUp.Elmish
open ToolUp.Platform
open HelloWorld.Module.SharedTypes

// ─── Elmish MVU ──────────────────────────────────────────────────

type Model = {
    Input: string
    LastResponse: string option
#if DEBUG
    /// Phase 12c verification — when true, the view throws during
    /// render. Toggled by a Debug-only button. The boundary should
    /// catch the exception and surface its localised error UI; clicking
    /// Reload re-runs `init ()` (which sets this back to false), proving
    /// containment + recovery.
    ShouldThrow: bool
#endif
}

type Msg =
    | SubmitEcho of string
    | EchoSucceeded of EchoResponse
    | EchoFailed of string
#if DEBUG
    | TriggerThrow
#endif

let init () : Model * Step<Msg, RemoteEffect<Msg>> list =
    {
        Input = ""
        LastResponse = None
#if DEBUG
        ShouldThrow = false
#endif
    },
    []

/// ToolUp.Remoting proxy. `UserSession.withRequestHeaders` attaches the
/// auth header (Bearer token in authenticated modes; X-User-Id in
/// Anonymous mode) so the server's per-request scope resolution works.
let private helloWorldApi: HelloWorldApi =
    Api.makeProxy<HelloWorldApi> (customOptions = UserSession.withRequestHeaders)

let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | SubmitEcho text ->
        { model with Input = text },
        [
            Do(
                RemoteEffect.call "HelloWorldApi.Echo" helloWorldApi.Echo { Text = text } (function
                    | Ok response -> EchoSucceeded response
                    | Error ex -> EchoFailed ex.Message)
            )
        ]
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
#if DEBUG
    | TriggerThrow -> { model with ShouldThrow = true }, []
#endif
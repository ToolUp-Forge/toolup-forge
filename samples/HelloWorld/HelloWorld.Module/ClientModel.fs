module HelloWorld.Module.ClientModel

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

let init () : Model * Cmd<Msg> =
    {
        Input = ""
        LastResponse = None
#if DEBUG
        ShouldThrow = false
#endif
    },
    Cmd.none

/// ToolUp.Remoting proxy. `UserSession.withRequestHeaders` attaches the
/// auth header (Bearer token in authenticated modes; X-User-Id in
/// Anonymous mode) so the server's per-request scope resolution works.
let private helloWorldApi: HelloWorldApi =
    Api.makeProxy<HelloWorldApi> (customOptions = UserSession.withRequestHeaders)

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | SubmitEcho text ->
        let cmd =
            Cmd.OfRemoting.call helloWorldApi.Echo { Text = text } EchoSucceeded (fun ex -> EchoFailed ex.Message)

        { model with Input = text }, cmd
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
#if DEBUG
    | TriggerThrow -> { model with ShouldThrow = true }, Cmd.none
#endif
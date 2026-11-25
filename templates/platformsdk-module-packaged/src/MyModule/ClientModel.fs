module MODULE_NAMESPACE_ROOT.ClientModel

open Fuaran.MVU
open ToolUp.Elmish
open ToolUp.Platform
open MODULE_NAMESPACE_ROOT.SharedTypes
open ToolUp.Platform.Config

// ─── Elmish MVU ──────────────────────────────────────────────────
//
// Client-tier. Packed as source under `fable/` and compiled by the
// CONSUMER's Fable build; also compiled into this package's assembly
// (the fsproj defines FABLE_COMPILER) so symbols resolve for .NET
// consumers and so this repo's own conformance test can bind the real
// registration.

type Model = {
    Input: string
    LastResponse: string option
}

type Msg =
    | InputChanged of string
    | SubmitEcho
    | EchoSucceeded of EchoResponse
    | EchoFailed of string

let init () : Model * Step<Msg, RemoteEffect<Msg>> list = { Input = ""; LastResponse = None }, []

/// The remoting proxy, behind a `lazy`.
///
/// Deliberate, not incidental: `Api.makeProxy` is a client-runtime
/// call. This module is also compiled into the .NET assembly, and the
/// repo's own conformance test binds `ClientRegister.registerWith` on
/// .NET — which touches this module and would run a module-level
/// initialiser. `lazy` defers it to the first dispatch, where a browser
/// is actually present. Fable compiles `lazy` unchanged.
let private api =
    lazy (Api.makeProxy<ModuleApi> (customOptions = UserSession.withRequestHeaders))

let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | InputChanged text -> { model with Input = text }, []
    | SubmitEcho ->
        let call =
            RemoteEffect.call "ModuleApi.Echo" api.Value.Echo { Text = model.Input } (function
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
                LastResponse = Some $"Error: {err}"
        },
        []
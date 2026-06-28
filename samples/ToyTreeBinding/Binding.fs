// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Toolup.Samples.ToyTreeBinding.Binding

open Feliz
open ToolUp.Elmish
open ToolUp.Platform
open Toolup.Samples.ToyTreeBinding.ToyNode

// ─── The client binding — `withElementView` (neutrality proof) ────────
//
// This is the load-bearing half of the proof: the toy tree language
// hosts as an ordinary `ClientModule` view through the additive
// `withElementView` overload, and EVERY one of the four
// `ClientHostCapabilities` (`Navigate` / `Notify` / `Dispatch` / `Call`)
// is reached by a toy event. The seam required no toy-specific change —
// the toy binds an adapter (`route`) from its own `ToyEvent` vocabulary
// onto the four hooks, exactly as the seam's contract promises any
// external tree language would.
//
// A pipeline that never calls `Binding.register` never constructs the
// capability bag and pays nothing; the file is inert when unused.

/// Minimal hosting-module state. The toy's `DispatchBump` / `CallEcho`
/// events land here through the module's MVU loop, proving the
/// `Dispatch` / `Call` hooks round-trip into ordinary Elmish.
type Model = { Count: int; LastEcho: string }

type Msg =
    | Bumped
    | Echoed of string
    | EchoFailed of string

let init () : Model * Cmd<Msg> = { Count = 0; LastEcho = "" }, Cmd.none

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | Bumped -> { model with Count = model.Count + 1 }, Cmd.none
    | Echoed s -> { model with LastEcho = s }, Cmd.none
    | EchoFailed e -> { model with LastEcho = $"error: {e}" }, Cmd.none

/// The toy view tree. One event site per capability, so the binding
/// exercises all four hooks.
let private tree (model: Model) : ToyNode =
    Element(
        "section",
        [
            Element("p", [ Text $"count: {model.Count} · last echo: {model.LastEcho}" ])
            OnClick(NavigateTo "Home", Element("button", [ Text "go home" ]))
            OnClick(NotifyWith(Info, "hello from the toy tree"), Element("button", [ Text "notify" ]))
            OnClick(DispatchBump, Element("button", [ Text "bump count" ]))
            OnClick(CallEcho "ping", Element("button", [ Text "echo" ]))
        ]
    )

/// Adapt the toy's event vocabulary onto the four host capabilities.
/// THIS is the neutrality proof in one function — each `ToyEvent` case
/// routes through exactly one `ClientHostCapabilities` hook, no hook
/// left unbound, no seam change required.
let private route (host: ClientHostCapabilities<Msg>) (event: ToyEvent) : unit =
    match event with
    | NavigateTo sidebarId -> host.Navigate sidebarId
    | NotifyWith(level, text) ->
        let intent =
            match level with
            | Info -> ToastIntent.info text
            | Warning -> ToastIntent.warning text
            | Error -> ToastIntent.error text

        host.Notify intent
    | DispatchBump -> host.Dispatch Bumped
    | CallEcho input -> host.Call(async { return input }, Echoed, (fun ex -> EchoFailed ex.Message))

/// The full-width view: lower the toy tree, wiring its events through
/// the host-capability bag the `withElementView` overload supplies.
let private view (model: Model) (_dispatch: Msg -> unit) (host: ClientHostCapabilities<Msg>) : ReactElement =
    lower (route host) (tree model)

/// Register the toy as an ordinary `ClientModule`. The pipeline shape is
/// identical to any `withFullWidthView` module — the only difference is
/// the view additionally receives the capability bag.
let register () : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = "Toy Tree Binding"
        Icon = Icon.ofUrl "/svg/chart.svg"
    }
    |> ClientHostView.withElementView view
    |> ClientModule.register
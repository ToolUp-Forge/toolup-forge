// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Drive an `update`'s step list by hand, outside a runtime: a follow-up is
/// dispatched, and an effect is performed with its one answer dispatched.
/// The tests that use it read what the steps DO, not the order a runtime
/// would run them in; the runtime's own order is pinned by the tests that
/// mount a `Fuaran.MVU` runtime (`ShellRuntimeTests`).
module ToolUp.AI.Client.Tests.StepDriver

open Fuaran.MVU
open ToolUp.Elmish

/// Run one step against `dispatch`.
let runStep (dispatch: 'msg -> unit) (step: Step<'msg, RemoteEffect<'msg>>) : unit =
    match step with
    | Then msg -> dispatch msg
    | Do effect ->
        match RemoteEffect.perform effect with
        | Performance.Signal fire -> fire ()
        | Reply answer -> answer dispatch

/// Run every step of a list against `dispatch`, in order.
let runSteps (dispatch: 'msg -> unit) (steps: Step<'msg, RemoteEffect<'msg>> list) : unit =
    List.iter (runStep dispatch) steps

[<Fable.Core.Import("renderToString", "react-dom/server")>]
let private renderToString (element: obj) : string = Fable.Core.Util.jsNative

[<Fable.Core.Import("createElement", "react")>]
let private createElement (kind: obj, props: obj) : obj = Fable.Core.Util.jsNative

/// Build an element view against `dispatch` inside a React render, as a
/// mount's root does (the shell's view reads React hooks); the markup is
/// dropped. A test reads what the build saw.
let buildInRender (view: Fuaran.MVU.React.ElementView<'msg>) (dispatch: 'msg -> unit) : unit =
    renderToString (createElement (System.Func<obj, obj>(fun _ -> box (view.Build dispatch)), null))
    |> ignore

/// A program's view as a test calls it: the model and the dispatch, built
/// inside a React render. A test stands for a mount by its dispatch: a
/// dispatch the previous call was not handed starts a new mount.
let viewOf (program: ToolUp.Platform.Client.ShellProgram<'model, 'msg>) : 'model -> ('msg -> unit) -> unit =
    let last: ('msg -> unit) option ref = ref None

    fun model dispatch ->
        match last.Value with
        | Some seen when obj.ReferenceEquals(seen, dispatch) -> ()
        | _ ->
            program.BeginMount()
            last.Value <- Some dispatch

        buildInRender (program.Component.View model) dispatch
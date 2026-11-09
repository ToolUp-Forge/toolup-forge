// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.ModuleDispatchStabilityTests

// ─── The module view's dispatch prop is stable and bound to its module ──
//
// The shell hands every module view a `ModuleMsg` wrapper over `dispatch`,
// carrying that module's id. Built inline in `Client.view`, the module's one
// dispatch prop would be a fresh closure on every render and no memo boundary
// around a module view could ever hold. The loop's `Client.Dispatchers` set
// builds it once per module, and the program builds the set once per loop
// (`Program.withStableDispatch`), so every render hands out the SAME function.
//
// This is the transpiled code's property (a JavaScript function's
// identity is what `React.memo` compares), so it is pinned here under
// Node on Fable's output, not on .NET. `Object.is` is the comparison a
// memo boundary makes.

open Fable.Core
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

[<Emit("Object.is($0, $1)")>]
let private objectIs (a: obj) (b: obj) : bool = jsNative

/// The dispatcher a render of the shell's program hands module `moduleId`
/// for the loop dispatcher `dispatch` — through the program's own view, so
/// the once-per-loop build is what is measured.
let private moduleDispatchFor =
    let rendered: Client.Dispatchers option ref = ref None

    let view =
        Client.programOver
            ClientConfig.defaults
            (fun () -> (), Cmd.none)
            (fun (_: Client.Msg) () -> (), Cmd.none)
            Client.dispatchers
            (fun () dispatchers ->
                rendered.Value <- Some dispatchers
                Feliz.Html.none)
        |> Program.view

    fun (moduleId: string) (dispatch: Client.Msg -> unit) ->
        view () dispatch |> ignore
        rendered.Value.Value.ForModule moduleId

let tests =
    testList "The module dispatch prop is referentially stable" [

        testCase "the same loop's dispatch yields the identical module dispatcher on every render"
        <| fun () ->
            let received = ResizeArray<Client.Msg>()
            // One loop, one `dispatch'` — as `Program.runWithDispatch` hands
            // the shell's view the same function on every render.
            let dispatch: Client.Msg -> unit = received.Add

            let first = moduleDispatchFor "m1" dispatch
            let second = moduleDispatchFor "m1" dispatch
            let third = moduleDispatchFor "m1" dispatch

            Expect.isTrue (objectIs (box first) (box second)) "render 1 and render 2 receive the same function"
            Expect.isTrue (objectIs (box second) (box third)) "render 2 and render 3 receive the same function"

            // …and it still routes into the shell as `ModuleMsg`.
            first (box 42)

            match List.ofSeq received with
            | [ Client.ModuleMsg(moduleId, payload) ] ->
                Expect.equal moduleId "m1" "the message names the module the view belongs to"
                Expect.equal (unbox<int> payload) 42 "the module message reaches the loop"
            | other -> failwithf "expected one ModuleMsg, got %A" other

        testCase "each module has its own dispatcher, and going back to a module finds the same one"
        <| fun () ->
            let received = ResizeArray<Client.Msg>()
            let dispatch: Client.Msg -> unit = received.Add

            let forA = moduleDispatchFor "a" dispatch
            let forB = moduleDispatchFor "b" dispatch
            let forAAgain = moduleDispatchFor "a" dispatch

            Expect.isFalse (objectIs (box forA) (box forB)) "two modules, two wrappers"

            Expect.isTrue
                (objectIs (box forA) (box forAAgain))
                "leaving a module and returning keeps its prop's identity"

            forB (box "from-b")

            match List.ofSeq received with
            | [ Client.ModuleMsg(moduleId, _) ] ->
                Expect.equal moduleId "b" "a view's dispatch stays bound to its module"
            | other -> failwithf "expected one ModuleMsg, got %A" other

        testCase "a different loop's dispatch yields a different module dispatcher"
        <| fun () ->
            // HMR re-runs the program: a new `dispatch'`, so a new wrapper,
            // never a stale one routing into the torn-down loop.
            let a = ResizeArray<Client.Msg>()
            let b = ResizeArray<Client.Msg>()
            let dispatchA: Client.Msg -> unit = a.Add
            let dispatchB: Client.Msg -> unit = b.Add

            let forA = moduleDispatchFor "m1" dispatchA
            let forB = moduleDispatchFor "m1" dispatchB

            Expect.isFalse (objectIs (box forA) (box forB)) "two loops, two wrappers"

            forB (box "to-b")
            Expect.equal a.Count 0 "nothing reached the first loop"
            Expect.equal b.Count 1 "the second loop received it"

            // Going back to the first loop rebuilds rather than serving B's.
            let forAAgain = moduleDispatchFor "m1" dispatchA
            forAAgain (box "to-a")
            Expect.equal a.Count 1 "the rebuilt wrapper routes to the first loop"
            Expect.equal b.Count 1 "…and not to the second"
    ]
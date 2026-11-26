// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.ModuleDispatchStabilityTests

// ─── The module view's dispatch prop is stable and bound to its module ──
//
// The shell hands every module view a `ModuleMsg` wrapper over `dispatch`,
// carrying that module's id. Built inline in `Client.view`, the module's one
// dispatch prop would be a fresh closure on every render and no memo boundary
// around a module view could ever hold. The mount's `Client.Dispatchers` set
// builds it once per module, and the program builds the set once per mount
// (`Client.programOver`), so every render hands out the SAME function.
//
// This is the transpiled code's property (a JavaScript function's
// identity is what `React.memo` compares), so it is pinned here under
// Node on Fable's output, not on .NET. `Object.is` is the comparison a
// memo boundary makes.

open Fable.Core
open Fuaran.MVU
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.AI.Client.Tests.StepDriver

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
            (fun () -> (), [])
            (fun (_: Client.Msg) () -> (), [])
            (fun _ -> Map.empty)
            Client.dispatchers
            (fun () dispatchers ->
                rendered.Value <- Some dispatchers
                Feliz.Html.none)
        |> viewOf

    fun (moduleId: string) (dispatch: Client.Msg -> unit) ->
        view () dispatch |> ignore
        rendered.Value.Value.ForModule moduleId

// ─── A re-evaluated module keeps its model and takes its new code ───────
//
// A save in a development build runs the client's entry again: the program
// is built over the module's NEW `update` and view and mounted under the
// same key, and the adapter's hot reload terminates the old runtime and
// resumes the new one from its model and last sequence (`Runtime.Resume`).
// That registry is compiled only under DEBUG (Fable's watch mode); this pack
// compiles Release, where it is inert, so the harness below makes the carry
// with the same public call, and is otherwise a mount as the adapter makes
// one: ONE dispatch for its life, every render built with it.

/// What one render of a module version saw.
type private Rendered = {
    Version: string
    Model: int
    ForModule: obj -> unit
}

/// The shell's program over one version of module "m1": its `update` adds
/// `step` times the message, its view records what it was handed.
let private moduleVersion (version: string) (step: int) (seen: ResizeArray<Rendered>) =
    Client.programOver
        ClientConfig.defaults
        (fun () -> 0, [])
        (fun (msg: Client.Msg) (model: int) ->
            match msg with
            | Client.ModuleMsg("m1", payload) -> model + step * unbox<int> payload, []
            | _ -> model, [])
        (fun _ -> Map.empty)
        Client.dispatchers
        (fun model dispatchers ->
            seen.Add {
                Version = version
                Model = model
                ForModule = dispatchers.ForModule "m1"
            }

            Feliz.Html.none)

/// A mount of `program`: fresh, or resumed from a carried model and
/// sequence. Like the adapter's, it hands every render one dispatch.
type private HotMount(program: Client.ShellProgram<int, Client.Msg>, carried: (int * int64) option) =
    let mutable running: Runtime<unit, int, Client.Msg, _, _, _> option = None

    // Held in a cell, as the adapter holds it: a class's function-valued
    // `let` compiles to a method, and passing a method builds a new
    // function on every render.
    let handed: (Client.Msg -> unit) ref =
        ref (fun msg ->
            match running with
            | Some runtime -> runtime.Dispatch(msg, User "local") |> ignore
            | None -> ())

    let host = {
        program.Host with
            Render = fun view -> buildInRender view handed.Value
    }

    let runtime =
        match carried with
        | Some(model, sequence) -> Runtime.Resume(program.Component, program.Interpreter, host, model, [], sequence)
        | None -> Runtime(program.Component, program.Interpreter, host, ())

    do
        running <- Some runtime
        runtime.Start()

    member _.Runtime = runtime

    /// The re-evaluation: terminate this mount and mount `next` over what it
    /// carried, as the adapter's hot reload does.
    member _.Reload(next: Client.ShellProgram<int, Client.Msg>) : HotMount =
        let carried = runtime.Model, runtime.Sequence
        runtime.Terminate()
        HotMount(next, Some carried)

let tests =
    testList "The module dispatch prop is referentially stable" [

        testCase "the same loop's dispatch yields the identical module dispatcher on every render"
        <| fun () ->
            let received = ResizeArray<Client.Msg>()
            // One mount, one `dispatch` — a mount hands the shell's view the
            // same function on every render.
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

        testCase "a re-evaluated module keeps its model, runs its new update and view, and its mount one dispatch"
        <| fun () ->
            let before = ResizeArray<Rendered>()
            let after = ResizeArray<Rendered>()
            let first = HotMount(moduleVersion "v1" 1 before, None)

            let oldDispatch = before[before.Count - 1].ForModule
            oldDispatch (box 1)
            oldDispatch (box 1)
            Expect.equal first.Runtime.Model 2 "the first version's update ran twice"

            // The save: the module's `update` now adds ten, its view is v2.
            let second = first.Reload(moduleVersion "v2" 10 after)

            Expect.equal after[0].Model 2 "the model held its value across the reload"
            Expect.equal after[0].Version "v2" "the reloaded mount renders the new view"

            let forModule = after[0].ForModule
            forModule (box 1)
            Expect.equal second.Runtime.Model 12 "the next dispatch ran the NEW update"
            forModule (box 1)
            Expect.equal second.Runtime.Model 22 "…and the one after it"

            let last = after[after.Count - 1]
            Expect.equal last.Version "v2" "the view is still the new view"
            Expect.equal last.Model 22 "drawn over the new update's model"
            Expect.isTrue (after.Count > 2) "each dispatch rendered again"

            for rendered in after do
                Expect.isTrue
                    (objectIs (box rendered.ForModule) (box forModule))
                    "every render of the reloaded mount hands the module the same dispatch"

            Expect.isFalse
                (objectIs (box oldDispatch) (box forModule))
                "the reloaded mount's dispatch is its own, not the old mount's"

            oldDispatch (box 1)

            Expect.equal first.Runtime.Model 2 "the old mount's dispatch reaches nothing: its runtime was terminated"

            Expect.equal
                second.Runtime.Model
                22
                "…and nothing of it reaches the reloaded mount, which holds the only live dispatch"
    ]
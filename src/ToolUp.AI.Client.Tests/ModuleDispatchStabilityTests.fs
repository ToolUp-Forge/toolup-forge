// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.ModuleDispatchStabilityTests

// ─── Phase 851.D — the module view's dispatch prop is stable ─────────
//
// The shell hands every module view `ModuleMsg >> dispatch`. Until 851
// that wrapper was built inline in `Client.view`, so the module's one
// dispatch prop was a fresh closure on every render and no memo boundary
// around a module view could ever hold. `Client.moduleDispatchFor` now
// builds it once per loop and hands the SAME function to every render.
//
// This is the transpiled code's property (a JavaScript function's
// identity is what `React.memo` compares), so it is pinned here under
// Node on Fable's output, not on .NET. `Object.is` is the comparison a
// memo boundary makes.

open Fable.Core
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

[<Emit("Object.is($0, $1)")>]
let private objectIs (a: obj) (b: obj) : bool = jsNative

let tests =
    testList "Phase 851.D - the module dispatch prop is referentially stable" [

        testCase "the same loop's dispatch yields the identical module dispatcher on every render"
        <| fun () ->
            let received = ResizeArray<Client.Msg>()
            // One loop, one `dispatch'` — as `Program.runWithDispatch` hands
            // the shell's view the same function on every render.
            let dispatch: Client.Msg -> unit = received.Add

            let first = Client.moduleDispatchFor dispatch
            let second = Client.moduleDispatchFor dispatch
            let third = Client.moduleDispatchFor dispatch

            Expect.isTrue (objectIs (box first) (box second)) "render 1 and render 2 receive the same function"
            Expect.isTrue (objectIs (box second) (box third)) "render 2 and render 3 receive the same function"

            // …and it still routes into the shell as `ModuleMsg`.
            first (box 42)

            match List.ofSeq received with
            | [ Client.ModuleMsg payload ] -> Expect.equal (unbox<int> payload) 42 "the module message reaches the loop"
            | other -> failwithf "expected one ModuleMsg, got %A" other

        testCase "a different loop's dispatch yields a different module dispatcher"
        <| fun () ->
            // HMR re-runs the program: a new `dispatch'`, so a new wrapper,
            // never a stale one routing into the torn-down loop.
            let a = ResizeArray<Client.Msg>()
            let b = ResizeArray<Client.Msg>()
            let dispatchA: Client.Msg -> unit = a.Add
            let dispatchB: Client.Msg -> unit = b.Add

            let forA = Client.moduleDispatchFor dispatchA
            let forB = Client.moduleDispatchFor dispatchB

            Expect.isFalse (objectIs (box forA) (box forB)) "two loops, two wrappers"

            forB (box "to-b")
            Expect.equal a.Count 0 "nothing reached the first loop"
            Expect.equal b.Count 1 "the second loop received it"

            // Going back to the first loop rebuilds rather than serving B's.
            let forAAgain = Client.moduleDispatchFor dispatchA
            forAAgain (box "to-a")
            Expect.equal a.Count 1 "the rebuilt wrapper routes to the first loop"
            Expect.equal b.Count 1 "…and not to the second"
    ]
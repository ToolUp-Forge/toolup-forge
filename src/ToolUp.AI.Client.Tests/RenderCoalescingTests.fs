// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.RenderCoalescingTests

// ─── Phase 851.A — two drains in one task construct the view once ────
//
// The loop paints once per drain; the React adapter (`React.fs`) then
// constructs the view once per TASK, through `RenderScheduler` over
// `queueMicrotask`. This pins the scheduler's coalescing on the
// transpiled code, under Node's real microtask queue: two requests in one
// synchronous stretch run the render once, after the stretch and before
// the next macrotask; a request made after that runs it again. The
// adapter's `setState` is a one-line caller of `Request`, so this is the
// mechanism the acceptance names, measured where it runs.

open Fable.Core
open ToolUp.Elmish.React
open ToolUp.AI.Client.Tests.NodeTest

[<Emit("queueMicrotask($0)")>]
let private queueMicrotask (callback: unit -> unit) : unit = jsNative

let tests =
    testList "Phase 851.A - the React adapter constructs the view once per task" [

        testCaseDeferred "two requests in one task run the render once, on the microtask queue" 5
        <| fun () ->
            let renders = ResizeArray<int>()
            let scheduler = RenderScheduler queueMicrotask
            let mutable model = 0

            // Two drains, one task: each paints (requests a construction).
            model <- 1
            scheduler.Request(fun () -> renders.Add model)
            model <- 2
            scheduler.Request(fun () -> renders.Add model)

            Expect.equal renders.Count 0 "nothing is constructed synchronously - the task finishes first"
            Expect.isTrue scheduler.Pending "one construction is queued"

            // The deferred body runs one macrotask later, after every microtask.
            fun () ->
                Expect.equal (List.ofSeq renders) [ 2 ] "one construction, reading the model the task ended on"
                Expect.isFalse scheduler.Pending "and the queue is clear"

        testCaseDeferred "a request after the construction ran schedules a fresh one" 5
        <| fun () ->
            let renders = ResizeArray<string>()
            let scheduler = RenderScheduler queueMicrotask

            scheduler.Request(fun () -> renders.Add "first task")

            queueMicrotask (fun () ->
                // Runs after the first construction's microtask (FIFO), so
                // this is a later request, not a coalesced one.
                scheduler.Request(fun () -> renders.Add "second request"))

            fun () ->
                Expect.equal (List.ofSeq renders) [ "first task"; "second request" ] "two requests, two constructions"

        testCaseDeferred "a request made from inside the render is not swallowed" 5
        <| fun () ->
            // The flag is cleared BEFORE the render runs, so a hook call the
            // render provokes queues a fresh construction rather than being
            // folded into the one that is finishing.
            let renders = ResizeArray<int>()
            let mutable scheduler: RenderScheduler option = None
            scheduler <- Some(RenderScheduler queueMicrotask)

            let rec render () =
                renders.Add renders.Count

                if renders.Count = 1 then
                    scheduler.Value.Request render

            scheduler.Value.Request render

            fun () -> Expect.equal (List.ofSeq renders) [ 0; 1 ] "the nested request ran as a second construction"
    ]
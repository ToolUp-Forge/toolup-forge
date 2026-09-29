// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ElmishLoopProofOracleTests

open System.Numerics
open Expecto
open ToolUp.Elmish
open ToolUp.Platform.Tests.Client.ElmishLoopDifferential

// ─── Phase 789 — the proved dispatch loop as oracle ──────────────────
//
// The Expecto skin. Since Phase 884 the differential itself — the script,
// its generator, the drivers of the real loop and of the extracted
// machine, the hand-transcribed skeleton with its go-red variants, and
// every comparison over the campaign as a ROW — is the host-neutral
// `Client/ElmishLoopDifferential.fs`, compiled here and into the Fable
// pack (`ToolUp.AI.Client.Tests/ElmishProofOracleTests.fs`), which runs
// the same rows over the same campaign against the TRANSPILED loop and
// the extraction compiled by Fable. Read that file's header for what is
// compared and why; a new property over the campaign is a new row there,
// so both hosts run it.
//
// What stays here is what is not the campaign: the hand-written
// production scenarios below, several of which read the process-wide
// `Console.Error` the guard's last resort writes to, and the fallback
// arm's one-off comparison against the model's `fallback_terminate`.

let private big (n: int) : BigInteger = BigInteger n

/// Run `body` with `Console.Error` captured, and hand back what it wrote.
/// The guard's last resort is the console, so this is both how the
/// reporter cases READ what the guard did and how they keep a deliberate
/// failure's stack trace out of the gate's log. The cases that use it are
/// `testSequenced`: the writer is process-wide.
let private capturingStderr (body: unit -> unit) : string =
    let original = System.Console.Error
    use captured = new System.IO.StringWriter()
    System.Console.SetError captured

    try
        body ()
    finally
        System.Console.SetError original

    captured.ToString()

/// How many times `needle` occurs in `text`.
let private occurrences (needle: string) (text: string) : int =
    (text.Length - text.Replace(needle, "").Length) / needle.Length

[<Literal>]
let private ReporterRaised = "The error reporter raised while reporting: "

/// Every row of the shared differential, as one Expecto case each.
let private corpusTests =
    corpusChecks
    |> List.map (fun check -> test check.Name {
        let violations = check.Violations()
        Expect.isEmpty violations (report check violations)
    })

let tests =
    testList "Phase 789 - the proved dispatch loop as oracle" [

        // The campaign rows — the same rows the Fable pack runs.
        testList "the campaign, on both hosts" corpusTests

        test "a drain of N messages calls the render hook once - render_once_per_drain, run" {
            // Phase 851.A's acceptance, on production with a quiet hook: one
            // external dispatch whose command fans out into a chain of
            // re-dispatches is ONE drain, and the hook runs once at its end
            // — however long the chain. The boot is one paint too.
            let renders = ResizeArray<int list>()
            let mutable handle: IDispatcher<int> option = None

            let update (msg: int) (model: int list) =
                // 0 fans out to 1..4; each of those to its double, up to 40.
                let fanOut =
                    if msg = 0 then [ 1..4 ]
                    elif msg > 0 && msg * 2 <= 40 then [ msg * 2 ]
                    else []

                model @ [ msg ], Cmd.batch [ for m in fanOut -> Cmd.ofMsg m ]

            Program.mkProgram (fun () -> [], Cmd.none) update (fun _ _ -> ())
            |> Program.withSetState (fun m _ -> renders.Add m)
            |> Program.withDispatcherHandle (fun d -> handle <- Some d)
            |> Program.runWithDispatch id ()

            Expect.equal (List.ofSeq renders) [ [] ] "the boot painted the init model exactly once"

            handle.Value.Dispatch 0
            let drained = renders.[renders.Count - 1]

            Expect.equal renders.Count 2 "one external dispatch, one drain, one paint"
            Expect.isGreaterThan (List.length drained) 10 "the drain processed the whole fan-out before painting"
            Expect.equal (List.head drained) 0 "…starting with the dispatched message"

            handle.Value.Dispatch 100
            Expect.equal renders.Count 3 "a second drain, a second paint"
            Expect.equal renders.[2] (drained @ [ 100 ]) "handed the model the drain ended on"
        }

        test "a terminating dispatch paints nothing - render_once_per_drain, the terminated arm, run" {
            let renders = ResizeArray<int list>()
            let mutable handle: IDispatcher<int> option = None

            Program.mkProgram (fun () -> [], Cmd.none) (fun msg model -> model @ [ msg ], Cmd.none) (fun _ _ -> ())
            |> Program.withSetState (fun m _ -> renders.Add m)
            |> Program.withTermination ((=) 9) ignore
            |> Program.withDispatcherHandle (fun d -> handle <- Some d)
            |> Program.runWithDispatch id ()

            handle.Value.Dispatch 1
            Expect.equal renders.Count 2 "boot paint + one drain paint"
            handle.Value.Dispatch 9
            Expect.equal renders.Count 2 "the terminating drain painted nothing"
            Expect.isFalse handle.Value.IsActive "…and terminated"
        }

        test "the fallback arm drives the two flags apart - the encoding's one exception" {
            // `fallback_breaks_encoding`, on production: a dispatcher exposed
            // without its terminate callback clears `active` and leaves the
            // loop untouched — `IDispatcher.Dispatch` refuses while the
            // wired `dispatch` still runs. The runtime never wires the
            // interface this way; the arm reports the defect, and this pins
            // that it is the ONE state the proved invariant excludes.
            let seen = ResizeArray<int>()
            let reported = ResizeArray<exn>()
            let core = DispatcherCore<int>()
            core.Wire seen.Add
            let handle = core.AsInterface reported.Add

            Expect.isTrue handle.IsActive "wired"
            handle.Terminate()
            Expect.isFalse handle.IsActive "the fallback cleared `active`"
            Expect.equal reported.Count 1 "…and reported the wiring defect"
            handle.Dispatch 1
            Expect.isEmpty seen "the interface refuses"

            // The model computes the same state.
            let s = ElmishLoop.fallback_terminate (ElmishLoop.initial (big 2) ([]: int list))
            Expect.isFalse s.active "the model's `active`"
            Expect.isFalse s.terminated "…while the loop's flag is untouched"
        }

        test
            "an effect that dispatches from its start function does not move the boot paint - boot_paints_init_model, run" {
            let renders = ResizeArray<int list>()
            let seen = ResizeArray<int>()
            let seenAtBootPaint = ResizeArray<int>()

            let update (msg: int) (model: int list) =
                seen.Add msg
                model @ [ msg ], Cmd.none

            Program.mkProgram (fun () -> [], Cmd.ofMsg 3) update (fun _ _ -> ())
            |> Program.withSetState (fun m _ ->
                if renders.Count = 0 then
                    seenAtBootPaint.AddRange seen

                renders.Add m)
            |> Program.withDispatcherHandle (fun d -> d.Dispatch 1)
            |> Program.withEffect (
                EffectHandle.programLifetime "dispatches-at-start" (fun dispatch ->
                    dispatch 2

                    { new System.IDisposable with
                        member _.Dispose() = ()
                    })
            )
            |> Program.runWithDispatch id ()

            Expect.isEmpty seenAtBootPaint "`update` had been handed nothing when the boot paint ran"

            Expect.equal
                (List.ofSeq renders)
                [ []; [ 1; 2; 3 ] ]
                "the boot paint was handed the init model, and ONE drain painted everything raised at boot"

            Expect.equal
                (List.ofSeq seen)
                [ 1; 2; 3 ]
                "the sink's message, the effect's, then init's command's — in the order they were raised"
        }

        test "a subscription an effect's message asks for is running after the boot" {
            // The second thing the old order broke. The early drain started
            // the subscriptions of the model it produced; the boot then
            // diffed against the INIT model's subscriptions, computed before
            // anything ran, and stopped them — leaving a model that wants a
            // subscription with none running until the next message.
            let started = ResizeArray<string>()
            let stopped = ResizeArray<string>()

            let subscribe (model: int list) : Sub<int> =
                if List.contains 1 model then
                    [
                        [ "wanted-once-1-arrived" ],
                        (fun _ ->
                            started.Add "wanted-once-1-arrived"

                            { new System.IDisposable with
                                member _.Dispose() = stopped.Add "wanted-once-1-arrived"
                            })
                    ]
                else
                    []

            Program.mkProgram (fun () -> [], Cmd.none) (fun msg model -> model @ [ msg ], Cmd.none) (fun _ _ -> ())
            |> Program.withSubscription subscribe
            |> Program.withEffect (
                EffectHandle.programLifetime "dispatches-at-start" (fun dispatch ->
                    dispatch 1

                    { new System.IDisposable with
                        member _.Dispose() = ()
                    })
            )
            |> Program.runWithDispatch id ()

            Expect.equal (List.ofSeq started) [ "wanted-once-1-arrived" ] "started, once"
            Expect.isEmpty stopped "…and not stopped by the boot's diff against the init model's subscriptions"
        }

        // ─── The reporter is total by construction ───────────────────────
        //
        // The model has no transition for an error reporter that throws: an
        // exception from a callee is an oracle reply, reported, and the
        // loop goes on. That is only true of a reporter that returns, so
        // production routes every call to the program's reporter through a
        // guard. These pin that it does, at each site a reporter is reached
        // from — a reporter that escaped any of them would leave the latch
        // set, and every later dispatch would queue and never drain.

        testSequenced
        <| test "a reporter that throws does not wedge the loop - from update, from a command, from the render hook" {
            let seen = ResizeArray<int>()
            let renders = ResizeArray<int list>()
            let mutable handle: IDispatcher<int> option = None
            let mutable reports = 0

            let update (msg: int) (model: int list) =
                seen.Add msg

                if msg = 1 then
                    failwith "update raised"

                let cmd =
                    if msg = 2 then
                        Cmd.ofEffect (fun _ -> failwith "the command raised")
                    else
                        Cmd.none

                model @ [ msg ], cmd

            let setState (m: int list) (_: int -> unit) =
                renders.Add m

                if List.tryLast m = Some 3 then
                    failwith "the render hook raised"

            let written =
                capturingStderr (fun () ->
                    Program.mkProgram (fun () -> [], Cmd.none) update (fun _ _ -> ())
                    |> Program.withSetState setState
                    |> Program.withErrorReporter (fun _ ->
                        reports <- reports + 1
                        failwith "the reporter raised")
                    |> Program.withDispatcherHandle (fun d -> handle <- Some d)
                    |> Program.runWithDispatch id ()

                    for msg in [ 1; 2; 3 ] do
                        // Neither may raise into the caller, and the second must be
                        // DRAINED: a latch left set would accept it and process nothing.
                        handle.Value.Dispatch msg
                        handle.Value.Dispatch(msg * 10))

            Expect.equal (List.ofSeq seen) [ 1; 10; 2; 20; 3; 30 ] "every message was handed to `update`, in order"
            Expect.equal reports 3 "each failure reached the reporter once"

            // …and neither failure was lost: the guard wrote the reporter's
            // exception AND the one it had been asked to report.
            Expect.equal (occurrences ReporterRaised written) 3 "the guard said so, once per failure"

            for original in [ "update raised"; "the command raised"; "the render hook raised" ] do
                Expect.stringContains written original "the failure being reported reached the console"

            Expect.equal
                renders.[renders.Count - 1]
                [ 10; 2; 20; 3; 30 ]
                "…and the last drain painted the model it ended on"
        }

        testSequenced
        <| test
            "a reporter that throws does not wedge the boot - from a sink, an effect, a subscription, init's command" {
            let seen = ResizeArray<int>()
            let mutable handle: IDispatcher<int> option = None
            let mutable reports = 0

            let update (msg: int) (model: int list) =
                seen.Add msg
                model @ [ msg ], Cmd.none

            let written =
                capturingStderr (fun () ->
                    Program.mkProgram
                        (fun () -> [], Cmd.ofEffect (fun _ -> failwith "init's command raised"))
                        update
                        (fun _ _ -> ())
                    |> Program.withSubscription (fun _ -> [
                        [ "raises" ], (fun _ -> failwith "the subscription's start raised")
                    ])
                    |> Program.withErrorReporter (fun _ ->
                        reports <- reports + 1
                        failwith "the reporter raised")
                    |> Program.withDispatcherHandle (fun d -> handle <- Some d)
                    |> Program.withDispatcherHandle (fun _ -> failwith "the sink raised")
                    |> Program.withEffect (
                        EffectHandle.programLifetime "raises" (fun _ -> failwith "the effect's start raised")
                    )
                    |> Program.runWithDispatch id ())

            Expect.equal reports 4 "the sink, the effect, the subscription and the command each reached the reporter"
            Expect.equal (occurrences ReporterRaised written) 4 "the guard said so, once per failure"

            for original in
                [
                    "the sink raised"
                    "the effect's start raised"
                    "the subscription's start raised"
                    "init's command raised"
                ] do
                Expect.stringContains written original "the failure being reported reached the console"

            capturingStderr (fun () -> handle.Value.Dispatch 7) |> ignore
            Expect.equal (List.ofSeq seen) [ 7 ] "the boot returned with the latch released, and the loop drains"
        }

        testSequenced
        <| test "a reporter that throws does not stop Terminate tearing down" {
            let disposed = ResizeArray<string>()
            let mutable handle: IDispatcher<int> option = None

            let raisingOnDispose (name: string) =
                { new System.IDisposable with
                    member _.Dispose() =
                        disposed.Add name
                        failwith (name + " raised on dispose")
                }

            let written =
                capturingStderr (fun () ->
                    Program.mkProgram (fun () -> [], Cmd.none) (fun msg model -> model @ [ msg ], Cmd.none) (fun _ _ ->
                        ())
                    |> Program.withSubscription (fun _ -> [ [ "sub" ], (fun _ -> raisingOnDispose "sub") ])
                    |> Program.withErrorReporter (fun _ -> failwith "the reporter raised")
                    |> Program.withDispatcherHandle (fun d -> handle <- Some d)
                    |> Program.withEffect (EffectHandle.programLifetime "effect" (fun _ -> raisingOnDispose "effect"))
                    |> Program.runWithDispatch id ()

                    handle.Value.Terminate())

            Expect.equal (List.ofSeq disposed) [ "sub"; "effect" ] "the subscription and the effect were both disposed"
            Expect.equal (occurrences ReporterRaised written) 2 "the guard said so, once per failure"
            Expect.isFalse handle.Value.IsActive "…and the program terminated"
        }

        // ─── Termination is total (Phase 871) ────────────────────────────
        //
        // Both routes to termination — a message the predicate takes, and
        // `IDispatcher.Terminate` — run ONE teardown: the loop's flag first,
        // then the subscriptions stopped, the effects disposed and the handler
        // called, each under its own guard, and `MarkTerminated` in a
        // `finally`. And the boot starts nothing once the program is
        // terminated: it checks before every effect's registration, before the
        // subscription start and before `init`'s command, and a start that
        // terminated the program has what it returned released at once.
        // `terminated_holds_nothing` and `terminated_starts_nothing` are those
        // rules on the model; these are the production arms, each shown red on
        // the tree before the phase.

        test "a terminating message whose handler raises still terminates - teardown is total, run" {
            let seen = ResizeArray<int>()
            let reported = ResizeArray<ErrorContext>()
            let disposed = ResizeArray<string>()
            let mutable handle: IDispatcher<int> option = None

            let disposable (name: string) =
                { new System.IDisposable with
                    member _.Dispose() = disposed.Add name
                }

            let update (msg: int) (model: int list) =
                seen.Add msg
                model @ [ msg ], Cmd.none

            Program.mkProgram (fun () -> [], Cmd.none) update (fun _ _ -> ())
            |> Program.withTermination ((=) 9) (fun _ -> failwith "the terminate handler raised")
            |> Program.withSubscription (fun _ -> [ [ "sub" ], (fun _ -> disposable "sub") ])
            |> Program.withErrorReporter reported.Add
            |> Program.withDispatcherHandle (fun d -> handle <- Some d)
            |> Program.withEffect (EffectHandle.programLifetime "effect" (fun _ -> disposable "effect"))
            |> Program.runWithDispatch id ()

            handle.Value.Dispatch 1
            handle.Value.Dispatch 9
            Expect.isFalse handle.Value.IsActive "the handler raised, and the program terminated anyway"
            handle.Value.Dispatch 2
            Expect.equal (List.ofSeq seen) [ 1 ] "a dispatch after the terminating message reached `update` zero times"

            Expect.equal
                (List.ofSeq disposed)
                [ "sub"; "effect" ]
                "the subscription and the effect were disposed, in the teardown's order"

            Expect.equal reported.Count 1 "the handler's exception was reported, once"
            handle.Value.Terminate()
            Expect.equal (List.ofSeq disposed) [ "sub"; "effect" ] "a second Terminate tore nothing down twice"
        }

        test "a sink that terminates at boot starts no effect, no subscription and no command - run" {
            let started = ResizeArray<string>()
            let disposed = ResizeArray<string>()
            let renders = ResizeArray<int list>()
            let mutable handle: IDispatcher<int> option = None

            let disposable (name: string) =
                { new System.IDisposable with
                    member _.Dispose() = disposed.Add name
                }

            Program.mkProgram
                (fun () -> [], Cmd.ofEffect (fun _ -> started.Add "init's command"))
                (fun msg model -> model @ [ msg ], Cmd.none)
                (fun _ _ -> ())
            |> Program.withSetState (fun m _ -> renders.Add m)
            |> Program.withSubscription (fun _ -> [
                [ "sub" ],
                (fun _ ->
                    started.Add "sub"
                    disposable "sub")
            ])
            |> Program.withDispatcherHandle (fun d ->
                handle <- Some d
                d.Terminate())
            |> Program.withEffect (
                EffectHandle.programLifetime "effect" (fun _ ->
                    started.Add "effect"
                    disposable "effect")
            )
            |> Program.runWithDispatch id ()

            Expect.isEmpty started "nothing was started once a sink had terminated the program"
            Expect.isEmpty disposed "…so nothing was left to dispose"
            Expect.equal (List.ofSeq renders) [ [] ] "the boot paint is unconditional: the init model, once"
            Expect.isFalse handle.Value.IsActive "terminated"
        }

        test "an effect that terminates from its own start function has its handle disposed - run" {
            let started = ResizeArray<string>()
            let disposed = ResizeArray<string>()
            let mutable handle: IDispatcher<int> option = None

            let disposable (name: string) =
                { new System.IDisposable with
                    member _.Dispose() = disposed.Add name
                }

            // Effects start in the order they were attached (Phase 900;
            // until then in the reverse), so `terminates` (attached first)
            // starts first and `after` would start second.
            Program.mkProgram
                (fun () -> [], Cmd.ofEffect (fun _ -> started.Add "init's command"))
                (fun msg model -> model @ [ msg ], Cmd.none)
                (fun _ _ -> ())
            |> Program.withSubscription (fun _ -> [
                [ "sub" ],
                (fun _ ->
                    started.Add "sub"
                    disposable "sub")
            ])
            |> Program.withDispatcherHandle (fun d -> handle <- Some d)
            |> Program.withEffect (
                EffectHandle.programLifetime "terminates" (fun _ ->
                    started.Add "terminates"
                    handle.Value.Terminate()
                    disposable "terminates")
            )
            |> Program.withEffect (
                EffectHandle.programLifetime "after" (fun _ ->
                    started.Add "after"
                    disposable "after")
            )
            |> Program.runWithDispatch id ()

            Expect.equal
                (List.ofSeq started)
                [ "terminates" ]
                "the effect that terminated ran; no effect, subscription or command started after it"

            Expect.equal
                (List.ofSeq disposed)
                [ "terminates" ]
                "the handle it returned after the teardown had run was disposed at once, not registered"

            Expect.isFalse handle.Value.IsActive "terminated"
        }

        test "a subscription that terminates from its own start function is stopped - run" {
            let started = ResizeArray<string>()
            let disposed = ResizeArray<string>()
            let mutable handle: IDispatcher<int> option = None

            let disposable (name: string) =
                { new System.IDisposable with
                    member _.Dispose() = disposed.Add name
                }

            Program.mkProgram
                (fun () -> [], Cmd.ofEffect (fun _ -> started.Add "init's command"))
                (fun msg model -> model @ [ msg ], Cmd.none)
                (fun _ _ -> ())
            |> Program.withSubscription (fun _ -> [
                [ "terminates" ],
                (fun _ ->
                    started.Add "terminates"
                    handle.Value.Terminate()
                    disposable "terminates")
            ])
            |> Program.withDispatcherHandle (fun d -> handle <- Some d)
            |> Program.runWithDispatch id ()

            Expect.equal (List.ofSeq started) [ "terminates" ] "the subscription ran; init's command did not"

            Expect.equal
                (List.ofSeq disposed)
                [ "terminates" ]
                "the subscription the teardown could not yet see was stopped once its start returned"

            Expect.isFalse handle.Value.IsActive "terminated"
        }

        test "a handler that calls Terminate tears down once, on either route - teardown is not re-entered" {
            // Bounded, so the tree before the phase shows red rather than
            // overflowing the stack: there the handler's `Terminate` found
            // the flag still clear and ran the whole teardown again.
            for viaMessage in [ true; false ] do
                let mutable calls = 0
                let disposed = ResizeArray<string>()
                let mutable handle: IDispatcher<int> option = None

                Program.mkProgram (fun () -> [], Cmd.none) (fun msg model -> model @ [ msg ], Cmd.none) (fun _ _ -> ())
                |> Program.withTermination ((=) 9) (fun _ ->
                    calls <- calls + 1

                    if calls < 5 then
                        handle.Value.Terminate())
                |> Program.withDispatcherHandle (fun d -> handle <- Some d)
                |> Program.withEffect (
                    EffectHandle.programLifetime "effect" (fun _ ->
                        { new System.IDisposable with
                            member _.Dispose() = disposed.Add "effect"
                        })
                )
                |> Program.runWithDispatch id ()

                if viaMessage then
                    handle.Value.Dispatch 9
                else
                    handle.Value.Terminate()

                let route =
                    if viaMessage then
                        "the message route"
                    else
                        "the callback route"

                Expect.equal calls 1 $"the handler ran once ({route})"
                Expect.equal (List.ofSeq disposed) [ "effect" ] $"the effect was disposed once ({route})"
                Expect.isFalse handle.Value.IsActive $"terminated ({route})"
        }

        test "a command's synchronous prefix runs before dispatch returns on .NET, as on Fable - Phase 907" {
            // `Cmd.OfAsync` starts its async body via `AsyncHelpers.start`
            // (Prelude.fs). The boot always paints the untouched init model
            // first (`boot_paints_init_model`); init's command then runs,
            // and - per `reentrant_no_loss` - a command whose body
            // dispatches before it reaches a real await has that dispatch
            // drained into a SECOND paint. What this pins is WHEN that
            // second paint happens. Phase 907 makes the .NET branch use
            // `Async.StartImmediate`, matching Fable's 851.C branch: an
            // async body with no real await runs to completion on the
            // calling thread, so its dispatch - and the second paint it
            // causes - lands inside the SAME call to `runWithDispatch`,
            // before that call returns. Before 907, .NET's `Async.Start`
            // posted the whole body to the thread pool; `runWithDispatch`
            // returned with only the boot paint on record
            // (`renders = [ [] ]`), the second paint arriving - if at all -
            // on another thread after this assertion had already run, and
            // that is the red this test pinned.
            let renders = ResizeArray<string list>()

            let task () = async { return "async-result" }

            let init () = [], Cmd.OfAsync.perform task () id
            let update (msg: string) (model: string list) = model @ [ msg ], Cmd.none

            Program.mkProgram init update (fun _ _ -> ())
            |> Program.withSetState (fun m _ -> renders.Add m)
            |> Program.runWithDispatch id ()

            Expect.equal
                (List.ofSeq renders)
                [ []; [ "async-result" ] ]
                "the async command's synchronous prefix (no real await) must dispatch and drain a second paint before runWithDispatch returns - as it does on Fable"
        }

        // ─── Phase 900 — a message's diff that terminates mid-way ────────
        //
        // One message's model asks for three subscriptions: `before`, which
        // starts normally; `terminates`, which calls `Terminate` from its
        // start function; and `after`. Until Phase 900 `Subs.Fx.change`
        // started every entry of the diff whatever the flag said, the loop
        // assigned all three to the set the teardown had already stopped,
        // nothing ever disposed them, and the message's command ran after.
        // Each of the three faults is its own case, so each was shown red
        // on its own on the tree before the phase (log: w900/red-first-pin).
        // `Started` / `Disposed` are the two lists the run leaves behind.

        let terminatingDiffRun () =
            let started = ResizeArray<string>()
            let disposed = ResizeArray<string>()
            let mutable handle: IDispatcher<int> option = None

            let disposable (name: string) =
                { new System.IDisposable with
                    member _.Dispose() = disposed.Add name
                }

            let subscription (name: string) (start: unit -> unit) : SubId * Subscribe<int> =
                [ name ],
                fun _ ->
                    started.Add name
                    start ()
                    disposable name

            Program.mkProgram
                (fun () -> [], Cmd.none)
                (fun msg model -> model @ [ msg ], Cmd.ofEffect (fun _ -> started.Add $"command {msg}"))
                (fun _ _ -> ())
            |> Program.withSubscription (fun model ->
                if List.contains 1 model then
                    [
                        subscription "before" ignore
                        subscription "terminates" (fun () -> handle.Value.Terminate())
                        subscription "after" ignore
                    ]
                else
                    [])
            |> Program.withDispatcherHandle (fun d -> handle <- Some d)
            |> Program.runWithDispatch id ()

            handle.Value.Dispatch 1
            Expect.isFalse handle.Value.IsActive "the subscription's start terminated the program"
            List.ofSeq started, List.ofSeq disposed

        test
            "a subscription a message's diff starts that terminates stops the rest of the diff starting - Phase 900, run" {
            let started, _ = terminatingDiffRun ()

            Expect.isFalse
                (List.contains "after" started)
                $"the subscription after the one that terminated must not start; started {started}"
        }

        test
            "a subscription a message's diff starts that terminates has what the diff started disposed - Phase 900, run" {
            let _, disposed = terminatingDiffRun ()

            Expect.equal
                disposed
                [ "before"; "terminates" ]
                "what the diff started before and including the terminating subscription is disposed as soon as the diff returns"
        }

        test
            "a subscription a message's diff starts that terminates stops the message's command running - Phase 900, run" {
            let started, _ = terminatingDiffRun ()

            Expect.isFalse
                (List.contains "command 1" started)
                $"the message's command must not run once its diff terminated the program; started {started}"
        }

        test "effects start in the order they were attached, the order effectIds reports - Phase 900, run" {
            // `withEffect` prepends, as the sink registrations do; the boot
            // reverses the sinks on the way out and, since Phase 900, the
            // effects too. Until 900 three effects attached a, b, c started
            // c, b, a — the reverse of what `effectIds` reported and of what
            // a consumer reading its own composition expects. Pinned here
            // as the contract, beside the accessor that already stated it.
            let started = ResizeArray<string>()

            let effect (name: string) =
                EffectHandle.programLifetime name (fun _ ->
                    started.Add name

                    { new System.IDisposable with
                        member _.Dispose() = ()
                    })

            let program =
                Program.mkProgram (fun () -> [], Cmd.none) (fun msg model -> model @ [ msg ], Cmd.none) (fun _ _ -> ())
                |> Program.withEffect (effect "a")
                |> Program.withEffect (effect "b")
                |> Program.withEffect (effect "c")

            Expect.equal (Program.effectIds program) [ "a"; "b"; "c" ] "effectIds reports attach order"

            program |> Program.runWithDispatch id ()

            Expect.equal (List.ofSeq started) [ "a"; "b"; "c" ] "the effects started in attach order"
        }
    ]
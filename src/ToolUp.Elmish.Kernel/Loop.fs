// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish.Kernel

/// What the step machine asks of its host: the callees
/// `proofs/ElmishLoop.fst` abstracts as oracles. The machine owns the ring,
/// the re-entrancy latch, the termination flag, the dirty flag and the
/// model; the host owns everything those callees reach (the update, the
/// subscriptions it holds, the command performer, the render hook, the
/// reporter, the handles it hands out).
[<NoEquality; NoComparison>]
type LoopHost<'msg, 'model> = {
    /// `to_terminate`: does this message end the program? Asked before the
    /// message reaches `Update`; a raise is reported as an update error.
    Terminates: 'msg -> bool
    /// `step`, with the reply's effects performed: hand the message and the
    /// current model to the update, perform what the reply asks for (the
    /// subscription diff's starts, then the command) through the dispatch
    /// given, reading the termination flag given before each start, and
    /// return the next model. A raise is handed to `OnUpdateError` and
    /// leaves the model as it was.
    Update: ('msg -> unit) -> (unit -> bool) -> 'msg -> 'model -> 'model
    /// `paint`'s render hook, handed the dispatch and the model. A dispatch
    /// it makes is queued and drained before the drain ends; a raise is
    /// handed to `OnRenderError`.
    Render: ('msg -> unit) -> 'model -> unit
    /// Report an exception raised while handling a message. Must return.
    OnUpdateError: 'msg -> exn -> unit
    /// Report an exception raised by the render hook. Must return.
    OnRenderError: exn -> unit
    /// The teardown's callees, run once, after the termination flag is set,
    /// with the model as it stands: stop what the host holds and mark its
    /// handles inactive. Must return; a dispatch it makes is refused.
    Teardown: 'model -> unit
}

/// The dispatch loop's step machine — `proofs/ElmishLoop.fst` clause for
/// clause: the ring (`RingBuffer`), the re-entrancy latch, the termination
/// flag, the dirty flag and the model, the drain (`process_msgs` / `loop`),
/// the paint, the one teardown, and the boot with its drain.
///
/// The render hook runs once at the END of each drain with the model the
/// drain ended on, not after every update: the drain runs while there is a
/// message to process OR a model to paint, so the model on screen is the
/// model whenever the loop is idle (`painted_is_model`), and a dispatch from
/// idle paints exactly once when the hook dispatches nothing
/// (`render_once_per_drain`). A dispatch made while the latch is set queues
/// its message behind what is pending and processes nothing
/// (`reentrant_no_loss`). Termination is absorbing (`terminated_absorbing`):
/// the flag is set before the host's teardown runs, so nothing that teardown
/// does reaches the update again, and a terminated program paints nothing.
///
/// `syncDispatch` shapes the dispatch the machine hands out (to the update's
/// effects, the render hook and the host) from the one it runs.
[<Sealed>]
type Loop<'msg, 'model>
    (capacity: int, model: 'model, syncDispatch: ('msg -> unit) -> 'msg -> unit, host: LoopHost<'msg, 'model>) =
    let rb = RingBuffer<'msg> capacity
    let mutable reentered = false
    let mutable state = model
    let mutable terminated = false
    // The model has moved since the hook last saw it. Set by every message
    // handed to the update and cleared by the paint; the init model is
    // unpainted until the boot paints it.
    let mutable dirty = true

    let isTerminated () = terminated

    // `terminate` — the ONE teardown, run by a message `Terminates` takes and
    // by `Terminate` alike. The flag is set FIRST, so nothing the host's
    // callees do can re-enter it or reach the update.
    let teardown () =
        if not terminated then
            terminated <- true
            host.Teardown state

    let rec dispatch msg =
        if not terminated then
            rb.Push msg

            if not reentered then
                reentered <- true
                processMsgs ()
                reentered <- false

    and dispatch' = syncDispatch dispatch

    // `paint`. The flag is cleared BEFORE the hook runs, so a dispatch the
    // hook makes re-dirties the model (and the drain paints again once the
    // ring is empty) rather than being lost or painted twice.
    and paint () =
        dirty <- false

        try
            host.Render dispatch' state
        with ex ->
            host.OnRenderError ex

    // `process_msgs` / `loop`. The `None` arm is the paint on an empty ring,
    // followed by one more pop because the hook may have dispatched. A
    // terminating message exits without painting.
    and processMsgs () =
        let mutable nextMsg = rb.Pop()

        while not terminated && (Option.isSome nextMsg || dirty) do
            match nextMsg with
            | None ->
                paint ()
                nextMsg <- rb.Pop()
            | Some msg ->
                try
                    if host.Terminates msg then
                        teardown ()
                    else
                        // Dirty BEFORE the update runs: the model's `step`
                        // marks every message it hands the update, and a
                        // raise is, to the model, a reply carrying the old
                        // model — painted once at the end of the drain.
                        dirty <- true
                        state <- host.Update dispatch' isTerminated msg state
                with ex ->
                    host.OnUpdateError msg ex

                nextMsg <- rb.Pop()

    /// The dispatch to hand out: `syncDispatch` over the machine's own. A
    /// message dispatched once the program is terminated is dropped.
    member _.Dispatch: 'msg -> unit = dispatch'

    /// Whether the program has terminated.
    member _.IsTerminated = terminated

    /// Run the one teardown. Idempotent: it does nothing once the program is
    /// terminated.
    member _.Terminate() = teardown ()

    /// `boot`. The latch is set BEFORE anything the host supplied runs
    /// (`preboot`), so a dispatch any of it makes only queues its message.
    /// Then the `sinks` run, ungated (they hand out handles and start
    /// nothing); each of the `effects` runs only while the program is not
    /// terminated (`gated`); the init model is painted (`boot_paint`), before
    /// anything is drained; each of the `starts` (the init model's
    /// subscriptions, then init's command) runs only while the program is not
    /// terminated; the boot drain hands every queued message to the update,
    /// in the order it was raised; the latch is cleared.
    member _.Boot(sinks: (unit -> unit) list, effects: (unit -> unit) list, starts: (unit -> unit) list) =
        reentered <- true

        for sink in sinks do
            sink ()

        for effect in effects do
            if not terminated then
                effect ()

        paint ()

        for start in starts do
            if not terminated then
                start ()

        processMsgs ()
        reentered <- false
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish

open System
open System.Collections.Generic
open ToolUp.Elmish.Kernel

/// `Program` — captures the runtime behaviour of an Elmish loop. Preserved
/// from upstream Elmish v5.x at the existing field shape; new fields added
/// for the ToolUp-specific primitives (dispatcher handle, structured error
/// reporter, lifetime-aware effects, configurable ring-buffer capacity).
/// The record stays `private` — consumers construct via `mkProgram` /
/// `mkSimple` and decorate via the `with*` functions.
type Program<'arg, 'model, 'msg, 'view> = private {
    init: 'arg -> 'model * Cmd<'msg>
    update: 'msg -> 'model -> 'model * Cmd<'msg>
    subscribe: 'model -> Sub<'msg>
    view: 'model -> Dispatch<'msg> -> 'view
    setState: 'model -> Dispatch<'msg> -> unit
    onError: (string * exn) -> unit
    errorReporter: ErrorContext -> unit
    termination: ('msg -> bool) * ('model -> unit)
    ringBufferCapacity: int
    /// Each `withDispatcherHandle` call appends a sink. The runtime
    /// invokes every sink once at program-start with the same
    /// `IDispatcher` instance, so layered composers (HMR + React +
    /// consumer) can each capture the handle without clobbering
    /// each other's slot — a real bug under the prior `option`
    /// shape where the last `withDispatcherHandle` won.
    dispatcherHandleSinks: (IDispatcher<'msg> -> unit) list
    /// Same fan-out shape for the typed effect-registry handle —
    /// HMR + consumer can both capture it without clobbering.
    effectControllerHandleSinks: (IEffectController -> unit) list
    effects: EffectHandle<'msg> list
}

/// `Program` module — construct, decorate, and run programs.
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Program =

    /// Format a message for diagnostics without ever crashing the dispatch
    /// loop. A message can legitimately carry a large payload (e.g. an uploaded
    /// file's raw contents); `sprintf "%A"` on multi-MB data can blow the JS
    /// call stack ("Maximum call stack size exceeded"). That overflow must not
    /// propagate through the error reporter — which would re-format the same
    /// message and overflow again, recursing through the reporter itself.
    /// Total + bounded: catches the overflow and caps the output length.
    let safeMsgRepr (msg: 'msg) : string =
        try
            let s = sprintf "%A" msg

            if s.Length > 1000 then
                s.Substring(0, 1000) + "…(truncated)"
            else
                s
        with _ ->
            "<message could not be formatted (large or cyclic payload)>"

    /// Default error reporter — routes to the platform console / trace.
    /// Replaced by `withErrorReporter`.
    let private defaultErrorReporter (ctx: ErrorContext) =
        Log.onError (ctx.Message, ctx.Exception)

    /// Default upstream-shape onError. The runtime uses `errorReporter`
    /// internally; this is the seed `withErrorReporter` replaces with its
    /// upstream-shape shim.
    let private defaultOnError = Log.onError

    /// Typical program — `init` and `update` produce commands alongside state.
    let mkProgram
        (init: 'arg -> 'model * Cmd<'msg>)
        (update: 'msg -> 'model -> 'model * Cmd<'msg>)
        (view: 'model -> Dispatch<'msg> -> 'view)
        =
        {
            init = init
            update = update
            view = view
            setState = fun model -> view model >> ignore
            subscribe = fun _ -> Sub.none
            onError = defaultOnError
            errorReporter = defaultErrorReporter
            termination = (fun _ -> false), ignore
            ringBufferCapacity = 10
            dispatcherHandleSinks = []
            effectControllerHandleSinks = []
            effects = []
        }

    /// Simple program — `init` and `update` produce only new state.
    let mkSimple (init: 'arg -> 'model) (update: 'msg -> 'model -> 'model) (view: 'model -> Dispatch<'msg> -> 'view) = {
        init = init >> fun state -> state, Cmd.none
        update = fun msg -> update msg >> fun state -> state, Cmd.none
        view = view
        setState = fun model -> view model >> ignore
        subscribe = fun _ -> Sub.none
        onError = defaultOnError
        errorReporter = defaultErrorReporter
        termination = (fun _ -> false), ignore
        ringBufferCapacity = 10
        dispatcherHandleSinks = []
        effectControllerHandleSinks = []
        effects = []
    }

    /// Subscribe to external sources of events. Returns the subscriptions
    /// that should be active for the current model; the runtime starts /
    /// stops to match. Preserved from upstream — for the simpler one-shot
    /// case use `withEffect` instead.
    let withSubscription (subscribe: 'model -> Sub<'msg>) (program: Program<'arg, 'model, 'msg, 'view>) = {
        program with
            subscribe = subscribe
    }

    /// Map the existing subscription generator.
    let mapSubscription map (program: Program<'arg, 'model, 'msg, 'view>) = {
        program with
            subscribe = map program.subscribe
    }

    /// Register a lifetime-aware one-shot effect (SSE listener, notification
    /// stream, browser event hook). The runtime tracks the effect by
    /// `Id` and disposes it according to its `Lifetime`. Multiple calls
    /// append to the effect list.
    let withEffect (effect: EffectHandle<'msg>) (program: Program<'arg, 'model, 'msg, 'view>) = {
        program with
            effects = effect :: program.effects
    }

    /// The ids of every registered lifetime-aware effect, in attach order.
    /// Read-only diagnostic accessor (the record itself stays private) —
    /// lets a composition test pin that an outer composer re-attached the
    /// inner program's effects rather than silently dropping them, which
    /// is otherwise invisible until a bus fires with no subscriber.
    let effectIds (program: Program<'arg, 'model, 'msg, 'view>) : string list =
        program.effects |> List.rev |> List.map _.Id

    /// Trace messages as they update the model and subscriptions. The
    /// callback receives `(msg, newState, activeSubIds)`. Preserved from
    /// upstream as an opt-in tracing hook, and the per-message
    /// interceptor that replaced the upstream-shape `withConsoleTrace`
    /// shim: log bounded string reprs via
    /// `safeMsgRepr`, never the live msg/model object — a message can
    /// carry a multi-MB payload, which `%A`-formats to a stack overflow on
    /// the .NET sink and pins memory in the Fable devtools sink.
    let withTrace trace (program: Program<'arg, 'model, 'msg, 'view>) =
        let update msg model =
            let state, cmd = program.update msg model
            let subIds = program.subscribe state |> List.map fst
            trace msg state subIds
            state, cmd

        { program with update = update }

    /// Structured error reporter — receives the full `ErrorContext`
    /// including `Phase`, optional `ModuleId`, optional `CorrelationId`,
    /// the message-string the upstream `onError` would have passed, and
    /// the raw exception. The one error hook, since the upstream-shape
    /// `withErrorHandler` shim was removed: an upstream
    /// `string * exn -> unit` callback composes as
    /// `withErrorReporter (fun ctx -> onError (ctx.Message, ctx.Exception))`.
    let withErrorReporter (reporter: ErrorContext -> unit) (program: Program<'arg, 'model, 'msg, 'view>) =
        let upstreamShim (text: string, ex: exn) =
            reporter (ErrorContext.ofUpstreamShape text ex)

        {
            program with
                onError = upstreamShim
                errorReporter = reporter
        }

    /// Capture the runtime's typed dispatch handle. The callback fires
    /// once, right after `init` runs and before the first `update`. The
    /// resulting `IDispatcher` flips `IsActive = false` when `withTermination`
    /// triggers (or `IDispatcher.Terminate()` is called explicitly), so
    /// background callbacks can no-op cleanly.
    ///
    /// Multiple `withDispatcherHandle` calls append; each registered sink
    /// is invoked once at program-start with the same handle. The HMR
    /// adapter and the React adapter both compose their own sinks, so
    /// the consumer's `withDispatcherHandle` is no longer at risk of
    /// being clobbered by a later renderer-side call.
    let withDispatcherHandle (onReady: IDispatcher<'msg> -> unit) (program: Program<'arg, 'model, 'msg, 'view>) = {
        program with
            dispatcherHandleSinks = onReady :: program.dispatcherHandleSinks
    }

    /// Capture the runtime's typed effect-registry handle. The callback
    /// fires once at program-start; the resulting `IEffectController`
    /// can dispose effects by `Id` (`EffectLifetime.Manual`) or by
    /// `ModuleId` (`EffectLifetime.Module moduleId`) without tearing
    /// the program down. Multiple registrations append in the same way
    /// `withDispatcherHandle` does.
    let withEffectControllerHandle (onReady: IEffectController -> unit) (program: Program<'arg, 'model, 'msg, 'view>) = {
        program with
            effectControllerHandleSinks = onReady :: program.effectControllerHandleSinks
    }

    /// Override the message ring-buffer capacity. Default 10 (matches
    /// upstream). Capacity auto-grows on overflow, so this is purely an
    /// optimisation hint for apps whose `update` synchronously dispatches
    /// many follow-up messages from a single handler. Floored at
    /// `RingBuffer.MinimumCapacity` (2) — the same floor the constructor
    /// applies and the one the ring theorems' precondition needs.
    let withRingBufferCapacity (capacity: int) (program: Program<'arg, 'model, 'msg, 'view>) = {
        program with
            ringBufferCapacity = max RingBuffer<'msg>.MinimumCapacity capacity
    }

    /// Return the configured ring-buffer capacity.
    let ringBufferCapacity (program: Program<'arg, 'model, 'msg, 'view>) = program.ringBufferCapacity

    /// Termination criteria and handler. Override the predicate to stop
    /// the program on a specific message; the cleanup runs after the loop
    /// halts.
    let withTermination
        (predicate: 'msg -> bool)
        (terminate: 'model -> unit)
        (program: Program<'arg, 'model, 'msg, 'view>)
        =
        {
            program with
                termination = predicate, terminate
        }

    /// Map the termination tuple.
    let mapTermination map (program: Program<'arg, 'model, 'msg, 'view>) = {
        program with
            termination = map program.termination
    }

    /// Map the upstream-shape error handler. Preserved for source-compat.
    let mapErrorHandler map (program: Program<'arg, 'model, 'msg, 'view>) = {
        program with
            onError = map program.onError
    }

    /// Return the current upstream-shape error handler.
    let onError (program: Program<'arg, 'model, 'msg, 'view>) = program.onError

    /// Return the current structured error reporter.
    let errorReporter (program: Program<'arg, 'model, 'msg, 'view>) = program.errorReporter

    /// Override the view-driving setState callback. Used by the React
    /// renderer to wire React's render call into the dispatch loop.
    let withSetState (setState: 'model -> Dispatch<'msg> -> unit) (program: Program<'arg, 'model, 'msg, 'view>) = {
        program with
            setState = setState
    }

    /// Return the current setState callback.
    let setState (program: Program<'arg, 'model, 'msg, 'view>) = program.setState

    /// Sets the view to one that receives a set of dispatch wrappers built
    /// ONCE per loop, instead of the raw `dispatch`. `build` turns the loop's
    /// dispatcher into whatever the view hands out — a message lifted into a
    /// module's route, a sidebar's callbacks — and runs again only when a
    /// different loop renders (a hot reload re-runs the program), because the
    /// loop's dispatcher is one function for the loop's life. So a memo
    /// boundary keyed on those callbacks holds across renders, and no cache
    /// outside the program keys on `dispatch`.
    let withStableDispatch
        (build: Dispatch<'msg> -> 'stable)
        (view: 'model -> 'stable -> 'view)
        (program: Program<'arg, 'model, 'msg, 'view>)
        : Program<'arg, 'model, 'msg, 'view> =
        let stableFor = Memo.byRef build

        {
            program with
                view = fun model dispatch -> view model (stableFor dispatch)
        }

    /// Return the view function.
    let view (program: Program<'arg, 'model, 'msg, 'view>) = program.view

    /// Return the init function.
    let init (program: Program<'arg, 'model, 'msg, 'view>) = program.init

    /// Return the update function.
    let update (program: Program<'arg, 'model, 'msg, 'view>) = program.update

    /// Map the program record. Preserves the ToolUp-specific fields
    /// (errorReporter, ringBufferCapacity, dispatcherHandleSinks,
    /// effectControllerHandleSinks, effects) verbatim — only the
    /// upstream fields are passed through caller-supplied map functions.
    let map
        mapInit
        mapUpdate
        mapView
        mapSetState
        mapSubscribe
        mapTermination
        (program: Program<'arg, 'model, 'msg, 'view>)
        =
        {
            init = mapInit program.init
            update = mapUpdate program.update
            view = mapView program.view
            setState = mapSetState program.setState
            subscribe = mapSubscribe program.subscribe
            onError = program.onError
            errorReporter = program.errorReporter
            termination = mapTermination program.termination
            ringBufferCapacity = program.ringBufferCapacity
            dispatcherHandleSinks = program.dispatcherHandleSinks
            effectControllerHandleSinks = program.effectControllerHandleSinks
            effects = program.effects
        }

    module Subs = Subscriptions

    /// Internal effect-registry. Tracks live `EffectHandle` instances by
    /// id so HMR and module-reset can dispose them on lifetime boundaries.
    type private EffectRegistry<'msg>() =
        let live = Dictionary<string, EffectLifetime * IDisposable>()

        /// `terminated` is read AFTER the start function returns: a start
        /// that terminated the program (through `IDispatcher.Terminate`) ran
        /// the teardown while it was still running, so `DisposeAll` has
        /// already emptied the registry and nothing will ever dispose what
        /// the start returned. It is disposed here instead of registered
        /// (`gated` in `proofs/ElmishLoop.fst`).
        member _.Register
            (handle: EffectHandle<'msg>)
            (dispatch: Dispatch<'msg>)
            (reportError: exn -> unit)
            (terminated: unit -> bool)
            =
            // Dispose any prior handle with the same id (HMR re-registration).
            match live.TryGetValue handle.Id with
            | true, (_, disposable) ->
                try
                    disposable.Dispose()
                with ex ->
                    reportError ex

                live.Remove handle.Id |> ignore
            | _ -> ()

            try
                let disposable = handle.Start dispatch

                if terminated () then
                    disposable.Dispose()
                else
                    live.[handle.Id] <- (handle.Lifetime, disposable)
            with ex ->
                reportError ex

        member _.DisposeByModule (moduleId: string) (reportError: exn -> unit) =
            let toRemove =
                live
                |> Seq.choose (fun kv ->
                    match fst kv.Value with
                    | EffectLifetime.Module mId when mId = moduleId -> Some kv.Key
                    | _ -> None)
                |> Seq.toList

            for id in toRemove do
                let _, disposable = live.[id]

                try
                    disposable.Dispose()
                with ex ->
                    reportError ex

                live.Remove id |> ignore

        member _.DisposeById (id: string) (reportError: exn -> unit) =
            match live.TryGetValue id with
            | true, (_, disposable) ->
                try
                    disposable.Dispose()
                with ex ->
                    reportError ex

                live.Remove id |> ignore
            | _ -> ()

        member _.DisposeAll(reportError: exn -> unit) =
            for kv in live do
                let _, disposable = kv.Value

                try
                    disposable.Dispose()
                with ex ->
                    reportError ex

            live.Clear()

    /// Start the program loop with a custom `syncDispatch` shape.
    /// The skeleton — the ring, the re-entrancy latch, the termination
    /// flag, the drain, the paint once per drain and the boot — is the
    /// kernel's step machine (`ToolUp.Elmish.Kernel.Loop`), which
    /// `proofs/ElmishLoop.fst` models clause for clause and
    /// `ElmishLoopProofOracleTests` holds this function to. What is here
    /// is the host the machine runs over: the update with its subscription
    /// diff and command, the render hook (`setState`), the teardown's
    /// callees, the reporter, the dispatcher handle and the effect
    /// registry. `withTrace` still sees every message (it wraps `update`).
    ///
    /// Two things the model takes for granted are made true here by
    /// construction. The error reporter RETURNS: every call to the
    /// program's reporter goes through `guarded`, because a reporter
    /// that raised used to escape the drain with the latch still set,
    /// after which every dispatch queued its message and nothing was
    /// processed again. And the teardown's callees each run under their
    /// own guard, with `MarkTerminated` in a `finally`, so a subscription,
    /// an effect or a terminate handler that raises is reported and the
    /// program still ends inactive (`terminated_holds_nothing`,
    /// `terminated_starts_nothing`).
    let runWithDispatch
        (syncDispatch: Dispatch<'msg> -> Dispatch<'msg>)
        (arg: 'arg)
        (program: Program<'arg, 'model, 'msg, 'view>)
        =
        let dispatcherCore = DispatcherCore<'msg>()
        let effectRegistry = EffectRegistry<'msg>()

        // The reporter, made total. `text` and `ex` are what was being
        // reported; if the reporter raises, both failures go to the
        // console — the original, which would otherwise be lost, and the
        // reporter's own. The console write is the last resort and is
        // itself guarded: there is nowhere left to report to.
        let guarded (text: string) (ex: exn) (report: unit -> unit) =
            try
                report ()
            with reporterEx ->
                try
                    Log.onError ("The error reporter raised while reporting: " + text, reporterEx)
                    Log.onError (text, ex)
                with _ ->
                    ()

        let reportException (phase: ErrorPhase) (text: string) (ex: exn) =
            guarded text ex (fun () ->
                program.errorReporter {
                    Phase = phase
                    ModuleId = None
                    CorrelationId = None
                    Message = text
                    Exception = ex
                })

        let reportRaw (ex: exn) =
            reportException (ErrorPhase.Update(box ())) "Background callback raised" ex

        // The upstream-shape hook the subscription effects report through
        // (`withErrorReporter` points it at the same reporter), guarded
        // the same way.
        let onError (text: string, ex: exn) =
            guarded text ex (fun () -> program.onError (text, ex))

        let model, cmd = program.init arg
        let sub = program.subscribe model
        let toTerminate, terminate = program.termination
        let mutable activeSubs = Subs.empty

        let host: LoopHost<'msg, 'model> = {
            Terminates = toTerminate
            // F*: `step`. The diff is gated between its starts: a
            // subscription that terminates the program from its start
            // function stops the rest of the diff starting, and
            // `change` returns the empty set having stopped what it
            // started (`start_all`). The message's command runs only
            // while the program is still running after its diff.
            Update =
                fun dispatch isTerminated msg state ->
                    let model', cmd' = program.update msg state
                    let sub' = program.subscribe model'

                    activeSubs <- Subs.diff activeSubs sub' |> Subs.Fx.change onError dispatch isTerminated

                    if not (isTerminated ()) then
                        cmd'
                        |> Cmd.exec
                            (fun ex ->
                                reportException
                                    (ErrorPhase.Update(box msg))
                                    ("Error handling the message: " + safeMsgRepr msg)
                                    ex)
                            dispatch

                    model'
            Render = fun dispatch state -> program.setState state dispatch
            OnUpdateError =
                fun msg ex ->
                    reportException
                        (ErrorPhase.Update(box msg))
                        ("Unable to process the message: " + safeMsgRepr msg)
                        ex
            OnRenderError = fun ex -> reportException ErrorPhase.View "Error rendering the model" ex
            // F*: `terminate` — the ONE teardown, run by a message the
            // termination predicate takes and by `IDispatcher.Terminate`
            // alike. The machine has set its flag before this runs, so
            // nothing the callees do can re-enter it or reach `update`.
            // Each callee runs under its own guard, and `MarkTerminated`
            // is in a `finally`, so `IsActive` goes false whatever
            // happened above it — and still goes false LAST, so a
            // handler that reads `IsActive` sees what it always saw.
            Teardown =
                fun state ->
                    try
                        try
                            Subs.Fx.stop onError activeSubs
                        with ex ->
                            reportRaw ex

                        try
                            effectRegistry.DisposeAll reportRaw
                        with ex ->
                            reportRaw ex

                        try
                            terminate state
                        with ex ->
                            reportException ErrorPhase.Termination "The terminate handler raised" ex
                    finally
                        dispatcherCore.MarkTerminated()
        }

        let loop = Loop(program.ringBufferCapacity, model, syncDispatch, host)

        let dispatch' = loop.Dispatch

        // Read by `Subs.Fx.change` between the subscriptions one diff
        // starts, and by `EffectRegistry.Register` after an effect's start
        // returns: a start that calls `Terminate` ran the teardown while it
        // was still running, and nothing started after it would ever be
        // stopped.
        let isTerminated () = loop.IsTerminated

        // Wire the dispatcher core BEFORE init's cmds run so background
        // callbacks captured during init can dispatch safely.
        dispatcherCore.Wire dispatch'

        // `IDispatcher.Terminate()` runs the machine's one teardown — the
        // same one the termination predicate does. Idempotent.
        dispatcherCore.SetTerminateCallback loop.Terminate

        let dispatcher = dispatcherCore.AsInterface reportRaw

        let effectController =
            { new IEffectController with
                member _.DisposeById id = effectRegistry.DisposeById id reportRaw

                member _.DisposeByModule moduleId =
                    effectRegistry.DisposeByModule moduleId reportRaw
            }

        // F*: `boot`. The machine sets the latch BEFORE anything below is
        // called, so a sink or an effect's start function that dispatches
        // synchronously only queues its message (`reentrant_no_loss`); the
        // boot drain hands it to `update` after the boot paint, ahead of
        // `init`'s command's messages and in the order it was raised. The
        // boot paint runs before `init`'s command: a hydrating renderer
        // (`withReactHydrate`) must be handed the model the server rendered.
        //
        // The sinks fan out in declaration order (they were prepended, so
        // reverse here), each in its own try so a failing sink doesn't
        // suppress the next; they are handed handles and start nothing, so
        // they are not gated. The effects start in ATTACH order (`withEffect`
        // prepends too), each only while the program is not terminated
        // (`gated`), and a start that terminates the program has what it
        // returned released at once (`Register`'s check). See
        // `docs/migrations/900-effect-start-order.md`.
        let sinks = [
            for sink in List.rev program.dispatcherHandleSinks ->
                fun () ->
                    try
                        sink dispatcher
                    with ex ->
                        reportException ErrorPhase.Init "DispatcherHandle sink raised" ex
            for sink in List.rev program.effectControllerHandleSinks ->
                fun () ->
                    try
                        sink effectController
                    with ex ->
                        reportException ErrorPhase.Init "EffectControllerHandle sink raised" ex
        ]

        let effects = [
            for effect in List.rev program.effects ->
                fun () -> effectRegistry.Register effect dispatch' reportRaw isTerminated
        ]

        // Each gated: the init model's subscriptions, then init's command.
        // `activeSubs` is empty when the first runs, so what `change` returns
        // is exactly what it started — and if one of those starts terminated
        // the program, `change` itself stopped what it had started and
        // returns nothing.
        let starts = [
            fun () -> activeSubs <- Subs.diff activeSubs sub |> Subs.Fx.change onError dispatch' isTerminated
            fun () ->
                cmd
                |> Cmd.exec (fun ex -> reportException ErrorPhase.Init "Error initialising" ex) dispatch'
        ]

        loop.Boot(sinks, effects, starts)

    /// Start the single-threaded dispatch loop.
    let runWith (arg: 'arg) (program: Program<'arg, 'model, 'msg, 'view>) = runWithDispatch id arg program

    /// Start the dispatch loop with `unit` for the init function.
    let run (program: Program<unit, 'model, 'msg, 'view>) = runWith () program
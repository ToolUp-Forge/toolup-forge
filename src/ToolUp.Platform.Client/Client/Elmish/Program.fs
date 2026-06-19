// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish

open System
open System.Collections.Generic

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
    /// internally; this exists so `withErrorHandler` can keep working.
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

    /// Trace every message and model transition to the platform console.
    /// Kept as an `[<Obsolete>]` shim for source-compat with consumers
    /// migrating from upstream Elmish; prefer composing
    /// `Program.withErrorReporter` with a per-update interceptor for
    /// structured / filterable observability.
    [<System.Obsolete("Use Program.withErrorReporter + an update interceptor for structured tracing. withConsoleTrace will be removed in a future major.")>]
    let withConsoleTrace (program: Program<'arg, 'model, 'msg, 'view>) =
        let traceInit (arg: 'arg) =
            let initModel, cmd = program.init arg
            Log.toConsole ("Initial state:", initModel)
            initModel, cmd

        let traceUpdate msg model =
            Log.toConsole ("New message:", msg)
            let newModel, cmd = program.update msg model
            Log.toConsole ("Updated state:", newModel)
            newModel, cmd

        let traceSubscribe model =
            let sub = program.subscribe model
            Log.toConsole ("Updated subs:", sub |> List.map fst)
            sub

        {
            program with
                init = traceInit
                update = traceUpdate
                subscribe = traceSubscribe
        }

    /// Trace messages as they update the model and subscriptions. The
    /// callback receives `(msg, newState, activeSubIds)`. Preserved from
    /// upstream as an opt-in tracing hook — for structured per-message
    /// observability prefer `withErrorReporter` + a middleware
    /// interceptor when one ships.
    let withTrace trace (program: Program<'arg, 'model, 'msg, 'view>) =
        let update msg model =
            let state, cmd = program.update msg model
            let subIds = program.subscribe state |> List.map fst
            trace msg state subIds
            state, cmd

        { program with update = update }

    /// Upstream-shape error handler. Kept for source-compat with code
    /// migrated from `Fable.Elmish`; new code should prefer
    /// `withErrorReporter` to access the structured `ErrorContext`.
    [<System.Obsolete("Use Program.withErrorReporter for the structured ErrorContext shape (Phase / ModuleId / CorrelationId / Message / Exception). withErrorHandler will be removed in a future major.")>]
    let withErrorHandler (onError: string * exn -> unit) (program: Program<'arg, 'model, 'msg, 'view>) =
        let reporter (ctx: ErrorContext) = onError (ctx.Message, ctx.Exception)

        {
            program with
                onError = onError
                errorReporter = reporter
        }

    /// Structured error reporter — receives the full `ErrorContext`
    /// including `Phase`, optional `ModuleId`, optional `CorrelationId`,
    /// the message-string the upstream `onError` would have passed, and
    /// the raw exception. Replaces `withErrorHandler` for new code.
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
    /// upstream). Capacity auto-doubles on overflow, so this is purely an
    /// optimisation hint for apps whose `update` synchronously dispatches
    /// many follow-up messages from a single handler.
    let withRingBufferCapacity (capacity: int) (program: Program<'arg, 'model, 'msg, 'view>) = {
        program with
            ringBufferCapacity = max 1 capacity
    }

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

    module Subs = Sub.Internal

    /// Internal effect-registry. Tracks live `EffectHandle` instances by
    /// id so HMR and module-reset can dispose them on lifetime boundaries.
    type private EffectRegistry<'msg>() =
        let live = Dictionary<string, EffectLifetime * IDisposable>()

        member _.Register (handle: EffectHandle<'msg>) (dispatch: Dispatch<'msg>) (reportError: exn -> unit) =
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
    /// Preserved verbatim from upstream; the new primitives plumb through
    /// the existing dispatch path and are invoked alongside the upstream
    /// happy path so the runtime shape is unchanged for migrators.
    let runWithDispatch
        (syncDispatch: Dispatch<'msg> -> Dispatch<'msg>)
        (arg: 'arg)
        (program: Program<'arg, 'model, 'msg, 'view>)
        =
        let dispatcherCore = DispatcherCore<'msg>()
        let effectRegistry = EffectRegistry<'msg>()

        let reportException (phase: ErrorPhase) (text: string) (ex: exn) =
            program.errorReporter {
                Phase = phase
                ModuleId = None
                CorrelationId = None
                Message = text
                Exception = ex
            }

        let reportRaw (ex: exn) =
            program.errorReporter {
                Phase = ErrorPhase.Update(box ())
                ModuleId = None
                CorrelationId = None
                Message = "Background callback raised"
                Exception = ex
            }

        let model, cmd = program.init arg
        let sub = program.subscribe model
        let toTerminate, terminate = program.termination
        let rb = RingBuffer program.ringBufferCapacity
        let mutable reentered = false
        let mutable state = model
        let mutable activeSubs = Subs.empty
        let mutable terminated = false

        let rec dispatch msg =
            if not terminated then
                rb.Push msg

                if not reentered then
                    reentered <- true
                    processMsgs ()
                    reentered <- false

        and dispatch' = syncDispatch dispatch

        and processMsgs () =
            let mutable nextMsg = rb.Pop()

            while not terminated && Option.isSome nextMsg do
                let msg = nextMsg.Value

                try
                    if toTerminate msg then
                        Subs.Fx.stop program.onError activeSubs
                        effectRegistry.DisposeAll reportRaw
                        terminate state
                        terminated <- true
                        dispatcherCore.MarkTerminated()
                    else
                        let model', cmd' = program.update msg state
                        let sub' = program.subscribe model'
                        program.setState model' dispatch'

                        activeSubs <- Subs.diff activeSubs sub' |> Subs.Fx.change program.onError dispatch'

                        cmd'
                        |> Cmd.exec
                            (fun ex ->
                                reportException
                                    (ErrorPhase.Update(box msg))
                                    ("Error handling the message: " + safeMsgRepr msg)
                                    ex)
                            dispatch'

                        state <- model'
                with ex ->
                    reportException
                        (ErrorPhase.Update(box msg))
                        ("Unable to process the message: " + safeMsgRepr msg)
                        ex

                nextMsg <- rb.Pop()

        // Wire the dispatcher core BEFORE init's cmds run so background
        // callbacks captured during init can dispatch safely.
        dispatcherCore.Wire dispatch'

        // Wire the terminate callback so `IDispatcher.Terminate()` can
        // trigger the same teardown path the termination-message
        // predicate does (subs stopped + effects disposed + IsActive flipped).
        // Idempotent: a second call to `Terminate` is a no-op because the
        // first call set `terminated <- true`.
        dispatcherCore.SetTerminateCallback(fun () ->
            if not terminated then
                try
                    Subs.Fx.stop program.onError activeSubs
                with ex ->
                    reportRaw ex

                effectRegistry.DisposeAll reportRaw

                try
                    terminate state
                with ex ->
                    reportRaw ex

                terminated <- true
                dispatcherCore.MarkTerminated())

        let dispatcher = dispatcherCore.AsInterface reportRaw

        let effectController =
            { new IEffectController with
                member _.DisposeById id = effectRegistry.DisposeById id reportRaw

                member _.DisposeByModule moduleId =
                    effectRegistry.DisposeByModule moduleId reportRaw
            }

        // Fan out to every registered sink (in declaration order — sinks
        // were prepended so reverse here). Each sink runs in its own try
        // so a failing sink doesn't suppress the next.
        for sink in List.rev program.dispatcherHandleSinks do
            try
                sink dispatcher
            with ex ->
                reportException ErrorPhase.Init "DispatcherHandle sink raised" ex

        for sink in List.rev program.effectControllerHandleSinks do
            try
                sink effectController
            with ex ->
                reportException ErrorPhase.Init "EffectControllerHandle sink raised" ex

        // Register all configured effects.
        for effect in program.effects do
            effectRegistry.Register effect dispatch' reportRaw

        reentered <- true
        program.setState model dispatch'

        activeSubs <- Subs.diff activeSubs sub |> Subs.Fx.change program.onError dispatch'

        cmd
        |> Cmd.exec (fun ex -> reportException ErrorPhase.Init "Error initialising" ex) dispatch'

        processMsgs ()
        reentered <- false

    /// Start the single-threaded dispatch loop.
    let runWith (arg: 'arg) (program: Program<'arg, 'model, 'msg, 'view>) = runWithDispatch id arg program

    /// Start the dispatch loop with `unit` for the init function.
    let run (program: Program<unit, 'model, 'msg, 'view>) = runWith () program
namespace Elmish

open System

/// Typed out-of-band dispatch handle. The runtime captures one of these
/// per `Program.run` invocation; the consumer receives it via
/// `Program.withDispatcherHandle` and uses it from background callbacks
/// (SSE reconnect timers, notification listeners, cross-cutting subjects)
/// that can't reach the Elmish loop's `dispatch` argument directly.
///
/// Replaces the `let mutable shellDispatch : (Msg -> unit) option = None`
/// pattern that every non-trivial Elmish app re-invents. The `IsActive`
/// flag is the load-bearing addition — background callbacks fired after
/// `Program.withTermination` triggers can check it and no-op cleanly,
/// rather than dispatching against a torn-down loop and getting an
/// observability-blind crash on the next render.
type IDispatcher<'msg> =
    /// Dispatch a message synchronously into the program's message queue.
    /// Safe to call from any thread / Fable execution context. A no-op
    /// if `IsActive = false`.
    abstract Dispatch: 'msg -> unit

    /// Dispatch a message asynchronously — the async block is started on
    /// the current execution context, and the resulting message is
    /// dispatched via `Dispatch` when the block completes. Exceptions in
    /// the block are routed to the program's `ErrorReporter`.
    abstract DispatchAsync: Async<'msg> -> unit

    /// `true` while the underlying program is running; `false` after
    /// `Program.withTermination`'s predicate matches, after `Terminate`
    /// is called explicitly, or the program otherwise tears down.
    /// Background callbacks should always check this before calling
    /// `Dispatch` to avoid spraying messages at a dead loop.
    abstract IsActive: bool

    /// Tear down the program. Flips `IsActive` to `false`, disposes every
    /// registered `EffectHandle` regardless of declared lifetime, and
    /// stops the active subscription set. Used by `ToolUp.Elmish.HMR` to
    /// dispose the previous build's program when a hot-reload arrives,
    /// and by the React adapter's `beforeunload` hook so background
    /// listeners (SSE, notifications) no-op cleanly on page navigation
    /// rather than firing against a dead loop.
    ///
    /// Idempotent: calling `Terminate` on an already-torn-down dispatcher
    /// is a safe no-op.
    abstract Terminate: unit -> unit

/// Fine-grained handle to the runtime's `EffectHandle` registry. The
/// consumer receives this via `Program.withEffectControllerHandle` and
/// uses it to dispose individual effects (or every effect tagged with a
/// given module id) without tearing the whole program down.
///
/// Module-lifetime effects (`EffectLifetime.Module moduleId`) and
/// manual-lifetime effects (`EffectLifetime.Manual`) cannot be disposed
/// without this handle — they're documented as caller-disposed, and
/// this interface is the way the caller reaches them.
type IEffectController =
    /// Dispose the effect with the given `Id`. Used to retire a single
    /// manual-lifetime subscription. Silently no-ops if no live effect
    /// is registered under the id.
    abstract DisposeById: id: string -> unit

    /// Dispose every effect tagged with `EffectLifetime.Module moduleId`.
    /// Used at module-reset / team-switch / tenant-switch boundaries so
    /// per-module SSE listeners don't survive into the new scope.
    /// Silently no-ops if no module-scoped effects are live.
    abstract DisposeByModule: moduleId: string -> unit

/// Internal default implementation. Composition roots receive the public
/// `IDispatcher` interface; this concrete type is the runtime-side handle
/// that flips `IsActive` and the underlying `dispatch` reference.
type internal DispatcherCore<'msg>() =
    let mutable dispatch: ('msg -> unit) option = None
    let mutable active = false
    let mutable terminateCallback: (unit -> unit) option = None

    member this.Wire(d: 'msg -> unit) =
        dispatch <- Some d
        active <- true

    member this.MarkTerminated() = active <- false

    /// Set by `runWithDispatch` so `IDispatcher.Terminate` can trigger
    /// the full teardown path (effect-dispose + subscription-stop +
    /// `MarkTerminated`) without exposing the runtime internals.
    member this.SetTerminateCallback(callback: unit -> unit) = terminateCallback <- Some callback

    member this.AsInterface(reportError: exn -> unit) =
        { new IDispatcher<'msg> with
            member _.Dispatch msg =
                if active then
                    match dispatch with
                    | Some d -> d msg
                    | None -> ()

            member _.DispatchAsync block =
                if active then
                    async {
                        try
                            let! msg = block

                            if active then
                                match dispatch with
                                | Some d -> d msg
                                | None -> ()
                        with ex ->
                            reportError ex
                    }
                    |> AsyncHelpers.start

            member _.IsActive = active

            member _.Terminate() =
                if active then
                    match terminateCallback with
                    | Some cb ->
                        try
                            cb ()
                        with ex ->
                            reportError ex
                    | None ->
                        // 0.4.3 — the fallback used to silently flip
                        // the flag, leaving effects and subscriptions
                        // attached to a dead loop while the program
                        // looked cleanly terminated. The runtime
                        // always wires `SetTerminateCallback` before
                        // exposing the interface, so reaching here
                        // means a wiring defect — surface it via
                        // reportError so it's visible to operators
                        // instead of trading observability for a
                        // resource leak.
                        active <- false

                        reportError (
                            exn
                                "IDispatcher.Terminate: terminateCallback was not wired before the dispatcher was exposed; effect / subscription disposal has been skipped and listeners may leak. This indicates a runtime wiring defect — please file an issue."
                        )
        }
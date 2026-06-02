namespace ToolUp.Elmish

open System

/// Dispatch — feed a new message into the processing loop.
type Dispatch<'msg> = 'msg -> unit

/// Effect — returns immediately, may schedule dispatch of a message at any time.
type Effect<'msg> = Dispatch<'msg> -> unit

/// Cmd — container for effects that may produce messages.
type Cmd<'msg> = Effect<'msg> list

/// `Cmd` module — create and manipulate commands.
///
/// Trimmed from upstream Elmish v5.x: the `OfFunc`, `OfPromise`, `OfTask`,
/// `OfValueTask`, `OfAsyncWith`, and `OfAsyncImmediate` families are dropped
/// (zero observed call sites across the ToolUp consumer base; see
/// `MIGRATION.md` for replacement shapes). The `OfRemoting` family is added
/// to name the dominant RPC-call pattern and integrate with
/// `ToolUp.Remoting`'s correlation-id / telemetry surface.
[<RequireQualifiedAccess>]
module Cmd =

    /// Internal — execute every effect in a `Cmd`, routing exceptions to
    /// `onError`. Invoked by the runtime; not part of the consumer surface.
    let internal exec onError (dispatch: Dispatch<'msg>) (cmd: Cmd<'msg>) =
        cmd
        |> List.iter (fun call ->
            try
                call dispatch
            with ex ->
                onError ex)

    /// `Cmd.none` — no commands, equivalent to `[]`.
    let none: Cmd<'msg> = []

    /// Issue a specific message.
    let inline ofMsg (msg: 'msg) : Cmd<'msg> = [
        fun dispatch -> dispatch msg
    ]

    /// Wrap an arbitrary effect as a command.
    let ofEffect (effect: Effect<'msg>) : Cmd<'msg> = [ effect ]

    /// When emitting the message, map it to another type.
    let map (f: 'a -> 'msg) (cmd: Cmd<'a>) : Cmd<'msg> =
        cmd |> List.map (fun g -> (fun dispatch -> f >> dispatch) >> g)

    /// Aggregate multiple commands.
    let batch (cmds: #seq<Cmd<'msg>>) : Cmd<'msg> = cmds |> List.concat

    /// `Cmd.OfAsync` — wrap an async computation as a command.
    /// Only `either`, `perform`, `attempt` survive from upstream — the
    /// parameterised `OfAsyncWith` and Fable-only `OfAsyncImmediate` are
    /// dropped (consumers always used the default `start`).
    module OfAsync =

        /// Evaluate an async block and map the result into success or
        /// error (of exception).
        let either (task: 'a -> Async<_>) (arg: 'a) (ofSuccess: _ -> 'msg) (ofError: _ -> 'msg) : Cmd<'msg> =
            let bind dispatch = async {
                try
                    let! r = task arg
                    dispatch (ofSuccess r)
                with ex ->
                    dispatch (ofError ex)
            }

            [ bind >> AsyncHelpers.start ]

        /// Evaluate an async block and dispatch the success message;
        /// errors are silently swallowed.
        let perform (task: 'a -> Async<_>) (arg: 'a) (ofSuccess: _ -> 'msg) : Cmd<'msg> =
            let bind dispatch = async {
                try
                    let! r = task arg
                    dispatch (ofSuccess r)
                with _ ->
                    ()
            }

            [ bind >> AsyncHelpers.start ]

        /// Evaluate an async block and dispatch the error message on
        /// exception; success is silently swallowed.
        let attempt (task: 'a -> Async<_>) (arg: 'a) (ofError: _ -> 'msg) : Cmd<'msg> =
            let bind dispatch = async {
                try
                    let! _ = task arg
                    ()
                with ex ->
                    dispatch (ofError ex)
            }

            [ bind >> AsyncHelpers.start ]

    /// Retry policy for `Cmd.OfRemoting.callWithRetry`. Kept deliberately
    /// minimal — exponential backoff with a max-attempts cap covers the
    /// observed transient-transport-failure shape; consumers needing more
    /// sophisticated policies (circuit-breaking, jitter, per-error-class
    /// branching) should compose at the call site.
    type RetryPolicy = {
        /// Maximum number of attempts (including the first). Must be ≥1.
        MaxAttempts: int
        /// Initial delay before the first retry, in milliseconds.
        InitialDelayMs: int
        /// Multiplier applied to the delay on each successive retry.
        BackoffMultiplier: float
        /// Predicate: should we retry given this exception? Defaults to
        /// "any exception is retryable" if not specified by the caller.
        ShouldRetry: exn -> bool
    }

    /// Convenience constructors for `RetryPolicy`.
    [<RequireQualifiedAccess>]
    module RetryPolicy =

        /// Three attempts, 200 ms / 400 ms / 800 ms backoff, retry on
        /// any exception. Reasonable starting point for transient RPC failures.
        let defaultPolicy: RetryPolicy = {
            MaxAttempts = 3
            InitialDelayMs = 200
            BackoffMultiplier = 2.0
            ShouldRetry = fun _ -> true
        }

        /// No retries — dispatched once, errors propagate immediately.
        let none: RetryPolicy = {
            MaxAttempts = 1
            InitialDelayMs = 0
            BackoffMultiplier = 1.0
            ShouldRetry = fun _ -> false
        }

    /// `Cmd.OfRemoting` — intent-named wrapper around `Cmd.OfAsync` for
    /// RPC call sites. Documents that the underlying call is a Fable.Remoting
    /// (or `ToolUp.Remoting`) proxy invocation rather than arbitrary async,
    /// exposes a `RetryPolicy` knob, and runs through a real interceptor
    /// registry so cross-cutting concerns (correlation id stash, telemetry,
    /// categorised-error bridge) plug in at the boundary instead of every
    /// call site.
    ///
    /// 0.4.1 — `Interceptors` is a public mutable registry; consumers
    /// register at boot time via `OfRemoting.Interceptors.register`.
    /// Each interceptor's `OnCalling` runs before the proxy call; its
    /// `OnSuccess` runs on success; `OnError` runs on exception (with
    /// the chance to return a replacement / wrapped exception that
    /// downstream observation sees). Failing interceptors are swallowed
    /// so they cannot crash the call site.
    module OfRemoting =

        /// Per-call telemetry carried into / out of every interceptor.
        type CallInfo = {
            /// Wall-clock UTC when the call entered the interceptor chain.
            StartedAt: System.DateTime
            /// Best-effort method name for the call. Defaults to "<anonymous>"
            /// when `callWithName` isn't used.
            MethodName: string
            /// Per-call mutable bag. Interceptors stash state here
            /// (correlation id, span id, request id) for downstream
            /// observation. The dictionary is per-call and short-lived.
            Bag: System.Collections.Generic.Dictionary<string, obj>
        }

        /// Interceptor hook. All three callbacks are optional — implement
        /// only what's relevant. Implementations should be cheap (this
        /// runs on the dispatch hot path) and must not throw — the
        /// registry swallows exceptions so a failing interceptor can't
        /// crash the consumer's `update`.
        type IRemotingInterceptor =
            /// Fires before the proxy call is dispatched. Use to stash
            /// per-call state (correlation id, span id) into `info.Bag`.
            abstract OnCalling: info: CallInfo -> unit
            /// Fires on a successful result. The result is opaque
            /// (`obj`) so the interceptor surface stays non-generic.
            abstract OnSuccess: info: CallInfo * result: obj -> unit
            /// Fires on a thrown exception. The interceptor may return
            /// `Some replacement` to substitute a different exception
            /// (the categorised-error bridge in the ToolUp.Remoting
            /// adapter does this — it parses the server's
            /// `CategorisedErrorResult` envelope and returns a typed
            /// `RemotingException` carrying the `ErrorCategory`). Return
            /// `None` to leave the exception unchanged. When multiple
            /// interceptors return `Some`, the last-registered wins.
            abstract OnError: info: CallInfo * exn: exn -> exn option

        /// Public registry. Consumers call `register` at boot to install
        /// an interceptor; the registry runs in registration order on
        /// every `OfRemoting.call` / `OfRemoting.callWithRetry`.
        ///
        /// 0.4.3 — failing interceptors no longer disappear silently.
        /// Each catch routes the exception through `errorReporter` (a
        /// mutable hook, default: `eprintfn` one-liner) so a misbehaving
        /// interceptor — typically a tracer that forgot to handle a
        /// null Bag entry — is visible to operators rather than blinding
        /// the whole observability layer. Consumers can override via
        /// `Interceptors.setErrorReporter` to route into structured
        /// logging.
        [<RequireQualifiedAccess>]
        module Interceptors =

            let private interceptors = System.Collections.Generic.List<IRemotingInterceptor>()

            /// 0.4.3 — Where interceptor exceptions land. The default
            /// emits one line per failure to stderr (console.error on
            /// Fable) tagged with the throwing hook so consumers can
            /// triage from devtools without bespoke wiring. Override to
            /// route into a structured logger.
            let mutable private errorReporter: string -> exn -> unit =
                fun phase ex ->
                    try
                        eprintfn "[Cmd.OfRemoting.Interceptors] %s threw: %s" phase ex.Message
                    with _ ->
                        ()

            /// Replace the default interceptor-exception reporter.
            /// Useful for routing into the consumer's `ErrorReporter`
            /// instead of stderr. Set once at boot, before the first
            /// `Cmd.OfRemoting.call` fires.
            let setErrorReporter (reporter: string -> exn -> unit) : unit = errorReporter <- reporter

            /// Register an interceptor. Idempotent at the reference level —
            /// the same instance can't be registered twice (silently
            /// ignored on the second call).
            let register (interceptor: IRemotingInterceptor) : unit =
                if not (interceptors.Contains interceptor) then
                    interceptors.Add interceptor

            /// Remove a previously-registered interceptor. Silently
            /// no-ops if the instance wasn't registered.
            let unregister (interceptor: IRemotingInterceptor) : unit =
                interceptors.Remove interceptor |> ignore

            /// Internal — invoke `OnCalling` on every registered
            /// interceptor. Catches and reports per-interceptor
            /// exceptions; the dispatch loop continues unaffected.
            let internal fireOnCalling (info: CallInfo) =
                for interceptor in interceptors do
                    try
                        interceptor.OnCalling info
                    with ex ->
                        errorReporter (sprintf "OnCalling[%s]" info.MethodName) ex

            /// Internal — invoke `OnSuccess` on every registered
            /// interceptor. Catches and reports per-interceptor
            /// exceptions.
            let internal fireOnSuccess (info: CallInfo) (result: obj) =
                for interceptor in interceptors do
                    try
                        interceptor.OnSuccess(info, result)
                    with ex ->
                        errorReporter (sprintf "OnSuccess[%s]" info.MethodName) ex

            /// Internal — invoke `OnError` on every registered
            /// interceptor. Returns the (possibly replaced) exception.
            /// Last-write-wins on replacement. Catches and reports
            /// per-interceptor exceptions; an interceptor that throws
            /// is treated as "no replacement" for that interceptor.
            let internal fireOnError (info: CallInfo) (ex: exn) : exn =
                let mutable current = ex

                for interceptor in interceptors do
                    try
                        match interceptor.OnError(info, current) with
                        | Some replacement -> current <- replacement
                        | None -> ()
                    with iex ->
                        errorReporter (sprintf "OnError[%s]" info.MethodName) iex

                current

        let private makeInfo (methodName: string) : CallInfo = {
            StartedAt = System.DateTime.UtcNow
            MethodName = methodName
            Bag = System.Collections.Generic.Dictionary<string, obj>()
        }

        /// Internal — the common interceptor-aware happy-path used by
        /// every `OfRemoting.call*` variant.
        let private invokeWithInterceptors
            (methodName: string)
            (proxyCall: 'a -> Async<'result>)
            (arg: 'a)
            (ofSuccess: 'result -> 'msg)
            (ofError: exn -> 'msg)
            : Cmd<'msg> =
            let bind dispatch = async {
                let info = makeInfo methodName
                Interceptors.fireOnCalling info

                try
                    let! r = proxyCall arg
                    Interceptors.fireOnSuccess info (box r)
                    dispatch (ofSuccess r)
                with ex ->
                    let final = Interceptors.fireOnError info ex
                    dispatch (ofError final)
            }

            [ bind >> AsyncHelpers.start ]

        /// Call a Remoting proxy method; dispatch `ofSuccess` on success,
        /// `ofError` on transport / serialisation / server-raised exception.
        ///
        /// Routes through the registered `IRemotingInterceptor` chain
        /// (`OnCalling` before; `OnSuccess` / `OnError` after). Each
        /// interceptor's `OnError` may substitute a replacement
        /// exception (the categorised-error bridge does this — see
        /// `ToolUp.Remoting`'s `CategorisedErrorResult` envelope).
        ///
        /// Equivalent runtime shape to `Cmd.OfAsync.either api.SomeCall
        /// arg OnOk OnErr` with the interceptor chain added.
        let call
            (proxyCall: 'a -> Async<'result>)
            (arg: 'a)
            (ofSuccess: 'result -> 'msg)
            (ofError: exn -> 'msg)
            : Cmd<'msg> =
            invokeWithInterceptors "<anonymous>" proxyCall arg ofSuccess ofError

        /// As `call`, but attaches an explicit method name to the
        /// `CallInfo` so interceptors can route / tag per-method. Use
        /// when the call site has a meaningful name (typically the
        /// proxy field, e.g. `"TeamApi.GetMyTeams"`).
        let callWithName
            (methodName: string)
            (proxyCall: 'a -> Async<'result>)
            (arg: 'a)
            (ofSuccess: 'result -> 'msg)
            (ofError: exn -> 'msg)
            : Cmd<'msg> =
            invokeWithInterceptors methodName proxyCall arg ofSuccess ofError

        /// As `callWithRetry`, but attaches an explicit method name for
        /// per-method interceptor routing.
        let callWithRetryAndName
            (methodName: string)
            (policy: RetryPolicy)
            (proxyCall: 'a -> Async<'result>)
            (arg: 'a)
            (ofSuccess: 'result -> 'msg)
            (ofError: exn -> 'msg)
            : Cmd<'msg> =
            let attempts = max 1 policy.MaxAttempts

            let bind dispatch = async {
                let mutable attempt = 1
                let mutable lastError: exn option = None
                let mutable result: 'result option = None

                while attempt <= attempts && result.IsNone do
                    let info = makeInfo methodName
                    Interceptors.fireOnCalling info

                    try
                        let! r = proxyCall arg
                        Interceptors.fireOnSuccess info (box r)
                        result <- Some r
                    with ex ->
                        lastError <- Some ex
                        let isRetryable = policy.ShouldRetry ex
                        let hasMoreAttempts = attempt < attempts

                        if isRetryable && hasMoreAttempts then
                            let delay =
                                float policy.InitialDelayMs * (policy.BackoffMultiplier ** float (attempt - 1))
                                |> int

                            do! Async.Sleep delay
                            attempt <- attempt + 1
                        else
                            // Not retryable, or out of attempts — break.
                            attempt <- attempts + 1

                match result, lastError with
                | Some r, _ -> dispatch (ofSuccess r)
                | None, Some ex ->
                    let info = makeInfo methodName
                    let final = Interceptors.fireOnError info ex
                    dispatch (ofError final)
                | None, None ->
                    // Unreachable — the loop guarantees one of these.
                    dispatch (ofError (exn "OfRemoting.callWithRetry: no result and no error"))
            }

            [ bind >> AsyncHelpers.start ]

        /// Call a Remoting proxy method with retry-on-failure per
        /// `RetryPolicy`. Successful calls dispatch `ofSuccess` immediately;
        /// failing calls retry up to `MaxAttempts` times (with backoff)
        /// before dispatching `ofError` with the final exception.
        ///
        /// The retry loop runs inside the same Async block as the call, so
        /// no Elmish messages are dispatched mid-retry — the caller's
        /// `update` only ever sees a single success or a single failure.
        ///
        /// 0.4.1 — `OnCalling` fires once per attempt (interceptors observe
        /// each retry). `OnSuccess` fires on the successful attempt;
        /// `OnError` fires once after the final attempt — so the
        /// caller's `update` sees the same single-success-or-single-failure
        /// shape as before.
        let callWithRetry
            (policy: RetryPolicy)
            (proxyCall: 'a -> Async<'result>)
            (arg: 'a)
            (ofSuccess: 'result -> 'msg)
            (ofError: exn -> 'msg)
            : Cmd<'msg> =
            callWithRetryAndName "<anonymous>" policy proxyCall arg ofSuccess ofError
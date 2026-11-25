// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Elmish

// ─── Remote calls as effect values, until the runtime moves ──────────
//
// A module's `update` names its remote calls as VALUES here and returns
// them in a `Fuaran.MVU` step list, instead of building `Cmd.OfRemoting`
// commands. Three things, each small:
//
//   * **The effect shape.** `RemoteEffect.call` names one call: the
//     method, its argument and the constructor that turns its one reply
//     into the module's message. It has equality, so a test can state the
//     effect an `update` should return. Two calls are equal when they name
//     the same method with an equal argument and the same retry numbers;
//     the proxy and the reply are functions and take no part, exactly as a
//     `Fuaran.MVU.LegacyEffect` compares by its site label alone.
//   * **The performer.** `RemoteEffect.perform` sends that value's call
//     through the interceptor chain (`RemoteEffect.Interceptors`), its
//     retry policy and the declared read policy, all of which live here.
//     It answers once: a declared read served from the cache delivers the
//     cached value, and its refresh only refills the cache.
//   * **The adapter.** `RemoteEffect.run` / `RemoteEffect.toCmd` hand each
//     `Do` to the performer and dispatch its one reply, and turn each `Then`
//     into the follow-up message `Cmd.ofMsg` would dispatch. They exist so a
//     module can move before the runtime does; the runtime's own loop
//     replaces them.
//
// **Where the adapter attaches: in the ported file, at its own `init` and
// `update`.** Each ported file keeps the function its callers use today,
// returning `Cmd`, and gains the pure step function beside it:
//
//     let stepWith api msg model : Model * Step<Msg, RemoteEffect<Msg>> list = ...
//     let updateWith api msg model = stepWith api msg model |> RemoteEffect.run
//
// Nothing shared changes. A registered module (`ClientModule.create {
// Init; Update; ... }`) and a screen the shell embeds (a `BuiltIn` record
// that `ShellSlots.builtIn` turns into the same `ClientModule`) both read
// `update` returning `Cmd`, so both keep reading it, and the module
// contract (`ErasedModule`) is untouched. An `update` of another shape (a
// child returning `Model * Cmd<Msg> * Outcome` to its parent) uses
// `toCmd` on its step list and keeps its tuple; a parent lifting a ported
// child's steps maps them with `Step.map` and `RemoteEffect.map`.
//
// **Porting one file, in order.**
//
//   1. `open Fuaran.MVU` FIRST among the file's opens, so the file's own
//      and forge's names shadow the runtime's.
//   2. Rewrite each command helper to return a `RemoteEffect<Msg>`:
//      `Cmd.OfRemoting.call api.M arg ofOk ofErr` becomes
//      `RemoteEffect.call "Api.M" api.M arg reply`, where `reply` takes the
//      call's `Result<'r, exn>`. A call already answering
//      `Result<'t, string>` replies through `RemoteEffect.flatten`, which
//      folds a thrown exception into that call's own `Error` branch. Name
//      the method as the API record and its field: interceptors route on it.
//   3. Rename `update` to the step function and make it return a step
//      list: `Cmd.none` is `[]`, `Cmd.batch` is list concatenation,
//      `Cmd.ofMsg m` is `Then m`, and each call is `Do effect`.
//   4. Define `update` (and `init`) again as the step function piped to
//      `RemoteEffect.run`, with the signature it had.
//   5. Build, then run the file's existing tests unchanged: they call the
//      old `update` and see the same commands.
//
// **Two cases to leave alone, on `Cmd.OfRemoting` / `Cmd.ofEffect`.**
//
//   * **A call that answers twice.** A site that relies on a declared read
//     delivering its cached value and then the fresh one, and every
//     streaming call (`Cmd.OfRemoting.callStreaming*`). These are resources,
//     which the runtime's move brings; a one-shot effect answers once.
//   * **A body that dispatches more than once.** The adapter delivers one
//     message per effect and drops any later one. A `RemoteEffect.legacy`
//     body (code with no value form yet, a timer say) must dispatch exactly
//     once; one that dispatches more stays where it is.

open Fuaran.MVU

/// The retry policy a remote call runs under: exponential backoff with a
/// cap on attempts. Anything richer (circuit-breaking, jitter, branching
/// per error class) composes at the call site.
type RetryPolicy = {
    /// Maximum number of attempts, the first included. At least 1.
    MaxAttempts: int
    /// The delay before the first retry, in milliseconds.
    InitialDelayMs: int
    /// The multiplier applied to the delay on each later retry.
    BackoffMultiplier: float
    /// Whether a failure is worth another attempt.
    ShouldRetry: exn -> bool
}

/// Ready-made retry policies.
[<RequireQualifiedAccess>]
module RetryPolicy =

    /// Three attempts, 200 ms then 400 ms between them, retrying any failure.
    let defaultPolicy: RetryPolicy = {
        MaxAttempts = 3
        InitialDelayMs = 200
        BackoffMultiplier = 2.0
        ShouldRetry = fun _ -> true
    }

    /// One attempt; a failure answers at once.
    let none: RetryPolicy = {
        MaxAttempts = 1
        InitialDelayMs = 0
        BackoffMultiplier = 1.0
        ShouldRetry = fun _ -> false
    }

/// What every interceptor sees of one remote call.
type RemoteCallInfo = {
    /// When the call entered the interceptor chain, UTC.
    StartedAt: System.DateTime
    /// The method the call names, as the API record and its field.
    MethodName: string
    /// A per-call bag where interceptors stash state (a correlation id, a
    /// span id) for the later hooks of the same call.
    Bag: System.Collections.Generic.Dictionary<string, obj>
}

/// A hook around every performed remote call. Implementations must be
/// cheap and should not throw: a hook that throws is reported and skipped,
/// so it cannot break the call.
type IRemotingInterceptor =
    /// Before the request is sent.
    abstract OnCalling: info: RemoteCallInfo -> unit
    /// On a successful result, boxed so the hook stays non-generic.
    abstract OnSuccess: info: RemoteCallInfo * result: obj -> unit
    /// On a failure: `Some replacement` substitutes the exception the call
    /// answers with (the last interceptor to replace it wins), `None`
    /// leaves it as it is.
    abstract OnError: info: RemoteCallInfo * exn: exn -> exn option

/// The identity of a remote call: the method it names, its argument and
/// its retry policy's numbers. A non-generic base, so equality is one type
/// test that both hosts honour; the retry predicate is a function and takes
/// no part.
[<AllowNullLiteral>]
type RemoteCallSite(methodName: string, argument: obj, retry: RetryPolicy option) =

    /// The method the call names, as the API record and its field.
    member _.Method = methodName

    /// The call's argument.
    member _.Argument = argument

    /// The retry policy the call runs under; `None` is a single attempt.
    member _.Retry = retry

    override _.Equals(other: obj) =
        match other with
        | :? RemoteCallSite as that ->
            let sameRetry =
                match retry, that.Retry with
                | None, None -> true
                | Some mine, Some theirs ->
                    mine.MaxAttempts = theirs.MaxAttempts
                    && mine.InitialDelayMs = theirs.InitialDelayMs
                    && mine.BackoffMultiplier = theirs.BackoffMultiplier
                | _ -> false

            methodName = that.Method && argument = that.Argument && sameRetry
        | _ -> false

    override _.GetHashCode() = hash methodName

/// One remote call as a value: its identity, and the typed call it sends
/// when it is performed. Built by `RemoteEffect.call` and
/// `RemoteEffect.callWithRetry`.
[<Sealed>]
type RemoteCall<'msg>
    internal (methodName: string, argument: obj, retry: RetryPolicy option, send: ('msg -> unit) -> unit) =
    inherit RemoteCallSite(methodName, argument, retry)

    /// Send the call; its one reply is delivered through the callback.
    member internal _.Send = send

/// Code that dispatches nothing (a browser redirect, a beacon), compared by
/// its site label as a `LegacyEffect` is.
[<Sealed>]
type SignalEffect(site: string, run: unit -> unit) =
    inherit LegacySite(site)

    /// The code; it answers nothing.
    member _.Run: unit -> unit = run

/// What a ported module's step list performs: a remote call, code with no
/// value form yet, or code that answers nothing, each compared by its
/// identity.
type RemoteEffect<'msg> =
    /// A remote call.
    | Invoke of RemoteCall<'msg>
    /// A one-shot asynchronous operation that is not a remote call (a plain
    /// HTTP read, a delay, a resolver), named and compared as a call is; it
    /// does not pass the remoting interceptor chain.
    | Await of RemoteCall<'msg>
    /// Code with no value form yet (a timer, say), which dispatches once.
    | Legacy of LegacyEffect<'msg>
    /// Code that dispatches nothing, performed as a signal.
    | Fire of SignalEffect

/// The effect shape's constructors, the interceptor registry and the
/// performer.
[<RequireQualifiedAccess>]
module RemoteEffect =

    /// The interceptors every performed remote call passes through, in
    /// registration order. Register at boot, before the first call.
    [<RequireQualifiedAccess>]
    module Interceptors =

        let private interceptors = System.Collections.Generic.List<IRemotingInterceptor>()

        let mutable private errorReporter: string -> exn -> unit =
            fun hook ex ->
                try
                    eprintfn "[RemoteEffect.Interceptors] %s threw: %s" hook ex.Message
                with _ ->
                    ()

        /// Replace where an interceptor's own exception is reported (by
        /// default one line to stderr, `console.error` in the browser).
        let setErrorReporter (reporter: string -> exn -> unit) : unit = errorReporter <- reporter

        /// Register an interceptor. The same instance twice is ignored.
        let register (interceptor: IRemotingInterceptor) : unit =
            if not (interceptors.Contains interceptor) then
                interceptors.Add interceptor

        /// Remove an interceptor; one never registered is ignored.
        let unregister (interceptor: IRemotingInterceptor) : unit =
            interceptors.Remove interceptor |> ignore

        let internal fireOnCalling (info: RemoteCallInfo) =
            for interceptor in interceptors do
                try
                    interceptor.OnCalling info
                with ex ->
                    errorReporter (sprintf "OnCalling[%s]" info.MethodName) ex

        let internal fireOnSuccess (info: RemoteCallInfo) (result: obj) =
            for interceptor in interceptors do
                try
                    interceptor.OnSuccess(info, result)
                with ex ->
                    errorReporter (sprintf "OnSuccess[%s]" info.MethodName) ex

        let internal fireOnError (info: RemoteCallInfo) (ex: exn) : exn =
            let mutable current = ex

            for interceptor in interceptors do
                try
                    match interceptor.OnError(info, current) with
                    | Some replacement -> current <- replacement
                    | None -> ()
                with iex ->
                    errorReporter (sprintf "OnError[%s]" info.MethodName) iex

            current

    let private makeInfo (methodName: string) : RemoteCallInfo = {
        StartedAt = System.DateTime.UtcNow
        MethodName = methodName
        Bag = System.Collections.Generic.Dictionary<string, obj>()
    }

    // A proxy method with a registered read policy
    // (`ToolUp.Remoting.Client.ReadPolicies`) shares one request among
    // identical calls in flight, and may be served from a cache while a
    // refresh runs. The interceptor chain observes REQUESTS, not calls: the
    // first call to reach a shared request owns its chain (`OnCalling` once,
    // then `OnSuccess` or `OnError` once, as the request settles), and every
    // call waiting on it receives that chain's outcome, a replaced exception
    // included. A cache hit sends no request of its own, so it fires nothing;
    // the refresh it starts is a request, and fires the chain like any other.

    let private claimChain (methodName: string) (request: ToolUp.Remoting.Client.ReadPolicies.SharedRequest) =
        let info = makeInfo methodName

        let intercept (raw: Result<obj, exn>) =
            match raw with
            | Ok value ->
                Interceptors.fireOnSuccess info value
                raw
            | Error ex -> Error(Interceptors.fireOnError info ex)

        if request.TryClaimChain intercept then
            Interceptors.fireOnCalling info

    /// A declared read answers once: an awaited request with its outcome,
    /// a cache hit with the cached value. The refresh a hit starts only
    /// refills the cache; a failed refresh leaves the served value standing
    /// and the stale entry gone, so the next call goes to the server.
    let private awaitDeclaredRead
        (methodName: string)
        (leg: ToolUp.Remoting.Client.ReadPolicies.ReadLeg)
        (reply: Result<'result, exn> -> unit)
        : Async<unit> =
        async {
            match leg with
            | ToolUp.Remoting.Client.ReadPolicies.Awaiting request ->
                claimChain methodName request

                match! request.AwaitFinal() with
                | Ok value -> reply (Ok(unbox<'result> value))
                | Error ex -> reply (Error ex)
            | ToolUp.Remoting.Client.ReadPolicies.Served(cached, refresh) ->
                reply (Ok(unbox<'result> cached))
                claimChain methodName refresh
                // Awaiting sends the refresh; its outcome lands in the cache.
                let! _ = refresh.AwaitFinal()
                ()
        }

    let private sendOnce
        (methodName: string)
        (proxyCall: 'a -> Async<'result>)
        (arg: 'a)
        (reply: Result<'result, exn> -> unit)
        : Async<unit> =
        let plain (invoke: unit -> Async<'result>) = async {
            let info = makeInfo methodName
            Interceptors.fireOnCalling info

            try
                let! r = invoke ()
                Interceptors.fireOnSuccess info (box r)
                reply (Ok r)
            with ex ->
                reply (Error(Interceptors.fireOnError info ex))
        }

        // With no read policy registered anywhere, `tryObserve` invokes
        // nothing and answers `None`: the proxy is invoked inside the `try`,
        // after `OnCalling`.
        match ToolUp.Remoting.Client.ReadPolicies.tryObserve (fun () -> proxyCall arg) with
        | None -> plain (fun () -> proxyCall arg)
        | Some(ToolUp.Remoting.Client.ReadPolicies.Plain call) -> plain (fun () -> call)
        | Some(ToolUp.Remoting.Client.ReadPolicies.Shared leg) -> awaitDeclaredRead methodName leg reply

    // `OnCalling` fires once per attempt and `OnSuccess` on the attempt that
    // succeeds; `OnError` fires once, after the last attempt. No message is
    // delivered between attempts.
    let private sendWithRetry
        (methodName: string)
        (policy: RetryPolicy)
        (proxyCall: 'a -> Async<'result>)
        (arg: 'a)
        (reply: Result<'result, exn> -> unit)
        : Async<unit> =
        let attempts = max 1 policy.MaxAttempts

        async {
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

                    if policy.ShouldRetry ex && attempt < attempts then
                        let delay =
                            float policy.InitialDelayMs * (policy.BackoffMultiplier ** float (attempt - 1))
                            |> int

                        do! Async.Sleep delay
                        attempt <- attempt + 1
                    else
                        attempt <- attempts + 1

            match result, lastError with
            | Some r, _ -> reply (Ok r)
            | None, Some ex -> reply (Error(Interceptors.fireOnError (makeInfo methodName) ex))
            | None, None -> reply (Error(exn "RemoteEffect.callWithRetry: no result and no error"))
        }

    /// A remote call: the method's name, the proxy field, its argument and
    /// the constructor that turns the reply into the module's message.
    let call
        (methodName: string)
        (proxyCall: 'a -> Async<'result>)
        (arg: 'a)
        (reply: Result<'result, exn> -> 'msg)
        : RemoteEffect<'msg> =
        Invoke(
            RemoteCall(
                methodName,
                box arg,
                None,
                fun dispatch -> Async.StartImmediate(sendOnce methodName proxyCall arg (reply >> dispatch))
            )
        )

    /// As `call`, retried under `policy`.
    let callWithRetry
        (methodName: string)
        (policy: RetryPolicy)
        (proxyCall: 'a -> Async<'result>)
        (arg: 'a)
        (reply: Result<'result, exn> -> 'msg)
        : RemoteEffect<'msg> =
        Invoke(
            RemoteCall(
                methodName,
                box arg,
                Some policy,
                fun dispatch -> Async.StartImmediate(sendWithRetry methodName policy proxyCall arg (reply >> dispatch))
            )
        )

    /// A one-shot asynchronous operation that is not a remote call: its
    /// name, the operation, its argument and the constructor that turns its
    /// one outcome into the module's message. Two are equal when they name
    /// the same operation with an equal argument.
    let await
        (name: string)
        (operation: 'a -> Async<'result>)
        (arg: 'a)
        (reply: Result<'result, exn> -> 'msg)
        : RemoteEffect<'msg> =
        Await(
            RemoteCall(
                name,
                box arg,
                None,
                fun dispatch ->
                    Async.StartImmediate(
                        async {
                            let! outcome = Async.Catch(operation arg)

                            match outcome with
                            | Choice1Of2 result -> dispatch (reply (Ok result))
                            | Choice2Of2 ex -> dispatch (reply (Error ex))
                        }
                    )
            )
        )

    /// Code with no value form yet, under a site label unique to it. The
    /// body must dispatch exactly once.
    let legacy (site: string) (body: Dispatch<'msg> -> unit) : RemoteEffect<'msg> = Legacy(LegacyEffect(site, body))

    /// Code that answers nothing (a redirect, a beacon), under a site label
    /// unique to it: put what distinguishes one firing from another, a URL
    /// say, in the label.
    let signal (site: string) (body: unit -> unit) : RemoteEffect<'msg> = Fire(SignalEffect(site, body))

    /// The reply of a call that answers `Result<'t, string>`, with a thrown
    /// exception folded into its `Error` branch as the exception's message.
    let flatten (reply: Result<Result<'t, string>, exn>) : Result<'t, string> =
        match reply with
        | Ok answer -> answer
        | Error ex -> Error ex.Message

    /// A child's effect in its parent's message type; the identity is kept.
    let map (f: 'a -> 'b) (effect: RemoteEffect<'a>) : RemoteEffect<'b> =
        match effect with
        | Invoke call ->
            Invoke(RemoteCall(call.Method, call.Argument, call.Retry, (fun dispatch -> call.Send(f >> dispatch))))
        | Await call ->
            Await(RemoteCall(call.Method, call.Argument, call.Retry, (fun dispatch -> call.Send(f >> dispatch))))
        | Legacy legacy -> Legacy(LegacyEffect(legacy.Site, (fun dispatch -> legacy.Run(f >> dispatch))))
        | Fire signal -> Fire signal

    let private once (deliver: Dispatch<'msg>) : Dispatch<'msg> =
        let answered = ref false

        fun msg ->
            if not answered.Value then
                answered.Value <- true
                deliver msg

    /// The performer: an effect's one reply, delivered through the callback
    /// it is handed, or none for a signal. A second answer is dropped.
    let perform (effect: RemoteEffect<'msg>) : Performance<'msg> =
        match effect with
        | Invoke call
        | Await call -> Reply(fun deliver -> call.Send(once deliver))
        | Legacy legacy -> Reply(fun deliver -> legacy.Run(once deliver))
        | Fire signal -> Performance.Signal signal.Run

    let private ofStep (step: Step<'msg, RemoteEffect<'msg>>) : Effect<'msg> =
        match step with
        | Then msg -> fun dispatch -> dispatch msg
        | Do effect ->
            fun dispatch ->
                match perform effect with
                | Reply answer -> answer dispatch
                | Performance.Signal fire -> fire ()

    /// The adapter: a step list as today's command, one command element per
    /// step, in order.
    let toCmd (steps: Step<'msg, RemoteEffect<'msg>> list) : Cmd<'msg> = List.map ofStep steps

    /// The adapter over one effect value.
    let asCmd (effect: RemoteEffect<'msg>) : Cmd<'msg> = toCmd [ Do effect ]

    /// The adapter over an `init` or `update` result.
    let run (model: 'model, steps: Step<'msg, RemoteEffect<'msg>> list) : 'model * Cmd<'msg> = model, toCmd steps
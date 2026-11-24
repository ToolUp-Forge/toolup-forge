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
//   * **The performer.** `RemoteEffect.perform` turns that value into the
//     call through `Cmd.OfRemoting.callWithName` (or
//     `callWithRetryAndName`), so a performed effect passes through the
//     interceptor chain, the retry policy and the declared read policy that
//     `Cmd.OfRemoting` already carries, and nothing of them is written a
//     second time. It answers once: a declared read served from the cache
//     delivers the cached value, and its refresh only refills the cache.
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

/// The identity of a remote call: the method it names, its argument and
/// its retry policy's numbers. A non-generic base, so equality is one type
/// test that both hosts honour; the retry predicate is a function and takes
/// no part.
[<AllowNullLiteral>]
type RemoteCallSite(methodName: string, argument: obj, retry: Cmd.RetryPolicy option) =

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
/// through `Cmd.OfRemoting` when it is performed. Built by
/// `RemoteEffect.call` and `RemoteEffect.callWithRetry`.
[<Sealed>]
type RemoteCall<'msg>
    internal (methodName: string, argument: obj, retry: Cmd.RetryPolicy option, send: Dispatch<'msg> -> unit) =
    inherit RemoteCallSite(methodName, argument, retry)

    /// Send the call; its reply is dispatched through the callback.
    member internal _.Send = send

/// What a ported module's step list performs: a remote call, or code with
/// no value form yet, compared by its site label.
type RemoteEffect<'msg> =
    /// A remote call.
    | Invoke of RemoteCall<'msg>
    /// Code with no value form yet (a timer, say), which dispatches once.
    | Legacy of LegacyEffect<'msg>

/// The effect shape's constructors, its performer and the adapter that runs
/// a step list on today's loop.
[<RequireQualifiedAccess>]
module RemoteEffect =

    let private sendThrough (cmd: Cmd<'msg>) (dispatch: Dispatch<'msg>) =
        for effect in cmd do
            effect dispatch

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
                sendThrough (Cmd.OfRemoting.callWithName methodName proxyCall arg (Ok >> reply) (Error >> reply))
            )
        )

    /// As `call`, retried under `policy` as `Cmd.OfRemoting.callWithRetryAndName`
    /// retries it.
    let callWithRetry
        (methodName: string)
        (policy: Cmd.RetryPolicy)
        (proxyCall: 'a -> Async<'result>)
        (arg: 'a)
        (reply: Result<'result, exn> -> 'msg)
        : RemoteEffect<'msg> =
        Invoke(
            RemoteCall(
                methodName,
                box arg,
                Some policy,
                sendThrough (
                    Cmd.OfRemoting.callWithRetryAndName methodName policy proxyCall arg (Ok >> reply) (Error >> reply)
                )
            )
        )

    /// Code with no value form yet, under a site label unique to it. The
    /// body must dispatch exactly once.
    let legacy (site: string) (body: Dispatch<'msg> -> unit) : RemoteEffect<'msg> = Legacy(LegacyEffect(site, body))

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
        | Legacy legacy -> Legacy(LegacyEffect(legacy.Site, (fun dispatch -> legacy.Run(f >> dispatch))))

    let private once (deliver: Dispatch<'msg>) : Dispatch<'msg> =
        let answered = ref false

        fun msg ->
            if not answered.Value then
                answered.Value <- true
                deliver msg

    /// The performer: an effect's one reply, delivered through the callback
    /// it is handed. A second answer is dropped.
    let perform (effect: RemoteEffect<'msg>) : Performance<'msg> =
        match effect with
        | Invoke call -> Reply(fun deliver -> call.Send(once deliver))
        | Legacy legacy -> Reply(fun deliver -> legacy.Run(once deliver))

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

    /// The adapter over an `init` or `update` result.
    let run (model: 'model, steps: Step<'msg, RemoteEffect<'msg>> list) : 'model * Cmd<'msg> = model, toCmd steps
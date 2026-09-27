// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.ReadPolicyTests

// ─── Phase 854 — declared reads: in-flight sharing and stale-while-revalidate ─
//
// Run on the TRANSPILED client, through the real proxy
// (`Remoting.buildProxy`) and the real `Cmd.OfRemoting`, against a scripted
// XMLHttpRequest that counts what is sent:
//
//   * two identical calls in flight make ONE request, and both callers
//     receive its result; different arguments do not share;
//   * a `Cacheable` read within its age returns SYNCHRONOUSLY from the cache
//     and a refresh follows, whose value an Elmish consumer receives as an
//     ordinary second `ofSuccess` message;
//   * the interceptor chain fires once per REQUEST — and a replaced
//     exception reaches every waiter;
//   * an `Invalidates` mutation clears the cached read; `clear` (the
//     identity change) drops everything;
//   * an undeclared method, and a mutation, are neither shared nor cached,
//     and their interceptor events are one set per call, as before.
//
// The XMLHttpRequest stub is the one `JsonClientDecodeTests` installs (same
// global, same shape; whichever pack runs first installs it), so request
// counts are read as deltas.

open Fable.Core
open ToolUp.Elmish
open ToolUp.Remoting.Client
open ToolUp.Platform.Tests.Remoting.ReadPolicyFixture
open ToolUp.AI.Client.Tests.NodeTest

// ─── XMLHttpRequest stub (shared with JsonClientDecodeTests) ─────────

[<Emit("""(() => {
    if (globalThis.__xhrStub) return;
    const stub = { calls: [], status: 200, body: '' };
    globalThis.__xhrStub = stub;
    globalThis.XMLHttpRequest = class {
        constructor() { this.readyState = 0; this.status = 0; this.responseText = ''; this.headers = {}; }
        open(method, url) { this.method = method; this.url = url; }
        setRequestHeader(key, value) { this.headers[key] = value; }
        abort() { }
        send(body) {
            stub.calls.push({ method: this.method, url: this.url, body });
            this.status = stub.status;
            this.responseText = stub.body;
            this.readyState = 4;
            queueMicrotask(() => { if (this.onreadystatechange) this.onreadystatechange(); });
        }
    };
})()""")>]
let private installXhrStubJs () : unit = jsNative

// Phase 855 - these cases script XMLHttpRequest, so they select the XHR
// transport (`fetch` is the default).
let private installXhrStub () : unit =
    Http.useTransport Http.Transport.Xhr
    installXhrStubJs ()

[<Emit("(globalThis.__xhrStub.status = $0, globalThis.__xhrStub.body = $1, undefined)")>]
let private scriptResponse (status: int) (body: string) : unit = jsNative

[<Emit("globalThis.__xhrStub.calls.length")>]
let private callCount () : int = jsNative

// ─── The API under test ─────────────────────────────────────────────

// `ReadCatalogApi` and its registered `declarations` are the shared
// fixture the .NET pack pins to the record's attributes.

let private api: ReadCatalogApi =
    Remoting.createApi () |> Remoting.buildProxy<ReadCatalogApi>

/// Registration is per page and permanent, so it is made by the first case
/// that needs it rather than at import — the packs that run before this one
/// run with nothing registered, the ones after with this record registered.
let private setUp () =
    installXhrStub ()
    ReadPolicies.registerFor<ReadCatalogApi> declarations
    ReadPolicies.clear ()

type private Outcome<'T> =
    | Pending
    | Returned of 'T
    | Raised of exn

let private start (call: Async<'T>) : (unit -> Outcome<'T>) =
    let mutable outcome = Pending

    Async.StartImmediate(
        async {
            try
                let! value = call
                outcome <- Returned value
            with ex ->
                outcome <- Raised ex
        }
    )

    fun () -> outcome

let private same (actual: 'T) (expected: 'T) (message: string) : unit =
    Expect.isTrue (actual = expected) (sprintf "%s\n  actual:   %A\n  expected: %A" message actual expected)

let private returned (outcome: Outcome<int>) : int option =
    match outcome with
    | Returned value -> Some value
    | _ -> None

// ─── Elmish harness ─────────────────────────────────────────────────

type private Msg =
    | Got of int
    | Failed of string

let private run (dispatch: Msg -> unit) (cmd: Cmd<Msg>) =
    for effect in cmd do
        effect dispatch

/// Counts one method's interceptor events, and optionally replaces its
/// exceptions.
type private Counting(methodName: string, replaceWith: string option) =
    member val Calling = 0 with get, set
    member val Succeeded = 0 with get, set
    member val Errored = 0 with get, set

    interface Cmd.OfRemoting.IRemotingInterceptor with
        member this.OnCalling info =
            if info.MethodName = methodName then
                this.Calling <- this.Calling + 1

        member this.OnSuccess(info, _) =
            if info.MethodName = methodName then
                this.Succeeded <- this.Succeeded + 1

        member this.OnError(info, _) =
            if info.MethodName = methodName then
                this.Errored <- this.Errored + 1
                replaceWith |> Option.map exn
            else
                None

let tests =
    testList "Phase 854 — declared reads: in-flight sharing and stale-while-revalidate" [

        testList "854.B — identical in-flight calls share one request" [
            testCaseDeferred
                "two concurrent identical calls make one request, and both callers receive it"
                30
                (fun () ->
                    setUp ()
                    scriptResponse 200 "7"
                    let before = callCount ()
                    let first = start (api.GetShared "shared-1")
                    let second = start (api.GetShared "shared-1")

                    fun () ->
                        same (callCount () - before) 1 "one request"
                        same (returned (first ())) (Some 7) "the first caller"
                        same (returned (second ())) (Some 7) "the second caller")

            testCaseDeferred "different arguments are different requests" 30 (fun () ->
                setUp ()
                scriptResponse 200 "1"
                let before = callCount ()
                let a = start (api.GetShared "shared-a")
                let b = start (api.GetShared "shared-b")

                fun () ->
                    same (callCount () - before) 2 "two requests"
                    same (returned (a ()), returned (b ())) (Some 1, Some 1) "both answered")

            testCaseDeferred "Cacheable 0 shares only while in flight: a later call is a new request" 30 (fun () ->
                setUp ()
                scriptResponse 200 "2"
                let before = callCount ()
                let mutable later = fun () -> Pending

                Async.StartImmediate(
                    async {
                        let! _ = api.GetShared "shared-later"
                        later <- start (api.GetShared "shared-later")
                    }
                )

                fun () ->
                    same (callCount () - before) 2 "the settled request is not reused"
                    same (returned (later ())) (Some 2) "the later call is answered")

            testCaseDeferred "an undeclared method is untouched: two concurrent calls, two requests" 30 (fun () ->
                setUp ()
                scriptResponse 200 "3"
                let before = callCount ()
                let a = start (api.GetUndeclared "plain")
                let b = start (api.GetUndeclared "plain")

                fun () ->
                    same (callCount () - before) 2 "two requests"
                    same (returned (a ()), returned (b ())) (Some 3, Some 3) "both answered")

            testCaseDeferred "a mutation is never shared, even when declared" 30 (fun () ->
                setUp ()
                scriptResponse 200 "0"
                let before = callCount ()
                let a = start (api.Bump "twice")
                let b = start (api.Bump "twice")

                fun () ->
                    same (callCount () - before) 2 "both mutations are sent"
                    same (returned (a ()), returned (b ())) (Some 0, Some 0) "both answered")
        ]

        testList "854.B — the interceptor chain counts one request once" [
            testCaseDeferred
                "two Cmd calls sharing a request: OnCalling once, OnSuccess once, both messages"
                50
                (fun () ->
                    setUp ()
                    scriptResponse 200 "9"
                    let counting = Counting("ReadCatalogApi.GetShared", None)
                    Cmd.OfRemoting.Interceptors.register counting
                    let before = callCount ()
                    let seen = ResizeArray<Msg>()

                    Cmd.batch [
                        Cmd.OfRemoting.callWithName "ReadCatalogApi.GetShared" api.GetShared "chain-1" Got (fun e ->
                            Failed e.Message)
                        Cmd.OfRemoting.callWithName "ReadCatalogApi.GetShared" api.GetShared "chain-1" Got (fun e ->
                            Failed e.Message)
                    ]
                    |> run seen.Add

                    fun () ->
                        Cmd.OfRemoting.Interceptors.unregister counting
                        same (callCount () - before) 1 "one request"
                        same (counting.Calling, counting.Succeeded, counting.Errored) (1, 1, 0) "one set of events"
                        same (List.ofSeq seen) [ Got 9; Got 9 ] "every waiter receives the result")

            testCaseDeferred
                "a failure is intercepted once, and every waiter receives the replaced exception"
                50
                (fun () ->
                    setUp ()
                    scriptResponse 500 "boom"
                    let counting = Counting("ReadCatalogApi.GetShared", Some "replaced by the chain")
                    Cmd.OfRemoting.Interceptors.register counting
                    let before = callCount ()
                    let seen = ResizeArray<Msg>()

                    Cmd.batch [
                        Cmd.OfRemoting.callWithName "ReadCatalogApi.GetShared" api.GetShared "chain-fail" Got (fun e ->
                            Failed e.Message)
                        Cmd.OfRemoting.callWithName "ReadCatalogApi.GetShared" api.GetShared "chain-fail" Got (fun e ->
                            Failed e.Message)
                    ]
                    |> run seen.Add

                    fun () ->
                        Cmd.OfRemoting.Interceptors.unregister counting
                        same (callCount () - before) 1 "one request"
                        same (counting.Calling, counting.Succeeded, counting.Errored) (1, 0, 1) "one set of events"

                        same
                            (List.ofSeq seen)
                            [ Failed "replaced by the chain"; Failed "replaced by the chain" ]
                            "the chain's replacement reaches every waiter")

            testCaseDeferred "an undeclared method keeps one set of events per call" 50 (fun () ->
                setUp ()
                scriptResponse 200 "4"
                let counting = Counting("ReadCatalogApi.GetUndeclared", None)
                Cmd.OfRemoting.Interceptors.register counting
                let before = callCount ()
                let seen = ResizeArray<Msg>()

                Cmd.batch [
                    Cmd.OfRemoting.callWithName "ReadCatalogApi.GetUndeclared" api.GetUndeclared "each" Got (fun e ->
                        Failed e.Message)
                    Cmd.OfRemoting.callWithName "ReadCatalogApi.GetUndeclared" api.GetUndeclared "each" Got (fun e ->
                        Failed e.Message)
                ]
                |> run seen.Add

                fun () ->
                    Cmd.OfRemoting.Interceptors.unregister counting
                    same (callCount () - before) 2 "two requests"
                    same (counting.Calling, counting.Succeeded, counting.Errored) (2, 2, 0) "one set per call"
                    same (List.ofSeq seen) [ Got 4; Got 4 ] "both answered")
        ]

        testList "854.C — stale-while-revalidate" [
            testCaseDeferred "a cached read returns synchronously, and a refresh follows" 50 (fun () ->
                setUp ()
                scriptResponse 200 "1"
                let before = callCount ()
                let mutable servedAtOnce = Pending
                let mutable afterRefresh = fun () -> Pending

                Async.StartImmediate(
                    async {
                        let! _ = api.GetCount "swr"
                        scriptResponse 200 "2"
                        // Read at once: a served value is returned without a
                        // single await.
                        servedAtOnce <- (start (api.GetCount "swr")) ()
                        // Once the refresh has landed, the cache holds it.
                        do! Async.Sleep 20
                        afterRefresh <- start (api.GetCount "swr")
                    }
                )

                fun () ->
                    same (returned servedAtOnce) (Some 1) "the cached value, synchronously"
                    same (returned (afterRefresh ())) (Some 2) "the refreshed value is what is cached now"
                    // prime + the first hit's refresh + the second hit's refresh
                    same (callCount () - before) 3 "every hit refreshes")

            testCaseDeferred "an Elmish consumer receives the fresh value as an ordinary second message" 60 (fun () ->
                setUp ()
                scriptResponse 200 "4"
                let seen = ResizeArray<Msg>()

                Async.StartImmediate(
                    async {
                        let! _ = api.GetCount "swr-cmd"
                        scriptResponse 200 "5"

                        Cmd.OfRemoting.call api.GetCount "swr-cmd" Got (fun e -> Failed e.Message)
                        |> run seen.Add
                    }
                )

                fun () -> same (List.ofSeq seen) [ Got 4; Got 5 ] "the cached value, then the refreshed one")

            testCaseDeferred "a failed refresh dispatches nothing more, and evicts the stale value" 60 (fun () ->
                setUp ()
                scriptResponse 200 "6"
                let seen = ResizeArray<Msg>()
                let before = callCount ()
                let mutable afterFailure = fun () -> Pending

                Async.StartImmediate(
                    async {
                        let! _ = api.GetCount "swr-fail"
                        scriptResponse 500 "down"

                        Cmd.OfRemoting.call api.GetCount "swr-fail" Got (fun e -> Failed e.Message)
                        |> run seen.Add

                        do! Async.Sleep 25
                        scriptResponse 200 "7"
                        afterFailure <- start (api.GetCount "swr-fail")
                    }
                )

                fun () ->
                    same (List.ofSeq seen) [ Got 6 ] "the served value stands; the failure is not a message"
                    same (returned (afterFailure ())) (Some 7) "the next call went to the server"
                    same (callCount () - before) 3 "prime, failed refresh, fresh read")

            testCaseDeferred "an Invalidates mutation clears the cached read" 50 (fun () ->
                setUp ()
                scriptResponse 200 "1"
                let before = callCount ()
                let mutable afterMutation = Pending
                let mutable settled = fun () -> Pending

                Async.StartImmediate(
                    async {
                        let! _ = api.GetCount "inv"
                        scriptResponse 200 "0"
                        let! _ = api.Bump "inv"
                        scriptResponse 200 "3"
                        settled <- start (api.GetCount "inv")
                        afterMutation <- settled ()
                    }
                )

                fun () ->
                    same afterMutation Pending "not served: the read waits on the server"
                    same (returned (settled ())) (Some 3) "the server's post-mutation value"
                    same (callCount () - before) 3 "read, mutation, read")

            testCaseDeferred "clear (an identity change) drops every cached read" 50 (fun () ->
                setUp ()
                scriptResponse 200 "8"
                let mutable afterClear = Pending

                Async.StartImmediate(
                    async {
                        let! _ = api.GetCount "who"
                        ReadPolicies.clear ()
                        afterClear <- (start (api.GetCount "who")) ()
                    }
                )

                fun () -> same afterClear Pending "nothing is served after clear")
        ]
    ]
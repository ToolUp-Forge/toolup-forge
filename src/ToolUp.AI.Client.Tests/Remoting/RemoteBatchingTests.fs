// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.RemoteBatchingTests

// ─── Phase 855 — the fetch transport, and same-tick batching ────────────
//
// Run on the TRANSPILED client, through the real proxy
// (`Remoting.buildProxy`), against a scripted `fetch` that records every
// request and answers a batch envelope element by element:
//
//   * the default transport is `fetch` — same URL, method, headers and
//     cookies as the XHR request it replaces, no `keepalive` flag — and a
//     rejected `fetch` reads as XHR's status-0 network failure;
//   * with batching enabled, five same-tick calls make ONE request, a lone
//     call makes its own plain request, and each call resolves from its
//     own element (a failed element fails only its call);
//   * a refused envelope is re-sent one request per call;
//   * the Phase 854 read table runs FIRST: identical in-flight reads are
//     one element, never two;
//   * calls whose proxies send different headers never share an envelope.
//
// Every case restores the transport, the batching switch and `fetch`
// before asserting, so the packs after this one see the page as they
// found it.

open Fable.Core
open ToolUp.Remoting.Client
open ToolUp.AI.Client.Tests.NodeTest

type BatchProbeApi = {
    Get: int -> Async<int>
    GetNothing: unit -> Async<int>
}

// ─── fetch stub ─────────────────────────────────────────────────────
//
// mode 'answer'  — a batch envelope is answered per element (Get n -> n * 10,
//                  GetNothing -> 99); a plain call likewise.
// mode 'refuse'  — the envelope is refused (400 remoting_batch_refused);
//                  plain calls are answered.
// mode 'fail2'   — the envelope's second element answers 500.
// mode 'reject'  — every fetch rejects (a network failure).

[<Emit("""(() => {
    const stub = { calls: [], mode: 'answer', saved: globalThis.fetch };
    globalThis.__batchFetch = stub;
    const answer = (route, body) => {
        if (route.endsWith('/GetNothing')) { return { status: 200, body: '99' }; }
        const v = JSON.parse(body);
        const n = Array.isArray(v) ? v[0] : v;
        return { status: 200, body: String(n * 10) };
    };
    const respond = (r) => Promise.resolve({
        status: r.status,
        text: () => Promise.resolve(r.body),
        arrayBuffer: () => Promise.resolve(new TextEncoder().encode(r.body).buffer)
    });
    globalThis.fetch = (url, init) => {
        stub.calls.push({ url, init });
        if (stub.mode === 'reject') { return Promise.reject(new TypeError('Failed to fetch')); }
        if (url.endsWith('/_batch')) {
            if (stub.mode === 'refuse') {
                return respond({ status: 400, body: '{"error":"remoting_batch_refused","reason":"scripted"}' });
            }
            const elements = JSON.parse(init.body);
            const answers = elements.map((e, i) =>
                stub.mode === 'fail2' && i === 1 ? { status: 500, body: 'boom' } : answer(e.route, e.body));
            return respond({ status: 200, body: JSON.stringify(answers) });
        }
        return respond(answer(url, init.body));
    };
})()""")>]
let private installFetchStub () : unit = jsNative

[<Emit("(globalThis.fetch = globalThis.__batchFetch.saved, undefined)")>]
let private restoreFetch () : unit = jsNative

[<Emit("(globalThis.__batchFetch.mode = $0, undefined)")>]
let private mode (value: string) : unit = jsNative

[<Emit("globalThis.__batchFetch.calls.length")>]
let private callCount () : int = jsNative

[<Emit("globalThis.__batchFetch.calls[$0].url")>]
let private callUrl (i: int) : string = jsNative

[<Emit("globalThis.__batchFetch.calls[$0].init.method")>]
let private callMethod (i: int) : string = jsNative

[<Emit("globalThis.__batchFetch.calls[$0].init.credentials")>]
let private callCredentials (i: int) : string = jsNative

[<Emit("('keepalive' in globalThis.__batchFetch.calls[$0].init)")>]
let private callSetsKeepalive (i: int) : bool = jsNative

[<Emit("globalThis.__batchFetch.calls[$0].init.headers.some(function (h) { return h[0] === $1; })")>]
let private callHasHeader (i: int) (name: string) : bool = jsNative

/// The routes an envelope call carried.
[<Emit("JSON.parse(globalThis.__batchFetch.calls[$0].init.body).map(function (e) { return e.route; })")>]
let private envelopeRoutes (i: int) : string[] = jsNative

/// The element bodies an envelope call carried (`null` for a GET element).
[<Emit("JSON.parse(globalThis.__batchFetch.calls[$0].init.body).map(function (e) { return e.body === null ? 'null' : e.body; })")>]
let private envelopeBodies (i: int) : string[] = jsNative

let private api: BatchProbeApi =
    Remoting.createApi () |> Remoting.buildProxy<BatchProbeApi>

/// The same API record, from a proxy that sends one more header.
let private otherApi: BatchProbeApi =
    Remoting.createApi ()
    |> Remoting.withCustomHeader [ "X-Probe", "other" ]
    |> Remoting.buildProxy<BatchProbeApi>

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

let private statusOf (outcome: Outcome<int>) : int option =
    match outcome with
    | Raised(:? ProxyRequestException as error) -> Some error.StatusCode
    | _ -> None

/// Arrange the page for one case; the returned function puts it back.
let private arrange (batching: bool) : (unit -> unit) =
    let transport = Http.currentTransport ()
    Http.useTransport Http.Transport.Fetch
    installFetchStub ()

    if batching then
        RemoteBatching.enable RemoteBatching.DefaultRoute
    else
        RemoteBatching.disable ()

    fun () ->
        RemoteBatching.disable ()
        restoreFetch ()
        Http.useTransport transport

/// A deferred case whose page state is restored before its assertions run.
let private case (name: string) (batching: bool) (act: unit -> (unit -> unit)) =
    testCaseDeferred name 30 (fun () ->
        let restore = arrange batching
        let check = act ()

        fun () ->
            restore ()
            check ())

let tests =
    testList "Phase 855 — the fetch transport, and same-tick batching" [

        testList "855.A — the fetch transport" [
            case "the default transport is fetch: one call, one fetch, the request XHR would have sent" false (fun () ->
                let outcome = start (api.Get 3)

                fun () ->
                    same (callCount ()) 1 "one request"
                    same (callUrl 0) "/BatchProbeApi/Get" "the method's own route"
                    same (callMethod 0) "POST" "a call with arguments is a POST"
                    same (callCredentials 0) "same-origin" "cookies as XHR sends them without withCredentials"
                    Expect.isTrue (callHasHeader 0 "x-remoting-proxy") "the proxy's headers ride along"

                    Expect.isFalse
                        (callSetsKeepalive 0)
                        "no keepalive flag (it caps in-flight bodies at 64 KiB; connection reuse needs no flag)"

                    same (returned (outcome ())) (Some 30) "decoded as before")

            case "a parameterless call is a GET" false (fun () ->
                let outcome = start (api.GetNothing())

                fun () ->
                    same (callMethod 0) "GET" "no arguments, no body"
                    same (returned (outcome ())) (Some 99) "decoded as before")

            case "a rejected fetch is status 0 — the network failure XHR reports" false (fun () ->
                mode "reject"
                let outcome = start (api.Get 1)

                fun () -> same (statusOf (outcome ())) (Some 0) "a ProxyRequestException carrying status 0")

            case "the XHR opt-out is honoured: fetch is not called" false (fun () ->
                Http.useTransport Http.Transport.Xhr
                let transport = Http.currentTransport ()
                Http.useTransport Http.Transport.Fetch

                fun () -> same transport Http.Transport.Xhr "the selection reads back")
        ]

        testList "855.C — same-tick calls travel as one request" [
            case "five same-tick calls make one request, and each resolves from its own element" true (fun () ->
                let outcomes = [ for n in 1..5 -> start (api.Get n) ]

                fun () ->
                    same (callCount ()) 1 "ONE request for five calls"
                    same (callUrl 0) RemoteBatching.DefaultRoute "the envelope route"
                    same (envelopeRoutes 0) (Array.create 5 "/BatchProbeApi/Get") "five elements, one per call"

                    same
                        (outcomes |> List.map (fun outcome -> returned (outcome ())))
                        [ Some 10; Some 20; Some 30; Some 40; Some 50 ]
                        "each call received its own element, in order")

            case "a lone call travels as it does today" true (fun () ->
                let outcome = start (api.Get 4)

                fun () ->
                    same (callCount ()) 1 "one request"
                    same (callUrl 0) "/BatchProbeApi/Get" "its own route, not the envelope"
                    same (returned (outcome ())) (Some 40) "answered")

            case "a parameterless call is a null-body element" true (fun () ->
                let nothing = start (api.GetNothing())
                let one = start (api.Get 1)

                fun () ->
                    same (callCount ()) 1 "one envelope"
                    same (envelopeBodies 0) [| "null"; "[1]" |] "the GET element carries no body"
                    same (returned (nothing ()), returned (one ())) (Some 99, Some 10) "both answered")

            case "a failed element fails only its own call" true (fun () ->
                mode "fail2"
                let outcomes = [ for n in 1..3 -> start (api.Get n) ]

                fun () ->
                    let results = outcomes |> List.map (fun outcome -> outcome ())
                    same (returned results.[0]) (Some 10) "the first call is served"
                    same (statusOf results.[1]) (Some 500) "the second raises its element's status"
                    same (returned results.[2]) (Some 30) "the third call is served")

            case "a refused envelope is re-sent one request per call" true (fun () ->
                mode "refuse"
                let outcomes = [ for n in 1..3 -> start (api.Get n) ]

                fun () ->
                    same (callCount ()) 4 "the refused envelope, then three plain requests"
                    same [ for i in 1..3 -> callUrl i ] (List.replicate 3 "/BatchProbeApi/Get") "each on its own route"

                    same
                        (outcomes |> List.map (fun outcome -> returned (outcome ())))
                        [ Some 10; Some 20; Some 30 ]
                        "every call answered as if batching were off")

            case "a network failure fails every call in the envelope as status 0" true (fun () ->
                mode "reject"
                let outcomes = [ for n in 1..2 -> start (api.Get n) ]

                fun () ->
                    same
                        (outcomes |> List.map (fun outcome -> statusOf (outcome ())))
                        [ Some 0; Some 0 ]
                        "each call fails as it would have alone")

            case "calls from proxies with different headers never share an envelope" true (fun () ->
                let a = [ start (api.Get 1); start (api.Get 2) ]
                let b = [ start (otherApi.Get 3); start (otherApi.Get 4) ]

                fun () ->
                    same (callCount ()) 2 "one envelope per header set"
                    Expect.isFalse (callHasHeader 0 "X-Probe") "the first envelope carries the first proxy's headers"
                    Expect.isTrue (callHasHeader 1 "X-Probe") "the second carries the second's"

                    same
                        (a @ b |> List.map (fun outcome -> returned (outcome ())))
                        [ Some 10; Some 20; Some 30; Some 40 ]
                        "all answered")

            case "Phase 854's in-flight sharing runs first: identical reads are one element" true (fun () ->
                ReadPolicies.registerFor<BatchProbeApi> [ "Get", ReadPolicy.cacheable 0 ]
                let first = start (api.Get 7)
                let second = start (api.Get 7)
                let other = start (api.Get 8)

                fun () ->
                    same (callCount ()) 1 "one envelope"
                    same (envelopeBodies 0) [| "[7]"; "[8]" |] "the duplicate read is not a second element"

                    same
                        (returned (first ()), returned (second ()), returned (other ()))
                        (Some 70, Some 70, Some 80)
                        "both sharers receive the shared element")
        ]
    ]
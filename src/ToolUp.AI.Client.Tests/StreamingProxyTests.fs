// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.StreamingProxyTests

// ─── Phase 69c.D — the Fable proxy over a streaming method ───────────────
//
// Exercises `ToolUp.Remoting.Client`'s streaming consumer under Node (the
// module lives in Platform.Client, reached transitively — the same route
// `NotificationClientTests` takes). Three things are pinned:
//
//   * the proxy recognises an `'arg -> IAsyncEnumerable<'T>` field at
//     build time and returns a COLD stream for it — nothing is sent until
//     `RemoteStream.subscribe`, and what is then sent is the same route,
//     headers and JSON-array body a request/response call would send;
//   * the SSE decoder is correct across arbitrary response-chunk
//     boundaries — a frame split mid-`data:` line decodes once the rest
//     arrives, never twice and never as garbage;
//   * every non-happy ending is an `OnError`, never silence: the server's
//     `event: error`, a non-200 response, a body that ends with no terminal
//     frame; and a disposed subscription delivers nothing at all.
//
// The browser substrate is stubbed before any module under test runs:
// `globalThis.fetch` is a scripted fake that records each call and serves
// the response body as a `getReader()` stream of pre-cut chunks.

open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Remoting.Client
open ToolUp.AI.Client.Tests.NodeTest

// ─── fetch stub ─────────────────────────────────────────────────────

[<Emit("""(() => {
    const calls = [];
    let script = { status: 200, chunks: [] };
    globalThis.__sseStub = {
        calls,
        set: (status, chunks) => { script = { status, chunks }; }
    };
    globalThis.fetch = (url, init) => {
        calls.push({ url, init });
        const enc = new TextEncoder();
        const encoded = script.chunks.map((c) => enc.encode(c));
        let i = 0;
        const reader = {
            read: () => Promise.resolve(
                i < encoded.length ? { done: false, value: encoded[i++] } : { done: true, value: undefined })
        };
        const response = {
            status: script.status,
            body: { getReader: () => reader },
            text: () => Promise.resolve(script.chunks.join(''))
        };
        return Promise.resolve(response);
    };
})()""")>]
let private installFetchStub () : unit = jsNative

[<Emit("globalThis.__sseStub.set($0, $1)")>]
let private scriptResponse (status: int) (chunks: string[]) : unit = jsNative

[<Emit("globalThis.__sseStub.calls.length")>]
let private callCount () : int = jsNative

[<Emit("globalThis.__sseStub.calls[$0].url")>]
let private callUrl (i: int) : string = jsNative

[<Emit("globalThis.__sseStub.calls[$0].init.method")>]
let private callMethod (i: int) : string = jsNative

[<Emit("globalThis.__sseStub.calls[$0].init.body")>]
let private callBody (i: int) : string = jsNative

[<Emit("globalThis.__sseStub.calls[$0].init.headers[$1]")>]
let private callHeader (i: int) (name: string) : string = jsNative

installFetchStub ()

// ─── the API under test ─────────────────────────────────────────────

type TickRequest = { Upto: int }

type TickEvent = { N: int; Label: string }

/// A streaming method beside a request/response one on the same record —
/// the coexistence the phase's first acceptance criterion names.
type TickStreamApi = {
    Tick: TickRequest -> IAsyncEnumerable<TickEvent>
    Ping: unit -> Async<string>
}

let private api: TickStreamApi =
    Remoting.createApi () |> Remoting.buildProxy<TickStreamApi>

/// A recording observer: what arrived, in order, and how the stream ended.
type private Recorder() =
    let chunks = ResizeArray<TickEvent>()
    let mutable completed = 0
    let mutable errors: exn list = []

    member _.Chunks = List.ofSeq chunks
    member _.Completed = completed
    member _.Errors = List.rev errors

    member _.Observer: StreamObserver<TickEvent> = {
        OnChunk = chunks.Add
        OnComplete = fun () -> completed <- completed + 1
        OnError = fun e -> errors <- e :: errors
    }

let private frame (id: string) (json: string) =
    sprintf "event: chunk\nid: %s\ndata: %s\n\n" id json

// ─── Tests ──────────────────────────────────────────────────────────

let tests =
    testList "Phase 69c.D — streaming proxy (Fable)" [

        testList "SseFrame decoder (pure)" [
            testCase "parse decodes chunk / complete / error frames with ids and multiline data" (fun () ->
                let wire =
                    "event: chunk\nid: c-0\ndata: {\"a\":1}\n\n"
                    + "event: chunk\nid: c-1\ndata: line-a\ndata: line-b\n\n"
                    + ": keepalive\n\n"
                    + "data: no-event-field\n\n"
                    + "event: complete\ndata: {}\n\n"
                    + "event: error\ndata: {\"message\":\"boom\"}\n\n"

                Expect.equal
                    (SseFrame.parse wire)
                    [
                        {
                            Event = "chunk"
                            Id = Some "c-0"
                            Data = "{\"a\":1}"
                        }
                        {
                            Event = "chunk"
                            Id = Some "c-1"
                            Data = "line-a\nline-b"
                        }
                        {
                            Event = "message"
                            Id = None
                            Data = "no-event-field"
                        }
                        {
                            Event = "complete"
                            Id = None
                            Data = "{}"
                        }
                        {
                            Event = "error"
                            Id = None
                            Data = "{\"message\":\"boom\"}"
                        }
                    ]
                    "frames decode in order; the comment is dropped; a missing event: is `message`")

            testCase "parse tolerates CRLF and drops an event with no data" (fun () ->
                Expect.equal
                    (SseFrame.parse "event: chunk\r\nid: x\r\ndata: 1\r\n\r\nevent: chunk\r\nid: y\r\n\r\n")
                    [
                        {
                            Event = "chunk"
                            Id = Some "x"
                            Data = "1"
                        }
                    ]
                    "CRLF decodes; the data-less event is dropped")

            testCase "splitComplete keeps the trailing partial event for the next chunk" (fun () ->
                Expect.equal
                    (SseFrame.splitComplete "event: chunk\ndata: 1\n\nevent: chunk\ndata: 2")
                    ("event: chunk\ndata: 1\n\n", "event: chunk\ndata: 2")
                    "complete events before the last delimiter; the partial event after it"

                Expect.equal
                    (SseFrame.splitComplete "event: chunk\ndata: par")
                    ("", "event: chunk\ndata: par")
                    "nothing complete yet")
        ]

        testCaseDeferred
            "the proxy returns a COLD stream: nothing is sent until subscribed, then the remoting POST"
            30
            (fun () ->
                scriptResponse 200 [| "event: complete\ndata: {}\n\n" |]
                let before = callCount ()
                let stream = api.Tick { Upto = 2 }
                let afterBuild = callCount ()
                let recorder = Recorder()
                RemoteStream.subscribe stream recorder.Observer |> ignore

                fun () ->
                    Expect.equal afterBuild before "calling the streaming method sends nothing"
                    Expect.equal (callCount ()) (before + 1) "subscribing sends exactly one request"
                    let i = callCount () - 1
                    Expect.equal (callUrl i) "/TickStreamApi/Tick" "the same /Type/Method route as request/response"
                    Expect.equal (callMethod i) "POST" "the argument travels in a POST body"

                    Expect.equal
                        (callBody i)
                        "[{\"Upto\": 2}]"
                        "the same JSON-array body a request/response call sends (SimpleJson spacing)"

                    Expect.equal (callHeader i "x-remoting-proxy") "true" "the proxy marker header rides along"
                    Expect.equal recorder.Completed 1 "an immediate complete frame completes the stream")

        testCaseDeferred
            "chunks are decoded across arbitrary response-chunk boundaries; complete ends the stream"
            30
            (fun () ->
                // Every frame is cut somewhere awkward: mid-JSON, mid-`event:`
                // line, and the terminal frame is split from its delimiter.
                scriptResponse 200 [|
                    "event: chunk\nid: c-0\ndata: {\"N\":1,\"La"
                    "bel\":\"one\"}\n\nevent: chunk\nid: c-1\ndata: {\"N\":2,\"Label\":\"two\"}\n\nevent: chu"
                    "nk\nid: c-2\ndata: {\"N\":3,\"Label\":\"three\"}\n\nevent: complete\ndata: {}\n"
                    "\n"
                |]

                let recorder = Recorder()
                RemoteStream.subscribe (api.Tick { Upto = 3 }) recorder.Observer |> ignore

                fun () ->
                    Expect.equal
                        recorder.Chunks
                        [
                            { N = 1; Label = "one" }
                            { N = 2; Label = "two" }
                            { N = 3; Label = "three" }
                        ]
                        "each chunk decodes exactly once, in order, whatever the cut"

                    Expect.equal recorder.Completed 1 "complete fires once"
                    Expect.isEmpty recorder.Errors "no error")

        testCaseDeferred "an error frame surfaces as ProxyRequestException carrying the server's message" 30 (fun () ->
            scriptResponse 200 [|
                frame "e-0" "{\"N\":1,\"Label\":\"one\"}"
                + "event: error\ndata: {\"message\":\"boom\"}\n\n"
            |]

            let recorder = Recorder()
            RemoteStream.subscribe (api.Tick { Upto = 9 }) recorder.Observer |> ignore

            fun () ->
                Expect.equal recorder.Chunks [ { N = 1; Label = "one" } ] "chunks before the error were delivered"
                Expect.equal recorder.Completed 0 "complete does not fire"

                match recorder.Errors with
                | [ (:? ProxyRequestException as e) ] ->
                    Expect.isTrue (e.Message.Contains "boom") "the server's message is carried"
                    Expect.equal e.StatusCode 200 "the error rode a 200 stream"
                | other -> failwithf "expected one ProxyRequestException, got %A" other)

        testCaseDeferred "a non-200 response is an error, never a stream" 30 (fun () ->
            scriptResponse 500 [| "Internal server error" |]
            let recorder = Recorder()
            RemoteStream.subscribe (api.Tick { Upto = 1 }) recorder.Observer |> ignore

            fun () ->
                Expect.isEmpty recorder.Chunks "no chunks"
                Expect.equal recorder.Completed 0 "no complete"

                match recorder.Errors with
                | [ (:? ProxyRequestException as e) ] ->
                    Expect.equal e.StatusCode 500 "the status is carried"
                    Expect.equal e.ResponseText "Internal server error" "the body is carried"
                | other -> failwithf "expected one ProxyRequestException, got %A" other)

        testCaseDeferred "a body that ends without a terminal frame is an error, not a silent completion" 30 (fun () ->
            scriptResponse 200 [| frame "t-0" "{\"N\":1,\"Label\":\"one\"}" |]
            let recorder = Recorder()
            RemoteStream.subscribe (api.Tick { Upto = 1 }) recorder.Observer |> ignore

            fun () ->
                Expect.equal recorder.Chunks [ { N = 1; Label = "one" } ] "the chunk that did arrive was delivered"
                Expect.equal recorder.Completed 0 "no complete"

                match recorder.Errors with
                | [ e ] -> Expect.isTrue (e.Message.Contains "ended without") "the truncation is named"
                | other -> failwithf "expected one error, got %A" other)

        testCaseDeferred "disposing the subscription delivers nothing further" 30 (fun () ->
            scriptResponse 200 [|
                frame "d-0" "{\"N\":1,\"Label\":\"one\"}" + "event: complete\ndata: {}\n\n"
            |]

            let recorder = Recorder()
            let subscription = RemoteStream.subscribe (api.Tick { Upto = 1 }) recorder.Observer
            subscription.Dispose()

            fun () ->
                Expect.isEmpty recorder.Chunks "no chunk after dispose"
                Expect.equal recorder.Completed 0 "no complete after dispose"
                Expect.isEmpty recorder.Errors "no error after dispose")
    ]
module ToolUp.Platform.Tests.InProcess.StreamingDispatchTests

open System.Net.Http
open System.Text
open System.Collections.Generic
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe

// ─── Phase 69c.tail A — AsyncStream.fromCallback over the real dispatcher ──
//
// `AsyncStream.fromCallback` is the reusable bridge that turns a push-based
// (callback / sink) producer into the `IAsyncEnumerable<'T>` a streaming
// method returns — the shape the typed AI chat endpoint (StreamChatV2) and
// the future Phase 53 / KB streaming adopters all need. These tests mount a
// streaming API record whose method is built with `fromCallback` on a real
// ToolUp.Remoting dispatcher (TestServer) and assert the end-to-end SSE
// framing:
//
//   * a SYNCHRONOUS producer (emits every value then returns) streams each
//     value as `event: chunk` and ends with `event: complete` after the
//     terminal value;
//   * a DETACHED-TAIL producer (returns once it has kicked off background
//     emission — the AI agent-loop shape) keeps the stream open until the
//     terminal value the background later emits, NOT until the producer's
//     foreground returns. This is the contract StreamChatV2 relies on.

// NOT private — the wire converters + dispatcher proxy reflect over the shape.
type CountEvent =
    | Tick of n: int
    | Done

/// `FailAfter > 0` makes the producer fault after that many ticks (Phase
/// 69c.C — pins the chunk count on the `error` terminal path).
type CountRequest = {
    Upto: int
    Detached: bool
    FailAfter: int
}

type CounterStreamApi = {
    [<AllowAnonymous>]
    Count: CountRequest -> IAsyncEnumerable<CountEvent>
}

let private isDone =
    function
    | Done -> true
    | _ -> false

/// Build the streaming handler. When `Detached`, the producer returns
/// immediately after starting a background emitter (the AI shape); the
/// stream must still complete on the terminal `Done`.
let private impl: CounterStreamApi = {
    Count =
        fun req ->
            AsyncStream.fromCallback isDone (fun emit ->
                if req.Detached then
                    async {
                        // Foreground returns at once; the tail emits later.
                        Async.Start(
                            async {
                                do! Async.Sleep 30

                                for i in 1 .. req.Upto do
                                    emit (Tick i)

                                emit Done
                            }
                        )

                        return ()
                    }
                else
                    async {
                        for i in 1 .. req.Upto do
                            if req.FailAfter > 0 && i > req.FailAfter then
                                failwith "producer fault"

                            emit (Tick i)

                        emit Done
                    })
}

/// Phase 69c.C — capturing `IRemotingTelemetry`: one record per call.
type private CapturingTelemetry() =
    let events = System.Collections.Concurrent.ConcurrentQueue<MethodTelemetry>()
    member _.Events = events |> Seq.toList

    interface IRemotingTelemetry with
        member _.OnMethodCompleted t = events.Enqueue t

let private buildHost (handler: HttpHandler) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost.UseTestServer().Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe handler)
            |> ignore)
        .Build()

let private handler: HttpHandler =
    Remoting.createApi () |> Remoting.fromValue impl |> Remoting.buildHttpHandler

/// One streaming call. `telemetry` composes a capturing sink when given;
/// `correlationId` sets the request's `x-correlation-id` header so the
/// per-chunk ids are predictable. Returns the body text and the
/// `x-correlation-id` the dispatcher stamped on the response.
let private streamCall (telemetry: CapturingTelemetry option) (correlationId: string option) (req: CountRequest) = async {
    let handler =
        match telemetry with
        | Some sink ->
            Remoting.createApi ()
            |> Remoting.fromValue impl
            |> Remoting.withTelemetry sink
            |> Remoting.buildHttpHandler
        | None -> handler

    use host = buildHost handler
    do! host.StartAsync() |> Async.AwaitTask
    use client = host.GetTestClient()
    use request = new HttpRequestMessage(HttpMethod.Post, "/CounterStreamApi/Count")
    request.Headers.Add("x-remoting-proxy", "true")

    correlationId
    |> Option.iter (fun c -> request.Headers.Add("x-correlation-id", c))

    let body =
        sprintf """[{"Upto":%d,"Detached":%b,"FailAfter":%d}]""" req.Upto req.Detached req.FailAfter

    request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    let! resp = client.SendAsync request |> Async.AwaitTask
    let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

    let stamped =
        match resp.Headers.TryGetValues "x-correlation-id" with
        | true, vs -> Seq.head vs
        | _ -> ""

    do! host.StopAsync() |> Async.AwaitTask
    return text, stamped
}

let private streamBody (detached: bool) (upto: int) = async {
    let! text, _ =
        streamCall None None {
            Upto = upto
            Detached = detached
            FailAfter = 0
        }

    return text
}

[<Tests>]
let tests =
    testList "Phase 69c.tail A — AsyncStream.fromCallback dispatch (integration)" [

        testAsync "synchronous producer streams every value as a chunk then completes" {
            let! text = streamBody false 3

            Expect.stringContains text "event: chunk" "each emitted value is an SSE chunk"
            // Fable wire shape for the DU cases: Tick is {"Tick":[n]}, Done is "Done".
            Expect.stringContains text "Tick" "the Tick values are framed"
            Expect.stringContains text "\"Done\"" "the terminal Done value is framed as a chunk"

            Expect.stringContains
                text
                "event: complete"
                "the stream ends with a complete event after the terminal value"
        }

        testAsync "detached-tail producer (the AI shape) stays open until the background terminal" {
            // The producer's foreground returns immediately; if the stream
            // ended on producer-return rather than on the terminal value,
            // none of the background-emitted ticks would appear.
            let! text = streamBody true 2

            Expect.stringContains text "event: chunk" "background-emitted values still framed as chunks"
            Expect.stringContains text "Tick" "ticks emitted AFTER the foreground returned still reach the stream"
            Expect.stringContains text "\"Done\"" "the background terminal value is delivered"

            Expect.stringContains
                text
                "event: complete"
                "the stream completes on the background terminal, not on foreground return"
        }

        // ── Phase 69c.C — per-chunk correlation + the telemetry chunk-count dimension ──

        testAsync "every chunk frame carries id: <correlation-id>-<chunk-index>, from the caller's header" {
            let! text, stamped =
                streamCall None (Some "corr-xyz") {
                    Upto = 3
                    Detached = false
                    FailAfter = 0
                }

            Expect.equal stamped "corr-xyz" "the caller's correlation id is echoed on the response header"

            let frames = SseFrame.parse text
            let chunkIds = frames |> List.filter (fun f -> f.Event = "chunk") |> List.map _.Id

            // 3 ticks + the Done terminal value = 4 chunks, zero-based.
            Expect.equal
                chunkIds
                [ Some "corr-xyz-0"; Some "corr-xyz-1"; Some "corr-xyz-2"; Some "corr-xyz-3" ]
                "chunk ids are the correlation id joined to the zero-based chunk index"

            Expect.equal (frames |> List.last).Event "complete" "the terminal frame is `complete`"
        }

        testAsync "chunk ids use the dispatcher-generated correlation id when the caller sends none" {
            let! text, stamped =
                streamCall None None {
                    Upto = 1
                    Detached = false
                    FailAfter = 0
                }

            Expect.isNotEmpty stamped "the dispatcher generated a correlation id"

            let chunkIds =
                SseFrame.parse text |> List.filter (fun f -> f.Event = "chunk") |> List.map _.Id

            Expect.equal
                chunkIds
                [ Some(stamped + "-0"); Some(stamped + "-1") ]
                "generated correlation id + index on every chunk"
        }

        testAsync "telemetry: one event per streaming call with ChunkCount = the chunks written (success)" {
            let sink = CapturingTelemetry()

            let! _, stamped =
                streamCall (Some sink) (Some "corr-t") {
                    Upto = 5
                    Detached = false
                    FailAfter = 0
                }

            match sink.Events with
            | [ t ] ->
                Expect.equal t.MethodName "Count" "the streaming method name"
                Expect.equal t.CorrelationId (Some stamped) "the call's correlation id rides on the event"
                Expect.equal t.ChunkCount (Some 6) "5 ticks + Done = 6 chunks"

                match t.Outcome with
                | MethodOutcome.Succeeded -> ()
                | other -> failtestf "expected Succeeded, got %A" other
            | other -> failtestf "expected exactly one telemetry event per streaming call, got %d" other.Length
        }

        testAsync "telemetry: a mid-stream fault reports Failed with the chunks written BEFORE the error" {
            let sink = CapturingTelemetry()

            let! text, _ =
                streamCall (Some sink) (Some "corr-f") {
                    Upto = 5
                    Detached = false
                    FailAfter = 2
                }

            let frames = SseFrame.parse text

            Expect.equal
                (frames |> List.filter (fun f -> f.Event = "chunk") |> List.length)
                2
                "two chunks reached the wire"

            Expect.equal (frames |> List.last).Event "error" "the terminal frame is `error`"

            match sink.Events with
            | [ t ] ->
                Expect.equal t.ChunkCount (Some 2) "the count is the chunks written before the fault"

                match t.Outcome with
                | MethodOutcome.Failed ex -> Expect.stringContains ex.Message "producer fault" "the fault is carried"
                | other -> failtestf "expected Failed, got %A" other
            | other -> failtestf "expected exactly one telemetry event, got %d" other.Length
        }
    ]
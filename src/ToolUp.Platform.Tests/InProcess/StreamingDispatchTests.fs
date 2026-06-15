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

type CountRequest = { Upto: int; Detached: bool }

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
                            emit (Tick i)

                        emit Done
                    })
}

let private buildHost (handler: HttpHandler) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost.UseTestServer().Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe handler)
            |> ignore)
        .Build()

let private handler: HttpHandler =
    Remoting.createApi () |> Remoting.fromValue impl |> Remoting.buildHttpHandler

let private streamBody (detached: bool) (upto: int) = async {
    use host = buildHost handler
    do! host.StartAsync() |> Async.AwaitTask
    use client = host.GetTestClient()

    use req = new HttpRequestMessage(HttpMethod.Post, "/CounterStreamApi/Count")
    req.Headers.Add("x-remoting-proxy", "true")

    let body = sprintf """[{"Upto":%d,"Detached":%b}]""" upto detached
    req.Content <- new StringContent(body, Encoding.UTF8, "application/json")

    let! resp = client.SendAsync req |> Async.AwaitTask
    let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    do! host.StopAsync() |> Async.AwaitTask
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
    ]
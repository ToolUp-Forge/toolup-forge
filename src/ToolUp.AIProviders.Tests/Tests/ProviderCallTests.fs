// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Tests.ProviderCallTests

// The connector kernel's own contract, over a connector built here: the
// caller's token reaches the socket, a timeout is an attempt failure, and a
// rate limit waits the floor. Offline: the HTTP client is a stub.

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.AI
open ToolUp.AI.Wire
open ToolUp.Platform.AI
open ToolUp.Platform.Transport

/// Never answers: waits on the request's token, and records that it fired.
type private BlockingHandler() =
    inherit HttpMessageHandler()
    let cancelled = TaskCompletionSource<unit>()

    member _.Cancelled = cancelled.Task

    override _.SendAsync(_request, cancellationToken) = task {
        use _ = cancellationToken.Register(fun () -> cancelled.TrySetResult() |> ignore)
        do! Task.Delay(Timeout.Infinite, cancellationToken)
        return new HttpResponseMessage(HttpStatusCode.OK)
    }

/// Answers with each status in turn; the last one repeats.
type private ScriptedHandler(statuses: HttpStatusCode list) =
    inherit HttpMessageHandler()
    let mutable remaining = statuses

    override _.SendAsync(_request, _cancellationToken) =
        let status =
            match remaining with
            | [ last ] -> last
            | next :: rest ->
                remaining <- rest
                next
            | [] -> HttpStatusCode.BadRequest

        Task.FromResult(new HttpResponseMessage(status, Content = new StringContent("{}")))

let private reply: AIProviderResponse = {
    Content = "ok"
    ToolCalls = []
    StopReason = StopReason.EndTurn
    Usage = None
}

/// A connector whose every vendor rule is trivial, over `handler`.
let private connector (handler: HttpMessageHandler) : Connector<unit> = {
    Capabilities =
        fun model -> {
            AIProviderCapabilities.unknown with
                Model = model
        }
    CanServe = fun _ -> true
    UnservedReason = ""
    IsVisionCapable = fun _ -> false
    StructuredOutput = StructuredOutput.Native
    Client = new HttpClient(handler, BaseAddress = Uri "https://vendor.example")
    Credential = fun () -> async { return Ok [ "x-key", "k" ] }
    Endpoint = fun _ -> Endpoint.At "/v1/call"
    Body = fun _ -> Ok "{}"
    ParseResponse = fun _ _ -> Ok reply
    Stream = fun _ -> StreamProcessor.overData () (fun s _ -> s, None) (fun () -> reply)
    RateLimitFloor = None
    Pause = fun _ -> async { return () }
}

let private request streaming : ProviderRequest = {
    ConfiguredModel = "m"
    Model = "m"
    Options = AIProviderCallOptions.none
    Messages = [ AIProviderMessage.text "user" "hi" ]
    Tools = []
    SystemPrompt = None
    Schema = None
    Streaming = streaming
}

/// Start a call on a blocking vendor, cancel the caller's token, and report
/// whether the request saw it and how the call ended.
let private cancelInFlight (streaming: bool) =
    let handler = new BlockingHandler()
    use caller = new CancellationTokenSource()
    let onStream = if streaming then Some ignore else None

    let call =
        ProviderCall.send (connector handler) (request streaming) onStream RetryPolicy.defaults caller.Token
        |> Async.StartAsTask

    caller.CancelAfter(TimeSpan.FromMilliseconds 100.0)

    let finished =
        Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds 10.0)).Result = (call :> Task)

    let sawIt = handler.Cancelled.Wait(TimeSpan.FromSeconds 1.0)
    finished, sawIt, call

let tests =
    testList "Phase 1079 — the connector kernel" [
        for streaming in [ false; true ] do
            let shape = if streaming then "streaming" else "buffered"

            test $"cancelling the caller's token aborts the in-flight {shape} request" {
                let finished, sawIt, call = cancelInFlight streaming
                Expect.isTrue finished "the call ended promptly instead of waiting on the vendor"
                Expect.isTrue sawIt "the request's own token fired: the abort reached the socket"

                Expect.isTrue
                    (call.IsCanceled
                     || (call.IsFaulted
                         && (call.Exception.InnerExceptions
                             |> Seq.exists (fun e -> e :? OperationCanceledException))))
                    "the call reports cancellation, not a vendor failure, and is not retried"
            }

        test "a per-call timeout fails the attempt as a transient network error" {
            let handler = new BlockingHandler()

            let policy = {
                RetryPolicy.noRetry with
                    Timeout = Some(TimeSpan.FromSeconds 1.0)
            }

            let result =
                ProviderCall.send (connector handler) (request false) None policy CancellationToken.None
                |> Async.RunSynchronously

            match result with
            | Error(TransientNetwork message) -> Expect.stringContains message "timed out" "named as a timeout"
            | other -> failtestf "expected a timeout, got %A" other
        }

        test "a 429 waits the connector's floor when the caller's policy names none" {
            let pauses = ResizeArray<TimeSpan>()

            let c = {
                connector (new ScriptedHandler([ HttpStatusCode.TooManyRequests; HttpStatusCode.BadRequest ])) with
                    RateLimitFloor = Some(TimeSpan.FromSeconds 15.0)
                    Pause = fun pause -> async { pauses.Add pause }
            }

            let result =
                ProviderCall.send c (request false) None RetryPolicy.defaults CancellationToken.None
                |> Async.RunSynchronously

            Expect.isError result "the scripted refusal ends the call"
            Expect.equal (List.ofSeq pauses) [ TimeSpan.FromSeconds 15.0 ] "one retry, after the floor"
        }

        test "a vision call on a text-only model is refused before any request" {
            let handler = new BlockingHandler()

            let visionRequest = {
                request false with
                    Messages = [
                        AIProviderMessage.multipart "user" [
                            ImagePart {
                                MediaType = "image/png"
                                Source = Base64Bytes [| 1uy |]
                            }
                        ]
                    ]
            }

            let result =
                ProviderCall.send (connector handler) visionRequest None RetryPolicy.defaults CancellationToken.None
                |> Async.RunSynchronously

            match result with
            | Error(UnsupportedCapability("vision", _)) -> ()
            | other -> failtestf "expected the vision refusal, got %A" other

            Expect.isFalse handler.Cancelled.IsCompleted "nothing reached the wire"
        }
    ]
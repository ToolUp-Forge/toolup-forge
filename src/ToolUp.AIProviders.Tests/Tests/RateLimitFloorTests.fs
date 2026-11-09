// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Tests.RateLimitFloorTests

// A 429 waits the rate-limit floor on every send of every connector: they
// all run the kernel's one retry loop. Anthropic's connector brings its own
// 15 s floor (its per-minute buckets are not refilled by a sub-second pause);
// every other connector honours the floor a caller's policy names. Offline:
// the HTTP client is a stub and the pause is recorded, not slept.

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Expecto
open ToolUp.AI
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Transport

/// Answers with each status in turn; the last one repeats.
type private ScriptedHandler(statuses: HttpStatusCode list) =
    inherit HttpMessageHandler()
    let mutable remaining = statuses

    override _.SendAsync(request, _cancellationToken) =
        let status =
            match remaining with
            | [ last ] -> last
            | next :: rest ->
                remaining <- rest
                next
            | [] -> HttpStatusCode.BadRequest

        let body =
            if request.RequestUri.AbsolutePath.EndsWith "/conversations" then
                """{"id":"c-1"}"""
            else
                """{"error":{"message":"stop here"}}"""

        Task.FromResult(new HttpResponseMessage(status, Content = new StringContent(body)))

let private testKey () = async { return Some "test-key" }

/// A rate-limited first attempt, a refused second: the loop pauses exactly
/// once. Returns the pauses the connector asked for.
let private pausesAfterOne429
    (build: HttpClient -> (TimeSpan -> Async<unit>) -> IAIProvider)
    (send: IAIProvider -> Async<Result<AIProviderResponse, AIProviderError>>)
    =
    let pauses = ResizeArray<TimeSpan>()

    let client =
        new HttpClient(
            new ScriptedHandler([ HttpStatusCode.TooManyRequests; HttpStatusCode.BadRequest ]),
            BaseAddress = Uri "https://vendor.example"
        )

    let provider =
        build client (fun pause -> async { lock pauses (fun () -> pauses.Add pause) })

    let result = send provider |> Async.RunSynchronously
    result, List.ofSeq pauses

/// Each connector over an injected client and pause.
let private connectors: (string * (HttpClient -> (TimeSpan -> Async<unit>) -> IAIProvider)) list = [
    "Claude",
    fun client pause ->
        ConnectorProvider(
            {
                ClaudeAIProvider.connector client testKey ClaudeAIProvider.DefaultMaxTokens None with
                    Pause = pause
            },
            ClaudeAIProvider.DefaultModel
        )
    "OpenAI",
    fun client pause ->
        ConnectorProvider(
            {
                OpenAIProvider.connector client testKey with
                    Pause = pause
            },
            OpenAIProvider.DefaultModel
        )
]

let private oneMessage = [ AIProviderMessage.text "user" "hello" ]
let private schema = """{"type":"object","properties":{}}"""
let private callerFloor = TimeSpan.FromSeconds 20.0

let private withFloor = {
    RetryPolicy.defaults with
        RateLimitFloor = Some callerFloor
}

let private sends: (string * (RetryPolicy -> IAIProvider -> Async<Result<AIProviderResponse, AIProviderError>>)) list = [
    "chat", fun policy provider -> provider.SendMessage(oneMessage, [], None, None, policy)
    "streaming chat", fun policy provider -> provider.SendMessage(oneMessage, [], None, Some ignore, policy)
    "structured", fun policy provider -> provider.SendStructuredMessage(oneMessage, [], None, schema, policy)
]

let tests =
    testList "Phase 1079 — a 429 waits the rate-limit floor on every send of every connector" [
        for name, build in connectors do
            for sendName, send in sends do
                test $"{name}: a rate-limited {sendName} call waits the caller's floor" {
                    let result, pauses = pausesAfterOne429 build (send withFloor)
                    Expect.isError result "the scripted refusal ends the call"
                    Expect.equal pauses [ callerFloor ] "one retry, after the floor"
                }

        for sendName, send in sends do
            test $"Claude: a rate-limited {sendName} call waits Anthropic's 15 s floor under the default policy" {
                let build = connectors |> List.find (fst >> (=) "Claude") |> snd
                let result, pauses = pausesAfterOne429 build (send RetryPolicy.defaults)
                Expect.isError result "the scripted refusal ends the call"
                Expect.equal pauses [ ClaudeAIProvider.RateLimitFloor ] "the per-minute bucket gets time to refill"
            }

        test "the floor scales with the attempt and leaves other retryable errors on the exponential pause" {
            let policy = {
                RetryPolicy.defaults with
                    RateLimitFloor = Some ClaudeAIProvider.RateLimitFloor
            }

            Expect.equal (RetryPolicy.rateLimitDelay policy 2) (TimeSpan.FromSeconds 30.0) "twice the floor"

            Expect.equal
                (RetryRunner.classify (TransientServer(503, "")))
                RetryClass.Transient
                "a 503 is an ordinary transient failure"
        }
    ]
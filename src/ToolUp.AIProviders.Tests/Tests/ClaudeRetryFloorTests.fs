// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Tests.ClaudeRetryFloorTests

// ─── Claude's two sends back off from a 429 alike ───────────────────
//
// Anthropic rate-limits per minute, so a 429 is retried after a floor of
// 15 s (then 30 s, 45 s, …), not after the policy's sub-second exponential
// pause. Both sends run the provider's one retry loop, so a structured
// call that is rate-limited waits the same floor a chat call does.
// Offline: the HTTP client is a stub and the pause is recorded, not slept.

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Transport

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

        let body =
            if status = HttpStatusCode.TooManyRequests then
                """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}"""
            else
                """{"type":"error","error":{"type":"invalid_request_error","message":"stop here"}}"""

        Task.FromResult(new HttpResponseMessage(status, Content = new StringContent(body)))

/// A provider whose first call is rate-limited and whose second is refused
/// outright, so the loop pauses exactly once; returns the pauses it asked for.
let private pausesAfterOne429 (send: IAIProvider -> Async<Result<AIProviderResponse, AIProviderError>>) =
    let pauses = ResizeArray<TimeSpan>()

    let client =
        new HttpClient(
            new ScriptedHandler([ HttpStatusCode.TooManyRequests; HttpStatusCode.BadRequest ]),
            BaseAddress = Uri "https://api.anthropic.com"
        )

    let provider =
        ClaudeAIProvider.ClaudeAIProvider(
            "test-key",
            client,
            fun pause -> async { lock pauses (fun () -> pauses.Add pause) }
        )
        :> IAIProvider

    let result = send provider |> Async.RunSynchronously
    result, List.ofSeq pauses

let private oneMessage = [ AIProviderMessage.text "user" "hello" ]
let private floor = TimeSpan.FromSeconds 15.0

let tests =
    testList "Phase 1076 — Claude: a 429 waits the rate-limit floor on both sends" [

        test "a rate-limited structured call waits at least the floor before retrying" {
            let result, pauses =
                pausesAfterOne429 (fun provider ->
                    provider.SendStructuredMessage(
                        oneMessage,
                        [],
                        None,
                        """{"type":"object","properties":{}}""",
                        RetryPolicy.defaults
                    ))

            Expect.isError result "the scripted refusal ends the call"
            Expect.hasLength pauses 1 "one retry after the 429"
            Expect.isGreaterThanOrEqual pauses.Head floor "the per-minute bucket gets time to refill"
        }

        test "a rate-limited chat call waits the same floor" {
            let result, pauses =
                pausesAfterOne429 (fun provider ->
                    provider.SendMessage(oneMessage, [], None, None, RetryPolicy.defaults))

            Expect.isError result "the scripted refusal ends the call"
            Expect.hasLength pauses 1 "one retry after the 429"
            Expect.isGreaterThanOrEqual pauses.Head floor "the per-minute bucket gets time to refill"
        }

        test "a retryable error other than 429 keeps the policy's exponential pause" {
            Expect.equal
                (ClaudeAIProvider.backoffFor RetryPolicy.defaults (TransientServer(503, "")) 0)
                (RetryPolicy.delayFor RetryPolicy.defaults 2)
                "the first retry after a 503 waits the policy's first backoff"

            Expect.equal
                (ClaudeAIProvider.backoffFor RetryPolicy.defaults (TransientServer(429, "")) 1)
                (TimeSpan.FromSeconds 30.0)
                "the second 429 retry waits twice the floor"
        }
    ]
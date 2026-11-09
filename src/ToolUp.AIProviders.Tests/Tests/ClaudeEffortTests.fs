// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Tests.ClaudeEffortTests

// ─── forge#1242 — the effort a Claude call is sent with ─────────────
//
// What reaches the request body, read from the request itself. Offline: the
// HTTP client is a stub that records each body and answers with one status.

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Transport
open ToolUp.AI

let private okBody =
    """{"content":[{"type":"text","text":"ok"},{"type":"tool_use","id":"t1","name":"structured_response","input":{}}],"stop_reason":"end_turn"}"""

let private rejection =
    """{"type":"error","error":{"type":"invalid_request_error","message":"this model does not support the effort value"}}"""

/// Records each request body and answers every request with `status`.
type private RecordingHandler(status: HttpStatusCode) =
    inherit HttpMessageHandler()
    let bodies = ResizeArray<string>()
    member _.Bodies = List.ofSeq bodies

    override _.SendAsync(request, _cancellationToken) = task {
        let! body = request.Content.ReadAsStringAsync()
        lock bodies (fun () -> bodies.Add body)
        let answer = if status = HttpStatusCode.OK then okBody else rejection
        return new HttpResponseMessage(status, Content = new StringContent(answer))
    }

let private configuredModel = "claude-opus-5-5"

let private providerOver (status: HttpStatusCode) (effort: string option) =
    let handler = new RecordingHandler(status)
    let client = new HttpClient(handler, BaseAddress = Uri "https://api.anthropic.com")

    let connector =
        ClaudeAIProvider.connector
            client
            (fun () -> async { return Some "test-key" })
            ClaudeAIProvider.DefaultMaxTokens
            effort

    let provider =
        ConnectorProvider(
            {
                connector with
                    Pause = fun _ -> async { return () }
            },
            configuredModel
        )

    provider, handler

let private oneMessage = [ AIProviderMessage.text "user" "hello" ]

let private send (provider: ConnectorProvider<_>) =
    (provider :> IAIProvider).SendMessage(oneMessage, [], None, None, RetryPolicy.defaults)
    |> Async.RunSynchronously

let private sendWith (options: AIProviderCallOptions) (provider: ConnectorProvider<_>) =
    (provider :> IAIProviderModelOverride).SendMessageWith(options, oneMessage, [], None, None, RetryPolicy.defaults)
    |> Async.RunSynchronously
    |> ignore

let private effortMember = """"output_config":{"effort":"""

let tests =
    testList "forge#1242 — Claude: the effort a call is sent with" [

        test "with no effort anywhere, the body is byte for byte the body with no effort support" {
            let provider, handler = providerOver HttpStatusCode.OK None
            send provider |> ignore

            Expect.equal
                handler.Bodies
                [
                    ClaudeAIProviderWire.buildRequestBody
                        configuredModel
                        ClaudeAIProvider.DefaultMaxTokens
                        oneMessage
                        []
                        None
                        false
                        None
                ]
                "no output_config member, and nothing else moved"
        }

        test "a configured effort reaches a chat call" {
            let provider, handler = providerOver HttpStatusCode.OK (Some "high")
            send provider |> ignore
            Expect.stringContains handler.Bodies.Head (effortMember + "\"high\"}") "the configured effort is sent"
        }

        test "a configured effort reaches a structured call" {
            let provider, handler = providerOver HttpStatusCode.OK (Some "high")

            (provider :> IAIProvider)
                .SendStructuredMessage(oneMessage, [], None, """{"type":"object"}""", RetryPolicy.defaults)
            |> Async.RunSynchronously
            |> ignore

            Expect.stringContains handler.Bodies.Head (effortMember + "\"high\"}") "the configured effort is sent"
        }

        test "a call's effort wins over the configured one, and leaves the provider's alone" {
            let provider, handler = providerOver HttpStatusCode.OK (Some "high")

            provider
            |> sendWith (AIProviderCallOptions.none |> AIProviderCallOptions.withEffort "max")

            send provider |> ignore

            Expect.stringContains handler.Bodies[0] (effortMember + "\"max\"}") "the call's effort is sent"

            Expect.stringContains
                handler.Bodies[1]
                (effortMember + "\"high\"}")
                "the next call is back to the configured one"
        }

        test "a blank effort is no effort" {
            let provider, handler = providerOver HttpStatusCode.OK (Some "  ")
            send provider |> ignore

            provider
            |> sendWith (AIProviderCallOptions.none |> AIProviderCallOptions.withEffort "")

            for body in handler.Bodies do
                Expect.isFalse (body.Contains "output_config") "a blank value is not sent"
        }

        test "a call moved to another model does not carry the configured effort" {
            let provider, handler = providerOver HttpStatusCode.OK (Some "high")
            provider |> sendWith (AIProviderCallOptions.forModel "claude-haiku-4-5")

            Expect.stringContains handler.Bodies.Head "\"model\":\"claude-haiku-4-5\"" "the override is served"
            Expect.isFalse (handler.Bodies.Head.Contains "output_config") "the configured effort stays with its model"
        }

        test "a call moved to another model carries an effort it names" {
            let provider, handler = providerOver HttpStatusCode.OK (Some "high")

            provider
            |> sendWith (
                AIProviderCallOptions.forModel "claude-sonnet-5-5"
                |> AIProviderCallOptions.withEffort "medium"
            )

            Expect.stringContains handler.Bodies.Head (effortMember + "\"medium\"}") "the call's own effort is sent"
        }

        test "a call that names the configured model keeps the configured effort" {
            let provider, handler = providerOver HttpStatusCode.OK (Some "high")
            provider |> sendWith (AIProviderCallOptions.forModel configuredModel)
            Expect.stringContains handler.Bodies.Head (effortMember + "\"high\"}") "no model moved"
        }

        test "a rejected effort reports the provider's error and is not retried" {
            let provider, handler = providerOver HttpStatusCode.BadRequest (Some "not-a-level")

            match send provider with
            | Error(PermanentClient(400, message)) ->
                Expect.stringContains message "does not support the effort" "the provider's own words"
            | other -> failtestf "expected a permanent 400, got %A" other

            Expect.equal handler.Bodies.Length 1 "one request, no retry"
        }

        test "a rejected effort on a streaming call reports the provider's error and is not retried" {
            let provider, handler = providerOver HttpStatusCode.BadRequest (Some "not-a-level")

            let result =
                (provider :> IAIProvider).SendMessage(oneMessage, [], None, Some ignore, RetryPolicy.defaults)
                |> Async.RunSynchronously

            match result with
            | Error(PermanentClient(400, message)) ->
                Expect.stringContains message "does not support the effort" "the provider's own words"
            | other -> failtestf "expected a permanent 400, got %A" other

            Expect.equal handler.Bodies.Length 1 "one request, no retry"
        }
    ]
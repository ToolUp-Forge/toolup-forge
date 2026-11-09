// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Tests.ConnectorRequestBytesTests

// What every shipped connector SENDS for the four call shapes, end to end.
// Each connector is driven over a capturing HTTP client and the request it
// put on the wire (method, route, headers, body) is compared with the goldens
// the AI.Wire conformance corpus also asserts through the wire mappers. The
// goldens are the net under the connector kernel: no request byte moves.
// Offline: the client answers every request itself.

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Expecto
open ToolUp.Platform.AI
open ToolUp.Platform.Transport
open ToolUp.AI
open ToolUp.AI.Wire.Conformance

/// One request as it reached the wire.
type private Sent = {
    Method: string
    Url: string
    Headers: string list
    Body: string
}

/// Records each request and answers it from `answer` (by URL).
type private CapturingHandler(answer: string -> string) =
    inherit HttpMessageHandler()
    let sent = ResizeArray<Sent>()

    member _.Sent = List.ofSeq sent

    override _.SendAsync(request, _cancellationToken) = task {
        let! body =
            if isNull request.Content then
                Task.FromResult ""
            else
                request.Content.ReadAsStringAsync()

        let headers =
            [
                for h in request.Headers do
                    for v in h.Value -> $"{h.Key}: {v}"
                if not (isNull request.Content) then
                    for h in request.Content.Headers do
                        for v in h.Value -> $"{h.Key}: {v}"
            ]
            |> List.sort

        lock sent (fun () ->
            sent.Add {
                Method = request.Method.Method
                Url = string request.RequestUri
                Headers = headers
                Body = body
            })

        let url = string request.RequestUri

        return new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent(answer url))
    }

let private client (baseAddress: string option) (answer: string -> string) =
    let handler = new CapturingHandler(answer)
    let client = new HttpClient(handler)
    baseAddress |> Option.iter (fun b -> client.BaseAddress <- Uri b)
    handler, client

let private noRetry = RetryPolicy.noRetry

let private testKey () = async { return Some "test-key" }

/// Drive `provider` through one call shape.
let private drive (provider: IAIProvider) (shape: string) =
    let f = ConnectorFixtures.systemPrompt

    let call =
        match shape with
        | "buffered" -> provider.SendMessage(ConnectorFixtures.messages, ConnectorFixtures.tools, f, None, noRetry)
        | "streaming" ->
            provider.SendMessage(ConnectorFixtures.messages, ConnectorFixtures.tools, f, Some ignore, noRetry)
        | "structured" ->
            provider.SendStructuredMessage(
                ConnectorFixtures.messages,
                ConnectorFixtures.tools,
                f,
                ConnectorFixtures.schema,
                noRetry
            )
        | _ -> provider.SendMessage(ConnectorFixtures.visionMessages, [], f, None, noRetry)

    call |> Async.RunSynchronously

/// The headers every JSON post carries, beside the connector's own.
let private jsonContent = [ "Content-Length: "; "Content-Type: application/json; charset=utf-8" ]

let private expectSent (sent: Sent) (url: string) (headers: string list) (golden: string) =
    Expect.equal sent.Method "POST" "posted"
    Expect.equal sent.Url url "to the connector's route"

    Expect.equal
        (sent.Headers
         |> List.map (fun h ->
             if h.StartsWith "Content-Length: " then
                 "Content-Length: "
             else
                 h))
        (List.sort (headers @ jsonContent))
        "with the connector's headers"

    Expect.equal sent.Body golden "the request bytes are the golden's"

let private claude (shape: string) =
    let handler, http = client (Some "https://api.anthropic.com") (fun _ -> "")

    let provider =
        ConnectorProvider(
            ClaudeAIProvider.connector http testKey ClaudeAIProvider.DefaultMaxTokens None,
            ConnectorFixtures.claudeModel
        )
        :> IAIProvider

    drive provider shape |> ignore
    handler.Sent

let private openAI (shape: string) =
    let handler, http = client (Some "https://api.openai.com") (fun _ -> "")

    let provider =
        ConnectorProvider(OpenAIProvider.connector http testKey, ConnectorFixtures.openAIModel) :> IAIProvider

    drive provider shape |> ignore
    handler.Sent

let private gemini (shape: string) =
    let handler, http =
        client (Some "https://generativelanguage.googleapis.com") (fun _ -> "")

    let provider =
        ConnectorProvider(GeminiAIProvider.connector http testKey, ConnectorFixtures.geminiModel) :> IAIProvider

    drive provider shape |> ignore
    handler.Sent

let private copilotEndpoint = "https://example.openai.azure.com"

let private copilot (shape: string) =
    let handler, http = client None (fun _ -> "")

    let provider =
        CopilotAIProvider.CopilotAIProvider(copilotEndpoint, "test-key", ConnectorFixtures.copilotModel, http)
        :> IAIProvider

    drive provider shape |> ignore
    handler.Sent

let private m365 (shape: string) =
    let handler, http =
        client None (fun url ->
            if url.EndsWith "/conversations" then
                """{"id":"c-1"}"""
            else
                "")

    let provider =
        Microsoft365CopilotAIProvider.createWithHttpClient
            (Microsoft365CopilotAIProvider.FromAccessToken(fun () -> async { return Some "test-token" }))
            Microsoft365CopilotAIProvider.Microsoft365CopilotOptions.defaults
            http

    let result = drive provider shape
    handler.Sent, result

let private anthropicHeaders = [ "anthropic-version: 2023-06-01"; "x-api-key: test-key" ]
let private bearer = [ "Authorization: Bearer test-key" ]

let tests =
    testList "Phase 1079 — every connector's request bytes, end to end" [
        for shape, golden in
            List.zip ConnectorFixtures.shapes [
                ConnectorFixtures.claudeBuffered
                ConnectorFixtures.claudeStreaming
                ConnectorFixtures.claudeStructured
                ConnectorFixtures.claudeVision
            ] do
            testCase $"Claude — {shape}" (fun () ->
                let sent = claude shape
                Expect.hasLength sent 1 "one request"
                expectSent sent.Head "https://api.anthropic.com/v1/messages" anthropicHeaders golden)

        for shape, golden in
            List.zip ConnectorFixtures.shapes [
                ConnectorFixtures.openAIBuffered
                ConnectorFixtures.openAIStreaming
                ConnectorFixtures.openAIStructured
                ConnectorFixtures.openAIVision
            ] do
            testCase $"OpenAI — {shape}" (fun () ->
                let sent = openAI shape
                Expect.hasLength sent 1 "one request"
                expectSent sent.Head "https://api.openai.com/v1/chat/completions" bearer golden)

        for shape, golden in
            List.zip ConnectorFixtures.shapes [
                ConnectorFixtures.geminiBuffered
                ConnectorFixtures.geminiStreaming
                ConnectorFixtures.geminiStructured
                ConnectorFixtures.geminiVision
            ] do
            testCase $"Gemini — {shape}" (fun () ->
                let sent = gemini shape

                let route =
                    if shape = "streaming" then
                        "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse"
                    else
                        "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent"

                Expect.hasLength sent 1 "one request"
                expectSent sent.Head route [ "x-goog-api-key: test-key" ] golden)

        for shape, golden in
            List.zip ConnectorFixtures.shapes [
                ConnectorFixtures.copilotBuffered
                ConnectorFixtures.copilotStreaming
                ConnectorFixtures.copilotStructured
                ConnectorFixtures.copilotVision
            ] do
            testCase $"Copilot — {shape}" (fun () ->
                let sent = copilot shape
                Expect.hasLength sent 1 "one request"

                expectSent
                    sent.Head
                    (copilotEndpoint
                     + "/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21")
                    [ "api-key: test-key" ]
                    golden)

        for shape in [ "buffered"; "streaming"; "structured" ] do
            testCase $"Microsoft 365 Copilot — {shape}" (fun () ->
                let sent, _ = m365 shape
                let base' = "https://workiq.svc.cloud.microsoft/rest/conversations"
                let token = [ "Authorization: Bearer test-token" ]
                Expect.hasLength sent 2 "opens a conversation, then asks"
                expectSent sent.Head base' token "{}"

                let route =
                    if shape = "streaming" then
                        "/c-1/chatOverStream"
                    else
                        "/c-1/chat"

                let golden =
                    if shape = "structured" then
                        ConnectorFixtures.m365Structured
                    else
                        ConnectorFixtures.m365Chat

                expectSent sent[1] (base' + route) token golden)

        testCase "Microsoft 365 Copilot — vision is refused before any request" (fun () ->
            let sent, result = m365 "vision"
            Expect.isEmpty sent "nothing reached the wire"

            match result with
            | Error(UnsupportedCapability("vision", _)) -> ()
            | other -> failtestf "expected the vision refusal, got %A" other)
    ]
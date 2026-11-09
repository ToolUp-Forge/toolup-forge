// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The Microsoft 365 Copilot provider, offline: the transcript mapping,
/// the request body, the reply selection, the event-stream framing, and
/// whole sends over a stub HTTP handler. No live arm: the Chat API takes a
/// delegated user token only, which no unattended run can mint.
module ToolUp.AIProviders.Tests.Tests.Microsoft365CopilotProviderTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Transport
open Microsoft365CopilotWire
open Microsoft365CopilotAIProvider

let private user = AIProviderMessage.text "user"
let private assistant = AIProviderMessage.text "assistant"

/// A conversation document: the user's echo, then the reply.
let private conversation (prompt: string) (reply: string) =
    JsonSerializer.Serialize {|
        id = "conv-1"
        messages = [| {| text = prompt |}; {| text = reply |} |]
    |}

/// Records each request and answers from a queue of canned responses.
type private StubHandler(responses: (HttpStatusCode * string * string) list) =
    inherit HttpMessageHandler()
    let mutable queue = responses
    let seen = ResizeArray<string * string * string option>()

    member _.Seen = List.ofSeq seen

    override _.SendAsync(request: HttpRequestMessage, _: CancellationToken) : Task<HttpResponseMessage> = task {
        let! body =
            if isNull request.Content then
                Task.FromResult ""
            else
                request.Content.ReadAsStringAsync()

        let auth =
            match request.Headers.Authorization with
            | null -> None
            | value -> Some(string value)

        seen.Add(request.RequestUri.AbsoluteUri, body, auth)

        match queue with
        | (status, contentType, payload) :: rest ->
            queue <- rest
            let response = new HttpResponseMessage(status)
            response.Content <- new StringContent(payload, Encoding.UTF8, contentType)
            return response
        | [] -> return new HttpResponseMessage(HttpStatusCode.InternalServerError)
    }

let private created =
    HttpStatusCode.Created, "application/json", """{"id":"conv-1","turnCount":0}"""

let private providerOver (handler: StubHandler) (token: string option) =
    createWithHttpClient
        (FromAccessToken(fun () -> async { return token }))
        (Microsoft365CopilotOptions.defaults
         |> Microsoft365CopilotOptions.withTimeZone "Europe/London")
        (new HttpClient(handler))

let private oneAttempt = {
    RetryPolicy.defaults with
        MaxAttempts = 1
}

let tests =
    testList "Microsoft 365 Copilot provider" [
        testList "turn mapping" [
            test "the last user message is the question; the system prompt and earlier turns are context" {
                let turn =
                    buildTurn (Some "Be brief.") [ user "first"; assistant "answer one"; user "second" ]
                    |> Result.defaultWith (fun e -> failtestf "refused: %A" e)

                Expect.equal turn.Prompt "second" "the question"
                Expect.equal turn.AdditionalContext.Length 2 "instructions then history"
                Expect.stringContains turn.AdditionalContext[0] "Be brief." "the system prompt"
                Expect.stringContains turn.AdditionalContext[1] "User: first" "earlier user turn"
                Expect.stringContains turn.AdditionalContext[1] "Assistant: answer one" "earlier reply"
                Expect.isFalse (turn.AdditionalContext[1].Contains "second") "the question is not repeated"
            }

            test "a single message with no system prompt sends no context" {
                let turn =
                    buildTurn None [ user "hi" ] |> Result.defaultWith (fun e -> failtestf "%A" e)

                Expect.isEmpty turn.AdditionalContext "nothing to ground"
            }

            test "a turn with no user message is refused" {
                Expect.equal (buildTurn None [ assistant "x" ]) (Error NoUserMessage) "no question"
            }

            test "an image is refused" {
                let image =
                    AIProviderMessage.multipart "user" [
                        ImagePart {
                            MediaType = "image/png"
                            Source = Url "https://example.com/a.png"
                        }
                    ]

                Expect.equal (buildTurn None [ image ]) (Error ImageInput) "text only"
            }
        ]

        testList "request body" [
            test "carries the message, the context, the time zone, files and the web toggle" {
                let turn = {
                    Prompt = "q"
                    AdditionalContext = [ "ctx" ]
                }

                use doc =
                    JsonDocument.Parse(
                        chatBody "Europe/London" (Some false) [ "https://contoso.sharepoint.com/a.docx" ] turn
                    )

                let root = doc.RootElement
                Expect.equal (root.GetProperty("message").GetProperty("text").GetString()) "q" "message.text"

                Expect.equal
                    (root.GetProperty("additionalContext").Item(0).GetProperty("text").GetString())
                    "ctx"
                    "context"

                Expect.equal (root.GetProperty("locationHint").GetProperty("timeZone").GetString()) "Europe/London" "tz"
                let resources = root.GetProperty "contextualResources"

                Expect.equal
                    (resources.GetProperty("files").Item(0).GetProperty("uri").GetString())
                    "https://contoso.sharepoint.com/a.docx"
                    "file"

                Expect.isFalse (resources.GetProperty("webContext").GetProperty("isWebEnabled").GetBoolean()) "web off"
            }

            test "omits optional members it was not given" {
                use doc =
                    JsonDocument.Parse(chatBody "UTC" None [] { Prompt = "q"; AdditionalContext = [] })

                let has name =
                    fst (doc.RootElement.TryGetProperty(name: string))

                Expect.isFalse (has "additionalContext") "no context"
                Expect.isFalse (has "contextualResources") "no resources"
            }
        ]

        testList "reply selection" [
            test "the reply is the last message that is not the echoed prompt" {
                Expect.equal (tryReplyText "q" (conversation "q" "the answer")) (Some "the answer") "reply"
            }

            test "a conversation holding only the echo has an empty reply" {
                let json = JsonSerializer.Serialize {| messages = [| {| text = "q" |} |] |}
                Expect.equal (tryReplyText "q" json) (Some "") "nothing yet"
            }

            test "a document that is not a conversation reads as None" {
                Expect.isNone (tryReplyText "q" """{"error":{}}""") "no messages"
                Expect.isNone (tryReplyText "q" "not json") "not json"
            }
        ]

        testList "event stream" [
            let feedAll (lines: string list) =
                let frame, events =
                    lines
                    |> List.fold
                        (fun (frame, acc) line ->
                            let next, completed = SseFrame.feed frame line

                            next,
                            (match completed with
                             | Some e -> e :: acc
                             | None -> acc))
                        (SseFrame.empty, [])

                List.rev (
                    match SseFrame.finish frame with
                    | Some e -> e :: events
                    | None -> events
                )

            test "events end at a blank line; comments and other fields are skipped" {
                Expect.equal
                    (feedAll [
                        ": keep-alive"
                        "event: message"
                        "data: {\"a\":1}"
                        ""
                        "data: {\"a\":2}"
                        ""
                    ])
                    [ "{\"a\":1}"; "{\"a\":2}" ]
                    "two events"
            }

            test "pretty-printed data continues on unprefixed lines, and an unterminated event is kept" {
                Expect.equal (feedAll [ "data: {"; "  \"a\": 1"; "}" ]) [ "{\n  \"a\": 1\n}" ] "one event"
            }

            test "a growing snapshot yields its new text; a rewrite yields nothing" {
                Expect.equal (streamDelta "Hel" "Hello") (Some "lo") "extension"
                Expect.isNone (streamDelta "Hello" "Hello") "unchanged"
                Expect.isNone (streamDelta "Hello" "Goodbye") "rewrite"
            }
        ]

        testList "sends" [
            testAsync "a send opens a conversation, then chats with the user's bearer token" {
                let handler =
                    new StubHandler(
                        [
                            created
                            HttpStatusCode.OK, "application/json", conversation "hi" "Hello there."
                        ]
                    )

                let! result = (providerOver handler (Some "tok")).SendMessage([ user "hi" ], [], None, None, oneAttempt)
                let response = Expect.wantOk result "a reply"
                Expect.equal response.Content "Hello there." "reply text"
                Expect.isEmpty response.ToolCalls "no tools"

                match handler.Seen with
                | [ (createUrl, createBody, auth1); (chatUrl, _, auth2) ] ->
                    Expect.equal createUrl "https://workiq.svc.cloud.microsoft/rest/conversations" "create"
                    Expect.equal createBody "{}" "empty body"
                    Expect.equal chatUrl "https://workiq.svc.cloud.microsoft/rest/conversations/conv-1/chat" "chat"
                    Expect.equal auth1 (Some "Bearer tok") "bearer on create"
                    Expect.equal auth2 (Some "Bearer tok") "bearer on chat"
                | other -> failtestf "expected two requests, saw %A" other
            }

            testAsync "a streamed send hands over deltas that add up to the reply" {
                let sse =
                    [
                        "data: " + conversation "hi" "Hel"
                        ""
                        "data: " + conversation "hi" "Hello"
                        ""
                        "data: " + conversation "hi" "Hello there."
                        ""
                    ]
                    |> String.concat "\n"

                let handler =
                    new StubHandler([ created; HttpStatusCode.OK, "text/event-stream", sse ])

                let deltas = ResizeArray<string>()

                let! result =
                    (providerOver handler (Some "tok"))
                        .SendMessage([ user "hi" ], [], None, Some deltas.Add, oneAttempt)

                let response = Expect.wantOk result "a reply"
                Expect.equal response.Content "Hello there." "final text"
                Expect.equal (String.concat "" deltas) "Hello there." "deltas add up"

                Expect.stringContains (let (url, _, _) = handler.Seen[1] in url) "/chatOverStream" "streamed route"
            }

            testAsync "no user token refuses before any request" {
                let handler = new StubHandler([])
                let! result = (providerOver handler None).SendMessage([ user "hi" ], [], None, None, oneAttempt)

                match result with
                | Error(PermanentClient(401, _)) -> ()
                | other -> failtestf "expected a 401 refusal, got %A" other

                Expect.isEmpty handler.Seen "nothing sent"
            }

            testAsync "a forbidden conversation is a permanent error" {
                let handler =
                    new StubHandler([ HttpStatusCode.Forbidden, "application/json", """{"error":"no licence"}""" ])

                let! result =
                    (providerOver handler (Some "tok")).SendMessage([ user "hi" ], [], None, None, RetryPolicy.defaults)

                match result with
                | Error(PermanentClient(403, _)) -> ()
                | other -> failtestf "expected a 403, got %A" other

                Expect.equal handler.Seen.Length 1 "not retried"
            }
        ]

        test "the descriptor declares what the Chat API offers" {
            Expect.equal descriptor.Id "microsoft-365-copilot" "id"
            Expect.isTrue descriptor.Capabilities.Streaming "streams"
            Expect.isFalse descriptor.Capabilities.ToolUse "no tools"
            Expect.isFalse descriptor.Capabilities.Vision "no images"
        }
    ]
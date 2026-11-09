// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

// ─── The connector kernel ────────────────────────────────────────
//
// A provider call is one function. Everything a vendor differs on is data
// in a `Connector`: where the call goes, the headers its credential yields,
// the body, how the reply parses and how the stream assembles. Everything
// that is the same for every vendor is `ProviderCall.send`: one vision
// preflight, one credential step, one attempt (buffered or streaming, its
// timeout linked to the caller's token), one classification and one retry
// over the policy and its rate-limit floor. `ConnectorProvider` implements
// `IAIProvider` and `IAIProviderModelOverride` once over it, so a connector
// is a record and its `create*` helpers.

open System
open System.IO
open System.Net.Http
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Transport
open ToolUp.Platform.Infra.Transport
open ToolUp.Platform.ProviderExceptions
open ToolUp.AI.Wire

/// One call as a connector sees it: the model that serves it, the options
/// the caller passed, and the turn.
type ProviderRequest = {
    /// The model the provider was constructed on.
    ConfiguredModel: string
    /// The model that serves this call (`ConfiguredModel` unless an override
    /// was asked for and the connector can serve it).
    Model: string
    /// The per-call options, for the knobs a connector reads (a reply cap,
    /// an effort).
    Options: AIProviderCallOptions
    Messages: AIProviderMessage list
    Tools: AIProviderToolDef list
    SystemPrompt: string option
    /// The JSON Schema of a structured call; `None` for a chat call.
    Schema: string option
    /// Whether the reply streams (the caller passed an `onStream`).
    Streaming: bool
}

/// Where one call is posted.
[<RequireQualifiedAccess>]
type Endpoint =
    /// A URL, absolute or relative to the connector's client.
    | At of url: string
    /// A stateful API: post `body` to `url` first, and read the call's URL
    /// from that response. Both requests are one attempt.
    | Opened of url: string * body: string * route: (string -> Result<string, AIProviderError>)

/// How a connector answers a structured call.
[<RequireQualifiedAccess>]
type StructuredOutput =
    /// The vendor's own structured-output mode, in the connector's `Body`.
    | Native
    /// A chat call carrying the schema as an instruction
    /// (`IAIProviderDefaults.sendStructuredViaFallback`).
    | ViaPrompt

/// One vendor, as data. Pure except `Credential`.
type Connector<'S> = {
    /// The provider's capabilities on a model.
    Capabilities: string -> AIProviderCapabilities
    /// Whether the connector can serve a model id (a vocabulary test).
    CanServe: string -> bool
    /// Why an id it cannot serve fell back to the configured model.
    UnservedReason: string
    /// Whether a model accepts image input.
    IsVisionCapable: string -> bool
    StructuredOutput: StructuredOutput
    /// The client the calls go out on (its base address and instance
    /// timeout already set).
    Client: HttpClient
    /// The headers the credential yields for one call, or why there are none.
    Credential: unit -> Async<Result<(string * string) list, AIProviderError>>
    Endpoint: ProviderRequest -> Endpoint
    Body: ProviderRequest -> Result<string, AIProviderError>
    /// Parse a 2xx body.
    ParseResponse: ProviderRequest -> string -> Result<AIProviderResponse, AIProviderError>
    /// The stream assembly for one call.
    Stream: ProviderRequest -> StreamProcessor<'S>
    /// The rate-limit floor the vendor needs when the caller's policy names
    /// none (`RetryPolicy.RateLimitFloor`).
    RateLimitFloor: TimeSpan option
    /// The pause between attempts.
    Pause: TimeSpan -> Async<unit>
}

module Connector =
    /// The production pause.
    let sleep (delay: TimeSpan) : Async<unit> = Async.Sleep delay

    /// A credential that is one header built from a secret, refused as a
    /// deployment misconfiguration when the secret is absent or blank.
    let secretHeader
        (fetch: unit -> Async<string option>)
        (missing: string)
        (header: string -> (string * string) list)
        : unit -> Async<Result<(string * string) list, AIProviderError>> =
        fun () -> async {
            match! fetch () with
            | Some secret when not (String.IsNullOrWhiteSpace secret) -> return Ok(header secret)
            | _ -> return Error(PermanentClient(0, missing))
        }

module ProviderCall =

    /// The caller's token, with the policy's per-call timeout.
    let private attemptToken (policy: RetryPolicy) (cancellationToken: CancellationToken) =
        let cts = CancellationTokenSource.CreateLinkedTokenSource cancellationToken

        match policy.Timeout with
        | Some t ->
            cts.CancelAfter(TimeSpan.FromMilliseconds(float (RetryPolicy.clampTimeoutMs (int t.TotalMilliseconds))))
        | None -> ()

        cts

    /// Resolve the URL one attempt posts to, opening the conversation first
    /// where the API is stateful.
    let private resolveUrl
        (transport: HttpClientTransport)
        (headers: (string * string) list)
        (endpoint: Endpoint)
        (token: CancellationToken)
        : Async<Result<string, AIProviderError>> =
        async {
            match endpoint with
            | Endpoint.At url -> return Ok url
            | Endpoint.Opened(url, body, route) ->
                let! response = transport.SendWith(HttpRequest.post url headers body, token)

                if HttpResponse.isSuccess response then
                    return route response.Body
                else
                    return Error(ErrorClassifier.classifyStatus response.StatusCode response.Body)
        }

    let private timedOut (policy: RetryPolicy) =
        $"Request timed out after {RetryPolicy.timeoutDescription policy}"

    /// One buffered attempt.
    let private buffered
        (connector: Connector<'S>)
        (transport: HttpClientTransport)
        (request: ProviderRequest)
        (headers: (string * string) list)
        (body: string)
        (policy: RetryPolicy)
        (cancellationToken: CancellationToken)
        : Async<Result<AIProviderResponse, AIProviderError>> =
        async {
            use cts = attemptToken policy cancellationToken

            try
                match! resolveUrl transport headers (connector.Endpoint request) cts.Token with
                | Error e -> return Error e
                | Ok url ->
                    let! response = transport.SendWith(HttpRequest.post url headers body, cts.Token)

                    if not (HttpResponse.isSuccess response) then
                        return Error(ErrorClassifier.classifyStatus response.StatusCode response.Body)
                    else
                        try
                            return connector.ParseResponse request response.Body
                        with ex ->
                            return Error(MalformedResponse ex.Message)
            with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                return raise (OperationCanceledException cancellationToken)
            | :? OperationCanceledException -> return Error(TransientNetwork(timedOut policy))
            | ProviderException(ex: HttpRequestException) -> return Error(TransientNetwork ex.Message)
            | ex -> return Error(TransientNetwork ex.Message)
        }

    /// One streaming attempt. A failure before any text was forwarded may be
    /// retried; after it, the call is `StreamingAborted` with what arrived.
    let private streaming
        (connector: Connector<'S>)
        (transport: HttpClientTransport)
        (request: ProviderRequest)
        (headers: (string * string) list)
        (body: string)
        (onStream: string -> unit)
        (policy: RetryPolicy)
        (cancellationToken: CancellationToken)
        : Async<Result<AIProviderResponse, AIProviderError>> =
        async {
            use cts = attemptToken policy cancellationToken
            let processor = connector.Stream request
            let mutable forwarded = ""

            let forward (text: string option) =
                text
                |> Option.iter (fun t ->
                    onStream t
                    forwarded <- forwarded + t)

            let failure (detail: string) =
                if forwarded <> "" then
                    Error(StreamingAborted(forwarded, detail))
                else
                    Error(TransientNetwork detail)

            try
                match! resolveUrl transport headers (connector.Endpoint request) cts.Token with
                | Error e -> return Error e
                | Ok url ->
                    use! response = transport.SendForStreaming(HttpRequest.post url headers body, cts.Token)

                    if not response.IsSuccessStatusCode then
                        let! errorBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                        return Error(ErrorClassifier.classifyStatus (int response.StatusCode) errorBody)
                    else
                        let! stream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
                        use reader = new StreamReader(stream)
                        let mutable state = processor.Initial
                        let mutable reading = true

                        while reading do
                            let! line = reader.ReadLineAsync(cts.Token).AsTask() |> Async.AwaitTask

                            if isNull line then
                                reading <- false
                            else
                                let next, text = processor.Step state line
                                state <- next
                                forward text

                        let trailing, reply = processor.Finish state
                        forward trailing
                        return Ok reply
            with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                return raise (OperationCanceledException cancellationToken)
            | :? OperationCanceledException when cts.IsCancellationRequested ->
                return
                    if forwarded <> "" then
                        failure
                            $"Timed out after {RetryPolicy.timeoutDescription policy} with partial content delivered"
                    else
                        failure (timedOut policy)
            | ProviderException(ex: HttpRequestException) -> return failure ex.Message
            | ex -> return failure ex.Message
        }

    /// The ONE provider call: vision preflight, body, credential, then each
    /// attempt (buffered, or streaming when `onStream` is given) under the
    /// policy, the connector's rate-limit floor filling in when the policy
    /// names none. Cancelling `cancellationToken` aborts the in-flight
    /// request and the call raises `OperationCanceledException`; a timeout is
    /// an attempt failure the policy may retry.
    let send
        (connector: Connector<'S>)
        (request: ProviderRequest)
        (onStream: (string -> unit) option)
        (policy: RetryPolicy)
        (cancellationToken: CancellationToken)
        : Async<Result<AIProviderResponse, AIProviderError>> =
        async {
            if
                request.Messages |> List.exists AIProviderMessage.isMultimodal
                && not (connector.IsVisionCapable request.Model)
            then
                return
                    Error(
                        UnsupportedCapability("vision", sprintf "Model '%s' does not accept image input." request.Model)
                    )
            else
                match connector.Body request with
                | Error e -> return Error e
                | Ok body ->
                    match! connector.Credential() with
                    | Error e -> return Error e
                    | Ok headers ->
                        let transport = HttpClientTransport(connector.Client)

                        let policy = {
                            policy with
                                RateLimitFloor = policy.RateLimitFloor |> Option.orElse connector.RateLimitFloor
                        }

                        let attempt () =
                            match onStream with
                            | Some callback ->
                                streaming connector transport request headers body callback policy cancellationToken
                            | None -> buffered connector transport request headers body policy cancellationToken

                        return! RetryRunner.runPausing connector.Pause policy attempt
        }

/// `IAIProvider` and `IAIProviderModelOverride`, implemented once over a
/// `Connector` bound to its configured `model`. A per-call override is served
/// by naming the model on the request, never by a second instance; the
/// ambient async cancellation token rides into the call.
type ConnectorProvider<'S>(connector: Connector<'S>, model: string) =

    let call
        (options: AIProviderCallOptions)
        (messages: AIProviderMessage list)
        (tools: AIProviderToolDef list)
        (systemPrompt: string option)
        (schema: string option)
        (onStream: (string -> unit) option)
        (retryPolicy: RetryPolicy)
        : Async<Result<AIProviderCallResponse, AIProviderError>> =
        async {
            let served, outcome =
                ModelOverrideOutcome.resolve connector.CanServe connector.UnservedReason model options

            let request = {
                ConfiguredModel = model
                Model = served
                Options = options
                Messages = messages
                Tools = tools
                SystemPrompt = systemPrompt
                Schema = schema
                Streaming = onStream.IsSome
            }

            let! cancellationToken = Async.CancellationToken
            let! result = ProviderCall.send connector request onStream retryPolicy cancellationToken
            return result |> Result.map (fun response -> { Response = response; Model = outcome })
        }

    let responseOf (call: Async<Result<AIProviderCallResponse, AIProviderError>>) = async {
        let! result = call
        return result |> Result.map _.Response
    }

    /// The connector this provider runs on.
    member _.Connector = connector

    interface IAIProvider with
        member _.Capabilities = connector.Capabilities model

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) =
            call AIProviderCallOptions.none messages tools systemPrompt None onStream retryPolicy
            |> responseOf

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            match connector.StructuredOutput with
            | StructuredOutput.Native ->
                call AIProviderCallOptions.none messages tools systemPrompt (Some schema) None retryPolicy
                |> responseOf
            | StructuredOutput.ViaPrompt ->
                IAIProviderDefaults.sendStructuredViaFallback this messages tools systemPrompt schema retryPolicy

    interface IAIProviderModelOverride with
        member _.SendMessageWith(options, messages, tools, systemPrompt, onStream, retryPolicy) =
            call options messages tools systemPrompt None onStream retryPolicy

        member _.SendStructuredMessageWith(options, messages, tools, systemPrompt, schema, retryPolicy) =
            match connector.StructuredOutput with
            | StructuredOutput.Native -> call options messages tools systemPrompt (Some schema) None retryPolicy
            | StructuredOutput.ViaPrompt ->
                let served, outcome =
                    ModelOverrideOutcome.resolve connector.CanServe connector.UnservedReason model options

                IAIProviderDefaults.sendStructuredViaFallback
                    (ConnectorProvider(connector, served))
                    messages
                    tools
                    systemPrompt
                    schema
                    retryPolicy
                |> AIProviderCallResponse.attach outcome
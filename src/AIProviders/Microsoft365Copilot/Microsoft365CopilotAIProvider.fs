// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Microsoft365CopilotAIProvider

open System
open System.IO
open System.Net.Http
open System.Threading
open Azure.Core
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Infra.Transport
open ToolUp.Platform.ProviderExceptions
open ToolUp.Platform.Transport
open ToolUp.AI
open Microsoft365CopilotWire

// ─── Microsoft 365 Copilot provider (Work IQ Chat API) ───────────────
//
// Puts the assistant's turns to Microsoft 365 Copilot itself, so answers
// are grounded in the signed-in user's mail, calendar, chats and files
// under the organisation's own access controls. This is a different
// service from Azure OpenAI (the `ToolUp.AIProviders.Copilot` package):
// there is no model to choose, no tool calling and no system role, and
// the caller must be a licensed USER.
//
// Three properties of the API shape everything below:
//
//   1. Delegated auth only. The Work IQ scope
//      `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask` has no
//      application permission, so the token must represent a signed-in
//      user who holds a Microsoft 365 Copilot licence, in a tenant whose
//      administrator has enabled Work IQ and consented to the scope. A
//      server-side deployment normally obtains it on behalf of the
//      requesting user (an `OnBehalfOfCredential`, or a token source that
//      reads the user's token from the request).
//   2. Server-side conversations. Each call opens a fresh conversation
//      and sends the transcript as grounding (see `Microsoft365CopilotWire`),
//      which keeps the provider stateless between calls.
//   3. Snapshot streaming. `chatOverStream` events carry the whole reply
//      so far; the provider turns them into deltas for `onStream`.
//
// Distributed-ready: no state is held between calls.

/// Stable provider identifier used in user configs and settings.
[<Literal>]
let ProviderId = "microsoft-365-copilot"

/// Human-readable name for settings UI labels.
[<Literal>]
let DisplayName = "Microsoft 365 Copilot"

/// Copilot chooses its own model, so the provider reports this fixed
/// name wherever a model id is expected.
[<Literal>]
let DefaultModel = "microsoft-365-copilot"

/// The only "model" the settings UI should offer.
let KnownModels = [ DefaultModel ]

/// Work IQ REST root (the GA route). The `/beta` route is
/// `https://workiq.svc.cloud.microsoft/rest/beta`.
[<Literal>]
let DefaultBaseUrl = "https://workiq.svc.cloud.microsoft/rest"

/// The delegated Entra scope every Work IQ Chat API call needs.
[<Literal>]
let WorkIQAskScope = "api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask"

/// The time zone sent as `locationHint` when none is configured. Copilot
/// uses it to read relative dates ("tomorrow at 9").
[<Literal>]
let DefaultTimeZone = "UTC"

/// What the provider declares. No tool use, vision, caching or triage:
/// the Chat API offers none of them.
let capabilities: AIProviderCapabilities = {
    Streaming = true
    ToolUse = false
    Vision = false
    SupportsPromptCaching = false
    SupportsTriage = false
    TriageModelId = None
    ProviderName = ProviderId
    Model = DefaultModel
}

/// The descriptor a composition root lists in its provider factory.
let descriptor: AIProviderDescriptor = {
    Id = ProviderId
    DisplayName = DisplayName
    SupportedModels = KnownModels
    DefaultModel = DefaultModel
    Capabilities = capabilities
}

/// Per-deployment settings for the Chat API request.
type Microsoft365CopilotOptions = {
    /// Work IQ REST root, without a trailing slash.
    BaseUrl: string
    /// IANA time zone sent as the required `locationHint`.
    TimeZone: string
    /// `Some false` turns web-search grounding off, so Copilot answers
    /// from enterprise data only; `None` leaves the tenant's default.
    WebSearch: bool option
    /// OneDrive / SharePoint file URIs Copilot should read as context on
    /// every call.
    Files: string list
}

module Microsoft365CopilotOptions =
    /// The GA route, UTC, the tenant's web-grounding default, no files.
    let defaults: Microsoft365CopilotOptions = {
        BaseUrl = DefaultBaseUrl
        TimeZone = DefaultTimeZone
        WebSearch = None
        Files = []
    }

    /// Sets the Work IQ REST root (for example the `/beta` route).
    let withBaseUrl (baseUrl: string) (options: Microsoft365CopilotOptions) = { options with BaseUrl = baseUrl }

    /// Sets the IANA time zone sent as `locationHint`.
    let withTimeZone (timeZone: string) (options: Microsoft365CopilotOptions) = { options with TimeZone = timeZone }

    /// Sets whether Copilot may ground answers in web search.
    let withWebSearch (enabled: bool) (options: Microsoft365CopilotOptions) = {
        options with
            WebSearch = Some enabled
    }

    /// Adds OneDrive / SharePoint file URIs as context for every call.
    let withFiles (uris: string list) (options: Microsoft365CopilotOptions) = {
        options with
            Files = options.Files @ uris
    }

/// Where the user's delegated access token comes from.
type Microsoft365CopilotTokenSource =
    /// An `Azure.Core` credential that yields a USER token, for example
    /// `OnBehalfOfCredential`, `InteractiveBrowserCredential` or
    /// `DeviceCodeCredential`. The provider asks it for `WorkIQAskScope`.
    | FromCredential of credential: TokenCredential
    /// A function returning a token already acquired for `WorkIQAskScope`,
    /// called once per send (before any retry). `None` or blank refuses
    /// the call as not signed in.
    | FromAccessToken of fetch: (unit -> Async<string option>)

// Shared per-process client, built through the platform client factory so
// the egress policy sees the Work IQ origin. Absolute request URLs; no
// BaseAddress. Per-request auth rides on the request, never the client.
let private sharedClient =
    lazy
        (let c =
            ToolUp.Platform.Compose.PlatformHttpClient.create ToolUp.Platform.EgressSurface.AIProvider

         c.Timeout <- TimeSpan.FromMinutes(5.0)
         c)

let private refusal (refused: TurnRefusal) : AIProviderError =
    match refused with
    | NoUserMessage -> PermanentClient(0, "Microsoft 365 Copilot needs a user message to answer; the turn has none.")
    | ImageInput ->
        UnsupportedCapability(
            "vision",
            "Microsoft 365 Copilot's Chat API accepts text only; the turn carries an image."
        )

type Microsoft365CopilotProvider
    (tokenSource: Microsoft365CopilotTokenSource, options: Microsoft365CopilotOptions, client: HttpClient) =

    let baseUrl = options.BaseUrl.TrimEnd('/')

    let acquireToken () : Async<Result<string, AIProviderError>> = async {
        match tokenSource with
        | FromAccessToken fetch ->
            let! token = fetch ()

            match token with
            | Some t when not (String.IsNullOrWhiteSpace t) -> return Ok t
            | _ ->
                return
                    Error(
                        PermanentClient(
                            401,
                            "No Microsoft 365 user token is available. Microsoft 365 Copilot needs a delegated token for "
                            + WorkIQAskScope
                        )
                    )
        | FromCredential credential ->
            try
                let! ct = Async.CancellationToken
                let request = TokenRequestContext [| WorkIQAskScope |]
                let! token = credential.GetTokenAsync(request, ct).AsTask() |> Async.AwaitTask
                return Ok token.Token
            with ex ->
                return
                    Error(
                        TransientNetwork(
                            sprintf "Entra ID token acquisition failed for Microsoft 365 Copilot: %s" ex.Message
                        )
                    )
    }

    /// Open a conversation; its id is the path of the chat call.
    let createConversation (transport: IHttpTransport) headers : Async<Result<string, AIProviderError>> = async {
        let! response = transport.Send(HttpRequest.post (baseUrl + "/conversations") headers "{}")

        if not (HttpResponse.isSuccess response) then
            return Error(ErrorClassifier.classifyStatus response.StatusCode response.Body)
        else
            match tryConversationId response.Body with
            | Some id -> return Ok id
            | None -> return Error(MalformedResponse "The Work IQ conversation response carried no id.")
    }

    let chatUrl (conversationId: string) (verb: string) =
        sprintf "%s/conversations/%s/%s" baseUrl (Uri.EscapeDataString conversationId) verb

    let sendSync (transport: HttpClientTransport) headers (turn: CopilotTurn) body = async {
        try
            let transport = transport :> IHttpTransport
            let! conversation = createConversation transport headers

            match conversation with
            | Error e -> return Error e
            | Ok id ->
                let! response = transport.Send(HttpRequest.post (chatUrl id "chat") headers body)

                if not (HttpResponse.isSuccess response) then
                    return Error(ErrorClassifier.classifyStatus response.StatusCode response.Body)
                else
                    match tryReplyText turn.Prompt response.Body with
                    | Some reply -> return Ok(toResponse reply)
                    | None -> return Error(MalformedResponse "The Work IQ chat response was not a conversation.")
        with
        | :? OperationCanceledException -> return Error(TransientNetwork "Microsoft 365 Copilot request timed out")
        | ProviderException(ex: HttpRequestException) ->
            return Error(ErrorClassifier.classifyTransportFailure ex.Message)
        | ex -> return Error(ErrorClassifier.classifyTransportFailure ex.Message)
    }

    let sendStreaming
        (transport: HttpClientTransport)
        headers
        (turn: CopilotTurn)
        body
        (onStream: string -> unit)
        (retryPolicy: RetryPolicy)
        =
        async {
            let streamed = ref ""
            let latest = ref ""

            use cts =
                match retryPolicy.Timeout with
                | Some t ->
                    new CancellationTokenSource(
                        TimeSpan.FromMilliseconds(float (RetryPolicy.clampTimeoutMs (int t.TotalMilliseconds)))
                    )
                | None -> new CancellationTokenSource()

            let accept (data: string) =
                match tryReplyText turn.Prompt data with
                | Some reply when reply <> "" ->
                    latest.Value <- reply

                    match streamDelta streamed.Value reply with
                    | Some delta ->
                        streamed.Value <- reply
                        onStream delta
                    | None -> ()
                | _ -> ()

            let failure (detail: string) =
                if streamed.Value <> "" then
                    Error(StreamingAborted(streamed.Value, detail))
                else
                    Error(TransientNetwork detail)

            try
                let! conversation = createConversation (transport :> IHttpTransport) headers

                match conversation with
                | Error e -> return Error e
                | Ok id ->
                    let request = HttpRequest.post (chatUrl id "chatOverStream") headers body
                    let! response = transport.SendForStreaming(request, cts.Token)
                    use response = response

                    if not response.IsSuccessStatusCode then
                        let! errorBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                        return Error(ErrorClassifier.classifyStatus (int response.StatusCode) errorBody)
                    else
                        let! stream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
                        use reader = new StreamReader(stream)
                        let mutable frame = SseFrame.empty
                        let mutable reading = true

                        while reading do
                            let! line = reader.ReadLineAsync(cts.Token).AsTask() |> Async.AwaitTask

                            if isNull line then
                                reading <- false
                            else
                                let next, completed = SseFrame.feed frame line
                                frame <- next
                                completed |> Option.iter accept

                        SseFrame.finish frame |> Option.iter accept
                        return Ok(toResponse latest.Value)
            with
            | :? OperationCanceledException when cts.IsCancellationRequested ->
                return failure $"Timed out after {RetryPolicy.timeoutDescription retryPolicy}"
            | ProviderException(ex: HttpRequestException) -> return failure ex.Message
            | ex -> return failure ex.Message
        }

    /// A provider using the shared platform HTTP client.
    new(tokenSource: Microsoft365CopilotTokenSource, options: Microsoft365CopilotOptions) =
        Microsoft365CopilotProvider(tokenSource, options, sharedClient.Value)

    /// A provider with the default options (GA route, UTC).
    new(tokenSource: Microsoft365CopilotTokenSource) =
        Microsoft365CopilotProvider(tokenSource, Microsoft365CopilotOptions.defaults)

    interface IAIProvider with
        member _.Capabilities = capabilities

        // `tools` are not sent: the Chat API cannot call them, and the
        // capability flag tells the agent loop not to offer any.
        member _.SendMessage(messages, _tools, systemPrompt, onStream, retryPolicy) = async {
            match buildTurn systemPrompt messages with
            | Error refused -> return Error(refusal refused)
            | Ok turn ->
                let! token = acquireToken ()

                match token with
                | Error e -> return Error e
                | Ok token ->
                    let headers = [ "Authorization", "Bearer " + token ]
                    let body = chatBody options.TimeZone options.WebSearch options.Files turn
                    let transport = HttpClientTransport(client, ?timeout = retryPolicy.Timeout)

                    let attempt () =
                        match onStream with
                        | Some callback -> sendStreaming transport headers turn body callback retryPolicy
                        | None -> sendSync transport headers turn body

                    return! RetryRunner.run retryPolicy attempt
        }

        // No native structured output: the schema rides as an instruction
        // and the reply is checked to be JSON.
        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback this messages tools systemPrompt schema retryPolicy

// ─── Factory helpers ─────────────────────────────────────────────────

/// A provider over an `Azure.Core` credential that yields a user token
/// (for example `OnBehalfOfCredential`), with the default options.
let create (credential: TokenCredential) : IAIProvider =
    Microsoft365CopilotProvider(FromCredential credential) :> IAIProvider

/// A provider over a user-token credential with explicit options.
let createWithOptions (credential: TokenCredential) (options: Microsoft365CopilotOptions) : IAIProvider =
    Microsoft365CopilotProvider(FromCredential credential, options) :> IAIProvider

/// A provider whose token comes from a function, called once per send.
/// The usual server shape: read the requesting user's Work IQ token
/// from the request context.
let createWithTokenSource (fetch: unit -> Async<string option>) (options: Microsoft365CopilotOptions) : IAIProvider =
    Microsoft365CopilotProvider(FromAccessToken fetch, options) :> IAIProvider

/// A provider over a fixed access token, for the BYOK factory's
/// `apiKey -> IAIProvider` builder. A Work IQ token is a short-lived
/// user token, not a key: it stops working when it expires (about an
/// hour), so prefer a credential or a token source in a deployment.
let createWithAccessToken (accessToken: string) : IAIProvider =
    createWithTokenSource (fun () -> async { return Some accessToken }) Microsoft365CopilotOptions.defaults

/// The BYOK factory's `apiKey -> model -> IAIProvider` shape. Copilot has
/// one model, so `model` is ignored.
let createWithAccessTokenAndModel (accessToken: string) (_model: string) : IAIProvider =
    createWithAccessToken accessToken

/// A provider over a caller-supplied `HttpClient` — a custom handler, a
/// proxy, or an offline test.
let createWithHttpClient
    (tokenSource: Microsoft365CopilotTokenSource)
    (options: Microsoft365CopilotOptions)
    (client: HttpClient)
    : IAIProvider =
    Microsoft365CopilotProvider(tokenSource, options, client) :> IAIProvider
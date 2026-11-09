// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Microsoft365CopilotAIProvider

open System
open System.Net.Http
open Azure.Core
open ToolUp.Platform.AI
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

let private credential (tokenSource: Microsoft365CopilotTokenSource) =
    fun () -> async {
        match tokenSource with
        | FromAccessToken fetch ->
            match! fetch () with
            | Some t when not (String.IsNullOrWhiteSpace t) -> return Ok [ "Authorization", "Bearer " + t ]
            | _ ->
                return
                    Error(
                        PermanentClient(
                            401,
                            "No Microsoft 365 user token is available. Microsoft 365 Copilot needs a delegated token for "
                            + WorkIQAskScope
                        )
                    )
        | FromCredential tokenCredential ->
            try
                let! ct = Async.CancellationToken
                let request = TokenRequestContext [| WorkIQAskScope |]
                let! token = tokenCredential.GetTokenAsync(request, ct).AsTask() |> Async.AwaitTask
                return Ok [ "Authorization", "Bearer " + token.Token ]
            with ex ->
                return
                    Error(
                        TransientNetwork(
                            sprintf "Entra ID token acquisition failed for Microsoft 365 Copilot: %s" ex.Message
                        )
                    )
    }

/// The Microsoft 365 Copilot connector over `client`, its delegated token from
/// `tokenSource`, configured by `options`. A call opens a Work IQ
/// conversation and asks in it (both requests are one attempt); a
/// structured call is a chat call carrying the schema instruction.
let connector
    (client: HttpClient)
    (tokenSource: Microsoft365CopilotTokenSource)
    (options: Microsoft365CopilotOptions)
    : Connector<Microsoft365CopilotStream> =
    let baseUrl = options.BaseUrl.TrimEnd('/')

    let turnOf (call: ProviderRequest) =
        buildTurn call.SystemPrompt call.Messages

    let chatUrl (verb: string) (conversationId: string) =
        sprintf "%s/conversations/%s/%s" baseUrl (Uri.EscapeDataString conversationId) verb

    {
        Capabilities = fun _ -> capabilities
        CanServe = fun _ -> false
        UnservedReason = "Microsoft 365 Copilot serves one model"
        IsVisionCapable = fun _ -> false
        StructuredOutput = StructuredOutput.ViaPrompt
        Client = client
        Credential = credential tokenSource
        Endpoint =
            fun call ->
                Endpoint.Opened(
                    baseUrl + "/conversations",
                    "{}",
                    fun body ->
                        match tryConversationId body with
                        | Some id -> Ok(chatUrl (if call.Streaming then "chatOverStream" else "chat") id)
                        | None -> Error(MalformedResponse "The Work IQ conversation response carried no id.")
                )
        Body =
            fun call ->
                turnOf call
                |> Result.mapError refusal
                |> Result.map (chatBody options.TimeZone options.WebSearch options.Files)
        ParseResponse =
            fun call body ->
                match turnOf call with
                | Error refused -> Error(refusal refused)
                | Ok turn ->
                    match tryReplyText turn.Prompt body with
                    | Some reply -> Ok(toResponse reply)
                    | None -> Error(MalformedResponse "The Work IQ chat response was not a conversation.")
        Stream =
            fun call ->
                let prompt =
                    match turnOf call with
                    | Ok turn -> turn.Prompt
                    | Error _ -> ""

                Microsoft365CopilotStream.processor prompt
        RateLimitFloor = None
        Pause = Connector.sleep
    }

// ─── Factory helpers ─────────────────────────────────────────────────

/// A provider over a caller-supplied `HttpClient` — a custom handler, a
/// proxy, or an offline test.
let createWithHttpClient
    (tokenSource: Microsoft365CopilotTokenSource)
    (options: Microsoft365CopilotOptions)
    (client: HttpClient)
    : IAIProvider =
    ConnectorProvider(connector client tokenSource options, DefaultModel) :> IAIProvider

/// A provider over an `Azure.Core` credential that yields a user token
/// (for example `OnBehalfOfCredential`), with the default options.
let create (credential: TokenCredential) : IAIProvider =
    createWithHttpClient (FromCredential credential) Microsoft365CopilotOptions.defaults sharedClient.Value

/// A provider over a user-token credential with explicit options.
let createWithOptions (credential: TokenCredential) (options: Microsoft365CopilotOptions) : IAIProvider =
    createWithHttpClient (FromCredential credential) options sharedClient.Value

/// A provider whose token comes from a function, called once per send.
/// The usual server shape: read the requesting user's Work IQ token
/// from the request context.
let createWithTokenSource (fetch: unit -> Async<string option>) (options: Microsoft365CopilotOptions) : IAIProvider =
    createWithHttpClient (FromAccessToken fetch) options sharedClient.Value

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
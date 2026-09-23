// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 6f.B — the Twilio WhatsApp `INotificationSink`, its settings and
/// constructors.
module ToolUp.Platform.NotificationChannels.WhatsApp.Twilio

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Public surface ──────────────────────────────────────────────
//
// Phase 6f.B Twilio WhatsApp sink. Implements `INotificationSink` with
// `Kind = SinkKind.WhatsApp` over the same Twilio Messages REST API the
// SMS companion uses (`POST /2010-04-01/Accounts/{Sid}/Messages.json`):
// same account, same auth token, same endpoint — the only differences
// on the wire are the `whatsapp:` address prefix on `From` / `To` and,
// for a business-initiated message, Twilio's content-template fields
// (`ContentSid` + `ContentVariables`) in place of `Body`.
//
// **What the sink does NOT decide.** The 24-hour customer-care window,
// the template's registration and its parameter arity are enforced on
// the server by the shared WhatsApp send policy before any sink runs
// (Phase 827). A message that reaches this sink was admitted; the sink
// sends what it is handed and classifies what the vendor says.
//
// **Template name → content SID.** Both WhatsApp companions address a
// template by the vendor-neutral NAME the registry holds; each maps that
// name onto its own vendor handle. Twilio's handle is a content SID
// (`HX…`), one per approved template language, so the mapping lives in
// `TwilioWhatsAppSettings.ContentSids` keyed `name@language` (with a
// bare `name` entry as the language-agnostic fallback).
//
// **Per-recipient send.** One recipient per request, serially; the
// first failure stops the loop and the dispatcher retries the whole
// envelope — the SMS companion's shape exactly.
//
// **Auth.** Basic auth, `AccountSid:AuthToken`. The auth token is read
// from `ISecretStore.GetSecret("_platform", "TWILIO_AUTH_TOKEN")` fresh
// on every send (rotation-aware) — the same secret the SMS sink reads,
// because it is the same Twilio account.

[<Literal>]
let private ProviderName = "Twilio"

[<Literal>]
let private SecretScope = "_platform"

[<Literal>]
let private SecretKey = "TWILIO_AUTH_TOKEN"

[<Literal>]
let private ApiBase = "https://api.twilio.com/2010-04-01/Accounts"

[<Literal>]
let private WhatsAppPrefix = "whatsapp:"

/// Connection settings independent of the rotation-aware auth token.
/// `AccountSid` is half-public (Twilio shows it unredacted in its
/// console and support tooling), so it lives here rather than in
/// `ISecretStore` — the same split the SMS companion makes.
type TwilioWhatsAppSettings = {
    /// The Twilio account SID (`AC…`). Part of the request path and the
    /// Basic-auth user name.
    AccountSid: string
    /// The WhatsApp-enabled sender number in E.164 (`+14155238886` for
    /// the Twilio sandbox). Stored WITHOUT the `whatsapp:` prefix — the
    /// sink adds it; a configured prefix is tolerated and stripped.
    FromWhatsAppNumber: string
    /// Full URL of the Messages endpoint, overriding
    /// `https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/Messages.json`.
    /// For tests and Twilio-compatible mocks; `None` in production.
    EndpointOverride: string option
    /// Template name → Twilio content SID (`HX…`). Keyed `name@language`
    /// for a language-specific content resource, or bare `name` for the
    /// entry used when no language-specific one matches. A template send
    /// whose name has no entry fails permanently: the deployment has
    /// registered a template it never mapped onto a Twilio resource.
    ContentSids: Map<string, string>
}

/// Construction and lookup helpers for `TwilioWhatsAppSettings`.
module TwilioWhatsAppSettings =
    /// Parse the `TOOLUP_TWILIO_WHATSAPP_CONTENT_SIDS` format: a comma-
    /// separated list of `key=HX…` pairs, where `key` is `name` or
    /// `name@language`. Blank input is the empty map. A malformed pair
    /// (no `=`, a blank key or a blank SID) is an `Error` naming it, so a
    /// typo fails at startup rather than as a permanent failure per send.
    let parseContentSids (raw: string) : Result<Map<string, string>, string> =
        if String.IsNullOrWhiteSpace raw then
            Ok Map.empty
        else
            let pairs =
                raw.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                |> Array.toList

            let parsed =
                pairs
                |> List.map (fun pair ->
                    match pair.Split('=', 2, StringSplitOptions.TrimEntries) with
                    | [| key; sid |] when not (String.IsNullOrWhiteSpace key) && not (String.IsNullOrWhiteSpace sid) ->
                        Ok(key, sid)
                    | _ -> Error pair)

            let malformed =
                parsed
                |> List.choose (fun r ->
                    match r with
                    | Error pair -> Some pair
                    | Ok _ -> None)

            if List.isEmpty malformed then
                parsed |> List.choose Result.toOption |> Map.ofList |> Ok
            else
                Error(
                    sprintf
                        "malformed content-SID entries (expected name[@language]=HX…): %s"
                        (String.Join(", ", malformed))
                )

    /// The content SID for `templateName` in `language`: the
    /// `name@language` entry when there is one, else the bare `name`
    /// entry. `None` when the template is not mapped at all.
    let contentSidFor
        (templateName: string)
        (language: string option)
        (settings: TwilioWhatsAppSettings)
        : string option =
        let specific =
            language
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.bind (fun lang -> settings.ContentSids |> Map.tryFind $"{templateName}@{lang}")

        match specific with
        | Some sid -> Some sid
        | None -> settings.ContentSids |> Map.tryFind templateName

    /// The sender number without any `whatsapp:` prefix, trimmed.
    let bareFromNumber (settings: TwilioWhatsAppSettings) : string =
        let from = settings.FromWhatsAppNumber.Trim()

        if from.StartsWith(WhatsAppPrefix, StringComparison.OrdinalIgnoreCase) then
            from.Substring(WhatsAppPrefix.Length).Trim()
        else
            from

    /// Read settings from the `TOOLUP_TWILIO_*` env vars:
    ///   TOOLUP_TWILIO_ACCOUNT_SID             — required (shared with the SMS sink)
    ///   TOOLUP_TWILIO_WHATSAPP_FROM           — required (E.164 WhatsApp sender)
    ///   TOOLUP_TWILIO_WHATSAPP_CONTENT_SIDS   — optional (`name[@language]=HX…,…`)
    ///   TOOLUP_TWILIO_ENDPOINT                — optional Messages-endpoint override (shared)
    /// Raises on a missing required variable or a malformed content-SID
    /// list, so a misconfigured deployment fails at compose time.
    let fromEnv () : TwilioWhatsAppSettings =
        let read name =
            match Environment.GetEnvironmentVariable name with
            | null
            | "" -> None
            | v -> Some v

        let readRequired name =
            match read name with
            | Some v -> v
            | None -> failwithf "Phase 6f.B Twilio WhatsApp: env var %s is required" name

        let contentSids =
            match
                read ConfigKeys.Names.twilioWhatsAppContentSids
                |> Option.defaultValue ""
                |> parseContentSids
            with
            | Ok map -> map
            | Error message ->
                failwithf "Phase 6f.B Twilio WhatsApp: %s: %s" ConfigKeys.Names.twilioWhatsAppContentSids message

        {
            AccountSid = readRequired ConfigKeys.Names.twilioAccountSid
            FromWhatsAppNumber = readRequired ConfigKeys.Names.twilioWhatsAppFrom
            EndpointOverride = read ConfigKeys.Names.twilioEndpoint
            ContentSids = contentSids
        }

let private buildAuthHeader (sid: string) (token: string) =
    let raw = sprintf "%s:%s" sid token
    let bytes = Encoding.ASCII.GetBytes raw
    Convert.ToBase64String bytes

/// What one request carries besides the addresses: a content template
/// or a free-form body.
type private OutboundMessage =
    | Template of contentSid: string * variables: string list
    | FreeForm of body: string

/// Twilio's `ContentVariables`: a JSON object keyed by the 1-based
/// placeholder number (`{{1}}`, `{{2}}`, …). `TemplateParameters` is
/// already flat and ordered, so position `i` is placeholder `i + 1`.
let private contentVariablesJson (parameters: string list) : string =
    let variables = Dictionary<string, string>()
    parameters |> List.iteri (fun i value -> variables[string (i + 1)] <- value)
    JsonSerializer.Serialize variables

/// Twilio WhatsApp sink.
///
/// `handler` is the transport. The four-argument constructor builds the
/// platform's own egress-policy-wrapped client, which is what a
/// deployment wants; the explicit one exists so a test can serve canned
/// Twilio responses through the same code path.
type TwilioWhatsAppNotificationSink
    (
        addressBook: INotificationAddressBook,
        secretStore: ISecretStore,
        settings: TwilioWhatsAppSettings,
        logger: ILogger option,
        handler: HttpMessageHandler
    ) =

    // Phase 772 — through the platform client factory (egress policy
    // handler in front of the primary handler).
    let client = PlatformHttpClient.createWith EgressSurface.Notification handler

    let endpoint =
        settings.EndpointOverride
        |> Option.defaultValue (sprintf "%s/%s/Messages.json" ApiBase settings.AccountSid)

    let fromAddress = WhatsAppPrefix + TwilioWhatsAppSettings.bareFromNumber settings

    let logWarn (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    /// Resolve every recipient to a WhatsApp number, dropping the ones
    /// with none. `ResolveWhatsApp` is consent-gated for an `External`
    /// recipient, so a contact with no live WhatsApp opt-in yields
    /// nothing here; the REFUSAL for that case is recorded upstream by
    /// the consent filter, before the envelope reaches any sink.
    let resolveRecipients (scopeId: string) (recipients: RecipientId list) : Async<string list> = async {
        let lookups =
            recipients
            |> List.map (fun recipient -> async { return! addressBook.ResolveWhatsApp(recipient, scopeId) })
            |> Async.Parallel

        let! results = lookups

        return
            results
            |> Array.choose id
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.toList
    }

    let classifyHttp (status: HttpStatusCode) (body: string) : SinkResult =
        let code = int status

        let trimmed =
            if body.Length > 512 then
                body.Substring(0, 512) + "…"
            else
                body

        match code with
        | 429 -> SinkResult.TransientFailure(sprintf "Twilio 429 rate limited: %s" trimmed)
        | n when n >= 500 -> SinkResult.TransientFailure(sprintf "Twilio %d: %s" n trimmed)
        | n -> SinkResult.PermanentFailure(sprintf "Twilio %d: %s" n trimmed)

    let parseMessageSid (responseBody: string) : string option =
        try
            use doc = JsonDocument.Parse responseBody

            match doc.RootElement.TryGetProperty "sid" with
            | true, prop -> prop.GetString() |> Option.ofObj
            | false, _ -> None
        with _ ->
            None

    let sendOne (token: string) (toNumber: string) (message: OutboundMessage) : Async<SinkResult> = async {
        try
            let pairs = [
                "From", fromAddress
                "To", WhatsAppPrefix + toNumber.Trim()
                match message with
                | Template(contentSid, variables) ->
                    "ContentSid", contentSid

                    if not (List.isEmpty variables) then
                        "ContentVariables", contentVariablesJson variables
                | FreeForm body -> "Body", body
            ]

            use content =
                new FormUrlEncodedContent(pairs |> List.map (fun (k, v) -> KeyValuePair<string, string>(k, v)))

            use request = new HttpRequestMessage(HttpMethod.Post, endpoint, Content = content)

            request.Headers.Authorization <-
                Headers.AuthenticationHeaderValue("Basic", buildAuthHeader settings.AccountSid token)

            let! response = client.SendAsync request |> Async.AwaitTask
            let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            match int response.StatusCode with
            | 200
            | 201 -> return SinkResult.Delivered(parseMessageSid responseBody)
            | _ -> return classifyHttp response.StatusCode responseBody
        with
        | :? HttpRequestException as ex -> return SinkResult.TransientFailure(sprintf "Twilio network: %s" ex.Message)
        | :? TaskCanceledException as ex -> return SinkResult.TransientFailure(sprintf "Twilio timeout: %s" ex.Message)
        | ex ->
            logWarn $"[TwilioWhatsAppNotificationSink] unhandled exception: {ex.GetType().Name}: {ex.Message}"
            return SinkResult.TransientFailure(sprintf "%s: %s" (ex.GetType().Name) ex.Message)
    }

    /// The message shape the envelope asks for, or the permanent failure
    /// that says why it cannot be sent through Twilio.
    let outboundOf (whatsApp: WhatsAppEnvelope) : Result<OutboundMessage, string> =
        match whatsApp.TemplateName with
        | Some name when not (String.IsNullOrWhiteSpace name) ->
            match TwilioWhatsAppSettings.contentSidFor name whatsApp.TemplateLanguage settings with
            | Some sid -> Ok(Template(sid, whatsApp.TemplateParameters))
            | None -> Error $"twilio_content_sid_not_configured: template '{name}' has no content SID"
        | _ ->
            match whatsApp.Body with
            | Some body when not (String.IsNullOrWhiteSpace body) -> Ok(FreeForm body)
            | _ -> Error "whatsapp_message_has_no_content: neither a template nor a body"

    /// Construct with the platform's default transport.
    new
        (
            addressBook: INotificationAddressBook,
            secretStore: ISecretStore,
            settings: TwilioWhatsAppSettings,
            logger: ILogger option
        ) =
        TwilioWhatsAppNotificationSink(addressBook, secretStore, settings, logger, new HttpClientHandler())

    interface INotificationSink with
        member _.Kind = NotificationKind.SinkKind.WhatsApp
        member _.Provider = ProviderName

        member _.Send(scopeId, envelope) = async {
            match envelope.Notification with
            | TransactionalWhatsApp whatsApp ->
                match outboundOf whatsApp with
                | Error reason -> return SinkResult.PermanentFailure reason
                | Ok message ->
                    let! recipients = resolveRecipients scopeId whatsApp.Recipients

                    if List.isEmpty recipients then
                        return SinkResult.Skipped "no_addressable_recipients"
                    else
                        let! token = secretStore.GetSecret(SecretScope, SecretKey)

                        match token with
                        | None -> return SinkResult.PermanentFailure "TWILIO_AUTH_TOKEN not configured in secret store"
                        | Some t ->
                            // Send serially — first failure stops and
                            // returns; the dispatcher retries the whole
                            // envelope.
                            let mutable lastResult = SinkResult.Skipped "no_recipients_processed"
                            let mutable continueSending = true
                            let mutable iter = recipients

                            while continueSending && not iter.IsEmpty do
                                let head = iter.Head
                                iter <- iter.Tail
                                let! result = sendOne t head message
                                lastResult <- result

                                match result with
                                | SinkResult.Delivered _
                                | SinkResult.Skipped _ -> ()
                                | SinkResult.TransientFailure _
                                | SinkResult.PermanentFailure _ -> continueSending <- false

                            return lastResult
            | other ->
                return
                    SinkResult.PermanentFailure
                        $"TwilioWhatsAppNotificationSink received unexpected notification kind: {NotificationKind.ofNotification other}"
        }

/// Constructors for `TwilioWhatsAppNotificationSink`, mirroring the SMS
/// companion's `TwilioNotificationSink` module.
module TwilioWhatsAppNotificationSink =
    /// Construct the sink from explicit settings. Register it with
    /// `ServerApp.withTransactionalSink`.
    let create
        (addressBook: INotificationAddressBook)
        (secretStore: ISecretStore)
        (settings: TwilioWhatsAppSettings)
        (logger: ILogger option)
        : INotificationSink =
        TwilioWhatsAppNotificationSink(addressBook, secretStore, settings, logger) :> _

    /// Construct the sink over a caller-supplied primary HTTP handler —
    /// the egress-policy handler still sits in front of it. For tests and
    /// deployments that need their own transport.
    let createWithHandler
        (addressBook: INotificationAddressBook)
        (secretStore: ISecretStore)
        (settings: TwilioWhatsAppSettings)
        (logger: ILogger option)
        (handler: HttpMessageHandler)
        : INotificationSink =
        TwilioWhatsAppNotificationSink(addressBook, secretStore, settings, logger, handler) :> _

    /// Construct the sink from `TwilioWhatsAppSettings.fromEnv ()`.
    let fromEnv (addressBook: INotificationAddressBook) (secretStore: ISecretStore) (logger: ILogger option) =
        TwilioWhatsAppNotificationSink(addressBook, secretStore, TwilioWhatsAppSettings.fromEnv (), logger)
        :> INotificationSink
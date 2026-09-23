// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 6f.C - the Meta WhatsApp Business Cloud `INotificationSink`
/// (Graph API over BCL `HttpClient`) and its settings.
module ToolUp.Platform.NotificationChannels.WhatsApp.MetaCloud

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Phase 6f.C — WhatsApp over the Meta WhatsApp Business Cloud API ─
//
// A `SinkKind.WhatsApp` sink talking straight to Meta's Graph API
// (`POST {base}/{version}/{phoneNumberId}/messages`) over BCL
// `HttpClient` — no vendor SDK, no paid dependency (GP 1). It is the
// direct alternative to a WhatsApp sink that goes through a messaging
// intermediary; both consume the same `TransactionalWhatsApp` envelope
// and a deployment registers at most one of them.
//
// **What the sink does NOT decide.** The WhatsApp Business rules — a
// business-initiated message must be an approved template, a free-form
// message only inside the 24-hour customer-care window, the template's
// parameter arity — are enforced server-side by `WhatsAppSendPolicyFilter`
// before any sink runs (Phase 827). A free-form message reaching this
// sink was admitted by that rule, so the sink sends what it is handed.
// Consent is enforced upstream too: `INotificationAddressBook.ResolveWhatsApp`
// releases a contact's number only under a live WhatsApp opt-in.
//
// **Templates.** The envelope carries its parameters flat (header, then
// body, then buttons). The sink reads the template's per-component arity
// from the same `IWhatsAppTemplateRegistry` the policy filter checked,
// splits the list, and emits Meta's `components` array. Header
// parameters are text parameters; each button parameter addresses its
// own dynamic-URL button, in order (`index` 0, 1, …). When the envelope
// names no language the template's first registered language is used.
//
// **One recipient per request.** The Cloud API's `to` is a single
// number — there is no batch endpoint — so the sink sends serially and
// stops at the first failure; the dispatcher's retry budget applies to
// the envelope. `CorrelationId` rides as `biz_opaque_callback_data`,
// which Meta echoes on every status callback, so a `failed` status
// arriving at the inbound handler carries the correlation id back.
//
// **Auth.** A long-lived System User access token, read from
// `ISecretStore` (`_platform` / `meta_whatsapp_access_token`) on EVERY
// send, so a manual rotation is picked up without a restart and the sink
// never caches a credential. `MetaWhatsAppSettings` carries no secret.

/// Connection settings for the Meta WhatsApp Business Cloud sink,
/// health probe and validator. Carries no credential — the access
/// token, the app secret and the webhook verify token all come from
/// `ISecretStore` (see `MetaWhatsAppSettings.AccessTokenSecretKey`).
type MetaWhatsAppSettings = {
    /// The WhatsApp Business phone number id (numeric, from the Meta
    /// App dashboard) — the sending number, and the path segment every
    /// Cloud API call is addressed to. Not the phone number itself.
    PhoneNumberId: string
    /// The Graph API version segment, e.g. `"v26.0"`. Pinned by
    /// `MetaWhatsAppSettings.DefaultGraphApiVersion`; override it to move
    /// ahead of (or hold behind) the pin without a package upgrade.
    GraphApiVersion: string
    /// Replaces the Graph API base URL (`https://graph.facebook.com`).
    /// The version and path segments are still appended, so a fake
    /// transport sees the same request shape Meta would.
    EndpointOverride: string option
}

/// Constants and constructors for `MetaWhatsAppSettings`.
module MetaWhatsAppSettings =
    /// The Graph API version this release is written and tested against.
    [<Literal>]
    let DefaultGraphApiVersion = "v26.0"

    /// The Graph API base URL.
    [<Literal>]
    let DefaultGraphBaseUrl = "https://graph.facebook.com"

    /// Secret-store scope every Meta WhatsApp secret is read from.
    [<Literal>]
    let SecretScope = "_platform"

    /// Secret key of the System User access token (sends + health probe).
    [<Literal>]
    let AccessTokenSecretKey = "meta_whatsapp_access_token"

    /// Secret key of the Meta App secret — the HMAC key Meta signs
    /// inbound webhook deliveries with (`X-Hub-Signature-256`).
    [<Literal>]
    let AppSecretKey = "meta_whatsapp_app_secret"

    /// Secret key of the webhook verify token — the string the Meta App
    /// dashboard is given, and echoed back on the `GET` subscription
    /// challenge (`hub.verify_token`).
    [<Literal>]
    let VerifyTokenSecretKey = "meta_whatsapp_verify_token"

    /// Settings for `phoneNumberId` at the pinned Graph API version,
    /// against Meta's own endpoint.
    let create (phoneNumberId: string) : MetaWhatsAppSettings = {
        PhoneNumberId = phoneNumberId
        GraphApiVersion = DefaultGraphApiVersion
        EndpointOverride = None
    }

    /// Read settings from the environment:
    ///   TOOLUP_META_WHATSAPP_PHONE_NUMBER_ID   — required
    ///   TOOLUP_META_WHATSAPP_GRAPH_API_VERSION — optional (default `DefaultGraphApiVersion`)
    ///   TOOLUP_META_WHATSAPP_ENDPOINT          — optional base-URL override
    let fromEnv () : MetaWhatsAppSettings =
        let read name =
            match Environment.GetEnvironmentVariable name with
            | null -> None
            | v when String.IsNullOrWhiteSpace v -> None
            | v -> Some(v.Trim())

        let phoneNumberId =
            match read ConfigKeys.Names.metaWhatsAppPhoneNumberId with
            | Some v -> v
            | None ->
                failwithf "Phase 6f.C Meta WhatsApp: env var %s is required" ConfigKeys.Names.metaWhatsAppPhoneNumberId

        {
            PhoneNumberId = phoneNumberId
            GraphApiVersion =
                read ConfigKeys.Names.metaWhatsAppGraphApiVersion
                |> Option.defaultValue DefaultGraphApiVersion
            EndpointOverride = read ConfigKeys.Names.metaWhatsAppEndpoint
        }

    /// The Graph API base URL in force — the override, else Meta's.
    let baseUrl (settings: MetaWhatsAppSettings) : string =
        (settings.EndpointOverride |> Option.defaultValue DefaultGraphBaseUrl).TrimEnd '/'

    /// `{base}/{version}/{phoneNumberId}` — the phone-number node the
    /// health probe reads.
    let phoneNumberUrl (settings: MetaWhatsAppSettings) : string =
        sprintf "%s/%s/%s" (baseUrl settings) (settings.GraphApiVersion.Trim '/') settings.PhoneNumberId

    /// `{base}/{version}/{phoneNumberId}/messages` — the send endpoint.
    let messagesUrl (settings: MetaWhatsAppSettings) : string = phoneNumberUrl settings + "/messages"

[<Literal>]
let private ProviderName = "meta-cloud"

/// Graph API error codes Meta documents as throttling or a temporarily
/// unavailable service — retryable, unlike every other coded error.
let private transientErrorCodes =
    set [ 1; 2; 4; 80007; 130429; 131000; 131016; 131056; 133004 ]

/// Digits only — Meta addresses a recipient by its number without the
/// E.164 `+` (the `wa_id` form its callbacks carry back).
let internal toWaId (e164: string) : string = e164 |> String.filter Char.IsDigit

/// The message payload an envelope maps to, before a recipient is set.
type private MessageShape =
    | Template of name: string * language: string * components: JsonArray
    | Text of body: string

let private textParameters (values: string list) : JsonArray =
    let array = JsonArray()

    for value in values do
        let parameter = JsonObject()
        parameter["type"] <- JsonValue.Create "text"
        parameter["text"] <- JsonValue.Create value
        array.Add parameter

    array

/// Split the flat parameter list by the registered arity into Meta's
/// `components`. A component with no parameters is omitted, as Meta
/// expects for a static header / body.
let private templateComponents (descriptor: WhatsAppTemplateDescriptor) (parameters: string list) : JsonArray =
    let header = parameters |> List.truncate descriptor.HeaderParameterCount

    let body =
        parameters
        |> List.skip descriptor.HeaderParameterCount
        |> List.truncate descriptor.BodyParameterCount

    let buttons =
        parameters
        |> List.skip (descriptor.HeaderParameterCount + descriptor.BodyParameterCount)

    let components = JsonArray()

    let addComponent (kind: string) (values: string list) =
        if not (List.isEmpty values) then
            let part = JsonObject()
            part["type"] <- JsonValue.Create kind
            part["parameters"] <- textParameters values
            components.Add part

    addComponent "header" header
    addComponent "body" body

    buttons
    |> List.iteri (fun index value ->
        let part = JsonObject()
        part["type"] <- JsonValue.Create "button"
        part["sub_type"] <- JsonValue.Create "url"
        part["index"] <- JsonValue.Create(string index)
        part["parameters"] <- textParameters [ value ]
        components.Add part)

    components

/// The request body for one recipient.
let private requestBody (shape: MessageShape) (waId: string) (correlationId: string option) : string =
    let message = JsonObject()
    message["messaging_product"] <- JsonValue.Create "whatsapp"
    message["recipient_type"] <- JsonValue.Create "individual"
    message["to"] <- JsonValue.Create waId

    match shape with
    | Template(name, language, components) ->
        let languageNode = JsonObject()
        languageNode["code"] <- JsonValue.Create language
        let template = JsonObject()
        template["name"] <- JsonValue.Create name
        template["language"] <- languageNode
        // Deep-cloned per recipient: a JsonNode may have one parent.
        template["components"] <- components.DeepClone()
        message["type"] <- JsonValue.Create "template"
        message["template"] <- template
    | Text body ->
        let text = JsonObject()
        text["body"] <- JsonValue.Create body
        message["type"] <- JsonValue.Create "text"
        message["text"] <- text

    correlationId
    |> Option.filter (String.IsNullOrWhiteSpace >> not)
    |> Option.iter (fun corr -> message["biz_opaque_callback_data"] <- JsonValue.Create corr)

    message.ToJsonString()

/// `messages[0].id` from a send response — the `wamid` Meta's status
/// callbacks are keyed on.
let private parseMessageId (responseBody: string) : string option =
    try
        use doc = JsonDocument.Parse responseBody

        match doc.RootElement.TryGetProperty "messages" with
        | true, messages when messages.ValueKind = JsonValueKind.Array && messages.GetArrayLength() > 0 ->
            match messages[0].TryGetProperty "id" with
            | true, id -> id.GetString() |> Option.ofObj
            | _ -> None
        | _ -> None
    with _ ->
        None

/// `error.code` from a Graph API error response.
let private parseErrorCode (responseBody: string) : int option =
    try
        use doc = JsonDocument.Parse responseBody

        match doc.RootElement.TryGetProperty "error" with
        | true, error ->
            match error.TryGetProperty "code" with
            | true, code when code.ValueKind = JsonValueKind.Number -> Some(code.GetInt32())
            | _ -> None
        | _ -> None
    with _ ->
        None

/// Classify a non-success Graph API response.
let internal classifyFailure (status: HttpStatusCode) (responseBody: string) : SinkResult =
    let code = int status

    let trimmed =
        if responseBody.Length > 512 then
            responseBody.Substring(0, 512) + "…"
        else
            responseBody

    let transientCode =
        parseErrorCode responseBody |> Option.exists transientErrorCodes.Contains

    if code = 429 || code >= 500 || transientCode then
        SinkResult.TransientFailure(sprintf "Meta WhatsApp %d: %s" code trimmed)
    else
        SinkResult.PermanentFailure(sprintf "Meta WhatsApp %d: %s" code trimmed)

/// The Meta WhatsApp Business Cloud sink. `handler` is the primary
/// transport, always wrapped by the platform's egress-policy handler;
/// the constructor without it uses a fresh `HttpClientHandler`.
type MetaWhatsAppCloudNotificationSink
    (
        addressBook: INotificationAddressBook,
        secretStore: ISecretStore,
        templates: IWhatsAppTemplateRegistry,
        settings: MetaWhatsAppSettings,
        handler: HttpMessageHandler,
        logger: ILogger option
    ) =

    // Phase 772 — through the platform client factory, so the egress
    // policy decides every outbound call.
    let client = PlatformHttpClient.createWith EgressSurface.Notification handler

    let endpoint = MetaWhatsAppSettings.messagesUrl settings

    let logWarn (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    /// Resolve every recipient's WhatsApp number through the address
    /// book, dropping those with none (no number, or no live WhatsApp
    /// consent for an external contact).
    let resolveRecipients (scopeId: string) (recipients: RecipientId list) : Async<string list> = async {
        let! results =
            recipients
            |> List.map (fun recipient -> addressBook.ResolveWhatsApp(recipient, scopeId))
            |> Async.Parallel

        return
            results
            |> Array.choose id
            |> Array.map toWaId
            |> Array.filter (String.IsNullOrEmpty >> not)
            |> Array.toList
    }

    /// Map the envelope to the message it sends, or the permanent
    /// failure that makes it unsendable.
    let shapeOf (envelope: WhatsAppEnvelope) : Async<Result<MessageShape, string>> = async {
        match envelope.TemplateName, envelope.Body with
        | Some _, Some _ -> return Error "whatsapp_template_and_body: a template send must not carry a Body"
        | None, None -> return Error "whatsapp_no_content: neither TemplateName nor Body is set"
        | None, Some body -> return Ok(Text body)
        | Some name, None ->
            let! descriptor = templates.GetTemplate name

            match descriptor with
            | None -> return Error(sprintf "whatsapp_template_unknown: '%s' is not in the template registry" name)
            | Some descriptor when
                WhatsAppTemplateDescriptor.totalParameterCount descriptor
                <> List.length envelope.TemplateParameters
                ->
                return
                    Error(
                        sprintf
                            "whatsapp_template_arity_mismatch: '%s' takes %d parameters, the envelope carries %d"
                            name
                            (WhatsAppTemplateDescriptor.totalParameterCount descriptor)
                            (List.length envelope.TemplateParameters)
                    )
            | Some descriptor ->
                let language =
                    envelope.TemplateLanguage
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Option.orElse (
                        if isNull (box descriptor.Languages) then
                            None
                        else
                            List.tryHead descriptor.Languages
                    )

                match language with
                | None -> return Error(sprintf "whatsapp_template_language_unsupported: '%s' has no language" name)
                | Some language ->
                    return Ok(Template(name, language, templateComponents descriptor envelope.TemplateParameters))
    }

    let sendOne (token: string) (shape: MessageShape) (waId: string) (correlationId: string option) = async {
        try
            use content =
                new StringContent(requestBody shape waId correlationId, Encoding.UTF8, "application/json")

            use request = new HttpRequestMessage(HttpMethod.Post, endpoint, Content = content)
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)

            let! response = client.SendAsync request |> Async.AwaitTask
            let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            if response.IsSuccessStatusCode then
                return SinkResult.Delivered(parseMessageId responseBody)
            else
                return classifyFailure response.StatusCode responseBody
        with
        | :? HttpRequestException as ex ->
            return SinkResult.TransientFailure(sprintf "Meta WhatsApp network: %s" ex.Message)
        | :? TaskCanceledException as ex ->
            return SinkResult.TransientFailure(sprintf "Meta WhatsApp timeout: %s" ex.Message)
        | ex ->
            logWarn $"[MetaWhatsAppCloudNotificationSink] unhandled exception: {ex.GetType().Name}: {ex.Message}"
            return SinkResult.TransientFailure(sprintf "%s: %s" (ex.GetType().Name) ex.Message)
    }

    /// The production constructor — a fresh `HttpClientHandler` behind
    /// the egress policy. Explicit rather than an optional argument, so
    /// the narrow shape stays its own token in the approval baseline.
    new
        (
            addressBook: INotificationAddressBook,
            secretStore: ISecretStore,
            templates: IWhatsAppTemplateRegistry,
            settings: MetaWhatsAppSettings,
            logger: ILogger option
        ) =
        MetaWhatsAppCloudNotificationSink(
            addressBook,
            secretStore,
            templates,
            settings,
            new HttpClientHandler(),
            logger
        )

    interface INotificationSink with
        member _.Kind = NotificationKind.SinkKind.WhatsApp
        member _.Provider = ProviderName

        member _.Send(scopeId, envelope) = async {
            match envelope.Notification with
            | TransactionalWhatsApp whatsApp ->
                let! recipients = resolveRecipients scopeId whatsApp.Recipients

                if List.isEmpty recipients then
                    return SinkResult.Skipped "no_addressable_recipients"
                else
                    let! shape = shapeOf whatsApp

                    match shape with
                    | Error reason -> return SinkResult.PermanentFailure reason
                    | Ok shape ->
                        let! token =
                            secretStore.GetSecret(
                                MetaWhatsAppSettings.SecretScope,
                                MetaWhatsAppSettings.AccessTokenSecretKey
                            )

                        match token with
                        | None
                        | Some "" ->
                            return
                                SinkResult.PermanentFailure(
                                    sprintf
                                        "%s not configured in secret store scope %s"
                                        MetaWhatsAppSettings.AccessTokenSecretKey
                                        MetaWhatsAppSettings.SecretScope
                                )
                        | Some token ->
                            // Serial, first failure stops: the dispatcher
                            // retries the envelope as a whole.
                            let rec loop (remaining: string list) (last: SinkResult) = async {
                                match remaining with
                                | [] -> return last
                                | waId :: rest ->
                                    let! result = sendOne token shape waId whatsApp.CorrelationId

                                    match result with
                                    | SinkResult.Delivered _
                                    | SinkResult.Skipped _ -> return! loop rest result
                                    | SinkResult.TransientFailure _
                                    | SinkResult.PermanentFailure _ -> return result
                            }

                            return! loop recipients (SinkResult.Skipped "no_recipients_processed")
            | other ->
                return
                    SinkResult.PermanentFailure
                        $"MetaWhatsAppCloudNotificationSink received unexpected notification kind: {NotificationKind.ofNotification other}"
        }

/// Constructors for the Meta WhatsApp Business Cloud sink.
module MetaWhatsAppCloudNotificationSink =
    /// Build the sink over the platform's default transport.
    let create
        (addressBook: INotificationAddressBook)
        (secretStore: ISecretStore)
        (templates: IWhatsAppTemplateRegistry)
        (settings: MetaWhatsAppSettings)
        (logger: ILogger option)
        : INotificationSink =
        MetaWhatsAppCloudNotificationSink(addressBook, secretStore, templates, settings, logger) :> _

    /// Build the sink over a caller-supplied transport (a test stub, a
    /// proxy-aware handler). The egress policy handler still wraps it.
    let createWith
        (addressBook: INotificationAddressBook)
        (secretStore: ISecretStore)
        (templates: IWhatsAppTemplateRegistry)
        (settings: MetaWhatsAppSettings)
        (handler: HttpMessageHandler)
        (logger: ILogger option)
        : INotificationSink =
        MetaWhatsAppCloudNotificationSink(addressBook, secretStore, templates, settings, handler, logger) :> _

    /// Build the sink with `MetaWhatsAppSettings.fromEnv ()`.
    let fromEnv
        (addressBook: INotificationAddressBook)
        (secretStore: ISecretStore)
        (templates: IWhatsAppTemplateRegistry)
        (logger: ILogger option)
        : INotificationSink =
        create addressBook secretStore templates (MetaWhatsAppSettings.fromEnv ()) logger
module ToolUp.Platform.NotificationChannels.Email.SendGrid

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Public surface ──────────────────────────────────────────────
//
// Phase 6f SendGrid email sink. Implements `INotificationSink` over
// the SendGrid v3 REST API (`POST /v3/mail/send`). Activated via
// `TOOLUP_TRANSACTIONAL_EMAIL=sendgrid` in the reference app
// composition root, or constructed directly via
// `SendGridNotificationSink.create addressBook secretStore settings`.
//
// **Why HTTP-direct instead of the SendGrid NuGet SDK.** The official
// SDK pulls in Newtonsoft 13 + a transient System.Net.Http chain that
// duplicates `HttpClient` we already pool elsewhere. The wire format
// is small (one POST, one envelope shape), so calling REST directly
// costs ~80 lines and avoids the dep chain. Same approach the
// `src/AIProviders/Claude/` companion took for Anthropic.
//
// **API key sourcing.** Reads from `ISecretStore.GetSecret("_platform",
// "SENDGRID_API_KEY")` on every send (supports key rotation without
// restart). Mirrors the legacy Claude provider path. A misconfigured
// store missing the key yields `PermanentFailure` so the audit trail
// records the deployment gap loudly.

[<Literal>]
let private ProviderName = "SendGrid"

[<Literal>]
let private SecretScope = "_platform"

[<Literal>]
let private SecretKey = "SENDGRID_API_KEY"

[<Literal>]
let private MailSendEndpoint = "https://api.sendgrid.com/v3/mail/send"

/// Connection-shape settings independent of the API key (which lives
/// in `ISecretStore`). `DefaultFromAddress` mirrors the SMTP sink;
/// SendGrid requires the `from` address to match a verified sender,
/// so deployments configure it once at the SDK boundary.
type SendGridSettings = {
    DefaultFromAddress: string
    DefaultFromDisplayName: string option
    /// Optional override for testing. Production deployments leave
    /// this `None` to use the canonical `api.sendgrid.com` endpoint.
    EndpointOverride: string option
}

module SendGridSettings =
    /// Read the deployment-shape settings from env vars:
    ///   TOOLUP_SENDGRID_FROM        — required default sender
    ///   TOOLUP_SENDGRID_FROM_NAME   — optional sender display name
    ///   TOOLUP_SENDGRID_ENDPOINT    — optional REST endpoint override
    let fromEnv () : SendGridSettings =
        let read name =
            match Environment.GetEnvironmentVariable name with
            | null
            | "" -> None
            | v -> Some v

        let fromAddress =
            match read ConfigKeys.Names.sendGridFrom with
            | Some v -> v
            | None -> failwithf "Phase 6f SendGrid: env var TOOLUP_SENDGRID_FROM is required"

        {
            DefaultFromAddress = fromAddress
            DefaultFromDisplayName = read ConfigKeys.Names.sendGridFromName
            EndpointOverride = read ConfigKeys.Names.sendGridEndpoint
        }

// ─── Wire DTOs ───────────────────────────────────────────────────
//
// Plain records serialised by `System.Text.Json` (camelCase). Lower-
// case property names match the `mail/send` v3 schema verbatim — no
// converter required, no Newtonsoft dependency.

type private SgAddress = { email: string; name: string option }

type private SgPersonalization = {
    ``to``: SgAddress array
    dynamic_template_data: System.Collections.Generic.IDictionary<string, string> option
    subject: string option
}

type private SgContent = { ``type``: string; value: string }

type private SgPayload = {
    personalizations: SgPersonalization array
    from: SgAddress
    subject: string option
    content: SgContent array option
    template_id: string option
    custom_args: System.Collections.Generic.IDictionary<string, string> option
}

let private outboundJsonOptions =
    let opts = JsonSerializerOptions(PropertyNamingPolicy = null)
    opts.DefaultIgnoreCondition <- System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    opts

// ─── Implementation ──────────────────────────────────────────────

let private toSgAddress (addr: EmailAddress) : SgAddress = {
    email = addr.Address
    name = addr.DisplayName
}

let private buildPayload
    (fromAddr: EmailAddress)
    (recipients: EmailAddress list)
    (correlationId: string option)
    (content: EmailContent)
    : SgPayload =
    let toArray = recipients |> List.map toSgAddress |> List.toArray

    let customArgs: System.Collections.Generic.IDictionary<string, string> option =
        correlationId
        |> Option.map (fun corr ->
            let dict = System.Collections.Generic.Dictionary<string, string>()
            dict["correlation_id"] <- corr
            dict :> System.Collections.Generic.IDictionary<string, string>)

    match content with
    | InlineEmail(subject, bodyText, bodyHtml) ->
        let textPart = {
            ``type`` = "text/plain"
            value = bodyText
        }

        let parts =
            match bodyHtml with
            | Some html -> [| textPart; { ``type`` = "text/html"; value = html } |]
            | None -> [| textPart |]

        {
            personalizations = [|
                {
                    ``to`` = toArray
                    dynamic_template_data = None
                    subject = None
                }
            |]
            from = toSgAddress fromAddr
            subject = Some subject
            content = Some parts
            template_id = None
            custom_args = customArgs
        }
    | TemplatedEmail(templateId, variables) ->
        let dict = System.Collections.Generic.Dictionary<string, string>()

        for KeyValue(k, v) in variables do
            dict[k] <- v

        {
            personalizations = [|
                {
                    ``to`` = toArray
                    dynamic_template_data = Some(dict :> System.Collections.Generic.IDictionary<string, string>)
                    subject = None
                }
            |]
            from = toSgAddress fromAddr
            subject = None
            content = None
            template_id = Some templateId
            custom_args = customArgs
        }

let private resolveFromAddress (settings: SendGridSettings) : Result<EmailAddress, string> =
    if String.IsNullOrWhiteSpace settings.DefaultFromAddress then
        Error "no SendGrid From: address configured (set TOOLUP_SENDGRID_FROM or pass DefaultFromAddress)"
    else
        Ok {
            Address = settings.DefaultFromAddress
            DisplayName = settings.DefaultFromDisplayName
        }

/// SendGrid v3 REST email sink.
type SendGridNotificationSink
    (
        addressBook: INotificationAddressBook,
        secretStore: ISecretStore,
        settings: SendGridSettings,
        logger: ILogger option
    ) =

    // Single shared HttpClient — SendGrid's REST endpoint reuses
    // connections happily. Construction is cheap; reuse keeps the
    // socket pool warm.
    let client = new HttpClient()

    let endpoint = settings.EndpointOverride |> Option.defaultValue MailSendEndpoint

    let logWarn (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    let resolveRecipients (scopeId: string) (userIds: string list) : Async<EmailAddress list> = async {
        let lookups =
            userIds
            |> List.map (fun userId -> async { return! addressBook.ResolveEmail(userId, scopeId) })
            |> Async.Parallel

        let! results = lookups
        return results |> Array.choose id |> Array.toList
    }

    let classifyHttp (status: HttpStatusCode) (body: string) : SinkResult =
        let code = int status

        let trimmed =
            if body.Length > 512 then
                body.Substring(0, 512) + "…"
            else
                body

        match code with
        | 200
        | 201
        | 202 ->
            // SendGrid returns 202 Accepted with no body on success;
            // the message-id is in the `X-Message-Id` response header
            // (captured separately at the call site). The arm exists
            // for completeness but is normally short-circuited above.
            SinkResult.Delivered None
        | 429 -> SinkResult.TransientFailure(sprintf "SendGrid 429 rate limited: %s" trimmed)
        | n when n >= 500 -> SinkResult.TransientFailure(sprintf "SendGrid %d: %s" n trimmed)
        | n -> SinkResult.PermanentFailure(sprintf "SendGrid %d: %s" n trimmed)

    interface INotificationSink with
        member _.Kind = NotificationKind.SinkKind.Email
        member _.Provider = ProviderName

        member _.Send(scopeId, envelope) = async {
            match envelope.Notification with
            | TransactionalEmail email ->
                let! recipients = resolveRecipients scopeId email.RecipientUserIds

                if List.isEmpty recipients then
                    return SinkResult.Skipped "no_addressable_recipients"
                else
                    match resolveFromAddress settings with
                    | Error err -> return SinkResult.PermanentFailure err
                    | Ok fromAddr ->
                        let! apiKey = secretStore.GetSecret(SecretScope, SecretKey)

                        match apiKey with
                        | None -> return SinkResult.PermanentFailure "SENDGRID_API_KEY not configured in secret store"
                        | Some key ->
                            try
                                let payload = buildPayload fromAddr recipients email.CorrelationId email.Content
                                let body = JsonSerializer.Serialize(payload, outboundJsonOptions)
                                use content = new StringContent(body, Encoding.UTF8, "application/json")
                                use request = new HttpRequestMessage(HttpMethod.Post, endpoint, Content = content)

                                request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", key)

                                let! response = client.SendAsync request |> Async.AwaitTask
                                let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                                match int response.StatusCode with
                                | 200
                                | 201
                                | 202 ->
                                    // SendGrid returns the canonical
                                    // message id in `X-Message-Id`.
                                    let vendorId =
                                        match response.Headers.TryGetValues("X-Message-Id") with
                                        | true, values -> values |> Seq.tryHead
                                        | false, _ -> None

                                    return SinkResult.Delivered vendorId
                                | _ -> return classifyHttp response.StatusCode responseBody
                            with
                            | :? HttpRequestException as ex ->
                                return SinkResult.TransientFailure(sprintf "SendGrid network: %s" ex.Message)
                            | :? TaskCanceledException as ex ->
                                return SinkResult.TransientFailure(sprintf "SendGrid timeout: %s" ex.Message)
                            | ex ->
                                logWarn
                                    $"[SendGridNotificationSink] unhandled exception: {ex.GetType().Name}: {ex.Message}"

                                return SinkResult.TransientFailure(sprintf "%s: %s" (ex.GetType().Name) ex.Message)
            | other ->
                return
                    SinkResult.PermanentFailure
                        $"SendGridNotificationSink received unexpected notification kind: {NotificationKind.ofNotification other}"
        }

module SendGridNotificationSink =
    /// Construct an `INotificationSink` with explicit settings. The
    /// API key continues to come from `ISecretStore` (rotation-aware);
    /// `settings` carries the from-address and any endpoint override.
    let create
        (addressBook: INotificationAddressBook)
        (secretStore: ISecretStore)
        (settings: SendGridSettings)
        (logger: ILogger option)
        : INotificationSink =
        SendGridNotificationSink(addressBook, secretStore, settings, logger) :> _

    /// Construct an `INotificationSink` reading settings from the
    /// `TOOLUP_SENDGRID_*` env vars. The API key still goes through
    /// `ISecretStore` so rotation works.
    let fromEnv (addressBook: INotificationAddressBook) (secretStore: ISecretStore) (logger: ILogger option) =
        SendGridNotificationSink(addressBook, secretStore, SendGridSettings.fromEnv (), logger) :> INotificationSink
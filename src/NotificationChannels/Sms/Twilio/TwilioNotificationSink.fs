module ToolUp.Platform.NotificationChannels.Sms.Twilio

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading.Tasks
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Public surface ──────────────────────────────────────────────
//
// Phase 6f Twilio SMS sink. Implements `INotificationSink` over the
// Twilio Messages REST API (`POST /2010-04-01/Accounts/{Sid}/Messages.json`).
// Activated via `TOOLUP_TRANSACTIONAL_SMS=twilio` in the reference app
// composition root, or constructed directly via `TwilioNotificationSink.create`.
//
// **Per-recipient send.** Twilio's API takes one recipient per
// request — there's no native fan-out. The sink iterates the resolved
// recipient list and sends serially. A failure on recipient N stops
// the loop and the dispatcher retries the whole envelope; idempotency
// is the caller's responsibility (use `CorrelationId` mapped to a
// per-recipient idempotency key by Twilio).
//
// **Auth.** Basic auth — `AccountSid:AuthToken` base64-encoded on the
// `Authorization` header. The Sid is part of the URL path; the auth
// token comes from `ISecretStore.GetSecret("_platform", "TWILIO_AUTH_TOKEN")`
// fresh on every send (rotation-aware).

[<Literal>]
let private ProviderName = "Twilio"

[<Literal>]
let private SecretScope = "_platform"

[<Literal>]
let private SecretKey = "TWILIO_AUTH_TOKEN"

[<Literal>]
let private ApiBase = "https://api.twilio.com/2010-04-01/Accounts"

/// Connection settings independent of the rotation-aware auth token.
/// `AccountSid` lives here (not in `ISecretStore`) because it's
/// half-public — Twilio surfaces it in support tickets and logs
/// without redaction. `FromPhoneNumber` is the verified Twilio
/// number used as the `From:` field; deployments running
/// alphanumeric senders configure that string here too.
type TwilioSettings = {
    AccountSid: string
    FromPhoneNumber: string
    EndpointOverride: string option
}

module TwilioSettings =
    /// Read settings from the `TOOLUP_TWILIO_*` env vars:
    ///   TOOLUP_TWILIO_ACCOUNT_SID — required
    ///   TOOLUP_TWILIO_FROM        — required (E.164 or alphanumeric)
    ///   TOOLUP_TWILIO_ENDPOINT    — optional override
    let fromEnv () : TwilioSettings =
        let read name =
            match Environment.GetEnvironmentVariable name with
            | null
            | "" -> None
            | v -> Some v

        let readRequired name =
            match read name with
            | Some v -> v
            | None -> failwithf "Phase 6f Twilio: env var %s is required" name

        {
            AccountSid = readRequired "TOOLUP_TWILIO_ACCOUNT_SID"
            FromPhoneNumber = readRequired "TOOLUP_TWILIO_FROM"
            EndpointOverride = read "TOOLUP_TWILIO_ENDPOINT"
        }

let private buildAuthHeader (sid: string) (token: string) =
    let raw = sprintf "%s:%s" sid token
    let bytes = Encoding.ASCII.GetBytes raw
    Convert.ToBase64String bytes

/// Twilio SMS sink.
type TwilioNotificationSink
    (addressBook: INotificationAddressBook, secretStore: ISecretStore, settings: TwilioSettings, logger: ILogger option)
    =

    let client = new HttpClient()

    let endpoint =
        settings.EndpointOverride
        |> Option.defaultValue (sprintf "%s/%s/Messages.json" ApiBase settings.AccountSid)

    let logWarn (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    let resolveRecipients (scopeId: string) (userIds: string list) : Async<PhoneNumber list> = async {
        let lookups =
            userIds
            |> List.map (fun userId -> async { return! addressBook.ResolvePhone(userId, scopeId) })
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
        | 201 -> SinkResult.Delivered None // overridden by call site if message SID parsed
        | 429 -> SinkResult.TransientFailure(sprintf "Twilio 429 rate limited: %s" trimmed)
        | n when n >= 500 -> SinkResult.TransientFailure(sprintf "Twilio %d: %s" n trimmed)
        | n -> SinkResult.PermanentFailure(sprintf "Twilio %d: %s" n trimmed)

    let parseMessageSid (responseBody: string) : string option =
        // Twilio returns JSON like `{"sid": "SM...", ...}`. Use
        // `System.Text.Json.JsonDocument` for a single-field probe —
        // avoids pulling in a full DTO type for one value.
        try
            use doc = System.Text.Json.JsonDocument.Parse responseBody

            match doc.RootElement.TryGetProperty "sid" with
            | true, prop -> prop.GetString() |> Option.ofObj
            | false, _ -> None
        with _ ->
            None

    let sendOne
        (token: string)
        (toNumber: PhoneNumber)
        (body: string)
        (correlationId: string option)
        : Async<SinkResult> =
        async {
            try
                let formData = [
                    "From", settings.FromPhoneNumber
                    "To", toNumber.E164
                    "Body", body
                    yield!
                        correlationId
                        |> Option.map (fun corr -> [ "ProvideFeedback", "true"; "MessagingServiceSid", "" ])
                        |> Option.defaultValue []
                ]

                // Drop empty values — Twilio rejects empty strings on some
                // optional fields. The `MessagingServiceSid` placeholder
                // above is illustrative only; the production filter
                // removes it.
                let pairs = formData |> List.filter (fun (_, v) -> not (String.IsNullOrEmpty v))

                use content = new FormUrlEncodedContent(dict pairs)
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
            | :? HttpRequestException as ex ->
                return SinkResult.TransientFailure(sprintf "Twilio network: %s" ex.Message)
            | :? TaskCanceledException as ex ->
                return SinkResult.TransientFailure(sprintf "Twilio timeout: %s" ex.Message)
            | ex ->
                logWarn $"[TwilioNotificationSink] unhandled exception: {ex.GetType().Name}: {ex.Message}"
                return SinkResult.TransientFailure(sprintf "%s: %s" (ex.GetType().Name) ex.Message)
        }

    interface INotificationSink with
        member _.Kind = NotificationKind.SinkKind.Sms
        member _.Provider = ProviderName

        member _.Send(scopeId, envelope) = async {
            match envelope.Notification with
            | TransactionalSms sms ->
                let! recipients = resolveRecipients scopeId sms.RecipientUserIds

                if List.isEmpty recipients then
                    return SinkResult.Skipped "no_addressable_recipients"
                else
                    let! token = secretStore.GetSecret(SecretScope, SecretKey)

                    match token with
                    | None -> return SinkResult.PermanentFailure "TWILIO_AUTH_TOKEN not configured in secret store"
                    | Some t ->
                        // Send serially — first failure stops and
                        // returns. The dispatcher retries the whole
                        // envelope; per-recipient idempotency is the
                        // caller's responsibility via CorrelationId.
                        let mutable lastResult = SinkResult.Skipped "no_recipients_processed"
                        let mutable continueSending = true
                        let mutable iter = recipients

                        while continueSending && not iter.IsEmpty do
                            let head = iter.Head
                            iter <- iter.Tail
                            let! result = sendOne t head sms.Body sms.CorrelationId
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
                        $"TwilioNotificationSink received unexpected notification kind: {NotificationKind.ofNotification other}"
        }

module TwilioNotificationSink =
    let create
        (addressBook: INotificationAddressBook)
        (secretStore: ISecretStore)
        (settings: TwilioSettings)
        (logger: ILogger option)
        : INotificationSink =
        TwilioNotificationSink(addressBook, secretStore, settings, logger) :> _

    let fromEnv (addressBook: INotificationAddressBook) (secretStore: ISecretStore) (logger: ILogger option) =
        TwilioNotificationSink(addressBook, secretStore, TwilioSettings.fromEnv (), logger) :> INotificationSink
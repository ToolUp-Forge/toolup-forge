module ToolUp.Platform.NotificationChannels.Push.WebPush

open System
open WebPush
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Public surface ──────────────────────────────────────────────
//
// Phase 6f Web Push sink. Implements `INotificationSink` over the
// `WebPush` NuGet package (MIT-licensed; wraps RFC 8030 Web Push
// protocol with VAPID auth). Activated via
// `TOOLUP_TRANSACTIONAL_PUSH=webpush` in the reference app composition
// root.
//
// **VAPID identity.** Web Push requires a VAPID key pair (subject +
// public + private key). Public key is shipped to the browser at
// subscription time; private key signs JWTs for each push send.
// The SDK reads:
//   * `WEBPUSH_VAPID_PUBLIC` — base64url public key (cleared for
//     non-secret use; leaked = no compromise)
//   * `WEBPUSH_VAPID_PRIVATE` — base64url private key (secret;
//     rotation-aware via ISecretStore)
//   * `WEBPUSH_VAPID_SUBJECT` — `mailto:` URI per RFC 8292; deployments
//     use the operator's contact email
//
// **Per-token send.** Web Push has one endpoint per registered
// browser tab (the W3C Push API endpoint). The sink iterates the
// resolved `PushToken list`, sending one HTTP POST per token.
// Failures don't short-circuit — a `410 Gone` for one stale token
// shouldn't drop deliveries for the user's other devices.
//
// **iOS / Android native push.** Out of scope. Native APNs / FCM
// pipelines need device-token management (registration, expiry, badge
// counts) that Phase 6f doesn't ship. WebPush works in PWA-context
// browsers including iOS Safari 16.4+; deployments needing strict
// mobile-app delivery write their own `INotificationSink` against
// FCM / APNs and register it instead.

[<Literal>]
let private ProviderName = "WebPush"

[<Literal>]
let private SecretScope = "_platform"

[<Literal>]
let private VapidPrivateKeyName = "WEBPUSH_VAPID_PRIVATE"

/// Web Push platform discriminator. Tokens with `Platform != WebPush`
/// are silently skipped — a deployment that mixes WebPush + future
/// FCM / APNs sinks routes by token-platform without misdelivering.
[<Literal>]
let private WebPushPlatform = "WebPush"

/// Settings independent of the rotation-aware private key. Public key
/// + subject are deployment-stable; published by the deployment to the
/// service worker via a static config endpoint.
type WebPushSettings = {
    VapidPublicKey: string
    VapidSubject: string
}

module WebPushSettings =
    /// Read settings from `WEBPUSH_VAPID_*` env vars:
    ///   WEBPUSH_VAPID_PUBLIC   — base64url-encoded VAPID public key
    ///   WEBPUSH_VAPID_SUBJECT  — `mailto:` URI per RFC 8292
    let fromEnv () : WebPushSettings =
        let read name =
            match Environment.GetEnvironmentVariable name with
            | null
            | "" -> None
            | v -> Some v

        let readRequired name =
            match read name with
            | Some v -> v
            | None -> failwithf "Phase 6f WebPush: env var %s is required" name

        {
            VapidPublicKey = readRequired "WEBPUSH_VAPID_PUBLIC"
            VapidSubject = readRequired "WEBPUSH_VAPID_SUBJECT"
        }

let private buildPayload (envelope: PushEnvelope) : string =
    // Service worker reads `title` / `body` / `url` from the JSON
    // payload. The shape is part of the contract between the SDK and
    // the deployment-shipped service worker — see deploy/sw.js example.
    let escape (s: string) =
        System.Text.Json.JsonEncodedText.Encode(s).ToString()

    let parts = [
        sprintf "\"title\":\"%s\"" (escape envelope.Title)
        sprintf "\"body\":\"%s\"" (escape envelope.Body)
        match envelope.DeepLink with
        | Some url -> sprintf "\"url\":\"%s\"" (escape url)
        | None -> ()
        match envelope.CorrelationId with
        | Some corr -> sprintf "\"correlation_id\":\"%s\"" (escape corr)
        | None -> ()
    ]

    sprintf "{%s}" (String.concat "," parts)

/// Web Push sink.
type WebPushNotificationSink
    (addressBook: INotificationAddressBook, secretStore: ISecretStore, settings: WebPushSettings, logger: ILogger option)
    =

    let logWarn (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    let resolveTokens (scopeId: string) (userIds: string list) : Async<PushToken list> = async {
        let lookups =
            userIds
            |> List.map (fun userId -> async { return! addressBook.ResolvePushTokens(userId, scopeId) })
            |> Async.Parallel

        let! results = lookups

        return
            results
            |> Array.collect List.toArray
            |> Array.filter (fun t -> t.Platform = WebPushPlatform)
            |> Array.toList
    }

    let parseSubscription (token: PushToken) : Result<PushSubscription, string> =
        // The W3C Push API token is conventionally serialised as a
        // JSON object with `endpoint` + `keys.p256dh` + `keys.auth`.
        // Service workers persist that shape; the deployment's
        // "save-my-push-token" handler stores it directly into the
        // `UserContact.PushTokens.Token` field. Here we parse it back.
        try
            use doc = System.Text.Json.JsonDocument.Parse token.Token
            let root = doc.RootElement

            let endpoint =
                match root.TryGetProperty "endpoint" with
                | true, prop -> prop.GetString()
                | false, _ -> ""

            let keys =
                match root.TryGetProperty "keys" with
                | true, prop -> prop
                | false, _ -> System.Text.Json.JsonElement()

            let p256dh =
                match keys.ValueKind with
                | System.Text.Json.JsonValueKind.Undefined -> ""
                | _ ->
                    match keys.TryGetProperty "p256dh" with
                    | true, prop -> prop.GetString()
                    | false, _ -> ""

            let auth =
                match keys.ValueKind with
                | System.Text.Json.JsonValueKind.Undefined -> ""
                | _ ->
                    match keys.TryGetProperty "auth" with
                    | true, prop -> prop.GetString()
                    | false, _ -> ""

            if
                String.IsNullOrEmpty endpoint
                || String.IsNullOrEmpty p256dh
                || String.IsNullOrEmpty auth
            then
                Error "PushToken JSON missing endpoint or keys.p256dh / keys.auth"
            else
                Ok(PushSubscription(endpoint, p256dh, auth))
        with ex ->
            Error(sprintf "PushToken parse failed: %s" ex.Message)

    let classifyWebPushException (ex: WebPushException) : SinkResult =
        let code = int ex.StatusCode

        match code with
        | 404
        | 410 ->
            // 410 Gone = subscription expired or unsubscribed.
            // 404 = endpoint no longer exists. Both are permanent
            // for this token; the deployment's user-management flow
            // should evict expired tokens from the address book.
            SinkResult.PermanentFailure(sprintf "WebPush %d (subscription expired or removed): %s" code ex.Message)
        | 429 -> SinkResult.TransientFailure(sprintf "WebPush 429 rate limited: %s" ex.Message)
        | n when n >= 500 -> SinkResult.TransientFailure(sprintf "WebPush %d: %s" n ex.Message)
        | n -> SinkResult.PermanentFailure(sprintf "WebPush %d: %s" n ex.Message)

    interface INotificationSink with
        member _.Kind = NotificationKind.SinkKind.Push NotificationKind.PushVariant.WebPush
        member _.Provider = ProviderName

        member _.Send(scopeId, envelope) = async {
            match envelope.Notification with
            | MobilePush push ->
                let! tokens = resolveTokens scopeId push.RecipientUserIds

                if List.isEmpty tokens then
                    return SinkResult.Skipped "no_addressable_recipients"
                else
                    let! privateKey = secretStore.GetSecret(SecretScope, VapidPrivateKeyName)

                    match privateKey with
                    | None ->
                        return
                            SinkResult.PermanentFailure(sprintf "%s not configured in secret store" VapidPrivateKeyName)
                    | Some privKey ->
                        let vapid = VapidDetails(settings.VapidSubject, settings.VapidPublicKey, privKey)

                        use webPushClient = new WebPushClient()
                        let payload = buildPayload push

                        // Per-token send. First permanent failure
                        // surfaces (the dispatcher won't retry); first
                        // transient failure surfaces too. Successes
                        // continue the loop; the last per-token
                        // outcome is returned. A future enhancement
                        // would aggregate per-token outcomes into a
                        // single composite SinkResult.
                        let mutable lastResult = SinkResult.Skipped "no_tokens_processed"
                        let mutable iter = tokens

                        while not iter.IsEmpty do
                            let head = iter.Head
                            iter <- iter.Tail

                            match parseSubscription head with
                            | Error err -> lastResult <- SinkResult.PermanentFailure err
                            | Ok subscription ->
                                try
                                    do!
                                        webPushClient.SendNotificationAsync(subscription, payload, vapid)
                                        |> Async.AwaitTask

                                    lastResult <- SinkResult.Delivered None
                                with
                                | :? WebPushException as ex -> lastResult <- classifyWebPushException ex
                                | ex ->
                                    logWarn
                                        $"[WebPushNotificationSink] unhandled exception: {ex.GetType().Name}: {ex.Message}"

                                    lastResult <-
                                        SinkResult.TransientFailure(sprintf "%s: %s" (ex.GetType().Name) ex.Message)

                        return lastResult
            | other ->
                return
                    SinkResult.PermanentFailure
                        $"WebPushNotificationSink received unexpected notification kind: {NotificationKind.ofNotification other}"
        }

module WebPushNotificationSink =
    let create
        (addressBook: INotificationAddressBook)
        (secretStore: ISecretStore)
        (settings: WebPushSettings)
        (logger: ILogger option)
        : INotificationSink =
        WebPushNotificationSink(addressBook, secretStore, settings, logger) :> _

    let fromEnv (addressBook: INotificationAddressBook) (secretStore: ISecretStore) (logger: ILogger option) =
        WebPushNotificationSink(addressBook, secretStore, WebPushSettings.fromEnv (), logger) :> INotificationSink
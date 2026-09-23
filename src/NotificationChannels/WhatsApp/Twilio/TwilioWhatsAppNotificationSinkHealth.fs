// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 6f.B — the readiness probe for the Twilio WhatsApp sink.
module ToolUp.Platform.NotificationChannels.WhatsApp.TwilioHealth

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets
open ToolUp.Platform.NotificationChannels.WhatsApp.Twilio

// ─── Phase 6f.B Twilio WhatsApp sink health probe ────────────────────
//
// Two questions, in order: does `TWILIO_AUTH_TOKEN` resolve from
// `ISecretStore` (the secret-rotation failure mode the SMS probe
// covers), and does Twilio accept it — a `GET` of the account resource
// (`/2010-04-01/Accounts/{Sid}.json`) with the same Basic auth a send
// uses. The GET reads the account; it sends nothing and bills nothing.
//
// 401 / 403 is `Unhealthy` (the credential is wrong, every send will
// fail permanently). A 5xx, a 429 or a transport failure is `Degraded`:
// Twilio is having a bad minute, and the dispatcher's retry loop is
// built for exactly that — flipping `/ready` would take the process out
// of rotation for a vendor outage it cannot fix.

[<Literal>]
let private SecretScope = "_platform"

[<Literal>]
let private SecretKey = "TWILIO_AUTH_TOKEN"

[<Literal>]
let private ApiBase = "https://api.twilio.com/2010-04-01/Accounts"

/// The account resource the probe reads. With an `EndpointOverride`
/// ending `/Messages.json` the account URL is derived from it (the
/// Messages endpoint is the account URL plus `/Messages.json`); any other
/// override is probed as given.
let accountUrl (settings: TwilioWhatsAppSettings) : string =
    let messagesSuffix = "/Messages.json"

    match settings.EndpointOverride with
    | Some url when url.EndsWith(messagesSuffix, StringComparison.OrdinalIgnoreCase) ->
        url.Substring(0, url.Length - messagesSuffix.Length) + ".json"
    | Some url -> url
    | None -> sprintf "%s/%s.json" ApiBase settings.AccountSid

/// Readiness probe for the Twilio WhatsApp sink. `handler` is the
/// transport; the two-argument constructor uses the platform's own
/// egress-policy-wrapped client.
type TwilioWhatsAppNotificationSinkHealthCheck
    (secretStore: ISecretStore, settings: TwilioWhatsAppSettings, handler: HttpMessageHandler) =

    let client = PlatformHttpClient.createWith EgressSurface.Notification handler

    /// Construct with the platform's default transport.
    new(secretStore: ISecretStore, settings: TwilioWhatsAppSettings) =
        TwilioWhatsAppNotificationSinkHealthCheck(secretStore, settings, new HttpClientHandler())

    interface IHealthCheck with
        member _.Name = "transactional_sink:twilio_whatsapp"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 5.0

        member _.Check() = async {
            try
                let! secret = secretStore.GetSecret(SecretScope, SecretKey)

                match secret with
                | None -> return Unhealthy "TWILIO_AUTH_TOKEN not configured in secret store"
                | Some value when String.IsNullOrWhiteSpace value ->
                    return Unhealthy "TWILIO_AUTH_TOKEN is set but empty"
                | Some token ->
                    use request = new HttpRequestMessage(HttpMethod.Get, accountUrl settings)

                    let credential =
                        Convert.ToBase64String(Encoding.ASCII.GetBytes(sprintf "%s:%s" settings.AccountSid token))

                    request.Headers.Authorization <- AuthenticationHeaderValue("Basic", credential)

                    let! response = client.SendAsync request |> Async.AwaitTask

                    match int response.StatusCode with
                    | code when code >= 200 && code < 300 -> return Healthy
                    | 401
                    | 403 as code -> return Unhealthy(sprintf "Twilio rejected the account credentials (%d)" code)
                    | 404 -> return Unhealthy "Twilio does not know the configured account SID (404)"
                    | code -> return Degraded(sprintf "Twilio account probe returned %d" code)
            with ex ->
                return Degraded(sprintf "Twilio account probe failed: %s" ex.Message)
        }

/// Construct the probe with the platform's default transport.
let create (secretStore: ISecretStore) (settings: TwilioWhatsAppSettings) : IHealthCheck =
    TwilioWhatsAppNotificationSinkHealthCheck(secretStore, settings) :> IHealthCheck

/// Construct the probe over a caller-supplied primary HTTP handler.
let createWithHandler
    (secretStore: ISecretStore)
    (settings: TwilioWhatsAppSettings)
    (handler: HttpMessageHandler)
    : IHealthCheck =
    TwilioWhatsAppNotificationSinkHealthCheck(secretStore, settings, handler) :> IHealthCheck
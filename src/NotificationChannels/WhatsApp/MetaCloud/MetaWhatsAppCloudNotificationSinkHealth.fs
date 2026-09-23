// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 6f.C - the readiness probe for the Meta WhatsApp Business Cloud sink.
module ToolUp.Platform.NotificationChannels.WhatsApp.MetaCloudHealth

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.NotificationChannels.WhatsApp.MetaCloud
open ToolUp.Platform.Secrets

// ─── Phase 6f.C — Meta WhatsApp Cloud sink readiness probe ───────────
//
// One authenticated `GET {base}/{version}/{phoneNumberId}` — the
// phone-number node the sink sends through. It proves the two things a
// send needs that configuration alone cannot: the System User token
// resolves AND Meta accepts it, and the phone number id names a number
// that token can reach. Read-only, unbilled, and never sends a message.
//
//   * token missing / blank in the secret store → Unhealthy
//   * 401 / 403 → Unhealthy (token expired, revoked, or not granted
//     `whatsapp_business_messaging` on this number)
//   * 400 / 404 → Unhealthy (the phone number id is wrong)
//   * 429 / 5xx / network → Degraded (Meta-side, retryable)

/// Readiness probe for the Meta WhatsApp Business Cloud sink. `handler`
/// is the primary transport, wrapped by the platform egress policy.
type MetaWhatsAppCloudNotificationSinkHealthCheck
    (secretStore: ISecretStore, settings: MetaWhatsAppSettings, handler: HttpMessageHandler) =

    let client = PlatformHttpClient.createWith EgressSurface.Notification handler

    /// The production constructor — a fresh `HttpClientHandler` behind
    /// the egress policy. Explicit rather than an optional argument, so
    /// the narrow shape stays its own token in the approval baseline.
    new(secretStore: ISecretStore, settings: MetaWhatsAppSettings) =
        MetaWhatsAppCloudNotificationSinkHealthCheck(secretStore, settings, new HttpClientHandler())

    interface IHealthCheck with
        member _.Name = "transactional_sink:meta-whatsapp"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 5.0

        member _.Check() = async {
            try
                let! token =
                    secretStore.GetSecret(MetaWhatsAppSettings.SecretScope, MetaWhatsAppSettings.AccessTokenSecretKey)

                match token with
                | None ->
                    return
                        Unhealthy(sprintf "%s not configured in secret store" MetaWhatsAppSettings.AccessTokenSecretKey)
                | Some value when String.IsNullOrWhiteSpace value ->
                    return Unhealthy(sprintf "%s is set but empty" MetaWhatsAppSettings.AccessTokenSecretKey)
                | Some value ->
                    use request =
                        new HttpRequestMessage(HttpMethod.Get, MetaWhatsAppSettings.phoneNumberUrl settings)

                    request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", value)
                    let! response = client.SendAsync request |> Async.AwaitTask

                    match int response.StatusCode with
                    | code when code >= 200 && code < 300 -> return Healthy
                    | 401
                    | 403 ->
                        return
                            Unhealthy(
                                sprintf
                                    "Meta rejected the access token (%d) — expired, revoked, or not granted this phone number"
                                    (int response.StatusCode)
                            )
                    | 400
                    | 404 ->
                        return
                            Unhealthy(
                                sprintf
                                    "Meta does not recognise phone number id %s (%d)"
                                    settings.PhoneNumberId
                                    (int response.StatusCode)
                            )
                    | code -> return Degraded(sprintf "Meta Graph API answered %d" code)
            with
            | :? HttpRequestException as ex -> return Degraded(sprintf "Meta Graph API unreachable: %s" ex.Message)
            | ex -> return Unhealthy ex.Message
        }

/// Build the probe over the platform's default transport.
let create (secretStore: ISecretStore) (settings: MetaWhatsAppSettings) : IHealthCheck =
    MetaWhatsAppCloudNotificationSinkHealthCheck(secretStore, settings) :> IHealthCheck

/// Build the probe over a caller-supplied transport.
let createWith
    (secretStore: ISecretStore)
    (settings: MetaWhatsAppSettings)
    (handler: HttpMessageHandler)
    : IHealthCheck =
    MetaWhatsAppCloudNotificationSinkHealthCheck(secretStore, settings, handler) :> IHealthCheck
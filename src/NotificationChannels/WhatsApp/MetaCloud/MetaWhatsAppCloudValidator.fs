// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 6f.C - the startup preflight for the Meta WhatsApp Business Cloud sink.
module ToolUp.Platform.NotificationChannels.WhatsApp.MetaCloudValidator

open System
open System.Text.RegularExpressions
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.NotificationChannels.WhatsApp.MetaCloud
open ToolUp.Platform.Secrets

// ─── Phase 6f.C — Meta WhatsApp Cloud preflight ──────────────────────
//
// Runs once at compose end, offline: it reads the secret store and the
// deployment config but never calls Meta (the readiness probe does
// that on every `/ready` poll). What it refuses to boot with:
//
//   * a phone number id that is not numeric — Meta's ids are digits, and
//     a phone NUMBER pasted in its place is the commonest mistake;
//   * no System User access token in the secret store;
//   * with the inbound handler registered: no app secret (every signed
//     delivery would be rejected, fail-closed) and — in a production-
//     shaped deployment — a `PublicBaseUrl` that is not HTTPS, because
//     Meta refuses to deliver webhooks to a non-HTTPS callback.
//
// What it only warns about: a Graph API version that does not look like
// `vNN.N`, a non-HTTPS endpoint override, a missing webhook verify token
// (the dashboard's subscription check will fail until one is set), no
// `PublicBaseUrl` at all, and a non-HTTPS one outside production.
//
// "Production-shaped" is `DeploymentConfig.hasPersistentAuthenticatedStorage`
// — the predicate the other persistent-mode preflights escalate on.

let private versionShape = Regex(@"^v\d+\.\d+$", RegexOptions.Compiled)

type private Impl
    (secretStore: ISecretStore, settings: MetaWhatsAppSettings, config: ServerConfig, inboundRegistered: bool) =

    let secretSet (key: string) = async {
        let! value = secretStore.GetSecret(MetaWhatsAppSettings.SecretScope, key)

        return
            match value with
            | Some v -> not (String.IsNullOrWhiteSpace v)
            | None -> false
    }

    interface IConfigValidator with
        member _.Name = "meta-whatsapp-cloud"
        member _.Timeout = IConfigValidator.defaultTimeout

        member _.Validate() = async {
            let errors = ResizeArray<string>()
            let warnings = ResizeArray<string>()

            if
                String.IsNullOrWhiteSpace settings.PhoneNumberId
                || not (settings.PhoneNumberId |> Seq.forall Char.IsDigit)
            then
                errors.Add(
                    sprintf
                        "PhoneNumberId '%s' is not numeric — it is the phone number ID from the Meta App dashboard, not the phone number."
                        settings.PhoneNumberId
                )

            if not (versionShape.IsMatch(settings.GraphApiVersion)) then
                warnings.Add(
                    sprintf
                        "GraphApiVersion '%s' does not look like a Graph API version (expected e.g. '%s')."
                        settings.GraphApiVersion
                        MetaWhatsAppSettings.DefaultGraphApiVersion
                )

            match settings.EndpointOverride with
            | None -> ()
            | Some url ->
                match Uri.TryCreate(url, UriKind.Absolute) with
                | true, uri when uri.Scheme = Uri.UriSchemeHttps -> ()
                | true, _ ->
                    warnings.Add(sprintf "EndpointOverride '%s' is not HTTPS — the access token travels on it." url)
                | false, _ -> errors.Add(sprintf "EndpointOverride '%s' is not an absolute URL." url)

            let! tokenSet = secretSet MetaWhatsAppSettings.AccessTokenSecretKey

            if not tokenSet then
                errors.Add(
                    sprintf
                        "%s is not set in secret store scope %s — every send would fail."
                        MetaWhatsAppSettings.AccessTokenSecretKey
                        MetaWhatsAppSettings.SecretScope
                )

            if inboundRegistered then
                let! appSecretSet = secretSet MetaWhatsAppSettings.AppSecretKey

                if not appSecretSet then
                    errors.Add(
                        sprintf
                            "the inbound webhook handler is registered but %s is not set — every signed delivery would be rejected."
                            MetaWhatsAppSettings.AppSecretKey
                    )

                let! verifyTokenSet = secretSet MetaWhatsAppSettings.VerifyTokenSecretKey

                if not verifyTokenSet then
                    warnings.Add(
                        sprintf
                            "%s is not set — the Meta dashboard's callback verification (GET hub.verify_token) will be refused."
                            MetaWhatsAppSettings.VerifyTokenSecretKey
                    )

                let production = DeploymentConfig.hasPersistentAuthenticatedStorage config

                match config.PublicBaseUrl with
                | None ->
                    warnings.Add
                        "ServerConfig.PublicBaseUrl is not set — Meta must be given the public HTTPS callback URL for /webhooks/whatsapp-meta."
                | Some url ->
                    let https =
                        match Uri.TryCreate(url, UriKind.Absolute) with
                        | true, uri -> uri.Scheme = Uri.UriSchemeHttps
                        | false, _ -> false

                    if not https then
                        let message =
                            sprintf
                                "ServerConfig.PublicBaseUrl '%s' is not HTTPS — Meta refuses non-HTTPS webhook URLs."
                                url

                        if production then
                            errors.Add message
                        else
                            warnings.Add message

            if errors.Count > 0 then
                return Error(String.concat "; " (Seq.append errors warnings))
            elif warnings.Count > 0 then
                return Warning(String.concat "; " warnings)
            else
                return Ok
        }

/// Build the Meta WhatsApp Cloud preflight. `inboundRegistered` is `true`
/// when the deployment registers the `whatsapp-meta` inbound handler,
/// which adds the app-secret and HTTPS-callback checks.
let create
    (secretStore: ISecretStore)
    (settings: MetaWhatsAppSettings)
    (config: ServerConfig)
    (inboundRegistered: bool)
    : IConfigValidator =
    Impl(secretStore, settings, config, inboundRegistered) :> IConfigValidator
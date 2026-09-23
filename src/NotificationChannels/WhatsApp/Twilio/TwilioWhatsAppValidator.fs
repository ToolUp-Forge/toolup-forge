// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 6f.B — the compose-time preflight for the Twilio WhatsApp sink.
module ToolUp.Platform.NotificationChannels.WhatsApp.TwilioValidator

open System
open System.Text.RegularExpressions
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Secrets
open ToolUp.Platform.NotificationChannels.WhatsApp.Twilio

// ─── Phase 6f.B Twilio WhatsApp config preflight ─────────────────────
//
// Runs once at compose end, when the sink is registered. Checks what a
// send needs and a deploy can get wrong WITHOUT calling Twilio: the auth
// token resolves from `ISecretStore`, the account SID is present, and
// the sender number is E.164. Whether Twilio accepts the credential is
// the health probe's question (it runs on every `/ready` poll), not the
// preflight's — a vendor outage at deploy time must not abort a boot.

[<Literal>]
let private SecretScope = "_platform"

[<Literal>]
let private SecretKey = "TWILIO_AUTH_TOKEN"

/// E.164: a `+`, a non-zero leading digit, at most 15 digits in all.
let private e164 = Regex(@"^\+[1-9]\d{1,14}$", RegexOptions.Compiled)

/// `true` when `number` is an E.164 telephone number (`+` and up to 15
/// digits, no spaces or punctuation).
let isE164 (number: string) : bool =
    not (String.IsNullOrWhiteSpace number) && e164.IsMatch number

type private Impl(secretStore: ISecretStore, settings: TwilioWhatsAppSettings) =
    interface IConfigValidator with
        member _.Name = "twilio-whatsapp-notification"
        member _.Timeout = IConfigValidator.defaultTimeout

        member _.Validate() = async {
            let from = TwilioWhatsAppSettings.bareFromNumber settings

            let! token = async {
                try
                    return! secretStore.GetSecret(SecretScope, SecretKey)
                with _ ->
                    return None
            }

            let problems = [
                match token with
                | Some value when not (String.IsNullOrWhiteSpace value) -> ()
                | _ -> "TWILIO_AUTH_TOKEN does not resolve from the secret store (scope _platform)"

                if String.IsNullOrWhiteSpace settings.AccountSid then
                    "the Twilio account SID is empty"

                if not (isE164 from) then
                    sprintf "the WhatsApp sender number '%s' is not E.164 (expected e.g. +14155238886)" from
            ]

            return
                match problems with
                | [] -> Ok
                | _ -> Error(String.Join("; ", problems))
        }

/// Construct a validator for explicit settings — the same record the
/// sink was built from.
let create (secretStore: ISecretStore) (settings: TwilioWhatsAppSettings) : IConfigValidator =
    Impl(secretStore, settings) :> IConfigValidator

/// Construct a validator from `TwilioWhatsAppSettings.fromEnv ()`. Use
/// only when the deployment has already decided to run the Twilio
/// WhatsApp companion, so a missing variable fails the way the sink's
/// `fromEnv` does.
let fromEnv (secretStore: ISecretStore) : IConfigValidator =
    create secretStore (TwilioWhatsAppSettings.fromEnv ())
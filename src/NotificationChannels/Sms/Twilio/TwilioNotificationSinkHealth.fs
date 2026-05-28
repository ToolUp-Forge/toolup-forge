module ToolUp.Platform.NotificationChannels.Sms.TwilioHealth

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets

// ─── Phase 9k Twilio SMS sink health probe ───────────────────────────
//
// Verifies the Twilio auth token resolves from `ISecretStore`. The
// account SID is part of `TwilioSettings` (env-driven), so missing-
// configuration there crashes at startup — the probe only needs to
// catch the secret-rotation failure mode.

[<Literal>]
let private SecretScope = "_platform"

[<Literal>]
let private SecretKey = "TWILIO_AUTH_TOKEN"

type TwilioNotificationSinkHealthCheck(secretStore: ISecretStore) =
    interface IHealthCheck with
        member _.Name = "transactional_sink:twilio"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 3.0

        member _.Check() = async {
            try
                let! secret = secretStore.GetSecret(SecretScope, SecretKey)

                match secret with
                | Some value when not (String.IsNullOrWhiteSpace value) -> return Healthy
                | Some _ -> return Unhealthy "TWILIO_AUTH_TOKEN is set but empty"
                | None -> return Unhealthy "TWILIO_AUTH_TOKEN not configured in secret store"
            with ex ->
                return Unhealthy ex.Message
        }

let create (secretStore: ISecretStore) : IHealthCheck =
    TwilioNotificationSinkHealthCheck(secretStore) :> IHealthCheck
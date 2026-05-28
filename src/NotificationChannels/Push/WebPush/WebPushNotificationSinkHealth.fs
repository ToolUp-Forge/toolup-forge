module ToolUp.Platform.NotificationChannels.Push.WebPushHealth

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets

// ─── Phase 9k WebPush sink health probe ──────────────────────────────
//
// Verifies the VAPID private key resolves from `ISecretStore`. The
// public key + subject come from env vars and crash at startup if
// missing, so the probe only covers the secret-rotation failure mode.
// Browser push delivery itself is per-subscriber and out of band — no
// upstream service to ping at probe time.

[<Literal>]
let private SecretScope = "_platform"

[<Literal>]
let private SecretKey = "WEBPUSH_VAPID_PRIVATE"

type WebPushNotificationSinkHealthCheck(secretStore: ISecretStore) =
    interface IHealthCheck with
        member _.Name = "transactional_sink:webpush"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 1.0

        member _.Check() = async {
            try
                let! secret = secretStore.GetSecret(SecretScope, SecretKey)

                match secret with
                | Some value when not (String.IsNullOrWhiteSpace value) -> return Healthy
                | Some _ -> return Unhealthy "WEBPUSH_VAPID_PRIVATE is set but empty"
                | None -> return Unhealthy "WEBPUSH_VAPID_PRIVATE not configured in secret store"
            with ex ->
                return Unhealthy ex.Message
        }

let create (secretStore: ISecretStore) : IHealthCheck =
    WebPushNotificationSinkHealthCheck(secretStore) :> IHealthCheck
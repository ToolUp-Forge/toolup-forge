module ToolUp.Platform.NotificationChannels.Email.SendGridHealth

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets

// ─── Phase 9k SendGrid transactional sink health probe ───────────────
//
// Verifies the SendGrid API key resolves from `ISecretStore` (matches
// the key the sink itself reads on every send). Same trade-off as the
// AI provider probes — no upstream call to api.sendgrid.com on every
// poll, only the configuration-presence check.

[<Literal>]
let private SecretScope = "_platform"

[<Literal>]
let private SecretKey = "SENDGRID_API_KEY"

type SendGridNotificationSinkHealthCheck(secretStore: ISecretStore) =
    interface IHealthCheck with
        member _.Name = "transactional_sink:sendgrid"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 3.0

        member _.Check() = async {
            try
                let! secret = secretStore.GetSecret(SecretScope, SecretKey)

                match secret with
                | Some value when not (String.IsNullOrWhiteSpace value) -> return Healthy
                | Some _ -> return Unhealthy "SENDGRID_API_KEY is set but empty"
                | None -> return Unhealthy "SENDGRID_API_KEY not configured in secret store"
            with ex ->
                return Unhealthy ex.Message
        }

let create (secretStore: ISecretStore) : IHealthCheck =
    SendGridNotificationSinkHealthCheck(secretStore) :> IHealthCheck
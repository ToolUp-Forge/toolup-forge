module ToolUp.Platform.WebhookSecretValidator

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.ConfigValidation

// ─── Phase 6d.A — webhook plaintext-secret-at-rest preflight ─────────
//
// Refuses startup when any persisted webhook subscription still carries a
// plaintext signing secret inline (`Secret` / `PreviousSecret`) instead
// of a `SecretRef` into `ISecretStore`. Two failure modes it catches:
//
//   * a half-migrated deployment (the one-shot migration moved some blobs
//     but not all — e.g. it errored partway, or new pre-6d.A blobs
//     appeared from a lagging replica);
//   * a blob tampered / hand-edited to re-inject a literal `Secret`
//     (matching the acceptance-criteria adversarial case).
//
// Security-class (`ISecurityClassValidator`): a plaintext-secret-at-rest
// exposure, so it runs even under `ServerConfig.SkipPreflight` — a
// noisy-companion bypass lever must never silently re-expose every
// tenant's webhook signing secret. Registered only when
// `Webhooks = EnabledWebhooks`, so a deployment without webhooks never
// scans.
//
// Remediation is the compose-time opt-in
// `ServerConfig.MigrateWebhookSecretsAtRest = true` (or
// `TOOLUP_MIGRATE_WEBHOOK_SECRETS=1`) for one boot, which runs
// `WebhookSecretMigration.migrate` before this validator.

/// Refuse startup on any persisted subscription with an inline plaintext
/// signing secret. Reads blob state (heavier than a config probe, but
/// preflight runs once at compose end, off the request path).
type WebhookSecretAtRestValidator(storage: IBlobStorage, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // Plaintext webhook secrets at rest are a secret-exposure hole —
    // security-class, so it runs even under SkipPreflight.
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "webhook-secret-at-rest"
        member _.Timeout = timeout

        member _.Validate() = async {
            let! subs = WebhookRegistry.listAllSubscriptions storage

            let hasInline (v: string option) =
                v |> Option.exists (fun s -> not (String.IsNullOrEmpty s))

            let offenders =
                subs |> List.filter (fun s -> hasInline s.Secret || hasInline s.PreviousSecret)

            match offenders with
            | [] -> return Ok
            | _ ->
                let ids =
                    offenders
                    |> List.map (fun s -> sprintf "%O (scope %s)" s.SubscriptionId s.ScopeId)
                    |> String.concat ", "

                return
                    Error(
                        sprintf
                            "%d webhook subscription(s) persist a plaintext signing secret inline instead of a SecretRef into ISecretStore — a blob-storage breach would leak every affected tenant's webhook signing secret. This is a half-migrated (or tampered) pre-Phase-6d.A deployment. Resolution: set ServerConfig.MigrateWebhookSecretsAtRest = true (or TOOLUP_MIGRATE_WEBHOOK_SECRETS=1) for one boot to run the one-shot WebhookSecretMigration, which moves each secret into ISecretStore and rewrites the blob with only the reference; then remove the flag. Offending subscription(s): %s."
                            offenders.Length
                            ids
                    )
        }
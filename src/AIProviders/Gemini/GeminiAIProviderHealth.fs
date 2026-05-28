module GeminiAIProviderHealth

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets

// ─── Gemini provider health probe ────────────────────────────────
//
// Mirrors `OpenAIProviderHealth` / `ClaudeAIProviderHealth` —
// verifies that `GEMINI_API_KEY` resolves to a non-empty value in
// the `_platform` scope without burning tokens against
// generativelanguage.googleapis.com on every probe.

type GeminiAIProviderHealthCheck(secretStore: ISecretStore) =
    interface IHealthCheck with
        member _.Name = "ai_provider:google-gemini"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                let! secret = secretStore.GetSecret("_platform", GeminiAIProvider.SecretKeyName)

                match secret with
                | Some value when not (String.IsNullOrWhiteSpace value) -> return Healthy
                | Some _ -> return Unhealthy "GEMINI_API_KEY is set but empty"
                | None -> return Unhealthy "GEMINI_API_KEY not configured in secret store"
            with ex ->
                return Unhealthy ex.Message
        }

let create (secretStore: ISecretStore) : IHealthCheck =
    GeminiAIProviderHealthCheck(secretStore) :> IHealthCheck
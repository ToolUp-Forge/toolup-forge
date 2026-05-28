module OpenAIProviderHealth

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets

// ─── Phase 9k OpenAI provider health probe ───────────────────────────
//
// Mirrors `ClaudeAIProviderHealth` — verifies that `OPENAI_API_KEY`
// resolves to a non-empty value in the `_platform` scope without
// burning tokens against api.openai.com on every probe. See the
// ClaudeAIProviderHealth header for the rationale.

type OpenAIProviderHealthCheck(secretStore: ISecretStore) =
    interface IHealthCheck with
        member _.Name = "ai_provider:openai"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                let! secret = secretStore.GetSecret("_platform", "OPENAI_API_KEY")

                match secret with
                | Some value when not (String.IsNullOrWhiteSpace value) -> return Healthy
                | Some _ -> return Unhealthy "OPENAI_API_KEY is set but empty"
                | None -> return Unhealthy "OPENAI_API_KEY not configured in secret store"
            with ex ->
                return Unhealthy ex.Message
        }

let create (secretStore: ISecretStore) : IHealthCheck =
    OpenAIProviderHealthCheck(secretStore) :> IHealthCheck
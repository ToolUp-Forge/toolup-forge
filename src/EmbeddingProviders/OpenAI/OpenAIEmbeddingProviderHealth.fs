module OpenAIEmbeddingProviderHealth

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets

// ─── Phase 9k OpenAI embedding provider health probe ─────────────────
//
// Verifies that the `openai-api-key` secret resolves to a non-empty
// value in the `_platform` scope. Note the key name differs from the
// AI provider's (`OPENAI_API_KEY` for chat-completions vs
// `openai-api-key` for embeddings) — match each provider's actual
// lookup key so the probe reflects what the runtime read will see.
//
// Same trade-off as the AI provider probes: no upstream call to
// api.openai.com on every probe (token cost amortisation). Bad keys
// surface at first embed-call time via the existing error path.

type OpenAIEmbeddingProviderHealthCheck(secretStore: ISecretStore) =
    interface IHealthCheck with
        member _.Name = "embedding_provider:openai"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                let! secret = secretStore.GetSecret("_platform", "openai-api-key")

                match secret with
                | Some value when not (String.IsNullOrWhiteSpace value) -> return Healthy
                | Some _ -> return Unhealthy "openai-api-key is set but empty"
                | None -> return Unhealthy "openai-api-key not configured in secret store"
            with ex ->
                return Unhealthy ex.Message
        }

let create (secretStore: ISecretStore) : IHealthCheck =
    OpenAIEmbeddingProviderHealthCheck(secretStore) :> IHealthCheck
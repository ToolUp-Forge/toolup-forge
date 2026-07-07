module CopilotAIProviderHealth

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets

// ─── Azure OpenAI ("Microsoft Copilot") provider health probe ────────
//
// Mirrors `OpenAIProviderHealth` — verifies that `AZURE_OPENAI_API_KEY`
// resolves to a non-empty value in the `_platform` scope without burning
// tokens against the Azure endpoint on every probe.
//
// This probe is for the api-key auth path. Entra ID deployments carry no
// static secret, so they should NOT register this probe (it would falsely
// report Unhealthy); a credential-validity probe belongs with the identity
// substrate, not here.

type CopilotAIProviderHealthCheck(secretStore: ISecretStore) =
    interface IHealthCheck with
        member _.Name = "ai_provider:azure-openai"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                let! secret = secretStore.GetSecret("_platform", "AZURE_OPENAI_API_KEY")

                match secret with
                | Some value when not (String.IsNullOrWhiteSpace value) -> return Healthy
                | Some _ -> return Unhealthy "AZURE_OPENAI_API_KEY is set but empty"
                | None -> return Unhealthy "AZURE_OPENAI_API_KEY not configured in secret store"
            with ex ->
                return Unhealthy ex.Message
        }

let create (secretStore: ISecretStore) : IHealthCheck =
    CopilotAIProviderHealthCheck(secretStore) :> IHealthCheck
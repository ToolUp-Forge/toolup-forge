module ClaudeAIProviderHealth

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets

// ─── Phase 9k Claude provider health probe ───────────────────────────
//
// Verifies that `ANTHROPIC_API_KEY` resolves to a non-empty value in
// the `_platform` scope of the configured `ISecretStore`. We do NOT
// burn tokens calling Anthropic's API on every probe — `/ready` is
// polled on every load-balancer health-check interval and even cheap
// API calls would generate per-deployment cost during normal
// operation.
//
// What this catches: missing or empty API key, key removed from the
// secret store, secret-store backend offline. What it does NOT catch:
// invalid API key (rejected at request time by Anthropic), network
// reachability of api.anthropic.com (a separate health-check target
// for deployments that need it). The trade-off is intentional: the
// signal is "is the configuration sane enough to attempt a call" not
// "will the call succeed".

/// Construct via `ClaudeAIProviderHealth.create secretStore` and
/// register through `ServerApp.withHealthCheck`. The unique probe
/// name (`ai_provider:claude`) lets it coexist with the OpenAI probe
/// (`ai_provider:openai`) under BCL's per-name registration rule.
type ClaudeAIProviderHealthCheck(secretStore: ISecretStore) =
    interface IHealthCheck with
        member _.Name = "ai_provider:claude"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                let! secret = secretStore.GetSecret("_platform", "ANTHROPIC_API_KEY")

                match secret with
                | Some value when not (String.IsNullOrWhiteSpace value) -> return Healthy
                | Some _ -> return Unhealthy "ANTHROPIC_API_KEY is set but empty"
                | None -> return Unhealthy "ANTHROPIC_API_KEY not configured in secret store"
            with ex ->
                return Unhealthy ex.Message
        }

let create (secretStore: ISecretStore) : IHealthCheck =
    ClaudeAIProviderHealthCheck(secretStore) :> IHealthCheck